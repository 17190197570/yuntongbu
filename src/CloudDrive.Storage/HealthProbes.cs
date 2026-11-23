using Microsoft.Data.Sqlite;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;

namespace CloudDrive.Storage;

public interface IHealthProbe
{
    Task<IReadOnlyList<HealthResult>> CheckAsync(string module, HealthConfiguration configuration, CancellationToken token);
}

// All checks are read-only with respect to user files and SMB sessions. No mapping creation, hydration,
// deletion, task retry, registration or credential retrieval is allowed in this class.
public sealed class WindowsHealthProbe(IndexStore index, Func<ProductSnapshot> runtime, ICachePlatform cache, Func<DateTimeOffset>? clock = null) : IHealthProbe
{
    private readonly Dictionary<Guid, (long Bytes, DateTimeOffset Changed, DateTimeOffset First)> progress = [];
    private HealthResult Result(string module, HealthStatus status, string code, string summary, string evidence, string advice = "查看模块详情与原任务日志", Guid? connection = null, Guid? task = null, string[]? recovery = null)
        => new(module, "observed", status, code, summary, evidence, advice, DateTimeOffset.UtcNow, ConnectionId: connection, TaskId: task, Dependencies: module switch { "CFAPI" => ["Process", "Database"], "SMB" => ["Process"], "Sync" or "Cache" or "Archive" => ["CFAPI", "SMB", "Database"], _ => [] }, Recovery: recovery ?? []);
    public async Task<IReadOnlyList<HealthResult>> CheckAsync(string module, HealthConfiguration configuration, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (module == "Process") return [await ProcessAsync(token)];
        if (module == "SMB")
        {
            var results = new List<HealthResult>(); var budget = Stopwatch.StartNew();
            var mappings = index.Connections();
            var candidates = mappings.Concat(index.Storages().Where(s => !mappings.Any(m => (m.StorageId ?? m.Id) == s.Id)).Select(s => s.Access()));
            foreach (var c in candidates)
            {
                token.ThrowIfCancellationRequested(); double remaining = configuration.TimeoutSeconds - 0.25 - budget.Elapsed.TotalSeconds;
                if (remaining <= 0.1) { results.Add(Result("SMB", HealthStatus.Unknown, "CheckBudget", "本轮预算不足，连接尚未检查", "未将此连接标记为可用或不可用", "提高检查超时或分次核查", c.Id)); continue; }
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token); deadline.CancelAfter(TimeSpan.FromSeconds(Math.Min(3, remaining)));
                try { results.Add(await SmbAsync(c, deadline.Token)); }
                catch (OperationCanceledException) when (!token.IsCancellationRequested) { results.Add(Result("SMB", HealthStatus.Unknown, "ResponseTimeout", "此SMB连接检查响应超时", "没有确认认证/共享或文件状态；未更改会话", "检查连接后重试，不推断远端删除", c.Id)); }
            }
            return results.Count > 0 ? results : [Result("SMB", HealthStatus.Healthy, "NoConnections", "没有配置SMB连接", "未发起网络或写入操作")];
        }
        return await Task.Run<IReadOnlyList<HealthResult>>(() => module switch
        {
            "CFAPI" => Cfapi(), "Sync" => [Sync(configuration)], "Cache" => [Cache(configuration, token)], "Archive" => [Archive(configuration)],
            "Database" or "DatabaseFull" => [CheckDatabase(index.DatabasePath, module == "DatabaseFull")],
            _ => [Result(module, HealthStatus.Unknown, "Unsupported", "未实现此检查", "没有证据")]
        }, token);
    }
    private async Task<HealthResult> ProcessAsync(CancellationToken token)
    {
        var s = runtime();
        if (!Alive(s.AgentPid)) return Result("Process", HealthStatus.Critical, "AgentExited", "Agent进程不存在", "受监控PID已退出", "由现有Bootstrap按退避预算恢复；不要强杀文件操作");
        if (DateTimeOffset.UtcNow - s.UpdatedAt > TimeSpan.FromSeconds(60)) return Result("Process", HealthStatus.Critical, "SchedulerStale", "Agent调度心跳过期", "进程存在但业务循环超过60秒未更新", "等待安全操作收尾并检查日志；不能仅因超时强杀");
        try
        {
            var reply = await ManagementClient.SendAsync(new("status"), token, timeoutSeconds: 3);
            if (!reply.Success || reply.Snapshot?.AgentPid != s.AgentPid) return Result("Process", HealthStatus.Degraded, "IpcMismatch", "IPC未返回对应Agent", "实际状态请求响应不匹配");
            return Result("Process", HealthStatus.Healthy, "Responsive", "Agent进程、调度与IPC响应正常", $"PID={s.AgentPid}；调度={s.UpdatedAt:O}；IPC实际往返成功；界面可关闭，未以UI缺席判故障");
        }
        catch (Exception e) when (e is IOException or OperationCanceledException or TimeoutException)
        { token.ThrowIfCancellationRequested(); return Result("Process", HealthStatus.Degraded, "IpcUnavailable", "Agent存在但IPC不可达", "状态请求未在3秒内完成", "查看当前会话和进程日志，不创建第二个Agent"); }
    }
    private IReadOnlyList<HealthResult> Cfapi()
    {
        var results = new List<HealthResult>(); var s = runtime();
        foreach (var c in index.Connections())
        {
            var host = s.Providers.FirstOrDefault(p => p.ConnectionId == c.Id);
            try { _ = CfNewFileFinalizer.RootId(c); }
            catch (Exception e) when (e is IOException or System.Runtime.InteropServices.COMException or UnauthorizedAccessException)
            { results.Add(Result("CFAPI", HealthStatus.Degraded, "RootInvalid", "同步根未注册或身份不匹配", "实际CfGetSyncRootInfoByPath/连接身份校验失败：0x" + e.HResult.ToString("X8"), "核对连接与根；禁止自动重新注册或重建", c.Id)); continue; }
            if (host is null) { results.Add(Result("CFAPI", HealthStatus.Unknown, "HostUnobserved", "同步根存在，但没有此后台拥有的Host报告", "可能尚未启动，或运行在独立/受控UAC Agent中；未把未观察当作进程退出", "核对原连接管理入口和会话", c.Id)); continue; }
            if (!Alive(host.HostPid)) { results.Add(Result("CFAPI", HealthStatus.Degraded, "HostExited", "同步根存在但受监控Host已退出", "已核对根身份；原Host PID不可用", "在原生命周期管理中安全连接", c.Id, recovery: c.Port == 445 ? ["Reconnect"] : [])); continue; }
            if (!host.Ready) { results.Add(Result("CFAPI", HealthStatus.Degraded, "ProviderDisconnected", "Host存在但Provider未就绪", "根注册不能证明Provider已连接", "通过原生命周期安全重连，保留占位和缓存", c.Id, recovery: ["RestartHost"])); continue; }
            if (host.HeartbeatAt is null || DateTimeOffset.UtcNow - host.HeartbeatAt > TimeSpan.FromSeconds(45))
            { results.Add(Result("CFAPI", HealthStatus.Unknown, "HostHeartbeatStale", "Host心跳过期，连接状态未知", "独立CFAPI探针超过45秒未更新；SMB事件不会刷新此时间", "检查Host响应；不强杀或自动注销", c.Id, recovery: ["RestartHost"])); continue; }
            using (var db = Read())
            using (var command = db.CreateCommand())
            {
                command.CommandText = "SELECT message,time FROM logs WHERE connection_id=$c AND category='cfapi' ORDER BY seq DESC LIMIT 500"; command.Parameters.AddWithValue("$c", c.Id.ToString());
                using var row = command.ExecuteReader(); int failures = 0; DateTimeOffset lastFailure = DateTimeOffset.MinValue, lastSuccess = DateTimeOffset.MinValue;
                while (row.Read())
                {
                    var time = DateTimeOffset.Parse(row.GetString(1)); if (time < DateTimeOffset.UtcNow.AddMinutes(-15)) break;
                    string message = row.GetString(0);
                    if (message.StartsWith("HYDRATION_FAILED", StringComparison.Ordinal)) { failures++; if (time > lastFailure) lastFailure = time; }
                    if (message.StartsWith("RANGE_COMPLETED", StringComparison.Ordinal) && time > lastSuccess) lastSuccess = time;
                }
                if (failures >= 3 && lastFailure > lastSuccess) { results.Add(Result("CFAPI", HealthStatus.Warning, "CallbackFailures", "Provider已连接，但最近持续出现水合失败", $"最近15分钟/500条日志上限：失败={failures}，最后失败={lastFailure:O}；未从单次日志判定损坏", "检查SMB/映射及原回调错误；不要强制标记已同步", c.Id)); continue; }
            }
            results.Add(Result("CFAPI", HealthStatus.Healthy, "Connected", "同步根身份及Provider探针正常", $"Host PID={host.HostPid}；CFAPI探针={host.HeartbeatAt:O}。无请求时不伪造水合/释放成功；业务结果见Cache与原任务日志。", connection: c.Id));
        }
        return results.Count > 0 ? results : [Result("CFAPI", HealthStatus.Healthy, "NoRoots", "没有配置受管根", "没有根需要连接")];
    }
    private async Task<HealthResult> SmbAsync(SmbConnection c, CancellationToken token)
    {
        string stage = "DNS"; bool canReconnect = c.Port == 445 && index.Get(c.Id) is not null;
        try
        {
            var addresses = await Dns.GetHostAddressesAsync(c.Server, token); if (addresses.Length == 0) throw new StorageException(FailureKind.Dns, "empty DNS");
            stage = "TCP"; using (var tcp = new TcpClient()) await tcp.ConnectAsync(c.Server, c.Port, token);
            string? root = SmbMapper.OwnedRoot(index, c);
            if (root is null) return Result("SMB", HealthStatus.Unknown, "SessionUnverified", "网络可达，当前会话尚未验证", "DNS与TCP成功；无当前会话拥有的映射。没有创建/拆除任何映射，也未读取密码", "通过原连接页面建立会话；自定义端口使用受控UAC", c.Id, recovery: canReconnect ? ["Reconnect"] : []);
            stage = "ExistingSession";
            if (!await SmbMapper.VerifyOwnedMappingAsync(index, c, token)) return Result("SMB", HealthStatus.Degraded, "SessionExpired", "已记录的SMB会话当前不存在或身份已变化", "实际Get-SmbMapping未匹配本会话拥有的LocalPath/RemotePath；没有沿过期盘符读文件", "由原连接管理恢复；不拆除其他映射", c.Id, recovery: canReconnect ? ["Reconnect"] : []);
            stage = "Share/List"; FileSystemSource.CheckChain(root);
            using (var entries = Directory.EnumerateFileSystemEntries(root).GetEnumerator()) _ = entries.MoveNext();
            stage = "FileRead";
            string? file = Directory.EnumerateFiles(root).FirstOrDefault();
            if (file is not null)
            {
                FileSystemSource.CheckChain(file);
                await using var read = new FileStream(Paths.Native(file), FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1, FileOptions.Asynchronous);
                _ = await read.ReadAsync(new byte[1], token);
            }
            return Result("SMB", HealthStatus.Healthy, "Accessible", "现有SMB会话及共享读取正常", "DNS/TCP/现有映射/共享/列表成功；" + (file is null ? "根内无直接文件，文件读取能力未测。" : "读取远端文件至多1字节成功。") + "未执行写入测试；只读权限不算故障。未重新验证已保存密码。", connection: c.Id);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or SocketException or System.ComponentModel.Win32Exception)
        {
            var d = FailureClassifier.Describe(stage, e); string code = e is SocketException && stage == "DNS" ? "Dns" : d.Kind.ToString();
            return Result("SMB", HealthStatus.Degraded, code, "连接健康检查失败：" + stage, d.Message + "；0x" + e.HResult.ToString("X8"), d.Kind is FailureKind.Authentication or FailureKind.CredentialConflict ? "重新配置凭据或处理凭据冲突；保留其他SMB会话" : "检查网络/端口/共享；沿用既有重连退避，不解释为远端删除", c.Id, recovery: d.Kind is FailureKind.NetworkUnreachable or FailureKind.PortClosed or FailureKind.Timeout && canReconnect ? ["Reconnect"] : []);
        }
    }
    private HealthResult Sync(HealthConfiguration c)
    {
        using var db = Read(); var now = clock?.Invoke() ?? DateTimeOffset.UtcNow;
        using var command = db.CreateCommand(); command.CommandText = "SELECT Id,TaskStatus,FileSize,TransferredBytes,StartedAt,Phase FROM SyncTasks WHERE TaskStatus IN ('Processing','Failed','Conflict','Pending') ORDER BY CreatedAt LIMIT 1001"; using var rows = command.ExecuteReader();
        int pending = 0, failed = 0, conflicts = 0, processing = 0, count = 0; Guid? stalled = null; string phase = "";
        while (rows.Read())
        {
            count++; string state = rows.GetString(1); Guid id = Guid.Parse(rows.GetString(0));
            if (state == "Pending") pending++; if (state == "Failed") failed++; if (state == "Conflict") conflicts++;
            if (state != "Processing") continue; processing++;
            long bytes = rows.GetInt64(3), size = rows.IsDBNull(2) ? 0 : rows.GetInt64(2);
            if (!progress.TryGetValue(id, out var previous) || previous.Bytes != bytes) progress[id] = previous = (bytes, now, now);
            DateTimeOffset started = rows.IsDBNull(4) ? now : DateTimeOffset.Parse(rows.GetString(4));
            // First observation cannot prove no progress before monitor startup.
            if (now - previous.Changed > HealthRules.StallThreshold(size, bytes, now - started, c)) { stalled = id; phase = rows.GetString(5); }
        }
        foreach (var id in progress.Where(p => now - p.Value.Changed > TimeSpan.FromDays(7)).Select(p => p.Key).ToArray()) progress.Remove(id);
        string evidence = $"样本≤1000；Pending={pending} Processing={processing} Failed={failed} Conflict={conflicts}；完整双向编辑仍未实现。";
        if (stalled is not null) return Result("Sync", HealthStatus.Warning, "ProgressStalled", "任务疑似停滞，需要核查", evidence + "阶段=" + phase + "；连续观察期间进度未变化", "检查占用/SMB与原提交日志；不自动重复上传或清空任务", task: stalled);
        if (failed + conflicts > 0) return Result("Sync", HealthStatus.Warning, conflicts > 0 ? "Conflict" : "TaskFailed", "同步试验存在失败或冲突", evidence, "保留两端副本，查看原任务原因后再决定恢复");
        return Result("Sync", HealthStatus.Warning, "FeatureIncomplete", "新增上传试验可观察，完整双向同步未实现", evidence + (count > 1000 ? "队列超出检查窗口，未检查全部任务。" : "空闲/Pending不据此判定调度失败。"), "保持已有文件只读；完成第三阶段前置验收");
    }
    private HealthResult Cache(HealthConfiguration configuration, CancellationToken token)
    {
        using var db = Read(); using var command = db.CreateCommand(); command.CommandText = "SELECT ConnectionId,RelativePath,PhysicalAllocatedBytes,Snapshot FROM CacheUsageStats ORDER BY UpdatedAt LIMIT 16"; using var rows = command.ExecuteReader(); int checkedCount = 0, mismatch = 0, unknown = 0;
        while (rows.Read())
        {
            token.ThrowIfCancellationRequested(); var connection = index.Get(Guid.Parse(rows.GetString(0))); if (connection is null) { unknown++; continue; }
            var actual = cache.Probe(connection, rows.GetString(1)); checkedCount++;
            if (actual.Error is not null || !actual.Owned) { unknown++; continue; }
            if (Math.Abs(actual.AllocatedBytes - rows.GetInt64(2)) > Math.Max(16L << 20, Math.Max(actual.AllocatedBytes, rows.GetInt64(2)) / 10)) mismatch++;
            if (!rows.IsDBNull(3) && JsonSerializer.Deserialize<CacheSnapshot>(rows.GetString(3)) is { } saved && actual.Pinned != saved.Pinned) mismatch++;
        }
        using var failures = db.CreateCommand(); failures.CommandText = "SELECT COUNT(*) FROM CacheOperations WHERE OperationStatus IN ('Failed','DehydrationFailed')"; long failed = (long)failures.ExecuteScalar()!;
        using var unsafeCandidates = db.CreateCommand(); unsafeCandidates.CommandText = "SELECT COUNT(*) FROM CacheOperations c WHERE c.OperationType='Release' AND c.OperationStatus IN ('Pending','Processing') AND EXISTS(SELECT 1 FROM SyncTasks s WHERE s.FileId=c.FileId AND s.TaskStatus NOT IN ('Completed','Cancelled'))";
        long unsafeCount = (long)unsafeCandidates.ExecuteScalar()!;
        if (unsafeCount > 0) return Result("Cache", HealthStatus.Warning, "UnsafeReleaseCandidate", "释放候选仍有关联未完成同步任务", $"实际队列关联={unsafeCount}；原执行器仍必须重检并拒绝不安全释放", "暂停原清理任务并检查；不调用释放或标记已同步");
        using var stalled = db.CreateCommand(); stalled.CommandText = "SELECT COUNT(*) FROM CacheOperations WHERE OperationStatus='Processing' AND StartedAt<$at"; stalled.Parameters.AddWithValue("$at", DateTimeOffset.UtcNow.AddSeconds(-configuration.WarningThreshold).ToString("O"));
        if ((long)stalled.ExecuteScalar()! > 0) return Result("Cache", HealthStatus.Warning, "ReleaseLongRunning", "释放任务长时间未完成，需要核查", "实际Processing开始时间超过配置阈值；未据此强制中止", "检查原释放保护/SMB/占用，不重复脱水");
        if (failed > 0) return Result("Cache", HealthStatus.Warning, "ReleaseFailed", "缓存存在失败操作", $"失败记录={failed}，元数据样本={checkedCount}；未执行释放", "查看原失败记录；固定/未同步/占用是保护条件，不强行释放");
        if (mismatch > 0) return Result("Cache", HealthStatus.Warning, "StatsMismatch", "缓存实际分配与统计存在差异", $"16项上限样本：检查={checkedCount} 差异={mismatch}；阈值=max(16MiB,10%)", "可重新校准统计，不释放文件", recovery: ["RecalibrateCache"]);
        return Result("Cache", unknown > 0 ? HealthStatus.Unknown : HealthStatus.Healthy, unknown > 0 ? "ProbeUnknown" : "SampleValid", unknown > 0 ? "部分缓存元数据无法确认" : "缓存元数据抽样未发现异常", $"检查={checkedCount} 未知={unknown}；没有读取文件正文或触发水合。抽样不代表全盘核验。无自动策略不算失败。");
    }
    private HealthResult Archive(HealthConfiguration configuration)
    {
        using var db = Read(); using var command = db.CreateCommand(); command.CommandText = "SELECT i.RunId,i.ArchiveStatus,COUNT(*),r.Status FROM ArchiveItems i JOIN ArchiveRuns r ON r.Id=i.RunId WHERE i.ArchiveStatus NOT IN ('Completed','Cancelled','Skipped') GROUP BY i.RunId,i.ArchiveStatus ORDER BY CASE WHEN i.ArchiveStatus IN ('TargetCommitted','MappingUpdated','SourceDeleting') THEN 0 ELSE 1 END LIMIT 100"; using var rows = command.ExecuteReader(); var stages = new List<string>(); Guid? run = null; bool committed = false, failed = false;
        while (rows.Read())
        {
            string stage = rows.GetString(1); stages.Add(stage + "=" + rows.GetInt64(2) + "；run=" + rows.GetString(3));
            committed |= stage is "TargetCommitted" or "MappingUpdated" or "SourceDeleting"; failed |= stage is "Failed" or "Conflict" or "RecoveryRequired";
            if (rows.GetString(3) is "RecoveryRequired" or "Failed" && stage != "Conflict") run ??= Guid.Parse(rows.GetString(0));
        }
        string[] recovery = run is null ? [] : ["ResumeArchive"];
        if (committed) return Result("Archive", HealthStatus.Warning, "CommittedPendingSource", "归档目标已提交，原事务仍待完成", string.Join("；", stages), run is null ? "活动事务可能仍在执行，不提供并行恢复；沿用原日志观察" : "沿用原日志恢复，不重传/盲删目标；恢复可能删除已校验的源文件", task: run, recovery: recovery);
        if (failed) return Result("Archive", HealthStatus.Warning, "TransactionPending", "归档事务需要检查", string.Join("；", stages), "核对源/目标SMB、空间、哈希、映射与原事务；不要清空队列", task: run, recovery: recovery);
        using var stalled = db.CreateCommand(); stalled.CommandText = "SELECT RunId FROM ArchiveItems WHERE ArchiveStatus IN ('Copying','Verifying') AND UpdatedAt<$at LIMIT 1"; stalled.Parameters.AddWithValue("$at", DateTimeOffset.UtcNow.AddSeconds(-configuration.WarningThreshold).ToString("O"));
        if (stalled.ExecuteScalar() is string stopped) return Result("Archive", HealthStatus.Warning, "ArchiveLongRunning", "归档活动阶段长时间没有持久进度更新", "实际Copying/Verifying记录的UpdatedAt超过阈值；同步系统调用可能仍在执行", "查看原事务与两端存储；不并行重试或强杀", task: Guid.Parse(stopped));
        return Result("Archive", HealthStatus.Healthy, "Observed", "归档队列未发现待修复阶段", stages.Count == 0 ? "没有未完成项目；空闲与暂停不算故障。源/目标可用性由各SMB模块独立报告。" : string.Join("；", stages));
    }
    public static HealthResult CheckDatabase(string path, bool full)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow; string module = full ? "DatabaseFull" : "Database";
        HealthResult R(HealthStatus s, string code, string message, string evidence) => new(module, full ? "integrity_check" : "schema/writeability", s, code, message, evidence, "保留数据库及备份；修复前停止后台并备份，不盲目覆盖", now);
        if (!File.Exists(path)) return R(HealthStatus.Critical, "DatabaseMissing", "数据库不存在", "没有自动新建替代数据库");
        try
        {
            if ((File.GetAttributes(path) & FileAttributes.ReadOnly) != 0) return R(HealthStatus.Critical, "DatabaseReadOnly", "数据库文件被标记只读", "实际文件属性包含ReadOnly；检查器没有清除此属性");
            using var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadWrite, Pooling = false, DefaultTimeout = 1 }.ToString()); db.Open();
            using var command = db.CreateCommand(); command.CommandTimeout = 1; command.CommandText = "PRAGMA user_version";
            if ((long)command.ExecuteScalar()! != IndexStore.CurrentSchema) return R(HealthStatus.Critical, "SchemaMismatch", "数据库版本/迁移不匹配", "检查器不自行迁移或降级");
            command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE (type='table' AND name IN ('StorageConnections','FolderMappings','connections','files','SyncTasks','CacheUsageStats','ArchiveItems','FileStorageMappings','HealthCheckRecords','HealthIncidents','RecoveryActions','HealthConfiguration')) OR (type='index' AND name IN ('files_parent','sync_due','health_recent','health_open_incident','folder_mapping_storage'))";
            if ((long)command.ExecuteScalar()! != 17) return R(HealthStatus.Critical, "SchemaObjectsMissing", "必要数据表或索引缺失", "没有自动重建缺失对象");
            // Acquires SQLite's real write lock, then rolls back without changing any data.
            using (var transaction = db.BeginTransaction(deferred: false)) transaction.Rollback();
            if (full) { command.CommandText = "PRAGMA integrity_check"; if (command.ExecuteScalar() as string != "ok") return R(HealthStatus.Critical, "IntegrityFailed", "SQLite完整性检查失败", "实际PRAGMA integrity_check未返回ok"); }
            command.CommandText = "SELECT EXISTS(SELECT 1 FROM files WHERE connection_id NOT IN (SELECT id FROM connections)) OR EXISTS(SELECT 1 FROM FileStorageMappings WHERE StorageConnectionId NOT IN (SELECT id FROM connections)) OR EXISTS(SELECT 1 FROM FolderMappings WHERE MappingId NOT IN (SELECT id FROM connections) OR StorageId NOT IN (SELECT Id FROM StorageConnections)) OR EXISTS(SELECT 1 FROM connections c LEFT JOIN FolderMappings f ON f.MappingId=c.id WHERE f.MappingId IS NULL OR f.StorageId<>COALESCE(json_extract(c.config,'$.StorageId'),c.id))";
            if ((long)command.ExecuteScalar()! != 0) return R(HealthStatus.Critical, "OrphanMapping", "存在孤立文件映射", "实际关联查询发现缺失连接；未修改映射");
            return R(HealthStatus.Healthy, "DatabaseValid", "数据库版本、索引与读写事务检查正常", $"大小={new FileInfo(path).Length}字节；" + (full ? "完整性检查ok" : "轻量检查未执行全库完整性扫描"));
        }
        catch (Exception e) when (e is SqliteException or IOException or UnauthorizedAccessException)
        { return R(e is SqliteException se && se.SqliteErrorCode is 5 or 6 ? HealthStatus.Unknown : HealthStatus.Critical, "DatabaseUnavailable", "数据库访问/读写检查失败", e.GetType().Name + " 0x" + e.HResult.ToString("X8")); }
    }
    private SqliteConnection Read() { var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = index.DatabasePath, Mode = SqliteOpenMode.ReadOnly, DefaultTimeout = 1, Pooling = false }.ToString()); db.Open(); return db; }
    public static bool Alive(int pid) { if (pid <= 0) return false; try { using var process = Process.GetProcessById(pid); return !process.HasExited; } catch (ArgumentException) { return false; } }
}
