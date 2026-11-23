using CloudDrive.Storage;
using Microsoft.Data.Sqlite;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;

var fixture = Path.GetFullPath(Path.Combine("artifacts", "tests", "phase3-" + Guid.NewGuid().ToString("N")));
Directory.CreateDirectory(fixture);
int passed = 0;
void Check(bool value, string name) { if (!value) throw new Exception("FAIL " + name); passed++; Console.WriteLine("PASS " + name); }
var id = Guid.NewGuid();
string remote = Path.Combine(fixture, "remote"), cloud = Path.Combine(fixture, "cloud"); Directory.CreateDirectory(remote); Directory.CreateDirectory(cloud);
var config = new SmbConnection(id, "phase3 fixture", "127.0.0.1", 445, "test", "", "user", "", false, cloud);
var index = new IndexStore(Path.Combine(fixture, "index.db")); index.Save(config);
Check(!index.SyncSettings(id).Enabled, "upload disabled by default");
index.SetSyncSettings(id, new(true, 3, 200, 5));
string old = "existing.txt"; File.WriteAllText(Path.Combine(remote, old), "original");
await new RemoteScanner(index).ScanAsync(id, new FileSystemSource(id, remote), CancellationToken.None);
using (var db = new SqliteConnection("Data Source=" + index.DatabasePath))
{
    db.Open(); using var command = db.CreateCommand();
    command.CommandText = "DROP TABLE SyncSettings; DROP TABLE FileVersions; DROP TABLE SyncTasks; DROP TABLE FileChangeEvents; DROP TABLE ConflictRecords; DROP TABLE DeletedFileRecords; PRAGMA user_version=1;"; command.ExecuteNonQuery();
}
index = new IndexStore(index.DatabasePath);
Check(index.Get(id) == config && index.Count(id) == 1 && index.FindPath(id, old) is not null, "v1 additive migration preserves configuration/index");
index.SetSyncSettings(id, new(true, 3, 200, 5));
string futureDb = Path.Combine(fixture, "future.db");
using (var db = new SqliteConnection("Data Source=" + futureDb)) { db.Open(); using var cmd = db.CreateCommand(); cmd.CommandText = "PRAGMA user_version=99"; cmd.ExecuteNonQuery(); }
try { _ = new IndexStore(futureDb); throw new Exception("FAIL future schema"); } catch (InvalidOperationException) { Check(true, "future schema rejected without downgrade"); }
var adopter = new FixtureFinalizer();
NewFileUpload Uploader(INewFileFinalizer? finalizer = null) => new(index, config, _ => Task.FromResult(remote), finalizer ?? adopter);
SyncTask NewTask(string relative, string content)
{
    File.WriteAllText(Paths.Under(cloud, relative), content);
    index.EnqueueNewFile(id, relative); return index.ClaimNewUpload(id) ?? throw new Exception("task not claimed");
}
var tasks = await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => Task.Run(() => index.EnqueueNewFile(id, "中文 # & ' 文件.txt"))));
Check(tasks.Distinct().Count() == 1, "concurrent duplicate hints merge durably");
File.WriteAllText(Paths.Under(cloud, "中文 # & ' 文件.txt"), "中文 payload");
var first = index.ClaimNewUpload(id)!;
await Uploader().ExecuteAsync(first, CancellationToken.None);
Check(File.ReadAllText(Paths.Under(remote, first.TargetPath)) == "中文 payload", "temp upload + SMB-style reread + no-replace handle rename");
Check(index.SyncTasks(id).Single(t => t.Id == first.Id).TaskStatus == SyncTaskStatus.Completed && index.FindPath(id, first.TargetPath) is not null, "verified upload persists mapping/base version/completion");
Check(!File.Exists(Paths.Under(remote, first.TempPath!)) && adopter.Completed == 1, "temp committed before finalizer and removed by rename");
var collision = NewTask("collision.txt", "local"); File.WriteAllText(Paths.Under(remote, collision.TargetPath), "remote-other-device");
try { await Uploader().ExecuteAsync(collision, CancellationToken.None); throw new Exception("FAIL collision"); }
catch (SyncConflictException e) { index.FailUpload(collision, e.Message, true); }
Check(File.ReadAllText(Paths.Under(remote, collision.TargetPath)) == "remote-other-device" && File.ReadAllText(Paths.Under(cloud, collision.SourcePath)) == "local", "collision preserves both files");
var crash = NewTask("crash.txt", "commit before SQLite completion");
try { await Uploader(new FixtureFinalizer { FailCompletion = true }).ExecuteAsync(crash, CancellationToken.None); throw new Exception("FAIL crash fixture"); }
catch (FixtureCrash) { }
Check(index.SyncTasks(id).Single(t => t.Id == crash.Id).Phase == "Committed", "remote commit journal survives completion crash");
index.RecoverSyncTasks(id); var recovered = index.ClaimNewUpload(id)!;
await Uploader().ExecuteAsync(recovered, CancellationToken.None);
Check(index.SyncTasks(id).Single(t => t.Id == crash.Id).TaskStatus == SyncTaskStatus.Completed, "restart recognizes exact committed ID and hash without reupload");
var changed = NewTask("changed-after-commit.txt", "local first version");
try { await Uploader(new FixtureFinalizer { FailCompletion = true }).ExecuteAsync(changed, CancellationToken.None); } catch (FixtureCrash) { }
File.WriteAllText(Paths.Under(remote, changed.TargetPath), "other device replaced content");
index.RecoverSyncTasks(id); changed = index.ClaimNewUpload(id)!;
try { await Uploader().ExecuteAsync(changed, CancellationToken.None); throw new Exception("FAIL changed recovery"); }
catch (SyncConflictException e) { index.FailUpload(changed, e.Message, true); Check(true, "recovery refuses changed remote instead of blind overwrite"); }
var occupied = NewTask("occupied.txt", "writer holds file");
using (var writer = new FileStream(Paths.Under(cloud, occupied.SourcePath), FileMode.Open, FileAccess.Write, FileShare.ReadWrite))
{
    try { await Uploader().ExecuteAsync(occupied, CancellationToken.None); throw new Exception("FAIL occupied"); }
    catch (System.ComponentModel.Win32Exception e) when (e.NativeErrorCode == 32) { index.FailUpload(occupied, "writer open", false); Check(true, "open writer prevents upload"); }
}
index.ControlSyncTask(id, occupied.Id, SyncTaskStatus.Paused);
Check(index.ClaimNewUpload(id) is null, "paused tasks cannot be claimed");
index.ControlSyncTask(id, occupied.Id, SyncTaskStatus.Pending);
await Uploader().ExecuteAsync(index.ClaimNewUpload(id)!, CancellationToken.None);
Check(index.SyncTasks(id).Single(t => t.Id == occupied.Id).TaskStatus == SyncTaskStatus.Completed, "manual resume after writer closes");
var orphan = NewTask("orphan.txt", "safe local"); File.WriteAllText(Paths.Under(remote, orphan.TempPath!), "unproven temp");
try { await Uploader().ExecuteAsync(orphan, CancellationToken.None); throw new Exception("FAIL orphan"); }
catch (SyncConflictException e) { index.FailUpload(orphan, e.Message, true); Check(File.ReadAllText(Paths.Under(remote, orphan.TempPath!)) == "unproven temp", "unproven temp never truncated"); }
await new RemoteScanner(index).ScanAsync(id, new FileSystemSource(id, remote), CancellationToken.None);
Check(index.FindPath(id, orphan.TempPath!) is null, "scanner excludes exact journal-owned temp paths");
string stranger = ".clouddrive-upload-not-owned.tmp"; File.WriteAllText(Paths.Under(remote, stranger), "legitimate remote file");
await new RemoteScanner(index).ScanAsync(id, new FileSystemSource(id, remote), CancellationToken.None);
Check(index.FindPath(id, stranger) is not null, "scanner retains similarly named unowned remote files");
var cancelled = NewTask("cancelled.txt", "local retained");
using (var stop = new CancellationTokenSource()) { stop.Cancel(); try { await Uploader().ExecuteAsync(cancelled, stop.Token); } catch (OperationCanceledException) { } }
index.RecoverSyncTasks(id);
Check(index.SyncTasks(id).Single(t => t.Id == cancelled.Id).TaskStatus == SyncTaskStatus.Pending && File.Exists(Paths.Under(cloud, cancelled.SourcePath)), "cancellation/restart retains local file and task");
index.ControlSyncTask(id, cancelled.Id, SyncTaskStatus.Cancelled);
File.WriteAllText(Path.Combine(cloud, "lost-event.txt"), "no watcher hint");
var sync = new NewFileSyncAgent(index, config, _ => Task.FromResult(remote), adopter);
await sync.ReconcileAsync(CancellationToken.None);
Check(index.SyncTasks(id).Any(t => t.SourcePath == "lost-event.txt"), "reconciliation discovers files without watcher events");
var hintTask = index.ClaimNewUpload(id)!;
await Uploader().ExecuteAsync(hintTask, CancellationToken.None);
await CheckAutomaticAgentAsync();
string taskLog = Path.Combine(fixture, "task-execution.log");
File.WriteAllLines(taskLog, index.Logs());
Console.WriteLine($"Task execution log: {taskLog}");
if (args.Contains("--cfapi", StringComparer.Ordinal)) await CheckCfapiAsync();
Console.WriteLine($"PASS {passed} phase 3 first-slice checks. LOCAL FIXTURES, NOT NAS. Retained: {fixture}");

