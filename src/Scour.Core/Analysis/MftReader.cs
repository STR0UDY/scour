using System.Buffers.Binary;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Scour.Core.Analysis;

/// <summary>
/// Reads an NTFS volume's master file table directly, the way WizTree does, instead of walking
/// folders. Needs admin (it opens the raw volume). One sequential read of the MFT gives every
/// file's name, parent, size and allocated size.
/// </summary>
/// <remarks>
/// Handles fixups, extension records (attributes that spill out of the base record) and
/// non-resident, compressed and sparse data. Hard links count once, under their first name.
/// </remarks>
public static partial class MftReader
{
    public const ulong RootRecord = 5;

    private const uint AttrFileName = 0x30;
    private const uint AttrData = 0x80;
    private const uint AttrEnd = 0xFFFFFFFF;

    /// <summary>True if the drive is NTFS and this process can open it raw (i.e. is elevated).</summary>
    public static bool CanRead(string drive)
    {
        try
        {
            var info = new DriveInfo(drive);
            if (!info.IsReady || !info.DriveFormat.Equals("NTFS", StringComparison.OrdinalIgnoreCase))
                return false;
            using var handle = OpenVolume(info.Name);
            return !handle.IsInvalid;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or Win32Exception)
        {
            return false;
        }
    }

    /// <summary>Every in-use file and folder on the volume. Throws if the volume can't be opened.</summary>
    public static IReadOnlyList<RawEntry> Read(string drive, IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        var root = Path.GetPathRoot(Path.GetFullPath(drive)) ?? throw new ArgumentException("Not a drive.", nameof(drive));
        using var volume = OpenVolume(root);
        if (volume.IsInvalid)
            throw new UnauthorizedAccessException($"Can't open {root} for raw reading (needs admin).", new Win32Exception(Marshal.GetLastPInvokeError()));

        // Boot sector: geometry and where the MFT starts.
        // 4096 bytes covers 4Kn drives, where reads must be whole 4 KB sectors.
        var boot = new byte[4096];
        ReadAt(volume, 0, boot);
        if (Encoding.ASCII.GetString(boot, 3, 8) != "NTFS    ")
            throw new InvalidDataException($"{root} isn't NTFS.");
        var bytesPerSector = BinaryPrimitives.ReadUInt16LittleEndian(boot.AsSpan(0x0B));
        var clusterSize = bytesPerSector * (long)boot[0x0D];
        var mftCluster = BinaryPrimitives.ReadInt64LittleEndian(boot.AsSpan(0x30));
        var clustersPerRecord = (sbyte)boot[0x40];
        var recordSize = clustersPerRecord > 0 ? (int)(clustersPerRecord * clusterSize) : 1 << -clustersPerRecord;

        // Record 0 is $MFT itself; its $DATA runs say where the rest of the table lives.
        var first = new byte[Math.Max(recordSize, (int)clusterSize)];
        ReadAt(volume, mftCluster * clusterSize, first);
        if (!ApplyFixups(first.AsSpan(0, recordSize), bytesPerSector))
            throw new InvalidDataException("The MFT's first record is damaged.");
        var (mftRuns, mftSize) = DataRuns(first.AsSpan(0, recordSize));
        if (mftRuns.Count == 0)
            throw new InvalidDataException("Couldn't locate the MFT's data.");

        var totalRecords = mftSize / recordSize;
        var state = new Records(checked((int)totalRecords));

        // Read the table run by run in large aligned chunks.
        const int chunkTarget = 8 << 20;
        var chunkBytes = (int)(chunkTarget / clusterSize * clusterSize);
        var buffer = new byte[chunkBytes];
        long recordNumber = 0;
        foreach (var (lcn, clusters) in mftRuns)
        {
            var runBytes = clusters * clusterSize;
            for (long offset = 0; offset < runBytes && recordNumber < totalRecords; offset += chunkBytes)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var length = (int)Math.Min(chunkBytes, runBytes - offset);
                ReadAt(volume, lcn * clusterSize + offset, buffer.AsSpan(0, length));
                for (var pos = 0; pos + recordSize <= length && recordNumber < totalRecords; pos += recordSize, recordNumber++)
                    ParseRecord(buffer.AsSpan(pos, recordSize), (ulong)recordNumber, bytesPerSector, clusterSize, state);
                progress?.Report((double)recordNumber / totalRecords);
            }
        }

