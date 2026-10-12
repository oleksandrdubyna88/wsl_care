using System.Runtime.InteropServices;

using FluentAssertions;

using Microsoft.Win32.SafeHandles;

using WslCare.Core.Collectors;
using WslCare.Core.Mcp;
using WslCare.Core.Processes;
using WslCare.TestSupport;

namespace WslCare.Core.Tests.Processes;

/// <summary>
/// WR-D3 on the Windows legs (PLAN_windows_runner.md §3.3, §6): the real job, read back through Win32 — its handle is not inheritable,
/// it is kill-on-close with no breakaway — and the real supervisor over the test's OWN helper tree: a Windows PowerShell worker that
/// starts a <c>ping.exe</c> grandchild and writes both pids and creation times (<see cref="JobHelperTree"/>). After the deadline (and after a cancellation) both processes, held by
/// their own handles from the moment they were seen — pid AND creation time — must have exited. Nothing else is started, signalled or
/// looked up: whatever is still alive at the end of a test is one of these two, ended through the handle the test holds.
/// </summary>
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
public sealed partial class Win32JobControlTests
{
    private const string WindowsOnly = "the Job Object is Windows'; the distro's binary supervises no task";
    private const uint HandleFlagInherit = 0x1;
    private const int ExtendedLimitInformationClass = 9;
    private const uint KillOnJobClose = 0x2000;
    private const uint BreakawayOk = 0x0800;
    private const uint SilentBreakawayOk = 0x1000;

