using System.Text.RegularExpressions;
using Scour.Core.Paths;

namespace Scour.Core.Rules;

/// <summary>Static checks on a rule. Nothing here touches the filesystem.</summary>
public static partial class RuleValidator
{
    [GeneratedRegex(@"^[a-z0-9]+(\.[a-z0-9-]+)+$")]
    private static partial Regex IdPattern();

    [GeneratedRegex(@"^%[A-Za-z_][A-Za-z0-9_()]*%")]
    private static partial Regex EnvVarRoot();

    [GeneratedRegex(@"\{([^}]*)\}")]
    private static partial Regex Token();

    public static IReadOnlyList<string> Validate(Rule rule)
    {
        var issues = new List<string>();

        if (!IdPattern().IsMatch(rule.Id))
            issues.Add("Id must be lower-case and dotted, e.g. 'app.discord.cache'.");
        if (string.IsNullOrWhiteSpace(rule.Name))
            issues.Add("Name is empty.");
        if (!RuleCategories.All.Contains(rule.Category))
            issues.Add($"Category must be one of: {string.Join(", ", RuleCategories.All)}.");
        if (rule.Detect.Count == 0)
            issues.Add("Every rule needs at least one 'detect' path.");
        if (rule.Handler is not null && !RuleHandlers.All.Contains(rule.Handler))
            issues.Add($"Unknown handler '{rule.Handler}'. Known: {string.Join(", ", RuleHandlers.All)}.");
        if (rule.Targets.Count == 0 && rule.Handler is null)
            issues.Add("Every rule needs at least one target.");
        if (rule.MinAgeHours < 0)
            issues.Add("minAgeHours cannot be negative.");

        foreach (var process in rule.RunningProcesses)
        {
            if (!process.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || process.IndexOfAny(['\\', '/']) >= 0)
                issues.Add($"runningProcesses entry '{process}' should be an image name like 'Discord.exe'.");
        }

        foreach (var path in rule.Detect)
            CheckPath(path, "detect", issues);

        foreach (var target in rule.Targets)
        {
            CheckPath(target.Path, "target", issues);
            if (string.IsNullOrWhiteSpace(target.Pattern) || target.Pattern.IndexOfAny(['\\', '/']) >= 0)
                issues.Add($"Target pattern '{target.Pattern}' must be a file-name glob without slashes.");
        }

        foreach (var exclude in rule.Exclude)
        {
            // Excludes may be anchored at an env var or float with a leading wildcard.
            if (!exclude.StartsWith('*'))
                CheckPath(exclude, "exclude", issues);
        }

        return issues;
    }

    private static void CheckPath(string path, string field, List<string> issues)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            issues.Add($"Empty {field} path.");
            return;
        }

        if (!EnvVarRoot().IsMatch(path))
            issues.Add($"{field} path '{path}' must start with an environment variable such as %LOCALAPPDATA%, not an absolute path.");

        if (path.Contains('/'))
            issues.Add($"{field} path '{path}' should use backslashes.");

        var segments = path.Split('\\');
        if (segments.Any(s => s is ".." or "."))
            issues.Add($"{field} path '{path}' cannot contain '.' or '..' segments.");

        foreach (Match match in Token().Matches(path))
        {
            if (!PathExpander.KnownTokens.Contains(match.Groups[1].Value))
                issues.Add($"{field} path '{path}' uses unknown token '{match.Value}'. Known: {string.Join(", ", PathExpander.KnownTokens.Select(t => "{" + t + "}"))}.");
        }
    }
}
