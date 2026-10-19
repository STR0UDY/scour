using System.Security.Principal;
using Scour.Core.Cleaning;
using Scour.Core.Paths;
using Scour.Core.Rules;
using Scour.Core.Safety;
using Scour.Core.Scanning;

namespace Scour.Core;

/// <summary>Which rules to run. With no ids, the default-on rules (or all, with <see cref="IncludeOptIn"/>).</summary>
public sealed record RuleSelection
{
    public IReadOnlyList<string> Ids { get; init; } = [];
    public IReadOnlyList<string> Categories { get; init; } = [];

    /// <summary>Also run rules that are off by default (cookies, history, dev caches…).</summary>
    public bool IncludeOptIn { get; init; }
}

/// <summary>Everything a front end needs, wired up with the real machine's defaults.</summary>
public sealed class ScourEngine
{
    public ScourEngine(string? rulesDirectory = null, DenyList? denyList = null, PathExpander? expander = null, bool? isElevated = null)
    {
        RulesDirectory = rulesDirectory ?? RuleLoader.DefaultRulesDirectory;
        Rules = RuleLoader.LoadDirectory(RulesDirectory);
        DenyList = denyList ?? DenyList.ForCurrentSystem();
        IsElevated = isElevated ?? CurrentProcessIsElevated();
        Scanner = new Scanner(expander ?? new PathExpander(), DenyList, IsElevated);
        Cleaner = new Cleaner(DenyList);
    }

    public string RulesDirectory { get; }
    public RuleLoadResult Rules { get; }
    public DenyList DenyList { get; }
    public bool IsElevated { get; }
    public Scanner Scanner { get; }
    public Cleaner Cleaner { get; }

    /// <summary>Resolves a selection to rules. Unknown ids or categories are returned, not ignored.</summary>
    public IReadOnlyList<Rule> Select(RuleSelection selection, out IReadOnlyList<string> unknown)
    {
        var missing = new List<string>();
        IEnumerable<Rule> rules;

        if (selection.Ids.Count > 0)
        {
            var byId = Rules.Rules.ToDictionary(r => r.Id, StringComparer.OrdinalIgnoreCase);
            var picked = new List<Rule>();
            foreach (var id in selection.Ids)
            {
                // A trailing ".*" picks a whole family, e.g. "browser.chrome.*".
                var matches = id.EndsWith(".*", StringComparison.Ordinal)
                    ? Rules.Rules.Where(r => r.Id.StartsWith(id[..^1], StringComparison.OrdinalIgnoreCase)).ToList()
                    : byId.TryGetValue(id, out var rule) ? [rule] : [];
                if (matches.Count == 0)
                    missing.Add(id);
                picked.AddRange(matches);
            }
            rules = picked.Distinct();
        }
        else
        {
            rules = Rules.Rules.Where(r => selection.IncludeOptIn || r.DefaultEnabled);
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
