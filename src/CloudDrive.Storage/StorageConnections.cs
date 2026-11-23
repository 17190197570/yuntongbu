using Microsoft.Data.Sqlite;
using System.Text.Json;

namespace CloudDrive.Storage;

public sealed record StorageConnection(Guid Id, string Server, int Port, string Share, string UserName, string Domain = "")
{
    public string Name => $"{Server}:{Port} / {Share} · {UserName}";
    public string CredentialKey => "CloudDriveLite/" + Id.ToString("N");
    public string Account => string.IsNullOrWhiteSpace(Domain) ? UserName : Domain + "\\" + UserName;
    public StorageConnection Validate() { Access().Validate(requireRoot: false); return this; }
    public SmbConnection Access() => new(Id, Name, Server, Port, Share, "", UserName, Domain, false, "", Id);
    public static StorageConnection From(SmbConnection c) => new(c.StorageId ?? c.Id, c.Server, c.Port, c.Share, c.UserName, c.Domain);
}

public sealed record FolderMapping(Guid Id, Guid StorageId, string LocalPath, string RemotePath = "", bool AutoConnect = false)
{
    public SmbConnection Bind(StorageConnection storage)
    {
        storage.Validate();
        if (StorageId != storage.Id) throw new ArgumentException("映射选择的NAS不匹配。");
        if (string.IsNullOrWhiteSpace(LocalPath) || !Path.IsPathFullyQualified(LocalPath)) throw new ArgumentException("请填写本地文件夹的完整路径。");
        string local = Path.TrimEndingDirectorySeparator(Path.GetFullPath(LocalPath));
        return new SmbConnection(Id, Path.GetFileName(local) + " → " + storage.Server + "\\" + storage.Share + (RemotePath.Length == 0 ? "" : "\\" + RemotePath), storage.Server, storage.Port, storage.Share, RemotePath, storage.UserName, storage.Domain, AutoConnect, local, storage.Id).Validate();
    }
}

public sealed partial class IndexStore
{
    internal static void MigrateStorageConnections(SqliteConnection db)
    {
        using var stream = typeof(IndexStore).Assembly.GetManifestResourceStream("CloudDrive.Storage.Migrations.006_storage_connections.sql") ?? throw new IOException("缺少NAS连接迁移。");
        using var reader = new StreamReader(stream); using var command = Cmd(db, reader.ReadToEnd()); command.ExecuteNonQuery();
    }
    public IReadOnlyList<StorageConnection> Storages()
    {
        using var db = Open(); using var command = Cmd(db, "SELECT Config FROM StorageConnections ORDER BY Id"); using var row = command.ExecuteReader();
        var items = new List<StorageConnection>(); while (row.Read()) items.Add(JsonSerializer.Deserialize<StorageConnection>(row.GetString(0))!.Validate()); return items;
    }
    public StorageConnection Storage(Guid id) => Storages().SingleOrDefault(s => s.Id == id) ?? throw new ArgumentException("NAS连接不存在。");
    public Guid StorageForMapping(Guid id)
    {
        using var db = Open(); using var command = Cmd(db, "SELECT StorageId FROM FolderMappings WHERE MappingId=$id", ("$id", id.ToString()));
        return command.ExecuteScalar() is string saved ? Guid.Parse(saved) : throw new ArgumentException("文件夹映射不存在。");
    }
    public IReadOnlyList<SmbConnection> MappingsForStorage(Guid id) => Connections().Where(c => (c.StorageId ?? c.Id) == id).ToArray();
    private static void WriteStorage(SqliteConnection db, StorageConnection storage)
    {
        using var command = Cmd(db, "INSERT INTO StorageConnections VALUES($id,$json,$ref) ON CONFLICT(Id) DO UPDATE SET Config=$json,CredentialRef=$ref", ("$id", storage.Id.ToString()), ("$json", JsonSerializer.Serialize(storage)), ("$ref", storage.CredentialKey)); command.ExecuteNonQuery();
    }
    private static void WriteMapping(SqliteConnection db, SmbConnection config)
    {
        using var command = Cmd(db, "INSERT INTO connections VALUES($id,$json,$ref) ON CONFLICT(id) DO UPDATE SET config=$json,credential_ref=$ref; INSERT INTO FolderMappings VALUES($id,$storage) ON CONFLICT(MappingId) DO UPDATE SET StorageId=$storage", ("$id", config.Id.ToString()), ("$json", JsonSerializer.Serialize(config)), ("$ref", config.CredentialKey), ("$storage", (config.StorageId ?? config.Id).ToString())); command.ExecuteNonQuery();
    }
    public void ValidateStorageSave(StorageConnection storage)
    {
        storage.Validate(); foreach (var c in MappingsForStorage(storage.Id)) ValidateSave(c with { Server = storage.Server, Port = storage.Port, Share = storage.Share, UserName = storage.UserName, Domain = storage.Domain });
    }
    public void SaveStorage(StorageConnection storage)
    {
        ValidateStorageSave(storage); var mappings = MappingsForStorage(storage.Id);
        using var db = Open(); using var transaction = db.BeginTransaction(); WriteStorage(db, storage);
        foreach (var c in mappings) WriteMapping(db, c with { Server = storage.Server, Port = storage.Port, Share = storage.Share, UserName = storage.UserName, Domain = storage.Domain });
        transaction.Commit();
    }
    public void SaveFolderMapping(FolderMapping mapping) => Save(mapping.Bind(Storage(mapping.StorageId)));
    public void DeleteStorage(Guid id)
    {
        if (MappingsForStorage(id).Count > 0) throw new IOException("仍有文件夹映射使用此NAS，请先处理映射，不能移除其凭据。");
        using var db = Open(); using var command = Cmd(db, "DELETE FROM StorageConnections WHERE Id=$id", ("$id", id.ToString())); command.ExecuteNonQuery();
    }
}
