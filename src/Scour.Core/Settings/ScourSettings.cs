using System.Text.Json;
using System.Text.Json.Serialization;

namespace Scour.Core.Settings;

public enum ScheduleFrequency
{
    Daily,
    Weekly,
    Monthly,
}

public sealed class ScheduleSettings
{
    public bool Enabled { get; set; }
    public ScheduleFrequency Frequency { get; set; } = ScheduleFrequency.Weekly;
    public DayOfWeek Day { get; set; } = DayOfWeek.Sunday;

    /// <summary>Local time of day, "HH:mm".</summary>
    public string Time { get; set; } = "12:00";

    /// <summary>Which profile the scheduled clean runs: quick, deep, dev or custom.</summary>
    public string Profile { get; set; } = RuleProfiles.Quick;
}

/// <summary>What the last clean did, for the Home page.</summary>
public sealed record LastCleanInfo(DateTimeOffset When, long BytesFreed, int FilesFreed, int Locked, string? LogPath);

/// <summary>Everything the user can change. Stored as JSON in <c>%LOCALAPPDATA%\Scour\settings.json</c>.</summary>
public sealed class ScourSettings
{
    /// <summary>system, light or dark.</summary>
    public string Theme { get; set; } = "system";

    /// <summary>Send cleaned files to the Recycle Bin instead of deleting them.</summary>
    public bool UseRecycleBin { get; set; }

    /// <summary>The Recycle Bin rule only removes items deleted more than this many days ago.</summary>
    public double RecycleBinMinAgeDays { get; set; }

    /// <summary>Files and folders no rule may touch.</summary>
    public List<string> Exclusions { get; set; } = [];

    /// <summary>The user's ticks on the Clean page: rule id → on/off, overriding the rule's default.</summary>
    public Dictionary<string, bool> RuleChoices { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Profile the Home page's Scan button uses.</summary>
    public string Profile { get; set; } = RuleProfiles.Custom;

    /// <summary>Opt-in. Only ever calls the GitHub releases API, with no identifiers.</summary>
    public bool CheckForUpdates { get; set; }

    public ScheduleSettings Schedule { get; set; } = new();

    public LastCleanInfo? LastClean { get; set; }

    /// <summary>Show Microsoft-signed startup items. Off so people can't break Windows by accident.</summary>
    public bool ShowMicrosoftStartupItems { get; set; }

    /// <summary>Services Scour switched from Automatic to Manual, so they stay listed and can go back.</summary>
    public List<string> ManualServices { get; set; } = [];
}

/// <summary>Loads and saves <see cref="ScourSettings"/>. Saving is atomic, so a crash can't leave half a file.</summary>
public sealed class SettingsStore
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public SettingsStore(string? path = null)
    {
        FilePath = path ?? Path.Combine(ScourPaths.DataDirectory, "settings.json");
    }

    public string FilePath { get; }

    /// <summary>The saved settings, or defaults if there are none or the file is unreadable.</summary>
    public ScourSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var settings = JsonSerializer.Deserialize<ScourSettings>(File.ReadAllText(FilePath), Json);
                if (settings is not null)
                {
                    // JSON gives us a case-sensitive dictionary; rule ids are not case-sensitive.
                    settings.RuleChoices = new Dictionary<string, bool>(settings.RuleChoices, StringComparer.OrdinalIgnoreCase);
                    return settings;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            // A broken settings file shouldn't stop the app; the next save replaces it.
        }
        return new ScourSettings();
    }

    public void Save(ScourSettings settings)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        var temp = FilePath + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(settings, Json));
        File.Move(temp, FilePath, overwrite: true);
    }
}
