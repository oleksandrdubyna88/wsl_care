using System.Globalization;

using WslCare.Core.Collectors;
using WslCare.Core.Docker;
using WslCare.Core.Processes;
using WslCare.Core.Systemd;

namespace WslCare.TestSupport;

/// <summary>
/// The real <c>docker</c> / <c>systemctl</c> / <c>journalctl</c> answers captured from WSL <c>Ubuntu</c> on
/// 2026-10-02 by the live contract (<c>src_daemon/tests/fixtures/docker/ubuntu-2026-10-02</c>; its
/// <c>SOURCE.txt</c> says how and what was redacted), copied beside every test project that links them.
/// </summary>
/// <remarks>Each answer is keyed by the product's OWN command: <see cref="Answers"/> pairs every
/// <see cref="DockerCommands"/> argv with the file it was captured into — and the inspect argv is built from the
/// container ids the product's parser reads out of the captured <c>system df -v</c>, in its order, never typed
/// here. A recording runner or a scenario fake scripted from this list replays exactly what Docker said.</remarks>
public static class DockerFixture
{
    public const string Name = "ubuntu-2026-10-02";

    /// <summary>The fixture directory beside the test assembly; asserts it is there.</summary>
    public static string Root
    {
        get
        {
            var path = Path.Combine(AppContext.BaseDirectory, "fixtures", "docker", Name);
            return Directory.Exists(path) ? path : throw new DirectoryNotFoundException($"the docker fixture should have been copied to {path}; does the project link ../fixtures?");
        }
    }

    /// <summary>When the answers were captured — the "now" their ages are relative to.</summary>
    public static DateTimeOffset CapturedAt =>
        DateTimeOffset.Parse(File.ReadAllText(PathOf("captured-at.txt")).Trim(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal);

    public static string PathOf(string file) => Path.Combine(Root, file);

    public static string Read(string file) => File.ReadAllText(PathOf(file));

    /// <summary>The captured inventory, through the product's parser.</summary>
    public static DockerInventory Inventory =>
        DockerInventory.Parse(Read("system-df-v.out")) is Reading<DockerInventory>.Available { Value: var inventory }
            ? inventory
            : throw new InvalidOperationException("the captured system df -v does not parse");

    /// <summary>The inspect command the product builds for the captured containers.</summary>
    public static ToolCommand Inspect => DockerCommands.ContainerInspect([.. Inventory.Containers.Select(c => c.Id)]);

    /// <summary>Every command <c>preview</c> runs, with the file its captured answer is in.</summary>
    public static IReadOnlyList<(ToolCommand Command, string File)> Answers =>
    [
        (DockerCommands.Version, "version.out"),
        (DockerCommands.SystemDf, "system-df.out"),
        (DockerCommands.SystemDfVerbose, "system-df-v.out"),
        (DockerCommands.DanglingVolumes, "volume-ls-dangling.out"),
        (Inspect, "container-inspect.out"),
    ];

    /// <summary>The runner answering every <see cref="Answers"/> command with its captured stdout (exit 0).</summary>
    public static RecordingCommandRunner Runner()
    {
        var runner = new RecordingCommandRunner { Default = new CommandOutcome.FailedToStart("not scripted by the docker fixture") };
        foreach (var (command, file) in Answers)
        {
            runner.Script(command.Argv, 0, Read(file));
        }

        return runner.Script(DockerCommands.Stats.Argv, 0, Read("stats.out"))
            .Script(DockerCommands.ContainerList.Argv, 0, Read("ps-a.out"))
            .Script(SystemdCommands.JournalDiskUsage.Argv, 0, Read("journalctl-disk-usage.out"));
    }
}
