using System.CommandLine;
using Scour.Core;
using Scour.Core.Scanning;

namespace Scour.Cli;

internal static class ScanCommand
{
    public static Command Create(Option<string?> rulesDir)
    {
        var selection = new SelectionOptions();
        var json = new Option<bool>("--json") { Description = "Machine-readable output." };
        var listFiles = new Option<bool>("--list-files") { Description = "With --json, include every file path." };
        var largest = new Option<int>("--largest")
        {
            Description = "Show this many of the largest files under each rule.",
            DefaultValueFactory = _ => 0,
        };

        var command = new Command("scan", "Show what would be cleaned. Changes nothing.");
        selection.AddTo(command);
        command.Options.Add(json);
        command.Options.Add(listFiles);
        command.Options.Add(largest);

        command.SetAction(async (parse, ct) =>
        {
            var resolved = SelectionOptions.Resolve(parse.GetValue(rulesDir), selection.Read(parse));
            if (resolved is not { } r)
                return 2;

            var asJson = parse.GetValue(json);
            await using var service = new CleanService(r.Engine);
            if (!await Elevation.PrepareAsync(service, r.Rules, parse.GetValue(selection.Elevate), ct))
                return 1;
            var results = await Run(service, r.Rules, quiet: asJson, ct);

            if (asJson)
                Report.PrintJson(results, parse.GetValue(listFiles));
            else
                Report.PrintTable(results, parse.GetValue(largest));
            return 0;
        });
        return command;
    }

    /// <summary>Scans with a one-line progress counter on stderr.</summary>
    public static async Task<IReadOnlyList<RuleScanResult>> Run(
        CleanService service, IReadOnlyList<Core.Rules.Rule> rules, bool quiet, CancellationToken ct)
    {
        var done = 0;
        var showProgress = !quiet && !Console.IsErrorRedirected;
        var progress = new SyncProgress<RuleScanResult>(_ =>
        {
            var n = Interlocked.Increment(ref done);
            if (showProgress)
                Console.Error.Write($"\rScanning {n}/{rules.Count} rules…");
        });

        var results = await service.ScanAsync(rules, progress, ct);
        if (showProgress)
            Console.Error.Write("\r" + new string(' ', 40) + "\r");
        return results;
    }
}

/// <summary>Runs the callback inline, so output is finished before the awaited call returns.</summary>
internal sealed class SyncProgress<T>(Action<T> report) : IProgress<T>
{
    private readonly Lock _lock = new();

    public void Report(T value)
    {
        lock (_lock)
            report(value);
    }
}
