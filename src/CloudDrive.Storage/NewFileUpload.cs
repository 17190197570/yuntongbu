using System.Buffers;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace CloudDrive.Storage;

// First phase-3 slice: CREATE NEW only. No replacement, directory creation, rename of user files or deletion.
public sealed class NewFileUpload(IndexStore index, SmbConnection config, Func<CancellationToken, Task<string>> mappedRoot, INewFileFinalizer finalizer)
{
    public async Task ExecuteAsync(SyncTask task, CancellationToken token)
    {
        if (task.ConnectionId != config.Id || task.TaskType != SyncTaskType.UPLOAD_FILE || task.TaskStatus != SyncTaskStatus.Processing)
            throw new ArgumentException("不是此连接的新增上传任务。");
        using var operationLock = ConnectionLock.Acquire(Guid.ParseExact(task.FileId, "N"), "file-operation");
        if (index.StorageMapping(task.FileId) is not null) throw new SyncConflictException("已归档文件必须使用映射感知的已有文件写回；新增上传拒绝写入原NAS。");
        finalizer.ValidateRoot(config);
        string path = Paths.Under(config.SyncRoot, task.SourcePath);
        CheckLocalParents(config, path);
        var before = new FileInfo(path); long size = before.Length, modified = before.LastWriteTimeUtc.ToFileTimeUtc();
        await Task.Delay(index.SyncSettings(config.Id).DebounceMilliseconds, token);
        // Deny existing/new writers and rename/delete for the entire hash/upload/finalize window.
        // WRITE_DAC permits CFAPI adoption even when recovering a previously adopted read-only file.
        using var handle = NativeUpload.Open(path, 0x80040100, 1, 3, false);
        await using var local = new FileStream(handle, FileAccess.Read, Wire.Chunk, false);
        finalizer.ValidateLocal(config, local, task.SourcePath);
        var snapshot = FileSystemSource.ReadMetadata(handle, config.Id, task.SourcePath, true);
        if (snapshot.IsDirectory || snapshot.Size != size || snapshot.ModifiedTime != modified) throw new IOException("文件仍在变化，等待稳定后重试。");
        string hash = await StreamHash(local, token); local.Position = 0;
        using var mappingDeadline = CancellationTokenSource.CreateLinkedTokenSource(token); mappingDeadline.CancelAfter(TimeSpan.FromSeconds(30));
        string root = await mappedRoot(mappingDeadline.Token); FileSystemSource.CheckChain(root);
        string target = Paths.Under(root, task.TargetPath), temp = Paths.Under(root, task.TempPath ?? throw new IOException("缺少临时文件日志。"));
        FileSystemSource.CheckChain(Path.GetDirectoryName(target)!);
        RemoteEntry? existing = ReadEntry(target, task.TargetPath);
        if (existing is not null)
        {
            // A commit-intent journal plus the exact staged file ID and digest is required.
            if (task.Phase is not ("CommitIntent" or "Committed") || task.RemoteEntry is null || task.LocalHash is null ||
                existing.RemoteId != task.RemoteEntry.RemoteId || existing.Size != task.FileSize)
                throw new SyncConflictException("远端已有同名项目；本地文件与远端均保留，未覆盖。");
            using var committedHandle = NativeUpload.Open(target, 0x80000000, 1, 3, true);
            await using var committedStream = new FileStream(committedHandle, FileAccess.Read, Wire.Chunk, true);
            var actual = FileSystemSource.ReadMetadata(committedHandle, config.Id, task.TargetPath);
            if (actual != existing || await StreamHash(committedStream, token) != task.LocalHash)
                throw new SyncConflictException("可能已提交的远端版本已变化；拒绝再次上传。");
            if (hash != task.LocalHash || snapshot.RemoteId != task.LocalFileId)
                throw new SyncConflictException("上次上传已提交，但本地又有修改；保留双方，不能标记已同步。");
            finalizer.Complete(config, local, actual);
            index.FinishNewUpload(task, actual, hash); return;
        }
        if (task.Phase == "Committed") throw new SyncConflictException("已提交文件在远端被删除；保留本地，不自动重建。");
        if (task.Phase == "CommitIntent" && (task.LocalHash != hash || task.LocalFileId != snapshot.RemoteId))
            throw new SyncConflictException("提交结果待核对且本地已有新版本；保留临时文件与本地文件。");
        var oldTemp = ReadEntry(temp, task.TempPath!);
        if (task.Phase == "CommitIntent" && oldTemp is null) throw new SyncConflictException("提交意图已记录，但目标和临时文件均不存在；保留本地并等待核对。");
        if (oldTemp is not null && (task.RemoteEntry is null || oldTemp.RemoteId != task.RemoteEntry.RemoteId))
            throw new SyncConflictException("临时路径存在但无法证明归属；保留它，等待人工检查。");
        if (task.Phase != "CommitIntent")
        {
            index.SaveUploadPhase(task, "Staging", hash, snapshot, task.RemoteEntry);
            using var writeHandle = NativeUpload.Open(temp, 0xC0000000, 1, oldTemp is null ? 1u : 3u, true);
            await using var output = new FileStream(writeHandle, FileAccess.ReadWrite, Wire.Chunk, true);
            var owned = FileSystemSource.ReadMetadata(writeHandle, config.Id, task.TempPath!);
            if (oldTemp is not null && owned.RemoteId != oldTemp.RemoteId) throw new SyncConflictException("临时文件被替换；拒绝写入。");
            index.SaveUploadPhase(task, "Copying", hash, snapshot, owned);
            output.SetLength(0);
            byte[] buffer = ArrayPool<byte>.Shared.Rent(Wire.Chunk);
            try
            {
                long transferred = 0, recorded = 0;
                while (true)
                {
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token); timeout.CancelAfter(TimeSpan.FromSeconds(30));
                    int count = await local.ReadAsync(buffer.AsMemory(0, Wire.Chunk), timeout.Token); if (count == 0) break;
                    await output.WriteAsync(buffer.AsMemory(0, count), timeout.Token); transferred += count;
                    if (transferred - recorded >= 16L * Wire.Chunk) { index.UploadProgress(task.Id, transferred); recorded = transferred; }
                }
                await output.FlushAsync(token); output.Flush(true);
                if (output.Length != snapshot.Size) throw new IOException("临时文件长度不一致。");
                NativeUpload.SetTimes(writeHandle, snapshot.CreationTime, snapshot.ModifiedTime);
                owned = FileSystemSource.ReadMetadata(writeHandle, config.Id, task.TempPath!);
                index.SaveUploadPhase(task, "Uploaded", hash, snapshot, owned);
                task = task with { RemoteEntry = owned };
            }
            finally { ArrayPool<byte>.Shared.Return(buffer); }
        }
        // Reopen via SMB and reread all bytes; hold READ+DELETE and deny other writers/deleters through rename.
        using var verifyHandle = NativeUpload.Open(temp, 0x80010000, 1, 3, true);
        await using var verify = new FileStream(verifyHandle, FileAccess.Read, Wire.Chunk, true);
        var staged = FileSystemSource.ReadMetadata(verifyHandle, config.Id, task.TempPath!);
        if (task.RemoteEntry is null || staged.RemoteId != task.RemoteEntry.RemoteId || staged.IsDirectory || staged.Size != snapshot.Size ||
            await StreamHash(verify, token) != hash)
            throw new SyncConflictException("SMB 回读临时文件的长度或 SHA-256 不一致；未提交。");
        index.SaveUploadPhase(task, "CommitIntent", hash, snapshot, staged);
        token.ThrowIfCancellationRequested();
        NativeUpload.RenameNoReplace(verifyHandle, target); // No overwrite even if another device wins the race.
        var committed = FileSystemSource.ReadMetadata(verifyHandle, config.Id, task.TargetPath);
        index.SaveUploadPhase(task, "Committed", hash, snapshot, committed);
        finalizer.Complete(config, local, committed);
        index.FinishNewUpload(task, committed, hash);
    }
    public static async Task<string> StreamHash(Stream stream, CancellationToken token)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256); byte[] buffer = ArrayPool<byte>.Shared.Rent(Wire.Chunk);
        try
        {
            while (true)
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token); timeout.CancelAfter(TimeSpan.FromSeconds(30));
                int count = await stream.ReadAsync(buffer.AsMemory(0, Wire.Chunk), timeout.Token); if (count == 0) break;
                hash.AppendData(buffer, 0, count);
            }
            return Convert.ToHexString(hash.GetHashAndReset());
        }
        finally { ArrayPool<byte>.Shared.Return(buffer); }
    }
    private RemoteEntry? ReadEntry(string fullPath, string relative)
    {
        try { using var handle = NativeUpload.Open(fullPath, 0x80, 7, 3, false); return FileSystemSource.ReadMetadata(handle, config.Id, relative); }
        catch (Win32Exception error) when (error.NativeErrorCode == 2) { return null; }
        // Missing parent (3), access denial and network failure must never be interpreted as absent target.
    }
    public static void CheckLocalParents(SmbConnection config, string path)
    {
        string canonicalRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(config.SyncRoot));
        var parent = new DirectoryInfo(Path.GetDirectoryName(path)!);
        while (!parent.FullName.Equals(canonicalRoot, StringComparison.OrdinalIgnoreCase))
        {
            if ((parent.Attributes & FileAttributes.ReparsePoint) != 0 && !CfNewFileFinalizer.IsOwnedDirectory(config, parent.FullName)) throw new IOException("本地父目录包含未知重解析点。");
            parent = parent.Parent ?? throw new IOException("本地文件不在同步根内。");
        }
    }
}

