namespace CloudDrive.Storage;

public enum CacheState { OnlineOnly, LocallyAvailable, AlwaysAvailable, Downloading, Uploading, NotSynced, Conflict, Dehydrating, DehydrationFailed }
public sealed record CacheSnapshot(string FileId, Guid ConnectionId, string RelativePath, bool IsDirectory,
    bool Owned, bool InSync, bool Pinned, long LogicalBytes, long HydratedBytes, long AllocatedBytes,
    long ModifiedBytes = 0, string? Error = null);
public sealed record CacheFile(CacheSnapshot Snapshot, CacheState State, DateTimeOffset? LastUserAccessAt,
    DateTimeOffset? LastOpenAt, DateTimeOffset? LastCloseAt, DateTimeOffset? LastHydratedAt, int OpenCount,
    bool Excluded, string? BlockReason);
public sealed record CachePolicy(bool Enabled = false, bool AgeEnabled = false, bool CapacityEnabled = false, bool DiskEnabled = false,
    long MaxCacheBytes = 50L << 30, long TriggerCacheBytes = 50L << 30, long TargetCacheBytes = 40L << 30,
    long MinDiskFreeBytes = 20L << 30, long TriggerDiskFreeBytes = 15L << 30, long TargetDiskFreeBytes = 30L << 30,
    int InactivityDays = 30, int ProtectionDays = 7, int PriorityDays = 90, int HighPriorityDays = 180, int CleanupConcurrency = 1)
{
    public CachePolicy Validate()
    {
        if (MaxCacheBytes <= 0 || TriggerCacheBytes <= 0 || TriggerCacheBytes > MaxCacheBytes || TargetCacheBytes < 0 || TargetCacheBytes >= TriggerCacheBytes ||
            TriggerDiskFreeBytes < 0 || MinDiskFreeBytes < TriggerDiskFreeBytes || TargetDiskFreeBytes <= MinDiskFreeBytes ||
            ProtectionDays < 0 || InactivityDays < ProtectionDays || InactivityDays < 1 || PriorityDays < InactivityDays || HighPriorityDays < PriorityDays || CleanupConcurrency is < 1 or > 4)
            throw new ArgumentException("缓存策略水位、时间或并发无效：目标缓存低于触发值；磁盘目标高于预警值；保护天数≤清理天数≤优先天数≤高优先天数；并发1–4。");
        return this;
    }
}
public sealed record CacheHint(string Kind, string? Path = null, int ProcessId = 0, string? ProcessName = null);
public sealed record CacheAccess(string FileId, Guid ConnectionId, string Path, string Kind, DateTimeOffset At, int ProcessId, string? ProcessName, string Source, bool UserAccess);
public sealed record CacheOperation(Guid Id, Guid RunId, Guid ConnectionId, string FileId, string Path, string Type, string Trigger,
    string Status, long BeforeBytes, long? AfterBytes, long ReleasedBytes, string? Error);
public sealed record CacheRun(Guid Id, Guid? ConnectionId, string Trigger, string Status, string StartedAt, string? CompletedAt,
    int Candidates, int Succeeded, int Failed, long ReleasedBytes, string? Error, string? ScopePath = null);
public sealed record CacheVolume(string Key, long AvailableBytes, long TotalBytes);
public interface ICachePlatform
{
    CacheSnapshot Probe(SmbConnection connection, string relative);
    CacheVolume Volume(SmbConnection connection);
    Task ReleaseAsync(SmbConnection connection, string relative, CancellationToken token);
    Task PinAsync(SmbConnection connection, string relative, bool pinned, CancellationToken token);
}
public static class CacheSafety
{
    public static string? Reject(CacheFile file, bool automatic, DateTimeOffset now, CachePolicy policy)
    {
        var s = file.Snapshot;
        if (!s.Owned || s.Error is not null) return s.Error ?? "不是当前连接的有效占位文件";
        if (s.IsDirectory) return "目录自身不含可释放内容";
        if (!s.InSync || s.ModifiedBytes != 0) return "CFAPI 存在未同步修改";
        if (s.Pinned || file.Excluded) return "用户固定或保护规则排除";
        if (file.BlockReason is not null) return file.BlockReason;
        if (file.OpenCount > 0) return "文件仍有打开提示；实际句柄保护也将再次检查";
        if (s.HydratedBytes == 0 || s.AllocatedBytes == 0) return "没有可释放本地内容";
        if (s.HydratedBytes < s.LogicalBytes) return "部分水合暂不释放，避免校验触发下载";
        if (automatic && file.LastUserAccessAt is null) return "尚无有效用户访问记录";
        if (automatic && file.LastUserAccessAt > now.AddDays(-policy.ProtectionDays)) return "最近访问保护期";
        // A recent unidentified/background open is protected briefly, without altering user LRU.
        if (automatic && file.LastOpenAt > now.AddMinutes(-5)) return "最近五分钟存在打开活动";
        return null;
    }
}
