using System.Runtime.InteropServices;
using Scour.Core.Paths;

namespace Scour.Core.Safety;

/// <summary>
/// Hard limits no rule can override. Checked when a target is expanded, again for every file
/// before it is shown, and again just before it is deleted.
/// </summary>
/// <remarks>
/// Two kinds of protection:
/// <list type="bullet">
/// <item><b>Folder</b> paths (drive roots, <c>C:\Windows</c>, <c>Program Files</c>, profile roots):
/// the folder itself can't be deleted or swept recursively, but named files inside it can
/// (e.g. <c>C:\Windows\MEMORY.DMP</c>).</item>
/// <item><b>Tree</b> paths (<c>System32</c>, Documents, Desktop, Pictures…): nothing at or below
/// them is ever touched.</item>
/// </list>
/// </remarks>
public sealed partial class DenyList
{
    private readonly List<string> _folders;
    private readonly List<string> _trees;

    public DenyList(IEnumerable<string> folders, IEnumerable<string> trees)
    {
        _folders = Normalize(folders);
        _trees = Normalize(trees);
    }

    public IReadOnlyList<string> Folders => _folders;
    public IReadOnlyList<string> Trees => _trees;

    /// <summary>May this file or folder be deleted?</summary>
    public bool IsProtected(string path)
    {
        string normalized;
        try
        {
            normalized = PathNormalizer.Normalize(path);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return true; // Anything we can't reason about is protected.
        }

        // A drive root, or anything directly in one (pagefile.sys, hiberfil.sys…).
        var parent = Path.GetDirectoryName(normalized);
        if (parent is null || Path.GetDirectoryName(parent) is null)
            return true;

        return _folders.Any(f => f.Equals(normalized, StringComparison.OrdinalIgnoreCase))
            || _trees.Any(t => PathNormalizer.IsSameOrUnder(normalized, t));
    }

    /// <summary>
    /// May a rule target start at this folder? Drive roots and tree paths never; folder paths
    /// only for a non-recursive target naming one exact file (no wildcards).
    /// </summary>
    public bool IsProtectedRoot(string directory, bool recurse, string pattern)
    {
        string normalized;
        try
        {
            normalized = PathNormalizer.Normalize(directory);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return true;
        }

        if (Path.GetDirectoryName(normalized) is null)
            return true; // Never start at a drive root, not even for one named file.

        if (_trees.Any(t => PathNormalizer.IsSameOrUnder(normalized, t)))
            return true;

        if (!_folders.Any(f => f.Equals(normalized, StringComparison.OrdinalIgnoreCase)))
            return false;

        return recurse || pattern.IndexOfAny(['*', '?']) >= 0;
    }

    /// <summary>The deny-list for this machine and the current user.</summary>
    public static DenyList ForCurrentSystem()
    {
        var folders = new List<string?>();
        var trees = new List<string?>();

        foreach (var drive in DriveInfo.GetDrives())
            folders.Add(drive.Name);

        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        folders.Add(windows);
        trees.Add(Environment.GetFolderPath(Environment.SpecialFolder.System));
        trees.Add(Environment.GetFolderPath(Environment.SpecialFolder.SystemX86));
        trees.Add(Path.Combine(windows, "WinSxS"));

        folders.Add(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles));
        folders.Add(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86));
        folders.Add(Environment.GetEnvironmentVariable("ProgramW6432"));
        folders.Add(Environment.GetFolderPath(Environment.SpecialFolder.CommonProgramFiles));
        folders.Add(Environment.GetFolderPath(Environment.SpecialFolder.CommonProgramFilesX86));
        folders.Add(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData));

        // The current user's profile and the folders apps hang their data off.
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        folders.Add(profile);
        folders.Add(Path.Combine(profile, "AppData"));
        folders.Add(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData));
        folders.Add(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));
        folders.Add(Path.Combine(profile, "AppData", "LocalLow"));

        trees.Add(Environment.GetFolderPath(Environment.SpecialFolder.Desktop));
        trees.Add(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments));
        trees.Add(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures));
        trees.Add(Environment.GetFolderPath(Environment.SpecialFolder.MyMusic));
        trees.Add(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos));
        trees.Add(GetDownloadsFolder());
        trees.Add(Environment.GetEnvironmentVariable("OneDrive"));
        trees.Add(Environment.GetEnvironmentVariable("OneDriveCommercial"));
        trees.Add(Environment.GetEnvironmentVariable("OneDriveConsumer"));

        // Every profile on the machine, including ones whose known folders we can't resolve.
        var usersRoot = Path.GetDirectoryName(profile);
        if (usersRoot is not null)
        {
            folders.Add(usersRoot);
            foreach (var other in SafeEnumerateDirectories(usersRoot))
            {
                folders.Add(other);
                folders.Add(Path.Combine(other, "AppData"));
                folders.Add(Path.Combine(other, "AppData", "Local"));
                folders.Add(Path.Combine(other, "AppData", "Roaming"));
                folders.Add(Path.Combine(other, "AppData", "LocalLow"));
                foreach (var name in (string[])["Desktop", "Documents", "Pictures", "Music", "Videos", "Downloads", "OneDrive"])
                    trees.Add(Path.Combine(other, name));
            }
        }

        return new DenyList(folders.OfType<string>(), trees.OfType<string>());
    }

    private static List<string> Normalize(IEnumerable<string> paths) =>
        paths.Where(p => !string.IsNullOrWhiteSpace(p))
            .Select(PathNormalizer.Normalize)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    private static IEnumerable<string> SafeEnumerateDirectories(string path)
    {
        try
        {
            return Directory.GetDirectories(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private static string? GetDownloadsFolder()
    {
        var downloads = new Guid("374DE290-123F-4565-9164-39C4925E467B");
        if (SHGetKnownFolderPath(downloads, 0, IntPtr.Zero, out var pathPtr) != 0)
            return null;
        try
        {
            return Marshal.PtrToStringUni(pathPtr);
        }
        finally
        {
            Marshal.FreeCoTaskMem(pathPtr);
        }
    }

    [LibraryImport("shell32.dll")]
    private static partial int SHGetKnownFolderPath(in Guid folderId, uint flags, IntPtr token, out IntPtr path);
}
