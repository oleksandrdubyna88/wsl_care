using System.Globalization;
using System.Text.Json;

using WslCare.Core.Collectors;

namespace WslCare.Core.Docker;

/// <summary>One container event of <c>docker events --format {{json .}}</c>.</summary>
/// <param name="At">The event's instant, UTC (from <c>timeNano</c>, else <c>time</c>).</param>
/// <param name="Testcontainers">The container carries <c>org.testcontainers=true</c> (plan §4.3: counted apart).</param>
public sealed record DockerEvent(string Type, string Action, string Id, string Name, string Image, bool Testcontainers, DateTimeOffset At);

/// <summary>
/// The parser the events follower (E2.S3) and its bounded backfill (plan §15b #0) read with. Brought by
/// E2.S2 because the live contract (plan §15b #6) holds it to the real <c>docker events --since</c>.
/// </summary>
public static class DockerEvents
{
    private const long NanosPerTick = 100;

    /// <summary>Every line of <see cref="DockerCommands.Events"/>; a line without a readable time makes the
    /// answer unavailable rather than inventing one.</summary>
    public static Reading<IReadOnlyList<DockerEvent>> Parse(string stdout) =>
        DockerJson.Lines(stdout, "docker events", Event).Bind(rows => rows.FirstOrDefault(r => !r.IsAvailable) is { } bad
            ? Reading.Missing<IReadOnlyList<DockerEvent>>(bad.ReasonOrEmpty)
            : Reading.Of<IReadOnlyList<DockerEvent>>([.. rows.OfType<Reading<DockerEvent>.Available>().Select(a => a.Value)]));

    private static Reading<DockerEvent> Event(JsonElement row)
    {
        var actor = row.TryGetProperty("Actor", out var a) ? a : default;
        var attributes = actor.ValueKind == JsonValueKind.Object && actor.TryGetProperty("Attributes", out var at) ? at : default;
        return At(row).Map(at => new DockerEvent(
            DockerText.Field(row, "Type"),
            DockerText.Field(row, "Action"),
            DockerText.Field(actor, "ID"),
            DockerText.Field(attributes, "name"),
            DockerText.Field(attributes, "image"),
            DockerText.Field(attributes, DockerLabels.Testcontainers) == "true",
            at));
    }

    private static Reading<DateTimeOffset> At(JsonElement row)
    {
        var nanos = DockerText.Field(row, "timeNano");
        var seconds = DockerText.Field(row, "time");
        if (long.TryParse(nanos, NumberStyles.None, CultureInfo.InvariantCulture, out var ns))
        {
            return Reading.Of(DateTimeOffset.UnixEpoch.AddTicks(ns / NanosPerTick));
        }

        return long.TryParse(seconds, NumberStyles.None, CultureInfo.InvariantCulture, out var s)
            ? Reading.Of(DateTimeOffset.FromUnixTimeSeconds(s))
            : Reading.Missing<DateTimeOffset>("an event carries neither timeNano nor time");
    }
}
