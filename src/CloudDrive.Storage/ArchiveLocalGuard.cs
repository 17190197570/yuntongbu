using System.Runtime.InteropServices;
using System.Text;
using System.ComponentModel;

namespace CloudDrive.Storage;

public interface IArchiveLocalGuard
{
    IArchiveLocalLease Acquire(SmbConnection connection, string path);
}
public interface IArchiveLocalLease : IDisposable { CacheSnapshot? Snapshot { get; } }

// Reuses the same CFAPI exclusive oplock + protected reference as native verified release.
// No read, hydration, pin change, dehydrate or SetInSync is performed here.
public sealed class WindowsArchiveLocalGuard : IArchiveLocalGuard
{
    public IArchiveLocalLease Acquire(SmbConnection connection, string path)
    {
        string full = Paths.Under(connection.SyncRoot, path); NewFileUpload.CheckLocalParents(connection, full);
        Marshal.ThrowExceptionForHR(CfOpenFileWithOplock(full, 1, out nint handle));
        if (!CfReferenceProtectedHandle(handle)) { CfCloseHandle(handle); throw new IOException("本地文件正在使用；归档未开始。"); }
        return new Lease(handle, connection, path);
    }
    private sealed class Lease(nint handle, SmbConnection connection, string path) : IArchiveLocalLease
    {
        private nint value = handle;
        public CacheSnapshot? Snapshot
        {
            get
            {
                byte[] info = new byte[4200], standard = new byte[24], basic = new byte[40];
                Marshal.ThrowExceptionForHR(CfGetPlaceholderInfo(value, 1, info, (uint)info.Length, out _));
                nint file = CfGetWin32HandleFromProtectedHandle(value);
                if (!GetFileInformationByHandleEx(file, 1, standard, (uint)standard.Length)) throw new Win32Exception(Marshal.GetLastWin32Error());
                if (!GetFileInformationByHandleEx(file, 0, basic, (uint)basic.Length)) throw new Win32Exception(Marshal.GetLastWin32Error());
                string id = Paths.StableId(connection.Id, path); int length = checked((int)BitConverter.ToUInt32(info, 56));
                if (length < 64 || length > info.Length - 60 || BitConverter.ToInt64(info, 48) != CfNewFileFinalizer.RootId(connection)) throw new IOException("锁定文件不属于当前同步根。");
                var identity = info.AsSpan(60, length);
                if (BitConverter.ToUInt32(identity) != 0x314C4443 || BitConverter.ToUInt32(identity[4..]) != 1 || !identity.Slice(24,16).SequenceEqual(Convert.FromHexString(id)) || Encoding.Unicode.GetString(identity[64..]) != path)
                    throw new IOException("锁定文件身份不匹配。");
                if (BitConverter.ToInt64(identity[40..]) != BitConverter.ToInt64(standard,8) || BitConverter.ToInt64(identity[48..]) != BitConverter.ToInt64(basic,0) || BitConverter.ToInt64(identity[56..]) != BitConverter.ToInt64(basic,16))
                    throw new IOException("本地大小或原始时间已变化，归档保留双方。");
                return new(id, connection.Id, path, standard[21] != 0, true, BitConverter.ToUInt32(info,36) == 1, BitConverter.ToUInt32(info,32) == 1,
                    BitConverter.ToInt64(standard,8), BitConverter.ToInt64(info,0), BitConverter.ToInt64(standard,0), BitConverter.ToInt64(info,16));
            }
        }
        public void Dispose() { nint h = Interlocked.Exchange(ref value, 0); if (h != 0) { CfReleaseProtectedHandle(h); CfCloseHandle(h); } }
    }
    [DllImport("cldapi.dll", CharSet = CharSet.Unicode)] private static extern int CfOpenFileWithOplock(string path, uint flags, out nint handle);
    [DllImport("cldapi.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CfReferenceProtectedHandle(nint handle);
    [DllImport("cldapi.dll")] private static extern void CfReleaseProtectedHandle(nint handle);
    [DllImport("cldapi.dll")] private static extern void CfCloseHandle(nint handle);
    [DllImport("cldapi.dll")] private static extern nint CfGetWin32HandleFromProtectedHandle(nint handle);
    [DllImport("cldapi.dll")] private static extern int CfGetPlaceholderInfo(nint handle, int kind, [Out] byte[] buffer, uint size, out uint returned);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetFileInformationByHandleEx(nint handle, int kind, [Out] byte[] buffer, uint size);
}
