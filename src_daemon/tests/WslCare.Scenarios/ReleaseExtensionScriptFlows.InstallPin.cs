using FluentAssertions;

using WslCare.TestSupport;

namespace WslCare.Scenarios;

/// <summary>
/// The install pin flows of <see cref="ReleaseExtensionScriptFlows"/> (#37 joined with E6.S2), a part of its own so neither
/// file passes the 800-line limit (coai round 8). It shares the checkout helper and the fake <c>gh</c> of the main part.
/// </summary>
public sealed partial class ReleaseExtensionScriptFlows
{
    /// <summary>The install pin (#37, 2026-10-06), joined with the actions minimum of E6.S2: min-daemon.json's
    /// `installDaemon` is the release Install daemon types, a value of its own at or above BOTH minima — daemon 0.1.0's act
    /// unit is defective, so a new install gets 0.1.2 while 0.1.0 still renders and acts. The guard admits it only when every
    /// release it names is published (non-draft) and the stamp is at or above the pin, and emits it as `install_daemon`.</summary>
    [Fact]
    public async Task The_guard_admits_an_install_pin_above_both_minima_only_when_it_is_published_and_the_stamp_reaches_it()
    {
        Linux();
        const string stamp012 = "Last verified: 2026-10-06 · the owner's installation · daemon 0.1.2";

        using (var root = new TempRoot("ext-guard-install-ok"))
        {
            var checkout = Make(root, install: "0.1.2", stamp: stamp012, ghAnswer: "by-tag");
            var result = await ReleaseScripts.RunAsync("release-extension-guard.sh", ["extension-v0.1.0"], checkout.Dir, checkout.Env);

            result.Exit.Should().Be(0, result.Stdout + result.Stderr);
            result.StdoutLines.Should().Contain("min_daemon=0.1.0").And.Contain("min_daemon_actions=0.1.0").And.Contain("install_daemon=0.1.2");
            File.ReadAllText(checkout.GhLog).Should().Contain("releases/tags/daemon-v0.1.0").And.Contain("releases/tags/daemon-v0.1.2", "every release it names is asked about");
        }

        (string What, string? Actions, string Install, string Stamp, string Drafts, string Says)[] refusals =
        [
            ("the install pin is a draft", null, "0.1.2", stamp012, "daemon-v0.1.2", "daemon-v0.1.2 is not a published, non-draft release"),
            ("the stamp is below the install pin", null, "0.1.2", Verified, "", "older than 0.1.2, the release Install daemon types"),
            ("the install pin is below the render minimum", null, "0.0.9", stamp012, "", "installDaemon 0.0.9 is below minDaemonForRender 0.1.0"),
            ("the install pin is below the actions minimum", "0.1.1", "0.1.0", stamp012, "", "installDaemon 0.1.0 is below minDaemonForActions 0.1.1"),
        ];
        foreach (var (what, actions, install, stamp, drafts, says) in refusals)
        {
            using var root = new TempRoot("ext-guard-install-bad");
            var checkout = Make(root, actions: actions, install: install, stamp: stamp, ghAnswer: "by-tag", drafts: drafts);
            var result = await ReleaseScripts.RunAsync("release-extension-guard.sh", ["extension-v0.1.0"], checkout.Dir, checkout.Env);

            result.Exit.Should().Be(1, $"{what}:\n{result.Stdout}{result.Stderr}");
            (result.Stdout + result.Stderr).Should().Contain(says, what);
        }

        using (var root = new TempRoot("ext-guard-install-missing"))
        {
            var checkout = Make(root, minDaemonJson: "{\n  \"minDaemonForRender\": \"0.1.0\",\n  \"minDaemonForActions\": \"0.1.0\"\n}\n");
            var result = await ReleaseScripts.RunAsync("release-extension-guard.sh", ["extension-v0.1.0"], checkout.Dir, checkout.Env);

            result.Exit.Should().Be(1, result.Stdout + result.Stderr);
            (result.Stdout + result.Stderr).Should().Contain("carries no installDaemon");
        }
    }
}
