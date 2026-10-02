using System.Globalization;
using System.Text.Json;

using WslCare.Core.Collectors;

namespace WslCare.Core.Docker;

/// <summary>
/// The three ways the docker CLI writes a figure for a person rather than a program — a human size, a
/// timestamp, a JSON field that may be a string, a number, null or absent — read back into values.
/// </summary>
/// <remarks>
/// <para><b>Sizes.</b> <c>docker system df</c> and <c>docker image ls</c> print go-units <c>HumanSize</c>:
/// DECIMAL units (<c>kB</c>, <c>MB</c>, <c>GB</c>, … = 10³ⁿ) with four significant digits; <c>docker
/// stats</c> prints <c>BytesSize</c>: BINARY units (<c>KiB</c>, <c>MiB</c>, <c>GiB</c>, … = 2¹⁰ⁿ). Both are
/// read here by their suffix, so a figure is never off by 7 % for being read in the wrong base. The bytes
/// are as precise as Docker printed them: "9.806GB" is 9 806 000 000, give or take Docker's rounding.</para>
/// <para><b>Times.</b> <c>container inspect</c> writes RFC 3339 with nanoseconds (<c>…T10:15:10.738186962Z</c>);
/// <c>system df -v</c> writes Go's <c>time.String</c> in the CLI's LOCAL zone (<c>2026-10-02 14:23:39 +0200
/// CEST</c>, <c>… 12:22:00.77656516 +0000 UTC</c>). Both carry their offset, so both become a UTC instant
/// without guessing a zone; the zone abbreviation is ignored. Go's zero time (<c>0001-01-01…</c>) means
/// "never" and reads as unavailable, not as year 1.</para>
/// </remarks>
public static class DockerText
{
    private static readonly IReadOnlyDictionary<string, double> Units = new Dictionary<string, double>(StringComparer.Ordinal)
    {
        ["B"] = 1,
        ["kB"] = 1e3,
        ["KB"] = 1e3,
        ["MB"] = 1e6,
        ["GB"] = 1e9,
        ["TB"] = 1e12,
        ["PB"] = 1e15,
        ["KiB"] = 1024d,
        ["MiB"] = 1024d * 1024,
        ["GiB"] = 1024d * 1024 * 1024,
        ["TiB"] = 1024d * 1024 * 1024 * 1024,
        ["PiB"] = 1024d * 1024 * 1024 * 1024 * 1024,
    };

    /// <summary>A size as Docker prints it — <c>988MB</c>, <c>19.71kB</c>, <c>0B</c>, <c>53.84MiB</c>, and the
    /// summary form <c>9.806GB (48%)</c> — in bytes; <c>N/A</c>, empty or anything else is unavailable.</summary>
    public static Reading<long> Bytes(string text)
    {
        var size = text.Trim().Split(' ', 2)[0];
        var split = size.IndexOfAny(['B', 'k', 'K', 'M', 'G', 'T', 'P']);
        var number = 0d;
        var unit = 0d;
        var parsed = split > 0
            && double.TryParse(size[..split], NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out number)
            && Units.TryGetValue(size[split..], out unit);
        return parsed
            ? Reading.Of((long)Math.Round(number * unit))
            : Reading.Missing<long>($"\"{text}\" is not a size Docker prints");
    }

    /// <summary>A percentage as <c>docker stats</c> prints it (<c>0.77%</c>).</summary>
    public static Reading<double> Percent(string text)
    {
        var trimmed = text.Trim();
        return trimmed.EndsWith('%') && double.TryParse(trimmed[..^1], NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var value)
            ? Reading.Of(value)
            : Reading.Missing<double>($"\"{text}\" is not a percentage");
    }

    /// <summary>A timestamp in either of the CLI's two spellings, as a UTC instant.</summary>
    public static Reading<DateTimeOffset> Instant(string text)
    {
        var normalised = Normalise(text.Trim());
        var parsed = DateTimeOffset.TryParse(normalised, CultureInfo.InvariantCulture, DateTimeStyles.None, out var instant);
        return parsed && instant.Year > 1
            ? Reading.Of(instant.ToUniversalTime())
            : Reading.Missing<DateTimeOffset>(parsed ? "never (Docker's zero time)" : $"\"{text}\" is not a time Docker prints");
    }

    /// <summary>A JSON member as text: a string as it is, a number or a boolean as written, null or absent as empty.</summary>
    public static string Field(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) ? AsText(value) : string.Empty;

    private static string AsText(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => value.GetString() ?? string.Empty,
        JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False => value.GetRawText(),
        _ => string.Empty,
    };

    /// <summary>
    /// <c>2026-10-02 12:22:00.77656516 +0000 UTC</c> → <c>2026-10-02T12:22:00.7765651+00:00</c>; an RFC 3339
    /// text keeps its shape. The fraction is cut to the seven digits .NET reads (100 ns; Docker writes nine).
    /// </summary>
    private static string Normalise(string text)
    {
        var parts = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var joined = parts.Length >= 3 ? $"{parts[0]}T{parts[1]}{Offset(parts[2])}" : text;
        return CutFraction(joined);
    }

    /// <summary><c>+0200</c> → <c>+02:00</c>; anything else is passed on for the parse to refuse.</summary>
    private static string Offset(string offset) =>
        offset.Length == 5 && offset[0] is '+' or '-' ? $"{offset[..3]}:{offset[3..]}" : offset;

    private static string CutFraction(string text)
    {
        var dot = text.IndexOf('.', StringComparison.Ordinal);
        if (dot < 0)
        {
            return text;
        }

        var end = dot + 1;
        while (end < text.Length && char.IsAsciiDigit(text[end]))
        {
            end++;
        }

        return end - dot - 1 <= 7 ? text : text[..(dot + 8)] + text[end..];
    }
}
