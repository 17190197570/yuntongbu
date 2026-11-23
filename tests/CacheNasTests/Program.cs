using CloudDrive.Storage;

if (args.Length != 2) throw new ArgumentException("需要已保存的测试专用 NAS 连接 GUID 及共享中已存在的测试文件相对路径；密码仅来自凭据管理器。");
Guid id = Guid.Parse(args[0]); string relative = Paths.Relative(args[1]);
var index = new IndexStore(Path.Combine(IndexStore.DefaultDirectory, "index.db")); var c = index.Get(id) ?? throw new ArgumentException("连接不存在");
using (ConnectionLock.Acquire(id, "agent")) { }
if (index.SyncSettings(id).Enabled || index.SyncTasks(id, 1000).Any(t => t.TaskStatus is not (SyncTaskStatus.Completed or SyncTaskStatus.Cancelled)))
    throw new ArgumentException("测试连接必须关闭新增上传且没有未完成同步任务。");
if (index.PendingCacheRuns().Count > 0 || index.CacheRuns().Any(r => r.Status == "Paused")) throw new ArgumentException("请先处理已有缓存任务，避免测试认领历史操作。");
var runtime = new AgentRuntime(index, new CredentialVault(), Path.GetFullPath("artifacts/bin")); var policy = index.CachePolicy(id); Task<int>? agent = null;
using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(15));
try
{
    index.SetCachePolicy(id, policy with { Enabled = false });
    foreach (var d in await runtime.Tester.TestAsync(c, false, timeout.Token)) { Console.WriteLine(d.Stage + ": " + d.Kind); if (!d.Success) throw new IOException(d.Message); }
    await runtime.ScanAsync(c, timeout.Token); var entry = index.FindPath(id, relative) ?? throw new IOException("测试文件不在远端索引。");
    if (entry.IsDirectory) throw new ArgumentException("需提供现有测试文件，不能是目录。");
    string root = await runtime.Mapper.ConnectAsync(c, timeout.Token);
    async Task<string> RemoteHash()
    {
        await using var stream = await new FileSystemSource(id, root).OpenReadAsync(entry, timeout.Token); return await NewFileUpload.StreamHash(stream, timeout.Token);
    }
    string sourceHash = await RemoteHash();
    agent = Task.Run(() => runtime.RunAsync(c, timeout.Token), timeout.Token);
    while (true)
    {
        if (agent.IsCompleted) throw new IOException("Agent 提前退出：" + await agent);
        try { if (runtime.CachePlatform.Probe(c, relative).Owned) break; } catch (IOException) { }
        await Task.Delay(500, timeout.Token);
    }
    var initial = runtime.CachePlatform.Probe(c, relative);
    if (initial.Pinned || index.DecorateCache(initial).Excluded) throw new IOException("测试目标已有固定或保护意图，请选用独立测试文件。");
    async Task<string> LocalHash()
    {
        await using var stream = new FileStream(Paths.Under(c.SyncRoot, relative), FileMode.Open, FileAccess.Read, FileShare.Read); return await NewFileUpload.StreamHash(stream, timeout.Token);
    }
    if (await LocalHash() != sourceHash) throw new IOException("首次水合哈希不符。");
    async Task Run(string type)
    {
        // Allow the asynchronous Host completion hints to settle; file state is rechecked by the manager.
        while (true)
        {
            var file = index.DecorateCache(runtime.CachePlatform.Probe(c, relative));
            if (file.OpenCount == 0 && file.State != CacheState.Downloading) break;
            await Task.Delay(500, timeout.Token);
        }
        Guid run = runtime.Cache.QueueManual(id, relative, type, false); await runtime.Cache.ExecuteAsync(run, timeout.Token);
        var result = index.CacheOperations(run).Single(); Console.WriteLine(type + " " + result.Status + " released=" + result.ReleasedBytes + " " + result.Error);
        if (result.Status != "Completed") throw new IOException("缓存操作未完成。");
    }
    await Run("Pin"); if (!runtime.CachePlatform.Probe(c, relative).Pinned) throw new IOException("固定未生效。");
    await Run("Unpin"); await Run("Release");
    var released = runtime.CachePlatform.Probe(c, relative);
    if (!released.Owned || released.HydratedBytes != 0 || released.LogicalBytes != entry.Size || !File.Exists(Paths.Under(c.SyncRoot, relative))) throw new IOException("释放后占位/大小/内容状态不符。");
    if (await LocalHash() != sourceHash || await RemoteHash() != sourceHash) throw new IOException("再次 NAS 水合或远端原件哈希不符。");
    Console.WriteLine("PASS actual NAS cache pin/unpin/release/rehydration; placeholder and remote source retained: " + relative);
}
finally
{
    AgentRuntime.Stop(id); await timeout.CancelAsync(); if (agent is not null) { try { await agent.WaitAsync(TimeSpan.FromSeconds(45)); } catch (OperationCanceledException) { } }
    index.SetCachePolicy(id, policy); foreach (string log in index.Logs().Reverse()) Console.WriteLine(log);
}
