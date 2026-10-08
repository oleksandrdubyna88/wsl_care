using System.Globalization;
using System.Text;

using FluentAssertions;

using WslCare.Core.Collectors;
using WslCare.Core.Config;
using WslCare.Core.Doctor;
using WslCare.Core.Files;
using WslCare.Core.Health;
using WslCare.Core.Processes;
using WslCare.Core.Processes.Policy;
using WslCare.Core.Records;
using WslCare.Core.Status;
using WslCare.Core.Systemd;
using WslCare.Core.Thresholds;
using WslCare.TestSupport;

namespace WslCare.Core.Tests.Health;

/// <summary>
/// The Windows Time guard (PLAN_windows_time_guard.md D1–D5) over the answers captured on 2026-10-08
/// (<see cref="TimeGuardFixture"/>) and the incident's numbers (research/2026-10-08_windows_time_stopped.md): the probe's
/// tagged service lines, the reference's parsers, the closed verdict of which clock is wrong, the three verdicts, the clock
/// fight's monotonic window, and <c>doctor</c>'s two checks.
/// </summary>
public sealed class ClockGuardTests
{
    private static readonly DateTimeOffset Now = TimeGuardFixture.CapturedAt;

    private static EffectiveConfig Machine(string json)
    {
        var loaded = ConfigLoader.Load(
        [
            (ConfigLoader.DefaultsFile, new FileReadResult.Content(ConfigLoader.EmbeddedDefaults())),
            (new ConfigLayerFile(ConfigLayer.Machine, "/etc/wsl-care/config.json"), new FileReadResult.Content(Encoding.UTF8.GetBytes(json))),
        ]);
        loaded.Errors.Should().BeEmpty();
        return loaded.Config;
    }

    private static EffectiveConfig Defaults => Machine("{}");

    private static WindowsClockSample Windows(double offsetSeconds, WindowsTimeService? service = null) =>
        new(Now, offsetSeconds, 0.5, string.Empty) { TimeService = service };

    private static Reading<ClockReference> Reference(double referenceMinusWsl) => Reading.Of(new ClockReference("the reference", referenceMinusWsl, Now));

    private static ClockJudgement Judge(double w, double r) => ClockStandings.Judge(Windows(w), Reference(r), 30);

    // ---- D1: the probe's tagged lines ----

    [Fact]
    public void The_tagged_probe_answer_of_2026_10_08_reads_the_service_and_never_takes_a_tag_for_the_profile()
    {
        var answer = HealthParsers.WindowsClock(TimeGuardFixture.Read("powershell-clock.out"));

        var probe = answer.Should().BeOfType<Reading<WindowsClockAnswer>.Available>().Which.Value;
        probe.Profile.Should().Be(@"C:\Users\user");
        probe.TimeService.Should().BeOfType<Reading<WindowsTimeService>.Available>().Which.Value.Should().Be(new WindowsTimeService("Running", "Manual"));
    }

    [Fact]
    public void A_three_line_answer_from_before_the_tags_still_parses_and_says_the_service_was_not_printed()
    {
        var probe = HealthParsers.WindowsClock(HealthFixture.Read("powershell-clock.out")).Should().BeOfType<Reading<WindowsClockAnswer>.Available>().Which.Value;

        probe.Profile.Should().Be(@"C:\Users\user");
        probe.TimeService.ReasonOrEmpty.Should().Contain("printed no w32time line");
    }

    [Fact]
    public void An_empty_profile_does_not_shift_a_tag_into_its_place_and_an_empty_tag_is_an_empty_value()
    {
        HealthParsers.WindowsClock("2026-10-08T08:58:31Z\n2026-10-08T08:58:30Z\n\nw32time.status=\nw32time.startType=Manual\n")
            .IsAvailable.Should().BeFalse("two instants and no profile: the tags are not a profile");
        ClockParsers.TimeService(["w32time.status=", "w32time.startType=Manual"]).Should().BeOfType<Reading<WindowsTimeService>.Available>()
            .Which.Value.Should().Be(new WindowsTimeService(string.Empty, "Manual"));
    }

    [Fact]
    public void The_probe_script_still_passes_the_never_list_and_a_different_powershell_script_does_not()
    {
        NeverList.FirstBroken(HealthCommands.WindowsClock.Argv).Should().BeNull();
        NeverList.FirstBroken(["powershell.exe", "-NoProfile", "-NonInteractive", "-Command", "Start-Service w32time"])!.Id.Should().Be("powershell");
        HealthCommands.ClockScript.IndexOf("Get-Service", StringComparison.Ordinal).Should().BeGreaterThan(HealthCommands.ClockScript.IndexOf("StartTime", StringComparison.Ordinal),
            "both instants are taken BEFORE the service query, so the launch latency is unchanged");
    }

