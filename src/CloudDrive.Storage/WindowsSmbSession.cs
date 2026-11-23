using System.Diagnostics;
using System.Management.Automation.Runspaces;

namespace CloudDrive.Storage;

// Use the Windows host that ships with SmbShare. The embedded PS Core host has its own
// configuration/policy context and is not interchangeable with Windows PowerShell.
// PSRP carries typed parameters over redirected local pipes, never command-line secrets.
internal sealed class WindowsSmbSession : IDisposable
{
    private readonly PowerShellProcessInstance process;
    private int disposed;
    internal Runspace Runspace { get; }
    internal Process HostProcess => process.Process;

    private WindowsSmbSession()
    {
        process = new PowerShellProcessInstance(new Version(5, 1), null, null, false);
        try
        {
            string expected = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe");
            if (!Path.GetFullPath(process.Process.StartInfo.FileName).Equals(expected, StringComparison.OrdinalIgnoreCase))
                throw new IOException("Windows SMB PowerShell host path is invalid.");
            // No alternate executable, startup script, credentials, policy switch or runas.
            Runspace = RunspaceFactory.CreateOutOfProcessRunspace(null, process);
            Runspace.ConnectionInfo.OpenTimeout = 10000;
        }
        catch { process.Dispose(); throw; }
    }

    internal static async Task<WindowsSmbSession> OpenAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var session = new WindowsSmbSession();
        try
        {
            // During startup only, cancellation terminates this owned helper, never another
            // PowerShell instance or an Agent/CFAPI worker. Invocations use PowerShell.Stop.
            using var cancellation = token.Register(() => session.process.Dispose());
            await Task.Run(session.Runspace.Open, CancellationToken.None);
            token.ThrowIfCancellationRequested();
            return session;
        }
        catch
        {
            session.Dispose();
            token.ThrowIfCancellationRequested();
            throw;
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        try { Runspace.Dispose(); }
        finally { process.Dispose(); }
    }
}
