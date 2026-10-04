using FluentAssertions;

using WslCare.Core.Actions;
using WslCare.Core.Actions.DockerCleanups;
using WslCare.Core.Actions.Engine;
using WslCare.Core.Config;
using WslCare.Core.Docker;
using WslCare.Core.Files;
using WslCare.Core.Preview;
using WslCare.Core.Processes;
using WslCare.Core.Processes.Policy;
using WslCare.Core.Records;
using WslCare.TestSupport;

namespace WslCare.Core.Tests.Actions;

/// <summary>
/// A4–A7 (E3.S2) over the CAPTURED Docker: the live preview is the row of <c>preview --all</c>, the run removes EXACTLY the
/// re-checked targets (the removal is the test's scripted answer — nothing real is removed), the keep label and the
/// refusals hold, an object already gone counts as gone and not as a failure, and freed bytes are measured from what
/// Docker CONFIRMED.
/// </summary>
public sealed class DockerCleanupTests : IDisposable
{
    private readonly LinuxSandbox _sandbox = new("docker-actions");

    public void Dispose() => _sandbox.Dispose();

    private static CommandOutcome.Exited Removed(IEnumerable<string> names, string stderr = "") =>
        RecordingCommandRunner.Exited(stderr.Length > 0 ? 1 : 0, string.Join('\n', names) + "\n", stderr);

    // ---------- A4 ----------

    [Fact]
    public async Task A4_removes_exactly_the_volumes_docker_confirmed_counts_an_already_gone_one_as_gone_and_measures_freed_from_the_confirmed_ones()
    {
        var world = new DockerWorld();
        var names = DockerWorld.DanglingAnonymous;
        world.Runner.ScriptEffect(argv => argv is ["docker", "volume", "rm", ..], _ =>
            Removed(names.Take(2), $"Error response from daemon: get {names[2]}: no such volume"));
        var action = new VolumeRemoval();
        var context = DockerWorld.Context(_sandbox);
        var commands = world.Commands(action, context);

        var preview = await action.PreviewAsync(context, commands, CancellationToken.None);
        var run = await action.RunAsync(context, preview, commands, CancellationToken.None);

        preview.Count.Should().Be(3);
        world.Calls("volume", "rm").Should().ContainSingle().Which.Argv.Skip(3).Should().Equal(names);
        run.Succeeded.Should().BeTrue(run.Failure);
        run.Removed.Select(r => r.Key).Should().Equal(names.Take(2));
        run.FreedBytes.Should().Be(DockerWorld.VolumeBytes(names[0]) + DockerWorld.VolumeBytes(names[1]), "a volume docker volume rm did not confirm counts nothing (plan 15c #1)");
        run.NotRemoved.Should().ContainSingle().Which.Note.Should().StartWith("already gone");
        run.BeforeBytes.Should().Be(43_030_000_000L, "Docker's Local Volumes total of the look, beside the measured figure");
        File.Exists(_sandbox.Paths.DistroPath("/var/lib/wsl-care/volume-seen.json")).Should().BeTrue("the action records first sightings (plan 15b #3)");
        world.Runner.Requests.Should().OnlyContain(r => CommandPolicy.Product.Review(r).IsAllowed);
    }

    [Fact]
    public async Task A4_never_names_a_volume_carrying_the_keep_label()
    {
        var keep = DockerWorld.DanglingAnonymous[1];
        var world = new DockerWorld(keepVolume: keep);
        world.Runner.ScriptEffect(argv => argv is ["docker", "volume", "rm", ..], r => Removed(r.Argv.Skip(3)));
        var action = new VolumeRemoval();
        var context = DockerWorld.Context(_sandbox);
        var commands = world.Commands(action, context);

        var run = await action.RunAsync(context, await action.PreviewAsync(context, commands, CancellationToken.None), commands, CancellationToken.None);

        run.Count.Should().Be(2);
        world.Calls("volume", "rm").SelectMany(r => r.Argv).Should().NotContain(keep);
    }

