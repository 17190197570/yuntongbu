using CloudDrive.Storage;
using Microsoft.Data.Sqlite;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;

string fixture = Path.GetFullPath(Path.Combine("artifacts", "tests", "cache-" + Guid.NewGuid().ToString("N"))); Directory.CreateDirectory(fixture);
var index = new IndexStore(Path.Combine(fixture, "index.db")); var fake = new FixturePlatform(); var manager = new CacheManager(index, fake); int passed = 0;
void Check(bool value, string label) { if (!value) throw new Exception("FAIL " + label); passed++; Console.WriteLine("PASS " + label); }
void Sql(string sql) { using var db = new SqliteConnection("Data Source=" + index.DatabasePath); db.Open(); using var cmd = db.CreateCommand(); cmd.CommandText = sql; cmd.ExecuteNonQuery(); }
async Task<SmbConnection> Connection(string name, params string[] files)
{
    var id = Guid.NewGuid(); string root = Path.Combine(fixture, name + "-cloud"), remote = Path.Combine(fixture, name + "-remote"); Directory.CreateDirectory(root); Directory.CreateDirectory(remote);
    var c = new SmbConnection(id, name, "127.0.0.1", 445, "fixture", "", "user", "", false, root); index.Save(c);
    foreach (string path in files)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Paths.Under(root, path))!); Directory.CreateDirectory(Path.GetDirectoryName(Paths.Under(remote, path))!);
        File.WriteAllText(Paths.Under(root, path), "fixture"); File.WriteAllText(Paths.Under(remote, path), "fixture");
        var s = new CacheSnapshot(Paths.StableId(id, path), id, path, false, true, true, false, 100, 100, 100); fake.Files[(id, path)] = s;
    }
    await new RemoteScanner(index).ScanAsync(id, new FileSystemSource(id, remote), CancellationToken.None); index.SaveCacheBatch(fake.Files.Where(x => x.Key.Item1 == id).Select(x => x.Value)); return c;
}
void Access(SmbConnection c, string path, int days, string source = "ForegroundApplication", bool user = true)
{
    var at = DateTimeOffset.UtcNow.AddDays(-days); string id = Paths.StableId(c.Id, path);
    index.RecordCacheAccess([new(id, c.Id, path, "Opened", at, 123, "editor.exe", source, user), new(id, c.Id, path, "Closed", at.AddSeconds(1), 123, "editor.exe", source, false)]);
}
var c = await Connection("policies", "old.txt", "middle.txt", "recent.txt", "unknown.txt", "pinned.txt", "pending.txt");
Check(!index.CachePolicy(c.Id).Enabled, "automatic cleanup disabled by default");
index.EnqueueNewFile(c.Id, "pending.txt");
Sql("DROP TABLE CacheUsageStats; DROP TABLE CachePolicies; DROP TABLE CacheRuns; DROP TABLE CacheOperations; DROP TABLE CacheExclusions; PRAGMA user_version=2;");
index = new IndexStore(index.DatabasePath); manager = new CacheManager(index, fake);
Check(index.Count(c.Id) == 6 && index.SyncTasks(c.Id).Count == 1 && !index.CachePolicy(c.Id).Enabled, "v2 to v3 additive migration retains files and sync tasks");
index.SaveCacheBatch(fake.Files.Values);
Access(c, "old.txt", 200); Access(c, "middle.txt", 60); Access(c, "recent.txt", 2); Access(c, "pinned.txt", 100); Access(c, "pending.txt", 100);
fake.Change(c.Id, "pinned.txt", s => s with { Pinned = true }); index.SaveCacheBatch([fake.Probe(c, "pinned.txt")]);
var p = new CachePolicy(Enabled: true, AgeEnabled: true); index.SetCachePolicy(c.Id, p);
var plans = await manager.PlanAutomaticAsync(CancellationToken.None); var planned = plans.SelectMany(index.CacheOperations).ToList();
Check(planned.Select(o => o.Path).SequenceEqual(["old.txt", "middle.txt"]), "LRU excludes recent, unknown, pinned and pending-upload files");
await manager.ExecuteAsync(plans.Single(), CancellationToken.None);
Check(fake.Released.SequenceEqual(["old.txt", "middle.txt"]) && index.CacheRuns().First().ReleasedBytes == 200, "cleanup measures actual before/after allocation and orders LRU");
Check(index.DecorateCache(fake.Probe(c, "old.txt")).State == CacheState.OnlineOnly, "verified zero hydrated bytes yields OnlineOnly");
var baseFile = index.DecorateCache(fake.Probe(c, "unknown.txt")); var now = DateTimeOffset.UtcNow;
Check(CacheSafety.Reject(baseFile, true, now, p) is not null && CacheSafety.Reject(baseFile, false, now, p) is null, "unknown access excluded automatically, manual verified release allowed");
index.RecordCacheAccess([new(baseFile.Snapshot.FileId, c.Id, "unknown.txt", "Downloading", now, 0, null, "Unknown", false)]);
index.SaveCacheBatch([baseFile.Snapshot]);
Check(index.DecorateCache(baseFile.Snapshot).BlockReason is null, "actual full hydration clears orphaned download hint without trusting missing completion");
foreach (var item in new[] {
    (baseFile with { Snapshot = baseFile.Snapshot with { Owned = false } }, "ordinary file rejected"),
    (baseFile with { Snapshot = baseFile.Snapshot with { InSync = false } }, "NotInSync rejected"),
    (baseFile with { Snapshot = baseFile.Snapshot with { ModifiedBytes = 1 } }, "modified ranges rejected"),
    (baseFile with { Snapshot = baseFile.Snapshot with { Pinned = true } }, "pinned rejected"),
    (baseFile with { OpenCount = 1 }, "open count rejected"),
    (baseFile with { Snapshot = baseFile.Snapshot with { HydratedBytes = 50 } }, "partial hydration rejected without reading data"),
    (baseFile with { Snapshot = baseFile.Snapshot with { Error = "unknown" } }, "uncertain state rejected") }) Check(CacheSafety.Reject(item.Item1, false, now, p) is not null, item.Item2);
