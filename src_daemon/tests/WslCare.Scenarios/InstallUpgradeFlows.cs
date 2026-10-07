using System.Runtime.Versioning;

using FluentAssertions;

using WslCare.TestSupport;

using static WslCare.Scenarios.InstallChecks;

namespace WslCare.Scenarios;

/// <summary><c>install.sh</c> over an installed daemon (E4.S1, E6.S1 — plan §15k #16): the follower restarted onto the new
/// binary, the binary renamed over the old one in one step, and never under a run in flight. See
/// <see cref="InstallFlows"/> for the harness.</summary>
/// <remarks>Linux only: the script is POSIX sh over GNU coreutils and tar, which the Linux CI legs have and the Windows
/// leg does not. Run by hand in WSL from a copy of the worktree under <c>/tmp</c>, as the test user — never as root, so a
/// path that escaped the prefix would be refused by the operating system.</remarks>
[SupportedOSPlatform("linux")]
public sealed class InstallUpgradeFlows
{
    [Fact]
    public async Task An_upgrade_restarts_the_running_follower_onto_the_new_binary()
    {
        Linux();
        using var world = new InstallWorld("upgrade");
        // Executable, as an installed binary is: one that cannot be started is no answer, and the wait refuses (retro of PR #11).
        // Its "old" is an answer with no running block — a binary older than E6.S0 — so nothing is in flight.
        Installed(world, "#!/bin/sh\necho old\n", InstallWorld.Executable);
        world.Override("systemctl", ["try-restart", "wsl-care-events.service"], 0);

        Succeeded(await world.RunAsync());

        File.ReadAllText(world.At(InstallWorld.BinaryPath)).Should().Be(world.StubScript());
        world.CallsOf("systemctl").Select(c => string.Join(' ', c.Argv)).Should().ContainInOrder(
            "daemon-reload", "try-restart wsl-care-events.service", string.Join(' ', EnableOurUnits));
    }

    // ---------- E6.S1: never under a run in flight (plan §15k #16) ----------

    /// <summary>The installed binary's <c>status --json</c>, as far as the installer reads it: the running block's state.</summary>
    private static string OldBinaryAnswering(string state) =>
        $$"""
        #!/bin/sh
        cat <<'EOF'
        {
          "schemaVersion": 1,
          "running": {
            "state": "{{state}}",
            "reason": "a test",
            "runId": "20261004T120000Z-4242"
          }
        }
        EOF

        """;

    [Theory]
    [InlineData("live")]
    [InlineData("queued")]
    [InlineData("wedged")]
    public async Task An_upgrade_under_a_run_in_flight_waits_bounded_then_refuses_naming_it_and_replaces_nothing(string state)
    {
        Linux();
        using var world = new InstallWorld($"upgrade-wait-{state}") { RunWaitSeconds = "0" };
        world.Write(InstallWorld.BinaryPath, OldBinaryAnswering(state));
        File.SetUnixFileMode(world.At(InstallWorld.BinaryPath), InstallWorld.Executable);
        world.Link(InstallWorld.LinkPath, InstallWorld.BinaryPath);
        var before = File.ReadAllText(world.At(InstallWorld.BinaryPath));

        var result = await world.RunAsync();

        FailedAt(result, "upgrade-wait");
        result.Stderr.Should().Contain($"a wsl-care run is {state}").And.Contain("still after").And.Contain("nothing was replaced")
            .And.NotContain("SKIP_RUN_WAIT", "the manual escape is only for a binary that cannot answer");
        File.ReadAllText(world.At(InstallWorld.BinaryPath)).Should().Be(before, "the running binary stays");
        File.Exists(world.At(InstallWorld.BinaryPath + ".new")).Should().BeFalse();
        world.CallsOf("systemctl").Should().BeEmpty("no unit was touched");
    }

