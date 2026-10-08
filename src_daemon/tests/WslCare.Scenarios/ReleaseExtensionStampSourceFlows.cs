using System.Runtime.Versioning;

using FluentAssertions;

using WslCare.TestSupport;

using static WslCare.Scenarios.ReleaseExtensionCheckout;

namespace WslCare.Scenarios;

/// <summary>
/// WHERE release-extension-guard.sh reads POST_DEPLOY.md's <c>Last verified:</c> stamp, run as release-extension.yml runs it
/// (<c>&lt;tag&gt; origin/main</c>). The stamp is a post-release FACT — recorded after the daemon was installed and watched
/// working — so it lands on main AFTER the commit the extension tag points at. Read from the tag's own tree it can never be
/// there: extension-v0.1.0's guard (run 37520151276) still refused "names no date" when it was re-run after the stamp PR
/// (#49) had merged, and the owner published that release by hand. So the stamp is read from MAIN'S TIP, while everything
/// else (package.json, min-daemon.json and its install pin, the root module) stays at the tag.
/// <para>Each flow makes the checkout a throwaway git repository: the tag's commit, then main's change on top of it, then
/// the working tree checked out back at the tag (detached), exactly what the guard job's checkout of a tag push is. Where a
/// flow pins the install above the minima, main's commit also LOWERS min-daemon.json's pin, so a guard that read the pin
/// from main instead of the tag would answer differently. The no-main-ref form (a local run) keeps reading the checkout;
/// every flow of <see cref="ReleaseExtensionInstallPinFlows"/> and all but the off-main one of
/// <see cref="ReleaseExtensionScriptFlows"/> run that form.</para>
/// </summary>
[SupportedOSPlatform("linux")]
public sealed class ReleaseExtensionStampSourceFlows
{
    private const string NotYetStamped = "Last verified: never, as of 2026-10-03 — nothing released yet";
    private const string Stamp012 = "Last verified: 2026-10-06 · the owner's installation · daemon 0.1.2";

    private static void Linux() => Assert.SkipUnless(OperatingSystem.IsLinux(), ReleaseScripts.LinuxOnly);

    /// <summary>The guard over a tag whose tree carries <paramref name="tagStamp"/>, with main one commit further carrying
    /// <paramref name="mainStamp"/> — or no POST_DEPLOY.md at all when it is null. With an <paramref name="install"/> pin,
    /// main's min-daemon.json pins the minimum (0.1.0) instead: the pin the guard reports must still be the tag's.</summary>
    private static async Task<ChildResult> GuardAsync(TempRoot root, string tagStamp, string? mainStamp, string? install = null)
    {
        var checkout = Make(root, stamp: tagStamp, install: install, ghAnswer: "by-tag");
        var env = GitEnv(checkout);
        await GitAsync(checkout, env, "init", "-q", "-b", "main");
        await GitAsync(checkout, env, "add", ".");
        await GitAsync(checkout, env, "commit", "-q", "-m", "the release commit the extension tag points at");
        var tag = await GitAsync(checkout, env, "rev-parse", "HEAD");

        var postDeploy = Path.Combine(checkout.Dir, "POST_DEPLOY.md");
        if (mainStamp is null)
        {
            await GitAsync(checkout, env, "rm", "-q", "POST_DEPLOY.md");
        }
        else
        {
            await File.WriteAllTextAsync(postDeploy, PostDeploy(mainStamp));
            await GitAsync(checkout, env, "add", "POST_DEPLOY.md");
        }

        if (install is not null)
        {
            await File.WriteAllTextAsync(Path.Combine(checkout.Dir, "src_vs_code", "min-daemon.json"), MinDaemonJson("0.1.0", "0.1.0", "0.1.0"));
            await GitAsync(checkout, env, "add", "src_vs_code/min-daemon.json");
        }

        await GitAsync(checkout, env, "commit", "-q", "-m", "the stamp lands on main after the release");
        await GitAsync(checkout, env, "checkout", "-q", "--detach", tag);
        (await File.ReadAllTextAsync(postDeploy)).Should().Contain(tagStamp, "the working tree the guard runs in is the TAG's tree");

        return await ReleaseScripts.RunAsync("release-extension-guard.sh", ["extension-v0.1.0", "main"], checkout.Dir, env);
    }

    [Fact]
    public async Task A_tag_cut_before_its_stamp_landed_is_released_once_main_carries_the_stamp()
    {
        Linux();
        using var root = new TempRoot("ext-guard-stamp-main");

        var result = await GuardAsync(root, tagStamp: NotYetStamped, mainStamp: Verified);

        result.Exit.Should().Be(0, result.Stdout + result.Stderr);
        result.StdoutLines.Should().Contain("min_daemon=0.1.0").And.Contain("install_daemon=0.1.0");
    }

    [Fact]
    public async Task A_stamp_on_main_naming_a_daemon_newer_than_every_pin_of_the_tag_is_enough()
    {
        Linux();
        using var root = new TempRoot("ext-guard-stamp-newer");

        var result = await GuardAsync(root, tagStamp: NotYetStamped, mainStamp: "Last verified: 2026-11-01 · the owner's installation · daemon 0.3.0 · extension 0.2.0", install: "0.1.2");

        result.Exit.Should().Be(0, result.Stdout + result.Stderr);
        result.StdoutLines.Should().Contain("install_daemon=0.1.2", "the install pin is still the TAG's — main's min-daemon.json pins 0.1.0");
    }

    /// <summary>The tag's tree carries a valid stamp of its own; main's does not — main is what is read, and named.</summary>
    [Fact]
    public async Task Main_without_a_dated_stamp_is_refused_naming_main_even_when_the_tag_tree_has_one()
    {
        Linux();
        using var root = new TempRoot("ext-guard-stamp-main-none");

        var result = await GuardAsync(root, tagStamp: Verified, mainStamp: NotYetStamped);

        result.Exit.Should().Be(1, result.Stdout + result.Stderr);
        result.Stdout.Should().Contain("POST_DEPLOY.md on main").And.Contain("names no date");
    }

    /// <summary>The install pin is the TAG's (min-daemon.json at the tag), the stamp is MAIN's: a main stamp below the pin
    /// refuses, though the tag's own tree stamps the pin — and though main's own min-daemon.json would accept that stamp.</summary>
    [Fact]
    public async Task A_stamp_on_main_below_the_tags_install_pin_is_refused_naming_main()
    {
        Linux();
        using var root = new TempRoot("ext-guard-stamp-main-old");

        var result = await GuardAsync(root, tagStamp: Stamp012, mainStamp: Verified, install: "0.1.2");

        result.Exit.Should().Be(1, result.Stdout + result.Stderr);
        result.Stdout.Should().Contain("POST_DEPLOY.md on main last verified daemon 0.1.0, older than 0.1.2, the release Install daemon types");
    }

    [Fact]
    public async Task Main_without_POST_DEPLOY_at_all_is_refused_naming_main()
    {
        Linux();
        using var root = new TempRoot("ext-guard-stamp-main-missing");

        var result = await GuardAsync(root, tagStamp: Verified, mainStamp: null);

        result.Exit.Should().Be(1, result.Stdout + result.Stderr);
        result.Stdout.Should().Contain("POST_DEPLOY.md is missing on main");
    }
}
