using System.Runtime.Versioning;
using System.Text.Json;

using FluentAssertions;

using WslCare.Core.Processes;
using WslCare.TestSupport;

namespace WslCare.Scenarios;

/// <summary>
/// The automated POST_DEPLOY items that read the owner's installation (1, 5, 7, 9, 11), run as the conventions'
/// <c>post-deploy-check.mjs --target</c> runs them — the command the checker's own <c>inspect</c> extracts (the FIRST code
/// span of the row's Check cell, <c>\|</c> unescaped), under <c>/bin/sh -c</c> — against a stand-in <c>wsl.exe</c> that
/// relays to stand-in <c>systemctl</c> / <c>journalctl</c> /
/// <c>wsl-care</c>. Each item must FAIL on the broken state it names, not only pass on the healthy one: at the 0.1.0 live
/// gate item 7 printed PASS while systemd had dropped <c>CollectMode=</c>, because the checker ran only its first span
/// (<c>systemctl cat</c>) and that asserted nothing; item 1's <c>systemctl is-active</c> of two units exits 0 when EITHER is
/// active; <c>doctor --json</c> exits 0 healthy or not; and item 11 passed on an empty journal.
/// </summary>
[SupportedOSPlatform("linux")]
public sealed class PostDeployCommandFlows
{
    /// <summary>The conventions checker itself (the <c>.agents/conventions</c> submodule; ci-daemon.yml fetches it).</summary>
    private static string Checker => Path.Combine(ReleaseFiles.Root, ".agents", "conventions", "tools", "post-deploy-check.mjs");

