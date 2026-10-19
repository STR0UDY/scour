using System.Text.Json.Serialization;

namespace Scour.Core.Rules;

/// <summary>How much the user loses if this rule runs.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<RuleSafety>))]
public enum RuleSafety
{
    /// <summary>Pure cache or junk; regenerated on demand.</summary>
    [JsonStringEnumMemberName("safe")] Safe,

    /// <summary>Loses something the user may want, e.g. cookies or history.</summary>
    [JsonStringEnumMemberName("caution")] Caution,

    /// <summary>Only for people who know what it does.</summary>
    [JsonStringEnumMemberName("advanced")] Advanced,
}

/// <summary>One location a rule cleans: a folder, a file-name glob and whether to recurse.</summary>
public sealed record RuleTarget
{
    /// <summary>Folder to look in. Env vars and tokens such as <c>{chromiumProfile}</c> are expanded.</summary>
    public required string Path { get; init; }

    /// <summary>File-name glob (<c>*</c> and <c>?</c>) matched against each file's name.</summary>
    public string Pattern { get; init; } = "*";

    /// <summary>Also match files in subfolders.</summary>
    public bool Recurse { get; init; }
}

/// <summary>A cleanable location, loaded from one JSON file under <c>rules/</c>.</summary>
public sealed record Rule
{
    /// <summary>Editors use this to find <c>rule.schema.json</c>; ignored by the engine.</summary>
    [JsonPropertyName("$schema")]
    public string? Schema { get; init; }

    /// <summary>Stable dotted id, used in settings, logs and the CLI.</summary>
    public required string Id { get; init; }

    public required string Name { get; init; }

    public string? Description { get; init; }

    /// <summary>UI grouping: one of <see cref="RuleCategories.All"/>.</summary>
    public required string Category { get; init; }

    public RuleSafety Safety { get; init; } = RuleSafety.Safe;

    public bool DefaultEnabled { get; init; }

    /// <summary>Needs the admin helper (Windows folders, other users).</summary>
    public bool Elevation { get; init; }

    /// <summary>The rule only shows if one of these paths exists.</summary>
    public IReadOnlyList<string> Detect { get; init; } = [];

    /// <summary>Process image names (e.g. <c>Discord.exe</c>) that hold these files open.</summary>
    public IReadOnlyList<string> RunningProcesses { get; init; } = [];

    public IReadOnlyList<RuleTarget> Targets { get; init; } = [];

    /// <summary>
    /// A built-in handler for locations a path rule can't describe, e.g. <c>recycleBin</c>
    /// (pairs <c>$I</c>/<c>$R</c> files and ages them by deletion time). Targets may then be empty.
    /// </summary>
    public string? Handler { get; init; }

    /// <summary>Full-path globs that are never touched, even inside a target.</summary>
    public IReadOnlyList<string> Exclude { get; init; } = [];

    /// <summary>Skip files newer than this, so active temp files survive.</summary>
    public double MinAgeHours { get; init; }

    /// <summary>File the rule was loaded from; not part of the JSON.</summary>
    [JsonIgnore]
    public string? SourceFile { get; init; }
}

public static class RuleHandlers
{
    public const string RecycleBin = "recycleBin";

    public static readonly IReadOnlyList<string> All = [RecycleBin];
}

public static class RuleCategories
{
    public const string System = "System";
    public const string Browsers = "Browsers";
    public const string Apps = "Apps";
    public const string Games = "Games";
    public const string Dev = "Dev";

    public static readonly IReadOnlyList<string> All = [System, Browsers, Apps, Games, Dev];
}
