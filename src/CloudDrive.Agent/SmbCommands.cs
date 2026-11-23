using CloudDrive.Storage;
using System.Diagnostics;
using System.Security;
using System.Security.Principal;
using System.Text.Json;

internal static class SmbCommands
{
    public static async Task<int> RunAsync(string[] args)
    {
        var index = new IndexStore(ProductEnvironment.DatabasePath);
        var vault = new CredentialVault(); var runtime = new AgentRuntime(index, vault);
        using var stop = new CancellationTokenSource();
        ConsoleCancelEventHandler handler = (_, e) => { e.Cancel = true; stop.Cancel(); };
        Console.CancelKeyPress += handler;
        try
        {
            if (args.Length > 0 && args[0].StartsWith("smb-archive-", StringComparison.Ordinal)) return await ArchiveCommands.RunAsync(args, index, runtime, stop.Token);
            if (args is ["smb-ui"])
            {
                string dotnet = Path.GetFullPath(Path.Combine(System.Runtime.InteropServices.RuntimeEnvironment.GetRuntimeDirectory(), "../../..", "dotnet.exe"));
                var start = new ProcessStartInfo(dotnet) { UseShellExecute = false, CreateNoWindow = true };
                start.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "CloudDrive.Desktop.dll"));
                using var process = Process.Start(start) ?? throw new IOException("界面启动失败。"); return 0;
            }
            if (args is ["smb-list"])
            {
                foreach (var c in index.Connections()) Console.WriteLine($"{c.Id} {c.Name} {c.RemoteShare} TCP/{c.Port} {index.RuntimeStatus(c.Id)} 索引 {index.Count(c.Id)} 扫描 {index.ScanStatus(c.Id)}"); return 0;
            }
            if (args is ["smb-add", "--config", var path])
            {
                var config = JsonSerializer.Deserialize<SmbConnection>(await File.ReadAllTextAsync(path, stop.Token))?.Validate() ?? throw new ArgumentException("无效配置 JSON。");
                if (config.StorageId is not null) throw new ArgumentException("共用NAS的映射请通过文件夹映射页面维护，不在CLI重写共享凭据。");
                index.ValidateSave(config);
                if (index.Get(config.Id) is not null) throw new ArgumentException("CLI 新增仅接受新 GUID；编辑请使用界面并先停止连接。");
                Console.Write("请输入密码（不会回显或出现在进程参数中）："); using var password = new SecureString();
                while (true) { var key = Console.ReadKey(true); if (key.Key == ConsoleKey.Enter) break; if (key.Key == ConsoleKey.Backspace) { if (password.Length > 0) password.RemoveAt(password.Length - 1); } else if (!char.IsControl(key.KeyChar)) password.AppendChar(key.KeyChar); }
                Console.WriteLine(); vault.Save(config.CredentialKey, config.Account, password); index.Save(config); Console.WriteLine("连接已保存。"); return 0;
            }
            if (args is ["smb-auto"])
            {
                var connections = index.Connections().Where(c => c.AutoConnect).ToArray();
                if (connections.Length == 0) { Console.WriteLine("没有启用自动连接的配置。"); return 0; }
                var tasks = connections.Select(async c =>
                {
                    if (c.Port != 445 && !SmbMapper.Elevated) { index.Log(c.Id, "elevation-required", "自动连接需要用户在界面通过 UAC 启动专用 Agent。"); return 2; }
                    try { return await runtime.RunAsync(c, stop.Token); }
                    catch (Exception e) { index.Log(c.Id, "agent-failure", FailureClassifier.Describe("agent", e).Message); return 2; }
                });
                return (await Task.WhenAll(tasks)).Any(code => code != 0) ? 2 : 0;
            }
            if (args is ["smb-cache-watch"]) { await runtime.Cache.RunBackgroundAsync(stop.Token); return 0; }
            if (args is ["smb-cache-refresh"]) { await runtime.Cache.CalibrateAsync(stop.Token); Console.WriteLine("已按实际本地元数据校准缓存。"); return 0; }
            if (args is ["smb-cache-runs"]) { Console.WriteLine(JsonSerializer.Serialize(index.CacheRuns(), new JsonSerializerOptions { WriteIndented = true })); return 0; }
            if (args is ["smb-cache-run", var runId, var runAction])
            {
                var run = Guid.Parse(runId);
                index.ControlCacheRun(run, runAction switch { "pause" => "Paused", "resume" => "Pending", "cancel" => "Cancelled", _ => throw new ArgumentException("使用pause/resume/cancel") });
                if (runAction == "resume") await runtime.Cache.ExecuteAsync(run, stop.Token); return 0;
            }
            if (args.Length < 2 || !Guid.TryParse(args[1], out var id)) throw new ArgumentException("需要有效的连接 GUID。");
            var connection = index.Get(id) ?? throw new ArgumentException("连接不存在。");
            if (args is ["smb-cache-policy", _]) { Console.WriteLine(JsonSerializer.Serialize(index.CachePolicy(id), new JsonSerializerOptions { WriteIndented = true })); return 0; }
            if (args is ["smb-cache-policy", _, "--config", var policyPath])
            { index.SetCachePolicy(id, JsonSerializer.Deserialize<CachePolicy>(await File.ReadAllTextAsync(policyPath, stop.Token)) ?? throw new ArgumentException("无效缓存策略")); return 0; }
            if (args is ["smb-cache-status", _])
            { Console.WriteLine(JsonSerializer.Serialize(index.CacheSnapshots(id).Select(index.DecorateCache), new JsonSerializerOptions { WriteIndented = true })); return 0; }
            if (args is ["smb-cache-clean", _] or ["smb-cache-clean", _, _] or ["smb-cache-pin", _, _] or ["smb-cache-unpin", _, _])
            {
                string type = args[0] switch { "smb-cache-pin" => "Pin", "smb-cache-unpin" => "Unpin", _ => "Release" };
                Guid run = runtime.Cache.QueueManual(id, args.Length == 3 ? args[2] : "", type, true);
                await runtime.Cache.ExecuteAsync(run, stop.Token); Console.WriteLine(JsonSerializer.Serialize(index.CacheOperations(run), new JsonSerializerOptions { WriteIndented = true }));
                return index.CacheOperations(run).Any(o => o.Status == "Failed") ? 1 : 0;
            }
            if (args is ["smb-sync-new", _, "on"] or ["smb-sync-new", _, "off"])
            {
                index.SetSyncSettings(id, index.SyncSettings(id) with { Enabled = args[2] == "on" });
                Console.WriteLine(args[2] == "on" ? "已启用新增文件上传试验；仅用于可写 NAS 测试目录，修改/重命名/删除上传尚未实现。" : "已暂停认领新上传；执行中的任务将安全完成。"); return 0;
            }
            if (args is ["smb-sync-tasks", _])
            {
                foreach (var task in index.SyncTasks(id)) Console.WriteLine($"{task.Id} {task.TaskStatus} phase={task.Phase} retry={task.RetryCount} bytes={task.TransferredBytes} {task.SourcePath} {task.ErrorMessage}"); return 0;
            }
            if (args is ["smb-sync-task", _, var taskId, var action])
            {
                var state = action switch { "retry" or "resume" => SyncTaskStatus.Pending, "pause" => SyncTaskStatus.Paused, "cancel" => SyncTaskStatus.Cancelled, _ => throw new ArgumentException("任务操作须为retry/resume/pause/cancel。") };
                index.ControlSyncTask(id, Guid.Parse(taskId), state); return 0;
            }
            if (args is ["smb-stop", _]) { AgentRuntime.Stop(id); return 0; }
            if (args is ["smb-test", _] or ["smb-test", _, "--write-probe"])
            {
                var diagnostics = await runtime.Tester.TestAsync(connection, args.Length == 3, stop.Token);
                foreach (var result in diagnostics) { Console.WriteLine($"{result.Stage}: {result.Kind} — {result.Message}"); index.Log(id, "diagnostic", result.Stage + ": " + result.Kind + " " + result.Message); }
                return diagnostics.All(d => d.Success) ? 0 : 1;
            }
            if (args is ["smb-scan", _]) { await runtime.ScanAsync(connection, stop.Token); return 0; }
            if (args is ["smb-run", _]) return await runtime.RunAsync(connection, stop.Token);
            if (args is ["smb-run-elevated", _, var sid])
            {
                if (!SmbMapper.Elevated || sid != WindowsIdentity.GetCurrent().User?.Value) throw new ArgumentException("提权令牌必须属于发起连接的同一用户。");
                var diagnostics = await runtime.Tester.TestAsync(connection, false, stop.Token);
                foreach (var d in diagnostics) index.Log(id, "elevated-diagnostic", d.Stage + ": " + d.Kind + " " + d.Message);
                if (diagnostics.Any(d => !d.Success)) return 1;
                return await runtime.RunAsync(connection, stop.Token);
            }
            if (args is ["smb-release", _, var relative]) return await runtime.ReleaseAsync(connection, relative, stop.Token);
            if (args is ["smb-delete", _])
            {
                await runtime.DisconnectOwnedAsync(connection, stop.Token); index.Delete(id); Console.WriteLine("连接配置已删除；同步根与缓存保留。"); return 0;
            }
            throw new ArgumentException("SMB 命令或参数无效。使用 --help 查看支持的命令。");
        }
        catch (OperationCanceledException) { return 130; }
        catch (Exception error) when (error is not ArgumentException)
        {
            var diagnostic = FailureClassifier.Describe("SMB", error); Console.Error.WriteLine(diagnostic.Message); return 2;
        }
        finally { Console.CancelKeyPress -= handler; }
    }
}
