-- Additive operational history. No user files, tasks or physical mappings are rebuilt.
CREATE TABLE IF NOT EXISTS HealthCheckRecords(
 Id INTEGER PRIMARY KEY AUTOINCREMENT, ModuleName TEXT NOT NULL, CheckType TEXT NOT NULL,
 HealthStatus TEXT NOT NULL, ResultSummary TEXT NOT NULL, ErrorCode TEXT, CheckedAt TEXT NOT NULL,
 DurationMs INTEGER NOT NULL, ResultJson TEXT NOT NULL);
CREATE INDEX IF NOT EXISTS health_recent ON HealthCheckRecords(ModuleName,Id DESC);
CREATE TABLE IF NOT EXISTS HealthIncidents(
 Id INTEGER PRIMARY KEY AUTOINCREMENT, IncidentId TEXT NOT NULL UNIQUE, IncidentKey TEXT NOT NULL,
 ModuleName TEXT NOT NULL, Severity TEXT NOT NULL, Title TEXT NOT NULL, Description TEXT NOT NULL,
 RootCause TEXT NOT NULL, AffectedResource TEXT, IncidentStatus TEXT NOT NULL,
 FirstOccurredAt TEXT NOT NULL, LastOccurredAt TEXT NOT NULL, ResolvedAt TEXT, Occurrences INTEGER NOT NULL DEFAULT 1);
CREATE UNIQUE INDEX IF NOT EXISTS health_open_incident ON HealthIncidents(IncidentKey) WHERE IncidentStatus='Open';
CREATE INDEX IF NOT EXISTS health_incident_time ON HealthIncidents(LastOccurredAt);
CREATE TABLE IF NOT EXISTS RecoveryActions(
 Id TEXT PRIMARY KEY, IncidentId TEXT NOT NULL, ActionType TEXT NOT NULL, ActionDescription TEXT NOT NULL,
 RiskLevel TEXT NOT NULL, ExecutionStatus TEXT NOT NULL, StartedAt TEXT NOT NULL, CompletedAt TEXT, ErrorMessage TEXT);
CREATE INDEX IF NOT EXISTS health_recovery_time ON RecoveryActions(StartedAt);
CREATE TABLE IF NOT EXISTS HealthConfiguration(
 ModuleName TEXT PRIMARY KEY, Enabled INTEGER NOT NULL, CheckIntervalSeconds INTEGER NOT NULL,
 TimeoutSeconds INTEGER NOT NULL, WarningThreshold INTEGER NOT NULL, CriticalThreshold INTEGER NOT NULL,
 AutoRecoveryEnabled INTEGER NOT NULL, UpdatedAt TEXT NOT NULL);
PRAGMA user_version=5;
