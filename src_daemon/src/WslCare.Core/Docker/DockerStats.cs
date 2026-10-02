using System.Text.Json;

using WslCare.Core.Collectors;
using WslCare.Core.Records;

namespace WslCare.Core.Docker;

/// <summary>
/// <c>docker stats --no-stream</c> — the per-container memory and CPU a FULL run samples (plan §4.2) and
/// stores in its run record (<see cref="SlowParts.ContainerStats"/>); <c>status</c> reads it back with its age
/// and never starts it (plan §15b #5). <c>collect</c> (E2.S3) is the caller.
/// </summary>
public static class DockerStats
{
    /// <summary>Every line of <see cref="DockerCommands.Stats"/>. Memory is the used half of <c>MemUsage</c>
    /// (<c>53.84MiB / 44.89GiB</c>, binary units); the id is the full one when Docker printed it.</summary>
    public static Reading<IReadOnlyList<ContainerStat>> Parse(string stdout)
    {
        var rows = DockerJson.Lines(stdout, "docker stats", Row);
        return rows.Bind(list => list.FirstOrDefault(r => !r.IsAvailable) is { } bad
            ? Reading.Missing<IReadOnlyList<ContainerStat>>(bad.ReasonOrEmpty)
            : Reading.Of<IReadOnlyList<ContainerStat>>([.. list.OfType<Reading<ContainerStat>.Available>().Select(a => a.Value)]));
    }

    /// <summary>One sample, or the reason there is none — never an exception and never an empty list
    /// standing in for "Docker did not answer" (plan §15b #7).</summary>
    public static async Task<ContainerStatsSample> SampleAsync(DockerCli docker, TimeProvider clock, CancellationToken cancellationToken)
    {
        var answer = await docker.RunAsync(DockerCommands.Stats, cancellationToken).ConfigureAwait(false);
        var sampledAt = clock.GetUtcNow();
        var parsed = answer switch
        {
            DockerAnswer.Answered a => Parse(a.Stdout),
            DockerAnswer.Failed f => Reading.Missing<IReadOnlyList<ContainerStat>>(f.Problem.Reason),
            _ => throw new System.Diagnostics.UnreachableException("DockerAnswer is a closed set"),
        };
        return new ContainerStatsSample(sampledAt, parsed.ValueOr([]), parsed.ReasonOrEmpty);
    }

    private static Reading<ContainerStat> Row(JsonElement row)
    {
        var full = DockerText.Field(row, "Container");
        var id = DockerJson.IsFullId(full) ? full : DockerText.Field(row, "ID");
        var name = DockerText.Field(row, "Name");
        var memory = DockerText.Bytes(DockerText.Field(row, "MemUsage").Split('/')[0]);
        var cpu = DockerText.Percent(DockerText.Field(row, "CPUPerc"));
        return Reading.Combine(memory, cpu, (m, c) => new ContainerStat(id, name, m, c));
    }
}
