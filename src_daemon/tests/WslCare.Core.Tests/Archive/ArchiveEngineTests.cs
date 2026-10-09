using FluentAssertions;

using WslCare.Core.Actions;
using WslCare.Core.Actions.Engine;
using WslCare.Core.Archive;
using WslCare.Core.Collectors;
using WslCare.Core.Config;
using WslCare.Core.Processes;
using WslCare.Core.Processes.Policy;
using WslCare.Core.Records;
using WslCare.Core.Tests.Actions;
using WslCare.TestSupport;

namespace WslCare.Core.Tests.Archive;

/// <summary>
/// Plan §15r D8, E9.S4 — A13 through the ENGINE: the dry-run week records what WOULD move and starts no run child; a hung run
/// child fails A13 and the run goes on to the actions behind it; with no base the timer skips it naming why.
/// </summary>
public sealed class ArchiveEngineTests : IDisposable
{
    private const int Pid = 4242;

    private readonly LinuxSandbox _sandbox = new("archive-engine");
    private readonly ManualTimeProvider _clock = new(FixedTimeProvider.DefaultNow);
    private readonly FakeProcessTable _processes = new FakeProcessTable().Alive(Pid, FixedTimeProvider.DefaultNow.AddMinutes(-1));
    private readonly List<string> _journal = [];

    public ArchiveEngineTests()
    {
        _sandbox.Write("/etc/passwd", "root:x:0:0::/root:/bin/bash\nme:x:1000:1000::/home/me:/bin/bash\n");
        _sandbox.Write("/proc/sys/kernel/random/boot_id", "6d1c1c5e-0000-4000-8000-0000000000a3\n");
        _sandbox.Write("/etc/pam.d/runuser", ArchiveActionTests.SafeRunuserStack);
        _sandbox.Load(0.1, 0.1, 0.1, cpus: 4);
    }

    public void Dispose() => _sandbox.Dispose();

    private static Func<SelfBinaryResult> Installed => static () => new SelfBinaryResult.Found(ArchiveActionTests.Installed);

    private void Base() => _sandbox.Write("/home/me/.config/wsl-care/config.json", """{ "archive": { "baseFolder": "/mnt/v/ai-archive" } }""");

    private (ActionEngine Engine, RecordingCommandRunner Runner) Engine(IReadOnlyList<string>? runLines = null, CommandOutcome? runOutcome = null, params ICleanupAction[] more)
    {
        var runner = new RecordingCommandRunner { Policy = CommandPolicy.Over(CommandCatalogue.Product, Installed) }
            .Script(argv => TargetUserArgv.Parse(argv) is { Arguments: ["archive", "preview", ..] }, RecordingCommandRunner.Exited(0, ArchiveActionTests.PreviewJson()))
            .Script(argv => TargetUserArgv.Parse(argv) is { Arguments: ["archive", "reach", ..] }, RecordingCommandRunner.Exited(0, ArchiveActionTests.RunJson(ArchiveActionTests.RunReport(copied: 0))));
        runner.Stream(new StreamScript(runLines ?? [ArchiveActionTests.RunJson()], runOutcome ?? RecordingCommandRunner.Exited(0)));
        var context = new EngineContext(_sandbox.Paths, _sandbox.Files, runner, _clock, new LinuxProbe(_sandbox.Files, _sandbox.Paths, _clock), ConfigLoader.Load(_sandbox.Paths, _sandbox.Files), _processes, Pid, new ActionRegistry([new ArchiveAction(), .. more]))
        {
            Self = Installed,
        };
        return (new ActionEngine(context), runner);
    }

    private static ActRequest Run(RunTrigger trigger, params string[] ids) => new([.. ids.Select(i => ActionId.Find(i)!)], trigger, Execute: true);

    private static IReadOnlyList<string> Statuses(ActResult result) => [.. result.Should().BeOfType<ActResult.Done>().Subject.Detail.Actions.Select(a => $"{a.Id}:{a.Status}")];

    [Fact]
    public async Task A13_acts_only_with_a_base_folder_and_after_the_dry_run_week()
    {
        var (withoutBase, idle) = Engine();
        var none = await withoutBase.ExecuteAsync(Run(RunTrigger.Timer, "A13"), CancellationToken.None);
        Base();
        var (engine, runner) = Engine();

        var dry = await engine.ExecuteAsync(Run(RunTrigger.Timer, "A13"), CancellationToken.None);

        Statuses(none).Should().Equal("A13:skipped");
        idle.Requests.Should().BeEmpty("no base: no child at all");
        Statuses(dry).Should().Equal($"A13:{ActionStatus.DryRun}");
        runner.Requests.Should().ContainSingle("the dry-run week records the preview and starts no run").Which.Argv.Should().Contain("preview");
        ((ActResult.Done)dry).Detail.Actions.Single().Preview!.Count.Should().Be(3, "what WOULD move is recorded");
    }

    /// <summary>D8: a run child that hangs is killed at its ceiling — A13 fails with that reason and the run goes on.</summary>
    [Fact]
    public async Task A_hung_archive_child_is_killed_at_its_ceiling_and_the_run_goes_on()
    {
        Base();
        var after = new ScriptedAction("A1", _journal);
        var (engine, _) = Engine([], new CommandOutcome.TimedOut(CapturedText.Empty, CapturedText.Empty, TimeSpan.FromMinutes(35)), after);

        var result = await engine.ExecuteAsync(Run(RunTrigger.Manual, "A13", "A1"), CancellationToken.None);

        Statuses(result).Should().Equal($"A13:{ActionStatus.Failed}", $"A1:{ActionStatus.Ran}");
        ((ActResult.Done)result).Detail.Actions[0].Reason.Should().Contain("ceiling");
    }
}
