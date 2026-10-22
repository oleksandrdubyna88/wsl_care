using System.Text.RegularExpressions;

using WslCare.Core.Actions;
using WslCare.Core.Config;
using WslCare.Core.Docker;
using WslCare.Core.Processes;
using WslCare.Core.Processes.Policy;
using WslCare.Core.Records;
using WslCare.TestSupport;

namespace WslCare.Core.Tests.Actions;

/// <summary>
/// The CAPTURED 2026-10-02 Docker (<see cref="DockerFixture"/>) as a recording runner under the PRODUCT policy — with the
/// edits a test needs made to the captured TEXT (a server version, a keep label, a Testcontainers label), so the action
/// parses them through the product's own parsers. Nothing real runs: a "removal" is an answer the test scripts.
/// </summary>
internal sealed class DockerWorld
{
    public const string AllAges = """{ "volumes": { "anonymousOlderThanDays": 0 }, "containers": { "stoppedOlderThanDays": 0, "testcontainersOlderThanHours": 0 }, "images": { "unusedOlderThanDays": 0 }, "buildCache": { "olderThanDays": 0 } }""";

    public static readonly DateTimeOffset Now = DockerFixture.CapturedAt;

    /// <summary>The project name <c>compose:</c> gives a container — the name the redaction maps a real project to.</summary>
    public const string ComposeProject = "project-1";

    /// <param name="unlabelledVolume">A volume whose <c>com.docker.volume.anonymous</c> label is taken away in the captured
    /// <c>system df -v</c> — what <c>docker volume create</c> without a name leaves: a 64-hex name Docker treats as NAMED.</param>
    /// <param name="unlistedDangling">A 64-hex name added to the dangling list but absent from <c>system df -v</c> — a volume
    /// whose labels are unknown.</param>
    /// <param name="shareVolume">A container id and a volume name: the volume is ALSO mounted by that container, as
    /// <c>--volumes-from</c> leaves it — one anonymous volume held by two containers.</param>
    /// <param name="compose">A container id that is made a member of compose project <see cref="ComposeProject"/>: its captured line gets the
    /// <c>"compose"</c> key the product's template now reads. The captured fixture already holds ONE such container (a real compose
    /// container, recaptured in E21.S1), so this must name another.</param>
    public DockerWorld(string? serverVersion = null, string keepVolume = "", string keepContainer = "", string testcontainer = "", string unlabelledVolume = "", string unlistedDangling = "", (string Container, string Volume) shareVolume = default, string compose = "")
    {
        var version = DockerFixture.Read("version.out");
        if (serverVersion is not null)
        {
            version = Regex.Replace(version, "(\"Server\":\\{\"Platform\":\\{[^}]*\\},\"Version\":\")[^\"]*", $"${{1}}{serverVersion}");
        }

        var dfv = DockerFixture.Read("system-df-v.out");
        if (keepVolume.Length > 0)
        {
            dfv = dfv.Replace($"\"Labels\":\"com.docker.volume.anonymous=\",\"Links\":\"0\",\"Mountpoint\":\"/var/lib/docker/volumes/{keepVolume}/", $"\"Labels\":\"com.docker.volume.anonymous=,wsl-care.keep=true\",\"Links\":\"0\",\"Mountpoint\":\"/var/lib/docker/volumes/{keepVolume}/", StringComparison.Ordinal);
        }

        if (unlabelledVolume.Length > 0)
        {
            var labelled = Regex.Escape("\"Labels\":\"com.docker.volume.anonymous=\",");
            dfv = Regex.Replace(dfv, labelled + "(\"Links\":\"[0-9]+\",\"Mountpoint\":\"/var/lib/docker/volumes/" + unlabelledVolume + "/)", "\"Labels\":\"\",$1");
        }

        var dangling = DockerFixture.Read("volume-ls-dangling.out") + (unlistedDangling.Length > 0 ? unlistedDangling + "\n" : string.Empty);
        var inspect = string.Join('\n', DockerFixture.Read("container-inspect.out").Split('\n').Select(line =>
            Edit(
                Edit(
                    Edit(
                        Edit(line, keepContainer, "\"keep\":null", "\"keep\":\"true\""),
                        testcontainer, "\"testcontainers\":null", "\"testcontainers\":\"true\""),
                    compose, "\"mounts\":[", $"\"compose\":\"{ComposeProject}\",\"mounts\":["),
                shareVolume.Container, "\"mounts\":[", $"\"mounts\":[{{\"type\":\"volume\",\"name\":\"{shareVolume.Volume}\"}},")));
        Runner = new RecordingCommandRunner { Policy = CommandPolicy.Product, Default = new CommandOutcome.FailedToStart("not scripted by the docker world") }
            .Script(DockerCommands.Version.Argv, 0, version)
            .Script(DockerCommands.SystemDf.Argv, 0, DockerFixture.Read("system-df.out"))
            .Script(DockerCommands.SystemDfVerbose.Argv, 0, dfv)
            .Script(DockerCommands.DanglingVolumes.Argv, 0, dangling)
            .Script(DockerFixture.Inspect.Argv, 0, inspect)
            .Script(DockerCommands.VolumeList.Argv, 0, string.Join('\n', DockerFixture.Inventory.Volumes.Select(v => v.Name)));
    }

    public RecordingCommandRunner Runner { get; }

    /// <summary>The edit one test asks of ONE captured line: applied only to the line of container <paramref name="id"/>, and only when one was named.</summary>
    private static string Edit(string line, string? id, string from, string to) =>
        id is { Length: > 0 } && line.Contains($"\"id\":\"{id}\"", StringComparison.Ordinal) ? line.Replace(from, to, StringComparison.Ordinal) : line;

    /// <summary>The three anonymous volumes no container uses in the capture, as Docker listed them.</summary>
    public static IReadOnlyList<string> DanglingAnonymous =>
        [.. DockerFixture.Read("volume-ls-dangling.out").Split('\n').Select(l => l.Trim()).Where(DockerJson.IsFullId).Order(StringComparer.Ordinal)];

    /// <summary>A volume's size as the captured <c>system df -v</c> reports it.</summary>
    public static long VolumeBytes(string name) => DockerFixture.Inventory.Volumes.Single(v => v.Name == name).SizeBytes.ValueOr(-1);

    public static ActionContext Context(LinuxSandbox sandbox, string userConfig = AllAges, RunTrigger trigger = RunTrigger.Cli)
    {
        sandbox.Write("/home/me/.config/wsl-care/config.json", userConfig);
        var loaded = ConfigLoader.Load(sandbox.Paths, sandbox.Files);
        if (loaded.IsObserveOnly)
        {
            throw new InvalidOperationException("the test configuration must pass the real validator");
        }

        return new ActionContext(sandbox.Paths, sandbox.Files, new FixedTimeProvider(Now), loaded.Config, trigger, new TargetUserResult.None("machine-scoped"));
    }

    public ActionCommands Commands(ICleanupAction action, ActionContext context) => new(action, Runner, context.TargetUser, []);

    /// <summary>Every argv of the runner that starts with <c>docker</c> and these words.</summary>
    public IReadOnlyList<CommandRequest> Calls(params string[] words) =>
        [.. Runner.Requests.Where(r => r.Argv.Count > words.Length && r.Argv[0] == "docker" && words.Select((w, i) => r.Argv[i + 1] == w).All(ok => ok))];
}
