using System.Text.RegularExpressions;

namespace WslCare.Core.Processes.Redaction;

/// <summary>
/// E15.S7 (todo/PLAN_disk_health.md, "E15.S7 design" §2.2–§2.3): what makes a text SECRET-LOOKING — the closed lists the rule judges
/// by. The secret words, the creds-shaped tokens of the providers the owner's tools talk to, the blob that holds a secret word next to
/// a separator, the basic-auth userinfo shape, and the program-specific flags that carry a password.
/// </summary>
/// <remarks>
/// The lists are CLOSED in code and have no configuration key: a user-widenable list needs a safe direction "a superset is safer" that
/// <c>SafeDirection</c> does not have, and a list that can be shortened weakens the redaction (design §4). Every pattern is anchored
/// to a token boundary and has no nested quantifier; every match is bounded by its input (<c>processes.maxJudgedArgChars</c>) and by
/// <see cref="PatternCeilingMilliseconds"/>; a match that runs out of time counts as a HIT, because the rule denies by default.
/// </remarks>
internal static partial class SecretShapes
{
    /// <summary>The match ceiling compiled into every pattern: the range MAXIMUM of <c>patterns.matchTimeoutMilliseconds</c> — the
    /// bootstrap ceiling <c>TextRule.Matching</c> uses, and the most a generated pattern can be given (an attribute takes a constant,
    /// never a configured value). A test holds the two equal.</summary>
    internal const int PatternCeilingMilliseconds = 1000;

    private const RegexOptions Options = RegexOptions.CultureInvariant;

    private const RegexOptions Folded = RegexOptions.CultureInvariant | RegexOptions.IgnoreCase;

    /// <summary>Design §2.2, the words a name or a blob is judged by (substring match, case-insensitive, as the old rule's eight were),
    /// widened with <c>passphrase</c>, <c>pwd</c>, <c>creds</c>, <c>auth</c> (covers <c>authorization</c> and <c>oauth</c>),
    /// <c>bearer</c>, <c>cookie</c>, <c>csrf</c>, <c>signature</c>, and the private / access / client key spellings. <c>key</c>
    /// is not here: it is a BOUNDED word (<see cref="BoundedKey"/>). <c>session</c>, <c>id</c> and <c>resume</c> are not secret
    /// words — session ids are what the archive's in-use check and E15.S6 read.</summary>
    private const string WordList =
        "token|secret|password|passwd|apikey|api-key|api_key|credential|passphrase|pwd|creds|auth|bearer|cookie|csrf|signature"
        + "|private-key|private_key|access-key|access_key|client-secret|client_secret";

    private static readonly string[] Words = [.. WordList.Split('|')];

    /// <summary>Whether a flag or an assignment's NAME holds a secret word — <c>--password</c>, <c>GITHUB_TOKEN</c>,
    /// <c>--max-thinking-tokens</c> (the one false positive the golden already carries, kept: a number lost is cheaper than a token
    /// shown), and <c>key</c> only as a whole word: <c>--key</c>, <c>API_KEY</c>, <c>-key</c>, not <c>--keyboard</c> or <c>--keep</c>.</summary>
    public static bool NamesASecret(string name) =>
        Words.Any(word => name.Contains(word, StringComparison.OrdinalIgnoreCase)) || Hits(BoundedKey(), name);

    /// <summary>The bounded word <c>key</c> (design §2.2): at the start or after <c>-</c> <c>_</c> <c>.</c>, and at the end or before
    /// <c>-</c> <c>_</c> <c>.</c> <c>=</c>.</summary>
    [GeneratedRegex(@"(?:^|[-_.])key(?:$|[-_.=])", Folded, PatternCeilingMilliseconds)]
    private static partial Regex BoundedKey();

    /// <summary>Whether a text holds a secret word, then a run of name characters, then a separator — <c>CollectorApiKey":</c>,
    /// <c>Authorization:</c>, <c>token=</c>, <c>Password=</c> (design §2.1 arm 7). The extent of a value inside a blob cannot be
    /// delimited, so a hit makes the whole blob go.</summary>
    public static bool HoldsASecretBeforeASeparator(string text) => Hits(WordBeforeSeparator(), text);

    [GeneratedRegex("(?:" + WordList + @"|(?<![A-Za-z0-9])key)[A-Za-z0-9_-]{0,32}[ \t]{0,3}[:=""']", Folded, PatternCeilingMilliseconds)]
    private static partial Regex WordBeforeSeparator();

    /// <summary>Whether a text holds a creds-shaped token ANYWHERE (design §2.3): the providers' own prefixes, anchored to a token
    /// boundary so <c>task-queue-…</c> is not an <c>sk-</c> key.</summary>
    public static bool IsCredsShaped(string text) => Hits(CredsShape(), text);

    private const string Boundary = "(?<![A-Za-z0-9])";

