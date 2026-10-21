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
/// under another PowerShell program. Since E15.S1b the set has a THIRD member: the same disk script under the bare
/// <c>pwsh.exe</c> (the MSI PowerShell 7) — never the clock probe under pwsh, never pwsh spelled another way.
/// </summary>
public sealed class PowerShellClosedSetTests
{
    private static readonly TimeSpan Ceiling = TimeSpan.FromSeconds(5);

    private static IReadOnlyList<string> Clock => HealthCommands.WindowsClock.Argv;

    private static IReadOnlyList<string> Disk => WindowsDiskCommands.WindowsDisk.Argv;

    private static string? Broken(IReadOnlyList<string> argv) => NeverList.FirstBroken(argv)?.Id;

    [Fact]
    public void Every_member_of_the_closed_set_passes_the_never_list_and_the_product_policy()
    {
        Broken(Clock).Should().BeNull();
        Broken(Disk).Should().BeNull("the Windows disk script is the second member of the closed set");
        Broken(WindowsDiskCommands.WindowsDiskUnderPwsh.Argv).Should().BeNull("the same script under the MSI pwsh is the third");
        Broken(DefenderCpuCommands.DefenderCpu.Argv).Should().BeNull("the Defender CPU script (E15.S10) is the fourth");
        foreach (var command in HealthCommands.FixedScripts)
        {
            var verdict = CommandPolicy.Product.Review(command.ToRequest());
            verdict.Should().Be(CommandVerdict.Allowed, (verdict as CommandVerdict.Refused)?.Reason ?? command.Name);
        }
    }

    /// <summary>E15.S1b: THREE members — the clock probe (always Windows PowerShell 5.1), the disk script under 5.1, and the SAME
    /// disk script under the MSI pwsh 7. pwsh never gets the clock probe. E15.S10: a FOURTH — the Defender CPU script, under 5.1 only
    /// (it needs no module, so a pwsh variant would add a fifth member for no measured gain).</summary>
    [Fact]
    public void The_closed_set_is_the_clock_probe_the_windows_disk_script_under_each_engine_and_the_defender_script_and_nothing_else()
    {
        HealthCommands.FixedScripts.Select(c => c.Name).Should().Equal(HealthCommands.WindowsClock.Name, WindowsDiskCommands.Name, WindowsDiskCommands.PwshName, DefenderCpuCommands.Name);
        HealthCommands.FixedScripts.Select(c => c.Executable).Should().Equal([HealthCommands.PowerShell, HealthCommands.PowerShell, HealthCommands.Pwsh, HealthCommands.PowerShell], "the clock probe and the Defender script run under 5.1 only; the disk script under either engine");
    }

    [Fact]
    public void The_defender_script_passes_with_its_exact_arguments_and_the_product_policy_allows_it()
    {
        var defender = DefenderCpuCommands.DefenderCpu;

        Broken(defender.Argv).Should().BeNull();
        var verdict = CommandPolicy.Product.Review(defender.ToRequest());
        verdict.Should().Be(CommandVerdict.Allowed, (verdict as CommandVerdict.Refused)?.Reason ?? defender.Name);
    }

    /// <summary>E15.S10 RED 7: the set admits exactly the new script, and a ONE-WORD variant is still refused — also the variant that a
    /// PREFIX match would let through (the real script, then more).</summary>
    [Theory]
    [InlineData("MsMpEng", "MsMpEng2")]
    [InlineData("MsMpEng", "explorer")]
    [InlineData("Win32_PerfRawData_PerfProc_Process", "Win32_Process")]
    [InlineData("Get-CimInstance", "Invoke-CimMethod")]
    public void A_one_word_variant_of_the_defender_script_is_refused(string word, string other)
    {
        var changed = DefenderCpuCommands.Script.Replace(word, other, StringComparison.Ordinal);
        changed.Should().NotBe(DefenderCpuCommands.Script, "the word is in the script");

        Broken([HealthCommands.PowerShell, "-NoProfile", "-NonInteractive", "-Command", changed]).Should().Be("powershell");
        CommandPolicy.Product.Review(new CommandRequest([HealthCommands.PowerShell, "-NoProfile", "-NonInteractive", "-Command", changed], Ceiling))
            .Should().BeOfType<CommandVerdict.Refused>().Which.Reason.Should().Contain("never-list (powershell:");
    }

