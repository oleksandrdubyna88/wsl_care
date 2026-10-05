using System.Text.RegularExpressions;

namespace WslCare.Core.Config;

/// <summary>
/// What a text key accepts (plan §15q R1.3, review B1) — a closed set of shapes, so a text value can never be free text. A new
/// kind of text is a new record here, reviewed; there is no "anything" rule.
/// </summary>
public abstract record TextRule
{
    private TextRule()
    {
    }

    /// <summary>The rule in the words of a refusal message.</summary>
    public abstract string Describe { get; }

    /// <summary>Why <paramref name="value"/> is refused; empty when it is accepted.</summary>
    public abstract string Problem(string value);

    /// <summary>Exactly one of <paramref name="Values"/>, compared ordinally. The order is the order "lower" means
    /// (<c>logging.minimumLevel</c>: Verbose before Debug before Information).</summary>
    public sealed record OneOf(IReadOnlyList<string> Values) : TextRule
    {
        public override string Describe => $"one of: {string.Join(", ", Values)}";

        public override string Problem(string value) => Values.Contains(value, StringComparer.Ordinal) ? string.Empty : $"not {Describe}";
    }

    /// <summary>Text matching <paramref name="Expression"/> as a whole (described as <paramref name="Description"/>).</summary>
    public sealed record Matching(string Expression, string Description) : TextRule
    {
        private static readonly TimeSpan MatchCeiling = TimeSpan.FromMilliseconds(250);

        public override string Describe => Description;

        /// <summary>E7.S0 review S5: .NET's <c>$</c> matches before a final newline, so "Ubuntu\n" passed <c>^…$</c> — the match
        /// must cover the WHOLE value (the expression stays JavaScript-compatible for the extension's schema).</summary>
        public override string Problem(string value) =>
            Regex.Match(value, Expression, RegexOptions.CultureInvariant, MatchCeiling) is { Success: true } m && m.Index == 0 && m.Length == value.Length
                ? string.Empty
                : $"not {Description}";
    }

    /// <summary>Empty, or an absolute path — <c>/…</c> or <c>X:\…</c> — of at most <see cref="MaxLength"/> characters, with no
    /// control character and no <c>..</c> segment. What a path key accepts BEFORE the reader of that key checks the
    /// filesystem (E9 adds the base folder's own rules).</summary>
    public sealed record AbsolutePathOrEmpty : TextRule
    {
        public const int MaxLength = 1024;

        public override string Describe => $"empty, or an absolute path (/… or X:\\…) of at most {MaxLength} characters without a .. segment";

        public override string Problem(string value) => value.Length == 0 || IsAbsolutePath(value) ? string.Empty : $"not {Describe}";

        private static bool IsAbsolutePath(string value) =>
            value.Length <= MaxLength && !value.Any(char.IsControl) && (value.StartsWith('/') || IsDrivePath(value)) && !HasParentSegment(value);

        private static bool IsDrivePath(string value) => value.Length >= 3 && char.IsAsciiLetter(value[0]) && value[1] == ':' && value[2] == '\\';

        private static bool HasParentSegment(string value) => value.Split('/', '\\').Contains("..", StringComparer.Ordinal);
    }
}

/// <summary>Which direction of change of a key is the SAFE one for a root run (plan §15q R1.2): the value a user layer may
/// move a root-effective key towards when its loosening is not trusted.</summary>
public enum SafeDirection
{
    /// <summary>Not root-effective: a display figure, a warning level, a key nothing reads.</summary>
    None,

    /// <summary>A larger number is safer (an older age, a longer keep, a larger size trigger).</summary>
    Higher,

    /// <summary>A smaller number — or an earlier value of a <see cref="TextRule.OneOf"/> — is safer.</summary>
    Lower,

    /// <summary><c>true</c> is safer (<c>dryRun</c>).</summary>
    On,

    /// <summary><c>false</c> is safer (an <c>auto.*</c> switch).</summary>
    Off,

