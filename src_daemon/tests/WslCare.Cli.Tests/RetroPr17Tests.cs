using FluentAssertions;

using WslCare.Cli;
using WslCare.Core.Docker;
using WslCare.TestSupport;

namespace WslCare.Cli.Tests;

/// <summary>The retro coai gate over PR #17 (E7, the daemon half): the code round's findings, each RED first.</summary>
public sealed class RetroPr17Tests
{
    private const char Escape = '\u001b';

    // F2: the checks' details pass through CommandLine.Printable, the versions line did not — a version string the docker
    // daemon answers reached the operator's terminal raw (an OSC 52 sequence writes their clipboard).
    [Fact]
    public void Doctor_never_writes_a_terminal_control_sequence_a_version_answer_carries()
    {
        using var sandbox = new SandboxHost("retro17-doctor");
        var hostile = DockerFixture.Read("version.out").Replace("\"Server\":{\"Platform\":{\"Name\":\"Docker Desktop 4.81.0 (232925)\"},\"Version\":\"29.6.1\"", "\"Server\":{\"Platform\":{\"Name\":\"Docker Desktop 4.81.0 (232925)\"},\"Version\":\"29.6.1\\u001b]52;c;eA==\\u0007\"", StringComparison.Ordinal);
        hostile.Should().Contain("\\u001b]52", "the fixture must carry the sequence, or this test proves nothing");
        var runner = HealthFixture.Script(DockerFixture.Runner()).Script(DockerCommands.Version.Argv, 0, hostile);
        var host = new CliHost(sandbox.Paths, sandbox.Files, new FixedTimeProvider(DockerFixture.CapturedAt), runner);

        var (_, stdout, _) = CliRun.Over(host, "doctor");

        stdout.Should().Contain("docker 29.6.1").And.NotContain(Escape.ToString(), "no control character of a version answer reaches the terminal");
    }

    // F1 + F3: every --process value copied the whole list so far — quadratic in the count the cap still allows.
    [Fact]
    public void Parsing_ten_thousand_process_keys_allocates_linearly_not_quadratically()
    {
        string[] args = ["act", "A18", "--confirm", .. Enumerable.Range(1, 10_000).SelectMany(i => new[] { "--process", $"{i + 100}:{123456789 + i}" })];

        var before = GC.GetAllocatedBytesForCurrentThread();
        var request = CommandLine.Parse(args);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        request.Should().BeOfType<Request.Act>((request as Request.Failed)?.Message).Which.Processes.Should().HaveCount(10_000);
        allocated.Should().BeLessThan(64L * 1024 * 1024, "ten thousand keys are a few hundred kilobytes; a copy per key is hundreds of megabytes");
    }
}
