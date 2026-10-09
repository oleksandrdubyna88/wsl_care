using System.Diagnostics;

using FluentAssertions;

using WslCare.Core.Processes;
using WslCare.TestSupport;

namespace WslCare.Core.Tests.Processes;

/// <summary>
/// The real runner against real children: exit codes, captured output, the ceiling, the tree kill,
/// the cap, cancellation, and the policy gate. The shell here is the SUBJECT's child — a way to get a
/// process that spawns a grandchild — never the way the product runs anything. Wall-clock budgets: run alone
/// (<see cref="WallClock"/>), each wide enough for a loaded machine (2026-10-03).
/// </summary>
[Collection(WallClock.Name)]
public sealed partial class ProcessCommandRunnerTests
{
    // The subject's children are shells, which the product's never-list refuses: the runner's own tests take its one unguarded seam.
    private static readonly ICommandRunner Runner = ProcessCommandRunner.UnguardedForItsOwnTests(_ => CommandVerdict.Allowed);

    private static IReadOnlyList<string> Shell(string script) =>
        OperatingSystem.IsWindows() ? ["cmd.exe", "/d", "/c", script] : ["sh", "-c", script];

    /// <summary>A parent that starts a long-lived grandchild, prints the grandchild's pid, then waits for it.</summary>
    private static IReadOnlyList<string> ParentWithGrandchild() =>
        OperatingSystem.IsWindows()
            ? ["powershell.exe", "-NoProfile", "-NonInteractive", "-Command",
               "$p = Start-Process -FilePath ping.exe -ArgumentList '-n','90','127.0.0.1' -PassThru -NoNewWindow -RedirectStandardOutput ([System.IO.Path]::GetTempFileName()); Write-Output $p.Id; Wait-Process -Id $p.Id"]
            : ["sh", "-c", "sleep 90 & echo $!; wait $!"];

    [Fact]
    public async Task An_exiting_command_reports_its_code_and_both_streams()
    {
        var outcome = await Runner.RunAsync(new CommandRequest(Shell("echo out& echo err 1>&2& exit 3"), TimeSpan.FromSeconds(30)), CancellationToken.None);

        var exited = outcome.Should().BeOfType<CommandOutcome.Exited>().Subject;
        exited.ExitCode.Should().Be(3);
        exited.Stdout.Text.Trim().Should().Be("out");
        exited.Stderr.Text.Trim().Should().Be("err");
        exited.Stdout.Truncated.Should().BeFalse();
    }

    [Fact]
    public async Task An_executable_that_does_not_exist_is_a_typed_failure_to_start()
    {
        var outcome = await Runner.RunAsync(new CommandRequest(["wsl-care-no-such-binary-7f3a"], TimeSpan.FromSeconds(5)), CancellationToken.None);

        outcome.Should().BeOfType<CommandOutcome.FailedToStart>().Which.Reason.Should().Contain("wsl-care-no-such-binary-7f3a");
    }

