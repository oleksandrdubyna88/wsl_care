using System.Text;

using FluentAssertions;

using WslCare.Core.Config;
using WslCare.Core.Files;
using WslCare.Core.Health;
using WslCare.Core.Processes;
using WslCare.Core.Processes.Policy;

namespace WslCare.Core.Tests.Health;

/// <summary>
/// E15.S10: the THIRD fixed interop script — its text, its engine (Windows PowerShell 5.1 only), its own ceiling and the keys that
/// shape the Defender figure. The text is held to the rules every fixed script keeps: the module path pinned FIRST, one line of ASCII
/// with no double quote and no control character, the answer out by the one UTF-8 road.
/// </summary>
public sealed class DefenderCpuCommandsTests
{
    private static readonly string Script = DefenderCpuCommands.Script;

    [Fact]
    public void The_script_pins_the_module_path_first_and_prints_by_the_one_utf8_road()
    {
        Script.Should().StartWith(HealthCommands.ModulePathPin + "; ", "no cmdlet may autoload a module from a user's Documents");
        Script.Should().Contain(HealthCommands.Utf8WriteStart).And.EndWith(HealthCommands.Utf8WriteEnd, "the answer leaves by the road the other fixed scripts print by");
    }

    [Fact]
    public void The_script_is_one_line_of_ascii_with_no_double_quote_and_no_control_character()
    {
        Script.Should().NotBeEmpty();
        Script.ToCharArray().Should().OnlyContain(c => c >= ' ' && c < 127, "ASCII only, no control character");
        Script.Should().NotContain("\"", "an interop argv carries no double quote");
    }

    [Fact]
    public void The_script_is_one_filtered_read_of_msmpeng_in_the_raw_process_class_with_a_single_quoted_filter()
    {
        Script.Should().Contain("Get-CimInstance -ClassName Win32_PerfRawData_PerfProc_Process");
        Script.Should().Contain("-Filter 'Name=''MsMpEng'''", "a single-quoted filter, the process named in it and nothing else");
        Script.Should().Contain("PercentProcessorTime").And.Contain("Timestamp_Sys100NS").And.Contain("IDProcess").And.Contain("ElapsedTime");
        Script.Should().NotContain("Set-").And.NotContain("Add-Mp").And.NotContain("Remove-Mp").And.NotContain("MpCmdRun", "read-only: nothing about Defender is changed");
    }

    [Fact]
    public void Every_tag_the_script_can_print_is_a_quoted_prefix_in_it()
    {
        Script.Should().Contain("'" + DefenderCpuCommands.Tag).And.Contain("'" + DefenderCpuCommands.NoneTag).And.Contain("'" + DefenderCpuCommands.ErrorTag);
    }

    [Fact]
    public void The_command_runs_under_windows_powershell_51_only_with_the_fixed_words_and_the_script()
    {
        var command = DefenderCpuCommands.DefenderCpu;

        command.Executable.Should().Be(HealthCommands.PowerShell, "the script needs no module, so the reason the disk script runs under pwsh does not apply");
        command.Name.Should().Be("powershell-defender-cpu");
        command.Arguments.Should().Equal("-NoProfile", "-NonInteractive", "-Command", Script);
    }

    [Fact]
    public void The_command_has_its_own_ceiling_of_fifteen_seconds_and_the_health_output_cap()
    {
        var command = DefenderCpuCommands.DefenderCpu;

        command.Ceiling.Should().Be(TimeSpan.FromSeconds(15), "health.defenderCpuTimeoutSeconds, not the disk script's");
        command.Ceiling.Should().NotBe(WindowsDiskCommands.WindowsDisk.Ceiling);
        command.OutputCapChars.Should().Be(1048576);
    }

    [Fact]
    public void The_ceiling_follows_the_configuration_in_force()
    {
        using (Tuning.Use(Config("""{ "health": { "defenderCpuTimeoutSeconds": 45 } }""")))
        {
            DefenderCpuCommands.DefenderCpu.Ceiling.Should().Be(TimeSpan.FromSeconds(45));
            WindowsDiskCommands.WindowsDisk.Ceiling.Should().Be(TimeSpan.FromSeconds(30), "the disk script's own key is untouched");
        }
    }

    [Fact]
    public void The_keys_have_their_ranges_defaults_and_trust()
    {
        var defaults = Tuning.Default.Config;

        ConfigKeys.Health.DefenderCpuTimeoutSeconds.Should().Match<ConfigKey.IntKey>(k => k.Name == "health.defenderCpuTimeoutSeconds" && k.Min == 5 && k.Max == 60 && k.Trust.MachineOnly && k.Trust.Safe == SafeDirection.Lower);
        defaults.Int(ConfigKeys.Health.DefenderCpuTimeoutSeconds).Should().Be(15);
        ConfigKeys.Defender.LocalRetentionDays.Should().Match<ConfigKey.IntKey>(k => k.Name == "defender.localRetentionDays" && k.Min == 30 && k.Max == 1000 && k.Trust.MachineOnly);
        defaults.Int(ConfigKeys.Defender.LocalRetentionDays).Should().Be(400);
        ConfigKeys.Thresholds.DefenderCoresWarnPercent.Should().Match<ConfigKey.IntKey>(k => k.Name == "thresholds.defenderCoresWarnPercent" && k.Min == 10 && k.Max == 800 && !k.Trust.MachineOnly);
        defaults.Int(ConfigKeys.Thresholds.DefenderCoresWarnPercent).Should().Be(100);
    }

    /// <summary><see cref="RunBudget"/> sums every <c>*TimeoutSeconds</c> key: the third script's ceiling reaches the timer run's worst
    /// case with no further change, because its command is one of the templates of the product's catalogue.</summary>
    [Fact]
    public void The_full_runs_worst_case_grows_with_the_defender_ceiling()
    {
        var shortCeiling = RunBudget.TimerRunWorstCase(Config("""{ "health": { "defenderCpuTimeoutSeconds": 5 } }"""));
        var longCeiling = RunBudget.TimerRunWorstCase(Config("""{ "health": { "defenderCpuTimeoutSeconds": 60 } }"""));

        (longCeiling - shortCeiling).Should().Be(TimeSpan.FromSeconds(55), "the new ceiling is counted once, as the disk script's is");
        RunBudget.Keys.Should().Contain(ConfigKeys.Health.DefenderCpuTimeoutSeconds);
    }

    [Fact]
    public void The_product_policy_has_a_template_for_the_command_with_its_ceiling_key()
    {
        ReadCommandTemplates.All.Select(t => t.Name).Should().Contain(DefenderCpuCommands.Name, "the product policy admits the command through a template of its own");
        var template = ReadCommandTemplates.All.Single(t => t.Name == DefenderCpuCommands.Name);

        template.Matches(DefenderCpuCommands.DefenderCpu.Arguments).Should().BeTrue();
        template.Ceiling.Should().Be(TimeSpan.FromSeconds(15));
    }

    private static EffectiveConfig Config(string machine)
    {
        var loaded = ConfigLoader.Load(
        [
            (ConfigLoader.DefaultsFile, new FileReadResult.Content(ConfigLoader.EmbeddedDefaults())),
            (new ConfigLayerFile(ConfigLayer.Machine, "/etc/wsl-care/config.json"), new FileReadResult.Content(Encoding.UTF8.GetBytes(machine))),
        ]);
        loaded.Errors.Should().BeEmpty();
        return loaded.Config;
    }
}
