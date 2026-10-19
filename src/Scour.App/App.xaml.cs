using Microsoft.UI.Xaml;
using Scour.App.Services;
using Scour.Core;

namespace Scour.App;

public partial class App : Application
{
    private MainWindow? _window;

    /// <summary>Unhandled exceptions are appended here, locally. Nothing is ever sent anywhere.</summary>
    public static string CrashLogPath => Path.Combine(ScourPaths.DataDirectory, "crash.log");

    public static MainWindow? MainWindow => (Current as App)?._window;

    public App()
    {
        UnhandledException += (_, e) => LogCrash(e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => LogCrash(e.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            LogCrash(e.Exception);
            e.SetObserved();
        };
        InitializeComponent();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        _window = new MainWindow();
        _window.Closed += async (_, _) => await AppServices.ShutdownAsync();
        _window.Activate();
    }

    public static void LogCrash(Exception? ex)
    {
        if (ex is null)
            return;
        try
        {
            Directory.CreateDirectory(ScourPaths.DataDirectory);
            File.AppendAllText(CrashLogPath, $"[{DateTimeOffset.Now:O}] {ex}{Environment.NewLine}{Environment.NewLine}");
        }
        catch (IOException)
        {
        }
    }
}
