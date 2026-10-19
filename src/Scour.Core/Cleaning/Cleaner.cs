using Scour.Core.Paths;
using Scour.Core.Safety;
using Scour.Core.Scanning;

namespace Scour.Core.Cleaning;

/// <summary>
/// Deletes what a scan found, and only that. Each file is re-opened, re-checked against the
/// scan (same size, same timestamp, not a link) and against the deny-list using the path the
/// open handle really points at, then deleted through that same handle.
/// </summary>
public sealed class Cleaner
{
    private readonly DenyList _denyList;

    public Cleaner(DenyList denyList)
    {
        _denyList = denyList;
    }

    public CleanSummary Clean(
        IEnumerable<RuleScanResult> results,
        CleanOptions options,
        IDeletionLog? log = null,
        IProgress<CleanFileResult>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var outcomes = new List<CleanFileResult>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var emptied = new List<ScanFile>();

        foreach (var result in results.Where(r => r.Status == RuleScanStatus.Ok))
        {
            foreach (var file in result.Files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!seen.Add(file.Path))
                    continue; // Two rules found the same file; the first one owns it.

                var outcome = CleanFile(file, result.Rule.Id, options);
                outcomes.Add(outcome);
                if (!options.DryRun)
                    log?.Write(outcome);
                progress?.Report(outcome);

                if (file.PruneFolders && outcome.Outcome is DeleteOutcome.Deleted or DeleteOutcome.Recycled)
                    emptied.Add(file);
            }
        }

        var removed = options.DryRun ? 0 : RemoveEmptiedFolders(emptied);
        return new CleanSummary(outcomes, removed, options.DryRun ? null : log?.FilePath);
    }

    private CleanFileResult CleanFile(ScanFile file, string ruleId, CleanOptions options)
    {
        CleanFileResult Result(DeleteOutcome outcome, string? error = null) =>
            new(file.Path, file.Size, ruleId, outcome, error);

        if (_denyList.IsProtected(file.Path))
            return Result(DeleteOutcome.Protected);

        using var handle = NativeDelete.Open(file.Path, out var openError);
        if (handle.IsInvalid)
            return Result(MapError(openError), $"Win32 error {openError}");

        NativeDelete.FileState state;
        try
        {
            state = NativeDelete.Inspect(handle);
        }
        catch (Exception ex)
        {
            return Result(DeleteOutcome.Failed, ex.Message);
        }

        if (state.Attributes.HasFlag(FileAttributes.Directory) || state.Attributes.HasFlag(FileAttributes.ReparsePoint))
            return Result(DeleteOutcome.Changed, "Became a folder or link since the scan.");
        if (state.Size != file.Size || state.LastWriteUtc != file.LastWriteUtc)
            return Result(DeleteOutcome.Changed, "Size or timestamp changed since the scan.");

        // The handle's real path: catches a parent folder swapped for a junction after the scan.
        string finalPath;
        try
        {
            finalPath = PathNormalizer.Normalize(state.FinalPath);
        }
        catch (Exception ex) when (ex is ArgumentException or IOException)
        {
            return Result(DeleteOutcome.Failed, ex.Message);
        }
        if (!finalPath.Equals(PathNormalizer.Normalize(file.Path), StringComparison.OrdinalIgnoreCase))
            return Result(DeleteOutcome.Changed, $"Path now resolves to {finalPath}.");
        if (_denyList.IsProtected(finalPath))
            return Result(DeleteOutcome.Protected);

        if (options.DryRun)
            return Result(DeleteOutcome.WouldDelete);

        if (options.UseRecycleBin)
        {
            handle.Dispose(); // The shell needs to open the file itself.
            var shellError = RecycleBin.Send(finalPath);
            return shellError == 0
                ? Result(DeleteOutcome.Recycled)
                : Result(DeleteOutcome.Failed, $"Shell error {shellError}");
        }

        var deleteError = NativeDelete.MarkForDeletion(handle);
        return deleteError == 0 ? Result(DeleteOutcome.Deleted) : Result(MapError(deleteError), $"Win32 error {deleteError}");
    }

    /// <summary>
    /// Removes folders between each deleted file and its target root that are now empty, deepest
    /// first. Never the root itself, never a link, never a folder that was empty before.
    /// </summary>
    private int RemoveEmptiedFolders(List<ScanFile> deleted)
    {
        var candidates = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in deleted)
        {
            var root = PathNormalizer.Normalize(file.Root);
            var dir = Path.GetDirectoryName(PathNormalizer.Normalize(file.Path));
            while (dir is not null && PathNormalizer.IsUnder(dir, root))
            {
                candidates.TryAdd(dir, root);
                dir = Path.GetDirectoryName(dir);
            }
        }

        var removed = 0;
        foreach (var dir in candidates.Keys.OrderByDescending(d => d.Length))
        {
            try
            {
                var info = new DirectoryInfo(dir);
                if (!info.Exists || info.Attributes.HasFlag(FileAttributes.ReparsePoint))
                    continue;
                if (_denyList.IsProtected(dir) || info.EnumerateFileSystemInfos().Any())
                    continue;

                info.Delete(recursive: false);
                removed++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // In use or not ours to remove; leave it.
            }
        }
        return removed;
    }

    private static DeleteOutcome MapError(int error) => error switch
    {
        NativeDelete.ErrorSharingViolation or NativeDelete.ErrorLockViolation => DeleteOutcome.Locked,
        NativeDelete.ErrorFileNotFound or NativeDelete.ErrorPathNotFound => DeleteOutcome.Missing,
        NativeDelete.ErrorAccessDenied => DeleteOutcome.AccessDenied,
        _ => DeleteOutcome.Failed,
    };
}
