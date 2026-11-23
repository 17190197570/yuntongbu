using System.Threading.Channels;

namespace CloudDrive.Storage;

// Watcher/CFAPI notifications are hints. Startup and periodic metadata reconciliation repair missed events.
public sealed class NewFileSyncAgent(IndexStore index, SmbConnection config, Func<CancellationToken, Task<string>> mappedRoot,
    INewFileFinalizer? finalizer = null)
{
    private readonly Channel<(string Type, string? OldPath, string? Path)> hints = Channel.CreateBounded<(string, string?, string?)>(new BoundedChannelOptions(4096) { SingleReader = true, FullMode = BoundedChannelFullMode.Wait });
    private int overflow;
    public void Hint(string type, string? path, string? oldPath = null)
    {
        if (!hints.Writer.TryWrite((type, oldPath, path))) Interlocked.Exchange(ref overflow, 1);
    }
    public async Task RunAsync(CancellationToken token)
    {
        index.RecoverSyncTasks(config.Id); // Caller must already own this connection's Agent lock.
        using var watcher = new FileSystemWatcher(config.SyncRoot) { IncludeSubdirectories = true, NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size, InternalBufferSize = 32768 };
        watcher.Created += (_, e) => Hint("Created", Relative(e.FullPath));
        watcher.Changed += (_, e) => Hint("Changed", Relative(e.FullPath));
        watcher.Deleted += (_, e) => Hint("Deleted", Relative(e.FullPath));
        watcher.Renamed += (_, e) => Hint("Renamed", Relative(e.FullPath), Relative(e.OldFullPath));
        watcher.Error += (_, _) => Interlocked.Exchange(ref overflow, 1);
        watcher.EnableRaisingEvents = true;
        using var workersStop = CancellationTokenSource.CreateLinkedTokenSource(token);
        Task[] workers = Enumerable.Range(0, 8).Select(worker => Task.Run(() => WorkAsync(worker, workersStop.Token), workersStop.Token)).ToArray();
        DateTimeOffset nextScan = DateTimeOffset.MinValue;
        DateTimeOffset quietUntil = DateTimeOffset.MinValue; bool pendingHints = false;
        try
        {
            while (!token.IsCancellationRequested)
            {
                var settings = index.SyncSettings(config.Id); bool dirty = false;
                for (int i = 0; i < 4096 && hints.Reader.TryRead(out var hint); i++)
                {
                    if (!settings.Enabled) continue;
                    if (hint.Path is not null && index.FindPath(config.Id, hint.Path) is not null) continue;
                    index.RecordChange(config.Id, hint.Type, hint.OldPath, hint.Path); dirty = true;
                }
                if (Interlocked.Exchange(ref overflow, 0) != 0)
                {
                    index.RecordChange(config.Id, "WatcherOverflow", null, null); dirty = true;
                    index.Log(config.Id, "watcher-overflow", "变化提示丢失，启动本地元数据补偿扫描。");
                }
                if (dirty) { pendingHints = true; quietUntil = DateTimeOffset.UtcNow.AddMilliseconds(settings.DebounceMilliseconds); }
                if (settings.Enabled && ((pendingHints && DateTimeOffset.UtcNow >= quietUntil) || DateTimeOffset.UtcNow >= nextScan))
                {
                    long watermark = index.EventWatermark(config.Id);
                    try { await ReconcileAsync(token); index.EventsReconciled(config.Id, watermark); pendingHints = false; }
                    catch (Exception error) when (error is not OperationCanceledException)
                    { index.Log(config.Id, "local-reconcile-retry", FailureClassifier.Describe("local-reconcile", error).Message); }
                    nextScan = DateTimeOffset.UtcNow.AddSeconds(settings.ReconcileSeconds);
                }
                await Task.Delay(500, token);
                foreach (var worker in workers) if (worker.IsFaulted) await worker; // Do not silently lose a scheduler worker.
            }
        }
        finally
        {
            await workersStop.CancelAsync();
            try { await Task.WhenAll(workers); } catch (OperationCanceledException) { }
        }
    }
    private string? Relative(string path)
    {
        try { return Paths.Relative(Path.GetRelativePath(config.SyncRoot, path)); } catch (ArgumentException) { Interlocked.Exchange(ref overflow, 1); return null; }
    }
    public async Task ReconcileAsync(CancellationToken token)
    {
        var adopter = finalizer ?? new CfNewFileFinalizer(); adopter.ValidateRoot(config);
        await Task.Run(() =>
        {
            var directories = new Stack<string>(); directories.Push(config.SyncRoot);
            while (directories.TryPop(out var directory))
            {
                token.ThrowIfCancellationRequested();
                foreach (var item in new DirectoryInfo(directory).EnumerateFileSystemInfos("*", new EnumerationOptions { AttributesToSkip = 0, IgnoreInaccessible = false }))
                {
                    token.ThrowIfCancellationRequested(); string relative = Paths.Relative(Path.GetRelativePath(config.SyncRoot, item.FullName));
                    var known = index.FindPath(config.Id, relative);
                    if ((item.Attributes & FileAttributes.Directory) != 0)
                    {
                        // Only traverse ordinary local dirs and indexed cloud dirs; reject junctions/symlinks.
                        if ((item.Attributes & FileAttributes.ReparsePoint) == 0 || (known?.IsDirectory == true && CfNewFileFinalizer.IsOwnedDirectory(config, item.FullName))) directories.Push(item.FullName);
                    }
                    else if (known is null && (item.Attributes & FileAttributes.ReparsePoint) == 0) index.EnqueueNewFile(config.Id, relative);
                }
            }
        }, token);
    }
    private async Task WorkAsync(int worker, CancellationToken token)
    {
        var uploader = new NewFileUpload(index, config, mappedRoot, finalizer ?? new CfNewFileFinalizer());
        while (!token.IsCancellationRequested)
        {
            var settings = index.SyncSettings(config.Id);
            var task = settings.Enabled && !ProductSettings.Load().SyncPaused && worker < settings.UploadConcurrency ? index.ClaimNewUpload(config.Id) : null;
            if (task is null) { await Task.Delay(500, token); continue; }
            index.Log(config.Id, "upload-start", task.Id + " " + task.SourcePath + " phase=" + task.Phase);
            try { await uploader.ExecuteAsync(task, token); }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; } // Recovery retains the precise journal phase.
            catch (Exception error)
            {
                string message = error is SyncConflictException ? error.Message : FailureClassifier.Describe("upload", error).Message;
                index.FailUpload(task, message, error is SyncConflictException);
            }
        }
    }
}
