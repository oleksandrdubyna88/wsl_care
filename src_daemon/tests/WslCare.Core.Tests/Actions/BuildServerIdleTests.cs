using System.Globalization;

using FluentAssertions;

using WslCare.Core.Actions;
using WslCare.Core.Actions.BuildServers;
using WslCare.Core.Actions.Suspects;
using WslCare.Core.Collectors;
using WslCare.Core.Collectors.Procfs;
using WslCare.Core.Config;
using WslCare.Core.Records;
using WslCare.TestSupport;

namespace WslCare.Core.Tests.Actions;

/// <summary>
/// E14 S3: <c>dotnet build-server shutdown</c> stops EVERY build server of the user at once, so A3's TIMER runs it only when
/// (as before) one is alive for <c>buildServers.idleHours</c> AND every one used no CPU for <c>buildServers.idleMinutes</c>,
/// measured by identity over the timer's CPU history (the one A18 and A19 use) — a server that worked within the window, or
/// has no history yet, holds the timer: a compile in flight is never cut. A button run is held by a build only, as before.
/// </summary>
public sealed class BuildServerIdleTests : IDisposable
{
    private const string Boot = "6d1c1c5e-0000-4000-8000-000000000001";
    private const long Start = 4000;
    private const string MsBuild = "/usr/share/dotnet/dotnet /usr/share/dotnet/sdk/10.0.100/MSBuild.dll /nodemode:1 /nodeReuse:true";

    private readonly UserWorld _world = new("a3-idle");
    private readonly ManualTimeProvider _clock = new(FixedTimeProvider.DefaultNow) { SteppedTimestamps = true };
    private readonly BuildServerShutdown _action = new();

    public BuildServerIdleTests()
    {
        _world.Sandbox.Write("/proc/sys/kernel/random/boot_id", Boot + "\n");
        _world.Tool("dotnet");
    }

    public void Dispose() => _world.Dispose();

    /// <summary>One process's <c>stat</c> and <c>status</c> as /proc answers them.</summary>
    private void Stat(int pid, long cpuTicks, int uid = 1000)
    {
        _world.Sandbox.Write($"/proc/{pid}/stat", string.Create(CultureInfo.InvariantCulture, $"{pid} (dotnet) S 1 {pid} {pid} 0 -1 0 0 0 0 0 {cpuTicks} 0 0 0 20 0 1 0 {Start} 0 0\n"));
        _world.Sandbox.Write($"/proc/{pid}/status", string.Create(CultureInfo.InvariantCulture, $"Name:\tdotnet\nState:\tS (sleeping)\nPPid:\t1\nUid:\t{uid}\t{uid}\t{uid}\t{uid}\nRssAnon:\t30000 kB\nRssShmem:\t0 kB\n"));
    }

    private static ProcessEntry Server(int pid, double ageHours = 6) =>
        UserWorld.Process(pid, MsBuild, family: BuildServerShutdown.Family, ageHours: ageHours) with { StartTicks = Reading.Of(Start) };

    /// <summary>What every timer run does before its pass: record the CPU history by identity.</summary>
    private void Record(IReadOnlyList<ProcessEntry> processes) =>
        AgentCpuHistory.Record(_world.Sandbox.Paths, _world.Sandbox.Files, processes, SampleTime.Of(_clock)).Should().BeEmpty();

    private async Task<(ActionPreview Preview, TriggerDecision Trigger)> Timer(IReadOnlyList<ProcessEntry> processes, string userConfig = "{}")
    {
        _world.Processes.Clear();
        _world.Processes.AddRange(processes);
        var context = _world.Context(RunTrigger.Timer, userConfig) with { Clock = _clock };
        var preview = await _action.PreviewAsync(context, _world.Commands(_action, context), CancellationToken.None);
        return (preview, _action.Trigger(preview, context.Config));
    }

    [Fact]
    public async Task The_timer_does_not_shut_build_servers_down_while_one_used_cpu_within_the_idle_window()
    {
        Stat(10, cpuTicks: 500);
        Stat(11, cpuTicks: 700);
        Record([Server(10), Server(11)]);
        _clock.Advance(TimeSpan.FromHours(2));
        Stat(11, cpuTicks: 760);
        Record([Server(10), Server(11)]);

        var (preview, trigger) = await Timer([Server(10), Server(11)]);

        preview.Facts[BuildServerShutdown.BusyServersFact].Should().Be(1, "pid 11 compiled within the last 60 minutes");
        trigger.Fired.Should().BeFalse("the shutdown stops every server, so one that works holds it");
        trigger.Reason.Should().Contain("buildServers.idleMinutes");
    }

