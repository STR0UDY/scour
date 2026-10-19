using System.Text.Json.Serialization;
using Scour.Core.Interop;

namespace Scour.Core.Startup;

[JsonConverter(typeof(JsonStringEnumConverter<StartupSource>))]
public enum StartupSource
{
    /// <summary>A Run or RunOnce registry value.</summary>
    RunKey,

    /// <summary>A shortcut or file in a Startup folder.</summary>
    StartupFolder,

    /// <summary>A Task Scheduler task with a logon or boot trigger.</summary>
    ScheduledTask,

    /// <summary>An automatic-start service.</summary>
    Service,
}

[JsonConverter(typeof(JsonStringEnumConverter<StartupScope>))]
public enum StartupScope
{
    /// <summary>Only the current user; changing it never needs admin.</summary>
    User,

    /// <summary>Every user on the machine; changing it needs admin.</summary>
    Machine,
}

/// <summary>Something that starts with Windows or at sign-in.</summary>
public sealed record StartupItem
{
    /// <summary>Stable key: source, scope, location and name. What enable/disable requests carry.</summary>
    public required string Id { get; init; }

    public required StartupSource Source { get; init; }
    public required StartupScope Scope { get; init; }

    /// <summary>Value name, shortcut name, task name or service display name.</summary>
    public required string Name { get; init; }

    /// <summary>Registry key, folder, task folder or service key name.</summary>
    public required string Location { get; init; }

    /// <summary>The raw command line, as Windows will run it.</summary>
    public string Command { get; init; } = "";

    /// <summary>The program the command runs, if Scour could work it out.</summary>
    public string? TargetPath { get; init; }

    public bool Enabled { get; init; }

    /// <summary>RunOnce entries and some tasks can't be toggled the way Task Manager does it.</summary>
    public bool CanToggle { get; init; } = true;

    public SignatureStatus Signature { get; init; }
    public string? Publisher { get; init; }
    public bool IsMicrosoft { get; init; }

    /// <summary>False when the program it points at is gone: a broken entry.</summary>
    public bool TargetExists { get; init; }

    public string? Description { get; init; }

    public bool NeedsElevation => Scope == StartupScope.Machine || Source == StartupSource.Service;
}
