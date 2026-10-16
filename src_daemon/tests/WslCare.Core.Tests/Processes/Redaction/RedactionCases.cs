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

    /// <summary>A creds config key: <c>cfgk_</c> and 43 base64url characters.</summary>
    public const string ConfigKey = "cfgk_q7Hn2Vx9LmT4bKd8RzY3wPc6Jf1sGa5UeN0oXiB_-Zr";

    public const string GitHubToken = "ghp_INVENTEDinvented0123456789INVENTED00";

    public const string SessionGuid = "6f1c2b7e-3a94-4d05-8c1e-9b2a7d4f5e60";

    /// <summary>48 base64url characters: no prefix a list could know.</summary>
    public const string Blob48 = "q7Hn2Vx9LmT4bKd8RzY3wPc6Jf1sGa5UeN0oXiBlZrMt4Kd9";

    public const string Hex32 = "3f9a1c7e5b2d84a06f1e9c3b7d5a2048";

    public const string Hex40 = "9c1e7a3f5b2d8406a1f93c7e5b2d84a06f1e9c3b";

    public const string Hex64 = "3f9a1c7e5b2d84a06f1e9c3b7d5a20489c1e7a3f5b2d8406a1f93c7e5b2d84a0";

    public static string Shown(params string[] argv) => CommandLineRedaction.Shown(argv, Limits);

    public static IReadOnlyList<string> Redact(params string[] argv) => CommandLineRedaction.Redact(argv, Limits);

    /// <summary>The fixture's cases: arm, argv, the shown line expected.</summary>
    public static IReadOnlyList<(string Arm, string[] Argv, string Expected)> FixtureLines()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "fixtures", "redaction", "realistic-leaks.txt");
        return [.. File.ReadAllLines(path).Where(l => l.Length > 0 && l[0] != '#').Select(Parse)];
    }

    private static (string Arm, string[] Argv, string Expected) Parse(string line)
    {
        var parts = line.Split('\t');
        using var document = JsonDocument.Parse(parts[1]);
        return (parts[0], [.. document.RootElement.EnumerateArray().Select(e => e.GetString() ?? string.Empty)], parts[2]);
    }
}
