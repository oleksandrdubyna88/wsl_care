using System.Text.Json;

using FluentAssertions;

using WslCare.Cli;
using WslCare.Core.Docker;
using WslCare.Core.Json;
using WslCare.Core.Preview;
using WslCare.FakeTool;
using WslCare.TestSupport;

namespace WslCare.Scenarios;

/// <summary>
/// Which <c>docker</c> the product starts is the PRODUCT's decision, made on <c>PATH</c> alone. Found by CI run
/// 37045304356 (win-x64): nine scenarios reached GitHub's real <c>C:\Windows\System32\docker.exe</c> instead of the
/// fake on their <c>PATH</c>, because a bare name handed to <c>CreateProcess</c> is looked up in the application's
/// directory, the CURRENT directory, System32 and the Windows directory before <c>PATH</c> (and .NET's Unix lookup
/// also tries the application's and the current directory first). The decoy here stands in for every one of those
/// places: it sits in the CLI's current directory, which the scenario can plant into without touching the machine.
/// </summary>
public sealed class ToolResolutionFlows
{
    [Fact]
    public async Task A_docker_in_the_current_directory_is_never_started_the_one_on_the_path_is()
    {
        using var home = new ScenarioHome("resolve-path-wins");
        var decoyFolder = home.PlantDecoyInWorkingDirectory(DockerCommands.Executable);

        var result = await home.RunAsync("preview", "--all", "--json");

        result.Exit.Should().Be((int)ExitCode.Ok, result.Stderr);
        var docker = home.Calls.Where(c => c.Tool == DockerCommands.Executable).ToList();
        docker.Should().NotBeEmpty("preview probes docker first");
        docker.Should().OnlyContain(c => SameFolder(c.Location, home.FakeBin), $"only the fake on PATH ({home.FakeBin}) may answer, never the decoy in the current directory ({decoyFolder})");
    }

    [Fact]
    public async Task Without_docker_on_the_path_a_docker_in_the_current_directory_still_leaves_docker_not_installed()
    {
        using var home = new ScenarioHome("resolve-decoy-only", [.. FakeToolProtocol.Tools.Where(t => t != DockerCommands.Executable)]);
        home.PlantDecoyInWorkingDirectory(DockerCommands.Executable);

        var result = await home.RunAsync("preview", "--all", "--json");

        result.Exit.Should().Be((int)ExitCode.Ok, result.Stderr);
        var report = JsonSerializer.Deserialize(result.Stdout, WslCareJsonContext.Default.PreviewReport) ?? throw new InvalidOperationException("preview printed null");
        report.Docker.Should().BeEquivalentTo(new { Available = false, Kind = "notInstalled" });
        report.Docker.Reason.Should().Contain("PATH", "the reason says where the product looked");
        home.Calls.Should().BeEmpty("nothing outside PATH may be started");
    }

    private static bool SameFolder(string a, string b) =>
        string.Equals(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(a)),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(b)),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
}
