using System.Text.Json;
using Scour.Core.Rules;

namespace Scour.Core.Tests;

public sealed class RuleLoaderTests : IDisposable
{
    private readonly Sandbox _box = new();

    public void Dispose() => _box.Dispose();

    private const string Valid = """
        {
          "id": "app.example.cache",
          "name": "Example cache",
          "category": "Apps",
          "safety": "safe",
          "defaultEnabled": true,
          "detect": ["%APPDATA%\\Example"],
          "targets": [{ "path": "%APPDATA%\\Example\\Cache", "recurse": true }]
        }
        """;

    private void Write(string name, string json) => File.WriteAllText(_box.PathOf(name), json);

    [Fact]
    public void Parses_the_spec_example()
    {
        var rule = RuleLoader.Parse(Valid);

        Assert.Equal("app.example.cache", rule.Id);
        Assert.Equal(RuleSafety.Safe, rule.Safety);
        Assert.Equal("*", rule.Targets[0].Pattern);
        Assert.True(rule.Targets[0].Recurse);
        Assert.Empty(RuleValidator.Validate(rule));
    }

    [Fact]
    public void Unknown_properties_are_rejected_so_typos_surface()
    {
        Assert.Throws<JsonException>(() => RuleLoader.Parse(Valid.Replace("\"safety\"", "\"saftey\"")));
    }

    [Fact]
    public void Missing_required_property_is_rejected()
    {
        Assert.Throws<JsonException>(() => RuleLoader.Parse(Valid.Replace("\"name\": \"Example cache\",", "")));
    }

    [Theory]
    [InlineData(@"C:\\Users\\me\\AppData\\Roaming\\Example\\Cache", "environment variable")]
    [InlineData(@"%APPDATA%\\Example\\..\\..\\Documents", "'..'")]
    [InlineData(@"%APPDATA%\\{nope}\\Cache", "unknown token")]
    public void Bad_target_paths_are_flagged(string path, string expected)
    {
        var rule = RuleLoader.Parse(Valid.Replace(@"%APPDATA%\\Example\\Cache", path));

        Assert.Contains(RuleValidator.Validate(rule), m => m.Contains(expected, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Rule_without_detect_is_flagged()
    {
        var rule = RuleLoader.Parse(Valid) with { Detect = [] };

        Assert.Contains(RuleValidator.Validate(rule), m => m.Contains("detect"));
    }

    [Fact]
    public void Loader_reports_bad_files_and_keeps_good_ones()
    {
        Write("app.example.cache.json", Valid);
        Write("broken.json", "{ not json");
        Write("wrong-name.json", Valid.Replace("app.example.cache", "app.other.cache"));

        var result = RuleLoader.LoadDirectory(_box.Root);

        Assert.Equal(["app.example.cache"], result.Rules.Select(r => r.Id));
        Assert.Contains(result.Issues, i => i.File == "broken.json");
        Assert.Contains(result.Issues, i => i.File == "wrong-name.json" && i.Message.Contains("File name"));
    }

    [Fact]
    public void Duplicate_ids_are_reported()
    {
        _box.Dir("a");
        _box.Dir("b");
        Write(@"a\app.example.cache.json", Valid);
        Write(@"b\app.example.cache.json", Valid);

        var result = RuleLoader.LoadDirectory(_box.Root);

        Assert.Single(result.Rules);
        Assert.Contains(result.Issues, i => i.Message.Contains("Duplicate"));
    }

    [Fact]
    public void Schema_file_is_not_loaded_as_a_rule()
    {
        Write("app.example.cache.json", Valid);
        Write("rule.schema.json", "{}");

        Assert.Empty(RuleLoader.LoadDirectory(_box.Root).Issues);
    }
}
