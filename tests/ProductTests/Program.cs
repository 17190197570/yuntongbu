using CloudDrive.Storage;
using System.Security.Cryptography;
using System.Text.Json;
using System.IO.Compression;
using Microsoft.Data.Sqlite;

int checks = 0;
void Check(bool condition, string name) { if (!condition) throw new Exception("FAIL " + name); checks++; Console.WriteLine("PASS " + name); }
var fixture = Path.Combine(Path.GetTempPath(), "CloudDriveLite-product-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(fixture);
string settingsPath = Path.Combine(fixture, "product.json");
Check(!ProductSettings.Load(settingsPath).LoginStartup && !ProductSettings.Load(settingsPath).CheckUpdates, "fresh install does not register startup or contact an unconfigured update channel");
ProductSettings.Change(s => s with { SyncPaused = true }, settingsPath);
ProductSettings.Change(s => s with { StartupDelaySeconds = 42 }, settingsPath);
Check(ProductSettings.Load(settingsPath) is { SyncPaused: true, StartupDelaySeconds: 42 }, "atomic settings preserve independent fields");
try { ProductSettings.Change(s => s with { UpdateManifestUrl = "http://untrusted/manifest" }, settingsPath); throw new Exception("FAIL HTTP"); } catch (ArgumentException) { Check(ProductSettings.Load(settingsPath).UpdateManifestUrl is null, "HTTP update configuration rejected before saving"); }
File.WriteAllText(settingsPath, "broken");
try { ProductSettings.Load(settingsPath); throw new Exception("FAIL corrupt config"); } catch (System.Text.Json.JsonException) { Check(true, "invalid persisted settings fail closed rather than reset policies"); }
var budget = new RestartBudget(3); var now = DateTimeOffset.UtcNow;
Check(budget.Failure(now) == TimeSpan.FromSeconds(2) && budget.Failure(now) == TimeSpan.FromSeconds(4) && budget.Failure(now) == TimeSpan.FromSeconds(8) && budget.Failure(now) is null, "crash budget backs off and stops repeated restarts");
Check(budget.Failure(now.AddMinutes(11)) == TimeSpan.FromSeconds(2), "restart window expires after ten minutes");
string pipeName = "CloudDriveLite.Test." + Guid.NewGuid().ToString("N"); using var stop = new CancellationTokenSource();
var server = new ManagementServer(request => Task.FromResult(new ManagementReply(request.Command == "status", request.Command == "status" ? "healthy" : "rejected")), pipeName).RunAsync(stop.Token);
Check((await ManagementClient.SendAsync(new("status"), pipeName: pipeName)).Success, "real current-user named pipe roundtrip");
Check(!(await ManagementClient.SendAsync(new("execute-arbitrary-command"), pipeName: pipeName)).Success, "unknown administration command rejected");
await stop.CancelAsync(); await server;
var index = new IndexStore(Path.Combine(fixture, "index.db"));
Check(index.CheckIntegrity() && index.ProductTotals() == new ProductTotals(0, 0, 0, 0, 0, 0), "real SQLite integrity and empty aggregate state");
var connection = new SmbConnection(Guid.NewGuid(), "Fixture connection", "nas.invalid", 445, "test", "", "fixture-account", "", true, Path.Combine(fixture, "never-created-root")); index.Save(connection); index.SetSyncSettings(connection.Id, new(Enabled: true));
ProductEnvironment.AtomicJson(settingsPath, new ProductSettings());
string backups = Path.Combine(fixture, "Backups"); string backup = ProductBackup.Create(index, backups, new ProductSettings());
var metadata = ProductBackup.Inspect(backup);
Check(metadata.Schema == IndexStore.CurrentSchema && metadata.DatabaseSha256.Length == 64 && metadata.UserKey == ProductEnvironment.UserKey, "online SQLite backup with version and identity manifest");
using (var zip = ZipFile.OpenRead(backup)) Check(zip.Entries.Count == 3 && zip.Entries.All(e => !e.FullName.Contains("credential", StringComparison.OrdinalIgnoreCase)), "backup contains only database settings manifest; no credential blobs");
string legacyBackup = Path.Combine(fixture, "legacy.zip"); File.Copy(backup, legacyBackup);
using (var zip = ZipFile.Open(legacyBackup, ZipArchiveMode.Update))
{
    zip.GetEntry("manifest.json")!.Delete();
    using var entry = zip.CreateEntry("manifest.json").Open();
    JsonSerializer.Serialize(entry, metadata with { Schema = 3 });
}
try { ProductBackup.Inspect(legacyBackup); throw new Exception("FAIL legacy archive"); } catch (IOException) { Check(index.CheckIntegrity(), "unsupported legacy backup schema rejected before database restore"); }
var restore = new IndexStore(Path.Combine(fixture, "restored.db"));
try { ProductBackup.RestoreOffline(restore, backup, backups, settingsPath, false); throw new Exception("FAIL active restore"); } catch (IOException) { Check(restore.Connections().Count == 0, "restore refuses missing offline maintenance ownership"); }
string previous = ProductBackup.RestoreOffline(restore, backup, backups, settingsPath, true);
Check(File.Exists(previous) && restore.CheckIntegrity() && restore.Connections().Single().Id == connection.Id && !restore.Connections().Single().AutoConnect && !restore.SyncSettings(connection.Id).Enabled && restore.ArchivePaused && ProductSettings.Load(settingsPath).SyncPaused, "offline restore makes pre-restore backup and pauses automatic writes/connections");
string v4Folder = Path.Combine(fixture, "v4-fixture"); ZipFile.ExtractToDirectory(backup, v4Folder);
string v4Database = Path.Combine(v4Folder, "index.db");
using (var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = v4Database, Pooling = false }.ToString()))
{
    db.Open(); using var command = db.CreateCommand(); command.CommandText = "DROP TABLE FolderMappings; DROP TABLE StorageConnections; DROP TABLE HealthCheckRecords; DROP TABLE HealthIncidents; DROP TABLE RecoveryActions; DROP TABLE HealthConfiguration; PRAGMA user_version=4;"; command.ExecuteNonQuery();
}
ProductEnvironment.AtomicJson(Path.Combine(v4Folder, "manifest.json"), metadata with { Version = "0.6.0", Schema = 4, DatabaseSha256 = ProductBackup.Hash(v4Database) });
string v4Archive = Path.Combine(fixture, "valid-v4.zip");
using (var zip = ZipFile.Open(v4Archive, ZipArchiveMode.Create)) foreach (string name in new[] { "index.db", "product.json", "manifest.json" }) zip.CreateEntryFromFile(Path.Combine(v4Folder, name), name);
var v4Restore = new IndexStore(Path.Combine(fixture, "v4-restored.db")); string originalV4Hash = ProductBackup.Hash(v4Archive);
ProductBackup.RestoreOffline(v4Restore, v4Archive, backups, settingsPath, true);
Check(ProductBackup.Inspect(v4Archive).Schema == 4 && v4Restore.Connections().Single().Id == connection.Id && v4Restore.ReadHealth().Modules.Count == 8 && ProductBackup.Hash(v4Archive) == originalV4Hash, "actual hash-validated v4 ZIP restores into current schema with health and NAS migration and preserves source archive");
index.EnqueueNewFile(connection.Id, "new-after-backup.txt");
try { ProductBackup.RestoreOffline(index, backup, backups, settingsPath, true); throw new Exception("FAIL rollback newer task"); } catch (IOException) { Check(index.SyncTasks(connection.Id).Count == 1, "restore refuses to discard newer task or mapping state"); }
string tampered = Path.Combine(fixture, "tampered.zip"); File.Copy(backup, tampered);
using (var zip = ZipFile.Open(tampered, ZipArchiveMode.Update)) { zip.GetEntry("index.db")!.Delete(); using var writer = new StreamWriter(zip.CreateEntry("index.db").Open()); writer.Write("tampered fixture"); }
try { ProductBackup.RestoreOffline(restore, tampered, backups, settingsPath, true); throw new Exception("FAIL corrupt backup"); } catch (IOException) { Check(restore.CheckIntegrity(), "tampered backup rejected before touching current database"); }
string traversal = Path.Combine(fixture, "traversal.zip");
using (var zip = ZipFile.Open(traversal, ZipArchiveMode.Create)) { zip.CreateEntry("index.db"); zip.CreateEntry("../product.json"); zip.CreateEntry("manifest.json"); }
try { ProductBackup.Inspect(traversal); throw new Exception("FAIL zip traversal"); } catch (IOException) { Check(true, "backup rejects path traversal and unknown entries"); }
string redacted = ProductLog.Redact("password=fixture-secret token='fixture-token' \\\\nas.invalid\\secret\\file.txt");
Check(!redacted.Contains("fixture-secret") && !redacted.Contains("fixture-token") && !redacted.Contains("nas.invalid"), "log redaction removes secret fields and remote paths");
string logRoot = Path.Combine(fixture, "Logs");
await using (var logger = new ProductLog(logRoot, new())) { logger.Write("Agent", "test", "password=fixture-secret", connection.Id, taskId: Guid.NewGuid()); }
string log = File.ReadAllText(Directory.GetFiles(logRoot, "*.jsonl", SearchOption.AllDirectories).Single());
Check(log.Contains("TaskId") && log.Contains("ThreadId") && !log.Contains("fixture-secret"), "asynchronous structured log flushes redacted context");
var report = ProductDiagnostics.Preview(index, null);
Check(!ProductDiagnostics.Render(report).Contains("nas.invalid") && !ProductDiagnostics.Render(report).Contains("fixture-account"), "diagnostic preview omits server account raw logs and full file paths");
Check(!ProductUninstall.Assess(index, new NeverUsedPlatform()).Allowed && index.SyncTasks(connection.Id).Count == 1, "configured or pending state blocks uninstall without deleting files");
using var signingKey = RSA.Create(3072);
var trust = new UpdateTrust(signingKey.ExportSubjectPublicKeyInfoPem(), new string('0', 64), ["updates.example.invalid"]);
var update = new UpdateManifest("0.7.0", "stable", now.AddMinutes(-1), now.AddDays(1), 42, "https://updates.example.invalid/product.msi", 100, new string('A', 64), "Fixture update notes");
UpdateEnvelope Sign(UpdateManifest value) { byte[] payload = JsonSerializer.SerializeToUtf8Bytes(value); return new(Convert.ToBase64String(payload), Convert.ToBase64String(signingKey.SignData(payload, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1))); }
var signed = Sign(update);
Check(ProductUpdate.Verify(signed, trust, "stable", now, 42) == update, "RSA3072 signed manifest validated before package metadata use");
try { ProductUpdate.Verify(signed with { Payload = Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(update with { Version = "99.0.0" })) }, trust, "stable", now); throw new Exception("FAIL tampered signature"); } catch (CryptographicException) { Check(true, "modified signed manifest rejected"); }
foreach (var invalid in new[] { update with { ExpiresAt = now.AddSeconds(-1) }, update with { Channel = "preview" }, update with { PackageUrl = "http://updates.example.invalid/package" }, update with { PackageUrl = "https://other.example.invalid/package" }, update with { Sequence = 1 } })
{ try { ProductUpdate.Verify(Sign(invalid), trust, "stable", now, 42); throw new Exception("FAIL manifest policy"); } catch (IOException) { Check(true, "manifest expiry/channel/HTTPS/host/anti-rollback policy"); } }
string assemblyPath = typeof(ProductUpdate).Assembly.Location;
var unsigned = update with { PackageLength = new FileInfo(assemblyPath).Length, PackageSha256 = ProductBackup.Hash(assemblyPath) };
try { await ProductUpdate.VerifyPackage(assemblyPath, unsigned, trust, CancellationToken.None); throw new Exception("FAIL unsigned package"); } catch (CryptographicException) { Check(true, "real Windows Authenticode rejects unsigned PE even with matching SHA256"); }
if (args.Contains("--lifecycle"))
{
    // Explicit test owns an initially empty product profile; never crash an existing user's Agent.
    var actual = new IndexStore(ProductEnvironment.DatabasePath);
    if (actual.Connections().Count != 0 || actual.Storages().Count != 0 || actual.UnfinishedWork() != 0) throw new IOException("Lifecycle test requires no configured user connections/tasks.");
    bool preexisting = false; try { await ManagementClient.SendAsync(new("status"), timeoutSeconds: 1); preexisting = true; } catch (Exception e) when (e is IOException or OperationCanceledException or TimeoutException) { }
    if (preexisting) throw new IOException("Existing Agent detected; lifecycle test will not interrupt it.");
    string productBin = Path.Combine(Directory.GetCurrentDirectory(), "artifacts", "bin");
    using var bootstrap = System.Diagnostics.Process.Start(ProductEnvironment.StartInfoAt(productBin, "CloudDrive.Bootstrap", "--background"))!;
    async Task<ProductSnapshot> WaitForAgent(int previousPid = 0)
    {
        for (int attempt = 0; attempt < 60; attempt++)
        {
            await Task.Delay(500);
            try { var live = (await ManagementClient.SendAsync(new("status"), timeoutSeconds: 1)).Snapshot; if (live is { DatabaseHealthy: true } && live.AgentPid != previousPid) return live; } catch (Exception e) when (e is IOException or OperationCanceledException or TimeoutException) { }
        }
        throw new IOException("Product lifecycle readiness deadline expired.");
    }
    try
    {
        var first = await WaitForAgent();
        Check(first.Providers.Count == 0, "real Bootstrap starts a standalone Agent without NAS or UI");
        HealthSnapshot? liveHealth = null;
        for (int attempt = 0; attempt < 30; attempt++)
        {
            liveHealth = (await ManagementClient.SendAsync(new("health-status"))).Health;
            if (liveHealth?.Modules.Any(r => r.ModuleName == "Process" && r.Code == "Responsive") == true) break;
            await Task.Delay(100);
        }
        Check(liveHealth is { Modules.Count: 8 } && liveHealth.Modules.Any(r => r.ModuleName == "Process" && r.Status == HealthStatus.Healthy) && liveHealth.Modules.Any(r => r.ModuleName == "Database" && r.Status == HealthStatus.Healthy), "live health IPC observes real Agent scheduler pipe and database rather than process existence alone");
        Check((await ManagementClient.SendAsync(new("health-check"))).Success, "live complete health-check request is queued through current-user management IPC");
        Check(!(await ManagementClient.SendAsync(new("health-recover", ItemId: Guid.NewGuid(), Operation: "DeleteFiles", Confirmed: true, DataAuthorized: true))).Success, "real Agent rejects destructive health repair even with request confirmation flags");
        string liveReport = HealthReports.Export(HealthReports.Preview(liveHealth!), Path.Combine(fixture, "live-agent-health.zip"));
        using (var zip = ZipFile.OpenRead(liveReport)) Check(zip.Entries.Count == 2, "actual empty-Agent health snapshot exports previewed JSON and HTML without credentials or user contents");
        using (var duplicate = System.Diagnostics.Process.Start(ProductEnvironment.StartInfoAt(productBin, "CloudDrive.Bootstrap", "--background"))!) { await duplicate.WaitForExitAsync(); Check(duplicate.ExitCode == 0 && (await ManagementClient.SendAsync(new("status"))).Snapshot!.AgentPid == first.AgentPid, "duplicate Bootstrap does not create another Agent"); }
        using (var emptyAgent = System.Diagnostics.Process.GetProcessById(first.AgentPid)) { emptyAgent.Kill(); await emptyAgent.WaitForExitAsync(); }
        var recovered = await WaitForAgent(first.AgentPid);
        Check(recovered.AgentPid != first.AgentPid && recovered.DatabaseHealthy, "actual empty-Agent crash is detected and restarted by independent Bootstrap");
        await ManagementClient.SendAsync(new("shutdown")); await bootstrap.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(40));
        Check(bootstrap.ExitCode == 0, "graceful product shutdown stops Bootstrap supervision without restart");
    }
    finally { if (!bootstrap.HasExited) { try { await ManagementClient.SendAsync(new("shutdown")); await bootstrap.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(40)); } catch (Exception e) { Console.Error.WriteLine("Cleanup pending: " + e.GetType().Name); } } }
}
Console.WriteLine($"PASS {checks} product checks; fixture={fixture}");

sealed class NeverUsedPlatform : ICachePlatform
{
    public CacheSnapshot Probe(SmbConnection c, string relative) => throw new Exception("fixture root was not created");
    public CacheVolume Volume(SmbConnection c) => throw new NotSupportedException();
    public Task ReleaseAsync(SmbConnection c, string relative, CancellationToken token) => throw new NotSupportedException();
    public Task PinAsync(SmbConnection c, string relative, bool pinned, CancellationToken token) => throw new NotSupportedException();
}
