using System.Text;
using System.Text.RegularExpressions;

using FluentAssertions;

using WslCare.Cli;
using WslCare.Core.Config;
using WslCare.Core.Files;

namespace WslCare.Scenarios;

/// <summary>
/// What a release ships besides the binary (plan §15e #1, E4.S1): the three systemd units and the machine
/// configuration layer, read where they live in this repository and judged by the PRODUCT's own parser and loader — a
/// unit that names a verb the CLI does not take, or a machine layer the loader refuses, fails here on every OS.
/// </summary>
public sealed partial class ShippedFilesTests
{
    private const string InstallPath = "/opt/wsl-care/bin/wsl-care";

    /// <summary>The directives of one unit file: section → key → values (a key may repeat).</summary>
    private static Dictionary<string, Dictionary<string, List<string>>> Unit(string name)
    {
        var sections = new Dictionary<string, Dictionary<string, List<string>>>(StringComparer.Ordinal);
        var current = string.Empty;
        foreach (var raw in File.ReadAllLines(Path.Combine(ShippedFiles.SystemdDirectory, name)))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#') || line.StartsWith(';'))
            {
                continue;
            }

            if (line.StartsWith('[') && line.EndsWith(']'))
            {
                current = line[1..^1];
                sections.TryAdd(current, new(StringComparer.Ordinal));
                continue;
            }

            var equals = line.IndexOf('=');
            equals.Should().BePositive($"{name}: every directive is key=value, got \"{line}\"");
            var key = line[..equals];
            if (!sections[current].TryGetValue(key, out var values))
            {
                values = [];
                sections[current][key] = values;
            }

            values.Add(line[(equals + 1)..]);
        }

        return sections;
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

    [Fact]
    public void The_service_counts_only_the_busy_exit_as_success()
    {
        Single(Unit("wsl-care.service"), "Service", "SuccessExitStatus").Should().Be(((int)ExitCode.Busy).ToString(System.Globalization.CultureInfo.InvariantCulture),
            "a second run meeting the lock is the designed answer (plan §5), not a failed unit");
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
