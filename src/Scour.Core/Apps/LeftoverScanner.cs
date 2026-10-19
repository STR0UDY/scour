using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using Scour.Core.Interop;
using Scour.Core.Paths;
using Scour.Core.Safety;

namespace Scour.Core.Apps;

[JsonConverter(typeof(JsonStringEnumConverter<LeftoverKind>))]
public enum LeftoverKind
{
    Folder,
    Shortcut,

    /// <summary>Listed for information only in v1; Scour doesn't delete registry keys.</summary>
    RegistryKey,
}

public sealed record Leftover(string Path, LeftoverKind Kind, long Size, string Reason, bool NeedsElevation)
{
    public bool CanDelete => Kind != LeftoverKind.RegistryKey;
}

/// <summary>
/// After an uninstall, looks for what the uninstaller left behind: folders in Program Files,
/// ProgramData and AppData named after the product or publisher, the old install folder, and
/// orphaned Start menu shortcuts. Results are suggestions, shown unticked; Scour never guesses silently.
/// </summary>
public static partial class LeftoverScanner
{
    // Folder names that are shared or too generic to ever attribute to one app.
    private static readonly HashSet<string> Generic = new(StringComparer.OrdinalIgnoreCase)
    {
        "microsoft", "windows", "commonfiles", "packages", "programs", "temp", "cache", "logs", "data",
        "app", "apps", "software", "update", "updater", "installer", "setup", "tools", "user", "users",
        "inc", "llc", "ltd", "corporation", "corp", "company", "games", "studio", "launcher", "client",
        "nvidia", "intel", "amd", "google", "adobe", "apple", "mozilla", "default",
    };

    [GeneratedRegex(@"\s*[\(\[].*?[\)\]]")]
    private static partial Regex Bracketed();

    [GeneratedRegex(@"\s+v?\d+(\.\d+)+.*$")]
    private static partial Regex TrailingVersion();

    [GeneratedRegex(@"\b(x64|x86|64-bit|32-bit|arm64)\b", RegexOptions.IgnoreCase)]
    private static partial Regex Architecture();

    [GeneratedRegex(@"[^a-z0-9]")]
    private static partial Regex NonAlnum();

