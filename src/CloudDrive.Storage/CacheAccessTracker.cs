using System.Runtime.InteropServices;
using System.Threading.Channels;

namespace CloudDrive.Storage;

public sealed class CacheAccessTracker(IndexStore index, SmbConnection config, ICachePlatform platform)
{
    private readonly Channel<(CacheHint Hint, DateTimeOffset Time, bool Foreground)> hints = Channel.CreateBounded<(CacheHint, DateTimeOffset, bool)>(8192);
    private int overflow;
    public void Hint(CacheHint hint)
    {
        bool foreground = false;
        if (hint.ProcessId > 0) { GetWindowThreadProcessId(GetForegroundWindow(), out uint pid); foreground = pid == hint.ProcessId; }
        if (!hints.Writer.TryWrite((hint, DateTimeOffset.UtcNow, foreground))) Interlocked.Exchange(ref overflow, 1);
    }
    public static (string Source, bool User) Classify(int pid, string? image, bool foreground)
    {
        string name = Path.GetFileName(image ?? "");
        if (pid == Environment.ProcessId || name.StartsWith("CloudDrive.", StringComparison.OrdinalIgnoreCase) || name.Equals("CloudProviderHost.exe", StringComparison.OrdinalIgnoreCase)) return ("Self", false);
        if (new[] { "SearchIndexer.exe", "SearchProtocolHost.exe", "SearchFilterHost.exe", "MsMpEng.exe", "MpDefenderCoreService.exe", "MsSense.exe", "explorer.exe", "dllhost.exe" }.Contains(name, StringComparer.OrdinalIgnoreCase)) return ("BackgroundOrShell", false);
        return pid > 0 && name.Length > 0 && foreground ? ("ForegroundApplication", true) : ("UnknownOpen", false);
    }
    public async Task RunAsync(CancellationToken token)
    {
        index.ResetCacheOpens(config.Id);
        while (!token.IsCancellationRequested)
        {
            await Task.Delay(500, token);
            var events = new List<CacheAccess>(); var refresh = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < 8192 && hints.Reader.TryRead(out var item); i++)
            {
                if (item.Hint.Kind == "Overflow" || item.Hint.Path is null) { Interlocked.Exchange(ref overflow, 1); continue; }
                string path;
                try { path = Paths.Relative(item.Hint.Path); } catch (ArgumentException) { Interlocked.Exchange(ref overflow, 1); continue; }
                var (source, user) = Classify(item.Hint.ProcessId, item.Hint.ProcessName, item.Foreground);
                if (source == "Self" && item.Hint.Kind is "Opened" or "Closed") continue;
                events.Add(new(Paths.StableId(config.Id, path), config.Id, path, item.Hint.Kind, item.Time, item.Hint.ProcessId, Path.GetFileName(item.Hint.ProcessName ?? ""), source, user && item.Hint.Kind == "Opened"));
                if (item.Hint.Kind is "Hydrated" or "DownloadFailed" or "Closed") refresh.Add(path);
            }
            if (events.Count > 0) index.RecordCacheAccess(events);
            foreach (string path in refresh) index.SaveCacheBatch([platform.Probe(config, path)]);
            if (Interlocked.Exchange(ref overflow, 0) != 0)
            {
                // Lost open/close pairs cannot establish a user access time. Native oplock still gates release.
                index.Log(config.Id, "cache-hint-overflow", "访问提示不完整；不推断有效访问，触发实际状态校准。");
                try { await new CacheManager(index, platform).CalibrateAsync(token); } catch (IOException) { }
            }
        }
    }
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint window, out uint processId);
}
