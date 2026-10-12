using FluentAssertions;

using WslCare.Core.Collectors;
using WslCare.Core.Health;
using WslCare.Core.Processes;
using WslCare.TestSupport;

namespace WslCare.Core.Tests.Processes;

/// <summary>
/// E15.S1b, the coordinator's rule after S4: the daemon NEVER starts the Store package or its alias — only the verified MSI
/// file at its fixed folder may run. A root collect through <c>wsl.exe -u root</c> inherits the Windows PATH, whose user part
/// holds <c>WindowsApps\pwsh.exe</c> (the alias). So <c>pwsh.exe</c> is never taken from PATH, with or without a PATH entry:
/// it resolves ONLY in <c>Program Files/PowerShell/7</c> on the mounted system drive, through the same checks as
/// <c>powershell.exe</c>'s fallback. The scenario harness reaches its fake through the sandbox's own drive folder
/// (<see cref="SystemDriveLookup.Sandboxed"/>), which only an UNPRIVILEGED process inside a sandbox may use.
/// </summary>
public sealed class PwshFixedFolderTests : IDisposable
{
    private static readonly SystemDriveMount DriveC = new("/mnt/c", 0, 159);

    private readonly TempRoot _root = new("pwsh-fixed-folder");

    private int _asked;

    public void Dispose() => _root.Dispose();

    private static string OnDriveC => Path.Combine(DriveC.MountPoint, WindowsSystemDrive.Programs[HealthCommands.Pwsh], HealthCommands.Pwsh);

    private SystemDriveLookup Lookup(string inspect = "") =>
        new(() => Ask(Reading.Of(DriveC)), () => Ask(string.Empty), _ => Ask(string.Empty), (_, _, _) => Ask(inspect), WindowsSystemDrive.Ceiling);

    private T Ask<T>(T answer)
    {
        Interlocked.Increment(ref _asked);
        return answer;
    }

    /// <summary>A startable <c>pwsh.exe</c> in a folder of its own — the decoy a PATH entry names.</summary>
    private string Decoy(string folder)
    {
        var path = _root.File($"{folder}/{HealthCommands.Pwsh}", "MZ a decoy");
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        }