    /// <summary>E6.S1 review S4: the wait failed OPEN — a status that crashed, timed out or printed nothing read as "nothing in
    /// flight" and the upgrade went ahead under whatever was running. No answer counts as in flight now; only an answer without a
    /// running block (a binary older than E6.S0) proceeds.</summary>
    [Theory]
    [InlineData("#!/bin/sh\nexit 70\n")]
    [InlineData("#!/bin/sh\nexit 0\n")]
    public async Task An_upgrade_whose_installed_binary_gives_no_status_answer_waits_then_refuses(string oldBinary)
    {
        Linux();
        using var world = new InstallWorld("upgrade-no-answer") { RunWaitSeconds = "0" };
        world.Write(InstallWorld.BinaryPath, oldBinary);
        File.SetUnixFileMode(world.At(InstallWorld.BinaryPath), InstallWorld.Executable);
        world.Link(InstallWorld.LinkPath, InstallWorld.BinaryPath);

        var result = await world.RunAsync();

        FailedAt(result, "upgrade-wait");
        result.Stderr.Should().Contain("the installed binary gave no status answer");
        // coai E6 code round #5: the binary cannot answer, so advice that runs it again is useless — the manual escape is named.
        result.Stderr.Should().Contain("remove /var/lib/wsl-care/running.json and /var/lib/wsl-care/requests/*.json by hand")
            .And.Contain("WSL_CARE_INSTALL_SKIP_RUN_WAIT=1").And.NotContain("sudo wsl-care collect");
        File.ReadAllText(world.At(InstallWorld.BinaryPath)).Should().Be(oldBinary);
    }

    [Fact]
    public async Task The_escape_skips_the_wait_for_an_installed_binary_that_cannot_answer_and_says_so()
    {
        Linux();
        using var world = new InstallWorld("upgrade-skip-wait") { RunWaitSeconds = "0", SkipRunWait = true };
        world.Write(InstallWorld.BinaryPath, "#!/bin/sh\nexit 70\n");
        File.SetUnixFileMode(world.At(InstallWorld.BinaryPath), InstallWorld.Executable);
        world.Link(InstallWorld.LinkPath, InstallWorld.BinaryPath);
        world.Override("systemctl", ["try-restart", "wsl-care-events.service"], 0);

        var result = await world.RunAsync();

        Succeeded(result);
        result.Stderr.Should().Contain("WSL_CARE_INSTALL_SKIP_RUN_WAIT=1: not checking");
        File.ReadAllText(world.At(InstallWorld.BinaryPath)).Should().Be(world.StubScript());
    }

    /// <summary>coai E6 code round #1: the wait decides from the STATE, never from the layout — each running-state golden (what
    /// the binary really prints) is put through the real guard: none / dead proceed, every other state waits, and a state with no
    /// decision here fails this test before it can silently switch the wait off.</summary>
    private static readonly IReadOnlyDictionary<string, bool> Waits = new Dictionary<string, bool>(StringComparer.Ordinal)
    {
        [Core.Status.RunningStateName.None] = false,
        [Core.Status.RunningStateName.Dead] = false,
        [Core.Status.RunningStateName.Queued] = true,
        [Core.Status.RunningStateName.Live] = true,
        [Core.Status.RunningStateName.Wedged] = true,
        [Core.Status.RunningStateName.Unknown] = true,
        [Core.Status.RunningStateName.Unreadable] = true,
    };

    public static TheoryData<string> StatusGoldens() =>
        [.. Directory.GetFiles(Path.Combine(ShippedFiles.RepositoryRoot, "contracts", "golden", "head"), "status*.json").Select(Path.GetFileName).OfType<string>().Order(StringComparer.Ordinal)];

    [Fact]
    public void Every_running_state_has_a_wait_decision()
    {
        Waits.Keys.Should().BeEquivalentTo(Core.Status.RunningStateName.All, "a new state needs its decision here — and in install.sh, whose guard fails closed");
    }

    [Theory]
    [MemberData(nameof(StatusGoldens))]
    public async Task The_upgrade_wait_decides_every_running_state_golden_by_its_state_never_by_its_layout(string golden)
    {
        Linux();
        var path = Path.Combine(ShippedFiles.RepositoryRoot, "contracts", "golden", "head", golden);
        var state = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(path))!["running"]!["state"]!.GetValue<string>();
        Waits.Should().ContainKey(state, $"{golden} holds a state with no decision");
        foreach (var compact in new[] { false, true })
        {
            using var world = new InstallWorld($"upgrade-golden-{state}-{(compact ? "compact" : "indented")}") { RunWaitSeconds = "0" };
            var text = compact ? System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(path))!.ToJsonString() : File.ReadAllText(path);
            world.Write(InstallWorld.BinaryPath, $"#!/bin/sh\ncat '{world.Answer($"{golden}.{compact}", text)}'\n");
            File.SetUnixFileMode(world.At(InstallWorld.BinaryPath), InstallWorld.Executable);
            world.Link(InstallWorld.LinkPath, InstallWorld.BinaryPath);
            world.Override("systemctl", ["try-restart", "wsl-care-events.service"], 0);