        return state.ToEntries();
    }

    // ---------------------------------------------------------------- Record parsing

    /// <summary>Per-record state, indexed by record number. Extension records write into their base record.</summary>
    private sealed class Records(int count)
    {
        public readonly string?[] Names = new string?[count];
        public readonly byte[] NameRank = new byte[count];
        public readonly ulong[] Parents = new ulong[count];
        public readonly long[] Sizes = new long[count];
        public readonly long[] Allocated = new long[count];
        public readonly byte[] Flags = new byte[count]; // 1 = in use, 2 = directory, 4 = base record seen

        public IReadOnlyList<RawEntry> ToEntries()
        {
            var entries = new List<RawEntry>(Names.Length / 2);
            for (var i = 0; i < Names.Length; i++)
            {
                if ((Flags[i] & 5) != 5 || Names[i] is not { } name || (ulong)i == RootRecord)
                    continue;
                var isDir = (Flags[i] & 2) != 0;
                entries.Add(new RawEntry((ulong)i, Parents[i], name, isDir, isDir ? 0 : Sizes[i], isDir ? 0 : Allocated[i]));
            }
            return entries;
        }
    }

    private static void ParseRecord(Span<byte> record, ulong number, int bytesPerSector, long clusterSize, Records state)
    {
        if (record[0] != (byte)'F' || record[1] != (byte)'I' || record[2] != (byte)'L' || record[3] != (byte)'E')
            return;
        var flags = BinaryPrimitives.ReadUInt16LittleEndian(record[0x16..]);
        if ((flags & 0x01) == 0)
            return; // Not in use.
        if (!ApplyFixups(record, bytesPerSector))
            return;

        // Extension records carry overflow attributes for their base record.
        var baseRef = BinaryPrimitives.ReadUInt64LittleEndian(record[0x20..]) & 0x0000FFFFFFFFFFFF;
        var target = baseRef == 0 ? number : baseRef;
        if (target >= (ulong)state.Names.Length)
            return;
        var t = (int)target;
        if (baseRef == 0)
            state.Flags[t] |= (byte)(5 | ((flags & 0x02) != 0 ? 2 : 0));

        var offset = (int)BinaryPrimitives.ReadUInt16LittleEndian(record[0x14..]);
        while (offset + 16 <= record.Length)
        {
            var type = BinaryPrimitives.ReadUInt32LittleEndian(record[offset..]);
            if (type == AttrEnd)
                break;
            var length = (int)BinaryPrimitives.ReadUInt32LittleEndian(record[(offset + 4)..]);
            if (length < 16 || offset + length > record.Length)
                break;
            var attr = record.Slice(offset, length);
            var nonResident = attr[8] != 0;
            var nameLength = attr[9];

            if (type == AttrFileName && !nonResident)
                ReadFileName(attr, state, t);
            else if (type == AttrData && nameLength == 0)
                ReadData(attr, nonResident, state, t);

            offset += length;
        }
    }

    private static void ReadFileName(Span<byte> attr, Records state, int target)
    {
        var contentOffset = BinaryPrimitives.ReadUInt16LittleEndian(attr[0x14..]);
        var content = attr[contentOffset..];
        if (content.Length < 0x42)
            return;
        var nameChars = content[0x40];
        var nameSpace = content[0x41];
        if (content.Length < 0x42 + nameChars * 2)
            return;

        // Prefer the Win32 name over the 8.3 DOS alias: POSIX=1, Win32 and Win32&DOS=2, DOS=0 rank.
        byte rank = nameSpace switch { 2 => 0, 0 => 1, _ => 2 };
        if (state.Names[target] is not null && rank <= state.NameRank[target] - 1)
            return;

        state.Names[target] = Encoding.Unicode.GetString(content.Slice(0x42, nameChars * 2));
        state.NameRank[target] = (byte)(rank + 1);
        state.Parents[target] = BinaryPrimitives.ReadUInt64LittleEndian(content) & 0x0000FFFFFFFFFFFF;
    }

    private static void ReadData(Span<byte> attr, bool nonResident, Records state, int target)
    {
        if (!nonResident)
        {
            // Small files live inside the MFT record: real size, no clusters of their own.
            state.Sizes[target] = BinaryPrimitives.ReadUInt32LittleEndian(attr[0x10..]);
            state.Allocated[target] = 0;
            return;
        }

        // Only the segment starting at VCN 0 carries the sizes.
        if (BinaryPrimitives.ReadInt64LittleEndian(attr[0x10..]) != 0)
            return;
        var attrFlags = BinaryPrimitives.ReadUInt16LittleEndian(attr[0x0C..]);
        var allocated = BinaryPrimitives.ReadInt64LittleEndian(attr[0x28..]);
        var real = BinaryPrimitives.ReadInt64LittleEndian(attr[0x30..]);
        // Compressed or sparse: the "total allocated" field is what's actually on disk.
        if ((attrFlags & 0x8001) != 0 && attr.Length >= 0x48)
            allocated = BinaryPrimitives.ReadInt64LittleEndian(attr[0x40..]);

        state.Sizes[target] = real;
        state.Allocated[target] = allocated;
    }

    /// <summary>The unnamed $DATA attribute's runs (LCN, cluster count) and real size.</summary>
    private static (List<(long Lcn, long Clusters)> Runs, long Size) DataRuns(Span<byte> record)
    {
        var offset = (int)BinaryPrimitives.ReadUInt16LittleEndian(record[0x14..]);
        while (offset + 16 <= record.Length)
        {
            var type = BinaryPrimitives.ReadUInt32LittleEndian(record[offset..]);
            if (type == AttrEnd)
                break;
            var length = (int)BinaryPrimitives.ReadUInt32LittleEndian(record[(offset + 4)..]);
            if (length < 16 || offset + length > record.Length)
                break;
            var attr = record.Slice(offset, length);
            if (type == AttrData && attr[8] != 0 && attr[9] == 0)
            {
                var size = BinaryPrimitives.ReadInt64LittleEndian(attr[0x30..]);
                var runsOffset = BinaryPrimitives.ReadUInt16LittleEndian(attr[0x20..]);
                return (DecodeRuns(attr[runsOffset..]), size);
            }
            offset += length;
        }
        return ([], 0);
    }

    internal static List<(long Lcn, long Clusters)> DecodeRuns(ReadOnlySpan<byte> runs)
    {
        var result = new List<(long, long)>();
        long lcn = 0;
        var pos = 0;
        while (pos < runs.Length && runs[pos] != 0)
        {
            var header = runs[pos++];
            var lengthBytes = header & 0x0F;
            var offsetBytes = header >> 4;
            if (pos + lengthBytes + offsetBytes > runs.Length)
                break;

            long clusters = 0;
            for (var i = 0; i < lengthBytes; i++)
                clusters |= (long)runs[pos + i] << (8 * i);
            pos += lengthBytes;

            if (offsetBytes == 0)
                continue; // Sparse run: no clusters on disk.

            long delta = 0;
            for (var i = 0; i < offsetBytes; i++)
                delta |= (long)runs[pos + i] << (8 * i);
            // Sign-extend.
            if ((runs[pos + offsetBytes - 1] & 0x80) != 0)
                delta |= -1L << (8 * offsetBytes);
            pos += offsetBytes;

            lcn += delta;
            result.Add((lcn, clusters));
        }
        return result;
    }

    /// <summary>
    /// NTFS stores the last two bytes of every sector in an update sequence array and puts a
    /// check value there instead, to detect torn writes. Put the real bytes back.
    /// </summary>
    internal static bool ApplyFixups(Span<byte> record, int bytesPerSector)
    {
        var usaOffset = BinaryPrimitives.ReadUInt16LittleEndian(record[0x04..]);
        var usaCount = BinaryPrimitives.ReadUInt16LittleEndian(record[0x06..]);
        if (usaCount < 2 || usaOffset + usaCount * 2 > record.Length)
            return false;
        var check = BinaryPrimitives.ReadUInt16LittleEndian(record[usaOffset..]);
        for (var i = 1; i < usaCount; i++)
        {
            var end = i * bytesPerSector - 2;
            if (end + 2 > record.Length)
                break;
            if (BinaryPrimitives.ReadUInt16LittleEndian(record[end..]) != check)
                return false;
            record[end] = record[usaOffset + i * 2];
            record[end + 1] = record[usaOffset + i * 2 + 1];
        }
        return true;
    }

    // ---------------------------------------------------------------- Volume I/O

    private static SafeFileHandle OpenVolume(string root)
    {
        var path = @"\\.\" + root.TrimEnd('\\');
        return CreateFileW(path, 0x80000000 /* GENERIC_READ */, 0x1 | 0x2 /* share read/write */, IntPtr.Zero, 3 /* OPEN_EXISTING */, 0, IntPtr.Zero);
    }

    private static void ReadAt(SafeFileHandle volume, long offset, Span<byte> buffer)
    {
        var done = 0;
        while (done < buffer.Length)
        {
            var read = RandomAccess.Read(volume, buffer[done..], offset + done);
            if (read <= 0)
                throw new EndOfStreamException("Unexpected end of volume while reading the MFT.");
            done += read;
        }
    }

    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    private static partial SafeFileHandle CreateFileW(string fileName, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);
}
