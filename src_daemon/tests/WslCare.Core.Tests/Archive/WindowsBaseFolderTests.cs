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
    [InlineData(@"profile\.gemini\antigravity-cli\archive")]
    [InlineData(@"temp\archive")]
    public void A_folder_inside_an_agents_folder_or_the_temporary_folder_is_refused(string relative)
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), WindowsOnly);

        Judge(_root.Dir(relative)).Rule.Should().Be(BaseFolderRule.Overlap);
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
