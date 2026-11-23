using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace CloudDrive.Storage;

public sealed record BackupManifest(string Version, DateTimeOffset CreatedAt, int Schema, string UserKey, string DeviceKey, string DatabaseSha256, string SettingsSha256);
public static class ProductBackup
{
    public static string DeviceKey => Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(Environment.MachineName)));
    public static string Create(IndexStore index, string directory, ProductSettings? settings = null)
    {
        Directory.CreateDirectory(directory); string id = DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss-fffffff") + "-" + Guid.NewGuid().ToString("N");
        string stage = Path.Combine(directory, ".backup-" + id); Directory.CreateDirectory(stage);
        string archive = Path.Combine(directory, "CloudDriveLite-" + id + ".zip");
        try
        {
            string database = Path.Combine(stage, "index.db");
            using (var source = Open(index.DatabasePath, SqliteOpenMode.ReadOnly))
            using (var target = Open(database, SqliteOpenMode.ReadWriteCreate)) source.BackupDatabase(target);
            ValidateDatabase(database, out int schema);
            string config = Path.Combine(stage, "product.json"); ProductEnvironment.AtomicJson(config, settings ?? ProductSettings.Load());
            var manifest = new BackupManifest(ProductEnvironment.Version, DateTimeOffset.UtcNow, schema, ProductEnvironment.UserKey, DeviceKey, Hash(database), Hash(config));
            ProductEnvironment.AtomicJson(Path.Combine(stage, "manifest.json"), manifest);
            using (var zip = ZipFile.Open(archive + ".partial", ZipArchiveMode.Create))
                foreach (string name in new[] { "index.db", "product.json", "manifest.json" }) zip.CreateEntryFromFile(Path.Combine(stage, name), name, CompressionLevel.Fastest);
            File.Move(archive + ".partial", archive); return archive;
        }
        finally { foreach (string file in Directory.GetFiles(stage)) File.Delete(file); Directory.Delete(stage); }
    }
    public static BackupManifest Inspect(string archive)
    {
        using var zip = ZipFile.OpenRead(archive); CheckEntries(zip);
        using var stream = zip.GetEntry("manifest.json")!.Open();
        var manifest = JsonSerializer.Deserialize<BackupManifest>(stream) ?? throw new IOException("备份清单无效。");
        if (manifest.Schema is < 4 or > IndexStore.CurrentSchema || !System.Version.TryParse(manifest.Version, out _)) throw new IOException("备份版本不受支持；仅支持v4至当前数据库，拒绝降级恢复。");
        return manifest;
    }
    // The caller must quiesce every process and hold maintenance + all root agent/operation locks.
    // Refuse rollback over newer tasks/mappings; this is intentionally more conservative than blind restore.
    public static string RestoreOffline(IndexStore current, string archive, string backupDirectory, string settingsPath, bool maintenanceHeld)
    {
        if (!maintenanceHeld) throw new IOException("必须先安全停止所有后台任务并持有维护锁。");
        var manifest = Inspect(archive); string stage = Path.Combine(backupDirectory, ".restore-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(stage);
        try
        {
            using (var zip = ZipFile.OpenRead(archive))
            {
                CheckEntries(zip);
                foreach (string name in new[] { "index.db", "product.json" }) zip.GetEntry(name)!.ExtractToFile(Path.Combine(stage, name));
            }
            string restored = Path.Combine(stage, "index.db"), restoredSettings = Path.Combine(stage, "product.json");
            if (Hash(restored) != manifest.DatabaseSha256 || Hash(restoredSettings) != manifest.SettingsSha256) throw new IOException("备份SHA-256不一致；没有更改当前数据。");
            ValidateDatabase(restored, out int schema); if (schema != manifest.Schema) throw new IOException("备份数据库与清单版本不一致。");
            if (schema < IndexStore.CurrentSchema) { using var upgrade = Open(restored, SqliteOpenMode.ReadWrite); using var migration = upgrade.BeginTransaction(); if (schema < 5) IndexStore.MigrateHealth(upgrade); if (schema < 6) IndexStore.MigrateStorageConnections(upgrade); migration.Commit(); }
            var settings = ProductSettings.Load(restoredSettings);
            if (!CompatibleState(current.DatabasePath, restored)) throw new IOException("备份后的任务/映射或同步状态已改变；拒绝覆盖新记录。请保留两份数据库进行人工恢复。");
            // Prepare safe paused state in the staged database before the atomic online-backup replacement.
            using (var prepared = Open(restored, SqliteOpenMode.ReadWrite))
            using (var transaction = prepared.BeginTransaction())
            using (var pause = prepared.CreateCommand())
            {
                pause.CommandText = "DELETE FROM leases; DELETE FROM runtime; UPDATE SyncSettings SET Enabled=0; UPDATE ArchiveSettings SET Paused=1; UPDATE connections SET config=json_set(config,'$.AutoConnect',json('false'));";
                pause.ExecuteNonQuery(); transaction.Commit();
            }
            string before = Create(current, backupDirectory, ProductSettings.Load(settingsPath));
            // BackupDatabase writes into the existing database safely; do not replace a live WAL file.
            SqliteConnection.ClearAllPools();
            using (var source = Open(restored, SqliteOpenMode.ReadOnly))
            using (var target = Open(current.DatabasePath, SqliteOpenMode.ReadWrite))
            {
                source.BackupDatabase(target);
            }
            ValidateDatabase(current.DatabasePath, out _);
            ProductEnvironment.AtomicJson(settingsPath, settings with { LoginStartup = false, SyncPaused = true, CheckUpdates = false });
            // Credential Manager blobs are never exported. Old references may be invalid on another device/account.
            return before;
        }
        finally { foreach (string file in Directory.GetFiles(stage)) File.Delete(file); Directory.Delete(stage); }
    }
    private static bool CompatibleState(string current, string backup)
    {
        using var db = Open(current, SqliteOpenMode.ReadOnly); using var attach = db.CreateCommand(); attach.CommandText = "ATTACH DATABASE $file AS saved"; attach.Parameters.AddWithValue("$file", backup); attach.ExecuteNonQuery();
        // Empty installations can recover a lost DB; a configured installation must have identical durable state.
        using var count = db.CreateCommand(); count.CommandText = "SELECT (SELECT COUNT(*) FROM connections)+(SELECT COUNT(*) FROM StorageConnections)"; if ((long)count.ExecuteScalar()! == 0) return true;
        foreach (string table in new[] { "StorageConnections", "FolderMappings", "connections", "files", "SyncTasks", "FileVersions", "FileChangeEvents", "ConflictRecords", "DeletedFileRecords", "ArchiveItems", "ArchiveJournal", "FileStorageMappings", "CacheOperations" })
        {
            using var command = db.CreateCommand(); command.CommandText = $"SELECT EXISTS(SELECT * FROM main.{table} EXCEPT SELECT * FROM saved.{table}) OR EXISTS(SELECT * FROM saved.{table} EXCEPT SELECT * FROM main.{table})";
            if ((long)command.ExecuteScalar()! != 0) return false;
        }
        return true;
    }
    private static void CheckEntries(ZipArchive zip)
    {
        string[] expected = ["index.db", "product.json", "manifest.json"];
        if (zip.Entries.Count != 3 || expected.Any(name => zip.Entries.Count(e => e.FullName == name) != 1)) throw new IOException("备份包含未知/重复路径，拒绝解压。");
        if (zip.Entries.Any(e => e.Length > (e.FullName == "index.db" ? 16L << 30 : 1L << 20))) throw new IOException("备份超出大小限制。");
    }
    public static void ValidateDatabase(string path, out int schema)
    {
        using var db = Open(path, SqliteOpenMode.ReadOnly); using var check = db.CreateCommand(); check.CommandText = "PRAGMA integrity_check";
        if (check.ExecuteScalar() as string != "ok") throw new IOException("SQLite完整性检查失败。");
        check.CommandText = "PRAGMA user_version"; schema = checked((int)(long)check.ExecuteScalar()!); if (schema > IndexStore.CurrentSchema) throw new IOException("数据库比程序新；禁止降级。");
    }
    public static void Prune(string directory, int keep)
    {
        foreach (var file in new DirectoryInfo(directory).EnumerateFiles("CloudDriveLite-*.zip").OrderByDescending(f => f.Name).Skip(keep))
        { Inspect(file.FullName); file.Delete(); }
    }
    private static SqliteConnection Open(string path, SqliteOpenMode mode)
    { var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Mode = mode, Pooling = false, DefaultTimeout = 15 }.ToString()); db.Open(); return db; }
    public static string Hash(string path) { using var stream = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(stream)); }
}
