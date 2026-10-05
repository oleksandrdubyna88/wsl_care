using System.Runtime.Versioning;

using FluentAssertions;

using WslCare.FakeTool;
using WslCare.TestSupport;

using static WslCare.Scenarios.InstallChecks;

namespace WslCare.Scenarios;

/// <summary><c>install.sh</c>'s attestation (plan §15e #4, E4.S1, the E5 code round): a refused or missing attestation, the
/// exact signer identity, the bundle decoder, the gh it accepts, a run without gh (and the last-resort line it is told), and
/// the escape that skips the check. See <see cref="InstallFlows"/> for the harness.</summary>
/// <remarks>Linux only: the script is POSIX sh over GNU coreutils and tar, which the Linux CI legs have and the Windows
/// leg does not. Run by hand in WSL from a copy of the worktree under <c>/tmp</c>, as the test user — never as root, so a
/// path that escaped the prefix would be refused by the operating system.</remarks>
[SupportedOSPlatform("linux")]
public sealed class InstallAttestationFlows
{
    /// <summary>The value after <paramref name="flag"/> in <paramref name="call"/>, or empty.</summary>
    private static string ValueOf(FakeCall call, string flag)
    {
        var at = call.Argv.ToList().IndexOf(flag);
        return at >= 0 && at + 1 < call.Argv.Count ? call.Argv[at + 1] : string.Empty;
    }

    [Fact]
    public async Task A_refused_attestation_aborts_before_anything_is_installed()
    {
        Linux();
        using var world = new InstallWorld("attestation");
        world.RefuseEveryVerification();
        var before = world.Tree();

        var result = await world.RunAsync();

        FailedAt(result, "attestation");
        result.Stderr.Should().Contain("nothing is installed");
        NothingChanged(world, before);
    }

    [Fact]
    public async Task Without_gh_the_installer_stops_before_downloading_anything_and_says_how_to_proceed()
    {
        Linux();
        using var world = new InstallWorld("no-gh", withoutTools: ["gh"]);
        var before = world.Tree();

        var result = await world.RunAsync();

        FailedAt(result, "preflight");
        result.Stderr.Should().Contain("GitHub CLI (gh) is not installed").And.Contain("Nothing was installed")
            .And.Contain("https://cli.github.com/packages").And.Contain("sh -s -- --skip-attestation");
        result.Stderr.Should().NotContain("auth login", "no login is needed: the installer verifies a bundle it fetched itself");
        world.CallsOf("curl").Should().BeEmpty("it stops BEFORE the download (plan §15e #4)");
        NothingChanged(world, before);
    }

    /// <summary>E5 code round (security #3): an install PINNED to a release — what the extension's *Install daemon* types,
    /// `refs/tags/daemon-v&lt;x&gt;/install.sh … --version &lt;x&gt;` — that stops for want of gh must be told the last-resort line
    /// for that SAME tag and version. The old advice named main's installer with no version: one paste away from the
    /// newest daemon, unverified, instead of the one the person chose.</summary>
    [Fact]
    public async Task Without_gh_a_pinned_install_is_told_the_last_resort_line_for_the_same_tag_and_version_never_main()
    {
        Linux();
        using var world = new InstallWorld("no-gh-pinned", withoutTools: ["gh"]);
        var before = world.Tree();

        var result = await world.RunAsync("--version", InstallWorld.NewestDaemon);

        FailedAt(result, "preflight");
        result.Stderr.Should().Contain($"curl -fsSL https://raw.githubusercontent.com/{InstallWorld.Repo}/refs/tags/daemon-v{InstallWorld.NewestDaemon}/install.sh | sudo sh -s -- --version {InstallWorld.NewestDaemon} --skip-attestation")
            .And.Contain("LAST RESORT");
        result.Stderr.Should().NotContain("/main/install.sh", "a pinned install is never pointed at main's installer");
        world.CallsOf("curl").Should().BeEmpty();
        NothingChanged(world, before);
    }

