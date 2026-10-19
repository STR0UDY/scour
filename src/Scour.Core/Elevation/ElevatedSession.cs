using System.ComponentModel;
using System.Diagnostics;
using System.IO.Pipes;
using System.Security.Principal;
using Scour.Core.Analysis;
using Scour.Core.Apps;
using Scour.Core.Cleaning;
using Scour.Core.Rules;
using Scour.Core.Scanning;
using Scour.Ipc;

namespace Scour.Core.Elevation;

/// <summary>
/// A connection to Scour.Elevator, the only part of Scour that runs as admin. Started on demand
/// with one UAC prompt, then reused until it goes idle (5 minutes) or the app closes.
/// </summary>
public sealed class ElevatedSession : IAsyncDisposable
{
    public const string HelperFileName = "Scour.Elevator.exe";

    private readonly NamedPipeClientStream _pipe;
    private readonly Process? _process;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private ElevatedSession(NamedPipeClientStream pipe, Process? process)
    {
        _pipe = pipe;
        _process = process;
    }

    public static string HelperPath => Path.Combine(AppContext.BaseDirectory, HelperFileName);

    public static bool HelperAvailable => File.Exists(HelperPath);

    public bool IsAlive => _pipe.IsConnected && (_process is null || !_process.HasExited);

    /// <summary>Starts the helper (UAC prompt) and connects. Null if the user declined.</summary>
    public static async Task<ElevatedSession?> StartAsync(CancellationToken cancellationToken = default)
    {
        if (!HelperAvailable)
            throw new FileNotFoundException("Scour.Elevator.exe is missing from the Scour folder.", HelperPath);

        var pipeName = $"scour-elevator-{Guid.NewGuid():N}";
        using var identity = WindowsIdentity.GetCurrent();
        var sid = identity.User?.Value ?? throw new InvalidOperationException("No user SID.");

        Process? process;
        try
        {
            process = Process.Start(new ProcessStartInfo(HelperPath)
            {
                UseShellExecute = true,
                Verb = "runas",
                WindowStyle = ProcessWindowStyle.Hidden,
                Arguments = $"--pipe {pipeName} --parent {Environment.ProcessId} --client-sid {sid}",
            });
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            return null; // The user said no at the UAC prompt.
        }

        var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        try
        {
            // The helper creates the pipe once it's running; give the user time at the prompt.
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(60));
            while (true)
            {
                try
                {
                    await pipe.ConnectAsync(500, timeout.Token);
                    break;
                }
                catch (TimeoutException)
                {
                    if (process is { HasExited: true })
                        throw new InvalidOperationException("The admin helper exited before it was ready.");
                }
            }

            var session = new ElevatedSession(pipe, process);
            var pong = await session.RequestAsync<PingResponse>(new PingRequest(), cancellationToken);
            if (pong.ProtocolVersion != PipeProtocol.Version)
                throw new InvalidOperationException("The admin helper is from a different Scour version.");
            return session;
        }
        catch
        {
            await pipe.DisposeAsync();
            throw;
        }
    }

    public async Task<IReadOnlyList<RuleScanResult>> ScanAsync(IReadOnlyList<Rule> rules, ScannerOptions options, CancellationToken cancellationToken = default)
    {
        var response = await RequestAsync<ScanResponse>(
            new ScanRequest(rules.Select(r => r.Id).ToList(), options.UserExclusions, options.RecycleBinMinAgeDays), cancellationToken);
        var byId = rules.ToDictionary(r => r.Id, StringComparer.OrdinalIgnoreCase);
        return response.Results.Where(r => byId.ContainsKey(r.RuleId)).Select(r => Dto.FromDto(r, byId[r.RuleId])).ToList();
    }

    public async Task<CleanSummary> CleanAsync(IReadOnlyList<RuleScanResult> results, ScannerOptions scannerOptions, CleanOptions options, CancellationToken cancellationToken = default)
    {
        var files = results.SelectMany(r => r.Files.Select(f => Dto.ToDto(f, r.Rule.Id))).ToList();
        var response = await RequestAsync<CleanResponse>(new CleanRequest(
            results.Select(r => r.Rule.Id).ToList(),
            scannerOptions.UserExclusions,
            scannerOptions.RecycleBinMinAgeDays,
            files,
            options.UseRecycleBin,
            options.DryRun), cancellationToken);
        return new CleanSummary(response.Outcomes.Select(Dto.FromDto).ToList(), response.DirectoriesRemoved, response.LogPath);
    }

    public async Task SetStartupEnabledAsync(string id, bool enabled, IReadOnlyList<string> manualServices, CancellationToken cancellationToken = default) =>
        await RequestAsync<StartupToggleResponse>(new StartupToggleRequest(id, enabled, manualServices), cancellationToken);

    public async Task<CleanSummary> DeleteLeftoversAsync(IEnumerable<Leftover> leftovers, bool useRecycleBin, CancellationToken cancellationToken = default)
    {
        var response = await RequestAsync<CleanResponse>(new DeleteLeftoversRequest(
            leftovers.Select(l => new LeftoverDto(l.Path, l.Kind.ToString())).ToList(), useRecycleBin), cancellationToken);
        return new CleanSummary(response.Outcomes.Select(Dto.FromDto).ToList(), response.DirectoriesRemoved, response.LogPath);
    }

    /// <summary>Reads a drive's MFT through the helper. Matches <see cref="ElevatedMftSource"/>.</summary>
    public async Task<IReadOnlyList<RawEntry>?> ReadMftAsync(string drive, IProgress<AnalyseProgress>? progress, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await PipeProtocol.WriteAsync(_pipe, new AnalyseRequest(drive), cancellationToken);
            var entries = new List<RawEntry>();
            while (true)
            {
                var frame = await PipeProtocol.ReadAsync(_pipe, cancellationToken)
                    ?? throw new IOException("The admin helper closed the connection.");
                if (frame.Entries is { } batch)
                {
                    foreach (var e in batch)
                        entries.Add(new RawEntry(e.Id, e.ParentId, e.Name, e.IsDirectory, e.Size, e.Allocated));
                    progress?.Report(new AnalyseProgress("Receiving the file table", null, entries.Count));
                    continue;
                }
                switch (frame.Message)
                {
                    case ProgressEvent p:
                        progress?.Report(new AnalyseProgress(p.Stage, p.Fraction, p.Items));
                        break;
                    case AnalyseDone:
                        return entries;
                    case ErrorResponse error:
                        throw Map(error);
                }
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<T> RequestAsync<T>(IpcMessage request, CancellationToken cancellationToken) where T : IpcMessage
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await PipeProtocol.WriteAsync(_pipe, request, cancellationToken);
            while (true)
            {
                var frame = await PipeProtocol.ReadAsync(_pipe, cancellationToken)
                    ?? throw new IOException("The admin helper closed the connection.");
                switch (frame.Message)
                {
                    case T response:
                        return response;
                    case ErrorResponse error:
                        throw Map(error);
                    case ProgressEvent:
                        continue;
                    default:
                        throw new InvalidDataException($"Unexpected reply from the admin helper: {frame.Message?.GetType().Name}");
                }
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private static Exception Map(ErrorResponse error) => error.Kind switch
    {
        "unauthorized" => new UnauthorizedAccessException(error.Message),
        "invalid" => new InvalidOperationException(error.Message),
        _ => new IOException(error.Message),
    };

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (_pipe.IsConnected)
            {
                using var quick = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                await PipeProtocol.WriteAsync(_pipe, new ShutdownRequest(), quick.Token);
            }
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException)
        {
        }
        await _pipe.DisposeAsync();
        _process?.Dispose();
        _gate.Dispose();
    }
}