    /// <summary>Linux, Node and the checker: in CI a missing one FAILS — a skip there would hide the very flows this class
    /// adds (coai code round, 2026-10-06); on a developer machine it skips with the reason.</summary>
    private static void Linux()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "the items are POSIX sh run inside WSL: covered on the Linux legs (and by hand in WSL)");
        var missing = ExecutableResolver.Resolve("node") is not ResolvedExecutable.Found ? "node is not on PATH"
            : !File.Exists(Checker) ? $"the conventions checker is not checked out at {Checker} (git submodule update --init .agents/conventions)"
            : string.Empty;
        if (Environment.GetEnvironmentVariable("CI") == "true")
        {
            missing.Should().BeEmpty("CI runs the items through the checker's own extraction");
        }

        Assert.SkipUnless(missing.Length == 0, missing);
    }

    /// <summary>Reads POST_DEPLOY.md with the checker's own exported <c>inspect</c> and prints each item's command as JSON —
    /// so the flows run EXACTLY what <c>post-deploy-check.mjs --target</c> runs (its row split, its <c>\|</c> unescape, its
    /// first-span rule), never a second copy of those rules (coai code round finding, 2026-10-06).</summary>
    /// <remarks>The two paths travel in the environment, not in argv: the checker runs its own <c>main()</c> when
    /// <c>process.argv[1]</c> is the checker's path.</remarks>
    private const string InspectScript =
        "import { readFileSync } from 'node:fs'; import { pathToFileURL } from 'node:url';" +
        "const { inspect } = await import(pathToFileURL(process.env.WSL_CARE_CHECKER).href);" +
        "const { items } = inspect(readFileSync(process.env.WSL_CARE_POST_DEPLOY, 'utf8'));" +
        "process.stdout.write(JSON.stringify(Object.fromEntries(items.filter((i) => !i.manual).map((i) => [String(i.number), i.command]))));";

    private static readonly Lazy<Task<Dictionary<string, string>>> Items = new(async () =>
    {
        var result = await ChildProcess.RunAsync("node", ["--input-type=module", "-e", InspectScript], new Dictionary<string, string?>
        {
            ["WSL_CARE_CHECKER"] = Checker,
            ["WSL_CARE_POST_DEPLOY"] = Path.Combine(ReleaseFiles.Root, "POST_DEPLOY.md"),
        });
        result.Exit.Should().Be(0, $"the checker's inspect reads POST_DEPLOY.md: {result.Stderr}");
        using var json = JsonDocument.Parse(result.Stdout);
        return json.RootElement.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetString() ?? string.Empty, StringComparer.Ordinal);
    });

    /// <summary>The command post-deploy-check runs for item <paramref name="item"/>, as the checker extracts it — only an
    /// AUTOMATED item has one: an item turned manual is one the checker stops running, and these flows say so.</summary>
    internal static async Task<string> CommandAsync(int item)
    {
        var key = item.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var items = await Items.Value;
        items.Should().ContainKey(key, $"item {item} is an automated item of POST_DEPLOY.md, which the checker runs");
        items[key].Should().NotBeEmpty($"item {item}'s check holds a command");
        return items[key];
    }

    /// <summary>A stand-in <c>wsl.exe</c> as the real one behaves when called from inside WSL (observed 2026-10-06, own
    /// review of this change): after <c>--</c> the arguments are NOT kept as argv — they are joined into one command line,
    /// each quoted the Windows way, and the distro's shell parses that line, so <c>wsl.exe -d Ubuntu -- sh -c 'x=1; echo
    /// "[$x]"'</c> prints <c>[]</c>; with <c>--exec</c> argv is kept and the same probe prints <c>[1]</c>
    /// (<see cref="The_stand_in_wsl_exe_re_parses_a_dash_dash_command_line_as_the_real_one_does"/>). The installed binary's
    /// path maps to the stand-in <c>wsl-care</c> (a fake cannot sit at <c>/opt</c> without root); every other program is
    /// found on the stand-ins' PATH first.</summary>
    private const string WslExe = """
        mode=line
        while [ "$#" -gt 0 ]; do
          case "$1" in
            --) shift; break ;;
            -e|--exec) shift; mode=argv; break ;;
            -d|-u|--cd) shift 2 ;;
            *) shift ;;
          esac
        done
        if [ "$1" = /opt/wsl-care/bin/wsl-care ]; then shift; set -- "$(dirname "$0")/wsl-care" "$@"; fi
        [ "$mode" = argv ] && exec "$@"
        line=""
        for a in "$@"; do
          case "$a" in
            *[[:space:]\"]*|"") a="\"$(printf '%s' "$a" | sed 's/"/\\"/g')\"" ;;
          esac
          line="$line${line:+ }$a"
        done
        exec sh -c "$line"

        """;

    private sealed class World : IDisposable
    {
        private readonly TempRoot _root = new("post-deploy");

        public World(string systemctl = "", string journalctl = "", string wslCare = "")
        {
            Tool("wsl.exe", WslExe);
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

        public async Task<ChildResult> RunAsync(int item) => await RunLineAsync(await CommandAsync(item));

        public Task<ChildResult> RunLineAsync(string command) =>
            ChildProcess.RunAsync("/bin/sh", ["-c", command], new Dictionary<string, string?>
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

    /// <summary>A doctor answering the two clock checks of PLAN_windows_time_guard.md D5 — written by the product's own JSON
    /// writer, so the stand-in cannot spell a field the real one does not.</summary>
    private static string ClockDoctor(params Core.Doctor.DoctorCheck[] checks)
    {
        var report = new Core.Doctor.DoctorReport(1, "wsl", DateTimeOffset.UnixEpoch, checks.All(c => c.State != Core.Doctor.DoctorRun.Problem), false, [], checks, []);
        var json = System.Text.Json.JsonSerializer.Serialize(report, Core.Json.WslCareJsonContext.Default.DoctorReport);
        return "[ \"$1 $2\" = \"doctor --json\" ] || exit 2\ncat <<'JSON'\n" + json + "\nJSON\n";
    }

    private static Core.Doctor.DoctorCheck Check(string id, string state, string detail) => new(id, state, detail);

    [Fact]
    public async Task Item_9_passes_when_the_windows_time_service_runs_as_configured_and_the_clocks_agree()
    {
        Linux();
        using var world = new World(wslCare: ClockDoctor(
            Check(Core.Doctor.ClockChecks.WindowsTimeId, Core.Doctor.DoctorRun.Ok, "Running, StartType Automatic: the Windows Time service is running"),
            Check(Core.Doctor.ClockChecks.ClockReferenceId, Core.Doctor.DoctorRun.Ok, "agree: Windows +0.3 s, the distro +0.1 s")));

        var result = await world.RunAsync(9);

        result.Exit.Should().Be(0, result.Stdout + result.Stderr);
        result.Stdout.Should().Contain("windowsTime: ok - Running, StartType Automatic");
    }

    [Theory]
    [InlineData("ok", "warning: Running, StartType Manual: does not start Automatic", "ok", "the start type is not as configured")]
    [InlineData("problem", "Stopped, StartType Manual: the Windows Time service is not running", "problem", "the incident of 2026-10-08")]
    [InlineData("ok", "Running, StartType Automatic", "unknown", "no reference could judge the clocks")]
    [InlineData("ok", "Running, StartType Automatic", "warning-ok", "the reference says the distro's clock is off (wslWrong is a warning)")]
    public async Task Item_9_fails_on_a_manual_start_a_stopped_service_or_clocks_it_cannot_vouch_for(string serviceState, string serviceDetail, string referenceState, string why)
    {
        Linux();
        var reference = referenceState == "warning-ok"
            ? Check(Core.Doctor.ClockChecks.ClockReferenceId, Core.Doctor.DoctorRun.Ok, "warning: wslWrong: the distro's clock is -600.5 s off https://www.microsoft.com (HTTP Date) while Windows agrees")
            : Check(Core.Doctor.ClockChecks.ClockReferenceId, referenceState, "the Windows clock is 7200.4 s slow against https://www.microsoft.com (HTTP Date)");
        using var world = new World(wslCare: ClockDoctor(Check(Core.Doctor.ClockChecks.WindowsTimeId, serviceState, serviceDetail), reference));

        var result = await world.RunAsync(9);

        result.Exit.Should().NotBe(0, why);
    }

    [Fact]
    public async Task Item_9_fails_on_a_daemon_older_than_the_guard_and_says_so()
    {
        Linux();
        using var world = new World(wslCare: ClockDoctor(Check("lastRun", Core.Doctor.DoctorRun.Ok, "recent")));

        var result = await world.RunAsync(9);

        result.Exit.Should().NotBe(0);
        result.Stdout.Should().Contain("no windowsTime check (a daemon older than the Windows Time guard)");
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

    /// <summary>What <c>journalctl -u wsl-care.service</c> can show: systemd's own OOM verdict for the unit, and a refusal
    /// the run logged as an action's reason. (An AppArmor <c>DENIED</c> line is a kernel line with no unit field, never in
    /// <c>-u</c>'s answer — own review of this change — so the item no longer claims to see one.)</summary>
    [Theory]
    [InlineData("Oct 06 16:02:20 host systemd[1]: wsl-care.service: Failed with result 'oom-kill'.")]
    [InlineData("Oct 06 16:02:20 host wsl-care[1]: A4 failed: docker: snap-confine has elevated permissions and is not confined")]
    public async Task Item_11_fails_on_an_oom_kill_or_a_snap_refusal(string line)
    {
        Linux();
        using var world = new World(journalctl: Journal(line, Finished));

        var result = await world.RunAsync(11);

        result.Exit.Should().NotBe(0, line);
    }

    /// <summary>The journal is the run's own words — action reasons, paths, a container's name — so it is DATA: a quote or a
    /// command substitution in it is read, never parsed by a shell (until 0.1.1 the item handed the journal through
    /// wsl.exe's command line to root's shell).</summary>
    [Fact]
    public async Task Item_11_reads_a_journal_line_with_quotes_and_a_command_substitution_as_text()
    {
        Linux();
        using var root = new TempRoot("post-deploy-journal");
        var planted = Path.Combine(root.Path, "ran");
        using var world = new World(journalctl: Journal($"Oct 06 16:02:21 host wsl-care[1]: A9 reason \"x\" $(touch {planted})", Finished));

        var result = await world.RunAsync(11);

        result.Exit.Should().Be(0, result.Stdout + result.Stderr);
        File.Exists(planted).Should().BeFalse("nothing in the journal is executed");
    }

    /// <summary>The stand-in's own test against the real contract it stands in for (generated-code-tests.md: a fake is never
    /// more permissive than the real thing): the observed probe, both forms.</summary>
    [Fact]
    public async Task The_stand_in_wsl_exe_re_parses_a_dash_dash_command_line_as_the_real_one_does()
    {
        Linux();
        using var world = new World();

        var line = await world.RunLineAsync("wsl.exe -d Ubuntu -- sh -c 'x=1; echo \"[$x]\"'");
        var argv = await world.RunLineAsync("wsl.exe -d Ubuntu --exec sh -c 'x=1; echo \"[$x]\"'");

        line.StdoutLines.Should().Equal(["[]"], "observed 2026-10-06 from inside WSL Ubuntu: the outer shell expanded $x");
        argv.StdoutLines.Should().Equal(["[1]"], "--exec keeps argv");
    }

    /// <summary>The companion (testing.md — a read that matches nothing passes forever): the checker's extraction yields a
    /// real row's command, its first span with <c>\|</c> unescaped, and only the automated items.</summary>
    [Fact]
    public async Task The_checker_s_extraction_yields_the_first_span_of_the_check_cell_with_pipes_unescaped()
    {
        Linux();

        (await CommandAsync(2)).Should().StartWith("wsl.exe -d Ubuntu -- /opt/wsl-care/bin/wsl-care --version | tr -d '\\r' | cut -d+ -f1");
        (await Items.Value).Keys.Should().Contain(["1", "5", "7", "9", "11"], "the items these flows run are automated")
            .And.NotContain("3", "item 3 is manual: the checker runs nothing for it");
    }
}
