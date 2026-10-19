using Scour.Core.Rules;

namespace Scour.Core.Tests;

public sealed class Winapp2ImporterTests : IDisposable
{
    private readonly Sandbox _box = new();

    public void Dispose() => _box.Dispose();

    private const string Sample = """
        ; Version: 999
        [Version]
        Version=1

        [Example App *]
        LangSecRef=3021
        DetectFile=%LocalAppData%\Example
        Default=False
        FileKey1=%LocalAppData%\Example\Cache|*.*|RECURSE
        FileKey2=%LocalAppData%\Example\Logs|*.log;*.txt
        ExcludeKey1=FILE|%LocalAppData%\Example\Cache\|keep.db
        ExcludeKey2=PATH|%LocalAppData%\Example\Cache\Important\
        RegKey1=HKCU\Software\Example\Recent

        [Some Browser *]
        LangSecRef=3029
        SpecialDetect=DET_CHROME
        FileKey1=%LocalAppData%\Google\Chrome\User Data\Default\Extra Cache|*|RECURSE

        [Program Files Tool *]
        Detect=HKLM\Software\Tool
        FileKey1=%ProgramFiles%\Tool\Temp|*.tmp

        [Registry Only *]
        RegKey1=HKCU\Software\Junk

        [Weird Variable *]
        FileKey1=%NotARealVariableAnywhere%\Cache|*.*

        [Example App *]
        FileKey1=%AppData%\Example2|*.*
        """;

    private Winapp2ImportResult Import() => Winapp2Importer.Import(Sample);

    private Rule Rule(string name) => Import().Rules.First(r => r.Name == name);

    [Fact]
    public void Maps_filekeys_patterns_and_recurse()
    {
        var rule = Rule("Example App *");

        Assert.Equal(RuleCategories.Apps, rule.Category);
        Assert.Contains(rule.Targets, t => t.Path == @"%LOCALAPPDATA%\Example\Cache" && t.Pattern == "*.*" && t.Recurse);
        Assert.Contains(rule.Targets, t => t.Path == @"%LOCALAPPDATA%\Example\Logs" && t.Pattern == "*.log" && !t.Recurse);
        Assert.Contains(rule.Targets, t => t.Path == @"%LOCALAPPDATA%\Example\Logs" && t.Pattern == "*.txt");
        Assert.Equal([@"%LOCALAPPDATA%\Example"], rule.Detect);
    }

    [Fact]
    public void Maps_excludes()
    {
        var rule = Rule("Example App *");

        Assert.Contains(@"%LOCALAPPDATA%\Example\Cache\keep.db", rule.Exclude);
        Assert.Contains(@"%LOCALAPPDATA%\Example\Cache\Important\*", rule.Exclude);
    }

    [Fact]
    public void Imported_rules_are_off_and_marked_caution()
    {
        Assert.All(Import().Rules, r =>
        {
            Assert.False(r.DefaultEnabled);
            Assert.NotEqual(RuleSafety.Safe, r.Safety);
            Assert.StartsWith("winapp2.", r.Id);
        });
    }

    [Fact]
    public void Program_files_means_both_folders_and_needs_admin()
    {
        var rule = Rule("Program Files Tool *");

        Assert.Contains(rule.Targets, t => t.Path == @"%ProgramFiles%\Tool\Temp");
        Assert.Contains(rule.Targets, t => t.Path == @"%ProgramFiles(x86)%\Tool\Temp");
        Assert.True(rule.Elevation);
        // Registry Detect is ignored, so the targets themselves become the detect paths.
        Assert.Contains(@"%ProgramFiles%\Tool\Temp", rule.Detect);
    }

    [Fact]
    public void Special_detect_and_browser_category()
    {
        var rule = Rule("Some Browser *");

        Assert.Equal(RuleCategories.Browsers, rule.Category);
        Assert.Contains(@"%LOCALAPPDATA%\Google\Chrome", rule.Detect);
    }

    [Fact]
    public void Registry_only_and_unknown_variables_are_skipped_with_issues()
    {
        var result = Import();

        Assert.DoesNotContain(result.Rules, r => r.Name == "Registry Only *");
        Assert.DoesNotContain(result.Rules, r => r.Name == "Weird Variable *");
        Assert.Contains(result.Issues, i => i.Contains("NotARealVariableAnywhere"));
    }

    [Fact]
    public void Duplicate_names_get_unique_ids()
    {
        var ids = Import().Rules.Where(r => r.Name == "Example App *").Select(r => r.Id).ToList();

        Assert.Equal(2, ids.Count);
        Assert.Equal(ids.Count, ids.Distinct().Count());
    }

    [Fact]
    public void Saved_rules_load_back_through_the_normal_loader()
    {
        var dir = _box.Dir("winapp2");
        Winapp2Importer.Save(Import().Rules, dir);

        var loaded = RuleLoader.LoadDirectory(dir);

        Assert.Empty(loaded.Issues);
        Assert.Equal(Import().Rules.Count, loaded.Rules.Count);
    }

    [Fact]
    public void Saving_again_replaces_the_previous_import()
    {
        var dir = _box.Dir("winapp2");
        _box.File(@"winapp2\winapp2.stale-rule.json");

        Winapp2Importer.Save(Import().Rules, dir);

        Assert.False(File.Exists(Path.Combine(dir, "winapp2.stale-rule.json")));
    }
}
