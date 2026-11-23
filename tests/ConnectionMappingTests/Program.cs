using CloudDrive.Storage;
using Microsoft.Data.Sqlite;
using System.Security;
using System.Text.Json;
using System.IO.Compression;

int checks = 0;
void Check(bool condition, string name) { if (!condition) throw new Exception("FAIL " + name); checks++; Console.WriteLine("PASS " + name); }
void Reject(Action action, string name)
{
    try { action(); } catch (Exception e) when (e is IOException or ArgumentException or UnauthorizedAccessException) { Check(true, name); return; }
    throw new Exception("FAIL " + name);
}
async Task RejectAsync(Func<Task> action, string name)
{
    try { await action(); } catch (Exception e) when (e is IOException or ArgumentException or UnauthorizedAccessException or OperationCanceledException) { Check(true, name); return; }
    throw new Exception("FAIL " + name);
}
string fixture = Path.Combine(Path.GetTempPath(), "CloudDriveLite-mappings-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(fixture);
string Scalar(string database, string sql)
{
    using var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = database, Pooling = false }.ToString()); db.Open(); using var command = db.CreateCommand(); command.CommandText = sql; return command.ExecuteScalar()?.ToString() ?? "";
}
void Sql(string database, string sql)
{
    using var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = database, Pooling = false }.ToString()); db.Open(); using var command = db.CreateCommand(); command.CommandText = sql; command.ExecuteNonQuery();
}
var index = new IndexStore(Path.Combine(fixture, "index.db"));
var nas = new StorageConnection(Guid.NewGuid(), "127.0.0.1", 445, "share", "fixture-reader", "WORKGROUP");
index.SaveStorage(nas);
Check(index.Storage(nas.Id) == nas && index.Connections().Count == 0, "NAS profile saves without folder mapping or local root");
Check(Directory.GetDirectories(fixture).Length == 0, "saving NAS creates no local or remote directories");
nas.Access().Validate(false); Reject(() => nas.Access().Validate(), "profile-only access cannot be used as CFAPI root");
Reject(() => index.SaveStorage(nas with { Share = @"share\folder" }), "share field cannot hide a remote subdirectory");
Reject(() => index.SaveStorage(nas with { Server = @"\\127.0.0.1\share" }), "address field requires host without UNC path");
var first = new FolderMapping(Guid.NewGuid(), nas.Id, Path.Combine(fixture, "local-one"), "中文\\资料");
var second = new FolderMapping(Guid.NewGuid(), nas.Id, Path.Combine(fixture, "local-two"), "photos");
index.SaveFolderMapping(first); index.SaveFolderMapping(second);
Check(index.MappingsForStorage(nas.Id).Count == 2 && index.StorageForMapping(first.Id) == nas.Id, "two independent folder mappings reuse one NAS profile");
Check(index.Get(first.Id)!.CredentialKey == index.Get(second.Id)!.CredentialKey && index.Get(first.Id)!.CredentialKey == nas.CredentialKey, "all mappings use shared Credential Manager reference");
Check(Paths.StableId(first.Id, "same.txt") != Paths.StableId(second.Id, "same.txt"), "separate mapping identities keep distinct CFAPI file IDs");
Check(!Directory.Exists(first.LocalPath), "offline mapping save does not create directories or contact NAS");
Reject(() => index.DeleteStorage(nas.Id), "NAS deletion blocked while mappings reference credentials");
using (var activeScan = ConnectionLock.Acquire(first.Id, "scan"))
    Reject(() => { using var forbidden = MappingMaintenance.Acquire([first.Id]); }, "active scan prevents credential or mapping maintenance");
