using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace CloudDrive.Storage;

public sealed partial class IndexStore
{
    private static void MigrateSync(SqliteConnection db)
    {
        using var stream = typeof(IndexStore).Assembly.GetManifestResourceStream("CloudDrive.Storage.Migrations.002_sync.sql")
            ?? throw new InvalidOperationException("缺少同步数据库迁移资源。");
        using var reader = new StreamReader(stream);
        using var command = Cmd(db, reader.ReadToEnd()); command.ExecuteNonQuery();
    }
    public SyncSettings SyncSettings(Guid id)
    {
        using var db = Open(); using var command = Cmd(db, "SELECT Enabled,UploadConcurrency,DebounceMilliseconds,ReconcileSeconds FROM SyncSettings WHERE ConnectionId=$c", ("$c", id.ToString()));
        using var row = command.ExecuteReader();
        return row.Read() ? new SyncSettings(row.GetBoolean(0), row.GetInt32(1), row.GetInt32(2), row.GetInt32(3)).Validate() : new();
    }
    public void SetSyncSettings(Guid id, SyncSettings settings)
    {
        settings.Validate(); if (Get(id) is null) throw new ArgumentException("连接不存在。");
        using var db = Open(); using var command = Cmd(db, "INSERT INTO SyncSettings VALUES($c,$e,$u,$d,$r) ON CONFLICT(ConnectionId) DO UPDATE SET Enabled=$e,UploadConcurrency=$u,DebounceMilliseconds=$d,ReconcileSeconds=$r",
            ("$c", id.ToString()), ("$e", settings.Enabled), ("$u", settings.UploadConcurrency), ("$d", settings.DebounceMilliseconds), ("$r", settings.ReconcileSeconds)); command.ExecuteNonQuery();
    }
    public RemoteEntry? FindPath(Guid connection, string relative)
    {
        using var db = Open(); using var command = Cmd(db, "SELECT entry FROM files WHERE connection_id=$c AND path_key=$p", ("$c", connection.ToString()), ("$p", Paths.Relative(relative).ToUpperInvariant()));
        return command.ExecuteScalar() is string json ? JsonSerializer.Deserialize<RemoteEntry>(json) : null; // Includes tombstones: never reinterpret deletion as a new file.
    }
    public Guid EnqueueNewFile(Guid connection, string relative)
    {
        Paths.Relative(relative); string fileId = Paths.StableId(connection, relative);
        var id = Guid.NewGuid(); string now = DateTimeOffset.UtcNow.ToString("O");
        using var db = Open(); using var transaction = db.BeginTransaction();
        using var command = Cmd(db, """
            INSERT OR IGNORE INTO SyncTasks(Id,ConnectionId,TaskIdempotencyKey,FileId,TaskType,SourcePath,TargetPath,TaskStatus,NextRetryAt,CreatedAt,TempPath)
            VALUES($id,$c,$key,$f,'UPLOAD_FILE',$p,$p,'Pending',$now,$now,$temp);
            SELECT Id FROM SyncTasks WHERE TaskIdempotencyKey=$key;
            """, ("$id", id.ToString()), ("$c", connection.ToString()), ("$key", "new:" + connection.ToString("N") + ":" + fileId),
            ("$f", fileId), ("$p", relative), ("$now", now), ("$temp", (Paths.Parent(relative) is var parent && parent.Length > 0 ? parent + "\\" : "") + ".clouddrive-upload-" + id.ToString("N") + ".tmp"));
        var result = Guid.Parse((string)command.ExecuteScalar()!); transaction.Commit(); return result;
    }
    public void RecordChange(Guid connection, string type, string? oldPath, string? newPath)
    {
        string? fileId = (newPath ?? oldPath) is string relative ? Paths.StableId(connection, relative) : null;
        using var db = Open(); using var command = Cmd(db, "INSERT INTO FileChangeEvents(ConnectionId,FileId,EventType,OldPath,NewPath,EventTime,CreatedAt) VALUES($c,$f,$t,$o,$n,$now,$now)",
            ("$c", connection.ToString()), ("$f", fileId), ("$t", type), ("$o", oldPath), ("$n", newPath), ("$now", DateTimeOffset.UtcNow.ToString("O"))); command.ExecuteNonQuery();
    }
    public long EventWatermark(Guid connection)
    {
        using var db = Open(); using var command = Cmd(db, "SELECT COALESCE(MAX(Id),0) FROM FileChangeEvents WHERE ConnectionId=$c", ("$c", connection.ToString())); return (long)command.ExecuteScalar()!;
    }
    public void EventsReconciled(Guid connection, long watermark)
    {
        using var db = Open(); using var command = Cmd(db, "UPDATE FileChangeEvents SET Processed=1 WHERE ConnectionId=$c AND Id<=$w", ("$c", connection.ToString()), ("$w", watermark)); command.ExecuteNonQuery();
    }
    private const string TaskColumns = "Id,ConnectionId,FileId,TaskType,SourcePath,TargetPath,TaskStatus,RetryCount,Phase,LocalHash,FileSize,LocalModifiedTime,LocalFileId,TempPath,RemoteEntry,TransferredBytes,ErrorMessage";
    private static SyncTask ReadTask(SqliteDataReader r) => new(Guid.Parse(r.GetString(0)), Guid.Parse(r.GetString(1)), r.GetString(2), Enum.Parse<SyncTaskType>(r.GetString(3)), r.GetString(4), r.GetString(5), Enum.Parse<SyncTaskStatus>(r.GetString(6)), r.GetInt32(7), r.GetString(8),
        r.IsDBNull(9) ? null : r.GetString(9), r.IsDBNull(10) ? null : r.GetInt64(10), r.IsDBNull(11) ? null : r.GetInt64(11), r.IsDBNull(12) ? null : r.GetString(12), r.IsDBNull(13) ? null : r.GetString(13), r.IsDBNull(14) ? null : JsonSerializer.Deserialize<RemoteEntry>(r.GetString(14)), r.GetInt64(15), r.IsDBNull(16) ? null : r.GetString(16));
    public IReadOnlyList<SyncTask> SyncTasks(Guid connection, int limit = 300)
    {
        if (limit is < 1 or > 1000) throw new ArgumentOutOfRangeException(nameof(limit));
        using var db = Open(); using var command = Cmd(db, "SELECT " + TaskColumns + " FROM SyncTasks WHERE ConnectionId=$c ORDER BY CreatedAt DESC LIMIT $l", ("$c", connection.ToString()), ("$l", limit));
        using var rows = command.ExecuteReader(); var tasks = new List<SyncTask>(); while (rows.Read()) tasks.Add(ReadTask(rows)); return tasks;
    }
    public SyncTask? ClaimNewUpload(Guid connection)
    {
        using var db = Open(); using var transaction = db.BeginTransaction();
        using var command = Cmd(db, """
            UPDATE SyncTasks SET TaskStatus='Processing',StartedAt=$now,ErrorMessage=NULL
            WHERE Id=(SELECT t.Id FROM SyncTasks t WHERE t.ConnectionId=$c AND t.TaskType='UPLOAD_FILE'
            AND t.TaskStatus IN ('Pending','Failed') AND t.NextRetryAt<=$now
            AND NOT EXISTS(SELECT 1 FROM SyncTasks older WHERE older.ConnectionId=t.ConnectionId AND older.FileId=t.FileId
              AND (older.TaskStatus='Processing' OR (older.CreatedAt<t.CreatedAt AND older.TaskStatus NOT IN ('Completed','Cancelled'))))
            ORDER BY t.CreatedAt LIMIT 1) RETURNING
            """ + " " + TaskColumns, ("$c", connection.ToString()), ("$now", DateTimeOffset.UtcNow.ToString("O")));
        SyncTask? result; using (var row = command.ExecuteReader()) result = row.Read() ? ReadTask(row) : null;
        transaction.Commit(); return result;
    }
    public void RecoverSyncTasks(Guid connection)
    {
        using var db = Open(); using var command = Cmd(db, "UPDATE SyncTasks SET TaskStatus='Pending',NextRetryAt=$now,ErrorMessage='Agent 重启；先核对提交日志与远端版本。' WHERE ConnectionId=$c AND TaskStatus='Processing'", ("$c", connection.ToString()), ("$now", DateTimeOffset.UtcNow.ToString("O"))); command.ExecuteNonQuery();
    }
    public void SaveUploadPhase(SyncTask task, string phase, string hash, RemoteEntry local, RemoteEntry? remote = null)
    {
        using var db = Open(); using var command = Cmd(db, "UPDATE SyncTasks SET Phase=$phase,LocalHash=$h,FileSize=$size,LocalModifiedTime=$m,LocalFileId=$fid,RemoteEntry=$r WHERE Id=$id AND TaskStatus='Processing'",
            ("$phase", phase), ("$h", hash), ("$size", local.Size), ("$m", local.ModifiedTime), ("$fid", local.RemoteId), ("$r", remote is null ? null : JsonSerializer.Serialize(remote)), ("$id", task.Id.ToString()));
        if (command.ExecuteNonQuery() != 1) throw new InvalidOperationException("任务状态变化；拒绝提交。");
    }
    public void UploadProgress(Guid task, long bytes)
    {
        using var db = Open(); using var command = Cmd(db, "UPDATE SyncTasks SET TransferredBytes=$b WHERE Id=$id AND TaskStatus='Processing'", ("$id", task.ToString()), ("$b", bytes)); command.ExecuteNonQuery();
    }
    public void FinishNewUpload(SyncTask task, RemoteEntry remote, string hash)
    {
        using var db = Open(); using var transaction = db.BeginTransaction(); string now = DateTimeOffset.UtcNow.ToString("O");
        using var command = Cmd(db, """
            INSERT INTO files VALUES($f,$c,$p,$parent,$entry,COALESCE((SELECT generation FROM scans WHERE connection_id=$c),'upload'),0)
            ON CONFLICT(connection_id,path_key) DO UPDATE SET entry=$entry,missing=0,generation=excluded.generation;
            UPDATE FileVersions SET IsCurrent=0 WHERE FileId=$f;
            INSERT INTO FileVersions(FileId,LocalHash,RemoteHash,FileSize,RemoteModifiedTime,RemoteFileId,VersionNumber,IsCurrent,CreatedAt)
              VALUES($f,$h,$h,$s,$m,$rid,COALESCE((SELECT MAX(VersionNumber)+1 FROM FileVersions WHERE FileId=$f),1),1,$now);
            UPDATE SyncTasks SET TaskStatus='Completed',Phase='Complete',CompletedAt=$now,ErrorMessage=NULL,TransferredBytes=$s WHERE Id=$id AND TaskStatus='Processing';
            """, ("$f", task.FileId), ("$c", task.ConnectionId.ToString()), ("$p", remote.RelativePath.ToUpperInvariant()), ("$parent", Paths.Parent(remote.RelativePath).ToUpperInvariant()), ("$entry", JsonSerializer.Serialize(remote)),
            ("$h", hash), ("$s", remote.Size), ("$m", remote.ModifiedTime), ("$rid", remote.RemoteId), ("$now", now), ("$id", task.Id.ToString())); command.ExecuteNonQuery(); transaction.Commit();
        Log(task.ConnectionId, "upload-complete", task.Id + " " + remote.RelativePath + " SHA256=" + hash);
    }
    public void FailUpload(SyncTask task, string message, bool conflict)
    {
        using var db = Open(); using var transaction = db.BeginTransaction(); string now = DateTimeOffset.UtcNow.ToString("O");
        using var command = Cmd(db, "UPDATE SyncTasks SET TaskStatus=$s,RetryCount=RetryCount+1,NextRetryAt=$next,ErrorMessage=$e WHERE Id=$id AND TaskStatus='Processing'",
            ("$id", task.Id.ToString()), ("$s", conflict ? "Conflict" : "Failed"), ("$next", DateTimeOffset.UtcNow.AddSeconds(Math.Min(300, Math.Pow(2, Math.Min(task.RetryCount + 1, 9)))).ToString("O")), ("$e", message)); command.ExecuteNonQuery();
        if (conflict)
        {
            using var record = Cmd(db, "INSERT OR IGNORE INTO ConflictRecords(Id,FileId,LocalVersionHash,LocalConflictPath,RemotePath,ConflictType,ResolutionStatus,CreatedAt) VALUES($id,$f,(SELECT LocalHash FROM SyncTasks WHERE Id=$id),$p,$r,'NewFileCollision','Unresolved',$now)",
                ("$id", task.Id.ToString()), ("$f", task.FileId), ("$p", task.SourcePath), ("$r", task.TargetPath), ("$now", now)); record.ExecuteNonQuery();
        }
        transaction.Commit(); Log(task.ConnectionId, conflict ? "upload-conflict" : "upload-retry", task.Id + " " + message);
    }
    public void ControlSyncTask(Guid connection, Guid task, SyncTaskStatus state)
    {
        string eligible = state switch { SyncTaskStatus.Pending => "'Failed','Paused'", SyncTaskStatus.Paused => "'Pending','Failed'", SyncTaskStatus.Cancelled => "'Pending','Failed','Paused'", _ => throw new ArgumentException("不支持的队列操作。") };
        using var db = Open(); using var command = Cmd(db, "UPDATE SyncTasks SET TaskStatus=$s,NextRetryAt=$now,ErrorMessage=NULL WHERE ConnectionId=$c AND Id=$id AND TaskStatus IN (" + eligible + ") AND ($s<>'Cancelled' OR Phase NOT IN ('CommitIntent','Committed'))",
            ("$s", state.ToString()), ("$now", DateTimeOffset.UtcNow.ToString("O")), ("$c", connection.ToString()), ("$id", task.ToString()));
        if (command.ExecuteNonQuery() != 1) throw new ArgumentException("任务正在执行、已结束，或必须先核对可能已提交的远端文件。");
    }
    public bool IsUploadTemp(Guid connection, string path)
    {
        using var db = Open(); using var command = Cmd(db, "SELECT 1 FROM SyncTasks WHERE ConnectionId=$c AND TempPath=$p COLLATE NOCASE LIMIT 1", ("$c", connection.ToString()), ("$p", path)); return command.ExecuteScalar() is not null;
    }
}
