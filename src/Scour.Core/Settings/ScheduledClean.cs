using System.Diagnostics;
using System.Globalization;

namespace Scour.Core.Settings;

/// <summary>
/// Scheduled cleans are a Task Scheduler entry that runs the CLI, not a background service.
/// The task runs as the current user, only while they're signed in, with no admin rights.
/// </summary>
public static class ScheduledClean
{
    public const string TaskName = "Scour scheduled clean";

    /// <summary><c>scour.exe</c> next to the running app.</summary>
    public static string DefaultCliPath => Path.Combine(AppContext.BaseDirectory, "scour.exe");

    /// <summary>Creates or replaces the task. Returns an error message, or null on success.</summary>
    public static string? Apply(ScheduleSettings schedule, string? cliPath = null)
    {
        if (!schedule.Enabled)
            return Remove();

        cliPath ??= DefaultCliPath;
        if (!File.Exists(cliPath))
            return $"Can't find the Scour CLI at {cliPath}.";
        if (!TimeOnly.TryParseExact(schedule.Time, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var time))
            return $"'{schedule.Time}' isn't a time like 09:30.";

        var command = $"\"{cliPath}\" clean --profile {schedule.Profile} --yes --scheduled";
        var args = new List<string> { "/Create", "/F", "/TN", TaskName, "/TR", command, "/ST", time.ToString("HH:mm", CultureInfo.InvariantCulture) };
        switch (schedule.Frequency)
        {
            case ScheduleFrequency.Daily:
                args.AddRange(["/SC", "DAILY"]);
                break;
            case ScheduleFrequency.Weekly:
                args.AddRange(["/SC", "WEEKLY", "/D", schedule.Day.ToString()[..3].ToUpperInvariant()]);
                break;
            case ScheduleFrequency.Monthly:
                args.AddRange(["/SC", "MONTHLY", "/D", "1"]);
                break;
        }

        var (code, output) = RunSchtasks(args);
        return code == 0 ? null : output.Trim();
    }

    /// <summary>Removes the task if it exists. Returns an error message, or null on success.</summary>
    public static string? Remove()
    {
        if (!IsRegistered())
            return null;
        var (code, output) = RunSchtasks(["/Delete", "/F", "/TN", TaskName]);
        return code == 0 ? null : output.Trim();
    }

    public static bool IsRegistered() => RunSchtasks(["/Query", "/TN", TaskName]).ExitCode == 0;

    private static (int ExitCode, string Output) RunSchtasks(IEnumerable<string> args)
    {
        var info = new ProcessStartInfo("schtasks.exe")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var arg in args)
            info.ArgumentList.Add(arg);

        using var process = Process.Start(info)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        process.WaitForExit();
        return (process.ExitCode, stderr.Result + stdout.Result);
    }
}
