using System.Text.Json.Serialization;

namespace Scour.Ipc;

/// <summary>
/// Everything that crosses the pipe between the app (standard user) and Scour.Elevator (admin).
/// Requests name rules, startup items and leftovers by id or path; the helper re-derives and
/// re-checks everything itself, so a compromised UI can't make it delete arbitrary files.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "$type")]
[JsonDerivedType(typeof(PingRequest), "ping")]
[JsonDerivedType(typeof(PingResponse), "pong")]
[JsonDerivedType(typeof(ScanRequest), "scan")]
[JsonDerivedType(typeof(ScanResponse), "scanResult")]
[JsonDerivedType(typeof(CleanRequest), "clean")]
[JsonDerivedType(typeof(CleanResponse), "cleanResult")]
[JsonDerivedType(typeof(AnalyseRequest), "analyse")]
[JsonDerivedType(typeof(AnalyseDone), "analyseDone")]
[JsonDerivedType(typeof(ProgressEvent), "progress")]
[JsonDerivedType(typeof(StartupToggleRequest), "startupToggle")]
[JsonDerivedType(typeof(StartupToggleResponse), "startupToggled")]
[JsonDerivedType(typeof(DeleteLeftoversRequest), "deleteLeftovers")]
[JsonDerivedType(typeof(ShutdownRequest), "shutdown")]
[JsonDerivedType(typeof(ErrorResponse), "error")]
public abstract record IpcMessage;

public sealed record PingRequest : IpcMessage;

public sealed record PingResponse(int ProtocolVersion, int ProcessId) : IpcMessage;

/// <summary>Scan these built-in rules with the helper's rights.</summary>
public sealed record ScanRequest(IReadOnlyList<string> RuleIds, IReadOnlyList<string> UserExclusions, double RecycleBinMinAgeDays) : IpcMessage;

public sealed record FileDto(string Path, long Size, long LastWriteUtcTicks, string Root, bool Prune, string? RuleId = null);

public sealed record RuleScanDto(
    string RuleId,
    string Status,
    IReadOnlyList<FileDto> Files,
    IReadOnlyList<string> RunningProcesses,
    IReadOnlyList<string> RefusedTargets,
    int ProtectedFiles,
    int TooNewFiles);

public sealed record ScanResponse(IReadOnlyList<RuleScanDto> Results) : IpcMessage;

/// <summary>
/// Delete what the user saw. The helper rescans the rules itself and only deletes files that
/// both its own scan and this list contain, with the same size and timestamp.
/// </summary>
public sealed record CleanRequest(
    IReadOnlyList<string> RuleIds,
    IReadOnlyList<string> UserExclusions,
    double RecycleBinMinAgeDays,
    IReadOnlyList<FileDto> Files,
    bool UseRecycleBin,
    bool DryRun) : IpcMessage;

public sealed record OutcomeDto(string Path, long Size, string RuleId, string Outcome, string? Error);

public sealed record CleanResponse(IReadOnlyList<OutcomeDto> Outcomes, int DirectoriesRemoved, string? LogPath) : IpcMessage;

/// <summary>Read a drive's MFT. Answered with binary entry frames, then <see cref="AnalyseDone"/>.</summary>
public sealed record AnalyseRequest(string Drive) : IpcMessage;

public sealed record AnalyseDone(int Count) : IpcMessage;

public sealed record ProgressEvent(string Stage, double? Fraction, long Items) : IpcMessage;

public sealed record StartupToggleRequest(string Id, bool Enabled, IReadOnlyList<string> ManualServices) : IpcMessage;

public sealed record StartupToggleResponse(string Id, bool Enabled) : IpcMessage;

public sealed record LeftoverDto(string Path, string Kind);

/// <summary>Remove leftovers under Program Files, ProgramData or the common Start menu.</summary>
public sealed record DeleteLeftoversRequest(IReadOnlyList<LeftoverDto> Leftovers, bool UseRecycleBin) : IpcMessage;

public sealed record ShutdownRequest : IpcMessage;

public sealed record ErrorResponse(string Message, string Kind = "failed") : IpcMessage;