using (MappingMaintenance.Acquire([first.Id])) Check(true, "failed maintenance attempt releases all gates and can retry after scan finishes");
Reject(() => index.SaveFolderMapping(first with { StorageId = Guid.NewGuid() }), "mapping cannot reference a missing NAS");
Reject(() => index.SaveFolderMapping(second with { LocalPath = Path.Combine(first.LocalPath, "nested") }), "overlapping mapping roots rejected");
var changed = nas with { UserName = "new-reader", Domain = "" }; index.SaveStorage(changed);
Check(index.MappingsForStorage(nas.Id).All(c => c.UserName == "new-reader" && c.Domain == "" && c.CredentialKey == nas.CredentialKey), "account edit propagates to every binding without changing credential reference");
var entry = new RemoteEntry(Paths.StableId(first.Id, "中文.txt"), first.Id, "中文.txt", false, 21, 1, 2, "fixture-file-id");
string generation = index.BeginScan(first.Id); index.SaveBatch(first.Id, generation, [entry]); index.DirectoryComplete(first.Id, generation, ""); index.FinishScan(first.Id, generation);
Reject(() => index.SaveStorage(changed with { Share = "other-share" }), "NAS source retarget rejected when a binding has indexed files");
Check(index.Storage(nas.Id).Share == "share" && index.Find(first.Id, entry.Id) == entry, "rejected source edit preserves NAS and indexed version");
Reject(() => index.SaveFolderMapping(first with { RemotePath = "other" }), "indexed remote folder cannot silently retarget");
var other = nas with { Id = Guid.NewGuid(), Share = "archive" }; index.SaveStorage(other);
Reject(() => index.SaveFolderMapping(first with { StorageId = other.Id }), "indexed mapping cannot silently switch NAS");
var legacy = new SmbConnection(Guid.NewGuid(), "legacy", "127.0.0.1", 445, "share", "old", "reader", "", true, Path.Combine(fixture, "legacy-root"));
var legacyIndex = new IndexStore(Path.Combine(fixture, "legacy.db")); legacyIndex.Save(legacy);
var legacyEntry = entry with { Id = Paths.StableId(legacy.Id, entry.RelativePath), ConnectionId = legacy.Id };
generation = legacyIndex.BeginScan(legacy.Id); legacyIndex.SaveBatch(legacy.Id, generation, [legacyEntry]);
Sql(legacyIndex.DatabasePath, "UPDATE connections SET config=json_remove(config,'$.StorageId'); DROP TABLE FolderMappings; DROP TABLE StorageConnections; PRAGMA user_version=5;");
string originalConfig = Scalar(legacyIndex.DatabasePath, "SELECT config FROM connections");
string originalCredential = Scalar(legacyIndex.DatabasePath, "SELECT credential_ref FROM connections");
legacyIndex = new IndexStore(legacyIndex.DatabasePath);
Check(legacyIndex.Get(legacy.Id) == legacy && Scalar(legacyIndex.DatabasePath, "SELECT config FROM connections") == originalConfig, "real v5 migration preserves exact legacy serialized mapping and ID");
Check(legacyIndex.Storage(legacy.Id) == StorageConnection.From(legacy) && legacyIndex.StorageForMapping(legacy.Id) == legacy.Id, "v5 migration creates reusable NAS and relation");
Check(legacyIndex.Find(legacy.Id, legacyEntry.Id) == legacyEntry && Scalar(legacyIndex.DatabasePath, "SELECT credential_ref FROM connections") == originalCredential, "migration preserves indexed metadata CFAPI IDs and credential reference");
Check(Directory.GetFiles(Path.Combine(fixture, "Backups"), "before-v5-*.db").Length == 1, "schema migration makes online pre-upgrade backup");
legacyIndex = new IndexStore(legacyIndex.DatabasePath);
Check(legacyIndex.Storages().Count == 1 && !Directory.Exists(legacy.SyncRoot), "reopen does not duplicate NAS or create root");
string legacyBackup = ProductBackup.Create(legacyIndex, Path.Combine(fixture, "v5-backups"), new());
string v5Stage = Path.Combine(fixture, "v5-zip"); ZipFile.ExtractToDirectory(legacyBackup, v5Stage);
string v5Database = Path.Combine(v5Stage, "index.db");
Sql(v5Database, "DROP TABLE FolderMappings; DROP TABLE StorageConnections; PRAGMA user_version=5;");
var manifest = ProductBackup.Inspect(legacyBackup) with { Schema = 5, Version = "0.7.0", DatabaseSha256 = ProductBackup.Hash(v5Database) };
ProductEnvironment.AtomicJson(Path.Combine(v5Stage, "manifest.json"), manifest);
string v5Archive = Path.Combine(fixture, "valid-v5.zip"); ZipFile.CreateFromDirectory(v5Stage, v5Archive);
var v5Restore = new IndexStore(Path.Combine(fixture, "v5-restored.db"));
ProductBackup.RestoreOffline(v5Restore, v5Archive, Path.Combine(fixture, "v5-backups"), Path.Combine(fixture, "restored-settings.json"), true);
Check(v5Restore.Storage(legacy.Id) == StorageConnection.From(legacy) && v5Restore.Find(legacy.Id, legacyEntry.Id) == legacyEntry && !v5Restore.Get(legacy.Id)!.AutoConnect, "actual v5 ZIP restore migrates NAS while retaining file identities and pausing connection");
string remoteRoot = Path.Combine(fixture, "test-share"); Directory.CreateDirectory(remoteRoot);
int resolutions = 0;
Task<string> Root(SmbConnection c, CancellationToken token) { Check(c.SubPath == "", "directory preparation resolves authenticated share base"); resolutions++; return Task.FromResult(remoteRoot); }
var directories = new MappingDirectories(index, Root); var config = first.Bind(changed);
await RejectAsync(() => directories.PrepareAsync(config, false, CancellationToken.None), "missing directory refuses creation without explicit choice");
Check(!Directory.Exists(first.LocalPath) && !Directory.Exists(Path.Combine(remoteRoot, "中文")), "missing-directory validation creates nothing");
await directories.PrepareAsync(config, true, CancellationToken.None);
Check(Directory.Exists(first.LocalPath) && Directory.Exists(Path.Combine(remoteRoot, "中文", "资料")), "explicit mapping preparation creates Unicode remote subfolders and local root");
await directories.PrepareAsync(config, true, CancellationToken.None);
Check(Directory.GetFiles(remoteRoot, "*", SearchOption.AllDirectories).Length == 0, "repeat preparation is idempotent and reads no file content");
Reject(() => index.Delete(first.Id), "existing local root keeps safe removal guard");
File.WriteAllText(Path.Combine(first.LocalPath, "unsynced.txt"), "preserve-local-data");
await RejectAsync(() => directories.PrepareAsync(config, true, CancellationToken.None), "nonempty unregistered local root rejected even for a saved mapping");
Check(File.ReadAllText(Path.Combine(first.LocalPath, "unsynced.txt")) == "preserve-local-data", "local unsynced file preserved on rejection");
var unsafeConfig = config with { Id = Guid.NewGuid(), SyncRoot = remoteRoot, SubPath = "never-created" };
await RejectAsync(() => directories.PrepareAsync(unsafeConfig, true, CancellationToken.None), "local and resolved remote share overlap rejected");
Check(!Directory.Exists(Path.Combine(remoteRoot, "never-created")), "overlap rejection occurs before remote write");
var aliasDirectories = new MappingDirectories(index, (_, _) => Task.FromResult(@"\\?\" + remoteRoot));
var aliasConfig = config with { Id = Guid.NewGuid(), SyncRoot = Path.Combine(remoteRoot, "alias-local"), SubPath = "alias-remote" };
await RejectAsync(() => aliasDirectories.PrepareAsync(aliasConfig, true, CancellationToken.None), "actual directory identity detects overlap through extended-path alias");
Check(!Directory.Exists(aliasConfig.SyncRoot) && !Directory.Exists(Path.Combine(remoteRoot, "alias-remote")), "physical alias rejection occurs before either directory is created");
var outside = config with { Id = Guid.NewGuid(), SyncRoot = Path.Combine(fixture, "clean-local"), SubPath = @"..\outside" };
int prior = resolutions; await RejectAsync(() => directories.PrepareAsync(outside, true, CancellationToken.None), "remote traversal rejected before network resolution");
Check(prior == resolutions, "invalid mapping never invokes SMB resolver");
var cancelled = config with { Id = Guid.NewGuid(), SyncRoot = Path.Combine(fixture, "cancel-local"), SubPath = "cancel-remote" };
using (var cancel = new CancellationTokenSource())
{
    var cancellation = new MappingDirectories(index, (_, _) => { cancel.Cancel(); return Task.FromResult(remoteRoot); });
    await RejectAsync(() => cancellation.PrepareAsync(cancelled, true, cancel.Token), "cancellation after SMB resolution stops directory creation");
    Check(!Directory.Exists(cancelled.SyncRoot) && !Directory.Exists(Path.Combine(remoteRoot, "cancel-remote")), "cancelled mapping creates no directories");
}
File.WriteAllText(Path.Combine(remoteRoot, "occupied"), "remote-file");
var occupied = cancelled with { SubPath = "occupied\\child" };
await RejectAsync(() => directories.PrepareAsync(occupied, true, CancellationToken.None), "remote regular-file ancestor rejected");
Check(File.ReadAllText(Path.Combine(remoteRoot, "occupied")) == "remote-file" && !Directory.Exists(cancelled.SyncRoot), "remote existing file preserved and local side not created");
void Junction(string link, string target)
{
    var start = new System.Diagnostics.ProcessStartInfo("cmd.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
    foreach (string arg in new[] { "/d", "/c", "mklink", "/J", link, target }) start.ArgumentList.Add(arg);
    using var process = System.Diagnostics.Process.Start(start)!; process.WaitForExit();
    if (process.ExitCode != 0) throw new IOException("Fixture junction creation failed; reparse tests cannot run.");
}
string junctionTarget = Path.Combine(fixture, "outside-junction"); Directory.CreateDirectory(junctionTarget);
string remoteJunction = Path.Combine(remoteRoot, "junction"); Junction(remoteJunction, junctionTarget);
await RejectAsync(() => directories.PrepareAsync(cancelled with { SubPath = "junction\\child" }, true, CancellationToken.None), "real remote junction cannot redirect directory creation outside share");
string localJunction = Path.Combine(fixture, "local-junction"); Junction(localJunction, junctionTarget);
prior = resolutions;
await RejectAsync(() => directories.PrepareAsync(cancelled with { SyncRoot = Path.Combine(localJunction, "child") }, true, CancellationToken.None), "real local junction rejected before SMB resolution");
Check(resolutions == prior && !Directory.Exists(Path.Combine(junctionTarget, "child")), "reparse rejection preserves outside target and performs no SMB operation");
await RejectAsync(() => directories.PrepareAsync(cancelled with { SyncRoot = fixture }, true, CancellationToken.None), "mapping cannot contain database directory");
var vault = new CredentialVault();
using (var secret = new SecureString())
{
    foreach (char c in "mapping-fixture-password") secret.AppendChar(c);
    vault.Save(nas.CredentialKey, changed.Account, secret);
    try
    {
        index.Delete(second.Id);
        using var retained = vault.Read(nas.CredentialKey);
        Check(retained.Length == secret.Length && index.Storage(nas.Id) == changed, "deleting unused mapping preserves shared NAS credential and profile");
    }
    finally { vault.Delete(nas.CredentialKey); }
}
index.DeleteStorage(other.Id); Check(index.Storages().All(s => s.Id != other.Id), "unreferenced NAS profile can be removed");
var profileOnly = new IndexStore(Path.Combine(fixture, "profile-only.db")); profileOnly.SaveStorage(nas with { Port = 1 });
Check(!ProductUninstall.Assess(profileOnly, new UnusedCache()).Allowed, "profile-only installation retains uninstall configuration guard");
string backup = ProductBackup.Create(profileOnly, Path.Combine(fixture, "restore-backups"), new());
profileOnly.SaveStorage(nas with { Port = 1, UserName = "changed-after-backup" });
Reject(() => ProductBackup.RestoreOffline(profileOnly, backup, Path.Combine(fixture, "restore-backups"), Path.Combine(fixture, "settings.json"), true), "restore refuses rollback over newer profile-only NAS edits");
var probe = new WindowsHealthProbe(profileOnly, () => throw new Exception("unused"), new UnusedCache());
var health = await probe.CheckAsync("SMB", HealthModules.Default("SMB"), CancellationToken.None);
Check(health.Single().ConnectionId == nas.Id && health.Single().Code != "NoConnections" && (health.Single().Recovery?.Length ?? 0) == 0, "health checks unmapped NAS without offering a CFAPI reconnect");
Check(WindowsHealthProbe.CheckDatabase(index.DatabasePath, true).Status == HealthStatus.Healthy, "new NAS and mapping tables pass actual integrity and relation checks");
SqliteConnection.ClearAllPools();
Check(!File.ReadAllText(index.DatabasePath).Contains("mapping-fixture-password", StringComparison.Ordinal), "shared password never enters SQLite");
Console.WriteLine($"PASS {checks} connection/mapping checks. Local fixture only; real NAS NOT tested. Retained: {fixture}");

sealed class UnusedCache : ICachePlatform
{
    public CacheSnapshot Probe(SmbConnection c, string relative) => throw new Exception("unused");
    public CacheVolume Volume(SmbConnection c) => throw new Exception("unused");
    public Task ReleaseAsync(SmbConnection c, string e, CancellationToken t) => throw new Exception("unused");
    public Task PinAsync(SmbConnection c, string relative, bool pin, CancellationToken t) => throw new Exception("unused");
}
