using System.ComponentModel;
using System.Management.Automation;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security;
using System.Security.Principal;
using System.Text.RegularExpressions;

namespace CloudDrive.Storage;

// All cmdlets use typed PSRP parameter binding to the system Windows PowerShell host.
// No script interpolation or secrets on a child process command line.
public sealed class SmbMapper(IndexStore index, ICredentialVault vault)
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private static readonly string Session = SessionIdentity();
    public static string? OwnedRoot(IndexStore index, SmbConnection config)
    {
        var lease = index.Lease(config.Id);
        return lease is not null && lease.Value.Session == Session && lease.Value.Remote.Equals(config.RemoteShare, StringComparison.OrdinalIgnoreCase)
            ? BasePath(lease.Value.Local, config.SubPath) : null;
    }
    public static async Task<bool> VerifyOwnedMappingAsync(IndexStore index, SmbConnection config, CancellationToken token)
    {
        var lease = index.Lease(config.Id);
        if (lease is null || lease.Value.Session != Session || !lease.Value.Remote.Equals(config.RemoteShare, StringComparison.OrdinalIgnoreCase)) return false;
        using var session = await WindowsSmbSession.OpenAsync(token);
        using var ps = PowerShell.Create(); ps.Runspace = session.Runspace;
        await Invoke(ps, "Import-Module", [("Name", Module)], token);
        var mappings = await Invoke(ps, "Get-SmbMapping", [], token);
        return mappings.Any(m => Eq(m, "LocalPath", lease.Value.Local) && Eq(m, "RemotePath", lease.Value.Remote));
    }
    public static bool Elevated => new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);
    private static string Module => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "WindowsPowerShell", "v1.0", "Modules", "SmbShare", "SmbShare.psd1");
    internal static bool HasParameter(PSObject command, string name)
        => command.Properties["Parameters"]?.Value is { } value &&
            PSObject.AsPSObject(value).BaseObject is System.Collections.IDictionary parameters && parameters.Contains(name);
    public static async Task<(bool TcpPort, bool Credential)> CapabilitiesAsync(CancellationToken token)
    {
        using var session = await WindowsSmbSession.OpenAsync(token);
        using var ps = PowerShell.Create(); ps.Runspace = session.Runspace;
        await Invoke(ps, "Import-Module", [("Name", Module)], token);
        var command = await Invoke(ps, "Get-Command", [("Name", "New-SmbMapping")], token);
        var info = command.Single();
        return (HasParameter(info, "TcpPort"), HasParameter(info, "Credential"));
    }
    public async Task<string> ConnectAsync(SmbConnection config, CancellationToken token)
    {
        config.Validate(requireRoot: false);
        if (config.Port != 445 && !OperatingSystem.IsWindowsVersionAtLeast(10, 0, 26100))
            throw new StorageException(FailureKind.PortUnsupported, "Unsupported OS custom port.");
        await gate.WaitAsync(token);
        try
        {
            using var session = await WindowsSmbSession.OpenAsync(token);
            using var ps = PowerShell.Create(); ps.Runspace = session.Runspace;
            await Invoke(ps, "Import-Module", [("Name", Module)], token);
            var command = await Invoke(ps, "Get-Command", [("Name", "New-SmbMapping")], token);
            var info = command.Single();
            if (config.Port != 445)
            {
                if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 26100) || !HasParameter(info, "TcpPort"))
                    throw new StorageException(FailureKind.PortUnsupported, "Unsupported custom port.");
                if (!Elevated) throw new StorageException(FailureKind.ElevationRequired, "Elevation required.");
            }
            var mappings = await Invoke(ps, "Get-SmbMapping", [], token);
            var lease = index.Lease(config.Id);
            if (lease is not null && lease.Value.Session == Session)
            {
                var owned = mappings.FirstOrDefault(m => Eq(m, "LocalPath", lease.Value.Local) && Eq(m, "RemotePath", config.RemoteShare));
                if (owned is not null && lease.Value.Remote.Equals(config.RemoteShare, StringComparison.OrdinalIgnoreCase))
                    return BasePath(lease.Value.Local, config.SubPath);
            }
            // Do not delete/reuse an unowned existing mapping, even to the same UNC.
            var occupied = mappings.Select(m => (string?)m.Properties["LocalPath"]?.Value).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var drives = DriveInfo.GetDrives().Select(d => d.Name[..2]).ToHashSet(StringComparer.OrdinalIgnoreCase);
            string? local = Enumerable.Range('D', 'Z' - 'D' + 1).Reverse().Select(c => ((char)c) + ":").FirstOrDefault(p => !occupied.Contains(p) && !drives.Contains(p));
            if (local is null) throw new IOException("没有可分配的独立 SMB 盘符。");
            using var secret = vault.Read(config.CredentialKey);
            var parameters = new List<(string, object?)> { ("LocalPath", local), ("RemotePath", config.RemoteShare), ("Persistent", false), ("SaveCredentials", false) };
            if (HasParameter(info, "Credential")) parameters.Add(("Credential", new PSCredential(config.Account, secret)));
            else { parameters.Add(("UserName", config.Account)); parameters.Add(("Password", new NetworkCredential("", secret).Password)); }
            if (config.Port != 445) parameters.Add(("TcpPort", (ushort)config.Port));
            // Only a confirmed successful creation confers ownership. An unjournaled orphan after a
            // process crash is retained rather than risking removal of another application's mapping.
            try { await Invoke(ps, "New-SmbMapping", parameters, token); index.SetLease(config.Id, Session, local, config.RemoteShare); }
            finally { parameters.Clear(); ps.Commands.Clear(); }
            index.Log(config.Id, "connection", "SMB 映射建立（当前用户令牌）。");
            return BasePath(local, config.SubPath);
        }
        finally { gate.Release(); }
    }
    private static string BasePath(string local, string sub) => local + "\\" + sub;
    private static bool Eq(PSObject obj, string name, string value) => string.Equals(obj.Properties[name]?.Value?.ToString(), value, StringComparison.OrdinalIgnoreCase);
    public async Task DisconnectOwnedAsync(SmbConnection config, CancellationToken token)
    {
        await gate.WaitAsync(token);
        try
        {
            var lease = index.Lease(config.Id);
            if (lease is null || lease.Value.Session != Session) return;
            using var session = await WindowsSmbSession.OpenAsync(token);
            using var ps = PowerShell.Create(); ps.Runspace = session.Runspace;
            await Invoke(ps, "Import-Module", [("Name", Module)], token);
            var mappings = await Invoke(ps, "Get-SmbMapping", [], token);
            if (mappings.Any(m => Eq(m, "LocalPath", lease.Value.Local) && Eq(m, "RemotePath", lease.Value.Remote)))
                await Invoke(ps, "Remove-SmbMapping", [("LocalPath", lease.Value.Local), ("Force", true), ("Confirm", false)], token);
            index.ClearLease(config.Id);
        }
        finally { gate.Release(); }
    }
    internal static async Task<System.Collections.ObjectModel.Collection<PSObject>> Invoke(PowerShell ps, string command, IEnumerable<(string, object?)> parameters, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        ps.Commands.Clear(); ps.Streams.Error.Clear(); ps.AddCommand(command);
        foreach (var (name, value) in parameters) ps.AddParameter(name, value);
        ps.AddParameter("ErrorAction", ActionPreference.Stop);
        using var cancellation = token.Register(() => { try { ps.Stop(); } catch (ObjectDisposedException) { } });
        try
        {
            var result = await Task.Run(ps.Invoke, CancellationToken.None); token.ThrowIfCancellationRequested();
            if (ps.HadErrors) throw new RuntimeException("SMB cmdlet failed."); return result;
        }
        catch (Exception error) when (error is RuntimeException or PipelineStoppedException)
        {
            token.ThrowIfCancellationRequested();
            if (command is "Import-Module" or "Get-Command")
                throw new StorageException(error.ToString().Contains("PSSecurityException", StringComparison.Ordinal) ||
                    error.Message.Contains("running scripts is disabled", StringComparison.Ordinal) ||
                    ps.Streams.Error.Any(e => e.CategoryInfo.Category == ErrorCategory.SecurityError)
                    ? FailureKind.SecurityPolicy : FailureKind.Unknown, "System SmbShare module loading was rejected.");
            var text = error.ToString() + " " + string.Join(" ", ps.Streams.Error.Select(e => e.ToString()));
            var code = error.HResult & 0xFFFF;
            // Parse native numeric status only; never propagate an error record or bound argument.
            var match = Regex.Match(text, @"(?i)(?:Windows System Error|System Error|系统错误|错误)\s*[:：]?\s*(\d{1,5})\b");
            if (match.Success) code = int.Parse(match.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
            else foreach (var candidate in new[] { 1219, 1326, 1330, 1331, 1909, 86, 67, 53, 5 })
                if (Regex.IsMatch(text, @"\b" + candidate + @"\b")) { code = candidate; break; }
            throw new StorageException(FailureClassifier.FromCode(code), "Windows SMB status " + code);
        }
    }
    private static string SessionIdentity()
    {
        using var identity = WindowsIdentity.GetCurrent();
        var buffer = Marshal.AllocHGlobal(256);
        try
        {
            if (!GetTokenInformation(identity.AccessToken.DangerousGetHandle(), 10, buffer, 256, out _)) throw new Win32Exception(Marshal.GetLastWin32Error());
            // TOKEN_STATISTICS.AuthenticationId follows TokenId (LUID).
            return identity.User!.Value + "/" + Marshal.ReadInt64(buffer, 8).ToString("X", System.Globalization.CultureInfo.InvariantCulture);
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }
    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetTokenInformation(nint token, int type, nint buffer, int length, out int needed);
}

public sealed class ConnectionTester(SmbMapper mapper)
{
    public async Task<IReadOnlyList<Diagnostic>> TestAsync(SmbConnection config, bool writeProbe, CancellationToken token)
    {
        config.Validate(requireRoot: false); var results = new List<Diagnostic>(); string stage = "DNS/IP";
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token); timeout.CancelAfter(TimeSpan.FromSeconds(30));
        try
        {
            IPAddress[] addresses;
            try { addresses = await Dns.GetHostAddressesAsync(config.Server, timeout.Token); }
            catch (SocketException) { throw new StorageException(FailureKind.Dns, "DNS failure."); }
            if (addresses.Length == 0) throw new StorageException(FailureKind.Dns, "DNS empty.");
            results.Add(new(stage, FailureKind.None, "地址解析成功。")); stage = "TCP";
            using (var socket = new TcpClient()) await socket.ConnectAsync(config.Server, config.Port, timeout.Token);
            results.Add(new(stage, FailureKind.None, "TCP 端口可达。")); stage = "SMB/认证/共享";
            string root = await mapper.ConnectAsync(config, timeout.Token);
            results.Add(new(stage, FailureKind.None, "Windows SMB 映射与账号认证成功。")); stage = "子目录访问/列表";
            await Task.Run(() =>
            {
                FileSystemSource.CheckChain(root);
                using var files = Directory.EnumerateFileSystemEntries(root).GetEnumerator(); _ = files.MoveNext();
            }, timeout.Token);
            results.Add(new(stage, FailureKind.None, "共享目录可访问，目录列表可读取。"));
            if (writeProbe)
            {
                stage = "可选写入探测";
                string probe = Path.Combine(root, ".clouddrive-probe-" + Guid.NewGuid().ToString("N"));
                await using var file = new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.DeleteOnClose | FileOptions.Asynchronous);
                await file.WriteAsync(new byte[] { 0x43 }, timeout.Token); await file.FlushAsync(timeout.Token);
                results.Add(new(stage, FailureKind.None, "临时探测文件写入成功并将在关闭时删除；产品仍为只读模式。"));
            }
        }
        catch (Exception error)
        {
            if (error is OperationCanceledException && !token.IsCancellationRequested) error = new StorageException(FailureKind.Timeout, "Timeout.");
            results.Add(FailureClassifier.Describe(stage, error));
        }
        return results;
    }
}
