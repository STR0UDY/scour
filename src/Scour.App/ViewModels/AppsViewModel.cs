using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Scour.App.Helpers;
using Scour.App.Services;
using Scour.Core.Apps;
using Scour.Core.Cleaning;

namespace Scour.App.ViewModels;

public sealed class AppRow(InstalledApp app)
{
    public InstalledApp App { get; } = app;
    public string Name => App.Name;
    public string Publisher => App.Publisher ?? "";
    public string Version => App.Version ?? "";
    public string SizeText => App.SizeBytes is { } s ? Format.Bytes(s) : "";
    public string DateText => Format.Date(App.InstallDate);
    public string Subtitle => string.Join("  ·  ", new[] { App.Publisher, App.Version, KindText }.Where(p => !string.IsNullOrWhiteSpace(p)));
    public string KindText => App.Kind == AppKind.Msix ? "Store app" : App.PerMachine ? "All users" : "This user";
    public string Glyph => App.Kind == AppKind.Msix ? "" : "";
    public bool CanUninstall => App.CanUninstall;
    public string UninstallLabel => $"Uninstall {Name}";
}

public enum AppSort
{
    Name,
    Size,
    Date,
}

public sealed partial class AppsViewModel : ObservableObject
{
    private List<InstalledApp> _all = [];

    public static AppsViewModel Instance { get; } = new();

    public ObservableCollection<AppRow> Rows { get; } = [];

    [ObservableProperty]
    public partial bool IsLoading { get; set; }

    [ObservableProperty]
    public partial string Search { get; set; } = "";

    [ObservableProperty]
    public partial AppSort Sort { get; set; } = AppSort.Name;

    [ObservableProperty]
    public partial string Summary { get; set; } = "";

    /// <summary>Shown while an uninstaller runs; the user can say it's done if detection lags.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsUninstalling))]
    public partial string? Uninstalling { get; set; }

    public bool IsUninstalling => Uninstalling is not null;

    private CancellationTokenSource? _wait;

    public async Task LoadAsync()
    {
        IsLoading = true;
        try
        {
            _all = (await Task.Run(AppInventory.List)).ToList();
            Filter();
        }
        finally
        {
            IsLoading = false;
        }
    }

    partial void OnSearchChanged(string value) => Filter();

    partial void OnSortChanged(AppSort value) => Filter();

