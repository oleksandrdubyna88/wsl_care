using System.Diagnostics;

using FluentAssertions;

using WslCare.Core.Collectors;
using WslCare.Core.Docker;
using WslCare.Core.Health;
using WslCare.Core.Processes;
using WslCare.Core.Systemd;

namespace WslCare.LiveContract;

/// <summary>
/// E2.S3's commands against the real tools of this machine, parsed by the product's parsers (plan §15b #6): the
/// health collectors' <c>systemctl</c> / <c>journalctl</c> / <c>timedatectl</c> / <c>snap</c> answers, the Windows clock
/// probe through WSL interop, and Docker's event stream — the follower's assumptions, observed rather than read
/// about: a past window answers and ends, and a FUTURE <c>--until</c> streams and then closes by itself.
/// </summary>
public sealed class HealthContractTests
{
    [Fact]
    public async Task Systemctl_lists_the_failed_units_as_json_the_parser_reads()
    {
        var units = Available(HealthParsers.FailedUnits(await Live.SystemdAsync(SystemdCommands.FailedUnits)));

        units.Should().NotContain(u => u.Unit.Length == 0);
    }

    [Fact]
    public async Task Journalctl_lists_the_boots_as_json_and_the_oldest_entry_is_in_the_past()
    {
        var oldest = Available(HealthParsers.OldestJournalEntry(await Live.SystemdAsync(SystemdCommands.ListBoots)));

        oldest.Should().BeBefore(DateTimeOffset.UtcNow).And.BeAfter(new DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.Zero));
    }

    [Theory]
    [InlineData("systemd-resolved", "Clock change detected")]
    [InlineData("", "page allocation failure|invoked oom-killer")]
    [InlineData("systemd-resolved", "wsl-care-live-contract-matches-nothing")]
    public async Task A_journal_search_answers_a_count_even_when_nothing_matched(string unit, string pattern)
    {
        await Live.SystemdAsync(SystemdCommands.JournalDiskUsage); // a host without systemd skips here
        JournalScope scope = unit.Length == 0 ? new JournalScope.Kernel() : new JournalScope.Unit(unit);
        var command = SystemdCommands.Search(DateTimeOffset.UtcNow.AddHours(-4), scope, pattern);

        var matches = Available(HealthParsers.SearchMatches(command, await Live.RunAsync(command)));

        matches.Should().NotBeNull();
        if (pattern.StartsWith("wsl-care", StringComparison.Ordinal))
        {
            matches.Should().BeEmpty("journalctl exits 1 with nothing printed, and that is a count of 0");
        }
    }

    [Fact]
    public async Task Timedatectl_says_whether_ntp_is_on_and_the_clock_synchronised()
    {
        await Live.SystemdAsync(SystemdCommands.JournalDiskUsage);
        var answer = ToolAnswers.Read(SystemdCommands.TimeSync, await Live.RunAsync(SystemdCommands.TimeSync));

        Available(answer.Bind(HealthParsers.TimeSync)).Should().NotBeNull();
    }

    [Fact]
    public async Task Systemctl_version_names_systemd_and_a_unit_carries_its_unit_file_state()
    {
        Available(HealthParsers.SystemdVersion(await Live.SystemdAsync(SystemdCommands.Version))).Should().StartWith("systemd ");
        var timer = Available(SystemdUnit.Parse(await Live.SystemdAsync(SystemdCommands.ShowUnit("fstrim.timer"))));
        Available(SystemdUnit.Parse(await Live.SystemdAsync(SystemdCommands.ShowUnit("wsl-pro.service")))).Id.Should().Be("wsl-pro.service");

        if (timer.Exists)
        {
            timer.UnitFileState.Should().NotBeEmpty("UnitFileState is one of the properties asked for");
        }
    }

    [Fact]
    public async Task Snap_list_all_reads_with_the_header_the_parser_skips()
    {
        var outcome = await Live.RunAsync(HealthCommands.SnapList);
        if (outcome is CommandOutcome.FailedToStart failed)
        {
            Live.Unavailable($"snap is not installed here: {failed.Reason}");
        }

        var stdout = Available(ToolAnswers.Read(HealthCommands.SnapList, outcome));

        stdout.Split('\n')[0].Should().StartWith("Name").And.Contain("Rev").And.Contain("Notes");
        HealthParsers.DisabledSnapRevisions(stdout).Should().NotContain(r => !r.Revision.All(char.IsAsciiDigit), "an empty list is the ordinary answer: no disabled revision");
    }

    [Fact]
    public async Task The_windows_clock_probe_prints_two_instants_and_the_profile_from_inside_the_distro()
    {
        if (OperatingSystem.IsWindows())
        {
            Live.Unavailable("the clock probe is the distro's (WSL interop to powershell.exe); this binary runs on Windows");
        }

        var mark = ClockMark.Now(TimeProvider.System);
        var launched = DateTimeOffset.UtcNow;
        var outcome = await Live.RunAsync(HealthCommands.WindowsClock);
        if (outcome is CommandOutcome.FailedToStart failed)
        {
            Live.Unavailable($"powershell.exe is not reachable (no WSL interop here): {failed.Reason}");
        }

        var probe = Available(ToolAnswers.Read(HealthCommands.WindowsClock, outcome).Bind(HealthParsers.WindowsClock));

        probe.Profile.Should().MatchRegex(@"^[A-Za-z]:\\");
        (probe.PrintedAt - probe.ProcessStartedAt).Should().BePositive("the launch latency is measured on one clock");
        Available(probe.TimeService).Status.Should().NotBeEmpty("the probe prints the Windows Time service's state (PLAN_windows_time_guard.md D1)");
        var skew = (probe.ProcessStartedAt - launched).TotalSeconds;
        if (Math.Abs(skew) >= 60)
        {
            // PLAN_windows_time_guard.md D8: a broken clock is REPORTED with its likely cause, in the product's own sentence.
            var sample = new Core.Records.WindowsClockSample(launched, skew, 0, string.Empty) { TimeService = Available(probe.TimeService) };
            var reference = await ClockReferences.MeasureAsync(Live.Commands, TimeProvider.System, mark, TestContext.Current.CancellationToken);
            Assert.Fail(ClockStandings.Diagnosis(sample, ClockStandings.Judge(sample, reference, ClockReferences.ToleranceSeconds)));
        }
    }

    /// <summary>E15.S1: the second fixed script through WSL interop, exactly as the full run starts it (through the product's
    /// policy, which admits it only as a member of the closed set), parsed by the product's parser: an end line, no malformed
    /// line, the system drive among the volumes, a machine id, and the reliability counters read or refused — never both.</summary>
    [Fact]
    public async Task The_windows_disk_script_answers_every_section_the_parser_reads_from_inside_the_distro()
    {
        if (OperatingSystem.IsWindows())
        {
            Live.Unavailable("the disk script is the distro's (WSL interop to powershell.exe); this binary runs on Windows");
        }

        var command = WindowsDiskCommands.WindowsDisk;
        var outcome = await Live.RunAsync(command);
        if (outcome is CommandOutcome.FailedToStart failed)
        {
            Live.Unavailable($"powershell.exe is not reachable (no WSL interop here): {failed.Reason}");
        }

        var facts = Available(ToolAnswers.Read(command, outcome).Bind(stdout => WindowsDiskParsers.Parse(stdout, WindowsDiskCommands.EventsMaxRead, WindowsDiskCommands.EventsSinceDays)));

        facts.MalformedLines.Should().Be(0, "every line the script prints is one the parser reads");
        Available(facts.Volumes).Should().Contain(v => v.Letter == "C", "the system drive is a lettered volume");
        Available(facts.PhysicalDisks).Should().NotBeEmpty();
        Available(facts.Events).Read.Should().BeLessThanOrEqualTo(WindowsDiskCommands.EventsMaxRead);
        Available(facts.Identity.MachineGuid).Should().NotBeEmpty();
        // E15.S1's plan round: interop runs the script as the Windows user who started the VM, so HKCU is THAT account's hive —
        // the account is a user SID, and the distro this test runs in is among the Lxss map's distros.
        Available(facts.Identity.Account).Should().StartWith("S-1-5-21-", "interop runs Windows programs as the signed-in user, not SYSTEM");
        if (Environment.GetEnvironmentVariable("WSL_DISTRO_NAME") is { Length: > 0 } distro)
        {
            Available(facts.Distros).Should().Contain(d => d.Name == distro, "the Lxss map read is the account that runs this distro");
        }
        Available(facts.CommitLimitBytes).Should().BePositive();
        (facts.Reliability.IsAvailable || facts.Reliability.ReasonOrEmpty.Contains("elevation", StringComparison.Ordinal)).Should().BeTrue(facts.Reliability.ReasonOrEmpty);
    }

    /// <summary>E15.S10: the third fixed script through WSL interop, exactly as the full run starts it (through the product's policy,
    /// which admits it only as a member of the closed set), parsed by the product's parser: Defender's engine answers one reading whose
    /// process id and start marker are positive, or says it is not running — never a malformed line. Two reads a moment apart are the
    /// SAME process, so the two-point rule does not call the second a restart.</summary>
    [Fact]
    public async Task The_defender_cpu_script_answers_one_reading_the_parser_reads_from_inside_the_distro()
    {
        if (OperatingSystem.IsWindows())
        {
            Live.Unavailable("the Defender script is the distro's (WSL interop to powershell.exe); this binary runs on Windows");
        }

        var command = DefenderCpuCommands.DefenderCpu;
        var first = await Live.RunAsync(command);
        if (first is CommandOutcome.FailedToStart failed)
        {
            Live.Unavailable($"powershell.exe is not reachable (no WSL interop here): {failed.Reason}");
        }

        var reading = ToolAnswers.Read(command, first).Bind(DefenderParsers.Parse);
        if (reading.ReasonOrEmpty == DefenderParsers.NotRunning)
        {
            Live.Unavailable("MsMpEng is not running on this machine (another product replaced Defender)");
        }

        var one = Available(reading);
        one.Pid.Should().BePositive();
        one.StartMarker.Should().BePositive("on the raw class ElapsedTime is the process start instant");
        var second = Available(ToolAnswers.Read(command, await Live.RunAsync(command)).Bind(DefenderParsers.Parse));
        DefenderCpu.Between(one, second).ReasonOrEmpty.Should().NotBe(DefenderCpu.Restarted, "two reads a moment apart are the same process");
    }

    [Fact]
    public async Task Timesyncd_status_answers_a_last_ntp_offset_the_parser_reads()
    {
        await Live.SystemdAsync(SystemdCommands.JournalDiskUsage);
        var answer = ToolAnswers.Read(SystemdCommands.TimesyncStatus, await Live.RunAsync(SystemdCommands.TimesyncStatus));
        if (answer is not Reading<string>.Available)
        {
            Live.Unavailable($"timesyncd does not answer here: {answer.ReasonOrEmpty}");
        }

        Available(answer.Bind(ClockParsers.Timesync)).Server.Should().NotBeEmpty();
    }

    [Fact]
    public async Task The_backward_jump_search_of_this_boot_carries_monotonic_stamps()
    {
        await Live.SystemdAsync(SystemdCommands.JournalDiskUsage);
        var command = SystemdCommands.TimeJumpsBackThisBoot;

        var lines = Available(HealthParsers.SearchMatches(command, await Live.RunAsync(command)));

        ClockParsers.MonotonicStamps(lines).Should().HaveCount(lines.Count, "every line journalctl --output=short-monotonic prints starts with its [ seconds ]");
    }

    [Fact]
    public async Task The_clock_reference_head_answers_one_date_near_this_clock()
    {
        var url = Core.Config.Tuning.Current.Config.Text(Core.Config.ConfigKeys.Clock.ReferenceUrl);
        var command = HealthCommands.ClockReference(url, HealthCommands.ReferenceSeconds);
        var outcome = await Live.RunAsync(command);
        if (outcome is CommandOutcome.FailedToStart failed)
        {
            Live.Unavailable($"curl is not installed here: {failed.Reason}");
        }

        var date = Available(ToolAnswers.Read(command, outcome).Bind(ClockParsers.HttpDate));

        Math.Abs((date - DateTimeOffset.UtcNow).TotalSeconds).Should().BeLessThan(ClockReferences.ToleranceSeconds, "{0}'s Date agrees with this clock within the tolerance — if not, one of them is wrong", url);
    }

    [Fact]
    public async Task Docker_events_of_a_past_window_answer_every_kind_of_event_with_its_time()
    {
        var now = DateTimeOffset.UtcNow;
        var events = Available(DockerEvents.Parse(await Live.DockerAsync(DockerCommands.Backfill(now.AddHours(-24), now))));

        events.Should().NotContain(e => e.At < now.AddHours(-24).AddSeconds(-1) || e.At > now.AddSeconds(1));
    }

    [Fact]
    public async Task Docker_events_with_a_future_until_stream_and_then_close_by_themselves_at_it()
    {
        await Live.DockerAsync(DockerCommands.Version); // no daemon skips here
        var since = DateTimeOffset.UtcNow.AddSeconds(-5);
        var until = DateTimeOffset.UtcNow.AddSeconds(4);
        var watch = Stopwatch.StartNew();

        var (outcome, lines) = await Live.StreamAsync(DockerCommands.EventStream(since, until, TimeSpan.FromSeconds(30)));

        outcome.Should().BeOfType<CommandOutcome.Exited>().Which.ExitCode.Should().Be(0, "Docker ends the stream at --until on its own");
        watch.Elapsed.Should().BeGreaterThan(TimeSpan.FromSeconds(2), "the stream stayed open until the future --until").And.BeLessThan(TimeSpan.FromSeconds(25));
        lines.Should().NotContain(l => !DockerEvents.Parse(l).IsAvailable, "no start may happen in the window, and that is fine");
    }

    private static T Available<T>(Reading<T> reading)
    {
        reading.Should().BeOfType<Reading<T>.Available>("the product's parser reads the real answer ({0})", reading.ReasonOrEmpty);
        return ((Reading<T>.Available)reading).Value;
    }
}
