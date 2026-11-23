using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace CloudDrive.Storage;

public sealed partial class IndexStore
{
    public static Action<Guid?, string, string>? ProductLogSink { get; set; }
    private readonly string connectionString;
    public string DatabasePath { get; }
    public static string DefaultDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CloudDriveLite");
    public IndexStore(string databasePath)
    {
        DatabasePath = Path.GetFullPath(databasePath);
        Directory.CreateDirectory(Path.GetDirectoryName(DatabasePath)!);
        connectionString = new SqliteConnectionStringBuilder { DataSource = DatabasePath, DefaultTimeout = 15 }.ToString();
        using var db = Open();
        using var versionCommand = db.CreateCommand();
        versionCommand.CommandText = "PRAGMA user_version";
        long version = (long)versionCommand.ExecuteScalar()!;
        if (version > CurrentSchema) throw new InvalidOperationException("数据库版本高于本程序支持的版本，拒绝降级打开。");
        if (version > 0 && version < CurrentSchema)
        {
            string backupRoot = Path.Combine(Path.GetDirectoryName(DatabasePath)!, "Backups"); Directory.CreateDirectory(backupRoot);
            using var backup = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path.Combine(backupRoot, "before-v" + version + "-" + Guid.NewGuid().ToString("N") + ".db"), Pooling = false }.ToString());
            backup.Open(); db.BackupDatabase(backup);
        }
        using var transaction = db.BeginTransaction();
        using var command = db.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS connections(id TEXT PRIMARY KEY, config TEXT NOT NULL, credential_ref TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS files(id TEXT PRIMARY KEY, connection_id TEXT NOT NULL, path_key TEXT NOT NULL,
              parent_key TEXT NOT NULL, entry TEXT NOT NULL, generation TEXT NOT NULL, missing INTEGER NOT NULL DEFAULT 0,
              UNIQUE(connection_id,path_key));
            CREATE INDEX IF NOT EXISTS files_parent ON files(connection_id,parent_key,path_key);
            CREATE TABLE IF NOT EXISTS scans(connection_id TEXT PRIMARY KEY, generation TEXT NOT NULL, state TEXT NOT NULL, completed TEXT);
            CREATE TABLE IF NOT EXISTS scan_queue(connection_id TEXT NOT NULL, generation TEXT NOT NULL, path TEXT NOT NULL,
              done INTEGER NOT NULL DEFAULT 0, PRIMARY KEY(connection_id,generation,path));
            CREATE TABLE IF NOT EXISTS leases(connection_id TEXT PRIMARY KEY, session TEXT NOT NULL, local_path TEXT NOT NULL, remote_path TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS logs(seq INTEGER PRIMARY KEY AUTOINCREMENT, time TEXT NOT NULL, connection_id TEXT, category TEXT NOT NULL, message TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS runtime(connection_id TEXT PRIMARY KEY, state TEXT NOT NULL, placeholders INTEGER NOT NULL DEFAULT 0, updated TEXT NOT NULL);
            """;
        command.ExecuteNonQuery();
        if (version < 2) MigrateSync(db);
        if (version < 3) MigrateCache(db);
        if (version < 4) MigrateArchive(db);
        if (version < 5) MigrateHealth(db);
        if (version < 6) MigrateStorageConnections(db);
        transaction.Commit();
        using var wal = Cmd(db, "PRAGMA journal_mode=WAL"); wal.ExecuteScalar();
    }
    private SqliteConnection Open() { var db = new SqliteConnection(connectionString); db.Open(); return db; }
    private static SqliteCommand Cmd(SqliteConnection db, string sql, params (string, object?)[] args)
    {
        var cmd = db.CreateCommand(); cmd.CommandText = sql;
        foreach (var (key, value) in args) cmd.Parameters.AddWithValue(key, value ?? DBNull.Value);
        return cmd;
    }
    public void ValidateSave(SmbConnection config)
    {
        config.Validate();
        var existing = Get(config.Id);
        using var inspection = Open(); using var count = Cmd(inspection, "SELECT (SELECT COUNT(*) FROM files WHERE connection_id=$id)+(SELECT COUNT(*) FROM SyncTasks WHERE ConnectionId=$id)+(SELECT COUNT(*) FROM CacheOperations WHERE ConnectionId=$id)+(SELECT COUNT(*) FROM CacheUsageStats WHERE ConnectionId=$id)+(SELECT COUNT(*) FROM ArchivePolicies WHERE SourceStorageId=$id OR TargetStorageId=$id)+(SELECT COUNT(*) FROM FileStorageMappings WHERE LogicalConnectionId=$id OR StorageConnectionId=$id)+(SELECT COUNT(*) FROM ArchiveJournal WHERE SourceStorageId=$id OR TargetStorageId=$id)", ("$id", config.Id.ToString()));
        long indexed = (long)count.ExecuteScalar()!;
        if (existing is null && indexed > 0) throw new ArgumentException("此 GUID 留有文件映射历史，请为新连接生成新 GUID。");
        if (existing is not null && indexed > 0 &&
            (existing.Server != config.Server || existing.Port != config.Port || existing.Share != config.Share ||
             existing.SubPath != config.SubPath || (existing.StorageId ?? existing.Id) != (config.StorageId ?? config.Id) || !existing.SyncRoot.Equals(config.SyncRoot, StringComparison.OrdinalIgnoreCase)))
            throw new ArgumentException("已有文件映射、同步或缓存记录的连接不能更换来源或同步根；请新增连接。");
        foreach (var other in Connections().Where(c => c.Id != config.Id))
        {
            var a = Path.TrimEndingDirectorySeparator(Path.GetFullPath(other.SyncRoot));
            var b = Path.TrimEndingDirectorySeparator(Path.GetFullPath(config.SyncRoot));
            if (a.Equals(b, StringComparison.OrdinalIgnoreCase) || a.StartsWith(b + "\\", StringComparison.OrdinalIgnoreCase) || b.StartsWith(a + "\\", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("不同连接的同步根不能重叠。");
        }
    }
    public void Save(SmbConnection config)
    {
        ValidateSave(config);
        if (config.StorageId is Guid storageId)
        {
            if (Storage(storageId) != StorageConnection.From(config)) throw new ArgumentException("映射的NAS参数已变化，请重新选择NAS并保存。");
            using var db = Open(); using var transaction = db.BeginTransaction(); WriteMapping(db, config); transaction.Commit();
        }
        else
        {
            // Legacy CLI keeps its serialized connection shape and stable credential/File IDs.
            var storage = StorageConnection.From(config); ValidateStorageSave(storage); var others = MappingsForStorage(storage.Id).Where(c => c.Id != config.Id).ToArray();
            using var db = Open(); using var transaction = db.BeginTransaction(); WriteStorage(db, storage); WriteMapping(db, config);
            foreach (var other in others) WriteMapping(db, other with { Server = config.Server, Port = config.Port, Share = config.Share, UserName = config.UserName, Domain = config.Domain });
            transaction.Commit();
        }
    }
    public IReadOnlyList<SmbConnection> Connections()
    {
        using var db = Open(); using var cmd = Cmd(db, "SELECT config FROM connections ORDER BY id"); using var rows = cmd.ExecuteReader();
        var result = new List<SmbConnection>(); while (rows.Read()) result.Add(JsonSerializer.Deserialize<SmbConnection>(rows.GetString(0))!.Validate()); return result;
    }
    public SmbConnection? Get(Guid id) => Connections().FirstOrDefault(c => c.Id == id);
    public void Delete(Guid id)
    {
        var config = Get(id);
        if (config is not null && Directory.Exists(config.SyncRoot)) throw new IOException("同步根仍存在；开发版尚未提供完整安全解除绑定向导，保留连接以避免丢失占位恢复入口。");
        using (var history = Open())
        using (var count = Cmd(history, "SELECT (SELECT COUNT(*) FROM files WHERE connection_id=$id)+(SELECT COUNT(*) FROM SyncTasks WHERE ConnectionId=$id)", ("$id", id.ToString())))
            if ((long)count.ExecuteScalar()! != 0) throw new IOException("文件或同步恢复记录仍引用此连接，不能删除凭据入口。");
        using var check = Open(); using var referenced = Cmd(check, "SELECT (SELECT COUNT(*) FROM ArchivePolicies WHERE SourceStorageId=$id OR TargetStorageId=$id)+(SELECT COUNT(*) FROM FileStorageMappings WHERE LogicalConnectionId=$id OR StorageConnectionId=$id)+(SELECT COUNT(*) FROM ArchiveJournal WHERE SourceStorageId=$id OR TargetStorageId=$id)", ("$id", id.ToString()));
        if ((long)referenced.ExecuteScalar()! > 0) throw new IOException("归档配置/映射/恢复历史仍引用此连接，不能删除凭据入口。");
        using var db = Open(); using var cmd = Cmd(db, "DELETE FROM connections WHERE id=$id; DELETE FROM FolderMappings WHERE MappingId=$id", ("$id", id.ToString())); cmd.ExecuteNonQuery();
        // Keep file mappings and logs as recovery evidence. Never unregister or delete the sync root here.
    }
    public long Count(Guid id) { using var db = Open(); using var cmd = Cmd(db, "SELECT COUNT(*) FROM files WHERE connection_id=$id AND missing=0", ("$id", id.ToString())); return (long)cmd.ExecuteScalar()!; }
    public RemoteEntry? Find(Guid connection, string id)
    {
        using var db = Open(); using var cmd = Cmd(db, "SELECT entry FROM files WHERE connection_id=$c AND id=$id AND missing=0", ("$c", connection.ToString()), ("$id", id));
        return cmd.ExecuteScalar() is string json ? JsonSerializer.Deserialize<RemoteEntry>(json) : null;
    }
    public IReadOnlyList<RemoteEntry> Page(Guid connection, string parent, string after = "", int limit = 256)
    {
        if (limit is < 1 or > 1024) throw new ArgumentOutOfRangeException(nameof(limit));
        using var db = Open(); using var cmd = Cmd(db, "SELECT entry FROM files WHERE connection_id=$c AND parent_key=$p AND path_key>$after AND missing=0 ORDER BY path_key LIMIT $limit",
            ("$c", connection.ToString()), ("$p", parent.ToUpperInvariant()), ("$after", after.ToUpperInvariant()), ("$limit", limit));
        using var rows = cmd.ExecuteReader(); var result = new List<RemoteEntry>();
        while (rows.Read()) result.Add(JsonSerializer.Deserialize<RemoteEntry>(rows.GetString(0))!); return result;
    }
    public string BeginScan(Guid id)
    {
        using var db = Open(); using var tx = db.BeginTransaction();
        using var query = Cmd(db, "SELECT generation FROM scans WHERE connection_id=$id AND state='running'", ("$id", id.ToString()));
        var generation = query.ExecuteScalar() as string ?? Guid.NewGuid().ToString("N");
        using var cmd = Cmd(db, "INSERT INTO scans(connection_id,generation,state) VALUES($id,$g,'running') ON CONFLICT(connection_id) DO UPDATE SET generation=$g,state='running'; INSERT OR IGNORE INTO scan_queue VALUES($id,$g,'',0)", ("$id", id.ToString()), ("$g", generation));
        cmd.ExecuteNonQuery(); tx.Commit(); return generation;
    }
    public string? NextDirectory(Guid id, string generation)
    {
        using var db = Open(); using var cmd = Cmd(db, "SELECT path FROM scan_queue WHERE connection_id=$id AND generation=$g AND done=0 ORDER BY length(path),path LIMIT 1", ("$id", id.ToString()), ("$g", generation)); return cmd.ExecuteScalar() as string;
    }
    public void RestartInterruptedScan(Guid id)
    {
        using var db = Open(); using var tx = db.BeginTransaction();
        using var cmd = Cmd(db, "UPDATE scans SET state='restart' WHERE connection_id=$id; DELETE FROM scan_queue WHERE connection_id=$id", ("$id", id.ToString()));
        cmd.ExecuteNonQuery(); tx.Commit(); // File index and completed-scan time are retained.
    }
    public void SaveBatch(Guid id, string generation, IReadOnlyList<RemoteEntry> entries)
    {
        using var db = Open(); using var tx = db.BeginTransaction();
        foreach (var entry in entries)
        {
            if (entry.ConnectionId != id || entry.Id != Paths.StableId(id, entry.RelativePath)) throw new ArgumentException("索引标识不匹配。");
            using var cmd = Cmd(db, "INSERT INTO files VALUES($id,$c,$path,$parent,$json,$g,0) ON CONFLICT(connection_id,path_key) DO UPDATE SET entry=$json,generation=$g,missing=0 WHERE NOT EXISTS(SELECT 1 FROM FileStorageMappings WHERE FileId=$id)",
                ("$id", entry.Id), ("$c", id.ToString()), ("$path", entry.RelativePath.ToUpperInvariant()), ("$parent", Paths.Parent(entry.RelativePath).ToUpperInvariant()), ("$json", JsonSerializer.Serialize(entry)), ("$g", generation)); cmd.ExecuteNonQuery();
            if (entry.IsDirectory) { using var queue = Cmd(db, "INSERT OR IGNORE INTO scan_queue VALUES($c,$g,$path,0)", ("$c", id.ToString()), ("$g", generation), ("$path", entry.RelativePath)); queue.ExecuteNonQuery(); }
        }
        tx.Commit();
    }
    public void DirectoryComplete(Guid id, string generation, string relative)
    {
        using var db = Open(); using var cmd = Cmd(db, "UPDATE scan_queue SET done=1 WHERE connection_id=$id AND generation=$g AND path=$p", ("$id", id.ToString()), ("$g", generation), ("$p", relative)); cmd.ExecuteNonQuery();
    }
    public void FinishScan(Guid id, string generation)
    {
        using var db = Open(); using var tx = db.BeginTransaction();
        using var pending = Cmd(db, "SELECT COUNT(*) FROM scan_queue WHERE connection_id=$id AND generation=$g AND done=0", ("$id", id.ToString()), ("$g", generation));
        if ((long)pending.ExecuteScalar()! != 0) throw new InvalidOperationException("不能完成未遍历完的扫描。");
        using var cmd = Cmd(db, "UPDATE files SET missing=1 WHERE connection_id=$id AND generation<>$g AND NOT EXISTS(SELECT 1 FROM FileStorageMappings m WHERE m.LogicalConnectionId=$id AND (m.FileId=files.id OR substr(upper(m.LogicalPath),1,length(files.path_key)+1)=files.path_key||char(92))); UPDATE scans SET state='complete',completed=$now WHERE connection_id=$id AND generation=$g; DELETE FROM scan_queue WHERE connection_id=$id AND generation=$g",
            ("$id", id.ToString()), ("$g", generation), ("$now", DateTimeOffset.UtcNow.ToString("O"))); cmd.ExecuteNonQuery(); tx.Commit();
    }
    public string ScanStatus(Guid id)
    {
        using var db = Open(); using var cmd = Cmd(db, "SELECT state || ' / ' || COALESCE(completed,'尚未完成') FROM scans WHERE connection_id=$id", ("$id", id.ToString())); return cmd.ExecuteScalar() as string ?? "尚未扫描";
    }
    public void Log(Guid? id, string category, string message)
    {
        message = ProductLog.Redact(message);
        ProductLogSink?.Invoke(id, category, message);
        using var db = Open(); using var cmd = Cmd(db, "INSERT INTO logs(time,connection_id,category,message) VALUES($t,$id,$c,$m); DELETE FROM logs WHERE seq < (SELECT COALESCE(MAX(seq),0)-5000 FROM logs)",
            ("$t", DateTimeOffset.UtcNow.ToString("O")), ("$id", id?.ToString()), ("$c", category), ("$m", message)); cmd.ExecuteNonQuery();
    }
    public IReadOnlyList<string> Logs()
    {
        using var db = Open(); using var cmd = Cmd(db, "SELECT time || ' [' || category || '] ' || COALESCE(connection_id,'') || ' ' || message FROM logs ORDER BY seq DESC LIMIT 300"); using var rows = cmd.ExecuteReader(); var result = new List<string>(); while (rows.Read()) result.Add(rows.GetString(0)); return result;
    }
    public (string Session, string Local, string Remote)? Lease(Guid id)
    {
        using var db = Open(); using var cmd = Cmd(db, "SELECT session,local_path,remote_path FROM leases WHERE connection_id=$id", ("$id", id.ToString())); using var r = cmd.ExecuteReader(); return r.Read() ? (r.GetString(0), r.GetString(1), r.GetString(2)) : null;
    }
    public void SetLease(Guid id, string session, string local, string remote)
    {
        using var db = Open(); using var cmd = Cmd(db, "INSERT OR REPLACE INTO leases VALUES($id,$s,$l,$r)", ("$id", id.ToString()), ("$s", session), ("$l", local), ("$r", remote)); cmd.ExecuteNonQuery();
    }
    public void ClearLease(Guid id)
    {
        using var db = Open(); using var cmd = Cmd(db, "DELETE FROM leases WHERE connection_id=$id", ("$id", id.ToString())); cmd.ExecuteNonQuery();
    }
    public void SetRuntime(Guid id, string state, long? placeholders)
    {
        using var db = Open(); using var cmd = Cmd(db, "INSERT INTO runtime VALUES($id,$s,COALESCE($p,0),$t) ON CONFLICT(connection_id) DO UPDATE SET state=$s,placeholders=COALESCE($p,placeholders),updated=$t",
            ("$id", id.ToString()), ("$s", state), ("$p", placeholders), ("$t", DateTimeOffset.UtcNow.ToString("O"))); cmd.ExecuteNonQuery();
    }
    public string RuntimeStatus(Guid id)
    {
        using var db = Open(); using var cmd = Cmd(db, "SELECT state || ' / 占位数 ' || placeholders || ' / 状态时间 ' || updated FROM runtime WHERE connection_id=$id", ("$id", id.ToString())); return cmd.ExecuteScalar() as string ?? "未运行";
    }
}
