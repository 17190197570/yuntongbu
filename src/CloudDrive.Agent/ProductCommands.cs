using CloudDrive.Storage;
using System.Text.Json;

internal static class ProductCommands
{
    public static async Task<int> Run(string[] args)
    {
        var index = new IndexStore(ProductEnvironment.DatabasePath); ProductEnvironment.EnsureDirectories();
        switch (args)
        {
            case ["product-upgrade-prepare"]:
                await ProductMaintenance.StopAsync(index, CancellationToken.None);
                using (ProductMaintenance.AcquireOffline(index))
                {
                    string backup = ProductBackup.Create(index, Path.Combine(ProductEnvironment.DataRoot, "Backups"));
                    ProductEnvironment.AtomicJson(ProductMaintenance.Marker, new { At = DateTimeOffset.UtcNow, Backup = backup, Reason = "Upgrade" });
                }
                return 0;
            case ["product-upgrade-finish"]:
                if (!index.CheckIntegrity()) return 3;
                if (File.Exists(ProductMaintenance.Marker)) File.Delete(ProductMaintenance.Marker); return 0;
            case ["product-uninstall-check"]:
                await ProductMaintenance.StopAsync(index, CancellationToken.None);
                using (ProductMaintenance.AcquireOffline(index))
                {
                    var runtime = new AgentRuntime(index, new CredentialVault());
                    var assessment = ProductUninstall.Assess(index, runtime.CachePlatform);
                    ProductEnvironment.AtomicJson(Path.Combine(ProductEnvironment.DataRoot, "Diagnostics", "uninstall-assessment.json"), assessment);
                    Console.WriteLine(JsonSerializer.Serialize(assessment));
                    if (!assessment.Allowed) return 3;
                    LoginStartup.Set(false); return 0;
                }
            case ["product-backup"]:
                Console.WriteLine(ProductBackup.Create(index, Path.Combine(ProductEnvironment.DataRoot, "Backups"))); return 0;
            case ["product-diagnostics"]:
                ProductSnapshot? snapshot = null;
                try { snapshot = (await ManagementClient.SendAsync(new("status"), timeoutSeconds: 2)).Snapshot; } catch (Exception e) when (e is IOException or OperationCanceledException or TimeoutException) { }
                Console.WriteLine(ProductDiagnostics.Export(ProductDiagnostics.Preview(index, snapshot), Path.Combine(ProductEnvironment.DataRoot, "Diagnostics"))); return 0;
            default: throw new ArgumentException("无效产品运维命令。");
        }
    }
}
