using FluentAssertions;

using WslCare.Cli;
using WslCare.Core.Hosting;
using WslCare.TestSupport;

namespace WslCare.Cli.Tests;

/// <summary>
/// WR-T1 (PLAN_windows_runner.md §3.12): <c>act</c> on the Windows side. The distro's rule — every act is root's — is inverted there:
/// A23 runs in the person's own, unelevated process, and an elevated one is refused. Over a sandboxed layout; no real temp folder
/// is in reach, and nothing is deleted (a preview, or a refusal before any file is read).
/// </summary>
public sealed class ActWindowsTests
{
    private const string WindowsOnly = "a CliHost over a Windows layout is built on Windows only (its probe); the Linux legs run the other test";

    private static CliHost Host(SandboxHost windows, bool elevated) =>
        new(windows.Paths, windows.Files, new FixedTimeProvider(), new RecordingCommandRunner())
        {
            Privilege = new ProcessPrivilege(elevated, elevated ? "this process is elevated" : "this process is not elevated"),
            UserSid = () => windows.Files.OwnerSidOf(windows.Paths.TempDirectory),
        };

    [Fact]
    public void An_unelevated_windows_process_may_preview_a23_and_nothing_is_written_or_deleted()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), WindowsOnly);
        using var windows = new SandboxHost("act-a23-preview");
        Directory.CreateDirectory(windows.Paths.TempDirectory);
        var old = Path.Combine(windows.Paths.TempDirectory, "coai-old");
        Directory.CreateDirectory(old);
        Directory.SetLastWriteTimeUtc(old, DateTime.UtcNow.AddDays(-30));
        Directory.SetCreationTimeUtc(old, DateTime.UtcNow.AddDays(-30));

        var (exit, stdout, stderr) = CliRun.Over(Host(windows, elevated: false), "act", "A23", "--preview");

        exit.Should().Be((int)ExitCode.Ok, stderr);
        stdout.Should().Contain("A23").And.Contain("1 objects", "the old coai-* folder of this very account is the one selection");
        Directory.Exists(old).Should().BeTrue("a preview deletes nothing");
        File.Exists(Path.Combine(windows.Paths.StateDirectory, "temp-cursor.json")).Should().BeFalse("a preview writes no state");
    }

    [Fact]
    public void An_elevated_windows_process_is_refused_before_anything_is_read()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), WindowsOnly);
        using var windows = new SandboxHost("act-a23-elevated");

        var (exit, stdout, stderr) = CliRun.Over(Host(windows, elevated: true), "act", "A23", "--confirm");

        exit.Should().Be((int)ExitCode.NotAsRoot);
        stdout.Should().BeEmpty();
        stderr.Should().Contain("NOT elevated").And.Contain("Nothing was done");
        Directory.Exists(windows.Paths.StateDirectory).Should().BeFalse("no run lock, no state: the refusal came first");
    }

    [Fact]
    public void An_unelevated_windows_request_that_also_holds_a_distro_action_is_refused_naming_the_side_not_needs_root()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), WindowsOnly);
        using var windows = new SandboxHost("act-a23-mixed");

        var (exit, _, stderr) = CliRun.Over(Host(windows, elevated: false), "act", "A23,A4", "--confirm");

        exit.Should().Be((int)ExitCode.Usage, stderr);
        stderr.Should().Contain("A4 runs inside the WSL distro").And.NotContain("needs root");
    }

    [Fact]
    public void A_windows_act_cannot_be_detached_to_a_systemd_unit()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), WindowsOnly);
        using var windows = new SandboxHost("act-a23-detach");

        var (exit, _, stderr) = CliRun.Over(Host(windows, elevated: false), "act", "A23", "--confirm", "--detach");

        exit.Should().Be((int)ExitCode.Usage);
        stderr.Should().Contain("systemd unit");
    }

    [Fact]
    public void The_distro_binary_refuses_a23_naming_the_windows_host()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "the distro's binary: the Linux legs");
        using var linux = new SandboxHost("act-a23-linux");
        var host = new CliHost(linux.Paths, linux.Files, new FixedTimeProvider(), new RecordingCommandRunner())
        {
            Privilege = new ProcessPrivilege(true, "claimed by the test"),
        };

        var (exit, _, stderr) = CliRun.Over(host, "act", "A23", "--preview");

        exit.Should().Be((int)ExitCode.Usage);
        stderr.Should().Contain("A23 runs on the Windows host, not on this side (the WSL binary)");
    }
}
