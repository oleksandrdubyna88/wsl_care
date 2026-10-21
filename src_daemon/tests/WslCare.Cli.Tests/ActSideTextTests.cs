using FluentAssertions;

using WslCare.Core.Actions;
using WslCare.Core.Config;
using WslCare.Core.Hosting;
using WslCare.Core.Processes.Policy;
using WslCare.TestSupport;

namespace WslCare.Cli.Tests;

/// <summary>
/// E19.S3: <c>act</c>'s refusal of an action of the other side names the side the action RUNS on. It said "runs inside the WSL distro"
/// whatever the action; the first Windows action (A24) would have been refused on the distro's binary with a sentence about the distro.
/// </summary>
public sealed class ActSideTextTests : IDisposable
{
    private static readonly ProcessPrivilege Root = new(true, "a test says so");

    private readonly LinuxSandbox _sandbox = new("act-side-text");

    public void Dispose() => _sandbox.Dispose();

    private (int Exit, string Stderr) Ask(IReadOnlyList<HostSide> sidesOfA10)
    {
        var registry = new ActionRegistry([new SidedA10(sidesOfA10)]);
        var host = new CliHost(_sandbox.Paths, _sandbox.Files, new FixedTimeProvider(), new RecordingCommandRunner { Policy = CommandPolicy.Product })
        {
            Privilege = Root,
            Processes = new FakeProcessTable(),
            Actions = registry,
        };

        var (exit, _, stderr) = CliRun.Over(host, "act", "A10", "--preview");
        return (exit, stderr);
    }

    [Fact]
    public void An_action_of_the_windows_side_asked_of_the_distros_binary_is_refused_naming_the_windows_host()
    {
        var (exit, stderr) = Ask([HostSide.Windows]);

        exit.Should().Be((int)ExitCode.Usage);
        stderr.Should().Contain("A10 runs on the Windows host, not on this side (the WSL binary)");
        stderr.Should().NotContain("inside the WSL distro");
    }

    [Fact]
    public void An_action_of_the_distro_asked_of_a_windows_layout_still_names_the_distro()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "the Windows layout's probe reads Win32 counters: the Windows legs");
        using var windows = new SandboxHost("act-side-text-windows");
        var registry = new ActionRegistry([new SidedA10([HostSide.Wsl])]);
        var host = new CliHost(windows.Paths, windows.Files, new FixedTimeProvider(), new RecordingCommandRunner { Policy = CommandPolicy.Product }) { Privilege = Root, Processes = new FakeProcessTable(), Actions = registry };

        var (exit, _, stderr) = CliRun.Over(host, "act", "A10", "--preview");

        exit.Should().Be((int)ExitCode.Usage);
        stderr.Should().Contain("A10 runs inside the WSL distro, not on this side (the Windows binary)");
    }

    [Fact]
    public void An_action_of_both_sides_is_not_refused_as_the_other_sides()
    {
        var (_, stderr) = Ask([HostSide.Wsl, HostSide.Windows]);

        stderr.Should().NotContain("not on this side");
    }

    private sealed class SidedA10(IReadOnlyList<HostSide> sides) : ICleanupAction
    {
        public ActionId Id { get; } = ActionId.Find("A10")!;

        public string Summary => "a test double";

        public CommandScope Scope => CommandScope.Machine;

        public IdleRule Idle => IdleRule.Never;

        public IReadOnlyList<HostSide> Sides { get; } = sides;

        public IReadOnlyList<CommandTemplate> Commands { get; } = [];

        public Task<ActionPreview> PreviewAsync(ActionContext context, ActionCommands commands, CancellationToken cancellationToken) =>
            Task.FromResult(ActionPreview.Of("a test double", 0, null, "scripted", new Dictionary<string, long>(), string.Empty, []));

        public TriggerDecision Trigger(ActionPreview preview, EffectiveConfig config) => new(false, "never");

        public Task<ActionRun> RunAsync(ActionContext context, ActionPreview preview, ActionCommands commands, CancellationToken cancellationToken) =>
            Task.FromResult(ActionRun.Nothing(commands.Ran));
    }
}
