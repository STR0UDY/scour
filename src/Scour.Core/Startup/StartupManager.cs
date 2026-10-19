using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.ServiceProcess;
using Microsoft.Win32;
using Scour.Core.Interop;

namespace Scour.Core.Startup;

/// <summary>
/// Lists what starts with Windows and turns it on or off the way Task Manager does, so it is
/// reversible and Windows agrees with Scour: Run keys and Startup folders through the
/// <c>StartupApproved</c> keys, tasks are disabled (never deleted), services go to Manual
/// (never Disabled).
/// </summary>
public static class StartupManager
{
    private const string RunPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunOncePath = @"Software\Microsoft\Windows\CurrentVersion\RunOnce";
    private const string Run32Path = @"Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Run";
    private const string RunOnce32Path = @"Software\WOW6432Node\Microsoft\Windows\CurrentVersion\RunOnce";
    private const string ApprovedPath = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved";

    /// <summary>Everything that starts automatically, Microsoft items included (the UI hides them).</summary>
    /// <param name="manualByScour">Services Scour switched to Manual, so they stay listed and can be switched back.</param>
    public static IReadOnlyList<StartupItem> List(IEnumerable<string>? manualByScour = null)
    {
        var items = new List<StartupItem>();
        items.AddRange(RunKeys());
        items.AddRange(StartupFolders());
        items.AddRange(Tasks());
        items.AddRange(Services(new HashSet<string>(manualByScour ?? [], StringComparer.OrdinalIgnoreCase)));
        return items;
    }

    /// <summary>
    /// Enables or disables one item, found by id in a fresh listing (so a caller can't name
    /// something that isn't a startup entry). Throws <see cref="UnauthorizedAccessException"/>
    /// when the change needs admin.
    /// </summary>
    public static StartupItem SetEnabled(string id, bool enabled, IEnumerable<string>? manualByScour = null)
    {
        var item = List(manualByScour).FirstOrDefault(i => i.Id == id)
            ?? throw new InvalidOperationException("That startup item no longer exists.");
        if (!item.CanToggle)
            throw new InvalidOperationException($"{item.Name} can't be switched off from here.");

        switch (item.Source)
        {
            case StartupSource.RunKey:
            case StartupSource.StartupFolder:
                WriteApproved(item, enabled);
                break;
            case StartupSource.ScheduledTask:
                SetTaskEnabled(item, enabled);
                break;
            case StartupSource.Service:
                SetServiceStart(item.Location, enabled);
                break;
        }
        return item with { Enabled = enabled };
    }

    // ---------------------------------------------------------------- Run keys

    private static IEnumerable<StartupItem> RunKeys()
    {
        foreach (var (hive, scope) in new[] { (RegistryHive.CurrentUser, StartupScope.User), (RegistryHive.LocalMachine, StartupScope.Machine) })
        {
            foreach (var (path, approved, once) in new[]
                     {
                         (RunPath, "Run", false), (Run32Path, "Run32", false),
                         (RunOncePath, (string?)null, true), (RunOnce32Path, null, true),
                     })
            {
                using var key = OpenKey(hive, path);
                if (key is null)
                    continue;
                foreach (var name in key.GetValueNames())
                {
                    if (name.Length == 0 || key.GetValue(name) is not string command)
                        continue;
                    var enabled = approved is null || IsApproved(hive, approved, name);
                    yield return Describe(new StartupItem
                    {
                        Id = $"run|{hive}|{path}|{name}",
                        Source = StartupSource.RunKey,
                        Scope = scope,
                        Name = name,
                        Location = $@"{HiveName(hive)}\{path}",
                        Command = command,
                        Enabled = enabled,
                        CanToggle = !once,
                        Description = once ? "Runs once at the next sign-in, then removes itself." : null,
                    }, CommandLine.ExecutablePath(command));
                }
            }
        }
    }

    // ---------------------------------------------------------------- Startup folders

    private static IEnumerable<StartupItem> StartupFolders()
    {
        foreach (var (folder, hive, scope) in new[]
                 {
                     (Environment.GetFolderPath(Environment.SpecialFolder.Startup), RegistryHive.CurrentUser, StartupScope.User),
                     (Environment.GetFolderPath(Environment.SpecialFolder.CommonStartup), RegistryHive.LocalMachine, StartupScope.Machine),
                 })
        {
            if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder))
                continue;
            foreach (var file in Directory.EnumerateFiles(folder))
            {
                var name = Path.GetFileName(file);
                if (name.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase))
                    continue;

