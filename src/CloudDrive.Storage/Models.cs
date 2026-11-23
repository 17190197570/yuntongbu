using System.Net;
using System.Security.Cryptography;
using System.Text;

namespace CloudDrive.Storage;

public sealed record SmbConnection(Guid Id, string Name, string Server, int Port, string Share,
    string SubPath, string UserName, string Domain, bool AutoConnect, string SyncRoot, Guid? StorageId = null)
{
    public string CredentialKey => "CloudDriveLite/" + (StorageId ?? Id).ToString("N");
    public string Account => string.IsNullOrWhiteSpace(Domain) ? UserName : Domain + "\\" + UserName;
    public string RemoteShare => @"\\" + Server + "\\" + Share;
    public SmbConnection Validate(bool requireRoot = true)
    {
        if (Id == Guid.Empty || string.IsNullOrWhiteSpace(Name) || string.IsNullOrWhiteSpace(UserName))
            throw new ArgumentException("连接名称、唯一标识和用户名不能为空。");
        if (Port is < 1 or > 65535) throw new ArgumentException("端口必须为 1–65535。");
        if (string.IsNullOrWhiteSpace(Server) || Server.Any(c => char.IsControl(c) || "\\/:@[] ".Contains(c)) ||
            (!IPAddress.TryParse(Server, out _) && Uri.CheckHostName(Server) != UriHostNameType.Dns))
            throw new ArgumentException("服务器必须为 IPv4 地址或 DNS 名称；IPv6 尚未支持。");
        Paths.Relative(Share);
        if (Share.Contains('\\')) throw new ArgumentException("共享名称不能包含目录。");
        if (SubPath.Length > 0) Paths.Relative(SubPath);
        if (new[] { Name, UserName, Domain }.Any(s => s.Any(char.IsControl)))
            throw new ArgumentException("字段不能包含控制字符。");
        if (requireRoot) _ = CloudDrive.Core.ProviderCommand.Parse(["unregister", "--root", SyncRoot]);
        return this;
    }
}

