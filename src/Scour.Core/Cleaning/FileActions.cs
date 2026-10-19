using Scour.Core.Paths;
using Scour.Core.Safety;

namespace Scour.Core.Cleaning;

/// <summary>One-off actions on a single path, e.g. from the disk analyser. Always deny-list checked.</summary>
public static class FileActions
{
    /// <summary>Sends a file or folder to the Recycle Bin. Returns an error message, or null on success.</summary>
    public static string? SendToRecycleBin(string path, DenyList denyList)
    {
        string normalized;
        try
        {
            normalized = PathNormalizer.Normalize(path);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return "That path isn't valid.";
        }

        var isDirectory = Directory.Exists(normalized);
        if (!isDirectory && !File.Exists(normalized))
            return "It's already gone.";
        if (denyList.IsProtected(normalized) || (isDirectory && denyList.IsProtectedRoot(normalized, recurse: true, "*")))
            return "Scour won't delete that: it's a protected Windows or user folder.";
        if (new FileInfo(normalized).Attributes.HasFlag(FileAttributes.ReparsePoint))
            return "That's a link to somewhere else; Scour doesn't delete links.";

        var error = RecycleBin.Send(normalized);
        return error == 0 ? null : error switch
        {
            5 or 0x78 => "Access denied. It may belong to another user, or need admin.",
            32 or 0x20 => "It's in use by another program.",
            _ => $"Windows couldn't move it to the Recycle Bin (error {error}).",
        };
    }
}
