using FluentAssertions;

using WslCare.Core.Files;
using WslCare.TestSupport;

namespace WslCare.Core.Tests.Files;

/// <summary>The two read-only queries the collectors added to <see cref="IFileSystem"/>: a link's target, a volume's size.</summary>
public sealed class ReadOnlyQueriesTests
{
    [Fact]
    public void A_directory_link_answers_its_target_and_a_plain_directory_is_not_a_link()
    {
        using var host = new SandboxHost("readlink");
        var target = host.Root.Dir("work/real");
        var link = host.Root.Under("work/link");
        Assert.SkipUnless(DirectoryLinks.TryCreate(link, target), "this account can create neither a symlink nor a junction here");

        host.Files.ReadLink(link).Should().BeOfType<LinkReadResult.Target>().Which.Path.Should().EndWith("real");
        host.Files.ReadLink(target).Should().BeOfType<LinkReadResult.NotALink>();
        host.Files.ReadLink(host.Root.Under("work/absent")).Should().BeOfType<LinkReadResult.NotALink>("a path that is not there is not a link");
    }

    [Fact]
    public void A_link_that_cannot_be_inspected_is_unreadable_not_a_plain_name()
    {
        using var host = new SandboxHost("readlink-denied");
        var files = new PhysicalFileSystem(host.Paths, static _ => throw new UnauthorizedAccessException("denied by the test"), static (_, _) => { });

        files.ReadLink(host.Root.Under("x")).Should().BeOfType<LinkReadResult.Unreadable>().Which.Reason.Should().Contain("denied");
    }

    [Fact]
    public void The_volume_holding_a_directory_is_measured_in_one_call()
    {
        using var host = new SandboxHost("volume");

        var measured = host.Files.MeasureVolume(host.Root.Path).Should().BeOfType<VolumeReadResult.Measured>().Subject;

        measured.TotalBytes.Should().BePositive();
        measured.FreeBytes.Should().BeInRange(0, measured.TotalBytes);
        measured.AvailableBytes.Should().BeInRange(0, measured.FreeBytes);
    }

    [Fact]
    public void A_volume_that_does_not_exist_is_unreadable_with_the_reason()
    {
        using var host = new SandboxHost("volume-absent");
        var absent = OperatingSystem.IsWindows() ? FreeDriveLetter() : host.Root.Under("no/such/dir");

        host.Files.MeasureVolume(absent).Should().BeOfType<VolumeReadResult.Unreadable>().Which.Reason.Should().NotBeEmpty();
    }

    private static string FreeDriveLetter() =>
        "QRSTUVWXYZ".Select(c => $"{c}:\\").First(d => !Directory.Exists(d));
}
