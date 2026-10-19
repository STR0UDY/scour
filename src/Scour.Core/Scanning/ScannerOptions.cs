namespace Scour.Core.Scanning;

/// <summary>Per-user knobs that change what a scan finds, without editing rules.</summary>
public sealed record ScannerOptions
{
    /// <summary>
    /// Files or folders the user never wants touched, from Settings or "exclude from future scans".
    /// A folder excludes everything under it.
    /// </summary>
    public IReadOnlyList<string> UserExclusions { get; init; } = [];

    /// <summary>Only empty Recycle Bin items deleted more than this many days ago. 0 empties it all.</summary>
    public double RecycleBinMinAgeDays { get; init; }
}