    private void Filter()
    {
        Rows.Clear();
        var matches = _all.Where(a => Search.Length == 0
            || a.Name.Contains(Search, StringComparison.OrdinalIgnoreCase)
            || (a.Publisher?.Contains(Search, StringComparison.OrdinalIgnoreCase) ?? false));
        matches = Sort switch
        {
            AppSort.Size => matches.OrderByDescending(a => a.SizeBytes ?? -1),
            AppSort.Date => matches.OrderByDescending(a => a.InstallDate ?? DateTime.MinValue),
            _ => matches.OrderBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase),
        };
        foreach (var app in matches)
            Rows.Add(new AppRow(app));
        Summary = $"{Format.Count(Rows.Count)} apps · {Format.Bytes(_all.Sum(a => a.SizeBytes ?? 0))} reported by their installers";
    }

    public void StopWaiting() => _wait?.Cancel();

    /// <summary>Uninstall → wait → leftover scan → let the user pick leftovers to remove.</summary>
    public async Task UninstallAsync(AppRow row, XamlRoot root)
    {
        var app = row.App;
        var confirm = await Dialogs.ShowAsync(root, $"Uninstall {app.Name}?",
            app.Kind == AppKind.Msix
                ? "Windows will remove this app and its data."
                : "Scour runs the app's own uninstaller; follow its steps. Afterwards Scour looks for anything it left behind and shows you before removing it.",
            primary: "Uninstall");
        if (confirm != ContentDialogResult.Primary)
            return;

        Uninstalling = app.Name;
        UninstallResult result;
        try
        {
            result = await Uninstaller.RunAsync(app, settleTime: TimeSpan.FromSeconds(15));
            if (result.Outcome == UninstallOutcome.StillInstalled)
            {
                // Some uninstallers keep running in another process; wait until gone or the user says so.
                _wait = new CancellationTokenSource();
                try
                {
                    if (await Uninstaller.WaitForRemovalAsync(app, TimeSpan.FromMinutes(15), _wait.Token))
                        result = new UninstallResult(UninstallOutcome.Completed);
                }
                catch (OperationCanceledException)
                {
                    result = AppInventory.StillInstalled(app) ? result : new UninstallResult(UninstallOutcome.Completed);
                }
            }
        }
        finally
        {
            Uninstalling = null;
        }

        switch (result.Outcome)
        {
            case UninstallOutcome.Cancelled:
                return;
            case UninstallOutcome.Failed:
                await Dialogs.InfoAsync(root, "The uninstaller didn't run", result.Message ?? "Unknown error.");
                return;
            case UninstallOutcome.StillInstalled:
                await Dialogs.InfoAsync(root, $"{app.Name} is still installed",
                    "It looks like the uninstall was cancelled or didn't finish, so Scour won't look for leftovers.");
                await LoadAsync();
                return;
        }

        await LoadAsync();
        var leftovers = await Task.Run(() => LeftoverScanner.Find(app, _all, AppServices.Engine.DenyList));
        await ReviewLeftoversAsync(app, leftovers, root);
    }

    private static async Task ReviewLeftoversAsync(InstalledApp app, IReadOnlyList<Leftover> leftovers, XamlRoot root)
    {
        var deletable = leftovers.Where(l => l.CanDelete).ToList();
        if (deletable.Count == 0)
        {
            var info = leftovers.Count == 0 ? "Nothing was left behind." : $"Only {leftovers.Count} registry setting(s) remain, which Scour lists but doesn't remove.";
            await Dialogs.InfoAsync(root, $"{app.Name} is uninstalled", info);
            return;
        }

        // Every box starts unticked: these are guesses, and Scour never removes a guess on its own.
        var panel = new StackPanel { Spacing = 6 };
        panel.Children.Add(new TextBlock
        {
            Text = "These look like they were left behind. Tick what you want removed.",
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 8),
        });
        var boxes = new List<(CheckBox Box, Leftover Item)>();
        foreach (var l in deletable)
        {
            var box = new CheckBox
            {
                Content = new StackPanel
                {
                    Children =
                    {
                        new TextBlock { Text = $"{l.Path}", TextWrapping = TextWrapping.Wrap },
                        new TextBlock
                        {
                            Text = $"{Format.Bytes(l.Size)} · {l.Reason}{(l.NeedsElevation ? " · needs admin" : "")}",
                            Style = (Style)Application.Current.Resources["ScourCaption"],
                        },
                    },
                },
            };
            boxes.Add((box, l));
            panel.Children.Add(box);
        }
        var registry = leftovers.Where(l => l.Kind == LeftoverKind.RegistryKey).ToList();
        if (registry.Count > 0)
        {
            panel.Children.Add(new TextBlock
            {
                Text = "Registry settings (listed for information, not removed):\n" + string.Join("\n", registry.Select(r => r.Path)),
                Style = (Style)Application.Current.Resources["ScourCaption"],
                Margin = new Thickness(0, 8, 0, 0),
                TextWrapping = TextWrapping.Wrap,
            });
        }

        var choice = await Dialogs.ShowAsync(root, $"{app.Name} left some things behind",
            new ScrollViewer { Content = panel, MaxHeight = 420 }, primary: "Remove selected", close: "Keep everything");
        var chosen = boxes.Where(b => b.Box.IsChecked == true).Select(b => b.Item).ToList();
        if (choice != ContentDialogResult.Primary || chosen.Count == 0)
            return;

        var recycle = AppServices.Settings.UseRecycleBin;
        var outcomes = new List<CleanFileResult>();
        var local = chosen.Where(l => !l.NeedsElevation || AppServices.Engine.IsElevated).ToList();
        var elevated = chosen.Except(local).ToList();
        try
        {
            if (local.Count > 0)
            {
                using var log = FileDeletionLog.Create();
                outcomes.AddRange((await Task.Run(() => LeftoverCleaner.Delete(local, AppServices.Engine.DenyList, recycle, log))).Files);
            }
            if (elevated.Count > 0 && await CleanViewModel.Instance.TryElevateAsync())
                outcomes.AddRange((await AppServices.CleanService.RunningHelper!.DeleteLeftoversAsync(elevated, recycle)).Files);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            await Dialogs.InfoAsync(root, "Some leftovers weren't removed", ex.Message);
            return;
        }

        var freed = outcomes.Where(o => o.Freed).Sum(o => o.Size);
        var failed = outcomes.Count(o => !o.Freed);
        await Dialogs.InfoAsync(root, "Leftovers removed",
            $"Freed {Format.Bytes(freed)}." + (failed > 0 ? $" {Format.Count(failed)} file(s) were in use or protected and were kept." : ""));
    }
}
