using System.Text.Json;

using WslCare.Core.Processes.Redaction;

namespace WslCare.Core.Tests.Processes.Redaction;

/// <summary>The shared inputs of the E15.S7 redaction tests: the limits in force by default, the invented values, the helpers.
/// Every value here is INVENTED — none is a credential of any service. A provider-shaped token is built from two halves so no
/// source line holds a contiguous secret-scanner pattern.</summary>
internal static class RedactionCases
{
    /// <summary>The defaults of <c>processes.secretMinChars</c>, <c>processes.maxJudgedArgChars</c>, <c>processes.shownCommandChars</c>.</summary>
    public static readonly RedactionLimits Limits = new(SecretMinChars: 20, MaxJudgedArgChars: 4096, ShownChars: 200);

    /// <summary><c>processes.shownSafeArgs</c>'s default.</summary>
    public const int SafeArgs = 2;

    public const string Redacted = "<redacted>";

    /// <summary>A creds config key: <c>cfgk_</c> and 43 base64url characters. Built from two halves (see the class summary).</summary>
    public const string ConfigKey = "cfgk" + "_" + "q7Hn2Vx9LmT4bKd8RzY3wPc6Jf1sGa5UeN0oXiB_-Zr";

    public const string GitHubToken = "gh" + "p_" + "INVENTEDinvented0123456789INVENTED00";

    /// <summary>An Anthropic-shaped key (<c>sk-ant-…</c>).</summary>
    public const string AnthropicKey = "sk" + "-ant-" + "api03-INVENTEDinvented0123456789";

    /// <summary>A JWT-shaped token: header, payload and signature, each invented and base64url.</summary>
    public const string Jwt = "ey" + "JhbGciOiJIUzI1NiJ9" + "." + "eyJzdWIiOiJpbnZlbnRlZCJ9" + "." + "aW52ZW50ZWRzaWc";

    public const string SessionGuid = "6f1c2b7e-3a94-4d05-8c1e-9b2a7d4f5e60";

    /// <summary>48 base64url characters: no prefix a list could know.</summary>
    public const string Blob48 = "q7Hn2Vx9LmT4bKd8RzY3wPc6Jf1sGa5UeN0oXiBlZrMt4Kd9";

    public const string Hex32 = "3f9a1c7e5b2d84a06f1e9c3b7d5a2048";

    public const string Hex40 = "9c1e7a3f5b2d8406a1f93c7e5b2d84a06f1e9c3b";

    public const string Hex64 = "3f9a1c7e5b2d84a06f1e9c3b7d5a20489c1e7a3f5b2d8406a1f93c7e5b2d84a0";

    public static string Shown(params string[] argv) => CommandLineRedaction.Shown(argv, Limits);

    public static IReadOnlyList<string> Redact(params string[] argv) => CommandLineRedaction.Redact(argv, Limits);

    /// <summary>The fixture's placeholders: the file holds <c>{GHP}</c>, never the token, so no line of the repository is a contiguous
    /// secret-scanner pattern (GitHub push protection reads this PUBLIC repository); <see cref="FixtureLines"/> expands them.</summary>
    private static readonly (string Placeholder, string Value)[] Placeholders =
        [("{GHP}", GitHubToken), ("{CFGK}", ConfigKey), ("{SKANT}", AnthropicKey), ("{JWT}", Jwt)];

    /// <summary>The fixture's cases, placeholders expanded: arm, argv, the shown line expected.</summary>
    public static IReadOnlyList<(string Arm, string[] Argv, string Expected)> FixtureLines()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "fixtures", "redaction", "realistic-leaks.txt");
        return [.. File.ReadAllLines(path).Where(l => l.Length > 0 && l[0] != '#').Select(Expand).Select(Parse)];
    }

    private static string Expand(string line) => Placeholders.Aggregate(line, (text, p) => text.Replace(p.Placeholder, p.Value, StringComparison.Ordinal));

    /// <summary>One invented token per credential family of the design's §2.3, each BUILT FROM HALVES: name, the token. The shape is
    /// what a break-it on any one pattern of <c>SecretShapes</c> turns red for.</summary>
    public static IReadOnlyList<(string Name, string Token)> CredsFamilyList { get; } =
    [
        ("GitHub classic", GitHubToken),
        ("GitHub fine-grained", "github" + "_pat_" + "INVENTEDinvented0123456789_INVENTED"),
        ("OpenAI / Anthropic", AnthropicKey),
        ("OpenAI project", "sk" + "-proj-" + "INVENTEDinvented0123456789"),
        ("Stripe", "sk" + "_live_" + "INVENTEDinvented0123456789"),
        ("Slack", "xox" + "b-" + "1234567890-INVENTED-invented"),
        ("AWS access key id", "AK" + "IA" + "INVENTED01234567"),
        ("Google API key", "AI" + "za" + "INVENTEDinvented0123456789-_INVENTE"),
        ("Google OAuth", "ya" + "29." + "INVENTEDinvented0123456789-_x"),
        ("GitLab", "glp" + "at-" + "INVENTEDinvented0123456789"),
        ("npm", "npm" + "_" + "INVENTEDinvented0123456789INVENTEDin"),
        ("PyPI", "pyp" + "i-" + "INVENTEDinvented0123456789"),
        ("DigitalOcean", "dop" + "_v1_" + Hex64),
        ("Hugging Face", "hf" + "_" + "INVENTEDinvented0123456789"),
        ("SendGrid", "SG" + "." + "INVENTEDinvented0123" + "." + "INVENTEDinvented0123"),
        ("Telegram bot", "bot" + "123456789" + ":" + "AAHq7Hn2Vx9LmT4bKd8RzY3wPc6Jf1sGa5"),
        ("CredsForDevs config key", ConfigKey),
        ("JWT", Jwt),
        ("PEM private key", "-----BEGIN " + "OPENSSH PRIVATE KEY-----"),
        ("age secret key", "AGE-SECRET-" + "KEY-1" + "INVENTED0123456789INVENTED0123456789"),
    ];

    /// <summary>The same catalogue as xUnit theory rows.</summary>
    public static TheoryData<string, string> CredsFamilies
    {
        get
        {
            var data = new TheoryData<string, string>();
            foreach (var (name, token) in CredsFamilyList)
            {
                data.Add(name, token);
            }

            return data;
        }
    }

    private static (string Arm, string[] Argv, string Expected) Parse(string line)
    {
        var parts = line.Split('\t');
        using var document = JsonDocument.Parse(parts[1]);
        return (parts[0], [.. document.RootElement.EnumerateArray().Select(e => e.GetString() ?? string.Empty)], parts[2]);
    }
}
