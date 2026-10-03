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
        world.CallsOf("curl").Select(c => c.Argv[1]).Should().Equal(
            [InstallWorld.ReleasesApi, InstallWorld.ReleaseUrl("0.1.0", $"{name}.tar.gz"), InstallWorld.ReleaseUrl("0.1.0", $"{name}.tar.gz.sha256")],
            "the newest daemon-v* release — the list's first entry is the extension's, which releases/latest would have handed out");
        var calls = world.Calls.Select(c => c.Tool).ToList();
        calls.IndexOf("gh").Should().BeLessThan(calls.IndexOf("systemctl"), "the attestation is verified before any unit is touched");
        var gh = world.CallsOf("gh").Should().ContainSingle().Subject;
        gh.Argv[^1].Should().StartWith(world.Temp).And.EndWith($"/{name}.tar.gz", "gh verifies the archive that was downloaded");
        world.CallsOf("curl").Should().OnlyContain(
            c => c.Argv.Contains("--proto") && c.Argv.Contains("--proto-redir") && c.Argv.Contains("=https") && c.Argv.Contains("--max-time"),
            "every download ASKS curl for https only, redirects included, under a ceiling — a request to curl, its effect is curl's");
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
        world.CallsOf("gh").Should().BeEmpty("a corrupted archive is not worth verifying");
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
        world.Override("gh", ["attestation", "verify"], 1, prefix: true);
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
            .And.Contain("gh auth login").And.Contain("sh -s -- --skip-attestation");
        world.CallsOf("curl").Should().BeEmpty("it stops BEFORE the download (plan §15e #4)");
        NothingChanged(world, before);
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
            "disable --now wsl-care.timer wsl-care-events.service", "stop wsl-care.service", "daemon-reload", "is-active --quiet wsl-care.timer");
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
        world.CallsOf("gh").Should().ContainSingle("a dry run still downloads and verifies — into a folder it removes");

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
    public async Task Under_sudo_the_attestation_is_verified_as_the_invoking_user_through_runuser()
    {
        Linux();
        using var world = new InstallWorld("sudo-user") { SudoUser = "alice" };
        world.Override("runuser", ["-u", "alice", "--", "gh", "attestation", "verify", "--repo", InstallWorld.Repo, "--signer-workflow", InstallWorld.SignerWorkflow], 0, prefix: true);

        Succeeded(await world.RunAsync());

        world.CallsOf("runuser").Should().ContainSingle("gh uses the login of the person who ran sudo");
        world.CallsOf("gh").Should().BeEmpty("the fake runuser stands for gh run as alice; root's own gh is not asked");
    }

    [Fact]
    public async Task An_upgrade_restarts_the_running_follower_onto_the_new_binary()
    {
        Linux();
        using var world = new InstallWorld("upgrade");
        world.Write(InstallWorld.BinaryPath, "#!/bin/sh\necho old\n");
        File.CreateSymbolicLink(world.At(InstallWorld.LinkPath).EnsureParent(), InstallWorld.BinaryPath);
        world.Override("systemctl", ["try-restart", "wsl-care-events.service"], 0);

        Succeeded(await world.RunAsync());

        File.ReadAllText(world.At(InstallWorld.BinaryPath)).Should().Be(world.StubScript());
        world.CallsOf("systemctl").Select(c => string.Join(' ', c.Argv)).Should().ContainInOrder(
            "daemon-reload", "try-restart wsl-care-events.service", string.Join(' ', EnableOurUnits));
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
    public void The_signer_workflow_the_installer_pins_is_this_repositorys_release_workflow()
    {
        var workflow = Path.Combine(ShippedFiles.RepositoryRoot, ".github", "workflows", "release.yml");
        Assert.SkipUnless(File.Exists(workflow), "release.yml is E4.S2's: until it exists no release is attested, and this check has nothing to compare");
        File.ReadAllText(ShippedFiles.InstallScript).Should().Contain($"readonly SIGNER_WORKFLOW=\"$REPO/.github/workflows/release.yml\"");
        File.ReadAllText(workflow).Should().Contain("attest-build-provenance", "the workflow the installer trusts is the one that attests");
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
        world.Override("systemctl", ["is-active", "--quiet", "wsl-care.timer"], 3);
    }
}

internal static class InstallPathExtensions
{
    /// <summary>Creates the parent folder of <paramref name="path"/> and returns the path.</summary>
    public static string EnsureParent(this string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        return path;
    }
}
