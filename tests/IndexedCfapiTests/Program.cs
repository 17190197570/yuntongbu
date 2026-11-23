using CloudDrive.Storage;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;

if (args.Length != 1) throw new ArgumentException("Pass absolute CloudProviderHost.exe path.");
string native = Path.GetFullPath(args[0]);
string fixture = Path.Combine(Path.GetDirectoryName(native)!, "..", "tests", "indexed-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(fixture); string sourcePath = Path.Combine(fixture, "source"); Directory.CreateDirectory(sourcePath);
string root = Path.Combine(fixture, "cloud"); Directory.CreateDirectory(root);
var id = Guid.NewGuid(); var config = new SmbConnection(id, "indexed fixture", "127.0.0.1", 445, "fixture", "", "reader", "", false, root);
var bytes = RandomNumberGenerator.GetBytes(3 * 1024 * 1024 + 123); const string file = "中文 # &.bin";
await File.WriteAllBytesAsync(Path.Combine(sourcePath, file), bytes);
var index = new IndexStore(Path.Combine(fixture, "index.db")); index.Save(config);
var source = new FileSystemSource(id, sourcePath); await new RemoteScanner(index).ScanAsync(id, source, CancellationToken.None);
await using var bridge = new SmbBridge(index, config, _ => Task.FromResult<IRemoteSource>(source)); bridge.Start();
string stopName = @"Local\CloudDriveLite.Test." + Guid.NewGuid().ToString("N"), refreshName = @"Local\CloudDriveLite.Refresh." + Guid.NewGuid().ToString("N");
using var stop = new EventWaitHandle(false, EventResetMode.ManualReset, stopName);
var start = Start("run-smb", "--source", id.ToString("N"), "--root", root, "--pipe", bridge.PipeName, "--agent-pid", Environment.ProcessId.ToString(), "--stop-event", stopName, "--refresh-event", refreshName);
using var process = new Process { StartInfo = start }; var lines = new List<string>(); var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
process.OutputDataReceived += (_, e) => { if (e.Data is not null) { lock (lines) lines.Add(e.Data); Console.WriteLine(e.Data); if (e.Data.StartsWith("READY", StringComparison.Ordinal)) ready.TrySetResult(); } };
process.ErrorDataReceived += (_, e) => { if (e.Data is not null) { lock (lines) lines.Add(e.Data); Console.Error.WriteLine(e.Data); } };
Console.WriteLine("Indexed CFAPI fixture: " + Path.GetFullPath(fixture));
bool started = false;
try
{
    process.Start(); started = true; process.BeginOutputReadLine(); process.BeginErrorReadLine();
    var first = await Task.WhenAny(ready.Task, process.WaitForExitAsync(), Task.Delay(TimeSpan.FromSeconds(30)));
    if (!ready.Task.IsCompletedSuccessfully) throw new IOException("Host did not reach READY; fixture preserved.");
    using (var state = await Status())
    {
        if (state.RootElement.GetProperty("onDiskDataSize").GetInt64() != 0) throw new Exception("Metadata population downloaded file bodies.");
        if (!state.RootElement.GetProperty("placeholder").GetBoolean()) throw new Exception("No CFAPI placeholder.");
    }
    Console.WriteLine("PASS indexed metadata creates real placeholder without content");
    string local = Path.Combine(root, file);
    string digest = await Task.Run(() => { using var input = File.OpenRead(local); return Convert.ToHexString(SHA256.HashData(input)); }).WaitAsync(TimeSpan.FromSeconds(40));
    if (digest != Convert.ToHexString(SHA256.HashData(bytes))) throw new Exception("Hydration digest mismatch.");
    Console.WriteLine("PASS real FETCH_DATA -> SQLite -> Agent pipe -> source stream -> CfExecute");
    try { await File.WriteAllTextAsync(local, "must not edit"); throw new Exception("Local write unexpectedly succeeded."); } catch (UnauthorizedAccessException) { Console.WriteLine("PASS read-only placeholder rejects normal local write"); }
    var release = await Command("release", "--root", root, "--path", file, "--pipe", bridge.PipeName, "--agent-pid", Environment.ProcessId.ToString());
    if (release.Code != 0) throw new Exception("Release failed: " + release.Text);
    using (var state = await Status()) if (state.RootElement.GetProperty("onDiskDataSize").GetInt64() != 0) throw new Exception("Release left local bytes.");
    if (!File.ReadAllBytes(Path.Combine(sourcePath, file)).SequenceEqual(bytes)) throw new Exception("Remote source changed.");
    Console.WriteLine("PASS indexed read-only cache release preserves source");
    stop.Set(); await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
    Console.WriteLine("PASS indexed CFAPI integration (local read-only fixture; no NAS assertion)");
}
finally
{
    stop.Set();
    if (started && !process.HasExited) { try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15)); } catch (TimeoutException) { process.Kill(); } }
    string[] log; lock (lines) log = lines.ToArray(); await File.WriteAllLinesAsync(Path.Combine(fixture, "provider.log"), log);
}
ProcessStartInfo Start(params string[] argv)
{
    var info = new ProcessStartInfo(native) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true }; foreach (string arg in argv) info.ArgumentList.Add(arg); return info;
}
async Task<(int Code, string Text)> Command(params string[] argv)
{
    using var p = Process.Start(Start(argv))!; Task<string> stdout = p.StandardOutput.ReadToEndAsync(), stderr = p.StandardError.ReadToEndAsync();
    try { await p.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(40)); }
    catch (TimeoutException) { p.Kill(); throw new IOException("Native command timeout: " + await stdout + await stderr); }
    return (p.ExitCode, await stdout + await stderr);
}
async Task<JsonDocument> Status()
{
    var result = await Command("status", "--root", root, "--path", file); if (result.Code != 0) throw new IOException(result.Text); return JsonDocument.Parse(result.Text);
}
