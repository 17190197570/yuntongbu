namespace CloudDrive.Storage;

// The only cache mutation coordinator. All destructive work remains a verified native CFAPI release.
public sealed class CacheManager(IndexStore index, ICachePlatform platform)
{
    private static IDisposable GlobalLock() => ConnectionLock.Acquire(Guid.Empty, "cache-execution");
    public Task CalibrateAsync(CancellationToken token) => Task.Run(() =>
    {
        using var lease = GlobalLock();
        foreach (var connection in index.Connections())
        {
            token.ThrowIfCancellationRequested();
            try { Calibrate(connection, token); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
            { index.Log(connection.Id, "cache-calibration-failed", e.Message); }
        }
    }, token);
    private void Calibrate(SmbConnection connection, CancellationToken token)
    {
        // Scan the actual local namespace, including files with no cache DB row. Never read data here.
        string scan = Guid.NewGuid().ToString("N"); var batch = new List<CacheSnapshot>(256);
        var directories = new Stack<string>(); directories.Push(connection.SyncRoot);
        while (directories.TryPop(out string? directory))
        {
            foreach (var item in new DirectoryInfo(directory).EnumerateFileSystemInfos("*", new EnumerationOptions { AttributesToSkip = 0, IgnoreInaccessible = false }))
            {
                token.ThrowIfCancellationRequested(); string relative = Paths.Relative(Path.GetRelativePath(connection.SyncRoot, item.FullName));
                var snapshot = platform.Probe(connection, relative); batch.Add(snapshot);
                if ((item.Attributes & FileAttributes.Directory) != 0 &&
                    ((item.Attributes & FileAttributes.ReparsePoint) == 0 || snapshot.Owned)) directories.Push(item.FullName);
                if (batch.Count == 256) { index.SaveCacheBatch(batch, scan); batch.Clear(); }
            }
        }
        index.SaveCacheBatch(batch, scan); index.FinishCacheScan(connection.Id, scan);
        index.Log(connection.Id, "cache-calibrated", "实际本地元数据及分配空间已校准；未读取文件内容。");
    }
    public Guid QueueManual(Guid connection, string path, string type, bool recursive, CancellationToken token = default)
    {
        if (path.Length > 0) Paths.Relative(path);
        using var lease = GlobalLock();
        var config = index.Get(connection) ?? throw new ArgumentException("连接不存在");
        Calibrate(config, token);
        var files = index.CacheSnapshots(connection).Where(s => InScope(s.RelativePath, path, recursive)).Where(s => type != "Release" || !s.IsDirectory).ToList();
        if (type == "Pin")
        {
            index.SetCacheExclusion(connection, path, false, "Unpinned");
            index.SetCacheExclusion(connection, path, true, "Pinned", "始终保留意图；即使下载失败也保护");
            foreach (var file in files) index.SetCacheExclusion(connection, file.RelativePath, false, "Unpinned");
        }
        if (files.Count == 0) throw new ArgumentException("指定范围没有本地文件；先启动 Agent 创建占位。");
        Guid run = index.QueueCacheRun(connection, "Manual", files.Select(s => (s, type)), path);
        return run == Guid.Empty ? throw new IOException("这些文件已有未完成缓存任务，请在历史页处理原任务。") : run;
    }
    public static bool InScope(string candidate, string scope, bool recursive) =>
        scope.Length == 0 || candidate.Equals(scope, StringComparison.OrdinalIgnoreCase) ||
        (recursive && candidate.StartsWith(scope + "\\", StringComparison.OrdinalIgnoreCase));
    public Task<IReadOnlyList<Guid>> PlanAutomaticAsync(CancellationToken token) => Task.Run<IReadOnlyList<Guid>>(() =>
    {
        using var lease = GlobalLock();
        var runs = new List<Guid>(); var now = DateTimeOffset.UtcNow;
        var snapshots = index.CacheSnapshots().Where(s => !s.IsDirectory).ToList();
        var files = snapshots.Select(index.DecorateCache).ToList();
        var connections = index.Connections();
        foreach (var c in connections)
        {
            token.ThrowIfCancellationRequested(); var p = index.CachePolicy(c.Id); if (!p.Enabled) continue;
            var own = files.Where(f => f.Snapshot.ConnectionId == c.Id).ToList();
            var eligible = own.Where(f => CacheSafety.Reject(f, true, now, p) is null)
                .OrderBy(f => f.LastUserAccessAt <= now.AddDays(-p.HighPriorityDays) ? 0 : f.LastUserAccessAt <= now.AddDays(-p.PriorityDays) ? 1 : 2).ThenBy(f => f.LastUserAccessAt).ToList();
            if (p.CapacityEnabled && own.Sum(f => f.Snapshot.AllocatedBytes) >= p.TriggerCacheBytes)
                runs.Add(index.QueueCacheRun(c.Id, "AutoCapacity", Budget(eligible, Math.Max(0, own.Sum(f => f.Snapshot.AllocatedBytes) - p.TargetCacheBytes)).Select(f => (f.Snapshot, "Release"))));
            if (p.AgeEnabled)
            {
                var old = eligible.Where(f => f.LastUserAccessAt <= now.AddDays(-p.InactivityDays)).ToList();
                if (old.Count > 0) runs.Add(index.QueueCacheRun(c.Id, "AutoAge", old.Select(f => (f.Snapshot, "Release"))));
            }
        }
        // Group by real volume GUID, not drive letters or number of roots. Free bytes are read once per volume.
        var volumes = connections.Select(c => (Connection: c, Policy: index.CachePolicy(c.Id))).Where(x => x.Policy.Enabled && x.Policy.DiskEnabled)
            .Select(x => (x.Connection, x.Policy, Volume: platform.Volume(x.Connection))).GroupBy(x => x.Volume.Key, StringComparer.OrdinalIgnoreCase);
        foreach (var volume in volumes)
        {
            long free = volume.First().Volume.AvailableBytes;
            if (!volume.Any(x => free <= x.Policy.TriggerDiskFreeBytes)) continue;
            long target = volume.Max(x => x.Policy.TargetDiskFreeBytes); var byId = volume.ToDictionary(x => x.Connection.Id, x => x.Policy);
            var eligible = files.Where(f => byId.TryGetValue(f.Snapshot.ConnectionId, out var p) && CacheSafety.Reject(f, true, now, p) is null).OrderBy(f => f.LastUserAccessAt);
            runs.Add(index.QueueCacheRun(null, "AutoDisk", Budget(eligible, Math.Max(0, target - free)).Select(f => (f.Snapshot, "Release"))));
        }
        return runs.Where(id => id != Guid.Empty).ToList();
    }, token);
    private static IEnumerable<CacheFile> Budget(IEnumerable<CacheFile> files, long bytes)
    {
        long selected = 0;
        foreach (var file in files) { if (selected >= bytes) yield break; yield return file; selected += file.Snapshot.AllocatedBytes; }
    }
    public async Task ExecuteAsync(Guid runId, CancellationToken token)
    {
        using var lease = GlobalLock(); index.RecoverCacheRuns();
        var run = index.CacheRuns(runId).SingleOrDefault() ?? throw new ArgumentException("清理任务不存在");
        if (run.Status is "Paused" or "Cancelled" or "Completed" or "Failed") return;
        index.CacheRunStatus(runId, "Processing");
        bool waiting = false;
        var operations = index.CacheOperations(runId).Where(o => o.Status == "Pending").ToList();
        int concurrency = operations.Count == 0 ? 1 : operations.Min(o => index.CachePolicy(o.ConnectionId).CleanupConcurrency);
        try
        {
            // Small batches allow pause/cancel and target-watermark rechecks before claiming more work.
            foreach (var batch in operations.Chunk(concurrency))
            {
                token.ThrowIfCancellationRequested();
                var state = index.CacheRuns(runId).Single().Status;
                if (state is "Paused" or "Cancelled") return;
                await Task.WhenAll(batch.Select(async operation =>
                {
                    token.ThrowIfCancellationRequested();
                    var c = index.Get(operation.ConnectionId);
                    if (c is null) { index.CacheOperationStatus(operation, "Skipped", operation.BeforeBytes, error: "连接已删除"); return; }
                    IDisposable fileLock;
                    try { fileLock = ConnectionLock.Acquire(Guid.ParseExact(operation.FileId, "N"), "file-operation"); }
                    catch (IOException e) { index.CacheOperationStatus(operation, "Skipped", operation.BeforeBytes, error: e.Message); return; }
                    using var fileLease = fileLock;
                    var before = platform.Probe(c, operation.Path); index.SaveCacheBatch([before]);
                    var p = index.CachePolicy(c.Id); bool auto = run.Trigger.StartsWith("Auto", StringComparison.Ordinal);
                    if (operation.Type == "Release")
                    {
                        string? reject = CacheSafety.Reject(index.DecorateCache(before), auto, DateTimeOffset.UtcNow, p);
                        if (auto && !p.Enabled) reject = "自动清理已禁用";
                        if (run.Trigger == "AutoAge" && (!p.AgeEnabled || index.DecorateCache(before).LastUserAccessAt > DateTimeOffset.UtcNow.AddDays(-p.InactivityDays))) reject = "访问时间策略已改变";
                        if (run.Trigger == "AutoCapacity" && (!p.CapacityEnabled || index.CacheSnapshots(c.Id).Where(s => !s.IsDirectory).Sum(s => s.AllocatedBytes) <= p.TargetCacheBytes)) reject = "已达到容量低水位或策略已禁用";
                        if (run.Trigger == "AutoDisk")
                        {
                            var v = platform.Volume(c); var policies = index.Connections().Where(x => platform.Volume(x).Key == v.Key).Select(x => index.CachePolicy(x.Id)).Where(x => x.Enabled && x.DiskEnabled).ToList();
                            if (!p.DiskEnabled || policies.Count == 0 || v.AvailableBytes >= policies.Max(x => x.TargetDiskFreeBytes)) reject = "已达到磁盘目标水位或策略已禁用";
                        }
                        if (reject is not null) { index.CacheOperationStatus(operation, "Skipped", before.AllocatedBytes, before, reject); return; }
                    }
                    else if (!before.Owned) { index.CacheOperationStatus(operation, "Skipped", before.AllocatedBytes, before, "无效占位不允许固定操作"); return; }
                    index.CacheOperationStatus(operation, "Processing", before.AllocatedBytes);
                    try
                    {
                        if (operation.Type == "Release") await platform.ReleaseAsync(c, operation.Path, token);
                        else await platform.PinAsync(c, operation.Path, operation.Type == "Pin", token);
                        var after = platform.Probe(c, operation.Path); index.SaveCacheBatch([after]);
                        if (!after.Owned || after.Error is not null || after.LogicalBytes != before.LogicalBytes ||
                            (operation.Type == "Release" && (after.HydratedBytes != 0 || !after.InSync)) ||
                            (operation.Type == "Pin" && (!after.Pinned || (!after.IsDirectory && after.HydratedBytes < after.LogicalBytes))) ||
                            (operation.Type == "Unpin" && after.Pinned)) throw new IOException("CFAPI 完成后实际状态不符，未标记成功");
                        if (operation.Type == "Unpin")
                        {
                            index.SetCacheExclusion(c.Id, operation.Path, false, "Pinned");
                            index.SetCacheExclusion(c.Id, operation.Path, true, "Unpinned", "明确取消固定，覆盖父目录固定意图");
                        }
                        index.CacheOperationStatus(operation, "Completed", before.AllocatedBytes, after);
                        index.Log(c.Id, "cache-complete", operation.Type + " " + operation.Path + " allocated=" + before.AllocatedBytes + "→" + after.AllocatedBytes);
                    }
                    catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                    catch (Exception e)
                    {
                        // Preserve actual state; a failed call can still have changed pin/partial data state.
                        var after = platform.Probe(c, operation.Path); index.SaveCacheBatch([after]);
                        bool remote = e is StorageException se && se.Kind is FailureKind.NetworkUnreachable or FailureKind.Timeout or FailureKind.Authentication;
                        if (auto && remote) { index.CacheOperationStatus(operation, "Pending", before.AllocatedBytes, after, "远端不可用，等待重试"); waiting = true; }
                        else index.CacheOperationStatus(operation, "Failed", before.AllocatedBytes, after, e.Message, "0x" + e.HResult.ToString("X8"));
                        index.Log(c.Id, "cache-failed", operation.Type + " " + operation.Path + " " + e.Message);
                    }
                }));
                if (waiting) break;
            }
            var final = index.CacheOperations(runId);
            if (!waiting && run.ConnectionId is Guid connection && run.ScopePath is not null && final.Count > 0 && final.All(o => o.Type == "Unpin" && o.Status == "Completed"))
            {
                index.SetCacheExclusion(connection, run.ScopePath, false, "Pinned");
                index.SetCacheExclusion(connection, run.ScopePath, true, "Unpinned", "明确取消范围固定");
            }
            index.CacheRunStatus(runId, waiting ? "WaitingRemote" : final.Any(o => o.Status == "Failed") ? "Failed" : "Completed");
        }
        catch (OperationCanceledException) { index.CacheRunStatus(runId, "Pending", "操作中断；恢复时重新核对实际文件"); throw; }
        catch (Exception e) { index.CacheRunStatus(runId, "Pending", e.Message); throw; }
    }
    public async Task RunBackgroundAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            IDisposable? leader = null;
            try { leader = ConnectionLock.Acquire(Guid.Empty, "cache-scheduler"); } catch (IOException) { }
            if (leader is null) { await Task.Delay(TimeSpan.FromSeconds(10), token); continue; }
            using (leader)
            {
                DateTimeOffset calibrate = DateTimeOffset.MinValue, plan = DateTimeOffset.MinValue; string previous = "";
                var warned = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                bool recovered = false;
                while (!token.IsCancellationRequested)
                {
                    try
                    {
                        var now = DateTimeOffset.UtcNow; var connections = index.Connections();
                        if (!recovered) { using var execution = GlobalLock(); index.RecoverCacheRuns(); recovered = true; }
                        string settings = string.Join("|", connections.Select(c => c.Id + System.Text.Json.JsonSerializer.Serialize(index.CachePolicy(c.Id))));
                        var disks = connections.Select(c => (Connection: c, Policy: index.CachePolicy(c.Id))).Where(x => x.Policy.Enabled && x.Policy.DiskEnabled).Select(x => (x.Connection, x.Policy, Volume: platform.Volume(x.Connection))).GroupBy(x => x.Volume.Key, StringComparer.OrdinalIgnoreCase).ToList();
                        foreach (var disk in disks)
                        {
                            bool warning = disk.Any(x => disk.First().Volume.AvailableBytes <= x.Policy.MinDiskFreeBytes);
                            if (warning && warned.Add(disk.Key)) index.Log(null, "cache-disk-warning", disk.Key + " 可用字节=" + disk.First().Volume.AvailableBytes);
                            if (!warning) warned.Remove(disk.Key);
                        }
                        bool low = disks.Any(disk => disk.Any(x => disk.First().Volume.AvailableBytes <= x.Policy.TriggerDiskFreeBytes));
                        bool changed = settings != previous; previous = settings;
                        if (now >= calibrate || low || changed) { await CalibrateAsync(token); calibrate = now.AddHours(1); }
                        foreach (var run in index.PendingCacheRuns()) await ExecuteAsync(run, token);
                        if (now >= plan || low || changed)
                        {
                            foreach (var id in await PlanAutomaticAsync(token)) await ExecuteAsync(id, token);
                            plan = now.AddMinutes(30);
                        }
                    }
                    catch (Exception e) when (e is not OperationCanceledException) { index.Log(null, "cache-scheduler-retry", e.Message); }
                    await Task.Delay(TimeSpan.FromSeconds(10), token);
                }
            }
        }
    }
}
