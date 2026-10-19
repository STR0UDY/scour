using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Scour.App.Helpers;
using Scour.App.Services;
using Scour.Core;
using Scour.Core.Rules;
using Scour.Core.Settings;
using Scour.Core.Updates;

namespace Scour.App.ViewModels;

public sealed partial class SettingsViewModel : ObservableObject
{
    private static ScourSettings S => AppServices.Settings;

    public SettingsViewModel()
    {
        foreach (var path in S.Exclusions)
            Exclusions.Add(path);
        RefreshRuleCounts();
        ScheduleStatus = S.Schedule.Enabled ? ScheduleDescription() : "Off";
    }

    public ObservableCollection<string> Exclusions { get; } = [];

    // ---------------------------------------------------------------- Appearance

    public int ThemeIndex
    {
        get => S.Theme switch { "light" => 1, "dark" => 2, _ => 0 };
        set
        {
            S.Theme = value switch { 1 => "light", 2 => "dark", _ => "system" };
            AppServices.SaveSettings();
            App.MainWindow?.ApplyTheme(S.Theme);
        }
    }

    // ---------------------------------------------------------------- Cleaning

    public bool UseRecycleBin
    {
        get => S.UseRecycleBin;
        set
        {
            S.UseRecycleBin = value;
            AppServices.SaveSettings();
        }
    }

    public double RecycleBinMinAgeDays
    {
        get => S.RecycleBinMinAgeDays;
        set
        {
            S.RecycleBinMinAgeDays = double.IsNaN(value) ? 0 : Math.Max(0, value);
            AppServices.ReloadEngine();
        }
    }

    public void AddExclusion(string path)
    {
        if (S.Exclusions.Contains(path, StringComparer.OrdinalIgnoreCase))
            return;
        S.Exclusions.Add(path);
        Exclusions.Add(path);
        AppServices.ReloadEngine();
    }

    public void RemoveExclusion(string path)
    {
        S.Exclusions.RemoveAll(p => p.Equals(path, StringComparison.OrdinalIgnoreCase));
        Exclusions.Remove(path);
        AppServices.ReloadEngine();
    }

    // ---------------------------------------------------------------- Rule packs

    [ObservableProperty]
    public partial string BuiltInRules { get; set; } = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasImport))]
    public partial int ImportedRules { get; set; }

    [ObservableProperty]
    public partial string ImportStatus { get; set; } = "";

    public bool HasImport => ImportedRules > 0;

    private void RefreshRuleCounts()
    {
        var rules = AppServices.Engine.Rules.Rules;
        ImportedRules = rules.Count(r => r.Id.StartsWith("winapp2.", StringComparison.Ordinal));
        BuiltInRules = $"{rules.Count - ImportedRules} rules, maintained with Scour";
        ImportStatus = ImportedRules > 0
            ? $"{ImportedRules} rules imported. They're off by default and marked Caution."
            : "Import the community winapp2.ini to cover more apps. Imported rules are off by default.";
    }

    public async Task<string> ImportWinapp2Async(string file)
    {
        var result = await Task.Run(() =>
        {
            var imported = Winapp2Importer.Import(Winapp2Importer.ReadFile(file));
            Winapp2Importer.Save(imported.Rules);
            return imported;
        });
        AppServices.ReloadEngine();
        RefreshRuleCounts();
        return $"Imported {result.Rules.Count} of {result.Sections} entries."
               + (result.Issues.Count > 0 ? $" {result.Issues.Count} were skipped (registry-only, unknown paths or unsafe targets)." : "");
    }

    public void RemoveWinapp2()
    {
        Winapp2Importer.Remove();
        AppServices.ReloadEngine();
        RefreshRuleCounts();
    }

    // ---------------------------------------------------------------- Scheduled clean

    public bool ScheduleEnabled
    {
        get => S.Schedule.Enabled;
        set
        {
            S.Schedule.Enabled = value;
            ApplySchedule();
            OnPropertyChanged();
        }
    }

    public int FrequencyIndex
    {
        get => (int)S.Schedule.Frequency;
        set
        {
            S.Schedule.Frequency = (ScheduleFrequency)Math.Clamp(value, 0, 2);
            ApplySchedule();
            OnPropertyChanged(nameof(IsWeekly));
        }
    }

    public bool IsWeekly => S.Schedule.Frequency == ScheduleFrequency.Weekly;

    public int DayIndex
    {
        get => (int)S.Schedule.Day;
        set
        {
            S.Schedule.Day = (DayOfWeek)Math.Clamp(value, 0, 6);
            ApplySchedule();
        }
    }

    public TimeSpan Time
    {
        get => TimeOnly.TryParse(S.Schedule.Time, out var t) ? t.ToTimeSpan() : TimeSpan.FromHours(12);
        set
        {
            S.Schedule.Time = TimeOnly.FromTimeSpan(value).ToString("HH:mm");
            ApplySchedule();
        }
    }

    public int ScheduleProfileIndex
    {
        get => Math.Max(0, RuleProfiles.All.ToList().IndexOf(S.Schedule.Profile));
        set
        {
            S.Schedule.Profile = RuleProfiles.All[Math.Clamp(value, 0, RuleProfiles.All.Count - 1)];
            ApplySchedule();
        }
    }

    public IReadOnlyList<string> ProfileNames => RuleProfiles.All.Select(RuleProfiles.DisplayName).ToList();

    [ObservableProperty]
    public partial string ScheduleStatus { get; set; } = "";

    private void ApplySchedule()
    {
        AppServices.SaveSettings();
        var error = ScheduledClean.Apply(S.Schedule);
        ScheduleStatus = error is not null ? $"Couldn't update the scheduled task: {error}"
            : S.Schedule.Enabled ? ScheduleDescription() : "Off";
    }

    private static string ScheduleDescription()
    {
        var when = S.Schedule.Frequency switch
        {
            ScheduleFrequency.Daily => "Every day",
            ScheduleFrequency.Weekly => $"Every {S.Schedule.Day}",
            _ => "On the 1st of each month",
        };
        return $"{when} at {S.Schedule.Time}, profile {RuleProfiles.DisplayName(S.Schedule.Profile)}. A Task Scheduler entry runs scour.exe; nothing stays running in the background.";
    }

    // ---------------------------------------------------------------- Updates

    public bool CheckForUpdates
    {
        get => S.CheckForUpdates;
        set
        {
            S.CheckForUpdates = value;
            AppServices.SaveSettings();
        }
    }

    [ObservableProperty]
    public partial string UpdateStatus { get; set; } = "";

    public async Task CheckNowAsync()
    {
        UpdateStatus = "Checking…";
        var info = await UpdateChecker.CheckAsync();
        UpdateStatus = info is null ? "Couldn't reach GitHub."
            : info.IsNewer ? $"Version {info.Latest} is available."
            : "You have the latest version.";
        LatestUrl = info?.IsNewer == true ? info.Url : null;
    }

    [ObservableProperty]
    public partial string? LatestUrl { get; set; }

    // ---------------------------------------------------------------- About

    public string Version => $"Version {UpdateChecker.CurrentVersion.ToString(3)}";
    public string Repository => UpdateChecker.ReleasesUrl.Replace("/releases", "");
    public string LogsFolder => ScourPaths.LogsDirectory;
    public string DataFolder => ScourPaths.DataDirectory;

    public void OpenLogs()
    {
        Directory.CreateDirectory(LogsFolder);
        Shell.Open(LogsFolder);
    }
}
