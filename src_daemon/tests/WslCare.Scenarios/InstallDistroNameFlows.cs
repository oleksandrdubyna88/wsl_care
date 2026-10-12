using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;

using FluentAssertions;

using WslCare.Cli;
using WslCare.Core.Collect;
using WslCare.Core.Health;
using WslCare.Core.Json;
using WslCare.TestSupport;

using static WslCare.Scenarios.InstallChecks;

namespace WslCare.Scenarios;

/// <summary>
/// <c>install.sh --distro-name</c> (E15.S2b): the installer VALIDATES this distro's WSL name by WSL's rule and hands it to its first full
/// run as <c>WSL_DISTRO_NAME</c>; the root <c>collect</c> records it through <see cref="DistroIdentity.Record"/> — the file's one writer —
/// so the timer's runs, which have no such variable, find this distro's own virtual disk from the first day. An empty value (the
/// one-liner run outside WSL) records nothing and says so; a name outside the rule, an argument without its value, and the argument
/// beside <c>--uninstall</c> are usage errors before anything changes. See <see cref="InstallFlows"/> for the harness.
/// </summary>
/// <remarks>
/// <para>The first full run here is the BUILT CLI, not the fake: the installed stub hands <c>collect</c> to it, with the environment
/// install.sh gave it plus a captured scenario's sandbox (<see cref="CollectFlows.Captured"/>, claiming root) — so "the name was
/// recorded" is the product's own <c>collect</c> writing the product's own file, and "nothing was recorded" is a <c>collect</c> that
/// WOULD have written it had the variable arrived. The world removes an inherited <c>WSL_DISTRO_NAME</c>, as sudo's
/// <c>env_reset</c> does.</para>
/// <para>Linux only: the script is POSIX sh over GNU coreutils and tar, which the Linux CI legs have and the Windows leg does not.
/// Run by hand in WSL from a copy of the worktree under <c>/tmp</c>, as the test user.</para>
/// </remarks>
[SupportedOSPlatform("linux")]
public sealed class InstallDistroNameFlows
{
    /// <summary>The longest name WSL's rule allows: one letter or digit, then 63 more.</summary>
    private static readonly string Longest = "U" + new string('b', 63);

    /// <summary>Names the rule refuses (the design's table, plus a leading dot, a newline and a letter outside ASCII).</summary>
    private static readonly string[] Refused = ["-x", "a b", "a/b", "a;b", Longest + "b", "$(x)", ".x", "a\nb", "Ubuntué"];

    /// <summary>Names the rule accepts.</summary>
    private static readonly string[] Accepted = ["Ubuntu", "Ubuntu-24.04", "u", "9", "x_y.z", Longest];

    private static CollectReport Report(ChildResult result) =>
        JsonSerializer.Deserialize(result.Stdout, WslCareJsonContext.Default.CollectReport) ?? throw new InvalidOperationException($"collect printed no report: {result.Stderr}");

    /// <summary>The name in <c>{state}/distro-name.json</c>, read with the product's own serializer — or empty when there is no file.</summary>
    private static string RecordedName(ScenarioHome home)
    {
        var file = DistroIdentity.File(home.Paths);
        return File.Exists(file)
            ? JsonSerializer.Deserialize(File.ReadAllBytes(file), WslCareJsonContext.Default.DistroNameFile)!.Name
            : string.Empty;
    }

    /// <summary>A captured scenario that claims root, and the world's installed binary handing <c>collect</c> to the BUILT CLI in that
    /// scenario's sandbox — keeping <c>WSL_DISTRO_NAME</c> as install.sh passed it (or did not).</summary>
    private static ScenarioHome RealFirstRun(InstallWorld world, string purpose)
    {
        var home = CollectFlows.Captured(purpose);
        home.ClaimsRoot = true;
        var prelude = new StringBuilder("if [ \"$1\" = collect ]; then\n");
        foreach (var (name, value) in home.Environment.Where(v => v.Key != DistroIdentity.Variable))
        {
            prelude.Append(value is null ? $"  unset {name}\n" : $"  {name}={Quoted(value)}; export {name}\n");
        }

        prelude.Append($"  TMPDIR={Quoted(home.WorkingDirectory)}; export TMPDIR\n");
        prelude.Append($"  exec {Quoted(ChildProcess.BesideTheTests(CommandLine.BinaryName))} \"$@\"\nfi\n");
        world.StubPrelude = prelude.ToString();
        world.Publish(InstallWorld.NewestDaemon, "linux-x64");
        return home;
    }

