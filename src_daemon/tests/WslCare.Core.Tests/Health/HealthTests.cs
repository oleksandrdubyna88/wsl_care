using FluentAssertions;

using WslCare.Core.Collectors;
using WslCare.Core.Files;
using WslCare.Core.Health;
using WslCare.Core.Hosting;
using WslCare.Core.Processes;
using WslCare.Core.Systemd;
using WslCare.TestSupport;

namespace WslCare.Core.Tests.Health;

/// <summary>The health collectors of plan §4.5: the parsers over the answers CAPTURED on 2026-10-02 (the
/// <c>fixtures/health</c> tree), and the collector over a Linux sandbox and the recorded runner — read verbs only.</summary>
public sealed class HealthTests : IDisposable
{
    private readonly TempRoot _root = new("health");

    public void Dispose() => _root.Dispose();

    private static T Value<T>(Reading<T> reading)
    {
        reading.Should().BeOfType<Reading<T>.Available>(reading.ReasonOrEmpty);
        return ((Reading<T>.Available)reading).Value;
    }

    [Fact]
    public void The_captured_answers_read_as_this_machine_was_that_afternoon()
    {
        Value(HealthParsers.FailedUnits(HealthFixture.Read("systemctl-failed.out"))).Should().Equal(new FailedUnit("getty@tty1.service", "Getty on tty1"));
        Value(HealthParsers.OldestJournalEntry(HealthFixture.Read("journalctl-list-boots.out"))).Should().Be(new DateTimeOffset(2026, 10, 1, 22, 10, 27, TimeSpan.Zero).AddTicks(1418300));
        Value(HealthParsers.TimeSync(HealthFixture.Read("timedatectl-show.out"))).Should().Be(new TimeSync(true, true));
        Value(HealthParsers.SystemdVersion(HealthFixture.Read("systemctl-version.out"))).Should().Be("systemd 255 (255.4-1ubuntu8.17)");
        Value(SystemdUnit.Parse(HealthFixture.Read("systemctl-show-wsl-pro.out"))).UnitFileState.Should().Be("enabled");
        Value(SystemdUnit.Parse(HealthFixture.Read("systemctl-show-fstrim.out"))).Should().Match<SystemdUnit>(u => u.UnitFileState == "enabled" && u.ActiveState == "inactive");
        HealthParsers.DisabledSnapRevisions(HealthFixture.Read("snap-list-all.out")).Should().BeEmpty();
        var clock = Value(HealthParsers.WindowsClock(HealthFixture.Read("powershell-clock.out")));
        clock.Profile.Should().Be(@"C:\Users\user");
        (clock.PrintedAt - clock.ProcessStartedAt).TotalSeconds.Should().BeApproximately(0.7509, 0.0001);
    }

    [Fact]
    public void A_journal_search_counts_its_lines_and_journalctls_exit_1_with_nothing_printed_is_zero()
    {
        var command = SystemdCommands.Search(HealthFixture.CapturedAt, new JournalScope.Unit("systemd-resolved"), "Clock change detected");

        Value(HealthParsers.SearchMatches(command, RecordingCommandRunner.Exited(0, HealthFixture.Read("journalctl-search-clock.out")))).Should().HaveCount(875);
        Value(HealthParsers.SearchMatches(command, RecordingCommandRunner.Exited(1))).Should().BeEmpty();
        HealthParsers.SearchMatches(command, RecordingCommandRunner.Exited(1, stderr: "Failed to compile pattern")).IsAvailable.Should().BeFalse("an error is not a zero");
    }

    [Fact]
    public void Snap_marks_superseded_revisions_disabled_and_the_parser_takes_only_those()
    {
        // SYNTHETIC: the capture had none disabled; the Notes column as `snap list --all` prints it.
        const string text = "Name    Version   Rev    Tracking       Publisher    Notes\ncore22  20260824  2955   latest/stable  canonical**  base\ncore22  20260701  2900   latest/stable  canonical**  base,disabled\nsnapd   2.76.3    27738  latest/stable  canonical**  snapd\n";

        HealthParsers.DisabledSnapRevisions(text).Should().Equal(new SnapRevision("core22", "2900"));
    }

    [Theory]
    [InlineData("/dev/sdd / ext4 rw,relatime,discard,errors=remount-ro,data=ordered 0 0\n", true)]
    [InlineData("/dev/sdd / ext4 rw,relatime,errors=remount-ro 0 0\n", false)]
    public void Discard_is_read_from_the_root_mount_options(string mounts, bool expected) =>
        Value(HealthParsers.RootHasDiscard(mounts)).Should().Be(expected);

    [Theory]
    [InlineData("", "/mnt/")]
    [InlineData("[boot]\nsystemd=true\n", "/mnt/")]
    [InlineData("[automount]\nroot = /win\n", "/win/")]
    public void The_automount_root_comes_from_wsl_conf_and_defaults_to_mnt(string wslConf, string expected) =>
        HealthParsers.AutomountRoot(wslConf).Should().Be(expected);

    [Fact]
    public void A_windows_profile_is_seen_through_the_automount_root()
    {
        Value(HealthCollector.InDistro(@"C:\Users\alice", "/mnt/")).Should().Be("/mnt/c/Users/alice");
        HealthCollector.InDistro(@"\\server\share", "/mnt/").IsAvailable.Should().BeFalse();
    }

