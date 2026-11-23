namespace CloudDrive.Storage;

public enum SyncTaskType { UPLOAD_FILE, CREATE_DIRECTORY, DELETE_FILE, DELETE_DIRECTORY, RENAME_FILE, RENAME_DIRECTORY, REMOTE_REFRESH, CONFLICT_RESOLVE }
public enum SyncTaskStatus { Pending, Processing, Completed, Failed, Paused, Conflict, Cancelled }
public sealed record SyncSettings(bool Enabled = false, int UploadConcurrency = 3, int DebounceMilliseconds = 2000, int ReconcileSeconds = 60)
{
    public SyncSettings Validate()
    {
        if (UploadConcurrency is < 1 or > 8 || DebounceMilliseconds is < 200 or > 60000 || ReconcileSeconds is < 5 or > 3600)
            throw new ArgumentException("上传并发须为1–8、防抖200–60000ms、补偿扫描5–3600秒。");
        return this;
    }
}
public sealed record SyncTask(Guid Id, Guid ConnectionId, string FileId, SyncTaskType TaskType, string SourcePath,
    string TargetPath, SyncTaskStatus TaskStatus, int RetryCount, string Phase, string? LocalHash,
    long? FileSize, long? LocalModifiedTime, string? LocalFileId, string? TempPath, RemoteEntry? RemoteEntry,
    long TransferredBytes, string? ErrorMessage);
public sealed class SyncConflictException(string message) : IOException(message);

// A successful upload and CFAPI adoption are separate durable steps. Test finalizers must be explicit.
public interface INewFileFinalizer
{
    void ValidateRoot(SmbConnection connection);
    void ValidateLocal(SmbConnection connection, FileStream lockedLocal, string relative);
    void Complete(SmbConnection connection, FileStream lockedLocal, RemoteEntry committed);
}
