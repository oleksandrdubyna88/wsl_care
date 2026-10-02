using System.Text.Json;

using FluentAssertions;

using WslCare.Cli;
using WslCare.Core;
using WslCare.Core.Docker;
using WslCare.Core.Json;
using WslCare.Core.Preview;
using WslCare.Core.Processes;
using WslCare.TestSupport;

namespace WslCare.Cli.Tests;

/// <summary><c>preview --all [--json]</c> in-process over the captured Docker answers (plan §6).</summary>
public sealed class PreviewCommandTests
{
    private static PreviewReport Report(string stdout) =>
        JsonSerializer.Deserialize(stdout, WslCareJsonContext.Default.PreviewReport) ?? throw new InvalidOperationException("preview --all --json printed null");

    [Fact]
    public void Preview_json_answers_every_row_the_kept_volumes_and_dockers_totals_with_read_commands_only()
    {
        using var sandbox = new SandboxHost("preview-json");
        var runner = DockerFixture.Runner();
        var host = new CliHost(sandbox.Paths, sandbox.Files, new FixedTimeProvider(DockerFixture.CapturedAt), runner);

        var (exit, stdout, stderr) = CliRun.Over(host, "preview", "--all", "--json");

        exit.Should().Be((int)ExitCode.Ok, stderr);
        var report = Report(stdout);
        report.SchemaVersion.Should().Be(SchemaVersion.Current);
        report.Docker.ServerVersion.Should().Be("29.6.1");
        report.Rows.Select(r => r.Id).Should().Equal("A4", "A5", "A5Testcontainers", "A6", "A6Unused", "A7", "A8", "A9");
        report.Kept.Count.Should().Be(13);
        report.Totals.Types!.Select(t => t.Type).Should().Equal(DockerTotal.Images, DockerTotal.Containers, DockerTotal.Volumes, DockerTotal.BuildCache);
        report.VolumeSeen.Recorded.Should().BeFalse("preview only reads the state, even where it could write it (plan section 15b #3)");
        report.VolumeSeen.Tracked.Should().Be(3, "the observation is made in memory for the rows");
        File.Exists(new VolumeSeenStore(sandbox.Paths, sandbox.Files).File).Should().BeFalse();
        runner.Requests.Should().OnlyContain(r => r.Argv[0] == DockerCommands.Executable && DockerCommands.IsReadVerb(r.Argv.Skip(1).ToList()));
    }

    [Fact]
    public void Preview_text_is_one_line_per_row_and_docker_unavailable_is_an_answer_not_an_error()
    {
        using var sandbox = new SandboxHost("preview-text");
        var runner = new RecordingCommandRunner { Default = new CommandOutcome.FailedToStart("docker: not found") };
        var host = new CliHost(sandbox.Paths, sandbox.Files, new FixedTimeProvider(), runner);

        var (exit, stdout, _) = CliRun.Over(host, "preview", "--all");

        exit.Should().Be((int)ExitCode.Ok);
        var lines = CliRun.Lines(stdout);
        lines[0].Should().StartWith("wsl-care preview (");
        lines[1].Should().StartWith("docker: unavailable (notInstalled");
        lines.Where(l => l.StartsWith('A')).Should().HaveCount(8).And.OnlyContain(l => l.Contains("unavailable"));
        stdout.Where(c => c >= 128).Should().BeEmpty("a Windows console on an OEM code page mangles anything else");
        runner.Requests.Should().ContainSingle("only the version probe runs when docker is missing");
    }
}
