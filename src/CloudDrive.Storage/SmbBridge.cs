using System.Buffers.Binary;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;

namespace CloudDrive.Storage;

public static class Wire
{
    public const uint Magic = 0x324C4443;
    public const int Chunk = 1024 * 1024;
    public static ulong VersionStamp(RemoteEntry entry) => BinaryPrimitives.ReadUInt64LittleEndian(SHA256.HashData(Encoding.UTF8.GetBytes(entry.RemoteId ?? "")));
    public static void String(BinaryWriter writer, string value)
    {
        var bytes = Encoding.Unicode.GetBytes(value); writer.Write((uint)bytes.Length); writer.Write(bytes);
    }
    public static string String(BinaryReader reader)
    {
        int count = checked((int)reader.ReadUInt32()); if (count < 0 || count > 16384 || count % 2 != 0) throw new IOException("Invalid wire string.");
        byte[] bytes = reader.ReadBytes(count); if (bytes.Length != count) throw new EndOfStreamException();
        return new UnicodeEncoding(false, false, true).GetString(bytes);
    }
    public static void Entry(BinaryWriter writer, RemoteEntry entry)
    {
        String(writer, entry.RelativePath); writer.Write(Convert.FromHexString(entry.Id)); writer.Write(VersionStamp(entry));
        writer.Write(entry.Size); writer.Write(entry.CreationTime); writer.Write(entry.ModifiedTime); writer.Write(entry.IsDirectory ? 1u : 0u);
    }
    public static (long Offset, long End) Range(long size, long offset, long length)
    {
        if (size < 0 || offset < 0 || length < 0 || offset > size || length > size - offset) throw new ArgumentOutOfRangeException(nameof(length));
        long start = offset & ~4095L;
        long end = offset + length;
        if (end < size) end = end > long.MaxValue - 4095 ? size : Math.Min(size, (end + 4095) & ~4095L);
        return (start, end);
    }
}

