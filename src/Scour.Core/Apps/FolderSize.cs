namespace Scour.Core.Apps;

public static class FolderSize
{
    /// <summary>Total size of everything under a folder. Doesn't follow links; skips what it can't read.</summary>
    public static long Measure(string folder, CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(folder))
            return File.Exists(folder) ? new FileInfo(folder).Length : 0;

        long total = 0;
        var pending = new Stack<string>();
        pending.Push(folder);
        var options = new EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = 0 };
        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            IEnumerable<FileSystemInfo> entries;
            try
            {
                entries = new DirectoryInfo(pending.Pop()).EnumerateFileSystemInfos("*", options).ToList();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            foreach (var entry in entries)
            {
                if (entry.Attributes.HasFlag(FileAttributes.ReparsePoint))
                    continue;
                if (entry is FileInfo file)
                    total += file.Length;
                else
                    pending.Push(entry.FullName);
            }
        }
        return total;
    }
}
