using System.Runtime.Versioning;
using System.Text.RegularExpressions;

using FluentAssertions;

using WslCare.TestSupport;

namespace WslCare.Scenarios;

/// <summary>
/// The automated POST_DEPLOY items that read the owner's installation (1, 5, 7, 11), run as the conventions'
/// <c>post-deploy-check.mjs --target</c> runs them — the FIRST code span of the row's Check cell, <c>\|</c> unescaped, under
/// <c>/bin/sh -c</c> — against a stand-in <c>wsl.exe</c> that relays to stand-in <c>systemctl</c> / <c>journalctl</c> /
/// <c>wsl-care</c>. Each item must FAIL on the broken state it names, not only pass on the healthy one: at the 0.1.0 live
/// gate item 7 printed PASS while systemd had dropped <c>CollectMode=</c>, because the checker ran only its first span
/// (<c>systemctl cat</c>) and that asserted nothing; item 1's <c>systemctl is-active</c> of two units exits 0 when EITHER is
/// active; <c>doctor --json</c> exits 0 healthy or not; and item 11 passed on an empty journal.
/// </summary>
[SupportedOSPlatform("linux")]
public sealed partial class PostDeployCommandFlows
{
    private static void Linux() => Assert.SkipUnless(OperatingSystem.IsLinux(), "the items are POSIX sh run inside WSL: covered on the Linux legs (and by hand in WSL)");

    /// <summary>The command post-deploy-check runs for item <paramref name="item"/>: the table row split on unescaped pipes,
    /// the Check cell with <c>\|</c> unescaped, its first backtick span (the checker's <c>parseTable</c> and
    /// <c>commandOf</c>, mirrored).</summary>
    internal static string Command(int item)
    {
        var lines = File.ReadAllLines(Path.Combine(ReleaseFiles.Root, "POST_DEPLOY.md"));
        var headers = Cells(lines.First(l => l.StartsWith("| # |", StringComparison.Ordinal)));
        var check = headers.FindIndex(h => h.Contains("check", StringComparison.OrdinalIgnoreCase));
        var row = Cells(lines.Single(l => l.StartsWith($"| {item} |", StringComparison.Ordinal)));
        var span = FirstSpan().Match(row[check]);
        span.Success.Should().BeTrue($"item {item}'s check holds a command");
        return span.Groups[1].Value.Trim();
    }

    private static List<string> Cells(string line) =>
        [.. UnescapedPipe().Split(TrailingPipe().Replace(line.Trim().TrimStart('|'), string.Empty)).Select(c => c.Trim().Replace("\\|", "|", StringComparison.Ordinal))];

    /// <summary>A stand-in WSL: <c>wsl.exe … -- &lt;argv&gt;</c> runs the argv here, the installed binary's path mapped to the
    /// stand-in <c>wsl-care</c> (a fake cannot sit at <c>/opt</c> without root), everything else found on the stand-ins'
    /// PATH first — which is what the real relay does with a Linux command.</summary>
    private sealed class World : IDisposable
    {
        private readonly TempRoot _root = new("post-deploy");

        public World(string systemctl = "", string journalctl = "", string wslCare = "")
        {
            Tool("wsl.exe", "while [ \"$#\" -gt 0 ] && [ \"$1\" != \"--\" ]; do shift; done\nshift\n" +
                "if [ \"$1\" = /opt/wsl-care/bin/wsl-care ]; then shift; exec \"$(dirname \"$0\")/wsl-care\" \"$@\"; fi\nexec \"$@\"\n");
            Tool("systemctl", systemctl);
            Tool("journalctl", journalctl);
            Tool("wsl-care", wslCare);
        }

        private string Bin => _root.Dir("bin");

        private void Tool(string name, string body)
        {
            var path = _root.File($"bin/{name}", "#!/bin/sh\n" + body);
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        public Task<ChildResult> RunAsync(int item) =>
            ChildProcess.RunAsync("/bin/sh", ["-c", Command(item)], new Dictionary<string, string?>
            {
                ["PATH"] = $"{Bin}:{Environment.GetEnvironmentVariable("PATH")}",
                ["TARGET"] = "0.1.1",
            });

        public void Dispose() => _root.Dispose();
    }

    /// <summary>systemctl is-active as systemd 255 answers it: one line per unit, exit 0 when ANY is active (observed in WSL
    /// Ubuntu, 2026-10-06: <c>is-active wsl-care.timer no-such.service</c> → active, inactive, exit 0).</summary>
    private static string IsActive(string timer, string follower) =>
        $"[ \"$1\" = is-active ] || exit 2\necho {timer}\necho {follower}\n[ {timer} = active ] || [ {follower} = active ]\n";

    [Fact]
    public async Task Item_1_passes_when_the_timer_and_the_follower_are_both_active()
    {
        Linux();
        using var world = new World(systemctl: IsActive("active", "active"));

        var result = await world.RunAsync(1);

        result.Exit.Should().Be(0, result.Stdout + result.Stderr);
    }

    [Theory]
    [InlineData("active", "inactive")]
    [InlineData("inactive", "active")]
    [InlineData("failed", "failed")]
    public async Task Item_1_fails_when_either_unit_is_not_active(string timer, string follower)
    {
        Linux();
        using var world = new World(systemctl: IsActive(timer, follower));

        var result = await world.RunAsync(1);

        result.Exit.Should().NotBe(0, $"the timer {timer} and the follower {follower}: a person loses the runs or the container count");
    }

