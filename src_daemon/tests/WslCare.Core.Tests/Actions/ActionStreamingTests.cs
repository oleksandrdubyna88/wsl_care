using FluentAssertions;

using WslCare.Core.Actions;
using WslCare.Core.Processes;
using WslCare.Core.Processes.Policy;
using WslCare.TestSupport;

namespace WslCare.Core.Tests.Actions;

/// <summary>Plan §15r D8, E9.S4 — an action streams ONLY a template it declared streamed, under a ceiling it chose that never
/// passes the template's own; the stream is recorded as every command is.</summary>
public sealed class ActionStreamingTests
{
    private static readonly CommandTemplate Streamed = new("t-streamed", CommandScope.Machine, "journalctl", [new ArgPart.Literal("--follow")], TimeSpan.FromMinutes(10), 1024) { Streamed = true };

    private static readonly CommandTemplate Whole = new("t-whole", CommandScope.Machine, "journalctl", [new ArgPart.Literal("--disk-usage")], TimeSpan.FromMinutes(1), 1024);

    private static (ActionCommands Commands, RecordingCommandRunner Runner) Commands()
    {
        var runner = new RecordingCommandRunner();
        // Two scripts: a template wrongly streamed takes one and must not leave the right one waiting for ever.
        runner.Stream(new StreamScript(["one", "two"], RecordingCommandRunner.Exited(0)));
        runner.Stream(new StreamScript(["one", "two"], RecordingCommandRunner.Exited(0)));
        var action = new ScriptedAction("A10", []) { Commands = [Streamed, Whole] };
        return (new ActionCommands(action, runner, new TargetUserResult.None("machine-scoped"), []), runner);
    }

    [Fact]
    public async Task An_action_streams_only_a_template_it_declared_streamed()
    {
        var (commands, runner) = Commands();
        var lines = new List<string>();

        var refused = await commands.StreamAsync(Whole, [], new StreamRequest(TimeSpan.FromSeconds(30), lines.Add), CancellationToken.None);
        var streamed = await commands.StreamAsync(Streamed, [], new StreamRequest(TimeSpan.FromMinutes(5), lines.Add), CancellationToken.None);

        refused.Should().BeOfType<CommandOutcome.Refused>().Which.Reason.Should().Contain("streams only what it declared streamed");
        streamed.Should().BeOfType<CommandOutcome.Exited>();
        lines.Should().Equal("one", "two");
        runner.Requests.Should().ContainSingle().Which.Timeout.Should().Be(TimeSpan.FromMinutes(5), "the ceiling the action chose");
        commands.Ran.Select(r => r.Outcome).Should().Equal("refused", "exited");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(11)]
    public async Task A_stream_ceiling_outside_the_templates_own_is_refused(int minutes)
    {
        var (commands, runner) = Commands();

        var outcome = await commands.StreamAsync(Streamed, [], new StreamRequest(TimeSpan.FromMinutes(minutes), _ => { }), CancellationToken.None);

        outcome.Should().BeOfType<CommandOutcome.Refused>().Which.Reason.Should().Contain("outside");
        runner.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task A_stream_tells_the_action_the_started_pid()
    {
        var (commands, _) = Commands();
        var started = 0;

        await commands.StreamAsync(Streamed, [], new StreamRequest(TimeSpan.FromMinutes(5), _ => { }) { OnStarted = pid => started = pid }, CancellationToken.None);

        started.Should().Be(4242);
    }
}
