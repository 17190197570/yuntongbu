-- Additive migration from phase 2; execute in one transaction. Never rebuild files/connections.
CREATE TABLE IF NOT EXISTS SyncSettings(
 ConnectionId TEXT PRIMARY KEY, Enabled INTEGER NOT NULL DEFAULT 0,
 UploadConcurrency INTEGER NOT NULL DEFAULT 3, DebounceMilliseconds INTEGER NOT NULL DEFAULT 2000,
 ReconcileSeconds INTEGER NOT NULL DEFAULT 60);
CREATE TABLE IF NOT EXISTS FileVersions(
 Id INTEGER PRIMARY KEY AUTOINCREMENT, FileId TEXT NOT NULL, LocalHash TEXT NOT NULL,
 RemoteHash TEXT NOT NULL, FileSize INTEGER NOT NULL, RemoteModifiedTime INTEGER NOT NULL,
 RemoteFileId TEXT, VersionNumber INTEGER NOT NULL, IsCurrent INTEGER NOT NULL, CreatedAt TEXT NOT NULL);
CREATE UNIQUE INDEX IF NOT EXISTS current_version ON FileVersions(FileId) WHERE IsCurrent=1;
CREATE TABLE IF NOT EXISTS SyncTasks(
 Id TEXT PRIMARY KEY, ConnectionId TEXT NOT NULL, TaskIdempotencyKey TEXT NOT NULL UNIQUE,
 FileId TEXT NOT NULL, TaskType TEXT NOT NULL, SourcePath TEXT NOT NULL, TargetPath TEXT NOT NULL,
 TaskStatus TEXT NOT NULL, RetryCount INTEGER NOT NULL DEFAULT 0, NextRetryAt TEXT NOT NULL,
 ErrorMessage TEXT, CreatedAt TEXT NOT NULL, StartedAt TEXT, CompletedAt TEXT,
 Phase TEXT NOT NULL DEFAULT 'Queued', LocalHash TEXT, FileSize INTEGER, LocalModifiedTime INTEGER,
 LocalFileId TEXT, TempPath TEXT, RemoteEntry TEXT, TransferredBytes INTEGER NOT NULL DEFAULT 0);
CREATE INDEX IF NOT EXISTS sync_due ON SyncTasks(ConnectionId,TaskStatus,NextRetryAt);
CREATE INDEX IF NOT EXISTS sync_file_order ON SyncTasks(ConnectionId,FileId,CreatedAt);
CREATE TABLE IF NOT EXISTS FileChangeEvents(
 Id INTEGER PRIMARY KEY AUTOINCREMENT, ConnectionId TEXT NOT NULL, FileId TEXT,
 EventType TEXT NOT NULL, OldPath TEXT, NewPath TEXT, EventTime TEXT NOT NULL,
 Processed INTEGER NOT NULL DEFAULT 0, CreatedAt TEXT NOT NULL);
CREATE TABLE IF NOT EXISTS ConflictRecords(
 Id TEXT PRIMARY KEY, FileId TEXT NOT NULL, LocalVersionHash TEXT, RemoteVersionHash TEXT,
 LocalConflictPath TEXT NOT NULL, RemotePath TEXT NOT NULL, ConflictType TEXT NOT NULL,
 ResolutionStatus TEXT NOT NULL, CreatedAt TEXT NOT NULL, ResolvedAt TEXT);
CREATE TABLE IF NOT EXISTS DeletedFileRecords(
 Id TEXT PRIMARY KEY, FileId TEXT NOT NULL, OriginalPath TEXT NOT NULL, RemotePath TEXT NOT NULL,
 DeletedAt TEXT NOT NULL, IsRecovered INTEGER NOT NULL DEFAULT 0, RecoveryPath TEXT);
PRAGMA user_version=2;
