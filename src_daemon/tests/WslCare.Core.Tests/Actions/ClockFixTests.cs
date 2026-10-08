using System.Globalization;

using FluentAssertions;

using WslCare.Core.Actions;
using WslCare.Core.Actions.Clock;
using WslCare.Core.Collectors;
using WslCare.Core.Config;
using WslCare.Core.Health;
using WslCare.Core.Processes;
using WslCare.Core.Processes.Policy;
using WslCare.Core.Records;
using WslCare.TestSupport;

namespace WslCare.Core.Tests.Actions;

/// <summary>
/// A16 (E3.S3, plan §15 #10): the clock fix ONCE per detected drift — two observations (the last full run's and a live one)
/// at least 5 minutes apart, both above <c>clock.maxDriftSeconds</c>; skipped when timesyncd reports synchronised or the
/// live observation is within the limit; at most once an hour (a button too); <c>chronyc makestep</c> when chronyd runs,
/// else <c>hwclock -s</c>; the correction recorded so a drift it did not cure is not corrected every run, until a full run
/// sees the clock agree. The probe, timedatectl and the tools are a recording runner under the PRODUCT policy.
/// </summary>
public sealed class ClockFixTests : IDisposable
{
    private static readonly DateTimeOffset Now = FixedTimeProvider.DefaultNow;

    private readonly LinuxSandbox _sandbox = new("a16");
    private readonly ClockFix _action = new();
    private readonly RecordingCommandRunner _runner = new() { Policy = CommandPolicy.Product };
    private readonly List<ProcessEntry> _processes = [];
    private DateTimeOffset _now = Now;

    public void Dispose() => _sandbox.Dispose();

    /// <summary>The probe reports Windows <paramref name="offsetSeconds"/> ahead of the distro (latency 0.5 s, subtracted).</summary>
    private void WindowsAhead(double offsetSeconds)
    {
        var started = _now.AddSeconds(offsetSeconds);
        _runner.Script(HealthCommands.WindowsClock.Argv, 0, $"{Iso(started.AddSeconds(0.5))}\n{Iso(started)}\nC:\\Users\\me\n");
    }

    private void Synchronized(bool synchronized) =>
        _runner.Script(Systemd.SystemdCommands.TimeSync.Argv, 0, $"NTP=yes\nNTPSynchronized={(synchronized ? "yes" : "no")}\n");

    /// <summary>The HTTP reference answers a <c>Date</c> <paramref name="referenceMinusWslSeconds"/> (whole seconds; the product
    /// adds the half second the header truncates) after the distro's clock — read back from the product's own command for the
    /// default <c>clock.referenceUrl</c>, never retyped.</summary>
    private void ReferenceAhead(int referenceMinusWslSeconds) =>
        _runner.Script(
            HealthCommands.ClockReference(Tuning.Default.Config.Text(ConfigKeys.Clock.ReferenceUrl), Tuning.Default.Int(ConfigKeys.Clock.ReferenceTimeoutSeconds)).Argv,
            0,
            $"HTTP/2 200\r\ndate: {_now.AddSeconds(referenceMinusWslSeconds).UtcDateTime.ToString("r", CultureInfo.InvariantCulture)}\r\n\r\n");

    /// <summary>A full run recorded at <paramref name="at"/> that observed <paramref name="offsetSeconds"/>.</summary>
    private void FullRunObserved(DateTimeOffset at, double offsetSeconds) =>
        new RunRecordWriter(_sandbox.Paths, _sandbox.Files).Append(new RunRecord(1, RunId.New(at, 9), RunTrigger.Timer, at, at, RunOutcome.Completed, [], RunKind.Collect)
        {
            Slow = new SlowParts { WindowsClock = new WindowsClockSample(at, offsetSeconds, 0.5, string.Empty) },
        });

    private (ActionContext Context, ActionCommands Commands) For(RunTrigger trigger = RunTrigger.Timer)
    {
        var config = ConfigLoader.Load(_sandbox.Paths, _sandbox.Files).Config;
        var context = new ActionContext(_sandbox.Paths, _sandbox.Files, new FixedTimeProvider(_now), config, trigger, new TargetUserResult.None("not needed"))
        {
            Processes = _ => Reading.Of(UserWorld.Snapshot(_processes)),
        };
        return (context, new ActionCommands(_action, _runner, context.TargetUser, []));
    }

    private async Task<(ActionPreview Preview, TriggerDecision Decision)> PreviewAsync()
    {
        var (context, commands) = For();
        var preview = await _action.PreviewAsync(context, commands, CancellationToken.None);
        return (preview, _action.Trigger(preview, context.Config));
    }

    private static string Iso(DateTimeOffset at) => at.UtcDateTime.ToString("o", CultureInfo.InvariantCulture);

