using FluentAssertions;

using WslCare.Core.Collectors;
using WslCare.Core.Docker;
using WslCare.Core.Systemd;
using WslCare.TestSupport;

namespace WslCare.Core.Tests.Docker;

/// <summary>
/// The product's parsers over the answers the live contract CAPTURED from this machine on 2026-10-02
/// (<see cref="DockerFixture"/>). The expected figures are Docker's OWN — its <c>system df</c> totals, its
/// counts — or were counted by hand from the raw files; a parser that drifted from Docker's arithmetic fails here.
/// </summary>
public sealed class DockerParserTests
{
    private static T Value<T>(Reading<T> reading) => reading.Should().BeOfType<Reading<T>.Available>(reading.ReasonOrEmpty).Subject.Value;

    [Fact]
    public void Version_names_docker_desktops_engine()
    {
        var engine = DockerReachability.From(new DockerAnswer.Answered(DockerFixture.Read("version.out")))
            .Should().BeOfType<DockerReachability.Reachable>().Subject.Engine;

        engine.Should().Be(new DockerEngine("29.6.1", 29, "29.6.1", "Docker Desktop 4.81.0 (232925)"));
    }

    [Fact]
    public void System_df_reads_dockers_own_totals_per_type()
    {
        var totals = Value(DockerTotal.Parse(DockerFixture.Read("system-df.out"))).ToDictionary(t => t.Type);

        totals.Keys.Should().BeEquivalentTo([DockerTotal.Images, DockerTotal.Containers, DockerTotal.Volumes, DockerTotal.BuildCache]);
        totals[DockerTotal.Images].TotalCount.Should().Be(Reading.Of(23));
        totals[DockerTotal.Images].ReclaimableBytes.Should().Be(Reading.Of(9_806_000_000L));
        totals[DockerTotal.Containers].TotalCount.Should().Be(Reading.Of(29));
        totals[DockerTotal.Volumes].TotalCount.Should().Be(Reading.Of(48));
        totals[DockerTotal.Volumes].ReclaimableBytes.Should().Be(Reading.Of(16_700_000_000L));
        totals[DockerTotal.BuildCache].ReclaimableBytes.Should().Be(Reading.Of(23_980_000L));
    }

    [Fact]
    public void System_df_v_rows_add_up_to_dockers_reclaimable_totals()
    {
        var inventory = Value(DockerInventory.Parse(DockerFixture.Read("system-df-v.out")));

        inventory.Images.Should().HaveCount(23);
        inventory.Containers.Should().HaveCount(29).And.OnlyContain(c => c.Id.Length == 64 && c.SizeBytes.IsAvailable && c.CreatedAt.IsAvailable);
        inventory.Volumes.Should().HaveCount(48).And.OnlyContain(v => v.SizeBytes.IsAvailable && v.Links.IsAvailable);
        inventory.BuildCache.Should().HaveCount(72);
        inventory.Images.Where(i => i.Containers.ValueOr(1) == 0).Sum(i => i.UniqueBytes.ValueOr(0)).Should().BeCloseTo(9_806_000_000L, 5_000_000, "Docker's images reclaimable is the unique size of the images no container uses");
        inventory.Volumes.Where(v => v.Links.ValueOr(1) == 0).Sum(v => v.SizeBytes.ValueOr(0)).Should().BeCloseTo(16_700_000_000L, 50_000_000);
        inventory.BuildCache.Where(b => b.Reclaimable).Sum(b => b.SizeBytes.ValueOr(0)).Should().BeCloseTo(23_980_000L, 100_000);
        inventory.Volumes.Count(v => v.Anonymous).Should().Be(20);
    }

    [Fact]
    public void Volume_ls_dangling_lists_three_anonymous_and_thirteen_named_volumes()
    {
        var dangling = DanglingVolumes.Parse(DockerFixture.Read("volume-ls-dangling.out"));

        dangling.Should().HaveCount(16);
        dangling.Count(DockerJson.IsFullId).Should().Be(3);
    }

