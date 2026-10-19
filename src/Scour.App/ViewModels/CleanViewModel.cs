using System.Collections.ObjectModel;
using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Scour.App.Helpers;
using Scour.App.Services;
using Scour.Core.Cleaning;
using Scour.Core.Rules;
using Scour.Core.Scanning;
using Scour.Core.Settings;

namespace Scour.App.ViewModels;

/// <summary>
/// Scan → review → clean, shared by the Home and Clean pages so a scan on one shows on both.
/// Every clean is the snapshot the user saw: nothing found after the scan is ever deleted.
/// </summary>
public sealed partial class CleanViewModel : ObservableObject
{
    private static readonly Dictionary<string, string> Glyphs = new()
    {
        [RuleCategories.System] = "",
        [RuleCategories.Browsers] = "",
        [RuleCategories.Apps] = "",
        [RuleCategories.Games] = "",
        [RuleCategories.Dev] = "",
    };

    private readonly DispatcherQueue _dispatcher = DispatcherQueue.GetForCurrentThread();
    private bool _suppressCheckHandling;
    private CancellationTokenSource? _scanCancel;

    public static CleanViewModel Instance { get; } = new();

    private CleanViewModel()
    {
        Build();
        AppServices.EngineReloaded += (_, _) => _dispatcher.TryEnqueue(Build);
    }

