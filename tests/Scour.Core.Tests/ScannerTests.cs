using Scour.Core.Rules;
using Scour.Core.Scanning;
using static Scour.Core.Tests.Sandbox;

namespace Scour.Core.Tests;

public sealed class ScannerTests : IDisposable
{
    private readonly Sandbox _box = new();

    public void Dispose() => _box.Dispose();

    private RuleScanResult Scan(Rule rule, bool elevated = false, params (string, string)[] env) =>
        new Scanner(_box.Expander(env), _box.DenyList(), elevated).ScanRule(rule);

    private static string[] Names(RuleScanResult result) =>
        result.Files.Select(f => Path.GetFileName(f.Path)).Order().ToArray();

    [Fact]
    public void Recursive_target_finds_files_in_subfolders()
    {
        _box.File(@"Cache\a.bin", 10);
        _box.File(@"Cache\sub\b.bin", 20);

        var result = Scan(Rule(Target(@"%SANDBOX%\Cache")));

        Assert.Equal(RuleScanStatus.Ok, result.Status);
        Assert.Equal(["a.bin", "b.bin"], Names(result));
        Assert.Equal(30, result.TotalBytes);
    }

    [Fact]
    public void Non_recursive_target_ignores_subfolders()
    {
        _box.File(@"Cache\a.bin");
        _box.File(@"Cache\sub\b.bin");

        var result = Scan(Rule(Target(@"%SANDBOX%\Cache", recurse: false)));

        Assert.Equal(["a.bin"], Names(result));
    }

    [Fact]
    public void Pattern_matches_file_names_only()
    {
        _box.File(@"Logs\setup.log");
        _box.File(@"Logs\setup.txt");
        _box.File(@"Logs\old\x.log");

        var result = Scan(Rule(Target(@"%SANDBOX%\Logs", "*.log")));

        Assert.Equal(["setup.log", "x.log"], Names(result));
    }

    [Fact]
    public void Excluded_files_and_folders_are_never_listed()
    {
        _box.File(@"Cache\a.bin");
        _box.File(@"Cache\keep.me");
        _box.File(@"Cache\Content.Outlook\attachment.docx");

        var rule = Rule(Target(@"%SANDBOX%\Cache")) with
        {
            Exclude = [@"%SANDBOX%\Cache\keep.me", @"*\Content.Outlook\*"],
        };

        Assert.Equal(["a.bin"], Names(Scan(rule)));
    }

    [Fact]
    public void Files_newer_than_min_age_survive()
    {
        _box.File(@"Temp\old.tmp", ageHours: 48);
        _box.File(@"Temp\fresh.tmp", ageHours: 1);

        var result = Scan(Rule(Target(@"%SANDBOX%\Temp")) with { MinAgeHours = 24 });

        Assert.Equal(["old.tmp"], Names(result));
        Assert.Equal(1, result.TooNewFiles);
    }

    [Fact]
    public void Rule_is_hidden_when_nothing_it_detects_exists()
    {
        _box.File(@"Cache\a.bin");

        var result = Scan(Rule(Target(@"%SANDBOX%\Cache")) with { Detect = [@"%SANDBOX%\NotInstalled"] });

        Assert.Equal(RuleScanStatus.NotDetected, result.Status);
        Assert.Empty(result.Files);
    }

    [Fact]
    public void Detect_accepts_a_file()
    {
        _box.File(@"App\app.exe");
        _box.File(@"App\Cache\a.bin");

        var result = Scan(Rule(Target(@"%SANDBOX%\App\Cache")) with { Detect = [@"%SANDBOX%\App\app.exe"] });

        Assert.Equal(RuleScanStatus.Ok, result.Status);
    }

    [Fact]
    public void Elevated_rule_is_skipped_when_not_elevated()
    {
        _box.File(@"Cache\a.bin");
        var rule = Rule(Target(@"%SANDBOX%\Cache")) with { Elevation = true };

        Assert.Equal(RuleScanStatus.NeedsElevation, Scan(rule).Status);
        Assert.Equal(RuleScanStatus.Ok, Scan(rule, elevated: true).Status);
    }

    [Fact]
    public void Undefined_variable_resolves_to_nothing_rather_than_a_literal_folder()
    {
        _box.File(@"%NOPE%\a.bin");

        var result = Scan(Rule(Target(@"%NOPE%")));

        Assert.Empty(result.Files);
    }

