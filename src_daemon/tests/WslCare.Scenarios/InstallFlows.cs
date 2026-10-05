using System.Runtime.Versioning;
using System.Text;

using FluentAssertions;

using WslCare.Core.Actions;
using WslCare.FakeTool;
using WslCare.TestSupport;

namespace WslCare.Scenarios;

/// <summary>
/// <c>install.sh</c> end to end (plan §9, §15 #12, §15a #3, §15c #2, §15e #1/#3/#4; E4.S1): the real script under the
/// real <c>/bin/sh</c>, over a temporary prefix, with curl / gh / systemctl / apt-get faked (<see cref="InstallWorld"/>).
/// Every guarantee of the install trust boundary is one flow here.
/// </summary>
/// <remarks>Linux only: the script is POSIX sh over GNU coreutils and tar, which the Linux CI legs have and the Windows
/// leg does not. Run by hand in WSL from a copy of the worktree under <c>/tmp</c>, as the test user — never as root, so a
/// path that escaped the prefix would be refused by the operating system.</remarks>
[SupportedOSPlatform("linux")]
public sealed class InstallFlows
{
    private const string LinuxOnly = "install.sh is POSIX sh over GNU coreutils and tar: covered on the Linux legs (and by hand in WSL)";

    private static readonly string[] EnableOurUnits = ["enable", "--now", "wsl-care.timer", "wsl-care-events.service"];

    private static void Linux() => Assert.SkipUnless(OperatingSystem.IsLinux(), LinuxOnly);

    private static void Succeeded(ChildResult result) => result.Exit.Should().Be(0, $"the install should succeed:\n{result.Stdout}\n{result.Stderr}");

    private static void FailedAt(ChildResult result, string step)
    {
        result.Exit.Should().Be(1, $"the install should fail:\n{result.Stdout}\n{result.Stderr}");
        result.Stderr.Should().Contain($"FAILED at step \"{step}\"");
    }

    private static void NothingChanged(InstallWorld world, IReadOnlyDictionary<string, string> before)
    {
        world.Tree().Should().BeEquivalentTo(before, "nothing under the prefix may change");
        world.CallsOf("systemctl").Should().BeEmpty("no unit was touched");
        world.CallsOf("apt-get").Should().BeEmpty("no package was installed");
        world.StubInvocations.Should().BeEmpty("the binary was never placed, so never run");
        Directory.EnumerateFileSystemEntries(world.Temp).Should().BeEmpty("the temporary folder is removed on every exit");
    }

    private static byte[] Repo(string file) => File.ReadAllBytes(file);

    /// <summary>The <c>gh attestation verify</c> calls that verify (not the preflight's <c>--help</c>).</summary>
    private static IReadOnlyList<FakeCall> Verifications(InstallWorld world) =>
        [.. world.CallsOf("gh").Where(c => c.Argv is ["attestation", "verify", ..] && !c.Argv.Contains("--help"))];

    /// <summary>The value after <paramref name="flag"/> in <paramref name="call"/>, or empty.</summary>
    private static string ValueOf(FakeCall call, string flag)
    {
        var at = call.Argv.ToList().IndexOf(flag);
        return at >= 0 && at + 1 < call.Argv.Count ? call.Argv[at + 1] : string.Empty;
    }

