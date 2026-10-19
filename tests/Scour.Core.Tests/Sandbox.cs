using System.Diagnostics;
using Scour.Core.Paths;
using Scour.Core.Rules;
using Scour.Core.Safety;

namespace Scour.Core.Tests;

/// <summary>A throwaway folder tree per test, with <c>%SANDBOX%</c> pointing at it.</summary>
internal sealed class Sandbox : IDisposable
{
    public Sandbox()
    {
        Root = Path.Combine(Path.GetTempPath(), "scour-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Root);
    }

    public string Root { get; }

    public string PathOf(string relative) => Path.Combine(Root, relative);

    public string Dir(string relative) => Directory.CreateDirectory(PathOf(relative)).FullName;

    /// <summary>Creates a file of <paramref name="size"/> bytes, last written <paramref name="ageHours"/> ago.</summary>
    public string File(string relative, int size = 100, double ageHours = 24 * 30)
    {
        var path = PathOf(relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        System.IO.File.WriteAllBytes(path, new byte[size]);
        var time = DateTime.UtcNow.AddHours(-ageHours);
        System.IO.File.SetCreationTimeUtc(path, time);
        System.IO.File.SetLastWriteTimeUtc(path, time);
        return path;
    }

    public PathExpander Expander(params (string Name, string Value)[] extra)
    {
        var env = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["SANDBOX"] = Root };
        foreach (var (name, value) in extra)
            env[name] = value;
        return new PathExpander(name => env.GetValueOrDefault(name));
    }

    /// <summary>A deny-list that protects <c>Protected\</c> as a tree and <c>Folder\</c> as a folder.</summary>
    public DenyList DenyList() => new([PathOf("Folder")], [PathOf("Protected")]);

    /// <summary>Creates a directory junction. Needs no admin rights, unlike symlinks.</summary>
    public void Junction(string linkRelative, string targetAbsolute)
    {
        var link = PathOf(linkRelative);
        Directory.CreateDirectory(Path.GetDirectoryName(link)!);
        using var process = Process.Start(new ProcessStartInfo("cmd.exe", ["/c", "mklink", "/J", link, targetAbsolute])
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        })!;
        process.WaitForExit();
        Assert.Equal(0, process.ExitCode);
        Assert.True(new DirectoryInfo(link).Attributes.HasFlag(FileAttributes.ReparsePoint));
    }

    public static Rule Rule(params RuleTarget[] targets) => new()
    {
        Id = "test.rule",
        Name = "Test rule",
        Category = RuleCategories.System,
        Detect = ["%SANDBOX%"],
        Targets = targets,
    };

    public static RuleTarget Target(string path, string pattern = "*", bool recurse = true) =>
        new() { Path = path, Pattern = pattern, Recurse = recurse };

    public void Dispose()
    {
        try
        {
            // Junctions are removed as links; Directory.Delete doesn't follow them.
            Directory.Delete(Root, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
