using FluentAssertions;

using WslCare.Core.Collectors;
using WslCare.Core.Docker;
using WslCare.Core.Health;
using WslCare.Core.Records;
using WslCare.TestSupport;

namespace WslCare.Core.Tests.Health;

/// <summary>
/// E15.S2: the Windows disk sample (the answer CAPTURED on 2026-10-10, <see cref="WindowsDiskFixture"/>) turned into the virtual
/// disks' facts — which disk is this distro's (by its name, never guessed), what each guest uses, the gap, the growth between two
/// MEASURED sizes, and the hosting volume's free space against what its files can still grow. Every unknown is a reason, never 0.
/// </summary>
public sealed class VirtualDisksTests
{
    private const long Gib = 1L << 30;

    /// <summary>D3: Ubuntu's own <c>/</c> — 1 007 GiB, 194.0 GiB used (208 337 252 352 B).</summary>
    private const long RootTotal = 1007 * Gib;
    private const long RootUsed = 208_337_252_352;

    /// <summary>The capture's sizes (D2): Ubuntu's VHDX 237.7 GiB, Docker's data disk 123.2 GiB.</summary>
    private const long UbuntuSize = 255_195_086_848;
    private const long DockerSize = 132_338_679_808;

    /// <summary>D3's <c>docker system df</c>: images 26.82 GB, containers 417.6 MB, local volumes 61.07 GB, build cache 7.05 GB.</summary>
    private const long DockerUsed = 26_820_000_000 + 417_600_000 + 61_070_000_000 + 7_050_000_000;

    private static readonly TimeSpan GrowthMin = TimeSpan.FromHours(1);

    private static T Value<T>(Reading<T> reading)
    {
        reading.Should().BeOfType<Reading<T>.Available>(reading.ReasonOrEmpty);
        return ((Reading<T>.Available)reading).Value;
    }

    internal static WindowsDiskSample Captured() =>
        new(WindowsDiskFixture.CapturedAt, Reading.Of(TimeSpan.FromSeconds(3.3)), WindowsDiskParsers.Parse(WindowsDiskFixture.Answer, 5000, 60));

    internal static Reading<VolumeUsage> Root() => Reading.Of(new VolumeUsage("/", RootTotal, RootUsed, RootTotal - RootUsed));

    internal static Reading<IReadOnlyList<DockerTotal>> DockerTotals() => Reading.Of<IReadOnlyList<DockerTotal>>(
    [
        Total(DockerTotal.Images, 26_820_000_000),
        Total(DockerTotal.Containers, 417_600_000),
        Total(DockerTotal.Volumes, 61_070_000_000),
        Total(DockerTotal.BuildCache, 7_050_000_000),
    ]);

    private static DockerTotal Total(string type, long bytes) => new(type, Reading.Of(1), Reading.Of(1), Reading.Of(bytes), Reading.Of(0L));

    private static DiskFacts Facts(Reading<string>? distro = null, Reading<VirtualDiskSizes>? previous = null, Reading<IReadOnlyList<DockerTotal>>? docker = null) =>
        Value(VirtualDisks.From(Captured(), Root(), docker ?? DockerTotals(), distro ?? Reading.Of("Ubuntu"), previous ?? Reading.Missing<VirtualDiskSizes>("no recorded full run has sampled the virtual disks yet"), GrowthMin));

    private static VirtualDiskFacts Disk(DiskFacts facts, string name) => facts.Disks.Single(d => d.Name == name);

    private static Reading<VirtualDiskSizes> Previous(TimeSpan before, params VirtualDiskSize[] sizes) =>
        Reading.Of(new VirtualDiskSizes(WindowsDiskFixture.CapturedAt - before, sizes));

    private static string UbuntuPath => Disk(Facts(), "Ubuntu").Path;

