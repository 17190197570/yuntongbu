using System.IO.Pipes;
using System.Text.Json;

namespace CloudDrive.Storage;

public sealed record ManagementRequest(string Command, Guid? ConnectionId = null, Guid? ItemId = null, string? RelativePath = null, string? Operation = null, bool Confirmed = false, bool DataAuthorized = false);
public sealed record ProviderHealth(Guid ConnectionId, int HostPid, bool Ready, DateTimeOffset ObservedAt, string State, bool SmbOnline = false, DateTimeOffset? HeartbeatAt = null);
public sealed record ProductSnapshot(int AgentPid, DateTimeOffset UpdatedAt, string State, bool DatabaseHealthy,
    IReadOnlyList<ProviderHealth> Providers, long IndexedFiles, long CacheBytes, long Waiting, long Failed,
    long ActiveUploads, int ActiveDownloads, int ArchiveActive, bool SyncPaused, bool ArchivePaused);
public sealed record ManagementReply(bool Success, string Message, ProductSnapshot? Snapshot = null, HealthSnapshot? Health = null, RecoveryPlan? Plan = null, FileDiagnosis? Diagnosis = null);

public static class ManagementClient
{
    public static async Task<ManagementReply> SendAsync(ManagementRequest request, CancellationToken token = default, string? pipeName = null, int timeoutSeconds = 15)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token); timeout.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
        await using var pipe = new NamedPipeClientStream(".", pipeName ?? ProductEnvironment.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        await pipe.ConnectAsync(timeout.Token);
        using var writer = new StreamWriter(pipe, leaveOpen: true) { AutoFlush = true }; using var reader = new StreamReader(pipe, leaveOpen: true);
        await writer.WriteLineAsync(JsonSerializer.Serialize(request).AsMemory(), timeout.Token);
        return JsonSerializer.Deserialize<ManagementReply>(await reader.ReadLineAsync(timeout.Token) ?? throw new IOException("Agent未返回状态。")) ?? throw new IOException("Agent响应无效。");
    }
    public static async Task EnsureStartedAsync(CancellationToken token)
    {
        try { if ((await SendAsync(new("status"), token, timeoutSeconds: 1)).Success) return; }
        catch (Exception e) when (e is IOException or OperationCanceledException or TimeoutException) { token.ThrowIfCancellationRequested(); }
        using var process = System.Diagnostics.Process.Start(ProductEnvironment.StartInfo("CloudDrive.Bootstrap", "--background")) ?? throw new IOException("Bootstrap启动失败。");
        for (int attempt = 0; attempt < 30; attempt++)
        {
            await Task.Delay(500, token);
            try { if ((await SendAsync(new("status"), token, timeoutSeconds: 1)).Success) return; }
            catch (Exception e) when (e is IOException or OperationCanceledException or TimeoutException) { token.ThrowIfCancellationRequested(); }
        }
        throw new IOException("后台启动未完成，请检查Agent/Crash日志；没有强制结束进程。");
    }
}

public sealed class ManagementServer(Func<ManagementRequest, Task<ManagementReply>> handler, string? pipeName = null)
{
    public async Task RunAsync(CancellationToken token)
    {
        var clients = new List<Task>(); using var slots = new SemaphoreSlim(8);
        try
        {
            while (!token.IsCancellationRequested)
            {
                await slots.WaitAsync(token);
                var pipe = new NamedPipeServerStream(pipeName ?? ProductEnvironment.PipeName, PipeDirection.InOut, 8, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly, 4096, 4096);
                try { await pipe.WaitForConnectionAsync(token); }
                catch { pipe.Dispose(); slots.Release(); throw; }
                clients.RemoveAll(t => t.IsCompleted); clients.Add(Task.Run(async () =>
                {
                    try
                    {
                        await using (pipe)
                        {
                            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token); deadline.CancelAfter(TimeSpan.FromSeconds(20));
                            using var interrupt = deadline.Token.Register(() => pipe.Dispose());
                            // The fixed binary-free protocol has a small bounded request. No executable/path/password input.
                            using var reader = new StreamReader(pipe, leaveOpen: true); using var writer = new StreamWriter(pipe, leaveOpen: true) { AutoFlush = true };
                            char[] buffer = new char[2048]; int length = 0;
                            while (length < buffer.Length)
                            { int count = await reader.ReadAsync(buffer.AsMemory(length, 1), deadline.Token); if (count == 0 || buffer[length] == '\n') break; length++; }
                            if (length == buffer.Length) throw new IOException("管理请求超出限制。");
                            var request = JsonSerializer.Deserialize<ManagementRequest>(new string(buffer, 0, length)) ?? throw new IOException("无效请求。");
                            var result = await handler(request); await writer.WriteLineAsync(JsonSerializer.Serialize(result).AsMemory(), deadline.Token);
                        }
                    }
                    catch (Exception e) when (e is IOException or OperationCanceledException or ObjectDisposedException or JsonException) { }
                    finally { slots.Release(); }
                }, CancellationToken.None));
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        finally { await Task.WhenAll(clients); }
    }
}
