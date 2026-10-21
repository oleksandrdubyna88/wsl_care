using System.Globalization;

using WslCare.Core.Actions.Engine;
using WslCare.Core.Collectors;
using WslCare.Core.Config;
using WslCare.Core.Hosting;
using WslCare.Core.Processes;
using WslCare.Core.Scheduler;

namespace WslCare.Cli.Commands;

/// <summary>
/// <c>tasks run &lt;watch|archive&gt; [--json]</c> (WR-D3 of PLAN_windows_runner.md §3.2, §3.3): what each per-user task's action runs —
/// the supervisor. It names the worker from the CLOSED table (<see cref="TaskWorkers"/>: this binary's own path, the arguments spelt
/// there), and hands it to <see cref="CliHost.Supervisor"/>: created suspended inside a kill-on-close job, resumed only inside it,
/// ended with its whole tree at its deadline. The worker's stdout and stderr ARE this process's (inherited), so the worker's answer is
/// the answer; the supervisor adds ONE stderr line when supervision starts (the task, the worker's argv, the deadline and its key) and
/// ONE terminal line (exited N, ended at the deadline, stopped, refused) — and nothing to stdout, ever.
/// </summary>
/// <remarks>Exit codes: the worker's own code when it exited by itself; 84 <c>TaskEnded</c> when the deadline ended it; 2 on the distro's
/// binary and when this process's own path cannot be read; 1 when the supervisor could not start
/// the worker inside a job (the reason is the stderr line — fail closed, nothing ran outside the job); 130 when a signal stopped the run
/// (the job was closed, which ended the tree). A configuration in error is not refused here: the worker reads the same layers and
/// answers for itself (the watch's observe-only rule), and the deadline's key keeps its default when its layer is in error.</remarks>
internal static partial class TasksCommand
{
    public static int Run(Request.TasksRun request, CliHost host, ConfigLoadResult loaded, TextWriter stderr, CancellationToken cancellationToken)
    {
        if (host.Paths is not WindowsHostPaths windows)
        {
            Output.Note(stderr, $"\"{CommandLine.BinaryName} tasks\" is the Windows binary's: the per-user scheduled tasks run the staged wsl-care.exe on the Windows host; nothing was done");
            return (int)ExitCode.Usage;
        }

        SweepStagingAtRunStart(host, windows, loaded.Config, stderr);
        var archive = new Lazy<DryRunDecision>(() => ArchiveBody.Decide(loaded, host.Privilege.IsRoot, windows, host.Files, host.Clock.GetUtcNow()));
        return TaskWorkers.For(request.Task, host.ProcessPath(), request.Json, loaded.Config, () => archive.Value) switch
        {
            Reading<WorkerLaunch>.Available launch => Started(request.Task, launch.Value, host.Supervisor, archive, () => DryRunWindow.Peek(windows, host.Files, loaded.Config, host.Clock.GetUtcNow(), request.Task.DryWindowStamp), stderr, cancellationToken),
            Reading<WorkerLaunch>.Unavailable refused => Refused(request.Task, refused.Reason, stderr),
            _ => throw new System.Diagnostics.UnreachableException("Reading is a closed set"),
        };
    }

    /// <summary>
    /// The start-of-run sweep (PLAN_windows_runner.md §3.10 and WR-D2's keep rule in §14): WR-D2's ONE sweep, <see cref="Staging.Sweep"/>,
    /// told the digest this run's own copy is staged under (empty when it runs from none) — it keeps that copy and the newest verified
    /// one and retires every other, under the run lock taken without waiting (held: skipped, the next run retries). It runs BEFORE the
    /// worker starts, so the worker's own take of the run lock never meets it. A sweep that fails does not stop the run — the run is
    /// the sampling, the sweep only retires old copies — and a retirement or a failure is said in one stderr line.
    /// </summary>
    private static void SweepStagingAtRunStart(CliHost host, WindowsHostPaths paths, EffectiveConfig config, TextWriter stderr)
    {
        var place = StagingPlace.For(paths);
        var own = StagedCopies.DigestOf(place.Root, host.ProcessPath());
        var access = new StagingAccess(place, host.Files, host.ArchiveFiles(), config.Int(ConfigKeys.Records.MaxStateFileBytes), host.Clock.GetUtcNow());
        try
        {
            SaySwept(Staging.Sweep(access, own), stderr);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Output.Note(stderr, $"tasks run: the staging sweep failed ({e.Message}); the run goes on");
        }
    }