    private const string HealthyDoctor = "[ \"$1 $2\" = \"doctor --json\" ] || exit 2\nprintf '{\\n  \"schemaVersion\": 1,\\n  \"side\": \"linux\",\\n  \"healthy\": %s,\\n  \"observeOnly\": false\\n}\\n' ";

    [Fact]
    public async Task Item_5_passes_on_a_healthy_doctor()
    {
        Linux();
        using var world = new World(wslCare: HealthyDoctor + "true\n");

        var result = await world.RunAsync(5);

        result.Exit.Should().Be(0, result.Stdout + result.Stderr);
    }

    [Fact]
    public async Task Item_5_fails_on_an_unhealthy_doctor_although_doctor_exits_zero()
    {
        Linux();
        using var world = new World(wslCare: HealthyDoctor + "false\n");

        var result = await world.RunAsync(5);

        result.Exit.Should().NotBe(0, "doctor --json answers exit 0 either way (Output.Answer); the item must read \"healthy\"");
    }

    /// <summary>The template's properties as systemd loads them; <paramref name="collectMode"/> is what 0.1.0 produced
    /// (<c>inactive</c>: the key ignored) or what 0.1.1 asks for.</summary>
    private static string Units(string collectMode, string execStart = "ExecStart=/opt/wsl-care/bin/wsl-care collect --timer") =>
        "case \"$1\" in\n" +
        $"  cat) printf '# /etc/systemd/system/wsl-care.service\\n[Service]\\nType=oneshot\\n{execStart}\\n' ;;\n" +
        $"  show) printf 'TimeoutStopUSec=1min 30s\\nMemoryMax=1073741824\\nKillMode=control-group\\nCollectMode={collectMode}\\n' ;;\n" +
        "  *) exit 2 ;;\nesac\n";

    [Fact]
    public async Task Item_7_passes_on_the_units_as_released()
    {
        Linux();
        using var world = new World(systemctl: Units("inactive-or-failed"));

        var result = await world.RunAsync(7);

        result.Exit.Should().Be(0, result.Stdout + result.Stderr);
    }

    [Fact]
    public async Task Item_7_fails_on_the_0_1_0_template_whose_CollectMode_systemd_ignored()
    {
        Linux();
        using var world = new World(systemctl: Units("inactive"));

        var result = await world.RunAsync(7);

        result.Exit.Should().NotBe(0, "systemctl show printed CollectMode=inactive at the 0.1.0 live gate and the item printed PASS");
        result.Stdout.Should().Contain("CollectMode=inactive-or-failed");
    }

    [Fact]
    public async Task Item_7_fails_on_a_timer_service_without_the_timer_flag()
    {
        Linux();
        using var world = new World(systemctl: Units("inactive-or-failed", "ExecStart=/opt/wsl-care/bin/wsl-care collect"));

        var result = await world.RunAsync(7);

        result.Exit.Should().NotBe(0, "a collect without --timer never acts");
    }

    private static string Journal(params string[] lines) =>
        $"printf '%s\\n' {string.Join(' ', lines.Select(l => $"'{l.Replace("'", "'\\''", StringComparison.Ordinal)}'"))}\n";

    private const string Finished = "Oct 06 16:02:23 host systemd[1]: wsl-care.service: Consumed 5.509s CPU time.";

    [Fact]
    public async Task Item_11_passes_after_a_run_and_prints_its_consumption()
    {
        Linux();
        using var world = new World(journalctl: Journal("Oct 06 16:02:23 host wsl-care[1]: timer action pass (dry run)", Finished));

        var result = await world.RunAsync(11);

        result.Exit.Should().Be(0, result.Stdout + result.Stderr);
        result.StdoutLines.Should().Equal([Finished], "the run's consumption is what the item shows beside the 1G ceiling");
    }

    [Fact]
    public async Task Item_11_fails_when_the_journal_holds_no_run()
    {
        Linux();
        using var world = new World(journalctl: "echo '-- No entries --'\n");

        var result = await world.RunAsync(11);

        result.Exit.Should().NotBe(0, "no run in two days is no evidence that the limits hold, not a pass");
    }

    [Theory]
    [InlineData("Oct 06 16:02:20 host systemd[1]: wsl-care.service: Failed with result 'oom-kill'.")]
    [InlineData("Oct 06 16:02:20 host kernel: audit: apparmor=\"DENIED\" operation=\"exec\" profile=\"snap.docker\"")]
    public async Task Item_11_fails_on_an_oom_kill_or_a_snap_refusal(string line)
    {
        Linux();
        using var world = new World(journalctl: Journal(line, Finished));

        var result = await world.RunAsync(11);

        result.Exit.Should().NotBe(0, line);
    }

    /// <summary>The extraction is the checker's: the companion that proves it reads a real row (testing.md — a scan that
    /// matches nothing passes forever).</summary>
    [Fact]
    public void The_extraction_reads_the_first_span_of_the_check_cell_with_pipes_unescaped()
    {
        Command(2).Should().StartWith("wsl.exe -d Ubuntu -- /opt/wsl-care/bin/wsl-care --version | tr -d '\\r'");
    }

    [GeneratedRegex(@"(?<!\\)\|")]
    private static partial Regex UnescapedPipe();

    [GeneratedRegex(@"(?<!\\)\|$")]
    private static partial Regex TrailingPipe();

    [GeneratedRegex("`([^`]+)`")]
    private static partial Regex FirstSpan();
}
