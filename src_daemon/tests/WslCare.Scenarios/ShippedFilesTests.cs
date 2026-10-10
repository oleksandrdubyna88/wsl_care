using System.Text;
using System.Text.RegularExpressions;

using FluentAssertions;

using WslCare.Cli;
using WslCare.Core.Config;
using WslCare.Core.Files;

namespace WslCare.Scenarios;

/// <summary>
/// What a release ships besides the binary (plan §15e #1, E4.S1): the systemd units of <c>src_daemon/systemd</c> (and the
/// drop-ins <c>wsl-care units dropin</c> renders for them) and the machine configuration layer, read where they live in this
/// repository and judged by the PRODUCT's own parser and loader — a unit that names a verb the CLI does not take, or a
/// machine layer the loader refuses, fails here on every OS.
/// </summary>
public sealed partial class ShippedFilesTests
{
    private const string InstallPath = "/opt/wsl-care/bin/wsl-care";

    /// <summary>The directives of one unit file: section → key → values (a key may repeat).</summary>
    private static Dictionary<string, Dictionary<string, List<string>>> Unit(string name) =>
        Sections(name, File.ReadAllLines(Path.Combine(ShippedFiles.SystemdDirectory, name)));

    /// <summary>The drop-in of the embedded defaults for <paramref name="name"/>, read the same way.</summary>
    private static Dictionary<string, Dictionary<string, List<string>>> DropIn(string name) =>
        Sections($"{name}'s drop-in", Core.Systemd.UnitDropIns.Defaults(name).Split('\n'));

    /// <summary>Unit-file lines as section → key → values; blanks and comments skipped.</summary>
    private static Dictionary<string, Dictionary<string, List<string>>> Sections(string name, IEnumerable<string> lines)
    {
        var sections = new Dictionary<string, Dictionary<string, List<string>>>(StringComparer.Ordinal);
        var outside = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var current = outside;
        foreach (var line in lines.Select(raw => raw.Trim()).Where(IsDirective))
        {
            current = IsSectionHeader(line) ? Section(sections, line[1..^1]) : Add(current, name, line);
        }

        outside.Should().BeEmpty($"{name}: every directive sits in a [section]");
        return sections;
    }

    private static bool IsDirective(string line) => line.Length > 0 && !line.StartsWith('#') && !line.StartsWith(';');

    private static bool IsSectionHeader(string line) => line.StartsWith('[') && line.EndsWith(']');

    private static Dictionary<string, List<string>> Section(Dictionary<string, Dictionary<string, List<string>>> sections, string section)
    {
        sections.TryAdd(section, new(StringComparer.Ordinal));
        return sections[section];
    }

    private static Dictionary<string, List<string>> Add(Dictionary<string, List<string>> section, string name, string line)
    {
        var equals = line.IndexOf('=');
        equals.Should().BePositive($"{name}: every directive is key=value, got \"{line}\"");
        section.TryAdd(line[..equals], []);
        section[line[..equals]].Add(line[(equals + 1)..]);
        return section;
    }

    private static string Single(Dictionary<string, Dictionary<string, List<string>>> unit, string section, string key) =>
        unit[section][key].Should().ContainSingle($"[{section}] {key} is set once").Subject;

    /// <summary>The argv an <c>ExecStart</c> starts the binary with, the absolute path checked first.</summary>
    private static IReadOnlyList<string> ExecArgs(string name)
    {
        var words = Single(Unit(name), "Service", "ExecStart").Split(' ', StringSplitOptions.RemoveEmptyEntries);
        words[0].Should().Be(InstallPath, $"{name} starts the binary by the absolute path install.sh installs it to (plan §15e #3)");
        return words[1..];
    }

