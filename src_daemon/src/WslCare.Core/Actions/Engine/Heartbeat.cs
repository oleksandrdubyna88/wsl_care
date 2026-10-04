using WslCare.Core.Files;
using WslCare.Core.Files.Deletion;
using WslCare.Core.Hosting;

namespace WslCare.Core.Actions.Engine;

/// <summary>
/// Keeps <c>running.json</c> beating while a run acts (plan §6: every 5 s): a detached loop that rewrites the heartbeat,
/// and an immediate rewrite whenever the run moves to its next action — so the extension's <i>Cleaning… A10</i> is
/// the truth, and a run that dies stops beating without having to notice.
/// </summary>
/// <remarks>The loop is a detached execution, so it ends in a catch-all that keeps the failure (reliability rule); a
/// heartbeat that could not be written is reported in the run's notes — a reader then sees a stale heartbeat and refuses
/// a second run (wedged), which is the conservative direction.</remarks>
internal sealed class Heartbeat : IAsyncDisposable
{
    private readonly IHostPaths _paths;
    private readonly IFileSystem _files;
    private readonly TimeProvider _clock;
    private readonly IProcessTable _processes;
    private readonly object _gate = new();
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _loop;
    private RunningFile _file;

    public Heartbeat(IHostPaths paths, IFileSystem files, TimeProvider clock, IProcessTable processes, RunningFile file, TimeSpan period)
    {
        _paths = paths;
        _files = files;
        _clock = clock;
        _processes = processes;
        _file = file;
        _loop = Task.Run(() => LoopAsync(period));
    }

    /// <summary>Why a write failed, the last time one did; empty when every write succeeded.</summary>
    public string Failure { get; private set; } = string.Empty;

    /// <summary>The run moved to <paramref name="actionId"/>: written now, not at the next tick.</summary>
    public void Current(string actionId)
    {
        lock (_gate)
        {
            _file = RunningState.WithHeartbeat(_file with { Current = actionId }, _clock.GetUtcNow(), _processes);
            Write();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync().ConfigureAwait(false);
        await _loop.ConfigureAwait(false);
        _stop.Dispose();
    }

    private async Task LoopAsync(TimeSpan period)
    {
        try
        {
            using var timer = new PeriodicTimer(period, _clock);
            while (await timer.WaitForNextTickAsync(_stop.Token).ConfigureAwait(false))
            {
                Beat();
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested)
        {
            // The run ended: the loop's normal way out.
        }
        catch (Exception e)
        {
            // The outermost frame of a detached loop: kept, never thrown into nobody.
            Failure = $"the heartbeat stopped: {e.GetType().Name}: {e.Message}";
        }
    }

    private void Beat()
    {
        lock (_gate)
        {
            _file = RunningState.WithHeartbeat(_file, _clock.GetUtcNow(), _processes);
            Write();
        }
    }

    private void Write()
    {
        try
        {
            if (RunningState.Write(_paths, _files, _file) is DeletionVerdict.Refused refused)
            {
                Failure = $"running.json could not be written: {refused.Reason}";
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Failure = $"running.json could not be written: {e.Message}";
        }
    }
}
