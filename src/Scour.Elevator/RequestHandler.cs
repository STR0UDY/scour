using Scour.Core;
using Scour.Core.Analysis;
using Scour.Core.Apps;
using Scour.Core.Cleaning;
using Scour.Core.Elevation;
using Scour.Core.Paths;
using Scour.Core.Rules;
using Scour.Core.Safety;
using Scour.Core.Scanning;
using Scour.Core.Startup;
using Scour.Ipc;

namespace Scour.Elevator;

/// <summary>
/// Answers one request at a time. Nothing the client sends is trusted as a path to delete:
/// rules come from the helper's own built-in rules folder, cleans are re-derived from its own
/// scan, and every path still goes through the deny-list.
/// </summary>
internal sealed class RequestHandler(Stream pipe)
{
    private readonly DenyList _denyList = DenyList.ForCurrentSystem();

    // Only the rules shipped next to the helper. User-writable rule packs never run as admin.
    private readonly Lazy<IReadOnlyList<Rule>> _rules = new(() => RuleLoader.LoadDirectory(ScourPaths.BuiltInRulesDirectory).Rules);

    /// <returns>False to end the session.</returns>
    public async Task<bool> HandleAsync(IpcMessage message)
    {
        try
        {
            IpcMessage? reply = message switch
            {
                PingRequest => new PingResponse(PipeProtocol.Version, Environment.ProcessId),
                ScanRequest scan => Scan(scan),
                CleanRequest clean => Clean(clean),
                StartupToggleRequest toggle => ToggleStartup(toggle),
                DeleteLeftoversRequest leftovers => DeleteLeftovers(leftovers),
                AnalyseRequest analyse => await AnalyseAsync(analyse),
                _ => new ErrorResponse($"Unsupported request {message.GetType().Name}.", "invalid"),
            };
            if (reply is not null)
                await PipeProtocol.WriteAsync(pipe, reply);
            return true;
        }
        catch (IOException)
        {
            return false; // The client went away.
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException)
        {
            await PipeProtocol.WriteAsync(pipe, new ErrorResponse(ex.Message, "unauthorized"));
            return true;
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or KeyNotFoundException)
        {
            await PipeProtocol.WriteAsync(pipe, new ErrorResponse(ex.Message, "invalid"));
            return true;
        }
        catch (Exception ex)
        {
            await PipeProtocol.WriteAsync(pipe, new ErrorResponse(ex.Message));
            return true;
        }
    }

    private Scanner CreateScanner(IReadOnlyList<string> exclusions, double recycleDays) =>
        new(new PathExpander(), _denyList, isElevated: true, new ScannerOptions { UserExclusions = exclusions, RecycleBinMinAgeDays = recycleDays });

    private List<Rule> Resolve(IReadOnlyList<string> ids)
    {
        var byId = _rules.Value.ToDictionary(r => r.Id, StringComparer.OrdinalIgnoreCase);
        return ids.Distinct(StringComparer.OrdinalIgnoreCase).Select(id =>
            byId.TryGetValue(id, out var rule) ? rule : throw new KeyNotFoundException($"'{id}' isn't a built-in rule.")).ToList();
    }

    private ScanResponse Scan(ScanRequest request)
    {
        var scanner = CreateScanner(request.UserExclusions, request.RecycleBinMinAgeDays);
        var results = Resolve(request.RuleIds).Select(rule => scanner.ScanRule(rule)).ToList();
        return new ScanResponse(results.Select(Dto.ToDto).ToList());
    }

