using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Scour.App.Helpers;
using Scour.App.Services;
using Scour.Core.Interop;
using Scour.Core.Startup;

namespace Scour.App.ViewModels;

/// <summary>One row on the Startup page. Flipping the switch applies the change immediately.</summary>
public sealed partial class StartupRow : ObservableObject
{
    private readonly StartupViewModel _owner;
    private bool _applying;

    public StartupRow(StartupItem item, StartupViewModel owner)
    {
        Item = item;
        _owner = owner;
        _isEnabled = item.Enabled;
    }

    public StartupItem Item { get; private set; }
    public string Name => Item.Name;
    public string Publisher => Item.Publisher ?? "Unknown publisher";
    public string Command => Item.Command;
    public string Location => Item.Location;
    public bool CanToggle => Item.CanToggle;
    public bool IsBroken => !Item.TargetExists;
    public bool NeedsElevation => Item.NeedsElevation;

    public string SignatureText => Item.Signature switch
    {
        SignatureStatus.Signed => "Signed",
        SignatureStatus.Unsigned => "Unsigned",
        SignatureStatus.Invalid => "Bad signature",
        SignatureStatus.Missing => "File missing",
        _ => "Not checked",
    };

    public bool IsUnsigned => Item.Signature is SignatureStatus.Unsigned or SignatureStatus.Invalid;

    public string Detail => IsBroken ? $"Points to a file that no longer exists: {Item.TargetPath ?? Item.Command}" : Item.TargetPath ?? Item.Command;

    private bool _isEnabled;

    public bool IsEnabled
    {
        get => _isEnabled;
        set
        {
            if (_isEnabled == value || _applying)
                return;
            _ = ApplyAsync(value);
        }
    }

    private async Task ApplyAsync(bool enabled)
    {
        _applying = true;
        var previous = _isEnabled;
        SetProperty(ref _isEnabled, enabled, nameof(IsEnabled));
        var ok = await _owner.SetEnabledAsync(this, enabled);
        if (!ok)
            SetProperty(ref _isEnabled, previous, nameof(IsEnabled));
        else
            Item = Item with { Enabled = enabled };
        _applying = false;
    }
}

public sealed partial class StartupViewModel : ObservableObject
{
    private List<StartupItem> _all = [];

    public static StartupViewModel Instance { get; } = new();

    public ObservableCollection<StartupRow> Rows { get; } = [];

    [ObservableProperty]
    public partial bool IsLoading { get; set; }

    [ObservableProperty]
    public partial StartupSource Tab { get; set; } = StartupSource.RunKey;

    [ObservableProperty]
    public partial string Search { get; set; } = "";

    [ObservableProperty]
    public partial string Summary { get; set; } = "";

    public bool ShowMicrosoft
    {
        get => AppServices.Settings.ShowMicrosoftStartupItems;
        set
        {
            AppServices.Settings.ShowMicrosoftStartupItems = value;
            AppServices.SaveSettings();
            OnPropertyChanged();
            Filter();
        }
    }

    public bool Loaded => _all.Count > 0;

    public async Task LoadAsync()
    {
        IsLoading = true;
        try
        {
            var manual = AppServices.Settings.ManualServices.ToList();
            _all = (await Task.Run(() => StartupManager.List(manual))).ToList();
            Filter();
        }
        finally
        {
            IsLoading = false;
        }
    }

    partial void OnTabChanged(StartupSource value) => Filter();

    partial void OnSearchChanged(string value) => Filter();

    private void Filter()
    {
        Rows.Clear();
        // The Apps tab covers Run keys and Startup folders, like Task Manager's Startup apps.
        var inTab = _all.Where(i => Tab == StartupSource.RunKey
            ? i.Source is StartupSource.RunKey or StartupSource.StartupFolder
            : i.Source == Tab).ToList();
        var visible = inTab.Where(i => ShowMicrosoft || !i.IsMicrosoft)
            .Where(i => Search.Length == 0 || i.Name.Contains(Search, StringComparison.OrdinalIgnoreCase)
                        || (i.Publisher?.Contains(Search, StringComparison.OrdinalIgnoreCase) ?? false))
            .OrderByDescending(i => !i.TargetExists)
            .ThenBy(i => i.Name, StringComparer.CurrentCultureIgnoreCase);
        foreach (var item in visible)
            Rows.Add(new StartupRow(item, this));

        var hidden = inTab.Count(i => i.IsMicrosoft);
        var on = inTab.Count(i => i.Enabled && (ShowMicrosoft || !i.IsMicrosoft));
        Summary = $"{Format.Count(on)} enabled"
                  + (hidden > 0 && !ShowMicrosoft ? $" · {Format.Count(hidden)} Microsoft items hidden" : "")
                  + (inTab.Count(i => !i.TargetExists) is var broken and > 0 ? $" · {broken} broken" : "");
    }

    /// <summary>User-scope changes happen here; machine-scope ones go through the admin helper.</summary>
    public async Task<bool> SetEnabledAsync(StartupRow row, bool enabled)
    {
        var item = row.Item;
        var manual = AppServices.Settings.ManualServices.ToList();
        try
        {
            if (item.NeedsElevation && !AppServices.Engine.IsElevated)
            {
                if (!await CleanViewModel.Instance.TryElevateAsync())
                    return false;
                await AppServices.CleanService.RunningHelper!.SetStartupEnabledAsync(item.Id, enabled, manual);
            }
            else
            {
                await Task.Run(() => StartupManager.SetEnabled(item.Id, enabled, manual));
            }

            if (item.Source == StartupSource.Service)
            {
                // Remember services we set to Manual, so they stay listed and can be turned back on.
                AppServices.Settings.ManualServices.RemoveAll(s => s.Equals(item.Location, StringComparison.OrdinalIgnoreCase));
                if (!enabled)
                    AppServices.Settings.ManualServices.Add(item.Location);
                AppServices.SaveSettings();
            }
            var index = _all.FindIndex(i => i.Id == item.Id);
            if (index >= 0)
                _all[index] = item with { Enabled = enabled };
            return true;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or InvalidOperationException or IOException)
        {
            if (App.MainWindow is { } w)
                await Dialogs.InfoAsync(w.XamlRoot, $"Couldn't change {item.Name}", ex.Message);
            return false;
        }
    }
}