    [Fact]
    public async Task One_observation_above_the_limit_is_not_a_drift_two_five_minutes_apart_are()
    {
        Synchronized(false);
        WindowsAhead(12);
        ReferenceAhead(12);

        var single = await PreviewAsync();
        single.Preview.Skip.Should().BeEmpty();
        single.Decision.Should().Match<TriggerDecision>(d => !d.Fired && d.Reason.Contains("one observation is not enough"));

        FullRunObserved(Now.AddMinutes(-4), 11);
        (await PreviewAsync()).Decision.Fired.Should().BeFalse("4 minutes apart is not 5");

        FullRunObserved(Now.AddMinutes(-5), 11);
        (await PreviewAsync()).Decision.Should().Match<TriggerDecision>(d => d.Fired && d.Reason.Contains("+12.00 s"));
    }

    [Fact]
    public async Task A_synchronised_clock_or_one_within_the_limit_is_a_skip_for_every_trigger()
    {
        FullRunObserved(Now.AddHours(-4), 11);
        Synchronized(true);
        WindowsAhead(12);
        (await PreviewAsync()).Preview.Skip.Should().Contain("synchronised");

        Synchronized(false);
        WindowsAhead(3);
        (await PreviewAsync()).Preview.Skip.Should().Contain("agrees with Windows'").And.Contain("+3.00 s");
    }

    [Fact]
    public async Task The_run_steps_with_hwclock_records_the_correction_and_the_same_drift_is_not_corrected_again()
    {
        FullRunObserved(Now.AddHours(-4), 11);
        Synchronized(false);
        WindowsAhead(12);
        ReferenceAhead(12);
        var (context, commands) = For();

        var preview = await _action.PreviewAsync(context, commands, CancellationToken.None);
        preview.Skip.Should().BeEmpty("the reference agrees with Windows: the distro is the one 12 s behind, so the engine would run it");
        var run = await _action.RunAsync(context, preview, commands, CancellationToken.None);

        run.Succeeded.Should().BeTrue();
        _runner.Commands.Should().Contain("hwclock -s").And.NotContain(c => c.StartsWith("chronyc", StringComparison.Ordinal));
        ClockFix.Read(_sandbox.Paths, _sandbox.Files).Should().Match<ClockFixRecord>(r => r.CorrectedAt == Now && r.Tool == "hwclock -s" && r.OffsetSeconds == 12);
        run.Notes.Should().Contain(n => n.Contains("+12.00 s before"));

        // Two hours later the drift is still there (the step did not cure it) and the last full run, after the fix, saw it too.
        _now = Now.AddHours(2);
        FullRunObserved(Now.AddHours(1), 12);
        WindowsAhead(12);
        ReferenceAhead(12);
        (await PreviewAsync()).Decision.Should().Match<TriggerDecision>(d => !d.Fired && d.Reason.Contains("already corrected once"));

        // A full run that saw the clock agree closes the event: a NEW drift is corrected again.
        FullRunObserved(Now.AddHours(1.5), 1);
        FullRunObserved(Now.AddHours(1.9), 13);
        var again = await PreviewAsync();
        again.Preview.Skip.Should().BeEmpty();
        again.Decision.Fired.Should().BeTrue();
    }

    /// <summary>Code round (coai #12): a preview whose clocks agree asks no reference — no HEAD to the network for nothing.</summary>
    [Fact]
    public async Task Clocks_that_agree_ask_no_reference()
    {
        Synchronized(false);
        WindowsAhead(3);

        (await PreviewAsync()).Preview.Skip.Should().Contain("agrees with Windows'");
        _runner.Commands.Should().NotContain(c => c.StartsWith("curl", StringComparison.Ordinal)).And.NotContain(c => c.Contains("timesync-status", StringComparison.Ordinal));
    }

    /// <summary>Own code review #5 / coai #7: timesyncd synchronised while the REFERENCE says the distro is off — the skip
    /// keeps plan §15 #10 (a synchronised clock is not stepped) but never claims "not WSL's".</summary>
    [Fact]
    public async Task A_synchronised_skip_never_says_not_wsls_when_the_reference_says_the_distro_is_off()
    {
        FullRunObserved(Now.AddHours(-4), 600);
        Synchronized(true);
        WindowsAhead(600);
        ReferenceAhead(600);

        var skip = (await PreviewAsync()).Preview.Skip;

        skip.Should().Contain("synchronised").And.NotContain("not WSL's");
        skip.Should().Contain("the distro's clock is -600.5 s off");
    }

