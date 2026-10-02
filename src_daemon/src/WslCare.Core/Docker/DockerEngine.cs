using System.Globalization;
using System.Text.Json;

namespace WslCare.Core.Docker;

/// <summary>The engine that answered <c>docker version</c>.</summary>
/// <param name="ServerVersion">The daemon's version, as Docker prints it (<c>29.6.1</c>).</param>
/// <param name="ServerMajor">Its major number; 0 when Docker printed something that does not start with one.</param>
/// <param name="ClientVersion">The CLI's version.</param>
/// <param name="Platform">The server platform's name (<c>Docker Desktop 4.81.0 (232925)</c>), or empty.</param>
public sealed record DockerEngine(string ServerVersion, int ServerMajor, string ClientVersion, string Platform)
{
    /// <summary>Plan §5 A4: Docker below 23 treats <c>volume prune</c> differently, and A4 refuses there.</summary>
    public const int MinimumMajorForA4 = 23;
}

/// <summary>Whether a Docker daemon answers, and which — the first question every Docker figure depends on.</summary>
public abstract record DockerReachability
{
    private DockerReachability()
    {
    }

    public sealed record Reachable(DockerEngine Engine) : DockerReachability;

    public sealed record Unreachable(DockerProblem Problem) : DockerReachability;

    /// <summary>
    /// The answer of <see cref="DockerCommands.Version"/>. The CLI prints its own half even when no daemon
    /// answers — <c>"Server": null</c> — so a null server is a stopped daemon whatever the exit code
    /// (measured 2026-10-02: Docker 29.6.1 exits 1 and prints the client JSON on stdout, the reason on stderr).
    /// </summary>
    public static DockerReachability From(DockerAnswer answer) => answer switch
    {
        DockerAnswer.Failed failed => new Unreachable(failed.Problem),
        DockerAnswer.Answered answered => FromJson(answered.Stdout),
        _ => throw new System.Diagnostics.UnreachableException("DockerAnswer is a closed set"),
    };

    private static DockerReachability FromJson(string stdout)
    {
        var parsed = DockerJson.Document(stdout, "docker version", ReadEngine);
        return parsed switch
        {
            Collectors.Reading<DockerEngine>.Available { Value.ServerVersion.Length: > 0 } a => new Reachable(a.Value),
            Collectors.Reading<DockerEngine>.Available => new Unreachable(new DockerProblem(DockerFailure.DaemonStopped, "docker version answered for the client only (\"Server\": null): no Docker daemon answers")),
            _ => new Unreachable(new DockerProblem(DockerFailure.Unparseable, parsed.ReasonOrEmpty)),
        };
    }

    private static DockerEngine ReadEngine(JsonElement root)
    {
        var server = root.TryGetProperty("Server", out var s) && s.ValueKind == JsonValueKind.Object ? s : default;
        var client = root.TryGetProperty("Client", out var c) ? c : default;
        var platform = server.ValueKind == JsonValueKind.Object && server.TryGetProperty("Platform", out var p) ? DockerText.Field(p, "Name") : string.Empty;
        var version = DockerText.Field(server, "Version");
        return new DockerEngine(version, Major(version), DockerText.Field(client, "Version"), platform);
    }

    private static int Major(string version) =>
        int.TryParse(version.Split('.')[0], NumberStyles.None, CultureInfo.InvariantCulture, out var major) ? major : 0;
}
