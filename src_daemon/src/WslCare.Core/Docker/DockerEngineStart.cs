using WslCare.Core.Collectors;

namespace WslCare.Core.Docker;

/// <summary>
/// The engine INSTANCE whose in-memory event buffer a backfill reads: an identity that changes with every engine start,
/// and the instant it started. Read from the default <c>bridge</c> network, which the engine deletes and re-creates at
/// each start (measured 2026-10-02, Docker Desktop 4.81.0 / Engine 29.6.1: <c>bridge</c> created 13:39:47.63Z, the
/// first restart-policy container started 13:39:48.27Z, while <c>host</c> and <c>none</c> carry their 2026-07-16 creation
/// — they persist). <c>docker info</c> has no start time, and its <c>ID</c> persists across restarts.
/// </summary>
/// <param name="Id">The <c>bridge</c> network's id — new at every engine start.</param>
/// <param name="StartedAt">Its creation instant, UTC: when this engine started (within a second).</param>
public sealed record EngineMark(string Id, DateTimeOffset StartedAt);

/// <summary>Reads <see cref="DockerCommands.EngineStart"/>.</summary>
/// <remarks>Residual: an engine running with <c>live-restore</c> may keep the bridge across a restart, and an engine started
/// with <c>--bridge=none</c> has no <c>bridge</c> network — the first reads as "not restarted" (its buffer still empty after a
/// restart, so a gap is missed only for an engine that was idle right up to the restart), the second as unavailable,
/// which falls back to the rule that trusts nothing but the oldest buffered event.</remarks>
public static class DockerEngineStart
{
    public static Reading<EngineMark> From(DockerAnswer answer) => answer switch
    {
        DockerAnswer.Answered answered => Parse(answered.Stdout),
        DockerAnswer.Failed failed => Reading.Missing<EngineMark>($"the engine's start could not be read: {failed.Problem.Kind}: {failed.Problem.Reason}"),
        _ => throw new System.Diagnostics.UnreachableException("DockerAnswer is a closed set"),
    };

    public static Reading<EngineMark> Parse(string stdout) =>
        DockerJson.Document(stdout, "docker network inspect bridge", row => (Id: DockerText.Field(row, "id"), Created: DockerText.Field(row, "created")))
            .Bind(row => row.Id.Length == 0
                ? Reading.Missing<EngineMark>("docker network inspect bridge named no network id")
                : DockerText.Instant(row.Created).Map(at => new EngineMark(row.Id, at)));
}