    [Fact]
    public async Task A_fresh_install_places_the_binary_link_units_and_machine_layer_enables_both_units_and_verifies_through_the_absolute_path()
    {
        Linux();
        using var world = new InstallWorld("fresh");

        var result = await world.RunAsync();

        Succeeded(result);
        var binary = world.At(InstallWorld.BinaryPath);
        File.ReadAllText(binary).Should().Be(world.StubScript(), "the archive's binary is what was installed");
        File.GetUnixFileMode(binary).Should().Be(InstallWorld.Executable, "install -m 0755");
        new FileInfo(world.At(InstallWorld.LinkPath)).LinkTarget.Should().Be(InstallWorld.BinaryPath, "the link names the ABSOLUTE install path (plan §15e #3)");
        foreach (var unit in ShippedFiles.UnitNames)
        {
            var installed = world.At($"/etc/systemd/system/{unit}");
            File.ReadAllBytes(installed).Should().Equal(Repo(Path.Combine(ShippedFiles.SystemdDirectory, unit)), $"{unit} is this repository's unit, byte for byte");
            File.GetUnixFileMode(installed).Should().Be(InstallWorld.Regular, "install -m 0644");
        }

        File.ReadAllBytes(world.At("/etc/wsl-care/config.json")).Should().Equal(Repo(ShippedFiles.MachineConfig), "no machine layer existed, so the shipped one was written");
        Directory.Exists(world.At("/var/lib/wsl-care")).Should().BeTrue();
        Directory.Exists(world.At("/var/log/wsl-care")).Should().BeTrue();

        world.CallsOf("systemctl").Select(c => string.Join(' ', c.Argv)).Should().Equal(
            "daemon-reload",
            string.Join(' ', EnableOurUnits),
            "enable --now sysstat.service atop.service",
            "is-active --quiet wsl-care.timer",
            "is-active --quiet wsl-care-events.service");
        world.CallsOf("wsl-care").Select(c => string.Join(' ', c.Argv)).Should().Equal(["collect", "doctor --json"], "one full run, then the health verdict");
        world.StubInvocations.Should().OnlyContain(p => p == binary, "the installer runs the binary by its absolute path, never through PATH (plan §15e #3)")
            .And.HaveCount(2);
        world.CallsOf("sudo").Should().BeEmpty("the installer never calls sudo");
        world.CallsOf("runuser").Should().BeEmpty("without SUDO_USER, gh runs as the script itself");
        Directory.EnumerateFileSystemEntries(world.Temp).Should().BeEmpty("the temporary folder is removed on success too");
    }

    [Fact]
    public async Task The_newest_daemon_release_is_downloaded_never_the_extensions_and_verified_before_any_write()
    {
        Linux();
        using var world = new InstallWorld("order");

        Succeeded(await world.RunAsync());

        var name = InstallWorld.ReleaseName(InstallWorld.NewestDaemon, "linux-x64");
        world.CallsOf("curl").Select(c => c.Argv[1]).Take(3).Should().Equal(
            [InstallWorld.ReleasesApi, InstallWorld.ReleaseUrl("0.1.0", $"{name}.tar.gz"), InstallWorld.ReleaseUrl("0.1.0", $"{name}.tar.gz.sha256")],
            "the newest daemon-v* release — the list's first entry is the extension's, which releases/latest would have handed out");
        var calls = world.Calls.ToList();
        calls.FindIndex(c => Verifications(world).Contains(c)).Should().BeLessThan(calls.FindIndex(c => c.Tool == "systemctl"), "the attestation is verified before any unit is touched");
        var gh = Verifications(world).Should().ContainSingle().Subject;
        gh.Argv[2].Should().StartWith(world.Temp).And.EndWith($"/{name}.tar.gz", "gh verifies the archive that was downloaded");
        world.CallsOf("curl").Should().OnlyContain(
            c => c.Argv.Contains("--proto") && c.Argv.Contains("--proto-redir") && c.Argv.Contains("=https") && c.Argv.Contains("--max-time"),
            "every download ASKS curl for https only, redirects included, under a ceiling — a request to curl, its effect is curl's");
    }

    /// <summary>GitHub may answer the releases list as COMPACT JSON — every object on one line. A line-based reader then
    /// sees one line holding every tag; the newest daemon release must still be the one installed, by version number,
    /// never a pre-release and never the extension's.</summary>
    [Fact]
    public async Task The_newest_daemon_release_is_chosen_by_version_number_from_a_compact_releases_list()
    {
        Linux();
        using var world = new InstallWorld("compact-releases");
        const string compact =
            """[{"tag_name":"extension-v1.0.0","draft":false},{"tag_name": "daemon-v0.9.1","draft":false},{"tag_name":"daemon-v0.11.0-rc.1","prerelease":true},{"tag_name":"daemon-v0.10.0","draft":false},{"tag_name":"daemon-v0.1.0","draft":false}]""";
        world.Override(InstallWorld.Download(InstallWorld.ReleasesApi, world.Answer("releases-compact.json", compact)));
        world.Publish("0.10.0", "linux-x64");

        var result = await world.RunAsync();

        Succeeded(result);
        world.CallsOf("curl").Select(c => c.Argv[1]).Should().Contain(InstallWorld.ReleaseUrl("0.10.0", "wsl-care-0.10.0-linux-x64.tar.gz"),
            "0.10.0 is the highest daemon version: numerically above 0.9.1, and 0.11.0-rc.1 is a pre-release");
        result.Stdout.Should().Contain("release daemon-v0.10.0");
    }