    [Fact]
    public void The_D2_D3_numbers_map_Ubuntu_by_its_own_root_and_give_a_43_6_GiB_gap()
    {
        var ubuntu = Disk(Facts(), "Ubuntu");

        ubuntu.MappedBy.Should().Be(MappedBy.Mount);
        Value(ubuntu.SizeBytes).Should().Be(UbuntuSize);
        Value(ubuntu.GuestUsedBytes).Should().Be(RootUsed, "the guest's usage is its own / (statvfs)");
        Value(ubuntu.GapBytes).Should().Be(UbuntuSize - RootUsed, "237.7 GiB − 194.0 GiB: 46 857 834 496 bytes, 43.6 GiB (D3 rounded both sides first: ≈ 43.7)");
        Value(ubuntu.PossibleGrowthBytes).Should().Be(RootTotal - UbuntuSize, "the guest filesystem's size is the disk's virtual size");
        Value(ubuntu.Volume).Should().Be("C");
        ubuntu.Note.Should().Contain("/");
    }

    [Fact]
    public void Dockers_data_disk_is_mapped_by_docker_system_df_and_labelled_an_estimate()
    {
        var docker = Disk(Facts(), WindowsDiskParsers.DockerDataDiskName);

        docker.MappedBy.Should().Be(MappedBy.DockerDf);
        Value(docker.GuestUsedBytes).Should().Be(DockerUsed);
        Value(docker.GapBytes).Should().Be(DockerSize - DockerUsed);
        docker.Note.Should().Contain("Docker's own totals").And.Contain("estimate");
        docker.PossibleGrowthBytes.IsAvailable.Should().BeFalse("Docker's estimate says nothing about the disk's virtual size");
    }

    [Fact]
    public void Dockers_disk_with_docker_not_answering_is_unmapped_with_dockers_reason()
    {
        var docker = Disk(Facts(docker: Reading.Missing<IReadOnlyList<DockerTotal>>("Docker is not reachable: the socket refused")), WindowsDiskParsers.DockerDataDiskName);

        docker.GuestUsedBytes.ReasonOrEmpty.Should().Contain("the socket refused");
        docker.GapBytes.IsAvailable.Should().BeFalse();
    }

    [Fact]
    public void Another_distro_is_not_mapped_and_says_why()
    {
        var other = Disk(Facts(), "Ubuntu-26.04");

        other.MappedBy.Should().Be(MappedBy.None);
        other.GapBytes.ReasonOrEmpty.Should().Contain("not mounted here");
        other.PossibleGrowthBytes.IsAvailable.Should().BeFalse();
        Value(other.SizeBytes).Should().Be(75_806_801_920, "the file's size is still measured");
    }

    [Fact]
    public void Without_a_distro_name_no_distro_is_mapped_and_the_reason_says_how_it_becomes_known()
    {
        var facts = Facts(distro: Reading.Missing<string>(DistroIdentity.NotKnownYet));

        facts.Disks.Where(d => d.Name != WindowsDiskParsers.DockerDataDiskName).Should().OnlyContain(d => d.MappedBy == MappedBy.None && d.Note.Contains("wsl.exe"));
        Disk(facts, WindowsDiskParsers.DockerDataDiskName).MappedBy.Should().Be(MappedBy.DockerDf, "Docker's disk is mapped by Docker's totals, not by the distro's name");
    }

    [Fact]
    public void A_recorded_name_that_is_not_in_the_lxss_map_maps_nothing_and_names_it()
    {
        var facts = Facts(distro: Reading.Of("Debian"));

        facts.Disks.Where(d => d.Name != WindowsDiskParsers.DockerDataDiskName).Should().OnlyContain(d => d.MappedBy == MappedBy.None && d.Note.Contains("Debian") && d.Note.Contains("Lxss"));
    }

    [Fact]
    public void One_measurement_gives_no_growth()
    {
        Disk(Facts(), "Ubuntu").GrowthBytesPerDay.ReasonOrEmpty.Should().Contain("two measured sizes");
    }

    [Fact]
    public void Two_measurements_give_their_difference_per_day()
    {
        var facts = Facts(previous: Previous(TimeSpan.FromHours(12), new VirtualDiskSize("Ubuntu", UbuntuPath, UbuntuSize - Gib)));

        Value(Disk(facts, "Ubuntu").GrowthBytesPerDay).Should().BeApproximately(2.0 * Gib, 1, "1 GiB in 12 h is 2 GiB a day");
    }

