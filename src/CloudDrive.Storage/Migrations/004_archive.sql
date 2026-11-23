-- Additive journal and physical storage routing. Existing logical file identities stay unchanged.
CREATE TABLE IF NOT EXISTS ArchivePolicies(Id TEXT PRIMARY KEY, TaskName TEXT NOT NULL, SourceStorageId TEXT NOT NULL,
 TargetStorageId TEXT NOT NULL, Settings TEXT NOT NULL, Enabled INTEGER NOT NULL, CreatedAt TEXT NOT NULL, UpdatedAt TEXT NOT NULL);
CREATE TABLE IF NOT EXISTS ArchiveRuns(Id TEXT PRIMARY KEY, ArchiveTaskId TEXT NOT NULL, Policy TEXT NOT NULL,
 TriggerType TEXT NOT NULL, DueKey TEXT, Status TEXT NOT NULL, StartedAt TEXT NOT NULL, CompletedAt TEXT, ErrorMessage TEXT,
 UNIQUE(ArchiveTaskId,DueKey));
CREATE TABLE IF NOT EXISTS ArchiveItems(Id TEXT PRIMARY KEY, RunId TEXT NOT NULL, ArchiveTaskId TEXT NOT NULL, FileId TEXT NOT NULL,
 ArchiveStatus TEXT NOT NULL, Item TEXT NOT NULL, ErrorMessage TEXT, CreatedAt TEXT NOT NULL, UpdatedAt TEXT NOT NULL);
CREATE UNIQUE INDEX IF NOT EXISTS archive_file_active ON ArchiveItems(FileId) WHERE ArchiveStatus NOT IN ('Completed','Cancelled','Skipped');
CREATE INDEX IF NOT EXISTS archive_run_items ON ArchiveItems(RunId,ArchiveStatus,Id);
CREATE INDEX IF NOT EXISTS archive_temp ON ArchiveItems(json_extract(Item,'$.TargetStorageId'),json_extract(Item,'$.TemporaryPath') COLLATE NOCASE);
CREATE TABLE IF NOT EXISTS ArchiveJournal(Id INTEGER PRIMARY KEY AUTOINCREMENT, ArchiveItemId TEXT NOT NULL, OperationStep TEXT NOT NULL,
 SourceStorageId TEXT NOT NULL, SourcePath TEXT NOT NULL, TargetStorageId TEXT NOT NULL, TargetPath TEXT NOT NULL,
 TemporaryPath TEXT NOT NULL, OperationStatus TEXT NOT NULL, LastError TEXT, CreatedAt TEXT NOT NULL);
CREATE TABLE IF NOT EXISTS FileStorageMappings(FileId TEXT PRIMARY KEY, LogicalConnectionId TEXT NOT NULL, LogicalPath TEXT NOT NULL,
 StorageConnectionId TEXT NOT NULL, RemoteEntry TEXT NOT NULL, StorageTier TEXT NOT NULL,
 OriginalStorageId TEXT NOT NULL, OriginalRemotePath TEXT NOT NULL, ArchiveTaskId TEXT NOT NULL, ArchiveItemId TEXT NOT NULL,
 Sha256 TEXT NOT NULL, LastMappedAt TEXT NOT NULL);
CREATE TABLE IF NOT EXISTS ArchiveSettings(Id INTEGER PRIMARY KEY CHECK(Id=1), Paused INTEGER NOT NULL DEFAULT 0);
INSERT OR IGNORE INTO ArchiveSettings VALUES(1,0);
PRAGMA user_version=4;
