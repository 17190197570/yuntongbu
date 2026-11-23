using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace CloudDrive.Storage;

public sealed partial class IndexStore
{
    private static void MigrateArchive(SqliteConnection db)
    {
        using var stream = typeof(IndexStore).Assembly.GetManifestResourceStream("CloudDrive.Storage.Migrations.004_archive.sql") ?? throw new IOException("缺少归档迁移。");
        using var reader = new StreamReader(stream); using var cmd = Cmd(db, reader.ReadToEnd()); cmd.ExecuteNonQuery();
    }
    public IReadOnlyList<ArchivePolicy> ArchivePolicies()
    {
        using var db = Open(); using var cmd = Cmd(db, "SELECT Settings FROM ArchivePolicies ORDER BY TaskName,Id"); using var r = cmd.ExecuteReader();
        var result = new List<ArchivePolicy>(); while (r.Read()) result.Add(JsonSerializer.Deserialize<ArchivePolicy>(r.GetString(0))!); return result;
    }
    public ArchivePolicy GetArchivePolicy(Guid id) => ArchivePolicies().SingleOrDefault(p => p.Id == id) ?? throw new ArgumentException("归档任务不存在。");
    public DateTimeOffset ArchivePolicyCreated(Guid id)
    { using var db = Open(); using var cmd = Cmd(db, "SELECT CreatedAt FROM ArchivePolicies WHERE Id=$id", ("$id", id.ToString())); return DateTimeOffset.Parse((string?)cmd.ExecuteScalar() ?? throw new ArgumentException("配置不存在。"), System.Globalization.CultureInfo.InvariantCulture); }
    public void SaveArchivePolicy(ArchivePolicy policy)
    {
        policy.Validate();
        var source = Get(policy.SourceStorageId) ?? throw new ArgumentException("源连接不存在。");
        var target = Get(policy.TargetStorageId) ?? throw new ArgumentException("目标连接不存在。");
        // Servers may have aliases: the execution layer also checks real root/file IDs before writing.
        if (source.Server.Equals(target.Server, StringComparison.OrdinalIgnoreCase) && source.Share.Equals(target.Share, StringComparison.OrdinalIgnoreCase))
        {
            string a = ArchivePaths.Combine(source.SubPath, policy.SourceRootPath.Length == 0 ? "__scope__" : policy.SourceRootPath);
            string b = ArchivePaths.Combine(target.SubPath, policy.TargetRootPath.Length == 0 ? "__scope__" : policy.TargetRootPath);
            if (source.SubPath == target.SubPath && (policy.SourceRootPath.Length == 0 || policy.TargetRootPath.Length == 0 || CacheManager.InScope(a, b, true) || CacheManager.InScope(b, a, true)))
                throw new ArgumentException("同一共享内重叠的源/目标归档范围不允许。");
        }
        using var db = Open(); using var cmd = Cmd(db, """
            INSERT INTO ArchivePolicies VALUES($id,$name,$s,$t,$p,$enabled,$now,$now)
            ON CONFLICT(Id) DO UPDATE SET TaskName=$name,SourceStorageId=$s,TargetStorageId=$t,Settings=$p,Enabled=$enabled,UpdatedAt=$now
            """, ("$id", policy.Id.ToString()), ("$name", policy.TaskName), ("$s", policy.SourceStorageId.ToString()), ("$t", policy.TargetStorageId.ToString()), ("$p", JsonSerializer.Serialize(policy)), ("$enabled", policy.Enabled), ("$now", DateTimeOffset.UtcNow.ToString("O"))); cmd.ExecuteNonQuery();
    }
    public void DeleteArchivePolicy(Guid id)
    {
        if (ArchiveRuns().Any(r => r.ArchiveTaskId == id && r.Status is not ("Completed" or "Cancelled" or "Failed"))) throw new IOException("先处理未完成归档任务；保留日志不能直接删除运行中的配置。");
        using var db = Open(); using var cmd = Cmd(db, "DELETE FROM ArchivePolicies WHERE Id=$id", ("$id", id.ToString())); cmd.ExecuteNonQuery();
    }
    public bool ArchivePaused
    {
        get { using var db = Open(); using var cmd = Cmd(db, "SELECT Paused FROM ArchiveSettings WHERE Id=1"); return (long)cmd.ExecuteScalar()! != 0; }
        set { using var db = Open(); using var cmd = Cmd(db, "UPDATE ArchiveSettings SET Paused=$v WHERE Id=1", ("$v", value)); cmd.ExecuteNonQuery(); }
    }
    public FileStorageMapping? StorageMapping(string fileId)
    {
        using var db = Open(); using var cmd = Cmd(db, "SELECT * FROM FileStorageMappings WHERE FileId=$f", ("$f", fileId)); using var r = cmd.ExecuteReader();
        return r.Read() ? new(r.GetString(0), Guid.Parse(r.GetString(1)), r.GetString(2), Guid.Parse(r.GetString(3)), JsonSerializer.Deserialize<RemoteEntry>(r.GetString(4))!, r.GetString(5), Guid.Parse(r.GetString(6)), r.GetString(7), Guid.Parse(r.GetString(8)), Guid.Parse(r.GetString(9)), r.GetString(10), r.GetString(11)) : null;
    }
    // Both hydration and future existing-file upload MUST route through this method.
    public (SmbConnection Connection, RemoteEntry Entry) ResolveStorage(RemoteEntry logical)
    {
        var mapping = StorageMapping(logical.Id);
        if (mapping is null) return (Get(logical.ConnectionId) ?? throw new IOException("存储连接缺失。"), logical);
        if (mapping.LogicalConnectionId != logical.ConnectionId || mapping.LogicalPath != logical.RelativePath || mapping.RemoteEntry.Size != logical.Size ||
            mapping.RemoteEntry.CreationTime != logical.CreationTime || mapping.RemoteEntry.ModifiedTime != logical.ModifiedTime || mapping.RemoteEntry.ConnectionId != mapping.StorageConnectionId || mapping.RemoteEntry.Id != Paths.StableId(mapping.StorageConnectionId, mapping.RemoteEntry.RelativePath))
            throw new IOException("归档映射与原占位版本不一致；保留文件并拒绝读取。");
        return (Get(mapping.StorageConnectionId) ?? throw new IOException("归档目标连接缺失，拒绝回退原NAS。"), mapping.RemoteEntry);
    }
    public IReadOnlyList<ArchiveRun> ArchiveRuns()
    {
        using var db = Open(); using var cmd = Cmd(db, "SELECT * FROM ArchiveRuns ORDER BY StartedAt DESC"); using var r = cmd.ExecuteReader(); var result = new List<ArchiveRun>();
        while (r.Read()) result.Add(new(Guid.Parse(r.GetString(0)), Guid.Parse(r.GetString(1)), JsonSerializer.Deserialize<ArchivePolicy>(r.GetString(2))!, r.GetString(3), r.IsDBNull(4) ? null : r.GetString(4), r.GetString(5), r.GetString(6), r.IsDBNull(7) ? null : r.GetString(7), r.IsDBNull(8) ? null : r.GetString(8))); return result;
    }
    public IEnumerable<ArchiveItem> ArchiveItems(Guid? run = null)
    {
        string after = "";
        while (true)
        {
            var batch = new List<ArchiveItem>(); using (var db = Open())
            { using var cmd = Cmd(db, "SELECT Id,Item FROM ArchiveItems WHERE ($r IS NULL OR RunId=$r) AND Id>$a ORDER BY Id LIMIT 256", ("$r", run?.ToString()), ("$a", after)); using var r = cmd.ExecuteReader(); while (r.Read()) { after = r.GetString(0); batch.Add(JsonSerializer.Deserialize<ArchiveItem>(r.GetString(1))!); } }
            foreach (var item in batch) yield return item; if (batch.Count < 256) yield break;
        }
    }
    public bool HasActiveArchive(string fileId)
    {
        using var db = Open(); using var cmd = Cmd(db, "SELECT 1 FROM ArchiveItems WHERE FileId=$f AND ArchiveStatus NOT IN ('Completed','Cancelled','Skipped') LIMIT 1", ("$f", fileId)); return cmd.ExecuteScalar() is not null;
    }
    public bool ArchiveUserProtected(Guid connection, string path)
    {
        using var db = Open(); using var cmd = Cmd(db, "SELECT 1 FROM CacheExclusions WHERE ConnectionId=$c AND ExclusionType='User' AND (RelativePath='' OR RelativePath=$p COLLATE NOCASE OR substr(upper($p),1,length(RelativePath)+1)=upper(RelativePath)||char(92)) LIMIT 1", ("$c", connection.ToString()), ("$p", path)); return cmd.ExecuteScalar() is not null;
    }
    public Guid QueueArchive(ArchivePolicy policy, IEnumerable<ArchivePreview> preview, string trigger = "Manual", string? dueKey = null, Guid? appendRun = null)
    {
        policy.Validate(); Guid id = appendRun ?? Guid.NewGuid(); string now = DateTimeOffset.UtcNow.ToString("O");
        using var db = Open(); using var tx = db.BeginTransaction();
        using (var existing = Cmd(db, "SELECT Id FROM ArchiveRuns WHERE ArchiveTaskId=$p AND (Status NOT IN ('Completed','Cancelled','Failed') OR ($due IS NOT NULL AND DueKey=$due)) LIMIT 1", ("$p", policy.Id.ToString()), ("$due", dueKey)))
            if (existing.ExecuteScalar() is string old && appendRun is null) return Guid.Parse(old);
        if (appendRun is null)
        { using var create = Cmd(db, "INSERT INTO ArchiveRuns VALUES($id,$p,$json,$trigger,$due,'Scanning',$now,NULL,NULL)", ("$id", id.ToString()), ("$p", policy.Id.ToString()), ("$json", JsonSerializer.Serialize(policy)), ("$trigger", trigger), ("$due", dueKey), ("$now", now)); create.ExecuteNonQuery(); }
        else
        { using var valid = Cmd(db, "SELECT 1 FROM ArchiveRuns WHERE Id=$id AND ArchiveTaskId=$p AND Status='Scanning'", ("$id", id.ToString()), ("$p", policy.Id.ToString())); if (valid.ExecuteScalar() is null) throw new IOException("只能向扫描中的本次任务追加候选。"); }
        long selected = 0;
        foreach (var candidate in preview.Where(f => f.Eligible))
        {
            if (policy.MaxBytes is long max && candidate.Entry.Size > max - selected) continue;
            Guid itemId = Guid.NewGuid(); string target = candidate.TargetPath;
            if (policy.Collision == ArchiveCollision.ConflictName)
            { string parent = Paths.Parent(target), name = target.Split('\\')[^1]; string extension = Path.GetExtension(name); target = ArchivePaths.Combine(parent, name[..(name.Length - extension.Length)] + ".archive-" + itemId.ToString("N") + extension); }
            string temporary = ArchivePaths.Combine(Paths.Parent(target), ".clouddrive-archive-" + itemId.ToString("N") + ".tmp");
            var item = new ArchiveItem(itemId, id, policy.Id, candidate.Entry.Id, candidate.Entry, policy.TargetStorageId, target, temporary);
            using var insert = Cmd(db, "INSERT OR IGNORE INTO ArchiveItems VALUES($id,$r,$p,$f,'Eligible',$json,NULL,$now,$now)", ("$id", itemId.ToString()), ("$r", id.ToString()), ("$p", policy.Id.ToString()), ("$f", item.FileId), ("$json", JsonSerializer.Serialize(item)), ("$now", now));
            if (insert.ExecuteNonQuery() > 0) selected += item.Source.Size;
        }
        if (appendRun is not null) { using var ready = Cmd(db, "UPDATE ArchiveRuns SET Status='Pending' WHERE Id=$id", ("$id", id.ToString())); ready.ExecuteNonQuery(); }
        tx.Commit(); return id;
    }
    public void SaveArchiveItem(ArchiveItem item, string step)
    {
        using var db = Open(); using var tx = db.BeginTransaction(); SaveArchiveItem(db, item, step); tx.Commit();
    }
    private static void SaveArchiveItem(SqliteConnection db, ArchiveItem item, string step)
    {
        string now = DateTimeOffset.UtcNow.ToString("O");
        using var cmd = Cmd(db, "UPDATE ArchiveItems SET ArchiveStatus=$s,Item=$json,ErrorMessage=$error,UpdatedAt=$now WHERE Id=$id", ("$s", item.Status.ToString()), ("$json", JsonSerializer.Serialize(item)), ("$error", item.Error), ("$now", now), ("$id", item.Id.ToString()));
        if (cmd.ExecuteNonQuery() != 1) throw new IOException("归档任务记录缺失。");
        using var journal = Cmd(db, "INSERT INTO ArchiveJournal(ArchiveItemId,OperationStep,SourceStorageId,SourcePath,TargetStorageId,TargetPath,TemporaryPath,OperationStatus,LastError,CreatedAt) VALUES($i,$step,$s,$sp,$t,$tp,$tmp,$status,$error,$now)",
            ("$i", item.Id.ToString()), ("$step", step), ("$s", item.Source.ConnectionId.ToString()), ("$sp", item.Source.RelativePath), ("$t", item.TargetStorageId.ToString()), ("$tp", item.TargetPath), ("$tmp", item.TemporaryPath), ("$status", item.Status.ToString()), ("$error", item.Error), ("$now", now)); journal.ExecuteNonQuery();
    }
    public void SwitchArchiveMapping(ArchiveItem item)
    {
        if (item.Target is null || item.SourceHash is null || item.SourceHash != item.TargetHash || !item.CopyComplete) throw new IOException("完整性证据不足，不能更新映射。");
        using var db = Open(); using var tx = db.BeginTransaction();
        using (var current = Cmd(db, "SELECT ArchiveItemId FROM FileStorageMappings WHERE FileId=$f", ("$f", item.FileId)))
            if (current.ExecuteScalar() is string old && old != item.Id.ToString()) throw new SyncConflictException("文件映射已被其他归档修改。");
        using var cmd = Cmd(db, """
            INSERT INTO FileStorageMappings VALUES($f,$l,$lp,$t,$remote,'Archive',$l,$lp,$p,$i,$h,$now)
            ON CONFLICT(FileId) DO NOTHING;
            UPDATE files SET missing=0 WHERE id=$f;
            """, ("$f", item.FileId), ("$l", item.Source.ConnectionId.ToString()), ("$lp", item.Source.RelativePath), ("$t", item.TargetStorageId.ToString()), ("$remote", JsonSerializer.Serialize(item.Target)), ("$p", item.ArchiveTaskId.ToString()), ("$i", item.Id.ToString()), ("$h", item.SourceHash), ("$now", DateTimeOffset.UtcNow.ToString("O"))); cmd.ExecuteNonQuery();
        SaveArchiveItem(db, item with { Status = ArchiveStatus.MappingUpdated }, "MappingCommitted"); tx.Commit();
    }
    public void SetArchiveRun(Guid run, string state, string? error = null)
    {
        using var db = Open(); using var cmd = Cmd(db, "UPDATE ArchiveRuns SET Status=$s,ErrorMessage=$e,CompletedAt=CASE WHEN $s IN ('Completed','Failed','Cancelled') THEN $now ELSE NULL END WHERE Id=$id", ("$s", state), ("$e", error), ("$now", DateTimeOffset.UtcNow.ToString("O")), ("$id", run.ToString())); cmd.ExecuteNonQuery();
    }
    public void ControlArchiveRun(Guid run, string action)
    {
        string state = action switch { "pause" => "Paused", "resume" or "retry" => "Pending", "cancel" => "Cancelled", _ => throw new ArgumentException("归档操作须为pause/resume/retry/cancel。") };
        if (state == "Cancelled")
        {
            // Only unstarted items can be cancelled. A staged or mapped file always keeps its recovery journal.
            foreach (var item in ArchiveItems(run).Where(i => i.Status is ArchiveStatus.Eligible or ArchiveStatus.Conflict or ArchiveStatus.RecoveryRequired && i.Staged is null && i.Target is null && StorageMapping(i.FileId) is null)) SaveArchiveItem(item with { Status = ArchiveStatus.Cancelled }, "CancelledBeforeStart");
            if (ArchiveItems(run).Any(i => i.Status is not (ArchiveStatus.Completed or ArchiveStatus.Skipped or ArchiveStatus.Cancelled))) state = "Paused";
        }
        SetArchiveRun(run, state);
    }
    public bool IsArchiveTemp(Guid connection, string path)
    {
        using var db = Open(); using var cmd = Cmd(db, "SELECT 1 FROM ArchiveItems WHERE json_extract(Item,'$.TargetStorageId')=$c AND json_extract(Item,'$.TemporaryPath')=$p COLLATE NOCASE LIMIT 1", ("$c", connection.ToString()), ("$p", path)); return cmd.ExecuteScalar() is not null;
    }
    public ArchiveTotals ArchiveStatistics()
    {
        var items = ArchiveItems().ToList(); return new(items.Count(i => i.Status == ArchiveStatus.Completed), items.Where(i => i.Status == ArchiveStatus.Completed).Sum(i => i.Source.Size), items.Where(i => i.SourceDeleted).Sum(i => i.Source.Size), items.Count(i => i.Status is ArchiveStatus.Failed or ArchiveStatus.Conflict or ArchiveStatus.RecoveryRequired), items.Where(i => i.CompletedAt is not null).Select(i => i.CompletedAt).Max());
    }
}
