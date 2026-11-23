using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace CloudDrive.Storage;

public sealed class FileSystemSource(Guid connection, string root, Func<string, bool>? exclude = null) : IRemoteSource
{
    public FileSystemSource WithExclusions(Func<string, bool> predicate) => new(connection, root, path => (exclude?.Invoke(path) ?? false) || predicate(path));
    public static void CheckChain(string path)
    {
        var current = new DirectoryInfo(Path.GetFullPath(path));
        while (current is not null)
        {
            if ((current.Attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("远端目录包含重解析点；本阶段拒绝跟随。");
            current = current.Parent;
        }
    }
    public IEnumerable<RemoteEntry> Enumerate(string relative, CancellationToken token)
    {
        string directory = relative.Length == 0 ? root : Paths.Under(root, relative);
        CheckChain(directory);
        foreach (var item in new DirectoryInfo(directory).EnumerateFileSystemInfos("*", new EnumerationOptions { AttributesToSkip = 0, IgnoreInaccessible = false, RecurseSubdirectories = false }))
        {
            token.ThrowIfCancellationRequested();
            var path = relative.Length == 0 ? item.Name : relative + "\\" + item.Name; Paths.Relative(path);
            if (exclude?.Invoke(path) == true) continue;
            if ((item.Attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("扫描遇到重解析点，未将本次扫描标记为完成。");
            using var handle = Open(item.FullName, false);
            yield return ReadMetadata(handle, connection, path);
        }
    }
    public static RemoteEntry ReadMetadata(SafeFileHandle handle, Guid connection, string path, bool allowOwnedCloudFile = false)
    {
        if (!GetFileInformationByHandle(handle, out var data)) throw new Win32Exception(Marshal.GetLastWin32Error());
        if ((data.Attributes & 0x400) != 0 && !allowOwnedCloudFile) throw new IOException("远端重解析文件不受支持。");
        var id = data.Volume.ToString("X8", System.Globalization.CultureInfo.InvariantCulture) + ":" + data.IndexHigh.ToString("X8", System.Globalization.CultureInfo.InvariantCulture) + data.IndexLow.ToString("X8", System.Globalization.CultureInfo.InvariantCulture);
        if (GetFileInformationByHandleEx(handle, 18, out var extended, (uint)Marshal.SizeOf<FileIdInfo>()))
            id = extended.Volume.ToString("X16", System.Globalization.CultureInfo.InvariantCulture) + ":" + extended.Low.ToString("X16", System.Globalization.CultureInfo.InvariantCulture) + extended.High.ToString("X16", System.Globalization.CultureInfo.InvariantCulture);
        bool directory = (data.Attributes & 0x10) != 0;
        long size = directory ? 0 : checked(((long)data.SizeHigh << 32) | data.SizeLow);
        long created = ToLong(data.Created), modified = ToLong(data.Modified);
        bool reliableCreated = created > 0, reliableModified = modified > 0;
        if (created <= 0) created = modified > 0 ? modified : DateTime.UnixEpoch.ToFileTimeUtc();
        if (modified <= 0) modified = created;
        return new(Paths.StableId(connection, path), connection, path, directory, size,
            created, modified, id, data.Attributes, reliableCreated, reliableModified);
    }
    private static long ToLong(System.Runtime.InteropServices.ComTypes.FILETIME value) => ((long)value.dwHighDateTime << 32) | (uint)value.dwLowDateTime;
    public async Task<Stream> OpenReadAsync(RemoteEntry expected, CancellationToken token)
    {
        token.ThrowIfCancellationRequested(); string path = Paths.Under(root, expected.RelativePath);
        CheckChain(Path.GetDirectoryName(path)!);
        var handle = Open(path, true);
        try
        {
            var actual = ReadMetadata(handle, connection, expected.RelativePath);
            if (!actual.SameVersion(expected) || actual.Id != expected.Id || actual.IsDirectory) throw new StorageException(FailureKind.SourceChanged, "Source version changed.");
            var stream = new FileStream(handle, FileAccess.Read, 1024 * 1024, true);
            await Task.CompletedTask;
            return stream;
        }
        catch { handle.Dispose(); throw; }
    }
    private static SafeFileHandle Open(string path, bool data)
    {
        var handle = CreateFile(Paths.Native(path), data ? 0x80000000u : 0x80u, 1, 0, 3,
            0x00200000u | 0x02000000u | (data ? 0x40000000u : 0u), 0);
        if (handle.IsInvalid) { int code = Marshal.GetLastWin32Error(); handle.Dispose(); throw new Win32Exception(code); } return handle;
    }
    [StructLayout(LayoutKind.Sequential)] private struct ByHandleInfo
    {
        public uint Attributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME Created, Accessed, Modified;
        public uint Volume, SizeHigh, SizeLow, Links, IndexHigh, IndexLow;
    }
    [StructLayout(LayoutKind.Sequential)] private struct FileIdInfo { public ulong Volume, Low, High; }
    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string name, uint access, uint share, nint security, uint creation, uint flags, nint template);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle handle, out ByHandleInfo info);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandleEx(SafeFileHandle handle, int type, out FileIdInfo info, uint size);
}

public interface IRemoteSource
{
    IEnumerable<RemoteEntry> Enumerate(string relative, CancellationToken token);
    Task<Stream> OpenReadAsync(RemoteEntry expected, CancellationToken token);
}

public sealed class RemoteScanner(IndexStore index)
{
    public async Task ScanAsync(Guid connection, IRemoteSource source, CancellationToken token)
    {
        if (source is FileSystemSource fileSystem) source = fileSystem.WithExclusions(path => index.IsUploadTemp(connection, path) || index.IsArchiveTemp(connection, path));
        using var lease = ConnectionLock.Acquire(connection, "scan");
        var generation = index.BeginScan(connection);
        index.Log(connection, "scan", "开始或恢复扫描 " + generation);
        try
        {
            await Task.Run(() =>
            {
                string? directory;
                while ((directory = index.NextDirectory(connection, generation)) is not null)
                {
                    token.ThrowIfCancellationRequested();
                    var batch = new List<RemoteEntry>(256);
                    foreach (var entry in source.Enumerate(directory, token))
                    {
                        if (index.IsUploadTemp(connection, entry.RelativePath) || index.IsArchiveTemp(connection, entry.RelativePath)) continue;
                        batch.Add(entry);
                        if (batch.Count == 256) { index.SaveBatch(connection, generation, batch); batch.Clear(); }
                    }
                    if (batch.Count > 0) index.SaveBatch(connection, generation, batch);
                    token.ThrowIfCancellationRequested();
                    index.DirectoryComplete(connection, generation, directory);
                }
                token.ThrowIfCancellationRequested(); index.FinishScan(connection, generation);
            }, token);
            index.Log(connection, "scan", "完整扫描完成，索引项数 " + index.Count(connection));
        }
        catch (Exception error)
        {
            if (FailureClassifier.Describe("scan", error).Kind == FailureKind.ShareMissing) index.RestartInterruptedScan(connection);
            index.Log(connection, "scan-interrupted", FailureClassifier.Describe("scan", error).Message + "；保留索引及检查点。"); throw;
        }
    }
}

public sealed class ConnectionLock : IDisposable
{
    private readonly FileStream stream;
    private ConnectionLock(FileStream stream) { this.stream = stream; }
    public static ConnectionLock Acquire(Guid id, string purpose)
    {
        Directory.CreateDirectory(IndexStore.DefaultDirectory);
        try { return new(new FileStream(Path.Combine(IndexStore.DefaultDirectory, id.ToString("N") + "." + purpose + ".lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None)); }
        catch (IOException) { throw new IOException("此连接已有 " + purpose + " 操作运行（包括其他用户令牌）。"); }
    }
    public void Dispose() => stream.Dispose();
}
