using System.IO.Enumeration;
using System.Text.RegularExpressions;

namespace Scour.Core.Paths;

/// <summary>
/// Turns a rule path such as <c>%LOCALAPPDATA%\Google\Chrome\User Data\{chromiumProfile}\Cache</c>
/// into the concrete folders it means on this machine.
/// </summary>
/// <remarks>
/// Env vars are expanded first; an undefined variable makes the path resolve to nothing rather
/// than to a literal <c>%FOO%</c> folder. Then each segment that is a token or contains
/// <c>*</c>/<c>?</c> is matched against the subfolders that actually exist. Reparse points are
/// never matched, so a junction can't smuggle a target somewhere else.
/// </remarks>
public sealed partial class PathExpander
{
    /// <summary>Tokens a rule path may contain, without braces.</summary>
    public static readonly IReadOnlySet<string> KnownTokens = new HashSet<string>(StringComparer.Ordinal)
    {
        "chromiumProfile",
        "firefoxProfile",
    };

    [GeneratedRegex(@"%([^%\\]+)%")]
    private static partial Regex EnvVar();

    private readonly Func<string, string?> _environment;

    /// <param name="environment">Variable lookup; defaults to the process environment. Tests pass a sandbox.</param>
    public PathExpander(Func<string, string?>? environment = null)
    {
        _environment = environment ?? Environment.GetEnvironmentVariable;
    }

    /// <summary>Expands <c>%VAR%</c> references. Returns null if any variable is undefined or empty.</summary>
    public string? ExpandVariables(string path)
    {
        var unresolved = false;
        var expanded = EnvVar().Replace(path, m =>
        {
            var value = _environment(m.Groups[1].Value);
            if (string.IsNullOrEmpty(value))
            {
                unresolved = true;
                return m.Value;
            }
            // "C:\Temp\" â†’ "C:\Temp", but a root stays "C:\" rather than becoming drive-relative "C:".
            return System.IO.Path.TrimEndingDirectorySeparator(value);
        });
        return unresolved ? null : expanded;
    }

    /// <summary>
    /// Every concrete folder the rule path names. Paths without tokens or wildcards come back as-is
    /// (whether or not they exist); wildcard segments only yield folders that exist.
    /// </summary>
    public IReadOnlyList<string> ExpandDirectories(string path)
    {
        var expanded = ExpandVariables(path);
        if (expanded is null || !System.IO.Path.IsPathFullyQualified(expanded))
            return [];

        var root = System.IO.Path.GetPathRoot(expanded)!;
        var segments = expanded[root.Length..].Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries);

        IEnumerable<string> current = [root];
        foreach (var segment in segments)
        {
            if (segment is "." or "..")
                return [];

            if (IsDynamic(segment))
            {
                var seg = segment;
                current = current.SelectMany(dir => MatchSubdirectories(dir, seg)).ToList();
            }
            else
            {
                current = current.Select(dir => System.IO.Path.Combine(dir, segment)).ToList();
            }
        }

        return current.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>True if any expansion of the path exists as a file or folder.</summary>
    public bool AnyExists(string path)
    {
        var expanded = ExpandVariables(path);
        if (expanded is null)
            return false;

        // The last segment may name a file, so expand the parent and test the leaf against both kinds.
        var parent = System.IO.Path.GetDirectoryName(path);
        var leaf = System.IO.Path.GetFileName(path);
        if (string.IsNullOrEmpty(parent) || IsDynamic(leaf))
            return ExpandDirectories(path).Any(Directory.Exists);

        return ExpandDirectories(parent).Any(dir =>
        {
            var full = System.IO.Path.Combine(dir, leaf);
            return Directory.Exists(full) || File.Exists(full);
        });
    }

    private static bool IsDynamic(string segment) =>
        (segment.StartsWith('{') && segment.EndsWith('}')) || segment.IndexOfAny(['*', '?']) >= 0;

    private static IEnumerable<string> MatchSubdirectories(string parent, string segment)
    {
        if (!Directory.Exists(parent))
            return [];

        Func<string, bool> matches = segment switch
        {
            "{chromiumProfile}" => IsChromiumProfileName,
            "{firefoxProfile}" => _ => true,
            _ when segment.StartsWith('{') => _ => false,
            _ => name => FileSystemName.MatchesSimpleExpression(segment, name, ignoreCase: true),
        };

        try
        {
            return new DirectoryInfo(parent)
                .EnumerateDirectories("*", new EnumerationOptions
                {
                    IgnoreInaccessible = true,
                    AttributesToSkip = FileAttributes.ReparsePoint,
                })
                .Where(d => matches(d.Name))
                .Select(d => d.FullName)
                .ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    /// <summary>Chromium keeps each profile in "Default", "Profile N" or "Guest Profile".</summary>
    private static bool IsChromiumProfileName(string name) =>
        name.Equals("Default", StringComparison.OrdinalIgnoreCase)
        || name.Equals("Guest Profile", StringComparison.OrdinalIgnoreCase)
        || name.StartsWith("Profile ", StringComparison.OrdinalIgnoreCase);
}