    [Fact]
    public void Wslconfig_keys_are_read_from_either_section_and_the_harmful_ones_warn()
    {
        var settings = HealthParsers.WslConfig("[wsl2]\nmemory=36GB\n# a comment\nswap = 8GB\n[experimental]\nautoMemoryReclaim=gradual\nsparseVhd=true\n");

        settings.Should().Be(new WslConfigSettings("36GB", "8GB", "gradual", "true"));
    }

    [Fact]
    public async Task Over_the_captured_answers_every_part_is_read_with_read_verbs_only()
    {
        var paths = new LinuxHostPaths(LinuxEnvironment.Sandboxed(_root.Path));
        _root.File("proc/mounts", "/dev/sdd / ext4 rw,relatime,discard 0 0\n");
        _root.File("proc/uptime", "9253.22 145216.82\n");
        _root.File("mnt/c/Users/user/.wslconfig", "[wsl2]\nmemory=36GB\nsparseVhd=true\n");
        _root.File("var/log/sysstat/sa02", "x");
        var runner = HealthFixture.Script(new RecordingCommandRunner { Default = new CommandOutcome.FailedToStart("not scripted") })
            .Script(SystemdCommands.ShowUnit("earlyoom.service").Argv, 0, "Id=earlyoom.service\nLoadState=not-found\nActiveState=inactive\n")
            .Script(SystemdCommands.ShowUnit("systemd-oomd.service").Argv, 0, "Id=systemd-oomd.service\nLoadState=not-found\nActiveState=inactive\n");
        var clock = new FixedTimeProvider(HealthFixture.CapturedAt);

        var health = await new HealthCollector(runner, new PhysicalFileSystem(paths), paths, clock).CollectAsync(HealthFixture.CapturedAt.AddHours(-4), CancellationToken.None);

        Value(health.ClockJumps).Should().Be(875);
        Value(health.Kernel).Should().Match<KernelSignals>(k => k.AllocationFailures == 1 && k.OomKills == 0);
        Value(health.FailedUnits).Should().ContainSingle();
        Value(health.JournalBytes).Should().BePositive();
        Value(health.WslPro).UnitFileState.Should().Be("enabled");
        Value(health.RootDiscard).Should().BeTrue();
        Value(health.Uptime).TotalSeconds.Should().BeApproximately(9253.22, 0.01);
        Value(health.Sysstat).File.Should().EndWith("sa02");
        health.Atop.IsAvailable.Should().BeFalse("no atop file: not collecting");
        Value(health.OomDaemon).Should().BeFalse();
        health.WindowsClock.Measured.Should().BeTrue(health.WindowsClock.Unavailable);
        health.WindowsClock.OffsetSeconds.Should().BeApproximately(0.1867, 0.0001, "Windows' process start minus the instant this side launched it");
        health.WindowsClock.LaunchLatencySeconds.Should().BeApproximately(0.7509, 0.0001);
        Value(health.WindowsProfile).Should().Be(paths.DistroPath("/mnt/c/Users/user"));
        Value(health.WslConfig).Should().Match<WslConfigAudit>(a => a.Present && a.Settings.Memory == "36GB" && a.Warnings.Count == 1);
        runner.Requests.Select(r => r.Argv).Should().OnlyContain(a =>
            (a[0] == "systemctl" || a[0] == "journalctl" || a[0] == "timedatectl") ? SystemdCommands.IsReadVerb(a.Skip(1).ToList()) : a.SequenceEqual(HealthCommands.WindowsClock.Argv));
    }

    [Fact]
    public async Task On_the_windows_layout_the_distro_parts_name_the_linux_binary_and_wslconfig_is_read_directly()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "the Windows layout joins with backslashes: a file is found through it on Windows only (the win-x64 leg runs this)");
        var paths = new WindowsHostPaths(WindowsEnvironment.Sandboxed(_root.Path));
        _root.File("Users/me/.wslconfig", "[wsl2]\nmemory=40GB\n");
        var runner = new RecordingCommandRunner();

        var health = await new HealthCollector(runner, new PhysicalFileSystem(paths), paths, new FixedTimeProvider()).CollectAsync(FixedTimeProvider.DefaultNow, CancellationToken.None);

        health.FailedUnits.ReasonOrEmpty.Should().Be(HealthCollector.WindowsSide);
        health.WindowsClock.Unavailable.Should().Be(HealthCollector.WindowsIsTheReference);
        Value(health.WslConfig).Settings.Memory.Should().Be("40GB");
        runner.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task A_tool_that_is_not_installed_leaves_only_its_part_unavailable_with_the_reason()
    {
        var paths = new LinuxHostPaths(LinuxEnvironment.Sandboxed(_root.Path));
        var runner = new RecordingCommandRunner { Default = new CommandOutcome.FailedToStart("No such file or directory") };

        var health = await new HealthCollector(runner, new PhysicalFileSystem(paths), paths, new FixedTimeProvider()).CollectAsync(FixedTimeProvider.DefaultNow, CancellationToken.None);

        health.FailedUnits.ReasonOrEmpty.Should().Contain("systemctl could not be started");
        health.WindowsClock.Unavailable.Should().Contain("powershell.exe could not be started");
        health.WindowsProfile.IsAvailable.Should().BeFalse();
        health.RootDiscard.ReasonOrEmpty.Should().Contain("mounts", "a file part is read, and its own reason says why not");
    }
}
