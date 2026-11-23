namespace CloudDrive.Desktop;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        bool smoke = args.Contains("--smoke", StringComparer.Ordinal);
        if (smoke)
        {
            string errorPath = Path.Combine(AppContext.BaseDirectory, "desktop-smoke-error.txt"); File.Delete(errorPath);
            Application.ThreadException += (_, e) =>
            {
                File.WriteAllText(errorPath, e.Exception.ToString()); Environment.ExitCode = 1; Application.Exit();
            };
        }
        using var singleton = new Mutex(true, @"Local\CloudDriveLite.UI." + CloudDrive.Storage.ProductEnvironment.UserKey, out bool first);
        if (!first && !smoke)
        {
            if (EventWaitHandle.TryOpenExisting(@"Local\CloudDriveLite.Show." + CloudDrive.Storage.ProductEnvironment.UserKey, out var show)) using (show) show.Set();
            return;
        }
        using var window = new MainForm(smoke, args.Contains("--connections", StringComparer.Ordinal));
        Application.Run(window);
    }
}
