using CloudDrive.Storage;
using System.Collections.Concurrent;

internal sealed class ProductDaemon
{
    private sealed class ConnectionWorker
    {
        public bool Wanted; public Task? Task; public CancellationTokenSource? Stop;
        public RestartBudget Budget = new(); public DateTimeOffset Next;
    }
    private readonly IndexStore index;
    private readonly AgentRuntime runtime;
    private readonly HealthMonitor healthMonitor;
    private readonly ConcurrentDictionary<Guid, ProviderHealth> providers = new();
    private readonly Dictionary<Guid, ConnectionWorker> workers = [];
    private readonly SemaphoreSlim management = new(1);
    private readonly CancellationTokenSource shutdown = new();
    private readonly List<Task> operations = [];
    private ProductSnapshot snapshot = new(Environment.ProcessId, DateTimeOffset.UtcNow, "启动中", false, [], 0, 0, 0, 0, 0, 0, 0, false, false);
    private bool stopping;
    private DateTimeOffset nextBackup;
    private DateTimeOffset nextUpdate;
    private Task<FileDiagnosis>? fileDiagnosis;
    public ProductDaemon()
    {
        using var initializing = ConnectionLock.Acquire(Guid.Empty, "product-maintenance");
        ProductEnvironment.EnsureDirectories(); index = new(ProductEnvironment.DatabasePath);
        runtime = new(index, new CredentialVault(), health: state => providers[state.ConnectionId] = state);
        healthMonitor = new(index, new WindowsHealthProbe(index, () => snapshot, runtime.CachePlatform), ExecuteHealthRecovery);
        foreach (var c in index.Connections()) workers[c.Id] = new() { Wanted = c.AutoConnect && c.Port == 445 };
    }
    public async Task<int> RunAsync()
    {
        using var ownership = ConnectionLock.Acquire(Guid.Empty, "product-agent");
        using var maintenance = ConnectionLock.Acquire(Guid.Empty, "product-maintenance");
        await using var productLog = new ProductLog(ProductEnvironment.LogRoot, ProductSettings.Load());
        IndexStore.ProductLogSink = (connection, category, message) => productLog.Write(category.StartsWith("archive", StringComparison.Ordinal) ? "Archive" : category.StartsWith("cache", StringComparison.Ordinal) ? "Cache" : category == "cfapi" ? "CFAPI" : "Agent", category, message, connection);
        using var ipcStop = new CancellationTokenSource();
        using var healthStop = CancellationTokenSource.CreateLinkedTokenSource(ipcStop.Token, shutdown.Token);
        Task server = new ManagementServer(Handle).RunAsync(ipcStop.Token);
        Task health = Task.Run(() => healthMonitor.RunAsync(healthStop.Token));
        DateTimeOffset nextTotals = DateTimeOffset.MinValue;
        ProductTotals totals = new(0, 0, 0, 0, 0, 0);
        ConsoleCancelEventHandler cancel = (_, e) => { e.Cancel = true; shutdown.Cancel(); }; Console.CancelKeyPress += cancel;
        try
        {
            index.Log(null, "product-agent", "当前用户后台Agent启动；UI生命周期独立。");
            while (!shutdown.IsCancellationRequested)
            {
                await management.WaitAsync();
                try
                {
                    if (server.IsCompleted) await server;
                    foreach (var pair in workers)
                    {
                        var worker = pair.Value;
                        if (worker.Task?.IsCompleted == true)
                        {
                            try { await worker.Task; } catch (Exception e) { index.Log(pair.Key, "product-worker-exit", FailureClassifier.Describe("worker", e).Message); }
                            worker.Task = null; worker.Stop!.Dispose(); worker.Stop = null;
                            var delay = worker.Budget.Failure(DateTimeOffset.UtcNow);
                            worker.Next = delay is null ? DateTimeOffset.MaxValue : DateTimeOffset.UtcNow + delay.Value;
                            index.Log(pair.Key, "product-restart", delay is null ? "十分钟重启预算耗尽；请检查错误并手动重新连接。" : "Host已完全退出；退避后重新启动，任务日志保留。");
                        }
                        if (!worker.Wanted || worker.Task is not null || DateTimeOffset.UtcNow < worker.Next) continue;
                        var connection = index.Get(pair.Key); if (connection is null) continue;
                        if (connection.Port != 445) { worker.Wanted = false; continue; }
                        worker.Stop = new(); worker.Task = Task.Run(() => runtime.RunAsync(connection, worker.Stop.Token));
                    }
                    operations.RemoveAll(t => t.IsCompleted);
                    var settings = ProductSettings.Load();
                    if (DateTimeOffset.UtcNow >= nextBackup)
                    {
                        nextBackup = DateTimeOffset.UtcNow.AddMinutes(10);
                        string backupDirectory = Path.Combine(ProductEnvironment.DataRoot, "Backups"); Directory.CreateDirectory(backupDirectory);
                        var last = new DirectoryInfo(backupDirectory).EnumerateFiles("CloudDriveLite-*.zip").OrderByDescending(f => f.CreationTimeUtc).FirstOrDefault();
                        if (last is null || last.CreationTimeUtc <= DateTime.UtcNow.AddHours(-settings.BackupHours)) Queue(() => Task.Run(() => { string path = ProductBackup.Create(index, backupDirectory); ProductBackup.Prune(backupDirectory, settings.BackupKeep); index.Log(null, "backup-complete", Path.GetFileName(path)); }));
                    }
                    if (settings.CheckUpdates && DateTimeOffset.UtcNow >= nextUpdate)
                    {
                        nextUpdate = DateTimeOffset.UtcNow.AddHours(settings.UpdateHours);
                        Queue(async () => { var update = await ProductUpdate.CheckAsync(settings, shutdown.Token); index.Log(null, "update-check", "可信清单版本 " + update.Manifest.Version + "；安装需用户确认。"); });
                    }
                }
                finally { management.Release(); }
                try
                {
                    if (DateTimeOffset.UtcNow >= nextTotals) { totals = index.ProductTotals(); nextTotals = DateTimeOffset.UtcNow.AddSeconds(30); }
                    var settings = ProductSettings.Load();
                    bool databaseHealthy = healthMonitor.Snapshot.Modules.Any(r => r.ModuleName == "Database" && r.Status == HealthStatus.Healthy);
                    snapshot = new(Environment.ProcessId, DateTimeOffset.UtcNow, "运行中", databaseHealthy, providers.Values.ToArray(), totals.Indexed, totals.CacheBytes, totals.Waiting, totals.Failed, totals.Uploading, SmbBridge.ActiveReads, totals.Archives, settings.SyncPaused, index.ArchivePaused);
                }
                catch (Exception e) { snapshot = snapshot with { UpdatedAt = DateTimeOffset.UtcNow, State = "数据库/设置检查失败", DatabaseHealthy = false }; Console.Error.WriteLine("Health " + e.GetType().Name); }
                try { await Task.Delay(3000, shutdown.Token); } catch (OperationCanceledException) { }
            }
            stopping = true; snapshot = snapshot with { State = "正在安全退出", UpdatedAt = DateTimeOffset.UtcNow };
            foreach (var connection in index.Connections()) AgentRuntime.Stop(connection.Id);
            foreach (var worker in workers.Values) worker.Stop?.Cancel();
            foreach (var worker in workers.Values.Where(w => w.Task is not null))
                try { await worker.Task!; } catch (OperationCanceledException) { } catch (Exception e) { index.Log(null, "shutdown-worker", FailureClassifier.Describe("shutdown", e).Message); }
            await Task.WhenAll(operations);
            foreach (var connection in index.Connections().Concat(index.Storages().Select(s => s.Access())).DistinctBy(c => c.Id))
            {
                // Include profile-only test sessions and legacy/elevated Agents. No force termination while they still own a root.
                for (;;)
                {
                    try { using var released = ConnectionLock.Acquire(connection.Id, "agent"); break; }
                    catch (IOException) { await Task.Delay(500); }
                }
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                try { await runtime.DisconnectOwnedAsync(connection, deadline.Token); }
                catch (Exception e) { index.Log(connection.Id, "shutdown-mapping-preserved", FailureClassifier.Describe("disconnect", e).Message); }
            }
            File.WriteAllText(Path.Combine(ProductEnvironment.DataRoot, "Config", "shutdown.request"), DateTimeOffset.UtcNow.ToString("O"));
            index.Log(null, "product-stopped", "任务状态已保留；Host、管道安全退出，不注销同步根。"); return 0;
        }
        finally { IndexStore.ProductLogSink = null; Console.CancelKeyPress -= cancel; await ipcStop.CancelAsync(); await Task.WhenAll(server, health); foreach (var worker in workers.Values) worker.Stop?.Dispose(); shutdown.Dispose(); management.Dispose(); }
    }
    private async Task<ManagementReply> Handle(ManagementRequest request)
    {
        if (request.Command == "status") return new(true, "缓存的后台状态；Ready表示Host最后一次CFAPI连接报告。", snapshot);
        if (request.Command == "health-status") return new(true, "独立健康检查缓存；过期数据为Unknown。", Health: healthMonitor.Snapshot);
        if (request.Command == "health-diagnose-file")
        {
            Task<FileDiagnosis> diagnosis;
            await management.WaitAsync();
            try
            {
                if (stopping || shutdown.IsCancellationRequested) return new(false, "正在退出。");
                if (fileDiagnosis is { IsCompleted: false }) return new(false, "上次文件检查仍在收尾，不启动重复检查。");
                if (request.ConnectionId is not Guid c || index.Get(c) is not { } connection || request.RelativePath is not { } relative) return new(false, "缺少已保存连接与安全相对路径。");
                Paths.Relative(relative);
                fileDiagnosis = diagnosis = Task.Run(() => HealthDiagnosisEngine.DiagnoseFileAsync(index, connection, relative, healthMonitor.Snapshot, runtime.CachePlatform, shutdown.Token));
                _ = diagnosis.ContinueWith(t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);
            }
            catch (ArgumentException) { return new(false, "文件相对路径无效。"); }
            finally { management.Release(); }
            try { return new(true, "仅检查所见元数据、映射及远端只读打开，没有修复文件。", Diagnosis: await diagnosis.WaitAsync(TimeSpan.FromSeconds(15), shutdown.Token)); }
            catch (TimeoutException) { return new(false, "文件检查超时，状态Unknown；保留原操作，检查实际收尾前不会启动新探测。"); }
        }
        if (request.Command == "health-check") { healthMonitor.RequestFullCheck(); return new(true, "完整检测已排队，业务线程继续运行。", Health: healthMonitor.Snapshot); }
        if (request.Command == "health-plan")
        {
            try { return new(true, "先查看风险与影响，再明确执行。", Plan: healthMonitor.Plan(request.ItemId?.ToString() ?? "", request.Operation ?? "")); }
            catch (IOException e) { return new(false, e.Message); }
        }
        if (request.Command == "health-recover")
        {
            // Plan and validate before queueing; the recovery worker revalidates current evidence again.
            try
            {
                var plan = healthMonitor.Plan(request.ItemId?.ToString() ?? "", request.Operation ?? "");
                if ((plan.Risk != RecoveryRisk.Low && !request.Confirmed) || (plan.Risk == RecoveryRisk.ExplicitDataAuthorization && !request.DataAuthorized)) return new(false, "此风险级别需要明确确认；未执行。");
                await management.WaitAsync();
                try { if (stopping || shutdown.IsCancellationRequested) return new(false, "正在安全退出。"); Queue(() => healthMonitor.RecoverAsync(plan.IncidentId, plan.Action, request.Confirmed, request.DataAuthorized, shutdown.Token)); }
                finally { management.Release(); }
                return new(true, "修复已排队；仅独立复检通过后才会标为恢复。");
            }
            catch (IOException e) { return new(false, e.Message); }
        }
        await management.WaitAsync();
        try
        {
            if (stopping || shutdown.IsCancellationRequested) return new(false, "后台正在安全退出，不再接收新操作。", snapshot);
            switch (request.Command)
            {
                case "shutdown": shutdown.Cancel(); return new(true, "已停止接收新任务，等待安全退出；不会强制结束文件操作。", snapshot);
                case "sync-pause": ProductSettings.Change(s => s with { SyncPaused = true }); break;
                case "sync-resume": ProductSettings.Change(s => s with { SyncPaused = false }); break;
                case "archive-pause": index.ArchivePaused = true; break;
                case "archive-resume": index.ArchivePaused = false; break;
                case "reconnect-all":
                    foreach (var c in index.Connections().Where(c => c.AutoConnect && c.Port == 445)) { if (!workers.TryGetValue(c.Id, out var existing)) workers[c.Id] = existing = new(); existing.Wanted = true; existing.Next = default; existing.Budget = new(); }
                    break;
                case "connection-start":
                case "connection-stop":
                    if (request.ConnectionId is not Guid id || index.Get(id) is not { } connection) return new(false, "连接不存在。");
                    if (connection.Port != 445) return new(false, "自定义端口仍需同用户的受控UAC入口；普通Agent不会自动提权。");
                    if (!workers.TryGetValue(id, out var worker)) workers[id] = worker = new();
                    worker.Wanted = request.Command == "connection-start"; worker.Next = default; worker.Budget = new();
                    if (!worker.Wanted && worker.Stop is not null) worker.Stop.Cancel();
                    break;
                case "archive-start":
                    if (request.ItemId is not Guid policyId) return new(false, "缺少配置ID。");
                    Guid queuedRun = index.QueueArchive(index.GetArchivePolicy(policyId), []); Queue(() => runtime.Archive.ExecuteAsync(queuedRun, shutdown.Token)); break;
                case "archive-retry":
                    if (request.ItemId is not Guid runId) return new(false, "缺少归档执行ID。");
                    index.ControlArchiveRun(runId, "resume"); Queue(() => runtime.Archive.ExecuteAsync(runId, shutdown.Token)); break;
                case "cache-refresh": Queue(() => runtime.Cache.CalibrateAsync(shutdown.Token)); break;
                case "cache-retry":
                    if (request.ItemId is not Guid cacheRun) return new(false, "缺少缓存任务ID。");
                    Queue(() => runtime.Cache.ExecuteAsync(cacheRun, shutdown.Token)); break;
                case "cache-manual":
                    if (request.ConnectionId is not Guid cacheConnection || request.Operation is not ("Release" or "Pin" or "Unpin")) return new(false, "缓存请求无效。");
                    string relative = request.RelativePath ?? ""; if (relative.Length > 0) Paths.Relative(relative);
                    Queue(async () => { Guid queued = runtime.Cache.QueueManual(cacheConnection, relative, request.Operation, true, shutdown.Token); await runtime.Cache.ExecuteAsync(queued, shutdown.Token); }); break;
                case "scan":
                    if (request.ConnectionId is not Guid scanId || index.Get(scanId) is not { } scanConnection) return new(false, "扫描连接不存在。");
                    Queue(() => runtime.ScanAsync(scanConnection, shutdown.Token)); break;
                case "backup": Queue(() => Task.Run(() => ProductBackup.Create(index, Path.Combine(ProductEnvironment.DataRoot, "Backups")))); break;
                default: return new(false, "不支持的管理命令；没有任意命令执行接口。");
            }
            return new(true, "已交给后台Agent处理。", snapshot);
        }
        catch (Exception e) { return new(false, FailureClassifier.Describe("management", e).Message); }
        finally { management.Release(); }
    }
    private void Queue(Func<Task> action) => operations.Add(Task.Run(async () => { try { await action(); } catch (Exception e) { index.Log(null, "product-operation", FailureClassifier.Describe("operation", e).Message); } }));
    private async Task ExecuteHealthRecovery(RecoveryPlan plan, CancellationToken token)
    {
        if (plan.Action == "RecalibrateCache") { await runtime.Cache.CalibrateAsync(token); return; }
        if (plan.Action == "ResumeArchive")
        {
            if (plan.TaskId is not Guid run) throw new IOException("原事务ID无效。");
            index.ControlArchiveRun(run, "resume"); await runtime.Archive.ExecuteAsync(run, token); return;
        }
        await management.WaitAsync(token);
        try
        {
            if (plan.ConnectionId is not Guid id || index.Get(id) is not { Port: 445 }) throw new IOException("仅允许当前用户普通445连接；自定义端口必须使用原受控UAC入口。");
            if (!workers.TryGetValue(id, out var worker)) workers[id] = worker = new();
            worker.Wanted = true; worker.Next = default; worker.Budget = new();
            if (plan.Action == "RestartHost") worker.Stop?.Cancel();
        }
        finally { management.Release(); }
    }
}