public static class Paths
{
    internal static string Native(string path)
    {
        string full = Path.GetFullPath(path);
        if (full.Length < 248 || full.StartsWith(@"\\?\", StringComparison.Ordinal)) return full;
        return full.StartsWith(@"\\", StringComparison.Ordinal) ? @"\\?\UNC\" + full[2..] : @"\\?\" + full;
    }
    public static string Relative(string value)
    {
        if (string.IsNullOrEmpty(value) || value.Contains('/') || value.Any(char.IsControl) ||
            value.Split('\\').Any(s => s.Length == 0 || s is "." or ".." || s.EndsWith('.') || s.EndsWith(' ') ||
                s.IndexOfAny([':', '*', '?', '"', '<', '>', '|']) >= 0 || IsDevice(s)))
            throw new ArgumentException("路径包含不安全或 Windows 不支持的名称。");
        return value;
    }
    private static bool IsDevice(string s)
    {
        var stem = s.Split('.')[0].ToUpperInvariant();
        return stem is "CON" or "PRN" or "AUX" or "NUL" ||
            (stem.Length == 4 && (stem.StartsWith("COM", StringComparison.Ordinal) || stem.StartsWith("LPT", StringComparison.Ordinal)) && "123456789¹²³".Contains(stem[3]));
    }
    public static string StableId(Guid connection, string relative) =>
        Convert.ToHexString(SHA256.HashData(Encoding.Unicode.GetBytes(connection.ToString("N") + "\n" + Relative(relative).ToUpperInvariant())))[..32];
    public static string Parent(string relative) => relative.LastIndexOf('\\') is var i && i >= 0 ? relative[..i] : "";
    public static string Under(string root, string relative)
    {
        Relative(relative);
        var result = Path.GetFullPath(Path.Combine(root, relative));
        if (!result.StartsWith(Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)) + "\\", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("路径超出远端根目录。");
        return result;
    }
}

public enum FailureKind { None, Dns, NetworkUnreachable, PortClosed, Timeout, Authentication, CredentialConflict,
    ShareMissing, AccessDenied, PortUnsupported, ElevationRequired, SecurityPolicy, ApplicationControl, Cancelled, SourceChanged, Unknown }
public sealed record Diagnostic(string Stage, FailureKind Kind, string Message)
{
    public bool Success => Kind == FailureKind.None;
}
public sealed class StorageException(FailureKind kind, string message) : IOException(message)
{
    public FailureKind Kind { get; } = kind;
}
public sealed record RemoteEntry(string Id, Guid ConnectionId, string RelativePath, bool IsDirectory,
    long Size, long CreationTime, long ModifiedTime, string? RemoteId, uint Attributes = 0,
    bool CreationTimeReliable = true, bool ModifiedTimeReliable = true)
{
    public bool SameVersion(RemoteEntry other) => IsDirectory == other.IsDirectory && Size == other.Size &&
        CreationTime == other.CreationTime && ModifiedTime == other.ModifiedTime && RemoteId == other.RemoteId;
}
// Future replacement boundary. The phase-3 first slice uses NewFileUpload's create-only journal;
// versioned replacement of existing files remains unavailable until the NAS gate passes.
public interface IRemoteWriter
{
    Task UploadAsync(RemoteEntry expectedVersion, Stream content, CancellationToken token);
}
public static class FailureClassifier
{
    public static FailureKind FromCode(int code) => code switch
    {
        5 or 65 => FailureKind.AccessDenied,
        86 or 1326 or 1330 or 1331 or 1909 => FailureKind.Authentication,
        1219 => FailureKind.CredentialConflict,
        4551 => FailureKind.ApplicationControl,
        2 or 3 or 67 => FailureKind.ShareMissing,
        53 or 64 or 1231 or 1232 => FailureKind.NetworkUnreachable,
        10061 => FailureKind.PortClosed,
        121 or 1460 or 10060 => FailureKind.Timeout,
        _ => FailureKind.Unknown
    };
    public static Diagnostic Describe(string stage, Exception error)
    {
        var kind = error switch
        {
            StorageException s => s.Kind,
            OperationCanceledException => FailureKind.Cancelled,
            UnauthorizedAccessException => FailureKind.AccessDenied,
            System.Net.Sockets.SocketException s => FromCode(s.ErrorCode),
            System.ComponentModel.Win32Exception s => FromCode(s.NativeErrorCode),
            _ => FromCode(error.HResult & 0xFFFF)
        };
        // Do not echo provider exception text: PowerShell error records can contain bound credentials.
        return new(stage, kind, kind switch
        {
            FailureKind.Authentication => "账号或密码认证失败，或账号已过期/锁定。",
            FailureKind.CredentialConflict => "Windows 已有同一服务器的其他凭据会话（1219）；请处理该会话，本软件不会断开它。",
            FailureKind.ShareMissing => "共享或子目录不存在。",
            FailureKind.AccessDenied => "权限不足。",
            FailureKind.NetworkUnreachable => "网络不可达或 SMB 会话已断开。",
            FailureKind.PortClosed => "TCP 端口拒绝连接。",
            FailureKind.Timeout => "连接或读取超时。",
            FailureKind.Cancelled => "操作已取消。",
            FailureKind.SourceChanged => "远端文件版本已改变；保留本地内容并拒绝混合版本。",
            FailureKind.PortUnsupported => "此系统或 New-SmbMapping 不支持自定义 TCP 端口。",
            FailureKind.ElevationRequired => "自定义端口需要通过 UAC 启动此连接的专用 Agent。",
            FailureKind.SecurityPolicy => "Windows PowerShell 有效执行策略阻止加载系统 SmbShare 模块；本软件不会更改或绕过该策略。",
            FailureKind.ApplicationControl => "Windows 应用控制策略阻止运行此程序或程序集（4551）；请在受信任的部署/签名环境验收。",
            FailureKind.Dns => "DNS 解析失败。",
            _ => "SMB 操作失败；错误代码 " + (error.HResult & 0xFFFF)
        });
    }
}
