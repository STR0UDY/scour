using Scour.Core.Apps;
using Scour.Core.Cleaning;
using Scour.Core.Interop;
using Scour.Core.Rules;
using Scour.Core.Scanning;
using Scour.Core.Settings;
using Scour.Ipc;
using static Scour.Core.Tests.Sandbox;

namespace Scour.Core.Tests;

public sealed class RecycleBinTests : IDisposable
{
    private readonly Sandbox _box = new();

    public void Dispose() => _box.Dispose();

    private string MetadataFile(long version, DateTime deletedUtc)
    {
        var path = _box.PathOf("$IABC123.txt");
        using var w = new BinaryWriter(File.Create(path));
        w.Write(version);
        w.Write(1234L);
        w.Write(deletedUtc.ToFileTimeUtc());
        if (version == 2)
        {
            w.Write(5);
            w.Write("C:\\x\0"u8.ToArray());
        }
        return path;
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void Reads_the_deletion_time_from_I_files(long version)
    {
        var deleted = new DateTime(2026, 3, 14, 9, 26, 53, DateTimeKind.Utc);

        Assert.Equal(deleted, RecycleBinScanner.ReadDeletionTime(MetadataFile(version, deleted)));
    }

    [Fact]
    public void Garbage_I_file_is_ignored()
    {
        var path = _box.File("$Ibroken", 10);

        Assert.Null(RecycleBinScanner.ReadDeletionTime(path));
    }
}

public sealed class UserExclusionTests : IDisposable
{
    private readonly Sandbox _box = new();

    public void Dispose() => _box.Dispose();

    [Fact]
    public void A_user_excluded_folder_is_never_listed()
    {
        _box.File(@"Cache\a.bin");
        _box.File(@"Cache\Keep Me [1]\b.bin");
        var scanner = new Scanner(_box.Expander(), _box.DenyList(), false,
            new ScannerOptions { UserExclusions = [_box.PathOf(@"Cache\Keep Me [1]")] });

        var result = scanner.ScanRule(Rule(Target(@"%SANDBOX%\Cache")), TestContext.Current.CancellationToken);

        Assert.Equal(["a.bin"], result.Files.Select(f => Path.GetFileName(f.Path)));
    }
}

public sealed class LeftoverTests : IDisposable
{
    private readonly Sandbox _box = new();

    public void Dispose() => _box.Dispose();

    [Theory]
    [InlineData("Mozilla Firefox (x64 en-US)", "Mozilla Firefox")]
    [InlineData("Notepad++ (64-bit x64)", "Notepad++")]
    [InlineData("Blender 5.1.2", "Blender")]
    [InlineData("7-Zip 24.08 (x64)", "7-Zip")]
    public void Clean_name_drops_versions_and_architecture(string raw, string expected) =>
        Assert.Equal(expected, LeftoverScanner.CleanName(raw));

    [Theory]
    [InlineData("Mozilla Corporation", "Mozilla")]
    [InlineData("Valve Corp.", "Valve")]
    [InlineData("Discord Inc.", "Discord")]
    public void Clean_publisher_drops_company_suffixes(string raw, string expected) =>
        Assert.Equal(expected, LeftoverScanner.CleanPublisher(raw));

    [Fact]
    public void Deleting_a_leftover_folder_removes_it_whole()
    {
        _box.File(@"AppData\OldApp\settings.json", 10);
        _box.File(@"AppData\OldApp\cache\a.bin", 20);
        _box.Dir(@"AppData\OldApp\empty\deeper");
        var leftover = new Leftover(_box.PathOf(@"AppData\OldApp"), LeftoverKind.Folder, 30, "test", false);

        var summary = LeftoverCleaner.Delete([leftover], _box.DenyList(), useRecycleBin: false, cancellationToken: TestContext.Current.CancellationToken);

        Assert.False(Directory.Exists(_box.PathOf(@"AppData\OldApp")));
        Assert.True(Directory.Exists(_box.PathOf("AppData")));
        Assert.Equal(30, summary.BytesFreed);
    }

