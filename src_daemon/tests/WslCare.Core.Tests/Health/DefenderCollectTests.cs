using FluentAssertions;

using WslCare.Core.Collectors;
using WslCare.Core.Files;
using WslCare.Core.Health;
using WslCare.Core.Hosting;
using WslCare.Core.Processes;
using WslCare.TestSupport;

namespace WslCare.Core.Tests.Health;

/// <summary>
/// E15.S10: the full run starts the Defender script AFTER the disk script, under its own ceiling, and a stall costs only itself: the
/// disk facts stay available, the Defender sample reports its own timeout, and the runner's tree kill is told for its child.
/// </summary>
public sealed class DefenderCollectTests : IDisposable
{
    private const string Measured = "defender=993513593750|134366539776100740|6964|134365236258808755\n";

    private readonly TempRoot _root = new("defender-collect");

    public void Dispose() => _root.Dispose();

    private static T Value<T>(Reading<T> reading)
    {
        reading.Should().BeOfType<Reading<T>.Available>(reading.ReasonOrEmpty);
        return ((Reading<T>.Available)reading).Value;
    }

    /// <summary>A runner that tells each request's <c>OnKilling</c> to a list, as the real runner does just before it kills a tree, then
    /// hands the request on — the double for "its child is killed".</summary>
    private sealed class KillWatchingRunner(ICommandRunner inner) : ICommandRunner
    {
        public List<IReadOnlyList<string>> Killed { get; } = [];

        public Task<CommandOutcome> RunAsync(CommandRequest request, CancellationToken cancellationToken) =>
            inner.RunAsync(request with { OnKilling = _ => Killed.Add(request.Argv) }, cancellationToken);

        public Task<CommandOutcome> StreamAsync(CommandRequest request, Action<string> onStdoutLine, CancellationToken cancellationToken) =>
            inner.StreamAsync(request, onStdoutLine, cancellationToken);
    }

    private RecordingCommandRunner Scripted(CommandOutcome defender) =>
        HealthFixture.Script(new RecordingCommandRunner { Default = new CommandOutcome.FailedToStart("not scripted") })
            .Script(WindowsDiskCommands.WindowsDisk.Argv, 0, WindowsDiskFixture.Answer)
            .Script(DefenderCpuCommands.DefenderCpu.Argv, defender);

    private async Task<(HealthSample Health, RecordingCommandRunner Runner, KillWatchingRunner Watched)> CollectAsync(CommandOutcome defender)
    {
        var paths = new LinuxHostPaths(LinuxEnvironment.Sandboxed(_root.Path));
        var runner = Scripted(defender);
        var watched = new KillWatchingRunner(runner);
        var health = await new HealthCollector(watched, new PhysicalFileSystem(paths), paths, new FixedTimeProvider(WindowsDiskFixture.CapturedAt))
            .CollectAsync(WindowsDiskFixture.CapturedAt.AddHours(-4), TestContext.Current.CancellationToken);
        return (health, runner, watched);
    }

    [Fact]
    public async Task The_full_run_reads_defender_after_the_disk_script_once_under_its_own_ceiling()
    {
        var (health, runner, _) = await CollectAsync(RecordingCommandRunner.Exited(0, Measured));

        Value(health.Defender.Read).Should().Be(new DefenderReading(993_513_593_750, 134_366_539_776_100_740, 6964, 134_365_236_258_808_755));
        Value(health.WindowsDisk.Facts).Volumes.IsAvailable.Should().BeTrue("the disk script answered");
        var argv = runner.Requests.Select(r => r.Argv).ToList();
        var diskAt = argv.FindIndex(a => a.SequenceEqual(WindowsDiskCommands.WindowsDisk.Argv));
        diskAt.Should().BeGreaterThanOrEqualTo(0);
        argv.FindIndex(a => a.SequenceEqual(DefenderCpuCommands.DefenderCpu.Argv)).Should().BeGreaterThan(diskAt, "a slow Defender read never costs the disk facts: the disk script is one sample and runs first");
        argv.Count(a => a.SequenceEqual(DefenderCpuCommands.DefenderCpu.Argv)).Should().Be(1, "once per full run");
        runner.Requests.Single(r => r.Argv.SequenceEqual(DefenderCpuCommands.DefenderCpu.Argv)).Timeout.Should().Be(TimeSpan.FromSeconds(15), "its own ceiling, health.defenderCpuTimeoutSeconds");
        health.Defender.SampledAt.Should().Be(WindowsDiskFixture.CapturedAt);
    }

