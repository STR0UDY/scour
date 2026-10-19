using Scour.Core.Cleaning;
using Scour.Core.Rules;
using Scour.Core.Scanning;
using Scour.Ipc;

namespace Scour.Core.Elevation;

/// <summary>Converts between engine types and the pipe's DTOs.</summary>
public static class Dto
{
    public static FileDto ToDto(ScanFile f, string? ruleId = null) =>
        new(f.Path, f.Size, f.LastWriteUtc.Ticks, f.Root, f.PruneFolders, ruleId);

    public static ScanFile FromDto(FileDto f) =>
        new(f.Path, f.Size, new DateTime(f.LastWriteUtcTicks, DateTimeKind.Utc), f.Root, f.Prune);

    public static RuleScanDto ToDto(RuleScanResult r) => new(
        r.Rule.Id,
        r.Status.ToString(),
        r.Files.Select(f => ToDto(f)).ToList(),
        r.RunningProcesses,
        r.RefusedTargets,
        r.ProtectedFiles,
        r.TooNewFiles);

    public static RuleScanResult FromDto(RuleScanDto dto, Rule rule) => new()
    {
        Rule = rule,
        Status = Enum.TryParse<RuleScanStatus>(dto.Status, out var s) ? s : RuleScanStatus.NeedsElevation,
        Files = dto.Files.Select(FromDto).ToList(),
        RunningProcesses = dto.RunningProcesses,
        RefusedTargets = dto.RefusedTargets,
        ProtectedFiles = dto.ProtectedFiles,
        TooNewFiles = dto.TooNewFiles,
    };

    public static OutcomeDto ToDto(CleanFileResult r) => new(r.Path, r.Size, r.RuleId, r.Outcome.ToString(), r.Error);

    public static CleanFileResult FromDto(OutcomeDto o) =>
        new(o.Path, o.Size, o.RuleId, Enum.TryParse<DeleteOutcome>(o.Outcome, out var d) ? d : DeleteOutcome.Failed, o.Error);
}
