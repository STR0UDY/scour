using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Scour.Core.Rules;

public sealed record Winapp2ImportResult(IReadOnlyList<Rule> Rules, IReadOnlyList<string> Issues, int Sections);

/// <summary>
/// Converts the community <c>winapp2.ini</c> format into Scour rules. <c>FileKey</c>,
/// <c>DetectFile</c> and <c>ExcludeKey</c> map onto the same model as built-in rules;
/// <c>RegKey</c> entries are ignored because Scour never cleans the registry. Every imported
/// rule is <c>caution</c> (or <c>advanced</c> if winapp2 carries a warning) and off by default.
/// </summary>
public static partial class Winapp2Importer
{
    /// <summary>Where imports are written, inside the user rules folder.</summary>
    public static string ImportDirectory => Path.Combine(ScourPaths.UserRulesDirectory, "winapp2");

    // winapp2 variables that aren't real environment variables, mapped onto ones that are.
    private static readonly Dictionary<string, string[]> Variables = new(StringComparer.OrdinalIgnoreCase)
    {
        ["AppData"] = ["%APPDATA%"],
        ["LocalAppData"] = ["%LOCALAPPDATA%"],
        ["LocalLowAppData"] = [@"%USERPROFILE%\AppData\LocalLow"],
        ["CommonAppData"] = ["%ProgramData%"],
        ["ProgramData"] = ["%ProgramData%"],
        ["UserProfile"] = ["%USERPROFILE%"],
        ["WinDir"] = ["%WINDIR%"],
        ["SystemRoot"] = ["%SystemRoot%"],
        ["SystemDrive"] = ["%SystemDrive%"],
        ["HomeDrive"] = ["%SystemDrive%"],
        ["Temp"] = ["%TEMP%"],
        ["Tmp"] = ["%TEMP%"],
        ["Public"] = ["%PUBLIC%"],
        ["Documents"] = [@"%USERPROFILE%\Documents"],
        ["Pictures"] = [@"%USERPROFILE%\Pictures"],
        ["Music"] = [@"%USERPROFILE%\Music"],
        ["Video"] = [@"%USERPROFILE%\Videos"],
        // winapp2's %ProgramFiles% means both Program Files folders.
        ["ProgramFiles"] = ["%ProgramFiles%", "%ProgramFiles(x86)%"],
        ["CommonProgramFiles"] = ["%CommonProgramFiles%", "%CommonProgramFiles(x86)%"],
    };

    private static readonly Dictionary<string, string> SpecialDetects = new(StringComparer.OrdinalIgnoreCase)
    {
        ["DET_CHROME"] = @"%LOCALAPPDATA%\Google\Chrome",
        ["DET_FIREFOX"] = @"%APPDATA%\Mozilla\Firefox",
        ["DET_OPERA"] = @"%APPDATA%\Opera Software",
        ["DET_THUNDERBIRD"] = @"%APPDATA%\Thunderbird",
        ["DET_EDGE"] = @"%LOCALAPPDATA%\Microsoft\Edge",
    };

    private static readonly string[] SystemRoots = ["%WINDIR%", "%SystemRoot%", "%SystemDrive%", "%ProgramFiles", "%ProgramData%", "%CommonProgramFiles"];

    [GeneratedRegex(@"%([^%\\]+)%")]
    private static partial Regex Variable();

    [GeneratedRegex(@"[^a-z0-9]+")]
    private static partial Regex NonSlug();

    public static Winapp2ImportResult Import(string iniText)
    {
        var rules = new List<Rule>();
        var issues = new List<string>();
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var sections = Parse(iniText);

        foreach (var (name, keys) in sections)
        {
            var rule = Convert(name, keys, issues, ids);
            if (rule is null)
                continue;

            var problems = RuleValidator.Validate(rule);
            if (problems.Count > 0)
            {
                issues.Add($"[{name}] skipped: {problems[0]}");
                continue;
            }
            rules.Add(rule);
        }
        return new Winapp2ImportResult(rules, issues, sections.Count);
    }

    /// <summary>Replaces any previous import with these rules.</summary>
    public static void Save(IReadOnlyList<Rule> rules, string? directory = null)
    {
        directory ??= ImportDirectory;
        if (Directory.Exists(directory))
        {
            foreach (var old in Directory.EnumerateFiles(directory, "*.json"))
                File.Delete(old);
        }
        Directory.CreateDirectory(directory);

        var json = new JsonSerializerOptions(RuleLoader.JsonOptions) { WriteIndented = true };
        foreach (var rule in rules)
            File.WriteAllText(Path.Combine(directory, rule.Id + ".json"), JsonSerializer.Serialize(rule, json));
    }

    /// <summary>Removes the imported pack.</summary>
    public static void Remove(string? directory = null)
    {
        directory ??= ImportDirectory;
        if (Directory.Exists(directory))
            Directory.Delete(directory, recursive: true);
    }

