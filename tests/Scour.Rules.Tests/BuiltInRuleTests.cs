using Scour.Core.Paths;
using Scour.Core.Rules;
using Scour.Core.Safety;
using Scour.Core.Scanning;

namespace Scour.Rules.Tests;

/// <summary>Lint and safety checks over the shipped <c>rules/</c> folder. Nothing here deletes anything.</summary>
public sealed class BuiltInRuleTests
{
    private static readonly string RulesDir = Path.Combine(AppContext.BaseDirectory, "rules");
    private static readonly RuleLoadResult Loaded = RuleLoader.LoadDirectory(RulesDir);

    public static TheoryData<string> RuleIds => new(Loaded.Rules.Select(r => r.Id));

    private static Rule Get(string id) => Loaded.Rules.Single(r => r.Id == id);

    [Fact]
    public void Every_rule_file_loads_without_issues()
    {
        Assert.True(Loaded.Issues.Count == 0, string.Join(Environment.NewLine, Loaded.Issues));
    }

    [Fact]
    public void Ships_about_forty_rules_across_every_category()
    {
        Assert.InRange(Loaded.Rules.Count, 40, 200);
        foreach (var category in RuleCategories.All)
            Assert.Contains(Loaded.Rules, r => r.Category == category);
    }

    [Fact]
    public void Rule_files_live_in_their_category_folder()
    {
        foreach (var rule in Loaded.Rules)
        {
            var folder = Path.GetFileName(Path.GetDirectoryName(rule.SourceFile));
            Assert.True(string.Equals(folder, rule.Category, StringComparison.OrdinalIgnoreCase), $"{rule.Id} is in '{folder}'");
        }
    }

    [Theory]
    [MemberData(nameof(RuleIds))]
    public void Risky_rules_are_off_by_default(string id)
    {
        var rule = Get(id);
        if (rule.Safety != RuleSafety.Safe)
            Assert.False(rule.DefaultEnabled, $"{id} is {rule.Safety} but on by default.");
    }

    [Theory]
    [MemberData(nameof(RuleIds))]
    public void Rules_touching_system_folders_ask_for_elevation(string id)
    {
        var rule = Get(id);
        string[] systemRoots = ["%WINDIR%", "%SystemRoot%", "%ProgramData%"];
        if (rule.Targets.Any(t => systemRoots.Any(r => t.Path.StartsWith(r, StringComparison.OrdinalIgnoreCase))))
            Assert.True(rule.Elevation, $"{id} targets a system folder without elevation: true.");
    }

    [Theory]
    [MemberData(nameof(RuleIds))]
    public void Rules_have_a_description(string id) =>
        Assert.False(string.IsNullOrWhiteSpace(Get(id).Description), $"{id} has no description.");

    [Theory]
    [MemberData(nameof(RuleIds))]
    public void No_target_on_this_machine_collides_with_the_deny_list(string id)
    {
        // The deny-list is the backstop, not the plan: a shipped rule should never need it.
        var deny = DenyList.ForCurrentSystem();
        var expander = new PathExpander();
        foreach (var target in Get(id).Targets)
        {
            foreach (var root in expander.ExpandDirectories(target.Path))
                Assert.False(deny.IsProtectedRoot(root, target.Recurse, target.Pattern), $"{id}: {root}");
        }
    }

    public static TheoryData<string, string> RulesAgainstProtectedTrees
    {
        get
        {
            var data = new TheoryData<string, string>();
            var trees = DenyList.ForCurrentSystem().Trees.Where(Directory.Exists).Take(6).ToList();
            // Handler rules (Recycle Bin) don't use paths, so there is nothing to redirect.
            foreach (var rule in Loaded.Rules.Where(r => r.Handler is null))
            foreach (var tree in trees)
                data.Add(rule.Id, tree);
            return data;
        }
    }

    /// <summary>
    /// Points every environment variable at a protected folder (System32, Documents…) and scans.
    /// Whatever the rule says, every target must be refused and nothing listed.
    /// </summary>
    [Theory]
    [MemberData(nameof(RulesAgainstProtectedTrees))]
    public void Every_rule_is_refused_when_its_variables_resolve_into_a_protected_tree(string id, string protectedTree)
    {
        var rule = Get(id) with { Detect = ["%ANY%"], Elevation = false };
        var expander = new PathExpander(_ => protectedTree);
        var scanner = new Scanner(expander, DenyList.ForCurrentSystem(), isElevated: false);

        var result = scanner.ScanRule(rule, TestContext.Current.CancellationToken);

        Assert.Equal(RuleScanStatus.Ok, result.Status);
        Assert.Empty(result.Files);
        foreach (var target in rule.Targets)
        {
            foreach (var root in expander.ExpandDirectories(target.Path))
                Assert.Contains(root, result.RefusedTargets);
        }
    }

    [Theory]
    [MemberData(nameof(RuleIds))]
    public void Every_rule_is_refused_when_its_variables_resolve_to_a_drive_root(string id)
    {
        var rule = Get(id) with { Detect = ["%ANY%"], Elevation = false, Handler = null };
        var drive = Path.GetPathRoot(Environment.SystemDirectory)!;
        var expander = new PathExpander(_ => drive);
        var scanner = new Scanner(expander, DenyList.ForCurrentSystem(), isElevated: false);

        // Only targets that *are* the variable (e.g. "%TEMP%") land on the root itself; deeper
        // targets become ordinary folders, so just check the root-level ones are refused.
        foreach (var target in rule.Targets.Where(t => t.Path.Trim('%').IndexOf('\\') < 0))
        {
            var single = rule with { Targets = [target] };
            var result = scanner.ScanRule(single, TestContext.Current.CancellationToken);
            Assert.Empty(result.Files);
            Assert.NotEmpty(result.RefusedTargets);
        }
    }
}