    // ---- D2: the reference's parsers ----

    [Theory]
    [InlineData("-18.401ms", -0.018401)]
    [InlineData("+2h 420.512ms", 7200.420512)]
    [InlineData("1min 3.5s", 63.5)]
    [InlineData("+335us", 0.000335)]
    [InlineData("-60.856ms", -0.060856)]
    public void A_systemd_time_span_reads_as_seconds(string text, double seconds) =>
        SystemdTimespan.Seconds(text).Should().BeOfType<Reading<double>.Available>().Which.Value.Should().BeApproximately(seconds, 1e-9);

    [Theory]
    [InlineData("")]
    [InlineData("soon")]
    [InlineData("12 parsecs")]
    [InlineData("ms")]
    public void Anything_else_is_not_a_time_span(string text) => SystemdTimespan.Seconds(text).IsAvailable.Should().BeFalse();

    [Fact]
    public void The_captured_timesync_status_reads_its_server_and_last_offset()
    {
        var sample = ClockParsers.Timesync(TimeGuardFixture.Read("timedatectl-timesync-status.out")).Should().BeOfType<Reading<TimesyncSample>.Available>().Which.Value;

        sample.Server.Should().Be("185.125.190.57 (ntp.ubuntu.com)");
        sample.OffsetSeconds.Should().BeApproximately(-0.060856, 1e-9);
    }

    /// <summary>Code round (coai, codex + gemini): a field printed twice must be an unavailable reading, never an exception
    /// that ends the health collection.</summary>
    [Fact]
    public void A_timesync_status_with_a_field_printed_twice_is_unavailable_not_an_exception()
    {
        var twice = ClockParsers.Timesync("       Server: a\n       Offset: -1ms\n       Offset: +2h\n");

        twice.IsAvailable.Should().BeFalse();
        twice.ReasonOrEmpty.Should().Contain("more than one Offset line");
    }

    /// <summary>Own code review #3: an EMPTY tag (Get-Service answered nothing) is not a fact about the service — the
    /// verdict is unknown, never "not running" nor "does not start Automatic".</summary>
    [Theory]
    [InlineData("", "Automatic", Level.Unknown)]
    [InlineData("", "", Level.Unknown)]
    [InlineData("Running", "", Level.Ok)]
    public void An_empty_service_tag_is_unknown_never_a_stopped_or_manual_service(string status, string startType, Level level)
    {
        var agree = new ClockJudgement(ClockStanding.Agree, 0, 0, false, "the reference", "agree");

        ClockVerdicts.TimeService(Reading.Of(new WindowsTimeService(status, startType)), agree, Defaults).Level.Should().Be(level, "an empty value is not a stopped service nor a manual start");
    }

    [Fact]
    public void The_captured_head_answer_reads_its_one_date_and_none_or_two_are_a_reason()
    {
        ClockParsers.HttpDate(TimeGuardFixture.Read("curl-head-microsoft.out")).Should().BeOfType<Reading<DateTimeOffset>.Available>()
            .Which.Value.Should().Be(new DateTimeOffset(2026, 10, 8, 8, 58, 11, TimeSpan.Zero));
        ClockParsers.HttpDate("HTTP/2 200\r\nserver: x\r\n").ReasonOrEmpty.Should().Contain("no Date header");
        ClockParsers.HttpDate("date: Thu, 08 Oct 2026 08:58:11 GMT\ndate: Thu, 08 Oct 2026 08:58:12 GMT\n").ReasonOrEmpty.Should().Contain("not one RFC 1123 instant");
        ClockParsers.HttpDate("date: yesterday\n").IsAvailable.Should().BeFalse();
    }

    // ---- D3: which clock is wrong ----

    [Theory]
    [InlineData(-30.1, 0, ClockStanding.WindowsSlow)]
    [InlineData(-30, 0, ClockStanding.Agree)]
    [InlineData(30.1, 0, ClockStanding.WindowsFast)]
    [InlineData(31, 31, ClockStanding.WslWrong)]
    [InlineData(12, 0, ClockStanding.Agree)]
    [InlineData(-7200.42, 0, ClockStanding.WindowsSlow)]
    [InlineData(0, 7200, ClockStanding.WindowsSlow)]
    public void The_standing_judges_windows_first_then_the_distro(double w, double r, ClockStanding expected) =>
        Judge(w, r).Standing.Should().Be(expected);

