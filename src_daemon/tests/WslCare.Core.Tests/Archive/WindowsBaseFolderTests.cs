using FluentAssertions;

using WslCare.Core.Archive;
using WslCare.Core.Files;
using WslCare.Core.Hosting;
using WslCare.TestSupport;

namespace WslCare.Core.Tests.Archive;

/// <summary>
/// Plan §15r D7 (E9.S0) — the base rules on the Windows side, over a profile in a temporary folder and the real file system: a
/// drive folder accepted with its drive's kind, a Linux path, a drive root, a folder inside an agent's, inside the temporary folder
/// or reached through a junction refused. The Windows legs only (the rules read the drive and the reparse points).
/// </summary>
public sealed class WindowsBaseFolderTests : IDisposable
{
    private const string WindowsOnly = "the Windows side's rules read a Windows drive and its reparse points: the Windows legs";

    private readonly TempRoot _root = new("windows-base");

    public void Dispose() => _root.Dispose();

    private WindowsHostPaths Paths => new(new WindowsEnvironment(
        _root.Dir("profile"), _root.Dir(@"profile\AppData\Roaming"), _root.Dir(@"profile\AppData\Local"), _root.Dir("programdata"), _root.Dir("temp")));

    private BaseFolderReport Judge(string given)
    {
        var paths = Paths;
        return BaseFolderRules.Judge(paths, new PhysicalFileSystem(paths), [], given);
    }

    [Fact]
    public void A_drive_folder_is_accepted_with_its_drives_kind_and_format()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), WindowsOnly);
        var folder = _root.Dir("archive");

        var report = Judge(folder);

        report.Accepted.Should().BeTrue(report.Refusal);
        report.Side.Should().Be("windows");
        report.Mount!.Type.Should().StartWith("fixed");
    }

    [Fact]
    public void A_linux_path_or_a_drive_root_is_refused()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), WindowsOnly);

        Judge("/mnt/v/ai-archive").Rule.Should().Be(BaseFolderRule.Shape);
        Judge(Path.GetPathRoot(_root.Path)!).Rule.Should().Be(BaseFolderRule.TooBroad);
    }

    [Theory]
    [InlineData(@"profile\.claude\archive")]
    [InlineData(@"profile\AppData\Roaming\Antigravity\archive")]
    [InlineData(@"temp\archive")]
    public void A_folder_inside_an_agents_folder_or_the_temporary_folder_is_refused(string relative)
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), WindowsOnly);

        Judge(_root.Dir(relative)).Rule.Should().Be(BaseFolderRule.Overlap);
    }

    /// <summary>E9.S0 review round S3: a share that is this machine — the distribution's own files, a loopback name, this machine's
    /// name — or an administrative share reaches the protected places under another spelling; refused before anything is looked
    /// at (every leg: no disk is read).</summary>
    [Theory]
    [InlineData(@"\\wsl$\Ubuntu\home\me\archive")]
    [InlineData(@"\\wsl.localhost\Ubuntu\home\me\archive")]
    [InlineData(@"\\localhost\share\archive")]
    [InlineData(@"\\127.0.0.1\share\archive")]
    [InlineData(@"\\0--1.ipv6-literal.net\share\archive")]
    [InlineData(@"\\nas\C$\archive")]
    [InlineData(@"\\nas\ADMIN$\archive")]
    [InlineData(@"\\nas\C$/Users\me\archive")]
    [InlineData(@"\\localhost.\share\archive")]
    [InlineData(@"\\0-0-0-0-0-0-0-1.ipv6-literal.net\share\archive")]
    [InlineData(@"\\127.0.0.2\share\archive")]
    public void A_share_that_is_this_machine_or_an_administrative_share_is_refused(string given)
    {
        var report = Judge(given);

        report.Rule.Should().Be(BaseFolderRule.Shape, report.Refusal);
    }

    [Fact]
    public void A_share_named_after_this_machine_is_refused()
    {
        Judge($@"\\{Environment.MachineName}\share\archive").Rule.Should().Be(BaseFolderRule.Shape);
    }

    /// <summary>E9.S1 review round m2: this machine's DNS host name (not always the 15-character NetBIOS name), bare or qualified.</summary>
    [Fact]
    public void A_share_named_after_this_machines_dns_host_name_is_refused()
    {
        Judge($@"\\{System.Net.Dns.GetHostName()}\share\archive").Rule.Should().Be(BaseFolderRule.Shape);
        Judge($@"\\{System.Net.Dns.GetHostName()}.example.lan\share\archive").Rule.Should().Be(BaseFolderRule.Shape);
    }

    /// <summary>S3: an 8.3 short name of an agent's folder is the same folder — compared by the file system's identity of the folder
    /// and its parents, not by its spelling.</summary>
    [Fact]
    public void A_short_name_of_an_agents_folder_is_refused()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), WindowsOnly);
        var inside = _root.Dir(@"profile\.claude\archive-of-sessions");
        var shortName = ShortName(inside);
        Assert.SkipWhen(string.Equals(shortName, inside, StringComparison.OrdinalIgnoreCase), "8.3 names are not generated on this volume");

        Judge(shortName).Rule.Should().Be(BaseFolderRule.Overlap, $"{shortName} is {inside}");
    }

    /// <summary>S3: the identity overlap over two chains, pure — a base inside the place, holding it, the place itself, and a sibling.</summary>
    [Fact]
    public void Two_chains_overlap_when_either_folder_is_in_the_others_chain()
    {
        FileIdentity Id(ulong index) => new(7, index);
        IReadOnlyList<FileIdentity> place = [Id(3), Id(2), Id(1)];

        WindowsIdentity.Overlap([Id(4), Id(3), Id(2), Id(1)], place).Should().BeTrue("the base lies inside the place");
        WindowsIdentity.Overlap([Id(2), Id(1)], place).Should().BeTrue("the base holds the place");
        WindowsIdentity.Overlap([Id(3), Id(2), Id(1)], place).Should().BeTrue("the base is the place");
        WindowsIdentity.Overlap([Id(5), Id(2), Id(1)], place).Should().BeFalse("a sibling under the same parent");
        WindowsIdentity.Overlap([], place).Should().BeFalse("a base that cannot be opened is judged by its spelling alone");
    }

    [Fact]
    public void A_folder_and_its_short_name_have_one_identity()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Skip(WindowsOnly);
            return;
        }

        var folder = _root.Dir(@"profile\some-long-folder-name");
        var shortName = ShortName(folder);
        Assert.SkipWhen(string.Equals(shortName, folder, StringComparison.OrdinalIgnoreCase), "8.3 names are not generated on this volume");

        WindowsIdentity.ChainOf(shortName).Should().Equal(WindowsIdentity.ChainOf(folder)).And.NotBeEmpty();
    }

    /// <summary>The short form cmd.exe reports for <paramref name="path"/> (its own path when the volume generates none).</summary>
    private static string ShortName(string path)
    {
        using var cmd = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd.exe", $"/d /c for %I in (\"{path}\") do @echo %~sI") { RedirectStandardOutput = true, UseShellExecute = false })!;
        var output = cmd.StandardOutput.ReadToEnd().Trim();
        cmd.WaitForExit();
        return output.Length > 0 ? output : path;
    }

    [Fact]
    public void A_folder_reached_through_a_junction_is_refused()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), WindowsOnly);
        var real = _root.Dir("real");
        Assert.SkipUnless(DirectoryLinks.TryCreate(_root.Under("linked"), real), "no junction could be made here");

        Judge(_root.Under("linked")).Rule.Should().Be(BaseFolderRule.LinkOnTheWay);
    }
}
