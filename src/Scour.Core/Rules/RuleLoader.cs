using System.Text.Json;
using System.Text.Json.Serialization;

namespace Scour.Core.Rules;

/// <summary>A problem found while loading or linting a rule file.</summary>
public sealed record RuleIssue(string File, string? RuleId, string Message)
{
    public override string ToString() =>
        RuleId is null ? $"{File}: {Message}" : $"{File} ({RuleId}): {Message}";
}

public sealed record RuleLoadResult(IReadOnlyList<Rule> Rules, IReadOnlyList<RuleIssue> Issues);

/// <summary>Loads every <c>*.json</c> rule under a folder. Broken rules are reported, never half-loaded.</summary>
public static class RuleLoader
{
    internal static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// <summary>The <c>rules</c> folder shipped next to the executable.</summary>
    public static string DefaultRulesDirectory => ScourPaths.BuiltInRulesDirectory;

    public static RuleLoadResult LoadDirectory(string directory) => LoadDirectories([directory]);

    /// <summary>
    /// Loads several folders as one set: the built-in rules first, then optional extra packs.
    /// Only the first folder must exist. Ids must be unique across all of them.
    /// </summary>
    public static RuleLoadResult LoadDirectories(IReadOnlyList<string> directories)
    {
        var rules = new List<Rule>();
        var issues = new List<RuleIssue>();
        var seen = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        for (var i = 0; i < directories.Count; i++)
        {
            if (Directory.Exists(directories[i]))
                LoadInto(directories[i], rules, issues, seen);
            else if (i == 0)
                issues.Add(new RuleIssue(directories[i], null, "Rules folder does not exist."));
        }
        return new RuleLoadResult(rules, issues);
    }

    private static void LoadInto(string directory, List<Rule> rules, List<RuleIssue> issues, Dictionary<string, string> seen)
    {

        var files = Directory.EnumerateFiles(directory, "*.json", SearchOption.AllDirectories)
            .Where(f => !Path.GetFileName(f).EndsWith(".schema.json", StringComparison.OrdinalIgnoreCase))
            .Order(StringComparer.OrdinalIgnoreCase);

        foreach (var file in files)
        {
            var relative = Path.GetRelativePath(directory, file);
            Rule rule;
            try
            {
                rule = Parse(File.ReadAllText(file)) with { SourceFile = file };
            }
            catch (JsonException ex)
            {
                issues.Add(new RuleIssue(relative, null, $"Invalid JSON: {ex.Message}"));
                continue;
            }

            var ruleIssues = RuleValidator.Validate(rule)
                .Select(m => new RuleIssue(relative, rule.Id, m))
                .ToList();

            if (!string.Equals(Path.GetFileNameWithoutExtension(file), rule.Id, StringComparison.Ordinal))
                ruleIssues.Add(new RuleIssue(relative, rule.Id, $"File name should be '{rule.Id}.json'."));

            if (seen.TryGetValue(rule.Id, out var firstFile))
                ruleIssues.Add(new RuleIssue(relative, rule.Id, $"Duplicate id; already defined in {firstFile}."));

            if (ruleIssues.Count > 0)
            {
                issues.AddRange(ruleIssues);
                continue;
            }

            seen[rule.Id] = relative;
            rules.Add(rule);
        }
    }

    public static Rule Parse(string json) =>
        JsonSerializer.Deserialize<Rule>(json, JsonOptions)
        ?? throw new JsonException("Rule file is empty.");
}