    [Fact]
    public async Task A4_never_removes_a_hex_named_volume_docker_treats_as_named_nor_one_whose_labels_are_unknown()
    {
        // docker volume create without a name: a random 64-hex name and NO com.docker.volume.anonymous label — Docker 23+'s
        // volume prune keeps it as named, so A4 taking it would be volume prune --all, the never-list's own example.
        var names = DockerWorld.DanglingAnonymous;
        var unknown = new string('a', 64);
        var world = new DockerWorld(unlabelledVolume: names[0], unlistedDangling: unknown);
        world.Runner.ScriptEffect(argv => argv is ["docker", "volume", "rm", ..], r => Removed(r.Argv.Skip(3)));
        var action = new VolumeRemoval();
        var context = DockerWorld.Context(_sandbox);
        var commands = world.Commands(action, context);

        var preview = await action.PreviewAsync(context, commands, CancellationToken.None);
        var run = await action.RunAsync(context, preview, commands, CancellationToken.None);

        preview.Targets.Select(t => t.Key).Should().Equal(names.Skip(1), "only volumes carrying Docker's anonymous label AND a 64-hex name are A4's");
        world.Calls("volume", "rm").SelectMany(r => r.Argv).Should().NotContain(names[0]).And.NotContain(unknown);
        run.Count.Should().Be(2);
        new VolumeSeenStore(_sandbox.Paths, _sandbox.Files).Read().Record.Volumes.Select(v => v.Name).Should().NotContain(names[0]).And.NotContain(unknown, "first sightings are kept for anonymous volumes only");
    }

    [Theory]
    [InlineData("22.0.4", "is below 23")]
    [InlineData("nightly", "could not be read as a number")]
    public async Task A4_refuses_on_docker_below_23_or_of_an_unreadable_version_and_runs_no_removal(string version, string reason)
    {
        var world = new DockerWorld(serverVersion: version);
        var engine = Engine(world);

        var result = await engine.ExecuteAsync(new ActRequest([ActionId.Find("A4")!], RunTrigger.Cli, Execute: true), CancellationToken.None);

        var a4 = result.Should().BeOfType<ActResult.Done>().Subject.Detail.Actions.Single();
        a4.Status.Should().Be(ActionStatus.Refused);
        a4.Reason.Should().Contain(reason);
        world.Calls("volume", "rm").Should().BeEmpty();
    }

    [Fact]
    public async Task A4_on_a_button_without_the_shown_list_refuses_and_with_it_removes_only_shown_volumes_that_are_still_candidates()
    {
        var names = DockerWorld.DanglingAnonymous;
        var world = new DockerWorld();
        world.Runner.ScriptEffect(argv => argv is ["docker", "volume", "rm", ..], r => Removed(r.Argv.Skip(3)));
        var engine = Engine(world);
        var a4 = ActionId.Find("A4")!;

        var bare = await engine.ExecuteAsync(new ActRequest([a4], RunTrigger.Manual, Execute: true), CancellationToken.None);
        var shown = await engine.ExecuteAsync(new ActRequest([a4], RunTrigger.Manual, Execute: true) { ShownVolumes = ShownList.Of([names[0], new string('f', 64)]) }, CancellationToken.None);

        bare.Should().BeOfType<ActResult.Done>().Which.Detail.Actions.Single().Reason.Should().Be(VolumeRemoval.ButtonNeedsShownList);
        var outcome = shown.Should().BeOfType<ActResult.Done>().Subject.Detail.Actions.Single();
        outcome.Status.Should().Be(ActionStatus.Ran, outcome.Reason);
        outcome.Preview!.Facts[VolumeRemoval.ShownNotCandidatesFact].Should().Be(1, "a shown name that is no longer a candidate is never removed");
        world.Calls("volume", "rm").Should().ContainSingle().Which.Argv.Skip(3).Should().Equal(names[0]);
        RunHistory.Read(_sandbox.Paths, _sandbox.Files).Records.Should().OnlyContain(r => r.Trigger == RunTrigger.Manual);
    }

