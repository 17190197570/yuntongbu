using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace CloudDrive.Storage;

internal static class ArchiveFile
{
    public static void CheckPhysicalOverlap(string source, string target)
    {
        string? Identity(string directory)
        {
            if (!Directory.Exists(directory)) return null;
            FileSystemSource.CheckChain(directory); using var h = NativeUpload.Open(directory, 0x80, 7, 3, false);
            return FileSystemSource.ReadMetadata(h, Guid.Empty, "scope").RemoteId;
        }
        bool Contains(string? id, string directory)
        {
            if (id is null) return false;
            for (var p = new DirectoryInfo(directory); p is not null; p = p.Parent)
                if (Identity(p.FullName) == id) return true;
            return false;
        }
        // Conservatively reject equal directory identities through mapped-drive/server aliases.
        // Duplicate identities reported by cloned volumes may also require manual configuration changes.
        if (Contains(Identity(source), target) || Contains(Identity(target), source)) throw new IOException("源/目标目录身份重叠或无法区分（包括SMB别名/克隆卷）；拒绝递归自归档。");
    }
    public static RemoteEntry? Metadata(string full, Guid storage, string relative, bool missingParents = false)
    {
        var checkedParent = new DirectoryInfo(Path.GetDirectoryName(full)!);
        if (!missingParents || checkedParent.Exists) FileSystemSource.CheckChain(checkedParent.FullName);
        try { using var h = NativeUpload.Open(full, 0x80, 7, 3, false); return FileSystemSource.ReadMetadata(h, storage, relative); }
        catch (Win32Exception e) when (e.NativeErrorCode == 2) { return null; }
        catch (Win32Exception e) when (e.NativeErrorCode == 3 && missingParents)
        {
            // A nonexistent target subdirectory is safe only after validating its nearest existing ancestor.
            var parent = new DirectoryInfo(Path.GetDirectoryName(full)!);
            while (!parent.Exists) parent = parent.Parent ?? throw new IOException("无法验证目标目录的祖先。");
            FileSystemSource.CheckChain(parent.FullName); return null;
        }
    }
    public static void CreateParents(string root, string relative)
    {
        string current = root;
        foreach (string part in Paths.Parent(relative).Split('\\', StringSplitOptions.RemoveEmptyEntries))
        { current = Paths.Under(current, part); Directory.CreateDirectory(current); FileSystemSource.CheckChain(current); }
    }
    public static long? AvailableBytes(string root) => GetDiskFreeSpaceEx(root, out ulong available, out _, out _) ? checked((long)available) : null;
    public static void DeleteLocked(SafeFileHandle handle)
    { byte[] disposition = [1]; if (!SetFileInformationByHandle(handle, 4, disposition, 1)) throw new Win32Exception(Marshal.GetLastWin32Error()); }
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetFileInformationByHandle(SafeFileHandle handle, int kind, byte[] data, uint length);
    [DllImport("kernel32.dll", EntryPoint = "GetDiskFreeSpaceExW", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetDiskFreeSpaceEx(string path, out ulong available, out ulong total, out ulong free);
}