    public ObservableCollection<CategoryGroup> Groups { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsBusy), nameof(IsIdle), nameof(ShowScanPrompt), nameof(ShowResults))]
    public partial bool IsScanning { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsBusy), nameof(IsIdle))]
    public partial bool IsCleaning { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowScanPrompt), nameof(ShowResults))]
    public partial bool HasScanned { get; set; }

    [ObservableProperty]
    public partial string ScanStatus { get; set; } = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ReclaimableText), nameof(CanClean))]
    public partial long ReclaimableBytes { get; set; }

    [ObservableProperty]
    public partial int ReclaimableFiles { get; set; }

    [ObservableProperty]
    public partial int AdminRulesWaiting { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasResult))]
    public partial CleanSummary? LastResult { get; set; }

    public bool IsBusy => IsScanning || IsCleaning;
    public bool IsIdle => !IsBusy;
    public bool ShowScanPrompt => !HasScanned && !IsScanning;
    public bool ShowResults => HasScanned && !IsScanning;
    public bool HasResult => LastResult is not null;
    public bool CanClean => ReclaimableBytes > 0;
    public string ReclaimableText => Format.Bytes(ReclaimableBytes);
    public string ReclaimableDetail => $"{Format.Files(ReclaimableFiles)} in the items you've ticked";

    public string ResultTitle => LastResult is null ? "" : $"Freed {Format.Bytes(LastResult.BytesFreed)}";

    public string ResultDetail
    {
        get
        {
            if (LastResult is null)
                return "";
            var parts = new List<string> { $"{Format.Files(LastResult.FilesFreed)} {(AppServices.Settings.UseRecycleBin ? "sent to the Recycle Bin" : "removed")}" };
            if (LastResult.Locked > 0)
                parts.Add($"{Format.Count(LastResult.Locked)} skipped because they were in use");
            if (LastResult.Skipped > 0)
                parts.Add($"{Format.Count(LastResult.Skipped)} skipped because they changed after the scan");
            if (LastResult.Failed > 0)
                parts.Add($"{Format.Count(LastResult.Failed)} couldn't be removed");
            return string.Join(" · ", parts);
        }
    }

    public IEnumerable<RuleItem> AllRules => Groups.SelectMany(g => g.Rules);

    /// <summary>(Re)creates the rule list from the engine: only rules whose app is installed here.</summary>
    private void Build()
    {
        Groups.Clear();
        var engine = AppServices.Engine;
        foreach (var category in RuleCategories.All)
        {
            var group = new CategoryGroup(category, Glyphs.GetValueOrDefault(category, ""));
            foreach (var rule in engine.Rules.Rules.Where(r => r.Category == category).OrderBy(r => r.Safety).ThenBy(r => r.Name))
            {
                if (!engine.Scanner.IsDetected(rule))
                    continue;
                group.Rules.Add(new RuleItem(rule, RuleProfiles.IsChosen(rule, AppServices.Settings), this));
            }
            if (group.Rules.Count > 0)
                Groups.Add(group);
        }
        HasScanned = false;
        LastResult = null;
        Recalculate();
    }

    /// <summary>
    /// Bulk selection from the Select menu: a profile (quick, deep, dev), "defaults" or "none".
    /// Never prompts for admin; ticked admin items wait behind the "Allow" bar instead.
    /// </summary>
    public void ApplySelection(string selection)
    {
        _suppressCheckHandling = true;
        if (selection == "defaults")
            AppServices.Settings.RuleChoices.Clear();
        foreach (var item in AllRules)
        {
            item.IsChecked = selection switch
            {
                "defaults" => item.Rule.DefaultEnabled,
                "none" => false,
                _ => RuleProfiles.Includes(selection, item.Rule, AppServices.Settings),
            };
            if (selection != "defaults")
                AppServices.Settings.RuleChoices[item.Id] = item.IsChecked;
        }
        _suppressCheckHandling = false;
        AppServices.SaveSettings();
        Recalculate();
    }

    public void SetAll(CategoryGroup group, bool on)
    {
        foreach (var item in group.Rules)
            item.IsChecked = on;
    }

    internal async void OnRuleChecked(RuleItem item, bool isChecked)
    {
        if (_suppressCheckHandling)
            return;
        AppServices.Settings.RuleChoices[item.Id] = isChecked;
        AppServices.SaveSettings();

        // Ticking a rule that needs admin is the one moment Scour asks for it.
        if (isChecked && item.NeedsElevation && !AppServices.CleanService.CanRunElevated)
        {
            if (!await TryElevateAsync())
            {
                _suppressCheckHandling = true;
                item.IsChecked = false;
                _suppressCheckHandling = false;
                AppServices.Settings.RuleChoices[item.Id] = false;
                AppServices.SaveSettings();
            }
            else if (HasScanned)
            {
                await ScanAsync(AllRules.Where(r => r.State == RuleState.NeedsAdmin).ToList());
            }
        }
        Recalculate();
    }

    /// <summary>Starts the admin helper. False if declined or unavailable.</summary>
    public async Task<bool> TryElevateAsync()
    {
        try
        {
            return await AppServices.CleanService.EnsureElevatedAsync();
        }
        catch (Exception ex) when (ex is FileNotFoundException or InvalidOperationException or IOException or TimeoutException or OperationCanceledException)
        {
            if (App.MainWindow is { } window)
                await Dialogs.InfoAsync(window.XamlRoot, "Couldn't get admin access", ex.Message);
            return false;
        }
    }

    public async Task UnlockAdminRulesAsync()
    {
        if (await TryElevateAsync())
            await ScanAsync(AllRules.Where(r => r.State == RuleState.NeedsAdmin).ToList());
    }

    /// <summary>Scans every listed rule (or just <paramref name="only"/>), streaming results in as each finishes.</summary>
    public async Task ScanAsync(IReadOnlyList<RuleItem>? only = null)
    {
        if (IsBusy)
            return;
        var items = only ?? AllRules.ToList();
        if (items.Count == 0)
            return;

        _scanCancel = new CancellationTokenSource();
        IsScanning = true;
        LastResult = null;
        foreach (var item in items)
            item.State = RuleState.Scanning;

        var byId = items.ToDictionary(i => i.Id, StringComparer.OrdinalIgnoreCase);
        var done = 0;
        var progress = new Progress<RuleScanResult>(result =>
        {
            if (byId.TryGetValue(result.Rule.Id, out var item))
                Apply(item, result);
            done++;
            ScanStatus = $"Scanned {done} of {items.Count}";
            Recalculate();
        });

        try
        {
            var results = await AppServices.CleanService.ScanAsync(items.Select(i => i.Rule).ToList(), progress, _scanCancel.Token);
            foreach (var result in results)
            {
                if (byId.TryGetValue(result.Rule.Id, out var item))
                    Apply(item, result);
            }
            HasScanned = true;
        }
        catch (OperationCanceledException)
        {
            foreach (var item in items.Where(i => i.State == RuleState.Scanning))
                item.State = RuleState.NotScanned;
        }
        finally
        {
            IsScanning = false;
            ScanStatus = "";
            Recalculate();
        }
    }

    public void CancelScan() => _scanCancel?.Cancel();

    private static void Apply(RuleItem item, RuleScanResult result)
    {
        item.Result = result;
        item.State = result.Status switch
        {
            RuleScanStatus.Ok => RuleState.Ready,
            RuleScanStatus.NeedsElevation => RuleState.NeedsAdmin,
            _ => RuleState.Ready,
        };
        item.Notify();
    }

    public void Recalculate()
    {
        var ticked = AllRules.Where(r => r.IsChecked && r.IsReady).ToList();
        ReclaimableBytes = ticked.Sum(r => r.Bytes);
        ReclaimableFiles = ticked.Sum(r => r.FileCount);
        AdminRulesWaiting = AllRules.Count(r => r.NeedsAdmin);
        foreach (var group in Groups)
            group.Recalculate(ReclaimableBytes);
        OnPropertyChanged(nameof(ReclaimableDetail));
    }

    /// <summary>The whole clean flow: running-app check, confirmation, clean, result card.</summary>
    public async Task CleanAsync(XamlRoot root)
    {
        if (IsBusy)
            return;
        var items = AllRules.Where(r => r.IsChecked && r.IsReady && r.HasFiles).ToList();
        if (items.Count == 0)
            return;

        // 1. Apps holding these files open: close them, or skip their rules.
        var busy = items.Where(i => i.IsRunning).ToList();
        if (busy.Count > 0)
        {
            var apps = busy.SelectMany(b => b.Running).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var choice = await Dialogs.ShowAsync(root, "Some apps are running",
                $"{string.Join(", ", apps.Select(a => Path.GetFileNameWithoutExtension(a)))} {(apps.Count == 1 ? "is" : "are")} open, so some of their files are in use.\n\n"
                + "Close them so their caches can be cleaned, or skip them this time.",
                primary: "Close apps", secondary: "Skip them");
            if (choice == ContentDialogResult.None)
                return;
            if (choice == ContentDialogResult.Secondary)
            {
                items = items.Except(busy).ToList();
            }
            else
            {
                var stillRunning = await CloseAppsAsync(apps);
                items = items.Where(i => !i.Running.Any(r => stillRunning.Contains(r, StringComparer.OrdinalIgnoreCase))).ToList();
                if (stillRunning.Count > 0)
                    await Dialogs.InfoAsync(root, "Some apps didn't close",
                        $"{string.Join(", ", stillRunning)} didn't close, so {(stillRunning.Count == 1 ? "its" : "their")} items were skipped.");
            }
            if (items.Count == 0)
                return;
        }

        // 2. Confirm, with the exact numbers.
        var bytes = items.Sum(i => i.Bytes);
        var files = items.Sum(i => i.FileCount);
        var recycle = AppServices.Settings.UseRecycleBin;
        var confirm = await Dialogs.ShowAsync(root, recycle ? "Send to the Recycle Bin?" : "Clean now?",
            $"{(recycle ? "Send" : "Delete")} {Format.Files(files)} ({Format.Bytes(bytes)}) from {items.Count} item{(items.Count == 1 ? "" : "s")}."
            + (recycle ? "" : "\n\nThese are caches and junk Windows and your apps rebuild on demand; they won't go to the Recycle Bin."),
            primary: recycle ? "Send to Recycle Bin" : "Clean");
        if (confirm != ContentDialogResult.Primary)
            return;

        // 3. Clean exactly what was shown, then update sizes from the outcomes.
        IsCleaning = true;
        try
        {
            var options = new CleanOptions { UseRecycleBin = recycle };
            var summary = await AppServices.CleanService.CleanAsync(items.Select(i => i.Result!).ToList(), options);
            var freed = summary.Files.Where(f => f.Freed).Select(f => f.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var item in items)
                item.ApplyClean(freed);

            AppServices.Settings.LastClean = new LastCleanInfo(DateTimeOffset.Now, summary.BytesFreed, summary.FilesFreed, summary.Locked, summary.LogPath);
            AppServices.SaveSettings();
            LastResult = summary;
            OnPropertyChanged(nameof(ResultTitle));
            OnPropertyChanged(nameof(ResultDetail));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            await Dialogs.InfoAsync(root, "The clean stopped", ex.Message);
        }
        finally
        {
            IsCleaning = false;
            Recalculate();
        }
    }

    public void DismissResult() => LastResult = null;

    public void OpenLog()
    {
        if (LastResult?.LogPath is { } path)
            Shell.ShowInExplorer(path);
    }

    /// <summary>Asks apps to close the way their X button would. Never kills them. Returns the ones still running.</summary>
    private static async Task<List<string>> CloseAppsAsync(IEnumerable<string> images)
    {
        var names = images.Select(Path.GetFileNameWithoutExtension).OfType<string>().ToList();
        foreach (var name in names)
        {
            foreach (var p in Process.GetProcessesByName(name))
            {
                using (p)
                {
                    try
                    {
                        p.CloseMainWindow();
                    }
                    catch (InvalidOperationException)
                    {
                    }
                }
            }
        }

        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline && names.Any(IsRunning))
            await Task.Delay(500);
        return names.Where(IsRunning).Select(n => n + ".exe").ToList();
    }

    private static bool IsRunning(string name)
    {
        var processes = Process.GetProcessesByName(name);
        foreach (var p in processes)
            p.Dispose();
        return processes.Length > 0;
    }
}
