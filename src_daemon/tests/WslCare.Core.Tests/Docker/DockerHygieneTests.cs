using System.Text.Json;

using FluentAssertions;

using WslCare.Core.Collectors;
using WslCare.Core.Config;
using WslCare.Core.Docker;
using WslCare.Core.Hosting;
using WslCare.Core.Json;
using WslCare.Core.Preview;
using WslCare.Core.Processes;
using WslCare.TestSupport;

namespace WslCare.Core.Tests.Docker;

/// <summary>Plan §4.5's Docker hygiene audit, the docker-stats sample of a full run, and the JSON contract of
/// <c>preview --all --json</c>.</summary>
public sealed class DockerHygieneTests
{
    private static async Task<DockerSnapshot> CapturedAsync(ICommandRunner? runner = null) =>
        await new DockerCollector(new DockerCli(runner ?? DockerFixture.Runner()), new FixedTimeProvider(DockerFixture.CapturedAt)).CollectAsync(CancellationToken.None);

    [Fact]
    public async Task Unbounded_json_file_logs_are_listed_and_their_size_is_read_where_the_log_is_visible()
    {
        using var sandbox = new TempRoot("hygiene-linux");
        var paths = new LinuxHostPaths(LinuxEnvironment.Sandboxed(sandbox.Path));
        var snapshot = await CapturedAsync();
        var first = snapshot.Details.ValueOr([]).First(d => d.UnboundedLog);
        sandbox.File(first.LogPath.TrimStart('/'), new string('x', 1234));

        var audit = DockerHygiene.Audit(snapshot, paths, new Core.Files.PhysicalFileSystem(paths));

        var logs = audit.UnboundedLogs.ValueOr([]);
        logs.Should().HaveCount(28);
        logs.Single(l => l.Container == first.Name).LogBytes.Should().Be(Reading.Of(1234L), "an engine in the distro writes its logs where this side can stat them");
        logs.Where(l => l.Container != first.Name).Should().OnlyContain(l => l.LogBytes.ReasonOrEmpty.Contains("inside its own VM", StringComparison.Ordinal));
        audit.BuilderGc.ReasonOrEmpty.Should().Contain("Windows side", "the distro does not read Docker Desktop's daemon.json");
        audit.Buildkit.ValueOr([]).Should().BeEmpty("no docker-container builder exists on this machine");
    }

    [Fact]
    public async Task On_windows_the_builder_gc_of_docker_desktops_daemon_json_is_read_and_log_sizes_are_the_linux_binarys()
    {
        using var sandbox = new TempRoot("hygiene-windows");
        var paths = new WindowsHostPaths(WindowsEnvironment.Sandboxed(sandbox.Path));
        var files = new Core.Files.PhysicalFileSystem(new LinuxHostPaths(LinuxEnvironment.Sandboxed(sandbox.Path)));
        var snapshot = await CapturedAsync();

        DockerHygiene.Audit(snapshot, paths, files).BuilderGc.Should().Be(Reading.Of(new BuilderGc(false, string.Empty, string.Empty)), "no daemon.json: nothing configured");
        // The daemon.json of this machine, as Docker Desktop wrote it (2026-10-02).
        sandbox.File(Path.GetRelativePath(sandbox.Path, paths.DockerDesktopConfigFile), """{ "builder": { "gc": { "defaultKeepStorage": "20GB", "enabled": true } }, "experimental": false }""");
        var audit = DockerHygiene.Audit(snapshot, paths, files);

        audit.BuilderGc.Should().Be(Reading.Of(new BuilderGc(true, "true", "20GB")));
        audit.UnboundedLogs.ValueOr([]).Should().OnlyContain(l => l.LogBytes.ReasonOrEmpty.Contains("Linux binary", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_docker_container_builders_container_and_state_volume_are_reported()
    {
        var snapshot = await CapturedAsync();
        var inventory = snapshot.Inventory.ValueOr(null!);
        // SYNTHETIC rows in the captured shape: this machine has no docker-container builder.
        var withBuilder = snapshot with
        {
            Inventory = Reading.Of(inventory with
            {
                Containers = [.. inventory.Containers, inventory.Containers[0] with { Name = "buildx_buildkit_wide0", State = "exited" }],
                Volumes = [.. inventory.Volumes, inventory.Volumes[0] with { Name = "buildx_buildkit_wide0_state" }],
            }),
        };
        using var sandbox = new SandboxHost("hygiene-buildkit");

        var leftovers = DockerHygiene.Audit(withBuilder, sandbox.Paths, sandbox.Files).Buildkit.ValueOr([]);

        leftovers.Select(l => (l.Name, l.Kind, l.State)).Should().Equal(("buildx_buildkit_wide0", "container", "exited"), ("buildx_buildkit_wide0_state", "volume", "attached"));
    }

    [Fact]
    public async Task Docker_stats_sampled_for_a_full_run_is_one_entry_per_running_container_or_the_reason_never_an_empty_success()
    {
        var clock = new FixedTimeProvider(DockerFixture.CapturedAt);

        var sample = await DockerStats.SampleAsync(new DockerCli(DockerFixture.Runner()), clock, CancellationToken.None);
        var down = await DockerStats.SampleAsync(new DockerCli(new RecordingCommandRunner().Script(DockerCommands.Stats.Argv, 1, stderr: "Cannot connect to the Docker daemon at unix:///var/run/docker.sock. Is the docker daemon running?")), clock, CancellationToken.None);

        sample.Unavailable.Should().BeEmpty();
        sample.Containers.Should().HaveCount(16);
        sample.SampledAt.Should().Be(DockerFixture.CapturedAt);
        down.Containers.Should().BeEmpty();
        down.Unavailable.Should().Contain("no Docker daemon answers");
    }

    [Fact]
    public async Task The_json_answer_writes_an_unavailable_row_with_its_reason_and_no_count_or_bytes_key()
    {
        using var sandbox = new SandboxHost("preview-json");
        var loaded = ConfigLoader.Load(sandbox.Paths, sandbox.Files);
        var runner = new RecordingCommandRunner().Script(DockerCommands.Version.Argv, new CommandOutcome.FailedToStart("docker: not found"));

        var report = await PreviewRun.RunAsync(sandbox.Paths, sandbox.Files, runner, new FixedTimeProvider(), loaded, CancellationToken.None);
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(report, WslCareJsonContext.Default.PreviewReport));

        json.RootElement.GetProperty("schemaVersion").GetInt32().Should().Be(SchemaVersion.Current);
        json.RootElement.GetProperty("docker").GetProperty("kind").GetString().Should().Be("notInstalled");
        var a4 = json.RootElement.GetProperty("rows").EnumerateArray().First();
        a4.GetProperty("available").GetBoolean().Should().BeFalse();
        a4.GetProperty("reason").GetString().Should().Contain("not installed");
        a4.TryGetProperty("count", out _).Should().BeFalse("an unread figure is absent, never 0");
        a4.TryGetProperty("reclaimableBytes", out _).Should().BeFalse();
        json.RootElement.GetProperty("volumeSeen").GetProperty("recorded").GetBoolean().Should().BeFalse("an outage records nothing and drops no first sighting");
    }
}
