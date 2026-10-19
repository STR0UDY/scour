using System.Text.Json.Serialization;

namespace Scour.Core.Cleaning;

[JsonConverter(typeof(JsonStringEnumConverter<DeleteOutcome>))]
public enum DeleteOutcome
{
    Deleted,
    Recycled,

    /// <summary>Dry run: would have been deleted.</summary>
    WouldDelete,

    /// <summary>Another process has it open. Skipped and counted, never forced.</summary>
    Locked,

    /// <summary>Size, timestamp or type changed since the scan, so the user hasn't seen this version.</summary>
    Changed,

    /// <summary>Already gone.</summary>
    Missing,

    /// <summary>Refused by the deny-list (including after resolving links).</summary>
    Protected,

    AccessDenied,
    Failed,
}

public sealed record CleanOptions
{
    public bool DryRun { get; init; }
    public bool UseRecycleBin { get; init; }
}

public sealed record CleanFileResult(string Path, long Size, string RuleId, DeleteOutcome Outcome, string? Error = null)
{
    public bool Freed => Outcome is DeleteOutcome.Deleted or DeleteOutcome.Recycled or DeleteOutcome.WouldDelete;
}

public sealed record CleanSummary(IReadOnlyList<CleanFileResult> Files, int DirectoriesRemoved, string? LogPath)
{
    public long BytesFreed => Files.Where(f => f.Freed).Sum(f => f.Size);
    public int FilesFreed => Files.Count(f => f.Freed);
    public int Locked => Files.Count(f => f.Outcome == DeleteOutcome.Locked);
    public int Skipped => Files.Count(f => f.Outcome is DeleteOutcome.Changed or DeleteOutcome.Missing or DeleteOutcome.Protected);
    public int Failed => Files.Count(f => f.Outcome is DeleteOutcome.AccessDenied or DeleteOutcome.Failed);
}