            var result = await world.RunAsync();

            if (Waits[state])
            {
                FailedAt(result, "upgrade-wait");
                result.Stderr.Should().Contain($"a wsl-care run is {state}", $"{golden} ({(compact ? "compact" : "indented")})");
            }
            else
            {
                Succeeded(result);
            }
        }
    }

    /// <summary>coai E6 code round #4: a wait of up to 10 minutes says it is still waiting — the state, the run and the time —
    /// once every progress period, and refuses at the ceiling. On the SCRIPTED clock (<see cref="InstallWorld.UseScriptedClock"/>):
    /// on the wall clock a 6 s ceiling against the 5 s poll left the one progress line to the scheduler — whenever the two
    /// status calls plus the part of a second already gone when the wait started passed 1 s, the second poll read 6 s and
    /// refused with no line at all (WSL, 2026-10-05: 1 run in 20 alone, 4 in 20 under 24 CPU burners).</summary>
    [Fact]
    public async Task A_long_wait_says_every_progress_period_what_it_waits_for_and_how_long()
    {
        Linux();
        using var world = new InstallWorld("upgrade-progress") { RunWaitSeconds = "30", ProgressSeconds = "10" };
        world.UseScriptedClock();
        world.Write(InstallWorld.BinaryPath, OldBinaryAnswering("live"));
        File.SetUnixFileMode(world.At(InstallWorld.BinaryPath), InstallWorld.Executable);
        world.Link(InstallWorld.LinkPath, InstallWorld.BinaryPath);

        var result = await world.RunAsync();

        FailedAt(result, "upgrade-wait");
        string[] waiting =
        [
            "wsl-care-install: a wsl-care run is live (20261004T120000Z-4242); waiting (at most 30s)",
            "wsl-care-install: still waiting: live 20261004T120000Z-4242, 10s of 30s",
            "wsl-care-install: still waiting: live 20261004T120000Z-4242, 20s of 30s",
        ];
        result.StdoutLines.Where(l => l.Contains("waiting", StringComparison.Ordinal)).Should().Equal(
            waiting, "the wait polls every 5 s and says so once per 10 s period — at 10 s and 20 s, never on the polls between");
        result.Stderr.Should().Contain("a wsl-care run is live (20261004T120000Z-4242), still after 30s");
        world.ClockSeconds.Should().Be(InstallWorld.ScriptedClockStart + 30, "the refusal comes on the poll that reaches the ceiling, before another sleep");
        File.ReadAllText(world.At(InstallWorld.BinaryPath)).Should().Be(OldBinaryAnswering("live"), "nothing was replaced");
    }

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

    private static void Installed(InstallWorld world, string binary, UnixFileMode mode)
    {
        world.Write(InstallWorld.BinaryPath, binary);
        File.SetUnixFileMode(world.At(InstallWorld.BinaryPath), mode);
        world.Link(InstallWorld.LinkPath, InstallWorld.BinaryPath);
    }

    /// <summary>G3: the 5 s sleep ran past the deadline — a 7 s wait refused at 10 s. On the scripted clock, where time moves only
    /// when the script sleeps: the last sleep ends AT the deadline and the refusal comes there.</summary>
    [Fact]
    public async Task The_wait_never_sleeps_past_its_deadline()
    {
        Linux();
        using var world = new InstallWorld("upgrade-sleep-deadline") { RunWaitSeconds = "7" };
        world.UseScriptedClock();
        Installed(world, OldBinaryAnswering("live"), InstallWorld.Executable);

        var result = await world.RunAsync();

        FailedAt(result, "upgrade-wait");
        result.Stderr.Should().Contain("a wsl-care run is live (20261004T120000Z-4242), still after 7s");
        world.ClockSeconds.Should().Be(InstallWorld.ScriptedClockStart + 7, "a sleep of 5 s, then of the 2 s left — never a whole 5 s past the deadline");
    }

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

    /// <summary>The wait failed OPEN on an installed binary that exists but cannot be started (<c>[ -x ]</c>): the upgrade went
    /// ahead as if nothing were in flight. It answers nothing, so it takes the no-answer path — in flight, then the refusal with
    /// the manual escape; only a binary that does not exist at all is a fresh install.</summary>
    [Fact]
    public async Task An_installed_binary_that_cannot_be_started_is_no_answer_never_nothing_in_flight()
    {
        Linux();
        using var world = new InstallWorld("upgrade-not-executable") { RunWaitSeconds = "0" };
        Installed(world, OldBinaryAnswering("live"), InstallWorld.Regular);
        const string request = """{"schemaVersion":1,"runId":"20261004T120000Z-4321","kind":"act","actions":["A10"],"trigger":"manual","createdAt":"2026-10-04T12:00:00+00:00"}""";
        world.Write("/var/lib/wsl-care/requests/20261004T120000Z-4321.json", request);

        var result = await world.RunAsync();

        FailedAt(result, "upgrade-wait");
        result.Stderr.Should().Contain("the installed binary gave no status answer").And.Contain("WSL_CARE_INSTALL_SKIP_RUN_WAIT=1");
        File.ReadAllText(world.At(InstallWorld.BinaryPath)).Should().Be(OldBinaryAnswering("live"), "nothing was replaced");
        File.ReadAllText(world.At("/var/lib/wsl-care/requests/20261004T120000Z-4321.json")).Should().Be(request);
        world.CallsOf("systemctl").Should().BeEmpty("no unit was touched");
    }

    [Fact]
    public async Task An_upgrade_with_nothing_in_flight_renames_the_new_binary_over_the_old_and_a_queued_request_survives_it()
    {
        Linux();
        using var world = new InstallWorld("upgrade-request");
        world.Write(InstallWorld.BinaryPath, OldBinaryAnswering("none"));
        File.SetUnixFileMode(world.At(InstallWorld.BinaryPath), InstallWorld.Executable);
        world.Link(InstallWorld.LinkPath, InstallWorld.BinaryPath);
        world.Override("systemctl", ["try-restart", "wsl-care-events.service"], 0);
        // An E6.S0-shaped request (no "shown"): the schema stays 1 and additive, so the new binary reads what the old one wrote.
        const string request = """{"schemaVersion":1,"runId":"20261004T120000Z-4321","kind":"act","actions":["A10"],"trigger":"manual","createdAt":"2026-10-04T12:00:00+00:00"}""";
        world.Write("/var/lib/wsl-care/requests/20261004T120000Z-4321.json", request);

        var result = await world.RunAsync();

        Succeeded(result);
        File.ReadAllText(world.At(InstallWorld.BinaryPath)).Should().Be(world.StubScript());
        File.Exists(world.At(InstallWorld.BinaryPath + ".new")).Should().BeFalse("renamed over the old one, never left beside it");
        File.ReadAllText(world.At("/var/lib/wsl-care/requests/20261004T120000Z-4321.json")).Should().Be(request, "an upgrade never touches the request folder");
        var parsed = System.Text.Json.JsonSerializer.Deserialize(request, Core.Json.WslCareJsonContext.Default.RunRequestFile)!;
        parsed.RunId.Text.Should().Be("20261004T120000Z-4321");
        world.StubInvocations.Should().NotBeEmpty();
    }

    [Fact]
    public async Task The_binary_goes_in_beside_the_old_one_and_is_renamed_over_it_in_one_step()
    {
        Linux();
        using var world = new InstallWorld("rename");

        var result = await world.RunAsync("--dry-run");

        Succeeded(result);
        result.Stdout.Should().Contain($"/wsl-care {world.At(InstallWorld.BinaryPath)}.new")
            .And.Contain($"mv -f {world.At(InstallWorld.BinaryPath)}.new {world.At(InstallWorld.BinaryPath)}");
    }
}
