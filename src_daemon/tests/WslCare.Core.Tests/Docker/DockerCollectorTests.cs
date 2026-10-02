using System.Reflection;

using FluentAssertions;

using WslCare.Core.Collectors;
using WslCare.Core.Docker;
using WslCare.Core.Processes;
using WslCare.TestSupport;

namespace WslCare.Core.Tests.Docker;

/// <summary>The collector over the recording runner: what it asks Docker, and what each failure leaves behind.</summary>
public sealed class DockerCollectorTests
{
    private static readonly FixedTimeProvider Clock = new(DockerFixture.CapturedAt);

    /// <summary>Every command <see cref="DockerCommands"/> can build — ENUMERATED from the type, so a command added
    /// there is checked here without anyone listing it.</summary>
    public static IReadOnlyList<ToolCommand> EveryCommand()
    {
        var properties = typeof(DockerCommands).GetProperties(BindingFlags.Public | BindingFlags.Static)
            .Where(p => p.PropertyType == typeof(ToolCommand))
            .Select(p => (ToolCommand)p.GetValue(null)!);
        var built = typeof(DockerCommands).GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Where(m => m.ReturnType == typeof(ToolCommand))
            .Select(m => (ToolCommand)m.Invoke(null, [.. m.GetParameters().Select(Sample)])!);
        return [.. properties, .. built];
    }

    private static object Sample(ParameterInfo parameter) =>
        parameter.ParameterType == typeof(DateTimeOffset) ? DateTimeOffset.UnixEpoch : new List<string> { new('a', 64) };

    [Fact]
    public void Every_docker_command_the_product_can_build_is_a_read_verb_with_a_ceiling()
    {
        var commands = EveryCommand();

        commands.Select(c => c.Name).Should().Contain(["version", "system-df", "system-df-v", "volume-ls-dangling", "ps-a", "stats", "container-inspect", "events"]);
        commands.Should().OnlyContain(c => c.Executable == DockerCommands.Executable && DockerCommands.IsReadVerb(c.Arguments) && c.Ceiling > TimeSpan.Zero);
    }

    [Theory]
    [InlineData("volume rm x")]
    [InlineData("volume prune -f")]
    [InlineData("system prune -a")]
    [InlineData("rm -v x")]
    [InlineData("image prune -af")]
    [InlineData("builder prune -af")]
    [InlineData("container stop x")]
    public void A_write_verb_is_not_a_read_verb(string arguments) =>
        DockerCommands.IsReadVerb(arguments.Split(' ')).Should().BeFalse();

