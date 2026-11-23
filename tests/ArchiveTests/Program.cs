using CloudDrive.Storage;
using Microsoft.Data.Sqlite;

string fixture = Path.GetFullPath(Path.Combine("artifacts", "tests", "archive-" + Guid.NewGuid().ToString("N")));
Directory.CreateDirectory(fixture);
var index = new IndexStore(Path.Combine(fixture, "index.db")); int passed = 0;
void Check(bool condition, string name) { if (!condition) throw new Exception("FAIL " + name); passed++; Console.WriteLine("PASS " + name); }
void Reject(Action action, string name) { try { action(); throw new Exception("Expected rejection " + name); } catch (ArgumentException) { Check(true, name); } }
SmbConnection Connection(string name) { var c = new SmbConnection(Guid.NewGuid(), name, name + ".test", 445, "share", "", "user", "", false, Path.Combine(fixture, name + "-cloud")); index.Save(c); return c; }
var source = Connection("source"); var target = Connection("target");
var policy = new ArchivePolicy(Guid.NewGuid(), "test", source.Id, target.Id);
index.SaveArchivePolicy(policy);
Check(index.GetArchivePolicy(policy.Id) == policy && !policy.Enabled && policy.CreationDays == 180 && policy.ModificationDays == 180 && policy.ScheduleTime == "02:00", "independent configuration and safe weekly defaults");
Reject(() => index.SaveArchivePolicy(policy with { IncludePinned = true }), "pinned files require explicit confirmation");
Reject(() => index.SaveArchivePolicy(policy with { TargetRootPath = @"..\escape" }), "target path traversal rejected");
Reject(() => index.SaveArchivePolicy(policy with { AllowInUse = true }), "in-use bypass unavailable");
using (var db = new SqliteConnection("Data Source=" + index.DatabasePath)) { db.Open(); using var cmd = db.CreateCommand(); cmd.CommandText = "PRAGMA user_version"; Check((long)cmd.ExecuteScalar()! == IndexStore.CurrentSchema, "additive database migration retains archive schema"); }
var now = DateTimeOffset.UtcNow; string path = @"资料\中文 # %.txt";
var entry = new RemoteEntry(Paths.StableId(source.Id, path), source.Id, path, false, 100, now.AddDays(-181).UtcDateTime.ToFileTimeUtc(), now.AddDays(-181).UtcDateTime.ToFileTimeUtc(), "test");
var snapshot = new CacheSnapshot(entry.Id, source.Id, path, false, true, true, false, 100, 0, 0);
var file = new CacheFile(snapshot, CacheState.OnlineOnly, null, null, null, null, 0, false, null);
Check(ArchiveEligibility.Reject(policy, entry, file, now) is null, "both timestamps older than cutoff accepted without local hydration");
Check(ArchiveEligibility.Reject(policy, entry with { ModifiedTime = now.AddDays(-1).UtcDateTime.ToFileTimeUtc() }, file, now) is not null, "old creation and recent modification rejected AND");
Check(ArchiveEligibility.Reject(policy, entry with { CreationTime = now.AddDays(-1).UtcDateTime.ToFileTimeUtc() }, file, now) is not null, "recent creation and old modification rejected AND");
Check(ArchiveEligibility.Reject(policy, entry with { CreationTime = now.AddDays(-180).UtcDateTime.ToFileTimeUtc() }, file, now) is not null, "exact cutoff excluded strict less-than");
Check(ArchiveEligibility.Reject(policy, entry with { CreationTimeReliable = false }, file, now) is not null, "unreliable creation time rejected");
Check(ArchivePaths.Target(policy, path) == path && ArchivePaths.Target(policy with { SourceRootPath = "资料", TargetRootPath = "历史" }, path) == @"历史\中文 # %.txt", "component-based target mapping preserves Unicode and nesting");
string sourceRoot = Path.Combine(fixture, "source-remote"), targetRoot = Path.Combine(fixture, "target-remote"); Directory.CreateDirectory(sourceRoot); Directory.CreateDirectory(targetRoot);
var roots = new Dictionary<Guid, string> { [source.Id] = sourceRoot, [target.Id] = targetRoot };
var platform = new ExplicitFixtureCache(index);
var manager = new ArchiveManager(index, (c, _) => Task.FromResult(roots[c.Id]), platform, new ExplicitFixtureGuard());
async Task<(ArchivePolicy Policy, RemoteEntry Entry, string Path)> Scenario(string name, string fileName = "中文 # % &.bin")
{
    string relative = name + "\\" + fileName; string full = Paths.Under(sourceRoot, relative); Directory.CreateDirectory(Path.GetDirectoryName(full)!);
    await File.WriteAllBytesAsync(full, System.Security.Cryptography.RandomNumberGenerator.GetBytes(32771));
    File.SetCreationTimeUtc(full, now.AddDays(-365).UtcDateTime); File.SetLastWriteTimeUtc(full, now.AddDays(-300).UtcDateTime);
    await new RemoteScanner(index).ScanAsync(source.Id, new FileSystemSource(source.Id, sourceRoot), CancellationToken.None);
    var p = policy with { Id = Guid.NewGuid(), TaskName = name, SourceRootPath = name, TargetRootPath = name }; index.SaveArchivePolicy(p);
    return (p, index.Find(source.Id, Paths.StableId(source.Id, relative))!, full);
}
var basic = await Scenario("basic");
var preview = await manager.PreviewAsync(basic.Policy, CancellationToken.None);
Check(preview.Candidates == 1 && preview.CandidateBytes == basic.Entry.Size && Directory.GetFileSystemEntries(targetRoot).Length == 0 && File.Exists(basic.Path), "dry-run scans metadata without target directories or source mutation");
Guid basicRun = index.QueueArchive(basic.Policy, preview.Files);
Check(index.QueueArchive(basic.Policy, preview.Files) == basicRun && index.ArchiveItems(basicRun).Count() == 1, "same policy cannot enqueue duplicate active run");
await manager.ExecuteAsync(basicRun, CancellationToken.None);
var completed = index.ArchiveItems(basicRun).Single();
Check(completed.Status == ArchiveStatus.Completed, "copy verify times mapping source-delete state machine completes with real Windows file IO and explicit local guard fixture");
Check(completed.SourceHash == completed.TargetHash && completed.SourceHash?.Length == 64 && !File.Exists(basic.Path) && File.Exists(Paths.Under(targetRoot, completed.TargetPath)), "reread SHA256 verified before exact source object deletion");
Check(File.GetCreationTimeUtc(Paths.Under(targetRoot, completed.TargetPath)).ToFileTimeUtc() == basic.Entry.CreationTime && File.GetLastWriteTimeUtc(Paths.Under(targetRoot, completed.TargetPath)).ToFileTimeUtc() == basic.Entry.ModifiedTime, "actual creation and modified timestamps retained and verified");
Check(index.Find(source.Id, basic.Entry.Id) == basic.Entry && index.ResolveStorage(basic.Entry).Connection.Id == target.Id && index.ResolveStorage(basic.Entry).Entry.Id != basic.Entry.Id, "logical FileId and path unchanged while physical target identity changes");
await new RemoteScanner(index).ScanAsync(source.Id, new FileSystemSource(source.Id, sourceRoot), CancellationToken.None);
Check(index.Find(source.Id, basic.Entry.Id) is not null, "primary rescan does not tombstone archived logical entry");
Directory.Delete(Path.GetDirectoryName(basic.Path)!);
await new RemoteScanner(index).ScanAsync(source.Id, new FileSystemSource(source.Id, sourceRoot), CancellationToken.None);
Check(index.FindPath(source.Id, "basic")?.IsDirectory == true && index.Find(source.Id, basic.Entry.Id) is not null, "archived ancestor directory survives primary folder removal and full rescan");
Check(index.ArchiveStatistics().SourceReleasedBytes == basic.Entry.Size, "actual released-source count only after deletion confirmed");
Reject(() => index.Save(source with { Server = "different.test" }), "archive referenced source cannot be redirected");
try { index.Delete(target.Id); throw new Exception("FAIL target deleted"); } catch (IOException) { Check(true, "archive target connection deletion protected"); }
var collision = await Scenario("collision"); Directory.CreateDirectory(Path.Combine(targetRoot, "collision")); string existingPath = Paths.Under(targetRoot, collision.Entry.RelativePath); await File.WriteAllTextAsync(existingPath, "KEEP");
preview = await manager.PreviewAsync(collision.Policy, CancellationToken.None);
Check(preview.Candidates == 0 && File.ReadAllText(existingPath) == "KEEP" && File.Exists(collision.Path), "existing target preview rejected without overwrite");
var interrupted = await Scenario("interrupted"); bool once = false;
var failBeforeMapping = new ArchiveManager(index, (c, _) => Task.FromResult(roots[c.Id]), platform, new ExplicitFixtureGuard(), (step, _) => { if (step == "TargetCommitted" && !once) { once = true; throw new IOException("injected database/mapping outage"); } return Task.CompletedTask; });
Guid recoveryRun = await failBeforeMapping.StartAsync(interrupted.Policy.Id, CancellationToken.None);
Check(index.ArchiveItems(recoveryRun).Single().Status == ArchiveStatus.RecoveryRequired && File.Exists(interrupted.Path) && index.StorageMapping(interrupted.Entry.Id) is null, "failure after target commit retains both copies and old mapping");
await new ArchiveManager(new IndexStore(index.DatabasePath), (c, _) => Task.FromResult(roots[c.Id]), platform, new ExplicitFixtureGuard()).ExecuteAsync(recoveryRun, CancellationToken.None);
Check(index.ArchiveItems(recoveryRun).Single().Status == ArchiveStatus.Completed, "restart reconciles committed target using journal identity and hashes");
var deleteRetry = await Scenario("delete-retry"); once = false;
var failAfterMapping = new ArchiveManager(index, (c, _) => Task.FromResult(roots[c.Id]), platform, new ExplicitFixtureGuard(), (step, _) => { if (step == "SourceDeleteIntent" && !once) { once = true; throw new IOException("injected deletion failure"); } return Task.CompletedTask; });
Guid deleteRun = await failAfterMapping.StartAsync(deleteRetry.Policy.Id, CancellationToken.None);
Check(index.ResolveStorage(deleteRetry.Entry).Connection.Id == target.Id && File.Exists(deleteRetry.Path) && !index.ArchiveItems(deleteRun).Single().SourceDeleted, "delete failure preserves new mapping and both copies without claiming released bytes");
await manager.ExecuteAsync(deleteRun, CancellationToken.None); Check(index.ArchiveItems(deleteRun).Single().SourceDeleted, "source cleanup resumes without deleting committed target");
var mismatch = await Scenario("hash-mismatch"); once = false;
var corrupt = new ArchiveManager(index, (c, _) => Task.FromResult(roots[c.Id]), platform, new ExplicitFixtureGuard(), async (step, item) =>
{
    if (step == "BeforeTargetVerify" && !once) { once = true; string tmp = Paths.Under(targetRoot, item.TemporaryPath); await File.WriteAllBytesAsync(tmp, new byte[item.Source.Size]); File.SetCreationTimeUtc(tmp, DateTime.FromFileTimeUtc(item.Source.CreationTime)); File.SetLastWriteTimeUtc(tmp, DateTime.FromFileTimeUtc(item.Source.ModifiedTime)); }
});
Guid mismatchRun = await corrupt.StartAsync(mismatch.Policy.Id, CancellationToken.None);
Check(index.ArchiveItems(mismatchRun).Single().Status == ArchiveStatus.Conflict && File.Exists(mismatch.Path) && index.StorageMapping(mismatch.Entry.Id) is null, "target reread SHA256 mismatch preserves source and prevents mapping switch");
var held = await Scenario("held-source");
using (var reader = new FileStream(held.Path, FileMode.Open, FileAccess.Read, FileShare.Read))
{
    Guid heldRun = await manager.StartAsync(held.Policy.Id, CancellationToken.None);
    Check(index.ArchiveItems(heldRun).Single().Status != ArchiveStatus.Completed && File.Exists(held.Path), "actual Windows source handle sharing denies migration while another reader is active");
}
var schedulePolicy = policy with { TimeZoneId = "UTC", ScheduleType = "Weekly", ScheduleDay = 0, ScheduleTime = "02:00", WindowMinutes = 120 };
Check(ArchiveSchedule.LatestDue(schedulePolicy, DateTimeOffset.Parse("2026-11-23T02:30:00Z")) == DateTimeOffset.Parse("2026-11-22T02:00:00Z"), "weekly catchup collapses missed periods to one latest due");
Check(ArchiveSchedule.InWindow(schedulePolicy, DateTimeOffset.Parse("2026-11-23T03:59:00Z")) && !ArchiveSchedule.InWindow(schedulePolicy, DateTimeOffset.Parse("2026-11-23T04:00:00Z")), "execution window has strict ending boundary");
Check(ArchiveSchedule.LatestDue(schedulePolicy with { ScheduleType = "Monthly", ScheduleDay = 5 }, DateTimeOffset.Parse("2026-11-02T03:00:00Z")) == DateTimeOffset.Parse("2026-10-05T02:00:00Z"), "monthly independent schedule supported");
var scheduled = await Scenario("scheduled"); var enabled = scheduled.Policy with { Enabled = true, TimeZoneId = "UTC", WindowMinutes = 1440 }; index.SaveArchivePolicy(enabled);
// Policies must not catch up occurrences before their creation. Pick an occurrence after
// the persisted creation time so this fixture remains valid on later days and in CI.
var tick = ArchiveSchedule.LatestDue(enabled, index.ArchivePolicyCreated(enabled.Id).AddDays(8)).AddMinutes(30);
await manager.SchedulerTickAsync(tick, CancellationToken.None); int runsBefore = index.ArchiveRuns().Count;
var restartedScheduler = new ArchiveManager(new IndexStore(index.DatabasePath), (c, _) => Task.FromResult(roots[c.Id]), platform, new ExplicitFixtureGuard());
await restartedScheduler.SchedulerTickAsync(tick.AddMinutes(1), CancellationToken.None);
Check(index.ArchiveRuns().Count == runsBefore && index.StorageMapping(scheduled.Entry.Id) is not null, "persisted scheduler due key prevents repeated catchup execution");
index.ArchivePaused = true; Check(new IndexStore(index.DatabasePath).ArchivePaused, "global archive pause persists across manager restart"); index.ArchivePaused = false;
var paused = await Scenario("pause"); preview = await manager.PreviewAsync(paused.Policy, CancellationToken.None); Guid pausedRun = index.QueueArchive(paused.Policy, preview.Files); index.ControlArchiveRun(pausedRun, "pause"); await manager.ExecuteAsync(pausedRun, CancellationToken.None);
Check(File.Exists(paused.Path) && index.StorageMapping(paused.Entry.Id) is null, "paused run does not begin migration");
index.ControlArchiveRun(pausedRun, "resume"); await manager.ExecuteAsync(pausedRun, CancellationToken.None); Check(index.ArchiveItems(pausedRun).Single().Status == ArchiveStatus.Completed, "manual resume rechecks state then completes");
var cancelled = await Scenario("cancel"); preview = await manager.PreviewAsync(cancelled.Policy, CancellationToken.None); Guid cancelledRun = index.QueueArchive(cancelled.Policy, preview.Files); index.ControlArchiveRun(cancelledRun, "cancel"); await manager.ExecuteAsync(cancelledRun, CancellationToken.None);
Check(File.Exists(cancelled.Path) && index.ArchiveItems(cancelledRun).Single().Status == ArchiveStatus.Cancelled, "cancel only unstarted items without deleting source");
var protectedFile = await Scenario("user-protected"); index.SetCacheExclusion(source.Id, "user-protected", true);
Check((await manager.PreviewAsync(protectedFile.Policy with { IncludePinned = true, PinnedConfirmed = true }, CancellationToken.None)).Candidates == 0, "include-pinned option does not override independent user protection");
var unowned = await Scenario("unowned-temp"); preview = await manager.PreviewAsync(unowned.Policy, CancellationToken.None); Guid unownedRun = index.QueueArchive(unowned.Policy, preview.Files); var unownedItem = index.ArchiveItems(unownedRun).Single(); string unownedPath = Paths.Under(targetRoot, unownedItem.TemporaryPath); Directory.CreateDirectory(Path.GetDirectoryName(unownedPath)!); await File.WriteAllTextAsync(unownedPath, "KEEP UNOWNED");
await manager.ExecuteAsync(unownedRun, CancellationToken.None);
Check(File.ReadAllText(unownedPath) == "KEEP UNOWNED" && File.Exists(unowned.Path), "unproven temp is never truncated overwritten or deleted");
var changed = await Scenario("changed-after-pause"); once = false;
var pauseCopy = new ArchiveManager(index, (c, _) => Task.FromResult(roots[c.Id]), platform, new ExplicitFixtureGuard(), (step, item) => { if (step == "CopiedWithTimes" && !once) { once = true; index.ControlArchiveRun(item.RunId, "pause"); } return Task.CompletedTask; });
Guid changedRun = await pauseCopy.StartAsync(changed.Policy.Id, CancellationToken.None); var beforeChange = index.ArchiveItems(changedRun).Single();
Check(File.Exists(changed.Path) && beforeChange.Staged is not null && index.StorageMapping(changed.Entry.Id) is null, "pause after copying retains owned temp and source without committing mapping");
await File.WriteAllTextAsync(changed.Path, "NEW SOURCE VERSION"); index.ControlArchiveRun(changedRun, "resume"); await manager.ExecuteAsync(changedRun, CancellationToken.None);
Check(index.ArchiveItems(changedRun).Single().Status == ArchiveStatus.Conflict && !File.Exists(Paths.Under(targetRoot, beforeChange.TemporaryPath)) && File.Exists(changed.Path), "changed source removes only journal-owned invalid temp and preserves current source");
var database = await Scenario("database-failure");
void DatabaseSql(string sql) { using var d = new SqliteConnection("Data Source=" + index.DatabasePath); d.Open(); using var cmd = d.CreateCommand(); cmd.CommandText = sql; cmd.ExecuteNonQuery(); }
DatabaseSql("CREATE TRIGGER reject_mapping BEFORE INSERT ON FileStorageMappings WHEN NEW.FileId='" + database.Entry.Id + "' BEGIN SELECT RAISE(ABORT,'injected mapping database failure'); END;");
Guid databaseRun = await manager.StartAsync(database.Policy.Id, CancellationToken.None);
Check(File.Exists(database.Path) && index.StorageMapping(database.Entry.Id) is null && index.ArchiveItems(databaseRun).Single().Target is not null, "actual SQLite mapping transaction abort preserves committed target and source");
DatabaseSql("DROP TRIGGER reject_mapping;"); await manager.ExecuteAsync(databaseRun, CancellationToken.None); Check(index.ArchiveItems(databaseRun).Single().Status == ArchiveStatus.Completed, "mapping transaction failure resumes from exact committed target");
var race = await Scenario("target-race"); once = false;
var targetRace = new ArchiveManager(index, (c, _) => Task.FromResult(roots[c.Id]), platform, new ExplicitFixtureGuard(), async (step, item) => { if (step == "CommitIntent" && !once) { once = true; await File.WriteAllTextAsync(Paths.Under(targetRoot, item.TargetPath), "RACE WINNER"); } });
Guid raceRun = await targetRace.StartAsync(race.Policy.Id, CancellationToken.None);
Check(index.ArchiveItems(raceRun).Single().Status == ArchiveStatus.Conflict && File.ReadAllText(Paths.Under(targetRoot, race.Entry.RelativePath)) == "RACE WINNER" && File.Exists(race.Path), "same-directory rename refuses racing target without overwrite");
var duplicate = await Scenario("duplicate-policy"); preview = await manager.PreviewAsync(duplicate.Policy, CancellationToken.None); Guid duplicateRun = index.QueueArchive(duplicate.Policy, preview.Files); var otherPolicy = duplicate.Policy with { Id = Guid.NewGuid(), TaskName = "overlap" }; index.SaveArchivePolicy(otherPolicy); Guid otherRun = index.QueueArchive(otherPolicy, preview.Files);
Check(index.ArchiveItems(duplicateRun).Count() == 1 && !index.ArchiveItems(otherRun).Any(), "cross-policy unique file reservation prevents duplicate migration");
using (ConnectionLock.Acquire(Guid.Empty, "archive-execution")) { try { await manager.ExecuteAsync(duplicateRun, CancellationToken.None); throw new Exception("FAIL global archive lock"); } catch (IOException) { Check(File.Exists(duplicate.Path), "global cross-instance archive lock serializes execution"); } }
var conflictName = await Scenario("conflict-name"); Directory.CreateDirectory(Path.Combine(targetRoot, "conflict-name")); await File.WriteAllTextAsync(Paths.Under(targetRoot, conflictName.Entry.RelativePath), "EXISTING"); index.SaveArchivePolicy(conflictName.Policy with { Collision = ArchiveCollision.ConflictName }); Guid conflictNameRun = await manager.StartAsync(conflictName.Policy.Id, CancellationToken.None);
Check(index.ArchiveItems(conflictNameRun).Single().Status == ArchiveStatus.Completed && index.ArchiveItems(conflictNameRun).Single().TargetPath != conflictName.Entry.RelativePath && File.ReadAllText(Paths.Under(targetRoot, conflictName.Entry.RelativePath)) == "EXISTING", "explicit conflict-name policy preserves existing target");
Check(ArchivePaths.Target(policy with { SourceRootPath = "资料", Recursive = false }, @"资料\one.txt") == "one.txt", "nonrecursive scope accepts immediate children");
Reject(() => ArchivePaths.Target(policy with { SourceRootPath = "资料", Recursive = false }, @"资料\nested\one.txt"), "nonrecursive scope rejects descendants");
string bulkDir = Path.Combine(sourceRoot, "bulk"); Directory.CreateDirectory(bulkDir);
for (int n = 0; n < 257; n++) { string f = Path.Combine(bulkDir, n + ".txt"); await File.WriteAllTextAsync(f, "small " + n); File.SetCreationTimeUtc(f, now.AddDays(-400).UtcDateTime); File.SetLastWriteTimeUtc(f, now.AddDays(-300).UtcDateTime); }
await new RemoteScanner(index).ScanAsync(source.Id, new FileSystemSource(source.Id, sourceRoot), CancellationToken.None);
var bulkPolicy = policy with { Id = Guid.NewGuid(), TaskName = "257 small files", SourceRootPath = "bulk", TargetRootPath = "bulk", Concurrency = 4 }; index.SaveArchivePolicy(bulkPolicy); Guid bulkRun = await manager.StartAsync(bulkPolicy.Id, CancellationToken.None);
Check(index.ArchiveItems(bulkRun).Count() == 257 && index.ArchiveItems(bulkRun).All(i => i.Status == ArchiveStatus.Completed) && Directory.GetFiles(bulkDir).Length == 0, "257 small files cross journal pagination with four concurrent migrations");
var longFile = await Scenario("long-path", new string('深', 70) + "\\" + new string('长', 70) + "\\" + new string('文', 50) + ".bin");
Guid longRun = await manager.StartAsync(longFile.Policy.Id, CancellationToken.None);
Check(longFile.Path.Length > 260 && index.ArchiveItems(longRun).Single().Status == ArchiveStatus.Completed, "actual Windows paths over 260 characters migrate with safe Unicode components");
var hidden = await Scenario("hidden"); File.SetAttributes(hidden.Path, File.GetAttributes(hidden.Path) | FileAttributes.Hidden);
await new RemoteScanner(index).ScanAsync(source.Id, new FileSystemSource(source.Id, sourceRoot), CancellationToken.None);
Check((await manager.PreviewAsync(hidden.Policy, CancellationToken.None)).Candidates == 0 && (await manager.PreviewAsync(hidden.Policy with { IncludeHidden = true }, CancellationToken.None)).Candidates == 1, "actual hidden attribute excluded by default and independently configurable");
var readonlySource = await Scenario("readonly-source"); File.SetAttributes(readonlySource.Path, File.GetAttributes(readonlySource.Path) | FileAttributes.ReadOnly);
Guid readonlyRun = await manager.StartAsync(readonlySource.Policy.Id, CancellationToken.None);
Check(File.Exists(readonlySource.Path) && index.StorageMapping(readonlySource.Entry.Id) is not null && !index.ArchiveItems(readonlyRun).Single().SourceDeleted, "actual Windows read-only source deletion failure preserves new mapping and both copies");
File.SetAttributes(readonlySource.Path, File.GetAttributes(readonlySource.Path) & ~FileAttributes.ReadOnly);
await manager.ExecuteAsync(readonlyRun, CancellationToken.None); Check(index.ArchiveItems(readonlyRun).Single().Status == ArchiveStatus.Completed, "actual deletion failure recovers after source attributes are corrected");
var metaFail = await Scenario("metadata-failure"); once = false;
var changeTime = new ArchiveManager(index, (c, _) => Task.FromResult(roots[c.Id]), platform, new ExplicitFixtureGuard(), (step, item) => { if (step == "BeforeTargetVerify" && !once) { once = true; File.SetCreationTimeUtc(Paths.Under(targetRoot, item.TemporaryPath), now.UtcDateTime); } return Task.CompletedTask; });
Guid metaRun = await changeTime.StartAsync(metaFail.Policy.Id, CancellationToken.None);
Check(File.Exists(metaFail.Path) && index.StorageMapping(metaFail.Entry.Id) is null && index.ArchiveItems(metaRun).Single().Status == ArchiveStatus.Conflict, "target timestamp mismatch refuses commit and source deletion");
string migrationDb = Path.Combine(fixture, "migration-v3.db");
using (var currentDb = new SqliteConnection("Data Source=" + index.DatabasePath)) using (var copyDb = new SqliteConnection("Data Source=" + migrationDb))
{
    currentDb.Open(); copyDb.Open(); currentDb.BackupDatabase(copyDb); using var cmd = copyDb.CreateCommand();
    cmd.CommandText = "DROP TABLE ArchiveJournal; DROP TABLE ArchiveItems; DROP TABLE ArchiveRuns; DROP TABLE ArchivePolicies; DROP TABLE FileStorageMappings; DROP TABLE ArchiveSettings; PRAGMA user_version=3;"; cmd.ExecuteNonQuery();
}
var migrated = new IndexStore(migrationDb);
Check(migrated.Count(source.Id) == index.Count(source.Id) && migrated.Connections().Count == index.Connections().Count && migrated.CachePolicy(source.Id) == index.CachePolicy(source.Id), "actual v3 to v4 migration retains logical files connections and cache policy");
Console.WriteLine($"PASS {passed} archive checks; Windows local fixtures, explicit cache/local-guard fixtures, NOT NAS. Retained: {fixture}");
if (args.Contains("--cfapi", StringComparer.Ordinal)) await RealCfapi();

