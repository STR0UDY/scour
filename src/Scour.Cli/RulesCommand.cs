using System.CommandLine;
using Scour.Core.Rules;

namespace Scour.Cli;

internal static class RulesCommand
{
    public static Command Create(Option<string?> rulesDir)
    {
        var json = new Option<bool>("--json") { Description = "Machine-readable output." };
        var list = new Command("list", "List every loaded rule.") { json };
        list.SetAction(parse =>
        {
            var dir = parse.GetValue(rulesDir) ?? RuleLoader.DefaultRulesDirectory;
            var loaded = RuleLoader.LoadDirectory(dir);
            if (parse.GetValue(json))
            {
                Report.Write(loaded.Rules.Select(r => new { r.Id, r.Name, r.Category, r.Safety, r.DefaultEnabled, r.Elevation }));
                return 0;
            }

            foreach (var group in loaded.Rules.GroupBy(r => r.Category).OrderBy(g => RuleCategories.All.ToList().IndexOf(g.Key)))
            {
                Console.WriteLine();
                Console.WriteLine(group.Key);
                foreach (var rule in group.OrderBy(r => r.Id, StringComparer.Ordinal))
                {
                    var flags = string.Join(" ", new[]
                    {
                        rule.DefaultEnabled ? "on" : "off",
                        rule.Safety == RuleSafety.Safe ? null : rule.Safety.ToString().ToLowerInvariant(),
                        rule.Elevation ? "admin" : null,
                    }.OfType<string>());
                    Console.WriteLine($"  {Format.Fit(rule.Id, 34)} {Format.Fit(rule.Name, 38)} {flags}");
                }
            }
            Console.WriteLine();
            Console.WriteLine($"{loaded.Rules.Count} rules from {dir}");
            foreach (var issue in loaded.Issues)
                Format.Warn($"Skipped: {issue}");
            return 0;
        });

        var lintDir = new Argument<string?>("folder") { Description = "Rules folder to check.", Arity = ArgumentArity.ZeroOrOne };
        var lint = new Command("lint", "Validate rule files: schema, unique ids, env-var-rooted paths, detect present.") { lintDir };
        lint.SetAction(parse =>
        {
            var dir = parse.GetValue(lintDir) ?? parse.GetValue(rulesDir) ?? RuleLoader.DefaultRulesDirectory;
            var loaded = RuleLoader.LoadDirectory(dir);
            foreach (var issue in loaded.Issues)
                Format.Error(issue.ToString());
            Console.WriteLine($"{loaded.Rules.Count} valid, {loaded.Issues.Count} issue(s) in {dir}");
            return loaded.Issues.Count == 0 ? 0 : 1;
        });

        return new Command("rules", "Inspect and validate cleaning rules.") { list, lint };
    }
}
