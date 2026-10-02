using System.Globalization;
using System.Text.RegularExpressions;

using WslCare.Core.Collectors;

namespace WslCare.Core.Systemd;

/// <summary>One unit as <c>systemctl show --timestamp=unix</c> reports it (plan §4.5).</summary>
/// <param name="LoadState"><c>loaded</c>, or <c>not-found</c> for a unit that does not exist.</param>
/// <param name="ActiveEnteredAt">When it last became active; unavailable when it never did (<c>ActiveEnterTimestamp=</c>).</param>
public sealed record SystemdUnit(string Id, string LoadState, string ActiveState, string SubState, string Result, Reading<int> Restarts, Reading<DateTimeOffset> ActiveEnteredAt)
{
    public bool Exists => LoadState != "not-found";

    /// <summary><c>enabled</c>, <c>disabled</c>, <c>masked</c>, <c>static</c> — empty for a unit that does not exist or a line
    /// captured before E2.S3 asked for it.</summary>
    public string UnitFileState { get; init; } = string.Empty;

    /// <summary>The answer of <see cref="SystemdCommands.ShowUnit"/>: <c>Key=Value</c> lines, in the unit's
    /// own order (not the order asked for). A unit with no <c>Id</c> line is not an answer.</summary>
    public static Reading<SystemdUnit> Parse(string stdout)
    {
        var values = SystemdText.KeyValues(stdout);
        string Value(string key) => values.GetValueOrDefault(key, string.Empty);
        return Value("Id").Length == 0
            ? Reading.Missing<SystemdUnit>("systemctl show printed no Id line")
            : Reading.Of(new SystemdUnit(
                Value("Id"),
                Value("LoadState"),
                Value("ActiveState"),
                Value("SubState"),
                Value("Result"),
                int.TryParse(Value("NRestarts"), NumberStyles.None, CultureInfo.InvariantCulture, out var restarts) ? Reading.Of(restarts) : Reading.Missing<int>("no NRestarts"),
                SystemdText.UnixTimestamp(Value("ActiveEnterTimestamp")))
            {
                UnitFileState = Value("UnitFileState"),
            });
    }
}

/// <summary><c>journalctl --disk-usage</c>: how much the journal holds (plan §4.5; A10 acts above 1 GB).</summary>
public static partial class JournalDiskUsage
{
    private static readonly IReadOnlyDictionary<char, long> Units = new Dictionary<char, long>
    {
        ['B'] = 1,
        ['K'] = 1L << 10,
        ['M'] = 1L << 20,
        ['G'] = 1L << 30,
        ['T'] = 1L << 40,
        ['P'] = 1L << 50,
    };

    /// <summary><c>Archived and active journals take up 405.4M in the file system.</c> — systemd's
    /// <c>FORMAT_BYTES</c>: BINARY units with one decimal, so 405.4M is 425 089 434 bytes, give or take the
    /// rounding systemd did.</summary>
    public static Reading<long> Parse(string stdout)
    {
        var match = TakeUp().Match(stdout);
        return match.Success
            ? Reading.Of((long)Math.Round(double.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture) * Units[match.Groups[2].Value[0]]))
            : Reading.Missing<long>($"journalctl --disk-usage did not say how much the journal takes up: \"{stdout.Trim()}\"");
    }

    [GeneratedRegex(@"take up (\d+(?:\.\d+)?)([BKMGTP])\b", RegexOptions.CultureInvariant)]
    private static partial Regex TakeUp();
}

/// <summary>The text shapes systemd tools print.</summary>
internal static class SystemdText
{
    public static IReadOnlyDictionary<string, string> KeyValues(string stdout) =>
        stdout.Split('\n')
            .Select(l => l.TrimEnd('\r'))
            .Where(l => l.IndexOf('=', StringComparison.Ordinal) > 0)
            .Select(l => (Key: l[..l.IndexOf('=', StringComparison.Ordinal)], Value: l[(l.IndexOf('=', StringComparison.Ordinal) + 1)..]))
            .GroupBy(p => p.Key, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Last().Value, StringComparer.Ordinal);

    /// <summary><c>@1790948334</c> (what <c>--timestamp=unix</c> prints) as a UTC instant; empty is "never".</summary>
    public static Reading<DateTimeOffset> UnixTimestamp(string text) =>
        text.StartsWith('@') && long.TryParse(text[1..], NumberStyles.None, CultureInfo.InvariantCulture, out var seconds) && seconds > 0
            ? Reading.Of(DateTimeOffset.FromUnixTimeSeconds(seconds))
            : Reading.Missing<DateTimeOffset>(text.Length == 0 ? "never" : $"\"{text}\" is not an @unix timestamp");
}
