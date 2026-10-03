using FluentAssertions;

using WslCare.Core.Collectors;
using WslCare.Core.Config;
using WslCare.Core.Docker;
using WslCare.Core.Preview;
using WslCare.Core.Processes;
using WslCare.TestSupport;

namespace WslCare.Core.Tests.Docker;

/// <summary>
/// The cleanup rows of plan §4.3 over the CAPTURED 2026-10-02 Docker (14:22 UTC, after the morning's one-time
/// cleanup), at the shipped limits and at limit 0 — the button's "everything unused", which is what the one-time
/// cleanup did. The expected figures were counted by hand from the raw fixture files and agree with Docker's own
/// <c>system df</c> reclaimable totals.
/// </summary>
public sealed class CleanupPreviewTests
{
    private const string AllAges = """{ "volumes": { "anonymousOlderThanDays": 0 }, "containers": { "stoppedOlderThanDays": 0, "testcontainersOlderThanHours": 0 }, "images": { "unusedOlderThanDays": 0 }, "buildCache": { "olderThanDays": 0 } }""";

    private static readonly DateTimeOffset Now = DockerFixture.CapturedAt;

    private static async Task<DockerSnapshot> CapturedAsync(RecordingCommandRunner? runner = null) =>
        await new DockerCollector(new DockerCli(runner ?? DockerFixture.Runner()), new FixedTimeProvider(Now)).CollectAsync(CancellationToken.None);

    private static EffectiveConfig Config(string userLayer = "{}")
    {
        using var sandbox = new SandboxHost("preview-config");
        sandbox.WriteUserConfig(userLayer);
        var loaded = ConfigLoader.Load(sandbox.Paths, sandbox.Files);
        loaded.IsObserveOnly.Should().BeFalse("the fixture configuration must pass the real validator");
        return loaded.Config;
    }

    private static CleanupRow Row(CleanupPreview preview, string id) => preview.Rows.Single(r => r.Id == id);

    private static RowFigures Figures(CleanupRow row) => row.Figures.Should().BeOfType<Reading<RowFigures>.Available>(row.Figures.ReasonOrEmpty).Subject.Value;

    [Fact]
    public async Task At_limit_zero_the_rows_are_what_a_button_would_take_and_add_up_to_dockers_own_totals()
    {
        var snapshot = await CapturedAsync();
        var seen = VolumeSeenRecord.Empty.Observe(snapshot.UnattachedAnonymous.ValueOr([]), Now);

        var preview = CleanupPreviews.Build(snapshot, seen, Config(AllAges), Now);

        Figures(Row(preview, "A4")).Should().BeEquivalentTo(new { Count = 3, Bytes = 641_400_000L, Unsized = 0 });
        Figures(Row(preview, "A5")).Should().BeEquivalentTo(new { Count = 13, Bytes = 389_426_100L + 1_288_244_878L, Unsized = 0 }, "13 stopped containers, their layers and the 8 anonymous volumes they hold");
        Figures(Row(preview, "A5")).Notes.Single(n => n.What.StartsWith("named volumes", StringComparison.Ordinal)).Count.Should().Be(6);
        Figures(Row(preview, "A5Testcontainers")).Count.Should().Be(0, "no container carried the Testcontainers label — as on 2026-10-02");
        Figures(Row(preview, "A6")).Count.Should().Be(0, "no dangling image");
        Figures(Row(preview, "A6Unused")).Should().BeEquivalentTo(new { Count = 10, Bytes = 9_805_969_710L });
        Figures(Row(preview, "A6Unused")).Bytes.Should().BeCloseTo(9_806_000_000L, 5_000_000, "Docker's images reclaimable");
        Figures(Row(preview, "A7")).Should().BeEquivalentTo(new { Count = 4, Bytes = 23_948_690L });
        var kept = preview.Kept.Should().BeOfType<Reading<IReadOnlyList<KeptVolume>>.Available>().Subject.Value;
        kept.Should().HaveCount(13);
        (kept.Sum(v => v.Bytes.ValueOr(0)) + Figures(Row(preview, "A4")).Bytes).Should().BeCloseTo(16_700_000_000L, 50_000_000, "A4 + kept named volumes = Docker's volumes reclaimable");
    }