    private static Rule? Convert(string name, List<KeyValuePair<string, string>> keys, List<string> issues, HashSet<string> ids)
    {
        var targets = new List<RuleTarget>();
        var detect = new List<string>();
        var exclude = new List<string>();
        string? langSecRef = null, section = null;
        var warning = false;

        foreach (var (key, value) in keys)
        {
            if (key.StartsWith("FileKey", StringComparison.OrdinalIgnoreCase))
            {
                var parts = value.Split('|');
                var recurse = parts.Skip(2).Any(f => f.Trim().Equals("RECURSE", StringComparison.OrdinalIgnoreCase)
                                                  || f.Trim().Equals("REMOVESELF", StringComparison.OrdinalIgnoreCase));
                var patterns = parts.Length > 1 && parts[1].Trim().Length > 0 ? parts[1].Split(';') : ["*"];
                foreach (var path in MapPath(parts[0], name, issues))
                foreach (var pattern in patterns.Select(p => p.Trim()).Where(p => p.Length > 0))
                    targets.Add(new RuleTarget { Path = path, Pattern = pattern, Recurse = recurse });
            }
            else if (key.StartsWith("DetectFile", StringComparison.OrdinalIgnoreCase))
            {
                detect.AddRange(MapPath(value, name, issues));
            }
            else if (key.StartsWith("ExcludeKey", StringComparison.OrdinalIgnoreCase))
            {
                var parts = value.Split('|');
                if (parts.Length < 2 || parts[0].Trim().Equals("REG", StringComparison.OrdinalIgnoreCase))
                    continue;
                var kind = parts[0].Trim().ToUpperInvariant();
                foreach (var path in MapPath(parts[1], name, issues))
                {
                    var tail = kind == "FILE" && parts.Length > 2 ? parts[2].Trim() : "*";
                    exclude.Add(path.TrimEnd('\\') + "\\" + tail);
                }
            }
            else if (key.Equals("SpecialDetect", StringComparison.OrdinalIgnoreCase) && SpecialDetects.TryGetValue(value.Trim(), out var special))
            {
                detect.Add(special);
            }
            else if (key.Equals("LangSecRef", StringComparison.OrdinalIgnoreCase))
            {
                langSecRef = value.Trim();
            }
            else if (key.Equals("Section", StringComparison.OrdinalIgnoreCase))
            {
                section = value.Trim();
            }
            else if (key.Equals("Warning", StringComparison.OrdinalIgnoreCase))
            {
                warning = true;
            }
        }

        if (targets.Count == 0)
            return null; // Registry-only entries.

        // Registry-based Detect keys are ignored, so fall back to "the folders it cleans exist".
        if (detect.Count == 0)
            detect.AddRange(targets.Select(t => t.Path).Distinct(StringComparer.OrdinalIgnoreCase));

        var id = UniqueId(name, ids);
        return new Rule
        {
            Id = id,
            Name = name,
            Description = "Imported from winapp2.ini. Community rules vary in quality: check the preview before cleaning.",
            Category = MapCategory(langSecRef, section),
            Safety = warning ? RuleSafety.Advanced : RuleSafety.Caution,
            DefaultEnabled = false,
            Elevation = targets.Any(t => SystemRoots.Any(r => t.Path.StartsWith(r, StringComparison.OrdinalIgnoreCase))),
            Detect = detect.Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
            Targets = targets,
            Exclude = exclude,
        };
    }

    /// <summary>winapp2 variables → env vars. One path can become two (Program Files).</summary>
    private static IReadOnlyList<string> MapPath(string raw, string section, List<string> issues)
    {
        var path = raw.Trim().TrimEnd('\\').Replace('/', '\\');
        if (path.Length == 0)
            return [];

        IEnumerable<string> results = [path];
        foreach (Match match in Variable().Matches(path))
        {
            var variable = match.Groups[1].Value;
            if (Variables.TryGetValue(variable, out var replacements))
            {
                results = results.SelectMany(r => replacements.Select(rep => r.Replace(match.Value, rep, StringComparison.OrdinalIgnoreCase))).ToList();
            }
            else if (Environment.GetEnvironmentVariable(variable) is null)
            {
                issues.Add($"[{section}] unknown variable {match.Value}; that path was skipped.");
                return [];
            }
        }
        return results.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static string MapCategory(string? langSecRef, string? section)
    {
        if (section is not null && section.Contains("game", StringComparison.OrdinalIgnoreCase))
            return RuleCategories.Games;

        return langSecRef switch
        {
            "3025" or "3031" => RuleCategories.System,
            "3022" or "3026" or "3027" or "3028" or "3029" or "3030" or "3032" or "3033" => RuleCategories.Browsers,
            _ => RuleCategories.Apps,
        };
    }

    private static string UniqueId(string name, HashSet<string> ids)
    {
        var slug = NonSlug().Replace(name.Replace("*", "").Trim().ToLowerInvariant(), "-").Trim('-');
        if (slug.Length == 0)
            slug = "rule";
        var id = "winapp2." + slug;
        for (var n = 2; !ids.Add(id); n++)
            id = $"winapp2.{slug}-{n}";
        return id;
    }

    /// <summary>A forgiving INI reader: sections keep key order, comments start with ';' or '#'.</summary>
    internal static List<(string Name, List<KeyValuePair<string, string>> Keys)> Parse(string text)
    {
        var sections = new List<(string, List<KeyValuePair<string, string>>)>();
        List<KeyValuePair<string, string>>? current = null;

        foreach (var rawLine in text.Split('\n'))
        {
            var line = rawLine.Trim().TrimStart('\uFEFF');
            if (line.Length == 0 || line[0] is ';' or '#')
                continue;

            if (line[0] == '[' && line[^1] == ']')
            {
                var name = line[1..^1].Trim();
                if (name.Equals("Version", StringComparison.OrdinalIgnoreCase))
                {
                    current = null;
                    continue;
                }
                current = [];
                sections.Add((name, current));
                continue;
            }

            var eq = line.IndexOf('=');
            if (current is null || eq <= 0)
                continue;
            current.Add(new(line[..eq].Trim(), line[(eq + 1)..].Trim()));
        }
        return sections;
    }

    public static string ReadFile(string path) => File.ReadAllText(path, Encoding.UTF8);
}
