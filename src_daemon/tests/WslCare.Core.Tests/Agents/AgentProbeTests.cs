using FluentAssertions;

using WslCare.Core.Agents;
using WslCare.TestSupport;

namespace WslCare.Core.Tests.Agents;

/// <summary>
/// <c>agents probe &lt;path&gt;</c> (plan §15q D4): what a picked CLI is — looked at, never started, never read — a name from its
/// FILE NAME, its conventional data folders with their sizes, each judged by the manual-agent rules, and the entry the
/// extension would add.
/// </summary>
public sealed class AgentProbeTests : IDisposable
{
    private readonly LinuxSandbox _sandbox = new("agent-probe");

    public void Dispose() => _sandbox.Dispose();

    /// <summary>A CLI at the distro path <c>~/.local/bin/&lt;name&gt;</c> with an execute bit (its content is never run).</summary>
    private void Cli(string name)
    {
        var path = _sandbox.Write($"/home/me/.local/bin/{name}", "#!/bin/false");
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    private AgentProbeReport Probe(string path) => AgentProbe.Probe(_sandbox.Paths, _sandbox.Files, path, new FixedTimeProvider(), CancellationToken.None);

    [Fact]
    public void A_cli_with_its_own_folder_is_usable_named_by_its_file_and_suggested_with_the_folders_that_pass()
    {
        Cli("mycli");
        _sandbox.Sized("/home/me/.mycli/state.db", 300, FixedTimeProvider.DefaultNow);
        _sandbox.Sized("/home/me/.config/mycli/settings.json", 20, FixedTimeProvider.DefaultNow);

        var report = Probe("/home/me/.local/bin/mycli");

        report.Usable.Should().BeTrue(report.Reason);
        report.Name.Should().Be("mycli");
        report.TrackedAs.Should().BeEmpty();
        report.DataFolders.Select(f => f.Folder.Path).Should().Equal("/home/me/.mycli", "/home/me/.config/mycli");
        report.DataFolders.Should().OnlyContain(f => f.Refusal.Length == 0);
        report.DataFolders[0].Folder.Size.Bytes.Should().Be(300);
        report.Suggested.Should().Be(new ExtraAgent("/home/me/.local/bin/mycli", "wsl", "mycli", ["/home/me/.mycli", "/home/me/.config/mycli"], string.Empty));
    }

    [Fact]
    public void A_catalogue_binary_is_already_tracked_and_nothing_is_suggested()
    {
        Cli("claude");
        _sandbox.Sized("/home/me/.claude/x.json", 5, FixedTimeProvider.DefaultNow);

        var report = Probe("/home/me/.local/bin/claude");

        report.TrackedAs.Should().Be("Claude Code");
        report.DataFolders.Single().Refusal.Should().Contain("already tracked");
        report.Suggested.Should().BeNull();
    }

    [Fact]
    public void A_path_that_is_no_file_is_not_usable_and_nothing_under_the_home_is_looked_at()
    {
        _sandbox.Sized("/home/me/.ghost/x", 5, FixedTimeProvider.DefaultNow);

        var report = Probe("/home/me/.local/bin/ghost");

        report.Usable.Should().BeFalse();
        report.Reason.Should().Contain("not a file that exists");
        report.DataFolders.Should().BeEmpty();
    }

    [Fact]
    public void A_file_without_an_execute_bit_is_not_usable()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "an execute bit is the distro's rule: run in WSL or on the Linux legs");
        _sandbox.Write("/home/me/.local/bin/plain", "#!/bin/sh\n");

        Probe("/home/me/.local/bin/plain").Reason.Should().Contain("no execute bit");
    }

    [Theory]
    [InlineData("/home/me/.local/bin/my cli!", "mycli")]
    [InlineData("/opt/x/.hidden-tool", "hidden-tool")]
    [InlineData("/opt/x/!!!", "")]
    public void The_name_keeps_only_what_a_manual_agent_name_allows(string path, string name) =>
        AgentProbe.NameOf(path).Should().Be(name);

    /// <summary>Plan §15q H3 for the probe, at the syscall level: neither the CLI nor any file of its folders is opened.</summary>
    [Fact]
    public void The_probe_opens_no_file_of_the_cli_or_its_folders()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "inotify is the Linux kernel's: run in WSL or on the Linux legs");
        Cli("mycli");
        _sandbox.Sized("/home/me/.mycli/state.db", 300, FixedTimeProvider.DefaultNow);
        using var watch = InotifyWatch.Over(_sandbox.Paths.DistroPath("/home/me"));

        Probe("/home/me/.local/bin/mycli").Usable.Should().BeTrue();

        watch.FileEvents().Should().BeEmpty("the CLI is stat-ed, never opened or run; its folders are listed, never read (plan §15q D3, H3)");
    }
}
