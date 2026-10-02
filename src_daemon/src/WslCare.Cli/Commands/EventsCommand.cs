using System.Globalization;

using Serilog;

using WslCare.Core.Events;
using WslCare.Core.Files;

namespace WslCare.Cli.Commands;

/// <summary>
/// <c>events follow [--once]</c> (plan §4.3, §15b #0 and #8): the target of <c>wsl-care-events.service</c>. It runs
/// until a signal (SIGTERM from systemd, SIGINT from a terminal) and then exits 0 having written its stop marker; it
/// exits otherwise only on an unexpected error (70), so <c>Restart=always</c> restarts it then and not while Docker is
/// merely down. <c>--once</c> catches up — markers, one bounded backfill — and stops.
/// </summary>
/// <remarks>Exit codes: 0 stopped by a signal, or <c>--once</c> done; 1 the state directory is not writable by this
/// process (the follower records, so it runs as root); 75 another follower holds its lock.</remarks>
internal static class EventsCommand
{
    public static int Run(Request.EventsFollow request, CliHost host, TextWriter stdout, TextWriter stderr, ILogger logger, CancellationToken cancellationToken)
    {
        var log = logger.ForContext(typeof(EventsCommand));
        if (host.Files.ProbeWriteAccess(host.Paths.StateDirectory) is WriteAccess.NotWritable denied)
        {
            Output.Note(stderr, $"events follow records container starts under {host.Paths.StateDirectory}, which this process may not write; run it as root (the wsl-care-events unit does): {denied.Reason}");
            return (int)ExitCode.RunFailed;
        }

        var store = new ContainerStartsStore(host.Paths, host.Files);
        if (host.Files.TryLockExclusive(store.FollowerLock) is not ExclusiveLock.Held held)
        {
            Output.Note(stderr, $"another events follower is running ({store.FollowerLock} is held)");
            return (int)ExitCode.Busy;
        }

        using (held.Handle)
        {
            var follower = new EventsFollower(host.Commands, store, host.Clock, (delay, token) => Task.Delay(delay, host.Clock, token), note => log.Information("{Note}", note));
            try
            {
                var result = follower.RunAsync(request.Once, Environment.ProcessId, cancellationToken).GetAwaiter().GetResult();
                Output.Answer(stdout, Summary(result));
                return (int)ExitCode.Ok;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // The follower's normal end: the stop marker is written, the run returns clean (plan §15b #8).
                log.Information("stopped by a signal; the stop marker is written");
                return (int)ExitCode.Ok;
            }
        }
    }

    private static string Summary(FollowResult result) =>
        string.Create(CultureInfo.InvariantCulture, $"events follow: {result.StartsRecorded} start(s), {result.GapsRecorded} gap marker(s); covered until {(result.CoveredUntil is { } at ? at.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss'Z'", CultureInfo.InvariantCulture) : "never")}{(result.Problem.Length > 0 ? $"; Docker could not be read: {result.Problem}" : string.Empty)}");
}