    public static IReadOnlyList<string> Roots() =>
    [
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "AppData", "LocalLow"),
        Environment.GetFolderPath(Environment.SpecialFolder.Programs),
        Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms),
    ];

    /// <summary>Folders under these need admin to remove.</summary>
    public static IReadOnlyList<string> MachineRoots() =>
    [
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms),
    ];

    /// <param name="removed">The app that was just uninstalled.</param>
    /// <param name="stillInstalled">Everything still installed; their folders are never suggested.</param>
    public static IReadOnlyList<Leftover> Find(InstalledApp removed, IReadOnlyList<InstalledApp> stillInstalled, DenyList denyList, CancellationToken cancellationToken = default)
    {
        var product = Key(CleanName(removed.Name));
        var shortProduct = removed.Publisher is null ? product : Key(CleanName(StripPublisher(removed.Name, removed.Publisher)));
        var publisher = removed.Publisher is null ? null : Key(CleanPublisher(removed.Publisher));

        var names = new HashSet<string>(new[] { product, shortProduct }.Where(IsDistinctive));
        var others = stillInstalled.Where(a => a.Id != removed.Id).ToList();
        var otherLocations = others.Select(a => a.InstallLocation).OfType<string>().Select(SafeNormalize).OfType<string>().ToList();
        var otherNames = others.Select(a => Key(CleanName(a.Name))).Where(IsDistinctive).ToHashSet();
        var machineRoots = MachineRoots().Where(r => r.Length > 0).Select(PathNormalizer.Normalize).ToList();

        var results = new Dictionary<string, Leftover>(StringComparer.OrdinalIgnoreCase);

        void Add(string path, LeftoverKind kind, string reason)
        {
            var normalized = SafeNormalize(path);
            if (normalized is null || results.ContainsKey(normalized))
                return;
            if (kind == LeftoverKind.Folder && (denyList.IsProtectedRoot(normalized, recurse: true, "*") || BelongsToAnother(normalized, otherLocations)))
                return;
            if (kind == LeftoverKind.Shortcut && denyList.IsProtected(normalized))
                return;

            var size = kind == LeftoverKind.Folder ? FolderSize.Measure(normalized, cancellationToken)
                : kind == LeftoverKind.Shortcut ? new FileInfo(normalized).Length : 0;
            var elevated = kind != LeftoverKind.RegistryKey && machineRoots.Any(r => PathNormalizer.IsUnder(normalized, r));
            results[normalized] = new Leftover(normalized, kind, size, reason, elevated);
        }

        // 1. The install folder itself, if the uninstaller left it.
        if (removed.InstallLocation is { } location && Directory.Exists(location))
            Add(location, LeftoverKind.Folder, "The app's install folder");

        if (names.Count > 0)
        {
            foreach (var root in Roots().Where(Directory.Exists))
            {
                cancellationToken.ThrowIfCancellationRequested();
                foreach (var dir in SafeDirectories(root))
                {
                    var key = Key(Path.GetFileName(dir));
                    if (names.Contains(key) && !otherNames.Contains(key))
                    {
                        Add(dir, LeftoverKind.Folder, $"Named after {removed.Name}");
                    }
                    else if (publisher is not null && IsDistinctive(publisher) && key == publisher)
                    {
                        // Publisher\Product: suggest the product folder, and the publisher folder only
                        // when nothing else of that publisher's is left in it.
                        var inside = SafeDirectories(dir).ToList();
                        var matched = inside.Where(d => names.Contains(Key(Path.GetFileName(d)))).ToList();
                        foreach (var m in matched)
                            Add(m, LeftoverKind.Folder, $"{removed.Publisher}'s folder for {removed.Name}");
                        if (matched.Count > 0 && matched.Count == inside.Count && !SafeFiles(dir).Any()
                            && !others.Any(o => o.Publisher is not null && Key(CleanPublisher(o.Publisher)) == publisher))
                        {
                            Add(dir, LeftoverKind.Folder, $"{removed.Publisher}'s folder, now empty apart from {removed.Name}");
                        }
                    }
                }
            }
        }

        // 2. Start menu shortcuts that point nowhere, or into a leftover folder.
        var leftoverFolders = results.Values.Where(l => l.Kind == LeftoverKind.Folder).Select(l => l.Path).ToList();
        foreach (var menu in new[] { Environment.GetFolderPath(Environment.SpecialFolder.Programs), Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms) })
        {
            if (!Directory.Exists(menu))
                continue;
            foreach (var lnk in SafeShortcuts(menu))
            {
                if (leftoverFolders.Any(f => PathNormalizer.IsUnder(SafeNormalize(lnk) ?? "", f)))
                    continue; // Goes with its folder.
                var target = ShellLink.Read(lnk);
                if (target is null || target.Path.StartsWith("::", StringComparison.Ordinal))
                    continue;
                var targetPath = SafeNormalize(target.Path);
                if (targetPath is null)
                    continue;
                if (leftoverFolders.Any(f => PathNormalizer.IsUnder(targetPath, f)))
                    Add(lnk, LeftoverKind.Shortcut, $"Shortcut into {removed.Name}'s folder");
                else if (!File.Exists(targetPath) && !Directory.Exists(targetPath) && names.Contains(Key(Path.GetFileNameWithoutExtension(lnk))))
                    Add(lnk, LeftoverKind.Shortcut, "Shortcut to a program that no longer exists");
            }
        }

        // 3. Registry keys, for information.
        foreach (var keyPath in RegistryCandidates(removed))
            Add(keyPath, LeftoverKind.RegistryKey, "Settings the app stored in the registry (not removed)");

        return results.Values.OrderBy(l => l.Kind).ThenByDescending(l => l.Size).ToList();
    }

    private static IEnumerable<string> RegistryCandidates(InstalledApp app)
    {
        var names = new List<string> { CleanName(app.Name) };
        if (app.Publisher is not null)
        {
            names.Add($@"{CleanPublisher(app.Publisher)}\{CleanName(StripPublisher(app.Name, app.Publisher))}");
            names.Add($@"{CleanPublisher(app.Publisher)}\{CleanName(app.Name)}");
        }

        foreach (var (hive, label) in new[] { (RegistryHive.CurrentUser, "HKCU"), (RegistryHive.LocalMachine, "HKLM") })
        {
            using var root = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
            foreach (var name in names.Distinct(StringComparer.OrdinalIgnoreCase).Where(n => IsDistinctive(Key(n.Split('\\')[^1]))))
            {
                foreach (var prefix in new[] { "Software", @"Software\WOW6432Node" })
                {
                    using var key = SafeOpen(root, $@"{prefix}\{name}");
                    if (key is not null)
                        yield return $@"{label}\{prefix}\{name}";
                }
            }
        }
    }

    internal static string CleanName(string name)
    {
        var cleaned = Bracketed().Replace(name, "");
        cleaned = Architecture().Replace(cleaned, "");
        cleaned = TrailingVersion().Replace(cleaned, "");
        return cleaned.Trim(' ', '-', '_', '.');
    }

    internal static string CleanPublisher(string publisher)
    {
        var cleaned = Regex.Replace(publisher, @",?\s*\b(inc|llc|ltd|gmbh|corp|corporation|co|limited|s\.?a|b\.?v|ab|pty)\b\.?", "", RegexOptions.IgnoreCase);
        return cleaned.Trim(' ', ',', '.');
    }

    private static string StripPublisher(string name, string publisher)
    {
        var p = CleanPublisher(publisher);
        return name.StartsWith(p + " ", StringComparison.OrdinalIgnoreCase) ? name[(p.Length + 1)..] : name;
    }

    internal static string Key(string text) => NonAlnum().Replace(text.ToLowerInvariant(), "");

    private static bool IsDistinctive(string key) => key.Length >= 4 && !Generic.Contains(key);

    private static bool BelongsToAnother(string folder, List<string> otherLocations) =>
        otherLocations.Any(o => PathNormalizer.IsSameOrUnder(o, folder) || PathNormalizer.IsUnder(folder, o));

    private static string? SafeNormalize(string path)
    {
        try
        {
            return PathNormalizer.Normalize(path);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    private static IEnumerable<string> SafeDirectories(string path)
    {
        try
        {
            return new DirectoryInfo(path).EnumerateDirectories("*", new EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint })
                .Select(d => d.FullName).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private static IEnumerable<string> SafeFiles(string path)
    {
        try
        {
            return Directory.EnumerateFiles(path).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private static IEnumerable<string> SafeShortcuts(string path)
    {
        try
        {
            return Directory.EnumerateFiles(path, "*.lnk", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint }).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private static RegistryKey? SafeOpen(RegistryKey root, string path)
    {
        try
        {
            return root.OpenSubKey(path);
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
