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
    private readonly TimeProvider _time;

    public Scanner(PathExpander expander, DenyList denyList, bool isElevated, TimeProvider? time = null)
    {
        _expander = expander;
        _denyList = denyList;
        _isElevated = isElevated;
        _time = time ?? TimeProvider.System;
    }

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

    public RuleScanResult ScanRule(Rule rule, CancellationToken cancellationToken = default)
    {
        if (!rule.Detect.Any(_expander.AnyExists))
            return new RuleScanResult { Rule = rule, Status = RuleScanStatus.NotDetected };

        if (rule.Elevation && !_isElevated)
            return new RuleScanResult { Rule = rule, Status = RuleScanStatus.NeedsElevation };

        var excludes = rule.Exclude
            .Select(ExpandExclude)
            .OfType<string>()
            .ToList();
        var newestAllowed = _time.GetUtcNow().UtcDateTime - TimeSpan.FromHours(rule.MinAgeHours);

        var files = new List<ScanFile>();
        var seenFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var refused = new List<string>();
        var protectedFiles = 0;
        var tooNew = 0;

        foreach (var target in rule.Targets)
        {
            var prune = target.Recurse && target.Pattern == "*";
            foreach (var root in _expander.ExpandDirectories(target.Path))
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (_denyList.IsProtectedRoot(root, target.Recurse, target.Pattern))
                {
                    refused.Add(root);
                    continue;
                }

                DirectoryInfo rootInfo = new(root);
                if (!rootInfo.Exists)
                    continue;
                if (rootInfo.Attributes.HasFlag(FileAttributes.ReparsePoint))
                {
                    refused.Add(root);
                    continue;
                }

                var pending = new Stack<DirectoryInfo>();
                pending.Push(rootInfo);
                while (pending.Count > 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var dir = pending.Pop();

                    foreach (var entry in SafeEnumerate(dir))
                    {
                        // Never follow or delete links: a junction could point anywhere.
                        if (entry.Attributes.HasFlag(FileAttributes.ReparsePoint))
                            continue;
                        if (IsExcluded(entry.FullName, excludes))
                            continue;

                        if (entry is DirectoryInfo subdir)
                        {
                            if (target.Recurse)
                                pending.Push(subdir);
                            continue;
                        }

                        var file = (FileInfo)entry;
                        if (!FileSystemName.MatchesSimpleExpression(target.Pattern, file.Name, ignoreCase: true))
                            continue;

                        var newest = file.LastWriteTimeUtc > file.CreationTimeUtc ? file.LastWriteTimeUtc : file.CreationTimeUtc;
                        if (rule.MinAgeHours > 0 && newest > newestAllowed)
                        {
                            tooNew++;
                            continue;
                        }

                        if (_denyList.IsProtected(file.FullName))
                        {
                            protectedFiles++;
                            continue;
                        }

                        if (seenFiles.Add(file.FullName))
                            files.Add(new ScanFile(file.FullName, file.Length, file.LastWriteTimeUtc, root, prune));
                    }
                }
            }
        }

        return new RuleScanResult
        {
            Rule = rule,
            Status = RuleScanStatus.Ok,
            Files = files,
            RunningProcesses = FindRunning(rule.RunningProcesses),
            RefusedTargets = refused,
            ProtectedFiles = protectedFiles,
            TooNewFiles = tooNew,
        };
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
