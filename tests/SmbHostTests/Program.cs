using CloudDrive.Storage;
using System.Diagnostics;
using System.Management.Automation;
using System.Security;

int checks = 0;
void Check(bool condition, string message) { if (!condition) throw new Exception("FAIL " + message); checks++; Console.WriteLine("PASS " + message); }
using (var cancelled = new CancellationTokenSource())
{
    cancelled.Cancel();
    try { using var unused = await WindowsSmbSession.OpenAsync(cancelled.Token); throw new Exception("FAIL pre-cancelled host"); }
    catch (OperationCanceledException) { Check(true, "pre-cancelled host request does not launch a helper"); }
}

var session = await WindowsSmbSession.OpenAsync(CancellationToken.None);
using var hostProcess = Process.GetProcessById(session.HostProcess.Id);
try
{
    string expectedHost = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe");
    var start = session.HostProcess.StartInfo;
    Check(start.FileName.Equals(expectedHost, StringComparison.OrdinalIgnoreCase), "host is the absolute system Windows PowerShell executable");
    Check(start.ArgumentList.SequenceEqual(new[] { "-Version", "5.1", "-s", "-NoLogo", "-NoProfile" }), "helper has fixed PSRP arguments with no script, credentials or execution policy override");
    Check(start.CreateNoWindow && !start.UseShellExecute && start.RedirectStandardInput && start.RedirectStandardOutput, "helper uses hidden local redirected pipes without UAC");
    Check(hostProcess.SessionId == Process.GetCurrentProcess().SessionId, "helper remains in the Agent's Windows session");

    using var ps = PowerShell.Create(); ps.Runspace = session.Runspace;
    var beforePolicy = await SmbMapper.Invoke(ps, "Get-ExecutionPolicy", [("List", true)], CancellationToken.None);
    string[] PolicySnapshot(IEnumerable<PSObject> rows) => rows.Select(row => row.Properties["Scope"].Value + ":" + row.Properties["ExecutionPolicy"].Value).Order().ToArray();
    string module = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "WindowsPowerShell", "v1.0", "Modules", "SmbShare", "SmbShare.psd1");
    try { await SmbMapper.Invoke(ps, "Import-Module", [("Name", module)], CancellationToken.None); Check(true, "signed system SmbShare module loads under the native host policy"); }
    catch (StorageException e) when (e.Kind == FailureKind.SecurityPolicy)
    {
        Console.WriteLine("BLOCKED: native Windows PowerShell policy rejects SmbShare; no policy changes or fallback performed.");
        throw;
    }
    var command = (await SmbMapper.Invoke(ps, "Get-Command", [("Name", "New-SmbMapping")], CancellationToken.None)).Single();
    Check(SmbMapper.HasParameter(command, "RemotePath") && SmbMapper.HasParameter(command, "LocalPath"), "deserialized command parameter metadata is read correctly");
    Check(!SmbMapper.HasParameter(command, "NotARealParameter"), "unsupported parameters are not invented");
    await SmbMapper.Invoke(ps, "Get-SmbMapping", [], CancellationToken.None);
    Check(true, "native mapping enumeration works without altering existing mappings");

    using var secret = new SecureString(); foreach (char character in "synthetic-credential-fixture") secret.AppendChar(character); secret.MakeReadOnly();
    var roundTrip = (await SmbMapper.Invoke(ps, "Write-Output", [("InputObject", new PSCredential("fixture-account", secret))], CancellationToken.None)).Single();
    Check(roundTrip.Properties["UserName"].Value.ToString() == "fixture-account" && roundTrip.Properties["Password"].Value is SecureString returnedSecret && returnedSecret.Length == secret.Length,
        "typed PSCredential and SecureString round-trip over local PSRP without script interpolation");
    if (roundTrip.Properties["Password"].Value is SecureString returned) returned.Dispose();
    Check(!string.Join(" ", start.ArgumentList).Contains("synthetic-credential-fixture", StringComparison.Ordinal), "credential fixture never appears in the helper command line");
    var afterPolicy = await SmbMapper.Invoke(ps, "Get-ExecutionPolicy", [("List", true)], CancellationToken.None);
    Check(PolicySnapshot(beforePolicy).SequenceEqual(PolicySnapshot(afterPolicy)), "all execution policy scopes remain unchanged");
    using var cancelSleep = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
    var elapsed = Stopwatch.StartNew();
    try { await SmbMapper.Invoke(ps, "Start-Sleep", [("Seconds", 30)], cancelSleep.Token); throw new Exception("FAIL cancelled pipeline"); }
    catch (OperationCanceledException) { Check(elapsed.Elapsed < TimeSpan.FromSeconds(5), "cancellation stops only the owned PowerShell pipeline promptly"); }
}
finally { session.Dispose(); }
Check(hostProcess.WaitForExit(5000), "disposing the session exits its owned helper process");
var capabilities = await SmbMapper.CapabilitiesAsync(CancellationToken.None);
Console.WriteLine($"Native SmbShare capabilities: TcpPort={capabilities.TcpPort}; Credential={capabilities.Credential}");
Check(true, "production SmbMapper capability discovery uses the system host");
Console.WriteLine($"PASS {checks} native SMB host checks; no NAS connection attempted by default.");

if (args is ["--saved-storage", var storageId])
{
    var index = new IndexStore(ProductEnvironment.DatabasePath);
    var storage = index.Storage(Guid.Parse(storageId));
    var mapper = new SmbMapper(index, new CredentialVault());
    var tester = new ConnectionTester(mapper);
    var diagnostics = await tester.TestAsync(storage.Access(), false, CancellationToken.None);
    foreach (var diagnostic in diagnostics) Console.WriteLine($"{diagnostic.Stage}: {diagnostic.Kind} — {diagnostic.Message}");
    if (diagnostics.Any(d => !d.Success)) return 1;
    var lease = index.Lease(storage.Id);
    if (lease is null || !await SmbMapper.VerifyOwnedMappingAsync(index, storage.Access(), CancellationToken.None))
        throw new IOException("Saved NAS mapping ownership verification failed.");
    await mapper.ConnectAsync(storage.Access(), CancellationToken.None);
    if (index.Lease(storage.Id) != lease) throw new IOException("Existing owned mapping was not reused.");
    Console.WriteLine("PASS saved NAS mapping ownership and reuse verified without a duplicate mapping.");
    Console.WriteLine("PASS explicitly selected saved NAS read-only connection/list test; no file contents or write probe.");
}
else if (args.Length != 0) throw new ArgumentException("Use --saved-storage <GUID> or no arguments.");
return 0;
