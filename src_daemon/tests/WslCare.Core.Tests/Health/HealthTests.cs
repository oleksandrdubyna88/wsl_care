using FluentAssertions;

using WslCare.Core.Collectors;
using WslCare.Core.Files;
using WslCare.Core.Health;
using WslCare.Core.Hosting;
using WslCare.Core.Processes;
using WslCare.Core.Processes.Policy;
using WslCare.Core.Systemd;
using WslCare.TestSupport;

namespace WslCare.Core.Tests.Health;

/// <summary>The health collectors of plan §4.5: the parsers over the answers CAPTURED on 2026-10-02 (the
/// <c>fixtures/health</c> tree), and the collector over a Linux sandbox and the recorded runner — read verbs only.</summary>
public sealed class HealthTests : IDisposable
{
    private readonly TempRoot _root = new("health");

    public void Dispose() => _root.Dispose();

    /// <summary>Windows PowerShell 5.1, as the judgement answers it for a probe that verified nothing.</summary>
    private static readonly InteropEngine WindowsPowerShell = new(InteropEngineKind.WindowsPowerShell, HealthCommands.PowerShell, "a test that is not about the engine");

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
        health.JournalOldestEntry.IsAvailable.Should().BeTrue(health.JournalOldestEntry.ReasonOrEmpty);
        Value(health.JournalReadAt).Should().Be(HealthFixture.CapturedAt, "journal.history counts from the instant the journal was read (research/2026-10-12_wsl_clock_fight.md), not from the window start");
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
            (a[0] == "systemctl" || a[0] == "journalctl" || a[0] == "timedatectl") ? SystemdCommands.IsReadVerb(a.Skip(1).ToList())
            : a[0] == HealthCommands.Curl ? ReadCommandTemplates.ClockReference.Matches(a.Skip(1).ToList()) : HealthCommands.FixedScripts.Any(c => a.SequenceEqual(c.Argv)));
    }

    /// <summary>E15.S1: the full run reads the Windows disk facts through the second fixed script, AFTER the clock probe, and
    /// keeps what it said in the sample.</summary>
    [Fact]
    public async Task The_full_run_reads_the_windows_disk_facts_after_the_clock_probe()
    {
        var paths = new LinuxHostPaths(LinuxEnvironment.Sandboxed(_root.Path));
        var runner = HealthFixture.Script(new RecordingCommandRunner { Default = new CommandOutcome.FailedToStart("not scripted") })
            .Script(WindowsDiskCommands.WindowsDisk.Argv, 0, WindowsDiskFixture.Answer);

        var health = await new HealthCollector(runner, new PhysicalFileSystem(paths), paths, new FixedTimeProvider(WindowsDiskFixture.CapturedAt)).CollectAsync(WindowsDiskFixture.CapturedAt.AddHours(-4), CancellationToken.None);

        var facts = Value(health.WindowsDisk.Facts);
        Value(facts.Volumes).Should().HaveCount(4);
        Value(facts.Events).Read.Should().Be(63);
        health.WindowsDisk.SampledAt.Should().Be(WindowsDiskFixture.CapturedAt);
        var argv = runner.Requests.Select(r => r.Argv).ToList();
        var clockAt = argv.FindIndex(a => a.SequenceEqual(HealthCommands.WindowsClock.Argv));
        clockAt.Should().BeGreaterThanOrEqualTo(0, "the clock probe ran, so the order below compares two real positions");
        argv.FindIndex(a => a.SequenceEqual(WindowsDiskCommands.WindowsDisk.Argv)).Should().BeGreaterThan(clockAt, "the disk script runs after the clock probe");
        argv.Count(a => a.SequenceEqual(WindowsDiskCommands.WindowsDisk.Argv)).Should().Be(1, "once per full run");
        runner.Requests.Single(r => r.Argv.SequenceEqual(WindowsDiskCommands.WindowsDisk.Argv)).Timeout.Should().Be(TimeSpan.FromSeconds(30), "its own ceiling, health.windowsDiskTimeoutSeconds");
    }

    [Fact]
    public async Task The_windows_disk_script_s_run_time_is_kept_and_its_answer_parsed()
    {
        var runner = new RecordingCommandRunner().Script(WindowsDiskCommands.WindowsDisk.Argv,
            new CommandOutcome.Exited(0, new CapturedText(WindowsDiskFixture.Answer, false), CapturedText.Empty, TimeSpan.FromMilliseconds(3316)));

        var sample = await HealthCollector.MeasureWindowsDiskAsync(runner, new FixedTimeProvider(WindowsDiskFixture.CapturedAt), WindowsPowerShell, TestContext.Current.CancellationToken);

        Value(sample.Elapsed).Should().Be(TimeSpan.FromMilliseconds(3316));
        Value(sample.Facts).MalformedLines.Should().Be(0);
    }

    /// <summary>E15.S1b S4: a full run whose clock probe VERIFIED the MSI pwsh (the capture's answer with the two verdict lines a
    /// machine with the MSI build prints, derived) starts the disk script ONCE under the bare <c>pwsh.exe</c>, never under
    /// <c>powershell.exe</c>, after the clock probe — and the sample names the file it judged.</summary>
    [Fact]
    public async Task A_verified_probe_starts_the_disk_script_under_pwsh_and_the_sample_names_its_path()
    {
        var (health, argv) = await CollectWithClockAsync(RecordingCommandRunner.Exited(0, PwshEngineFixture.Verified), WindowsDiskCommands.WindowsDiskUnderPwsh);

        argv.Count(a => a.SequenceEqual(WindowsDiskCommands.WindowsDiskUnderPwsh.Argv)).Should().Be(1, "once per full run, under pwsh");
        argv.Should().NotContain(a => a.SequenceEqual(WindowsDiskCommands.WindowsDisk.Argv), "never also under powershell.exe");
        argv.FindIndex(a => a.SequenceEqual(WindowsDiskCommands.WindowsDiskUnderPwsh.Argv)).Should().BeGreaterThan(argv.FindIndex(a => a.SequenceEqual(HealthCommands.WindowsClock.Argv)));
        var engine = Value(health.WindowsDisk.Engine);
        engine.Kind.Should().Be(InteropEngineKind.Msi);
        engine.Program.Should().Be("/mnt/c/Program Files/PowerShell/7/pwsh.exe");
        Value(health.WindowsDisk.Facts).MalformedLines.Should().Be(0, "the scripted answer was read");
    }

    [Fact]
    public async Task The_captured_probe_without_an_msi_pwsh_starts_the_disk_script_under_powershell_with_the_reason()
    {
        var (health, argv) = await CollectWithClockAsync(RecordingCommandRunner.Exited(0, PwshEngineFixture.Answer), WindowsDiskCommands.WindowsDisk);

        argv.Count(a => a.SequenceEqual(WindowsDiskCommands.WindowsDisk.Argv)).Should().Be(1);
        argv.Should().NotContain(a => a[0] == HealthCommands.Pwsh, "the probe saw no MSI pwsh");
        var engine = Value(health.WindowsDisk.Engine);
        engine.Should().Match<InteropEngine>(e => e.Kind == InteropEngineKind.WindowsPowerShell && e.Program == HealthCommands.PowerShell);
        engine.Reason.Should().Contain("absent");
    }

    [Fact]
    public async Task A_failed_clock_probe_still_runs_the_disk_script_under_powershell_and_says_pwsh_was_not_verified()
    {
        var (health, argv) = await CollectWithClockAsync(RecordingCommandRunner.Exited(1, stderr: "interop is off"), WindowsDiskCommands.WindowsDisk);

        argv.Count(a => a.SequenceEqual(WindowsDiskCommands.WindowsDisk.Argv)).Should().Be(1, "one failure never ends a run (plan §5)");
        Value(health.WindowsDisk.Engine).Reason.Should().Contain("did not verify");
    }

    [Fact]
    public async Task The_msi_pwsh_is_named_under_the_automount_root_wsl_conf_sets()
    {
        _root.File("etc/wsl.conf", "[automount]\nroot = /win/\n");

        var (health, _) = await CollectWithClockAsync(RecordingCommandRunner.Exited(0, PwshEngineFixture.Verified), WindowsDiskCommands.WindowsDiskUnderPwsh);

        Value(health.WindowsDisk.Engine).Program.Should().Be("/win/c/Program Files/PowerShell/7/pwsh.exe");
    }

    /// <summary>A full run over the sandbox whose clock probe answers <paramref name="clock"/> and whose <paramref name="disk"/>
    /// command answers the captured disk facts; every argv asked for, in order.</summary>
    private async Task<(HealthSample Health, List<IReadOnlyList<string>> Argv)> CollectWithClockAsync(CommandOutcome clock, ToolCommand disk)
    {
        var paths = new LinuxHostPaths(LinuxEnvironment.Sandboxed(_root.Path));
        var runner = HealthFixture.Script(new RecordingCommandRunner { Default = new CommandOutcome.FailedToStart("not scripted") })
            .Script(HealthCommands.WindowsClock.Argv, clock)
            .Script(disk.Argv, 0, WindowsDiskFixture.Answer);

        var health = await new HealthCollector(runner, new PhysicalFileSystem(paths), paths, new FixedTimeProvider(PwshEngineFixture.CapturedAt)).CollectAsync(PwshEngineFixture.CapturedAt.AddHours(-4), TestContext.Current.CancellationToken);

        return (health, [.. runner.Requests.Select(r => r.Argv)]);
    }

    public static TheoryData<string, CommandOutcome, string> FailedDiskScripts() => new()
    {
        { "a timeout", new CommandOutcome.TimedOut(new CapturedText("distro=1|0|", false), CapturedText.Empty, TimeSpan.FromSeconds(30)), "did not answer within 30 s; its process tree was killed" },
        { "an exit code", RecordingCommandRunner.Exited(1, stderr: "Get-WinEvent : access denied"), "exited 1: Get-WinEvent : access denied" },
        { "a cut answer", RecordingCommandRunner.Exited(0, "distro=1|0|2026-10-10T18:29:09Z|C:\\x\\ext4.vhdx|Ubuntu\nvolume=C|0|NTFS|10|5|\n"), "cut" },
        { "no interop", new CommandOutcome.FailedToStart("no WSL interop"), "powershell.exe could not be started" },
        { "a refusal", new CommandOutcome.Refused("refused by the never-list"), "the command policy refused powershell.exe (the Windows disk script)" },
    };

    [Theory]
    [MemberData(nameof(FailedDiskScripts))]
    public async Task A_windows_disk_script_that_fails_leaves_the_whole_sample_unavailable_with_the_reason(string what, CommandOutcome outcome, string reason)
    {
        var runner = new RecordingCommandRunner().Script(WindowsDiskCommands.WindowsDisk.Argv, outcome);

        var sample = await HealthCollector.MeasureWindowsDiskAsync(runner, new FixedTimeProvider(), WindowsPowerShell, TestContext.Current.CancellationToken);

        sample.Facts.IsAvailable.Should().BeFalse(what);
        sample.Facts.ReasonOrEmpty.Should().Contain(reason, what);
        sample.Elapsed.IsAvailable.Should().BeFalse("no run time is claimed for {0}", what);
    }

    /// <summary>
    /// PR #10 retro round (consultation b41d9220, accepted): the system-drive lookup runs inside the launcher BEFORE the start
    /// and may take up to its ceiling; counted from an instant taken before the call, that time read as clock drift — and A16
    /// acts on drift. The offset is Windows' process start minus the instant the launcher STARTED it. One function serves the
    /// full run and A16 (<see cref="HealthCollector.MeasureWindowsClockAsync"/>), so this holds both.
    /// </summary>
    [Fact]
    public async Task A_slow_executable_lookup_before_the_start_is_not_counted_as_clock_drift()
    {
        var clock = new ManualTimeProvider(HealthFixture.CapturedAt);
        var lookup = TimeSpan.FromSeconds(4);
        var drift = TimeSpan.FromMilliseconds(250);
        var runner = new RecordingCommandRunner().ScriptEffect(a => a.SequenceEqual(HealthCommands.WindowsClock.Argv), _ =>
        {
            clock.Advance(lookup);
            var startedAt = clock.GetUtcNow();
            var windowsStart = startedAt + drift;
            var printed = $"{(windowsStart + TimeSpan.FromSeconds(0.75)):O}\n{windowsStart:O}\nC:\\Users\\user\n";
            return new CommandOutcome.Exited(0, new CapturedText(printed, false), CapturedText.Empty, TimeSpan.FromSeconds(1)) { StartedAt = Reading.Of(startedAt) };
        });

        var sample = await HealthCollector.MeasureWindowsClockAsync(runner, clock, TestContext.Current.CancellationToken);

        sample.Measured.Should().BeTrue(sample.Unavailable);
        sample.OffsetSeconds.Should().BeApproximately(drift.TotalSeconds, 0.0001, "the {0} s lookup happened before the start and is not drift", lookup.TotalSeconds);
        sample.LaunchLatencySeconds.Should().BeApproximately(0.75, 0.0001);
    }

    /// <summary>A runner that does not stamp its start (every scripted one) is measured from the instant before the call, as before.</summary>
    [Fact]
    public async Task A_runner_that_does_not_stamp_its_start_is_measured_from_the_instant_before_the_call()
    {
        var clock = new FixedTimeProvider(HealthFixture.CapturedAt);
        var windowsStart = HealthFixture.CapturedAt + TimeSpan.FromSeconds(2);
        var runner = new RecordingCommandRunner().Script(HealthCommands.WindowsClock.Argv, 0, $"{windowsStart:O}\n{windowsStart:O}\nC:\\Users\\user\n");

        var sample = await HealthCollector.MeasureWindowsClockAsync(runner, clock, TestContext.Current.CancellationToken);

        sample.OffsetSeconds.Should().BeApproximately(2, 0.0001);
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
        health.WindowsDisk.Facts.ReasonOrEmpty.Should().Be(HealthCollector.WindowsDiskIsTheDistros, "the Windows binary never runs the disk script (E15.S1)");
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
        health.WindowsDisk.Facts.ReasonOrEmpty.Should().Contain("powershell.exe could not be started");
        health.RootDiscard.ReasonOrEmpty.Should().Contain("mounts", "a file part is read, and its own reason says why not");
    }
}