    /// <summary>One alternative per family; each comment names where the shape is documented.</summary>
    private const string Families =
        // GitHub: the five classic token prefixes (ghp, gho, ghu, ghs, ghr) and the fine-grained token.
        "gh[pousr]_[A-Za-z0-9]{20,}|github_pat_[A-Za-z0-9_]{20,}"
        // OpenAI and Anthropic keys (sk-, sk-ant-, sk-proj-).
        + "|sk-[A-Za-z0-9_-]{16,}"
        // Stripe secret and publishable keys, live and test.
        + "|[sp]k_(?:live|test)_[A-Za-z0-9]{16,}"
        // Slack bot, app, user, refresh and legacy tokens.
        + "|xox[abprs]-[A-Za-z0-9-]{10,}"
        // AWS access key ids (long-term AKIA, temporary ASIA).
        + "|(?:AKIA|ASIA)[0-9A-Z]{16}"
        // Google API keys and OAuth access tokens.
        + @"|AIza[0-9A-Za-z_-]{35}|ya29\.[0-9A-Za-z_-]+"
        // GitLab, npm, PyPI, DigitalOcean and Hugging Face tokens.
        + "|glpat-[A-Za-z0-9_-]{20,}|npm_[A-Za-z0-9]{36}|pypi-[A-Za-z0-9_-]{20,}|dop_v1_[a-f0-9]{64}|hf_[A-Za-z0-9]{20,}"
        // SendGrid API keys.
        + @"|SG\.[A-Za-z0-9_-]{16,}\.[A-Za-z0-9_-]{16,}"
        // The CredsForDevs config key: cfgk_ and 43 base64url characters (dew_flow_creds_for_devs configKey.ts).
        + "|cfgk_[A-Za-z0-9_-]{20,}"
        // JSON web tokens: header, payload, signature (the signature may be empty).
        + @"|eyJ[A-Za-z0-9_-]{8,}\.[A-Za-z0-9_-]{8,}\.[A-Za-z0-9_-]*"
        // PEM blocks (every BEGIN line names a key or a certificate chain a command line should not carry) and age secret keys.
        + "|-----BEGIN |AGE-SECRET-KEY-1[A-Z0-9]{20,}";

    [GeneratedRegex(Boundary + "(?:" + Families + ")", Options, PatternCeilingMilliseconds)]
    private static partial Regex CredsShape();

    /// <summary>The userinfo shape of basic auth as a VALUE — <c>user:password</c>, no slash and no <c>@</c> — judged only after the
    /// flags that take one (<see cref="IsUserinfoFlag"/>), so <c>systemd --user</c>, whose next word holds no colon, is untouched.</summary>
    public static bool IsUserinfo(string text) => Hits(Userinfo(), text);

    [GeneratedRegex(@"^[^:/@\s]{1,64}:[^\s@]{1,256}\z", Options, PatternCeilingMilliseconds)]
    private static partial Regex Userinfo();

    private static readonly string[] UserinfoFlags = ["-u", "--user", "--proxy-user", "--credentials", "--basic-auth"];

    /// <summary>The flags that carry basic-auth userinfo.</summary>
    public static bool IsUserinfoFlag(string flag) => UserinfoFlags.Contains(flag, StringComparer.Ordinal);

    /// <summary>The programs whose <c>-u</c> / <c>--user</c> value is judged by the userinfo shape (design §2.3).</summary>
    private static readonly string[] UserinfoPrograms = ["curl", "wget", "git", "gh"];

    /// <summary>Programs whose NEXT argument after <c>-p</c> is a password (<c>sshpass -p x</c>).</summary>
    private static readonly string[] PasswordFlagPrograms = ["sshpass", "mysql", "mysqldump", "mariadb", "mariadb-dump"];

    /// <summary>The MySQL family: <c>-pSECRET</c> (no space) is the password, <c>-p</c> alone prompts.</summary>
    private static readonly string[] AttachedPasswordPrograms = ["mysql", "mysqldump", "mariadb", "mariadb-dump"];

    /// <summary>A program-specific rule applies to <paramref name="argument"/>, which follows <paramref name="previous"/>: the
    /// password of <c>sshpass -p</c> / <c>mysql -p</c>, or the userinfo of <c>curl -u</c> (design §2.1 arm 2). Docker's <c>-p 8080:80</c>
    /// is a port map and is not in the table.</summary>
    public static bool IsProgramPasswordValue(string program, string previous, string argument) =>
        (previous == "-p" && Listed(PasswordFlagPrograms, program))
        || (IsUserinfoFlag(previous) && Listed(UserinfoPrograms, program) && IsUserinfo(argument));

    /// <summary>The MySQL family's attached form: <c>-pSECRET</c> becomes <c>-p&lt;redacted&gt;</c>.</summary>
    public static bool IsAttachedPassword(string program, string argument) =>
        argument.Length > 2 && argument[0] == '-' && argument[1] == 'p' && Listed(AttachedPasswordPrograms, program);

    private static bool Listed(string[] programs, string program) => programs.Contains(program, StringComparer.OrdinalIgnoreCase);

    /// <summary>One match, with a match that ran out of time counted as a HIT: the rule denies by default.</summary>
    private static bool Hits(Regex pattern, string text)
    {
        try
        {
            return pattern.IsMatch(text);
        }
        catch (RegexMatchTimeoutException)
        {
            return true;
        }
    }
}
