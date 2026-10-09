using System.Text.Json;

using FluentAssertions;

using WslCare.Cli;
using WslCare.Core.Actions.Engine;
using WslCare.Core.Hosting;
using WslCare.Core.Json;
using WslCare.TestSupport;

namespace WslCare.Scenarios;

/// <summary>
/// Plan §15r E9.S4 over the BUILT CLI, root claimed in the sandbox: A13 and A20 start the archive's child only as the product's own
/// ROOT-OWNED binary — the scenario's binary is the test build's, owned by this account, so both are REFUSED naming why and no
/// <c>runuser</c> is ever started; without a base A13 skips; <c>--entry</c> belongs to A20.
/// </summary>
public sealed class ArchiveActFlows
{
    private static ScenarioHome Home(string purpose, bool withBase = true)
    {
        var home = new ScenarioHome(purpose) { ClaimsRoot = true };
        if (home.Paths.Side == HostSide.Wsl)
        {
            Write(home.Paths.DistroPath("/etc/passwd"), "root:x:0:0::/root:/bin/bash\nme:x:1000:1000::/home/me:/bin/bash\n");
            // The S4 own review round S-M2: the runuser gate fails closed — Ubuntu 24.04's stack, no pam_systemd.
            Write(home.Paths.DistroPath("/etc/pam.d/runuser"), "auth sufficient pam_rootok.so\nsession required pam_unix.so\n");
        }

        Write(home.Paths.UserConfigFile, withBase ? """{ "archive": { "baseFolder": "/mnt/v/ai-archive" } }""" : "{}");
        return home;
    }

    private static void Write(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    private static ActionOutcome Only(ChildResult result) => JsonSerializer.Deserialize(result.Stdout, WslCareJsonContext.Default.ActReport)!.Actions.Single();

    [Fact]
    public async Task A13_and_A20_refuse_a_product_binary_that_is_not_roots_alone_and_start_no_runuser()
    {
        using var home = Home("archive-act-binary");
        Assert.SkipWhen(home.Paths.Side == HostSide.Windows, "A13 and A20 are the distro's actions: the Linux legs");

        var archive = await home.RunAsync("act", "A13", "--preview", "--json");
        var restore = await home.RunAsync("act", "A20", "--confirm", "--manual", "--entry", "0123456789abcdef", "--json");

        archive.Exit.Should().Be((int)ExitCode.Ok, archive.Stderr);
        Only(archive).Reason.Should().Contain("root-owned binary", "the test build is this account's file, never root's");
        Only(restore).Status.Should().Be(ActionStatus.Refused, restore.Stdout);
        Only(restore).Reason.Should().Contain("root-owned binary");
        home.Calls.Should().NotContain(c => c.Tool == "runuser", "nothing is started as the user when the binary is not root's");
    }

    [Fact]
    public async Task Without_a_base_A13_skips_naming_that_no_archive_is_configured()
    {
        using var home = Home("archive-act-no-base", withBase: false);
        Assert.SkipWhen(home.Paths.Side == HostSide.Windows, "A13 is the distro's action: the Linux legs");

        var result = await home.RunAsync("act", "A13", "--preview", "--json");

        result.Exit.Should().Be((int)ExitCode.Ok, result.Stderr);
        Only(result).Reason.Should().Contain("no archive configured");
        home.Calls.Should().NotContain(c => c.Tool == "runuser");
    }

    [Fact]
    public async Task An_entry_named_for_another_action_is_a_usage_error()
    {
        using var home = Home("archive-act-entry");

        var result = await home.RunAsync("act", "A13", "--confirm", "--entry", "0123456789abcdef");

        result.Exit.Should().Be((int)ExitCode.Usage);
        result.Stderr.Should().Contain("A20");
    }
}
