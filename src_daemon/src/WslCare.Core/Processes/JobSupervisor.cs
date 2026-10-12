using WslCare.Core.Collectors;
using WslCare.Core.Config;

namespace WslCare.Core.Processes;

/// <summary>
/// WR-D3 (PLAN_windows_runner.md §3.3): the ONE bounded supervisor of the Windows tasks' workers — a kill-on-close Job Object with a
/// deadline, so no child or grandchild outlives a run. The rules are the ORDER of the operating-system calls, so they live here and
/// the calls themselves sit behind <see cref="IJobControl"/> (the real one is <see cref="Win32JobControl"/>, the only file that may
/// end a process this way — <c>ArchitectureTests</c>' job scan).
/// </summary>
/// <remarks>
/// <list type="number">
/// <item>The job first, kill-on-close, its handle not inheritable; then the worker, SUSPENDED, without a window, inheriting the three
/// standard handles only — and created ALREADY INSIDE the job (<c>PROC_THREAD_ATTRIBUTE_JOB_LIST</c>), so there is no moment, not even
/// between its creation and an assign, in which a supervisor that dies would leave it outside the kill-on-close job.</item>
/// <item>Its membership is verified; a worker not in the job is ended (through the job, then by its own handle, still suspended) and
/// the run is refused — fail closed, never resumed outside the job. Only then is its thread resumed.</item>
/// <item>The wait is on the worker's process with the deadline; each time the wait times out the elapsed time is read again on the
/// UNBIASED clock (<see cref="IUnbiasedClock.UnbiasedMilliseconds"/>, which stops while the host sleeps), so a sleep is not run time.
/// Past the deadline the whole job is ended with <see cref="EndedExitCode"/> — the caller exits 84 <c>TaskEnded</c>. A clock that
/// stops answering mid-run counts as past the deadline (fail closed: the run stays bounded).</item>
/// <item>The job's handle is closed ALWAYS (the <c>using</c>): kill-on-close is the net under the net — a cancelled run (Ctrl+C, a
/// stop) and a supervisor that dies take the whole tree with them, and so does a worker that exits leaving a grandchild.</item>
/// </list>
/// </remarks>
public sealed class JobSupervisor(IJobControl jobs, IUnbiasedClock clock) : IWorkerSupervisor
{
    /// <summary>The exit code every process of an ended job gets — <c>0xC5A1</c>, the plan's mark of a supervisor's deadline.</summary>
    public const uint EndedExitCode = 0xC5A1;

    /// <summary>How long the worker is waited for after its job was ended, before the run answers: the same grace the child runner
    /// gives a killed tree (<c>commands.drainGraceMilliseconds</c>).</summary>
    private static TimeSpan ReapGrace => Tuning.Current.Milliseconds(ConfigKeys.Commands.DrainGraceMilliseconds);

    public WorkerOutcome Run(WorkerLaunch launch, CancellationToken cancellationToken)
    {
        if (clock.UnbiasedMilliseconds() is not Reading<long>.Available start)
        {
            return new WorkerOutcome.Refused("the unbiased clock (QueryUnbiasedInterruptTime) could not be read, so no deadline could be kept; nothing was started");
        }

        return jobs.CreateJob() switch
        {
            Reading<IJobHandle>.Available created => InJob(created.Value, new Supervised(launch, start.Value, cancellationToken)),
            Reading<IJobHandle>.Unavailable missing => new WorkerOutcome.Refused($"no job could be made for the worker ({missing.Reason}); nothing was started"),
            _ => throw new System.Diagnostics.UnreachableException("Reading is a closed set"),
        };
    }

    /// <summary>One run's launch, the unbiased instant it began and its cancellation.</summary>
    private sealed record Supervised(WorkerLaunch Launch, long StartMilliseconds, CancellationToken Cancellation);