    [Fact]
    public void A_shrink_is_shown_as_it_is()
    {
        var facts = Facts(previous: Previous(TimeSpan.FromDays(1), new VirtualDiskSize("Ubuntu", UbuntuPath, UbuntuSize + (3 * Gib))));

        Value(Disk(facts, "Ubuntu").GrowthBytesPerDay).Should().BeApproximately(-3.0 * Gib, 1);
    }

    [Fact]
    public void Ten_minutes_apart_is_too_close_to_extrapolate()
    {
        var facts = Facts(previous: Previous(TimeSpan.FromMinutes(10), new VirtualDiskSize("Ubuntu", UbuntuPath, UbuntuSize - Gib)));

        Disk(facts, "Ubuntu").GrowthBytesPerDay.ReasonOrEmpty.Should().Contain("too close to the previous measurement", "1 GiB in 10 minutes would read as 144 GiB a day");
    }

    [Fact]
    public void A_different_path_gives_no_growth()
    {
        var facts = Facts(previous: Previous(TimeSpan.FromHours(12), new VirtualDiskSize("Ubuntu", @"D:\elsewhere\ext4.vhdx", UbuntuSize - Gib)));

        Disk(facts, "Ubuntu").GrowthBytesPerDay.IsAvailable.Should().BeFalse("growth is a difference of two sizes of the SAME file");
    }

    [Fact]
    public void The_hosting_volume_carries_its_free_space_the_known_growth_and_the_files_whose_growth_is_unknown()
    {
        var c = Facts().Volumes.Should().ContainSingle("every file of the capture is on C:").Subject;

        c.Letter.Should().Be("C");
        Value(c.FreeBytes).Should().Be(1_084_754_046_976);
        c.Disks.Should().BeEquivalentTo(["Ubuntu", "docker-desktop", "Ubuntu-26.04", WindowsDiskParsers.DockerDataDiskName]);
        c.KnownPossibleGrowthBytes.Should().Be(RootTotal - UbuntuSize);
        c.UnknownGrowth.Should().BeEquivalentTo(["docker-desktop", "Ubuntu-26.04", WindowsDiskParsers.DockerDataDiskName]);
        c.GrowthBytesPerDay.IsAvailable.Should().BeFalse("no file has a growth yet");
    }

    [Fact]
    public void An_unavailable_sample_leaves_the_facts_unavailable_with_its_reason()
    {
        var unread = WindowsDiskSample.Unavailable(WindowsDiskFixture.CapturedAt, "the Windows disk script did not answer within 30 s");

        VirtualDisks.From(unread, Root(), DockerTotals(), Reading.Of("Ubuntu"), Reading.Missing<VirtualDiskSizes>("none"), GrowthMin)
            .ReasonOrEmpty.Should().Contain("did not answer within 30 s");
    }

    [Fact]
    public void The_history_line_keeps_every_measured_size_and_the_next_run_reads_it_as_the_previous_one()
    {
        var facts = Facts();
        var run = new RunRecord(SchemaVersion.Current, RunId.New(WindowsDiskFixture.CapturedAt, 7), RunTrigger.Timer, WindowsDiskFixture.CapturedAt, WindowsDiskFixture.CapturedAt.AddMinutes(1), RunOutcome.Completed, [], RunKind.Collect)
        {
            Slow = new SlowParts { VirtualDisks = facts.Sizes },
        };

        var previous = LastFullRun.FromRecords([run], WindowsDiskFixture.CapturedAt.AddHours(4)).VirtualDisks;

        var sizes = Value(previous).Value;
        sizes.Disks.Select(d => d.Name).Should().Equal("Ubuntu", "docker-desktop", "Ubuntu-26.04", WindowsDiskParsers.DockerDataDiskName);
        sizes.Disks[0].SizeBytes.Should().Be(UbuntuSize);
        sizes.SampledAt.Should().Be(WindowsDiskFixture.CapturedAt);
    }
}