    [Fact]
    public void The_timers_service_runs_collect_with_the_timer_flag_as_the_cli_parses_it()
    {
        var request = CommandLine.Parse(ExecArgs("wsl-care.service"));

        request.Should().BeOfType<Request.Collect>()
            .Which.Timer.Should().BeTrue("the timer says so with --timer; INVOCATION_ID is never read (plan §15d CI row)");
        ((Request.Collect)request).Json.Should().BeFalse("the journal gets the text report");
        Single(Unit("wsl-care.service"), "Service", "Type").Should().Be("oneshot");
        Unit("wsl-care.service").Should().NotContainKey("Install", "started by its timer only, never enabled on its own");
    }

    /// <summary>The cgroup ceiling covers every child of the run (npm, dotnet, pip, the docker CLIs, the folder walk):
    /// plan §8's 256M would OOM-kill a child mid-cleanup (E4 review, 2026-10-03).</summary>
    /// <summary>Plan E14 S2b: the watch unit starts <c>watch --timer</c> — the only watch that may let A19 act — as the CLI parses it.</summary>
    [Fact]
    public void The_watch_service_runs_watch_with_the_timer_flag_as_the_cli_parses_it()
    {
        var request = CommandLine.Parse(ExecArgs("wsl-care-watch.service"));

        request.Should().BeOfType<Request.Watch>().Which.Timer.Should().BeTrue("the watch timer says so with --timer, never the environment");
        ((Request.Watch)request).Json.Should().BeFalse("the journal gets the text line");
        Single(Unit("wsl-care-watch.service"), "Service", "Type").Should().Be("oneshot");
        Unit("wsl-care-watch.service").Should().NotContainKey("Install", "started by its timer only, never enabled on its own");
    }

    /// <summary>Plan E14 S2b: a MONOTONIC timer every <c>mcpWatchdog.periodMinutes</c> — a period after the timer itself starts (at boot,
    /// or at <c>enable --now</c>) and after each run — with no catch-up (nothing to catch up after the VM was off), so no
    /// <c>Persistent=</c> and no calendar. Own code review 2026-10-09: <c>OnBootSec=</c> already in the past ELAPSES AT ONCE when the
    /// timer is started (systemd.timer(5)), so <c>install.sh</c>'s <c>enable --now</c> started a watch that took the run lock from the
    /// installer's first full run a second later; <c>OnActiveSec=</c> is relative to the timer's own start.</summary>
    [Fact]
    public void The_watch_timer_fires_a_period_after_it_starts_and_after_each_run()
    {
        var unit = Unit("wsl-care-watch.timer");

        unit["Timer"].Should().NotContainKey("OnBootSec", "a past OnBootSec fires at enable --now, into the installer's first run");
        Single(unit, "Timer", "OnActiveSec").Should().Be("5min");
        Single(unit, "Timer", "OnUnitActiveSec").Should().Be("5min", "the default of mcpWatchdog.periodMinutes");
        unit["Timer"].Should().NotContainKey("OnCalendar").And.NotContainKey("Persistent");
        Single(unit, "Timer", "Unit").Should().Be("wsl-care-watch.service");
        Single(unit, "Install", "WantedBy").Should().Be("timers.target");
    }

    /// <summary>Own code review 2026-10-09: in a <c>[Timer]</c> section an EMPTY assignment of ANY time setting resets EVERY time setting
    /// before it (systemd.timer(5): "the list of timers is reset (both monotonic timers and OnCalendar= timers)"), so a drop-in that
    /// cleared its second setting after setting its first lost the first — the watch timer kept only <c>OnUnitActiveSec</c>, which
    /// never fires for a service that never ran. Every drop-in clears once, before any value.</summary>
    [Fact]
    public void No_timer_drop_in_clears_a_time_setting_after_it_set_one()
    {
        foreach (var name in Core.Systemd.UnitDropIns.Units.Where(n => n.EndsWith(".timer", StringComparison.Ordinal)))
        {
            var lines = Core.Systemd.UnitDropIns.Defaults(name).Split('\n').Select(l => l.Trim()).Where(l => l.StartsWith("On", StringComparison.Ordinal)).ToList();
            var firstValue = lines.FindIndex(l => !l.EndsWith('='));
            var lastReset = lines.FindLastIndex(l => l.EndsWith('='));

            firstValue.Should().BeGreaterThanOrEqualTo(0, $"{name}'s drop-in sets a time");
            lastReset.Should().BeLessThan(firstValue, $"{name}: a reset after a value wipes that value ({string.Join(" | ", lines)})");
        }
    }