    /// <summary>§15j B1: the preview's items stop at 20, so a button that sent back the ITEMS would pass 20 of 387. A4's
    /// preview outcome carries <c>shown</c> — every name it selected, the keys its run matches — and no other action's does.</summary>
    [Fact]
    public async Task A4s_preview_outcome_carries_every_selected_name_as_shown_and_no_other_actions_outcome_carries_one()
    {
        var engine = Engine(new DockerWorld());

        var result = await engine.PreviewAsync(new ActRequest([ActionId.Find("A4")!, ActionId.Find("A5")!, ActionId.Find("A10")!], RunTrigger.Cli, Execute: false), CancellationToken.None);

        var outcomes = result.Should().BeOfType<ActResult.Previewed>().Subject.Actions;
        var a4 = outcomes.Single(o => o.Id == "A4");
        a4.Shown.Should().BeEquivalentTo(DockerWorld.DanglingAnonymous, "every anonymous volume the preview selected, by the name its run matches");
        a4.Shown!.Count.Should().Be(a4.Preview!.Count);
        a4.ShownTruncated.Should().BeNull("three names fit a shown list: the flag is absent, not false");
        outcomes.Where(o => o.Id != "A4").Should().OnlyContain(o => o.Shown == null, "only A4 is bound to its shown list (plan §15f #11)");
    }

    /// <summary>The cap: a preview selecting more than <see cref="ShownList.MaxNames"/> names lists the first that many — the
    /// most a shown list can carry back.</summary>
    [Fact]
    public void A4s_shown_list_is_every_target_key_in_order_capped_at_the_most_a_shown_list_carries()
    {
        var targets = Enumerable.Range(0, ShownList.MaxNames + 5).Select(i => new ActionItem("volume", $"v{i}", 1) { Key = i.ToString("x64", System.Globalization.CultureInfo.InvariantCulture) }).ToList();
        var preview = ActionPreview.Of("what", targets.Count, targets.Count, "basis", new Dictionary<string, long>(), string.Empty, targets);

        var shown = new VolumeRemoval().Shown(preview);

        shown.Should().HaveCount(ShownList.MaxNames);
        shown[0].Should().Be(targets[0].Key);
        shown[^1].Should().Be(targets[ShownList.MaxNames - 1].Key);
        ShownList.Truncates(preview.Count).Should().BeTrue("count stays the total; shownTruncated says only the first 10 000 go (coai #11)");
        ShownList.Truncates(ShownList.MaxNames).Should().BeFalse();
    }

    /// <summary>E6.S0 review D2: a removal cut off by a signal keeps what Docker CONFIRMED in the batches before — those
    /// deletions are real — and names the batch in flight as unknown and the rest as not attempted, instead of throwing
    /// everything it knew away.</summary>
    [Fact]
    public async Task A_removal_cut_off_mid_batch_keeps_the_confirmed_batches_and_names_the_rest()
    {
        using var signal = new CancellationTokenSource();
        var world = new DockerWorld();
        var targets = Enumerable.Range(1, DockerCleanupCommands.Batch * 2 + 1)
            .Select(i => i.ToString("x64", System.Globalization.CultureInfo.InvariantCulture))
            .Select(name => new ActionItem("volume", name, 10) { Key = name }).ToList();
        var calls = 0;
        world.Runner.ScriptEffect(argv => argv is ["docker", "volume", "rm", ..], r =>
        {
            if (++calls == 1)
            {
                return Removed(r.Argv.Skip(3));
            }

            signal.Cancel();
            throw new OperationCanceledException(signal.Token);
        });
        var context = DockerWorld.Context(_sandbox);
        var commands = world.Commands(new VolumeRemoval(), context);

        var removal = await DockerRemovals.RemoveAsync(commands, DockerCleanupCommands.VolumeRemove, targets, new Dictionary<string, string>(StringComparer.Ordinal), signal.Token);

        removal.Interrupted.Should().BeTrue();
        removal.Removed.Should().HaveCount(DockerCleanupCommands.Batch, "the first batch was confirmed before the signal");
        removal.NotRemoved.Where(n => n.Note.StartsWith("unknown: cut off mid-command", StringComparison.Ordinal)).Should().HaveCount(DockerCleanupCommands.Batch);
        removal.NotRemoved.Where(n => n.Note.StartsWith("not attempted", StringComparison.Ordinal)).Should().ContainSingle();
    }