    [Fact]
    public void A_distro_dragged_to_the_wrong_host_time_is_named_as_off_too_never_as_right()
    {
        var both = Judge(0, 7200);

        both.DistroAlsoOff.Should().BeTrue();
        both.Reason.Should().Contain("the Windows clock is 7200.0 s slow").And.Contain("the distro's clock is off too").And.Contain("Start-Service w32time");
        Judge(-7200.42, 0).DistroAlsoOff.Should().BeFalse("the incident: the distro was on NTP time");
    }

    /// <summary>D8: the live contract's failure names the likely cause and the fix, in the product's own sentence — the
    /// incident's shape (the coordinator's request, 2026-10-08).</summary>
    [Fact]
    public void The_live_contracts_clock_failure_names_the_windows_time_service_and_the_fix()
    {
        var incident = Windows(-7200.42, new WindowsTimeService("Stopped", "Manual"));

        ClockStandings.Diagnosis(incident, ClockStandings.Judge(incident, Reference(0), 30)).Should()
            .Be("the Windows clock disagrees with the reference by -7200.4 s — is the Windows Time service running? (w32time: Stopped, StartType Manual) — fix: " + ClockStandings.WindowsFix);
        ClockStandings.Diagnosis(incident, ClockStandings.Judge(incident, Reading.Missing<ClockReference>("curl is not installed"), 30)).Should()
            .Contain("no reference can say which is wrong").And.Contain("is the Windows Time service running? (w32time: Stopped, StartType Manual)");
    }

    [Fact]
    public void No_observation_or_no_reference_is_unknown_with_the_reason()
    {
        ClockStandings.Judge(new WindowsClockSample(Now, 0, 0, "no interop"), Reference(0), 30).Should()
            .Match<ClockJudgement>(j => j.Standing == ClockStanding.Unknown && j.Reason.Contains("no interop"));
        ClockStandings.Judge(Windows(-7200), Reading.Missing<ClockReference>("curl is not installed"), 30).Reason.Should().Contain("curl is not installed");
    }

    // ---- D2: the measurement ----

    [Fact]
    public async Task The_http_reference_is_the_date_plus_its_half_second_against_the_request_midpoint()
    {
        var runner = new RecordingCommandRunner().Script(DefaultHead().Argv, 0, "HTTP/2 200\r\ndate: Thu, 08 Oct 2026 10:58:11 GMT\r\n");
        var clock = new FixedTimeProvider(new DateTimeOffset(2026, 10, 8, 8, 58, 11, TimeSpan.Zero));

        var reference = await ClockReferences.MeasureAsync(runner, clock, ClockMark.Now(clock), CancellationToken.None);

        reference.Should().BeOfType<Reading<ClockReference>.Available>().Which.Value.Should()
            .Match<ClockReference>(r => r.Source == "https://www.microsoft.com (HTTP Date)" && Math.Abs(r.ReferenceMinusWslSeconds - 7200.5) < 1e-6);
        runner.Commands.Should().ContainSingle(c => c.StartsWith("curl --disable", StringComparison.Ordinal), "--disable is FIRST: no root .curlrc");
    }

    [Fact]
    public async Task Without_the_http_reference_a_synchronised_timesyncd_with_a_small_last_offset_is_the_reference()
    {
        var runner = new RecordingCommandRunner()
            .Script(DefaultHead().Argv, new CommandOutcome.FailedToStart("curl is not installed"))
            .Script(SystemdCommands.TimeSync.Argv, 0, "NTP=yes\nNTPSynchronized=yes\n")
            .Script(SystemdCommands.TimesyncStatus.Argv, 0, TimeGuardFixture.Read("timedatectl-timesync-status.out"));
        var clock = new FixedTimeProvider(Now);

        var reference = await ClockReferences.MeasureAsync(runner, clock, ClockMark.Now(clock), CancellationToken.None);

        reference.Should().BeOfType<Reading<ClockReference>.Available>().Which.Value.Should()
            .Match<ClockReference>(r => r.ReferenceMinusWslSeconds == 0 && r.Source.Contains("timesyncd (185.125.190.57 (ntp.ubuntu.com), last NTP offset -0.061 s)"));
    }

