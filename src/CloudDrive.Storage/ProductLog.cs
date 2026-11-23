using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Channels;

namespace CloudDrive.Storage;

public sealed record ProductLogEntry(DateTimeOffset At, int ProcessId, int ThreadId, string Category, string Operation,
    Guid? ConnectionId, string? FileId, Guid? TaskId, string Result, int? ErrorCode, long? ElapsedMilliseconds, string Message);
public sealed partial class ProductLog : IAsyncDisposable
{
    private readonly Channel<ProductLogEntry> queue = Channel.CreateBounded<ProductLogEntry>(new BoundedChannelOptions(4096) { FullMode = BoundedChannelFullMode.Wait, SingleReader = true });
    private readonly string root; private readonly ProductSettings settings; private readonly Task writer;
    private long dropped;
    public long Dropped => Interlocked.Read(ref dropped);
    public ProductLog(string root, ProductSettings settings) { this.root = root; this.settings = settings.Validate(); writer = WriteLoop(); }
    public void Write(string category, string operation, string message, Guid? connection = null, string? fileId = null, Guid? taskId = null, string result = "Information", int? errorCode = null, long? elapsed = null)
    {
        if (!Categories.Contains(category)) category = "Application";
        if (!queue.Writer.TryWrite(new(DateTimeOffset.UtcNow, Environment.ProcessId, Environment.CurrentManagedThreadId, category, operation, connection, fileId, taskId, result, errorCode, elapsed, Redact(message)))) Interlocked.Increment(ref dropped);
    }
    private static readonly HashSet<string> Categories = ["Application", "Agent", "CFAPI", "SMB", "Sync", "Cache", "Archive", "Installer", "Crash"];
    public static string Redact(string text)
    {
        text = Sensitive().Replace(text, "$1=[REDACTED]");
        text = text.Replace(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "%USERPROFILE%", StringComparison.OrdinalIgnoreCase);
        text = Unc().Replace(text, "[REMOTE_PATH]"); return text.Length > 4096 ? text[..4096] : text;
    }
    [GeneratedRegex(@"(?i)(password|passwd|pwd|token|secret|authorization)\s*[:=]\s*(?:""[^""]*""|'[^']*'|[^\s;,]+)")] private static partial Regex Sensitive();
    [GeneratedRegex(@"\\\\[^\s""']+")] private static partial Regex Unc();
    private async Task WriteLoop()
    {
        var sequences = new Dictionary<string, int>();
        await foreach (var entry in queue.Reader.ReadAllAsync())
        {
            try
            {
                if (settings.LogLevel == "Error" && entry.Result != "Error" || settings.LogLevel == "Warning" && entry.Result is not ("Warning" or "Error")) continue;
                string directory = Path.Combine(root, entry.Category); Directory.CreateDirectory(directory);
                string key = entry.Category + entry.At.ToString("yyyyMMdd"); sequences.TryGetValue(key, out int sequence);
                string PathFor() => Path.Combine(directory, $"cld-{entry.At:yyyyMMdd}-{Environment.ProcessId}-{sequence:D3}.jsonl");
                string path = PathFor(); if (File.Exists(path) && new FileInfo(path).Length >= (long)settings.LogFileMiB << 20) { sequence++; sequences[key] = sequence; path = PathFor(); }
                await File.AppendAllTextAsync(path, JsonSerializer.Serialize(entry) + Environment.NewLine);
                foreach (var file in new DirectoryInfo(directory).EnumerateFiles("cld-*.jsonl").Where(f => f.LastWriteTimeUtc < DateTime.UtcNow.AddDays(-settings.LogDays))) file.Delete();
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Interlocked.Increment(ref dropped); }
        }
    }
    public async ValueTask DisposeAsync() { queue.Writer.TryComplete(); await writer; }
}
