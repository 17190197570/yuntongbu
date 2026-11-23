namespace CloudDrive.Storage;

public static class ArchiveSchedule
{
    public static DateTimeOffset LatestDue(ArchivePolicy p, DateTimeOffset now)
    {
        var zone = TimeZoneInfo.FindSystemTimeZoneById(p.TimeZoneId ?? TimeZoneInfo.Local.Id);
        var local = TimeZoneInfo.ConvertTime(now, zone).DateTime;
        var time = TimeOnly.ParseExact(p.ScheduleTime, "HH:mm", System.Globalization.CultureInfo.InvariantCulture);
        DateTime due;
        if (p.ScheduleType == "Weekly")
        { due = local.Date.AddDays(-((7 + (int)local.DayOfWeek - p.ScheduleDay) % 7)).Add(time.ToTimeSpan()); if (due > local) due = due.AddDays(-7); }
        else
        { due = new DateTime(local.Year, local.Month, p.ScheduleDay).Add(time.ToTimeSpan()); if (due > local) due = due.AddMonths(-1); }
        // Move a nonexistent DST time to the first valid minute; ambiguous time occurs only once per due key.
        while (zone.IsInvalidTime(due)) due = due.AddMinutes(1);
        return new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(due, DateTimeKind.Unspecified), zone), TimeSpan.Zero);
    }
    public static bool InWindow(ArchivePolicy p, DateTimeOffset now)
    {
        var zone = TimeZoneInfo.FindSystemTimeZoneById(p.TimeZoneId ?? TimeZoneInfo.Local.Id);
        var local = TimeZoneInfo.ConvertTime(now, zone);
        var start = TimeOnly.ParseExact(p.ScheduleTime, "HH:mm", System.Globalization.CultureInfo.InvariantCulture).ToTimeSpan();
        double elapsed = (local.TimeOfDay - start).TotalMinutes; if (elapsed < 0) elapsed += 1440;
        return elapsed < p.WindowMinutes;
    }
}
public sealed partial class ArchiveManager
{
    public async Task SchedulerTickAsync(DateTimeOffset now, CancellationToken token)
    {
        using var gate = ConnectionLock.Acquire(Guid.Empty, "archive-scheduler"); if (index.ArchivePaused) return;
        foreach (var run in index.ArchiveRuns().Where(r => r.Status is "Pending" or "Processing" or "Scanning" or "RecoveryRequired"))
        {
            if (run.Trigger != "Manual" && (!index.ArchivePolicies().Any(p => p.Id == run.ArchiveTaskId && p.Enabled) || !ArchiveSchedule.InWindow(run.Policy, now))) continue;
            if (run.Status == "RecoveryRequired" && (!run.Policy.RetryFailures || index.ArchiveItems(run.Id).Any(i => i.Status == ArchiveStatus.Conflict))) continue;
            try { await ExecuteAsync(run.Id, token); }
            catch (Exception e) when (!token.IsCancellationRequested) { index.Log(run.Policy.SourceStorageId, "archive-retry", FailureClassifier.Describe("archive", e).Message); }
        }
        foreach (var p in index.ArchivePolicies().Where(p => p.Enabled))
        {
            token.ThrowIfCancellationRequested(); if (!ArchiveSchedule.InWindow(p, now)) continue;
            DateTimeOffset due = ArchiveSchedule.LatestDue(p, now);
            if (due < index.ArchivePolicyCreated(p.Id)) continue;
            string dueKey = due.ToString("O");
            if (index.ArchiveRuns().Any(r => r.ArchiveTaskId == p.Id && r.DueKey == dueKey)) continue;
            try { await StartAsync(p.Id, token, "Schedule", dueKey); }
            catch (Exception e) when (!token.IsCancellationRequested) { index.Log(p.SourceStorageId, "archive-schedule-deferred", FailureClassifier.Describe("archive", e).Message); }
        }
    }
    public async Task RunBackgroundAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try { await SchedulerTickAsync(DateTimeOffset.UtcNow, token); }
            catch (IOException) { /* Another current-user Agent/scheduler owns the same work. */ }
            await Task.Delay(TimeSpan.FromSeconds(30), token);
        }
    }
}
