using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace CloudDrive.Storage;

public sealed class WindowsCachePlatform(IndexStore index, Func<SmbConnection, CancellationToken, Task<string>> mappedRoot,
    string? nativeDirectory = null) : ICachePlatform
{
    public CacheSnapshot Probe(SmbConnection connection, string relative)
    {
        string id = Paths.StableId(connection.Id, relative); string path = Paths.Under(connection.SyncRoot, relative);
        long allocation = 0, size = 0; bool directory = false;
        try
        {
            NewFileUpload.CheckLocalParents(connection, path);
            using var handle = NativeUpload.Open(path, 0x80, 7, 3, false);
            byte[] standard = new byte[24];
            if (!GetFileInformationByHandleEx(handle, 1, standard, 24)) throw new Win32Exception(Marshal.GetLastWin32Error());
            allocation = BitConverter.ToInt64(standard, 0); size = BitConverter.ToInt64(standard, 8); directory = standard[21] != 0;
            long rootId = CfNewFileFinalizer.RootId(connection);
            byte[] info = new byte[4200]; int hr = CfGetPlaceholderInfo(handle, 1, info, (uint)info.Length, out _);
            Marshal.ThrowExceptionForHR(hr);
            int length = checked((int)BitConverter.ToUInt32(info, 56));
            if (BitConverter.ToInt64(info, 48) != rootId || length < 64 || length > info.Length - 60) throw new IOException("占位不属于当前同步根");
            var identity = info.AsSpan(60, length);
            if (BitConverter.ToUInt32(identity) != 0x314C4443 || BitConverter.ToUInt32(identity[4..]) != 1 ||
                !identity.Slice(24, 16).SequenceEqual(Convert.FromHexString(id)) || Encoding.Unicode.GetString(identity[64..]) != relative)
                throw new IOException("占位身份与当前文件映射不匹配");
            byte[] tag = new byte[8];
            if (!GetFileInformationByHandleEx(handle, 9, tag, 8)) throw new Win32Exception(Marshal.GetLastWin32Error());
            _ = CfGetPlaceholderStateFromAttributeTag(BitConverter.ToUInt32(tag), BitConverter.ToUInt32(tag, 4));
            // Attribute views can be sanitized for providers; validated CFAPI identity/info is authoritative.
            return new(id, connection.Id, relative, directory, true, BitConverter.ToUInt32(info, 36) == 1,
                BitConverter.ToUInt32(info, 32) == 1, size, BitConverter.ToInt64(info, 0), allocation, BitConverter.ToInt64(info, 16));
        }
        catch (Exception e) when (e is IOException or Win32Exception or UnauthorizedAccessException or ArgumentException or COMException or OverflowException)
        { return new(id, connection.Id, relative, directory, false, false, false, size, 0, allocation, 0, "状态不明，保留本地内容；" + e.GetType().Name + " 0x" + e.HResult.ToString("X8")); }
    }
    public CacheVolume Volume(SmbConnection connection)
    {
        var mount = new StringBuilder(1024); var volume = new StringBuilder(1024);
        if (!GetVolumePathName(connection.SyncRoot, mount, (uint)mount.Capacity) || !GetVolumeNameForVolumeMountPoint(mount.ToString(), volume, (uint)volume.Capacity) ||
            !GetDiskFreeSpaceEx(mount.ToString(), out ulong available, out ulong total, out _)) throw new Win32Exception(Marshal.GetLastWin32Error());
        return new(volume.ToString(), checked((long)available), checked((long)total));
    }
    public async Task ReleaseAsync(SmbConnection connection, string relative, CancellationToken token)
    {
        await VerifyRemoteAsync(connection, relative, token);
        // Native release independently rechecks identity, pin/InSync, exclusive access, remote version and SHA256.
        await using var bridge = new SmbBridge(index, connection, async ct => new FileSystemSource(connection.Id, await mappedRoot(connection, ct)), async (storage, ct) => new FileSystemSource(storage.Id, await mappedRoot(storage, ct)));
        bridge.Start();
        try { await InvokeAsync("release", connection, relative, token, "--pipe", bridge.PipeName, "--agent-pid", Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture)); }
        catch (IOException) { await VerifyRemoteAsync(connection, relative, token); throw; } // Classify a disconnect during native verification.
    }
    private async Task VerifyRemoteAsync(SmbConnection connection, string relative, CancellationToken token)
    {
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token); deadline.CancelAfter(TimeSpan.FromSeconds(30));
            var expected = index.Find(connection.Id, Paths.StableId(connection.Id, relative)) ?? throw new IOException("索引映射不可用");
            var routed = index.ResolveStorage(expected);
            string root = await mappedRoot(routed.Connection, deadline.Token);
            await using var verification = await new FileSystemSource(routed.Connection.Id, root).OpenReadAsync(routed.Entry, deadline.Token);
        }
        catch (Exception e) when (e is not StorageException)
        {
            var kind = FailureClassifier.Describe("cache-remote", e).Kind;
            if (kind is FailureKind.NetworkUnreachable or FailureKind.Timeout or FailureKind.Authentication or FailureKind.Cancelled)
                throw new StorageException(kind == FailureKind.Cancelled && !token.IsCancellationRequested ? FailureKind.Timeout : kind, "释放前远端不可用；本地内容保留");
            throw;
        }
    }
    public Task PinAsync(SmbConnection connection, string relative, bool pinned, CancellationToken token) => InvokeAsync(pinned ? "pin" : "unpin", connection, relative, token);
    private async Task InvokeAsync(string command, SmbConnection connection, string relative, CancellationToken token, params string[] extra)
    {
        var start = new ProcessStartInfo(Path.Combine(nativeDirectory ?? AppContext.BaseDirectory, "CloudProviderHost.exe")) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in new[] { command, "--root", connection.SyncRoot, "--path", Paths.Relative(relative) }.Concat(extra)) start.ArgumentList.Add(arg);
        using var process = Process.Start(start) ?? throw new IOException("无法启动 CFAPI 缓存操作");
        Task<string> stdout = process.StandardOutput.ReadToEndAsync(), stderr = process.StandardError.ReadToEndAsync();
        // Once a native operation starts, let its bounded completion/cancellation settle before releasing locks.
        // User cancellation stops subsequent files; killing a live FSCTL would make the outcome ambiguous.
        await process.WaitForExitAsync(CancellationToken.None); string output = await stdout, error = await stderr;
        index.Log(connection.Id, "cache-native", output.Trim());
        if (process.ExitCode != 0) throw new IOException((output + error).Trim());
    }
    [DllImport("cldapi.dll")] private static extern int CfGetPlaceholderInfo(Microsoft.Win32.SafeHandles.SafeFileHandle handle, int kind, [Out] byte[] buffer, uint size, out uint returned);
    [DllImport("cldapi.dll")] private static extern uint CfGetPlaceholderStateFromAttributeTag(uint attributes, uint tag);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandleEx(Microsoft.Win32.SafeHandles.SafeFileHandle handle, int kind, [Out] byte[] data, uint length);
    [DllImport("kernel32.dll", EntryPoint = "GetVolumePathNameW", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetVolumePathName(string path, StringBuilder volume, uint length);
    [DllImport("kernel32.dll", EntryPoint = "GetVolumeNameForVolumeMountPointW", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetVolumeNameForVolumeMountPoint(string path, StringBuilder volume, uint length);
    [DllImport("kernel32.dll", EntryPoint = "GetDiskFreeSpaceExW", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetDiskFreeSpaceEx(string path, out ulong available, out ulong total, out ulong free);
}
