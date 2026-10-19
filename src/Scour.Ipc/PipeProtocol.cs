using System.Buffers.Binary;
using System.Text;
using System.Text.Json;

namespace Scour.Ipc;

/// <summary>A disk entry from an MFT read, as sent over the pipe.</summary>
public readonly record struct EntryRecord(ulong Id, ulong ParentId, string Name, bool IsDirectory, long Size, long Allocated);

/// <summary>
/// Framing: one byte kind, four bytes little-endian length, then the payload. Kind 1 is a JSON
/// <see cref="IpcMessage"/>; kind 2 is a batch of binary <see cref="EntryRecord"/>s (MFT reads
/// are millions of entries, too many for JSON).
/// </summary>
public static class PipeProtocol
{
    public const int Version = 1;

    /// <summary>Upper bound for one frame, so a bad peer can't make us allocate gigabytes.</summary>
    public const int MaxFrame = 256 << 20;

    private const byte JsonFrame = 1;
    private const byte EntriesFrame = 2;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static async Task WriteAsync(Stream stream, IpcMessage message, CancellationToken cancellationToken = default)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(message, Json);
        await WriteFrameAsync(stream, JsonFrame, payload, cancellationToken);
    }

    public static async Task WriteEntriesAsync(Stream stream, IReadOnlyList<EntryRecord> entries, CancellationToken cancellationToken = default)
    {
        using var buffer = new MemoryStream();
        using (var writer = new BinaryWriter(buffer, Encoding.Unicode, leaveOpen: true))
        {
            writer.Write(entries.Count);
            foreach (var e in entries)
            {
                writer.Write(e.Id);
                writer.Write(e.ParentId);
                writer.Write(e.IsDirectory);
                writer.Write(e.Size);
                writer.Write(e.Allocated);
                writer.Write(e.Name);
            }
        }
        await WriteFrameAsync(stream, EntriesFrame, buffer.ToArray(), cancellationToken);
    }

    /// <summary>Reads one frame: a message, or a batch of entries. Null at end of stream.</summary>
    public static async Task<(IpcMessage? Message, IReadOnlyList<EntryRecord>? Entries)?> ReadAsync(Stream stream, CancellationToken cancellationToken = default)
    {
        var header = new byte[5];
        if (!await ReadExactlyAsync(stream, header, cancellationToken))
            return null;
        var length = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(1));
        if (length < 0 || length > MaxFrame)
            throw new InvalidDataException($"Frame of {length} bytes is out of range.");

        var payload = new byte[length];
        if (!await ReadExactlyAsync(stream, payload, cancellationToken))
            throw new EndOfStreamException("The pipe closed mid-frame.");

        switch (header[0])
        {
            case JsonFrame:
                return (JsonSerializer.Deserialize<IpcMessage>(payload, Json), null);
            case EntriesFrame:
                using (var reader = new BinaryReader(new MemoryStream(payload), Encoding.Unicode))
                {
                    var count = reader.ReadInt32();
                    var entries = new List<EntryRecord>(count);
                    for (var i = 0; i < count; i++)
                    {
                        var id = reader.ReadUInt64();
                        var parent = reader.ReadUInt64();
                        var isDir = reader.ReadBoolean();
                        var size = reader.ReadInt64();
                        var allocated = reader.ReadInt64();
                        var name = reader.ReadString();
                        entries.Add(new EntryRecord(id, parent, name, isDir, size, allocated));
                    }
                    return (null, entries);
                }
            default:
                throw new InvalidDataException($"Unknown frame kind {header[0]}.");
        }
    }

    private static async Task WriteFrameAsync(Stream stream, byte kind, byte[] payload, CancellationToken cancellationToken)
    {
        var header = new byte[5];
        header[0] = kind;
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(1), payload.Length);
        await stream.WriteAsync(header, cancellationToken);
        await stream.WriteAsync(payload, cancellationToken);
        await stream.FlushAsync(cancellationToken);
    }

    private static async Task<bool> ReadExactlyAsync(Stream stream, byte[] buffer, CancellationToken cancellationToken)
    {
        var read = 0;
        while (read < buffer.Length)
        {
            var n = await stream.ReadAsync(buffer.AsMemory(read), cancellationToken);
            if (n == 0)
                return read == 0 ? false : throw new EndOfStreamException("The pipe closed mid-frame.");
            read += n;
        }
        return true;
    }
}
