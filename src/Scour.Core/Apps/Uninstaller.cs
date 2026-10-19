using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Scour.Core.Interop;
using Windows.Management.Deployment;

namespace Scour.Core.Apps;

public enum UninstallOutcome
{
    /// <summary>The app is gone.</summary>
    Completed,

    /// <summary>The user said no at the UAC prompt or in the uninstaller.</summary>
    Cancelled,

    /// <summary>The uninstaller finished but the app is still registered (cancelled inside it, or still running).</summary>
    StillInstalled,

    Failed,
}

public sealed record UninstallResult(UninstallOutcome Outcome, string? Message = null);

/// <summary>Runs the app's own uninstaller. Scour never deletes an installed app's files itself.</summary>
public static class Uninstaller
{
    /// <param name="quiet">Use the app's QuietUninstallString when it has one.</param>
    /// <param name="settleTime">How long to keep checking for the app to disappear after the uninstaller exits.</param>
    public static async Task<UninstallResult> RunAsync(InstalledApp app, bool quiet = false, TimeSpan? settleTime = null, CancellationToken cancellationToken = default)
    {
        if (app.Kind == AppKind.Msix)
            return await RemovePackageAsync(app);

        var command = quiet && !string.IsNullOrWhiteSpace(app.QuietUninstallString) ? app.QuietUninstallString : app.UninstallString;
        if (string.IsNullOrWhiteSpace(command))
            return new UninstallResult(UninstallOutcome.Failed, "This app doesn't register an uninstaller.");

        var (program, arguments) = CommandLine.Split(command);
        if (program is null)
            return new UninstallResult(UninstallOutcome.Failed, $"Can't make sense of the uninstall command: {command}");

        try
        {
            // Shell execute so the uninstaller's own manifest can ask for admin.
            using var process = Process.Start(new ProcessStartInfo(program, arguments) { UseShellExecute = true });
            if (process is not null)
                await process.WaitForExitAsync(cancellationToken);
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            return new UninstallResult(UninstallOutcome.Cancelled);
        }
        catch (Win32Exception ex)
        {
            return new UninstallResult(UninstallOutcome.Failed, ex.Message);
        }

        // Many uninstallers relaunch themselves from %TEMP% and exit straight away, so the
        // registry is the real signal that they're done.
        return await WaitForRemovalAsync(app, settleTime ?? TimeSpan.FromSeconds(20), cancellationToken)
            ? new UninstallResult(UninstallOutcome.Completed)
            : new UninstallResult(UninstallOutcome.StillInstalled);
    }

    /// <summary>Polls until the app is no longer registered, or the timeout passes.</summary>
    public static async Task<bool> WaitForRemovalAsync(InstalledApp app, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (true)
        {
            if (!AppInventory.StillInstalled(app))
                return true;
            if (DateTime.UtcNow >= deadline)
                return false;
            await Task.Delay(1000, cancellationToken);
        }
    }

    private static async Task<UninstallResult> RemovePackageAsync(InstalledApp app)
    {
        try
        {
            var result = await new PackageManager().RemovePackageAsync(app.PackageFullName);
            return result.IsRegistered
                ? new UninstallResult(UninstallOutcome.StillInstalled, result.ErrorText)
                : new UninstallResult(UninstallOutcome.Completed);
        }
        catch (Exception ex) when (ex is COMException or UnauthorizedAccessException or ArgumentException)
        {
            return new UninstallResult(UninstallOutcome.Failed, ex.Message);
        }
    }
}