    [Fact]
    public async Task A_large_last_ntp_offset_is_no_reference_and_names_the_fight()
    {
        var runner = new RecordingCommandRunner()
            .Script(DefaultHead().Argv, new CommandOutcome.FailedToStart("curl is not installed"))
            .Script(SystemdCommands.TimeSync.Argv, 0, "NTP=yes\nNTPSynchronized=yes\n")
            .Script(SystemdCommands.TimesyncStatus.Argv, 0, "       Server: 185.125.190.57 (ntp.ubuntu.com)\n       Offset: +2h 420.512ms\n");
        var clock = new FixedTimeProvider(Now);

        var reference = await ClockReferences.MeasureAsync(runner, clock, ClockMark.Now(clock), CancellationToken.None);

        reference.ReasonOrEmpty.Should().Contain("curl could not be started").And.Contain("+7200.4 s off").And.Contain("something else set it");
    }

    [Fact]
    public async Task A_wall_clock_that_jumps_during_the_measurement_voids_it()
    {
        var clock = new JumpingClock(Now, TimeSpan.FromSeconds(7200));
        var mark = ClockMark.Now(clock);
        var runner = new RecordingCommandRunner().Script(DefaultHead().Argv, 0, "date: Thu, 08 Oct 2026 08:58:11 GMT\n");

        var reference = await ClockReferences.MeasureAsync(runner, clock, mark, CancellationToken.None);

        reference.ReasonOrEmpty.Should().Contain("jumped by +7200.0 s during the measurement");
    }

    [Fact]
    public void The_reference_command_passes_the_product_policy_for_any_configured_address_and_timeout_and_nothing_else()
    {
        var policy = CommandPolicy.Product;
        policy.Review(new CommandRequest(HealthCommands.ClockReference("https://time.example:8443/a/b", 7).Argv, TimeSpan.FromSeconds(7))).IsAllowed.Should().BeTrue("after a config change the product's own curl still passes");
        foreach (var bad in new[] { "http://www.microsoft.com", "https://www.microsoft.com/?q=1", "https://a=b", "-K/etc/x", "https://x/#f", "https://x y" })
        {
            policy.Review(new CommandRequest(HealthCommands.ClockReference(bad, 10).Argv, TimeSpan.FromSeconds(10))).IsAllowed.Should().BeFalse(bad);
        }

        policy.Review(new CommandRequest(HealthCommands.ClockReference("https://www.microsoft.com", 31).Argv, TimeSpan.FromSeconds(31))).IsAllowed.Should().BeFalse("the timeout slot is the key's range");
        new TextRule.HttpsUrlOrEmpty().Problem("https://www.microsoft.com/?q=1").Should().NotBeEmpty("the key holds the same rule");
        new TextRule.HttpsUrlOrEmpty().Problem(string.Empty).Should().BeEmpty("empty turns the HTTP reference off");
    }

    // ---- D4: the clock fight, the three verdicts ----

    [Fact]
    public void The_captured_backward_jumps_count_in_a_monotonic_four_hour_window()
    {
        var lines = TimeGuardFixture.Read("journalctl-time-jumps.out").Split('\n', StringSplitOptions.RemoveEmptyEntries);

        ClockParsers.MonotonicStamps(lines).Should().HaveCount(28);
        ClockReferences.JumpsInWindow(lines, TimeSpan.FromSeconds(4270.67)).Should().Be(28, "71 minutes of uptime: every jump of this boot is in the last 4 h");
        ClockReferences.JumpsInWindow(lines, TimeSpan.FromSeconds(869.19 + (4 * 3600) - 300)).Should().Be(10, "the window starts at 569.19 s since boot");
    }

    [Theory]
    [InlineData("Stopped", "Manual", ClockStanding.Agree, Level.Warn)]
    [InlineData("Stopped", "Manual", ClockStanding.WindowsSlow, Level.Critical)]
    [InlineData("Stopped", "Disabled", ClockStanding.Unknown, Level.Warn)]
    [InlineData("Running", "Manual", ClockStanding.Agree, Level.Warn)]
    [InlineData("Running", "Automatic", ClockStanding.Agree, Level.Ok)]
    public void The_service_verdict_is_critical_only_while_the_windows_clock_is_actually_wrong(string status, string startType, ClockStanding standing, Level level)
    {
        var judgement = new ClockJudgement(standing, -7200, 0, false, "the reference", "the Windows clock is 7200.0 s slow against the reference");

        ClockVerdicts.TimeService(Reading.Of(new WindowsTimeService(status, startType)), judgement, Defaults).Level.Should().Be(level);
    }

