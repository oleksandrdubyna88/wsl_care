using System.Runtime.Versioning;

using FluentAssertions;

using WslCare.TestSupport;

using static WslCare.Scenarios.InstallChecks;

namespace WslCare.Scenarios;

/// <summary>
/// <c>install.sh</c> end to end (plan §9, §15 #12, §15a #3, §15c #2, §15e #1/#3/#4; E4.S1): the real script under the
/// real <c>/bin/sh</c>, over a temporary prefix, with curl / gh / systemctl / apt-get faked (<see cref="InstallWorld"/>).
/// Every guarantee of the install trust boundary is one flow in an <c>Install*Flows</c> class; this one holds a fresh
/// install — the release it picks and downloads, the checksum, the machine layer, the verify steps, a dry run and the
/// preflight refusals. The attestation, the upgrade, the uninstall and the default user have a class each.
/// </summary>
/// <remarks>Linux only: the script is POSIX sh over GNU coreutils and tar, which the Linux CI legs have and the Windows
/// leg does not. Run by hand in WSL from a copy of the worktree under <c>/tmp</c>, as the test user — never as root, so a
/// path that escaped the prefix would be refused by the operating system.</remarks>
[SupportedOSPlatform("linux")]
public sealed class InstallFlows
{
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

        foreach (var unit in Core.Systemd.UnitDropIns.Units)
        {
            var dropIn = world.At($"/etc/systemd/system/{unit}.d/{Core.Systemd.UnitDropIns.FileName}");
            File.ReadAllText(dropIn).Should().Be(Core.Systemd.UnitDropIns.Defaults(unit), $"E7.S2c: {unit}'s drop-in is what the installed binary rendered");
            File.GetUnixFileMode(dropIn).Should().Be(InstallWorld.Regular, "install -m 0644");
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
        world.CallsOf("wsl-care").Select(c => string.Join(' ', c.Argv)).Should().Equal(
            [.. Core.Systemd.UnitDropIns.Units.Select(u => $"units dropin {u}"), "collect", "doctor --json"],
            "each unit's drop-in rendered by the installed binary (E7.S2c), one full run, then the health verdict");
        world.StubInvocations.Should().OnlyContain(p => p == binary, "the installer runs the binary by its absolute path, never through PATH (plan §15e #3)")
            .And.HaveCount(Core.Systemd.UnitDropIns.Units.Count + 2);
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

    /// <summary>E7.S2c: a drop-in the installed binary could not render fails the install at that step — the units are never
    /// enabled over a configuration the binary refused to say.</summary>
    [Fact]
    public async Task A_drop_in_the_binary_cannot_render_fails_the_install_before_any_unit_is_enabled()
    {
        Linux();
        using var world = new InstallWorld("dropin-fails");
        world.Override("wsl-care", ["units", "dropin", "wsl-care.timer"], 70);

        var result = await world.RunAsync();

        FailedAt(result, "install-units");
        result.Stderr.Should().Contain("units dropin wsl-care.timer failed");
        world.CallsOf("systemctl").Select(c => string.Join(' ', c.Argv)).Should().NotContain(c => c.StartsWith("enable", StringComparison.Ordinal), "the failed step stops the run");
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
            .And.Contain($"would write /etc/systemd/system/wsl-care.timer.d/{Core.Systemd.UnitDropIns.FileName} from: {InstallWorld.BinaryPath} units dropin wsl-care.timer")
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
}
