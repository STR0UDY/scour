using System.CommandLine;
using Scour.Core.Apps;
using Scour.Core.Cleaning;
using Scour.Core.Safety;
using Scour.Core.Settings;

namespace Scour.Cli;

internal static class AppsCommand
{
    public static Command Create()
    {
        var json = new Option<bool>("--json") { Description = "Machine-readable output." };
        var search = new Argument<string?>("search") { Description = "Only apps whose name or publisher contains this.", Arity = ArgumentArity.ZeroOrOne };
        var list = new Command("list", "List installed apps (Win32 and Store).") { search, json };
        list.SetAction(parse =>
        {
            var term = parse.GetValue(search);
            var apps = AppInventory.List().Where(a => term is null
                || a.Name.Contains(term, StringComparison.OrdinalIgnoreCase)
                || (a.Publisher?.Contains(term, StringComparison.OrdinalIgnoreCase) ?? false)).ToList();
            if (parse.GetValue(json))
            {
                Report.Write(apps);
                return 0;
            }
            foreach (var app in apps)
            {
                var size = app.SizeBytes is { } s ? Format.Bytes(s) : "";
                var date = app.InstallDate?.ToString("yyyy-MM-dd") ?? "";
                Console.WriteLine($"  {Format.Fit(app.Name, 42)} {Format.Fit(app.Publisher ?? "", 26)} {size,10} {date,10}  {(app.Kind == AppKind.Msix ? "store" : "")}");
            }
            Console.WriteLine();
            Console.WriteLine($"{apps.Count} apps");
            return 0;
        });

        var name = new Argument<string>("app") { Description = "Exact app name or id from 'scour apps list --json'." };
        var quiet = new Option<bool>("--quiet") { Description = "Use the app's silent uninstaller if it has one." };
        var removeLeftovers = new Option<bool>("--remove-leftovers") { Description = "After uninstalling, ask to remove what it left behind." };
        var uninstall = new Command("uninstall", "Run an app's own uninstaller, then look for leftovers.") { name, quiet, removeLeftovers };
        uninstall.SetAction(async (parse, ct) =>
        {
            var target = parse.GetValue(name)!;
            var apps = AppInventory.List();
            var matches = apps.Where(a => a.Id.Equals(target, StringComparison.OrdinalIgnoreCase) || a.Name.Equals(target, StringComparison.OrdinalIgnoreCase)).ToList();
            if (matches.Count != 1)
            {
                Format.Error(matches.Count == 0 ? $"No installed app called '{target}'." : $"'{target}' matches {matches.Count} apps; use the id from 'scour apps list --json'.");
                return 2;
            }

            var app = matches[0];
            Console.WriteLine($"Running {app.Name}'s uninstaller…");
            var result = await Uninstaller.RunAsync(app, parse.GetValue(quiet), cancellationToken: ct);
            switch (result.Outcome)
            {
                case UninstallOutcome.Cancelled:
                    Console.WriteLine("Cancelled.");
                    return 1;
                case UninstallOutcome.Failed:
                    Format.Error(result.Message ?? "The uninstaller failed.");
                    return 1;
                case UninstallOutcome.StillInstalled:
                    Format.Warn($"{app.Name} still looks installed. If the uninstaller is still open, finish it and run this again.");
                    return 1;
            }

            Console.WriteLine($"{app.Name} is uninstalled. Looking for leftovers…");
            var deny = DenyList.ForCurrentSystem();
            var leftovers = LeftoverScanner.Find(app, AppInventory.List(), deny, ct);
            if (leftovers.Count == 0)
            {
                Console.WriteLine("Nothing left behind.");
                return 0;
            }
            foreach (var l in leftovers)
                Console.WriteLine($"  {Format.Fit(l.Kind.ToString(), 12)} {(l.CanDelete ? Format.Bytes(l.Size) : ""),10}  {l.Path}{(l.NeedsElevation ? "  (admin)" : "")}");

            if (!parse.GetValue(removeLeftovers))
            {
                Console.WriteLine();
                Console.WriteLine("Nothing was removed. Pass --remove-leftovers to be asked about removing them.");
                return 0;
            }

            var deletable = leftovers.Where(l => l.CanDelete && !l.NeedsElevation).ToList();
            if (leftovers.Any(l => l.CanDelete && l.NeedsElevation))
                Format.Warn("Leftovers marked (admin) need the app, or an elevated prompt, to remove.");
            if (deletable.Count == 0 || Console.IsInputRedirected)
                return 0;
            Console.Write($"Remove {deletable.Count} item(s) ({Format.Bytes(deletable.Sum(l => l.Size))})? [y/N] ");
            if (!string.Equals(Console.ReadLine()?.Trim(), "y", StringComparison.OrdinalIgnoreCase))
                return 0;

            var settings = new SettingsStore().Load();
            using var log = FileDeletionLog.Create();
            var summary = LeftoverCleaner.Delete(deletable, deny, settings.UseRecycleBin, log, ct);
            Console.WriteLine($"Removed {Format.Bytes(summary.BytesFreed)}. Log: {summary.LogPath ?? log.FilePath}");
            return 0;
        });

        return new Command("apps", "List and uninstall apps.") { list, uninstall };
    }
}