    [Fact]
    public void Container_inspect_reads_the_templates_fields_for_every_container()
    {
        var details = Value(ContainerDetail.Parse(DockerFixture.Read("container-inspect.out")));

        details.Select(d => d.Id).Should().Equal(DockerFixture.Inventory.Containers.Select(c => c.Id));
        details.Count(d => d.Stopped).Should().Be(13);
        details.Should().OnlyContain(d => d.CreatedAt.IsAvailable && d.ImageId.StartsWith("sha256:", StringComparison.Ordinal) && !d.Name.StartsWith('/'));
        details.Count(d => d.UnboundedLog).Should().Be(28, "one container sets max-size; the other 28 log to json-file without it");
        details.SelectMany(d => d.Mounts).Select(m => m.Type).Distinct().Should().BeEquivalentTo(["volume", "bind"], "the template reads bind mounts too — the shape that broke $m.Name");
        details.SelectMany(d => d.Mounts).Where(m => m.Type == "bind").Should().OnlyContain(m => m.Name.Length == 0);
    }

    [Fact]
    public void Ps_a_rows_read_by_the_same_parser_name_the_df_v_containers()
    {
        var rows = Value(DockerJson.Lines(DockerFixture.Read("ps-a.out"), "docker ps -a", ContainerRow.From));

        rows.Select(r => r.Id).Should().BeEquivalentTo(DockerFixture.Inventory.Containers.Select(c => c.Id));
    }

    [Fact]
    public void Stats_reads_one_sample_per_running_container_in_binary_units()
    {
        var stats = Value(DockerStats.Parse(DockerFixture.Read("stats.out")));

        stats.Should().HaveCount(16).And.OnlyContain(s => s.Id.Length == 64 && s.MemoryBytes > 0 && s.CpuPercent >= 0);
        stats.Select(s => s.Id).Should().BeEquivalentTo(DockerFixture.Inventory.Containers.Where(c => c.State == "running").Select(c => c.Id));
    }

    [Fact]
    public void Events_read_type_action_id_and_time_and_an_empty_window_is_an_empty_list()
    {
        Value(DockerEvents.Parse(DockerFixture.Read("events.out"))).Should().BeEmpty();
        var exec = Value(DockerEvents.Parse(DockerFixture.Read("events-exec-die.out"))).Should().ContainSingle().Subject;

        exec.Type.Should().Be("container");
        exec.Action.Should().Be("exec_die");
        exec.Id.Should().HaveLength(64);
        exec.Name.Should().StartWith("container-");
        exec.At.Should().BeCloseTo(DockerFixture.CapturedAt, TimeSpan.FromHours(1), "captured right after the live contract run");
    }

    [Fact]
    public void Systemctl_show_reads_an_active_unit_and_a_missing_one()
    {
        var journald = Value(SystemdUnit.Parse(DockerFixture.Read("systemctl-show-journald.out")));
        var missing = Value(SystemdUnit.Parse(DockerFixture.Read("systemctl-show-missing.out")));

        journald.Should().BeEquivalentTo(new { Id = "systemd-journald.service", LoadState = "loaded", ActiveState = "active", SubState = "running", Exists = true });
        journald.ActiveEnteredAt.Should().Be(Reading.Of(DateTimeOffset.FromUnixTimeSeconds(1_790_948_334)));
        missing.Exists.Should().BeFalse();
        missing.ActiveEnteredAt.IsAvailable.Should().BeFalse();
        SystemdUnit.Parse("").IsAvailable.Should().BeFalse();
    }

    [Fact]
    public void Journalctl_disk_usage_is_binary_megabytes()
    {
        JournalDiskUsage.Parse(DockerFixture.Read("journalctl-disk-usage.out")).Should().Be(Reading.Of(426_980_147L));
        JournalDiskUsage.Parse("No journal files were found.").IsAvailable.Should().BeFalse();
    }
}
