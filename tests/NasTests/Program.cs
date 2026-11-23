using CloudDrive.Storage;
using System.Diagnostics;
using System.Security.Cryptography;

if (args.Length != 4) throw new ArgumentException("Connection GUID, relative file, expected SHA256, timeout seconds required.");
Guid id = Guid.Parse(args[0]); string relative = Paths.Relative(args[1]); string expected = args[2]; int seconds = int.Parse(args[3], System.Globalization.CultureInfo.InvariantCulture);
var index = new IndexStore(Path.Combine(IndexStore.DefaultDirectory, "index.db")); var config = index.Get(id) ?? throw new ArgumentException("Saved connection not found.");
// Native host is taken from the normal build output, rather than replacing or loading it differently.
string nativeDir = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../artifacts/bin"));
var runtime = new AgentRuntime(index, new CredentialVault(), nativeDir);
string dotnet = Path.GetFullPath(Path.Combine(System.Runtime.InteropServices.RuntimeEnvironment.GetRuntimeDirectory(), "../../..", "dotnet.exe"));
var start = new ProcessStartInfo(dotnet) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
foreach (string arg in new[] { Path.Combine(nativeDir, "CloudDrive.Agent.dll"), "smb-run", id.ToString() }) start.ArgumentList.Add(arg);
using var process = Process.Start(start) ?? throw new IOException("Agent failed to start.");
Task<string> errors = process.StandardError.ReadToEndAsync();
try
{
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(seconds)); bool ready = false;
    while (!process.HasExited)
    {
        string? line = await process.StandardOutput.ReadLineAsync(timeout.Token); if (line is null) break;
        Console.WriteLine(line); if (line.StartsWith("READY", StringComparison.Ordinal)) { ready = true; break; }
    }
    if (!ready) throw new IOException("CFAPI Host did not become ready: " + await errors);
    string path = Paths.Under(config.SyncRoot, relative);
    string hash = await Task.Run(() => { using var stream = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(stream)); }, timeout.Token).WaitAsync(timeout.Token);
    if (!hash.Equals(expected, StringComparison.OrdinalIgnoreCase)) throw new IOException("NAS hydration SHA256 mismatch.");
    Console.WriteLine("PASS real NAS hydration digest: " + hash);
    int released = await runtime.ReleaseAsync(config, relative, timeout.Token);
    if (released != 0) throw new IOException("Verified release failed; local cache retained.");
    Console.WriteLine("PASS verified cache release. Root and remote data retained.");
}
finally
{
    AgentRuntime.Stop(id); try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30)); } catch (TimeoutException) { process.Kill(); }
}
