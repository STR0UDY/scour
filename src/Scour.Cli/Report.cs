using System.Text.Json;
using System.Text.Json.Serialization;
using Scour.Core.Rules;
using Scour.Core.Scanning;

namespace Scour.Cli;

/// <summary>Prints scan results for people (grouped table) or machines (<c>--json</c>).</summary>
internal static class Report
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    public static void PrintTable(IReadOnlyList<RuleScanResult> results, int largest)
    {
        var shown = results.Where(r => r.Status != RuleScanStatus.NotDetected).ToList();
        foreach (var category in RuleCategories.All)
        {
            var group = shown.Where(r => r.Rule.Category == category).ToList();
            if (group.Count == 0)
                continue;

            Console.WriteLine();
            Console.WriteLine($"{category}  ({Format.Bytes(group.Sum(r => r.TotalBytes))})");
            foreach (var result in group)
                PrintRule(result, largest);
        }

        var hidden = results.Count - shown.Count;
        var total = shown.Sum(r => r.TotalBytes);
        var files = shown.Sum(r => r.FileCount);
        Console.WriteLine();
        Console.WriteLine($"Reclaimable: {Format.Bytes(total)} in {Format.Count(files)} files");
        if (hidden > 0)
            Console.WriteLine($"({hidden} rule(s) hidden because the app isn't installed)");
    }

    private static void PrintRule(RuleScanResult result, int largest)
    {
        var rule = result.Rule;
        var badge = rule.Safety switch
        {
            RuleSafety.Caution => " [caution]",
            RuleSafety.Advanced => " [advanced]",
            _ => "",
        };
        var name = Format.Fit(rule.Name + badge, 44);

        switch (result.Status)
        {
            case RuleScanStatus.NeedsElevation:
                Console.WriteLine($"  {name} needs admin: run from an elevated prompt");
                return;
            case RuleScanStatus.Ok:
                Console.WriteLine($"  {name} {Format.Count(result.FileCount),9} files {Format.Bytes(result.TotalBytes),10}   {rule.Id}");
                break;
        }

        if (result.RunningProcesses.Count > 0)
            Format.Warn($"      running: {string.Join(", ", result.RunningProcesses)} (close it first, or files may be locked)");
        foreach (var refused in result.RefusedTargets)
            Format.Warn($"      refused target (protected or a link): {refused}");
        if (result.ProtectedFiles > 0)
            Format.Warn($"      {result.ProtectedFiles} file(s) skipped by the deny-list");

        foreach (var file in result.Files.OrderByDescending(f => f.Size).Take(largest))
            Console.WriteLine($"      {Format.Bytes(file.Size),10}  {file.Path}");
        if (largest > 0 && result.FileCount > largest)
            Console.WriteLine($"      … and {Format.Count(result.FileCount - largest)} more");
    }

    public static void PrintJson(IReadOnlyList<RuleScanResult> results, bool includeFiles)
    {
        var payload = new
        {
            totalBytes = results.Sum(r => r.TotalBytes),
            totalFiles = results.Sum(r => r.FileCount),
            rules = results.Select(r => new
            {
                id = r.Rule.Id,
                name = r.Rule.Name,
                category = r.Rule.Category,
                safety = r.Rule.Safety,
                status = r.Status,
                files = r.FileCount,
                bytes = r.TotalBytes,
                running = r.RunningProcesses.Count > 0 ? r.RunningProcesses : null,
                refusedTargets = r.RefusedTargets.Count > 0 ? r.RefusedTargets : null,
                protectedFiles = r.ProtectedFiles > 0 ? r.ProtectedFiles : (int?)null,
                tooNewFiles = r.TooNewFiles > 0 ? r.TooNewFiles : (int?)null,
                paths = includeFiles ? r.Files.Select(f => new { path = f.Path, size = f.Size }) : null,
            }),
        };
        Console.WriteLine(JsonSerializer.Serialize(payload, Json));
    }

    public static void Write(object value) => Console.WriteLine(JsonSerializer.Serialize(value, Json));
}
