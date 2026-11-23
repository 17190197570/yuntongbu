namespace CloudDrive.Storage;

public sealed record DiagnosisFact(string Module, HealthStatus Status, string Code, string Evidence, string Suggestion, bool AutomaticRepair = false);
public sealed record FileDiagnosis(string FileId, DateTimeOffset At, IReadOnlyList<DiagnosisFact> Facts);
public static class HealthDiagnosisEngine
{
    public static IReadOnlyList<DiagnosisFact> Explain(HealthSnapshot snapshot)
    {
        var facts = snapshot.Modules.Where(r => r.Abnormal).Select(r => new DiagnosisFact(r.Key, r.Status, r.Code, r.Evidence, r.Advice, r.Recovery?.Contains("RecalibrateCache") == true)).ToList();
        foreach (var r in snapshot.Modules.Where(r => r.Abnormal && r.ModuleName is "Sync" or "Cache" or "Archive"))
        {
            var upstream = snapshot.Modules.Where(d => d.ModuleName is "SMB" or "CFAPI" or "Database" && d.Abnormal && (r.ConnectionId is null || d.ConnectionId is null || d.ConnectionId == r.ConnectionId)).ToArray();
            if (upstream.Length > 0) facts.Add(new(r.Key, HealthStatus.Warning, "DependencyEvidence", "同时检测到依赖异常：" + string.Join("；", upstream.Select(u => u.Key + "=" + u.Code)) + "。这是相关检测事实，不证明唯一根因。", "优先核对这些依赖，再独立复检本模块；不猜测或自动丢弃任务"));
        }
        return facts;
    }
    public static async Task<FileDiagnosis> DiagnoseFileAsync(IndexStore index, SmbConnection connection, string relative,
        HealthSnapshot snapshot, ICachePlatform cache, CancellationToken token)
    {
        Paths.Relative(relative); string fileId = Paths.StableId(connection.Id, relative); var facts = new List<DiagnosisFact>();
        void Add(string module, HealthStatus status, string code, string evidence, string suggestion) => facts.Add(new(module, status, code, evidence, suggestion));
        try
        {
            _ = File.GetAttributes(Paths.Native(Paths.Under(connection.SyncRoot, relative)));
            Add("Local", HealthStatus.Healthy, "EntryExists", "本地条目元数据存在，未读取正文", "继续检查Provider与映射");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        { Add("Local", HealthStatus.Degraded, "EntryUnavailable", "实际属性查询失败：0x" + e.HResult.ToString("X8"), "核对本地路径和访问权限；不自动重建或删除"); }
        var provider = snapshot.Modules.FirstOrDefault(r => r.ModuleName == "CFAPI" && r.ConnectionId == connection.Id);
        Add("CFAPI", provider?.Status ?? HealthStatus.Unknown, provider?.Code ?? "NoEvidence", provider?.Evidence ?? "没有此连接的CFAPI检查依据", "运行完整检查；根已注册不证明Provider已连接");
        var entry = index.Find(connection.Id, fileId);
        if (entry is null) { Add("Database", HealthStatus.Degraded, "MappingMissing", "SQLite没有此稳定文件ID的有效逻辑条目", "完成成功扫描后核对；不得猜测映射或返回旧内容"); return new(fileId, DateTimeOffset.UtcNow, facts); }
        Add("Database", HealthStatus.Healthy, "MappingFound", "逻辑映射可读取；FileId=" + fileId, "继续核查归档路由");
        if (index.HasActiveArchive(fileId)) Add("Archive", HealthStatus.Warning, "TransactionActive", "原归档事务尚未终结", "先核对原事务阶段，不重传或删除目标");
        var actual = cache.Probe(connection, relative);
        if (!actual.Owned || actual.Error is not null) Add("Cache", HealthStatus.Unknown, "LocalStateUnverified", "占位元数据身份/状态无法确认", "保留本地副本，不强制同步或释放");
        else if (actual.Pinned || !actual.InSync || actual.ModifiedBytes > 0) Add("Cache", HealthStatus.Warning, "ReleaseProtected", "本地文件固定或存在未同步修改", "这是释放保护依据；不要为了释放取消保护或丢弃修改");
        else Add("Cache", HealthStatus.Healthy, "LocalStateObserved", $"水合={actual.HydratedBytes}，逻辑={actual.LogicalBytes}；未触发水合", "占用和脱水条件仍由原释放流程再次独占校验");
        var storage = index.ResolveStorage(entry); string? root = SmbMapper.OwnedRoot(index, storage.Connection);
        var smb = snapshot.Modules.FirstOrDefault(r => r.ModuleName == "SMB" && r.ConnectionId == storage.Connection.Id);
        Add("SMB", smb?.Status ?? HealthStatus.Unknown, smb?.Code ?? "NoEvidence", smb?.Evidence ?? "缺少目标存储的实际检测结果", "按实际物理存储连接排查，归档文件不能仍指向原NAS");
        if (root is null) { Add("Remote", HealthStatus.Unknown, "SessionUnverified", "没有可用的当前会话拥有映射，未创建新映射", "从原连接页面建立会话后重新检测"); return new(fileId, DateTimeOffset.UtcNow, facts); }
        try
        {
            if (!await SmbMapper.VerifyOwnedMappingAsync(index, storage.Connection, token)) { Add("SMB", HealthStatus.Degraded, "SessionExpired", "实际Windows映射与原拥有记录不匹配，未沿该盘符读取", "使用原连接入口恢复，保留其他映射"); return new(fileId, DateTimeOffset.UtcNow, facts); }
            // Opening the existing version checks existence/permission/identity. It never reads local
            // placeholder contents, never downloads a whole file and never changes remote state.
            await using var read = await new FileSystemSource(storage.Connection.Id, root).OpenReadAsync(storage.Entry, token);
            Add("Remote", HealthStatus.Healthy, "ExpectedVersionReadable", "实际远端只读句柄可打开，身份/大小/时间与物理映射一致", "这是本次检查事实，后续水合仍需重新验证版本");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        { var d = FailureClassifier.Describe("remote-file", e); Add("Remote", HealthStatus.Degraded, d.Kind.ToString(), d.Message + "；0x" + e.HResult.ToString("X8"), "保留本地缓存；不得以旧内容完成新请求或误判删除"); }
        return new(fileId, DateTimeOffset.UtcNow, facts);
    }
}