    /// <summary>A subset of the value below is safer (<c>processes.families</c>).</summary>
    Subset,
}

/// <summary>What a key means to a ROOT run (plan §15q R1.2, R1.3, R1.6).</summary>
/// <param name="Safe">The safe direction; <see cref="SafeDirection.None"/> for a key that does not steer root.</param>
/// <param name="TightenOnlyForRoot">Even when the user layer is trusted, a root run takes this key's user value only in the safe
/// direction — root's own audit log is not the user's to steer (R1.6: <c>logging.*</c>).</param>
/// <param name="MachineOnly">Only the machine layer may set it; a user-layer value is ignored with a notice and
/// <c>config set</c> refuses it (<c>archive.baseFolder</c> until E9 validates it, review B1).</param>
/// <param name="DaemonUnused">No daemon code reads it (the extension's own <c>distro</c> / <c>refreshSeconds</c>); kept so a
/// layer that holds it stays valid (§15q Q6).</param>
/// <param name="ZeroIsUnbounded">For a <see cref="SafeDirection.Higher"/> number, 0 means "no limit" — the safest value
/// (<c>logging.retentionDays</c>: 0 disables the sweep).</param>
public sealed record KeyTrust(SafeDirection Safe, bool TightenOnlyForRoot = false, bool MachineOnly = false, bool DaemonUnused = false, bool ZeroIsUnbounded = false)
{
    public static readonly KeyTrust Display = new(SafeDirection.None);

    public static readonly KeyTrust Unused = new(SafeDirection.None, DaemonUnused: true);

    public static readonly KeyTrust Higher = new(SafeDirection.Higher);

    public static readonly KeyTrust Lower = new(SafeDirection.Lower);

    public static readonly KeyTrust Off = new(SafeDirection.Off);

    /// <summary>Whether a value of this key steers what a root run does.</summary>
    public bool RootEffective => Safe != SafeDirection.None || MachineOnly;
}

/// <summary>The safe-direction comparison of plan §15q R1.2 — pure, one place, so the loader and the contract the extension's
/// loosening modal keys on cannot disagree.</summary>
public static class KeySafety
{
    /// <summary>Whether <paramref name="candidate"/> is at least as safe as <paramref name="below"/> (the value the layers under
    /// it produced) for <paramref name="key"/>; always true for a key that is not root-effective.</summary>
    public static bool IsNoLooser(ConfigKey key, ConfigValue candidate, ConfigValue below) => key.Trust.Safe switch
    {
        SafeDirection.Higher or SafeDirection.Lower => Ordered(key, candidate, below),
        SafeDirection.On => candidate is ConfigValue.Bool { Value: true } || candidate == below,
        SafeDirection.Off => candidate is ConfigValue.Bool { Value: false } || candidate == below,
        SafeDirection.Subset => candidate is ConfigValue.TextList c && below is ConfigValue.TextList b && c.Values.All(v => b.Values.Contains(v, StringComparer.Ordinal)),
        _ => true,
    };

    private static bool Ordered(ConfigKey key, ConfigValue candidate, ConfigValue below)
    {
        var (c, b) = (Rank(key, candidate), Rank(key, below));
        return key.Trust.Safe == SafeDirection.Higher ? c >= b : c <= b;
    }

    /// <summary>A number's rank (0 → unbounded where the key says so), or a <see cref="TextRule.OneOf"/> value's index.</summary>
    private static long Rank(ConfigKey key, ConfigValue value) => value switch
    {
        ConfigValue.Int { Value: 0 } when key.Trust.ZeroIsUnbounded => long.MaxValue,
        ConfigValue.Int number => number.Value,
        ConfigValue.Text text when key is ConfigKey.TextKey { Rule: TextRule.OneOf one } => IndexOf(one, text.Value),
        _ => 0,
    };

    private static long IndexOf(TextRule.OneOf rule, string value) => rule.Values.ToList().IndexOf(value);
}
