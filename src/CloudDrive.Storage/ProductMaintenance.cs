namespace CloudDrive.Storage;

public static class ProductMaintenance
{
    public static string Marker => Path.Combine(ProductEnvironment.DataRoot, "Config", "maintenance.json");
    public static async Task StopAsync(IndexStore index, CancellationToken token)
    {
        try { await ManagementClient.SendAsync(new("shutdown"), token, timeoutSeconds: 3); }
        catch (Exception e) when (e is IOException or OperationCanceledException or TimeoutException) { token.ThrowIfCancellationRequested(); }
        foreach (var connection in index.Connections()) AgentRuntime.Stop(connection.Id);
        for (int i = 0; i < 60; i++)
        {
            try { using var offline = AcquireOffline(index); return; }
            catch (IOException) { await Task.Delay(500, token); }
        }
        throw new IOException("后台仍在收尾或另一个会话持有连接；拒绝强制升级/恢复/卸载。");
    }
    public static IDisposable AcquireOffline(IndexStore index)
    {
        var locks = new List<IDisposable>();
        try
        {
            foreach (string purpose in new[] { "product-maintenance", "product-agent", "bootstrap", "archive-execution", "cache-execution" }) locks.Add(ConnectionLock.Acquire(Guid.Empty, purpose));
            foreach (var connection in index.Connections()) locks.Add(ConnectionLock.Acquire(connection.Id, "agent"));
            return new Lease(locks);
        }
        catch { foreach (var held in locks.AsEnumerable().Reverse()) held.Dispose(); throw; }
    }
    private sealed class Lease(List<IDisposable> held) : IDisposable { public void Dispose() { foreach (var item in held.AsEnumerable().Reverse()) item.Dispose(); } }
}
