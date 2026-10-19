using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Scour.App.Helpers;
using Scour.Core.Rules;
using Scour.Core.Scanning;

namespace Scour.App.ViewModels;

public enum RuleState
{
    NotScanned,
    Scanning,
    Ready,
    NeedsAdmin,
}

public sealed record FileRow(string Path, long Size)
{
    public string SizeText => Format.Bytes(Size);
}

/// <summary>One rule on the Clean page: tick box, size, safety badge, and its largest files.</summary>
public sealed partial class RuleItem : ObservableObject
{
    private const int LargestCount = 15;
    private readonly CleanViewModel _owner;
    private readonly bool _ready;

    public RuleItem(Rule rule, bool isChecked, CleanViewModel owner)
    {
        Rule = rule;
        _owner = owner;
        IsChecked = isChecked;
        _ready = true;
    }

    public Rule Rule { get; }
    public string Name => Rule.Name;
    public string? Description => Rule.Description;
    public string Id => Rule.Id;
    public bool NeedsElevation => Rule.Elevation;

    public string? SafetyText => Rule.Safety switch
    {
        RuleSafety.Caution => "Caution",
        RuleSafety.Advanced => "Advanced",
        _ => null,
    };

    public bool IsCaution => Rule.Safety == RuleSafety.Caution;
    public bool IsAdvanced => Rule.Safety == RuleSafety.Advanced;

    [ObservableProperty]
    public partial bool IsChecked { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SizeText), nameof(DetailText), nameof(IsScanning), nameof(IsReady), nameof(NeedsAdmin))]
    public partial RuleState State { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SizeText), nameof(DetailText), nameof(HasFiles), nameof(HasMore))]
    public partial RuleScanResult? Result { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RunningText), nameof(IsRunning))]
    public partial IReadOnlyList<string> Running { get; set; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasMore))]
    public partial bool ShowingAll { get; set; }

    public ObservableCollection<FileRow> Files { get; } = [];

    public long Bytes => State == RuleState.Ready ? Result?.TotalBytes ?? 0 : 0;
    public int FileCount => State == RuleState.Ready ? Result?.FileCount ?? 0 : 0;

    public bool IsScanning => State == RuleState.Scanning;
    public bool IsReady => State == RuleState.Ready;
    public bool NeedsAdmin => State == RuleState.NeedsAdmin;
    public bool HasFiles => FileCount > 0;
    public bool HasMore => !ShowingAll && FileCount > LargestCount;
    public bool IsRunning => Running.Count > 0;

    public string SizeText => State switch
    {
        RuleState.Ready => Format.Bytes(Bytes),
        RuleState.NeedsAdmin => "Needs admin",
        RuleState.Scanning => "",
        _ => "—",
    };

    public string DetailText => State switch
    {
        RuleState.Ready when FileCount == 0 => "Nothing to clean",
        RuleState.Ready => Format.Files(FileCount),
        RuleState.NeedsAdmin => "Choose Allow above to scan",
        RuleState.Scanning => "Scanning…",
        _ => "Not scanned yet",
    };

    public string RunningText => IsRunning ? $"{string.Join(", ", Running)} is running" : "";

    public string MoreText => $"Show all {Format.Count(FileCount)} files";

    partial void OnIsCheckedChanged(bool value)
    {
        if (_ready)
            _owner.OnRuleChecked(this, value);
    }

    partial void OnResultChanged(RuleScanResult? value)
    {
        ShowingAll = false;
        Running = value?.RunningProcesses ?? [];
        Refill();
    }

    [RelayCommand]
    private void ShowAll()
    {
        ShowingAll = true;
        Refill();
    }

    private void Refill()
    {
        Files.Clear();
        if (Result is null)
            return;
        // Even "all" is capped: a list of 200,000 temp files helps nobody.
        var take = ShowingAll ? 5000 : LargestCount;
        foreach (var f in Result.Files.OrderByDescending(f => f.Size).Take(take))
            Files.Add(new FileRow(f.Path, f.Size));
    }

    /// <summary>Called after a clean: keep only the files that weren't removed.</summary>
    public void ApplyClean(IReadOnlySet<string> freedPaths)
    {
        if (Result is null)
            return;
        Result = Result with { Files = Result.Files.Where(f => !freedPaths.Contains(f.Path)).ToList() };
        OnPropertyChanged(nameof(Bytes));
        OnPropertyChanged(nameof(FileCount));
    }

    public void Notify()
    {
        OnPropertyChanged(nameof(Bytes));
        OnPropertyChanged(nameof(FileCount));
        OnPropertyChanged(nameof(SizeText));
        OnPropertyChanged(nameof(DetailText));
        OnPropertyChanged(nameof(HasFiles));
    }
}

/// <summary>A category on the Clean page (System, Browsers…), with the total of its ticked rules.</summary>
public sealed partial class CategoryGroup(string name, string glyph) : ObservableObject
{
    public string Name { get; } = name;
    public string Glyph { get; } = glyph;
    public ObservableCollection<RuleItem> Rules { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SizeText))]
    public partial long CheckedBytes { get; set; }

    [ObservableProperty]
    public partial double Share { get; set; }

    public string SizeText => Format.Bytes(CheckedBytes);

    public void Recalculate(long total)
    {
        CheckedBytes = Rules.Where(r => r.IsChecked).Sum(r => r.Bytes);
        Share = total > 0 ? (double)CheckedBytes / total * 100 : 0;
    }
}