    [Fact]
    public void The_inspect_template_names_its_fields_and_never_the_environment_or_the_command()
    {
        DockerCommands.InspectTemplate.Should().NotContain(".Config.Env").And.NotContain(".Config.Cmd").And.NotContain(".Path").And.NotContain("Source");
        var act = () => DockerCommands.ContainerInspect([.. Enumerable.Repeat(new string('a', 64), DockerCommands.InspectBatch + 1)]);
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public async Task Over_the_captured_answers_every_part_is_read_with_docker_read_commands_only()
    {
        var runner = DockerFixture.Runner();

        var snapshot = await new DockerCollector(new DockerCli(runner), Clock).CollectAsync(CancellationToken.None);

        snapshot.Reachability.Should().BeOfType<DockerReachability.Reachable>();
        snapshot.Totals.IsAvailable.Should().BeTrue(snapshot.Totals.ReasonOrEmpty);
        snapshot.Inventory.IsAvailable.Should().BeTrue();
        snapshot.Dangling.IsAvailable.Should().BeTrue();
        snapshot.Details.ValueOr([]).Should().HaveCount(29);
        snapshot.UnattachedAnonymous.ValueOr([]).Should().HaveCount(3);
        runner.Requests.Select(r => r.Argv).Should().Equal(DockerFixture.Answers.Select(a => a.Command.Argv), (a, b) => a.SequenceEqual(b));
        runner.Requests.Should().OnlyContain(r => r.Argv[0] == "docker" && DockerCommands.IsReadVerb(r.Argv.Skip(1).ToList()));
    }

    public static TheoryData<string, CommandOutcome, DockerFailure> VersionFailures => new()
    {
        { "missing", new CommandOutcome.FailedToStart("docker: not found"), DockerFailure.NotInstalled },
        { "stopped", new CommandOutcome.Exited(1, new CapturedText("{\"Client\":{},\"Server\":null}", false), new CapturedText("failed to connect to the docker API at unix:///var/run/docker.sock; check if the path is correct and if the daemon is running", false), TimeSpan.Zero), DockerFailure.DaemonStopped },
        { "refused", new CommandOutcome.Exited(1, CapturedText.Empty, new CapturedText("permission denied while trying to connect to the docker API at unix:///var/run/docker.sock", false), TimeSpan.Zero), DockerFailure.SocketRefused },
        { "hung", new CommandOutcome.TimedOut(CapturedText.Empty, CapturedText.Empty, DockerCommands.ProbeCeiling), DockerFailure.TimedOut },
    };

    [Theory]
    [MemberData(nameof(VersionFailures))]
    public async Task When_no_daemon_answers_nothing_else_is_started_and_every_part_carries_the_reason(string label, CommandOutcome version, DockerFailure kind)
    {
        var runner = new RecordingCommandRunner().Script(DockerCommands.Version.Argv, version);

        var snapshot = await new DockerCollector(new DockerCli(runner), Clock).CollectAsync(CancellationToken.None);

        var problem = snapshot.Reachability.Should().BeOfType<DockerReachability.Unreachable>(label).Subject.Problem;
        problem.Kind.Should().Be(kind);
        runner.Requests.Should().ContainSingle("only the version probe runs when no daemon answers");
        new[] { snapshot.Totals.IsAvailable, snapshot.Inventory.IsAvailable, snapshot.Dangling.IsAvailable, snapshot.Details.IsAvailable }.Should().AllBeEquivalentTo(false);
        snapshot.Totals.ReasonOrEmpty.Should().Be(problem.Reason);
    }

    [Fact]
    public async Task A_failed_disk_usage_leaves_the_parts_that_depend_on_it_unavailable_and_the_rest_read()
    {
        var runner = DockerFixture.Runner().Script(DockerCommands.SystemDfVerbose.Argv, new CommandOutcome.TimedOut(CapturedText.Empty, CapturedText.Empty, DockerCommands.DiskUsageCeiling));

        var snapshot = await new DockerCollector(new DockerCli(runner), Clock).CollectAsync(CancellationToken.None);

        snapshot.Totals.IsAvailable.Should().BeTrue();
        snapshot.Dangling.IsAvailable.Should().BeTrue();
        snapshot.Inventory.ReasonOrEmpty.Should().Contain("did not answer within 120 s");
        snapshot.Details.ReasonOrEmpty.Should().Be(snapshot.Inventory.ReasonOrEmpty, "no container list, nothing to inspect");
        runner.Requests.Should().NotContain(r => r.Argv.Contains("inspect"));
    }

    [Fact]
    public async Task More_containers_than_one_batch_are_inspected_in_batches_of_one_hundred()
    {
        var ids = Enumerable.Range(0, 150).Select(i => i.ToString("x64", System.Globalization.CultureInfo.InvariantCulture)).ToList();
        // SYNTHETIC: a system df -v of 150 bare containers, in the captured file's field names.
        var dfv = "{\"Images\":[],\"Containers\":[" + string.Join(',', ids.Select(id => $"{{\"ID\":\"{id}\",\"Names\":\"c{id[..4]}\",\"State\":\"exited\",\"CreatedAt\":\"2026-10-01 10:00:00 +0000 UTC\",\"Size\":\"0B\",\"Labels\":\"\"}}")) + "],\"Volumes\":[],\"BuildCache\":[]}";
        var runner = DockerFixture.Runner()
            .Script(DockerCommands.SystemDfVerbose.Argv, 0, dfv)
            .Script(DockerCommands.ContainerInspect(ids[..100]).Argv, 0, string.Concat(ids[..100].Select(Inspected)))
            .Script(DockerCommands.ContainerInspect(ids[100..]).Argv, 0, string.Concat(ids[100..].Select(Inspected)));

        var snapshot = await new DockerCollector(new DockerCli(runner), Clock).CollectAsync(CancellationToken.None);

        runner.Requests.Count(r => r.Argv.Contains("inspect")).Should().Be(2);
        snapshot.Details.ValueOr([]).Should().HaveCount(150);
    }

    private static string Inspected(string id) =>
        $"{{\"id\":\"{id}\",\"name\":\"/c\",\"created\":\"2026-10-01T10:00:00Z\",\"state\":\"exited\",\"finishedAt\":\"2026-10-01T11:00:00Z\",\"image\":\"sha256:x\",\"logDriver\":\"json-file\",\"mounts\":[]}}\n";
}