    /// <summary>The same class, swept (security.md — a measure applied at SOME of its sites): every re-run line the
    /// installer prints comes from one function, so the "run it as root" line repeats the pinned ref too.</summary>
    [Fact]
    public async Task A_pinned_install_started_without_root_is_told_to_re_run_the_same_tag_and_version()
    {
        Linux();
        using var world = new InstallWorld("not-root-pinned");
        world.Override("id", ["-u"], 0, "1000\n");

        var result = await world.RunAsync("--version", InstallWorld.NewestDaemon);

        FailedAt(result, "preflight");
        result.Stderr.Should().Contain($"curl -fsSL https://raw.githubusercontent.com/{InstallWorld.Repo}/refs/tags/daemon-v{InstallWorld.NewestDaemon}/install.sh | sudo sh -s -- --version {InstallWorld.NewestDaemon}\n");
        result.Stderr.Should().NotContain("/main/install.sh");
    }

    [Fact]
    public async Task Skip_attestation_installs_without_gh_says_so_loudly_and_still_refuses_a_bad_checksum()
    {
        Linux();
        using var world = new InstallWorld("skip", withoutTools: ["gh"]);

        var result = await world.RunAsync("--skip-attestation");

        Succeeded(result);
        result.Stderr.Should().Contain("ATTESTATION NOT VERIFIED (--skip-attestation)").And.Contain("does NOT")
            .And.Contain("prove who built it");
        File.Exists(world.At(InstallWorld.BinaryPath)).Should().BeTrue();

        using var corrupted = new InstallWorld("skip-checksum", withoutTools: ["gh"]);
        corrupted.Publish(InstallWorld.NewestDaemon, "linux-x64", sha256Line: $"{new string('f', 64)}  x\n");
        var before = corrupted.Tree();
        var refused = await corrupted.RunAsync("--skip-attestation");
        FailedAt(refused, "checksum");
        NothingChanged(corrupted, before);
    }

    [Fact]
    public async Task Under_sudo_root_verifies_a_bundle_it_fetched_itself_with_no_login_no_token_and_no_runuser()
    {
        Linux();
        using var world = new InstallWorld("sudo-user") { SudoUser = "alice", GhToken = "ghp_not_for_the_installer" };

        Succeeded(await world.RunAsync());

        world.CallsOf("runuser").Should().BeEmpty("the person who ran sudo has no say in the verdict: their gh login, config and cache are not used");
        var verify = Verifications(world).Should().ContainSingle().Subject;
        ValueOf(verify, "--bundle").Should().StartWith(world.Temp, "the bundle is the one root fetched into its own temporary folder");
        foreach (var variable in new[] { "HOME", "GH_CONFIG_DIR", "XDG_CONFIG_HOME", "XDG_CACHE_HOME", "XDG_DATA_HOME", "XDG_STATE_HOME" })
        {
            verify.Environment.Should().ContainKey(variable).WhoseValue.Should().StartWith(world.Temp, $"gh's {variable} is inside the installer's temporary folder, nobody's home");
        }

        verify.Environment.Should().NotContainKey("GH_TOKEN", "a bundle needs no login, so no token is handed to gh");
        var api = world.CallsOf("curl").Should().ContainSingle(c => c.Argv[1].StartsWith(InstallWorld.AttestationsApi, StringComparison.Ordinal)).Subject;
        api.Argv.Should().NotContain(a => a.Contains("Authorization", StringComparison.OrdinalIgnoreCase), "the attestation API of a public repository is read unauthenticated");
    }

    [Fact]
    public async Task An_attestation_of_release_yml_built_from_a_branch_is_refused_and_nothing_is_installed()
    {
        Linux();
        using var world = new InstallWorld("branch-attestation");
        world.Publish(InstallWorld.NewestDaemon, "linux-x64", signers: [AttestationBundles.Signer.ReleaseWorkflow(InstallWorld.Repo, "refs/heads/x")]);
        var before = world.Tree();

        var result = await world.RunAsync();

        FailedAt(result, "attestation");
        result.Stderr.Should().Contain("release.yml@refs/heads/x", "gh names the signer it refused");
        NothingChanged(world, before);
    }