    [Fact]
    public void The_service_memory_ceiling_leaves_room_for_the_tools_the_cleanups_start()
    {
        Single(Unit("wsl-care.service"), "Service", "MemoryMax").Should().Be("1G", "a ceiling against a runaway, not a budget for the binary alone");
    }

    [Fact]
    public void The_events_unit_runs_events_follow_and_restarts_always_after_thirty_seconds()
    {
        CommandLine.Parse(ExecArgs("wsl-care-events.service")).Should().Be(new Request.EventsFollow(Once: false));
        var unit = Unit("wsl-care-events.service");
        Single(unit, "Service", "Restart").Should().Be("always");
        Single(unit, "Service", "RestartSec").Should().Be("30", "plan §4.3; the follower backs off in-process (§15b #8)");
        Single(unit, "Install", "WantedBy").Should().Be("multi-user.target");
    }

    [Fact]
    public void The_timer_fires_every_four_hours_and_catches_up_a_run_the_night_missed()
    {
        var unit = Unit("wsl-care.timer");
        Single(unit, "Timer", "OnCalendar").Should().Be("*-*-* 00/4:00:00");
        Single(unit, "Timer", "Persistent").Should().Be("true", "the VM is off every night; Persistent= acts on OnCalendar= timers only");
        Single(unit, "Timer", "RandomizedDelaySec").Should().Be("5min");
        Single(unit, "Timer", "Unit").Should().Be("wsl-care.service");
        Single(unit, "Install", "WantedBy").Should().Be("timers.target");
    }

    /// <summary>The hardening decision, kept: each of these breaks a named action (the unit files say which).</summary>
    [Fact]
    public void No_unit_sets_a_sandbox_directive_that_would_break_a_cleanup()
    {
        string[] breaking = ["ProtectHome", "ProtectSystem", "ProtectKernelTunables", "ProtectClock", "PrivateDevices", "ProtectProc", "PrivateUsers", "PrivateTmp", "CapabilityBoundingSet", "DynamicUser", "User"];
        foreach (var name in ShippedFiles.UnitNames)
        {
            var keys = Unit(name).Values.SelectMany(s => s.Keys).ToList();
            keys.Should().NotIntersectWith(breaking, $"{name}: these break A8/A12/A14/A17, A9/A10, A1/A2, A16, A15, A11 or root (the unit's own comment)");
        }

        Unit("wsl-care.service")["Service"].Should().ContainKey("NoNewPrivileges", "the scan reads directives — it finds the one hardening line that IS set");
    }

    /// <summary>The hardening a run gets (plan §15k #9): the same in both units that run the binary as root — a line added to
    /// one and forgotten in the other is a red test, not a drift found on a live machine.</summary>
    private static readonly string[] HardeningKeys = ["Nice", "IOSchedulingClass", "MemoryMax", "NoNewPrivileges", "KillMode", "TimeoutStopSec"];

    private static Dictionary<string, string> Hardening(string name)
    {
        var service = Unit(name)["Service"];
        return HardeningKeys.Where(service.ContainsKey).ToDictionary(k => k, k => Single(Unit(name), "Service", k), StringComparer.Ordinal);
    }

    [Fact]
    public void The_timer_s_service_and_the_detached_run_s_template_carry_the_same_hardening()
    {
        Hardening("wsl-care-act@.service").Should().Equal(Hardening("wsl-care.service"), "plan §15k #9: one hardening set for every root run");
        Hardening("wsl-care-watch.service").Should().Equal(Hardening("wsl-care.service"), "plan E14 S2b: the watch is a root run too");
    }