    [Fact]
    public async Task A_timeout_kills_the_whole_process_tree_and_reports_what_was_captured()
    {
        var watch = Stopwatch.StartNew();
        var outcome = await Runner.RunAsync(new CommandRequest(ParentWithGrandchild(), TimeSpan.FromSeconds(20)), CancellationToken.None);
        watch.Stop();

        var timedOut = outcome.Should().BeOfType<CommandOutcome.TimedOut>().Subject;
        watch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(60), "a tree kill must not wait for the grandchild's own 90 s");
        var pidText = timedOut.Stdout.Text.Trim();
        int.TryParse(pidText, out var grandchild).Should().BeTrue($"the parent prints its child's pid before waiting; stdout was '{pidText}'");
        await WaitUntilGone(grandchild);
    }

    [Fact]
    public async Task Output_past_the_cap_is_cut_and_marked_truncated_without_blocking_the_child()
    {
        var script = OperatingSystem.IsWindows()
            ? "for /L %i in (1,1,3000) do @echo xxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx"
            : "yes xxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx | head -n 3000";
        var request = new CommandRequest(Shell(script), TimeSpan.FromMinutes(3)) { OutputCapChars = 10_000 };

        var outcome = await Runner.RunAsync(request, CancellationToken.None);

        var exited = outcome.Should().BeOfType<CommandOutcome.Exited>().Subject;
        exited.Stdout.Truncated.Should().BeTrue();
        exited.Stdout.Text.Length.Should().Be(10_000);
    }

    [Fact]
    public async Task The_callers_cancellation_kills_the_tree_and_surfaces_as_cancellation_not_as_a_timeout()
    {
        using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
        var watch = Stopwatch.StartNew();

        var act = () => Runner.RunAsync(new CommandRequest(Shell(OperatingSystem.IsWindows() ? "ping -n 60 127.0.0.1 > nul" : "sleep 60"), TimeSpan.FromMinutes(5)), cancel.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        watch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(60));
    }

    [Fact]
    public async Task A_refusing_policy_prevents_the_start_entirely()
    {
        var runner = new ProcessCommandRunner(Core.Processes.Policy.CommandPolicy.Over(Core.Processes.Policy.CommandCatalogue.Empty));

        // A binary that does not exist: had the runner tried to start it, the outcome would be FailedToStart.
        var outcome = await runner.RunAsync(new CommandRequest(["wsl-care-no-such-binary-7f3a"], TimeSpan.FromSeconds(5)), CancellationToken.None);

        outcome.Should().BeOfType<CommandOutcome.Refused>().Which.Reason.Should().Contain("no declared command template matches", "deny by default: a policy that declares nothing refuses everything");
    }

    [Fact]
    public void A_request_without_a_positive_ceiling_cannot_be_constructed()
    {
        var zero = () => new CommandRequest(["x"], TimeSpan.Zero);
        var infinite = () => new CommandRequest(["x"], Timeout.InfiniteTimeSpan);
        var empty = () => new CommandRequest([], TimeSpan.FromSeconds(1));

        zero.Should().Throw<ArgumentOutOfRangeException>();
        infinite.Should().Throw<ArgumentOutOfRangeException>();
        empty.Should().Throw<ArgumentException>();
    }

    /// <summary>A parent that starts a long-lived grandchild, prints "parentPid grandchildPid" on one line, then waits.</summary>
    private static IReadOnlyList<string> ParentAndGrandchildPids() =>
        OperatingSystem.IsWindows()
            ? ["powershell.exe", "-NoProfile", "-NonInteractive", "-Command",
               "$p = Start-Process -FilePath ping.exe -ArgumentList '-n','90','127.0.0.1' -PassThru -NoNewWindow -RedirectStandardOutput ([System.IO.Path]::GetTempFileName()); Write-Output \"$PID $($p.Id)\"; Wait-Process -Id $p.Id"]
            : ["sh", "-c", "sleep 90 & echo \"$$ $!\"; wait $!"];

    private static (int Parent, int Grandchild) Pids(string line)
    {
        var parts = line.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        parts.Should().HaveCount(2, $"the parent prints its own pid and its child's; the line was '{line}'");
        return (int.Parse(parts[0], System.Globalization.CultureInfo.InvariantCulture), int.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture));
    }

    [Fact]
    public async Task A_stream_callback_that_throws_kills_the_child_and_its_grandchild_before_the_exception_arrives()
    {
        // Gate finding #1/#5: the callback's failure must not leave the child running behind it. The PARENT is reaped
        // before StreamAsync throws (alive at that moment = killed but not awaited); the grandchild dies with the tree.
        (int Parent, int Grandchild)? pids = null;
        var act = () => Runner.StreamAsync(new CommandRequest(ParentAndGrandchildPids(), TimeSpan.FromMinutes(2)), line =>
        {
            pids = Pids(line);
            throw new InvalidOperationException("the caller could not take this line");
        }, TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("the caller could not take this line");

        pids.Should().NotBeNull("the first line reached the callback");
        IsAlive(pids!.Value.Parent).Should().BeFalse($"the streaming child {pids.Value.Parent} is killed AND awaited before the exception propagates");
        await WaitUntilGone(pids.Value.Grandchild);
    }

    [Fact]
    public async Task Cancelling_a_stream_kills_the_child_and_its_grandchild_and_surfaces_as_cancellation()
    {
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        (int Parent, int Grandchild)? pids = null;
        var act = () => Runner.StreamAsync(new CommandRequest(ParentAndGrandchildPids(), TimeSpan.FromMinutes(2)), line =>
        {
            pids = Pids(line);
            cancel.CancelAfter(TimeSpan.FromMilliseconds(200));
        }, cancel.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();

        pids.Should().NotBeNull("the first line reached the callback");
        IsAlive(pids!.Value.Parent).Should().BeFalse($"the streaming child {pids.Value.Parent} is killed AND awaited before the cancellation propagates");
        await WaitUntilGone(pids.Value.Grandchild);
    }

    [Fact]
    public async Task A_child_writing_more_than_a_megabyte_to_stderr_while_streaming_stdout_runs_to_its_end()
    {
        // stderr is drained concurrently into a BOUNDED capture: a chatty child neither blocks on a full stderr pipe
        // (which would stall its stdout and hang the stream to its ceiling) nor grows this process without bound.
        var padding = new string('e', 800);
        var script = OperatingSystem.IsWindows()
            ? $"for /L %i in (1,1,2000) do @(echo {padding} 1>&2& echo line%i)"
            : $"i=0; while [ $i -lt 2000 ]; do echo {padding} >&2; echo line$i; i=$((i+1)); done";
        var request = new CommandRequest(Shell(script), TimeSpan.FromMinutes(3)) { OutputCapChars = 64 * 1024 };
        var lines = 0;

        var outcome = await Runner.StreamAsync(request, _ => lines++, TestContext.Current.CancellationToken);

        var exited = outcome.Should().BeOfType<CommandOutcome.Exited>().Subject;
        exited.ExitCode.Should().Be(0);
        lines.Should().Be(2000, "every stdout line arrived while 1.6 MB went to stderr");
        exited.Stderr.Truncated.Should().BeTrue("the capture is bounded at the cap");
        exited.Stderr.Text.Length.Should().Be(64 * 1024);
    }

    private static async Task WaitUntilGone(int pid)
    {
        for (var attempt = 0; attempt < 50; attempt++)
        {
            if (!IsAlive(pid))
            {
                return;
            }

            await Task.Delay(100);
        }

        IsAlive(pid).Should().BeFalse($"the grandchild {pid} must be dead after the tree kill");
    }

    private static bool IsAlive(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }
}
