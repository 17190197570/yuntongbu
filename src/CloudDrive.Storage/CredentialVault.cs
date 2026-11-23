using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security;

namespace CloudDrive.Storage;

public interface ICredentialVault
{
    void Save(string key, string account, SecureString password);
    SecureString Read(string key);
    void Delete(string key);
}

public sealed class CredentialVault : ICredentialVault
{
    public void Save(string key, string account, SecureString password)
    {
        nint blob = Marshal.SecureStringToCoTaskMemUnicode(password);
        try
        {
            var credential = new Credential { Type = 1, TargetName = key, UserName = account,
                CredentialBlob = blob, CredentialBlobSize = checked((uint)password.Length * 2), Persist = 2 };
            if (!CredWrite(ref credential, 0)) throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        finally { Marshal.ZeroFreeCoTaskMemUnicode(blob); }
    }
    public SecureString Read(string key)
    {
        if (!CredRead(key, 1, 0, out nint value)) throw new StorageException(FailureKind.Authentication, "请先保存此连接的凭据。");
        try
        {
            var credential = Marshal.PtrToStructure<Credential>(value);
            var result = new SecureString();
            for (int i = 0; i < credential.CredentialBlobSize / 2; i++) result.AppendChar((char)Marshal.ReadInt16(credential.CredentialBlob, i * 2));
            result.MakeReadOnly();
            return result;
        }
        finally { CredFree(value); }
    }
    public void Delete(string key)
    {
        if (!CredDelete(key, 1, 0) && Marshal.GetLastWin32Error() != 1168) throw new Win32Exception(Marshal.GetLastWin32Error());
    }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct Credential
    {
        public uint Flags, Type;
        public string TargetName;
        public string? Comment;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
        public uint CredentialBlobSize;
        public nint CredentialBlob;
        public uint Persist, AttributeCount;
        public nint Attributes;
        public string? TargetAlias;
        public string UserName;
    }
    [DllImport("advapi32.dll", EntryPoint = "CredWriteW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CredWrite(ref Credential credential, uint flags);
    [DllImport("advapi32.dll", EntryPoint = "CredReadW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CredRead(string target, uint type, uint flags, out nint credential);
    [DllImport("advapi32.dll", EntryPoint = "CredDeleteW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CredDelete(string target, uint type, uint flags);
    [DllImport("advapi32.dll")] private static extern void CredFree(nint buffer);
}
