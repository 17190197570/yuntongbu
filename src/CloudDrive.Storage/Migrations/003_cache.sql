-- Additive cache metadata; file/index/synchronization records are never rebuilt.
CREATE TABLE IF NOT EXISTS CacheUsageStats(
 FileId TEXT PRIMARY KEY, ConnectionId TEXT NOT NULL, RelativePath TEXT NOT NULL,
 LastUserAccessAt TEXT, LastOpenAt TEXT, LastCloseAt TEXT, LastHydratedAt TEXT, LastDehydratedAt TEXT,
 PhysicalAllocatedBytes INTEGER NOT NULL DEFAULT 0, LogicalFileSize INTEGER NOT NULL DEFAULT 0,
 HydratedBytes INTEGER NOT NULL DEFAULT 0, OpenCount INTEGER NOT NULL DEFAULT 0,
 AccessSource TEXT, ProcessId INTEGER, ProcessName TEXT, DownloadCount INTEGER NOT NULL DEFAULT 0, Snapshot TEXT, LastScan TEXT, UpdatedAt TEXT NOT NULL);
CREATE INDEX IF NOT EXISTS cache_connection ON CacheUsageStats(ConnectionId,RelativePath);
CREATE TABLE IF NOT EXISTS CachePolicies(
 Id TEXT PRIMARY KEY, PolicyName TEXT NOT NULL, StorageConnectionId TEXT UNIQUE NOT NULL,
 Settings TEXT NOT NULL, Enabled INTEGER NOT NULL, MaxCacheBytes INTEGER NOT NULL, TriggerCacheBytes INTEGER NOT NULL, TargetCacheBytes INTEGER NOT NULL,
 MinDiskFreeBytes INTEGER NOT NULL, TriggerDiskFreeBytes INTEGER NOT NULL, TargetDiskFreeBytes INTEGER NOT NULL,
 InactivityDays INTEGER NOT NULL, ProtectionDays INTEGER NOT NULL, CleanupConcurrency INTEGER NOT NULL,
 CreatedAt TEXT NOT NULL, UpdatedAt TEXT NOT NULL);
CREATE TABLE IF NOT EXISTS CacheRuns(
 Id TEXT PRIMARY KEY, ConnectionId TEXT, TriggerType TEXT NOT NULL, Status TEXT NOT NULL,
 StartedAt TEXT NOT NULL, CompletedAt TEXT, ErrorMessage TEXT, ScopePath TEXT);
CREATE TABLE IF NOT EXISTS CacheOperations(
 Id TEXT PRIMARY KEY, RunId TEXT NOT NULL, ConnectionId TEXT NOT NULL, FileId TEXT NOT NULL, RelativePath TEXT NOT NULL,
 OperationType TEXT NOT NULL, TriggerType TEXT NOT NULL, OperationStatus TEXT NOT NULL DEFAULT 'Pending',
 BeforeAllocatedBytes INTEGER NOT NULL DEFAULT 0, AfterAllocatedBytes INTEGER, ReleasedBytes INTEGER NOT NULL DEFAULT 0,
 ErrorCode TEXT, ErrorMessage TEXT, StartedAt TEXT, CompletedAt TEXT);
CREATE INDEX IF NOT EXISTS cache_pending ON CacheOperations(RunId,OperationStatus);
CREATE TABLE IF NOT EXISTS CacheExclusions(
 Id TEXT PRIMARY KEY, ConnectionId TEXT NOT NULL, FileId TEXT, RelativePath TEXT NOT NULL,
 ExclusionType TEXT NOT NULL, Reason TEXT NOT NULL, CreatedAt TEXT NOT NULL,
 UNIQUE(ConnectionId,RelativePath,ExclusionType));
PRAGMA user_version=3;
