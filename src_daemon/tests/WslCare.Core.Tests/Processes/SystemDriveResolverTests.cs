using System.Diagnostics;

using FluentAssertions;

using WslCare.Core.Actions;
using WslCare.Core.Actions.Clock;
using WslCare.Core.Collectors;
using WslCare.Core.Health;
using WslCare.Core.Processes;
using WslCare.Core.Processes.Policy;
using WslCare.TestSupport;

namespace WslCare.Core.Tests.Processes;

/// <summary>
/// The resolver's Windows system drive fallback and what the launcher does with its answer (plan §17 #1). Live finding
/// 2026-10-04: under <c>wsl-care.service</c>, whose PATH is <c>/usr/local/sbin:/usr/local/bin:/usr/sbin:/usr/bin:/snap/bin</c>,
/// <c>powershell.exe</c> was "not found on PATH (5 directories searched, executable files only)" — so <c>clock.drift</c> was
/// unknown and A16 refused on every machine. The lookup's four questions are answered by fakes here (the mountinfo rule is
/// <see cref="WindowsSystemDriveTests"/>, the file checks <see cref="SystemDriveFilesTests"/>), so every leg runs these.
/// </summary>
public sealed class SystemDriveResolverTests : IDisposable
{
    /// <summary>The PATH systemd hands <c>wsl-care.service</c> (no <c>Environment=PATH</c> in the unit).</summary>
    private const string ServicePath = "/usr/local/sbin:/usr/local/bin:/usr/sbin:/usr/bin:/snap/bin";

    private static readonly SystemDriveMount DriveC = new("/mnt/c", 0, 159);

    private readonly TempRoot _root = new("system-drive-resolver");

    private int _asked;

    public void Dispose() => _root.Dispose();

    /// <summary>The program's folder as the PRODUCT names it, read back rather than retyped.</summary>
    private static string PowerShellFolder => WindowsSystemDrive.Programs[HealthCommands.PowerShell];

    private static string OnDriveC => Path.Combine(DriveC.MountPoint, PowerShellFolder, HealthCommands.PowerShell);

    /// <summary>A lookup answering from fixtures; every question counts in <see cref="_asked"/>.</summary>
    private SystemDriveLookup Lookup(
        string interop = "", string mountPoint = "", string inspect = "", Reading<SystemDriveMount>? mount = null, TimeSpan? ceiling = null, Func<string>? inspectWith = null) =>
        new(() => Ask(mount ?? Reading.Of(DriveC)), () => Ask(interop), _ => Ask(mountPoint), (_, _, _) => inspectWith is null ? Ask(inspect) : inspectWith(), ceiling ?? WindowsSystemDrive.Ceiling);

    private T Ask<T>(T answer)
    {
        Interlocked.Increment(ref _asked);
        return answer;
    }

    private static string Reason(ResolvedExecutable resolved) =>
        resolved.Should().BeOfType<ResolvedExecutable.NotFound>("the resolver answered {0}", resolved).Which.Reason;

    [Fact]
    public void Under_a_services_minimal_path_the_windows_clock_probe_still_resolves_powershell_on_the_mounted_system_drive()
    {
        var resolved = ExecutableResolver.Resolve(HealthCommands.PowerShell, ServicePath, windows: false, Lookup(), TestContext.Current.CancellationToken);

        resolved.Should().Be(new ResolvedExecutable.Found(OnDriveC) { OnTheSystemDrive = true }, "the resolver answered {0}", resolved);
    }

