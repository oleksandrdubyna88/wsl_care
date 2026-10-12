using FluentAssertions;

using WslCare.Core.Collectors;
using WslCare.Core.Mcp;
using WslCare.Core.Processes;

namespace WslCare.Core.Tests.Processes;

/// <summary>
/// WR-D3 (PLAN_windows_runner.md §3.3, §6): the supervisor's rules are the ORDER of its operating-system calls, so they are read here
/// through a fake <see cref="IJobControl"/> that records every call — no process is created, no job is made. The Win32 half is held
/// on the Windows legs by <see cref="Win32JobControlTests"/>.
/// </summary>
public sealed class JobSupervisorTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromMinutes(10);

    [Fact]
    public void The_worker_is_created_suspended_and_resumed_only_inside_the_job()
    {
        var jobs = new RecordingJobs { Waits = [new WorkerWait.Exited(7)] };

        var outcome = new JobSupervisor(jobs, new ScriptedClock(0)).Run(Launch(), CancellationToken.None);

        outcome.Should().Be(new WorkerOutcome.Exited(7), "the worker's own code is the run's answer");
        jobs.Calls.Should().Equal(
            "create-job",
            "start-suspended-in-job C:\\Users\\user\\AppData\\Local\\wsl-care\\bin\\" + new string('a', 64) + "\\wsl-care.exe watch --timer --json",
            "in-job 4242",
            "resume 4242",
            "wait 4242 00:10:00",
            "close-worker 4242",
            "close-job");
    }

    [Fact]
    public void A_worker_not_in_its_job_is_ended_and_never_resumed()
    {
        var jobs = new RecordingJobs { InJobAnswer = "IsProcessInJob answered false: the worker is not in its job" };

        var outcome = new JobSupervisor(jobs, new ScriptedClock(0)).Run(Launch(), CancellationToken.None);

        outcome.Should().BeOfType<WorkerOutcome.Refused>().Which.Reason.Should().Contain("is not in its job").And.Contain("IsProcessInJob answered false").And.Contain("never ran outside the job");
        jobs.Calls.Should().Equal("create-job", jobs.Calls[1], "in-job 4242", "end-job 0xC5A1", "end-suspended 4242 0xC5A1", "close-worker 4242", "close-job");
        jobs.Calls.Should().NotContain(c => c.StartsWith("resume", StringComparison.Ordinal), "a worker outside the job is never resumed (§4)");
    }

    [Fact]
    public void A_suspended_worker_that_cannot_be_ended_is_named_and_never_claimed_ended()
    {
        var jobs = new RecordingJobs { InJobAnswer = "IsProcessInJob answered false", EndSuspendedAnswer = "TerminateProcess failed: error 5 (Access is denied.)" };

        var outcome = new JobSupervisor(jobs, new ScriptedClock(0)).Run(Launch(), CancellationToken.None);

        var reason = outcome.Should().BeOfType<WorkerOutcome.Refused>().Subject.Reason;
        reason.Should().Contain("TerminateProcess failed: error 5").And.Contain("pid 4242").And.Contain("never resumed");
        reason.Should().NotContain("was ended", "a worker the supervisor could not end is never reported as ended");
        jobs.Calls.Should().NotContain(c => c.StartsWith("resume", StringComparison.Ordinal));
    }

    [Fact]
    public void An_exit_code_that_cannot_be_read_is_a_refusal_not_an_exit()
    {
        var jobs = new RecordingJobs { Waits = [new WorkerWait.Unreadable("GetExitCodeProcess failed: error 6 (The handle is invalid.)"), new WorkerWait.Exited(0)] };

        var outcome = new JobSupervisor(jobs, new ScriptedClock(0)).Run(Launch(), CancellationToken.None);

        outcome.Should().BeOfType<WorkerOutcome.Refused>().Which.Reason.Should().Contain("exit code could not be read").And.Contain("error 6");
        jobs.Calls.Count(c => c.StartsWith("wait", StringComparison.Ordinal)).Should().Be(1, "an unreadable exit is an answer, not a reason to wait again");
    }

    [Fact]
    public void The_deadline_counts_unbiased_time()
    {
        // The first wait timed out after the whole deadline of WALL time, but the unbiased clock moved only half of it: the host slept
        // the other half. A sleep is not run time — the supervisor waits again, for what is left.
        var clock = new ScriptedClock(1_000_000, 1_000_000, 1_000_000 + (long)(Deadline.TotalMilliseconds / 2));
        var jobs = new RecordingJobs { Waits = [new WorkerWait.TimedOut(), new WorkerWait.Exited(0)] };

        var outcome = new JobSupervisor(jobs, clock).Run(Launch(), CancellationToken.None);

        outcome.Should().Be(new WorkerOutcome.Exited(0));
        jobs.Calls.Where(c => c.StartsWith("wait", StringComparison.Ordinal)).Should().Equal("wait 4242 00:10:00", "wait 4242 00:05:00");
        jobs.Calls.Should().NotContain(c => c.StartsWith("end-job", StringComparison.Ordinal), "nothing is ended before the unbiased deadline");
    }

    [Fact]
    public void Awake_time_past_the_deadline_ends_the_whole_job_with_the_supervisor_s_code()
    {
        var clock = new ScriptedClock(0, 0, (long)Deadline.TotalMilliseconds);
        var jobs = new RecordingJobs { Waits = [new WorkerWait.TimedOut(), new WorkerWait.Exited(unchecked((int)JobSupervisor.EndedExitCode))] };

        var outcome = new JobSupervisor(jobs, clock).Run(Launch(), CancellationToken.None);

        outcome.Should().Be(new WorkerOutcome.DeadlinePassed(Deadline));
        jobs.Calls.Should().ContainInOrder("wait 4242 00:10:00", "end-job 0xC5A1", "close-worker 4242", "close-job");
    }

    [Fact]
    public void A_clock_that_stops_answering_mid_run_ends_the_job_rather_than_waiting_unbounded()
    {
        var clock = new ScriptedClock(0) { MissingAfter = 1 };
        var jobs = new RecordingJobs { Waits = [new WorkerWait.TimedOut(), new WorkerWait.TimedOut()] };

        var outcome = new JobSupervisor(jobs, clock).Run(Launch(), CancellationToken.None);

        outcome.Should().BeOfType<WorkerOutcome.DeadlinePassed>();
        jobs.Calls.Should().Contain("end-job 0xC5A1");
    }

    [Fact]
    public void A_cancelled_run_closes_the_job_which_ends_its_tree()
    {
        var jobs = new RecordingJobs { Waits = [new WorkerWait.Cancelled()] };

        var outcome = new JobSupervisor(jobs, new ScriptedClock(0)).Run(Launch(), CancellationToken.None);

        outcome.Should().Be(new WorkerOutcome.Cancelled());
        jobs.Calls.Should().EndWith("close-job").And.NotContain(c => c.StartsWith("end-job", StringComparison.Ordinal), "the close is the stop: the job is kill-on-close");
    }

    [Fact]
    public void An_unreadable_clock_or_a_job_that_cannot_be_made_starts_nothing()
    {
        var noClock = new RecordingJobs();
        var noJob = new RecordingJobs { JobAnswer = "CreateJobObjectW failed: error 8" };

        new JobSupervisor(noClock, new ScriptedClock(0) { MissingAfter = 0 }).Run(Launch(), CancellationToken.None)
            .Should().BeOfType<WorkerOutcome.Refused>().Which.Reason.Should().Contain("unbiased clock");
        new JobSupervisor(noJob, new ScriptedClock(0)).Run(Launch(), CancellationToken.None)
            .Should().BeOfType<WorkerOutcome.Refused>().Which.Reason.Should().Contain("error 8");
        noClock.Calls.Should().BeEmpty();
        noJob.Calls.Should().Equal("create-job");
    }

    [Fact]
    public void A_worker_that_cannot_be_created_closes_the_job_and_starts_nothing()
    {
        var jobs = new RecordingJobs { StartAnswer = "CreateProcessW failed: error 2" };

        var outcome = new JobSupervisor(jobs, new ScriptedClock(0)).Run(Launch(), CancellationToken.None);

        outcome.Should().BeOfType<WorkerOutcome.Refused>().Which.Reason.Should().Contain("error 2");
        jobs.Calls.Should().Equal("create-job", jobs.Calls[1], "close-job");
    }

    private static WorkerLaunch Launch() =>
        new("C:\\Users\\user\\AppData\\Local\\wsl-care\\bin\\" + new string('a', 64) + "\\wsl-care.exe", ["watch", "--timer", "--json"], Deadline, "mcpWatchdog.runLimitMinutes");

    /// <summary>The unbiased clock, one scripted reading per call (the last repeats); <see cref="MissingAfter"/> readings, then none.</summary>
    private sealed class ScriptedClock(params long[] readings) : IWindowsBoot
    {
        private int _calls;

        public int MissingAfter { get; init; } = int.MaxValue;

        public Reading<string> BootId() => Reading.Of("windows-1");

        public Reading<long> UnbiasedMilliseconds()
        {
            var call = _calls++;
            return call >= MissingAfter ? Reading.Missing<long>("scripted: the clock stopped answering") : Reading.Of(readings[Math.Min(call, readings.Length - 1)]);
        }
    }

    /// <summary>Records every call in order; answers as scripted. A wait past its script throws: a supervisor that waits on and on
    /// past its deadline fails the test instead of hanging it.</summary>
    private sealed class RecordingJobs : IJobControl
    {
        private int _waits;

        public List<string> Calls { get; } = [];

        public string JobAnswer { get; init; } = string.Empty;

        public string StartAnswer { get; init; } = string.Empty;

        public string InJobAnswer { get; init; } = string.Empty;

        public string EndSuspendedAnswer { get; init; } = string.Empty;

        public IReadOnlyList<WorkerWait> Waits { get; init; } = [];

        public Reading<IJobHandle> CreateJob()
        {
            Calls.Add("create-job");
            return JobAnswer.Length > 0 ? Reading.Missing<IJobHandle>(JobAnswer) : Reading.Of<IJobHandle>(new Handle(() => Calls.Add("close-job")));
        }

        public Reading<IWorkerProcess> StartSuspended(IJobHandle job, string executable, string commandLine)
        {
            Calls.Add($"start-suspended-in-job {commandLine}");
            return StartAnswer.Length > 0 ? Reading.Missing<IWorkerProcess>(StartAnswer) : Reading.Of<IWorkerProcess>(new Worker(() => Calls.Add("close-worker 4242")));
        }

        public string InJob(IJobHandle job, IWorkerProcess worker)
        {
            Calls.Add($"in-job {worker.Pid}");
            return InJobAnswer;
        }

        public string Resume(IWorkerProcess worker)
        {
            Calls.Add($"resume {worker.Pid}");
            return string.Empty;
        }

        public WorkerWait Wait(IWorkerProcess worker, TimeSpan timeout, CancellationToken cancellationToken)
        {
            Calls.Add($"wait {worker.Pid} {timeout:hh\\:mm\\:ss}");
            var at = _waits++;
            return at < Waits.Count ? Waits[at] : throw new InvalidOperationException($"wait #{at + 1} is past the script: the supervisor waited on past its deadline");
        }

        public void EndJob(IJobHandle job, uint exitCode) => Calls.Add($"end-job 0x{exitCode:X}");

        public string EndSuspended(IWorkerProcess worker, uint exitCode)
        {
            Calls.Add($"end-suspended {worker.Pid} 0x{exitCode:X}");
            return EndSuspendedAnswer;
        }

        private sealed class Handle(Action closed) : IJobHandle
        {
            public void Dispose() => closed();
        }

        private sealed class Worker(Action closed) : IWorkerProcess
        {
            public int Pid => 4242;

            public void Dispose() => closed();
        }
    }
}