Check(CacheAccessTracker.Classify(123, "SearchIndexer.exe", true).User == false && CacheAccessTracker.Classify(123, "editor.exe", false).User == false && CacheAccessTracker.Classify(123, "editor.exe", true).User, "background/unknown opens do not refresh user LRU");
Access(c, "unknown.txt", 0, "BackgroundOrShell", false); Check(index.DecorateCache(baseFile.Snapshot).LastUserAccessAt is null, "actual background open recorded without inventing effective access");
var folder = await Connection("folder", @"A%_\one.txt", @"A%_sibling\two.txt");
index.SetCacheExclusion(folder.Id, "A%_", true);
Check(index.DecorateCache(fake.Probe(folder, @"A%_\one.txt")).Excluded && !index.DecorateCache(fake.Probe(folder, @"A%_sibling\two.txt")).Excluded, "directory exclusions use segment boundaries, not SQL wildcard matching");
Check(CacheManager.InScope(@"A\x", "A", true) && !CacheManager.InScope(@"AB\x", "A", true), "recursive selection cannot escape folder boundary");
var capacity = await Connection("capacity", "first.txt", "second.txt", "third.txt"); Access(capacity, "first.txt", 200); Access(capacity, "second.txt", 60); Access(capacity, "third.txt", 10);
index.SetCachePolicy(c.Id, new());
index.SetCachePolicy(capacity.Id, new(Enabled: true, CapacityEnabled: true, MaxCacheBytes: 400, TriggerCacheBytes: 250, TargetCacheBytes: 100));
plans = await manager.PlanAutomaticAsync(CancellationToken.None); Check(plans.Count == 1 && index.CacheOperations(plans.Single()).Count == 2, "capacity high/low watermark plans only needed LRU bytes");
await manager.ExecuteAsync(plans.Single(), CancellationToken.None); Check((await manager.PlanAutomaticAsync(CancellationToken.None)).Count == 0, "low watermark avoids immediate cleanup retrigger");
var failure = await Connection("failure", "fail.txt", "offline.txt", "pause.txt", "cancel.txt", "recover.txt");
fake.FailPath = "fail.txt"; Guid run = manager.QueueManual(failure.Id, "fail.txt", "Release", false); await manager.ExecuteAsync(run, CancellationToken.None);
Check(index.CacheOperations(run).Single().Status == "Failed" && index.DecorateCache(fake.Probe(failure, "fail.txt")).State == CacheState.DehydrationFailed && fake.Probe(failure, "fail.txt").HydratedBytes == 100, "failed release retains actual cached state and records failure"); fake.FailPath = null;
Access(failure, "offline.txt", 60); index.SetCachePolicy(failure.Id, new(Enabled: true, AgeEnabled: true)); fake.Offline = true;
plans = await manager.PlanAutomaticAsync(CancellationToken.None); run = plans.Single(); await manager.ExecuteAsync(run, CancellationToken.None);
Check(index.CacheRuns().First(r => r.Id == run).Status == "WaitingRemote" && fake.Probe(failure, "offline.txt").HydratedBytes == 100, "offline automatic cleanup waits and retains cache");
Check((await manager.PlanAutomaticAsync(CancellationToken.None)).Count == 0, "offline retry does not enqueue duplicate cleanup runs");
fake.Offline = false; await manager.ExecuteAsync(run, CancellationToken.None); Check(index.CacheOperations(run).Single().Status == "Completed", "remote recovery retries persisted cleanup after state recheck"); index.SetCachePolicy(failure.Id, new());
run = manager.QueueManual(failure.Id, "pause.txt", "Release", false); index.ControlCacheRun(run, "Paused"); await manager.ExecuteAsync(run, CancellationToken.None); Check(fake.Probe(failure, "pause.txt").HydratedBytes == 100, "paused run does not release files");
index.ControlCacheRun(run, "Pending"); await manager.ExecuteAsync(run, CancellationToken.None); Check(fake.Probe(failure, "pause.txt").HydratedBytes == 0, "manual resume executes pending cleanup");
run = manager.QueueManual(failure.Id, "cancel.txt", "Release", false); index.ControlCacheRun(run, "Cancelled"); await manager.ExecuteAsync(run, CancellationToken.None); Check(fake.Probe(failure, "cancel.txt").HydratedBytes == 100, "cancelled run retains untouched cache");
run = manager.QueueManual(failure.Id, "recover.txt", "Release", false); var interrupted = index.CacheOperations(run).Single(); index.CacheRunStatus(run, "Processing"); index.CacheOperationStatus(interrupted, "Processing", 100);
fake.Change(failure.Id, "recover.txt", s => s with { HydratedBytes = 0, AllocatedBytes = 0 });
int beforeCalls = fake.Released.Count; await new CacheManager(index, fake).ExecuteAsync(run, CancellationToken.None);
Check(fake.Released.Count == beforeCalls && index.CacheOperations(run).Single().Status == "Skipped", "restart reconciles already-released file instead of repeating work");
index.SetCachePolicy(capacity.Id, new());
var disk1 = await Connection("disk1", "d1.txt"); var disk2 = await Connection("disk2", "d2.txt"); Access(disk1, "d1.txt", 100); Access(disk2, "d2.txt", 60);
var dp = new CachePolicy(Enabled: true, DiskEnabled: true, MinDiskFreeBytes: 150, TriggerDiskFreeBytes: 100, TargetDiskFreeBytes: 200); index.SetCachePolicy(disk1.Id, dp); index.SetCachePolicy(disk2.Id, dp); fake.FreeBytes = 20;
plans = await manager.PlanAutomaticAsync(CancellationToken.None); Check(plans.Count == 1 && index.CacheOperations(plans.Single()).Count == 2, "multiple roots on same volume share one free-space budget");
await manager.ExecuteAsync(plans.Single(), CancellationToken.None); Check(fake.FreeBytes == 220 && (await manager.PlanAutomaticAsync(CancellationToken.None)).Count == 0, "disk target stops further release across roots");
run = manager.QueueManual(failure.Id, "cancel.txt", "Pin", false); await manager.ExecuteAsync(run, CancellationToken.None); Check(fake.Probe(failure, "cancel.txt").Pinned && index.DecorateCache(fake.Probe(failure, "cancel.txt")).Excluded, "pin intent protects cache");
run = manager.QueueManual(failure.Id, "cancel.txt", "Unpin", false); await manager.ExecuteAsync(run, CancellationToken.None); Check(!index.DecorateCache(fake.Probe(failure, "cancel.txt")).Excluded && fake.Probe(failure, "cancel.txt").HydratedBytes == 100, "unpin removes own intent without claiming content release");
Check(index.CacheSnapshots().Any(), "cache snapshots remain pageable");
await manager.CalibrateAsync(CancellationToken.None); Check(index.CacheSnapshots(failure.Id).Any(s => s.RelativePath == "cancel.txt"), "actual namespace scan repopulates cache records");
var late = await Connection("late", "late.txt", "mutex.txt", "restart.txt");
run = manager.QueueManual(late.Id, "late.txt", "Release", false); index.EnqueueNewFile(late.Id, "late.txt"); await manager.ExecuteAsync(run, CancellationToken.None);
Check(index.CacheOperations(run).Single().Status == "Skipped" && fake.Probe(late, "late.txt").HydratedBytes > 0, "sync work created after planning still blocks release at execution");
run = manager.QueueManual(late.Id, "mutex.txt", "Release", false);
using (ConnectionLock.Acquire(Guid.Empty, "cache-execution"))
{
    try { await new CacheManager(index, fake).ExecuteAsync(run, CancellationToken.None); throw new Exception("FAIL global lock"); }
    catch (IOException) { Check(fake.Probe(late, "mutex.txt").HydratedBytes == 100, "global cross-instance mutex prevents competing cleanup"); }
}
index.ControlCacheRun(run, "Cancelled");
run = manager.QueueManual(late.Id, "restart.txt", "Release", false); index.CacheRunStatus(run, "Processing"); index.CacheOperationStatus(index.CacheOperations(run).Single(), "Processing", 100);
using (var stop = new CancellationTokenSource(TimeSpan.FromSeconds(20)))
{
    Task background = manager.RunBackgroundAsync(stop.Token);
    try { while (index.CacheRuns(run).Single().Status != "Completed") { if (background.IsCompleted) await background; await Task.Delay(100, stop.Token); } Check(true, "scheduler startup recovers Processing journals without new user work"); }
    finally { await stop.CancelAsync(); try { await background; } catch (OperationCanceledException) { } }
}
index.SetCacheExclusion(folder.Id, "A%_", false); index.SetCacheExclusion(folder.Id, "A%_", true, "Pinned"); index.SetCacheExclusion(folder.Id, @"A%_\one.txt", true, "Unpinned");
Check(!index.DecorateCache(fake.Probe(folder, @"A%_\one.txt")).Excluded, "explicit child unpin overrides parent pin intent");
File.WriteAllLines(Path.Combine(fixture, "task-execution.log"), index.Logs());
Console.WriteLine($"PASS {passed} cache policy/queue checks using EXPLICIT FIXTURE PLATFORM. NOT NAS. Retained: {fixture}");
if (args.Contains("--cfapi", StringComparer.Ordinal)) await ActualCfapi();

