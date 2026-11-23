namespace CloudDrive.Storage;

public enum HealthStatus { Healthy, Warning, Degraded, Critical, Unknown, Checking }
public enum RecoveryRisk { Low, Confirmation, ExplicitDataAuthorization, Prohibited }
public sealed record HealthConfiguration(string ModuleName, bool Enabled = true, int CheckIntervalSeconds = 60,
    int TimeoutSeconds = 15, int WarningThreshold = 1800, int CriticalThreshold = 7200, bool AutoRecoveryEnabled = false)
{
    public HealthConfiguration Validate()
    {
        if (!HealthModules.All.Contains(ModuleName) || CheckIntervalSeconds is < 10 or > 604800 || TimeoutSeconds is < 1 or > 120 ||
            WarningThreshold is < 30 or > 604800 || CriticalThreshold < WarningThreshold || CriticalThreshold > 2592000)
            throw new ArgumentException("健康检查模块/间隔/超时/阈值无效。");
        return this;
    }
}
public static class HealthModules
{
    public static readonly string[] All = ["Process", "CFAPI", "SMB", "Sync", "Cache", "Archive", "Database", "DatabaseFull"];
    public static HealthConfiguration Default(string module) => new(module, CheckIntervalSeconds: module switch
    { "Process" => 30, "SMB" or "Cache" or "Archive" => 300, "Database" => 600, "DatabaseFull" => 604800, _ => 60 }, TimeoutSeconds: module == "DatabaseFull" ? 120 : 15);
}
public sealed record HealthResult(string ModuleName, string CheckType, HealthStatus Status, string Code, string Summary,
    string Evidence, string Advice, DateTimeOffset CheckedAt, long DurationMs = 0, Guid? ConnectionId = null,
    Guid? TaskId = null, string[]? Dependencies = null, string[]? Recovery = null, Guid? BatchId = null)
{
    public string Key => ModuleName + ":" + (ConnectionId?.ToString("N") ?? "global");
    public bool Abnormal => Status is HealthStatus.Warning or HealthStatus.Degraded or HealthStatus.Critical;
}
public sealed record HealthIncident(string IncidentId, string ModuleName, string Severity, string Title, string Description,
    string RootCause, string? Resource, string Status, DateTimeOffset FirstAt, DateTimeOffset LastAt, DateTimeOffset? ResolvedAt, int Occurrences);
public sealed record HealthRecovery(string Id, string IncidentId, string ActionType, string Description, RecoveryRisk Risk,
    string Status, DateTimeOffset StartedAt, DateTimeOffset? CompletedAt, string? Error);
public sealed record HealthSnapshot(DateTimeOffset At, HealthStatus Status, IReadOnlyList<HealthResult> Modules,
    IReadOnlyList<HealthIncident> Incidents, IReadOnlyList<HealthRecovery> Recoveries, long LastSevenDayIncidents);
public sealed record RecoveryPlan(string IncidentId, string Action, RecoveryRisk Risk, Guid? ConnectionId, Guid? TaskId,
    string Problem, string Effect, bool RestartsProcess, bool NeedsCredentials);
public static class HealthRules
{
    public static HealthStatus Aggregate(IEnumerable<HealthResult> results)
    {
        var states = results.Select(r => r.Status).ToArray();
        if (states.Contains(HealthStatus.Critical)) return HealthStatus.Critical;
        if (states.Contains(HealthStatus.Degraded)) return HealthStatus.Degraded;
        if (states.Contains(HealthStatus.Warning)) return HealthStatus.Warning;
        if (states.Length == 0 || states.Contains(HealthStatus.Unknown)) return HealthStatus.Unknown;
        if (states.Contains(HealthStatus.Checking)) return HealthStatus.Checking;
        return HealthStatus.Healthy;
    }
    public static HealthResult Fresh(HealthResult r, DateTimeOffset now, int interval) => now - r.CheckedAt > TimeSpan.FromSeconds(Math.Max(60, interval * 2))
        ? r with { Status = HealthStatus.Unknown, Code = "Stale", Summary = "健康数据已过期", Evidence = "最后完成：" + r.CheckedAt.ToString("O"), Recovery = [] } : r;
    // A long transfer is suspect only after observed progress stops. Size gives slow links more time.
    public static TimeSpan StallThreshold(long size, long bytes, TimeSpan observed, HealthConfiguration c)
    {
        double rate = observed.TotalSeconds > 0 && bytes > 0 ? bytes / observed.TotalSeconds : 64 * 1024;
        double remaining = Math.Max(0, size - bytes) / Math.Max(16 * 1024, rate);
        return TimeSpan.FromSeconds(Math.Clamp(Math.Max(c.WarningThreshold, remaining * 2), c.WarningThreshold, c.CriticalThreshold));
    }
}