    /// <summary>The companion: the equality above compares what was READ — every hardening key is there, with its value.</summary>
    [Fact]
    public void The_hardening_comparison_reads_every_key_it_compares()
    {
        Hardening("wsl-care.service").Should().Equal(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["Nice"] = "19",
            ["IOSchedulingClass"] = "idle",
            ["MemoryMax"] = "1G",
            ["NoNewPrivileges"] = "yes",
            ["KillMode"] = "control-group",
            ["TimeoutStopSec"] = "90",
        });
    }

    [Fact]
    public void The_detached_run_s_template_runs_its_instance_s_request_as_the_cli_parses_it()
    {
        var words = ExecArgs("wsl-care-act@.service");
        words.Should().Equal("act", "--request", "%i");
        var request = CommandLine.Parse([.. words.Select(w => w == "%i" ? "20261004T120000Z-4321" : w)]);

        request.Should().Be(new Request.ActFromRequest(Core.Records.RunId.TryParse("20261004T120000Z-4321")!), "systemd puts the instance name — the run id — where %i stands");
        var unit = Unit("wsl-care-act@.service");
        Single(unit, "Service", "Type").Should().Be("oneshot");
        Single(unit, "Service", "TimeoutStartSec").Should().Be("infinity", "a confirm is never time-killed as a whole (§15f #9, §15k #0)");
        unit["Service"].Should().NotContainKey("CollectMode", "systemd 255 ignores it there — \"Unknown key name 'CollectMode' in section 'Service', ignoring.\" (daemon 0.1.0, POST_DEPLOY item 7)");
        unit["Unit"].Should().ContainKey("CollectMode", "systemd.unit(5) reads CollectMode= in [Unit] only");
        Single(unit, "Unit", "CollectMode").Should().Be("inactive-or-failed", "a finished instance never lingers in systemctl --failed (§15k #8)");
        unit.Should().NotContainKey("Install", "started by --detach only, never enabled");
    }

    /// <summary>The section systemd 255 reads each key from — the man page that defines it, per key (systemd.unit(5),
    /// .service(5), .exec(5), .kill(5), .resource-control(5), .timer(5)). systemd IGNORES a key in the wrong section with a
    /// warning and exit 0, so daemon 0.1.0 shipped <c>CollectMode=</c> under <c>[Service]</c> and a failed detached run was
    /// never collected (POST_DEPLOY item 7). CI's <c>systemd-analyze verify</c> (<c>.github/scripts/verify-systemd-units.sh</c>)
    /// is the real parser, on the Linux legs; this table makes the same mistake red on every OS. A key not listed here is
    /// refused until it is added with its man page — a new directive is a decision about which section reads it.</summary>
    private static readonly Dictionary<string, string[]> KeysBySection = new(StringComparer.Ordinal)
    {
        ["Unit"] = ["Description", "Documentation", "CollectMode"], // systemd.unit(5) [Unit]
        ["Service"] =
        [
            "Type", "ExecStart", "Restart", "RestartSec", "TimeoutStartSec", "TimeoutStopSec", "SuccessExitStatus", // systemd.service(5)
            "Nice", "IOSchedulingClass", "NoNewPrivileges", // systemd.exec(5)
            "KillMode", // systemd.kill(5)
            "MemoryMax", // systemd.resource-control(5)
        ],
        ["Timer"] = ["OnCalendar", "OnActiveSec", "OnUnitActiveSec", "Persistent", "RandomizedDelaySec", "AccuracySec", "Unit"], // systemd.timer(5)
        ["Install"] = ["WantedBy"], // systemd.unit(5) [Install]
    };

    /// <summary>The sections a unit of this TYPE has (systemd.service(5), systemd.timer(5)): a <c>[Timer]</c> in a service is
    /// ignored like a misplaced key.</summary>
    private static string[] SectionsOfType(string name) =>
        name.EndsWith(".timer", StringComparison.Ordinal) ? ["Unit", "Timer", "Install"] : ["Unit", "Service", "Install"];

    /// <summary>Every section of <paramref name="sections"/> is one this unit type has, and every key in it one systemd reads
    /// in that section.</summary>
    private static void EveryKeyWhereSystemdReadsIt(string unit, string name, Dictionary<string, Dictionary<string, List<string>>> sections)
    {
        foreach (var (section, keys) in sections)
        {
            SectionsOfType(unit).Should().Contain(section, $"{name}: [{section}] is a section a unit of this type has");
            keys.Keys.Should().BeSubsetOf(KeysBySection[section], $"{name}: every key of [{section}] is one systemd reads in [{section}] — anywhere else it is ignored with a warning and exit 0");
        }
    }

    [Fact]
    public void Every_key_of_every_shipped_unit_sits_in_the_section_systemd_reads_it_from()
    {
        foreach (var name in ShippedFiles.UnitNames)
        {
            EveryKeyWhereSystemdReadsIt(name, name, Unit(name));
        }
    }

    /// <summary>The same for each drop-in install.sh writes from <c>wsl-care units dropin</c> (E7.S2c): a drop-in is parsed
    /// exactly like its unit.</summary>
    [Fact]
    public void Every_key_of_every_drop_in_sits_in_the_section_systemd_reads_it_from()
    {
        foreach (var name in Core.Systemd.UnitDropIns.Units)
        {
            EveryKeyWhereSystemdReadsIt(name, $"{name}'s drop-in", DropIn(name));
        }
    }

    /// <summary>The companion of the two scans above (testing.md: a scan that matches nothing passes forever): they read the
    /// template's [Unit] section and find the one key there that is not a description.</summary>
    [Fact]
    public void The_section_scan_reads_the_template_s_unit_section()
    {
        Unit("wsl-care-act@.service").Should().ContainKey("Unit").WhoseValue.Keys.Should().Contain("Description");
        Unit("wsl-care-act@.service")["Unit"].Should().HaveCount(3, "Description, Documentation and CollectMode — read, not skipped");
    }

    /// <summary>E7.S2c review N-4 as REVERSED by the E7.S2b/S2c review (C-H2): no unit's start limit may kill its own run before
    /// its worst case, and no run may hang forever. A <c>oneshot</c> unit's start IS the whole run: either <c>infinity</c> (the
    /// detached confirm, never time-killed as a whole — the progress watchdog ends a hang) or a limit at least the DERIVED worst
    /// case of a timer run (wsl-care.service: every command template once at its ceiling with its drains, the two walks, a
    /// margin). A <c>simple</c> unit's start ends when its process starts, a worst case of zero that systemd's default is above.
    /// The old <c>TimeoutStartSec=10min</c> was below one A7 prune (<c>docker.pruneTimeoutSeconds</c>, 15 min).</summary>
    [Fact]
    public void Every_unit_s_start_limit_is_infinity_or_above_the_worst_case_of_its_run()
    {
        foreach (var name in ShippedFiles.UnitNames.Where(n => n.EndsWith(".service", StringComparison.Ordinal)))
        {
            var service = Unit(name)["Service"];
            var type = Single(Unit(name), "Service", "Type");
            if (type == "oneshot")
            {
                var limit = Single(Unit(name), "Service", "TimeoutStartSec");
                // Plan E14 S2b: each run's OWN derived worst case — the watch's is a sample and A19's signals, not a full run.
                var worst = name == "wsl-care-watch.service" ? Core.Config.RunBudget.WatchRunWorstCase(Core.Config.Tuning.Default.Config) : Core.Config.RunBudget.TimerRunWorstCase(Core.Config.Tuning.Default.Config);
                (limit == "infinity" || (limit.EndsWith("min", StringComparison.Ordinal) && TimeSpan.FromMinutes(int.Parse(limit[..^3], System.Globalization.CultureInfo.InvariantCulture)) >= worst))
                    .Should().BeTrue($"{name}: TimeoutStartSec={limit} must be infinity or at least the derived worst case {worst.TotalMinutes:0} min");
            }
            else
            {
                type.Should().Be("simple", $"{name}: the only other type shipped");
                service.Should().NotContainKey("TimeoutStartSec", $"{name}: a simple unit is started once its process is, a worst case of zero");
            }
        }

        Single(Unit("wsl-care.service"), "Service", "TimeoutStartSec").Should().NotBe("infinity", "review C-H2: the timer's run has a backstop");
        Single(Unit("wsl-care-act@.service"), "Service", "TimeoutStartSec").Should().Be("infinity", "a confirm is never time-killed as a whole (§15f #9)");
    }

    /// <summary>E7.S2c: the drop-in of the DEFAULTS says what each shipped unit file says — so a machine that changes nothing
    /// runs exactly the unit files, and a drop-in is a change only where the machine configuration is one.</summary>
    [Fact]
    public void The_drop_in_of_the_defaults_says_what_each_shipped_unit_says()
    {
        foreach (var name in Core.Systemd.UnitDropIns.Units)
        {
            var unit = Unit(name);
            var section = name.EndsWith(".timer", StringComparison.Ordinal) ? "Timer" : "Service";
            foreach (var (key, value) in DropInSettings(Core.Systemd.UnitDropIns.Defaults(name)))
            {
                Comparable(key, Single(unit, section, key)).Should().Be(Comparable(key, value), $"{name}: [{section}] {key} of the defaults' drop-in");
            }
        }
    }

    /// <summary>The companion: a drop-in under a changed machine configuration carries the configured values — the timer's
    /// calendar from <c>timer.periodHours</c> among them.</summary>
    [Fact]
    public void A_drop_in_carries_the_configured_values()
    {
        var loaded = ConfigLoader.Load(
        [
            (ConfigLoader.DefaultsFile, new FileReadResult.Content(ConfigLoader.EmbeddedDefaults())),
            (new ConfigLayerFile(ConfigLayer.Machine, "/etc/wsl-care/config.json"), new FileReadResult.Content(Encoding.UTF8.GetBytes("""{ "timer": { "periodHours": 6 }, "units": { "nice": 10, "memoryMaxMb": 2048, "stopTimeoutSeconds": 60, "eventsRestartSeconds": 45 }, "mcpWatchdog": { "periodMinutes": 3 } }"""))),
        ]);
        loaded.Errors.Should().BeEmpty();

        using (Tuning.Use(loaded.Config))
        {
            DropInSettings(Core.Systemd.UnitDropIns.Render("wsl-care.timer")).Should().Contain(("OnCalendar", "*-*-* 00/6:00:00"));
            DropInSettings(Core.Systemd.UnitDropIns.Render("wsl-care.service")).Should().Equal(("Nice", "10"), ("MemoryMax", "2048M"), ("TimeoutStopSec", "60"), ("TimeoutStartSec", "276min"));
            DropInSettings(Core.Systemd.UnitDropIns.Render("wsl-care-act@.service")).Should().Equal([.. DropInSettings(Core.Systemd.UnitDropIns.Render("wsl-care.service")).Where(s => s.Key != "TimeoutStartSec")], "one hardening set; a confirm keeps its infinity");
            DropInSettings(Core.Systemd.UnitDropIns.Render("wsl-care-events.service")).Should().Equal(("RestartSec", "45"), ("MemoryMax", "2048M"));
            DropInSettings(Core.Systemd.UnitDropIns.Render("wsl-care-watch.timer")).Should().Equal(("OnActiveSec", "3min"), ("OnUnitActiveSec", "3min"));
            DropInSettings(Core.Systemd.UnitDropIns.Render("wsl-care-watch.service")).Should().Equal(("Nice", "10"), ("MemoryMax", "2048M"), ("TimeoutStopSec", "60"), ("TimeoutStartSec", "10min"));
        }
    }

    /// <summary>A drop-in's settings, without the empty assignment that clears a list first (<c>OnCalendar=</c>).</summary>
    private static IReadOnlyList<(string Key, string Value)> DropInSettings(string text) =>
        [.. text.Split('\n').Select(l => l.Trim()).Where(l => IsDirective(l) && !IsSectionHeader(l) && !l.EndsWith('=')).Select(l => (l[..l.IndexOf('=')], l[(l.IndexOf('=') + 1)..]))];

    /// <summary>A value as systemd reads it: <c>1G</c> and <c>1024M</c> are the same ceiling.</summary>
    private static string Comparable(string key, string value) =>
        key == "MemoryMax" && value.EndsWith('G') ? (int.Parse(value[..^1], System.Globalization.CultureInfo.InvariantCulture) * 1024).ToString(System.Globalization.CultureInfo.InvariantCulture) + "M" : value;

    // The units' SuccessExitStatus lists are held by Cli.Tests/UnitSuccessExitTests, DERIVED from the exits every recorded ending
    // of the two runs returns (retro round over PR #11, O3) — the hand-typed comparisons that stood here were circular.

    [Fact]
    public void The_installer_installs_exactly_the_units_this_repository_ships()
    {
        var script = File.ReadAllText(ShippedFiles.InstallScript);
        var match = UnitsLine().Match(script);
        match.Success.Should().BeTrue("install.sh declares its units on one readonly line");
        var installed = match.Groups[1].Value.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var shipped = Directory.GetFiles(ShippedFiles.SystemdDirectory).Select(Path.GetFileName).ToList();

        installed.Should().BeEquivalentTo(shipped, "a unit added to src_daemon/systemd must be installed, and the installer installs nothing it does not ship");
        installed.Should().BeEquivalentTo(ShippedFiles.UnitNames, "the harness packs the same list");
    }

    [Fact]
    public void The_shipped_machine_layer_is_valid_sets_nothing_and_its_documented_example_is_valid_too()
    {
        var shipped = File.ReadAllBytes(ShippedFiles.MachineConfig);

        var loaded = Load(shipped);

        loaded.Should().BeOfType<ConfigLoadResult.Valid>("an invalid machine layer would make every run observe-only");
        loaded.Config.Entries.Should().OnlyContain(e => e.Layer == ConfigLayer.Default, "it is deliberately empty: every value stays the binary's default");

        // A file of negative assertions needs one positive: the example its own comment offers, uncommented.
        var example = Regex.Match(Encoding.UTF8.GetString(shipped), @"^//\s+(\{ ""dryRun"".*\})\s*$", RegexOptions.Multiline);
        example.Success.Should().BeTrue("machine.json documents an example line");
        var withExample = Load(Encoding.UTF8.GetBytes(example.Groups[1].Value));
        withExample.Should().BeOfType<ConfigLoadResult.Valid>("the documented example passes the real validator");
        withExample.Config.Entries.Where(e => e.Layer == ConfigLayer.Machine).Select(e => e.Key.Name)
            .Should().BeEquivalentTo(["dryRun", "volumes.anonymousMaxGb"], "the loader reads this file's shape and applies it as the machine layer");
    }

    private static ConfigLoadResult Load(byte[] machine) =>
        ConfigLoader.Load(
        [
            (ConfigLoader.DefaultsFile, new FileReadResult.Content(ConfigLoader.EmbeddedDefaults())),
            (new ConfigLayerFile(ConfigLayer.Machine, "/etc/wsl-care/config.json"), new FileReadResult.Content(machine)),
        ]);

    [GeneratedRegex("""^readonly UNITS="([^"]+)"$""", RegexOptions.Multiline)]
    private static partial Regex UnitsLine();
}
