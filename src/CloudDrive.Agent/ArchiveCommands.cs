using System.Text.Json;
using CloudDrive.Storage;

internal static class ArchiveCommands
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    public static async Task<int> RunAsync(string[] args, IndexStore index, AgentRuntime runtime, CancellationToken token)
    {
        if (args is ["smb-archive-list"]) { Console.WriteLine(JsonSerializer.Serialize(index.ArchivePolicies(), Json)); return 0; }
        if (args is ["smb-archive-policy", "--config", var path])
        { index.SaveArchivePolicy(JsonSerializer.Deserialize<ArchivePolicy>(await File.ReadAllTextAsync(path, token)) ?? throw new ArgumentException("无效归档配置")); Console.WriteLine("归档配置已保存；校验和元数据保留始终启用。"); return 0; }
        if (args is ["smb-archive-global", var global])
        { index.ArchivePaused = global switch { "pause" => true, "resume" => false, _ => throw new ArgumentException("使用pause/resume") }; return 0; }
        if (args is ["smb-archive-watch"]) { await runtime.Archive.RunBackgroundAsync(token); return 0; }
        if (args is ["smb-archive-history"])
        { Console.WriteLine(JsonSerializer.Serialize(new { Totals = index.ArchiveStatistics(), Runs = index.ArchiveRuns(), Items = index.ArchiveItems() }, Json)); return 0; }
        if (args is ["smb-archive-run", var runId, var action])
        {
            Guid run = Guid.Parse(runId); index.ControlArchiveRun(run, action);
            if (action is "resume" or "retry") await runtime.Archive.ExecuteAsync(run, token);
            Console.WriteLine(JsonSerializer.Serialize(index.ArchiveItems(run), Json)); return index.ArchiveRuns().Single(r => r.Id == run).Status == "RecoveryRequired" ? 1 : 0;
        }
        if (args.Length != 2 || !Guid.TryParse(args[1], out Guid id)) throw new ArgumentException("归档命令需要任务 GUID。");
        if (args[0] == "smb-archive-delete") { index.DeleteArchivePolicy(id); return 0; }
        if (args[0] == "smb-archive-preview") { Console.WriteLine(JsonSerializer.Serialize(await runtime.Archive.PreviewAsync(index.GetArchivePolicy(id), token), Json)); return 0; }
        if (args[0] == "smb-archive-start")
        {
            Guid run = await runtime.Archive.StartAsync(id, token); Console.WriteLine(JsonSerializer.Serialize(index.ArchiveItems(run), Json));
            return index.ArchiveRuns().Single(r => r.Id == run).Status == "Completed" ? 0 : 1;
        }
        throw new ArgumentException("不支持的归档命令。");
    }
}