    [Fact]
    public void The_job_handle_is_not_inheritable()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), WindowsOnly);
        using var job = Created();

        GetHandleInformation(job.Handle, out var flags).Should().BeTrue();

        (flags & HandleFlagInherit).Should().Be(0u, "no process the supervisor creates may hold the job's handle (§4)");
    }

    [Fact]
    public void The_job_is_kill_on_close_and_lets_no_child_break_away()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), WindowsOnly);
        using var job = Created();

        QueryInformationJobObject(job.Handle, ExtendedLimitInformationClass, out var limits, (uint)Marshal.SizeOf<ExtendedLimits>(), out _).Should().BeTrue();

        limits.Basic.LimitFlags.Should().Be(KillOnJobClose, "kill-on-close, and neither BREAKAWAY_OK nor SILENT_BREAKAWAY_OK");
        (limits.Basic.LimitFlags & (BreakawayOk | SilentBreakawayOk)).Should().Be(0u);
    }

    [Fact]
    public async Task A_child_and_its_grandchild_are_gone_after_the_deadline()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), WindowsOnly);
        using var root = new TempRoot("job-deadline");
        using var tree = new JobHelperTree(Path.Combine(root.Path, "pids.txt"));
        var supervisor = new JobSupervisor(new Win32JobControl(), new Win32Boot());

        var run = Task.Run(() => supervisor.Run(Launch(tree, TreeDeadline), CancellationToken.None));
        var seen = await tree.SeenAsync(SeenCeiling);
        var outcome = await run.WaitAsync(TimeSpan.FromSeconds(90), TestContext.Current.CancellationToken);

        seen.Should().BeEmpty("both helpers are held by pid AND creation time while the job still holds them");
        outcome.Should().Be(new WorkerOutcome.DeadlinePassed(TreeDeadline));
        (await tree.AllExitedAsync(TimeSpan.FromSeconds(10))).Should().BeTrue($"the worker and its grandchild are gone after the deadline: {tree.Describe()}");
        tree.ExitCodes().Should().AllBeEquivalentTo(unchecked((int)JobSupervisor.EndedExitCode), "both were ended by the job, not by themselves");
    }

    [Fact]
    public async Task A_cancelled_run_closes_the_job_and_its_whole_tree_goes_with_it()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), WindowsOnly);
        using var root = new TempRoot("job-cancel");
        using var tree = new JobHelperTree(Path.Combine(root.Path, "pids.txt"));
        using var cancel = new CancellationTokenSource();
        var supervisor = new JobSupervisor(new Win32JobControl(), new Win32Boot());

        var run = Task.Run(() => supervisor.Run(Launch(tree, TimeSpan.FromMinutes(5)), cancel.Token));
        var seen = await tree.SeenAsync(SeenCeiling);
        await cancel.CancelAsync();
        var outcome = await run.WaitAsync(TimeSpan.FromSeconds(60), TestContext.Current.CancellationToken);

        seen.Should().BeEmpty("both helpers are held by pid AND creation time");
        outcome.Should().Be(new WorkerOutcome.Cancelled());
        (await tree.AllExitedAsync(TimeSpan.FromSeconds(10))).Should().BeTrue($"closing the kill-on-close job ends the worker and its grandchild: {tree.Describe()}");
    }

    [Fact]
    public void A_worker_that_exits_by_itself_answers_its_own_code()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), WindowsOnly);

        var outcome = new JobSupervisor(new Win32JobControl(), new Win32Boot()).Run(new WorkerLaunch(Cmd, ["/d", "/c", "exit 7"], TimeSpan.FromSeconds(60), "test.deadline"), CancellationToken.None);

        outcome.Should().Be(new WorkerOutcome.Exited(7));
    }

    /// <summary>The coai code round on WR-D3, finding 1: the worker is created ALREADY INSIDE the job (<c>PROC_THREAD_ATTRIBUTE_JOB_LIST</c>)
    /// — right after its suspended start, before any resume, Windows says it is in the job, and the product's verify agrees. There is no
    /// window in which a supervisor that dies would leave it outside the kill-on-close job.</summary>
    [Fact]
    public void A_worker_is_inside_its_job_from_its_suspended_start()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), WindowsOnly);
        var control = new Win32JobControl();
        using var job = Created();
        using var worker = control.StartSuspended(job, Cmd, WindowsCommandLine.Of(Cmd, ["/d", "/c", "exit 7"])).Should().BeOfType<Reading<IWorkerProcess>.Available>().Subject.Value;

        var inTheJob = InTheJob(worker, job);
        var verified = control.InJob(job, worker);
        var ended = control.EndSuspended(worker, JobSupervisor.EndedExitCode);

        inTheJob.Should().BeTrue("the worker is in its job before it was ever resumed");
        verified.Should().BeEmpty();
        ended.Should().BeEmpty("the test's own helper is ended still suspended, inside or outside the job");
    }

    /// <summary>The worker really is created suspended: a <c>cmd /c exit 7</c> created by <see cref="Win32JobControl.StartSuspended"/>
    /// and put in its own job has NOT exited two seconds later; ended while still suspended, it exits with the supervisor's code.</summary>
    [Fact]
    public void A_worker_created_suspended_does_not_run_until_it_is_resumed()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), WindowsOnly);
        var control = new Win32JobControl();
        using var job = Created();
        using var worker = Started(control, job);

        var meanwhile = control.Wait(worker, TimeSpan.FromSeconds(2), CancellationToken.None);
        var ended = control.EndSuspended(worker, JobSupervisor.EndedExitCode);
        var after = control.Wait(worker, TimeSpan.FromSeconds(10), CancellationToken.None);

        meanwhile.Should().Be(new WorkerWait.TimedOut(), "a suspended `exit 7` runs no instruction");
        ended.Should().BeEmpty();
        after.Should().Be(new WorkerWait.Exited(unchecked((int)JobSupervisor.EndedExitCode)), "it was ended, it did not exit by itself");
    }

    /// <summary>A resume is accepted only from the suspended start (a previous suspend count of exactly 1): a second resume of the
    /// same worker answers "not in its suspended start".</summary>
    [Fact]
    public void A_resume_outside_the_suspended_start_is_refused()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), WindowsOnly);
        var control = new Win32JobControl();
        using var job = Created();
        using var worker = Started(control, job, "ping -n 30 127.0.0.1 >nul");

        var first = control.Resume(worker);
        var second = control.Resume(worker);

        first.Should().BeEmpty("the first resume leaves the suspended start");
        second.Should().Contain("not in its suspended start");
    }

    /// <summary>n1 of the WR-D3 review: a standard handle that cannot be duplicated refuses the start (fail closed) and closes the copies
    /// already made, rather than starting the worker without that stream.</summary>
    [Fact]
    public void A_standard_handle_that_cannot_be_copied_refuses_the_start_and_closes_the_other_copies()
    {
        var closed = new List<nint>();
        var copies = new Dictionary<int, Reading<nint>> { [-10] = Reading.Of<nint>(11), [-11] = Reading.Missing<nint>("DuplicateHandle failed: error 6 (The handle is invalid.)"), [-12] = Reading.Of<nint>(13) };

        var all = Win32JobControl.StandardCopies(which => copies[which], closed.Add);

        all.Should().BeOfType<Reading<nint[]>.Unavailable>().Which.Reason.Should().Contain("error 6").And.Contain("nothing was started");
        closed.Should().BeEquivalentTo(new nint[] { 11, 13 });
    }

    [Fact]
    public void A_standard_handle_this_process_does_not_have_is_simply_not_passed()
    {
        var copies = new Dictionary<int, Reading<nint>> { [-10] = Reading.Of<nint>(0), [-11] = Reading.Of<nint>(21), [-12] = Reading.Of<nint>(22) };

        Win32JobControl.StandardCopies(which => copies[which], _ => throw new InvalidOperationException("nothing is closed on success"))
            .Should().BeOfType<Reading<nint[]>.Available>().Which.Value.Should().Equal(0, 21, 22);
    }

    private static readonly TimeSpan TreeDeadline = TimeSpan.FromSeconds(30);

    /// <summary>Shorter than <see cref="TreeDeadline"/> (M1 of the review): the helper's line is read while the job still holds both.</summary>
    private static readonly TimeSpan SeenCeiling = TimeSpan.FromSeconds(20);

    private static string Cmd => Path.Combine(Environment.SystemDirectory, "cmd.exe");

    private static WorkerLaunch Launch(JobHelperTree tree, TimeSpan deadline) => new(JobHelperTree.Executable, tree.Arguments, deadline, "test.deadline");

    private static Win32JobControl.Job Created() =>
        new Win32JobControl().CreateJob().Should().BeOfType<Reading<IJobHandle>.Available>().Subject.Value.Should().BeOfType<Win32JobControl.Job>().Subject;

    /// <summary>A <c>cmd.exe</c> created suspended INSIDE <paramref name="job"/> — the test's own helper, ended when the job closes. A
    /// worker found outside the job is ended at once, still suspended, by its own handle, and the test fails: no helper of a test ever
    /// runs outside a job.</summary>
    private static IWorkerProcess Started(Win32JobControl control, IJobHandle job, string command = "exit 7")
    {
        var worker = control.StartSuspended(job, Cmd, WindowsCommandLine.Of(Cmd, ["/d", "/c", command])).Should().BeOfType<Reading<IWorkerProcess>.Available>().Subject.Value;
        if (!InTheJob(worker, job))
        {
            control.EndSuspended(worker, JobSupervisor.EndedExitCode);
            worker.Dispose();
            Assert.Fail("the helper was created outside its job; it was ended while still suspended");
        }

        return worker;
    }

    /// <summary>The test's own read of the membership, through Windows' <c>IsProcessInJob</c> — not the product's <c>InJob</c>.</summary>
    private static bool InTheJob(IWorkerProcess worker, IJobHandle job) =>
        IsProcessInJob(((Win32JobControl.Worker)worker).Exit.SafeWaitHandle, ((Win32JobControl.Job)job).Handle, out var inJob) && inJob;


    /// <summary>The test's OWN <c>JOBOBJECT_EXTENDED_LIMIT_INFORMATION</c> (a structure of another assembly is not blittable to the source
    /// generator), field for field: the basic limits, <c>IO_COUNTERS</c>, the four sizes.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct ExtendedLimits
    {
        public BasicLimits Basic;
        public ulong Io1;
        public ulong Io2;
        public ulong Io3;
        public ulong Io4;
        public ulong Io5;
        public ulong Io6;
        public nuint ProcessMemoryLimit;
        public nuint JobMemoryLimit;
        public nuint PeakProcessMemoryUsed;
        public nuint PeakJobMemoryUsed;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BasicLimits
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public nuint MinimumWorkingSetSize;
        public nuint MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public nuint Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetHandleInformation(SafeWaitHandle handle, out uint flags);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool QueryInformationJobObject(SafeWaitHandle job, int informationClass, out ExtendedLimits information, uint length, out uint returned);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool IsProcessInJob(SafeWaitHandle process, SafeWaitHandle job, [MarshalAs(UnmanagedType.Bool)] out bool result);
}
