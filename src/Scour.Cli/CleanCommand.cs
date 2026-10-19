using System.CommandLine;
using Scour.Core;
using Scour.Core.Cleaning;
using Scour.Core.Scanning;
using Scour.Core.Settings;

namespace Scour.Cli;

internal static class CleanCommand
{
    public static Command Create(Option<string?> rulesDir)
    {
        var selection = new SelectionOptions();
        var dryRun = new Option<bool>("--dry-run") { Description = "Scan and re-verify every file, but delete nothing." };
        var recycle = new Option<bool>("--recycle") { Description = "Send files to the Recycle Bin instead of deleting them (default: your app setting)." };
        var yes = new Option<bool>("--yes", "-y") { Description = "Don't ask for confirmation after the preview." };
        var ignoreRunning = new Option<bool>("--ignore-running")
        {
            Description = "Clean rules whose app is running anyway (files it holds open are skipped).",
        };
        var json = new Option<bool>("--json") { Description = "Print the result as JSON. Requires --yes or --dry-run." };
        var scheduled = new Option<bool>("--scheduled")
        {
            Description = "Used by the Task Scheduler entry: quiet, skips running apps, records the result for the app.",
            Hidden = true,
        };

        var command = new Command("clean", "Scan, show the preview, then delete what was shown.");
        selection.AddTo(command);
        command.Options.Add(dryRun);
        command.Options.Add(recycle);
        command.Options.Add(yes);
        command.Options.Add(ignoreRunning);
        command.Options.Add(json);
        command.Options.Add(scheduled);

        command.SetAction(async (parse, ct) =>
        {
            var resolved = SelectionOptions.Resolve(parse.GetValue(rulesDir), selection.Read(parse));
            if (resolved is not { } r)
                return 2;

            var isDryRun = parse.GetValue(dryRun);
            var isScheduled = parse.GetValue(scheduled);
            var asJson = parse.GetValue(json);
            var quiet = asJson || isScheduled;
            if (asJson && !isDryRun && !parse.GetValue(yes))
            {
                Format.Error("--json can't prompt for confirmation; add --yes or --dry-run.");
                return 2;
            }

            await using var service = new CleanService(r.Engine);
            if (!await Elevation.PrepareAsync(service, r.Rules, parse.GetValue(selection.Elevate), ct))
                return 1;

            // 1-4: expand, enumerate, deny-list, show.
            var results = await ScanCommand.Run(service, r.Rules, quiet, ct);
            if (!quiet)
                Report.PrintTable(results, largest: 0);

            var toClean = results.Where(x => x.Status == RuleScanStatus.Ok && x.FileCount > 0).ToList();
            if (!parse.GetValue(ignoreRunning))
            {
                foreach (var busy in toClean.Where(x => x.RunningProcesses.Count > 0).ToList())
                {
                    if (!quiet)
                        Format.Warn($"Skipping {busy.Rule.Name}: {string.Join(", ", busy.RunningProcesses)} is running. Close it, or pass --ignore-running.");
                    toClean.Remove(busy);
                }
            }

            if (toClean.Count == 0)
            {
                if (asJson)
                    Report.Write(new { dryRun = isDryRun, bytesFreed = 0, filesFreed = 0 });
                else if (!quiet)
                    Console.WriteLine("Nothing to clean.");
                return 0;
            }

            var useRecycleBin = parse.GetValue(recycle) || r.Engine.Settings.UseRecycleBin;
            var bytes = toClean.Sum(x => x.TotalBytes);
            var files = toClean.Sum(x => x.FileCount);
            if (!isDryRun && !parse.GetValue(yes) && !Confirm(bytes, files, useRecycleBin))
            {
                Console.WriteLine("Cancelled. Nothing was deleted.");
                return 1;
            }

            // 5-7: re-check, delete, log.
            var options = new CleanOptions { DryRun = isDryRun, UseRecycleBin = useRecycleBin };
            var summary = await service.CleanAsync(toClean, options, cancellationToken: ct);

            if (!isDryRun)
                RecordLastClean(summary);
            if (asJson)
                PrintJson(summary, isDryRun);
            else if (!quiet)
                PrintSummary(summary, isDryRun);
            // A dry run only predicts failures; it doesn't fail.
            return summary.Failed > 0 && !isDryRun ? 1 : 0;
        });
        return command;
    }

    /// <summary>So the app's Home page can show what the last clean (scheduled or not) did.</summary>
    private static void RecordLastClean(CleanSummary summary)
    {
        try
        {
            var store = new SettingsStore();
            var settings = store.Load();
            settings.LastClean = new LastCleanInfo(DateTimeOffset.Now, summary.BytesFreed, summary.FilesFreed, summary.Locked, summary.LogPath);
            store.Save(settings);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static bool Confirm(long bytes, int files, bool recycle)
    {
        if (Console.IsInputRedirected)
        {
            Format.Error("Input is redirected, so Scour can't ask for confirmation. Pass --yes to clean, or --dry-run to preview.");
            return false;
        }

        var verb = recycle ? "Send" : "Delete";
        var where = recycle ? " to the Recycle Bin" : "";
        Console.WriteLine();
        Console.Write($"{verb} {Format.Count(files)} files ({Format.Bytes(bytes)}){where}? [y/N] ");
        var answer = Console.ReadLine()?.Trim();
        return answer is not null && (answer.Equals("y", StringComparison.OrdinalIgnoreCase) || answer.Equals("yes", StringComparison.OrdinalIgnoreCase));
    }

    private static void PrintSummary(CleanSummary summary, bool dryRun)
    {
        Console.WriteLine();
        Console.WriteLine(dryRun
            ? $"Dry run: would free {Format.Bytes(summary.BytesFreed)} ({Format.Count(summary.FilesFreed)} files). Nothing was deleted."
            : $"Freed {Format.Bytes(summary.BytesFreed)} ({Format.Count(summary.FilesFreed)} files, {Format.Count(summary.DirectoriesRemoved)} empty folders).");

        if (summary.Locked > 0)
            Console.WriteLine($"  {Format.Count(summary.Locked)} locked file(s) skipped (in use).");
        if (summary.Skipped > 0)
            Console.WriteLine($"  {Format.Count(summary.Skipped)} file(s) skipped because they changed, vanished or are protected.");
        if (summary.Failed > 0)
            Format.Warn($"  {Format.Count(summary.Failed)} file(s) could not be deleted (access denied or error).");
        if (summary.LogPath is not null)
            Console.WriteLine($"  Log: {summary.LogPath}");
    }

    private static void PrintJson(CleanSummary summary, bool dryRun) => Report.Write(new
    {
        dryRun,
        bytesFreed = summary.BytesFreed,
        filesFreed = summary.FilesFreed,
        directoriesRemoved = summary.DirectoriesRemoved,
        locked = summary.Locked,
        skipped = summary.Skipped,
        failed = summary.Failed,
        log = summary.LogPath,
        outcomes = summary.Files.GroupBy(f => f.Outcome).ToDictionary(g => g.Key.ToString(), g => g.Count()),
        notFreed = summary.Files.Where(f => !f.Freed).Select(f => new { path = f.Path, rule = f.RuleId, outcome = f.Outcome, error = f.Error }),
    });
}
