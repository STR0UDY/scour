using System.Runtime.InteropServices;

namespace Scour.Core.Paths;

/// <summary>Canonical form for comparing paths: full, long-name, no trailing slash, no <c>\\?\</c>.</summary>
internal static partial class PathNormalizer
{
    public static string Normalize(string path)
    {
        if (path.StartsWith(@"\\?\UNC\", StringComparison.Ordinal))
            path = @"\\" + path[8..];
        else if (path.StartsWith(@"\\?\", StringComparison.Ordinal))
            path = path[4..];

        var full = Path.GetFullPath(path);

        // 8.3 names (C:\PROGRA~1, C:\Users\LONGNA~1) would otherwise slip past string comparison.
        if (full.Contains('~'))
            full = ToLongPath(full);

        return Path.TrimEndingDirectorySeparator(full);
    }

    /// <summary>Same folder, or a descendant of it. Both arguments must already be normalised.</summary>
    public static bool IsSameOrUnder(string path, string folder) =>
        path.Equals(folder, StringComparison.OrdinalIgnoreCase) || IsUnder(path, folder);

    /// <summary>Strict descendant. Both arguments must already be normalised.</summary>
    public static bool IsUnder(string path, string folder)
    {
        if (path.Length <= folder.Length || !path.StartsWith(folder, StringComparison.OrdinalIgnoreCase))
            return false;

        // Drive roots keep their slash ("C:\"); everything else needs one at the boundary.
        return folder.EndsWith('\\') || path[folder.Length] == '\\';
    }

    private static string ToLongPath(string path)
    {
        // Resolve the longest existing prefix, then re-append whatever does not exist yet.
        var existing = path;
        var suffix = "";
        while (!Path.Exists(existing))
        {
            var parent = Path.GetDirectoryName(existing);
            if (parent is null)
                return path;
            suffix = Path.Combine(Path.GetFileName(existing), suffix);
            existing = parent;
        }

        var buffer = new char[32768];
        uint length;
        unsafe
        {
            fixed (char* p = buffer)
                length = GetLongPathName(existing, p, (uint)buffer.Length);
        }
        if (length == 0 || length > buffer.Length)
            return path;

        var resolved = new string(buffer, 0, (int)length);
        return suffix.Length == 0 ? resolved : Path.Combine(resolved, suffix.TrimEnd('\\'));
    }

    [LibraryImport("kernel32.dll", EntryPoint = "GetLongPathNameW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    private static unsafe partial uint GetLongPathName(string shortPath, char* longPath, uint bufferLength);
}