        return _root.Under(folder);
    }

    [Fact]
    public void A_pwsh_on_path_never_wins_the_fixed_folder_file_on_the_drive_is_started()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "a Linux PATH (split at ':') cannot hold this machine's drive-letter temp path");

        var resolved = ExecutableResolver.Resolve(HealthCommands.Pwsh, Decoy("windowsapps"), windows: false, Lookup(), TestContext.Current.CancellationToken);

        resolved.Should().Be(new ResolvedExecutable.Found(OnDriveC) { OnTheSystemDrive = true }, "the resolver answered {0}", resolved);
        _asked.Should().Be(4, "the drive is asked although PATH holds a pwsh.exe");
    }

    [Fact]
    public void A_pwsh_on_path_with_no_file_in_the_fixed_folder_is_not_found_never_the_path_one()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "a Linux PATH (split at ':') cannot hold this machine's drive-letter temp path");

        var resolved = ExecutableResolver.Resolve(HealthCommands.Pwsh, Decoy("windowsapps"), windows: false, Lookup(inspect: "the file is absent"), TestContext.Current.CancellationToken);

        resolved.Should().BeOfType<ResolvedExecutable.NotFound>("the resolver answered {0}", resolved)
            .Which.Reason.Should().Contain("never from PATH").And.Contain("the file is absent");
    }

    [Fact]
    public void On_windows_a_pwsh_on_path_is_not_started_either()
    {
        var resolved = ExecutableResolver.Resolve(HealthCommands.Pwsh, Decoy("winpath"), windows: true, Lookup(), TestContext.Current.CancellationToken);

        resolved.Should().BeOfType<ResolvedExecutable.NotFound>("the resolver answered {0}", resolved).Which.Reason.Should().Contain("never from PATH");
        _asked.Should().Be(0, "the Windows binary has no mounted system drive to ask");
    }

    /// <summary>Code round: the system drive's 9p share is case-insensitive, so the rule is too — <c>Pwsh.exe</c> on PATH is the
    /// same Windows file and is never taken from there either.</summary>
    [Theory]
    [InlineData("Pwsh.exe")]
    [InlineData("PWSH.EXE")]
    public void Pwsh_spelled_in_another_case_is_never_taken_from_path_either(string name)
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "a Linux PATH (split at ':') cannot hold this machine's drive-letter temp path");
        var path = _root.File($"cased-{name}/{name}", "MZ a decoy");
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        }

        var resolved = ExecutableResolver.Resolve(name, _root.Under($"cased-{name}"), windows: false, Lookup(), TestContext.Current.CancellationToken);

        resolved.Should().BeOfType<ResolvedExecutable.NotFound>("the resolver answered {0}", resolved).Which.Reason.Should().Contain("never from PATH");
    }

    [Fact]
    public void Powershell_on_path_still_wins_the_rule_is_for_pwsh_alone()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "a Linux PATH (split at ':') cannot hold this machine's drive-letter temp path");
        var path = _root.File($"interop/{HealthCommands.PowerShell}", "MZ");
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        }

        ExecutableResolver.Resolve(HealthCommands.PowerShell, _root.Under("interop"), windows: false, Lookup(), TestContext.Current.CancellationToken)
            .Should().Be(new ResolvedExecutable.Found(path));
        _asked.Should().Be(0);
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public void Only_an_unprivileged_process_inside_a_sandbox_gets_the_sandbox_drive(bool privileged, bool sandboxed)
    {
        var lookup = SystemDriveLookup.Choose(_root.Path, privileged);
        var mount = lookup.Mount();

        (mount is Reading<SystemDriveMount>.Available { Value: var m } && m.MountPoint == Path.Combine(_root.Path, "mnt", "c"))
            .Should().Be(sandboxed, "only an unprivileged process inside a sandbox gets the sandbox drive; a real root process never trusts a folder an environment variable names");
        SystemDriveLookup.Choose(null, privileged: false).Mount().ValueOr(new SystemDriveMount(string.Empty, 0, 0)).MountPoint
            .Should().NotStartWith(_root.Path, "no sandbox root, no sandbox drive");
    }

    [Fact]
    public void In_the_sandbox_a_link_in_the_fixed_folder_is_not_started()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "symbolic links are the distro's: the Linux legs");
        var target = _root.File("elsewhere/pwsh-real", "a fake");
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(target, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        }

        var folder = _root.Dir($"mnt/c/{WindowsSystemDrive.Programs[HealthCommands.Pwsh]}");
        File.CreateSymbolicLink(Path.Combine(folder, HealthCommands.Pwsh), target);

        ExecutableResolver.Resolve(HealthCommands.Pwsh, Decoy("windowsapps"), windows: false, SystemDriveLookup.Sandboxed(_root.Path), TestContext.Current.CancellationToken)
            .Should().BeOfType<ResolvedExecutable.NotFound>().Which.Reason.Should().Contain("not a startable regular file");
    }

    [Fact]
    public void In_the_sandbox_the_fake_pwsh_in_the_fixed_folder_is_started_and_the_path_decoy_is_not()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "a Linux PATH (split at ':') cannot hold this machine's drive-letter temp path");
        var planted = _root.File($"mnt/c/{WindowsSystemDrive.Programs[HealthCommands.Pwsh]}/{HealthCommands.Pwsh}", "a fake");
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(planted, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        }

        var resolved = ExecutableResolver.Resolve(HealthCommands.Pwsh, Decoy("windowsapps"), windows: false, SystemDriveLookup.Sandboxed(_root.Path), TestContext.Current.CancellationToken);

        resolved.Should().Be(new ResolvedExecutable.Found(planted) { OnTheSystemDrive = true }, "the resolver answered {0}", resolved);
        ExecutableResolver.Resolve(HealthCommands.Pwsh, Decoy("windowsapps2"), windows: false, SystemDriveLookup.Sandboxed(_root.Dir("empty-sandbox")), TestContext.Current.CancellationToken)
            .Should().BeOfType<ResolvedExecutable.NotFound>("no fixed-folder file, so nothing — never the decoy");
    }
}
