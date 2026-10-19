using Scour.Core;
using Scour.Core.Rules;

namespace Scour.Cli;

internal static class Elevation
{
    /// <summary>
    /// With --elevate, starts the admin helper when any selected rule needs it. Without it,
    /// those rules are reported as "needs admin". False if the user declined the UAC prompt.
    /// </summary>
    public static async Task<bool> PrepareAsync(CleanService service, IReadOnlyList<Rule> rules, bool elevate, CancellationToken ct)
    {
        if (!elevate || service.CanRunElevated || !rules.Any(r => r.Elevation))
            return true;
        try
        {
            if (await service.EnsureElevatedAsync(ct))
                return true;
            Format.Error("The admin prompt was declined, so nothing ran.");
            return false;
        }
        catch (Exception ex) when (ex is FileNotFoundException or InvalidOperationException or IOException or TimeoutException or OperationCanceledException)
        {
            Format.Error($"Couldn't start the admin helper: {ex.Message}");
            return false;
        }
    }
}