    [Fact]
    public async Task An_explicit_version_skips_the_releases_list_and_a_malformed_one_is_refused_before_anything_runs()
    {
        Linux();
        using var world = new InstallWorld("version");
        world.Publish("0.0.9", "linux-x64");

        Succeeded(await world.RunAsync("--version", "0.0.9"));

        world.CallsOf("curl").Select(c => c.Argv[1]).Should().NotContain(InstallWorld.ReleasesApi);
        world.CallsOf("curl").Select(c => c.Argv[1]).Should().Contain(InstallWorld.ReleaseUrl("0.0.9", "wsl-care-0.0.9-linux-x64.tar.gz"));

        using var refused = new InstallWorld("version-bad");
        var before = refused.Tree();
        foreach (var bad in new[] { "0.1.0/../../x", "v0.1.0", "0.1.0;rm", "0.1.0\nx" })
        {
            var result = await refused.RunAsync("--version", bad);
            result.Exit.Should().Be(2, $"\"{bad}\" is not a version");
            result.Stderr.Should().Contain("--version must look like 1.2.3");
        }

        refused.Calls.Should().BeEmpty("a refused argument stops the script before it asks anything");
        NothingChanged(refused, before);
    }

    [Fact]
    public async Task A_checksum_mismatch_aborts_before_the_attestation_and_before_anything_is_installed()
    {
        Linux();
        using var world = new InstallWorld("checksum");
        world.Publish(InstallWorld.NewestDaemon, "linux-x64", sha256Line: $"{new string('0', 64)}  wsl-care-0.1.0-linux-x64.tar.gz\n");
        var before = world.Tree();

        var result = await world.RunAsync();

        FailedAt(result, "checksum");
        result.Stderr.Should().Contain("does not match its .sha256").And.Contain("nothing is installed");
        Verifications(world).Should().BeEmpty("a corrupted archive is not worth verifying");
        NothingChanged(world, before);
    }

