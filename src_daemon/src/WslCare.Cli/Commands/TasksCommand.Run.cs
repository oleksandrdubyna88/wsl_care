using System.Globalization;

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
/// the answer; the supervisor adds one stderr line when it ended the worker, and nothing to stdout, ever.
/// </summary>
/// <remarks>Exit codes: the worker's own code when it exited by itself; 84 <c>TaskEnded</c> when the deadline ended it; 2 on the distro's
/// binary, for <c>archive</c> (its body is WR-D5's) and when this process's own path cannot be read; 1 when the supervisor could not start
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

        SweepStagingAtRunStart(host, windows, loaded.Config);
        return TaskWorkers.For(request.Task, host.ProcessPath(), request.Json, loaded.Config) switch
        {
            Reading<WorkerLaunch>.Available launch => Ended(request.Task, host.Supervisor.Run(launch.Value, cancellationToken), stderr),
            Reading<WorkerLaunch>.Unavailable refused => Refused(request.Task, refused.Reason, stderr),
            _ => throw new System.Diagnostics.UnreachableException("Reading is a closed set"),
        };
    }

    /// <summary>
    /// THE SEAM FOR WR-D2 (PLAN_windows_runner.md §3.10: "the sweep also runs at every <c>tasks run</c> start, so a kept folder goes as
    /// soon as its run ends"). WR-D2's staging sweep — the one <c>tasks stage</c> runs, never a second one — is called from here, before
    /// the worker starts; until WR-D2 lands it does nothing. A sweep that fails must not stop the run: the run is the sampling, the sweep
    /// only retires old copies.
    /// </summary>
    private static void SweepStagingAtRunStart(CliHost host, WindowsHostPaths paths, EffectiveConfig config)
    {
        // WR-D2: Staging's sweep of StagedCopies.Root(paths) through host.Files, with config's numbers, goes here.
    }

    private static int Refused(WindowsTask task, string reason, TextWriter stderr)
    {
        Output.Note(stderr, $"tasks run {task}: {reason}");
        return (int)ExitCode.Usage;
    }

    /// <summary>The run's exit, from how the supervised worker ended.</summary>
    private static int Ended(WindowsTask task, WorkerOutcome outcome, TextWriter stderr)
    {
        switch (outcome)
        {
            case WorkerOutcome.Exited exited:
                return exited.Code;
            case WorkerOutcome.DeadlinePassed passed:
                Output.Note(stderr, string.Create(CultureInfo.InvariantCulture, $"tasks run {task}: the worker was still running at its deadline of {passed.Deadline.TotalMinutes:0.##} min ({TaskWorkers.DeadlineKey(task).Name}, counted on the unbiased clock); its job was terminated, so its whole tree is gone"));
                return (int)ExitCode.TaskEnded;
            case WorkerOutcome.Refused refused:
                Output.Note(stderr, $"tasks run {task}: {refused.Reason}");
                return (int)ExitCode.RunFailed;
            default:
                return Output.Interrupted(stderr);
        }
    }
}
