using FluentAssertions;

using WslCare.Core.Hosting;

namespace WslCare.Core.Tests.Hosting;

/// <summary>Plan §6 and §4.6 as paths, per OS — and the sandbox a test or a scenario run gets instead.</summary>
public sealed class HostPathsTests
{
    [Fact]
    public void Linux_layout_follows_plan_section_6()
    {
        var paths = new LinuxHostPaths(new LinuxEnvironment("/home/alice", "/etc", "/var", "/tmp", "/home/alice/.config"));

        paths.Side.Should().Be(HostSide.Wsl);
        paths.MachineConfigFile.Should().Be("/etc/wsl-care/config.json");
        paths.UserConfigFile.Should().Be("/home/alice/.config/wsl-care/config.json");
        paths.StateDirectory.Should().Be("/var/lib/wsl-care");
        paths.LogDirectory.Should().Be("/var/log/wsl-care");
        paths.TempDirectory.Should().Be("/tmp");
        paths.GitRoots.Should().Equal("/home/alice/git");
        paths.ClaudeTempRoots.Should().Equal("/tmp/claude");
        paths.AgentRoots.Should().Contain(["/home/alice/.claude", "/home/alice/.codex", "/home/alice/.gemini", "/home/alice/.cache/antigravity"]);
    }

    [Fact]
    public void Windows_layout_follows_plan_section_6_and_the_windows_plan()
    {
        var paths = new WindowsHostPaths(new WindowsEnvironment(
            @"C:\Users\alice", @"C:\Users\alice\AppData\Roaming", @"C:\Users\alice\AppData\Local", @"C:\ProgramData", @"C:\Users\alice\AppData\Local\Temp"));

        paths.Side.Should().Be(HostSide.Windows);
        paths.MachineConfigFile.Should().Be(@"C:\ProgramData\wsl-care\config.json");
        paths.UserConfigFile.Should().Be(@"C:\Users\alice\AppData\Roaming\wsl-care\config.json");
        paths.StateDirectory.Should().Be(@"C:\Users\alice\AppData\Local\wsl-care");
        paths.LogDirectory.Should().Be(@"C:\Users\alice\AppData\Local\wsl-care\logs");
        paths.ClaudeTempRoots.Should().Equal(@"C:\Users\alice\AppData\Local\Temp\claude");
        paths.GitRoots.Should().Equal(@"C:\Users\alice\git");
        paths.AgentRoots.Should().Contain([@"C:\Users\alice\.claude", @"C:\Users\alice\AppData\Local\AnthropicClaude", @"C:\Users\alice\AppData\Roaming\Claude", @"C:\Users\alice\.codex"]);
    }

    [Fact]
    public void A_sandboxed_layout_keeps_every_path_under_the_root()
    {
        var paths = HostPaths.ForThisMachine(sandboxRoot: OperatingSystem.IsWindows() ? @"C:\sandbox\r1" : "/sandbox/r1");
        var root = OperatingSystem.IsWindows() ? @"C:\sandbox\r1" : "/sandbox/r1";

        var all = new[] { paths.Home, paths.StateDirectory, paths.LogDirectory, paths.TempDirectory, paths.MachineConfigFile, paths.UserConfigFile }
            .Concat(paths.AgentRoots).Concat(paths.GitRoots).Concat(paths.ClaudeTempRoots);

        all.Should().OnlyContain(p => paths.Rules.IsStrictlyUnder(p, root), "a sandbox must never reach a real folder");
        paths.MachineConfigFile.Should().NotBe(paths.UserConfigFile);
    }

    [Fact]
    public void Without_a_sandbox_root_the_real_machine_layout_is_used()
    {
        var paths = HostPaths.ForThisMachine(sandboxRoot: null);

        paths.Home.Should().Be(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
        paths.Side.Should().Be(OperatingSystem.IsWindows() ? HostSide.Windows : HostSide.Wsl);
    }

    [Fact]
    public void The_sandbox_variable_name_is_the_documented_one()
    {
        HostPaths.SandboxRootVariable.Should().Be("WSL_CARE_ROOT");
    }
}
