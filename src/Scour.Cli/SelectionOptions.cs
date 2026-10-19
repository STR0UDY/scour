using System.CommandLine;
using Scour.Core;
using Scour.Core.Rules;

namespace Scour.Cli;

/// <summary>The rule-picking options shared by <c>scan</c> and <c>clean</c>.</summary>
internal sealed class SelectionOptions
{
    public Option<string[]> Rules { get; } = new("--rule", "-r")
    {
        Description = "Rule id to run (repeatable). 'browser.chrome.*' picks a family. Overrides the default set.",
        AllowMultipleArgumentsPerToken = true,
    };

    public Option<string[]> Categories { get; } = new("--category", "-c")
    {
        Description = $"Only rules in these categories: {string.Join(", ", RuleCategories.All)}.",
        AllowMultipleArgumentsPerToken = true,
    };

    public Option<bool> All { get; } = new("--all")
    {
        Description = "Include rules that are off by default (cookies, history, dev caches).",
    };

    public void AddTo(Command command)
    {
        command.Options.Add(Rules);
        command.Options.Add(Categories);
        command.Options.Add(All);
    }

    public RuleSelection Read(ParseResult result) => new()
    {
        Ids = result.GetValue(Rules) ?? [],
        Categories = result.GetValue(Categories) ?? [],
        IncludeOptIn = result.GetValue(All),
    };

    /// <summary>Loads the engine and resolves the selection, reporting problems on stderr.</summary>
    public static (ScourEngine Engine, IReadOnlyList<Rule> Rules)? Resolve(string? rulesDir, RuleSelection selection)
    {
        var engine = new ScourEngine(rulesDir);
        foreach (var issue in engine.Rules.Issues)
            Format.Warn($"Skipped rule: {issue}");

        var rules = engine.Select(selection, out var unknown);
        if (unknown.Count > 0)
        {
            Format.Error($"Unknown rule or category: {string.Join(", ", unknown)}. Try 'scour rules list'.");
            return null;
        }
        if (rules.Count == 0)
        {
            Format.Error("No rules selected.");
            return null;
        }
        return (engine, rules);
    }
}