    [Fact]
    public async Task At_the_shipped_limits_nothing_young_is_selected_and_the_notes_say_what_was_left_out()
    {
        var snapshot = await CapturedAsync();
        var seen = VolumeSeenRecord.Empty.Observe(snapshot.UnattachedAnonymous.ValueOr([]), Now);

        var preview = CleanupPreviews.Build(snapshot, seen, Config(), Now);

        var a4 = Figures(Row(preview, "A4"));
        a4.Count.Should().Be(0, "first seen now, and the timer's limit is one day");
        a4.Notes[0].Count.Should().Be(3);
        Figures(Row(preview, "A5")).Count.Should().Be(0, "the oldest stop is 2026-09-27, five days before the capture");
        Figures(Row(preview, "A5")).Notes.Single(n => n.What.StartsWith("stopped more recently", StringComparison.Ordinal)).Count.Should().Be(13);
        Figures(Row(preview, "A6Unused")).Count.Should().Be(8, "two of the ten unused images were built within the last 7 days");
        Row(preview, "A4").What.Should().Contain("1 day");
        Row(preview, "A5").Auto.Should().BeFalse();
        Row(preview, "A4").Auto.Should().BeTrue();
    }

    [Fact]
    public async Task A_hex_named_volume_without_dockers_anonymous_label_is_kept_as_named_and_one_whose_labels_are_unknown_is_never_selected()
    {
        var snapshot = await CapturedAsync();
        var names = snapshot.UnattachedAnonymous.ValueOr([]);
        var unknown = new string('a', 64);
        var inventory = snapshot.Inventory.ValueOr(null!);
        var changed = snapshot with
        {
            Inventory = Reading.Of(inventory with
            {
                Volumes = [.. inventory.Volumes.Select(v => v.Name == names[0] ? v with { Labels = new Dictionary<string, string>() } : v)],
            }),
            Dangling = Reading.Of<IReadOnlySet<string>>(new HashSet<string>(snapshot.Dangling.ValueOr(new HashSet<string>())) { unknown }),
        };
        var seen = new VolumeSeenRecord(1, Now, [.. names.Append(unknown).Select(n => new VolumeSighting(n, Now.AddDays(-2)))]);

        var preview = CleanupPreviews.Build(changed, seen, Config(), Now);

        var a4 = Figures(Row(preview, "A4"));
        a4.Count.Should().Be(2, "a 64-hex name alone is not anonymous: Docker's label decides, as volume prune does");
        a4.Notes.Single(n => n.What.Contains("anonymous label", StringComparison.Ordinal)).Count.Should().Be(1);
        a4.Notes.Single(n => n.What.Contains("labels are unknown", StringComparison.Ordinal)).Count.Should().Be(1);
        preview.Kept.ValueOr([]).Select(v => v.Name).Should().Contain(names[0], "Docker treats it as named, so it is listed with the kept named volumes");
        changed.UnattachedAnonymous.ValueOr([]).Should().NotContain(names[0]).And.NotContain(unknown, "volume-seen.json tracks anonymous volumes only");
    }

    [Fact]
    public async Task An_anonymous_volume_first_seen_before_the_limit_is_selected_and_one_kept_by_label_never_is()
    {
        var snapshot = await CapturedAsync();
        var names = snapshot.UnattachedAnonymous.ValueOr([]);
        var inventory = snapshot.Inventory.ValueOr(null!);
        var labelled = snapshot with
        {
            Inventory = Reading.Of(inventory with
            {
                Volumes = [.. inventory.Volumes.Select(v => v.Name == names[0] ? v with { Labels = new Dictionary<string, string> { [DockerLabels.Anonymous] = string.Empty, [DockerLabels.Keep] = "true" } } : v)],
            }),
        };
        var seen = new VolumeSeenRecord(1, Now, [.. names.Select(n => new VolumeSighting(n, Now.AddDays(-2)))]);

        var a4 = Figures(Row(CleanupPreviews.Build(labelled, seen, Config(), Now), "A4"));

        a4.Count.Should().Be(2);
        a4.Notes.Single(n => n.What.Contains(DockerLabels.Keep, StringComparison.Ordinal)).Count.Should().Be(1);
    }