    private WorkerOutcome InJob(IJobHandle job, Supervised run)
    {
        // CloseHandle(job) always — whatever this returns, every process still in the job ends with it.
        using (job)
        {
            return jobs.StartSuspended(job, run.Launch.Executable, WindowsCommandLine.Of(run.Launch.Executable, run.Launch.Arguments)) switch
            {
                Reading<IWorkerProcess>.Available started => Started(job, started.Value, run),
                Reading<IWorkerProcess>.Unavailable missing => new WorkerOutcome.Refused($"the worker could not be created ({missing.Reason}); nothing was started"),
                _ => throw new System.Diagnostics.UnreachableException("Reading is a closed set"),
            };
        }
    }

    private WorkerOutcome Started(IJobHandle job, IWorkerProcess worker, Supervised run)
    {
        using (worker)
        {
            return jobs.InJob(job, worker) is { Length: > 0 } notInJob ? NotInJob(job, worker, notInJob)
                : jobs.Resume(worker) is { Length: > 0 } notResumed ? NotResumed(job, worker, notResumed)
                : Supervise(job, worker, run);
        }
    }

    /// <summary>The worker is not (verifiably) in the job: it is ended through the job and then by its own handle while still
    /// suspended — and when that fails, the refusal says so and never claims it ended (m2 of the WR-D3 review). Either way it is never
    /// resumed.</summary>
    private WorkerOutcome NotInJob(IJobHandle job, IWorkerProcess worker, string why)
    {
        jobs.EndJob(job, EndedExitCode);
        var notEnded = jobs.EndSuspended(worker, EndedExitCode);
        var after = notEnded.Length == 0
            ? "it was ended while still suspended and never ran outside the job"
            : $"ending it while suspended failed too ({notEnded}): it stays suspended, never resumed, and must be ended by hand";
        return new WorkerOutcome.Refused($"the worker (pid {worker.Pid}) is not in its job ({why}); {after}");
    }

    private WorkerOutcome NotResumed(IJobHandle job, IWorkerProcess worker, string why)
    {
        jobs.EndJob(job, EndedExitCode);
        return new WorkerOutcome.Refused($"the worker (pid {worker.Pid}) could not be resumed ({why}); its job was ended");
    }

    /// <summary>Wait for the worker until its deadline — a loop (the second coai code round on WR-D3, finding 4): a wait that timed out is
    /// only a reason to read the unbiased clock again, and the next wait is for what is left of the deadline.</summary>
    private WorkerOutcome Supervise(IJobHandle job, IWorkerProcess worker, Supervised run)
    {
        for (var left = Remaining(run); left > TimeSpan.Zero; left = Remaining(run))
        {
            var wait = jobs.Wait(worker, left, run.Cancellation);
            if (wait is not WorkerWait.TimedOut)
            {
                return Answered(wait, worker);
            }
        }

        return End(job, worker, run.Launch.Deadline);
    }

    /// <summary>What a wait that did not time out answered: the worker's end, a stop, or an exit nobody can read.</summary>
    private static WorkerOutcome Answered(WorkerWait wait, IWorkerProcess worker) => wait switch
    {
        WorkerWait.Exited exited => new WorkerOutcome.Exited(exited.Code),
        WorkerWait.Cancelled => new WorkerOutcome.Cancelled(),
        WorkerWait.Unreadable unreadable => new WorkerOutcome.Refused($"the worker (pid {worker.Pid}) exited, but its exit code could not be read ({unreadable.Reason})"),
        _ => throw new System.Diagnostics.UnreachableException("a timed-out wait is not an answer; the loop reads the deadline again"),
    };

    /// <summary>What is left of the deadline on the UNBIASED clock; nothing when the clock no longer answers (fail closed).</summary>
    private TimeSpan Remaining(Supervised run) => clock.UnbiasedMilliseconds() switch
    {
        Reading<long>.Available now => run.Launch.Deadline - TimeSpan.FromMilliseconds(now.Value - run.StartMilliseconds),
        _ => TimeSpan.Zero,
    };

    /// <summary>The deadline passed: every process of the job ended, the worker waited for within the grace.</summary>
    private WorkerOutcome End(IJobHandle job, IWorkerProcess worker, TimeSpan deadline)
    {
        jobs.EndJob(job, EndedExitCode);
        jobs.Wait(worker, ReapGrace, CancellationToken.None);
        return new WorkerOutcome.DeadlinePassed(deadline);
    }
}
