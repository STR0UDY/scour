namespace Scour.Core.Interop;

/// <summary>Finds the program a startup command line runs.</summary>
public static class CommandLine
{
    private static readonly string[] Hosts = ["rundll32.exe", "regsvr32.exe"];

    /// <summary>
    /// The executable (or, for rundll32, the DLL) a command runs. Handles quotes, env vars,
    /// unquoted paths with spaces and bare names on the system path. Null if nothing plausible.
    /// </summary>
    public static string? ExecutablePath(string? command)
    {
        if (string.IsNullOrWhiteSpace(command))
            return null;

        var expanded = Environment.ExpandEnvironmentVariables(command.Trim());
        var (exe, rest) = SplitFirst(expanded);
        if (exe is null)
            return null;

        exe = Resolve(exe);
        if (Hosts.Any(h => Path.GetFileName(exe).Equals(h, StringComparison.OrdinalIgnoreCase)))
        {
            // rundll32 "C:\x\y.dll",Entry → the DLL is what actually runs.
            var (dll, _) = SplitFirst(rest.TrimStart());
            if (dll is not null)
            {
                var comma = dll.IndexOf(',');
                return Resolve(comma > 0 ? dll[..comma] : dll);
            }
        }
        return exe;
    }

    /// <summary>Program and arguments, ready for <c>ProcessStartInfo</c>.</summary>
    public static (string? Program, string Arguments) Split(string command)
    {
        var (first, rest) = SplitFirst(Environment.ExpandEnvironmentVariables(command.Trim()));
        return (first is null ? null : Resolve(first), rest.Trim());
    }

    /// <summary>
    /// Splits off the program part: quoted, or the shortest space-separated prefix that exists
    /// (the order CreateProcess tries for unquoted paths).
    /// </summary>
    private static (string? First, string Remainder) SplitFirst(string command)
    {
        if (command.Length == 0)
            return (null, "");

        if (command[0] == '"')
        {
            var end = command.IndexOf('"', 1);
            return end < 0 ? (command[1..], "") : (command[1..end], command[(end + 1)..]);
        }

        // Unquoted "C:\Program Files\App\app.exe /x": try longer prefixes until a file exists.
        var spaces = new List<int>();
        for (var i = 0; i < command.Length; i++)
        {
            if (command[i] == ' ')
                spaces.Add(i);
        }
        spaces.Add(command.Length);
        foreach (var end in spaces)
        {
            var candidate = command[..end];
            if (File.Exists(candidate) || File.Exists(candidate + ".exe"))
                return (candidate, command[end..]);
        }

        // Nothing exists (a broken entry): cut after the first ".exe" rather than the first space.
        var exe = command.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
        var first = exe > 0 ? exe + 4 : spaces[0];
        return (command[..first], command[first..]);
    }

    private static string Resolve(string path)
    {
        path = path.Trim();
        if (!Path.HasExtension(path) && File.Exists(path + ".exe"))
            path += ".exe";
        if (Path.IsPathFullyQualified(path))
            return path;

        // Bare names: System32 first, then PATH, like CreateProcess.
        var name = Path.HasExtension(path) ? path : path + ".exe";
        var system = Path.Combine(Environment.SystemDirectory, name);
        if (File.Exists(system))
            return system;
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var candidate = Path.Combine(dir.Trim(), name);
                if (File.Exists(candidate))
                    return candidate;
            }
            catch (ArgumentException)
            {
            }
        }
        return path;
    }
}
