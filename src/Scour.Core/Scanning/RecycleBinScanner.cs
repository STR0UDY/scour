using System.Security.Principal;

namespace Scour.Core.Scanning;

/// <summary>
/// The current user's Recycle Bin on every fixed drive. Each deleted item is a pair:
/// <c>$Ixxxx</c> (metadata, including when it was deleted) and <c>$Rxxxx</c> (the file or folder).
/// Ageing uses the deletion time from <c>$I</c>, because <c>$R</c> keeps the original timestamps.
/// </summary>
internal static class RecycleBinScanner
{
    public sealed record Item(string MetadataPath, string ContentPath, DateTime DeletedUtc, string Root);

    public static IEnumerable<string> Folders()
    {
        var sid = WindowsIdentity.GetCurrent().User?.Value;
        if (sid is null)
            yield break;

        foreach (var drive in DriveInfo.GetDrives())
        {
            if (drive.DriveType != DriveType.Fixed || !drive.IsReady)
                continue;
            var folder = Path.Combine(drive.RootDirectory.FullName, "$Recycle.Bin", sid);
            if (Directory.Exists(folder))
                yield return folder;
        }
    }

    public static IEnumerable<Item> Items(string folder)
    {
        IEnumerable<string> metadata;
        try
        {
            metadata = Directory.EnumerateFiles(folder, "$I*").ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            yield break;
        }

        foreach (var info in metadata)
        {
            var deleted = ReadDeletionTime(info);
            if (deleted is null)
                continue;
            var name = Path.GetFileName(info);
            var content = Path.Combine(folder, "$R" + name[2..]);
            yield return new Item(info, content, deleted.Value, folder);
        }
    }

    /// <summary>
    /// <c>$I</c> layout: int64 version, int64 original size, FILETIME deletion time, then the
    /// original path (fixed 260 chars in v1, length-prefixed in v2). Only the time is needed.
    /// </summary>
    internal static DateTime? ReadDeletionTime(string path)
    {
        try
        {
            Span<byte> header = stackalloc byte[24];
            using var stream = File.OpenRead(path);
            if (stream.ReadAtLeast(header, 24, throwOnEndOfStream: false) < 24)
                return null;
            var version = BitConverter.ToInt64(header[..8]);
            if (version is not (1 or 2))
                return null;
            var fileTime = BitConverter.ToInt64(header[16..24]);
            return fileTime > 0 ? DateTime.FromFileTimeUtc(fileTime) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentOutOfRangeException)
        {
            return null;
        }
    }
}