    [Fact]
    public void A_manual_start_warns_only_while_clock_manual_start_warns_is_on()
    {
        var agree = new ClockJudgement(ClockStanding.Agree, 0, 0, false, "the reference", "agree");
        var service = Reading.Of(new WindowsTimeService("Running", "Manual"));

        ClockVerdicts.TimeService(service, agree, Defaults).Reason.Should().Contain("does NOT restart it after a stop");
        ClockVerdicts.TimeService(service, agree, Machine("""{"clock":{"manualStartWarns":false}}""")).Level.Should().Be(Level.Ok);
        ClockVerdicts.TimeService(Reading.Missing<WindowsTimeService>("printed no w32time line"), agree, Defaults).Level.Should().Be(Level.Unknown);
    }

    [Fact]
    public void The_reference_verdict_is_critical_for_windows_a_warning_for_the_distro()
    {
        ClockVerdicts.Reference(Judge(-7200.42, 0), Defaults).Should().Match<Verdict>(v => v.Level == Level.Critical && v.Value.StartsWith("windowsSlow: Windows -7200.4 s", StringComparison.Ordinal));
        ClockVerdicts.Reference(Judge(31, 31), Defaults).Level.Should().Be(Level.Warn);
        ClockVerdicts.Reference(Judge(1, 0), Defaults).Level.Should().Be(Level.Ok);
        ClockVerdicts.Reference(ClockStandings.Judge(Windows(0), Reading.Missing<ClockReference>("off"), 30), Defaults).Level.Should().Be(Level.Unknown);
    }

    [Fact]
    public void The_fight_warns_above_its_threshold_in_four_hours()
    {
        ClockVerdicts.Fight(Reading.Of(11)).Should().Match<Verdict>(v => v.Level == Level.Warn && v.Reason.Contains("two time-keepers disagree"));
        ClockVerdicts.Fight(Reading.Of(10)).Level.Should().Be(Level.Ok);
        ClockVerdicts.Fight(Reading.Missing<int>("journal unread")).Level.Should().Be(Level.Unknown);
    }

    // ---- D5: doctor ----

    [Fact]
    public void Doctor_reads_the_newest_full_runs_verdicts_critical_is_a_problem_a_warning_is_ok()
    {
        var run = new RecordedVerdicts(RunId.New(Now.AddHours(-1), 7), Now.AddHours(-1),
        [
            new Verdict(ClockVerdicts.TimeServiceId, Level.Warn, "Running, StartType Manual", "l", "does not start Automatic"),
            new Verdict(ClockVerdicts.ReferenceId, Level.Critical, "windowsSlow: Windows -7200.4 s", "l", "the Windows clock is 7200.4 s slow"),
        ]);

        var checks = ClockChecks.From(Reading.Of(run), Now);

        checks.Should().ContainSingle(c => c.Id == ClockChecks.WindowsTimeId).Which.Should().Match<DoctorCheck>(c => c.State == DoctorRun.Ok && c.Detail.StartsWith("warning:", StringComparison.Ordinal));
        checks.Should().ContainSingle(c => c.Id == ClockChecks.ClockReferenceId).Which.State.Should().Be(DoctorRun.Problem);
        ClockChecks.From(Reading.Missing<RecordedVerdicts>("no full run has run yet"), Now).Should().OnlyContain(c => c.State == DoctorRun.Unknown);
        ClockChecks.From(Reading.Of(run with { Verdicts = [] }), Now).Should().OnlyContain(c => c.State == DoctorRun.Unknown && c.Detail.Contains("older than the Windows Time guard"));
    }

    private static ToolCommand DefaultHead() =>
        HealthCommands.ClockReference(Tuning.Default.Config.Text(ConfigKeys.Clock.ReferenceUrl), Tuning.Default.Int(ConfigKeys.Clock.ReferenceTimeoutSeconds));

    /// <summary>A wall clock that jumps by <paramref name="jump"/> after its first reading, while the monotonic clock does not.</summary>
    private sealed class JumpingClock(DateTimeOffset start, TimeSpan jump) : TimeProvider
    {
        private int _reads;

        public override DateTimeOffset GetUtcNow() => Interlocked.Increment(ref _reads) == 1 ? start : start + jump;

        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
    }
}