// One pipe session owns one remote read handle. Bounded sessions and chunk buffers prevent whole-file buffering.
public sealed class SmbBridge(IndexStore index, SmbConnection config, Func<CancellationToken, Task<IRemoteSource>> getSource,
    Func<SmbConnection, CancellationToken, Task<IRemoteSource>>? getMappedSource = null) : IAsyncDisposable
{
    public string PipeName { get; } = "CloudDriveLite.Smb." + Guid.NewGuid().ToString("N");
    private readonly CancellationTokenSource stop = new();
    private readonly SemaphoreSlim sessions = new(4, 4);
    private readonly List<Task> workers = [];
    private Task? listener;
    private static int activeReads;
    public static int ActiveReads => Volatile.Read(ref activeReads);
    public void Start() => listener = ListenAsync();
    private async Task ListenAsync()
    {
        try
        {
            while (!stop.IsCancellationRequested)
            {
                await sessions.WaitAsync(stop.Token);
                var pipe = new NamedPipeServerStream(PipeName, PipeDirection.InOut, 5, PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly, Wire.Chunk, Wire.Chunk);
                try { await pipe.WaitForConnectionAsync(stop.Token); }
                catch { pipe.Dispose(); sessions.Release(); throw; }
                var task = ServeAsync(pipe);
                lock (workers) { workers.RemoveAll(t => t.IsCompleted); workers.Add(task); }
            }
        }
        catch (OperationCanceledException) { }
    }
    private async Task ServeAsync(NamedPipeServerStream pipe)
    {
        bool reading = false;
        await using (pipe)
        {
            try
            {
                using var reader = new BinaryReader(pipe, Encoding.Unicode, true);
                using var writer = new BinaryWriter(pipe, Encoding.Unicode, true);
                // Parsing is synchronous on a bounded worker; deadlines close the pipe to unblock it.
                await Task.Run(async () =>
                {
                    using var deadline = CancellationTokenSource.CreateLinkedTokenSource(stop.Token);
                    deadline.CancelAfter(TimeSpan.FromSeconds(30));
                    using var interrupt = deadline.Token.Register(() => pipe.Dispose());
                    if (reader.ReadUInt32() != Wire.Magic) throw new IOException("Unknown bridge protocol.");
                    uint operation = reader.ReadUInt32();
                    if (operation == 1)
                    {
                        string parent = Wire.String(reader), after = Wire.String(reader);
                        if (parent.Length > 0) Paths.Relative(parent); if (after.Length > 0) Paths.Relative(after);
                        var entries = index.Page(config.Id, parent, after);
                        writer.Write(0u); writer.Write((uint)entries.Count); foreach (var entry in entries) Wire.Entry(writer, entry); writer.Flush();
                    }
                    else if (operation == 2)
                    {
                        reading = true; Interlocked.Increment(ref activeReads);
                        byte[] idBytes = reader.ReadBytes(16); if (idBytes.Length != 16) throw new EndOfStreamException();
                        ulong stamp = reader.ReadUInt64(); long size = reader.ReadInt64(), created = reader.ReadInt64(), modified = reader.ReadInt64();
                        var entry = index.Find(config.Id, Convert.ToHexString(idBytes));
                        if (entry is null || entry.IsDirectory || entry.Size != size || entry.CreationTime != created || entry.ModifiedTime != modified || Wire.VersionStamp(entry) != stamp)
                            throw new StorageException(FailureKind.SourceChanged, "Index version mismatch.");
                        var mapping = index.StorageMapping(entry.Id);
                        IRemoteSource source; RemoteEntry physical;
                        if (mapping is null) { source = await getSource(deadline.Token); physical = entry; }
                        else
                        {
                            var routed = index.ResolveStorage(entry); physical = routed.Entry;
                            source = getMappedSource is null ? throw new IOException("归档映射需要显式目标连接解析；拒绝回退原NAS。") : await getMappedSource(routed.Connection, deadline.Token);
                        }
                        await using var stream = await source.OpenReadAsync(physical, deadline.Token);
                        if (!stream.CanSeek || stream.Length != entry.Size) throw new StorageException(FailureKind.SourceChanged, "Invalid source stream.");
                        writer.Write(0u); writer.Flush();
                        byte[] buffer = new byte[Wire.Chunk]; long transferred = 0, lastLog = 0;
                        index.Log(config.Id, "hydration", "读取 " + entry.RelativePath);
                        while (!deadline.IsCancellationRequested)
                        {
                            deadline.CancelAfter(TimeSpan.FromSeconds(30));
                            long offset = reader.ReadInt64(); int count = reader.ReadInt32();
                            if (count == 0) break;
                            if (count == -1 && offset == 0) { writer.Write(0u); writer.Flush(); continue; }
                            if (count < 0 || count > buffer.Length || offset < 0 || offset > size || count > size - offset) throw new IOException("Invalid read range.");
                            stream.Position = offset;
                            await stream.ReadExactlyAsync(buffer.AsMemory(0, count), deadline.Token);
                            writer.Write(0u); writer.Write(count); writer.Write(buffer, 0, count); writer.Flush();
                            transferred += count;
                            if (transferred - lastLog >= 16 * 1024 * 1024) { index.Log(config.Id, "progress", entry.RelativePath + " transferred=" + transferred + " size=" + size); lastLog = transferred; }
                        }
                        index.Log(config.Id, "range-served", entry.RelativePath + " bytes=" + transferred + "（CFAPI 完成状态由 Host 确认）");
                    }
                    else throw new IOException("Unknown bridge operation.");
                }, stop.Token);
            }
            catch (Exception error)
            {
                try
                {
                    index.Log(config.Id, "bridge-failure", FailureClassifier.Describe("bridge", error).Message);
                    var bytes = BitConverter.GetBytes(1u); await pipe.WriteAsync(bytes, stop.Token); await pipe.FlushAsync(stop.Token);
                }
                catch (Exception nested) when (nested is IOException or ObjectDisposedException or OperationCanceledException) { }
            }
            finally { if (reading) Interlocked.Decrement(ref activeReads); sessions.Release(); }
        }
    }
    public async ValueTask DisposeAsync()
    {
        await stop.CancelAsync();
        if (listener is not null) await listener;
        Task[] pending; lock (workers) pending = workers.ToArray();
        await Task.WhenAll(pending); stop.Dispose(); sessions.Dispose();
    }
}
