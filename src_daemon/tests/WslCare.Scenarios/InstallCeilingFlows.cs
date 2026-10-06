using System.Runtime.Versioning;

using FluentAssertions;

using WslCare.FakeTool;

using static WslCare.Scenarios.InstallChecks;

namespace WslCare.Scenarios;

/// <summary>
/// <c>install.sh</c>'s ceilings (the retro review of PR #8, 2026-10-06): every wait the installer starts ends on the WALL clock —
/// a systemctl call that waits for a job, the health wait, a child that ignores SIGTERM — and every ceiling it reads from the
/// environment is a whole number of seconds or a refusal before anything runs. Also the line that says the first full run may take
/// minutes, before it starts. See <see cref="InstallFlows"/> for the harness.
/// </summary>
/// <remarks>Linux only: the script is POSIX sh over GNU coreutils and tar, which the Linux CI legs have and the Windows leg does
/// not. The timed flows measure on the real clock from where their wait BEGINS (a stamp the stub binary writes at its first
/// <c>doctor --json</c>), never the whole run: on a machine at load 100 the install's prelude alone took tens of seconds.</remarks>
[SupportedOSPlatform("linux")]
public sealed class InstallCeilingFlows
{
    private const string SystemctlSeconds = "WSL_CARE_INSTALL_SYSTEMCTL_SECONDS";
    private const string DoctorSeconds = "WSL_CARE_INSTALL_DOCTOR_SECONDS";

    /// <summary>G2: on WSL Ubuntu sysstat.service is Type=oneshot with TimeoutStartUSec=infinity and JobTimeoutUSec=infinity, so
    /// <c>systemctl enable --now sysstat.service atop.service</c> had no ceiling. Here it never answers (45 s, standing for
    /// forever): the install fails at its ceiling, naming the step and saying the job may still be running.</summary>
    [Fact]
    public async Task A_systemctl_job_that_never_finishes_fails_the_install_at_its_ceiling_naming_the_step()
    {
        Linux();
        using var world = new InstallWorld("systemctl-hangs");
        world.Variables[SystemctlSeconds] = "8";
        world.Override(new FakeAnswer("systemctl", ["enable", "--now", "sysstat.service", "atop.service"], 0, string.Empty, string.Empty, DelayMilliseconds: 45_000));

        var result = await world.RunAsync();

        FailedAt(result, "enable-units");
        // Said only when `timeout` ended the call (124 / 137): the call was cut at its ceiling, not waited out — the run's wall
        // time is not asserted, because on a loaded machine the install's own prelude moves it by tens of seconds.
        result.Stderr.Should().Contain("systemctl enable --now sysstat.service atop.service did not finish within 8s")
            .And.Contain("the systemd job may still be running");
        world.CallsOf("wsl-care").Select(c => string.Join(' ', c.Argv)).Should().NotContain("collect", "the failed step stops the run");
    }

    /// <summary>G3: a full run takes minutes; the installer says so — with its ceiling — BEFORE <c>collect</c> starts, never after.
    /// The fake's stderr and the script's stdout share one pipe here, so their order is the order they were written.</summary>
    [Fact]
    public async Task The_first_run_is_announced_with_its_ceiling_before_collect_starts()
    {
        Linux();
        using var world = new InstallWorld("first-run-said");
        const string started = "fake wsl-care: collect has started";
        world.Override(new FakeAnswer("wsl-care", ["collect"], 0, string.Empty, started + "\n"));

        var result = await world.RunMergedAsync();

        result.Exit.Should().Be(0, result.Stdout);
        var lines = result.StdoutLines.ToList();
        var said = lines.FindIndex(l => l.StartsWith("wsl-care-install: recording the first full run", StringComparison.Ordinal));
        said.Should().BeGreaterThanOrEqualTo(0, $"the installer says the first run may take minutes:\n{result.Stdout}");
        lines[said].Should().Contain("may take several minutes").And.Contain("at most 900s");
        said.Should().BeLessThan(lines.IndexOf(started), "it is said before collect starts, not after it returns");
    }

    /// <summary>Consultant finding: the health wait counted only its 5 s sleeps while every <c>doctor --json</c> could take 120 s,
    /// so <c>WSL_CARE_INSTALL_DOCTOR_SECONDS</c> was a count of polls, not seconds. Here doctor answers unhealthy after 6 s and the
    /// ceiling is 12 s: counted sleeps refuse after four calls (~39 s), the wall clock after two (~17 s).</summary>
    [Fact]
    public async Task The_health_wait_refuses_at_its_wall_clock_deadline_not_after_counting_sleeps()
    {
        Linux();
        using var world = new InstallWorld("doctor-wall-clock");
        world.Variables[DoctorSeconds] = "12";
        world.Override(new FakeAnswer("wsl-care", ["doctor", "--json"], 0, world.Answer("unhealthy.json", InstallWorld.DoctorJson(healthy: false)), string.Empty, DelayMilliseconds: 6_000));
        var stamp = world.At("/first-doctor-call");
        world.StubPrelude = StampFirstDoctorCall(stamp);
        world.Publish(InstallWorld.NewestDaemon, "linux-x64");

        var result = await world.RunAsync();

        FailedAt(result, "verify: doctor healthy");
        result.Stderr.Should().Contain("(at most 12s)");
        world.CallsOf("wsl-care").Count(c => c.Argv is ["doctor", "--json"]).Should().Be(2, "one call, a sleep to the deadline, the last look at it");
        Since(stamp).Should().BeLessThan(TimeSpan.FromSeconds(28), "the refusal comes at the deadline plus one call — ~17 s after the wait began, not ~39");
    }