    /// <summary>One line per thing worth saying, none when the sweep did nothing (the coai code round on WR-D2: finding 3 asked for the
    /// skipped and the kept lines; a line on every run was rejected as noise on an unattended five-minute task).</summary>
    private static void SaySwept(StagingSweep sweep, TextWriter stderr)
    {
        if (sweep.Busy.Length > 0)
        {
            Output.Note(stderr, $"tasks run: the staging sweep was skipped: {sweep.Busy}; the next run retries");
        }

        if (sweep.Swept.Count > 0)
        {
            Output.Note(stderr, string.Create(CultureInfo.InvariantCulture, $"tasks run: retired {sweep.Swept.Count} older staged copy folder(s): {string.Join(", ", sweep.Swept.Select(f => f.Digest))}"));
        }

        if (sweep.Kept.Count > 0)
        {
            Output.Note(stderr, string.Create(CultureInfo.InvariantCulture, $"tasks run: the staging sweep could not remove {sweep.Kept.Count} folder(s): {string.Join("; ", sweep.Kept.Select(f => $"{f.Path} ({f.Why})"))}"));
        }
    }

    /// <summary>The start of a launch that exists: the archive body's window said first (WR-D5, §3.5: why it is a preview, or why it is
    /// a real run — the decision the table already took for this launch), then the supervised run. Explicit per task, never inferred from
    /// the decision's own state (the coai code round on WR-D5, finding 0); the watch takes no decision and says none.</summary>
    private static int Started(WindowsTask task, WorkerLaunch launch, IWorkerSupervisor supervisor, Lazy<DryRunDecision> archive, Func<DryRunPeek> cleanupWindow, TextWriter stderr, CancellationToken cancellationToken)
    {
        if (task == WindowsTask.Archive)
        {
            Output.Note(stderr, $"tasks run {WindowsTask.Archive}: {ArchiveBody.Note(archive.Value)}");
        }

        if (task == WindowsTask.Cleanup)
        {
            // A PEEK, never Decide: the worker's engine decides on A23's stamp; a supervisor that wrote it would start the week for a run the worker may refuse.
            Output.Note(stderr, $"tasks run {WindowsTask.Cleanup}: A23's dry-run window: {cleanupWindow().Reason}");
        }

        return Supervised(task, launch, supervisor, stderr, cancellationToken);
    }

    private static int Refused(WindowsTask task, string reason, TextWriter stderr)
    {
        Output.Note(stderr, $"tasks run {task}: {reason}");
        return (int)ExitCode.Usage;
    }

    /// <summary>The supervised run: its start line, the run, its terminal line (the coai code round on WR-D3, finding 3).</summary>
    private static int Supervised(WindowsTask task, WorkerLaunch launch, IWorkerSupervisor supervisor, TextWriter stderr, CancellationToken cancellationToken)
    {
        Output.Note(stderr, string.Create(CultureInfo.InvariantCulture, $"tasks run {task}: starting {WindowsCommandLine.Of(launch.Executable, launch.Arguments)} in a kill-on-close job, deadline {launch.Deadline.TotalMinutes:0.##} min ({launch.DeadlineKey})"));
        return Ended(task, launch, supervisor.Run(launch, cancellationToken), stderr);
    }

    /// <summary>The run's exit and its ONE terminal line, from how the supervised worker ended. The deadline's key is the launch's own,
    /// read from the same table entry as its duration.</summary>
    private static int Ended(WindowsTask task, WorkerLaunch launch, WorkerOutcome outcome, TextWriter stderr)
    {
        switch (outcome)
        {
            case WorkerOutcome.Exited exited:
                Output.Note(stderr, string.Create(CultureInfo.InvariantCulture, $"tasks run {task}: the worker exited {exited.Code}"));
                return exited.Code;
            case WorkerOutcome.DeadlinePassed passed:
                Output.Note(stderr, string.Create(CultureInfo.InvariantCulture, $"tasks run {task}: the worker was still running at its deadline of {passed.Deadline.TotalMinutes:0.##} min ({launch.DeadlineKey}, counted on the unbiased clock); its job was terminated, so its whole tree is gone"));
                return (int)ExitCode.TaskEnded;
            case WorkerOutcome.Refused refused:
                Output.Note(stderr, $"tasks run {task}: refused: {refused.Reason}");
                return (int)ExitCode.RunFailed;
            default:
                Output.Note(stderr, $"tasks run {task}: stopped by a signal; its job was closed, so its whole tree is gone");
                return Output.Interrupted(stderr);
        }
    }
}