    [Fact]
    public void A_powershell_on_path_still_wins_and_the_system_drive_is_not_asked()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "a Linux PATH (split at ':') cannot hold this machine's drive-letter temp path");
        var onPath = _root.File($"interop/{HealthCommands.PowerShell}", "MZ");
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(onPath, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        }

        var resolved = ExecutableResolver.Resolve(HealthCommands.PowerShell, _root.Under("interop"), windows: false, Lookup(), TestContext.Current.CancellationToken);

        resolved.Should().Be(new ResolvedExecutable.Found(onPath));
        _asked.Should().Be(0);
    }

    [Fact]
    public void A_name_outside_the_closed_list_is_looked_up_on_path_alone_and_nothing_of_the_drive_is_read()
    {
        var resolved = ExecutableResolver.Resolve("wc-tool", ServicePath, windows: false, Lookup(), TestContext.Current.CancellationToken);

        Reason(resolved).Should().NotContain("system drive");
        _asked.Should().Be(0, "mountinfo, binfmt_misc and the share are read only for a program the product starts from the drive");
    }

    [Fact]
    public void On_windows_the_system_drive_is_never_consulted()
    {
        ExecutableResolver.Resolve(HealthCommands.PowerShell, _root.Under("empty"), windows: true, Lookup(), TestContext.Current.CancellationToken)
            .Should().BeOfType<ResolvedExecutable.NotFound>();
        _asked.Should().Be(0, "on Windows powershell.exe is System32's, on PATH; the fallback is the distro's");
    }

    [Fact]
    public void Without_wsl_interop_nothing_is_started_from_the_drive_and_the_reason_names_interop()
    {
        var resolved = ExecutableResolver.Resolve(HealthCommands.PowerShell, ServicePath, windows: false, Lookup(interop: "WSL interop is registered but not enabled"), TestContext.Current.CancellationToken);

        Reason(resolved).Should().Contain("PATH").And.Contain("WSL interop is registered but not enabled");
        _asked.Should().Be(2, "the mount, then interop — the file is not inspected once interop says no");
    }

    [Fact]
    public void A_mount_point_somebody_but_root_could_change_is_refused_with_its_reason()
    {
        Reason(ExecutableResolver.Resolve(HealthCommands.PowerShell, ServicePath, windows: false, Lookup(mountPoint: "/home/u, above the drive's mount point, is owned by uid 1000, not root"), TestContext.Current.CancellationToken))
            .Should().Contain("owned by uid 1000");
    }

    [Fact]
    public void A_file_the_checks_refuse_is_not_started_and_the_reason_names_it()
    {
        Reason(ExecutableResolver.Resolve(HealthCommands.PowerShell, ServicePath, windows: false, Lookup(inspect: "x is not a Windows program (no MZ header)"), TestContext.Current.CancellationToken))
            .Should().Contain("not started from the Windows system drive").And.Contain("no MZ header");
    }

    [Fact]
    public void When_no_system_drive_is_mounted_the_reason_says_PATH_was_searched_and_why_the_drive_was_not()
    {
        var resolved = ExecutableResolver.Resolve(
            HealthCommands.PowerShell, ServicePath, windows: false, Lookup(mount: Reading.Missing<SystemDriveMount>(@"no drvfs mount of C:\")), TestContext.Current.CancellationToken);

        Reason(resolved).Should().Contain("PATH").And.Contain(@"the Windows system drive was not searched: no drvfs mount of C:\");
    }

    [Fact]
    public void A_share_that_does_not_answer_is_a_named_refusal_within_the_ceiling_never_a_hang()
    {
        using var blocked = new ManualResetEventSlim();
        try
        {
            var clock = Stopwatch.StartNew();
            var resolved = ExecutableResolver.Resolve(HealthCommands.PowerShell, ServicePath, windows: false,
                Lookup(ceiling: TimeSpan.FromMilliseconds(200), inspectWith: () => blocked.Wait(TimeSpan.FromMinutes(1)) ? string.Empty : "timed out"), TestContext.Current.CancellationToken);

            Reason(resolved).Should().Contain("did not answer within 0.2 s");
            clock.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(10));
        }
        finally
        {
            blocked.Set();
        }
    }

    [Fact]
    public void The_callers_cancellation_ends_the_wait_as_a_cancellation()
    {
        using var blocked = new ManualResetEventSlim();
        using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        try
        {
            var resolve = () => ExecutableResolver.Resolve(HealthCommands.PowerShell, ServicePath, windows: false,
                Lookup(inspectWith: () => blocked.Wait(TimeSpan.FromMinutes(1)) ? string.Empty : "timed out"), cancel.Token);

            resolve.Should().Throw<OperationCanceledException>();
        }
        finally
        {
            blocked.Set();
        }
    }

    [Fact]
    public void The_products_own_resolve_consults_this_machines_system_drive_never_path_alone()
    {
        // The production wiring: Resolve(name) must hand the lookup THIS machine's mountinfo, not the PATH-only stand-in.
        // On a CI runner there is no drvfs mount, so the reason names mountinfo; on a WSL machine PATH or the drive finds it.
        Assert.SkipWhen(OperatingSystem.IsWindows(), "the fallback is the distro's; Windows never consults it");

        var resolved = ExecutableResolver.Resolve(HealthCommands.PowerShell, TestContext.Current.CancellationToken);

        if (resolved is ResolvedExecutable.NotFound missing)
        {
            missing.Reason.Should().NotContain("consults PATH alone").And.Contain("Windows system drive");
        }
    }

    [Fact]
    public void The_policy_judges_the_bare_program_and_would_refuse_the_path_it_resolves_to()
    {
        var bare = HealthCommands.WindowsClock.ToRequest();
        var resolvedPath = new CommandRequest([OnDriveC, .. HealthCommands.WindowsClock.Arguments], HealthCommands.WindowsClock.Ceiling);

        CommandPolicy.Product.Review(bare).Should().Be(CommandVerdict.Allowed);
        CommandPolicy.Product.Review(resolvedPath).Should().BeOfType<CommandVerdict.Refused>("the catalogue declares bare names only — the launcher, not the policy, sees the path");
    }

    [Fact]
    public async Task The_launcher_starts_exactly_the_file_the_lookup_found_and_the_action_record_names_both()
    {
        // A program that runs on this leg stands in for the drive's powershell.exe; the policy is the PRODUCT's, so it sees
        // the bare name (it would refuse the path, above), and the launcher starts what the lookup returned, unchanged.
        var runnable = OperatingSystem.IsWindows() ? Path.Combine(Environment.SystemDirectory, "whoami.exe") : "/bin/true";
        var asked = new List<string>();
        var runner = ProcessCommandRunner.WithResolverForItsOwnTests(CommandPolicy.Product, (name, _) =>
        {
            asked.Add(name);
            return new ResolvedExecutable.Found(runnable) { OnTheSystemDrive = true };
        });
        var commands = new ActionCommands(new ClockFix(), runner, new TargetUserResult.None("machine-scoped"), []);

        var outcome = await commands.AsRunner().RunAsync(HealthCommands.WindowsClock.ToRequest(), TestContext.Current.CancellationToken);

        asked.Should().Equal([HealthCommands.PowerShell], "the lookup is asked for the bare program the request names");
        outcome.Should().BeOfType<CommandOutcome.Exited>("{0}", outcome).Which.StartedFrom.Should().Be(runnable);
        commands.Ran.Should().ContainSingle().Which.Display.Should().StartWith($"{HealthCommands.PowerShell} -NoProfile").And.EndWith($"(started from {runnable})");
    }

    [Fact]
    public async Task A_program_found_on_path_reports_no_started_from()
    {
        var runnable = OperatingSystem.IsWindows() ? Path.Combine(Environment.SystemDirectory, "whoami.exe") : "/bin/true";
        var runner = ProcessCommandRunner.WithResolverForItsOwnTests(CommandPolicy.Product, (_, _) => new ResolvedExecutable.Found(runnable));

        var outcome = await runner.RunAsync(HealthCommands.WindowsClock.ToRequest(), TestContext.Current.CancellationToken);

        outcome.Should().BeOfType<CommandOutcome.Exited>().Which.StartedFrom.Should().BeEmpty();
    }

    /// <summary>PR #10 retro round (consultation b41d9220): the launcher stamps the instant it starts the process AFTER the
    /// lookup — a system-drive lookup that takes its time (here four seconds on the test's clock) is not part of the launch
    /// instant the clock probe's offset is measured from.</summary>
    [Fact]
    public async Task The_launch_instant_is_taken_after_the_lookup_so_a_slow_lookup_is_not_part_of_it()
    {
        var clock = new ManualTimeProvider(FixedTimeProvider.DefaultNow);
        var lookup = TimeSpan.FromSeconds(4);
        var runnable = OperatingSystem.IsWindows() ? Path.Combine(Environment.SystemDirectory, "whoami.exe") : "/bin/true";
        var runner = ProcessCommandRunner.WithResolverForItsOwnTests(CommandPolicy.Product, (_, _) =>
        {
            clock.Advance(lookup);
            return new ResolvedExecutable.Found(runnable) { OnTheSystemDrive = true };
        }, clock);

        var outcome = await runner.RunAsync(HealthCommands.WindowsClock.ToRequest(), TestContext.Current.CancellationToken);

        outcome.Should().BeOfType<CommandOutcome.Exited>("{0}", outcome).Which.StartedAt
            .Should().Be(Reading.Of(FixedTimeProvider.DefaultNow + lookup), "the instant is read when the process starts, after the lookup");
    }
}
