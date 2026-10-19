using Scour.Core.Cleaning;
using Scour.Core.Rules;
using Scour.Core.Scanning;
using static Scour.Core.Tests.Sandbox;

namespace Scour.Core.Tests;

public sealed class CleanerTests : IDisposable
{
    private readonly Sandbox _box = new();

    public void Dispose() => _box.Dispose();

    private RuleScanResult Scan(Rule rule) =>
        new Scanner(_box.Expander(), _box.DenyList(), isElevated: false).ScanRule(rule);

    private CleanSummary Clean(RuleScanResult result, bool dryRun = false, IDeletionLog? log = null) =>
        new Cleaner(_box.DenyList()).Clean([result], new CleanOptions { DryRun = dryRun }, log);

    [Fact]
    public void Deletes_exactly_what_was_scanned()
    {
        var a = _box.File(@"Cache\a.bin", 10);
        var b = _box.File(@"Cache\sub\b.bin", 20);
        var scan = Scan(Rule(Target(@"%SANDBOX%\Cache")));

        var summary = Clean(scan);

        Assert.Equal(30, summary.BytesFreed);
        Assert.Equal(2, summary.FilesFreed);
        Assert.False(File.Exists(a));
        Assert.False(File.Exists(b));
        Assert.True(Directory.Exists(_box.PathOf("Cache")), "The target root itself is kept.");
    }

    [Fact]
    public void A_file_created_after_the_scan_is_not_deleted()
    {
        _box.File(@"Cache\a.bin");
        var scan = Scan(Rule(Target(@"%SANDBOX%\Cache")));
        var late = _box.File(@"Cache\late.bin");

        Clean(scan);

        Assert.True(File.Exists(late));
    }

    [Fact]
    public void Dry_run_deletes_nothing_but_reports_what_it_would_free()
    {
        var a = _box.File(@"Cache\a.bin", 10);
        var scan = Scan(Rule(Target(@"%SANDBOX%\Cache")));

        var summary = Clean(scan, dryRun: true);

        Assert.True(File.Exists(a));
        Assert.Equal(10, summary.BytesFreed);
        Assert.All(summary.Files, f => Assert.Equal(DeleteOutcome.WouldDelete, f.Outcome));
        Assert.Null(summary.LogPath);
    }

    [Fact]
    public void File_changed_since_the_scan_is_skipped()
    {
        var a = _box.File(@"Cache\a.bin", 10);
        var scan = Scan(Rule(Target(@"%SANDBOX%\Cache")));
        File.WriteAllBytes(a, new byte[50]);

        var summary = Clean(scan);

        Assert.True(File.Exists(a));
        Assert.Equal(DeleteOutcome.Changed, Assert.Single(summary.Files).Outcome);
    }

    [Fact]
    public void File_touched_since_the_scan_is_skipped()
    {
        var a = _box.File(@"Cache\a.bin", 10);
        var scan = Scan(Rule(Target(@"%SANDBOX%\Cache")));
        File.SetLastWriteTimeUtc(a, DateTime.UtcNow);

        Assert.Equal(DeleteOutcome.Changed, Assert.Single(Clean(scan).Files).Outcome);
        Assert.True(File.Exists(a));
    }

    [Fact]
    public void Locked_file_is_skipped_and_counted_never_forced()
    {
        var a = _box.File(@"Cache\a.bin");
        var b = _box.File(@"Cache\b.bin");
        var scan = Scan(Rule(Target(@"%SANDBOX%\Cache")));

        CleanSummary summary;
        using (new FileStream(a, FileMode.Open, FileAccess.Read, FileShare.Read))
            summary = Clean(scan);

        Assert.True(File.Exists(a));
        Assert.False(File.Exists(b));
        Assert.Equal(1, summary.Locked);
        Assert.Equal(DeleteOutcome.Locked, summary.Files.Single(f => f.Path == a).Outcome);
    }