    [Fact]
    public async Task A_release_without_its_sha256_is_refused_never_installed_unchecked()
    {
        Linux();
        using var world = new InstallWorld("no-sha");
        world.Override("curl", ["--url", InstallWorld.ReleaseUrl("0.1.0", "wsl-care-0.1.0-linux-x64.tar.gz.sha256")], 22, prefix: true);
        var before = world.Tree();

        var result = await world.RunAsync();

        FailedAt(result, "download");
        result.Stderr.Should().Contain(".sha256").And.Contain("nothing is installed");
        NothingChanged(world, before);
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
    public async Task An_existing_machine_configuration_is_kept_byte_for_byte()
    {
        Linux();
        using var world = new InstallWorld("keep-config");
        const string mine = "{ \"dryRun\": false } // the owner's own\n";
        world.Write("/etc/wsl-care/config.json", mine);

        var result = await world.RunAsync();

        Succeeded(result);
        File.ReadAllText(world.At("/etc/wsl-care/config.json")).Should().Be(mine, "plan §15e #1: an existing machine layer is never overwritten");
        result.Stdout.Should().Contain("kept /etc/wsl-care/config.json");
    }

    [Fact]
    public async Task A_unit_that_is_not_active_after_enabling_fails_the_install_naming_that_step()
    {
        Linux();
        using var world = new InstallWorld("inactive");
        world.Override("systemctl", ["is-active", "--quiet", "wsl-care-events.service"], 3);

        var result = await world.RunAsync();

        FailedAt(result, "verify: wsl-care-events.service active");
        result.Stderr.Should().Contain("part of the installation is in place").And.Contain("--uninstall");
        world.CallsOf("wsl-care").Select(c => string.Join(' ', c.Argv)).Should().NotContain("doctor --json", "the failed step stops the run");
    }

    [Fact]
    public async Task An_unhealthy_doctor_fails_the_install_naming_the_step_and_showing_the_checks()
    {
        Linux();
        using var world = new InstallWorld("unhealthy");
        world.Override("wsl-care", ["doctor", "--json"], 0, InstallWorld.DoctorJson(healthy: false));
        world.Override("wsl-care", ["doctor"], 0, "wsl-care doctor (wsl): NOT healthy\n  problem    eventsFollower: the events follower has recorded nothing\n");

        var result = await world.RunAsync();

        FailedAt(result, "verify: doctor healthy");
        result.Stderr.Should().Contain("problem    eventsFollower", "the person sees which check failed");
    }

    [Fact]
    public async Task Missing_sysstat_and_atop_are_installed_with_apt_and_a_tool_still_missing_afterwards_fails_its_verify_step()
    {
        Linux();
        using var world = new InstallWorld("apt", withoutTools: ["sar", "atop"]);
        world.Override("debconf-set-selections", [], 0, prefix: true);
        world.Override("apt-get", ["update", "-q"], 0);
        world.Override("apt-get", ["install", "-y", "-q", "--no-install-recommends", "-o", "DPkg::Lock::Timeout=300", "sysstat", "atop"], 0);

        var result = await world.RunAsync();

        world.CallsOf("debconf-set-selections").Should().ContainSingle("sysstat collects only once its debconf switch is on");
        world.CallsOf("apt-get").Select(c => string.Join(' ', c.Argv)).Should().Equal(
            "update -q",
            "install -y -q --no-install-recommends -o DPkg::Lock::Timeout=300 sysstat atop");
        FailedAt(result, "verify: sysstat (sar on PATH)");
    }

    [Fact]
    public async Task Sysstat_switched_off_is_switched_on_through_debconf_and_the_install_fails_if_it_stays_off()
    {
        Linux();
        using var world = new InstallWorld("sysstat-off");
        world.Write("/etc/default/sysstat", "ENABLED=\"false\"\n");
        world.Override("debconf-set-selections", [], 0, prefix: true);
        world.Override("dpkg-reconfigure", ["-f", "noninteractive", "sysstat"], 0);

        var result = await world.RunAsync();

        world.CallsOf("apt-get").Should().BeEmpty("both tools are installed");
        world.CallsOf("dpkg-reconfigure").Should().ContainSingle();
        FailedAt(result, "packages");
        result.Stderr.Should().Contain("sysstat collection is still off");
    }

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
    public async Task Dry_run_changes_nothing_needs_no_root_and_prints_every_step()
    {
        Linux();
        using var world = new InstallWorld("dry-run");
        world.Override("id", ["-u"], 0, "1000\n");
        var before = world.Tree();

        var result = await world.RunAsync("--dry-run", "--set-default-user", "alice");

        Succeeded(result);
        NothingChanged(world, before);
        world.CallsOf("debconf-set-selections").Should().BeEmpty();
        result.Stdout.Should().Contain("dry run: nothing will be changed, and root is not needed")
            .And.Contain($"would run: install -m 0755 ")
            .And.Contain("would run: install -m 0644 ")
            .And.Contain("would add [user] default=alice to /etc/wsl.conf")
            .And.Contain($"would run: systemctl {string.Join(' ', EnableOurUnits)}")
            .And.Contain($"would run: {InstallWorld.BinaryPath} collect")
            .And.Contain("would verify:")
            .And.Contain("dry run: nothing was changed");
        Verifications(world).Should().ContainSingle("a dry run still downloads and verifies — into a folder it removes");

        world.Override("id", ["-u"], 0, "0\n");
        Succeeded(await world.RunAsync("--skip-attestation"));
        var installed = world.Tree();
        var uninstall = await world.RunAsync("--uninstall", "--purge", "--dry-run");
        Succeeded(uninstall);
        world.Tree().Should().BeEquivalentTo(installed, "an uninstall dry run removes nothing either");
        uninstall.Stdout.Should().Contain("would run: rm -rf -- ").And.Contain("dry run: nothing was changed");
    }

    [Fact]
    public async Task A_non_root_run_is_refused_with_the_sudo_line_and_never_calls_sudo_itself()
    {
        Linux();
        using var world = new InstallWorld("not-root");
        world.Override("id", ["-u"], 0, "1000\n");
        var before = world.Tree();

        var result = await world.RunAsync("--skip-attestation");

        FailedAt(result, "preflight");
        result.Stderr.Should().Contain("run it as root").And.Contain("| sudo sh -s -- --skip-attestation");
        world.CallsOf("sudo").Should().BeEmpty("no silent sudo");
        world.CallsOf("curl").Should().BeEmpty();
        NothingChanged(world, before);
    }

    [Fact]
    public async Task Without_systemd_running_the_installer_refuses_and_names_the_setting_it_never_writes_itself()
    {
        Linux();
        using var world = new InstallWorld("no-systemd");
        Directory.Delete(world.At("/run/systemd"), recursive: true);
        var before = world.Tree();

        var result = await world.RunAsync();

        FailedAt(result, "preflight");
        result.Stderr.Should().Contain("systemd is not running").And.Contain("systemd=true").And.Contain("yourself");
        NothingChanged(world, before);
    }

    [Fact]
    public async Task On_arm64_the_linux_arm64_archive_is_installed_and_an_unknown_architecture_is_refused()
    {
        Linux();
        using var world = new InstallWorld("arm64");
        world.Override("uname", ["-m"], 0, "aarch64\n");
        world.Publish(InstallWorld.NewestDaemon, "linux-arm64");

        Succeeded(await world.RunAsync());
        world.CallsOf("curl").Select(c => c.Argv[1]).Should().Contain(InstallWorld.ReleaseUrl("0.1.0", "wsl-care-0.1.0-linux-arm64.tar.gz"))
            .And.NotContain(u => u.Contains("linux-x64", StringComparison.Ordinal));

        using var other = new InstallWorld("riscv");
        other.Override("uname", ["-m"], 0, "riscv64\n");
        var before = other.Tree();
        var refused = await other.RunAsync();
        FailedAt(refused, "preflight");
        refused.Stderr.Should().Contain("unsupported architecture riscv64");
        NothingChanged(other, before);
    }

    [Fact]
    public async Task An_archive_member_that_leaves_its_folder_or_is_a_link_is_refused_before_anything_is_installed()
    {
        Linux();
        // Each hostile member rides along a COMPLETE real release, so only the member check can refuse it — tar's own
        // refusals and the missing-file check would otherwise answer "unpack" too, and the test could not tell.
        var hostile = new (string Purpose, Action<System.Formats.Tar.TarWriter, string> Extra, string Reason)[]
        {
            ("dotdot", (tar, name) => InstallWorld.AddFile(tar, $"{name}/../escaped", [1], InstallWorld.Regular), "a member leaves the archive's folder"),
            ("outside", (tar, _) => InstallWorld.AddFile(tar, "elsewhere/file", [1], InstallWorld.Regular), "a member outside wsl-care-0.1.0-linux-x64/"),
            ("link", (tar, name) => tar.WriteEntry(new System.Formats.Tar.UstarTarEntry(System.Formats.Tar.TarEntryType.SymbolicLink, $"{name}/shadow") { LinkName = "/etc/shadow" }), "holds a link or a special file"),
        };
        foreach (var (purpose, extra, reason) in hostile)
        {
            using var world = new InstallWorld($"archive-{purpose}");
            world.Publish(InstallWorld.NewestDaemon, "linux-x64", build: (tar, name) =>
            {
                world.WriteRealRelease(tar, name);
                extra(tar, name);
            });
            var before = world.Tree();

            var result = await world.RunAsync();

            FailedAt(result, "unpack");
            result.Stderr.Should().Contain(reason, $"the {purpose} member is refused by the member check");
            NothingChanged(world, before);
        }
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
    public async Task An_upgrade_restarts_the_running_follower_onto_the_new_binary()
    {
        Linux();
        using var world = new InstallWorld("upgrade");
        world.Write(InstallWorld.BinaryPath, "#!/bin/sh\necho old\n");
        world.Link(InstallWorld.LinkPath, InstallWorld.BinaryPath);
        world.Override("systemctl", ["try-restart", "wsl-care-events.service"], 0);

        Succeeded(await world.RunAsync());

        File.ReadAllText(world.At(InstallWorld.BinaryPath)).Should().Be(world.StubScript());
        world.CallsOf("systemctl").Select(c => string.Join(' ', c.Argv)).Should().ContainInOrder(
            "daemon-reload", "try-restart wsl-care-events.service", string.Join(' ', EnableOurUnits));
    }

    // ---------- E6.S1: never under a run in flight (plan §15k #16) ----------

    /// <summary>The installed binary's <c>status --json</c>, as far as the installer reads it: the running block's state.</summary>
    private static string OldBinaryAnswering(string state) =>
        $$"""
        #!/bin/sh
        cat <<'EOF'
        {
          "schemaVersion": 1,
          "running": {
            "state": "{{state}}",
            "reason": "a test",
            "runId": "20261004T120000Z-4242"
          }
        }
        EOF

        """;

    [Theory]
    [InlineData("live")]
    [InlineData("queued")]
    [InlineData("wedged")]
    public async Task An_upgrade_under_a_run_in_flight_waits_bounded_then_refuses_naming_it_and_replaces_nothing(string state)
    {
        Linux();
        using var world = new InstallWorld($"upgrade-wait-{state}") { RunWaitSeconds = "0" };
        world.Write(InstallWorld.BinaryPath, OldBinaryAnswering(state));
        File.SetUnixFileMode(world.At(InstallWorld.BinaryPath), InstallWorld.Executable);
        world.Link(InstallWorld.LinkPath, InstallWorld.BinaryPath);
        var before = File.ReadAllText(world.At(InstallWorld.BinaryPath));

        var result = await world.RunAsync();

        FailedAt(result, "upgrade-wait");
        result.Stderr.Should().Contain($"a wsl-care run is {state}").And.Contain("still after").And.Contain("nothing was replaced")
            .And.NotContain("SKIP_RUN_WAIT", "the manual escape is only for a binary that cannot answer");
        File.ReadAllText(world.At(InstallWorld.BinaryPath)).Should().Be(before, "the running binary stays");
        File.Exists(world.At(InstallWorld.BinaryPath + ".new")).Should().BeFalse();
        world.CallsOf("systemctl").Should().BeEmpty("no unit was touched");
    }

    /// <summary>E6.S1 review S4: the wait failed OPEN — a status that crashed, timed out or printed nothing read as "nothing in
    /// flight" and the upgrade went ahead under whatever was running. No answer counts as in flight now; only an answer without a
    /// running block (a binary older than E6.S0) proceeds.</summary>
    [Theory]
    [InlineData("#!/bin/sh\nexit 70\n")]
    [InlineData("#!/bin/sh\nexit 0\n")]
    public async Task An_upgrade_whose_installed_binary_gives_no_status_answer_waits_then_refuses(string oldBinary)
    {
        Linux();
        using var world = new InstallWorld("upgrade-no-answer") { RunWaitSeconds = "0" };
        world.Write(InstallWorld.BinaryPath, oldBinary);
        File.SetUnixFileMode(world.At(InstallWorld.BinaryPath), InstallWorld.Executable);
        world.Link(InstallWorld.LinkPath, InstallWorld.BinaryPath);

        var result = await world.RunAsync();

        FailedAt(result, "upgrade-wait");
        result.Stderr.Should().Contain("the installed binary gave no status answer");
        // coai E6 code round #5: the binary cannot answer, so advice that runs it again is useless — the manual escape is named.
        result.Stderr.Should().Contain("remove /var/lib/wsl-care/running.json and /var/lib/wsl-care/requests/*.json by hand")
            .And.Contain("WSL_CARE_INSTALL_SKIP_RUN_WAIT=1").And.NotContain("sudo wsl-care collect");
        File.ReadAllText(world.At(InstallWorld.BinaryPath)).Should().Be(oldBinary);
    }

    [Fact]
    public async Task The_escape_skips_the_wait_for_an_installed_binary_that_cannot_answer_and_says_so()
    {
        Linux();
        using var world = new InstallWorld("upgrade-skip-wait") { RunWaitSeconds = "0", SkipRunWait = true };
        world.Write(InstallWorld.BinaryPath, "#!/bin/sh\nexit 70\n");
        File.SetUnixFileMode(world.At(InstallWorld.BinaryPath), InstallWorld.Executable);
        world.Link(InstallWorld.LinkPath, InstallWorld.BinaryPath);
        world.Override("systemctl", ["try-restart", "wsl-care-events.service"], 0);

        var result = await world.RunAsync();

        Succeeded(result);
        result.Stderr.Should().Contain("WSL_CARE_INSTALL_SKIP_RUN_WAIT=1: not checking");
        File.ReadAllText(world.At(InstallWorld.BinaryPath)).Should().Be(world.StubScript());
    }

    /// <summary>coai E6 code round #1: the wait decides from the STATE, never from the layout — each running-state golden (what
    /// the binary really prints) is put through the real guard: none / dead proceed, every other state waits, and a state with no
    /// decision here fails this test before it can silently switch the wait off.</summary>
    private static readonly IReadOnlyDictionary<string, bool> Waits = new Dictionary<string, bool>(StringComparer.Ordinal)
    {
        [Core.Status.RunningStateName.None] = false,
        [Core.Status.RunningStateName.Dead] = false,
        [Core.Status.RunningStateName.Queued] = true,
        [Core.Status.RunningStateName.Live] = true,
        [Core.Status.RunningStateName.Wedged] = true,
        [Core.Status.RunningStateName.Unknown] = true,
        [Core.Status.RunningStateName.Unreadable] = true,
    };

    public static TheoryData<string> StatusGoldens() =>
        [.. Directory.GetFiles(Path.Combine(ShippedFiles.RepositoryRoot, "contracts", "golden", "head"), "status*.json").Select(Path.GetFileName).OfType<string>().Order(StringComparer.Ordinal)];

    [Fact]
    public void Every_running_state_has_a_wait_decision()
    {
        Waits.Keys.Should().BeEquivalentTo(Core.Status.RunningStateName.All, "a new state needs its decision here — and in install.sh, whose guard fails closed");
    }

    [Theory]
    [MemberData(nameof(StatusGoldens))]
    public async Task The_upgrade_wait_decides_every_running_state_golden_by_its_state_never_by_its_layout(string golden)
    {
        Linux();
        var path = Path.Combine(ShippedFiles.RepositoryRoot, "contracts", "golden", "head", golden);
        var state = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(path))!["running"]!["state"]!.GetValue<string>();
        Waits.Should().ContainKey(state, $"{golden} holds a state with no decision");
        foreach (var compact in new[] { false, true })
        {
            using var world = new InstallWorld($"upgrade-golden-{state}-{(compact ? "compact" : "indented")}") { RunWaitSeconds = "0" };
            var text = compact ? System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(path))!.ToJsonString() : File.ReadAllText(path);
            world.Write(InstallWorld.BinaryPath, $"#!/bin/sh\ncat '{world.Answer($"{golden}.{compact}", text)}'\n");
            File.SetUnixFileMode(world.At(InstallWorld.BinaryPath), InstallWorld.Executable);
            world.Link(InstallWorld.LinkPath, InstallWorld.BinaryPath);
            world.Override("systemctl", ["try-restart", "wsl-care-events.service"], 0);

            var result = await world.RunAsync();

            if (Waits[state])
            {
                FailedAt(result, "upgrade-wait");
                result.Stderr.Should().Contain($"a wsl-care run is {state}", $"{golden} ({(compact ? "compact" : "indented")})");
            }
            else
            {
                Succeeded(result);
            }
        }
    }

