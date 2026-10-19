using System.CommandLine;
using Scour.Core;
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
            var custom = parse.GetValue(rulesDir);
            var dir = custom ?? RuleLoader.DefaultRulesDirectory;
            var loaded = custom is null ? RuleLoader.LoadDirectories([dir, ScourPaths.UserRulesDirectory]) : RuleLoader.LoadDirectory(dir);
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

        var iniPath = new Argument<string>("winapp2.ini") { Description = "Path to a winapp2.ini file." };
        var import = new Command("import-winapp2", "Import community winapp2.ini rules (off by default, marked caution).") { iniPath };
        import.SetAction(parse =>
        {
            var file = parse.GetValue(iniPath)!;
            if (!File.Exists(file))
            {
                Format.Error($"{file} doesn't exist.");
                return 2;
            }
            var result = Winapp2Importer.Import(Winapp2Importer.ReadFile(file));
            Winapp2Importer.Save(result.Rules);
            Console.WriteLine($"Imported {result.Rules.Count} of {result.Sections} entries into {Winapp2Importer.ImportDirectory}.");
            if (result.Issues.Count > 0)
                Console.WriteLine($"{result.Issues.Count} entries or paths were skipped (registry-only, unknown variables or unsafe paths).");
            Console.WriteLine("Imported rules are off by default. Turn them on in the app, or pick them with --rule winapp2.*");
            return 0;
        });

        var remove = new Command("remove-winapp2", "Remove the imported winapp2.ini rules.");
        remove.SetAction(_ =>
        {
            Winapp2Importer.Remove();
            Console.WriteLine("Removed the imported winapp2 rules.");
            return 0;
        });

        return new Command("rules", "Inspect, validate and import cleaning rules.") { list, lint, import, remove };
    }
}
