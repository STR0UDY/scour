using Scour.Core.Safety;

namespace Scour.Core.Tests;

/// <summary>The real machine's deny-list. Read-only: nothing here deletes anything.</summary>
public sealed class DenyListTests
{
    private static readonly DenyList Deny = DenyList.ForCurrentSystem();

    private static string Windows => Environment.GetFolderPath(Environment.SpecialFolder.Windows);
    private static string Documents => Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);

    public static TheoryData<string> AlwaysProtected => new()
    {
        @"C:\",
        @"C:\pagefile.sys",
        Windows,
        Windows + @"\",
        Windows.ToLowerInvariant(),
        Path.Combine(Windows, "System32"),
        Path.Combine(Windows, "System32", "kernel32.dll"),
        Path.Combine(Windows, "System32", "drivers", "etc", "hosts"),
        Path.Combine(Windows, "Temp", "..", "System32", "ntdll.dll"),
        @"\\?\" + Path.Combine(Windows, "System32", "ntdll.dll"),
        Path.Combine(Windows, "WinSxS", "anything"),
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        Documents,
        Path.Combine(Documents, "thesis.docx"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Desktop), "notes.txt"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "Holiday", "1.jpg"),
        "C:\\Temp\\bad\0name", // Unparseable paths are treated as protected.
    };

    [Theory]
    [MemberData(nameof(AlwaysProtected))]
    public void Protected_paths_are_refused(string path) => Assert.True(Deny.IsProtected(path), path);

    public static TheoryData<string> Cleanable => new()
    {
        Path.Combine(Path.GetTempPath(), "foo.tmp"),
        Path.Combine(Windows, "Temp", "foo.tmp"),
        Path.Combine(Windows, "MEMORY.DMP"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "D3DSCache", "x"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Vendor", "cache", "x"),
    };

    [Theory]
    [MemberData(nameof(Cleanable))]
    public void Ordinary_cache_paths_are_allowed(string path) => Assert.False(Deny.IsProtected(path), path);

    [Fact]
    public void Lookalike_folder_names_are_not_confused_with_protected_ones()
    {
        // "System32-backup" starts with "System32" but isn't inside it.
        Assert.False(Deny.IsProtected(Path.Combine(Windows, "System32-backup", "x")));
    }

    [Fact]
    public void Every_protected_path_is_refused_as_a_recursive_target()
    {
        foreach (var path in Deny.Folders.Concat(Deny.Trees))
            Assert.True(Deny.IsProtectedRoot(path, recurse: true, "*"), path);
    }

    [Fact]
    public void Short_8dot3_names_resolve_to_the_protected_long_name()
    {
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var shortName = ShortPath(programFiles);
        if (shortName is null || shortName.Equals(programFiles, StringComparison.OrdinalIgnoreCase))
            Assert.Skip("8.3 names are disabled on this volume.");

        Assert.True(Deny.IsProtectedRoot(shortName, recurse: true, "*"), shortName);
    }

    private static string? ShortPath(string path)
    {
        var buffer = new char[1024];
        var length = GetShortPathNameW(path, buffer, (uint)buffer.Length);
        return length == 0 ? null : new string(buffer, 0, (int)length);
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
#pragma warning disable SYSLIB1054
    private static extern uint GetShortPathNameW(string longPath, char[] shortPath, uint length);
#pragma warning restore SYSLIB1054
}
