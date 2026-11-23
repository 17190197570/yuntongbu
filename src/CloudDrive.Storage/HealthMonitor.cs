using System.Collections.Concurrent;
using System.Diagnostics;

namespace CloudDrive.Storage;

public sealed class HealthMonitor(IndexStore index, IHealthProbe probe, Func<RecoveryPlan, CancellationToken, Task> execute)
{
    private readonly ConcurrentDictionary<string, Task<IReadOnlyList<HealthResult>>> running = new();
    private readonly Dictionary<string, DateTimeOffset> next = [];
    private readonly SemaphoreSlim checkGate = new(1);
    private readonly SemaphoreSlim recoveryGate = new(1);
    private volatile bool requested;
    private volatile HealthSnapshot snapshot = new(DateTimeOffset.UtcNow, HealthStatus.Unknown, [], [], [], 0);
    private readonly ConcurrentDictionary<string, int> intervals = new();
    private Task? automaticRecovery;
    public HealthSnapshot Snapshot
    {
        get { var current = snapshot; var results = current.Modules.Select(r => HealthRules.Fresh(r, DateTimeOffset.UtcNow, intervals.GetValueOrDefault(r.ModuleName, 60))).ToArray(); return current with { Modules = results, Status = HealthRules.Aggregate(results) }; }
    }
    public void RequestFullCheck() => requested = true;
    public async Task RunAsync(CancellationToken token)
    {
        bool interruptPending = true;
        DateTimeOffset prune = DateTimeOffset.MinValue;
        DateTimeOffset daily = DateTimeOffset.UtcNow.AddDays(1);
        while (!token.IsCancellationRequested)
        {
            try
            {
                if (interruptPending) { index.InterruptHealthRecoveries(); interruptPending = false; }
                bool force = requested; requested = false;
                if (DateTimeOffset.UtcNow >= daily) { foreach (string module in HealthModules.All.Where(m => m != "DatabaseFull")) next[module] = DateTimeOffset.MinValue; daily = DateTimeOffset.UtcNow.AddDays(1); }
                await CheckAsync(force, token);
                if (DateTimeOffset.UtcNow >= prune) { index.PruneHealth(DateTimeOffset.UtcNow); prune = DateTimeOffset.UtcNow.AddHours(24); }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { break; }
            catch (Exception e)
            {
                var failed = new HealthResult("Database", "monitor-persist", HealthStatus.Critical, "HealthStoreUnavailable", "健康记录存储不可用", e.GetType().Name + " 0x" + e.HResult.ToString("X8"), "保留原数据库和日志；不要新建空库替换", DateTimeOffset.UtcNow);
                snapshot = new(DateTimeOffset.UtcNow, HealthStatus.Critical, [failed], snapshot.Incidents, snapshot.Recoveries, snapshot.LastSevenDayIncidents);
            }
            try { await Task.Delay(1000, token); } catch (OperationCanceledException) { break; }
        }
        if (automaticRecovery is { } recovery) await recovery;
    }
    public async Task CheckAsync(bool full, CancellationToken token)
    {
        if (!await checkGate.WaitAsync(0, token)) { if (full) requested = true; return; }
        try
        {
            foreach (var c in index.HealthConfigurations().Where(c => c.Enabled))
            {
                intervals[c.ModuleName] = c.CheckIntervalSeconds;
                token.ThrowIfCancellationRequested();
                if (!full && next.TryGetValue(c.ModuleName, out var due) && DateTimeOffset.UtcNow < due) continue;
                var checking = new HealthResult(c.ModuleName, "checking", HealthStatus.Checking, "Checking", "正在检查", "原历史与异常保留；尚未得到本轮结论", "等待检查完成", DateTimeOffset.UtcNow);
                var displayed = snapshot.Modules.Where(r => r.ModuleName != c.ModuleName).Append(checking).ToArray();
                snapshot = snapshot with { Modules = displayed, Status = HealthRules.Aggregate(displayed) };
                var timer = Stopwatch.StartNew(); IReadOnlyList<HealthResult> results;
                if (running.TryGetValue(c.ModuleName, out var previous) && !previous.IsCompleted)
                    results = [Unknown(c.ModuleName, "PreviousProbeRunning", "上次探测仍在收尾；没有启动重复检查")];
                else
                {
                    // WaitAsync bounds the monitor, but a blocked synchronous Windows call may finish later.
                    // Keep ownership until it really completes; never spawn repeated hung probes.
                    var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
                    deadline.CancelAfter(TimeSpan.FromSeconds(c.TimeoutSeconds));
                    var task = Task.Run(() => probe.CheckAsync(c.ModuleName, c, deadline.Token), CancellationToken.None);
                    running[c.ModuleName] = task;
                    _ = task.ContinueWith(t => { _ = t.Exception; deadline.Dispose(); }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                    try { results = await task.WaitAsync(TimeSpan.FromSeconds(c.TimeoutSeconds), token); }
                    catch (Exception e) when (e is TimeoutException || e is OperationCanceledException && !token.IsCancellationRequested)
                    { results = [Unknown(c.ModuleName, "ProbeTimeout", "检查超时；结果未知，原业务继续运行")]; }
                    catch (Exception e) when (!token.IsCancellationRequested)
                    { var d = FailureClassifier.Describe("health", e); results = [new(c.ModuleName, "probe", HealthStatus.Unknown, d.Kind.ToString(), "检查未能完成", d.Message + "；0x" + e.HResult.ToString("X8"), "查看原错误，不推断模块已损坏", DateTimeOffset.UtcNow)]; }
                }
                if (results.Count == 0) results = [Unknown(c.ModuleName, "EmptyProbeResult", "检查未返回任何依据")];
                var completed = results.Select(r => r with { DurationMs = timer.ElapsedMilliseconds }).ToArray();
                index.RecordHealthBatch(completed);
                foreach (var result in completed) index.VerifyHealthRecoveries(result);
                next[c.ModuleName] = DateTimeOffset.UtcNow.AddSeconds(c.CheckIntervalSeconds);
                snapshot = index.ReadHealth();
                if (c.AutoRecoveryEnabled && automaticRecovery is not { IsCompleted: false })
                    foreach (var result in results.Where(r => r.Recovery?.Contains("RecalibrateCache") == true))
                    {
                        var incident = snapshot.Incidents.FirstOrDefault(i => i.ModuleName == result.Key && i.Status == "Open");
                        if (incident is null || snapshot.Recoveries.Any(a => a.IncidentId == incident.IncidentId)) continue;
                        // One separate, lifecycle-owned action; a slow calibration cannot stall checks.
                        automaticRecovery = Task.Run(async () =>
                        {
                            try { await RecoverAsync(incident.IncidentId, "RecalibrateCache", confirmed: false, dataAuthorized: false, token); }
                            catch (Exception) { /* RecoverAsync retains the failed action; original fault stays open. */ }
                        }, CancellationToken.None);
                        break;
                    }
            }
            snapshot = index.ReadHealth();
        }
        finally { checkGate.Release(); }
    }
    public RecoveryPlan Plan(string incidentId, string action)
    {
        var current = index.ReadHealth(); var incident = current.Incidents.FirstOrDefault(i => i.IncidentId == incidentId && i.Status == "Open") ?? throw new IOException("异常已变化或不存在，请重新检测。");
        var result = current.Modules.FirstOrDefault(r => r.Key == incident.ModuleName && r.Code == incident.RootCause) ?? throw new IOException("当前检查依据已过期或变化，拒绝修复。");
        if (result.Status is HealthStatus.Unknown or HealthStatus.Checking || result.Recovery?.Contains(action) != true) throw new IOException("此异常没有可执行的修复方案。");
        if (action == "ResumeArchive" && !index.ArchiveRuns().Any(r => r.Id == result.TaskId && r.Status is "RecoveryRequired" or "Failed")) throw new IOException("原归档事务并非待恢复阶段；不启动并行恢复。");
        return action switch
        {
            "RecalibrateCache" => new(incidentId, action, RecoveryRisk.Low, null, null, result.Summary, "重新读取缓存元数据并校准统计；不下载、释放或删除文件", false, false),
            "Reconnect" => new(incidentId, action, RecoveryRisk.Confirmation, result.ConnectionId, null, result.Summary, "请求既有当前用户Agent连接；不拆除其他会话。自动恢复仍沿用原运行时退避", false, false),
            "RestartHost" => new(incidentId, action, RecoveryRisk.Confirmation, result.ConnectionId, null, result.Summary, "请求旧Host安全退出后再启动；仅联机读取可能暂时中断；不强杀、不注销根", true, false),
            "ResumeArchive" => new(incidentId, action, RecoveryRisk.ExplicitDataAuthorization, null, result.TaskId, result.Summary, "沿用原归档事务日志恢复；校验提交后可能删除源NAS文件。源/目标/映射均由既有引擎重新检查", false, false),
            _ => throw new IOException("此操作不在健康修复白名单；删除、覆盖、重建、清队列等禁止自动执行。")
        };
    }
    public async Task RecoverAsync(string incidentId, string action, bool confirmed, bool dataAuthorized, CancellationToken token)
    {
        await recoveryGate.WaitAsync(token);
        try
        {
            var plan = Plan(incidentId, action);
            if (plan.Risk != RecoveryRisk.Low && !confirmed || plan.Risk == RecoveryRisk.ExplicitDataAuthorization && !dataAuthorized) throw new IOException("修复需要对应风险级别的明确确认；没有执行操作。");
            if (index.ReadHealth().Recoveries.Any(a => a.IncidentId == incidentId && a.Status is "Running" or "AwaitingVerification")) throw new IOException("此异常的修复正在执行/等待复检，拒绝重复认领。");
            Guid id = index.BeginHealthRecovery(plan);
            try
            {
                await execute(plan, token); index.EndHealthRecovery(id, "AwaitingVerification"); requested = true;
                index.Log(plan.ConnectionId, "health-recovery-submitted", "修复请求已完成/提交，等待独立复检；异常尚未标记已修复。");
            }
            catch (Exception e)
            {
                index.EndHealthRecovery(id, "Failed", FailureClassifier.Describe("recovery", e).Message);
                index.Log(plan.ConnectionId, "health-recovery-failed", "修复失败，保留原异常和任务日志。"); throw;
            }
            snapshot = index.ReadHealth();
        }
        finally { recoveryGate.Release(); }
    }
    private static HealthResult Unknown(string module, string code, string message) => new(module, "timeout", HealthStatus.Unknown, code, message, "未获得完整检测依据", "保留原任务；等待检查收尾或查看日志", DateTimeOffset.UtcNow);
}
