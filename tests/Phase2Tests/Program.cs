using CloudDrive.Storage;
using System.Security;

var fixture = Path.Combine(Path.GetTempPath(), "CloudDriveLite-tests-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(fixture);
var results = new List<string>();
void Check(bool value, string name) { if (!value) throw new Exception("FAIL " + name); results.Add(name); Console.WriteLine("PASS " + name); }
void Reject(Action action, string name) { try { action(); } catch (ArgumentException) { Check(true, name); return; } throw new Exception("FAIL " + name); }
var id = Guid.NewGuid();
var config = new SmbConnection(id, "测试", "127.0.0.1", 445, "share", "中文目录", "reader", "WORKGROUP", true, Path.Combine(fixture, "cloud"));
config.Validate();
Check(config.RemoteShare == @"\\127.0.0.1\share", "UNC never contains port");
Reject(() => (config with { Port = 65536 }).Validate(), "invalid TCP port");
Reject(() => (config with { Server = "host;bad" }).Validate(), "invalid hostname");
foreach (var path in new[] { "..\\x", @"\root", "a:stream", "CON.txt", "a\\..\\b", "bad.", "file*", "a\n.txt" }) Reject(() => Paths.Relative(path), "unsafe path " + path.Replace('\n', '_'));
Check(Paths.StableId(id, @"中文\File.txt") == Paths.StableId(id, @"中文\file.TXT"), "stable case-insensitive ID");
Check(Paths.StableId(id, "x") != Paths.StableId(Guid.NewGuid(), "x"), "connection-scoped ID");
Check(FailureClassifier.FromCode(1219) == FailureKind.CredentialConflict, "credential conflict diagnostic");
Check(FailureClassifier.FromCode(1326) == FailureKind.Authentication, "authentication diagnostic");
Check(FailureClassifier.FromCode(67) == FailureKind.ShareMissing, "missing share diagnostic");
Check(FailureClassifier.FromCode(5) == FailureKind.AccessDenied, "permission diagnostic");
Check(FailureClassifier.Describe("test", new IOException("SECRET-DO-NOT-LOG")).Message.Contains("SECRET", StringComparison.Ordinal) == false, "sanitize exception details");
using (var password = new SecureString())
{
    foreach (char c in "unique-test-secret") password.AppendChar(c);
    string key = "CloudDriveLite/test/" + Guid.NewGuid().ToString("N"); var vault = new CredentialVault();
    try { vault.Save(key, "test", password); using var secret = vault.Read(key); Check(secret.Length == password.Length, "Credential Manager round trip"); }
    finally { vault.Delete(key); }
}
var index = new IndexStore(Path.Combine(fixture, "index.db")); index.Save(config);
Check(new IndexStore(index.DatabasePath).Get(id) == config, "SQLite persistent configuration");
Reject(() => index.Save(config with { Id = Guid.NewGuid(), SyncRoot = Path.Combine(config.SyncRoot, "child") }), "overlapping roots rejected");
var sourceRoot = Path.Combine(fixture, "remote"); Directory.CreateDirectory(sourceRoot);
Directory.CreateDirectory(Path.Combine(sourceRoot, "中文子目录"));
File.WriteAllText(Path.Combine(sourceRoot, "中文子目录", "特殊 # & ' 文件.txt"), "远端内容");
for (int i = 0; i < 600; i++) File.WriteAllText(Path.Combine(sourceRoot, $"file{i:D4}.txt"), "data");
var source = new FileSystemSource(id, sourceRoot); var scanner = new RemoteScanner(index);
await scanner.ScanAsync(id, source, CancellationToken.None);
Check(index.Count(id) == 602, "recursive metadata scan in batches");
var page1 = index.Page(id, ""); var page2 = index.Page(id, "", page1[^1].RelativePath);
Check(page1.Count == 256 && page2.Count == 256 && !page1.Select(x => x.Id).Intersect(page2.Select(x => x.Id)).Any(), "keyset pagination");
var unicode = index.Page(id, "中文子目录").Single();
await using (var read = await source.OpenReadAsync(unicode, CancellationToken.None)) Check(read.Length == unicode.Size, "version-checked streaming source");
File.AppendAllText(Path.Combine(sourceRoot, unicode.RelativePath), "changed");
try { await using var changed = await source.OpenReadAsync(unicode, CancellationToken.None); throw new Exception("FAIL changed version"); }
catch (StorageException e) when (e.Kind == FailureKind.SourceChanged) { Check(true, "changed version rejected"); }
var generation = index.BeginScan(id);
Check(index.BeginScan(id) == generation, "interrupted scan generation resumes");
using (var cancelled = new CancellationTokenSource())
{
    cancelled.Cancel(); try { await scanner.ScanAsync(id, source, cancelled.Token); } catch (OperationCanceledException) { }
    Check(index.Count(id) == 602, "cancel does not infer remote deletion");
}
await scanner.ScanAsync(id, source, CancellationToken.None);
File.Delete(Path.Combine(sourceRoot, "file0001.txt"));
await scanner.ScanAsync(id, source, CancellationToken.None);
Check(index.Count(id) == 601, "complete scan marks missing only after success");
Reject(() => index.Save(config with { Share = "another" }), "indexed source cannot silently change");
Check(Wire.Range(10000, 4097, 10) == (4096L, 8192L), "CFAPI nonzero aligned range");
Check(Wire.Range(10001, 8192, 1809) == (8192L, 10001L), "CFAPI EOF tail range");
Reject(() => Wire.Range(100, long.MaxValue, 2), "CFAPI overflow range rejected");
var mapper = new SmbMapper(index, new CredentialVault());
try
{
    var capabilities = await SmbMapper.CapabilitiesAsync(CancellationToken.None);
    Console.WriteLine($"SMB cmdlet capabilities: TcpPort={capabilities.TcpPort} Credential={capabilities.Credential}");
    Check(true, "system Windows PowerShell SmbShare discovery");
}
catch (StorageException e) when (e.Kind == FailureKind.SecurityPolicy)
{
    Console.WriteLine("BLOCKED system Windows PowerShell SmbShare: existing execution policy prohibits the module; unchanged.");
    Check(true, "policy rejection is classified and respected");
}
var diagnostics = await new ConnectionTester(mapper).TestAsync(config with { Port = 1 }, false, CancellationToken.None);
Check(diagnostics[0].Success && !diagnostics[^1].Success && diagnostics[^1].Stage == "TCP", "real localhost closed-port diagnostic");
Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
Check(!File.ReadAllText(index.DatabasePath).Contains("unique-test-secret", StringComparison.Ordinal), "no password in SQLite");
await BridgeChecks.RunAsync(index, config, Check);
Console.WriteLine($"PASS {results.Count} phase 2 checks. Fixtures retained at {fixture}");
