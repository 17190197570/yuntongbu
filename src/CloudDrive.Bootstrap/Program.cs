using CloudDrive.Storage;
using System.Diagnostics;

try
{
if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041) || !Environment.Is64BitProcess) return 2;
ProductEnvironment.EnsureDirectories();
if (args is ["--apply-update", var package]) return await ProductUpdate.InstallStagedAsync(package, CancellationToken.None);
if (File.Exists(ProductMaintenance.Marker)) return 4;
IDisposable singleton;
try { singleton = ConnectionLock.Acquire(Guid.Empty, "bootstrap"); }
catch (IOException)
{
    if (!args.Contains("--background") && !args.Contains("--login")) using (Process.Start(ProductEnvironment.StartInfo("CloudDrive.Desktop"))) { }
    return 0;
}
using (singleton)
{
    if (args.Contains("--login")) await Task.Delay(TimeSpan.FromSeconds(ProductSettings.Load().StartupDelaySeconds));
    if (!args.Contains("--background") && !args.Contains("--login")) using (Process.Start(ProductEnvironment.StartInfo("CloudDrive.Desktop"))) { }
    var restart = new RestartBudget();
    for (;;)
    {
        // A separately started live daemon is adopted by IPC, never terminated or duplicated.
        try
        {
            var live = await ManagementClient.SendAsync(new("status"), timeoutSeconds: 2);
            if (live.Success)
            {
                using var adopted = Process.GetProcessById(live.Snapshot!.AgentPid); await adopted.WaitForExitAsync();
                if (File.Exists(Path.Combine(ProductEnvironment.DataRoot, "Config", "shutdown.request"))) return 0;
            }
        }
        catch (Exception e) when (e is IOException or OperationCanceledException or TimeoutException or ArgumentException) { }
        string stopMarker = Path.Combine(ProductEnvironment.DataRoot, "Config", "shutdown.request");
        if (File.Exists(stopMarker)) File.Delete(stopMarker);
        using var child = Process.Start(ProductEnvironment.StartInfo("CloudDrive.Agent", "product-daemon")) ?? throw new IOException("Agent启动失败。");
        await child.WaitForExitAsync();
        if (child.ExitCode == 0 || File.Exists(stopMarker)) return 0;
        var delay = restart.Failure(DateTimeOffset.UtcNow);
        ProductEnvironment.AtomicJson(Path.Combine(ProductEnvironment.LogRoot, "Crash", "bootstrap-last.json"), new { At = DateTimeOffset.UtcNow, child.ExitCode, RetrySeconds = delay?.TotalSeconds, Exhausted = delay is null });
        if (delay is null) return 3;
        await Task.Delay(delay.Value);
    }
}
}
catch (Exception failure)
{
    ProductEnvironment.AtomicJson(Path.Combine(ProductEnvironment.LogRoot, "Crash", "bootstrap-error.json"), new { At = DateTimeOffset.UtcNow, Type = failure.GetType().Name, failure.HResult });
    return 2;
}