    /// <summary>coai E6 code round #4: a wait of up to 10 minutes says it is still waiting — the state, the run and the time.</summary>
    [Fact]
    public async Task A_long_wait_says_every_progress_period_what_it_waits_for_and_how_long()
    {
        Linux();
        using var world = new InstallWorld("upgrade-progress") { RunWaitSeconds = "6", ProgressSeconds = "1" };
        world.Write(InstallWorld.BinaryPath, OldBinaryAnswering("live"));
        File.SetUnixFileMode(world.At(InstallWorld.BinaryPath), InstallWorld.Executable);
        world.Link(InstallWorld.LinkPath, InstallWorld.BinaryPath);

        var result = await world.RunAsync();

        FailedAt(result, "upgrade-wait");
        result.Stdout.Should().Contain("still waiting: live 20261004T120000Z-4242, ").And.Contain("s of 6s");
    }

    /// <summary>coai E6 code round #8: the ceiling is WALL time — counting the 5 s sleeps let a status that hangs 30 s per call
    /// stretch 600 s to about 70 minutes. Here every call takes 6 s: counted sleeps refuse after ~28 s, the wall clock at ~17 s.</summary>
    [Fact]
    public async Task The_wait_ceiling_is_measured_on_the_wall_clock_not_by_counting_sleeps()
    {
        Linux();
        using var world = new InstallWorld("upgrade-wall-clock") { RunWaitSeconds = "10" };
        world.Write(InstallWorld.BinaryPath, "#!/bin/sh\nsleep 6\n" + OldBinaryAnswering("live")["#!/bin/sh\n".Length..]);
        File.SetUnixFileMode(world.At(InstallWorld.BinaryPath), InstallWorld.Executable);
        world.Link(InstallWorld.LinkPath, InstallWorld.BinaryPath);

        var started = DateTime.UtcNow;
        var result = await world.RunAsync();

        FailedAt(result, "upgrade-wait");
        (DateTime.UtcNow - started).Should().BeLessThan(TimeSpan.FromSeconds(24), "the refusal comes at the advertised ceiling plus one status call and one sleep");
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

    [Fact]
    public async Task An_upgrade_with_nothing_in_flight_renames_the_new_binary_over_the_old_and_a_queued_request_survives_it()
    {
        Linux();
        using var world = new InstallWorld("upgrade-request");
        world.Write(InstallWorld.BinaryPath, OldBinaryAnswering("none"));
        File.SetUnixFileMode(world.At(InstallWorld.BinaryPath), InstallWorld.Executable);
        world.Link(InstallWorld.LinkPath, InstallWorld.BinaryPath);
        world.Override("systemctl", ["try-restart", "wsl-care-events.service"], 0);
        // An E6.S0-shaped request (no "shown"): the schema stays 1 and additive, so the new binary reads what the old one wrote.
        const string request = """{"schemaVersion":1,"runId":"20261004T120000Z-4321","kind":"act","actions":["A10"],"trigger":"manual","createdAt":"2026-10-04T12:00:00+00:00"}""";
        world.Write("/var/lib/wsl-care/requests/20261004T120000Z-4321.json", request);

        var result = await world.RunAsync();

        Succeeded(result);
        File.ReadAllText(world.At(InstallWorld.BinaryPath)).Should().Be(world.StubScript());
        File.Exists(world.At(InstallWorld.BinaryPath + ".new")).Should().BeFalse("renamed over the old one, never left beside it");
        File.ReadAllText(world.At("/var/lib/wsl-care/requests/20261004T120000Z-4321.json")).Should().Be(request, "an upgrade never touches the request folder");
        var parsed = System.Text.Json.JsonSerializer.Deserialize(request, Core.Json.WslCareJsonContext.Default.RunRequestFile)!;
        parsed.RunId.Text.Should().Be("20261004T120000Z-4321");
        world.StubInvocations.Should().NotBeEmpty();
    }

    [Fact]
    public async Task The_binary_goes_in_beside_the_old_one_and_is_renamed_over_it_in_one_step()
    {
        Linux();
        using var world = new InstallWorld("rename");

        var result = await world.RunAsync("--dry-run");

        Succeeded(result);
        result.Stdout.Should().Contain($"/wsl-care {world.At(InstallWorld.BinaryPath)}.new")
            .And.Contain($"mv -f {world.At(InstallWorld.BinaryPath)}.new {world.At(InstallWorld.BinaryPath)}");
    }

    [Fact]
    public async Task A_wsl_care_on_the_link_path_that_is_not_the_installers_link_is_refused_before_anything_is_installed()
    {
        Linux();
        using var world = new InstallWorld("foreign-link");
        world.Write(InstallWorld.LinkPath, "#!/bin/sh\necho someone else's\n");
        var before = world.Tree();

        var result = await world.RunAsync();

        FailedAt(result, "preflight");
        result.Stderr.Should().Contain("is not this installer's link");
        NothingChanged(world, before);
    }

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

    /// <summary>What a world's systemctl answers during an uninstall.</summary>
    private static void ScriptUninstall(InstallWorld world)
    {
        world.Override("systemctl", ["disable", "--now", "wsl-care.timer", "wsl-care-events.service"], 0);
        world.Override("systemctl", ["stop", "wsl-care.service"], 0);
        world.Override("systemctl", ["stop", "wsl-care-act@*.service"], 0);
        world.Override("systemctl", ["is-active", "--quiet", "wsl-care.timer"], 3);
    }
}
