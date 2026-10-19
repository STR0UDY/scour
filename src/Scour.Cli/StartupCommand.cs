using System.CommandLine;
using Scour.Core.Settings;
using Scour.Core.Startup;

namespace Scour.Cli;

internal static class StartupCommand
{
    public static Command Create()
    {
        var json = new Option<bool>("--json") { Description = "Machine-readable output." };
        var microsoft = new Option<bool>("--microsoft") { Description = "Include Microsoft-signed items." };
        var list = new Command("list", "List apps, tasks and services that start with Windows.") { json, microsoft };
        list.SetAction(parse =>
        {
            var settings = new SettingsStore().Load();
            var items = StartupManager.List(settings.ManualServices)
                .Where(i => parse.GetValue(microsoft) || !i.IsMicrosoft)
                .OrderBy(i => i.Source).ThenBy(i => i.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (parse.GetValue(json))
            {
                Report.Write(items);
                return 0;
            }

            foreach (var group in items.GroupBy(i => i.Source))
            {
                Console.WriteLine();
                Console.WriteLine(group.Key switch
                {
                    StartupSource.RunKey => "Run keys",
                    StartupSource.StartupFolder => "Startup folders",
                    StartupSource.ScheduledTask => "Scheduled tasks",
                    _ => "Services",
                });
                foreach (var item in group)
                {
                    var state = item.Enabled ? "on " : "off";
                    var flags = string.Join(" ", new[]
                    {
                        item.TargetExists ? null : "[broken]",
                        item.Signature.ToString().ToLowerInvariant(),
                        item.NeedsElevation ? "admin" : null,
                    }.OfType<string>());
                    Console.WriteLine($"  {state} {Format.Fit(item.Name, 36)} {Format.Fit(item.Publisher ?? "", 30)} {flags}");
                    Console.WriteLine($"      {item.Id}");
                }
            }
            return 0;
        });

        var id = new Argument<string>("id") { Description = "Item id from 'scour startup list'." };
        var enable = new Command("enable", "Turn a startup item back on.") { id };
        enable.SetAction(parse => Toggle(parse.GetValue(id)!, true));
        var disable = new Command("disable", "Turn a startup item off (reversibly, like Task Manager).") { id };
        disable.SetAction(parse => Toggle(parse.GetValue(id)!, false));

        return new Command("startup", "Manage what starts with Windows.") { list, enable, disable };
    }

    private static int Toggle(string id, bool enabled)
    {
        var store = new SettingsStore();
        var settings = store.Load();
        try
        {
            var item = StartupManager.SetEnabled(id, enabled, settings.ManualServices);
            if (item.Source == StartupSource.Service)
            {
                settings.ManualServices.RemoveAll(s => s.Equals(item.Location, StringComparison.OrdinalIgnoreCase));
                if (!enabled)
                    settings.ManualServices.Add(item.Location);
                store.Save(settings);
            }
            Console.WriteLine($"{item.Name} is now {(enabled ? "enabled" : "disabled")}.");
            return 0;
        }
        catch (UnauthorizedAccessException)
        {
            Format.Error("That change needs admin. Run Scour from an elevated prompt.");
            return 1;
        }
        catch (InvalidOperationException ex)
        {
            Format.Error(ex.Message);
            return 1;
        }
    }
}
