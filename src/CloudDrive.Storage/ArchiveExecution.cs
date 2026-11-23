using System.Buffers;
using System.ComponentModel;

namespace CloudDrive.Storage;

public sealed partial class ArchiveManager
{
    public async Task<Guid> StartAsync(Guid policyId, CancellationToken token, string trigger = "Manual", string? dueKey = null)
    {
        using var planning = ConnectionLock.Acquire(policyId, "archive-plan");
        var p = index.GetArchivePolicy(policyId);
        Guid run = index.QueueArchive(p, [], trigger, dueKey);
        var existing = index.ArchiveRuns().Single(r => r.Id == run);
        if (existing.Status == "Scanning")
        {
            try { var preview = await PreviewAsync(existing.Policy, token); index.QueueArchive(existing.Policy, preview.Files, existing.Trigger, existing.DueKey, run); }
            catch (Exception e) { index.SetArchiveRun(run, "Scanning", "扫描/连接中断，保留计划待重试；" + FailureClassifier.Describe("archive-scan", e).Message); throw; }
        }
        await ExecuteAsync(run, token); return run;
    }
    public async Task ExecuteAsync(Guid runId, CancellationToken token)
    {
        using var gate = ConnectionLock.Acquire(Guid.Empty, "archive-execution");
        var run = index.ArchiveRuns().SingleOrDefault(r => r.Id == runId) ?? throw new ArgumentException("归档执行记录不存在。");
        if (run.Status == "Scanning" && !index.ArchiveItems(runId).Any())
        {
            var preview = await PreviewAsync(run.Policy, token); index.QueueArchive(run.Policy, preview.Files, run.Trigger, run.DueKey, runId);
        }
        if (run.Status is "Paused" or "Cancelled" or "Completed" || index.ArchivePaused) return;
        index.SetArchiveRun(runId, "Processing");
        try
        {
            // Work in bounded batches; pause/cancel is checked before each item and irreversible boundary.
            foreach (var batch in index.ArchiveItems(runId).Where(i => i.Status is not (ArchiveStatus.Completed or ArchiveStatus.Cancelled or ArchiveStatus.Skipped)).Chunk(run.Policy.Concurrency))
            {
                if (Stopped(runId)) return;
                await Task.WhenAll(batch.Select(item => ExecuteItem(item, run.Policy, token)));
            }
            if (Stopped(runId)) return;
            var all = index.ArchiveItems(runId).ToList();
            index.SetArchiveRun(runId, all.Any(i => i.Status is not (ArchiveStatus.Completed or ArchiveStatus.Cancelled or ArchiveStatus.Skipped)) ? "RecoveryRequired" : "Completed");
            if (run.Policy.Notify) index.Log(run.Policy.SourceStorageId, "archive-summary", run.Policy.TaskName + " " + index.ArchiveRuns().Single(r => r.Id == runId).Status);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { index.SetArchiveRun(runId, "RecoveryRequired", "执行取消；保留日志，恢复时核对实际状态。"); throw; }
    }
    private bool Stopped(Guid run) => index.ArchivePaused || index.ArchiveRuns().Single(r => r.Id == run).Status is "Paused" or "Cancelled";
    private void Boundary(ArchiveItem item, CancellationToken token)
    {
        token.ThrowIfCancellationRequested(); if (Stopped(item.RunId)) throw new ArchivePausedException();
        var run = index.ArchiveRuns().Single(r => r.Id == item.RunId);
        if (run.Trigger == "Schedule" && !ArchiveSchedule.InWindow(run.Policy, DateTimeOffset.UtcNow)) throw new ArchivePausedException();
    }
    private async Task ExecuteItem(ArchiveItem original, ArchivePolicy policy, CancellationToken token)
    {
        var item = original with { Error = null };
        try
        {
            Boundary(item, token);
            using var fileLease = ConnectionLock.Acquire(Guid.ParseExact(item.FileId, "N"), "file-operation");
            var config = index.Get(item.Source.ConnectionId) ?? throw new IOException("源连接缺失。");
            using var local = localGuard.Acquire(config, item.Source.RelativePath);
            var snapshot = local.Snapshot ?? cache.Probe(config, item.Source.RelativePath);
            string? reason = ArchiveEligibility.Reject(policy, item.Source, index.DecorateCache(snapshot), DateTimeOffset.UtcNow);
            if (index.ArchiveUserProtected(config.Id, item.Source.RelativePath)) reason = "独立用户保护规则排除，包含固定选项不会覆盖该规则。";
            if (reason is not null) throw new SyncConflictException(reason);
            var logical = index.Find(config.Id, item.FileId);
            if (logical is null || !logical.SameVersion(item.Source)) throw new SyncConflictException("原始逻辑映射已变化。");
            var priorMap = index.StorageMapping(item.FileId);
            if (priorMap is not null && priorMap.ArchiveItemId != item.Id) throw new SyncConflictException("其他任务已经更改存储映射。");
            string sourceRoot = await Root(config.Id, token), targetRoot = await Root(item.TargetStorageId, token);
            ValidateSeparateRoots(sourceRoot, targetRoot, policy);
            string sourcePath = Paths.Under(sourceRoot, item.Source.RelativePath), targetPath = Paths.Under(targetRoot, item.TargetPath), tempPath = Paths.Under(targetRoot, item.TemporaryPath);
            FileSystemSource.CheckChain(Path.GetDirectoryName(sourcePath)!);
            var sourceMeta = ArchiveFile.Metadata(sourcePath, config.Id, item.Source.RelativePath);
            var finalMeta = ArchiveFile.Metadata(targetPath, item.TargetStorageId, item.TargetPath, true);
            if (finalMeta is not null)
            {
                if (!item.CopyComplete || item.Staged is null || item.SourceHash is null || finalMeta.RemoteId != item.Staged.RemoteId || finalMeta.Size != item.Source.Size)
                {
                    if (item.Staged is null && item.Target is null && policy.Collision == ArchiveCollision.Skip)
                    { await Saved(item with { Status = ArchiveStatus.Skipped, Error = "目标已有文件，未覆盖。" }, "CollisionSkipped"); return; }
                    throw new SyncConflictException("无法证明目标正式文件归属；保留双方。");
                }
            }
            if (sourceMeta is null && priorMap is null) throw new SyncConflictException("源文件丢失但尚未提交映射；不推断迁移完成。");
            if (sourceMeta is not null && (!sourceMeta.SameVersion(item.Source) || !sourceMeta.CreationTimeReliable || !sourceMeta.ModifiedTimeReliable)) throw new ArchiveSourceChangedException("源文件版本或原始时间变化，保留源并等待重新扫描。");
            // READ+DELETE denies remote writers/deleters, including other SMB clients. DELETE is checked before copying.
            using var sourceHandle = sourceMeta is null ? null : NativeUpload.Open(sourcePath, 0x80010000, 1, 3, true);
            await using var sourceStream = sourceHandle is null ? null : new FileStream(sourceHandle, FileAccess.Read, Wire.Chunk, true);
            if (sourceHandle is not null && !FileSystemSource.ReadMetadata(sourceHandle, config.Id, item.Source.RelativePath).SameVersion(item.Source)) throw new ArchiveSourceChangedException("锁定后的源版本变化。");
            if (sourceStream is not null)
            {
                string sourceHash = await NewFileUpload.StreamHash(sourceStream, token); sourceStream.Position = 0;
                if (item.SourceHash is not null && item.SourceHash != sourceHash) throw new ArchiveSourceChangedException("源内容与归档日志的SHA-256不同。");
                item = item with { SourceHash = sourceHash }; await Saved(item, "SourceHashed");
            }
            if (finalMeta is null)
            {
                if (priorMap is not null || item.Target is not null) throw new SyncConflictException("已映射/已提交目标丢失，禁止重建并删除源。");
                if (sourceStream is null) throw new SyncConflictException("源不可读。");
                Boundary(item, token); ArchiveFile.CreateParents(targetRoot, item.TargetPath);
                var old = ArchiveFile.Metadata(tempPath, item.TargetStorageId, item.TemporaryPath);
                if (old is not null && (item.Staged is null || old.RemoteId != item.Staged.RemoteId)) throw new SyncConflictException("临时文件归属不明，未截断或覆盖。");
                if (!item.CopyComplete)
                {
                    if (ArchiveFile.AvailableBytes(targetRoot) is long free && free < item.Source.Size) throw new IOException("目标可用空间不足；源文件保留。");
                    using (var writeHandle = NativeUpload.Open(tempPath, 0xC0010100, 1, old is null ? 1u : 3u, true))
                    await using (var output = new FileStream(writeHandle, FileAccess.ReadWrite, Wire.Chunk, true))
                    {
                        var staged = FileSystemSource.ReadMetadata(writeHandle, item.TargetStorageId, item.TemporaryPath);
                        if (old is not null && staged.RemoteId != old.RemoteId) throw new SyncConflictException("临时文件在打开前被替换。");
                        item = item with { Status = ArchiveStatus.Copying, Staged = staged, CopiedBytes = 0 }; await Saved(item, "TempOwned");
                        output.SetLength(0); byte[] buffer = ArrayPool<byte>.Shared.Rent(Wire.Chunk);
                        try
                        {
                            long copied = 0, recorded = 0; sourceStream.Position = 0;
                            while (true)
                            {
                                Boundary(item, token); using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token); deadline.CancelAfter(TimeSpan.FromSeconds(30));
                                int count = await sourceStream.ReadAsync(buffer.AsMemory(0, Wire.Chunk), deadline.Token); if (count == 0) break;
                                await output.WriteAsync(buffer.AsMemory(0, count), deadline.Token); copied += count;
                                if (copied - recorded >= 16L * Wire.Chunk) { item = item with { CopiedBytes = copied }; await Saved(item, "CopyProgress"); recorded = copied; }
                            }
                            await output.FlushAsync(token); output.Flush(true);
                            if (copied != item.Source.Size) throw new IOException("归档复制长度不一致。");
                            NativeUpload.SetTimes(writeHandle, item.Source.CreationTime, item.Source.ModifiedTime);
                            var after = FileSystemSource.ReadMetadata(writeHandle, item.TargetStorageId, item.TemporaryPath);
                            if (after.CreationTime != item.Source.CreationTime || after.ModifiedTime != item.Source.ModifiedTime) throw new IOException("目标无法可靠保留原始创建/修改时间，源保留。");
                            item = item with { Status = ArchiveStatus.Verifying, Staged = after, CopiedBytes = copied, CopyComplete = true }; await Saved(item, "CopiedWithTimes");
                        }
                        finally { ArrayPool<byte>.Shared.Return(buffer); }
                    }
                }
                await Saved(item, "BeforeTargetVerify");
                // New server read: verify all bytes, exact metadata, then same-directory rename with no replacement.
                using var verifyHandle = NativeUpload.Open(tempPath, 0x80010000, 1, 3, true);
                await using var verify = new FileStream(verifyHandle, FileAccess.Read, Wire.Chunk, true);
                var actual = FileSystemSource.ReadMetadata(verifyHandle, item.TargetStorageId, item.TemporaryPath);
                if (item.Staged is null || !actual.SameVersion(item.Staged) || actual.CreationTime != item.Source.CreationTime || actual.ModifiedTime != item.Source.ModifiedTime) throw new SyncConflictException("临时文件元数据不符。");
                string targetHash = await NewFileUpload.StreamHash(verify, token);
                if (targetHash != item.SourceHash) throw new SyncConflictException("目标SMB回读SHA-256不一致；源保留。");
                if (!FileSystemSource.ReadMetadata(sourceHandle!, config.Id, item.Source.RelativePath).SameVersion(item.Source)) throw new ArchiveSourceChangedException("复制期间源版本变化。");
                item = item with { TargetHash = targetHash }; await Saved(item, "CommitIntent"); Boundary(item, token);
                NativeUpload.RenameNoReplace(verifyHandle, targetPath);
                item = item with { Target = FileSystemSource.ReadMetadata(verifyHandle, item.TargetStorageId, item.TargetPath), Status = ArchiveStatus.TargetCommitted };
                await Saved(item, "TargetCommitted");
            }
            // Reopen committed target, hold its read lock through mapping verification and source deletion.
            using var targetHandle = NativeUpload.Open(targetPath, 0x80000000, 1, 3, true);
            await using var targetStream = new FileStream(targetHandle, FileAccess.Read, Wire.Chunk, true);
            var targetEntry = FileSystemSource.ReadMetadata(targetHandle, item.TargetStorageId, item.TargetPath);
            string committedHash = await NewFileUpload.StreamHash(targetStream, token);
            if (item.Staged is null || targetEntry.RemoteId != item.Staged.RemoteId || targetEntry.Size != item.Source.Size ||
                targetEntry.CreationTime != item.Source.CreationTime || targetEntry.ModifiedTime != item.Source.ModifiedTime || committedHash != item.SourceHash)
                throw new SyncConflictException("正式目标完整性或身份不符，源保留。");
            item = item with { Target = targetEntry, TargetHash = committedHash, Status = ArchiveStatus.TargetCommitted }; await Saved(item, "TargetReverified");
            Boundary(item, token);
            index.SwitchArchiveMapping(item); item = item with { Status = ArchiveStatus.MappingUpdated };
            if (checkpoint is not null) await checkpoint("MappingUpdated", item);
            var routed = index.ResolveStorage(logical);
            if (routed.Connection.Id != item.TargetStorageId || routed.Entry != targetEntry) throw new IOException("新映射解析不符；禁止删除源。");
            // Independent mapped read exercises the exact storage resolver used by FETCH_DATA.
            await using (var mapped = await new FileSystemSource(routed.Connection.Id, targetRoot).OpenReadAsync(routed.Entry, token))
                if (await NewFileUpload.StreamHash(mapped, token) != item.SourceHash) throw new IOException("新映射回读失败；源保留。");
            await Saved(item, "MappingReadVerified"); Boundary(item, token);
            // Recheck sync/CFAPI under the original local oplock before deleting the exact locked source object.
            reason = ArchiveEligibility.Reject(policy, item.Source, index.DecorateCache(local.Snapshot ?? cache.Probe(config, item.Source.RelativePath)), DateTimeOffset.UtcNow);
            if (index.ArchiveUserProtected(config.Id, item.Source.RelativePath)) reason = "独立用户保护规则排除。";
            if (reason is not null) throw new SyncConflictException(reason);
            item = item with { Status = ArchiveStatus.SourceDeleting }; await Saved(item, "SourceDeleteIntent");
            if (sourceHandle is not null)
            {
                if (!FileSystemSource.ReadMetadata(sourceHandle, config.Id, item.Source.RelativePath).SameVersion(item.Source)) throw new SyncConflictException("删除前源版本变化。");
                ArchiveFile.DeleteLocked(sourceHandle); await sourceStream!.DisposeAsync();
            }
            else if (priorMap?.ArchiveItemId != item.Id) throw new IOException("不能证明缺失源文件对应已提交的本次映射。");
            // Only error 2 counts as absent; parent/network/permissions failures require recovery.
            var remaining = ArchiveFile.Metadata(sourcePath, config.Id, item.Source.RelativePath);
            if (remaining is not null && remaining.RemoteId == item.Source.RemoteId) throw new IOException("源删除未确认。");
            item = item with { Status = ArchiveStatus.Completed, SourceDeleted = true, CompletedAt = DateTimeOffset.UtcNow.ToString("O") }; await Saved(item, "Completed");
        }
        catch (ArchivePausedException) { await Saved(item with { Status = ArchiveStatus.Paused, Error = "已暂停；持久日志保留。" }, "Paused"); }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { await Saved(item with { Status = ArchiveStatus.RecoveryRequired, Error = "取消/退出，等待实际状态核对。" }, "Interrupted"); throw; }
        catch (Exception e)
        {
            // Never delete either copy as generic error handling. A committed target or mapping is durable evidence.
            if (e is ArchiveSourceChangedException && item.Target is null && item.Staged is not null && index.StorageMapping(item.FileId) is null)
            {
                // Remove only the exact journal-owned invalid copy, never an unknown temp or a committed target.
                try
                {
                    string root = await Root(item.TargetStorageId, token), temp = Paths.Under(root, item.TemporaryPath);
                    bool deleted = false;
                    using (var owned = NativeUpload.Open(temp, 0x10080, 1, 3, false))
                        if (FileSystemSource.ReadMetadata(owned, item.TargetStorageId, item.TemporaryPath).RemoteId == item.Staged.RemoteId) { ArchiveFile.DeleteLocked(owned); deleted = true; }
                    if (deleted && ArchiveFile.Metadata(temp, item.TargetStorageId, item.TemporaryPath) is null)
                        item = item with { Staged = null, CopyComplete = false, CopiedBytes = 0, SourceHash = null, TargetHash = null };
                }
                catch (Exception cleanup) when (cleanup is IOException or Win32Exception or UnauthorizedAccessException or OperationCanceledException) { /* Keep uncertain copy and its ownership journal for manual recovery. */ }
            }
            var status = e is SyncConflictException or ArchiveSourceChangedException ? ArchiveStatus.Conflict : ArchiveStatus.RecoveryRequired;
            string message = e is Win32Exception win ? "Windows/SMB错误 " + win.NativeErrorCode : e.Message;
            await Saved(item with { Status = status, Error = message }, "Deferred");
        }
    }
    private sealed class ArchivePausedException : IOException;
    private sealed class ArchiveSourceChangedException(string message) : IOException(message);
}
