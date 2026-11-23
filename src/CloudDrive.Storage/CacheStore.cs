using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace CloudDrive.Storage;

public sealed partial class IndexStore
{
    private static void MigrateCache(SqliteConnection db)
    {
        using var stream = typeof(IndexStore).Assembly.GetManifestResourceStream("CloudDrive.Storage.Migrations.003_cache.sql") ?? throw new IOException("缺少缓存迁移。");
        using var reader = new StreamReader(stream); using var cmd = Cmd(db, reader.ReadToEnd()); cmd.ExecuteNonQuery();
    }
    public CachePolicy CachePolicy(Guid connection)
    {
        using var db = Open(); using var cmd = Cmd(db, "SELECT Settings FROM CachePolicies WHERE StorageConnectionId=$c", ("$c", connection.ToString()));
        return cmd.ExecuteScalar() is string json ? JsonSerializer.Deserialize<CachePolicy>(json)!.Validate() : new();
    }
    public void SetCachePolicy(Guid connection, CachePolicy settings)
    {
        settings.Validate(); if (Get(connection) is null) throw new ArgumentException("连接不存在。");
        using var db = Open(); using var cmd = Cmd(db, """
            INSERT INTO CachePolicies VALUES($c,'Default',$c,$s,$enabled,$max,$trigger,$target,$minDisk,$triggerDisk,$targetDisk,$days,$protect,$concurrency,$now,$now)
            ON CONFLICT(StorageConnectionId) DO UPDATE SET Settings=$s,Enabled=$enabled,MaxCacheBytes=$max,TriggerCacheBytes=$trigger,TargetCacheBytes=$target,
             MinDiskFreeBytes=$minDisk,TriggerDiskFreeBytes=$triggerDisk,TargetDiskFreeBytes=$targetDisk,InactivityDays=$days,ProtectionDays=$protect,CleanupConcurrency=$concurrency,UpdatedAt=$now
            """, ("$c", connection.ToString()), ("$s", JsonSerializer.Serialize(settings)), ("$enabled", settings.Enabled), ("$max", settings.MaxCacheBytes), ("$trigger", settings.TriggerCacheBytes), ("$target", settings.TargetCacheBytes),
            ("$minDisk", settings.MinDiskFreeBytes), ("$triggerDisk", settings.TriggerDiskFreeBytes), ("$targetDisk", settings.TargetDiskFreeBytes), ("$days", settings.InactivityDays), ("$protect", settings.ProtectionDays), ("$concurrency", settings.CleanupConcurrency), ("$now", DateTimeOffset.UtcNow.ToString("O"))); cmd.ExecuteNonQuery();
    }
    public void SaveCacheBatch(IEnumerable<CacheSnapshot> snapshots, string? scan = null)
    {
        using var db = Open(); using var transaction = db.BeginTransaction(); string now = DateTimeOffset.UtcNow.ToString("O");
        foreach (var s in snapshots)
        {
            if (s.FileId != Paths.StableId(s.ConnectionId, s.RelativePath)) throw new ArgumentException("缓存身份不匹配。");
            using var cmd = Cmd(db, """
                INSERT INTO CacheUsageStats(FileId,ConnectionId,RelativePath,PhysicalAllocatedBytes,LogicalFileSize,HydratedBytes,Snapshot,LastScan,UpdatedAt)
                VALUES($f,$c,$p,$a,$l,$h,$s,$scan,$now)
                ON CONFLICT(FileId) DO UPDATE SET
                 LastHydratedAt=CASE WHEN CacheUsageStats.HydratedBytes<excluded.HydratedBytes AND excluded.HydratedBytes>=excluded.LogicalFileSize THEN $now ELSE CacheUsageStats.LastHydratedAt END,
                 LastDehydratedAt=CASE WHEN CacheUsageStats.HydratedBytes>0 AND excluded.HydratedBytes=0 THEN $now ELSE CacheUsageStats.LastDehydratedAt END,
                 DownloadCount=CASE WHEN $complete=1 THEN 0 ELSE CacheUsageStats.DownloadCount END,
                 PhysicalAllocatedBytes=$a,LogicalFileSize=$l,HydratedBytes=$h,Snapshot=$s,LastScan=COALESCE($scan,CacheUsageStats.LastScan),UpdatedAt=$now;
                """, ("$f", s.FileId), ("$c", s.ConnectionId.ToString()), ("$p", s.RelativePath), ("$a", s.AllocatedBytes), ("$l", s.LogicalBytes), ("$h", s.HydratedBytes), ("$s", JsonSerializer.Serialize(s)), ("$scan", scan), ("$complete", s.Owned && s.InSync && !s.IsDirectory && s.HydratedBytes >= s.LogicalBytes), ("$now", now)); cmd.ExecuteNonQuery();
        }
        transaction.Commit();
    }
    public void FinishCacheScan(Guid connection, string scan)
    {
        using var db = Open(); using var cmd = Cmd(db, "UPDATE CacheUsageStats SET Snapshot=NULL,PhysicalAllocatedBytes=0,HydratedBytes=0 WHERE ConnectionId=$c AND COALESCE(LastScan,'')<>$scan", ("$c", connection.ToString()), ("$scan", scan)); cmd.ExecuteNonQuery();
    }
    public void RecordCacheAccess(IEnumerable<CacheAccess> events)
    {
        using var db = Open(); using var transaction = db.BeginTransaction();
        foreach (var e in events)
        {
            if (e.FileId != Paths.StableId(e.ConnectionId, e.Path)) throw new ArgumentException("访问记录身份不匹配。");
            using var cmd = Cmd(db, """
                INSERT INTO CacheUsageStats(FileId,ConnectionId,RelativePath,UpdatedAt) VALUES($f,$c,$p,$now) ON CONFLICT(FileId) DO NOTHING;
                UPDATE CacheUsageStats SET
                 LastUserAccessAt=CASE WHEN $user=1 AND (LastUserAccessAt IS NULL OR LastUserAccessAt<$now) THEN $now ELSE LastUserAccessAt END,
                 LastOpenAt=CASE WHEN $kind='Opened' AND (LastOpenAt IS NULL OR LastOpenAt<$now) THEN $now ELSE LastOpenAt END,
                 LastCloseAt=CASE WHEN $kind='Closed' AND (LastCloseAt IS NULL OR LastCloseAt<$now) THEN $now ELSE LastCloseAt END,
                 OpenCount=CASE WHEN $kind='Opened' THEN OpenCount+1 WHEN $kind='Closed' THEN MAX(0,OpenCount-1) ELSE OpenCount END,
                 DownloadCount=CASE WHEN $kind='Downloading' THEN DownloadCount+1 WHEN $kind IN ('Hydrated','DownloadFailed') THEN MAX(0,DownloadCount-1) ELSE DownloadCount END,
                 AccessSource=$source,ProcessId=$pid,ProcessName=$name,UpdatedAt=$now WHERE FileId=$f;
                """, ("$f", e.FileId), ("$c", e.ConnectionId.ToString()), ("$p", e.Path), ("$now", e.At.ToUniversalTime().ToString("O")), ("$user", e.UserAccess), ("$kind", e.Kind), ("$source", e.Source), ("$pid", e.ProcessId), ("$name", e.ProcessName)); cmd.ExecuteNonQuery();
        }
        transaction.Commit();
    }
    public void ResetCacheOpens(Guid connection)
    {
        using var db = Open(); using var cmd = Cmd(db, "UPDATE CacheUsageStats SET OpenCount=0,DownloadCount=0 WHERE ConnectionId=$c", ("$c", connection.ToString())); cmd.ExecuteNonQuery();
        // Hints are advisory after restart; native exclusive oplock remains authoritative.
    }
    public string? CacheSyncBlock(Guid connection, string fileId)
    {
        using var db = Open(); using var cmd = Cmd(db, """
            SELECT CASE
             WHEN EXISTS(SELECT 1 FROM SyncTasks WHERE ConnectionId=$c AND FileId=$f AND TaskStatus NOT IN ('Completed','Cancelled')) THEN '存在待执行、失败、暂停、上传中或冲突同步任务'
             WHEN EXISTS(SELECT 1 FROM ConflictRecords WHERE FileId=$f AND ResolutionStatus<>'Resolved') THEN '存在未解决冲突'
             WHEN EXISTS(SELECT 1 FROM FileChangeEvents WHERE ConnectionId=$c AND FileId=$f AND Processed=0 AND EventType NOT IN ('Opened','Closed','Downloading','Hydrated','DownloadFailed')) THEN '存在未处理本地变化'
             WHEN NOT EXISTS(SELECT 1 FROM files WHERE connection_id=$c AND id=$f AND missing=0) THEN '数据库远端映射缺失或已标记不存在'
             ELSE NULL END
            """, ("$c", connection.ToString()), ("$f", fileId)); return cmd.ExecuteScalar() as string;
    }
    public CacheFile DecorateCache(CacheSnapshot s)
    {
        using var db = Open(); using var cmd = Cmd(db, """
            SELECT LastUserAccessAt,LastOpenAt,LastCloseAt,LastHydratedAt,OpenCount,
             (EXISTS(SELECT 1 FROM CacheExclusions WHERE ConnectionId=$c AND ExclusionType='User' AND
               (RelativePath='' OR RelativePath=$p COLLATE NOCASE OR substr(upper($p),1,length(RelativePath)+1)=upper(RelativePath)||'\')) OR
              COALESCE((SELECT ExclusionType FROM CacheExclusions WHERE ConnectionId=$c AND ExclusionType IN ('Pinned','Unpinned') AND
               (RelativePath='' OR RelativePath=$p COLLATE NOCASE OR substr(upper($p),1,length(RelativePath)+1)=upper(RelativePath)||'\') ORDER BY length(RelativePath) DESC,ExclusionType DESC LIMIT 1),'')='Pinned'),
             (SELECT OperationStatus FROM CacheOperations WHERE FileId=$f AND OperationType='Release' ORDER BY rowid DESC LIMIT 1),
             EXISTS(SELECT 1 FROM ConflictRecords WHERE FileId=$f AND ResolutionStatus<>'Resolved'),DownloadCount
            FROM CacheUsageStats WHERE FileId=$f
            """, ("$f", s.FileId), ("$c", s.ConnectionId.ToString()), ("$p", s.RelativePath.ToUpperInvariant())); using var r = cmd.ExecuteReader();
        DateTimeOffset? user = null, opened = null, closed = null, hydrated = null; int count = 0; bool excluded = false, conflict = false; string? operation = null;
        int downloads = 0;
        if (r.Read()) { user = Date(r, 0); opened = Date(r, 1); closed = Date(r, 2); hydrated = Date(r, 3); count = r.GetInt32(4); excluded = r.GetBoolean(5); operation = r.IsDBNull(6) ? null : r.GetString(6); conflict = r.GetBoolean(7); downloads = r.GetInt32(8); }
        string? block = CacheSyncBlock(s.ConnectionId, s.FileId);
        if (downloads > 0) block ??= "存在活动水合请求";
        var state = conflict ? CacheState.Conflict : block is not null || !s.Owned || !s.InSync || s.ModifiedBytes != 0 ? CacheState.NotSynced :
            operation == "Processing" ? CacheState.Dehydrating : operation == "Failed" ? CacheState.DehydrationFailed :
            s.HydratedBytes < s.LogicalBytes && s.HydratedBytes > 0 ? CacheState.Downloading :
            s.Pinned && s.HydratedBytes >= s.LogicalBytes ? CacheState.AlwaysAvailable : s.HydratedBytes == 0 ? CacheState.OnlineOnly : CacheState.LocallyAvailable;
        if (block?.Contains("上传中", StringComparison.Ordinal) == true && HasProcessingUpload(s.FileId)) state = CacheState.Uploading;
        else if (downloads > 0) state = CacheState.Downloading;
        return new(s, state, user, opened, closed, hydrated, count, excluded, block);
    }
    private bool HasProcessingUpload(string fileId)
    {
        using var db = Open(); using var cmd = Cmd(db, "SELECT 1 FROM SyncTasks WHERE FileId=$f AND TaskStatus='Processing' LIMIT 1", ("$f", fileId)); return cmd.ExecuteScalar() is not null;
    }
    private static DateTimeOffset? Date(SqliteDataReader r, int i) => r.IsDBNull(i) ? null : DateTimeOffset.Parse(r.GetString(i), System.Globalization.CultureInfo.InvariantCulture);
    public IEnumerable<CacheSnapshot> CacheSnapshots(Guid? connection = null)
    {
        // Keyset pagination keeps SQLite readers short-lived and avoids loading all file JSON at once.
        string after = "";
        while (true)
        {
            var batch = new List<CacheSnapshot>();
            using (var db = Open())
            using (var cmd = Cmd(db, "SELECT FileId,Snapshot FROM CacheUsageStats WHERE ($c IS NULL OR ConnectionId=$c) AND Snapshot IS NOT NULL AND FileId>$after ORDER BY FileId LIMIT 512", ("$c", connection?.ToString()), ("$after", after)))
            using (var r = cmd.ExecuteReader()) while (r.Read()) { after = r.GetString(0); batch.Add(JsonSerializer.Deserialize<CacheSnapshot>(r.GetString(1))!); }
            foreach (var s in batch) yield return s;
            if (batch.Count < 512) yield break;
        }
    }
    public void SetCacheExclusion(Guid connection, string path, bool protect, string type = "User", string reason = "用户保护")
    {
        if (path.Length > 0) Paths.Relative(path);
        path = path.ToUpperInvariant();
        using var db = Open(); using var cmd = Cmd(db, protect ? "INSERT OR IGNORE INTO CacheExclusions VALUES($id,$c,$f,$p,$t,$r,$now)" : "DELETE FROM CacheExclusions WHERE ConnectionId=$c AND RelativePath=$p COLLATE NOCASE AND ExclusionType=$t",
            ("$id", Guid.NewGuid().ToString()), ("$c", connection.ToString()), ("$f", path.Length > 0 ? Paths.StableId(connection, path) : null), ("$p", path), ("$t", type), ("$r", reason), ("$now", DateTimeOffset.UtcNow.ToString("O"))); cmd.ExecuteNonQuery();
    }
    public Guid QueueCacheRun(Guid? connection, string trigger, IEnumerable<(CacheSnapshot File, string Type)> files, string? scope = null)
    {
        var id = Guid.NewGuid(); using var db = Open(); using var tx = db.BeginTransaction();
        var planned = new List<(CacheSnapshot File, string Type)>();
        var unique = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in files)
        {
            using var busy = Cmd(db, "SELECT 1 FROM CacheOperations o JOIN CacheRuns r ON r.Id=o.RunId WHERE o.FileId=$f AND o.OperationStatus IN ('Pending','Processing') AND r.Status IN ('Pending','Processing','Paused','WaitingRemote') LIMIT 1", ("$f", item.File.FileId));
            if (busy.ExecuteScalar() is null && unique.Add(item.File.FileId)) planned.Add(item);
        }
        if (planned.Count == 0) { tx.Commit(); return Guid.Empty; }
        using (var cmd = Cmd(db, "INSERT INTO CacheRuns(Id,ConnectionId,TriggerType,Status,StartedAt,ScopePath) VALUES($id,$c,$t,'Pending',$now,$scope)", ("$id", id.ToString()), ("$c", connection?.ToString()), ("$t", trigger), ("$scope", scope), ("$now", DateTimeOffset.UtcNow.ToString("O")))) cmd.ExecuteNonQuery();
        foreach (var (s, type) in planned)
        {
            if (type is not ("Release" or "Pin" or "Unpin")) throw new ArgumentException("无效缓存操作。");
            using var cmd = Cmd(db, "INSERT INTO CacheOperations(Id,RunId,ConnectionId,FileId,RelativePath,OperationType,TriggerType,BeforeAllocatedBytes) VALUES($id,$run,$c,$f,$p,$t,$trigger,$bytes)", ("$id", Guid.NewGuid().ToString()), ("$run", id.ToString()), ("$c", s.ConnectionId.ToString()), ("$f", s.FileId), ("$p", s.RelativePath), ("$t", type), ("$trigger", trigger), ("$bytes", s.AllocatedBytes)); cmd.ExecuteNonQuery();
        }
        tx.Commit(); return id;
    }
    public IReadOnlyList<CacheOperation> CacheOperations(Guid run)
    {
        using var db = Open(); using var cmd = Cmd(db, "SELECT Id,RunId,ConnectionId,FileId,RelativePath,OperationType,TriggerType,OperationStatus,BeforeAllocatedBytes,AfterAllocatedBytes,ReleasedBytes,ErrorMessage FROM CacheOperations WHERE RunId=$id ORDER BY rowid", ("$id", run.ToString())); using var r = cmd.ExecuteReader(); var result = new List<CacheOperation>();
        while (r.Read()) result.Add(new(Guid.Parse(r.GetString(0)), Guid.Parse(r.GetString(1)), Guid.Parse(r.GetString(2)), r.GetString(3), r.GetString(4), r.GetString(5), r.GetString(6), r.GetString(7), r.GetInt64(8), r.IsDBNull(9) ? null : r.GetInt64(9), r.GetInt64(10), r.IsDBNull(11) ? null : r.GetString(11))); return result;
    }
    public IReadOnlyList<CacheRun> CacheRuns(Guid? runId = null)
    {
        using var db = Open(); using var cmd = Cmd(db, """
            SELECT r.Id,r.ConnectionId,r.TriggerType,r.Status,r.StartedAt,r.CompletedAt,COUNT(o.Id),
             COALESCE(SUM(o.OperationStatus='Completed'),0),COALESCE(SUM(o.OperationStatus='Failed'),0),COALESCE(SUM(o.ReleasedBytes),0),r.ErrorMessage,r.ScopePath
            FROM CacheRuns r LEFT JOIN CacheOperations o ON r.Id=o.RunId WHERE ($id IS NULL OR r.Id=$id) GROUP BY r.Id ORDER BY r.rowid DESC LIMIT 300
            """, ("$id", runId?.ToString())); using var r = cmd.ExecuteReader(); var result = new List<CacheRun>(); while (r.Read()) result.Add(new(Guid.Parse(r.GetString(0)), r.IsDBNull(1) ? null : Guid.Parse(r.GetString(1)), r.GetString(2), r.GetString(3), r.GetString(4), r.IsDBNull(5) ? null : r.GetString(5), r.GetInt32(6), r.GetInt32(7), r.GetInt32(8), r.GetInt64(9), r.IsDBNull(10) ? null : r.GetString(10), r.IsDBNull(11) ? null : r.GetString(11))); return result;
    }
    public IReadOnlyList<Guid> PendingCacheRuns()
    {
        using var db = Open(); using var cmd = Cmd(db, "SELECT Id FROM CacheRuns WHERE Status IN ('Pending','WaitingRemote') ORDER BY rowid"); using var r = cmd.ExecuteReader(); var list = new List<Guid>(); while (r.Read()) list.Add(Guid.Parse(r.GetString(0))); return list;
    }
    public void ControlCacheRun(Guid run, string state)
    {
        if (state is not ("Paused" or "Pending" or "Cancelled")) throw new ArgumentException("清理任务状态无效。");
        using var db = Open(); using var tx = db.BeginTransaction(); using var cmd = Cmd(db, "UPDATE CacheRuns SET Status=$s,CompletedAt=CASE WHEN $s='Cancelled' THEN $now ELSE CompletedAt END WHERE Id=$id AND Status IN ('Pending','Processing','Paused','WaitingRemote')", ("$s", state), ("$id", run.ToString()), ("$now", DateTimeOffset.UtcNow.ToString("O"))); if (cmd.ExecuteNonQuery() != 1) throw new ArgumentException("任务已结束。");
        if (state == "Cancelled") { using var cancelled = Cmd(db, "UPDATE CacheOperations SET OperationStatus='Cancelled',CompletedAt=$now WHERE RunId=$id AND OperationStatus='Pending'", ("$id", run.ToString()), ("$now", DateTimeOffset.UtcNow.ToString("O"))); cancelled.ExecuteNonQuery(); } tx.Commit();
    }
    public void RecoverCacheRuns()
    {
        // Caller holds the global cache execution lock. Recheck actual state before retrying any operation.
        using var db = Open(); using var cmd = Cmd(db, "UPDATE CacheOperations SET OperationStatus='Pending',ErrorMessage='进程中断，重新核对实际状态' WHERE OperationStatus='Processing'; UPDATE CacheRuns SET Status='Pending' WHERE Status='Processing'"); cmd.ExecuteNonQuery();
    }
    public void CacheRunStatus(Guid run, string status, string? error = null)
    {
        using var db = Open(); using var cmd = Cmd(db, "UPDATE CacheRuns SET Status=$s,ErrorMessage=$e,CompletedAt=CASE WHEN $s IN ('Completed','Failed','Cancelled') THEN $now ELSE CompletedAt END WHERE Id=$id AND Status NOT IN ('Paused','Cancelled')", ("$s", status), ("$e", error), ("$id", run.ToString()), ("$now", DateTimeOffset.UtcNow.ToString("O"))); cmd.ExecuteNonQuery();
    }
    public void CacheOperationStatus(CacheOperation operation, string status, long before, CacheSnapshot? after = null, string? error = null, string? errorCode = null)
    {
        using var db = Open(); using var cmd = Cmd(db, "UPDATE CacheOperations SET OperationStatus=$s,BeforeAllocatedBytes=$before,AfterAllocatedBytes=$after,ReleasedBytes=$released,ErrorCode=$code,ErrorMessage=$e,StartedAt=COALESCE(StartedAt,$now),CompletedAt=CASE WHEN $s='Processing' THEN NULL ELSE $now END WHERE Id=$id",
            ("$s", status), ("$before", before), ("$after", after?.AllocatedBytes), ("$released", status == "Completed" && operation.Type == "Release" ? Math.Max(0, before - (after?.AllocatedBytes ?? before)) : 0), ("$code", errorCode), ("$e", error), ("$now", DateTimeOffset.UtcNow.ToString("O")), ("$id", operation.Id.ToString())); cmd.ExecuteNonQuery();
    }
}