    [Fact]
    public void A_protected_folder_is_never_removed_as_a_leftover()
    {
        _box.File(@"Protected\thesis.docx", 10);
        var leftover = new Leftover(_box.PathOf("Protected"), LeftoverKind.Folder, 10, "test", false);

        LeftoverCleaner.Delete([leftover], _box.DenyList(), useRecycleBin: false, cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(File.Exists(_box.PathOf(@"Protected\thesis.docx")));
    }

    [Fact]
    public void Registry_leftovers_are_never_deleted()
    {
        var leftover = new Leftover(@"HKCU\Software\Example", LeftoverKind.RegistryKey, 0, "test", false);

        Assert.False(leftover.CanDelete);
        Assert.Empty(LeftoverCleaner.Delete([leftover], _box.DenyList(), useRecycleBin: false, cancellationToken: TestContext.Current.CancellationToken).Files);
    }

    [Fact]
    public void Helper_only_accepts_shallow_folders_under_machine_roots()
    {
        var deny = Safety.DenyList.ForCurrentSystem();
        var pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);

        Assert.True(LeftoverCleaner.IsAcceptableForHelper(Path.Combine(pf, "SomeGoneVendor"), deny, []));
        Assert.True(LeftoverCleaner.IsAcceptableForHelper(Path.Combine(pf, "Vendor", "Product"), deny, []));
        Assert.False(LeftoverCleaner.IsAcceptableForHelper(Path.Combine(pf, "A", "B", "C"), deny, []));
        Assert.False(LeftoverCleaner.IsAcceptableForHelper(pf, deny, []));
        Assert.False(LeftoverCleaner.IsAcceptableForHelper(Environment.GetFolderPath(Environment.SpecialFolder.System), deny, []));
        Assert.False(LeftoverCleaner.IsAcceptableForHelper(_box.PathOf("x"), deny, []));

        var installed = new InstalledApp { Id = "x", Kind = AppKind.Win32, Name = "Live", InstallLocation = Path.Combine(pf, "Vendor", "Live") };
        Assert.False(LeftoverCleaner.IsAcceptableForHelper(Path.Combine(pf, "Vendor"), deny, [installed]));
    }
}

public sealed class CommandLineTests
{
    [Fact]
    public void Quoted_path_with_arguments()
    {
        var cmd = Path.Combine(Environment.SystemDirectory, "notepad.exe");
        Assert.Equal(cmd, CommandLine.ExecutablePath($"\"{cmd}\" --minimized"));
    }

    [Fact]
    public void Environment_variables_are_expanded()
    {
        Assert.Equal(Path.Combine(Environment.SystemDirectory, "notepad.exe"),
            CommandLine.ExecutablePath(@"%SystemRoot%\System32\notepad.exe /a"), ignoreCase: true);
    }

    [Fact]
    public void Bare_names_resolve_to_system32()
    {
        Assert.Equal(Path.Combine(Environment.SystemDirectory, "notepad.exe"), CommandLine.ExecutablePath("notepad /b"), ignoreCase: true);
    }

    [Fact]
    public void Rundll32_points_at_the_dll()
    {
        Assert.Equal(@"C:\Vendor\thing.dll", CommandLine.ExecutablePath(@"rundll32.exe C:\Vendor\thing.dll,Start"));
    }

    [Fact]
    public void Unquoted_missing_path_with_spaces_is_cut_after_exe()
    {
        Assert.Equal(@"C:\Program Files\Gone App\gone.exe", CommandLine.ExecutablePath(@"C:\Program Files\Gone App\gone.exe --flag"));
    }

    [Fact]
    public void Split_returns_program_and_arguments()
    {
        var (program, args) = CommandLine.Split("MsiExec.exe /X{1234}");
        Assert.EndsWith("msiexec.exe", program, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("/X{1234}", args);
    }
}

public sealed class SettingsTests : IDisposable
{
    private readonly Sandbox _box = new();

    public void Dispose() => _box.Dispose();

    [Fact]
    public void Settings_round_trip()
    {
        var store = new SettingsStore(_box.PathOf("settings.json"));
        var settings = new ScourSettings { UseRecycleBin = true, Theme = "dark" };
        settings.Exclusions.Add(@"D:\Keep");
        settings.RuleChoices["app.discord.cache"] = false;
        settings.Schedule.Enabled = true;
        settings.Schedule.Frequency = ScheduleFrequency.Monthly;
        store.Save(settings);

        var loaded = store.Load();

        Assert.True(loaded.UseRecycleBin);
        Assert.Equal("dark", loaded.Theme);
        Assert.Equal([@"D:\Keep"], loaded.Exclusions);
        Assert.False(loaded.RuleChoices["APP.DISCORD.CACHE"]);
        Assert.Equal(ScheduleFrequency.Monthly, loaded.Schedule.Frequency);
    }

    [Fact]
    public void Corrupt_settings_fall_back_to_defaults()
    {
        File.WriteAllText(_box.PathOf("settings.json"), "{ this isn't json");

        var loaded = new SettingsStore(_box.PathOf("settings.json")).Load();

        Assert.False(loaded.UseRecycleBin);
        Assert.Empty(loaded.Exclusions);
    }

    private static Rule R(string id, RuleSafety safety = RuleSafety.Safe, bool on = true, bool admin = false, string category = RuleCategories.Apps) => new()
    {
        Id = id, Name = id, Category = category, Safety = safety, DefaultEnabled = on, Elevation = admin, Detect = ["%SystemRoot%"],
    };

