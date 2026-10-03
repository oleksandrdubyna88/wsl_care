using System.Text.Json;

using WslCare.Core.Collectors;

namespace WslCare.Core.Docker;

/// <summary>One mount of a container: its type (<c>volume</c>, <c>bind</c>, <c>tmpfs</c>) and, for a volume, its name.</summary>
/// <remarks>Whether the volume is anonymous is NOT read from the name's shape here: the mount carries no labels, so the
/// decision is <see cref="AnonymousVolumes.IsAnonymous"/> over the inventory (independent review of E3, 2026-10-03).</remarks>
public sealed record ContainerMount(string Type, string Name)
{
    /// <summary>A volume mount with a name — anonymous or named is the inventory's answer.</summary>
    public bool Volume => Type == "volume" && Name.Length > 0;
}

/// <summary>
/// The fields of one container that <c>system df -v</c> does not carry, read through
/// <see cref="DockerCommands.InspectTemplate"/> — and nothing else of it: no environment, no command, no
/// bind-mount source ever reaches this process.
/// </summary>
/// <param name="FinishedAt">When it last stopped; unavailable when it never ran (Docker's zero time).</param>
/// <param name="ImageId">The image id it was created from — what keeps an image "used" (plan §12 A6).</param>
/// <param name="LogMaxSize">The json-file <c>max-size</c> option; empty when the log is unbounded (plan §4.5).</param>
/// <param name="LogPath">Where the daemon writes its log — inside the docker-desktop VM under Docker Desktop.</param>
public sealed record ContainerDetail(
    string Id,
    string Name,
    string State,
    Reading<DateTimeOffset> CreatedAt,
    Reading<DateTimeOffset> FinishedAt,
    string ImageId,
    string LogDriver,
    string LogMaxSize,
    string LogPath,
    bool Testcontainers,
    bool KeptByLabel,
    IReadOnlyList<ContainerMount> Mounts)
{
    /// <summary>Docker's states for a container that holds disk and runs nothing (plan §5 A5).</summary>
    private static readonly IReadOnlySet<string> StoppedStates = new HashSet<string>(StringComparer.Ordinal) { "exited", "created", "dead" };

    public bool Stopped => StoppedStates.Contains(State);

    /// <summary>Plan §4.3: <c>State.FinishedAt</c>, or <c>Created</c> for a container that never started.</summary>
    public Reading<DateTimeOffset> StoppedSince => FinishedAt.IsAvailable ? FinishedAt : CreatedAt;

    /// <summary>A json-file log with no <c>max-size</c>: it grows until the container is removed (plan §4.5).</summary>
    public bool UnboundedLog => LogDriver == "json-file" && LogMaxSize.Length == 0;

    /// <summary>Every line <see cref="DockerCommands.ContainerInspect"/> printed.</summary>
    public static Reading<IReadOnlyList<ContainerDetail>> Parse(string stdout) =>
        DockerJson.Lines(stdout, "docker container inspect", From);

    private static ContainerDetail From(JsonElement row) =>
        new(
            DockerText.Field(row, "id"),
            DockerText.Field(row, "name").TrimStart('/'),
            DockerText.Field(row, "state"),
            DockerText.Instant(DockerText.Field(row, "created")),
            DockerText.Instant(DockerText.Field(row, "finishedAt")),
            DockerText.Field(row, "image"),
            DockerText.Field(row, "logDriver"),
            DockerText.Field(row, "logMaxSize"),
            DockerText.Field(row, "logPath"),
            DockerText.Field(row, "testcontainers") == "true",
            DockerText.Field(row, "keep") == "true",
            DockerJson.Array(row, "mounts", m => new ContainerMount(DockerText.Field(m, "type"), DockerText.Field(m, "name"))));
}
