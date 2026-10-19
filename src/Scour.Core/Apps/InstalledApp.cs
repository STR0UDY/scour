using System.Text.Json.Serialization;

namespace Scour.Core.Apps;

[JsonConverter(typeof(JsonStringEnumConverter<AppKind>))]
public enum AppKind
{
    /// <summary>A classic installer, listed under an Uninstall registry key.</summary>
    Win32,

    /// <summary>A packaged (Store or sideloaded MSIX) app.</summary>
    Msix,
}

public sealed record InstalledApp
{
    /// <summary><c>win32|hive|view|key</c> or <c>msix|PackageFullName</c>.</summary>
    public required string Id { get; init; }

    public required AppKind Kind { get; init; }
    public required string Name { get; init; }
    public string? Publisher { get; init; }
    public string? Version { get; init; }
    public DateTime? InstallDate { get; init; }

    /// <summary>From the installer's own estimate, or measured. Null until known.</summary>
    public long? SizeBytes { get; init; }

    public string? InstallLocation { get; init; }
    public string? UninstallString { get; init; }
    public string? QuietUninstallString { get; init; }
    public string? DisplayIcon { get; init; }

    /// <summary>Installed for everyone (HKLM, Program Files) rather than just this user.</summary>
    public bool PerMachine { get; init; }

    /// <summary>Registry key that disappears when a Win32 app is gone.</summary>
    public string? RegistryKey { get; init; }

    public string? PackageFullName { get; init; }

    public bool CanUninstall => Kind == AppKind.Msix || !string.IsNullOrWhiteSpace(UninstallString);
}