    private static string Quoted(string value) =>
        value.Contains('\'', StringComparison.Ordinal) ? throw new ArgumentException($"no quote expected in {value}") : $"'{value}'";

    [Fact]
    public async Task With_a_distro_name_the_first_full_run_records_it_and_the_timers_next_run_maps_this_distros_disk_by_it()
    {
        Linux();
        using var world = new InstallWorld("distro-name");
        using var home = RealFirstRun(world, "install-distro-name");

        var result = await world.RunAsync("--distro-name", "Ubuntu");

        Succeeded(result);
        result.Stdout.Should().Contain("recording the distro name Ubuntu (through the first full run");
        world.StubInvocations.Should().Contain(world.At(InstallWorld.BinaryPath), "the first full run is the installed binary's");
        RecordedName(home).Should().Be("Ubuntu", "the root collect that install.sh started recorded the name it was handed");

        var next = await home.RunAsync("collect", "--json");

        next.Exit.Should().Be((int)ExitCode.Ok, next.Stderr);
        var disk = Report(next).Detail!.Disk!;
        disk.DistroName!.Value.Should().Be("Ubuntu", "a run without WSL_DISTRO_NAME — the timer's — reads the recorded name");
        disk.Vhdx!.Single(v => v.Name == "Ubuntu").MappedBy.Should().Be("mount", "this distro's own disk is mapped from the first day");
    }

    /// <summary>E15.S2b plan round: the longest name WSL allows passes install.sh's rule AND the daemon's — the first full run records
    /// it (<see cref="DistroIdentity.Record"/> refuses a name outside its rule, so a recorded file is the daemon's acceptance).</summary>
    [Fact]
    public async Task The_longest_name_wsl_allows_is_accepted_by_the_installer_and_recorded_by_the_daemon()
    {
        Linux();
        using var world = new InstallWorld("distro-name-longest");
        using var home = RealFirstRun(world, "install-distro-name-longest");

        var result = await world.RunAsync("--distro-name", Longest);

        Succeeded(result);
        Longest.Should().HaveLength(64);
        RecordedName(home).Should().Be(Longest);
    }

    [Fact]
    public async Task An_empty_distro_name_records_nothing_and_says_why()
    {
        Linux();
        using var world = new InstallWorld("distro-name-empty");
        using var home = RealFirstRun(world, "install-distro-name-empty");

        var result = await world.RunAsync("--distro-name", string.Empty);

        Succeeded(result);
        result.Stdout.Should().Contain("--distro-name is empty").And.Contain("no distro name is recorded").And.Contain("wsl.exe");
        result.Stdout.Should().NotContain("was not set", "the installer knows only that the argument is empty, not why (code round)");
        File.Exists(DistroIdentity.File(home.Paths)).Should().BeFalse("an empty value is no name");
    }

    [Fact]
    public async Task An_empty_distro_name_records_nothing_even_when_root_inherited_the_variable()
    {
        Linux();
        using var world = new InstallWorld("distro-name-empty-inherited");
        using var home = RealFirstRun(world, "install-distro-name-empty-inherited");
        world.Variables[DistroIdentity.Variable] = "Ubuntu";

        var result = await world.RunAsync("--distro-name", string.Empty);

        Succeeded(result);
        File.Exists(DistroIdentity.File(home.Paths)).Should().BeFalse("an explicitly empty --distro-name means no name, so the first run drops an inherited WSL_DISTRO_NAME (code round)");
    }

    [Fact]
    public async Task Without_the_argument_a_root_shell_from_wsl_exe_records_the_name_wsl_set()
    {
        Linux();
        using var world = new InstallWorld("distro-name-inherited");
        using var home = RealFirstRun(world, "install-distro-name-inherited");
        world.Variables[DistroIdentity.Variable] = "Ubuntu";

        var result = await world.RunAsync();

        Succeeded(result);
        RecordedName(home).Should().Be("Ubuntu", "root entered through wsl.exe -u root carries the variable WSL set for this distro — the same trust as any root collect through wsl.exe (code round, a reasoned rejection)");
    }

    [Fact]
    public async Task Without_the_argument_the_first_full_run_records_nothing_and_nothing_is_said_about_it()
    {
        Linux();
        using var world = new InstallWorld("distro-name-none");
        using var home = RealFirstRun(world, "install-distro-name-none");

        var result = await world.RunAsync();

        Succeeded(result);
        result.Stdout.Should().NotContain("distro name");
        File.Exists(DistroIdentity.File(home.Paths)).Should().BeFalse("sudo removed WSL_DISTRO_NAME and no --distro-name was given");
    }