    [Fact]
    public void The_defender_script_with_something_appended_is_refused_a_prefix_of_the_real_script_is_not_the_real_script()
    {
        var longer = DefenderCpuCommands.Script + "; Get-Date";
        var shorter = DefenderCpuCommands.Script[..^10];

        Broken([HealthCommands.PowerShell, "-NoProfile", "-NonInteractive", "-Command", longer]).Should().Be("powershell");
        Broken([HealthCommands.PowerShell, "-NoProfile", "-NonInteractive", "-Command", shorter]).Should().Be("powershell");
        Broken([.. DefenderCpuCommands.DefenderCpu.Argv, "-File", @"C:\x\defender.ps1"]).Should().Be("powershell");
    }

    [Fact]
    public void The_defender_script_under_pwsh_or_any_other_program_is_refused()
    {
        Broken([HealthCommands.Pwsh, .. DefenderCpuCommands.DefenderCpu.Arguments]).Should().Be("powershell", "the Defender script runs under Windows PowerShell 5.1 only");
        Broken(["/mnt/c/WINDOWS/System32/WindowsPowerShell/v1.0/powershell.exe", .. DefenderCpuCommands.DefenderCpu.Arguments]).Should().Be("powershell");
    }

    [Fact]
    public void Pwsh_passes_with_the_disk_scripts_exact_arguments_and_the_product_policy_allows_it()
    {
        var disk = WindowsDiskCommands.WindowsDiskUnderPwsh;

        disk.Argv.Should().Equal([HealthCommands.Pwsh, .. WindowsDiskCommands.WindowsDisk.Arguments], "one source: the disk script's own arguments");
        Broken(disk.Argv).Should().BeNull("the disk script under the MSI pwsh is the third member of the closed set");
        var verdict = CommandPolicy.Product.Review(disk.ToRequest());
        verdict.Should().Be(CommandVerdict.Allowed, (verdict as CommandVerdict.Refused)?.Reason ?? disk.Name);
    }

    [Fact]
    public void Pwsh_with_the_clock_probe_is_refused() =>
        Broken([HealthCommands.Pwsh, .. HealthCommands.WindowsClock.Arguments]).Should().Be("powershell", "the clock probe always runs under Windows PowerShell 5.1");

    [Fact]
    public void Pwsh_with_the_disk_script_with_one_word_changed_is_refused()
    {
        var changed = WindowsDiskCommands.Script.Replace("Get-PhysicalDisk", "Get-Disk", StringComparison.Ordinal);

        Broken([HealthCommands.Pwsh, "-NoProfile", "-NonInteractive", "-Command", changed]).Should().Be("powershell");
        CommandPolicy.Product.Review(new CommandRequest([HealthCommands.Pwsh, "-NoProfile", "-NonInteractive", "-Command", changed], Ceiling))
            .Should().BeOfType<CommandVerdict.Refused>().Which.Reason.Should().Contain("never-list (powershell:");
    }

    [Fact]
    public void Pwsh_with_the_disk_script_of_another_configuration_is_refused_and_its_own_scope_admits_it()
    {
        var other = WindowsDiskCommands.ScriptFor(7, 250, 500);

        Broken([HealthCommands.Pwsh, "-NoProfile", "-NonInteractive", "-Command", other]).Should().Be("powershell", "the closed set is the configuration in force");
        using (Tuning.Use(Config("""{ "disk": { "eventsSinceDays": 7, "eventsMaxRead": 250 } }""")))
        {
            Broken(WindowsDiskCommands.WindowsDiskUnderPwsh.Argv).Should().BeNull();
            CommandPolicy.Product.Review(WindowsDiskCommands.WindowsDiskUnderPwsh.ToRequest()).IsAllowed.Should().BeTrue("the pwsh template reads the configuration in force too");
        }
    }

    [Theory]
    [InlineData("pwsh")]
    [InlineData("PWSH.EXE")]
    [InlineData("/mnt/c/Program Files/PowerShell/7/pwsh.exe")]
    [InlineData(@"C:\Program Files\PowerShell\7\pwsh.exe")]
    [InlineData("/mnt/c/Users/user/AppData/Local/Microsoft/WindowsApps/pwsh.exe")]
    public void The_disk_script_under_a_pwsh_spelled_any_other_way_is_refused(string program) =>
        Broken([program, .. WindowsDiskCommands.WindowsDisk.Arguments]).Should().Be("powershell", "only the bare pwsh.exe, which the resolver finds in its one folder on the drive");

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

        rule.Description.Should().Contain("closed set").And.Contain("clock probe").And.Contain("Windows disk script").And.Contain("Defender CPU script").And.Contain("pwsh.exe");
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
