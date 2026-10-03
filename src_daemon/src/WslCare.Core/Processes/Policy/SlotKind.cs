using System.Globalization;

namespace WslCare.Core.Processes.Policy;

/// <summary>
/// What one variable argument of a <see cref="CommandTemplate"/> may be — a closed set of shapes, each a validator that
/// accepts a value only when it is EXACTLY that shape (plan §15 #1: the argv is built in code, never from user text).
/// </summary>
/// <remarks>
/// <para>Every shape but <see cref="OneOf"/> and <see cref="Prefixed"/> refuses a value that starts with <c>-</c>, so a
/// slot can never become an option (<c>--all</c>, <c>-f</c>); and every shape refuses control characters, so a value can
/// never carry a newline, a NUL or an escape. A shape that is not one of these records does not exist: a new kind of
/// argument is a new record here, reviewed, never a free-text escape hatch.</para>
/// </remarks>
public abstract record SlotKind
{
    private SlotKind()
    {
    }

    /// <summary>Whether <paramref name="value"/> is exactly this shape.</summary>
    public abstract bool Accepts(string value);

    /// <summary>The shape as a person reads it in a refusal or the docs.</summary>
    public abstract string Describe { get; }

    /// <summary>A decimal integer in [<paramref name="Min"/>, <paramref name="Max"/>], then <paramref name="Suffix"/>
    /// (<c>30d</c> for <c>--vacuum-time=30d</c>). Digits only — no sign, no spaces, no leading zero but 0 itself.</summary>
    public sealed record Number(long Min, long Max, string Suffix = "") : SlotKind
    {
        public override bool Accepts(string value)
        {
            if (!value.EndsWith(Suffix, StringComparison.Ordinal))
            {
                return false;
            }

            var digits = value[..^Suffix.Length];
            return IsCanonicalDigits(digits) && long.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n >= Min && n <= Max;
        }

        public override string Describe => $"<{Min}..{Max}>{Suffix}";

        private static bool IsCanonicalDigits(string digits) =>
            digits.Length is > 0 and <= 19 && digits.All(char.IsAsciiDigit) && (digits.Length == 1 || digits[0] != '0');
    }

    /// <summary><c>@</c> and unix seconds (<c>journalctl --since @1790948334</c>).</summary>
    public sealed record UnixSeconds : SlotKind
    {
        private static readonly Number Seconds = new(0, 99_999_999_999);

        public override bool Accepts(string value) => value.StartsWith('@') && Seconds.Accepts(value[1..]);

        public override string Describe => "@<unix-seconds>";
    }

    /// <summary>A UTC instant to the second, <c>yyyy-MM-ddTHH:mm:ssZ</c> (<c>docker events --since</c>).</summary>
    public sealed record Rfc3339Utc : SlotKind
    {
        public override bool Accepts(string value) =>
            value.Length == 20 && DateTime.TryParseExact(value, "yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out _);

        public override string Describe => "<yyyy-MM-ddTHH:mm:ssZ>";
    }

    /// <summary>Exactly <paramref name="Length"/> lowercase hex digits — a full Docker object id, an anonymous volume's name.</summary>
    public sealed record Hex(int Length) : SlotKind
    {
        public override bool Accepts(string value) => value.Length == Length && value.All(c => char.IsAsciiDigit(c) || c is >= 'a' and <= 'f');

        public override string Describe => $"<hex{Length}>";
    }

    /// <summary>A systemd unit name: letters, digits and <c>@ _ . : -</c>, not starting with <c>-</c>, ending in a unit type —
    /// or, with <paramref name="TypeRequired"/> false, a bare service name (<c>journalctl --unit=systemd-resolved</c>).</summary>
    public sealed record UnitName(bool TypeRequired = true) : SlotKind
    {
        private static readonly string[] Types = [".service", ".timer", ".socket", ".target", ".mount", ".path"];

        public override bool Accepts(string value) =>
            value.Length is > 0 and <= 128
            && char.IsAsciiLetterOrDigit(value[0])
            && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '@' or '_' or '.' or ':' or '-')
            && (!TypeRequired || Types.Any(t => value.EndsWith(t, StringComparison.Ordinal) && value.Length > t.Length));

        public override string Describe => TypeRequired ? "<unit>" : "<unit or service name>";
    }

    /// <summary>A POSIX account name (<c>[a-z_][a-z0-9_-]{0,31}</c>) — the target user of <c>runuser -u</c>.</summary>
    public sealed record UserName : SlotKind
    {
        public override bool Accepts(string value) =>
            value.Length is > 0 and <= 32
            && (char.IsAsciiLetterLower(value[0]) || value[0] == '_')
            && value.All(c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c is '_' or '-');

        public override string Describe => "<user>";
    }

    /// <summary>A snap's name as snapd allows it: lowercase letters, digits and inner hyphens, at most 40 characters, at
    /// least one letter, never starting or ending with <c>-</c> nor holding <c>--</c> (A9's <c>snap remove &lt;name&gt;</c>, E3.S2).</summary>
    public sealed record SnapName : SlotKind
    {
        public override bool Accepts(string value) =>
            value.Length is > 0 and <= 40
            && value.All(c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c == '-')
            && value.Any(char.IsAsciiLetterLower)
            && value[0] != '-' && value[^1] != '-'
            && !value.Contains("--", StringComparison.Ordinal);

        public override string Describe => "<snap>";
    }

    /// <summary>Plain text of at most <paramref name="MaxLength"/> characters — ASCII letters, digits, space and
    /// <c>| . _ : -</c> only, not starting with <c>-</c>: a journal search pattern after <c>--grep=</c>. No <c>/</c>,
    /// <c>~</c>, <c>=</c>, <c>$</c>, quote or separator, so it can be neither a path, nor a setting, nor a second
    /// command. Never used for a name or anything a write acts on.</summary>
    public sealed record Text(int MaxLength) : SlotKind
    {
        public override bool Accepts(string value) =>
            value.Length > 0 && value.Length <= MaxLength && value[0] != '-' && value.All(c => char.IsAsciiLetterOrDigit(c) || c is ' ' or '|' or '.' or '_' or ':' or '-');

        public override string Describe => $"<plain text, at most {MaxLength}>";
    }

    /// <summary>One of a fixed set of values, compared exactly.</summary>
    public sealed record OneOf(IReadOnlyList<string> Values) : SlotKind
    {
        public override bool Accepts(string value) => Values.Contains(value, StringComparer.Ordinal);

        public override string Describe => "{" + string.Join('|', Values) + "}";
    }

    /// <summary>A fixed prefix, then a value of <paramref name="Inner"/> (<c>--unit=&lt;unit&gt;</c>).</summary>
    public sealed record Prefixed(string Prefix, SlotKind Inner) : SlotKind
    {
        public override bool Accepts(string value) => value.StartsWith(Prefix, StringComparison.Ordinal) && Inner.Accepts(value[Prefix.Length..]);

        public override string Describe => Prefix + Inner.Describe;
    }

    /// <summary>A value any of <paramref name="Kinds"/> accepts.</summary>
    public sealed record AnyOf(IReadOnlyList<SlotKind> Kinds) : SlotKind
    {
        public override bool Accepts(string value) => Kinds.Any(k => k.Accepts(value));

        public override string Describe => string.Join(" | ", Kinds.Select(k => k.Describe));
    }
}
