using System.Text.Json;

namespace Scour.Core.Cleaning;

public interface IDeletionLog : IDisposable
{
    /// <summary>Where the log lives, for "view log" links. Null if it isn't a file.</summary>
    string? FilePath { get; }

    void Write(CleanFileResult result);
}

/// <summary>One JSON object per line: time, rule id, path, size and outcome of every file a clean touched.</summary>
public sealed class FileDeletionLog : IDeletionLog
{
    private readonly StreamWriter _writer;
    private readonly Lock _lock = new();

    private FileDeletionLog(string path)
    {
        FilePath = path;
        _writer = new StreamWriter(path, append: false) { AutoFlush = true };
    }

    public string? FilePath { get; }

    /// <summary><c>%LOCALAPPDATA%\Scour\logs</c>.</summary>
    public static string DefaultDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Scour", "logs");

    public static FileDeletionLog Create(string? directory = null)
    {
        directory ??= DefaultDirectory;
        Directory.CreateDirectory(directory);
        var name = $"clean-{DateTime.Now:yyyyMMdd-HHmmss}-{Environment.ProcessId}.jsonl";
        return new FileDeletionLog(Path.Combine(directory, name));
    }

    public void Write(CleanFileResult result)
    {
        var line = JsonSerializer.Serialize(new
        {
            time = DateTimeOffset.Now,
            rule = result.RuleId,
            path = result.Path,
            size = result.Size,
            outcome = result.Outcome,
            error = result.Error,
        });
        lock (_lock)
            _writer.WriteLine(line);
    }

    public void Dispose() => _writer.Dispose();
}
