using CloudDrive.Storage;
using System.Diagnostics;
using System.Text.Json;

if (args.Length != 1) throw new ArgumentException("需要已保存的测试专用 NAS 连接 GUID；密码只能来自凭据管理器。");
Guid id = Guid.Parse(args[0]); var index = new IndexStore(Path.Combine(IndexStore.DefaultDirectory, "index.db"));
var config = index.Get(id) ?? throw new ArgumentException("连接不存在。");
string nativeDirectory = Path.GetFullPath("artifacts/bin"); var runtime = new AgentRuntime(index, new CredentialVault(), nativeDirectory);
using (ConnectionLock.Acquire(id, "agent")) { } // Never compete with an already running Agent.
if (index.SyncTasks(id, 1000).Any(t => t.TaskStatus is not (SyncTaskStatus.Completed or SyncTaskStatus.Cancelled)))
    throw new ArgumentException("测试连接已有未完成任务，请先处理；不会替你执行历史任务。");
if (Directory.Exists(config.SyncRoot))
{
    var pending = new Stack<string>(); pending.Push(config.SyncRoot);
    while (pending.TryPop(out var directory))
    {
        foreach (var item in new DirectoryInfo(directory).EnumerateFileSystemInfos("*", new EnumerationOptions { AttributesToSkip = 0, IgnoreInaccessible = false }))
        {
            string relative = Paths.Relative(Path.GetRelativePath(config.SyncRoot, item.FullName));
            var entry = index.FindPath(id, relative);
            if (entry is null) throw new ArgumentException("本地根包含未索引项目，请使用空同步根的测试专用连接，避免上传已有用户文件。");
            if ((item.Attributes & FileAttributes.Directory) != 0)
            {
                if ((item.Attributes & FileAttributes.ReparsePoint) != 0 && !CfNewFileFinalizer.IsOwnedDirectory(config, item.FullName)) throw new IOException("本地未知重解析目录。");
                pending.Push(item.FullName);
            }
        }
    }
}
using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(5));
var diagnostics = await runtime.Tester.TestAsync(config, true, timeout.Token);
foreach (var diagnostic in diagnostics) Console.WriteLine(diagnostic.Stage + ": " + diagnostic.Kind + " " + diagnostic.Message);
if (diagnostics.Any(d => !d.Success)) throw new IOException("NAS 分阶段诊断未通过；未启动新增上传。");
await runtime.ScanAsync(config, timeout.Token);
var previous = index.SyncSettings(id); Task<int>? agent = null;
try
{
    index.SetSyncSettings(id, previous with { Enabled = true });
    agent = Task.Run(() => runtime.RunAsync(config, timeout.Token), timeout.Token);
    var finalizer = new CfNewFileFinalizer();
    while (true)
    {
        if (agent.IsCompleted) throw new IOException("Agent 提前结束。exit=" + await agent);
        try { finalizer.ValidateRoot(config); break; } catch (Exception e) when (e is System.Runtime.InteropServices.COMException or IOException) { }
        await Task.Delay(500, timeout.Token);
    }
    string relative = "新增上传验收 # & '-" + Guid.NewGuid().ToString("N") + ".txt";
    await File.WriteAllTextAsync(Paths.Under(config.SyncRoot, relative), "CloudDrive Lite real NAS upload " + Guid.NewGuid() + "\n中文内容", timeout.Token);
    SyncTask task;
    while (true)
    {
        if (agent.IsCompleted) throw new IOException("Agent 提前结束。exit=" + await agent);
        var found = index.SyncTasks(id).FirstOrDefault(t => t.SourcePath == relative);
        if (found?.TaskStatus == SyncTaskStatus.Completed) { task = found; break; }
        if (found?.TaskStatus == SyncTaskStatus.Conflict) throw new IOException(found.ErrorMessage);
        await Task.Delay(500, timeout.Token);
    }
    string root = await runtime.Mapper.ConnectAsync(config, timeout.Token);
    var entry = index.FindPath(id, relative) ?? throw new IOException("完成后缺少映射。");
    await using (var remote = await new FileSystemSource(id, root).OpenReadAsync(entry, timeout.Token))
    {
        string actual = await NewFileUpload.StreamHash(remote, timeout.Token);
        if (actual != task.LocalHash) throw new IOException("NAS 最终目标 SHA256 不一致。");
        Console.WriteLine("PASS real NAS final size/hash " + entry.Size + " " + actual);
    }
    var start = new ProcessStartInfo(Path.Combine(nativeDirectory, "CloudProviderHost.exe")) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
    foreach (string arg in new[] { "status", "--root", config.SyncRoot, "--path", relative }) start.ArgumentList.Add(arg);
    using var status = Process.Start(start) ?? throw new IOException("状态程序启动失败。");
    string output = await status.StandardOutput.ReadToEndAsync(timeout.Token); string errors = await status.StandardError.ReadToEndAsync(timeout.Token); await status.WaitForExitAsync(timeout.Token);
    using var json = JsonDocument.Parse(output.Trim());
    if (status.ExitCode != 0 || !json.RootElement.GetProperty("inSync").GetBoolean()) throw new IOException("CFAPI InSync 未通过。" + errors);
    Console.WriteLine("PASS real CFAPI InSync after new upload; task=" + task.Id);
    Console.WriteLine("Test file RETAINED locally and remotely: " + relative);
}
finally
{
    index.SetSyncSettings(id, previous); AgentRuntime.Stop(id); await timeout.CancelAsync();
    if (agent is not null) { try { await agent.WaitAsync(TimeSpan.FromSeconds(30)); } catch (OperationCanceledException) { } }
    foreach (string log in index.Logs().Reverse()) Console.WriteLine(log);
}