async Task CheckAutomaticAgentAsync()
{
    var autoId = Guid.NewGuid(); string autoLocal = Path.Combine(fixture, "auto-local"), autoRemote = Path.Combine(fixture, "auto-remote");
    Directory.CreateDirectory(autoLocal); Directory.CreateDirectory(autoRemote);
    var automaticConfig = config with { Id = autoId, SyncRoot = autoLocal }; index.Save(automaticConfig); index.SetSyncSettings(autoId, new(true, 3, 200, 5));
    index.EnqueueNewFile(autoId, "auto0.txt");
    try { index.Save(automaticConfig with { Share = "another-target" }); throw new Exception("FAIL queued source change"); }
    catch (ArgumentException) { Check(true, "queued-only connection cannot be redirected to another source"); }
    var threeConcurrent = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); int active = 0;
    var agent = new NewFileSyncAgent(index, automaticConfig, async cancellation =>
    {
        if (Interlocked.Increment(ref active) >= 3) threeConcurrent.TrySetResult();
        await threeConcurrent.Task.WaitAsync(cancellation); return autoRemote;
    }, new FixtureFinalizer());
    // Force bounded hint overflow. Startup/overflow reconciliation must discover files even before watcher setup.
    for (int i = 0; i < 4100; i++) agent.Hint("SyntheticOverflowHint", null);
    for (int i = 0; i < 3; i++) await File.WriteAllTextAsync(Path.Combine(autoLocal, $"auto{i}.txt"), "automatic " + i);
    using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(60));
    Task run = Task.Run(() => agent.RunAsync(stop.Token), stop.Token);
    try
    {
        await threeConcurrent.Task.WaitAsync(TimeSpan.FromSeconds(45));
        Check(true, "three different files execute concurrently");
        while (index.SyncTasks(autoId).Count(t => t.TaskStatus == SyncTaskStatus.Completed) != 3)
        {
            if (run.IsCompleted) await run;
            await Task.Delay(100, stop.Token);
        }
        Check(Enumerable.Range(0, 3).All(i => File.ReadAllText(Path.Combine(autoRemote, $"auto{i}.txt")) == "automatic " + i), "automatic watcher/reconciliation to durable queue to upload");
        Check(index.SyncTasks(autoId).Count == 3 && index.Logs().Any(l => l.Contains("watcher-overflow", StringComparison.Ordinal)), "overflow reconciliation and self-generated event deduplication");
    }
    finally { await stop.CancelAsync(); try { await run; } catch (OperationCanceledException) { } }
}