    [Fact]
    public async Task A_name_outside_wsls_rule_is_refused_before_anything_changes()
    {
        Linux();
        using var world = new InstallWorld("distro-name-refused");
        var before = world.Tree();
        foreach (var bad in Refused)
        {
            var result = await world.RunAsync("--distro-name", bad);

            result.Exit.Should().Be(2, $"\"{bad}\" is not a distro name:\n{result.Stdout}\n{result.Stderr}");
            result.Stderr.Should().Contain("--distro-name takes a WSL distribution name");
            DistroIdentity.IsName(bad).Should().BeFalse($"the daemon refuses \"{bad}\" too");
        }

        world.Calls.Should().BeEmpty("a refused argument stops the script before it asks anything");
        NothingChanged(world, before);
    }

    /// <summary>The installer's rule and <see cref="DistroIdentity.IsName"/> over one table: a name one accepts and the other refuses
    /// would be a name install.sh hands on and the daemon drops (or refuses and the daemon would have kept).
    /// <c>ArchitectureTests.The_installer_and_the_daemon_share_one_distro_name_rule</c> holds the two pattern texts equal.</summary>
    [Fact]
    public async Task The_installer_and_the_daemon_accept_exactly_the_same_names()
    {
        Linux();
        foreach (var name in Accepted.Concat(Refused))
        {
            using var world = new InstallWorld("distro-name-table");

            var result = await world.RunAsync("--dry-run", "--skip-attestation", "--distro-name", name);

            var installer = result.Exit == 0;
            installer.Should().Be(DistroIdentity.IsName(name), $"both judge \"{name}\" the same:\n{result.Stdout}\n{result.Stderr}");
            if (installer)
            {
                result.Stdout.Should().Contain($"would record the distro name {name} (through the first full run)");
            }
        }
    }

    [Fact]
    public async Task The_argument_without_its_value_is_a_usage_error_before_anything_changes()
    {
        Linux();
        using var world = new InstallWorld("distro-name-no-value");
        var before = world.Tree();

        var result = await world.RunAsync("--skip-attestation", "--distro-name");

        result.Exit.Should().Be(2, $"{result.Stdout}\n{result.Stderr}");
        result.Stderr.Should().Contain("--distro-name needs a value");
        world.Calls.Should().BeEmpty();
        NothingChanged(world, before);
    }

    [Fact]
    public async Task Uninstall_refuses_the_argument_even_empty()
    {
        Linux();
        using var world = new InstallWorld("distro-name-uninstall");
        var before = world.Tree();
        foreach (var value in new[] { "x", string.Empty })
        {
            var result = await world.RunAsync("--uninstall", "--distro-name", value);

            result.Exit.Should().Be(2, $"{result.Stdout}\n{result.Stderr}");
            result.Stderr.Should().Contain("--uninstall takes only --purge and --dry-run");
        }

        world.Calls.Should().BeEmpty();
        NothingChanged(world, before);
    }

    /// <summary>Own review: the line a refusal prints to re-run the installer was built from <c>"$*"</c>, which drops an empty
    /// argument — so <c>--distro-name ''</c> came back as a bare <c>--distro-name</c>, the usage error above, for the person who
    /// pastes it.</summary>
    [Fact]
    public async Task The_rerun_line_of_a_refusal_keeps_an_empty_distro_name_as_an_empty_argument()
    {
        Linux();
        using var world = new InstallWorld("distro-name-rerun");
        world.Override("id", ["-u"], 0, "1000\n");
        foreach (var (value, shown) in new[] { (string.Empty, "--distro-name ''"), ("Ubuntu", "--distro-name Ubuntu") })
        {
            var result = await world.RunAsync("--skip-attestation", "--distro-name", value);

            FailedAt(result, "preflight");
            result.Stderr.Should().Contain($"| sudo sh -s -- --skip-attestation {shown}\n", "the printed command re-runs the same installation");
        }
    }

    [Fact]
    public async Task A_dry_run_says_it_would_record_the_name_and_changes_nothing()
    {
        Linux();
        using var world = new InstallWorld("distro-name-dry-run");
        var before = world.Tree();

        var result = await world.RunAsync("--dry-run", "--skip-attestation", "--distro-name", "Ubuntu");

        Succeeded(result);
        result.Stdout.Should().Contain("would record the distro name Ubuntu (through the first full run)")
            .And.Contain($"would run: {InstallWorld.BinaryPath} collect");
        NothingChanged(world, before);
    }
}
