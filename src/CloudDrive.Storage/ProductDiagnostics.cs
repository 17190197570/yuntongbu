using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace CloudDrive.Storage;

public sealed record DiagnosticReport(string Version, DateTimeOffset At, string Windows, string Architecture,
    bool CfapiAvailable, bool DatabaseHealthy, ProductTotals Totals, ProductSnapshot? Runtime, int Connections,
    string SmbPortCapability, string Credentials, string Limits, HealthSnapshot? Health = null);
public static class ProductDiagnostics
{
    public static DiagnosticReport Preview(IndexStore index, ProductSnapshot? runtime)
    {
        bool cfapi = NativeLibrary.TryLoad("cldapi.dll", out nint module); if (cfapi) NativeLibrary.Free(module);
        return new(ProductEnvironment.Version, DateTimeOffset.UtcNow, RuntimeInformation.OSDescription, RuntimeInformation.OSArchitecture.ToString(), cfapi,
            index.CheckIntegrity(), index.ProductTotals(), runtime, index.Connections().Count,
            OperatingSystem.IsWindowsVersionAtLeast(10, 0, 26100) ? "系统版本满足；New-SmbMapping TcpPort参数与UAC仍需连接测试确认。" : "自定义端口不支持；445使用独立能力检查。",
            "未读取/导出凭据；恢复到其他用户/设备后需要重新输入。",
            "未导出服务器、账号、完整路径、原始日志、文件正文或数据库；Ready为Host最后一次报告；健康中心独立记录探针时间。完整双向同步与真实NAS未验收。", HealthReports.Preview(index.ReadHealth()).Health);
    }
    public static string Export(DiagnosticReport preview, string directory)
    {
        Directory.CreateDirectory(directory); string path = Path.Combine(directory, "CloudDriveLite-Diagnostics-" + DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss") + ".json");
        ProductEnvironment.AtomicJson(path, preview); return path;
    }
    public static string Render(DiagnosticReport preview) => JsonSerializer.Serialize(preview, new JsonSerializerOptions { WriteIndented = true });
}

public sealed record UninstallAssessment(bool Allowed, long UnfinishedTasks, int ManagedRoots, long UnknownOrModified,
    long OnlineOnly, long Pinned, long BytesToHydrate, string Reason);
public static class ProductUninstall
{
    public static UninstallAssessment Assess(IndexStore index, ICachePlatform platform)
    {
        long unknown = 0, online = 0, pinned = 0, hydrate = 0; int roots = 0;
        foreach (var c in index.Connections())
        {
            if (!Directory.Exists(c.SyncRoot)) { unknown++; continue; }
            roots++;
            // Walk actual namespace. Database cache statistics alone cannot authorize uninstall.
            var stack = new Stack<string>(); stack.Push(c.SyncRoot);
            while (stack.TryPop(out string? path))
            {
                foreach (var item in new DirectoryInfo(path).EnumerateFileSystemInfos("*", new EnumerationOptions { AttributesToSkip = 0, IgnoreInaccessible = false }))
                {
                    string relative = Paths.Relative(Path.GetRelativePath(c.SyncRoot, item.FullName));
                    var state = platform.Probe(c, relative);
                    if (!state.Owned || state.Error is not null || !state.InSync || state.ModifiedBytes != 0) unknown++;
                    if (state.Pinned) pinned++;
                    if (!state.IsDirectory && state.HydratedBytes < state.LogicalBytes) { online++; hydrate += state.LogicalBytes - state.HydratedBytes; }
                    if (state.IsDirectory && ((item.Attributes & FileAttributes.ReparsePoint) == 0 || state.Owned)) stack.Push(item.FullName);
                }
            }
        }
        long unfinished = index.UnfinishedWork();
        // Native unregister currently only supports empty, already stopped roots. Never remove the provider
        // while any configured root could still depend on it, even if every file appears hydrated.
        bool allowed = index.CheckIntegrity() && !index.HasOrphanedMappings() && index.Connections().Count == 0 && index.Storages().Count == 0 && unfinished == 0;
        return new(allowed, unfinished, roots, unknown, online, pinned, hydrate,
            allowed ? "没有配置的受管同步根或未完成任务；仅允许移除程序与启动项，保留用户数据。" :
            "禁止直接卸载：仍有配置/受管根或未完成任务。本开发版尚未实现非空根的普通文件转换及占位清理；请取消卸载，保留所有本地与NAS文件。");
    }
}
