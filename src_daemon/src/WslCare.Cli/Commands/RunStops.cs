using Serilog;

using WslCare.Core.Actions.Engine;
using WslCare.Core.Collectors.Procfs;
using WslCare.Core.Files;
using WslCare.Core.Hosting;
using WslCare.Core.Processes;
using WslCare.Core.Processes.Policy;
using WslCare.Core.Records;
using WslCare.Core.Systemd;

namespace WslCare.Cli.Commands;

/// <summary>
/// <c>act --stop &lt;runId&gt; [--json]</c> (E6.S1, plan §15j M4, §15k #18): asks systemd to stop a WEDGED run — only when its
/// process lives in the SYSTEM unit <c>wsl-care.service</c> or that run's own <c>wsl-care-act@&lt;runId&gt;.service</c>, matched by
/// the whole cgroup path (review S3: a user's own <c>systemd --user</c> unit named <c>wsl-care.service</c> ends in the same
/// name). Never a kill by pid.
/// </summary>
internal static class RunStops
{
    /// <summary>The cgroup of the timer's service.</summary>
    public const string TimerCgroup = "/system.slice/" + UnitCommands.TimerService;

    /// <summary>The slice systemd puts every instance of the template in (<c>-</c> escaped as <c>\x2d</c>).</summary>
    public const string ActSlice = "/system.slice/system-wsl\\x2dcare\\x2dact.slice/";

    public static int Stop(Request.ActStop request, CliHost host, TextWriter stdout, TextWriter stderr, ILogger log, CancellationToken cancellationToken)
    {
        if (ActCommand.NotRoot(host) is { } refused)
        {
            return DetachedRuns.Refuse(stderr, log, refused.Code, refused.Message);
        }

        var runId = request.RunId;
        return RunningState.Read(host.Paths, host.Files, host.Processes, host.Clock.GetUtcNow()) switch
        {
            RunningStatus.Wedged wedged when wedged.File.RunId == runId => StopWedged(wedged.File, request.Json, host, stdout, stderr, log, cancellationToken),
            RunningStatus.Live live when live.File.RunId == runId => DetachedRuns.Refuse(stderr, log, ExitCode.Busy, $"run {runId} is acting, not wedged: its heartbeat is fresh - let it finish"),
            var other => DetachedRuns.Refuse(stderr, log, ExitCode.Usage, $"run {runId} is not wedged here ({Describe(other)}): nothing to stop"),
        };
    }

    /// <summary>The unit to stop for a process in <paramref name="cgroup"/>: exactly one of the two system units' paths; empty
    /// for anything else (a user's unit of the same name, a session scope, an unread cgroup).</summary>
    public static string UnitOf(string cgroup, RunId runId) =>
        cgroup == TimerCgroup ? UnitCommands.TimerService
        : cgroup == ActSlice + SlotKind.ActUnit.Of(runId) ? SlotKind.ActUnit.Of(runId)
        : string.Empty;

    /// <summary>Only through systemd (SIGTERM, then SIGKILL after <c>TimeoutStopSec=90</c>); anything else is text with its pid.</summary>
    private static int StopWedged(RunningFile file, bool json, CliHost host, TextWriter stdout, TextWriter stderr, ILogger log, CancellationToken cancellationToken)
    {
        var cgroup = Cgroup(host, file.Pid);
        var unit = UnitOf(cgroup, file.RunId);
        if (unit.Length == 0)
        {
            return DetachedRuns.Refuse(stderr, log, ExitCode.Usage, $"run {file.RunId} is wedged, but its process (pid {file.Pid}) is not in the system's wsl-care.service nor in {SlotKind.ActUnit.Of(file.RunId)} ({(cgroup.Length > 0 ? cgroup : "its cgroup could not be read")}): nothing was stopped - stop pid {file.Pid} by hand if that is safe");
        }

        StopMarkers.Mark(host.Paths, host.Files, file.RunId, unit, host.Clock.GetUtcNow());
        var stopped = host.Commands.RunAsync(UnitCommands.Stop(unit).ToRequest(), cancellationToken).GetAwaiter().GetResult();
        if (stopped is not CommandOutcome.Exited { ExitCode: 0 })
        {
            StopMarkers.Remove(host.Paths, host.Files, file.RunId);
            return DetachedRuns.Refuse(stderr, log, ExitCode.RunFailed, $"systemctl stop {unit} did not succeed ({DetachedRuns.Describe(stopped)})");
        }

        log.Information("asked systemd to stop {Unit} for the wedged run {RunId}", unit, file.RunId.Text);
        return DetachedRuns.Answer(stdout, new HandOffReport(Core.SchemaVersion.Current, "stopping", "stop", file.RunId.Text, unit), json, $"wsl-care: asked systemd to stop {unit}; run {file.RunId} records itself interrupted, or the next root run records why it could not");
    }

    /// <summary>The cgroup v2 path of <paramref name="pid"/> (<c>/proc/[pid]/cgroup</c>, the <c>0::</c> line); empty when unread.</summary>
    private static string Cgroup(CliHost host, int pid) =>
        host.Paths is LinuxHostPaths linux
        && host.Files.ReadFile(linux.Rules.Join(linux.ProcRoot, pid.ToString(System.Globalization.CultureInfo.InvariantCulture), "cgroup")) is FileReadResult.Content content
            ? ProcCgroup.Parse(System.Text.Encoding.UTF8.GetString(content.Bytes))
            : string.Empty;

    private static string Describe(RunningStatus status) => status switch
    {
        RunningStatus.None => "no run holds running.json",
        RunningStatus.Live live => $"run {live.File.RunId} is acting",
        RunningStatus.Wedged wedged => $"run {wedged.File.RunId} is the wedged one",
        RunningStatus.Dead dead => $"run {dead.File.RunId} is dead: {dead.Why}",
        RunningStatus.Unknown unknown => unknown.Reason,
        RunningStatus.Unreadable unreadable => unreadable.Reason,
        _ => throw new System.Diagnostics.UnreachableException("RunningStatus is a closed set"),
    };
}
