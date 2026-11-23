using CloudDrive.Storage;
using Microsoft.Data.Sqlite;
using System.IO.Compression;
using System.Text.Json;

int checks = 0;
void Check(bool condition, string name) { if (!condition) throw new Exception("FAIL " + name); checks++; Console.WriteLine("PASS " + name); }
string root = Path.Combine(Path.GetTempPath(), "CloudDriveLite-health-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
var index = new IndexStore(Path.Combine(root, "index.db"));
void Sql(string sql) { using var db = new SqliteConnection("Data Source=" + index.DatabasePath); db.Open(); using var command = db.CreateCommand(); command.CommandText = sql; command.ExecuteNonQuery(); }
var now = DateTimeOffset.UtcNow;
HealthResult Result(HealthStatus status, string code = "Offline", string module = "SMB", Guid? c = null) => new(module, "fixture", status, code, "专用测试异常", "确定性故障注入；不是NAS", "检查实际配置", now, ConnectionId: c, Recovery: ["Reconnect"]);
Check(HealthRules.Aggregate([Result(HealthStatus.Degraded)]) == HealthStatus.Degraded, "one disconnected storage degrades rather than making all modules Critical");
Check(HealthRules.Aggregate([]) == HealthStatus.Unknown, "no evidence cannot display green");
Check(HealthRules.Fresh(Result(HealthStatus.Healthy) with { CheckedAt = now.AddMinutes(-3) }, now, 60).Status == HealthStatus.Unknown, "expired successful checks become Unknown");
Check(HealthRules.StallThreshold(100L << 30, 0, TimeSpan.Zero, HealthModules.Default("Sync")) > TimeSpan.FromMinutes(30), "large slow transfers get a bounded size-sensitive stall allowance");
Check(index.ReadHealth().Modules.All(r => r.Status == HealthStatus.Unknown), "fresh health database has no invented successes");
var batchIndex = new IndexStore(Path.Combine(root, "batches.db")); Guid firstConnection = Guid.NewGuid(), secondConnection = Guid.NewGuid();
batchIndex.RecordHealth(Result(HealthStatus.Unknown, "ProbeTimeout"));
batchIndex.RecordHealthBatch([Result(HealthStatus.Healthy, "Connected", c: firstConnection), Result(HealthStatus.Degraded, c: secondConnection)]);
Check(batchIndex.ReadHealth().Modules.Count(r => r.ModuleName == "SMB") == 2 && !batchIndex.ReadHealth().Modules.Any(r => r.Code == "ProbeTimeout"), "completed multi-connection batch replaces old global timeout atomically");
batchIndex.RecordHealth(Result(HealthStatus.Unknown, "ProbeTimeout"));
Check(batchIndex.ReadHealth().Modules.Single(r => r.ModuleName == "SMB").Code == "ProbeTimeout", "new module timeout suppresses older per-connection green results");
batchIndex.RecordHealthBatch([Result(HealthStatus.Healthy, "Connected", c: firstConnection)]);
Check(!batchIndex.ReadHealth().Modules.Any(r => r.ConnectionId == secondConnection) && batchIndex.ReadHealth().Incidents.Any(i => i.Status == "Open"), "removed connection disappears from latest module batch without falsely resolving its incident");
index.RecordHealth(Result(HealthStatus.Degraded)); index.RecordHealth(Result(HealthStatus.Degraded) with { CheckedAt = now.AddSeconds(1) });
var incident = index.ReadHealth().Incidents.Single();
Check(incident.Occurrences == 2 && incident.Status == "Open", "repeated same-resource faults merge durably");
index.RecordHealth(Result(HealthStatus.Unknown));
Check(index.ReadHealth().Incidents.Single().Status == "Open", "Unknown never resolves an existing fault");
index.RecordHealth(Result(HealthStatus.Healthy));
Check(index.ReadHealth().Incidents.Single().ResolvedAt is not null, "positive independent evidence resolves fault with time");
index.RecordHealth(Result(HealthStatus.Degraded));
Check(index.ReadHealth().Incidents.Count == 2 && index.ReadHealth().Incidents.Count(i => i.Status == "Open") == 1, "recurrence after recovery creates a new incident");
var connection = new SmbConnection(Guid.NewGuid(), "health fixture", "nas.invalid", 445, "test", "", "fixture", "", false, Path.Combine(root, "not-created")); index.Save(connection);
Sql("PRAGMA user_version=4; DROP TABLE FolderMappings; DROP TABLE StorageConnections; DROP TABLE HealthCheckRecords; DROP TABLE HealthIncidents; DROP TABLE RecoveryActions; DROP TABLE HealthConfiguration;");
index = new IndexStore(index.DatabasePath);
Check(index.Connections().Single().Id == connection.Id && index.ReadHealth().Modules.Count == 8, "actual v4 to current migration preserves connection and adds health schema");
Check(Directory.GetFiles(Path.Combine(root, "Backups"), "before-v4-*.db").Length == 1, "health migration makes real online pre-migration backup");
Check(WindowsHealthProbe.CheckDatabase(index.DatabasePath, false).Status == HealthStatus.Healthy, "real SQLite schema and rollback-only write transaction pass");
Check(WindowsHealthProbe.CheckDatabase(index.DatabasePath, true).Status == HealthStatus.Healthy, "actual SQLite integrity check passes");
Check(!File.Exists(Path.Combine(root, "absent.db")) && WindowsHealthProbe.CheckDatabase(Path.Combine(root, "absent.db"), false).Code == "DatabaseMissing", "missing database probe never silently creates a replacement");
var missingStore = new IndexStore(Path.Combine(root, "disappeared.db")); SqliteConnection.ClearAllPools(); File.Delete(missingStore.DatabasePath);
try { _ = missingStore.ReadHealth(); throw new Exception("FAIL missing health store"); } catch (SqliteException) { Check(!File.Exists(missingStore.DatabasePath), "health persistence refuses to recreate a database that disappeared after initialization"); }
string corrupt = Path.Combine(root, "corrupt.db"); File.WriteAllText(corrupt, "not a database");
Check(WindowsHealthProbe.CheckDatabase(corrupt, true).Status == HealthStatus.Critical && File.ReadAllText(corrupt) == "not a database", "actual corrupt SQLite produces evidence without modifying data");
Sql("DROP INDEX sync_due;");
Check(WindowsHealthProbe.CheckDatabase(index.DatabasePath, false).Code == "SchemaObjectsMissing", "missing required index diagnosed without rebuilding it");
Sql("CREATE INDEX sync_due ON SyncTasks(ConnectionId,TaskStatus,NextRetryAt);");
foreach (var c in index.HealthConfigurations()) index.SetHealthConfiguration(c with { Enabled = c.ModuleName == "SMB", TimeoutSeconds = 1 });
var blocking = new BlockingProbe(); var monitor = new HealthMonitor(index, blocking, (_, _) => Task.CompletedTask);
var checkingTask = monitor.CheckAsync(true, CancellationToken.None); await Task.Delay(50);
Check(monitor.Snapshot.Modules.Any(r => r.Status == HealthStatus.Checking), "actual in-flight check publishes Checking without persisting success");
await checkingTask;
Check(monitor.Snapshot.Modules.Single().Status == HealthStatus.Unknown, "timed-out real asynchronous probe becomes Unknown");
await monitor.CheckAsync(true, CancellationToken.None);
Check(blocking.Calls == 1 && monitor.Snapshot.Modules.Single().Code == "PreviousProbeRunning", "hung checker cannot create unbounded duplicate probes");
blocking.Release.TrySetResult([Result(HealthStatus.Healthy)]); await Task.Delay(50);
var recoveryProbe = new MutableProbe(Result(HealthStatus.Warning, "StatsMismatch", "Cache") with { Recovery = ["RecalibrateCache"] });
foreach (var c in index.HealthConfigurations()) index.SetHealthConfiguration(c with { Enabled = c.ModuleName == "Cache" });
int executed = 0; var recoveryMonitor = new HealthMonitor(index, recoveryProbe, (_, _) => { executed++; return Task.CompletedTask; });
await recoveryMonitor.CheckAsync(true, CancellationToken.None);
var active = recoveryMonitor.Snapshot.Incidents.Single(i => i.Status == "Open");
try { recoveryMonitor.Plan(active.IncidentId, "DeleteFiles"); throw new Exception("FAIL forbidden"); } catch (IOException) { Check(executed == 0, "destructive action is absent from repair whitelist"); }
await recoveryMonitor.RecoverAsync(active.IncidentId, "RecalibrateCache", false, false, CancellationToken.None);
Check(executed == 1 && index.ReadHealth().Recoveries.Single().Status == "AwaitingVerification" && index.ReadHealth().Incidents.Single(i => i.IncidentId == active.IncidentId).Status == "Open", "repair submission is not reported as recovery before evidence");
try { await recoveryMonitor.RecoverAsync(active.IncidentId, "RecalibrateCache", false, false, CancellationToken.None); throw new Exception("FAIL duplicate repair"); } catch (IOException) { Check(executed == 1, "pending repair cannot execute twice"); }
recoveryProbe.Result = recoveryProbe.Result with { Status = HealthStatus.Healthy, CheckedAt = DateTimeOffset.UtcNow };
await recoveryMonitor.CheckAsync(true, CancellationToken.None);
Check(index.ReadHealth().Recoveries.Single().Status == "Verified", "new independent successful probe verifies the repair");
var remoteFault = Result(HealthStatus.Degraded, c: connection.Id); var confirming = new MutableProbe(remoteFault);
foreach (var c in index.HealthConfigurations()) index.SetHealthConfiguration(c with { Enabled = c.ModuleName == "SMB" });
var confirmedMonitor = new HealthMonitor(index, confirming, (_, _) => { executed++; throw new IOException("injected error"); }); await confirmedMonitor.CheckAsync(true, CancellationToken.None);
string id = confirmedMonitor.Snapshot.Incidents.Single(i => i.Status == "Open").IncidentId;
try { await confirmedMonitor.RecoverAsync(id, "Reconnect", false, false, CancellationToken.None); throw new Exception("FAIL no confirmation"); } catch (IOException) { Check(executed == 1, "cancelled or unconfirmed repair has no business effect"); }
try { await confirmedMonitor.RecoverAsync(id, "Reconnect", true, false, CancellationToken.None); throw new Exception("FAIL injected recovery"); } catch (IOException) { Check(index.ReadHealth().Recoveries.Any(a => a.Status == "Failed") && index.ReadHealth().Incidents.Any(i => i.IncidentId == id && i.Status == "Open"), "repair failure retains original incident and failure journal"); }
Sql("UPDATE HealthCheckRecords SET CheckedAt='2000-01-01T00:00:00.0000000+00:00';"); index.PruneHealth(DateTimeOffset.UtcNow);
Check(index.HealthHistory("Cache:global").Count == 1 && index.ReadHealth().Incidents.Any(i => i.Status == "Open"), "retention prunes old history but preserves latest module evidence and unresolved incidents");
var oldAttributes = File.GetAttributes(index.DatabasePath);
try { File.SetAttributes(index.DatabasePath, oldAttributes | FileAttributes.ReadOnly); Check(WindowsHealthProbe.CheckDatabase(index.DatabasePath, false).Code == "DatabaseReadOnly", "actual Windows read-only database attribute diagnosed without clearing it"); }
finally { File.SetAttributes(index.DatabasePath, oldAttributes); }
using (var locked = new SqliteConnection("Data Source=" + index.DatabasePath))
{
    locked.Open(); using var transaction = locked.BeginTransaction(deferred: false);
    Check(WindowsHealthProbe.CheckDatabase(index.DatabasePath, false).Status == HealthStatus.Unknown, "actual SQLite writer contention is Unknown rather than database corruption");
}
var fakeCache = new MetadataFixture(); DateTimeOffset observed = now;
var product = new ProductSnapshot(Environment.ProcessId, now, "fixture", true, [], 0, 0, 0, 0, 0, 0, 0, false, false);
var windows = new WindowsHealthProbe(index, () => product, fakeCache, () => observed);
var cfapi = await windows.CheckAsync("CFAPI", HealthModules.Default("CFAPI"), CancellationToken.None);
Check(cfapi.Single().Code == "RootInvalid" && !Directory.Exists(connection.SyncRoot), "actual Windows CFAPI reports missing root without registering or creating it");
var tcpFixture = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0); tcpFixture.Start();
var tcpIndex = new IndexStore(Path.Combine(root, "tcp.db"));
var tcpConnection = connection with { Id = Guid.NewGuid(), Server = "127.0.0.1", Port = ((System.Net.IPEndPoint)tcpFixture.LocalEndpoint).Port, SyncRoot = Path.Combine(root, "tcp-root") }; tcpIndex.Save(tcpConnection);
var tcpProbe = new WindowsHealthProbe(tcpIndex, () => product, fakeCache);
Check((await tcpProbe.CheckAsync("SMB", HealthModules.Default("SMB"), CancellationToken.None)).Single().Code == "SessionUnverified", "real loopback DNS/TCP succeeds without inventing SMB authentication or creating a mapping");
tcpFixture.Stop();
Check((await tcpProbe.CheckAsync("SMB", HealthModules.Default("SMB"), CancellationToken.None)).Single().Code == "PortClosed", "actual closed Windows TCP endpoint produces a per-connection port diagnostic");
Guid taskId = index.EnqueueNewFile(connection.Id, "stalled.txt"); _ = index.ClaimNewUpload(connection.Id);
var threshold = HealthModules.Default("Sync") with { WarningThreshold = 30, CriticalThreshold = 60 };
_ = await windows.CheckAsync("Sync", threshold, CancellationToken.None); observed = observed.AddMinutes(2);
Check((await windows.CheckAsync("Sync", threshold, CancellationToken.None)).Single().Code == "ProgressStalled", "persisted Processing task with unchanged observed progress is flagged using injected clock");
index.UploadProgress(taskId, 10);
Check((await windows.CheckAsync("Sync", threshold, CancellationToken.None)).Single().Code != "ProgressStalled", "real SQLite progress update clears the suspected-stall rule");
index.RecoverSyncTasks(connection.Id); index.ControlSyncTask(connection.Id, taskId, SyncTaskStatus.Cancelled);
var target = connection with { Id = Guid.NewGuid(), Name = "target fixture", Server = "target.invalid", SyncRoot = Path.Combine(root, "other-root") }; index.Save(target);
var policy = new ArchivePolicy(Guid.NewGuid(), "stage fixture", connection.Id, target.Id); index.SaveArchivePolicy(policy);
var remote = new RemoteEntry(Paths.StableId(connection.Id, "archive.bin"), connection.Id, "archive.bin", false, 100, 1, 1, "fixture-id");
Guid archiveRun = index.QueueArchive(policy, [new(remote, "archive.bin", true, "explicit fixture")]);
var item = index.ArchiveItems(archiveRun).Single(); index.SaveArchiveItem(item with { Status = ArchiveStatus.TargetCommitted }, "FixtureCommitted");
index.SetArchiveRun(archiveRun, "Processing");
var activeStage = (await windows.CheckAsync("Archive", HealthModules.Default("Archive"), CancellationToken.None)).Single();
Check(activeStage.Code == "CommittedPendingSource" && activeStage.Recovery?.Length == 0, "active committed archive transaction offers no parallel resume action");
index.SetArchiveRun(archiveRun, "RecoveryRequired");
var stage = (await windows.CheckAsync("Archive", HealthModules.Default("Archive"), CancellationToken.None)).Single();
Check(stage.Code == "CommittedPendingSource" && stage.TaskId == archiveRun, "actual archive journal stage is pending source completion rather than generic failure");
foreach (var c in index.HealthConfigurations()) index.SetHealthConfiguration(c with { Enabled = c.ModuleName == "Archive" });
var archiveMonitor = new HealthMonitor(index, new MutableProbe(stage), (_, _) => { executed++; return Task.CompletedTask; }); await archiveMonitor.CheckAsync(true, CancellationToken.None);
var archiveIncident = archiveMonitor.Snapshot.Incidents.Single(i => i.ModuleName == "Archive:global" && i.Status == "Open");
int beforeArchive = executed;
try { await archiveMonitor.RecoverAsync(archiveIncident.IncidentId, "ResumeArchive", true, false, CancellationToken.None); throw new Exception("FAIL data authority"); } catch (IOException) { Check(executed == beforeArchive, "archive resume cannot run without separate explicit source-deletion authorization"); }
var noticePolicy = new HealthNotificationPolicy(); var noticeState = index.ReadHealth();
Check(noticePolicy.Select(noticeState, false, true).Count == 0, "ordinary warning notifications default off");
var notices = noticePolicy.Select(noticeState, true, true); foreach (var notice in notices) noticePolicy.Delivered(notice);
Check(noticePolicy.Select(noticeState, true, true).Count == 0, "same unresolved incident is notified only once");
index.RecordHealth(stage with { Status = HealthStatus.Healthy, CheckedAt = DateTimeOffset.UtcNow });
var restoredNotices = noticePolicy.Select(index.ReadHealth(), true, true);
Check(restoredNotices.Any(n => n.Recovered), "positive recovery produces one recovery notification");
foreach (var notice in restoredNotices) noticePolicy.Delivered(notice);
Check(!noticePolicy.Select(index.ReadHealth(), true, true).Any(n => n.Recovered), "recovery notification also deduplicates");
index.RecordHealth(Result(HealthStatus.Critical, "CriticalFixture", "Database"));
Check(noticePolicy.Select(index.ReadHealth(), false, false).Any(n => n.Critical), "critical incident is eligible for notification with warnings disabled");
string noticePath = Path.Combine(root, "notices.json"); string stillOpen = Guid.NewGuid().ToString();
var ledger = new Dictionary<string, DateTimeOffset> { [stillOpen + ":Open"] = now.AddYears(-1) };
for (int n = 0; n < 1200; n++) ledger[Guid.NewGuid() + ":Resolved"] = now;
File.WriteAllText(noticePath, JsonSerializer.Serialize(ledger));
var boundedPolicy = new HealthNotificationPolicy(noticePath, () => new HashSet<string> { stillOpen });
_ = boundedPolicy.Select(new(now, HealthStatus.Unknown, [], [], [], 0), false, true);
var boundedLedger = JsonSerializer.Deserialize<Dictionary<string, DateTimeOffset>>(File.ReadAllText(noticePath))!;
Check(boundedLedger.Count == 1001 && boundedLedger.ContainsKey(stillOpen + ":Open"), "terminal notification history is bounded while an old open fault outside UI window keeps dedup marker");
var unsafeSnapshot = noticeState with { Modules = [Result(HealthStatus.Warning) with { Evidence = "<script>alert(1)</script> password=fixture-secret \\\\nas.invalid\\private " + new string('测', 5000) }] };
var preview = HealthReports.Preview(unsafeSnapshot); string json = HealthReports.Json(preview);
using (var parsed = JsonDocument.Parse(json)) Check(json.Length > 4096 && !json.Contains("fixture-secret") && !json.Contains("nas.invalid"), "large sanitized report remains valid JSON without global string truncation");
string html = HealthReports.Html(preview); Check(!html.Contains("<script>") && html.Contains("&lt;script&gt;"), "HTML report encodes diagnostic text rather than executing it");
string zipPath = HealthReports.Export(preview, Path.Combine(root, "report.zip"));
using (var zip = ZipFile.OpenRead(zipPath)) Check(zip.Entries.Select(e => e.FullName).Order().SequenceEqual(new[] { "health.html", "health.json" }), "diagnostic ZIP contains only previewed sanitized JSON and HTML");
var fileDiagnosis = await HealthDiagnosisEngine.DiagnoseFileAsync(index, connection, "no-file.txt", index.ReadHealth(), fakeCache, CancellationToken.None);
Check(fileDiagnosis.Facts.Any(f => f.Code == "MappingMissing") && !Directory.Exists(connection.SyncRoot), "file diagnosis identifies absent local/mapping facts without creating or changing files");
var automaticIndex = new IndexStore(Path.Combine(root, "automatic.db"));
foreach (var c in automaticIndex.HealthConfigurations()) automaticIndex.SetHealthConfiguration(c with { Enabled = c.ModuleName == "Cache", AutoRecoveryEnabled = true });
var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); int automaticCalls = 0;
var automaticMonitor = new HealthMonitor(automaticIndex, new MutableProbe(Result(HealthStatus.Warning, "StatsMismatch", "Cache") with { Recovery = ["RecalibrateCache"] }), async (_, token) => { Interlocked.Increment(ref automaticCalls); entered.TrySetResult(); await release.Task.WaitAsync(token); });
await automaticMonitor.CheckAsync(true, CancellationToken.None); await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
await automaticMonitor.CheckAsync(true, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
Check(automaticCalls == 1 && automaticIndex.ReadHealth().Recoveries.Count(r => r.Status == "Running") == 1, "blocked optional auto calibration neither blocks checks nor starts duplicate recoveries");
release.TrySetResult();
for (int n = 0; n < 100 && !automaticIndex.ReadHealth().Recoveries.Any(r => r.Status == "AwaitingVerification"); n++) await Task.Delay(20);
Check(automaticIndex.ReadHealth().Recoveries.Any(r => r.Status == "AwaitingVerification"), "asynchronous auto repair still waits for independent positive verification");
Console.WriteLine($"PASS {checks} health checks; actual Windows/SQLite and explicit probe injection; NOT NAS. fixture={root}");

sealed class BlockingProbe : IHealthProbe
{
    public int Calls; public readonly TaskCompletionSource<IReadOnlyList<HealthResult>> Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public Task<IReadOnlyList<HealthResult>> CheckAsync(string module, HealthConfiguration configuration, CancellationToken token) { Calls++; return Release.Task; }
}
sealed class MutableProbe(HealthResult result) : IHealthProbe
{
    public HealthResult Result = result;
    public Task<IReadOnlyList<HealthResult>> CheckAsync(string module, HealthConfiguration configuration, CancellationToken token) => Task.FromResult<IReadOnlyList<HealthResult>>([Result with { CheckedAt = DateTimeOffset.UtcNow }]);
}
sealed class MetadataFixture : ICachePlatform
{
    public CacheSnapshot Probe(SmbConnection c, string relative) => new(Paths.StableId(c.Id, relative), c.Id, relative, false, true, true, false, 0, 0, 0);
    public CacheVolume Volume(SmbConnection c) => throw new NotSupportedException("not used by health fixture");
    public Task ReleaseAsync(SmbConnection c, string p, CancellationToken t) => throw new Exception("health must never release");
    public Task PinAsync(SmbConnection c, string p, bool b, CancellationToken t) => throw new Exception("health must never pin/hydrate");
}