    /// <summary>Shell lines for the stub binary: the instant of the first <c>doctor --json</c> (nanoseconds, the real <c>date</c>)
    /// written to <paramref name="stamp"/> — where the health wait begins, so a timing is measured from there and not from an
    /// install whose prelude a loaded machine stretches by tens of seconds.</summary>
    private static string StampFirstDoctorCall(string stamp) =>
        "if [ \"$1 ${2-}\" = 'doctor --json' ] && [ ! -e '" + stamp + "' ]; then date +%s%N > '" + stamp + "'; fi\n";

    private static TimeSpan Since(string stamp) =>
        DateTimeOffset.UtcNow - DateTimeOffset.FromUnixTimeMilliseconds(long.Parse(File.ReadAllText(stamp).Trim(), System.Globalization.CultureInfo.InvariantCulture) / 1_000_000);

    /// <summary>Consultant finding: no <c>timeout</c> had <c>-k</c>, so a child that ignores SIGTERM outlived every ceiling. Here
    /// the installed binary's <c>doctor --json</c> ignores SIGTERM and sleeps 45 s: the call is SIGKILLed at its ceiling (the 10 s
    /// floor, the wait being 0) plus the 10 s grace. The same helper shape is on every other ceiling.</summary>
    [Fact]
    public async Task A_child_that_ignores_sigterm_is_killed_at_its_ceiling_plus_the_grace()
    {
        Linux();
        using var world = new InstallWorld("sigterm-ignored");
        var stamp = world.At("/first-doctor-call");
        world.StubPrelude = StampFirstDoctorCall(stamp) + "if [ \"$1 ${2-}\" = 'doctor --json' ]; then trap '' TERM; sleep 45; exit 0; fi\n";
        world.Publish(InstallWorld.NewestDaemon, "linux-x64");

        var result = await world.RunAsync();

        FailedAt(result, "verify: doctor healthy");
        Since(stamp).Should().BeLessThan(TimeSpan.FromSeconds(35), "SIGKILL follows the ignored SIGTERM after the grace — ~20 s after the call began, never the child's 45 s");
    }

    /// <summary>O5: only <c>WSL_CARE_INSTALL_DOCTOR_SECONDS</c> was validated. A RUN_WAIT that is not a number made the wait's
    /// <c>-ge</c> an error inside <c>if</c> — false for ever — so an upgrade waited without end; a systemctl ceiling of 0 means NO
    /// ceiling to <c>timeout</c>. Every ceiling variable is a whole number of seconds in its range, or a usage refusal (exit 2)
    /// naming it before anything runs.</summary>
    [Theory]
    [InlineData("WSL_CARE_INSTALL_RUN_WAIT_SECONDS", "ten")]
    [InlineData("WSL_CARE_INSTALL_RUN_WAIT_SECONDS", "99999999999999999999")]
    [InlineData("WSL_CARE_INSTALL_PROGRESS_SECONDS", "30s")]
    [InlineData("WSL_CARE_INSTALL_PROGRESS_SECONDS", "0")]
    [InlineData("WSL_CARE_INSTALL_DOCTOR_SECONDS", "2m")]
    [InlineData("WSL_CARE_INSTALL_DOCTOR_SECONDS", "010")]
    [InlineData("WSL_CARE_INSTALL_SYSTEMCTL_SECONDS", "abc")]
    [InlineData("WSL_CARE_INSTALL_SYSTEMCTL_SECONDS", "0")]
    [InlineData("WSL_CARE_INSTALL_STATUS_SECONDS", "x")]
    [InlineData("WSL_CARE_INSTALL_STATUS_SECONDS", "0")]
    public async Task A_ceiling_variable_that_is_not_a_whole_number_of_seconds_in_its_range_is_refused_before_anything_runs(string variable, string value)
    {
        Linux();
        using var world = new InstallWorld("ceiling-variable");
        world.Variables[variable] = value;
        var before = world.Tree();

        var result = await world.RunAsync();

        result.Exit.Should().Be(2, $"{variable}={value} is a usage error:\n{result.Stdout}\n{result.Stderr}");
        result.Stderr.Should().Contain(variable);
        world.Calls.Should().BeEmpty("refused before the script asks anything");
        NothingChanged(world, before);
    }
}
