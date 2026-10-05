using System.Runtime.Versioning;
using System.Text;

using FluentAssertions;

using WslCare.Core.Actions;
using WslCare.TestSupport;

using static WslCare.Scenarios.InstallChecks;

namespace WslCare.Scenarios;

/// <summary><c>install.sh --set-default-user</c> and <c>/etc/wsl.conf</c> (E4.S1): never written without the flag, a default
/// user added and read back by the daemon, an existing one kept, the refusals — and the installer and the daemon reading the
/// same user from every shape. See <see cref="InstallFlows"/> for the harness.</summary>
/// <remarks>Linux only: the script is POSIX sh over GNU coreutils and tar, which the Linux CI legs have and the Windows
/// leg does not. Run by hand in WSL from a copy of the worktree under <c>/tmp</c>, as the test user — never as root, so a
/// path that escaped the prefix would be refused by the operating system.</remarks>
[SupportedOSPlatform("linux")]
public sealed class InstallDefaultUserFlows
{
    [Fact]
    public async Task Without_the_flag_wsl_conf_is_never_written_and_the_installer_says_how_to_name_a_user()
    {
        Linux();
        using var world = new InstallWorld("wslconf-advise");
        const string conf = "[boot]\nsystemd=true\n";
        world.Write("/etc/wsl.conf", conf);

        var result = await world.RunAsync();

        Succeeded(result);
        File.ReadAllText(world.At("/etc/wsl.conf")).Should().Be(conf, "never written without --set-default-user");
        result.Stdout.Should().Contain("names no default user").And.Contain("--set-default-user <name>").And.Contain("Nothing was written there");

        using var absent = new InstallWorld("wslconf-absent");
        Succeeded(await absent.RunAsync());
        File.Exists(absent.At("/etc/wsl.conf")).Should().BeFalse("not created either");
    }

    [Fact]
    public async Task With_the_flag_and_no_default_user_wsl_conf_gains_one_that_the_daemon_reads_back()
    {
        Linux();
        using var world = new InstallWorld("wslconf-write");
        const string conf = "[boot]\nsystemd=true\n";
        world.Write("/etc/wsl.conf", conf);

        var result = await world.RunAsync("--set-default-user", "alice");

        Succeeded(result);
        var written = File.ReadAllText(world.At("/etc/wsl.conf"));
        written.Should().StartWith(conf, "what was there stays").And.EndWith("[user]\ndefault=alice\n");
        TargetUserDiscovery.DefaultUser(written).Should().Be("alice", "the daemon's own reader finds the user the installer wrote");
        File.GetUnixFileMode(world.At("/etc/wsl.conf")).Should().Be(InstallWorld.Regular);
        result.Stdout.Should().Contain("WSL itself also logs in as");
    }

    [Fact]
    public async Task With_the_flag_an_existing_default_user_is_never_rewritten()
    {
        Linux();
        using var world = new InstallWorld("wslconf-keep");
        const string conf = "[user]\ndefault=zed\n";
        world.Write("/etc/wsl.conf", conf);

        var result = await world.RunAsync("--set-default-user", "alice");

        Succeeded(result);
        File.ReadAllText(world.At("/etc/wsl.conf")).Should().Be(conf);
        result.Stdout.Should().Contain("names the default user \"zed\"");
        result.Stderr.Should().Contain("--set-default-user alice ignored");
    }

    [Fact]
    public async Task The_flag_for_an_unknown_user_or_over_a_user_section_without_default_refuses_before_anything_is_installed()
    {
        Linux();
        foreach (var (purpose, conf, user, reason) in new[]
        {
            ("unknown", "[boot]\nsystemd=true\n", "nobody-here", "there is no user \"nobody-here\""),
            ("section", "[user]\n# default=zed\n", "alice", "has a [user] section without default="),
        })
        {
            using var world = new InstallWorld($"wslconf-{purpose}");
            world.Write("/etc/wsl.conf", conf);
            var before = world.Tree();

            var result = await world.RunAsync("--set-default-user", user);

            FailedAt(result, "preflight");
            result.Stderr.Should().Contain(reason);
            world.CallsOf("curl").Should().BeEmpty("decided before the download");
            NothingChanged(world, before);
        }
    }

    /// <summary>Two readers of one file — the installer's awk and the daemon's <see cref="TargetUserDiscovery.DefaultUser"/> — must
    /// agree on every shape, or the installer writes a default user the daemon does not see (or keeps one it does not use).</summary>
    [Fact]
    public async Task The_installer_and_the_daemon_read_the_same_default_user_from_every_wsl_conf_shape()
    {
        Linux();
        var shapes = new[]
        {
            "[user]\ndefault=zed\n",
            "[User]\n  Default = \"zed\"  \n",
            "[ user ]\ndefault='zed'\n",
            "[user]\r\ndefault=zed\r\n",
            "[user]\n;default=bob\ndefault=zed\ndefault=carol\n",
            "# [user]\n# default=bob\n",
            "[boot]\ndefault=bob\n",
            "[automount]\nenabled=true\n[user]\ndefault=zed\n[network]\nhostname=x\n",
            "default=bob\n",
            string.Empty,
        };
        foreach (var shape in shapes)
        {
            using var world = new InstallWorld("wslconf-shape");
            world.Write("/etc/wsl.conf", shape);

            var result = await world.RunAsync("--dry-run", "--skip-attestation", "--set-default-user", "alice");

            var daemon = TargetUserDiscovery.DefaultUser(shape);
            var installer = InstallerReading(result);
            installer.Should().Be(daemon, $"both read the same default user from {Visible(shape)}");
        }
    }

    /// <summary>What the installer read from wsl.conf, from what it said: the user it keeps, or empty when it would add one
    /// (or refuses because a [user] section has no default).</summary>
    private static string InstallerReading(ChildResult result)
    {
        const string keeps = "names the default user \"";
        var at = result.Stdout.IndexOf(keeps, StringComparison.Ordinal);
        if (at >= 0)
        {
            var start = at + keeps.Length;
            return result.Stdout[start..result.Stdout.IndexOf('"', start)];
        }

        (result.Stdout.Contains("would add [user] default=alice", StringComparison.Ordinal)
            || result.Stderr.Contains("has a [user] section without default=", StringComparison.Ordinal))
            .Should().BeTrue($"the installer either keeps, adds or refuses:\n{result.Stdout}\n{result.Stderr}");
        return string.Empty;
    }

    private static string Visible(string text) => new StringBuilder(text).Replace("\r", "\\r").Replace("\n", "\\n").ToString();
}
