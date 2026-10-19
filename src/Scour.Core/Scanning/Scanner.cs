using System.Diagnostics;
using System.IO.Enumeration;
using System.Text.RegularExpressions;
using Scour.Core.Paths;
using Scour.Core.Rules;
using Scour.Core.Safety;

namespace Scour.Core.Scanning;

/// <summary>Finds what each rule would delete. Read-only: never modifies the filesystem.</summary>
public sealed partial class Scanner
{
    private readonly PathExpander _expander;
    private readonly DenyList _denyList;
    private readonly bool _isElevated;
    private readonly ScannerOptions _options;
    private readonly List<string> _userExcludes;
    private readonly TimeProvider _time;

    public Scanner(PathExpander expander, DenyList denyList, bool isElevated, ScannerOptions? options = null, TimeProvider? time = null)
    {
        _expander = expander;
        _denyList = denyList;
        _isElevated = isElevated;
        _options = options ?? new ScannerOptions();
        _time = time ?? TimeProvider.System;
        _userExcludes = _options.UserExclusions.SelectMany(ExclusionPatterns).ToList();
    }

    public bool IsElevated => _isElevated;

    /// <summary>Scans rules in parallel. Results come back in input order; progress fires as each finishes.</summary>
    public async Task<IReadOnlyList<RuleScanResult>> ScanAsync(
        IReadOnlyList<Rule> rules,
        IProgress<RuleScanResult>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var results = new RuleScanResult[rules.Count];
        await Parallel.ForEachAsync(
            Enumerable.Range(0, rules.Count),
            new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = cancellationToken },
            (i, ct) =>
            {
                results[i] = ScanRule(rules[i], ct);
                progress?.Report(results[i]);
                return ValueTask.CompletedTask;
            });
        return results;
    }

    /// <summary>Would this rule be listed at all on this machine?</summary>
    public bool IsDetected(Rule rule) => rule.Detect.Any(_expander.AnyExists);

    public RuleScanResult ScanRule(Rule rule, CancellationToken cancellationToken = default)
    {
        if (!IsDetected(rule))
            return new RuleScanResult { Rule = rule, Status = RuleScanStatus.NotDetected };

        if (rule.Elevation && !_isElevated)
            return new RuleScanResult { Rule = rule, Status = RuleScanStatus.NeedsElevation, RunningProcesses = FindRunning(rule.RunningProcesses) };

        var state = new State(rule, _userExcludes.Concat(rule.Exclude.Select(ExpandExclude).OfType<string>()).ToList());

        if (rule.Handler == RuleHandlers.RecycleBin)
            ScanRecycleBin(state, cancellationToken);

        var newestAllowed = _time.GetUtcNow().UtcDateTime - TimeSpan.FromHours(rule.MinAgeHours);
        foreach (var target in rule.Targets)
        {
            var prune = target.Recurse && target.Pattern == "*";
            foreach (var root in _expander.ExpandDirectories(target.Path))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!CheckRoot(state, root, target.Recurse, target.Pattern))
                    continue;

                Walk(state, new DirectoryInfo(root), root, target.Recurse, file =>
                {
                    if (!FileSystemName.MatchesSimpleExpression(target.Pattern, file.Name, ignoreCase: true))
                        return false;
                    var newest = file.LastWriteTimeUtc > file.CreationTimeUtc ? file.LastWriteTimeUtc : file.CreationTimeUtc;
                    if (rule.MinAgeHours > 0 && newest > newestAllowed)
                    {
                        state.TooNew++;
                        return false;
                    }
                    return true;
                }, prune, cancellationToken);
            }
        }

        return new RuleScanResult
        {
            Rule = rule,
            Status = RuleScanStatus.Ok,
            Files = state.Files,
            RunningProcesses = FindRunning(rule.RunningProcesses),
            RefusedTargets = state.Refused,
            ProtectedFiles = state.Protected,
            TooNewFiles = state.TooNew,
        };
    }

    private void ScanRecycleBin(State state, CancellationToken cancellationToken)
    {
        var minAge = TimeSpan.FromDays(Math.Max(_options.RecycleBinMinAgeDays, state.Rule.MinAgeHours / 24));
        var cutoff = _time.GetUtcNow().UtcDateTime - minAge;

        foreach (var folder in RecycleBinScanner.Folders())
        {
            if (!CheckRoot(state, folder, recurse: true, "*"))
                continue;

            foreach (var item in RecycleBinScanner.Items(folder))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (minAge > TimeSpan.Zero && item.DeletedUtc > cutoff)
                {
                    state.TooNew++;
                    continue;
                }

                // Content first, metadata last: if the content can't go, Explorer still lists it.
                if (Directory.Exists(item.ContentPath))
                {
                    var content = new DirectoryInfo(item.ContentPath);
                    if (!content.Attributes.HasFlag(FileAttributes.ReparsePoint))
                        Walk(state, content, folder, recurse: true, _ => true, prune: true, cancellationToken);
                }
                else if (File.Exists(item.ContentPath))
                {
                    AddFile(state, new FileInfo(item.ContentPath), folder, prune: true);
                }
                AddFile(state, new FileInfo(item.MetadataPath), folder, prune: true);
            }
        }
    }

    private bool CheckRoot(State state, string root, bool recurse, string pattern)
    {
        if (_denyList.IsProtectedRoot(root, recurse, pattern))
        {
            state.Refused.Add(root);
            return false;
        }

        var info = new DirectoryInfo(root);
        if (!info.Exists)
            return false;
        if (info.Attributes.HasFlag(FileAttributes.ReparsePoint))
        {
            state.Refused.Add(root);
            return false;
        }
        return true;
    }

    private void Walk(State state, DirectoryInfo start, string root, bool recurse, Func<FileInfo, bool> accept, bool prune, CancellationToken cancellationToken)
    {
        var pending = new Stack<DirectoryInfo>();
        pending.Push(start);
        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var dir = pending.Pop();

            foreach (var entry in SafeEnumerate(dir))
            {
                // Never follow or delete links: a junction could point anywhere.
                if (entry.Attributes.HasFlag(FileAttributes.ReparsePoint))
                    continue;
                if (IsExcluded(entry.FullName, state.Excludes))
                    continue;

                if (entry is DirectoryInfo subdir)
                {
                    if (recurse)
                        pending.Push(subdir);
                    continue;
                }

                var file = (FileInfo)entry;
                if (accept(file))
                    AddFile(state, file, root, prune);
            }
        }
    }

    private void AddFile(State state, FileInfo file, string root, bool prune)
    {
        if (!file.Exists || file.Attributes.HasFlag(FileAttributes.ReparsePoint) || IsExcluded(file.FullName, state.Excludes))
            return;
        if (_denyList.IsProtected(file.FullName))
        {
            state.Protected++;
            return;
        }
        if (state.Seen.Add(file.FullName))
            state.Files.Add(new ScanFile(file.FullName, file.Length, file.LastWriteTimeUtc, root, prune));
    }

    private sealed class State(Rule rule, List<string> excludes)
    {
        public Rule Rule { get; } = rule;
        public List<string> Excludes { get; } = excludes;
        public List<ScanFile> Files { get; } = [];
        public HashSet<string> Seen { get; } = new(StringComparer.OrdinalIgnoreCase);
        public List<string> Refused { get; } = [];
        public int Protected { get; set; }
        public int TooNew { get; set; }
    }

    [GeneratedRegex(@"\{[^}]*\}")]
    private static partial Regex Token();

    /// <summary>
    /// Env vars expanded; tokens become <c>*</c> so one exclude covers every profile. Backslashes
    /// are escaped because simple expressions treat <c>\</c> as an escape character.
    /// </summary>
    private string? ExpandExclude(string pattern)
    {
        var expanded = _expander.ExpandVariables(pattern);
        return expanded is null ? null : Token().Replace(expanded, "*").Replace(@"\", @"\\");
    }

    /// <summary>A user exclusion is a literal path: the item itself and, for folders, everything below.</summary>
    private static IEnumerable<string> ExclusionPatterns(string path)
    {
        string full;
        try
        {
            full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            yield break;
        }

        // Escape everything simple expressions treat specially, so the path matches literally.
        var literal = full.Replace(@"\", @"\\").Replace("*", @"\*").Replace("?", @"\?")
            .Replace("<", @"\<").Replace(">", @"\>").Replace("\"", "\\\"");
        yield return literal;
        yield return literal + @"\\*";
    }

    private static bool IsExcluded(string path, List<string> excludes) =>
        excludes.Any(e => FileSystemName.MatchesSimpleExpression(e, path, ignoreCase: true));

    private static IEnumerable<FileSystemInfo> SafeEnumerate(DirectoryInfo dir)
    {
        try
        {
            return dir.EnumerateFileSystemInfos("*", new EnumerationOptions
            {
                IgnoreInaccessible = true,
                AttributesToSkip = 0,
                RecurseSubdirectories = false,
            }).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private static IReadOnlyList<string> FindRunning(IReadOnlyList<string> imageNames)
    {
        var running = new List<string>();
        foreach (var image in imageNames)
        {
            var processes = Process.GetProcessesByName(Path.GetFileNameWithoutExtension(image));
            if (processes.Length > 0)
                running.Add(image);
            foreach (var p in processes)
                p.Dispose();
        }
        return running;
    }
}
