using System.Runtime.Versioning;

using FluentAssertions;

using WslCare.TestSupport;

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

    /// <summary>Retro review of PR #8, G1: <c>--purge</c> removed the state, the logs and the run lock while a run started by hand
    /// (<c>sudo wsl-care collect</c> in a terminal) could still hold that lock. The lock is held here exactly as the daemon's
    /// <c>RunLock</c> holds it — an exclusive open (<c>FileShare.None</c>, which .NET takes as <c>flock(LOCK_EX)</c> on Linux): the
    /// purge refuses naming the step and why, keeps every byte of the state, and the same command purges once the run has ended.</summary>
    [Fact]
    public async Task A_purge_while_a_run_holds_the_run_lock_is_refused_and_removes_nothing_of_the_state()
    {
        Linux();
        using var world = new InstallWorld("purge-locked");
        Succeeded(await world.RunAsync());
        world.Write("/var/lib/wsl-care/history.jsonl", "{\"runId\":\"x\"}\n");
        world.Write("/var/log/wsl-care/2026-10-06/wsl-care-12-00-00-1.log", "a run log\n");
        ScriptUninstall(world);
        var state = new[] { "/var/lib/wsl-care/history.jsonl", "/var/log/wsl-care/2026-10-06/wsl-care-12-00-00-1.log", "/etc/wsl-care/config.json" }
            .ToDictionary(p => p, p => File.ReadAllBytes(world.At(p)));

        ChildResult refused;
        using (new FileStream(world.At("/run/wsl-care.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
        {
            refused = await world.RunAsync("--uninstall", "--purge");
        }

        FailedAt(refused, "purge");
        refused.Stderr.Should().Contain("a wsl-care run holds the run lock /run/wsl-care.lock")
            .And.Contain("nothing of the state was removed").And.Contain("run --uninstall --purge again when it ends");
        foreach (var (path, bytes) in state)
        {
            File.ReadAllBytes(world.At(path)).Should().Equal(bytes, $"{path} is kept while a run holds the lock");
        }

        File.Exists(world.At("/run/wsl-care.lock")).Should().BeTrue("the lock of a run in flight is never removed under it");

        var again = await world.RunAsync("--uninstall", "--purge");

        Succeeded(again);
        foreach (var gone in new[] { "/var/lib/wsl-care", "/var/log/wsl-care", "/etc/wsl-care" })
        {
            Directory.Exists(world.At(gone)).Should().BeFalse($"once the run has ended, the same command purges {gone}");
        }

        File.Exists(world.At("/run/wsl-care.lock")).Should().BeFalse("the lock file goes last, under the lock");
    }

    /// <summary>The purge opens the lock to take it; with no lock file there before, it leaves none behind either.</summary>
    [Fact]
    public async Task A_purge_with_no_run_lock_file_takes_the_lock_and_leaves_no_lock_file_behind()
    {
        Linux();
        using var world = new InstallWorld("purge-no-lock-file");
        Succeeded(await world.RunAsync());
        ScriptUninstall(world);
        File.Exists(world.At("/run/wsl-care.lock")).Should().BeFalse();

        Succeeded(await world.RunAsync("--uninstall", "--purge"));

        File.Exists(world.At("/run/wsl-care.lock")).Should().BeFalse();
        Directory.Exists(world.At("/var/lib/wsl-care")).Should().BeFalse();
    }

    /// <summary>Without <c>flock</c> the lock cannot be taken, so <c>--purge</c> refuses BEFORE anything is stopped or removed,
    /// naming the tool; a plain <c>--uninstall</c> removes no state and needs no lock.</summary>
    [Fact]
    public async Task Without_flock_a_purge_is_refused_before_anything_changes_and_a_plain_uninstall_still_works()
    {
        Linux();
        using var world = new InstallWorld("purge-no-flock", withoutRealTools: ["flock"]);
        Succeeded(await world.RunAsync());
        ScriptUninstall(world);
        var installed = world.Tree();
        var systemctlBefore = world.CallsOf("systemctl").Count;

        var refused = await world.RunAsync("--uninstall", "--purge");

        FailedAt(refused, "preflight");
        refused.Stderr.Should().Contain("flock (from util-linux) is not installed").And.Contain("nothing was changed");
        world.Tree().Should().BeEquivalentTo(installed, "refused before anything was stopped or removed");
        world.CallsOf("systemctl").Should().HaveCount(systemctlBefore, "no unit was touched");

        Succeeded(await world.RunAsync("--uninstall"));
        File.Exists(world.At(InstallWorld.BinaryPath)).Should().BeFalse();
        Directory.Exists(world.At("/var/lib/wsl-care")).Should().BeTrue("a plain uninstall keeps the state");
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
