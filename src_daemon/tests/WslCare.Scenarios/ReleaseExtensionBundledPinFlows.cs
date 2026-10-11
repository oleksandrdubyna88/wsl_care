using System.Runtime.Versioning;

using FluentAssertions;

using WslCare.TestSupport;

using static WslCare.Scenarios.ReleaseExtensionCheckout;

namespace WslCare.Scenarios;

/// <summary>
/// The bundled pin of release-extension-guard.sh (E7.S5a, todo/PLAN_bundle_windows_binary.md §3.1), run as
/// release-extension.yml runs it: min-daemon.json's <c>bundledDaemon</c> is the release whose Windows build the .vsix carries,
/// a pin of its own. The guard admits it only when <c>daemon-v&lt;bundled&gt;</c> is a published, non-draft release carrying
/// its win-x64 zip, and emits it as <c>bundled_daemon</c>. There is no WSL-side stamp rule for it: the Windows binary is
/// verified after install by POST_DEPLOY item 6. The checkout and the fake <c>gh</c> are <see cref="ReleaseExtensionCheckout"/>.
/// </summary>
[SupportedOSPlatform("linux")]
public sealed class ReleaseExtensionBundledPinFlows
{
    private static void Linux() => Assert.SkipUnless(OperatingSystem.IsLinux(), ReleaseScripts.LinuxOnly);

    [Fact]
    public async Task The_guard_emits_the_bundled_pin_when_its_release_is_published_with_the_win_x64_zip_and_needs_no_stamp_for_it()
    {
        Linux();
        using var root = new TempRoot("ext-guard-bundled-ok");
        var checkout = Make(root, bundled: "0.4.0", ghAnswer: "by-tag");

        var result = await ReleaseScripts.RunAsync("release-extension-guard.sh", ["extension-v0.1.0"], checkout.Dir, checkout.Env);

        result.Exit.Should().Be(0, result.Stdout + result.Stderr);
        result.StdoutLines.Should().Contain("bundled_daemon=0.4.0", "the stamp names daemon 0.1.0 — the Windows build needs none");
        File.ReadAllText(checkout.GhLog).Should().Contain("releases/tags/daemon-v0.4.0").And.Contain("wsl-care-0.4.0-win-x64.zip", "it asked whether the release carries the win-x64 zip by its contract name");
    }

    [Fact]
    public async Task The_guard_refuses_a_bundled_pin_whose_release_is_a_draft_lacks_the_zip_or_is_missing()
    {
        Linux();
        (string What, string Drafts, bool Windows, string? Json, string Says)[] refusals =
        [
            ("a draft", "daemon-v0.4.0", true, null, "the bundled daemon daemon-v0.4.0 is not a published, non-draft release carrying wsl-care-0.4.0-win-x64.zip"),
            ("no win-x64 zip", "", false, null, "carrying wsl-care-0.4.0-win-x64.zip"),
            ("no bundledDaemon", "", true, "{\n  \"minDaemonForRender\": \"0.1.0\",\n  \"minDaemonForActions\": \"0.1.0\",\n  \"installDaemon\": \"0.1.0\"\n}\n", "carries no bundledDaemon"),
            ("not x.y.z", "", true, "{\n  \"minDaemonForRender\": \"0.1.0\",\n  \"minDaemonForActions\": \"0.1.0\",\n  \"installDaemon\": \"0.1.0\",\n  \"bundledDaemon\": \"0.4\"\n}\n", "carries no bundledDaemon"),
        ];
        foreach (var (what, drafts, windows, json, says) in refusals)
        {
            using var root = new TempRoot("ext-guard-bundled-bad");
            var checkout = Make(root, bundled: "0.4.0", ghAnswer: "by-tag", drafts: drafts, windowsAsset: windows, minDaemonJson: json ?? string.Empty);

            var result = await ReleaseScripts.RunAsync("release-extension-guard.sh", ["extension-v0.1.0"], checkout.Dir, checkout.Env);

            result.Exit.Should().Be(1, $"{what}:\n{result.Stdout}{result.Stderr}");
            (result.Stdout + result.Stderr).Should().Contain(says, what);
        }
    }
}