    [Fact]
    public async Task An_attestation_of_another_release_tag_is_refused_the_identity_is_the_tag_of_the_version_installed()
    {
        Linux();
        using var world = new InstallWorld("other-tag");
        world.Publish(InstallWorld.NewestDaemon, "linux-x64", signers: [InstallWorld.GenuineSigner("0.0.9")]);
        var before = world.Tree();

        var result = await world.RunAsync();

        FailedAt(result, "attestation");
        NothingChanged(world, before);
        var verify = Verifications(world).Should().ContainSingle().Subject;
        ValueOf(verify, "--cert-identity").Should().Be(InstallWorld.SignerIdentity(InstallWorld.NewestDaemon), "pinned to release.yml AT the tag daemon-v<version>");
        verify.Argv.Should().Contain("--deny-self-hosted-runners").And.NotContain("--signer-workflow", "gh matches a signer workflow as a prefix, whatever the ref");
        ValueOf(verify, "--repo").Should().Be(InstallWorld.Repo);
    }

    [Fact]
    public async Task An_attestation_made_on_a_self_hosted_runner_is_refused()
    {
        Linux();
        using var world = new InstallWorld("self-hosted");
        world.Publish(InstallWorld.NewestDaemon, "linux-x64", signers: [AttestationBundles.Signer.ReleaseWorkflow(InstallWorld.Repo, "refs/tags/daemon-v0.1.0", "self-hosted")]);
        var before = world.Tree();

        var result = await world.RunAsync();

        FailedAt(result, "attestation");
        result.Stderr.Should().Contain("self-hosted");
        NothingChanged(world, before);
    }

    [Theory]
    [InlineData("")]
    [InlineData("/usr/bin/mawk")]
    [InlineData("/usr/bin/gawk")]
    public async Task A_real_github_bundle_is_decompressed_byte_for_byte_so_gh_reads_its_signer(string awk)
    {
        Linux();
        Assert.SkipWhen(awk.Length > 0 && !File.Exists(awk), $"{awk} is not installed here");
        using var world = new InstallWorld("captured-bundle", awk: awk);
        var digest = world.Publish(InstallWorld.NewestDaemon, "linux-x64");
        world.ServeAttestations(digest, [File.ReadAllBytes(ScenarioHome.Fixture("attestation/cli-cli-gh_2.60.0_linux_amd64.tar.gz.json.sn"))]);
        var before = world.Tree();

        var result = await world.RunAsync();

        FailedAt(result, "attestation");
        result.Stderr.Should().Contain("https://github.com/cli/cli/.github/workflows/deployment.yml@refs/heads/trunk",
            "the fake gh read the captured bundle's certificate — only an exact decompression of its 133 snappy elements gives it valid JSON");
        NothingChanged(world, before);
    }

    [Fact]
    public async Task Among_several_attestations_one_genuine_bundle_is_enough_and_a_foreign_one_beside_it_changes_nothing()
    {
        Linux();
        using var world = new InstallWorld("several");
        var digest = world.Publish(InstallWorld.NewestDaemon, "linux-x64");
        var genuine = AttestationBundles.Snappy(AttestationBundles.Utf8(AttestationBundles.Bundle(InstallWorld.GenuineSigner(InstallWorld.NewestDaemon), digest)));
        world.ServeAttestations(digest, [File.ReadAllBytes(ScenarioHome.Fixture("attestation/cli-cli-gh_2.60.0_linux_amd64.tar.gz.json.sn")), genuine], [InstallWorld.GenuineSigner(InstallWorld.NewestDaemon)]);

        Succeeded(await world.RunAsync());

        Verifications(world).Should().HaveCount(2, "each bundle is verified on its own (gh before 2.65 stops at the first bundle a set holds that fails)");
    }

