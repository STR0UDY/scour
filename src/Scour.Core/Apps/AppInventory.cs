using System.Globalization;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using Windows.Management.Deployment;

namespace Scour.Core.Apps;

/// <summary>Installed apps: Win32 from the three Uninstall keys, MSIX from PackageManager.</summary>
public static class AppInventory
{
    private const string UninstallPath = @"Software\Microsoft\Windows\CurrentVersion\Uninstall";
    private const string Uninstall32Path = @"Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall";

    public static IReadOnlyList<InstalledApp> List()
    {
        var apps = new List<InstalledApp>();
        apps.AddRange(Win32Apps());
        apps.AddRange(PackagedApps());

        // The same app is sometimes registered under more than one key.
        return apps
            .GroupBy(a => (a.Kind, Name: a.Name.ToLowerInvariant(), a.Version))
            .Select(g => g.First())
            .OrderBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    /// <summary>Is a Win32 app's Uninstall key still there?</summary>
    public static bool StillInstalled(InstalledApp app)
    {
        if (app.Kind == AppKind.Msix)
            return PackagedApps().Any(a => a.Id == app.Id);
        if (app.RegistryKey is null)
            return false;

        var parts = app.Id.Split('|');
        using var root = RegistryKey.OpenBaseKey(Enum.Parse<RegistryHive>(parts[1]), RegistryView.Registry64);
        using var key = root.OpenSubKey(app.RegistryKey);
        return key is not null;
    }

    private static IEnumerable<InstalledApp> Win32Apps()
    {
        foreach (var (hive, path) in new[]
                 {
                     (RegistryHive.LocalMachine, UninstallPath),
                     (RegistryHive.LocalMachine, Uninstall32Path),
                     (RegistryHive.CurrentUser, UninstallPath),
                 })
        {
            using var root = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
            using var uninstall = root.OpenSubKey(path);
            if (uninstall is null)
                continue;

            foreach (var name in uninstall.GetSubKeyNames())
            {
                using var key = uninstall.OpenSubKey(name);
                if (key is null)
                    continue;
                var app = Read(key, hive, path, name);
                if (app is not null)
                    yield return app;
            }
        }
    }

    private static InstalledApp? Read(RegistryKey key, RegistryHive hive, string path, string keyName)
    {
        if (key.GetValue("DisplayName") is not string displayName || string.IsNullOrWhiteSpace(displayName))
            return null;
        // Hidden components, and updates/patches that hang off a parent product.
        if (key.GetValue("SystemComponent") is int and 1)
            return null;
        if (key.GetValue("ParentKeyName") is string || key.GetValue("ParentDisplayName") is string)
            return null;
        if (key.GetValue("ReleaseType") is string release && release.Contains("Update", StringComparison.OrdinalIgnoreCase))
            return null;

        var uninstall = key.GetValue("UninstallString") as string;
        var isMsi = key.GetValue("WindowsInstaller") is int and 1;
        if (string.IsNullOrWhiteSpace(uninstall) && isMsi && keyName.StartsWith('{'))
            uninstall = $"MsiExec.exe /X{keyName}";
        if (string.IsNullOrWhiteSpace(uninstall))
            return null;

        long? size = key.GetValue("EstimatedSize") is int kb && kb > 0 ? kb * 1024L : null;
        DateTime? installed = key.GetValue("InstallDate") is string d
            && DateTime.TryParseExact(d, "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date) ? date : null;
        var location = (key.GetValue("InstallLocation") as string)?.Trim().Trim('"');

        return new InstalledApp
        {
            Id = $"win32|{hive}|{path}|{keyName}",
            Kind = AppKind.Win32,
            Name = displayName.Trim(),
            Publisher = (key.GetValue("Publisher") as string)?.Trim(),
            Version = (key.GetValue("DisplayVersion") as string)?.Trim(),
            InstallDate = installed,
            SizeBytes = size,
            InstallLocation = string.IsNullOrWhiteSpace(location) ? null : location.TrimEnd('\\'),
            UninstallString = uninstall,
            QuietUninstallString = key.GetValue("QuietUninstallString") as string,
            DisplayIcon = key.GetValue("DisplayIcon") as string,
            PerMachine = hive == RegistryHive.LocalMachine,
            RegistryKey = $@"{path}\{keyName}",
        };
    }

    private static List<InstalledApp> PackagedApps()
    {
        var apps = new List<InstalledApp>();
        try
        {
            var manager = new PackageManager();
            foreach (var package in manager.FindPackagesForUser(""))
            {
                try
                {
                    // Frameworks, resource packs and Windows' own system apps aren't "apps" to remove.
                    if (package.IsFramework || package.IsResourcePackage || package.SignatureKind == Windows.ApplicationModel.PackageSignatureKind.System)
                        continue;
                    if (package.IsStub)
                        continue;

                    var id = package.Id;
                    var name = SafeGet(() => package.DisplayName);
                    if (string.IsNullOrWhiteSpace(name) || name.StartsWith("ms-resource:", StringComparison.OrdinalIgnoreCase))
                        name = id.Name;

                    var v = id.Version;
                    apps.Add(new InstalledApp
                    {
                        Id = $"msix|{id.FullName}",
                        Kind = AppKind.Msix,
                        Name = name,
                        Publisher = SafeGet(() => package.PublisherDisplayName),
                        Version = $"{v.Major}.{v.Minor}.{v.Build}.{v.Revision}",
                        InstallDate = SafeGet(() => package.InstalledDate.LocalDateTime),
                        InstallLocation = SafeGet(() => package.InstalledLocation?.Path),
                        PackageFullName = id.FullName,
                    });
                }
                catch (Exception ex) when (ex is COMException or UnauthorizedAccessException or ArgumentException or IOException)
                {
                    // One unreadable package shouldn't hide the rest.
                }
            }
        }
        catch (Exception ex) when (ex is COMException or UnauthorizedAccessException)
        {
        }
        return apps;
    }

    private static T? SafeGet<T>(Func<T> get)
    {
        try
        {
            return get();
        }
        catch (Exception ex) when (ex is COMException or UnauthorizedAccessException or ArgumentException or IOException)
        {
            // Packages mid-update or with a missing folder throw from individual properties.
            return default;
        }
    }
}
