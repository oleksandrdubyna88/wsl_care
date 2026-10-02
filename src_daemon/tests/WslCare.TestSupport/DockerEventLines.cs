using System.Globalization;

using WslCare.Core.Docker;

namespace WslCare.TestSupport;

/// <summary>
/// SYNTHETIC <c>docker events --format {{json .}}</c> lines in the shape Docker 29.6.1 printed on 2026-10-02 (the
/// captured <c>fixtures/docker/ubuntu-2026-10-02/events-exec-die.out</c>): <c>Type</c>, <c>Action</c>, <c>Actor.ID</c>,
/// <c>Actor.Attributes.name</c> / <c>image</c> / the Testcontainers label, <c>time</c>, <c>timeNano</c>. Docker's buffer held
/// no start that day, so a start line is this shape with <c>"Action":"start"</c> — labelled synthetic wherever used.
/// </summary>
public static class DockerEventLines
{
    public static string Start(string id, string name, string image, DateTimeOffset at, bool testcontainers = false) =>
        Line("start", id, name, image, at, testcontainers);

    /// <summary>A healthcheck's <c>exec_die</c> — what filled Docker's buffer on 2026-10-02.</summary>
    public static string Exec(string id, DateTimeOffset at) => Line("exec_die", id, "container-26", "redis:7-alpine", at, false);

    public static string Text(params string[] lines) => string.Join('\n', lines) + "\n";

    /// <summary>The event as the product's own parser reads the line.</summary>
    public static DockerEvent Parsed(string line) =>
        DockerEvents.Parse(line) is Core.Collectors.Reading<IReadOnlyList<DockerEvent>>.Available { Value: [var e] } ? e : throw new InvalidOperationException($"the product does not read {line}");

    private static string Line(string action, string id, string name, string image, DateTimeOffset at, bool testcontainers)
    {
        var nanos = (at - DateTimeOffset.UnixEpoch).Ticks * 100;
        var label = testcontainers ? ",\"org.testcontainers\":\"true\"" : string.Empty;
        return string.Create(
            CultureInfo.InvariantCulture,
            $"{{\"Type\":\"container\",\"Action\":\"{action}\",\"Actor\":{{\"ID\":\"{id}\",\"Attributes\":{{\"name\":\"{name}\",\"image\":\"{image}\"{label}}}}},\"scope\":\"local\",\"time\":{at.ToUnixTimeSeconds()},\"timeNano\":{nanos}}}");
    }
}
