using System.Diagnostics;

using FluentAssertions;

using WslCare.Core.Processes;
using WslCare.TestSupport;

namespace WslCare.Core.Tests.Processes;

/// <summary>
/// The real runner against real children: exit codes, captured output, the ceiling, the tree kill,
/// the cap, cancellation, and the policy gate. The shell here is the SUBJECT's child — a way to get a
/// process that spawns a grandchild — never the way the product runs anything.
/// </summary>
public sealed class ProcessCommandRunnerTests
{
    private static readonly ICommandRunner Runner = new ProcessCommandRunner(new AllowAllCommandPolicy());

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
        var outcome = await Runner.RunAsync(new CommandRequest(ParentWithGrandchild(), TimeSpan.FromSeconds(6)), CancellationToken.None);
        watch.Stop();

        var timedOut = outcome.Should().BeOfType<CommandOutcome.TimedOut>().Subject;
        watch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(30), "a tree kill must not wait for the grandchild's own 90 s");
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
        var request = new CommandRequest(Shell(script), TimeSpan.FromSeconds(60)) { OutputCapChars = 10_000 };

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
        watch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(20));
    }

    [Fact]
    public async Task A_refusing_policy_prevents_the_start_entirely()
    {
        var runner = new ProcessCommandRunner(new RefuseAllCommandPolicy("the never-list says no"));

        // A binary that does not exist: had the runner tried to start it, the outcome would be FailedToStart.
        var outcome = await runner.RunAsync(new CommandRequest(["wsl-care-no-such-binary-7f3a"], TimeSpan.FromSeconds(5)), CancellationToken.None);

        outcome.Should().BeOfType<CommandOutcome.Refused>().Which.Reason.Should().Be("the never-list says no");
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
