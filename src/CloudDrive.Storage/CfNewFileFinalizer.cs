using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace CloudDrive.Storage;

// Retains the phase-1 native identity format and never dehydrates or alters user data.
public sealed class CfNewFileFinalizer : INewFileFinalizer
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct RootStandard
    {
        public long FileId;
        public uint Hydration, Population, InSync, HardLink, Status;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string Provider;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string Version;
        public uint IdentityLength;
    }
    public void ValidateRoot(SmbConnection connection) => _ = RootId(connection);
    public static bool IsOwnedDirectory(SmbConnection connection, string path)
    {
        using var handle = NativeUpload.Open(path, 0x80, 7, 3, false);
        var info = Placeholder(handle);
        return info is not null && BitConverter.ToInt64(info, 48) == RootId(connection);
    }
    internal static long RootId(SmbConnection connection)
    {
        byte[] buffer = new byte[67000];
        Marshal.ThrowExceptionForHR(CfGetSyncRootInfoByPath(connection.SyncRoot, 1, buffer, (uint)buffer.Length, out _));
        int lengthOffset = (int)Marshal.OffsetOf<RootStandard>(nameof(RootStandard.IdentityLength));
        int length = checked((int)BitConverter.ToUInt32(buffer, lengthOffset));
        if (length < 0 || length % 2 != 0 || length > buffer.Length - lengthOffset - 4) throw new IOException("同步根身份格式无效。");
        string identity = Encoding.Unicode.GetString(buffer, lengthOffset + 4, length);
        string expected = "CloudDriveLite.LocalPrototype.v1\nsmb:" + connection.Id.ToString("N") + "\nSQLiteStableMapping.v2";
        if (!identity.Equals(expected, StringComparison.OrdinalIgnoreCase)) throw new IOException("同步根不属于此 SMB 连接。");
        return BitConverter.ToInt64(buffer, 0);
    }
    private static byte[]? Placeholder(SafeFileHandle handle)
    {
        byte[] buffer = new byte[4200]; int result = CfGetPlaceholderInfo(handle, 1, buffer, (uint)buffer.Length, out _);
        if (result == unchecked((int)0x80070178)) return null; // ERROR_NOT_A_CLOUD_FILE (376).
        Marshal.ThrowExceptionForHR(result); return buffer;
    }
    public void ValidateLocal(SmbConnection connection, FileStream lockedLocal, string relative)
    {
        long rootId = RootId(connection);
        var info = Placeholder(lockedLocal.SafeFileHandle);
        byte[] fileRoot = new byte[8];
        Marshal.ThrowExceptionForHR(CfGetSyncRootInfoByHandle(lockedLocal.SafeFileHandle, 0, fileRoot, 8, out _));
        if (BitConverter.ToInt64(fileRoot) != rootId) throw new IOException("打开的本地文件已不在此同步根中。");
        if (info is null)
        {
            if (NativeUpload.IsReparsePoint(lockedLocal.SafeFileHandle)) throw new IOException("新增文件是未知重解析点，拒绝上传。");
            return;
        }
        int length = checked((int)BitConverter.ToUInt32(info, 56));
        if (BitConverter.ToInt64(info, 48) != rootId || length < 64 || length > info.Length - 60)
            throw new IOException("本地占位不属于此同步根。");
        var identity = info.AsSpan(60, length);
        if (BitConverter.ToUInt32(identity[..4]) != 0x314C4443 || BitConverter.ToUInt32(identity[4..8]) != 1 ||
            !identity.Slice(24, 16).SequenceEqual(Convert.FromHexString(Paths.StableId(connection.Id, relative))) ||
            Encoding.Unicode.GetString(identity[64..]) != relative)
            throw new IOException("本地占位映射身份不匹配。");
    }
    public void Complete(SmbConnection connection, FileStream lockedLocal, RemoteEntry committed)
    {
        ValidateLocal(connection, lockedLocal, committed.RelativePath);
        byte[] identity;
        using (var memory = new MemoryStream())
        {
            using var writer = new BinaryWriter(memory, Encoding.Unicode, true);
            writer.Write(0x314C4443u); writer.Write(1u); writer.Write((uint)committed.RelativePath.Length); writer.Write(0u);
            writer.Write(Wire.VersionStamp(committed)); writer.Write(Convert.FromHexString(committed.Id));
            writer.Write(committed.Size); writer.Write(committed.CreationTime); writer.Write(committed.ModifiedTime);
            writer.Write(Encoding.Unicode.GetBytes(committed.RelativePath)); identity = memory.ToArray();
        }
        if (identity.Length > 4096) throw new IOException("路径超出已有 CFAPI 身份长度限制。");
        // Until modified-file uploads pass NAS acceptance, completed additions share phase-2 read-only protection.
        NativeUpload.MarkReadOnly(lockedLocal.SafeFileHandle);
        long usn = 0;
        if (Placeholder(lockedLocal.SafeFileHandle) is null)
            Marshal.ThrowExceptionForHR(CfConvertToPlaceholder(lockedLocal.SafeFileHandle, identity, (uint)identity.Length, 0, out usn, 0));
        // Align timestamps with the actual server precision. Size was verified against the locked local snapshot.
        // No dehydrate ranges or data changes; the conversion USN guards the metadata update.
        byte[] metadata = NativeUpload.LocalMetadata(lockedLocal.SafeFileHandle, committed);
        Marshal.ThrowExceptionForHR(CfUpdatePlaceholder(lockedLocal.SafeFileHandle, metadata, identity, (uint)identity.Length, 0, 0, 0, ref usn, 0));
        Marshal.ThrowExceptionForHR(CfSetInSyncState(lockedLocal.SafeFileHandle, 1, 0, ref usn));
    }
    [DllImport("cldapi.dll", CharSet = CharSet.Unicode)]
    private static extern int CfGetSyncRootInfoByPath(string path, int kind, [Out] byte[] buffer, uint size, out uint returned);
    [DllImport("cldapi.dll")]
    private static extern int CfGetSyncRootInfoByHandle(SafeFileHandle handle, int kind, [Out] byte[] buffer, uint size, out uint returned);
    [DllImport("cldapi.dll")]
    private static extern int CfGetPlaceholderInfo(SafeFileHandle handle, int kind, [Out] byte[] buffer, uint size, out uint returned);
    [DllImport("cldapi.dll")]
    private static extern int CfConvertToPlaceholder(SafeFileHandle handle, byte[] identity, uint length, uint flags, out long usn, nint overlapped);
    [DllImport("cldapi.dll")]
    private static extern int CfUpdatePlaceholder(SafeFileHandle handle, byte[] metadata, byte[] identity, uint length, nint ranges, uint count, uint flags, ref long usn, nint overlapped);
    [DllImport("cldapi.dll")]
    private static extern int CfSetInSyncState(SafeFileHandle handle, int state, uint flags, ref long usn);
}
