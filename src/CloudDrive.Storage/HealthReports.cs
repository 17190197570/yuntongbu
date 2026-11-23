using System.IO.Compression;
using System.Net;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace CloudDrive.Storage;

public sealed record HealthReport(string Version, string Windows, string Architecture, DateTimeOffset At, HealthSnapshot Health, string Privacy, string Limitations, IReadOnlyList<DiagnosisFact> Diagnoses, FileDiagnosis? File = null);
public static class HealthReports
{
    public static HealthReport Preview(HealthSnapshot snapshot, FileDiagnosis? file = null) => new(ProductEnvironment.Version, RuntimeInformation.OSDescription, RuntimeInformation.OSArchitecture.ToString(), DateTimeOffset.UtcNow, Clean(snapshot),
        "仅导出所见健康结果、24小时/未解决异常、7天计数、依赖及修复记录。不含原始日志、数据库、凭据、DPAPI密钥、服务器、完整路径或文件正文。资源ID仍可关联操作，应妥善保管。",
        "实际检查范围由每项Evidence说明。Unknown/抽样/未连接/未实现不能视为通过。无真实NAS、签名升级和完整双向同步验收。历史窗口最多500条异常/100条修复，其余保留在库中。", HealthDiagnosisEngine.Explain(Clean(snapshot)), file is null ? null : file with { Facts = file.Facts.Select(CleanFact).ToArray() });
    private static DiagnosisFact CleanFact(DiagnosisFact f) => f with { Evidence = ProductLog.Redact(f.Evidence), Suggestion = ProductLog.Redact(f.Suggestion) };
    private static HealthSnapshot Clean(HealthSnapshot snapshot) => snapshot with
    {
        Modules = snapshot.Modules.Select(r => r with { Summary = ProductLog.Redact(r.Summary), Evidence = ProductLog.Redact(r.Evidence), Advice = ProductLog.Redact(r.Advice) }).ToArray(),
        Incidents = snapshot.Incidents.Select(i => i with { Title = ProductLog.Redact(i.Title), Description = ProductLog.Redact(i.Description), RootCause = ProductLog.Redact(i.RootCause) }).ToArray(),
        Recoveries = snapshot.Recoveries.Select(r => r with { Description = ProductLog.Redact(r.Description), Error = r.Error is null ? null : ProductLog.Redact(r.Error) }).ToArray()
    };
    public static string Json(HealthReport report) => JsonSerializer.Serialize(report with { Health = Clean(report.Health), Diagnoses = report.Diagnoses.Select(CleanFact).ToArray(), File = report.File is null ? null : report.File with { Facts = report.File.Facts.Select(CleanFact).ToArray() } }, new JsonSerializerOptions { WriteIndented = true });
    public static string Html(HealthReport report)
    {
        report = report with { Health = Clean(report.Health) };
        string E(string value) => WebUtility.HtmlEncode(value);
        return "<!doctype html><html lang=\"zh-CN\"><meta charset=\"utf-8\"><title>CloudDrive Lite 健康诊断</title><style>body{font:15px system-ui;max-width:1100px;margin:36px auto;padding:0 20px;color:#203047}pre{white-space:pre-wrap;overflow-wrap:anywhere}td,th{padding:9px;text-align:left;border-bottom:1px solid #ddd}table{width:100%;border-collapse:collapse}</style><h1>CloudDrive Lite 健康诊断</h1><p>" + E(report.At.ToString("O") + " · " + report.Health.Status) + "</p><p>" + E(report.Privacy) + "</p><table><tr><th>模块</th><th>状态</th><th>依据</th><th>建议</th></tr>" + string.Concat(report.Health.Modules.Select(r => "<tr><td>" + E(r.Key) + "</td><td>" + E(r.Status.ToString()) + "</td><td>" + E(ProductLog.Redact(r.Evidence)) + "</td><td>" + E(r.Advice) + "</td></tr>")) + "</table><h2>所见完整报告</h2><pre>" + E(Json(report)) + "</pre></html>";
    }
    public static string Export(HealthReport preview, string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        string extension = Path.GetExtension(path).ToLowerInvariant();
        if (extension == ".json") File.WriteAllText(path, Json(preview));
        else if (extension == ".html") File.WriteAllText(path, Html(preview));
        else if (extension == ".zip")
        {
            using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
            using (var writer = new StreamWriter(zip.CreateEntry("health.json").Open())) writer.Write(Json(preview));
            using (var writer = new StreamWriter(zip.CreateEntry("health.html").Open())) writer.Write(Html(preview));
        }
        else throw new ArgumentException("诊断导出仅支持JSON/HTML/ZIP。");
        return path;
    }
}

