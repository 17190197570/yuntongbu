namespace CloudDrive.Storage;

public sealed record ProductTotals(long Indexed, long CacheBytes, long Waiting, long Failed, long Uploading, int Archives);
public sealed partial class IndexStore
{
    public bool CheckIntegrity()
    { using var db = Open(); using var cmd = Cmd(db, "PRAGMA quick_check"); return string.Equals(cmd.ExecuteScalar() as string, "ok", StringComparison.Ordinal); }
    public ProductTotals ProductTotals()
    {
        using var db = Open();
        long Count(string sql) { using var cmd = Cmd(db, sql); return Convert.ToInt64(cmd.ExecuteScalar()); }
        return new(Count("SELECT COUNT(*) FROM files WHERE missing=0 AND json_extract(entry,'$.IsDirectory')=0"),
            Count("SELECT COALESCE(SUM(PhysicalAllocatedBytes),0) FROM CacheUsageStats"),
            Count("SELECT COUNT(*) FROM SyncTasks WHERE TaskStatus IN ('Pending','Paused')"),
            Count("SELECT COUNT(*) FROM SyncTasks WHERE TaskStatus IN ('Failed','Conflict')"),
            Count("SELECT COUNT(*) FROM SyncTasks WHERE TaskStatus='Processing'"),
            checked((int)Count("SELECT COUNT(*) FROM ArchiveRuns WHERE Status IN ('Scanning','Processing')")));
    }
    public long UnfinishedWork()
    {
        using var db = Open(); using var cmd = Cmd(db, """
            SELECT (SELECT COUNT(*) FROM SyncTasks WHERE TaskStatus NOT IN ('Completed','Cancelled'))
            +(SELECT COUNT(*) FROM ArchiveRuns WHERE Status NOT IN ('Completed','Cancelled'))
            +(SELECT COUNT(*) FROM CacheRuns WHERE Status IN ('Pending','Processing','Paused'))
            """); return (long)cmd.ExecuteScalar()!;
    }
    public bool HasOrphanedMappings()
    {
        using var db = Open(); using var cmd = Cmd(db, "SELECT EXISTS(SELECT 1 FROM files WHERE connection_id NOT IN (SELECT id FROM connections)) OR EXISTS(SELECT 1 FROM FileStorageMappings WHERE StorageConnectionId NOT IN (SELECT id FROM connections))");
        return (long)cmd.ExecuteScalar()! != 0;
    }
}