    [Fact]
    public async Task A4_keeps_a_volume_docker_refuses_as_in_use_and_fails_only_on_an_answer_it_cannot_read()
    {
        var names = DockerWorld.DanglingAnonymous;
        var world = new DockerWorld();
        world.Runner.ScriptEffect(argv => argv is ["docker", "volume", "rm", ..], _ => Removed([names[0]],
            $"Error response from daemon: remove {names[1]}: volume is in use - [0997307242ff]\nsomething else about {names[2]}"));
        var action = new VolumeRemoval();
        var context = DockerWorld.Context(_sandbox);
        var commands = world.Commands(action, context);

        var run = await action.RunAsync(context, await action.PreviewAsync(context, commands, CancellationToken.None), commands, CancellationToken.None);

        run.Count.Should().Be(1);
        run.NotRemoved.Single(n => n.Key == names[1]).Note.Should().StartWith("kept by Docker, in use");
        run.Failure.Should().Contain(names[2], "an answer about a volume that is neither removed, gone nor in use is not counted and fails the action");
    }

    // ---------- A5 ----------

    [Fact]
    public async Task A5_removes_the_stopped_containers_docker_confirms_and_counts_only_the_anonymous_volumes_that_went_with_them()
    {
        var world = new DockerWorld();
        var action = new ContainerRemoval(testcontainers: false);
        var context = DockerWorld.Context(_sandbox);
        var commands = world.Commands(action, context);
        var preview = await action.PreviewAsync(context, commands, CancellationToken.None);
        var containers = preview.Targets.Where(t => t.Kind == "container").ToList();
        var volumes = preview.Targets.Where(t => t.Kind == "anonymous volume").ToList();
        // Docker confirms all but the first (it started since: "container is running"); one volume stays listed.
        world.Runner.ScriptEffect(argv => argv is ["docker", "rm", "-v", ..], r => Removed(r.Argv.Skip(3).Skip(1),
            $"Error response from daemon: cannot remove container \"/{containers[0].Name}\": container is running: stop the container before removing or force remove"));
        var remaining = volumes.Where(v => v.Key.StartsWith(containers[0].Key, StringComparison.Ordinal)).Select(v => v.Name).Append(volumes.Last().Name);
        world.Runner.Script(DockerCommands.VolumeList.Argv, 0, string.Join('\n', remaining));

        var run = await action.RunAsync(context, preview, commands, CancellationToken.None);

        containers.Should().HaveCount(13);
        run.Succeeded.Should().BeTrue(run.Failure);
        run.Count.Should().Be(12);
        run.NotRemoved.Should().ContainSingle(n => n.Key == containers[0].Key).Which.Note.Should().StartWith("kept by Docker, in use");
        var goneVolumes = volumes.Where(v => !remaining.Contains(v.Name)).ToList();
        run.Removed.Where(r => r.Kind == "anonymous volume").Select(r => r.Name).Should().BeEquivalentTo(goneVolumes.Select(v => v.Name));
        run.FreedBytes.Should().Be(containers.Skip(1).Sum(c => c.Bytes ?? 0) + goneVolumes.Sum(v => v.Bytes ?? 0));
        world.Calls("rm", "-v").SelectMany(r => r.Argv).Should().NotContain("-f").And.NotContain("--force");
    }

