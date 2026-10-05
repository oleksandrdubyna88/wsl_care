using System.Globalization;
using System.Security.Cryptography;
using System.Text;

using WslCare.Core.Docker;
using WslCare.FakeTool;
using WslCare.TestSupport;

namespace WslCare.Scenarios;

/// <summary>
/// A SYNTHETIC Docker for the fakes: <paramref name="count"/> anonymous volumes (Docker's <c>com.docker.volume.anonymous</c>
/// label AND a 64-hex name), none attached, no image, no container, no build cache — the shape of the 2026-10-02 morning
/// (387 volumes, 59.6 GB), invented and labelled so, never a capture. Names are SHA-256 digests of a fixed text, so two runs
/// script the same Docker. The version answer is the captured one (a Docker ≥ 23, which A4 needs).
/// </summary>
internal static class SyntheticDocker
{
    /// <summary>What each synthetic volume holds: 387 × 154 MB ≈ the 59.6 GB of 2026-10-02.</summary>
    public const string VolumeSize = "154MB";

    /// <summary>The <paramref name="count"/> volume names, in the order Docker lists them.</summary>
    public static IReadOnlyList<string> Names(int count) =>
        [.. Enumerable.Range(1, count).Select(i => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(string.Create(CultureInfo.InvariantCulture, $"wsl-care synthetic anonymous volume {i}")))))];

    /// <summary>Scripts the five read commands a Docker look runs (no container: no inspect) over <paramref name="count"/>
    /// unattached anonymous volumes; answers their names.</summary>
    public static IReadOnlyList<string> AnonymousVolumes(ScenarioHome home, int count)
    {
        var names = Names(count);
        var volumes = string.Join(',', names.Select(n => $$"""{"Availability":"N/A","Driver":"local","Group":"N/A","Labels":"com.docker.volume.anonymous=","Links":"0","Mountpoint":"/var/lib/docker/volumes/{{n}}/_data","Name":"{{n}}","Scope":"local","Size":"{{VolumeSize}}","Status":"N/A"}"""));
        var total = string.Create(CultureInfo.InvariantCulture, $"{count * 154 / 1000.0:0.##}GB");
        var df = string.Join('\n',
            """{"Active":"0","Reclaimable":"0B (0%)","Size":"0B","TotalCount":"0","Type":"Images"}""",
            """{"Active":"0","Reclaimable":"0B (0%)","Size":"0B","TotalCount":"0","Type":"Containers"}""",
            $$"""{"Active":"0","Reclaimable":"{{total}} (100%)","Size":"{{total}}","TotalCount":"{{count}}","Type":"Local Volumes"}""",
            """{"Active":"0","Reclaimable":"0B","Size":"0B","TotalCount":"0","Type":"Build Cache"}""") + "\n";
        home.Script(DockerCommands.Executable, DockerCommands.Version.Arguments, 0, $"docker/{DockerFixture.Name}/version.out");
        Answer(home, DockerCommands.SystemDf.Arguments, "synthetic-system-df.out", df);
        Answer(home, DockerCommands.SystemDfVerbose.Arguments, "synthetic-system-df-v.out", $$"""{"Images":[],"Containers":[],"Volumes":[{{volumes}}],"BuildCache":[]}""" + "\n");
        Answer(home, DockerCommands.DanglingVolumes.Arguments, "synthetic-dangling.out", string.Join('\n', names) + "\n");
        return names;
    }

    private static void Answer(ScenarioHome home, IReadOnlyList<string> argv, string file, string content) =>
        home.Answer(new FakeAnswer(DockerCommands.Executable, argv, 0, home.WriteFile(file, content), string.Empty));
}
