using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Text.Json;

namespace CloudDrive.Storage;

public sealed class AgentRuntime(IndexStore index, ICredentialVault vault, string? nativeDirectory = null, Action<ProviderHealth>? health = null)
{
    public SmbMapper Mapper { get; } = new(index, vault);
    public ConnectionTester Tester => new(Mapper);
    public ICachePlatform CachePlatform => new WindowsCachePlatform(index, async (connection, cancellation) => await Mapper.ConnectAsync(index.Get(connection.Id) ?? connection, cancellation), nativeDirectory);
    public CacheManager Cache => new(index, CachePlatform);
    public ArchiveManager Archive => new(index, async (connection, cancellation) => await Mapper.ConnectAsync(index.Get(connection.Id) ?? connection, cancellation), CachePlatform, new WindowsArchiveLocalGuard());
    public async Task ScanAsync(SmbConnection config, CancellationToken token)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token); timeout.CancelAfter(TimeSpan.FromSeconds(30));
        string root = await Mapper.ConnectAsync(config, timeout.Token);
        await new RemoteScanner(index).ScanAsync(config.Id, new FileSystemSource(config.Id, root), token);
        SignalRefresh(config.Id);
    }
    public async Task<int> RunAsync(SmbConnection config, CancellationToken token)
    {
        config.Validate(); using var ownership = ConnectionLock.Acquire(config.Id, "agent");
        string native = Path.Combine(nativeDirectory ?? AppContext.BaseDirectory, "CloudProviderHost.exe");
        if (!File.Exists(native)) throw new FileNotFoundException("请先构建 CloudProviderHost.exe。", native);
        using var stop = new EventWaitHandle(false, EventResetMode.ManualReset, EventName(config.Id, "Stop"));
        using var refresh = new EventWaitHandle(false, EventResetMode.AutoReset, EventName(config.Id, "Refresh"));
        stop.Reset();
        using var tokenRegistration = token.Register(() => stop.Set());
        // An existing index can populate the namespace while NAS is offline. Startup never clears it.
        if (index.ScanStatus(config.Id) == "尚未扫描")
        {
            try { await ScanAsync(config, token); }
            catch (Exception error) when (error is not OperationCanceledException) { index.Log(config.Id, "offline-start", FailureClassifier.Describe("startup", error).Message); }
        }
        await using var bridge = new SmbBridge(index, config, async cancellation =>
        {
            string root = await Mapper.ConnectAsync(index.Get(config.Id) ?? config, cancellation);
            return new FileSystemSource(config.Id, root);
        }, async (storage, cancellation) => new FileSystemSource(storage.Id, await Mapper.ConnectAsync(index.Get(storage.Id) ?? storage, cancellation)));
        bridge.Start();
        var start = new ProcessStartInfo(native) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        foreach (string argument in new[] { "run-smb", "--source", config.Id.ToString("N"), "--root", config.SyncRoot,
            "--pipe", bridge.PipeName, "--agent-pid", Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "--stop-event", EventName(config.Id, "Stop"), "--refresh-event", EventName(config.Id, "Refresh") }) start.ArgumentList.Add(argument);
        using var process = new Process { StartInfo = start };
        using var recoveryStop = CancellationTokenSource.CreateLinkedTokenSource(token);
        Task recovery = Task.CompletedTask;
        Task sync = Task.CompletedTask;
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int smbOnline = 0;
        long heartbeatTicks = 0;
        var newFiles = new NewFileSyncAgent(index, config, async cancellation =>
            await Mapper.ConnectAsync(index.Get(config.Id) ?? config, cancellation));
        var access = new CacheAccessTracker(index, config, CachePlatform);
        try
        {
            if (!process.Start()) throw new IOException("无法启动 CFAPI Host。");
            health?.Invoke(new(config.Id, process.Id, false, DateTimeOffset.UtcNow, "Host启动中"));
            index.SetRuntime(config.Id, "CFAPI Host 启动中", null);
            recovery = RecoverAsync(config, recoveryStop.Token, online => { Interlocked.Exchange(ref smbOnline, online ? 1 : 0); long ticks = Interlocked.Read(ref heartbeatTicks); health?.Invoke(new(config.Id, process.Id, ready.Task.IsCompletedSuccessfully, DateTimeOffset.UtcNow, online ? "SMB在线" : "SMB离线/重试", online, ticks == 0 ? null : new DateTimeOffset(ticks, TimeSpan.Zero))); });
            Task output = CaptureAsync(process.StandardOutput, config.Id, token, line =>
            {
                if (line.StartsWith("READY", StringComparison.Ordinal)) { ready.TrySetResult(); var at = DateTimeOffset.UtcNow; Interlocked.Exchange(ref heartbeatTicks, at.Ticks); health?.Invoke(new(config.Id, process.Id, true, at, "CFAPI READY", Volatile.Read(ref smbOnline) != 0, at)); }
                if (line.StartsWith("HEALTH cfapi=connected", StringComparison.Ordinal)) { var at = DateTimeOffset.UtcNow; Interlocked.Exchange(ref heartbeatTicks, at.Ticks); health?.Invoke(new(config.Id, process.Id, true, at, "CFAPI连接探针正常", Volatile.Read(ref smbOnline) != 0, at)); }
                if (line.StartsWith("LOCAL_HINT ", StringComparison.Ordinal))
                {
                    int split = line.IndexOf(' ', 11);
                    if (split > 0) { try { newFiles.Hint(line[11..split], JsonSerializer.Deserialize<string>(line[(split + 1)..])); } catch (JsonException) { newFiles.Hint("NativeOverflow", null); } }
                }
                if (line.StartsWith("CACHE_HINT ", StringComparison.Ordinal))
                {
                    try { if (JsonSerializer.Deserialize<CacheHint>(line[11..]) is { } hint) access.Hint(hint); }
                    catch (JsonException) { access.Hint(new("Overflow")); }
                }
            });
            Task errors = CaptureAsync(process.StandardError, config.Id, token);
            sync = Task.Run(async () =>
            {
                await ready.Task.WaitAsync(recoveryStop.Token);
                Task[] services = [newFiles.RunAsync(recoveryStop.Token), access.RunAsync(recoveryStop.Token), Cache.RunBackgroundAsync(recoveryStop.Token), Archive.RunBackgroundAsync(recoveryStop.Token)];
                Task first = await Task.WhenAny(services);
                if (!recoveryStop.IsCancellationRequested) { await recoveryStop.CancelAsync(); stop.Set(); }
                await Task.WhenAll(services); await first;
            }, recoveryStop.Token);
            _ = sync.ContinueWith(task => { if (task.IsFaulted) { stop.Set(); index.Log(config.Id, "sync-agent-failure", "新增同步调度异常；停止 Host，任务日志保留。"); } }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            await process.WaitForExitAsync(CancellationToken.None);
            health?.Invoke(new(config.Id, process.Id, false, DateTimeOffset.UtcNow, "Host已退出"));
            await Task.WhenAll(output, errors);
            index.SetRuntime(config.Id, process.ExitCode == 0 ? "已停止" : "CFAPI Host 失败，检查日志", null);
            return process.ExitCode;
        }
        finally
        {
            stop.Set(); await recoveryStop.CancelAsync();
            try { await recovery; } catch (OperationCanceledException) { }
            try { await sync; } catch (OperationCanceledException) { }
            // No wildcard disconnection and no unregister at shutdown. Fully hydrated cache stays in place.
        }
    }
    private async Task RecoverAsync(SmbConnection original, CancellationToken token, Action<bool>? connectivity = null)
    {
        using var wake = new SemaphoreSlim(0, 1); int failures = 0;
        NetworkAddressChangedEventHandler changed = (_, _) => { try { wake.Release(); } catch (SemaphoreFullException) { } catch (ObjectDisposedException) { } };
        NetworkChange.NetworkAddressChanged += changed;
        try
        {
            while (!token.IsCancellationRequested)
            {
                var config = index.Get(original.Id); if (config is null) return;
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token); deadline.CancelAfter(TimeSpan.FromSeconds(30));
                try
                {
                    string root = await Mapper.ConnectAsync(config, deadline.Token);
                    await Task.Run(() => FileSystemSource.CheckChain(root), deadline.Token);
                    if (failures > 0) index.Log(config.Id, "reconnect", "SMB 已恢复，既有占位和缓存保留。");
                    index.SetRuntime(config.Id, "SMB 在线 / CFAPI 运行中", null); failures = 0;
                    connectivity?.Invoke(true);
                    if (index.ScanStatus(config.Id).StartsWith("running", StringComparison.Ordinal) || index.ScanStatus(config.Id).StartsWith("restart", StringComparison.Ordinal) || index.ScanStatus(config.Id) == "尚未扫描")
                    {
                        await new RemoteScanner(index).ScanAsync(config.Id, new FileSystemSource(config.Id, root), token); SignalRefresh(config.Id);
                    }
                }
                catch (Exception error) when (!token.IsCancellationRequested)
                {
                    failures = Math.Min(8, failures + 1);
                    var diagnostic = FailureClassifier.Describe("recovery", error);
                    connectivity?.Invoke(false);
                    index.SetRuntime(config.Id, diagnostic.Message, null);
                    index.Log(config.Id, "retry", diagnostic.Message + "；保留索引和本地文件，等待重试。");
                    if (diagnostic.Kind is FailureKind.Authentication or FailureKind.NetworkUnreachable or FailureKind.Timeout or FailureKind.Unknown)
                    {
                        try { await Mapper.DisconnectOwnedAsync(config, token); }
                        catch (Exception reconnectError) when (!token.IsCancellationRequested) { index.Log(config.Id, "reconnect-deferred", FailureClassifier.Describe("reconnect", reconnectError).Message); }
                    }
                }
                await wake.WaitAsync(TimeSpan.FromSeconds(failures == 0 ? 30 : Math.Min(60, 1 << failures)), token);
            }
        }
        finally { NetworkChange.NetworkAddressChanged -= changed; }
    }
    private async Task CaptureAsync(StreamReader reader, Guid id, CancellationToken token, Action<string>? received = null)
    {
        string? line;
        while ((line = await reader.ReadLineAsync(CancellationToken.None)) is not null)
        {
            received?.Invoke(line);
            index.Log(id, "cfapi", line);
            var marker = line.IndexOf("placeholders=", StringComparison.Ordinal);
            if (marker >= 0 && long.TryParse(line[(marker + 13)..].Split(' ')[0], out long count)) index.SetRuntime(id, "CFAPI 已连接", count);
            if (line.StartsWith("READY", StringComparison.Ordinal)) Console.WriteLine(line);
            if (token.IsCancellationRequested) break;
        }
    }
    public async Task<int> ReleaseAsync(SmbConnection config, string relative, CancellationToken token)
    {
        var manager = Cache; Guid run = manager.QueueManual(config.Id, Paths.Relative(relative), "Release", false);
        await manager.ExecuteAsync(run, token);
        return index.CacheOperations(run).All(o => o.Status == "Completed") ? 0 : 1;
    }
    public async Task DisconnectOwnedAsync(SmbConnection config, CancellationToken token)
    {
        using var lease = ConnectionLock.Acquire(config.Id, "agent");
        await Mapper.DisconnectOwnedAsync(config, token);
    }
    public static string EventName(Guid id, string action) => @"Local\CloudDriveLite." + action + "." + id.ToString("N");
    public static void SignalRefresh(Guid id)
    {
        if (EventWaitHandle.TryOpenExisting(EventName(id, "Refresh"), out var handle)) using (handle) handle.Set();
    }
    public static void Stop(Guid id)
    {
        if (EventWaitHandle.TryOpenExisting(EventName(id, "Stop"), out var handle)) using (handle) handle.Set();
    }
}