    [Fact]
    public async Task An_image_any_container_uses_is_never_listed_even_when_dockers_count_says_zero()
    {
        var snapshot = await CapturedAsync();
        var details = snapshot.Details.ValueOr([]);
        var stopped = details.First(d => d.Stopped);
        var inventory = snapshot.Inventory.ValueOr(null!);
        // Docker's count wrongly zero for the image a STOPPED container uses: the inspected image id still protects it.
        var lying = snapshot with { Inventory = Reading.Of(inventory with { Images = [.. inventory.Images.Select(i => i.Id == stopped.ImageId ? i with { Containers = Reading.Of(0) } : i)] }) };

        var unused = Figures(Row(CleanupPreviews.Build(lying, VolumeSeenRecord.Empty, Config(AllAges), Now), "A6Unused"));

        unused.Count.Should().Be(10, "the image of a stopped container is not unused");
    }

    [Fact]
    public async Task A_testcontainers_container_is_counted_apart_by_its_hour_limit()
    {
        var snapshot = await CapturedAsync();
        var details = snapshot.Details.ValueOr([]);
        var marked = details.Where(d => d.Stopped && d.StoppedSince.ValueOr(Now) <= Now.AddHours(-3)).Take(2).Select(d => d.Id).ToHashSet();
        var withTc = snapshot with { Details = Reading.Of<IReadOnlyList<ContainerDetail>>([.. details.Select(d => marked.Contains(d.Id) ? d with { Testcontainers = true } : d)]) };

        var preview = CleanupPreviews.Build(withTc, VolumeSeenRecord.Empty, Config(), Now);

        Figures(Row(preview, "A5Testcontainers")).Count.Should().Be(2, "stopped more than 2 hours ago");
        Figures(Row(preview, "A5")).Notes.Single(n => n.What.StartsWith("stopped more recently", StringComparison.Ordinal)).Count.Should().Be(11);
    }

    [Fact]
    public async Task The_build_cache_age_filter_alone_selects_little_while_the_cap_shows_what_a_capped_prune_frees()
    {
        var preview = CleanupPreviews.Build(await CapturedAsync(), VolumeSeenRecord.Empty, Config("""{ "buildCache": { "maxGb": 0 } }"""), Now);

        var a7 = Figures(Row(preview, "A7"));
        a7.Notes[0].Count.Should().Be(0, "every reclaimable entry was used within 7 days — the 2026-10-02 lesson");
        a7.Notes[1].Bytes.Should().Be(Reading.Of(1_008_126_551L), "a 0 GB cap frees the whole cache");
    }

    [Fact]
    public async Task On_docker_below_23_the_a4_row_still_counts_and_names_the_refusal()
    {
        var snapshot = await CapturedAsync();
        var old = snapshot with { Reachability = new DockerReachability.Reachable(new DockerEngine("22.0.4", 22, "22.0.4", string.Empty)) };

        var a4 = Row(CleanupPreviews.Build(old, VolumeSeenRecord.Empty, Config(), Now), "A4");

        a4.Figures.IsAvailable.Should().BeTrue();
        a4.Refusal.Should().Be("Docker 22.0.4 is below 23: A4 refuses to run there (plan 5)");
        Row(CleanupPreviews.Build(snapshot, VolumeSeenRecord.Empty, Config(), Now), "A4").Refusal.Should().BeEmpty();
    }

    [Fact]
    public async Task When_docker_does_not_answer_every_docker_row_is_unavailable_with_the_reason_and_never_zero()
    {
        var runner = new RecordingCommandRunner().Script(DockerCommands.Version.Argv, new CommandOutcome.FailedToStart("docker: not found"));
        var snapshot = await CapturedAsync(runner);

        var preview = CleanupPreviews.Build(snapshot, VolumeSeenRecord.Empty, Config(), Now);

        preview.Rows.Should().HaveCount(8).And.OnlyContain(r => !r.Figures.IsAvailable && r.Figures.ReasonOrEmpty.Length > 0);
        preview.Rows.Where(r => r.Id is not "A8" and not "A9").Should().OnlyContain(r => r.Figures.ReasonOrEmpty.Contains("not installed", StringComparison.Ordinal));
        preview.Kept.IsAvailable.Should().BeFalse();
    }
}
