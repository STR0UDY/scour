using Scour.Core.Rules;

namespace Scour.Core.Scanning;

/// <summary>A file a rule would delete, with the size and timestamp seen at scan time.</summary>
/// <param name="Root">The expanded target folder the file was found under.</param>
/// <param name="PruneFolders">
/// The target sweeps whole folders (pattern <c>*</c>, recursive), so subfolders this clean
/// empties may be removed. Folders that were already empty are always left alone.
/// </param>
public sealed record ScanFile(string Path, long Size, DateTime LastWriteUtc, string Root, bool PruneFolders = false);

public enum RuleScanStatus
{
    /// <summary>Scanned; <see cref="RuleScanResult.Files"/> may still be empty.</summary>
    Ok,

    /// <summary>None of the rule's detect paths exist, so the app isn't installed.</summary>
    NotDetected,

    /// <summary>The rule needs admin and this process isn't elevated.</summary>
    NeedsElevation,

    Cancelled,
}

public sealed record RuleScanResult
{
    public required Rule Rule { get; init; }
    public required RuleScanStatus Status { get; init; }
    public IReadOnlyList<ScanFile> Files { get; init; } = [];

    /// <summary>Image names from <see cref="Rule.RunningProcesses"/> that are running now.</summary>
    public IReadOnlyList<string> RunningProcesses { get; init; } = [];

    /// <summary>Expanded target folders refused by the deny-list or because they are reparse points.</summary>
    public IReadOnlyList<string> RefusedTargets { get; init; } = [];

    /// <summary>Files inside a target that the deny-list refused.</summary>
    public int ProtectedFiles { get; init; }

    /// <summary>Files skipped because they are newer than <see cref="Rule.MinAgeHours"/>.</summary>
    public int TooNewFiles { get; init; }

    public long TotalBytes => Files.Sum(f => f.Size);
    public int FileCount => Files.Count;
}