// A notification ledger belongs to the existing single UI process. Pure selection is testable without
// displaying or fabricating Windows notifications. No normal-information notifications are generated.
public sealed record HealthNotice(string Key, string Title, string Text, bool Critical, bool Recovered);
public sealed class HealthNotificationPolicy
{
    private readonly Dictionary<string, DateTimeOffset> seen;
    private readonly string? path;
    private readonly Func<IReadOnlySet<string>>? activeIds;
    public HealthNotificationPolicy(string? path = null, Func<IReadOnlySet<string>>? activeIds = null)
    {
        this.path = path; this.activeIds = activeIds; seen = [];
        if (path is not null && File.Exists(path))
        {
            using var data = JsonDocument.Parse(File.ReadAllText(path));
            if (data.RootElement.ValueKind == JsonValueKind.Array)
                foreach (var key in data.RootElement.EnumerateArray()) seen[key.GetString()!] = DateTimeOffset.UtcNow;
            else foreach (var entry in data.RootElement.EnumerateObject()) seen[entry.Name] = entry.Value.GetDateTimeOffset();
        }
    }
    public IReadOnlyList<HealthNotice> Select(HealthSnapshot snapshot, bool warnings, bool recoveries)
    {
        var notices = new List<HealthNotice>();
        int beforePrune = seen.Count;
        var active = activeIds?.Invoke();
        // Keep every currently open fault's dedup marker, including faults outside the 500-row UI
        // window. Terminal history is bounded; abandoned markers are removed using actual DB state.
        var terminalIds = snapshot.Incidents.Where(i => i.Status != "Open").Select(i => i.IncidentId).ToHashSet();
        foreach (var key in seen.Keys.Where(k => k.EndsWith(":Open", StringComparison.Ordinal) &&
            (active is not null ? !active.Contains(k[..^5]) : terminalIds.Contains(k[..^5]))).ToArray())
        {
            // Preserve the open marker until a recovery notice was delivered or is no longer wanted.
            bool awaitingNotice = recoveries && snapshot.Incidents.Any(i => i.IncidentId == key[..^5] && i.Status == "Resolved") && !seen.ContainsKey(key[..^5] + ":Resolved");
            if (!awaitingNotice) seen.Remove(key);
        }
        foreach (var key in seen.Where(p => !p.Key.EndsWith(":Open", StringComparison.Ordinal)).OrderByDescending(p => p.Value).Where((p, n) => n >= 1000 || p.Value < DateTimeOffset.UtcNow.AddDays(-30)).Select(p => p.Key).ToArray()) seen.Remove(key);
        if (seen.Count != beforePrune && path is not null) ProductEnvironment.AtomicJson(path, seen);
        foreach (var incident in snapshot.Incidents)
        {
            bool critical = incident.Severity == "Critical"; string key = incident.IncidentId + ":" + incident.Status;
            if (seen.ContainsKey(key)) continue;
            if (incident.Status == "Open" && (critical || warnings)) notices.Add(new(key, incident.Title, incident.ModuleName + " · " + incident.RootCause, critical, false));
            if (incident.Status == "Resolved" && recoveries && seen.ContainsKey(incident.IncidentId + ":Open")) notices.Add(new(key, "异常已由检测确认恢复", incident.ModuleName + " · " + incident.RootCause, false, true));
        }
        return notices;
    }
    public void Delivered(HealthNotice notice)
    {
        seen[notice.Key] = DateTimeOffset.UtcNow;
        if (notice.Recovered) seen.Remove(notice.Key[..^9] + ":Open");
        if (path is not null) ProductEnvironment.AtomicJson(path, seen);
    }
}