    [Fact]
    public async Task A5_counts_an_anonymous_volume_two_removed_containers_share_once_in_the_preview_and_in_the_freed_bytes()
    {
        var context = DockerWorld.Context(_sandbox);
        var action = new ContainerRemoval(testcontainers: false);
        var plain = await action.PreviewAsync(context, new DockerWorld().Commands(action, context), CancellationToken.None);
        var holder = plain.Targets.First(t => t.Kind == "anonymous volume");
        var volume = holder.Name;
        var other = plain.Targets.First(t => t.Kind == "container" && !holder.Key.StartsWith(t.Key, StringComparison.Ordinal));
        var world = new DockerWorld(shareVolume: (other.Key, volume));
        world.Runner.ScriptEffect(argv => argv is ["docker", "rm", "-v", ..], r => Removed(r.Argv.Skip(3)));
        world.Runner.Script(DockerCommands.VolumeList.Argv, 0, string.Empty);
        var commands = world.Commands(action, context);

        var preview = await action.PreviewAsync(context, commands, CancellationToken.None);
        var run = await action.RunAsync(context, preview, commands, CancellationToken.None);

        preview.Targets.Count(t => t.Name == volume).Should().Be(1, "a volume shared by two selected containers is one object");
        run.Removed.Count(r => r.Name == volume).Should().Be(1, "it went once");
        run.FreedBytes.Should().Be(preview.Bytes, "every container and every distinct volume, each counted once");
    }

    [Fact]
    public async Task A5_counts_a_hex_named_volume_without_dockers_anonymous_label_as_named_and_kept_not_as_going_with_its_container()
    {
        var context = DockerWorld.Context(_sandbox);
        var action = new ContainerRemoval(testcontainers: false);
        var plain = new DockerWorld();
        var before = await action.PreviewAsync(context, plain.Commands(action, context), CancellationToken.None);
        var volume = before.Targets.First(t => t.Kind == "anonymous volume").Name;
        var world = new DockerWorld(unlabelledVolume: volume);

        var after = await action.PreviewAsync(context, world.Commands(action, context), CancellationToken.None);

        after.Targets.Where(t => t.Kind == "anonymous volume").Select(t => t.Name).Should().NotContain(volume, "docker rm -v keeps a volume Docker does not treat as anonymous");
        after.Bytes.Should().Be(before.Bytes - DockerWorld.VolumeBytes(volume));
    }

    [Fact]
    public async Task A5_never_selects_a_container_carrying_the_keep_label_and_a5_testcontainers_takes_only_labelled_ones()
    {
        var details = await StoppedIdsAsync();
        var world = new DockerWorld(keepContainer: details[0], testcontainer: details[1]);
        var context = DockerWorld.Context(_sandbox);
        var a5 = new ContainerRemoval(testcontainers: false);
        var tc = new ContainerRemoval(testcontainers: true);

        var others = await a5.PreviewAsync(context, world.Commands(a5, context), CancellationToken.None);
        var labelled = await tc.PreviewAsync(context, world.Commands(tc, context), CancellationToken.None);

        others.Targets.Select(t => t.Key).Should().NotContain(details[0]).And.NotContain(details[1]);
        others.Count.Should().Be(11);
        labelled.Targets.Where(t => t.Kind == "container").Select(t => t.Key).Should().Equal(details[1]);
    }

    [Fact]
    public async Task A5_with_nothing_selected_starts_no_removal()
    {
        var world = new DockerWorld();
        var action = new ContainerRemoval(testcontainers: true);
        var context = DockerWorld.Context(_sandbox);
        var commands = world.Commands(action, context);

        var run = await action.RunAsync(context, await action.PreviewAsync(context, commands, CancellationToken.None), commands, CancellationToken.None);

        run.Count.Should().Be(0);
        world.Calls("rm").Should().BeEmpty();
    }

    // ---------- A6 ----------