async Task CheckCfapiAsync()
{
    string native = Path.GetFullPath(Path.Combine("artifacts", "bin", "CloudProviderHost.exe"));
    var nativeId = Guid.NewGuid(); string nativeRoot = Path.Combine(fixture, "native-cloud");
    var nativeConfig = config with { Id = nativeId, SyncRoot = nativeRoot }; index.Save(nativeConfig); index.SetSyncSettings(nativeId, new(true, 3, 200, 5));
    await new RemoteScanner(index).ScanAsync(nativeId, new FileSystemSource(nativeId, remote), CancellationToken.None);
    await using var bridge = new SmbBridge(index, nativeConfig, _ => Task.FromResult<IRemoteSource>(new FileSystemSource(nativeId, remote))); bridge.Start();
    string stopName = AgentRuntime.EventName(nativeId, "Stop"), refreshName = AgentRuntime.EventName(nativeId, "Refresh");
    using var stopEvent = new EventWaitHandle(false, EventResetMode.ManualReset, stopName);
    var start = new ProcessStartInfo(native) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
    foreach (string arg in new[] { "run-smb", "--source", nativeId.ToString("N"), "--root", nativeRoot, "--pipe", bridge.PipeName, "--agent-pid", Environment.ProcessId.ToString(), "--stop-event", stopName, "--refresh-event", refreshName }) start.ArgumentList.Add(arg);
    using var process = Process.Start(start) ?? throw new IOException("Host failed");
    var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    Task stdout = Task.Run(async () => { string? line; while ((line = await process.StandardOutput.ReadLineAsync()) is not null) { Console.WriteLine(line); if (line.StartsWith("READY", StringComparison.Ordinal)) ready.TrySetResult(); } });
    Task<string> stderr = process.StandardError.ReadToEndAsync();
    try
    {
        await ready.Task.WaitAsync(TimeSpan.FromSeconds(30));
        string relative = "真实CFAPI新增-" + Guid.NewGuid().ToString("N") + ".txt"; File.WriteAllText(Paths.Under(nativeRoot, relative), "CFAPI upload adopted");
        index.EnqueueNewFile(nativeId, relative); var task = index.ClaimNewUpload(nativeId)!;
        try { await new NewFileUpload(index, nativeConfig, _ => Task.FromResult(remote), new AfterCfCrashFinalizer()).ExecuteAsync(task, CancellationToken.None); }
        catch (FixtureCrash) { }
        Check((File.GetAttributes(Paths.Under(nativeRoot, relative)) & FileAttributes.ReadOnly) != 0, "completed new additions retain stage-2 read-only protection");
        index.RecoverSyncTasks(nativeId); task = index.ClaimNewUpload(nativeId)!;
        await new NewFileUpload(index, nativeConfig, _ => Task.FromResult(remote), new CfNewFileFinalizer()).ExecuteAsync(task, CancellationToken.None);
        Check(index.SyncTasks(nativeId).Single().TaskStatus == SyncTaskStatus.Completed, "real CFAPI conversion + USN-guarded InSync after verified upload (local fixture)");
        var state = new ProcessStartInfo(native) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (string arg in new[] { "status", "--root", nativeRoot, "--path", relative }) state.ArgumentList.Add(arg);
        using var statusProcess = Process.Start(state)!; string output = await statusProcess.StandardOutput.ReadToEndAsync(); string error = await statusProcess.StandardError.ReadToEndAsync(); await statusProcess.WaitForExitAsync(); Console.WriteLine(output + error);
        using var json = JsonDocument.Parse(output.Trim()); Check(statusProcess.ExitCode == 0 && json.RootElement.GetProperty("inSync").GetBoolean(), "native host recognizes reused identity and InSync state");
    }
    finally { stopEvent.Set(); await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30)); await stdout; Console.WriteLine(await stderr); }
}

sealed class FixtureCrash : Exception;
sealed class FixtureFinalizer : INewFileFinalizer
{
    public bool FailCompletion { get; init; }
    public int Completed { get; private set; }
    public void ValidateRoot(SmbConnection connection) { }
    public void ValidateLocal(SmbConnection connection, FileStream local, string relative) { }
    public void Complete(SmbConnection connection, FileStream local, RemoteEntry committed) { if (FailCompletion) throw new FixtureCrash(); Completed++; }
}
sealed class AfterCfCrashFinalizer : INewFileFinalizer
{
    private readonly CfNewFileFinalizer inner = new();
    public void ValidateRoot(SmbConnection c) => inner.ValidateRoot(c);
    public void ValidateLocal(SmbConnection c, FileStream stream, string relative) => inner.ValidateLocal(c, stream, relative);
    public void Complete(SmbConnection c, FileStream stream, RemoteEntry remote) { inner.Complete(c, stream, remote); throw new FixtureCrash(); }
}
