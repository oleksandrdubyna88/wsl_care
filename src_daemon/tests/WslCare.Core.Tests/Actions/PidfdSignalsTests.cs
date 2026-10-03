using System.Globalization;

using FluentAssertions;

using WslCare.Core.Hosting;
using WslCare.Core.Processes;
using WslCare.TestSupport;

namespace WslCare.Core.Tests.Actions;

/// <summary>
/// The pidfd sender's LOGIC over a fake of its native calls (<see cref="IPidfdCalls"/>) and a clock the test moves — on any
/// platform: a <c>poll</c> that fails is a failure, never an end (independent review of E3, item 6).
/// </summary>
public sealed class PidfdSignalsTests : IDisposable
{
    private const int SigTerm = 15;
    private const int SigKill = 9;

    private readonly LinuxSandbox _sandbox = new("pidfd-logic");
    private readonly ManualTimeProvider _clock = new(FixedTimeProvider.DefaultNow);

    public void Dispose() => _sandbox.Dispose();

    /// <summary>Native calls where every process is alive and never ends; a poll "waits" by moving the clock, or fails.</summary>
    private sealed class NeverEnding(ManualTimeProvider clock) : IPidfdCalls
    {
        public int PollError { get; init; }

        public List<(int Fd, int Signal)> Sent { get; } = [];

        public List<int> Closed { get; } = [];

        public Action OnPoll { get; init; } = static () => { };

        public int Open(int pid) => pid;

        public int Signal(int fd, int signal)
        {
            Sent.Add((fd, signal));
            return 0;
        }

        public int Poll(IReadOnlyList<int> fds, bool[] ready, int milliseconds)
        {
            if (PollError != 0)
            {
                return -PollError;
            }

            OnPoll();
            clock.Advance(TimeSpan.FromMilliseconds(milliseconds));
            return 0;
        }

        public void Close(int fd) => Closed.Add(fd);
    }

    private void Stat(int pid, long cpuTicks = 100, long start = 4000)
    {
        _sandbox.Write($"/proc/{pid}/stat", string.Create(CultureInfo.InvariantCulture, $"{pid} (VBCSCompiler) S 1 {pid} {pid} 0 -1 0 0 0 0 0 {cpuTicks} 0 0 0 20 0 1 0 {start} 0 0\n"));
        _sandbox.Write($"/proc/{pid}/status", "Name:\tVBCSCompiler\nState:\tS (sleeping)\nPPid:\t1\nUid:\t1000\t1000\t1000\t1000\nRssAnon:\t1000 kB\nRssShmem:\t0 kB\n");
    }

    private PidfdProcessSignals Signals(IPidfdCalls calls) => new(_sandbox.Files, ((LinuxHostPaths)_sandbox.Paths).ProcRoot, calls, _clock);

    [Fact]
    public async Task A_poll_that_fails_is_a_failure_never_an_end_and_no_sigkill_is_sent_on_that_assumption()
    {
        Stat(10);
        var calls = new NeverEnding(_clock) { PollError = 9 }; // EBADF

        var outcome = await Signals(calls).TerminateAsync(new ProcessIdentity(10, 4000), TimeSpan.FromSeconds(10), CancellationToken.None);

        outcome.Should().BeOfType<SignalOutcome.Failed>().Which.Reason.Should().Contain("poll");
        calls.Sent.Should().Equal([(10, SigTerm)], "whether it ended is unknown: nothing more is sent on a guess");
    }
}
