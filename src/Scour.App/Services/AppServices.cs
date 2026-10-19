using Scour.Core;
using Scour.Core.Settings;

namespace Scour.App.Services;

/// <summary>
/// The app's shared state: settings, the engine and the clean service (which owns the admin
/// helper). One instance for the whole app; pages read from here instead of each loading their own.
/// </summary>
public static class AppServices
{
    private static ScourEngine? _engine;
    private static CleanService? _cleanService;

    public static SettingsStore Store { get; } = new();

    public static ScourSettings Settings { get; private set; } = Store.Load();

    public static ScourEngine Engine => _engine ??= new ScourEngine(new EngineOptions { Settings = Settings });

    public static CleanService CleanService => _cleanService ??= new CleanService(Engine);

    /// <summary>Raised after settings that change scanning (exclusions, rule packs) are saved.</summary>
    public static event EventHandler? EngineReloaded;

    public static void SaveSettings()
    {
        try
        {
            Store.Save(Settings);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Settings are a convenience; failing to save one shouldn't crash anything.
        }
    }

    /// <summary>
    /// Rebuilds the engine after rule packs or exclusions change. The admin helper is kept: it
    /// is still valid, and starting another would mean another UAC prompt.
    /// </summary>
    public static void ReloadEngine()
    {
        SaveSettings();
        _engine = new ScourEngine(new EngineOptions { Settings = Settings });
        var oldService = _cleanService;
        _cleanService = new CleanService(_engine);
        if (oldService is not null)
            _cleanService.AdoptHelperFrom(oldService);
        EngineReloaded?.Invoke(null, EventArgs.Empty);
    }

    public static async Task ShutdownAsync()
    {
        if (_cleanService is not null)
            await _cleanService.DisposeAsync();
    }
}