    [Fact]
    public async Task A_stalled_defender_script_costs_only_itself_the_disk_facts_stay_and_its_child_is_killed()
    {
        var stalled = new CommandOutcome.TimedOut(CapturedText.Empty, CapturedText.Empty, TimeSpan.FromSeconds(15));

        var (health, _, watched) = await CollectAsync(stalled);

        health.Defender.Read.IsAvailable.Should().BeFalse();
        health.Defender.Read.ReasonOrEmpty.Should().Contain("did not answer within 15 s; its process tree was killed", "the Defender sample reports its own timeout");
        health.Defender.Elapsed.IsAvailable.Should().BeFalse("no run time is claimed for a script that did not answer");
        health.WindowsDisk.Facts.IsAvailable.Should().BeTrue("the disk script already answered and is not touched by the stall");
        Value(health.WindowsDisk.Facts).Volumes.IsAvailable.Should().BeTrue();
        watched.Killed.Should().ContainSingle().Which.Should().Equal(DefenderCpuCommands.DefenderCpu.Argv, "the runner's tree kill was told for the Defender script, and for nothing else");
    }

    [Fact]
    public async Task The_run_time_of_an_answering_script_is_kept()
    {
        var answered = new CommandOutcome.Exited(0, new CapturedText(Measured, false), CapturedText.Empty, TimeSpan.FromMilliseconds(1234));

        var (health, _, _) = await CollectAsync(answered);

        Value(health.Defender.Elapsed).Should().Be(TimeSpan.FromMilliseconds(1234));
    }

    public static TheoryData<string, CommandOutcome, string> FailedScripts() => new()
    {
        { "an exit code", RecordingCommandRunner.Exited(1, stderr: "Get-CimInstance : Access denied"), "exited 1: Get-CimInstance : Access denied" },
        { "no interop", new CommandOutcome.FailedToStart("no WSL interop"), "powershell.exe could not be started" },
        { "a refusal", new CommandOutcome.Refused("refused by the never-list"), "the command policy refused powershell.exe (the Defender CPU script)" },
        { "no MsMpEng", RecordingCommandRunner.Exited(0, "defender.none=\n"), "MsMpEng is not running" },
        { "a failed read", RecordingCommandRunner.Exited(0, "defender.error=CimException\n"), "CimException" },
        { "an empty answer", RecordingCommandRunner.Exited(0, string.Empty), "printed no defender line" },
        { "a cut answer", RecordingCommandRunner.Exited(0, "defender=993513593750|13436653977"), "does not parse" },
    };

    [Theory]
    [MemberData(nameof(FailedScripts))]
    public async Task A_defender_script_that_fails_leaves_only_the_defender_sample_unavailable_with_the_reason(string what, CommandOutcome outcome, string reason)
    {
        var runner = new RecordingCommandRunner().Script(DefenderCpuCommands.DefenderCpu.Argv, outcome);

        var sample = await HealthCollector.MeasureDefenderAsync(runner, new FixedTimeProvider(), TestContext.Current.CancellationToken);

        sample.Read.IsAvailable.Should().BeFalse(what);
        sample.Read.ReasonOrEmpty.Should().Contain(reason, what);
    }

    [Fact]
    public async Task The_windows_binary_runs_no_defender_script_and_says_the_distro_reads_it()
    {
        var paths = new WindowsHostPaths(WindowsEnvironment.Sandboxed(_root.Path));
        var runner = new RecordingCommandRunner();

        var health = await new HealthCollector(runner, new PhysicalFileSystem(paths), paths, new FixedTimeProvider()).CollectAsync(DateTimeOffset.UnixEpoch, TestContext.Current.CancellationToken);

        health.Defender.Read.ReasonOrEmpty.Should().Be(HealthCollector.DefenderCpuIsTheDistros);
        runner.Requests.Should().BeEmpty("the Windows binary starts no PowerShell for its health");
    }
}