    [Fact]
    public async Task No_attestation_or_an_unreadable_bundle_is_refused_before_anything_is_installed()
    {
        Linux();
        foreach (var (purpose, bundles, says) in new (string, byte[][], string)[]
        {
            ("none", [], "no attestation"),
            ("corrupt", [[0x05, 0x00, 0x41]], "could not be read"),
        })
        {
            using var world = new InstallWorld($"attestation-{purpose}");
            world.ServeAttestations(world.Publish(InstallWorld.NewestDaemon, "linux-x64"), bundles);
            var before = world.Tree();

            var result = await world.RunAsync();

            FailedAt(result, "attestation");
            result.Stderr.Should().Contain(says, purpose);
            NothingChanged(world, before);
        }
    }

    [Fact]
    public async Task A_gh_without_attestation_verify_is_refused_before_any_download_pointing_at_githubs_apt_repository()
    {
        Linux();
        using var world = new InstallWorld("gh-2.45");
        world.Override("gh", ["--version"], 0, "gh version 2.45.0 (2024-03-04)\n");
        world.Override(new FakeAnswer("gh", ["attestation", "verify", "--help"], 1, string.Empty, "unknown command \"attestation\" for \"gh\"\n"));
        var before = world.Tree();

        var result = await world.RunAsync();

        FailedAt(result, "preflight");
        result.Stderr.Should().Contain("2.45.0").And.Contain("2.56.0").And.Contain("https://cli.github.com/packages")
            .And.Contain("sh -s -- --skip-attestation");
        result.Stderr.Should().NotContain("apt-get install gh", "Ubuntu's own package is the one that is too old").And.NotContain("auth login");
        world.CallsOf("curl").Should().BeEmpty("refused BEFORE any download");
        NothingChanged(world, before);
    }

    [Fact]
    public async Task A_gh_whose_attestation_verify_fails_or_lacks_a_flag_the_check_uses_is_refused_before_any_download()
    {
        Linux();
        foreach (var (purpose, help, exit) in new[]
        {
            ("help-fails", string.Empty, 1),
            ("no-deny-flag", InstallWorld.GhVerifyHelp.Replace("--deny-self-hosted-runners", "--deny-self-hosted", StringComparison.Ordinal), 0),
        })
        {
            using var world = new InstallWorld($"gh-{purpose}");
            world.Override("gh", ["attestation", "verify", "--help"], exit, help);
            var before = world.Tree();

            var result = await world.RunAsync();

            FailedAt(result, "preflight");
            result.Stderr.Should().Contain("2.97.0").And.Contain("https://cli.github.com/packages", purpose);
            world.CallsOf("curl").Should().BeEmpty(purpose);
            NothingChanged(world, before);
        }
    }

    [Fact]
    public async Task A_gh_that_has_attestation_verify_but_is_older_than_the_measured_floor_is_refused_before_any_download()
    {
        Linux();
        using var world = new InstallWorld("gh-2.49");
        world.Override("gh", ["--version"], 0, "gh version 2.49.0 (2024-04-30)\n");
        var before = world.Tree();

        var result = await world.RunAsync();

        FailedAt(result, "preflight");
        result.Stderr.Should().Contain("2.49.0").And.Contain("2.56.0");
        world.CallsOf("curl").Should().BeEmpty();
        NothingChanged(world, before);
    }

    [Fact]
    public void The_identity_the_installer_pins_is_this_repositorys_attesting_release_workflow_at_the_release_tag()
    {
        var script = File.ReadAllText(ShippedFiles.InstallScript);
        script.Should().Contain("readonly SIGNER_WORKFLOW=\"$REPO/.github/workflows/release.yml\"");
        script.Should().Contain("SIGNER_IDENTITY=\"https://github.com/$SIGNER_WORKFLOW@refs/tags/daemon-v$VERSION\"", "the exact certificate identity: release.yml at the tag of the version installed");
        script.Split('\n').Where(line => !line.TrimStart().StartsWith('#')).Should().NotContain(
            line => line.Contains("--signer-workflow", StringComparison.Ordinal),
            "gh matches a signer workflow as a PREFIX of the identity, so release.yml built from any branch would pass");
        File.ReadAllText(ReleaseFiles.Workflow("release.yml")).Should().Contain("actions/attest-build-provenance@", "the workflow the installer trusts is the one that attests");
    }
}
