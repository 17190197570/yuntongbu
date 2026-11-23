namespace CloudDrive.Storage;

// Invoked only by explicit mapping maintenance, never by a connection test or health probe.
public sealed class MappingDirectories(IndexStore index, Func<SmbConnection, CancellationToken, Task<string>> shareRoot)
{
    public async Task PrepareAsync(SmbConnection config, bool createMissing, CancellationToken token)
    {
        ValidateLocal(config); token.ThrowIfCancellationRequested();
        string local = Path.TrimEndingDirectorySeparator(Path.GetFullPath(config.SyncRoot));
        // The provider supplies an authenticated share root, not config.SubPath. The share must exist.
        string remoteRoot = await shareRoot(config with { SubPath = "" }, token);
        await Task.Run(() =>
        {
            token.ThrowIfCancellationRequested(); FileSystemSource.CheckChain(remoteRoot);
            string remote = config.SubPath.Length == 0 ? remoteRoot : Paths.Under(remoteRoot, config.SubPath);
            if (Overlaps(local, remoteRoot)) throw new IOException("本地文件夹与远端共享位置不能重叠。");
            ArchiveFile.CheckPhysicalOverlap(remoteRoot, local);
            // Check both sides before creating anything. New folders are retained on partial failure.
            ValidateChain(remote); ValidateChain(local);
            if (!createMissing && (!Directory.Exists(local) || !Directory.Exists(remote)))
                throw new IOException("本地或远端目录不存在；请勾选“创建缺失目录”后保存映射。共享本身必须已存在。");
            EnsureDirectory(remote, createMissing, token);
            EnsureDirectory(local, createMissing, token);
            CheckLocal(config, local); token.ThrowIfCancellationRequested();
        }, token);
    }
    public void ValidateLocal(SmbConnection config)
    {
        index.ValidateSave(config);
        string local = Path.TrimEndingDirectorySeparator(Path.GetFullPath(config.SyncRoot));
        var drive = new DriveInfo(Path.GetPathRoot(local)!);
        if (drive.DriveType != DriveType.Fixed || !drive.DriveFormat.Equals("NTFS", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("本地文件夹必须位于本地 NTFS 固定磁盘。");
        RejectProtectedLocation(local, index.DatabasePath); RejectProtectedLocation(local, AppContext.BaseDirectory);
        CheckLocal(config, local);
    }
    private void CheckLocal(SmbConnection config, string local)
    {
        ValidateChain(local);
        if (!Directory.Exists(local) || !Directory.EnumerateFileSystemEntries(local).Any()) return;
        var saved = index.Get(config.Id);
        if (saved is null || !Path.GetFullPath(saved.SyncRoot).Equals(local, StringComparison.OrdinalIgnoreCase))
            throw new IOException("新映射的本地目录必须为空，不能覆盖已有本地文件。");
        try { _ = CfNewFileFinalizer.RootId(config); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException)
        { throw new IOException("非空本地目录未通过原CFAPI同步根身份检查，保留文件并拒绝绑定。", e); }
    }
    private static void RejectProtectedLocation(string local, string protectedPath)
    {
        if (Overlaps(local, Path.GetFullPath(protectedPath))) throw new ArgumentException("本地映射不能覆盖程序或数据库目录，也不能位于其内部。");
    }
    private static bool Overlaps(string a, string b)
    {
        a = Path.TrimEndingDirectorySeparator(Path.GetFullPath(a)); b = Path.TrimEndingDirectorySeparator(Path.GetFullPath(b));
        return a.Equals(b, StringComparison.OrdinalIgnoreCase) || a.StartsWith(b + "\\", StringComparison.OrdinalIgnoreCase) || b.StartsWith(a + "\\", StringComparison.OrdinalIgnoreCase);
    }
    private static void ValidateChain(string path)
    {
        for (var part = new DirectoryInfo(Path.GetFullPath(path)); part is not null; part = part.Parent)
        {
            try
            {
                var attributes = File.GetAttributes(part.FullName);
                if ((attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("目录包含重解析点，拒绝跟随。");
                if ((attributes & FileAttributes.Directory) == 0) throw new IOException("目录位置已被普通文件占用。");
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }
    }
    private static void EnsureDirectory(string path, bool create, CancellationToken token)
    {
        token.ThrowIfCancellationRequested(); ValidateChain(path);
        if (Directory.Exists(path)) return;
        if (!create) throw new DirectoryNotFoundException("目录不存在。");
        var pending = new Stack<string>(); var part = new DirectoryInfo(Path.GetFullPath(path));
        while (!part.Exists) { pending.Push(part.FullName); part = part.Parent ?? throw new IOException("目录祖先不可用。"); }
        while (pending.TryPop(out string? next))
        {
            token.ThrowIfCancellationRequested(); ValidateChain(next);
            Directory.CreateDirectory(next); FileSystemSource.CheckChain(next);
        }
    }
}
