using Microsoft.Data.Sqlite;
using System.Text.Json;

namespace CloudDrive.Storage;

public sealed partial class IndexStore
{
    private SqliteConnection OpenHealth()
    {
        // Health persistence must not recreate a missing database through ReadWriteCreate.
        var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = DatabasePath, Mode = SqliteOpenMode.ReadWrite, Pooling = false, DefaultTimeout = 1 }.ToString());
        db.Open(); return db;
    }
    public const int CurrentSchema = 6;
    internal static void MigrateHealth(SqliteConnection db)
    {
        using var stream = typeof(IndexStore).Assembly.GetManifestResourceStream("CloudDrive.Storage.Migrations.005_health.sql") ?? throw new IOException("缺少健康迁移资源。");
        using var reader = new StreamReader(stream); using var command = Cmd(db, reader.ReadToEnd()); command.ExecuteNonQuery();
    }
    public IReadOnlyList<HealthConfiguration> HealthConfigurations()
    {
        using var db = OpenHealth(); using var command = Cmd(db, "SELECT ModuleName,Enabled,CheckIntervalSeconds,TimeoutSeconds,WarningThreshold,CriticalThreshold,AutoRecoveryEnabled FROM HealthConfiguration"); using var row = command.ExecuteReader();
        var result = HealthModules.All.ToDictionary(m => m, HealthModules.Default);
        while (row.Read()) { var c = new HealthConfiguration(row.GetString(0), row.GetBoolean(1), row.GetInt32(2), row.GetInt32(3), row.GetInt32(4), row.GetInt32(5), row.GetBoolean(6)).Validate(); result[c.ModuleName] = c; }
        return result.Values.ToArray();
    }
    public void SetHealthConfiguration(HealthConfiguration c)
    {
        c.Validate(); using var db = OpenHealth(); using var command = Cmd(db, "INSERT INTO HealthConfiguration VALUES($m,$e,$i,$t,$w,$c,$a,$u) ON CONFLICT(ModuleName) DO UPDATE SET Enabled=$e,CheckIntervalSeconds=$i,TimeoutSeconds=$t,WarningThreshold=$w,CriticalThreshold=$c,AutoRecoveryEnabled=$a,UpdatedAt=$u",
            ("$m", c.ModuleName), ("$e", c.Enabled), ("$i", c.CheckIntervalSeconds), ("$t", c.TimeoutSeconds), ("$w", c.WarningThreshold), ("$c", c.CriticalThreshold), ("$a", c.AutoRecoveryEnabled), ("$u", DateTimeOffset.UtcNow.ToString("O"))); command.ExecuteNonQuery();
    }
    public void RecordHealth(HealthResult result)
        => RecordHealthBatch([result]);
    public void RecordHealthBatch(IReadOnlyList<HealthResult> results)
    {
        if (results.Count == 0 || results.Select(r => r.ModuleName).Distinct().Count() != 1 || results.Select(r => r.Key).Distinct().Count() != results.Count)
            throw new ArgumentException("健康批次必须包含同一模块的非重复结果。");
        Guid batch = Guid.NewGuid();
        using var db = OpenHealth(); using var transaction = db.BeginTransaction();
        foreach (var result in results) RecordHealth(db, result with { BatchId = batch });
        transaction.Commit();
    }
    private static void RecordHealth(SqliteConnection db, HealthResult result)
    {
        // Only operational IDs and fixed rule text are persisted; no raw exception/provider payload.
        using (var command = Cmd(db, "INSERT INTO HealthCheckRecords(ModuleName,CheckType,HealthStatus,ResultSummary,ErrorCode,CheckedAt,DurationMs,ResultJson) VALUES($m,$t,$s,$r,$c,$a,$d,$j)",
            ("$m", result.Key), ("$t", result.CheckType), ("$s", result.Status.ToString()), ("$r", result.Summary), ("$c", result.Code), ("$a", result.CheckedAt.ToString("O")), ("$d", result.DurationMs), ("$j", JsonSerializer.Serialize(result)))) command.ExecuteNonQuery();
        if (result.Abnormal)
        {
            // A changing root cause replaces the active incident; Unknown never resolves a previous failure.
            using (var reclassify = Cmd(db, "UPDATE HealthIncidents SET IncidentStatus='Reclassified' WHERE ModuleName=$m AND IncidentStatus='Open' AND RootCause<>$c", ("$m", result.Key), ("$c", result.Code))) reclassify.ExecuteNonQuery();
            using var incident = Cmd(db, """
                INSERT INTO HealthIncidents(IncidentId,IncidentKey,ModuleName,Severity,Title,Description,RootCause,AffectedResource,IncidentStatus,FirstOccurredAt,LastOccurredAt)
                VALUES($id,$key,$m,$s,$title,$description,$cause,$resource,'Open',$at,$at)
                ON CONFLICT(IncidentKey) WHERE IncidentStatus='Open' DO UPDATE SET LastOccurredAt=$at,Occurrences=Occurrences+1,Severity=$s,Description=$description
                """, ("$id", Guid.NewGuid().ToString()), ("$key", result.Key + ":" + result.Code), ("$m", result.Key), ("$s", result.Status.ToString()), ("$title", result.Summary), ("$description", result.Evidence), ("$cause", result.Code), ("$resource", result.TaskId?.ToString() ?? result.ConnectionId?.ToString()), ("$at", result.CheckedAt.ToString("O"))); incident.ExecuteNonQuery();
        }
        else if (result.Status == HealthStatus.Healthy) ResolveOther(db, result, null);
    }
    private static void ResolveOther(SqliteConnection db, HealthResult result, string? keep)
    {
        using var resolve = Cmd(db, "UPDATE HealthIncidents SET IncidentStatus='Resolved',ResolvedAt=$at WHERE ModuleName=$m AND IncidentStatus='Open' AND ($keep IS NULL OR RootCause<>$keep)", ("$at", result.CheckedAt.ToString("O")), ("$m", result.Key), ("$keep", keep)); resolve.ExecuteNonQuery();
    }
    public HealthSnapshot ReadHealth(DateTimeOffset? at = null)
    {
        DateTimeOffset now = at ?? DateTimeOffset.UtcNow; var config = HealthConfigurations().ToDictionary(c => c.ModuleName);
        using var db = OpenHealth(); var results = new List<HealthResult>();
        var latest = new List<HealthResult>();
        using (var command = Cmd(db, "SELECT ResultJson FROM HealthCheckRecords WHERE Id IN (SELECT MAX(Id) FROM HealthCheckRecords GROUP BY ModuleName) ORDER BY Id DESC"))
        using (var row = command.ExecuteReader()) while (row.Read()) latest.Add(JsonSerializer.Deserialize<HealthResult>(row.GetString(0))!);
        // A complete module batch replaces the previous resource set, including global timeouts and
        // deleted connections. Older incidents stay open until actual positive evidence resolves them.
        foreach (var group in latest.GroupBy(r => r.ModuleName))
            if (config.TryGetValue(group.Key, out var c) && c.Enabled)
                results.AddRange(group.Where(r => r.BatchId == group.First().BatchId).Select(r => HealthRules.Fresh(r, now, c.CheckIntervalSeconds)));
        foreach (var c in config.Values.Where(c => c.Enabled && !results.Any(r => r.ModuleName == c.ModuleName))) results.Add(new(c.ModuleName, "pending", HealthStatus.Unknown, "NeverChecked", "尚未完成检查", "没有检测依据", "运行完整检查", now));
        var incidents = new List<HealthIncident>();
        using (var command = Cmd(db, "SELECT IncidentId,ModuleName,Severity,Title,Description,RootCause,AffectedResource,IncidentStatus,FirstOccurredAt,LastOccurredAt,ResolvedAt,Occurrences FROM HealthIncidents WHERE IncidentStatus='Open' OR LastOccurredAt>=$at ORDER BY (IncidentStatus='Open') DESC,(Severity='Critical') DESC,LastOccurredAt DESC LIMIT 500", ("$at", now.AddHours(-24).ToString("O"))))
        using (var row = command.ExecuteReader()) while (row.Read()) incidents.Add(new(row.GetString(0), row.GetString(1), row.GetString(2), row.GetString(3), row.GetString(4), row.GetString(5), row.IsDBNull(6) ? null : row.GetString(6), row.GetString(7), DateTimeOffset.Parse(row.GetString(8)), DateTimeOffset.Parse(row.GetString(9)), row.IsDBNull(10) ? null : DateTimeOffset.Parse(row.GetString(10)), row.GetInt32(11)));
        var recovery = new List<HealthRecovery>();
        using (var command = Cmd(db, "SELECT Id,IncidentId,ActionType,ActionDescription,RiskLevel,ExecutionStatus,StartedAt,CompletedAt,ErrorMessage FROM RecoveryActions ORDER BY StartedAt DESC LIMIT 100"))
        using (var row = command.ExecuteReader()) while (row.Read()) recovery.Add(new(row.GetString(0), row.GetString(1), row.GetString(2), row.GetString(3), Enum.Parse<RecoveryRisk>(row.GetString(4)), row.GetString(5), DateTimeOffset.Parse(row.GetString(6)), row.IsDBNull(7) ? null : DateTimeOffset.Parse(row.GetString(7)), row.IsDBNull(8) ? null : row.GetString(8)));
        using var total = Cmd(db, "SELECT COUNT(*) FROM HealthIncidents WHERE FirstOccurredAt>=$at", ("$at", now.AddDays(-7).ToString("O")));
        return new(now, HealthRules.Aggregate(results), results, incidents, recovery, (long)total.ExecuteScalar()!);
    }
    public IReadOnlyList<HealthResult> HealthHistory(string moduleKey)
    {
        using var db = OpenHealth(); using var command = Cmd(db, "SELECT ResultJson FROM HealthCheckRecords WHERE ModuleName=$m ORDER BY Id DESC LIMIT 100", ("$m", moduleKey)); using var row = command.ExecuteReader(); var results = new List<HealthResult>(); while (row.Read()) results.Add(JsonSerializer.Deserialize<HealthResult>(row.GetString(0))!); return results;
    }
    public IReadOnlySet<string> OpenHealthIncidentIds()
    {
        using var db = OpenHealth(); using var command = Cmd(db, "SELECT IncidentId FROM HealthIncidents WHERE IncidentStatus='Open'");
        using var row = command.ExecuteReader(); var ids = new HashSet<string>(); while (row.Read()) ids.Add(row.GetString(0)); return ids;
    }
    public Guid BeginHealthRecovery(RecoveryPlan plan)
    {
        Guid id = Guid.NewGuid(); using var db = OpenHealth(); using var command = Cmd(db, "INSERT INTO RecoveryActions VALUES($id,$i,$a,$d,$r,'Running',$at,NULL,NULL)", ("$id", id.ToString()), ("$i", plan.IncidentId), ("$a", plan.Action), ("$d", plan.Effect), ("$r", plan.Risk.ToString()), ("$at", DateTimeOffset.UtcNow.ToString("O"))); command.ExecuteNonQuery(); return id;
    }
    public void EndHealthRecovery(Guid id, string status, string? error = null)
    { using var db = OpenHealth(); using var command = Cmd(db, "UPDATE RecoveryActions SET ExecutionStatus=$s,CompletedAt=$at,ErrorMessage=$e WHERE Id=$id", ("$s", status), ("$at", DateTimeOffset.UtcNow.ToString("O")), ("$e", error is null ? null : ProductLog.Redact(error)), ("$id", id.ToString())); command.ExecuteNonQuery(); }
    public void VerifyHealthRecoveries(HealthResult result)
    {
        if (result.Status != HealthStatus.Healthy) return;
        using var db = OpenHealth(); using var command = Cmd(db, "UPDATE RecoveryActions SET ExecutionStatus='Verified',CompletedAt=$at WHERE ExecutionStatus='AwaitingVerification' AND IncidentId IN (SELECT IncidentId FROM HealthIncidents WHERE ModuleName=$m AND IncidentStatus='Resolved')", ("$at", result.CheckedAt.ToString("O")), ("$m", result.Key)); command.ExecuteNonQuery();
    }
    public void InterruptHealthRecoveries()
    { using var db = OpenHealth(); using var command = Cmd(db, "UPDATE RecoveryActions SET ExecutionStatus='Interrupted',CompletedAt=$at,ErrorMessage='Agent重启；上次修复没有完成证据，必须重新检测。' WHERE ExecutionStatus='Running'", ("$at", DateTimeOffset.UtcNow.ToString("O"))); command.ExecuteNonQuery(); }
    public void PruneHealth(DateTimeOffset now)
    {
        using var db = OpenHealth(); using var command = Cmd(db, """
            DELETE FROM HealthCheckRecords WHERE (CheckedAt<$check OR Id NOT IN (SELECT Id FROM HealthCheckRecords ORDER BY Id DESC LIMIT 20000))
                AND Id NOT IN (SELECT MAX(Id) FROM HealthCheckRecords GROUP BY substr(ModuleName,1,instr(ModuleName,':')-1));
            DELETE FROM HealthIncidents WHERE IncidentStatus IN ('Resolved','Reclassified') AND LastOccurredAt<CASE WHEN Severity='Critical' THEN $critical ELSE $ordinary END;
            DELETE FROM RecoveryActions WHERE ExecutionStatus<>'Running' AND CompletedAt<$recovery;
            """, ("$check", now.AddDays(-7).ToString("O")), ("$critical", now.AddDays(-365).ToString("O")), ("$ordinary", now.AddDays(-30).ToString("O")), ("$recovery", now.AddDays(-180).ToString("O"))); command.ExecuteNonQuery();
    }
}
