namespace Scour.Core.Analysis;

public enum AnalyseMethod
{
    /// <summary>Read the NTFS master file table: seconds for a whole drive. Needs admin.</summary>
    Mft,

    /// <summary>Walk folders. Works anywhere; slower, and skips folders it can't open.</summary>
    Walk,
}

public sealed record AnalyseProgress(string Stage, double? Fraction, long Items);

/// <summary>Reads the MFT from an elevated helper. Returns null if the helper isn't available.</summary>
public delegate Task<IReadOnlyList<RawEntry>?> ElevatedMftSource(string drive, IProgress<AnalyseProgress>? progress, CancellationToken cancellationToken);

/// <summary>Builds a <see cref="DiskTree"/> for a drive or folder, choosing the fastest way available.</summary>
public static class DiskAnalyser
{
    /// <param name="elevatedSource">How to reach the MFT when this process isn't elevated (the admin helper), or null to walk.</param>
    public static async Task<DiskTree> AnalyseAsync(string path, ElevatedMftSource? elevatedSource = null,
        IProgress<AnalyseProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        var full = Path.GetFullPath(path);
        var root = Path.GetPathRoot(full);
        var isDriveRoot = root is not null && Path.TrimEndingDirectorySeparator(full).Equals(Path.TrimEndingDirectorySeparator(root), StringComparison.OrdinalIgnoreCase);

        if (isDriveRoot && IsNtfs(root!))
        {
            IReadOnlyList<RawEntry>? entries = null;
            if (MftReader.CanRead(root!))
            {
                var fraction = new Progress<double>(f => progress?.Report(new AnalyseProgress("Reading the master file table", f, 0)));
                entries = await Task.Run(() => MftReader.Read(root!, fraction, cancellationToken), cancellationToken);
            }
            else if (elevatedSource is not null)
            {
                entries = await elevatedSource(root!, progress, cancellationToken);
            }

            if (entries is not null)
            {
                progress?.Report(new AnalyseProgress("Adding up sizes", null, entries.Count));
                return await Task.Run(() =>
                {
                    var builder = new DiskTreeBuilder();
                    builder.AddRange(entries);
                    return builder.Build(MftReader.RootRecord, root!, fromMft: true);
                }, cancellationToken);
            }
        }

        var counter = new Progress<long>(n => progress?.Report(new AnalyseProgress("Walking folders", null, n)));
        var walked = await Task.Run(() => FileSystemWalker.Walk(full, counter, cancellationToken), cancellationToken);
        progress?.Report(new AnalyseProgress("Adding up sizes", null, walked.Entries.Count));
        return await Task.Run(() =>
        {
            var builder = new DiskTreeBuilder();
            builder.AddRange(walked.Entries);
            return builder.Build(walked.RootId, full, fromMft: false);
        }, cancellationToken);
    }

    public static bool IsNtfs(string root)
    {
        try
        {
            var drive = new DriveInfo(root);
            return drive.IsReady && drive.DriveFormat.Equals("NTFS", StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
