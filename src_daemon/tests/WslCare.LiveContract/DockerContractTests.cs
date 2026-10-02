using FluentAssertions;

using WslCare.Core.Collectors;
using WslCare.Core.Docker;

namespace WslCare.LiveContract;

/// <summary>
/// The real <c>docker</c> of this machine against the product's parsers (plan §15b #6). Each test runs the
/// product's own <see cref="DockerCommands"/> argv and asserts structural facts that hold on any healthy
/// Docker — and, where Docker reports a figure twice (a total in <c>system df</c>, the rows in
/// <c>system df -v</c>), that the product's arithmetic over the rows lands on Docker's own total.
/// </summary>
public sealed class DockerContractTests
{
    /// <summary>Docker prints four significant digits; a sum of many rounded rows drifts from the rounded
    /// total by at most this much (and by a few kB when nothing is reclaimable at all).</summary>
    private const double RelativeTolerance = 0.01;
    private const long AbsoluteToleranceBytes = 2_000_000;

    [Fact]
    public async Task Docker_version_names_a_server_the_product_reads_as_reachable()
    {
        var reachability = DockerReachability.From(new DockerAnswer.Answered(await Live.DockerAsync(DockerCommands.Version)));

        var engine = reachability.Should().BeOfType<DockerReachability.Reachable>().Subject.Engine;
        engine.ServerVersion.Should().NotBeEmpty();
        engine.ServerMajor.Should().BeGreaterThan(0, "the server version starts with its major number");
        engine.ClientVersion.Should().NotBeEmpty();
    }

    [Fact]
    public async Task System_df_has_the_four_types_with_every_figure_readable()
    {
        var totals = Available(DockerTotal.Parse(await Live.DockerAsync(DockerCommands.SystemDf)));

        totals.Select(t => t.Type).Should().BeEquivalentTo([DockerTotal.Images, DockerTotal.Containers, DockerTotal.Volumes, DockerTotal.BuildCache]);
        totals.Should().OnlyContain(t => t.TotalCount.IsAvailable && t.Active.IsAvailable && t.SizeBytes.IsAvailable && t.ReclaimableBytes.IsAvailable);
    }

    [Fact]
    public async Task System_df_v_rows_add_up_to_dockers_own_totals()
    {
        var (df, dfv) = await Live.QuietAsync(DockerCommands.SystemDf, DockerCommands.SystemDfVerbose, Reclaimable);
        var totals = Available(DockerTotal.Parse(df)).ToDictionary(t => t.Type);
        var inventory = Available(DockerInventory.Parse(dfv));

        inventory.Images.Should().OnlyContain(i => i.UniqueBytes.IsAvailable && i.Containers.IsAvailable && i.CreatedAt.IsAvailable);
        inventory.Volumes.Should().OnlyContain(v => v.SizeBytes.IsAvailable && v.Links.IsAvailable);
        inventory.Containers.Should().OnlyContain(c => c.SizeBytes.IsAvailable && c.CreatedAt.IsAvailable && c.Id.Length == 64);
        inventory.BuildCache.Should().OnlyContain(b => b.SizeBytes.IsAvailable && b.LastUsedAt.IsAvailable);

        Near(inventory.Images.Where(i => i.Containers.ValueOr(1) == 0).Sum(i => i.UniqueBytes.ValueOr(0)), totals[DockerTotal.Images].ReclaimableBytes.ValueOr(-1), "images no container uses");
        Near(inventory.Volumes.Where(v => v.Links.ValueOr(1) == 0).Sum(v => v.SizeBytes.ValueOr(0)), totals[DockerTotal.Volumes].ReclaimableBytes.ValueOr(-1), "volumes no container refers to");
        Near(inventory.BuildCache.Where(b => b.Reclaimable).Sum(b => b.SizeBytes.ValueOr(0)), totals[DockerTotal.BuildCache].ReclaimableBytes.ValueOr(-1), "build cache neither in use nor shared");
        inventory.Images.Should().HaveCount(totals[DockerTotal.Images].TotalCount.ValueOr(-1));
        inventory.Volumes.Should().HaveCount(totals[DockerTotal.Volumes].TotalCount.ValueOr(-1));
    }

    [Fact]
    public async Task Volume_ls_dangling_names_the_volumes_df_v_shows_without_a_link()
    {
        var (volumes, dfv) = await Live.QuietAsync(DockerCommands.DanglingVolumes, DockerCommands.SystemDfVerbose, text => text);
        var dangling = DanglingVolumes.Parse(volumes);
        var inventory = Available(DockerInventory.Parse(dfv));

        dangling.Should().BeEquivalentTo(inventory.Volumes.Where(v => v.Links.ValueOr(1) == 0).Select(v => v.Name));
    }

    [Fact]
    public async Task Ps_a_rows_read_by_the_same_container_parser_name_the_containers_df_v_lists()
    {
        var (ps, dfv) = await Live.QuietAsync(DockerCommands.ContainerList, DockerCommands.SystemDfVerbose, Ids);
        var listed = Available(DockerJson.Lines(ps, "docker ps -a", ContainerRow.From));
        var inventory = Available(DockerInventory.Parse(dfv));

        listed.Should().OnlyContain(c => c.Id.Length == 64 && c.Name.Length > 0 && c.State.Length > 0 && c.CreatedAt.IsAvailable);
        listed.Select(c => c.Id).Should().BeEquivalentTo(inventory.Containers.Select(c => c.Id));
    }

