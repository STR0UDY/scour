using System.Collections.Concurrent;
using System.IO.Enumeration;
using System.Runtime.InteropServices;

namespace Scour.Core.Analysis;

/// <summary>
/// The fallback when the MFT can't be read (no admin, or not NTFS): walks folders in parallel
/// with large directory reads. Slower than the MFT, but works anywhere and needs no rights.
/// Folders it can't open are counted, not fatal.
/// </summary>
public static partial class FileSystemWalker
{
    public sealed record Result(IReadOnlyList<RawEntry> Entries, ulong RootId, int InaccessibleFolders);

    public static Result Walk(string root, IProgress<long>? progress = null, CancellationToken cancellationToken = default)
    {
        root = Path.GetFullPath(root);
        var clusterSize = ClusterSize(root);
        var entries = new ConcurrentBag<RawEntry>();
        long nextId = 1;
        long seen = 0;
        var inaccessible = 0;
        const ulong rootId = 0;

        var work = new BlockingCollection<(string Path, ulong Id)>();
        var pending = 1;
        work.Add((root, rootId));

        void Process((string Path, ulong Id) dir)
        {
            try
            {
                var enumerable = new FileSystemEnumerable<(RawEntry Entry, bool IsLink)>(dir.Path,
                    (ref FileSystemEntry e) =>
                    {
                        var isDir = e.IsDirectory;
                        var id = (ulong)Interlocked.Increment(ref nextId);
                        var length = isDir ? 0 : e.Length;
                        var entry = new RawEntry(id, dir.Id, e.FileName.ToString(), isDir, length, isDir ? 0 : Allocated(e, length, clusterSize));
                        return (entry, (e.Attributes & FileAttributes.ReparsePoint) != 0);
                    },
                    new EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = 0, RecurseSubdirectories = false });

                foreach (var (entry, isLink) in enumerable)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    entries.Add(entry);
                    // Links are listed but never followed, so nothing is counted twice.
                    if (entry.IsDirectory && !isLink)
                    {
                        Interlocked.Increment(ref pending);
                        work.Add((Path.Combine(dir.Path, entry.Name), entry.Id));
                    }
                }
                var total = Interlocked.Add(ref seen, 1);
                if (total % 500 == 0)
                    progress?.Report(entries.Count);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Interlocked.Increment(ref inaccessible);
            }
            finally
            {
                if (Interlocked.Decrement(ref pending) == 0)
                    work.CompleteAdding();
            }
        }

        var workers = Enumerable.Range(0, Math.Clamp(Environment.ProcessorCount, 2, 8)).Select(_ => Task.Run(() =>
        {
            foreach (var dir in work.GetConsumingEnumerable(cancellationToken))
                Process(dir);
        }, cancellationToken)).ToArray();
        Task.WaitAll(workers, cancellationToken);
        progress?.Report(entries.Count);

        var list = new List<RawEntry>(entries.Count);
        list.AddRange(entries);
        entries.Clear();
        return new Result(list, rootId, inaccessible);
    }

    private static long Allocated(in FileSystemEntry e, long length, long clusterSize)
    {
        // Compressed and sparse files take less than their length; ask the filesystem.
        if ((e.Attributes & (FileAttributes.Compressed | FileAttributes.SparseFile)) != 0)
        {
            var path = e.ToFullPath();
            var low = GetCompressedFileSizeW(path, out var high);
            if (low != 0xFFFFFFFF || Marshal.GetLastPInvokeError() == 0)
                return ((long)high << 32) | low;
        }
        return clusterSize <= 0 ? length : (length + clusterSize - 1) / clusterSize * clusterSize;
    }

    private static long ClusterSize(string root)
    {
        var volume = Path.GetPathRoot(root);
        if (volume is not null && GetDiskFreeSpaceW(volume, out var sectorsPerCluster, out var bytesPerSector, out _, out _))
            return (long)sectorsPerCluster * bytesPerSector;
        return 4096;
    }

    [LibraryImport("kernel32.dll", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    private static partial uint GetCompressedFileSizeW(string fileName, out uint fileSizeHigh);

    [LibraryImport("kernel32.dll", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetDiskFreeSpaceW(string root, out uint sectorsPerCluster, out uint bytesPerSector, out uint freeClusters, out uint totalClusters);
}