    [Fact]
    public void Quick_profile_is_safe_default_on_and_never_needs_admin()
    {
        Assert.True(RuleProfiles.Includes(RuleProfiles.Quick, R("a.b")));
        Assert.False(RuleProfiles.Includes(RuleProfiles.Quick, R("a.b", admin: true)));
        Assert.False(RuleProfiles.Includes(RuleProfiles.Quick, R("a.b", RuleSafety.Caution, on: false)));
        Assert.False(RuleProfiles.Includes(RuleProfiles.Quick, R("a.b", on: false)));
    }

    [Fact]
    public void Deep_and_dev_profiles()
    {
        Assert.True(RuleProfiles.Includes(RuleProfiles.Deep, R("a.b", on: false, admin: true)));
        Assert.False(RuleProfiles.Includes(RuleProfiles.Deep, R("a.b", RuleSafety.Caution)));
        Assert.False(RuleProfiles.Includes(RuleProfiles.Deep, R("dev.x", on: false, category: RuleCategories.Dev)));
        Assert.True(RuleProfiles.Includes(RuleProfiles.DevBox, R("dev.x", on: false, category: RuleCategories.Dev)));
    }

    [Fact]
    public void Custom_profile_follows_the_users_ticks()
    {
        var settings = new ScourSettings();
        settings.RuleChoices["a.b"] = false;
        settings.RuleChoices["c.d"] = true;

        Assert.False(RuleProfiles.Includes(RuleProfiles.Custom, R("a.b"), settings));
        Assert.True(RuleProfiles.Includes(RuleProfiles.Custom, R("c.d", RuleSafety.Caution, on: false), settings));
        Assert.True(RuleProfiles.Includes(RuleProfiles.Custom, R("e.f"), settings));
    }
}

public sealed class PipeProtocolTests
{
    [Fact]
    public async Task Messages_round_trip_with_their_type()
    {
        using var stream = new MemoryStream();
        var request = new CleanRequest(["system.windows-temp"], [@"C:\Keep"], 7,
            [new FileDto(@"C:\Windows\Temp\a.tmp", 10, 638000000000000000, @"C:\Windows\Temp", true, "system.windows-temp")], false, true);

        await PipeProtocol.WriteAsync(stream, request, TestContext.Current.CancellationToken);
        await PipeProtocol.WriteAsync(stream, new ErrorResponse("nope", "invalid"), TestContext.Current.CancellationToken);
        stream.Position = 0;

        var first = await PipeProtocol.ReadAsync(stream, TestContext.Current.CancellationToken);
        var second = await PipeProtocol.ReadAsync(stream, TestContext.Current.CancellationToken);
        var end = await PipeProtocol.ReadAsync(stream, TestContext.Current.CancellationToken);

        var clean = Assert.IsType<CleanRequest>(first!.Value.Message);
        Assert.Equal(request.RuleIds, clean.RuleIds);
        Assert.Equal(request.Files[0], clean.Files[0]);
        Assert.True(clean.DryRun);
        Assert.Equal("invalid", Assert.IsType<ErrorResponse>(second!.Value.Message).Kind);
        Assert.Null(end);
    }

    [Fact]
    public async Task Entry_batches_round_trip()
    {
        using var stream = new MemoryStream();
        EntryRecord[] entries = [new(6, 5, "Windows", true, 0, 0), new(70, 6, "naïve файл.txt", false, 123, 4096)];

        await PipeProtocol.WriteEntriesAsync(stream, entries, TestContext.Current.CancellationToken);
        stream.Position = 0;
        var frame = await PipeProtocol.ReadAsync(stream, TestContext.Current.CancellationToken);

        Assert.Equal(entries, frame!.Value.Entries);
    }

    [Fact]
    public async Task Oversized_frames_are_rejected()
    {
        using var stream = new MemoryStream([1, 0xFF, 0xFF, 0xFF, 0x7F]);

        await Assert.ThrowsAsync<InvalidDataException>(() => PipeProtocol.ReadAsync(stream, TestContext.Current.CancellationToken));
    }
}

public sealed class FileActionsTests : IDisposable
{
    private readonly Sandbox _box = new();

    public void Dispose() => _box.Dispose();

    [Fact]
    public void Protected_paths_are_refused_before_touching_the_recycle_bin()
    {
        var path = _box.File(@"Protected\keep.txt");

        Assert.NotNull(FileActions.SendToRecycleBin(path, _box.DenyList()));
        Assert.NotNull(FileActions.SendToRecycleBin(_box.PathOf("Protected"), _box.DenyList()));
        Assert.True(File.Exists(path));
    }

    [Fact]
    public void Missing_path_is_reported()
    {
        Assert.Equal("It's already gone.", FileActions.SendToRecycleBin(_box.PathOf("nope.txt"), _box.DenyList()));
    }
}
