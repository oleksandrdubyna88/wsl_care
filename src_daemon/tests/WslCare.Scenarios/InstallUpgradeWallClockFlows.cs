using System.Runtime.Versioning;

using FluentAssertions;

using static WslCare.Scenarios.InstallChecks;
using static WslCare.Scenarios.InstallUpgradeFlows;

namespace WslCare.Scenarios;

/// <summary>The upgrade wait's flows that hold a budget on the REAL clock (E6.S1; the retro review of PR #11): the ceiling is
/// wall time, and a status call is cut at the wait's deadline and at its own setting. See <see cref="InstallUpgradeFlows"/>
/// for the rest of the upgrade, and <see cref="InstallFlows"/> for the harness.</summary>
/// <remarks>In <see cref="WallClock"/>, which runs alone (the retro round over PR #14, 2026-10-07): since the install classes
/// were split they ran beside each other, and the first flow's ~17 s against its 24 s bound was observed at 26.8 s and 34 s on
/// a loaded machine — the budget measured the suite, not the wait. No bound was loosened. Linux only, as every install flow.</remarks>
[SupportedOSPlatform("linux")]
[Collection(WallClock.Name)]
public sealed class InstallUpgradeWallClockFlows
{
    /// <summary>coai E6 code round #8: the ceiling is WALL time — counting the 5 s sleeps let a status that hangs 30 s per call
    /// stretch 600 s to about 70 minutes. Here every call takes 6 s: counted sleeps refuse after ~28 s, the wall clock at ~17 s.</summary>
    [Fact]
    public async Task The_wait_ceiling_is_measured_on_the_wall_clock_not_by_counting_sleeps()
    {
        Linux();
        using var world = new InstallWorld("upgrade-wall-clock") { RunWaitSeconds = "10" };
        world.Write(InstallWorld.BinaryPath, "#!/bin/sh\nsleep 6\n" + OldBinaryAnswering("live")["#!/bin/sh\n".Length..]);
        File.SetUnixFileMode(world.At(InstallWorld.BinaryPath), InstallWorld.Executable);
        world.Link(InstallWorld.LinkPath, InstallWorld.BinaryPath);

        var started = DateTime.UtcNow;
        var result = await world.RunAsync();

        FailedAt(result, "upgrade-wait");
        (DateTime.UtcNow - started).Should().BeLessThan(TimeSpan.FromSeconds(24), "the refusal comes at the advertised ceiling plus one status call and one sleep");
    }

    // ---------- the retro review of PR #11 (2026-10-06): the wait ends AT its deadline ----------

    /// <summary>An installed binary whose <c>status --json</c> takes <paramref name="seconds"/> seconds on the real clock, and
    /// first writes the instant it started (nanoseconds since the epoch, the real <c>date</c>) to <paramref name="stamp"/>.</summary>
    private static string SlowBinaryAnswering(string state, int seconds, string stamp) =>
        $"#!/bin/sh\ndate +%s%N > '{stamp}'\nsleep {seconds}\n" + OldBinaryAnswering(state)["#!/bin/sh\n".Length..];

    /// <summary>How long ago the last status call started, read from its stamp — the call's own life plus the refusal after it,
    /// without the install's prelude, so a loaded machine does not blur a difference of seconds.</summary>
    private static TimeSpan SinceTheLastStatusCallStarted(string stamp) =>
        DateTimeOffset.UtcNow - DateTimeOffset.FromUnixTimeMilliseconds(long.Parse(File.ReadAllText(stamp).Trim(), System.Globalization.CultureInfo.InvariantCulture) / 1_000_000);

    /// <summary>G3: a status call started just before the deadline ran its whole 30 s ceiling past it. Here every status takes
    /// 25 s and the wait is 8 s: the call is cut at the deadline (no answer, so in flight) and the refusal comes ~8 s after it
    /// started, not 25.</summary>
    [Fact]
    public async Task A_status_call_is_cut_at_the_waits_deadline()
    {
        Linux();
        using var world = new InstallWorld("upgrade-status-deadline") { RunWaitSeconds = "8" };
        var stamp = world.At("/status-call-started");
        Installed(world, SlowBinaryAnswering("live", 25, stamp), InstallWorld.Executable);

        var result = await world.RunAsync();

        FailedAt(result, "upgrade-wait");
        result.Stderr.Should().Contain("the installed binary gave no status answer");
        SinceTheLastStatusCallStarted(stamp).Should().BeLessThan(TimeSpan.FromSeconds(12), "the call ends at the wait's deadline, 8 s after it started — not after the binary's 25 s");
    }

    /// <summary>G1: the status call's ceiling was a bare 30 in the call. It is <c>WSL_CARE_INSTALL_STATUS_SECONDS</c> now, in
    /// force: 1 s here — below the 5 s floor a call near the deadline gets — against a status that takes 25 s.</summary>
    [Fact]
    public async Task The_status_call_ceiling_is_the_setting_in_force()
    {
        Linux();
        using var world = new InstallWorld("upgrade-status-setting") { RunWaitSeconds = "0" };
        world.Variables["WSL_CARE_INSTALL_STATUS_SECONDS"] = "1";
        var stamp = world.At("/status-call-started");
        Installed(world, SlowBinaryAnswering("live", 25, stamp), InstallWorld.Executable);

        var result = await world.RunAsync();

        FailedAt(result, "upgrade-wait");
        result.Stderr.Should().Contain("the installed binary gave no status answer");
        SinceTheLastStatusCallStarted(stamp).Should().BeLessThan(TimeSpan.FromSeconds(3.5), "the status call ends at its 1 s setting, not at the 5 s floor");
    }
}
