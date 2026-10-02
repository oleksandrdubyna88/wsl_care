using WslCare.Core.Collectors;
using WslCare.Core.Docker;
using WslCare.Core.Processes;

namespace WslCare.Core.Events;

/// <summary>How a follower run ended.</summary>
/// <param name="CoveredUntil">How far coverage reached; <c>null</c> when nothing was ever recorded.</param>
/// <param name="Problem">Why Docker could not be read when the run ended waiting for it (<c>--once</c>); empty otherwise.</param>
public sealed record FollowResult(DateTimeOffset? CoveredUntil, int StartsRecorded, int GapsRecorded, string Problem);

/// <summary>
/// <c>events follow</c> (plan §4.3, §15 #9, §15b #0 and #8): records every container start under
/// <c>{state}/container-starts/</c>, with markers that say how far the record is complete.
/// </summary>
/// <remarks>
/// <para><b>The states.</b> <i>Starting</i>: a <c>followerStarted</c> marker, the day files past their 14 days pruned,
/// the last coverage read back. <i>Catching up</i>: Docker probed (<c>docker version</c>, 10 s ceiling); while it does
/// not answer, the follower waits IN-PROCESS — 5 s, doubling to 5 minutes — and writes nothing, so a Docker that is
/// merely down never makes <c>Restart=always</c> cycle. When it answers, ONE backfill (<see cref="Coverage.Plan"/>)
/// recovers what Docker's buffer still proves and writes at most ONE <c>gap</c> marker for the whole outage.
/// <i>Streaming</i>: segments of <see cref="SegmentLength"/> (<see cref="DockerCommands.EventStream"/>), each start
/// written as it arrives, each normal segment end a <c>covered</c> marker; a segment that fails goes back to catching
/// up. <i>Stopping</i>: on the caller's cancellation (SIGTERM / SIGINT) a <c>followerStopped</c> marker carries the
/// coverage reached, and the run returns.</para>
/// <para>It ends only on cancellation or an unexpected error (a state directory that cannot be written): those are
/// the two reasons for systemd to see it exit (plan §15b #8). With <c>once</c> it stops after the first catch-up —
/// at once, without waiting, when Docker does not answer.</para>
/// <para>The wait is injected (<paramref name="wait"/>) so a test drives the backoff on a clock of its own.</para>
/// </remarks>
public sealed class EventsFollower(
    ICommandRunner commands,
    ContainerStartsStore store,
    TimeProvider clock,
    Func<TimeSpan, CancellationToken, Task> wait,
    Action<string> note)
{
    /// <summary>One stream segment: Docker closes the stream at its <c>--until</c>; the next one resumes from there.</summary>
    public static readonly TimeSpan SegmentLength = TimeSpan.FromMinutes(10);

    /// <summary>The ceiling of a segment beyond its length.</summary>
    public static readonly TimeSpan SegmentSlack = TimeSpan.FromMinutes(1);

    /// <summary>A segment that ends earlier than its <c>--until</c> by more than this did not end normally.</summary>
    private static readonly TimeSpan EarlyEnd = TimeSpan.FromSeconds(2);

    private readonly DockerCli _docker = new(commands);
    private int _starts;
    private int _gaps;

    /// <summary>How far coverage reaches right now — moved by every start recorded, every marker; what the stop
    /// marker carries even when the stop interrupts a segment.</summary>
    private DateTimeOffset? _covered;

    public async Task<FollowResult> RunAsync(bool once, int processId, CancellationToken cancellationToken)
    {
        store.Append(new CoverageLine.FollowerStarted(clock.GetUtcNow(), processId));
        foreach (var problem in store.Prune(clock.GetUtcNow()))
        {
            note($"retention: {problem}");
        }

        _covered = Coverage.LastCovered(store.ReadAll());
        var problemAtEnd = string.Empty;
        try
        {
            problemAtEnd = await CatchUpAsync(once, cancellationToken).ConfigureAwait(false);
            while (!once)
            {
                await FollowAsync(_covered ?? clock.GetUtcNow(), cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            // Written even when the token is cancelled: the stop is the event being recorded.
            store.Append(new CoverageLine.FollowerStopped(clock.GetUtcNow(), processId, _covered));
        }

        return new FollowResult(_covered, _starts, _gaps, problemAtEnd);
    }

    /// <summary>One segment and what follows it: a normal end is a <c>covered</c> marker, any other a catch-up.</summary>
    private async Task FollowAsync(DateTimeOffset since, CancellationToken cancellationToken)
    {
        var until = (since > clock.GetUtcNow() ? since : clock.GetUtcNow()) + SegmentLength;
        var outcome = await commands.StreamAsync(DockerCommands.EventStream(since, until, SegmentSlack).ToRequest(), Record, cancellationToken).ConfigureAwait(false);
        if (outcome is CommandOutcome.Exited { ExitCode: 0 } && clock.GetUtcNow() >= until - EarlyEnd)
        {
            store.Append(new CoverageLine.Covered(until));
            _covered = until;
            return;
        }

        note($"the event stream ended early ({Describe(outcome)}); catching up");
        await CatchUpAsync(once: false, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>One streamed line: a start newer than the coverage is written at once.</summary>
    private void Record(string line)
    {
        if (DockerEvents.Parse(line) is not Reading<IReadOnlyList<DockerEvent>>.Available { Value: var events })
        {
            note($"an event line could not be read and was skipped: {line}");
            return;
        }

        foreach (var start in events.Where(e => Coverage.IsStart(e) && (_covered is not { } covered || e.At > covered)))
        {
            store.Append(Line(start, backfilled: false));
            _starts++;
            _covered = start.At;
        }
    }

    /// <summary>Waits for Docker (backoff, nothing written meanwhile), then ONE backfill from the coverage reached. In
    /// <paramref name="once"/> mode an unreachable Docker ends the catch-up at once, coverage unchanged; the answer is
    /// why Docker could not be read then, or empty.</summary>
    private async Task<string> CatchUpAsync(bool once, CancellationToken cancellationToken)
    {
        TimeSpan? delay = null;
        while (true)
        {
            var (events, problem) = await BufferedAsync(cancellationToken).ConfigureAwait(false);
            if (events is not null)
            {
                _covered = Apply(Coverage.Plan(_covered, events, clock.GetUtcNow()));
                return string.Empty;
            }

            if (once)
            {
                note($"Docker cannot be read ({problem}); nothing recorded");
                return problem;
            }

            delay = Coverage.NextBackoff(delay);
            note($"Docker cannot be read ({problem}); waiting {delay.Value.TotalSeconds:0} s");
            await wait(delay.Value, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Every event Docker buffered in the last 24 h, or why not.</summary>
    private async Task<(IReadOnlyList<DockerEvent>? Events, string Problem)> BufferedAsync(CancellationToken cancellationToken)
    {
        if (DockerReachability.From(await _docker.RunAsync(DockerCommands.Version, cancellationToken).ConfigureAwait(false)) is DockerReachability.Unreachable { Problem: var down })
        {
            return (null, $"{down.Kind}: {down.Reason}");
        }

        var now = clock.GetUtcNow();
        return await _docker.RunAsync(DockerCommands.Backfill(now - Coverage.BackfillWindow, now), cancellationToken).ConfigureAwait(false) switch
        {
            DockerAnswer.Answered a when DockerEvents.Parse(a.Stdout) is Reading<IReadOnlyList<DockerEvent>>.Available { Value: var events } => (events, string.Empty),
            DockerAnswer.Answered a => (null, DockerEvents.Parse(a.Stdout).ReasonOrEmpty),
            DockerAnswer.Failed f => (null, $"{f.Problem.Kind}: {f.Problem.Reason}"),
            _ => throw new System.Diagnostics.UnreachableException("DockerAnswer is a closed set"),
        };
    }

    private DateTimeOffset Apply(BackfillPlan plan)
    {
        if (plan.Gap is { } gap)
        {
            store.Append(gap);
            _gaps++;
            note($"unrecoverable gap {gap.From:u} .. {gap.To:u}: {gap.Reason}");
        }

        foreach (var start in plan.Starts)
        {
            store.Append(Line(start, backfilled: true));
            _starts++;
        }

        store.Append(new CoverageLine.Covered(plan.CoveredUntil));
        return plan.CoveredUntil;
    }

    private static CoverageLine.Start Line(DockerEvent e, bool backfilled) => new(e.At, e.Id, e.Name, e.Image, e.Testcontainers, backfilled);

    private static string Describe(CommandOutcome outcome) => outcome switch
    {
        CommandOutcome.Exited e => $"docker events exited {e.ExitCode}{Health.ToolAnswers.Said(e.Stderr.Text)}",
        CommandOutcome.TimedOut t => $"docker events did not end within {t.Timeout.TotalSeconds:0} s; its tree was killed",
        CommandOutcome.FailedToStart f => $"docker could not be started: {f.Reason}",
        CommandOutcome.Refused r => $"refused: {r.Reason}",
        _ => throw new System.Diagnostics.UnreachableException("CommandOutcome is a closed set"),
    };
}
