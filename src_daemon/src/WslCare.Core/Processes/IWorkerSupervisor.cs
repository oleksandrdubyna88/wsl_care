using WslCare.Core.Collectors;

namespace WslCare.Core.Processes;

/// <summary>
/// WR-D3 (PLAN_windows_runner.md §3.3): runs ONE worker of a Windows task as a bounded tree — every process it starts lives and dies
/// inside one kill-on-close Job Object, and the whole tree is ended at the worker's deadline. The real one is
/// <see cref="JobSupervisor"/> (the Windows binary); everything else holds a <see cref="RefusingWorkerSupervisor"/>.
/// </summary>
public interface IWorkerSupervisor
{
    /// <summary>Starts <paramref name="launch"/> inside a new job, waits for it until its deadline (counted on the unbiased clock), and
    /// answers how it ended. The job is closed before this returns, whatever happened — and with it every process still in it.</summary>
    WorkerOutcome Run(WorkerLaunch launch, CancellationToken cancellationToken);
}

/// <summary>What one supervised run starts: an ABSOLUTE executable, its arguments and its deadline. The constructor is internal: the
/// product builds one only from the CLOSED table of <see cref="Scheduler.TaskWorkers"/>, so no free argv ever reaches the supervisor
/// (§4: "never start free argv through the supervisor").</summary>
public sealed record WorkerLaunch
{
    internal WorkerLaunch(string executable, IReadOnlyList<string> arguments, TimeSpan deadline)
    {
        Executable = executable;
        Arguments = arguments;
        Deadline = deadline;
    }

    /// <summary>The worker's executable, by its absolute path (never looked up on <c>PATH</c>).</summary>
    public string Executable { get; }

    /// <summary>The worker's arguments, each one argument (quoted for the Windows command line by <see cref="WindowsCommandLine"/>).</summary>
    public IReadOnlyList<string> Arguments { get; }

    /// <summary>How long the worker may run, counted on the unbiased clock (a host sleep is not run time).</summary>
    public TimeSpan Deadline { get; }
}

/// <summary>How a supervised run ended — a closed set.</summary>
public abstract record WorkerOutcome
{
    private WorkerOutcome()
    {
    }

    /// <summary>The worker exited by itself with <paramref name="Code"/>; whatever it left in the job was ended when the job closed.</summary>
    public sealed record Exited(int Code) : WorkerOutcome;

    /// <summary>The worker was still running at its deadline: the job was terminated, so its whole tree is gone.</summary>
    public sealed record DeadlinePassed(TimeSpan Deadline) : WorkerOutcome;

    /// <summary>No worker ran, or it was ended before it ran one instruction outside the job: <paramref name="Reason"/> says why.</summary>
    public sealed record Refused(string Reason) : WorkerOutcome;

    /// <summary>A signal (Ctrl+C, a stop) cancelled the run: the job was closed, which ended the worker's tree (kill-on-close).</summary>
    public sealed record Cancelled : WorkerOutcome;
}

/// <summary>A supervisor that starts nothing — the distro's binary and every host a test builds without its own.</summary>
public sealed class RefusingWorkerSupervisor(string why) : IWorkerSupervisor
{
    public WorkerOutcome Run(WorkerLaunch launch, CancellationToken cancellationToken) => new WorkerOutcome.Refused(why);
}

/// <summary>
/// The operating-system half of <see cref="JobSupervisor"/>, as a seam: the real one is <see cref="Win32JobControl"/>; a test hands a
/// fake that records the ORDER of the calls, which is what the supervisor's rules are about (§3.3).
/// </summary>
public interface IJobControl
{
    /// <summary>A new job: kill-on-close, no breakaway, its handle NOT inheritable — or why it could not be made.</summary>
    Reading<IJobHandle> CreateJob();

    /// <summary>The worker created SUSPENDED and without a window, inheriting the three standard handles and nothing else — or why it
    /// could not be created.</summary>
    Reading<IWorkerProcess> StartSuspended(string executable, string commandLine);

    /// <summary>Puts the suspended worker in the job; empty when it is in, else why not.</summary>
    string Assign(IJobHandle job, IWorkerProcess worker);

    /// <summary>Lets the worker's first thread run; empty when it does, else why not.</summary>
    string Resume(IWorkerProcess worker);

    /// <summary>Waits for the worker's exit, at most <paramref name="timeout"/>, or until <paramref name="cancellationToken"/> fires.</summary>
    WorkerWait Wait(IWorkerProcess worker, TimeSpan timeout, CancellationToken cancellationToken);

    /// <summary>Ends every process in the job with <paramref name="exitCode"/>.</summary>
    void EndJob(IJobHandle job, uint exitCode);

    /// <summary>Ends the worker that never left its suspended start (an assign that failed) with <paramref name="exitCode"/>; empty
    /// when it was ended, else why not (it then stays suspended — never resumed).</summary>
    string EndSuspended(IWorkerProcess worker, uint exitCode);
}

/// <summary>A job's handle; disposing it closes it — and, the job being kill-on-close, ends every process still in it.</summary>
public interface IJobHandle : IDisposable;

/// <summary>A created worker's process (and its first thread); disposing it closes the handles, never the process.</summary>
public interface IWorkerProcess : IDisposable
{
    int Pid { get; }
}

/// <summary>How one wait on the worker ended — a closed set.</summary>
public abstract record WorkerWait
{
    private WorkerWait()
    {
    }

    public sealed record Exited(int Code) : WorkerWait;

    public sealed record TimedOut : WorkerWait;

    public sealed record Cancelled : WorkerWait;

    /// <summary>The worker exited, but its exit code could not be read: why.</summary>
    public sealed record Unreadable(string Reason) : WorkerWait;
}
