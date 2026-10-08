using FluentAssertions;

using WslCare.Core.Actions.Engine;
using WslCare.Core.Processes;

namespace WslCare.Core.Tests.Processes;

/// <summary>Plan §15r D8 and risk consult 9/9.4 — what E9.S4 asks of the launcher: a streamed line is a step of the run, a
/// self-invocation's stdin reads end-of-file, and whoever asked hears the started child's pid.</summary>
public sealed partial class ProcessCommandRunnerTests
{
    /// <summary>D8: a long archive child that reports reads live — every line it prints is progress, as a command's start and end
    /// are.</summary>
    [Fact]
    public async Task A_streamed_line_is_a_run_step()
    {
        var progress = RunProgress.Begin();
        var lines = 0;

        var outcome = await Runner.StreamAsync(new CommandRequest(Shell("echo a& echo b& echo c"), TimeSpan.FromSeconds(30)), _ => lines++, TestContext.Current.CancellationToken);

        outcome.Should().BeOfType<CommandOutcome.Exited>();
        lines.Should().Be(3);
        progress.Steps.Should().BeGreaterThanOrEqualTo(5, "the start, each of the three lines and the end are steps");
    }

    /// <summary>9/9.4 #2: a self-invocation never reads this process's stdin (a terminal when a person runs <c>act</c>): a child
    /// that waits for a line on stdin reads end-of-file at once.</summary>
    [Fact]
    public async Task A_child_whose_stdin_is_closed_reads_end_of_file_at_once()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "a shell's read builtin: the Linux legs");
        var request = new CommandRequest(Shell("read line; echo \"read [$line]\""), TimeSpan.FromSeconds(30)) { StdinClosed = true };

        var outcome = await Runner.RunAsync(request, TestContext.Current.CancellationToken);

        outcome.Should().BeOfType<CommandOutcome.Exited>().Which.Stdout.Text.Trim().Should().Be("read []");
    }

    /// <summary>9/9.4 #1: the archive records its children's identities — the launcher tells it the pid it started.</summary>
    [Fact]
    public async Task The_started_childs_pid_is_told_to_whoever_asked()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "a shell printing its own pid: the Linux legs");
        var told = 0;
        var request = new CommandRequest(Shell("echo $$"), TimeSpan.FromSeconds(30)) { OnStarted = pid => told = pid };

        var outcome = await Runner.RunAsync(request, TestContext.Current.CancellationToken);

        told.Should().BePositive();
        outcome.Should().BeOfType<CommandOutcome.Exited>().Which.Stdout.Text.Trim().Should().Be(told.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }
}