                string command = file;
                string? target = file;
                if (name.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase))
                {
                    var link = ShellLink.Read(file);
                    target = link?.Path;
                    command = link is null ? file : $"\"{link.Path}\" {link.Arguments}".Trim();
                }

                yield return Describe(new StartupItem
                {
                    Id = $"folder|{hive}|{folder}|{name}",
                    Source = StartupSource.StartupFolder,
                    Scope = scope,
                    Name = Path.GetFileNameWithoutExtension(name),
                    Location = folder,
                    Command = command,
                    Enabled = IsApproved(hive, "StartupFolder", name),
                }, target);
            }
        }
    }

    // ---------------------------------------------------------------- StartupApproved

    /// <summary>Task Manager's format: 12 bytes; an odd first byte means disabled.</summary>
    private static bool IsApproved(RegistryHive hive, string subkey, string name)
    {
        using var key = OpenKey(hive, $@"{ApprovedPath}\{subkey}");
        return key?.GetValue(name) is not byte[] { Length: > 0 } data || (data[0] & 1) == 0;
    }

    private static void WriteApproved(StartupItem item, bool enabled)
    {
        var parts = item.Id.Split('|');
        var hive = Enum.Parse<RegistryHive>(parts[1]);
        var subkey = item.Source == StartupSource.StartupFolder ? "StartupFolder"
            : parts[2].Equals(Run32Path, StringComparison.OrdinalIgnoreCase) ? "Run32" : "Run";
        var valueName = item.Source == StartupSource.StartupFolder ? parts[3] : item.Name;

        var data = new byte[12];
        data[0] = enabled ? (byte)2 : (byte)3;
        if (!enabled)
            BitConverter.GetBytes(DateTime.UtcNow.ToFileTimeUtc()).CopyTo(data, 4);

        using var root = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
        using var key = root.CreateSubKey($@"{ApprovedPath}\{subkey}", writable: true);
        key.SetValue(valueName, data, RegistryValueKind.Binary);
    }

    // ---------------------------------------------------------------- Tasks

    private static IEnumerable<StartupItem> Tasks()
    {
        var items = new List<StartupItem>();
        var type = Type.GetTypeFromProgID("Schedule.Service");
        if (type is null)
            return items;

        dynamic? service = null;
        try
        {
            service = Activator.CreateInstance(type)!;
            service.Connect();
            WalkTaskFolder(service.GetFolder("\\"), items, CurrentUserNames());
        }
        catch (COMException)
        {
        }
        finally
        {
            if (service is not null)
                Marshal.ReleaseComObject(service);
        }
        return items;
    }

    private static void WalkTaskFolder(dynamic folder, List<StartupItem> items, HashSet<string> me)
    {
        try
        {
            foreach (dynamic task in folder.GetTasks(1 /* TASK_ENUM_HIDDEN */))
            {
                try
                {
                    var item = DescribeTask(task, me);
                    if (item is not null)
                        items.Add(item);
                }
                catch (COMException)
                {
                    // Tasks we can't read the definition of are skipped.
                }
            }
            foreach (dynamic sub in folder.GetFolders(0))
                WalkTaskFolder(sub, items, me);
        }
        catch (Exception ex) when (ex is COMException or UnauthorizedAccessException)
        {
        }
    }

    private static StartupItem? DescribeTask(dynamic task, HashSet<string> me)
    {
        dynamic definition = task.Definition;
        var atStartup = false;
        foreach (dynamic trigger in definition.Triggers)
        {
            int kind = trigger.Type;
            if (kind is 8 /* boot */ or 9 /* logon */)
                atStartup = true;
        }
        if (!atStartup)
            return null;

        string? exe = null;
        var command = "";
        foreach (dynamic action in definition.Actions)
        {
            if ((int)action.Type != 0 /* exec */)
                continue;
            string path = action.Path ?? "";
            string args = action.Arguments ?? "";
            command = $"\"{path}\" {args}".Trim();
            exe ??= CommandLine.ExecutablePath(command);
        }

        string fullPath = task.Path;
        string? userId = null;
        try
        {
            userId = definition.Principal.UserId;
        }
        catch (COMException)
        {
        }
        var scope = userId is not null && me.Contains(userId) ? StartupScope.User : StartupScope.Machine;
        var folder = fullPath[..(fullPath.LastIndexOf('\\') + 1)];

        var item = Describe(new StartupItem
        {
            Id = $"task|{fullPath}",
            Source = StartupSource.ScheduledTask,
            Scope = scope,
            Name = (string)task.Name,
            Location = folder,
            Command = command,
            Enabled = (bool)task.Enabled,
            Description = definition.RegistrationInfo.Description as string,
        }, exe);

        return fullPath.StartsWith(@"\Microsoft\", StringComparison.OrdinalIgnoreCase)
            ? item with { IsMicrosoft = true }
            : item;
    }

    private static void SetTaskEnabled(StartupItem item, bool enabled)
    {
        var path = item.Id["task|".Length..];
        var type = Type.GetTypeFromProgID("Schedule.Service") ?? throw new InvalidOperationException("Task Scheduler isn't available.");
        dynamic service = Activator.CreateInstance(type)!;
        try
        {
            service.Connect();
            var split = path.LastIndexOf('\\');
            dynamic folder = service.GetFolder(split <= 0 ? "\\" : path[..split]);
            dynamic task = folder.GetTask(path[(split + 1)..]);
            task.Enabled = enabled;
        }
        catch (COMException ex) when (ex.HResult == unchecked((int)0x80070005))
        {
            throw new UnauthorizedAccessException("Changing this task needs admin.", ex);
        }
        finally
        {
            Marshal.ReleaseComObject(service);
        }
    }

    private static HashSet<string> CurrentUserNames()
    {
        using var identity = WindowsIdentity.GetCurrent();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { identity.Name, Environment.UserName };
        if (identity.User is not null)
            names.Add(identity.User.Value);
        return names;
    }

    // ---------------------------------------------------------------- Services

    private static IEnumerable<StartupItem> Services(HashSet<string> manualByScour)
    {
        ServiceController[] services;
        try
        {
            services = ServiceController.GetServices();
        }
        catch (InvalidOperationException)
        {
            yield break;
        }

        foreach (var service in services)
        {
            using (service)
            {
                StartupItem? item = null;
                try
                {
                    var automatic = service.StartType == ServiceStartMode.Automatic;
                    var scourManual = service.StartType == ServiceStartMode.Manual && manualByScour.Contains(service.ServiceName);
                    if (!automatic && !scourManual)
                        continue;

                    using var key = OpenKey(RegistryHive.LocalMachine, $@"SYSTEM\CurrentControlSet\Services\{service.ServiceName}");
                    var image = key?.GetValue("ImagePath") as string;
                    var binary = CommandLine.ExecutablePath(image);
                    if (binary is not null && Path.GetFileName(binary).Equals("svchost.exe", StringComparison.OrdinalIgnoreCase))
                    {
                        using var parameters = OpenKey(RegistryHive.LocalMachine, $@"SYSTEM\CurrentControlSet\Services\{service.ServiceName}\Parameters");
                        if (parameters?.GetValue("ServiceDll") is string dll)
                            binary = Environment.ExpandEnvironmentVariables(dll);
                    }

                    item = Describe(new StartupItem
                    {
                        Id = $"service|{service.ServiceName}",
                        Source = StartupSource.Service,
                        Scope = StartupScope.Machine,
                        Name = service.DisplayName,
                        Location = service.ServiceName,
                        Command = image ?? "",
                        Enabled = automatic,
                        Description = key?.GetValue("Description") as string is { } d && !d.StartsWith('@') ? d : null,
                    }, binary);
                }
                catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
                {
                }
                if (item is not null)
                    yield return item;
            }
        }
    }

    /// <summary>Disable → Manual (never Disabled); enable → Automatic. Needs admin.</summary>
    private static void SetServiceStart(string serviceName, bool enabled)
    {
        var info = new ProcessStartInfo("sc.exe")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        info.ArgumentList.Add("config");
        info.ArgumentList.Add(serviceName);
        info.ArgumentList.Add("start=");
        info.ArgumentList.Add(enabled ? "auto" : "demand");

        using var process = Process.Start(info)!;
        var output = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode == 5)
            throw new UnauthorizedAccessException("Changing a service needs admin.");
        if (process.ExitCode != 0)
            throw new InvalidOperationException(output.Trim());
    }

    // ---------------------------------------------------------------- Helpers

    private static StartupItem Describe(StartupItem item, string? target)
    {
        var signature = target is null ? new SignatureInfo(SignatureStatus.Unknown, null) : Authenticode.Verify(target);
        return item with
        {
            TargetPath = target,
            TargetExists = target is not null && File.Exists(target),
            Signature = signature.Status,
            Publisher = signature.Signer ?? (target is not null && File.Exists(target) ? CompanyName(target) : null),
            IsMicrosoft = item.IsMicrosoft || signature.IsMicrosoft,
        };
    }

    /// <summary>Version-info company, shown only when there's no signature to go on.</summary>
    private static string? CompanyName(string path)
    {
        try
        {
            var company = FileVersionInfo.GetVersionInfo(path).CompanyName;
            return string.IsNullOrWhiteSpace(company) ? null : company + " (unverified)";
        }
        catch (FileNotFoundException)
        {
            return null;
        }
    }

    private static RegistryKey? OpenKey(RegistryHive hive, string path)
    {
        try
        {
            using var root = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
            return root.OpenSubKey(path);
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static string HiveName(RegistryHive hive) => hive == RegistryHive.CurrentUser ? "HKCU" : "HKLM";
}