    [Fact]
    public void Read_only_file_is_deleted()
    {
        var a = _box.File(@"Cache\a.bin");
        File.SetAttributes(a, FileAttributes.ReadOnly);
        var scan = Scan(Rule(Target(@"%SANDBOX%\Cache")));

        Assert.Equal(DeleteOutcome.Deleted, Assert.Single(Clean(scan).Files).Outcome);
        Assert.False(File.Exists(a));
    }

    [Fact]
    public void Folder_swapped_for_a_junction_after_the_scan_is_not_followed()
    {
        // Same name, size and timestamp on both sides, so only the path check can tell them apart.
        var inCache = _box.File(@"Cache\sub\doc.docx", 10, ageHours: 100);
        var outside = _box.File(@"Outside\doc.docx", 10, ageHours: 100);
        File.SetLastWriteTimeUtc(outside, File.GetLastWriteTimeUtc(inCache));
        var scan = Scan(Rule(Target(@"%SANDBOX%\Cache")));

        Directory.Delete(_box.PathOf(@"Cache\sub"), recursive: true);
        _box.Junction(@"Cache\sub", _box.PathOf("Outside"));

        var summary = Clean(scan);

        Assert.True(File.Exists(outside));
        Assert.Equal(DeleteOutcome.Changed, Assert.Single(summary.Files).Outcome);
    }

    [Fact]
    public void Emptied_subfolders_are_removed_but_pre_existing_empty_ones_are_kept()
    {
        _box.File(@"Cache\deep\er\a.bin");
        _box.Dir(@"Cache\was-empty");
        var scan = Scan(Rule(Target(@"%SANDBOX%\Cache")));

        var summary = Clean(scan);

        Assert.False(Directory.Exists(_box.PathOf(@"Cache\deep")));
        Assert.True(Directory.Exists(_box.PathOf(@"Cache\was-empty")));
        Assert.True(Directory.Exists(_box.PathOf("Cache")));
        Assert.Equal(2, summary.DirectoriesRemoved);
    }

    [Fact]
    public void Pattern_targets_never_remove_folders()
    {
        _box.File(@"Logs\CBS\old.log");
        var scan = Scan(Rule(Target(@"%SANDBOX%\Logs", "*.log")));

        Clean(scan);

        Assert.True(Directory.Exists(_box.PathOf(@"Logs\CBS")));
    }

    [Fact]
    public void Every_deletion_is_logged_with_path_size_rule_and_outcome()
    {
        var a = _box.File(@"Cache\a.bin", 42);
        var scan = Scan(Rule(Target(@"%SANDBOX%\Cache")));
        var logDir = _box.Dir("logs");

        string? logPath;
        using (var log = FileDeletionLog.Create(logDir))
            logPath = Clean(scan, log: log).LogPath;

        Assert.NotNull(logPath);
        var line = Assert.Single(File.ReadAllLines(logPath));
        Assert.Contains("\"rule\":\"test.rule\"", line);
        Assert.Contains("\"size\":42", line);
        Assert.Contains("\"outcome\":\"Deleted\"", line);
        Assert.Contains(System.Text.Json.JsonEncodedText.Encode(a).ToString(), line);
    }

    [Fact]
    public void Protected_path_is_refused_even_if_a_scan_result_claims_it()
    {
        // A tampered or stale scan result must not be able to smuggle a protected file through.
        var victim = _box.File(@"Protected\precious.docx", 10);
        var forged = new RuleScanResult
        {
            Rule = Rule(Target("%SANDBOX%")),
            Status = RuleScanStatus.Ok,
            Files = [new ScanFile(victim, 10, File.GetLastWriteTimeUtc(victim), _box.Root, true)],
        };

        var summary = Clean(forged);

        Assert.True(File.Exists(victim));
        Assert.Equal(DeleteOutcome.Protected, Assert.Single(summary.Files).Outcome);
    }
}