internal static class NativeUpload
{
    public static void SetTimes(SafeFileHandle handle, long created, long modified)
    {
        byte[] info = new byte[40];
        if (!GetFileInformationByHandleEx(handle, 0, info, (uint)info.Length)) throw new Win32Exception(Marshal.GetLastWin32Error());
        BitConverter.GetBytes(created).CopyTo(info, 0); BitConverter.GetBytes(modified).CopyTo(info, 16);
        Array.Clear(info, 24, 8); // Leave change-time maintenance to the server.
        if (!SetFileInformationByHandle(handle, 0, info, (uint)info.Length)) throw new Win32Exception(Marshal.GetLastWin32Error());
    }
    public static byte[] LocalMetadata(SafeFileHandle handle, RemoteEntry remote)
    {
        byte[] info = new byte[48];
        if (!GetFileInformationByHandleEx(handle, 0, info, 40)) throw new Win32Exception(Marshal.GetLastWin32Error());
        BitConverter.GetBytes(remote.CreationTime).CopyTo(info, 0); BitConverter.GetBytes(remote.ModifiedTime).CopyTo(info, 8);
        BitConverter.GetBytes(remote.ModifiedTime).CopyTo(info, 16); Array.Clear(info, 24, 8);
        uint attributes = (BitConverter.ToUInt32(info, 32) & 0x2027u) | 1u;
        BitConverter.GetBytes(attributes).CopyTo(info, 32); BitConverter.GetBytes(remote.Size).CopyTo(info, 40);
        return info;
    }
    public static bool IsReparsePoint(SafeFileHandle handle)
    {
        byte[] info = new byte[8];
        if (!GetFileInformationByHandleEx(handle, 9, info, (uint)info.Length)) throw new Win32Exception(Marshal.GetLastWin32Error());
        return (BitConverter.ToUInt32(info) & 0x400) != 0;
    }
    public static void MarkReadOnly(SafeFileHandle handle)
    {
        byte[] info = new byte[40];
        if (!GetFileInformationByHandleEx(handle, 0, info, (uint)info.Length)) throw new Win32Exception(Marshal.GetLastWin32Error());
        uint attributes = BitConverter.ToUInt32(info, 32); BitConverter.GetBytes((attributes & ~0x80u) | 1u).CopyTo(info, 32);
        if (!SetFileInformationByHandle(handle, 0, info, (uint)info.Length)) throw new Win32Exception(Marshal.GetLastWin32Error());
    }
    public static SafeFileHandle Open(string path, uint access, uint share, uint creation, bool asynchronous)
    {
        var handle = CreateFile(Paths.Native(path), access, share, 0, creation, 0x00200000u | 0x02000000u | (asynchronous ? 0x40000000u : 0), 0);
        if (handle.IsInvalid) { int code = Marshal.GetLastWin32Error(); handle.Dispose(); throw new Win32Exception(code); }
        return handle;
    }
    public static void RenameNoReplace(SafeFileHandle handle, string target)
    {
        if (!Environment.Is64BitProcess) throw new PlatformNotSupportedException("上传提交目前要求 x64。");
        byte[] name = Encoding.Unicode.GetBytes(Paths.Native(target));
        byte[] info = new byte[20 + name.Length + 2]; // x64 FILE_RENAME_INFO, ReplaceIfExists=FALSE, RootDirectory=NULL.
        BitConverter.GetBytes(name.Length).CopyTo(info, 16); name.CopyTo(info, 20);
        if (!SetFileInformationByHandle(handle, 3, info, (uint)info.Length))
        {
            int code = Marshal.GetLastWin32Error();
            if (code is 80 or 183) throw new SyncConflictException("提交时远端出现同名文件；未覆盖。");
            throw new Win32Exception(code);
        }
    }
    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string path, uint access, uint share, nint security, uint creation, uint flags, nint template);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetFileInformationByHandle(SafeFileHandle handle, int kind, byte[] data, uint length);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandleEx(SafeFileHandle handle, int kind, [Out] byte[] data, uint length);
}
