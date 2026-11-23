using CloudDrive.Storage;
using System.Diagnostics;
using System.IO.Pipes;
using System.Text;

internal static class BridgeChecks
{
    public static async Task RunAsync(IndexStore index, SmbConnection config, Action<bool, string> check)
    {
        var large = new RemoteEntry(Paths.StableId(config.Id, "大文件 # &.bin"), config.Id, "大文件 # &.bin", false,
            1024L * 1024 * 1024 + 123, DateTime.UtcNow.ToFileTimeUtc(), DateTime.UtcNow.ToFileTimeUtc(), "synthetic-file-id");
        string generation = index.BeginScan(config.Id); index.SaveBatch(config.Id, generation, [large]);
        await using var bridge = new SmbBridge(index, config, _ => Task.FromResult<IRemoteSource>(new SyntheticSource(large)));
        bridge.Start();
        await using (var listing = new NamedPipeClientStream(".", bridge.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous))
        {
            await listing.ConnectAsync(5000); using var reader = new BinaryReader(listing, Encoding.Unicode, true); using var writer = new BinaryWriter(listing, Encoding.Unicode, true);
            writer.Write(Wire.Magic); writer.Write(1u); Wire.String(writer, ""); Wire.String(writer, ""); writer.Flush();
            check(reader.ReadUInt32() == 0 && reader.ReadUInt32() > 0, "IPC metadata page from SQLite");
        }
        await using (var client = await ConnectRead(bridge.PipeName, large))
        {
            using var reader = new BinaryReader(client, Encoding.Unicode, true); using var writer = new BinaryWriter(client, Encoding.Unicode, true);
            check(reader.ReadUInt32() == 0, "IPC opens indexed version");
            byte[] buffer = new byte[Wire.Chunk]; using var process = Process.GetCurrentProcess(); process.Refresh(); long baseline = process.PrivateMemorySize64, peak = baseline;
            for (long offset = 0; offset < large.Size;)
            {
                int count = (int)Math.Min(buffer.Length, large.Size - offset);
                writer.Write(offset); writer.Write(count); writer.Flush();
                if (reader.ReadUInt32() != 0 || reader.ReadInt32() != count) throw new Exception("IPC range failed.");
                reader.BaseStream.ReadExactly(buffer.AsSpan(0, count));
                if (buffer[0] != (byte)(offset % 251) || buffer[count - 1] != (byte)((offset + count - 1) % 251)) throw new Exception("IPC content mismatch.");
                offset += count;
                process.Refresh(); peak = Math.Max(peak, process.PrivateMemorySize64);
            }
            writer.Write(8192L); writer.Write(4096); writer.Flush();
            check(reader.ReadUInt32() == 0 && reader.ReadInt32() == 4096, "IPC nonzero repeated range"); reader.BaseStream.ReadExactly(buffer.AsSpan(0, 4096));
            check(buffer[0] == (byte)(8192 % 251), "IPC nonzero range content");
            writer.Write(0L); writer.Write(0); writer.Flush();
            check(peak - baseline < 128L * 1024 * 1024, "1 GiB plus tail streaming memory delta < 128 MiB (actual " + (peak - baseline) / 1024 / 1024 + " MiB)");
        }
        var changed = large with { ModifiedTime = large.ModifiedTime + 1 };
        index.SaveBatch(config.Id, generation, [changed]);
        await using (var stale = await ConnectRead(bridge.PipeName, large))
        {
            using var reader = new BinaryReader(stale); check(reader.ReadUInt32() != 0, "IPC refuses old placeholder version after index update");
        }
        await using (var abandoned = await ConnectRead(bridge.PipeName, changed))
        {
            using var reader = new BinaryReader(abandoned, Encoding.Unicode, true); _ = reader.ReadUInt32();
            // Disconnect during a request; the server must release its bounded worker and source handle.
        }
        await using (var next = await ConnectRead(bridge.PipeName, changed))
        {
            using var reader = new BinaryReader(next, Encoding.Unicode, true); check(reader.ReadUInt32() == 0, "IPC serves next request after disconnect");
            using var writer = new BinaryWriter(next, Encoding.Unicode, true); writer.Write(0L); writer.Write(0); writer.Flush();
        }
    }
    private static async Task<NamedPipeClientStream> ConnectRead(string pipe, RemoteEntry entry)
    {
        var client = new NamedPipeClientStream(".", pipe, PipeDirection.InOut, PipeOptions.Asynchronous); await client.ConnectAsync(5000);
        using var writer = new BinaryWriter(client, Encoding.Unicode, true);
        writer.Write(Wire.Magic); writer.Write(2u); writer.Write(Convert.FromHexString(entry.Id)); writer.Write(Wire.VersionStamp(entry)); writer.Write(entry.Size); writer.Write(entry.CreationTime); writer.Write(entry.ModifiedTime); writer.Flush(); return client;
    }
    private sealed class SyntheticSource(RemoteEntry entry) : IRemoteSource
    {
        public IEnumerable<RemoteEntry> Enumerate(string relative, CancellationToken token) => [entry];
        public Task<Stream> OpenReadAsync(RemoteEntry expected, CancellationToken token) => Task.FromResult<Stream>(new GeneratedStream(entry.Size));
    }
    private sealed class GeneratedStream(long size) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => false;
        public override long Length => size;
        public override long Position { get; set; }
        public override int Read(byte[] buffer, int offset, int count)
        {
            int read = (int)Math.Min(count, size - Position);
            for (int i = 0; i < read; i++) buffer[offset + i] = (byte)((Position + i) % 251);
            Position += read; return read;
        }
        public override long Seek(long offset, SeekOrigin origin) { Position = origin switch { SeekOrigin.Begin => offset, SeekOrigin.Current => Position + offset, _ => size + offset }; return Position; }
        public override void Flush() { }
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
