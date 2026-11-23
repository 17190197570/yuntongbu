using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using CloudDrive.Core;

return await Agent.RunAsync(args);

[SupportedOSPlatform("windows")]
internal static class Agent
{
    public static async Task<int> RunAsync(string[] args)
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041))
        {
            Console.Error.WriteLine("需要 Windows 10 2004 或更新版本，且同步根位于本地 NTFS 磁盘。");
            return 2;
        }
        try
        {
            if (args is ["product-daemon"])
            {
                if (File.Exists(CloudDrive.Storage.ProductMaintenance.Marker)) throw new IOException("安装维护尚未完成，暂停后台启动；请查看恢复指南。");
                return await new ProductDaemon().RunAsync();
            }
            if (args is ["product-status"] or ["product-stop"])
            {
                var reply = await CloudDrive.Storage.ManagementClient.SendAsync(new(args[0] == "product-status" ? "status" : "shutdown"));
                Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(reply)); return reply.Success ? 0 : 1;
            }
            if (args.Length > 0 && args[0].StartsWith("product-", StringComparison.Ordinal)) return await ProductCommands.Run(args);
            if (args.Length > 0 && args[0].StartsWith("smb-", StringComparison.Ordinal)) return await SmbCommands.RunAsync(args);
            ProviderCommand command = ProviderCommand.Parse(args);
            if (command.Verb == "help") { PrintHelp(); return 0; }
            if (command.Verb == "doctor") return Doctor();

            string nativePath = Path.Combine(AppContext.BaseDirectory, "CloudProviderHost.exe");
            if (!File.Exists(nativePath))
                throw new FileNotFoundException("未找到 CloudProviderHost.exe。请先运行 scripts/build.ps1。", nativePath);
            // Named events keep control messages separate from paths and command text.
            string? stopName = null;
            using var providerStopEvent = command.Verb == "run"
                ? CreateStopEvent(out stopName)
                : null;
            var start = new ProcessStartInfo(nativePath) { UseShellExecute = false };
            foreach (string argument in command.NativeArguments()) start.ArgumentList.Add(argument);
            if (stopName is not null)
            {
                start.ArgumentList.Add("--stop-event");
                start.ArgumentList.Add(stopName);
            }
            using var provider = new Process { StartInfo = start };
            ConsoleCancelEventHandler cancel = (_, eventArgs) =>
            {
                eventArgs.Cancel = true;
                providerStopEvent?.Set();
            };
            Console.CancelKeyPress += cancel;
            try
            {
                if (!provider.Start()) throw new IOException("无法启动 CFAPI Host。");
                await provider.WaitForExitAsync();
                return provider.ExitCode;
            }
            finally { Console.CancelKeyPress -= cancel; }
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            Console.Error.WriteLine(exception.Message);
            return 2;
        }
    }

    private static EventWaitHandle CreateStopEvent(out string name)
    {
        name = @"Local\CloudDriveLite.Stop." + Guid.NewGuid().ToString("N");
        return new(false, EventResetMode.ManualReset, name);
    }

    private static int Doctor()
    {
        Console.WriteLine($"OS: {RuntimeInformation.OSDescription}");
        Console.WriteLine($"Agent: {RuntimeInformation.FrameworkDescription} / {RuntimeInformation.ProcessArchitecture}");
        string path = Path.Combine(AppContext.BaseDirectory, "CloudProviderHost.exe");
        Console.WriteLine($"Native host: {(File.Exists(path) ? path : "MISSING — run scripts/build.ps1")}");
        bool hasCfapi = NativeLibrary.TryLoad("cldapi.dll", out nint handle);
        if (hasCfapi) NativeLibrary.Free(handle);
        Console.WriteLine($"Cloud Files API: {(hasCfapi ? "available" : "MISSING")}");
        Console.WriteLine("此检查只确认环境；实际 CFAPI 注册和水合请运行 scripts/test-integration.ps1。");
        return hasCfapi && File.Exists(path) ? 0 : 1;
    }

    private static void PrintHelp() => Console.WriteLine("""
        CloudDrive Lite — CFAPI 本地模拟原型（第 1 阶段）

        CloudDrive.Agent run --source <绝对路径> --root <绝对路径>
        CloudDrive.Agent status --root <绝对路径> --path <相对文件路径>
        CloudDrive.Agent release --root <绝对路径> --path <相对文件路径>
        CloudDrive.Agent unregister --root <绝对路径>
        CloudDrive.Agent doctor
        CloudDrive.Agent smb-ui
        CloudDrive.Agent smb-add --config <JSON文件> （在控制台安全输入密码）
        CloudDrive.Agent smb-list
        CloudDrive.Agent smb-test <连接GUID> [--write-probe]
        CloudDrive.Agent smb-scan <连接GUID>
        CloudDrive.Agent smb-run <连接GUID>
        CloudDrive.Agent smb-auto
        CloudDrive.Agent smb-stop <连接GUID>
        CloudDrive.Agent smb-release <连接GUID> <相对路径>
        CloudDrive.Agent smb-sync-new <连接GUID> on|off （新增上传试验，默认关闭）
        CloudDrive.Agent smb-sync-tasks <连接GUID>
        CloudDrive.Agent smb-sync-task <连接GUID> <任务GUID> retry|resume|pause|cancel
        CloudDrive.Agent smb-cache-refresh
        CloudDrive.Agent smb-cache-status <连接GUID>
        CloudDrive.Agent smb-cache-clean <连接GUID> [相对文件或文件夹]
        CloudDrive.Agent smb-cache-pin <连接GUID> <相对文件或文件夹>
        CloudDrive.Agent smb-cache-unpin <连接GUID> <相对文件或文件夹>
        CloudDrive.Agent smb-cache-policy <连接GUID> [--config <策略JSON文件>]
        CloudDrive.Agent smb-cache-runs
        CloudDrive.Agent smb-cache-run <清理任务GUID> pause|resume|cancel
        CloudDrive.Agent smb-cache-watch
        CloudDrive.Agent smb-archive-list
        CloudDrive.Agent smb-archive-policy --config <归档JSON文件>
        CloudDrive.Agent smb-archive-preview <归档配置GUID>
        CloudDrive.Agent smb-archive-start <归档配置GUID>
        CloudDrive.Agent smb-archive-history
        CloudDrive.Agent smb-archive-run <归档执行GUID> pause|resume|retry|cancel
        CloudDrive.Agent smb-archive-delete <归档配置GUID>
        CloudDrive.Agent smb-archive-global pause|resume
        CloudDrive.Agent smb-archive-watch

        首次启动使用空的 NTFS 同步根。模拟源只读；此阶段不支持修改同步。
        run 必须保持运行，Ctrl+C 安全断开提供程序；同步根注册和占位文件会保留。
        release 仅处理经验证与源一致的已水合文件；不会修改模拟远端。
        unregister 仅支持已停止且为空的本软件同步根；请自行保存本地修改并清空本地项。
        """);
}