    [Fact]
    public async Task A6_unused_prunes_with_the_age_and_the_keep_filter_and_takes_dockers_own_total_as_freed()
    {
        var world = new DockerWorld();
        world.Runner.ScriptEffect(argv => argv is ["docker", "image", "prune", ..], _ => RecordingCommandRunner.Exited(0,
            "Deleted Images:\nuntagged: composer:2\ndeleted: sha256:af98f42dfff7c68ba8d53c2164fd9fde1087b7d449514baa38c418b1f6bc4bac\ndeleted: sha256:ebfe2f90462722a7a4de65e91990e97fe0d401c70e0e762c5b53302f905ec1c1\n\nTotal reclaimed space: 531.4MB\n"));
        var action = new ImagePrune(unused: true);
        var context = DockerWorld.Context(_sandbox, """{ "images": { "unusedOlderThanDays": 7 } }""");
        var commands = world.Commands(action, context);

        var preview = await action.PreviewAsync(context, commands, CancellationToken.None);
        var run = await action.RunAsync(context, preview, commands, CancellationToken.None);

        preview.Count.Should().Be(8);
        world.Calls("image", "prune").Should().ContainSingle().Which.Argv.Should().Equal("docker", "image", "prune", "-a", "-f", "--filter", "until=168h", "--filter", "label!=wsl-care.keep=true");
        run.FreedBytes.Should().Be(531_400_000L);
        run.FreedBasis.Should().Be("Docker's own Total reclaimed space");
        run.Count.Should().Be(2);
    }

    [Fact]
    public async Task A6_dangling_with_no_dangling_image_starts_no_prune()
    {
        var world = new DockerWorld();
        var action = new ImagePrune(unused: false);
        var context = DockerWorld.Context(_sandbox);
        var commands = world.Commands(action, context);

        var run = await action.RunAsync(context, await action.PreviewAsync(context, commands, CancellationToken.None), commands, CancellationToken.None);

        run.Count.Should().Be(0);
        world.Calls("image", "prune").Should().BeEmpty("the capture holds no dangling image: a prune is asked only when the preview found something");
    }

    // ---------- A7 ----------

    [Fact]
    public async Task A7_on_the_timer_prunes_down_to_the_cap_with_the_flag_this_dockers_help_lists_and_a_button_prunes_all()
    {
        var timer = await A7Async(RunTrigger.Timer, DockerFixture.Read("builder-prune-help.out"));
        var button = await A7Async(RunTrigger.Manual, DockerFixture.Read("builder-prune-help.out"));

        timer.World.Calls("builder", "prune").Last().Argv.Should().Equal("docker", "builder", "prune", "-f", "--max-used-space", "20GB");
        timer.Run.FreedBytes.Should().Be(23_980_000L, "Docker's own Total:");
        button.World.Calls("builder", "prune").Should().ContainSingle().Which.Argv.Should().Equal("docker", "builder", "prune", "-a", "-f");
    }

    [Fact]
    public async Task A7_on_the_timer_takes_keep_storage_from_an_older_builder_and_refuses_when_neither_cap_is_listed()
    {
        var older = await A7Async(RunTrigger.Timer, "Usage:  docker builder prune\n\nOptions:\n  -a, --all\n      --keep-storage bytes   Amount of disk space to keep for cache\n");
        var neither = await A7Async(RunTrigger.Timer, "Usage:  docker builder prune\n\nOptions:\n  -a, --all\n  -f, --force\n");

        older.World.Calls("builder", "prune").Last().Argv.Should().Equal("docker", "builder", "prune", "-f", "--keep-storage", "20GB");
        neither.Preview.Refusal.Should().Contain("takes no size cap");
    }

