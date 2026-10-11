using System.Text;

using FluentAssertions;

using WslCare.Core.Config;
using WslCare.Core.Files;
using WslCare.Core.Health;
using WslCare.Core.Processes;
using WslCare.Core.Processes.Policy;

namespace WslCare.Core.Tests.Processes.Policy;

/// <summary>
/// E15.S1 (PLAN_disk_health.md §3.1): PowerShell is a CLOSED set — the clock probe and the Windows disk script, each EXACTLY
/// (program and arguments, under the configuration in force). Anything else a PowerShell would run is refused by the
/// never-list before any template is asked: a third script, a variant of one of the two, <c>-File</c>, the right arguments
/// under another PowerShell program.
/// </summary>
public sealed class PowerShellClosedSetTests
{
    private static readonly TimeSpan Ceiling = TimeSpan.FromSeconds(5);

    private static IReadOnlyList<string> Clock => HealthCommands.WindowsClock.Argv;

    private static IReadOnlyList<string> Disk => WindowsDiskCommands.WindowsDisk.Argv;

    private static string? Broken(IReadOnlyList<string> argv) => NeverList.FirstBroken(argv)?.Id;

    [Fact]
    public void Both_fixed_scripts_pass_the_never_list_and_the_product_policy()
    {
        Broken(Clock).Should().BeNull();
        Broken(Disk).Should().BeNull("the Windows disk script is the second member of the closed set");
        foreach (var command in HealthCommands.FixedScripts)
        {
            var verdict = CommandPolicy.Product.Review(command.ToRequest());
            verdict.Should().Be(CommandVerdict.Allowed, (verdict as CommandVerdict.Refused)?.Reason ?? command.Name);
        }
    }

    [Fact]
    public void The_closed_set_is_the_clock_probe_and_the_windows_disk_script_and_nothing_else()
    {
        HealthCommands.FixedScripts.Select(c => c.Name).Should().Equal(HealthCommands.WindowsClock.Name, WindowsDiskCommands.Name);
        HealthCommands.FixedScripts.Should().OnlyContain(c => c.Executable == HealthCommands.PowerShell, "both run Windows PowerShell, found as the clock probe's is");
    }

    [Theory]
    [InlineData("Get-Date")]
    [InlineData("Get-PhysicalDisk")]
    [InlineData("Start-Service w32time")]
    public void A_third_script_is_refused(string script) =>
        Broken([HealthCommands.PowerShell, "-NoProfile", "-NonInteractive", "-Command", script]).Should().Be("powershell");

    [Fact]
    public void The_clock_script_with_one_word_changed_is_refused()
    {
        var changed = HealthCommands.ClockScript.Replace("Get-Service", "Set-Service", StringComparison.Ordinal);
        changed.Should().NotBe(HealthCommands.ClockScript, "the word is in the script");

        Broken([HealthCommands.PowerShell, "-NoProfile", "-NonInteractive", "-Command", changed]).Should().Be("powershell");
    }

    [Fact]
    public void The_disk_script_with_one_word_changed_is_refused()
    {
        var changed = WindowsDiskCommands.Script.Replace("Get-PhysicalDisk", "Get-Disk", StringComparison.Ordinal);
        changed.Should().NotBe(WindowsDiskCommands.Script, "the word is in the script");

        Broken([HealthCommands.PowerShell, "-NoProfile", "-NonInteractive", "-Command", changed]).Should().Be("powershell");
    }

    [Theory]
    [InlineData("-File", "C:\\x\\disk.ps1")]
    [InlineData("-f", "C:\\x\\disk.ps1")]
    [InlineData("-EncodedCommand", "ZQBjAGgAbwA=")]
    public void A_script_file_or_an_encoded_command_is_refused(string flag, string value) =>
        Broken([HealthCommands.PowerShell, "-NoProfile", "-NonInteractive", flag, value]).Should().Be("powershell");

    [Fact]
    public void A_fixed_script_with_a_trailing_argument_is_refused() =>
        Broken([.. Disk, "-File", "C:\\x\\disk.ps1"]).Should().Be("powershell");

    [Theory]
    [InlineData("pwsh")]
    [InlineData("pwsh.exe")]
    [InlineData("/mnt/c/WINDOWS/System32/WindowsPowerShell/v1.0/powershell.exe")]
    public void The_right_arguments_under_another_powershell_program_are_refused(string program)
    {
        Broken([program, .. HealthCommands.WindowsClock.Arguments]).Should().Be("powershell", "the closed set names the program too");
        Broken([program, .. WindowsDiskCommands.WindowsDisk.Arguments]).Should().Be("powershell");
    }

    [Fact]
    public void The_disk_script_built_under_another_configuration_is_refused_and_its_own_scope_admits_it()
    {
        var other = WindowsDiskCommands.ScriptFor(7, 250, 500);
        other.Should().NotBe(WindowsDiskCommands.Script, "the defaults are 60 days and 5000 events");

        Broken([HealthCommands.PowerShell, "-NoProfile", "-NonInteractive", "-Command", other]).Should().Be("powershell", "the closed set is the configuration in force");
        using (Tuning.Use(Config("""{ "disk": { "eventsSinceDays": 7, "eventsMaxRead": 250 } }""")))
        {
            WindowsDiskCommands.Script.Should().Be(other);
            Broken(WindowsDiskCommands.WindowsDisk.Argv).Should().BeNull();
            CommandPolicy.Product.Review(WindowsDiskCommands.WindowsDisk.ToRequest()).IsAllowed.Should().BeTrue("the template reads the configuration in force too");
        }
    }

    [Fact]
    public void The_policy_refuses_a_disk_script_variant_naming_the_never_rule()
    {
        var changed = WindowsDiskCommands.Script.Replace("-MaxEvents 5000", "-MaxEvents 5001", StringComparison.Ordinal);

        CommandPolicy.Product.Review(new CommandRequest([HealthCommands.PowerShell, "-NoProfile", "-NonInteractive", "-Command", changed], Ceiling))
            .Should().BeOfType<CommandVerdict.Refused>().Which.Reason.Should().Contain("never-list (powershell:");
    }

    [Fact]
    public void The_rule_says_it_is_a_closed_set()
    {
        var rule = NeverList.Rules.Single(r => r.Id == "powershell");

        rule.Description.Should().Contain("closed set").And.Contain("clock probe").And.Contain("Windows disk script");
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
