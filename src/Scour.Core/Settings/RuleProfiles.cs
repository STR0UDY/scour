using Scour.Core.Rules;

namespace Scour.Core.Settings;

/// <summary>Named sets of rules, so a scan doesn't need the user to tick boxes first.</summary>
public static class RuleProfiles
{
    /// <summary>Safe, default-on rules that don't need admin. Never prompts.</summary>
    public const string Quick = "quick";

    /// <summary>Every safe rule outside the Dev category, including ones that need admin.</summary>
    public const string Deep = "deep";

    /// <summary>Quick, plus the package-manager caches in Dev.</summary>
    public const string DevBox = "dev";

    /// <summary>Whatever the user ticked on the Clean page.</summary>
    public const string Custom = "custom";

    public static readonly IReadOnlyList<string> All = [Quick, Deep, DevBox, Custom];

    public static string DisplayName(string profile) => profile switch
    {
        Quick => "Quick",
        Deep => "Deep",
        DevBox => "Dev box",
        Custom => "My selection",
        _ => profile,
    };

    public static bool Includes(string profile, Rule rule, ScourSettings? settings = null) => profile switch
    {
        Quick => IsQuick(rule),
        Deep => rule.Safety == RuleSafety.Safe && rule.Category != RuleCategories.Dev,
        DevBox => IsQuick(rule) || (rule.Category == RuleCategories.Dev && rule.Safety == RuleSafety.Safe),
        _ => IsChosen(rule, settings),
    };

    /// <summary>The user's tick for this rule, or its default if they never changed it.</summary>
    public static bool IsChosen(Rule rule, ScourSettings? settings) =>
        settings is not null && settings.RuleChoices.TryGetValue(rule.Id, out var on) ? on : rule.DefaultEnabled;

    private static bool IsQuick(Rule rule) =>
        rule.DefaultEnabled && rule.Safety == RuleSafety.Safe && !rule.Elevation;
}