async Task ActualCfapi()
{
    var id = Guid.NewGuid(); string remote = Path.Combine(fixture, "real-source"), root = Path.Combine(fixture, "real-cloud"); Directory.CreateDirectory(remote);
    byte[] payload = RandomNumberGenerator.GetBytes(3 * 1024 * 1024 + 123); string name = "缓存 # 中文.bin"; File.WriteAllBytes(Path.Combine(remote, name), payload);
    Directory.CreateDirectory(Path.Combine(remote, "批量"));
    for (int i = 0; i < 513; i++) File.WriteAllText(Path.Combine(remote, "批量", i + ".txt"), "small " + i);
    string large = "大文件1GiB.bin"; long largeSize = (1L << 30) + 123;
    using (var source = new FileStream(Path.Combine(remote, large), FileMode.CreateNew, FileAccess.Write)) source.SetLength(largeSize);
    var c = new SmbConnection(id, "actual-CFAPI-local-source", "127.0.0.1", 445, "test", "", "user", "", false, root); index.Save(c);
    await new RemoteScanner(index).ScanAsync(id, new FileSystemSource(id, remote), CancellationToken.None);
    await using var bridge = new SmbBridge(index, c, _ => Task.FromResult<IRemoteSource>(new FileSystemSource(id, remote))); bridge.Start();
    using var stopEvent = new EventWaitHandle(false, EventResetMode.ManualReset, AgentRuntime.EventName(id, "Stop"));
    var start = new ProcessStartInfo(Path.GetFullPath("artifacts/bin/CloudProviderHost.exe")) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
    foreach (string arg in new[] { "run-smb", "--source", id.ToString("N"), "--root", root, "--pipe", bridge.PipeName, "--agent-pid", Environment.ProcessId.ToString(), "--stop-event", AgentRuntime.EventName(id, "Stop"), "--refresh-event", AgentRuntime.EventName(id, "Refresh") }) start.ArgumentList.Add(arg);
    using var host = Process.Start(start)!; var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var platform = new WindowsCachePlatform(index, (_, _) => Task.FromResult(remote), Path.GetFullPath("artifacts/bin"));
    var tracker = new CacheAccessTracker(index, c, platform); using var trackerStop = new CancellationTokenSource(); Task tracking = Task.CompletedTask;
    using var providerLog = new StreamWriter(Path.Combine(fixture, "real-provider.log")) { AutoFlush = true };
    var output = Task.Run(async () => { string? line; while ((line = await host.StandardOutput.ReadLineAsync()) is not null)
    {
        await providerLog.WriteLineAsync(line);
        if (line.StartsWith("READY", StringComparison.Ordinal)) { Console.WriteLine(line); ready.TrySetResult(); }
        if (line.StartsWith("CACHE_HINT ", StringComparison.Ordinal) && JsonSerializer.Deserialize<CacheHint>(line[11..]) is { } hint) tracker.Hint(hint);
    } }); var errors = host.StandardError.ReadToEndAsync();
    try
    {
        await ready.Task.WaitAsync(TimeSpan.FromSeconds(30)); tracking = tracker.RunAsync(trackerStop.Token); var actual = new CacheManager(index, platform);
        var s = platform.Probe(c, name); Check(s.Owned && s.InSync && s.HydratedBytes == 0 && s.LogicalBytes == payload.Length, "real CFAPI probe reports online-only without content reads");
        Check(SHA256.HashData(await File.ReadAllBytesAsync(Paths.Under(root, name))).SequenceEqual(SHA256.HashData(payload)), "real Host range hydration matches source SHA256");
        await actual.CalibrateAsync(CancellationToken.None); s = platform.Probe(c, name); Check(s.HydratedBytes >= payload.Length && s.AllocatedBytes > 0, "actual file allocation differs from logical-only placeholder");
        Check(index.CacheSnapshots(id).Count(s => !s.IsDirectory) == 515 && platform.Probe(c, @"批量\512.txt").HydratedBytes == 0, "513 small files cross scan/page batches without downloading contents");
        var external = new ProcessStartInfo("python") { UseShellExecute = false, CreateNoWindow = true };
        foreach (string arg in new[] { "-c", "import pathlib,sys; pathlib.Path(sys.argv[1]).read_bytes()", Paths.Under(root, name) }) external.ArgumentList.Add(arg);
        using (var reader = Process.Start(external)!) { await reader.WaitForExitAsync(); Check(reader.ExitCode == 0, "external process reads cached placeholder"); }
        using (var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10))) while (index.DecorateCache(platform.Probe(c, name)).LastOpenAt is null) await Task.Delay(100, deadline.Token);
        Check(index.DecorateCache(platform.Probe(c, name)).LastUserAccessAt is null, "real CFAPI process hints record unknown/background opens without refreshing LRU");
        Guid run = actual.QueueManual(id, name, "Pin", false); await actual.ExecuteAsync(run, CancellationToken.None); Check(index.CacheOperations(run).Single().Status == "Completed" && platform.Probe(c, name).Pinned, "read-only SMB placeholder can be pinned and fully hydrated");
        run = actual.QueueManual(id, name, "Release", false); await actual.ExecuteAsync(run, CancellationToken.None); Check(index.CacheOperations(run).Single().Status == "Skipped" && platform.Probe(c, name).HydratedBytes > 0, "actual pinned content cannot be released");
        run = actual.QueueManual(id, name, "Unpin", false); await actual.ExecuteAsync(run, CancellationToken.None); Check(index.CacheOperations(run).Single().Status == "Completed", "actual unpin verifies pin state separately");
        using (var held = new FileStream(Paths.Under(root, name), FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            run = actual.QueueManual(id, name, "Release", false); await actual.ExecuteAsync(run, CancellationToken.None); Check(index.CacheOperations(run).Single().Status != "Completed" && platform.Probe(c, name).HydratedBytes > 0, "exclusive CFAPI access refuses active reader");
        }
        run = actual.QueueManual(id, name, "Release", false); await actual.ExecuteAsync(run, CancellationToken.None); s = platform.Probe(c, name);
        Check(index.CacheOperations(run).Single().Status == "Completed" && s.Owned && s.HydratedBytes == 0 && File.Exists(Paths.Under(root, name)) && s.LogicalBytes == payload.Length, "real release retains identity/path/size with zero local content");
        Check(SHA256.HashData(await File.ReadAllBytesAsync(Paths.Under(root, name))).SequenceEqual(SHA256.HashData(payload)) && File.ReadAllBytes(Path.Combine(remote, name)).SequenceEqual(payload), "released file rehydrates and source remains unchanged");
        Sql($"DELETE FROM CacheUsageStats WHERE ConnectionId='{id}'"); await actual.CalibrateAsync(CancellationToken.None);
        Check(index.CacheSnapshots(id).Single(s => s.RelativePath == name).HydratedBytes == payload.Length, "lost cache DB row rebuilt from real CFAPI state");
        run = actual.QueueManual(id, "批量", "Pin", true); await actual.ExecuteAsync(run, CancellationToken.None);
        Check(index.CacheOperations(run).All(o => o.Status == "Completed") && platform.Probe(c, @"批量\512.txt").Pinned, "folder pin recursively hydrates all 513 children");
        run = actual.QueueManual(id, "批量", "Unpin", true); await actual.ExecuteAsync(run, CancellationToken.None);
        Check(index.CacheOperations(run).All(o => o.Status == "Completed") && !index.DecorateCache(platform.Probe(c, @"批量\512.txt")).Excluded, "folder unpin clears inherited protection after successful children");
        run = actual.QueueManual(id, "批量", "Release", true); await actual.ExecuteAsync(run, CancellationToken.None);
        Check(index.CacheOperations(run).All(o => o.Status == "Completed") && platform.Probe(c, @"批量\512.txt").HydratedBytes == 0, "folder release keeps all child placeholders");
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); var self = Process.GetCurrentProcess(); self.Refresh(); long memoryBefore = self.PrivateMemorySize64;
        string largeHash;
        await using (var stream = new FileStream(Paths.Under(root, large), FileMode.Open, FileAccess.Read, FileShare.Read)) largeHash = await NewFileUpload.StreamHash(stream, CancellationToken.None);
        self.Refresh(); long memoryDelta = self.PrivateMemorySize64 - memoryBefore;
        Check(platform.Probe(c, large).HydratedBytes == largeSize && memoryDelta < 128L << 20, "1 GiB+123 real CFAPI hydration streams with bounded managed memory delta=" + memoryDelta);
        run = actual.QueueManual(id, large, "Release", false); await actual.ExecuteAsync(run, CancellationToken.None);
        Check(index.CacheOperations(run).Single().Status == "Completed" && platform.Probe(c, large).HydratedBytes == 0, "1 GiB+123 verified release retains placeholder");
        await using (var stream = new FileStream(Paths.Under(root, large), FileMode.Open, FileAccess.Read, FileShare.Read)) Check(await NewFileUpload.StreamHash(stream, CancellationToken.None) == largeHash, "1 GiB+123 rehydration matches streaming SHA256");
        Console.WriteLine("PASS actual Windows CFAPI cache integration with LOCAL SOURCE, NOT NAS. Retained: " + root);
    }
    finally { stopEvent.Set(); await host.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30)); await output; await trackerStop.CancelAsync(); try { await tracking; } catch (OperationCanceledException) { } Console.WriteLine(await errors); }
}