    [Fact]
    public async Task The_timer_shuts_them_down_when_every_server_is_idle_for_the_window_and_one_is_old_enough()
    {
        Stat(10, cpuTicks: 500);
        Stat(11, cpuTicks: 700);
        Record([Server(10), Server(11)]);
        _clock.Advance(TimeSpan.FromHours(2));
        Record([Server(10), Server(11)]);

        var (_, oldAndIdle) = await Timer([Server(10), Server(11, ageHours: 1)]);
        var (_, youngAndIdle) = await Timer([Server(10, ageHours: 3), Server(11, ageHours: 1)]);

        oldAndIdle.Fired.Should().BeTrue("both idle for 2 h, and pid 10 is 6 h old (buildServers.idleHours = 4)");
        youngAndIdle.Fired.Should().BeFalse("the age rule still holds: none is 4 h old");
    }

    [Fact]
    public async Task A_server_with_no_history_holds_the_timer()
    {
        Stat(10, cpuTicks: 500);
        Stat(12, cpuTicks: 100);
        Record([Server(10)]);
        _clock.Advance(TimeSpan.FromHours(2));
        Record([Server(10)]);

        var (preview, trigger) = await Timer([Server(10), Server(12)]);

        preview.Facts[BuildServerShutdown.BusyServersFact].Should().Be(1, "pid 12 was never seen by a timer run: missing history is not idle");
        trigger.Fired.Should().BeFalse();
    }

    [Fact]
    public async Task On_the_four_hour_timer_the_idle_window_is_a_floor_a_burst_inside_the_interval_waits_for_a_later_pass()
    {
        // coai plan round 2026-10-08 (session b0d57159), finding 2: sightings are four hours apart, so a burst anywhere in the
        // interval restarts the idle clock at the sighting that sees it — conservative, never early.
        Stat(10, cpuTicks: 500);
        Record([Server(10)]);
        _clock.Advance(TimeSpan.FromHours(4));
        Stat(10, cpuTicks: 510);
        Record([Server(10)]);

        var (_, atTheBurst) = await Timer([Server(10)]);
        _clock.Advance(TimeSpan.FromHours(4));
        Record([Server(10)]);
        var (_, aPassLater) = await Timer([Server(10)]);

        atTheBurst.Fired.Should().BeFalse("its ticks moved since the previous sighting, whenever inside the 4 h that was");
        aPassLater.Fired.Should().BeTrue("unchanged across a whole interval: idle 4 h >= 60 min");
    }

    [Fact]
    public async Task A_longer_configured_idle_window_holds_the_timer_longer()
    {
        Stat(10, cpuTicks: 500);
        Record([Server(10)]);
        _clock.Advance(TimeSpan.FromHours(2));
        Record([Server(10)]);

        var (_, trigger) = await Timer([Server(10)], "{ \"buildServers\": { \"idleMinutes\": 180 } }");

        trigger.Fired.Should().BeFalse("2 h idle is under the configured 180 minutes");
        ConfigLoader.Load([(ConfigLoader.DefaultsFile, new Core.Files.FileReadResult.Content(ConfigLoader.EmbeddedDefaults()))]).Config
            .Int(ConfigKeys.BuildServers.IdleMinutes).Should().Be(60);
        ConfigKeys.BuildServers.IdleMinutes.Trust.Safe.Should().Be(SafeDirection.Higher, "a longer window stops less");
    }

    [Fact]
    public async Task A_button_run_is_not_held_by_idleness_only_by_a_build()
    {
        Stat(10, cpuTicks: 500);
        _world.Processes.Add(Server(10));
        var context = _world.Context(RunTrigger.Manual) with { Clock = _clock };

        var preview = await _action.PreviewAsync(context, _world.Commands(_action, context), CancellationToken.None);

        preview.Available.Should().BeTrue();
        preview.Refusal.Should().BeEmpty("no build is alive; idleness decides only WHEN the timer runs");
        preview.Skip.Should().BeEmpty();
        preview.Count.Should().Be(1);
    }

    [Fact]
    public void The_timer_records_build_servers_in_the_cpu_history()
    {
        Stat(10, cpuTicks: 500);
        Stat(13, cpuTicks: 500);

        Record([Server(10), UserWorld.Process(13, "node server.js", family: "node")]);

        AgentCpuHistory.Read(_world.Sandbox.Paths, _world.Sandbox.Files).Entries.Select(e => e.Pid).Should().Equal([10],
            "the build server is A3's evidence; a node process of no recorded family is not");
    }
}