    [Fact]
    public void Junction_inside_a_target_is_not_followed()
    {
        var outside = _box.File(@"Outside\precious.docx");
        _box.File(@"Cache\a.bin");
        _box.Junction(@"Cache\link", Path.GetDirectoryName(outside)!);

        var result = Scan(Rule(Target(@"%SANDBOX%\Cache")));

        Assert.Equal(["a.bin"], Names(result));
    }

    [Fact]
    public void Target_that_is_itself_a_junction_is_refused()
    {
        var outside = _box.File(@"Outside\precious.docx");
        _box.Junction("Cache", Path.GetDirectoryName(outside)!);

        var result = Scan(Rule(Target(@"%SANDBOX%\Cache")));

        Assert.Empty(result.Files);
        Assert.Single(result.RefusedTargets);
    }

    [Fact]
    public void Protected_tree_as_target_is_refused()
    {
        _box.File(@"Protected\a.bin");

        var result = Scan(Rule(Target(@"%SANDBOX%\Protected")));

        Assert.Empty(result.Files);
        Assert.Single(result.RefusedTargets);
    }

    [Fact]
    public void Protected_tree_inside_a_target_is_skipped_file_by_file()
    {
        _box.File(@"a.bin");
        _box.File(@"Protected\b.bin");

        var result = Scan(Rule(Target("%SANDBOX%")));

        Assert.Equal(["a.bin"], Names(result));
        Assert.Equal(1, result.ProtectedFiles);
    }

    [Fact]
    public void Protected_folder_allows_a_named_file_but_not_a_sweep()
    {
        _box.File(@"Folder\MEMORY.DMP");
        _box.File(@"Folder\other.bin");

        Assert.Equal(["MEMORY.DMP"], Names(Scan(Rule(Target(@"%SANDBOX%\Folder", "MEMORY.DMP", recurse: false)))));
        Assert.Empty(Scan(Rule(Target(@"%SANDBOX%\Folder", "*.DMP", recurse: false))).Files);
        Assert.Empty(Scan(Rule(Target(@"%SANDBOX%\Folder", "MEMORY.DMP", recurse: true))).Files);
    }

    [Fact]
    public void Chromium_profile_token_covers_every_profile_and_nothing_else()
    {
        _box.File(@"User Data\Default\Cache\a");
        _box.File(@"User Data\Profile 1\Cache\b");
        _box.File(@"User Data\Guest Profile\Cache\c");
        _box.File(@"User Data\Crashpad\Cache\d");
        _box.File(@"User Data\System Profile\Cache\e");

        var result = Scan(Rule(Target(@"%SANDBOX%\User Data\{chromiumProfile}\Cache")));

        Assert.Equal(["a", "b", "c"], Names(result));
    }

    [Fact]
    public void Wildcard_folder_segment_matches_existing_folders()
    {
        _box.File(@"Saved\webcache_4430\a");
        _box.File(@"Saved\webcache_4147\b");
        _box.File(@"Saved\Config\c");

        var result = Scan(Rule(Target(@"%SANDBOX%\Saved\webcache*")));

        Assert.Equal(["a", "b"], Names(result));
    }

    [Fact]
    public void Same_file_from_two_targets_is_listed_once()
    {
        _box.File(@"Cache\a.bin");

        var result = Scan(Rule(Target(@"%SANDBOX%\Cache"), Target(@"%SANDBOX%\Cache", "*.bin")));

        Assert.Single(result.Files);
    }

    [Fact]
    public async Task ScanAsync_returns_results_in_rule_order_and_reports_progress()
    {
        _box.File(@"One\a");
        _box.File(@"Two\b");
        var rules = new[]
        {
            Rule(Target(@"%SANDBOX%\One")) with { Id = "test.one" },
            Rule(Target(@"%SANDBOX%\Two")) with { Id = "test.two" },
        };
        var reported = 0;

        var results = await new Scanner(_box.Expander(), _box.DenyList(), false)
            .ScanAsync(rules, new InlineProgress<RuleScanResult>(_ => Interlocked.Increment(ref reported)), TestContext.Current.CancellationToken);

        Assert.Equal(["test.one", "test.two"], results.Select(r => r.Rule.Id));
        Assert.Equal(2, reported);
    }
}

internal sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
{
    public void Report(T value) => report(value);
}
