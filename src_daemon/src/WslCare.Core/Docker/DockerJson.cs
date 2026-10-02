using System.Text.Json;

using WslCare.Core.Collectors;

namespace WslCare.Core.Docker;

/// <summary>
/// Reading what <c>--format '{{json .}}'</c> prints: one JSON document, or one per line. Read with
/// <see cref="JsonDocument"/>, never a reflective serializer (Native AOT), and member by member through
/// <see cref="DockerText.Field"/>, so a member Docker leaves out reads as empty rather than as a null
/// that crashes three calls later (C# doctrine §4a).
/// </summary>
public static class DockerJson
{
    /// <summary>Every non-empty line mapped; the first line that is not JSON makes the whole answer
    /// unavailable, naming the line — a half-read listing is not a shorter truth.</summary>
    public static Reading<IReadOnlyList<T>> Lines<T>(string stdout, string what, Func<JsonElement, T> map)
    {
        var rows = new List<T>();
        var number = 0;
        foreach (var line in stdout.Split('\n'))
        {
            number++;
            var text = line.Trim();
            if (text.Length == 0)
            {
                continue;
            }

            if (Parse(text, map) is not { } row)
            {
                return Reading.Missing<IReadOnlyList<T>>($"line {number} of {what} is not a JSON object");
            }

            rows.Add(row.Value);
        }

        return Reading.Of<IReadOnlyList<T>>(rows);
    }

    /// <summary>The whole answer as one JSON object, mapped.</summary>
    public static Reading<T> Document<T>(string stdout, string what, Func<JsonElement, T> map) =>
        Parse(stdout.Trim(), map) is { } parsed ? Reading.Of(parsed.Value) : Reading.Missing<T>($"{what} is not a JSON object");

    /// <summary>The members of an array member, mapped; absent or null is an empty list.</summary>
    public static IReadOnlyList<T> Array<T>(JsonElement element, string name, Func<JsonElement, T> map) =>
        element.TryGetProperty(name, out var array) && array.ValueKind == JsonValueKind.Array ? [.. array.EnumerateArray().Select(map)] : [];

    /// <summary>A full 64-hex id, as Docker names containers and anonymous volumes.</summary>
    public static bool IsFullId(string text) => text.Length == 64 && text.All(char.IsAsciiHexDigitLower);

    /// <summary>
    /// The <c>Labels</c> member of a <c>ps</c> / <c>system df -v</c> row: <c>k=v,k=v</c>. A value may hold a
    /// comma (compose writes <c>config_files=a.yml,b.yml</c>), so a piece with no <c>=</c> belongs to the
    /// value before it.
    /// </summary>
    public static IReadOnlyDictionary<string, string> Labels(string text)
    {
        var labels = new Dictionary<string, string>(StringComparer.Ordinal);
        var last = string.Empty;
        foreach (var piece in text.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            var equals = piece.IndexOf('=', StringComparison.Ordinal);
            if (equals > 0)
            {
                last = piece[..equals];
                labels[last] = piece[(equals + 1)..];
            }
            else if (last.Length > 0)
            {
                labels[last] = $"{labels[last]},{piece}";
            }
        }

        return labels;
    }

    /// <summary>A whole number Docker printed as text (<c>"Containers":"0"</c>).</summary>
    public static Reading<int> Count(string text) =>
        int.TryParse(text, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var count)
            ? Reading.Of(count)
            : Reading.Missing<int>($"\"{text}\" is not a count");

    private static Box<T>? Parse<T>(string text, Func<JsonElement, T> map)
    {
        try
        {
            using var document = JsonDocument.Parse(text);
            return document.RootElement.ValueKind == JsonValueKind.Object ? new Box<T>(map(document.RootElement)) : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>A parsed row, so "no row" can be told from a row whose value is a default.</summary>
    private sealed record Box<T>(T Value);
}