    private CleanResponse Clean(CleanRequest request)
    {
        // Rescan, then keep only files the user was shown AND our own scan still finds unchanged.
        var scanner = CreateScanner(request.UserExclusions, request.RecycleBinMinAgeDays);
        var shown = request.Files.ToLookup(f => f.RuleId ?? "", StringComparer.OrdinalIgnoreCase);
        var filtered = new List<RuleScanResult>();
        var outcomes = new List<CleanFileResult>();

        foreach (var rule in Resolve(request.RuleIds))
        {
            var ours = scanner.ScanRule(rule);
            var found = ours.Files.ToDictionary(f => f.Path, StringComparer.OrdinalIgnoreCase);
            var keep = new List<ScanFile>();
            foreach (var file in shown[rule.Id])
            {
                if (found.TryGetValue(file.Path, out var match) && match.Size == file.Size && match.LastWriteUtc.Ticks == file.LastWriteUtcTicks)
                    keep.Add(match);
                else
                    outcomes.Add(new CleanFileResult(file.Path, file.Size, rule.Id, DeleteOutcome.Changed, "Not matched by the rule's own rescan."));
            }
            filtered.Add(ours with { Files = keep });
        }

        using var log = request.DryRun ? null : FileDeletionLog.Create();
        var summary = new Cleaner(_denyList).Clean(filtered, new CleanOptions { DryRun = request.DryRun, UseRecycleBin = request.UseRecycleBin }, log);
        outcomes.AddRange(summary.Files);
        return new CleanResponse(outcomes.Select(Dto.ToDto).ToList(), summary.DirectoriesRemoved, summary.LogPath);
    }

    private static StartupToggleResponse ToggleStartup(StartupToggleRequest request)
    {
        var item = StartupManager.SetEnabled(request.Id, request.Enabled, request.ManualServices);
        return new StartupToggleResponse(item.Id, item.Enabled);
    }

    private CleanResponse DeleteLeftovers(DeleteLeftoversRequest request)
    {
        var installed = AppInventory.List();
        var commonMenu = PathNormalizer.Normalize(Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms));
        var accepted = new List<Leftover>();
        var refused = new List<CleanFileResult>();

        foreach (var dto in request.Leftovers)
        {
            var kind = Enum.TryParse<LeftoverKind>(dto.Kind, out var k) ? k : LeftoverKind.RegistryKey;
            var ok = kind switch
            {
                LeftoverKind.Folder => LeftoverCleaner.IsAcceptableForHelper(dto.Path, _denyList, installed),
                LeftoverKind.Shortcut => dto.Path.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase)
                    && PathNormalizer.IsUnder(PathNormalizer.Normalize(dto.Path), commonMenu)
                    && !_denyList.IsProtected(dto.Path),
                _ => false,
            };
            if (ok)
                accepted.Add(new Leftover(dto.Path, kind, 0, "", NeedsElevation: true));
            else
                refused.Add(new CleanFileResult(dto.Path, 0, LeftoverCleaner.LogRuleId, DeleteOutcome.Protected, "The admin helper only removes leftovers under Program Files, ProgramData or the Start menu."));
        }

        using var log = FileDeletionLog.Create();
        var summary = LeftoverCleaner.Delete(accepted, _denyList, request.UseRecycleBin, log);
        return new CleanResponse([.. refused.Select(Dto.ToDto), .. summary.Files.Select(Dto.ToDto)], summary.DirectoriesRemoved, summary.LogPath);
    }

    private async Task<IpcMessage?> AnalyseAsync(AnalyseRequest request)
    {
        var root = Path.GetPathRoot(request.Drive);
        if (root is null || !Path.TrimEndingDirectorySeparator(request.Drive).Equals(Path.TrimEndingDirectorySeparator(root), StringComparison.OrdinalIgnoreCase))
            return new ErrorResponse("Only whole drives can be read from the MFT.", "invalid");

        var lastSent = DateTime.MinValue;
        var progress = new InlineProgress<double>(f =>
        {
            if ((DateTime.UtcNow - lastSent).TotalMilliseconds < 200)
                return;
            lastSent = DateTime.UtcNow;
            PipeProtocol.WriteAsync(pipe, new ProgressEvent("Reading the master file table", f, 0)).GetAwaiter().GetResult();
        });
        var entries = MftReader.Read(root, progress);

        const int batch = 50_000;
        for (var i = 0; i < entries.Count; i += batch)
        {
            var slice = new List<EntryRecord>(Math.Min(batch, entries.Count - i));
            for (var j = i; j < Math.Min(i + batch, entries.Count); j++)
            {
                var e = entries[j];
                slice.Add(new EntryRecord(e.Id, e.ParentId, e.Name, e.IsDirectory, e.Size, e.Allocated));
            }
            await PipeProtocol.WriteEntriesAsync(pipe, slice);
        }
        return new AnalyseDone(entries.Count);
    }

    private sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}
