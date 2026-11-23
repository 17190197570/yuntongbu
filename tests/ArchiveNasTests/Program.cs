using System.Text.Json;
using CloudDrive.Storage;

if (args is not [var sourceId, var targetId, var relative, var targetDirectory, "--allow-source-delete"])
    throw new ArgumentException("需要源连接GUID、目标GUID、专用旧测试文件相对路径、目标目录及--allow-source-delete。成功后将删除该源测试文件。不要使用业务文件。");
Paths.Relative(relative); ArchivePaths.Optional(targetDirectory);
var index = new IndexStore(Path.Combine(IndexStore.DefaultDirectory, "index.db"));
var source = index.Get(Guid.Parse(sourceId)) ?? throw new ArgumentException("源连接不存在");
var target = index.Get(Guid.Parse(targetId)) ?? throw new ArgumentException("目标连接不存在");
if (source.Id == target.Id) throw new ArgumentException("需要两个不同的存储连接。");
if (!EventWaitHandle.TryOpenExisting(AgentRuntime.EventName(source.Id, "Stop"), out var agent)) throw new IOException("先运行源连接的 Agent，保证归档后实际 CFAPI 水合可验证。");
agent.Dispose();
if (index.SyncSettings(source.Id).Enabled || index.CachePolicy(source.Id).Enabled || index.ArchivePolicies().Any(p => p.Enabled)) throw new IOException("测试前先暂停新增上传、源连接自动缓存清理和所有自动归档计划。");
var runtime = new AgentRuntime(index, new CredentialVault(), Path.GetFullPath("artifacts/bin"));
var entry = index.Find(source.Id, Paths.StableId(source.Id, relative)) ?? throw new IOException("需要现有已扫描的专用旧测试文件。");
var policy = new ArchivePolicy(Guid.NewGuid(), "双NAS归档验收-" + DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss"), source.Id, target.Id, Paths.Parent(relative), targetDirectory);
var before = runtime.CachePlatform.Probe(source, relative); if (before.Pinned) throw new IOException("使用未固定的专用测试文件。");
string root = await runtime.Mapper.ConnectAsync(source, CancellationToken.None);
string expectedHash;
await using (var stream = await new FileSystemSource(source.Id, root).OpenReadAsync(entry, CancellationToken.None)) expectedHash = await NewFileUpload.StreamHash(stream, CancellationToken.None);
var preview = await runtime.Archive.PreviewAsync(policy, CancellationToken.None); var selected = preview.Files.SingleOrDefault(f => f.Entry.Id == entry.Id);
if (selected?.Eligible != true) throw new IOException("文件未满足原始时间AND规则及安全条件：" + selected?.Reason);
index.SaveArchivePolicy(policy); Guid run = index.QueueArchive(policy, [selected]);
try { await runtime.Archive.ExecuteAsync(run, CancellationToken.None); }
catch (IOException) when (index.ArchiveRuns().Single(r => r.Id == run).Status == "Completed") { /* The owning Agent may have safely completed the same persisted run. */ }
var item = index.ArchiveItems(run).Single();
if (item.Status != ArchiveStatus.Completed || !item.SourceDeleted || item.SourceHash != expectedHash || item.TargetHash != expectedHash || index.ResolveStorage(entry).Connection.Id != target.Id) throw new IOException("归档未完成，保留任务用于恢复：" + JsonSerializer.Serialize(item));
string path = Paths.Under(source.SyncRoot, relative);
if (!File.Exists(path) || runtime.CachePlatform.Probe(source, relative).LogicalBytes != entry.Size) throw new IOException("原本地占位路径/大小不符。");
await using (var local = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
    if (await NewFileUpload.StreamHash(local, CancellationToken.None) != expectedHash) throw new IOException("归档后原CFAPI路径读取哈希不符。");
string log = Path.GetFullPath(Path.Combine("artifacts", "tests", "nas-archive-" + run.ToString("N") + ".json")); Directory.CreateDirectory(Path.GetDirectoryName(log)!);
await File.WriteAllTextAsync(log, JsonSerializer.Serialize(new { Scope = "ACTUAL TWO CONFIGURED SMB CONNECTIONS", Policy = policy, Item = item, Before = before, After = runtime.CachePlatform.Probe(source, relative), EditingUpload = "NOT IMPLEMENTED / NOT ACCEPTED" }, new JsonSerializerOptions { WriteIndented = true }));
Console.WriteLine("PASS configured SMB archive and original CFAPI path hydration; source test file deleted after verified mapping. Report: " + log);
