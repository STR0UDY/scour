using System.Security.Principal;
using Scour.Core.Cleaning;
using Scour.Core.Paths;
using Scour.Core.Rules;
using Scour.Core.Safety;
using Scour.Core.Scanning;
using Scour.Core.Settings;

namespace Scour.Core;

/// <summary>Which rules to run. Explicit ids win; otherwise a profile (default: the user's own ticks).</summary>
public sealed record RuleSelection
{
    public IReadOnlyList<string> Ids { get; init; } = [];
    public IReadOnlyList<string> Categories { get; init; } = [];

    /// <summary>quick, deep, dev or custom. Null means custom: the user's ticks, else rule defaults.</summary>
    public string? Profile { get; init; }

    /// <summary>Every rule, including ones that are off by default (cookies, history, dev caches…).</summary>
    public bool IncludeOptIn { get; init; }
}

public sealed record EngineOptions
{
    public string? RulesDirectory { get; init; }

    /// <summary>Extra rule packs. Defaults to <see cref="ScourPaths.UserRulesDirectory"/>; empty string disables.</summary>
    public string? UserRulesDirectory { get; init; }

    public ScourSettings? Settings { get; init; }
    public DenyList? DenyList { get; init; }
    public PathExpander? Expander { get; init; }
    public bool? IsElevated { get; init; }
}

/// <summary>Everything a front end needs, wired up with the real machine's defaults.</summary>
public sealed class ScourEngine
{
    public ScourEngine(EngineOptions? options = null)
    {
        options ??= new EngineOptions();
        RulesDirectory = options.RulesDirectory ?? RuleLoader.DefaultRulesDirectory;
        var userRules = options.UserRulesDirectory ?? ScourPaths.UserRulesDirectory;
        Rules = RuleLoader.LoadDirectories(userRules.Length == 0 ? [RulesDirectory] : [RulesDirectory, userRules]);
        Settings = options.Settings ?? new SettingsStore().Load();
        DenyList = options.DenyList ?? DenyList.ForCurrentSystem();
        IsElevated = options.IsElevated ?? CurrentProcessIsElevated();
        Expander = options.Expander ?? new PathExpander();
        Scanner = new Scanner(Expander, DenyList, IsElevated, new ScannerOptions
        {
            UserExclusions = Settings.Exclusions,
            RecycleBinMinAgeDays = Settings.RecycleBinMinAgeDays,
        });
        Cleaner = new Cleaner(DenyList);
    }

    public string RulesDirectory { get; }
    public RuleLoadResult Rules { get; }
    public ScourSettings Settings { get; }
    public DenyList DenyList { get; }
    public PathExpander Expander { get; }
    public bool IsElevated { get; }
    public Scanner Scanner { get; }
    public Cleaner Cleaner { get; }

    public Rule? FindRule(string id) =>
        Rules.Rules.FirstOrDefault(r => r.Id.Equals(id, StringComparison.OrdinalIgnoreCase));

    /// <summary>Resolves a selection to rules. Unknown ids, categories or profiles are returned, not ignored.</summary>
    public IReadOnlyList<Rule> Select(RuleSelection selection, out IReadOnlyList<string> unknown)
    {
        var missing = new List<string>();
        IEnumerable<Rule> rules;

        if (selection.Ids.Count > 0)
        {
            var picked = new List<Rule>();
            foreach (var id in selection.Ids)
            {
                // A trailing ".*" picks a whole family, e.g. "browser.chrome.*".
                var matches = id.EndsWith(".*", StringComparison.Ordinal)
                    ? Rules.Rules.Where(r => r.Id.StartsWith(id[..^1], StringComparison.OrdinalIgnoreCase)).ToList()
                    : FindRule(id) is { } rule ? [rule] : [];
                if (matches.Count == 0)
                    missing.Add(id);
                picked.AddRange(matches);
            }
            rules = picked.Distinct();
        }
        else if (selection.IncludeOptIn)
        {
            rules = Rules.Rules;
        }
        else
        {
            var profile = selection.Profile ?? RuleProfiles.Custom;
            if (!RuleProfiles.All.Contains(profile, StringComparer.OrdinalIgnoreCase))
            {
                missing.Add(profile);
                profile = RuleProfiles.Custom;
            }
            rules = Rules.Rules.Where(r => RuleProfiles.Includes(profile.ToLowerInvariant(), r, Settings));
        }

        if (selection.Categories.Count > 0)
        {
            missing.AddRange(selection.Categories.Where(c => !RuleCategories.All.Contains(c, StringComparer.OrdinalIgnoreCase)));
            rules = rules.Where(r => selection.Categories.Contains(r.Category, StringComparer.OrdinalIgnoreCase));
        }

        unknown = missing;
        return rules.ToList();
    }

    public static bool CurrentProcessIsElevated()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }
}
