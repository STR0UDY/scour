namespace Scour.Core;

/// <summary>Where Scour keeps its own files. Everything lives under one folder the user can delete.</summary>
public static class ScourPaths
{
    /// <summary><c>%LOCALAPPDATA%\Scour</c>.</summary>
    public static string DataDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Scour");

    /// <summary>Rule packs the user added (e.g. winapp2.ini imports), loaded after the built-in rules.</summary>
    public static string UserRulesDirectory => Path.Combine(DataDirectory, "rules");

    public static string LogsDirectory => Path.Combine(DataDirectory, "logs");

    /// <summary>The <c>rules</c> folder shipped next to the executable.</summary>
    public static string BuiltInRulesDirectory => Path.Combine(AppContext.BaseDirectory, "rules");
}