async Task RealCfapi()
{
    string dir = Path.Combine(fixture, "real"); Directory.CreateDirectory(dir);
    var db = new IndexStore(Path.Combine(dir, "index.db"));
    string a = Path.Combine(dir, "source"), b = Path.Combine(dir, "target"), cloud = Path.Combine(dir, "cloud"); Directory.CreateDirectory(a); Directory.CreateDirectory(b);
    var c = new SmbConnection(Guid.NewGuid(), "real-primary-local-source-NOT-NAS", "primary.test", 445, "share", "", "u", "", false, cloud);
    var t = new SmbConnection(Guid.NewGuid(), "real-archive-local-source-NOT-NAS", "archive.test", 445, "share", "", "u", "", false, Path.Combine(dir, "target-cloud")); db.Save(c); db.Save(t);
    byte[] payload = System.Security.Cryptography.RandomNumberGenerator.GetBytes(3 * 1024 * 1024 + 123);
    foreach (string name in new[] { "仅联机 # 中文.bin", "固定缓存.bin" }) { await File.WriteAllBytesAsync(Paths.Under(a, name), payload); File.SetCreationTimeUtc(Paths.Under(a, name), now.AddDays(-400).UtcDateTime); File.SetLastWriteTimeUtc(Paths.Under(a, name), now.AddDays(-300).UtcDateTime); }
    string large = "1GiB以上.bin"; long largeSize = (1L << 30) + 123;
    using (var f = new FileStream(Paths.Under(a, large), FileMode.CreateNew, FileAccess.Write)) f.SetLength(largeSize);
    File.SetCreationTimeUtc(Paths.Under(a, large), now.AddDays(-400).UtcDateTime); File.SetLastWriteTimeUtc(Paths.Under(a, large), now.AddDays(-300).UtcDateTime);
    await new RemoteScanner(db).ScanAsync(c.Id, new FileSystemSource(c.Id, a), CancellationToken.None);
    string RootFor(SmbConnection storage) => storage.Id == c.Id ? a : storage.Id == t.Id ? b : throw new Exception("unknown storage");
    await using var bridge = new SmbBridge(db, c, _ => Task.FromResult<IRemoteSource>(new FileSystemSource(c.Id, a)), (storage, _) => Task.FromResult<IRemoteSource>(new FileSystemSource(storage.Id, RootFor(storage)))); bridge.Start();
    using var stop = new EventWaitHandle(false, EventResetMode.ManualReset, AgentRuntime.EventName(c.Id, "Stop"));
    var start = new System.Diagnostics.ProcessStartInfo(Path.GetFullPath("artifacts/bin/CloudProviderHost.exe")) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
    foreach (string arg in new[] { "run-smb", "--source", c.Id.ToString("N"), "--root", cloud, "--pipe", bridge.PipeName, "--agent-pid", Environment.ProcessId.ToString(), "--stop-event", AgentRuntime.EventName(c.Id, "Stop"), "--refresh-event", AgentRuntime.EventName(c.Id, "Refresh") }) start.ArgumentList.Add(arg);
    using var host = System.Diagnostics.Process.Start(start)!; var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    using var log = new StreamWriter(Path.Combine(dir, "provider.log")) { AutoFlush = true };
    var output = Task.Run(async () => { string? line; while ((line = await host.StandardOutput.ReadLineAsync()) is not null) { await log.WriteLineAsync(line); if (line.StartsWith("READY", StringComparison.Ordinal)) ready.TrySetResult(); } }); var errors = host.StandardError.ReadToEndAsync();
    try
    {
        await ready.Task.WaitAsync(TimeSpan.FromSeconds(30));
        var realPlatform = new WindowsCachePlatform(db, (storage, _) => Task.FromResult(RootFor(storage)), Path.GetFullPath("artifacts/bin"));
        var realManager = new ArchiveManager(db, (storage, _) => Task.FromResult(RootFor(storage)), realPlatform, new WindowsArchiveLocalGuard());
        await realPlatform.PinAsync(c, "固定缓存.bin", true, CancellationToken.None);
        var cached = realPlatform.Probe(c, "固定缓存.bin");
        Check(cached.Pinned && cached.HydratedBytes == payload.Length && realPlatform.Probe(c, "仅联机 # 中文.bin").HydratedBytes == 0, "real CFAPI initial pinned/full and online-only states");
        var p = new ArchivePolicy(Guid.NewGuid(), "actual-CFAPI-local-source-NOT-NAS", c.Id, t.Id, TargetRootPath: "历史", IncludePinned: true, PinnedConfirmed: true); db.SaveArchivePolicy(p);
        var dry = await realManager.PreviewAsync(p, CancellationToken.None); Check(dry.Candidates == 3 && Directory.GetFileSystemEntries(b).Length == 0, "real CFAPI archive preview reads metadata without downloading or creating target");
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); var self = System.Diagnostics.Process.GetCurrentProcess(); self.Refresh(); long memoryBefore = self.PrivateMemorySize64;
        Guid run = await realManager.StartAsync(p.Id, CancellationToken.None);
        var records = db.ArchiveItems(run).ToList();
        foreach (var item in records) Console.WriteLine("REAL ARCHIVE " + item.Source.RelativePath + " " + item.Status + " " + item.Error);
        Check(records.Count == 3 && records.All(i => i.Status == ArchiveStatus.Completed && i.SourceDeleted), "actual CFAPI guarded archive copies verifies maps and deletes exact source files");
        self.Refresh(); long memoryDelta = self.PrivateMemorySize64 - memoryBefore;
        Check(records.Single(i => i.Source.RelativePath == large).Source.Size == largeSize && memoryDelta < 128L << 20 && realPlatform.Probe(c, large).HydratedBytes == 0, "1 GiB+123 archive streams without local hydration; process before/after memory delta=" + memoryDelta);
        var after = realPlatform.Probe(c, "固定缓存.bin"); var online = realPlatform.Probe(c, "仅联机 # 中文.bin");
        Check(after.Pinned && after.HydratedBytes == cached.HydratedBytes && after.AllocatedBytes == cached.AllocatedBytes && online.HydratedBytes == 0, "archive leaves real cached allocation pin and online-only content state unchanged");
        Check(System.Security.Cryptography.SHA256.HashData(await File.ReadAllBytesAsync(Paths.Under(cloud, "仅联机 # 中文.bin"))).SequenceEqual(System.Security.Cryptography.SHA256.HashData(payload)), "original CFAPI path hydrates through mapped archive target after primary source deletion");
        await new RemoteScanner(db).ScanAsync(c.Id, new FileSystemSource(c.Id, a), CancellationToken.None); AgentRuntime.SignalRefresh(c.Id);
        Check(db.Count(c.Id) == 3 && File.Exists(Paths.Under(cloud, "仅联机 # 中文.bin")), "primary rescan retains archived logical namespace and original placeholders");
        await realPlatform.ReleaseAsync(c, "仅联机 # 中文.bin", CancellationToken.None);
        Check(realPlatform.Probe(c, "仅联机 # 中文.bin").HydratedBytes == 0 && File.Exists(Paths.Under(b, @"历史\仅联机 # 中文.bin")), "cache verified release uses mapped archive NAS route without deleting target");
        Check(System.Security.Cryptography.SHA256.HashData(await File.ReadAllBytesAsync(Paths.Under(cloud, "仅联机 # 中文.bin"))).SequenceEqual(System.Security.Cryptography.SHA256.HashData(payload)), "archive path rehydrates again after independent cache release");
        await using (var f = new FileStream(Paths.Under(cloud, large), FileMode.Open, FileAccess.Read, FileShare.Read)) Check(await NewFileUpload.StreamHash(f, CancellationToken.None) == records.Single(i => i.Source.RelativePath == large).SourceHash, "1 GiB+123 archived CFAPI path hydrates from new target with matching streaming SHA256");
        Console.WriteLine("PASS real Windows CFAPI archive integration with TWO LOCAL DIRECTORIES, NOT NAS. Retained: " + dir);
    }
    finally { stop.Set(); await host.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30)); await output; Console.WriteLine(await errors); }
}

sealed class ExplicitFixtureGuard : IArchiveLocalGuard
{
    public IArchiveLocalLease Acquire(SmbConnection c, string p) => new EmptyLease();
    private sealed class EmptyLease : IArchiveLocalLease { public CacheSnapshot? Snapshot => null; public void Dispose() { } }
}
sealed class ExplicitFixtureCache(IndexStore index) : ICachePlatform
{
    public CacheSnapshot Probe(SmbConnection c, string path)
    { var entry = index.Find(c.Id, Paths.StableId(c.Id, path)); return new(Paths.StableId(c.Id, path), c.Id, path, false, entry is not null, true, false, entry?.Size ?? 0, 0, 0); }
    public CacheVolume Volume(SmbConnection c) => new("fixture-volume", 1L << 40, 2L << 40);
    public Task ReleaseAsync(SmbConnection c, string p, CancellationToken token) => throw new NotSupportedException();
    public Task PinAsync(SmbConnection c, string p, bool pinned, CancellationToken token) => throw new NotSupportedException();
}