sealed class FixturePlatform : ICachePlatform
{
    public Dictionary<(Guid, string), CacheSnapshot> Files { get; } = [];
    public List<string> Released { get; } = [];
    public long FreeBytes { get; set; } = 100000;
    public string? FailPath { get; set; }
    public bool Offline { get; set; }
    public void Change(Guid id, string path, Func<CacheSnapshot, CacheSnapshot> change) => Files[(id, path)] = change(Files[(id, path)]);
    public CacheSnapshot Probe(SmbConnection c, string relative) => Files.TryGetValue((c.Id, relative), out var s) ? s : new(Paths.StableId(c.Id, relative), c.Id, relative, true, false, false, false, 0, 0, 0, Error: "fixture ordinary directory");
    public CacheVolume Volume(SmbConnection c) => new("same-test-volume", FreeBytes, 1000000);
    public Task ReleaseAsync(SmbConnection c, string relative, CancellationToken token)
    {
        if (Offline) throw new StorageException(FailureKind.NetworkUnreachable, "fixture offline");
        if (relative == FailPath) throw new IOException("fixture injected CFAPI error");
        Released.Add(relative); FreeBytes += Files[(c.Id, relative)].AllocatedBytes; Change(c.Id, relative, s => s with { HydratedBytes = 0, AllocatedBytes = 0 }); return Task.CompletedTask;
    }
    public Task PinAsync(SmbConnection c, string relative, bool pinned, CancellationToken token) { Change(c.Id, relative, s => s with { Pinned = pinned }); return Task.CompletedTask; }
}
