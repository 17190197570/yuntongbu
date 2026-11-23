namespace CloudDrive.Storage;

public enum ArchiveStatus { Pending, Scanning, Eligible, Copying, Verifying, TargetCommitted, MappingUpdated, SourceDeleting, Completed, Failed, Conflict, Paused, RecoveryRequired, Cancelled, Skipped }
public enum ArchiveCollision { Skip, ConflictName, Manual }
public sealed record ArchivePolicy(Guid Id, string TaskName, Guid SourceStorageId, Guid TargetStorageId,
    string SourceRootPath = "", string TargetRootPath = "", int CreationDays = 180, int ModificationDays = 180,
    bool Recursive = true, bool PreserveStructure = true, bool IncludeHidden = false, bool IncludeSystem = false,
    bool IncludePinned = false, bool PinnedConfirmed = false, bool AllowInUse = false,
    bool Enabled = false, string ScheduleType = "Weekly", int ScheduleDay = 0, string ScheduleTime = "02:00",
    string? TimeZoneId = null, int WindowMinutes = 120, int Concurrency = 1, long? MaxBytes = null,
    ArchiveCollision Collision = ArchiveCollision.Skip, bool RetryFailures = true, bool Notify = true)
{
    public ArchivePolicy Validate()
    {
        if (Id == Guid.Empty || string.IsNullOrWhiteSpace(TaskName) || SourceStorageId == Guid.Empty || TargetStorageId == Guid.Empty || SourceStorageId == TargetStorageId)
            throw new ArgumentException("归档名称及不同的源/目标连接必填。");
        ArchivePaths.Optional(SourceRootPath); ArchivePaths.Optional(TargetRootPath);
        if (CreationDays < 1 || CreationDays > 36500 || ModificationDays < 1 || ModificationDays > 36500 || Concurrency is < 1 or > 4 || MaxBytes is <= 0 || WindowMinutes is < 1 or > 1440)
            throw new ArgumentException("归档天数、容量、执行窗口或并发无效。");
        if (AllowInUse) throw new ArgumentException("无法在缺少安全独占保护时迁移使用中的文件；此选项暂不支持。");
        if (IncludePinned && !PinnedConfirmed) throw new ArgumentException("包含固定文件必须明确确认；迁移仍保留本地缓存及固定状态。");
        if (ScheduleType is not ("Weekly" or "Monthly") || (ScheduleType == "Weekly" ? ScheduleDay is < 0 or > 6 : ScheduleDay is < 1 or > 28) ||
            !TimeOnly.TryParseExact(ScheduleTime, "HH:mm", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out _))
            throw new ArgumentException("计划须为每周0–6（周日为0）或每月1–28日，以及HH:mm时间。");
        _ = TimeZoneInfo.FindSystemTimeZoneById(TimeZoneId ?? TimeZoneInfo.Local.Id);
        if (!Enum.IsDefined(Collision)) throw new ArgumentException("同名策略无效。");
        return this;
    }
}
public static class ArchivePaths
{
    public static void Optional(string path) { if (path.Length > 0) Paths.Relative(path); }
    public static string Combine(string parent, string child) { Optional(parent); Paths.Relative(child); return Paths.Relative(parent.Length == 0 ? child : parent + "\\" + child); }
    public static string Target(ArchivePolicy policy, string source)
    {
        Paths.Relative(source);
        if (!CacheManager.InScope(source, policy.SourceRootPath, true) || source.Equals(policy.SourceRootPath, StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("文件不在归档源目录内。");
        string relative = policy.SourceRootPath.Length == 0 ? source : source[(policy.SourceRootPath.Length + 1)..];
        if (!policy.Recursive && relative.Contains('\\')) throw new ArgumentException("已禁用递归。");
        return Combine(policy.TargetRootPath, policy.PreserveStructure ? relative : relative.Split('\\')[^1]);
    }
}
public sealed record ArchivePreview(RemoteEntry Entry, string TargetPath, bool Eligible, string Reason, bool Pinned = false);
public sealed record ArchivePreviewResult(IReadOnlyList<ArchivePreview> Files, long? TargetFreeBytes)
{
    public int Candidates => Files.Count(f => f.Eligible);
    public long CandidateBytes => Files.Where(f => f.Eligible).Sum(f => f.Entry.Size);
    public int Rejected => Files.Count - Candidates;
}
public sealed record ArchiveItem(Guid Id, Guid RunId, Guid ArchiveTaskId, string FileId, RemoteEntry Source,
    Guid TargetStorageId, string TargetPath, string TemporaryPath, ArchiveStatus Status = ArchiveStatus.Eligible,
    string? SourceHash = null, string? TargetHash = null, RemoteEntry? Staged = null, RemoteEntry? Target = null,
    string? Error = null, long CopiedBytes = 0, bool SourceDeleted = false, string? CompletedAt = null,
    bool CopyComplete = false);
public sealed record ArchiveRun(Guid Id, Guid ArchiveTaskId, ArchivePolicy Policy, string Trigger, string? DueKey, string Status, string StartedAt, string? CompletedAt, string? Error);
public sealed record FileStorageMapping(string FileId, Guid LogicalConnectionId, string LogicalPath, Guid StorageConnectionId,
    RemoteEntry RemoteEntry, string StorageTier, Guid OriginalStorageId, string OriginalRemotePath, Guid ArchiveTaskId, Guid ArchiveItemId, string Sha256, string LastMappedAt);
public sealed record ArchiveTotals(int Completed, long ArchivedBytes, long SourceReleasedBytes, int Failed, string? LastCompletedAt);

public static class ArchiveEligibility
{
    public static string? Reject(ArchivePolicy p, RemoteEntry entry, CacheFile file, DateTimeOffset now)
    {
        if (entry.IsDirectory) return "目录本身不迁移";
        if (!entry.CreationTimeReliable || !entry.ModifiedTimeReliable || entry.CreationTime <= 0 || entry.ModifiedTime <= 0) return "原始创建/修改时间不可靠";
        try
        {
            if (!(DateTime.FromFileTimeUtc(entry.CreationTime) < now.UtcDateTime.AddDays(-p.CreationDays) && DateTime.FromFileTimeUtc(entry.ModifiedTime) < now.UtcDateTime.AddDays(-p.ModificationDays)))
                return "创建及修改时间必须同时严格早于阈值（AND）";
        }
        catch (ArgumentOutOfRangeException) { return "时间元数据超出有效范围"; }
        if (!p.IncludeHidden && (entry.Attributes & 2) != 0) return "隐藏文件排除";
        if (!p.IncludeSystem && (entry.Attributes & 4) != 0) return "系统文件排除";
        var s = file.Snapshot;
        if (s.LogicalBytes != entry.Size) return "本地占位长度与源映射不同";
        if (!s.Owned || s.Error is not null || !s.InSync || s.ModifiedBytes != 0) return "占位身份或同步状态异常";
        if (file.BlockReason is not null) return file.BlockReason;
        if (file.State is CacheState.Conflict or CacheState.Uploading or CacheState.Downloading or CacheState.Dehydrating) return "存在冲突或活动传输";
        if (file.OpenCount > 0) return "文件存在打开活动";
        if ((s.Pinned || file.Excluded) && !p.IncludePinned) return "固定或保护文件排除";
        return null;
    }
}