    [Fact]
    public async Task Container_inspect_through_the_template_answers_every_container_and_never_its_environment()
    {
        var inventory = Available(DockerInventory.Parse(await Live.DockerAsync(DockerCommands.SystemDfVerbose)));
        Assert.SkipWhen(inventory.Containers.Count == 0, "no container exists to inspect");
        var details = new List<ContainerDetail>();
        foreach (var batch in inventory.Containers.Select(c => c.Id).Chunk(DockerCommands.InspectBatch))
        {
            var stdout = await Live.DockerAsync(DockerCommands.ContainerInspect(batch));
            stdout.Should().NotContain("\"Env\"").And.NotContain("PATH=", "the template prints only the fields it names");
            details.AddRange(Available(ContainerDetail.Parse(stdout)));
        }

        // A container removed between the listing and the inspect is simply absent (DockerCli reads the rest).
        details.Should().NotBeEmpty();
        details.Select(d => d.Id).Should().BeSubsetOf(inventory.Containers.Select(c => c.Id));
        details.Should().OnlyContain(d => d.CreatedAt.IsAvailable && d.State.Length > 0 && d.ImageId.StartsWith("sha256:") && d.LogDriver.Length > 0);
        details.Where(d => d.Stopped).Should().OnlyContain(d => d.StoppedSince.IsAvailable);
        var volumes = inventory.Volumes.Select(v => v.Name).ToHashSet();
        details.SelectMany(d => d.Mounts).Where(m => m.Type == "volume").Should().OnlyContain(m => volumes.Contains(m.Name), "every volume a container mounts is one df -v lists");
    }

    [Fact]
    public async Task Stats_no_stream_reads_one_sample_per_running_container()
    {
        var stats = Available(DockerStats.Parse(await Live.DockerAsync(DockerCommands.Stats)));
        var inventory = Available(DockerInventory.Parse(await Live.DockerAsync(DockerCommands.SystemDfVerbose)));

        stats.Select(s => s.Id).Should().BeEquivalentTo(inventory.Containers.Where(c => c.State == "running").Select(c => c.Id));
        stats.Should().OnlyContain(s => s.MemoryBytes >= 0 && s.CpuPercent >= 0 && s.Name.Length > 0);
    }

    [Fact]
    public async Task Events_of_the_last_day_are_container_starts_inside_the_window()
    {
        var until = DateTimeOffset.UtcNow;
        var since = until.AddHours(-24); // the follower's backfill bound (plan §15b #0)

        var events = Available(DockerEvents.Parse(await Live.DockerAsync(DockerCommands.Events(since, until))));

        // A day with no start is an ordinary answer: then this proves the exit and the empty parse only.
        events.Where(e => e.Type != "container" || e.Action != "start" || e.Id.Length != 64).Should().BeEmpty();
        events.Where(e => e.At < since.AddSeconds(-1) || e.At > until.AddSeconds(1)).Should().BeEmpty();
    }

    [Fact]
    public async Task The_bridge_network_names_an_engine_start_no_later_than_any_running_container_started()
    {
        // The continuity rule's engine signal (gate finding #2/#7/#9): the default bridge is re-created at every engine start,
        // so its creation is the engine's start. No container can be running from before its engine started.
        var engine = Available(DockerEngineStart.Parse(await Live.DockerAsync(DockerCommands.EngineStart)));
        engine.Id.Should().HaveLength(64);
        engine.StartedAt.Should().BeBefore(DateTimeOffset.UtcNow.AddSeconds(5));

        var inventory = Available(DockerInventory.Parse(await Live.DockerAsync(DockerCommands.SystemDfVerbose)));
        var running = inventory.Containers.Where(c => c.State == "running").Select(c => c.Id).ToList();
        Assert.SkipWhen(running.Count == 0, "no running container to compare the engine's start with");
        var starts = new List<DateTimeOffset>();
        foreach (var batch in running.Chunk(DockerCommands.InspectBatch))
        {
            foreach (var line in (await Live.DockerAsync(DockerCommands.ContainerInspect(batch))).Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                using var row = System.Text.Json.JsonDocument.Parse(line);
                starts.AddRange(DockerText.Instant(row.RootElement.GetProperty("startedAt").GetString() ?? string.Empty) is Reading<DateTimeOffset>.Available { Value: var at } ? [at] : []);
            }
        }

        starts.Should().NotBeEmpty();
        starts.Min().Should().BeOnOrAfter(engine.StartedAt.AddSeconds(-1), "every running container started after its engine (a second for the two clocks' rounding)");
    }

    /// <summary>What the sums are compared with: counts and reclaimable figures, not the total size a running
    /// container grows.</summary>
    private static string Reclaimable(string systemDf) =>
        string.Join(';', DockerTotal.Parse(systemDf).ValueOr([]).Select(t => $"{t.Type}:{t.TotalCount.ValueOr(-1)}:{t.ReclaimableBytes.ValueOr(-1)}"));

    /// <summary>Which containers exist, not how long each has been up.</summary>
    private static string Ids(string ps) =>
        string.Join(';', DockerJson.Lines(ps, "docker ps -a", ContainerRow.From).ValueOr([]).Select(c => c.Id).Order(StringComparer.Ordinal));

    private static T Available<T>(Reading<T> reading)
    {
        reading.Should().BeOfType<Reading<T>.Available>("the product's parser reads the real answer ({0})", reading.ReasonOrEmpty);
        return ((Reading<T>.Available)reading).Value;
    }

    private static void Near(long sum, long total, string what)
    {
        total.Should().BeGreaterThanOrEqualTo(0, $"Docker's total for {what} is readable");
        var tolerance = Math.Max(AbsoluteToleranceBytes, (long)(total * RelativeTolerance));
        sum.Should().BeCloseTo(total, (ulong)tolerance, $"the product's sum over the rows of {what} is Docker's own reclaimable figure");
    }
}
