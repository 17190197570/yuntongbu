namespace CloudDrive.Storage;

public sealed partial class ArchiveManager(IndexStore index,
    Func<SmbConnection, CancellationToken, Task<string>> mappedRoot, ICachePlatform cache, IArchiveLocalGuard localGuard,
    Func<string, ArchiveItem, Task>? checkpoint = null)
{
    private async Task<string> Root(Guid id, CancellationToken token)
    {
        var config = index.Get(id) ?? throw new IOException("归档引用的存储连接缺失。");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token); deadline.CancelAfter(TimeSpan.FromSeconds(30));
        string path = await mappedRoot(config, deadline.Token); FileSystemSource.CheckChain(path); return path;
    }
    public async Task<ArchivePreviewResult> PreviewAsync(ArchivePolicy policy, CancellationToken token, DateTimeOffset? at = null)
    {
        policy.Validate(); var source = index.Get(policy.SourceStorageId) ?? throw new ArgumentException("源连接不存在。");
        string root = await Root(source.Id, token); string targetRoot = await Root(policy.TargetStorageId, token);
        ValidateSeparateRoots(root, targetRoot, policy);
        long? available = ArchiveFile.AvailableBytes(targetRoot);
        var result = new List<ArchivePreview>(); var stack = new Stack<string>(); stack.Push(policy.SourceRootPath);
        var provider = new FileSystemSource(source.Id, root);
        await Task.Run(() =>
        {
            while (stack.TryPop(out string? directory))
            {
                foreach (var entry in provider.Enumerate(directory, token))
                {
                    token.ThrowIfCancellationRequested();
                    if (index.IsUploadTemp(source.Id, entry.RelativePath) || index.IsArchiveTemp(source.Id, entry.RelativePath)) continue;
                    if (entry.IsDirectory) { if (policy.Recursive && (policy.IncludeHidden || (entry.Attributes & 2) == 0) && (policy.IncludeSystem || (entry.Attributes & 4) == 0)) stack.Push(entry.RelativePath); continue; }
                    string target = ArchivePaths.Target(policy, entry.RelativePath);
                    var local = cache.Probe(source, entry.RelativePath);
                    string? reject = ArchiveEligibility.Reject(policy, entry, index.DecorateCache(local), at ?? DateTimeOffset.UtcNow);
                    if (index.ArchiveUserProtected(source.Id, entry.RelativePath)) reject ??= "独立用户保护规则排除";
                    var expected = index.Find(source.Id, entry.Id);
                    if (expected is null || !expected.SameVersion(entry)) reject ??= "远端元数据与当前占位映射不同；请先处理版本变化";
                    if (index.StorageMapping(entry.Id) is not null) reject ??= "该文件已经归档，保留原始来源记录";
                    if (index.HasActiveArchive(entry.Id)) reject ??= "文件已有未完成归档日志";
                    if (reject is null && policy.Collision != ArchiveCollision.ConflictName && ArchiveFile.Metadata(Paths.Under(targetRoot, target), policy.TargetStorageId, target, true) is not null) reject = "目标已有同名文件，禁止覆盖";
                    result.Add(new(entry, target, reject is null, reject ?? "创建和修改时间均严格早于阈值；执行前再次检查状态及独占句柄", local.Pinned));
                }
            }
        }, token);
        return new(result, available);
    }
    internal static void ValidateSeparateRoots(string source, string target, ArchivePolicy p)
    {
        string a = Path.TrimEndingDirectorySeparator(Path.GetFullPath(p.SourceRootPath.Length == 0 ? source : Paths.Under(source, p.SourceRootPath)));
        string b = Path.TrimEndingDirectorySeparator(Path.GetFullPath(p.TargetRootPath.Length == 0 ? target : Paths.Under(target, p.TargetRootPath)));
        if (a.Equals(b, StringComparison.OrdinalIgnoreCase) || a.StartsWith(b + "\\", StringComparison.OrdinalIgnoreCase) || b.StartsWith(a + "\\", StringComparison.OrdinalIgnoreCase)) throw new IOException("实际源/目标范围重叠；拒绝归档。");
        ArchiveFile.CheckPhysicalOverlap(a, b);
    }
    private async Task Saved(ArchiveItem item, string step)
    {
        index.SaveArchiveItem(item, step); if (checkpoint is not null) await checkpoint(step, item);
    }
}
