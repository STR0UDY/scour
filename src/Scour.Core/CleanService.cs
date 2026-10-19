using Scour.Core.Cleaning;
using Scour.Core.Elevation;
using Scour.Core.Rules;
using Scour.Core.Scanning;

namespace Scour.Core;

/// <summary>
/// Scans and cleans through the right process: rules that need admin go to the elevated
/// helper (started on demand, one UAC prompt per session); everything else runs here.
/// </summary>
public sealed class CleanService : IAsyncDisposable
{
    private readonly ScourEngine _engine;
    private ElevatedSession? _helper;

    public CleanService(ScourEngine engine)
    {
        _engine = engine;
    }

    public ScourEngine Engine => _engine;

    public bool HelperRunning => _helper?.IsAlive == true;

    /// <summary>Admin rules can run: either we're elevated already, or the helper is up.</summary>
    public bool CanRunElevated => _engine.IsElevated || HelperRunning;

    public ScannerOptions ScannerOptions => new()
    {
        UserExclusions = _engine.Settings.Exclusions,
        RecycleBinMinAgeDays = _engine.Settings.RecycleBinMinAgeDays,
    };

    /// <summary>Takes over another service's running helper, so a reload doesn't cost a second UAC prompt.</summary>
    public void AdoptHelperFrom(CleanService other)
    {
        _helper = other._helper;
        other._helper = null;
    }

    /// <summary>The helper if it's already running; never prompts.</summary>
    public ElevatedSession? RunningHelper => _helper?.IsAlive == true ? _helper : null;

    /// <summary>The helper, starting it (UAC prompt) if needed. Null if the user declined.</summary>
    public async Task<ElevatedSession?> GetHelperAsync(CancellationToken cancellationToken = default)
    {
        if (_helper?.IsAlive == true)
            return _helper;
        if (_helper is not null)
            await _helper.DisposeAsync();
        _helper = await ElevatedSession.StartAsync(cancellationToken);
        return _helper;
    }

    /// <summary>Ensures admin rules can run. False if the user declined the prompt.</summary>
    public async Task<bool> EnsureElevatedAsync(CancellationToken cancellationToken = default) =>
        _engine.IsElevated || await GetHelperAsync(cancellationToken) is not null;

    public async Task<IReadOnlyList<RuleScanResult>> ScanAsync(IReadOnlyList<Rule> rules, IProgress<RuleScanResult>? progress = null, CancellationToken cancellationToken = default)
    {
        var elevated = rules.Where(NeedsHelper).ToList();
        var local = rules.Except(elevated).ToList();

        var localTask = _engine.Scanner.ScanAsync(local, progress, cancellationToken);
        IReadOnlyList<RuleScanResult> remote = [];
        if (elevated.Count > 0)
        {
            // Only rules that are installed here go to the helper; the rest are reported as hidden.
            var detected = elevated.Where(_engine.Scanner.IsDetected).ToList();
            if (HelperRunning && detected.Count > 0)
            {
                try
                {
                    remote = await _helper!.ScanAsync(detected, ScannerOptions, cancellationToken);
                }
                catch (IOException)
                {
                    remote = []; // Helper went away; fall back to "needs admin".
                }
            }
            remote = elevated.Select(rule =>
                remote.FirstOrDefault(r => r.Rule.Id == rule.Id)
                ?? _engine.Scanner.ScanRule(rule, cancellationToken)).ToList();
            foreach (var r in remote)
                progress?.Report(r);
        }

        var localResults = await localTask;
        var byId = localResults.Concat(remote).ToDictionary(r => r.Rule.Id, StringComparer.OrdinalIgnoreCase);
        return rules.Select(r => byId[r.Id]).ToList();
    }

    public async Task<CleanSummary> CleanAsync(IReadOnlyList<RuleScanResult> results, CleanOptions options, IProgress<CleanFileResult>? progress = null, CancellationToken cancellationToken = default)
    {
        var ready = results.Where(r => r.Status == RuleScanStatus.Ok && r.FileCount > 0).ToList();
        var elevated = ready.Where(r => NeedsHelper(r.Rule)).ToList();
        var local = ready.Except(elevated).ToList();

        var outcomes = new List<CleanFileResult>();
        var dirs = 0;
        string? logPath = null;

        if (local.Count > 0)
        {
            using var log = options.DryRun ? null : FileDeletionLog.Create();
            var summary = await Task.Run(() => _engine.Cleaner.Clean(local, options, log, progress, cancellationToken), cancellationToken);
            outcomes.AddRange(summary.Files);
            dirs += summary.DirectoriesRemoved;
            logPath = summary.LogPath;
        }

        if (elevated.Count > 0)
        {
            var helper = await GetHelperAsync(cancellationToken);
            if (helper is null)
            {
                outcomes.AddRange(elevated.SelectMany(r => r.Files.Select(f =>
                    new CleanFileResult(f.Path, f.Size, r.Rule.Id, DeleteOutcome.AccessDenied, "Needs admin, and the prompt was declined."))));
            }
            else
            {
                var summary = await helper.CleanAsync(elevated, ScannerOptions, options, cancellationToken);
                outcomes.AddRange(summary.Files);
                dirs += summary.DirectoriesRemoved;
                logPath ??= summary.LogPath;
                foreach (var o in summary.Files)
                    progress?.Report(o);
            }
        }

        return new CleanSummary(outcomes, dirs, logPath);
    }

    private bool NeedsHelper(Rule rule) => rule.Elevation && !_engine.IsElevated;

    public async ValueTask DisposeAsync()
    {
        if (_helper is not null)
            await _helper.DisposeAsync();
    }
}