    /// <summary>The incident of 2026-10-08 (research/2026-10-08_windows_time_stopped.md): Windows 7 200 s slow on two
    /// observations, timesyncd NOT synchronised at the instant of the preview (the fight), and no independent reference
    /// answering. Stepping the distro to the host's clock would set it 2 h wrong, so A16 must not step — for a button too.</summary>
    [Fact]
    public async Task The_incident_shape_never_steps_the_distro_to_a_host_two_hours_slow_when_no_reference_answers()
    {
        FullRunObserved(Now.AddHours(-4), -7200.42);
        Synchronized(false);
        WindowsAhead(-7200.42);

        var (preview, decision) = await PreviewAsync();

        decision.Fired.Should().BeTrue("the drift is real on two observations — only the skip stands between it and hwclock -s");
        preview.Skip.Should().Contain("no independent reference", "an unverified host clock is never stepped to");
    }

    /// <summary>The same incident, timesyncd unsynchronised at the instant, but the HTTP reference answering and agreeing with
    /// the distro: the skip names WINDOWS as the wrong clock, slow by 2 h, and says what stepping would do.</summary>
    [Fact]
    public async Task The_incident_shape_with_a_reference_agreeing_with_the_distro_skips_naming_windows_as_two_hours_slow()
    {
        FullRunObserved(Now.AddHours(-4), -7200.42);
        Synchronized(false);
        WindowsAhead(-7200.42);
        ReferenceAhead(0);

        var (preview, decision) = await PreviewAsync();

        decision.Fired.Should().BeTrue();
        preview.Skip.Should().Contain("the Windows clock is 7200.9 s slow against https://www.microsoft.com (HTTP Date)")
            .And.Contain("stepping the distro to the host's clock would set it wrong").And.Contain("Start-Service w32time");
    }

    /// <summary>The consultant's case (plan round 1): Windows 12 s ahead, the distro EXACTLY on the reference — D3 reads
    /// <c>agree</c> at a 30 s tolerance, and a step would move the distro AWAY from true time. It must not step.</summary>
    [Fact]
    public async Task A_distro_on_the_reference_is_not_stepped_to_a_windows_twelve_seconds_ahead()
    {
        FullRunObserved(Now.AddHours(-4), 12);
        Synchronized(false);
        WindowsAhead(12);
        ReferenceAhead(0);

        (await PreviewAsync()).Preview.Skip.Should().Contain("would not bring the distro closer");
    }

    [Fact]
    public async Task With_timesyncd_synchronised_the_skip_names_the_windows_clock_as_the_wrong_one()
    {
        FullRunObserved(Now.AddHours(-4), -7200.42);
        Synchronized(true);
        WindowsAhead(-7200.42);

        (await PreviewAsync()).Preview.Skip.Should().Contain("the Windows clock is wrong, not WSL's");
    }

    [Fact]
    public async Task A_correction_less_than_an_hour_ago_refuses_a_button_too()
    {
        FullRunObserved(Now.AddHours(-4), 11);
        Synchronized(false);
        WindowsAhead(12);
        _sandbox.Write("/var/lib/wsl-care/clock-fix.json", $"{{\"schemaVersion\":1,\"correctedAt\":\"{Iso(Now.AddMinutes(-30))}\",\"offsetSeconds\":20,\"tool\":\"hwclock -s\"}}");
        var (context, commands) = For(RunTrigger.Manual);

        var preview = await _action.PreviewAsync(context, commands, CancellationToken.None);

        preview.Refusal.Should().Contain("less than 60 minutes ago", "clock.minimumGapMinutes, said as the number in force");
    }

    [Fact]
    public async Task With_chronyd_running_the_step_is_chronyc_makestep()
    {
        _processes.Add(UserWorld.Process(321, "/usr/sbin/chronyd -F 1", user: "_chrony") with { Name = "chronyd" });
        FullRunObserved(Now.AddHours(-4), 11);
        Synchronized(false);
        WindowsAhead(-15);
        ReferenceAhead(-15);
        var (context, commands) = For();

        var preview = await _action.PreviewAsync(context, commands, CancellationToken.None);
        preview.Skip.Should().BeEmpty("the reference agrees with Windows: the distro is the one behind");
        await _action.RunAsync(context, preview, commands, CancellationToken.None);

        preview.What.Should().StartWith("chronyc makestep");
        _runner.Commands.Should().Contain("chronyc makestep").And.NotContain("hwclock -s");
    }

    [Fact]
    public async Task An_unanswered_probe_is_unavailable_and_a_failed_step_records_nothing()
    {
        Synchronized(false);
        _runner.Script(HealthCommands.WindowsClock.Argv, new CommandOutcome.FailedToStart("no interop"));
        var (context, commands) = For();
        (await _action.PreviewAsync(context, commands, CancellationToken.None)).Available.Should().BeFalse();

        _runner.Script(["hwclock", "-s"], 1, string.Empty, "hwclock: Cannot access the Hardware Clock");
        var run = await _action.RunAsync(context, ActionPreview.Unavailable("x", "y"), commands, CancellationToken.None);
        run.Failure.Should().Contain("hwclock -s exited 1");
        ClockFix.Read(_sandbox.Paths, _sandbox.Files).Should().BeNull();
    }
}
