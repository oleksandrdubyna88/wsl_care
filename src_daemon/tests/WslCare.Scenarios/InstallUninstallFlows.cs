using System.Runtime.Versioning;

using FluentAssertions;

using static WslCare.Scenarios.InstallChecks;

namespace WslCare.Scenarios;

/// <summary><c>install.sh --uninstall</c> (E4.S1): what it removes, what it keeps, and what <c>--purge</c> adds. See
/// <see cref="InstallFlows"/> for the harness.</summary>
/// <remarks>Linux only: the script is POSIX sh over GNU coreutils and tar, which the Linux CI legs have and the Windows
/// leg does not. Run by hand in WSL from a copy of the worktree under <c>/tmp</c>, as the test user — never as root, so a
/// path that escaped the prefix would be refused by the operating system.</remarks>
[SupportedOSPlatform("linux")]
public sealed class InstallUninstallFlows
{
    [Fact]
    public async Task Uninstall_removes_the_units_binary_and_link_and_keeps_history_logs_and_machine_config()
    {
        Linux();
        using var world = new InstallWorld("uninstall");
        Succeeded(await world.RunAsync());
        world.Write("/var/lib/wsl-care/history.jsonl", "{\"runId\":\"x\"}\n");
        world.Write("/var/log/wsl-care/2026-10-03/wsl-care-12-00-00-1.log", "a run log\n");
        ScriptUninstall(world);
        var kept = new[] { "/var/lib/wsl-care/history.jsonl", "/var/log/wsl-care/2026-10-03/wsl-care-12-00-00-1.log", "/etc/wsl-care/config.json" }
            .ToDictionary(p => p, p => File.ReadAllBytes(world.At(p)));

        var result = await world.RunAsync("--uninstall");

        Succeeded(result);
        foreach (var unit in ShippedFiles.UnitNames)
        {
            File.Exists(world.At($"/etc/systemd/system/{unit}")).Should().BeFalse($"{unit} is removed");
        }

        foreach (var unit in Core.Systemd.UnitDropIns.Units)
        {
            Directory.Exists(world.At($"/etc/systemd/system/{unit}.d")).Should().BeFalse($"E7.S2c: {unit}'s drop-in and its emptied folder are removed");
        }

        File.Exists(world.At(InstallWorld.BinaryPath)).Should().BeFalse();
        Directory.Exists(world.At("/opt/wsl-care")).Should().BeFalse("its emptied folders go with it");
        Directory.EnumerateFileSystemEntries(world.At("/usr/local/bin")).Should().BeEmpty("the link is removed (a dangling link counts as an entry here)");
        foreach (var (path, bytes) in kept)
        {
            File.ReadAllBytes(world.At(path)).Should().Equal(bytes, $"{path} is kept without --purge");
        }

        result.Stdout.Should().Contain("kept: /var/lib/wsl-care").And.Contain("--uninstall --purge removes them");
        world.CallsOf("systemctl").Select(c => string.Join(' ', c.Argv)).Should().ContainInOrder(
            "disable --now wsl-care.timer wsl-care-events.service", "stop wsl-care.service", "stop wsl-care-act@*.service", "daemon-reload", "is-active --quiet wsl-care.timer");
    }

    [Fact]
    public async Task Uninstall_with_purge_removes_exactly_the_state_logs_machine_config_and_lock_and_names_them()
    {
        Linux();
        using var world = new InstallWorld("purge");
        world.Write("/etc/wsl.conf", "[boot]\nsystemd=true\n");
        Succeeded(await world.RunAsync());
        world.Write("/var/lib/wsl-care/history.jsonl", "{}\n");
        world.Write("/run/wsl-care.lock", string.Empty);
        ScriptUninstall(world);

        var result = await world.RunAsync("--uninstall", "--purge");

        Succeeded(result);
        foreach (var gone in new[] { "/var/lib/wsl-care", "/var/log/wsl-care", "/etc/wsl-care" })
        {
            Directory.Exists(world.At(gone)).Should().BeFalse($"--purge removes {gone}");
            result.Stdout.Should().Contain($"  {gone}   (", $"--purge names {gone} before removing it");
        }

        File.Exists(world.At("/run/wsl-care.lock")).Should().BeFalse();
        result.Stdout.Should().Contain("/run/wsl-care.lock");
        File.ReadAllText(world.At("/etc/wsl.conf")).Should().Be("[boot]\nsystemd=true\n", "never removed, never rewritten");
        File.Exists(world.At("/etc/passwd")).Should().BeTrue();
        File.Exists(world.At("/etc/default/sysstat")).Should().BeTrue("sysstat's settings are not ours");
        result.Stdout.Should().Contain("never removed: sysstat and atop");
    }

    [Fact]
    public async Task Uninstall_removes_a_new_binary_an_interrupted_install_left_beside_the_old_one()
    {
        Linux();
        using var world = new InstallWorld("uninstall-new");
        Succeeded(await world.RunAsync());
        world.Write(InstallWorld.BinaryPath + ".new", "left behind\n");
        ScriptUninstall(world);

        Succeeded(await world.RunAsync("--uninstall"));

        File.Exists(world.At(InstallWorld.BinaryPath + ".new")).Should().BeFalse();
        Directory.Exists(world.At("/opt/wsl-care")).Should().BeFalse("its emptied folders go with it");
    }

    /// <summary>What a world's systemctl answers during an uninstall.</summary>
    private static void ScriptUninstall(InstallWorld world)
    {
        world.Override("systemctl", ["disable", "--now", "wsl-care.timer", "wsl-care-events.service"], 0);
        world.Override("systemctl", ["stop", "wsl-care.service"], 0);
        world.Override("systemctl", ["stop", "wsl-care-act@*.service"], 0);
        world.Override("systemctl", ["is-active", "--quiet", "wsl-care.timer"], 3);
    }
}
