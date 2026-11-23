namespace CloudDrive.Storage;

// The UI first waits for the affected Agent(s) to stop, then holds these gates through
// credential/configuration changes. Active scans, cache work and archive work fail closed.
public static class MappingMaintenance
{
    public static IDisposable Acquire(IEnumerable<Guid> mappings)
    {
        var held = new List<IDisposable>();
        try
        {
            foreach (string purpose in new[] { "archive-execution", "cache-execution" }) held.Add(ConnectionLock.Acquire(Guid.Empty, purpose));
            foreach (Guid id in mappings.Distinct().Order())
                foreach (string purpose in new[] { "agent", "scan" }) held.Add(ConnectionLock.Acquire(id, purpose));
            return new Lease(held);
        }
        catch (Exception e)
        {
            foreach (var gate in held.AsEnumerable().Reverse()) gate.Dispose();
            if (e is IOException) throw new IOException("后台正在扫描或处理文件，请等待完成后重试。配置及凭据未更改。", e);
            throw;
        }
    }
    private sealed class Lease(List<IDisposable> held) : IDisposable
    {
        public void Dispose() { foreach (var gate in held.AsEnumerable().Reverse()) gate.Dispose(); }
    }
}
