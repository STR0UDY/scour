using Scour.Core.Cleaning;
using Scour.Core.Paths;
using Scour.Core.Rules;
using Scour.Core.Safety;
using Scour.Core.Scanning;

namespace Scour.Core.Apps;

/// <summary>
/// Removes leftovers the user ticked, through the same <see cref="Cleaner"/> as rules: each
/// file re-checked and deleted through its own handle, links never followed, everything logged.
/// </summary>
public static class LeftoverCleaner
{
    public const string LogRuleId = "apps.leftovers";

    public static CleanSummary Delete(IEnumerable<Leftover> leftovers, DenyList denyList, bool useRecycleBin, IDeletionLog? log = null, CancellationToken cancellationToken = default)
    {
        var files = new List<ScanFile>();
        var recycled = new List<CleanFileResult>();

        foreach (var leftover in leftovers.Where(l => l.CanDelete))
        {
            var path = PathNormalizer.Normalize(leftover.Path);
            var parent = Path.GetDirectoryName(path);
            if (parent is null)
                continue;

            if (leftover.Kind == LeftoverKind.Shortcut)
            {
                var file = new FileInfo(path);
                if (file.Exists)
                    files.Add(new ScanFile(path, file.Length, file.LastWriteTimeUtc, parent));
                continue;
            }

            var dir = new DirectoryInfo(path);
            if (!dir.Exists || dir.Attributes.HasFlag(FileAttributes.ReparsePoint) || denyList.IsProtectedRoot(path, recurse: true, "*"))
                continue;

            if (useRecycleBin)
            {
                // One folder in the bin is easier to restore than its files scattered.
                var size = FolderSize.Measure(path, cancellationToken);
                var error = RecycleBin.Send(path);
                var result = new CleanFileResult(path, size, LogRuleId, error == 0 ? DeleteOutcome.Recycled : DeleteOutcome.Failed,
                    error == 0 ? null : $"Shell error {error}");
                recycled.Add(result);
                log?.Write(result);
                continue;
            }

            foreach (var file in EnumerateFiles(dir, cancellationToken))
                files.Add(new ScanFile(file.FullName, file.Length, file.LastWriteTimeUtc, parent, PruneFolders: true));
        }

        var scan = new RuleScanResult
        {
            Rule = new Rule { Id = LogRuleId, Name = "Leftovers", Category = RuleCategories.Apps, Detect = ["%SystemRoot%"] },
            Status = RuleScanStatus.Ok,
            Files = files,
        };
        var summary = new Cleaner(denyList).Clean([scan], new CleanOptions { UseRecycleBin = useRecycleBin }, log, cancellationToken: cancellationToken);

        // The whole leftover folder was chosen, so empty subfolders inside it go too.
        var removed = summary.DirectoriesRemoved;
        if (!useRecycleBin)
        {
            foreach (var leftover in leftovers.Where(l => l.Kind == LeftoverKind.Folder))
                removed += RemoveEmptyTree(leftover.Path, denyList);
        }
        return summary with { Files = [.. recycled, .. summary.Files], DirectoriesRemoved = removed };
    }

    private static int RemoveEmptyTree(string folder, DenyList denyList)
    {
        var info = new DirectoryInfo(folder);
        if (!info.Exists || info.Attributes.HasFlag(FileAttributes.ReparsePoint))
            return 0;

        var removed = 0;
        var dirs = new List<DirectoryInfo> { info };
        try
        {
            dirs.AddRange(info.EnumerateDirectories("*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint }));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }

        foreach (var dir in dirs.OrderByDescending(d => d.FullName.Length))
        {
            try
            {
                if (denyList.IsProtectedRoot(dir.FullName, recurse: true, "*") || dir.EnumerateFileSystemInfos().Any())
                    continue;
                dir.Delete(recursive: false);
                removed++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }
        return removed;
    }

    /// <summary>
    /// What the elevated helper accepts: a folder or shortcut directly (or one level) under
    /// Program Files, ProgramData or the common Start menu, not protected, and not where any
    /// installed app lives.
    /// </summary>
    public static bool IsAcceptableForHelper(string path, DenyList denyList, IReadOnlyList<InstalledApp> installed)
    {
        string normalized;
        try
        {
            normalized = PathNormalizer.Normalize(path);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }

        var underRoot = LeftoverScanner.MachineRoots()
            .Where(r => r.Length > 0)
            .Select(PathNormalizer.Normalize)
            .Any(root =>
            {
                if (!PathNormalizer.IsUnder(normalized, root))
                    return false;
                var depth = normalized[root.Length..].Trim('\\').Split('\\').Length;
                return depth <= 2;
            });
        if (!underRoot || denyList.IsProtected(normalized) || denyList.IsProtectedRoot(normalized, recurse: true, "*"))
            return false;

        return !installed.Select(a => a.InstallLocation).OfType<string>().Any(location =>
        {
            try
            {
                var l = PathNormalizer.Normalize(location);
                return PathNormalizer.IsSameOrUnder(l, normalized) || PathNormalizer.IsUnder(normalized, l);
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                return false;
            }
        });
    }

    private static IEnumerable<FileInfo> EnumerateFiles(DirectoryInfo root, CancellationToken cancellationToken)
    {
        var pending = new Stack<DirectoryInfo>();
        pending.Push(root);
        var options = new EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = 0 };
        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            List<FileSystemInfo> entries;
            try
            {
                entries = pending.Pop().EnumerateFileSystemInfos("*", options).ToList();
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
                    yield return file;
                else if (entry is DirectoryInfo dir)
                    pending.Push(dir);
            }
        }
    }
}
