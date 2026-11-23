using Microsoft.Win32;

namespace CloudDrive.Storage;

public static class LoginStartup
{
    public static bool Enabled
    {
        get { using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run"); return key?.GetValue("CloudDriveLite") is string; }
    }
    public static void Set(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
        if (enabled)
        {
            var start = ProductEnvironment.StartInfo("CloudDrive.Bootstrap", "--login");
            string Quote(string value) => "\"" + value.Replace("\"", "", StringComparison.Ordinal) + "\"";
            key.SetValue("CloudDriveLite", Quote(start.FileName) + " " + string.Join(" ", start.ArgumentList.Select(Quote)));
        }
        else key.DeleteValue("CloudDriveLite", false);
        ProductSettings.Change(s => s with { LoginStartup = enabled });
    }
}