    private async Task<(DockerWorld World, ActionPreview Preview, ActionRun Run)> A7Async(RunTrigger trigger, string help)
    {
        var world = new DockerWorld();
        world.Runner.Script(argv => argv is ["docker", "builder", "prune", "--help"], RecordingCommandRunner.Exited(0, help));
        world.Runner.ScriptEffect(argv => argv is ["docker", "builder", "prune", "-f" or "-a", ..], _ => RecordingCommandRunner.Exited(0, "ID\tRECLAIMABLE\tSIZE\tLAST ACCESSED\nk2ztc1xq9f\ttrue\t23.98MB\t2 days ago\nTotal:\t23.98MB\n"));
        var action = new BuildCachePrune();
        var context = DockerWorld.Context(_sandbox, DockerWorld.AllAges, trigger);
        var commands = world.Commands(action, context);
        var preview = await action.PreviewAsync(context, commands, CancellationToken.None);
        return (world, preview, preview.Refusal.Length > 0 ? ActionRun.Nothing([]) : await action.RunAsync(context, preview, commands, CancellationToken.None));
    }

    // ---------- shared ----------

    [Fact]
    public async Task Every_docker_and_folder_actions_preview_equals_its_row_of_preview_all()
    {
        // A folder sample, so A8 / A9 compare figures, not two equal "unavailable"s.
        var sampledAt = DockerWorld.Now.AddHours(-2);
        new RunRecordWriter(_sandbox.Paths, _sandbox.Files).Append(new RunRecord(SchemaVersion.Current, RunId.New(sampledAt, 5), RunTrigger.Timer, sampledAt, sampledAt, RunOutcome.Completed, [])
        {
            Slow = new SlowParts { Folders = new FolderSizesSample(sampledAt, [new("npm-cache", "/home/me/.npm", 5_300_000_000, 41_000, true, string.Empty), new("apt-cache", "/var/cache/apt", 116_815_541, 40, true, string.Empty), new("snap-disabled", "/var/lib/snapd/snaps", 300_000_000, 2, true, string.Empty)]) },
        });
        var context = DockerWorld.Context(_sandbox);
        var report = await PreviewRun.RunAsync(_sandbox.Paths, _sandbox.Files, new DockerWorld().Runner, new FixedTimeProvider(DockerWorld.Now), ConfigLoader.Load(_sandbox.Paths, _sandbox.Files), CancellationToken.None);
        var withRows = ActionRegistry.Product.Actions.Where(a => report.Rows.Any(r => r.Id == a.Id.Text)).ToList();

        withRows.Select(a => a.Id.Text).Should().BeEquivalentTo(report.Rows.Select(r => r.Id), "every row of preview --all is some action's");
        foreach (var action in withRows)
        {
            var row = report.Rows.Single(r => r.Id == action.Id.Text);
            var preview = await action.PreviewAsync(context, new DockerWorld().Commands(action, context), CancellationToken.None);
            preview.Should().Match<ActionPreview>(p => p.What == row.What && p.Available == row.Available && p.Basis == row.Basis, action.Id.Text);
            (preview.Available ? preview.Count : (int?)null).Should().Be(row.Count, action.Id.Text);
            (preview.Available ? preview.Bytes : null).Should().Be(row.ReclaimableBytes, action.Id.Text);
            preview.Refusal.Should().Be(row.Refusal ?? string.Empty, action.Id.Text);
        }
    }

    private ActionEngine Engine(DockerWorld world)
    {
        _sandbox.Write("/home/me/.config/wsl-care/config.json", DockerWorld.AllAges);
        _sandbox.Write("/etc/passwd", "root:x:0:0::/root:/bin/bash\n");
        return new ActionEngine(new EngineContext(_sandbox.Paths, _sandbox.Files, world.Runner, new FixedTimeProvider(DockerWorld.Now), new FakeProbe(Core.Hosting.HostSide.Wsl, new FixedTimeProvider(DockerWorld.Now)),
            ConfigLoader.Load(_sandbox.Paths, _sandbox.Files), new FakeProcessTable(), 77, ActionRegistry.Product));
    }

    private static async Task<IReadOnlyList<string>> StoppedIdsAsync()
    {
        var snapshot = await new DockerCollector(new DockerCli(DockerFixture.Runner()), new FixedTimeProvider(DockerWorld.Now)).CollectAsync(CancellationToken.None);
        return [.. snapshot.Details.ValueOr([]).Where(d => d.Stopped).Select(d => d.Id)];
    }
}
