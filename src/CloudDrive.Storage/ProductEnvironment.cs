using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;

namespace CloudDrive.Storage;

public static class ProductEnvironment
{
    public static string Version => typeof(ProductEnvironment).Assembly.GetName().Version!.ToString(3);
    public static string DataRoot => IndexStore.DefaultDirectory;
    // Keep the original database in place. Never choose a new empty database over legacy mappings.
    public static string DatabasePath => File.Exists(Path.Combine(DataRoot, "index.db"))
        ? Path.Combine(DataRoot, "index.db") : Path.Combine(DataRoot, "Database", "index.db");
    public static string UserKey => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
        WindowsIdentity.GetCurrent().User?.Value ?? throw new IOException("缺少当前用户SID。"))))[..24];
    public static string PipeName => "CloudDriveLite.Management." + UserKey + "." + Process.GetCurrentProcess().SessionId;
    public static string SettingsPath => Path.Combine(DataRoot, "Config", "product.json");
    public static string LogRoot => Path.Combine(DataRoot, "Logs");
    public static void EnsureDirectories()
    {
        foreach (string directory in new[] { "Database", "Config", "Logs", "Cache", "Backups", "Diagnostics" }) Directory.CreateDirectory(Path.Combine(DataRoot, directory));
    }
    public static ProcessStartInfo StartInfo(string assembly, params string[] args)
        => StartInfoAt(AppContext.BaseDirectory, assembly, args);
    public static ProcessStartInfo StartInfoAt(string directory, string assembly, params string[] args)
    {
        // Framework-dependent developer builds use the same runtime; self-contained releases use their apphost.
        string executable = Path.Combine(directory, assembly + ".exe");
        bool selfContained = File.Exists(Path.Combine(directory, "coreclr.dll"));
        var start = selfContained ? new ProcessStartInfo(executable) : new ProcessStartInfo(Path.GetFullPath(Path.Combine(RuntimeEnvironment.GetRuntimeDirectory(), "../../..", "dotnet.exe")));
        if (!selfContained) start.ArgumentList.Add(Path.Combine(directory, assembly + ".dll"));
        start.UseShellExecute = false; start.CreateNoWindow = true;
        foreach (string argument in args) start.ArgumentList.Add(argument);
        return start;
    }
    public static void AtomicJson<T>(string path, T value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        string temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { JsonSerializer.Serialize(stream, value, new JsonSerializerOptions { WriteIndented = true }); stream.Flush(true); }
            File.Move(temp, path, true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
}

public sealed record ProductSettings(bool LoginStartup = false, int StartupDelaySeconds = 15,
    bool SyncPaused = false, bool CheckUpdates = false, string? UpdateManifestUrl = null,
    string UpdateChannel = "stable", int UpdateHours = 24, int BackupHours = 24, int BackupKeep = 7,
    int LogDays = 30, int LogFileMiB = 10, string LogLevel = "Information", bool HealthWarningNotifications = false, bool HealthRecoveryNotifications = true)
{
    public ProductSettings Validate()
    {
        if (StartupDelaySeconds is < 0 or > 600 || UpdateHours is < 1 or > 720 || BackupHours is < 1 or > 720 || BackupKeep is < 1 or > 100 || LogDays is < 1 or > 365 || LogFileMiB is < 1 or > 100 ||
            LogLevel is not ("Debug" or "Information" or "Warning" or "Error") || UpdateChannel is not ("stable" or "preview")) throw new ArgumentException("产品设置参数超出范围。");
        if (UpdateManifestUrl is not null && (!Uri.TryCreate(UpdateManifestUrl, UriKind.Absolute, out var uri) || uri.Scheme != "https" || uri.UserInfo.Length != 0)) throw new ArgumentException("更新地址必须是无凭据的HTTPS地址。");
        return this;
    }
    public static ProductSettings Load(string? path = null) => File.Exists(path ?? ProductEnvironment.SettingsPath)
        ? (JsonSerializer.Deserialize<ProductSettings>(File.ReadAllText(path ?? ProductEnvironment.SettingsPath)) ?? throw new IOException("设置文件无效；拒绝重置运行策略。")).Validate() : new();
    public static ProductSettings Change(Func<ProductSettings, ProductSettings> change, string? path = null)
    {
        path ??= ProductEnvironment.SettingsPath;
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        using var gate = new FileStream(path + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        var value = change(Load(path)).Validate(); ProductEnvironment.AtomicJson(path, value); return value;
    }
}

public sealed class RestartBudget(int maximum = 5)
{
    private readonly Queue<DateTimeOffset> failures = [];
    public TimeSpan? Failure(DateTimeOffset now)
    {
        while (failures.TryPeek(out var first) && now - first > TimeSpan.FromMinutes(10)) failures.Dequeue();
        failures.Enqueue(now); return failures.Count > maximum ? null : TimeSpan.FromSeconds(Math.Min(120, 2 << Math.Min(failures.Count - 1, 6)));
    }
}
