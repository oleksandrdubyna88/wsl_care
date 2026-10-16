namespace WslCare.Core.Processes.Redaction;

/// <summary>The three numbers the rule needs, handed in as arguments so the rule reads no configuration (as <c>HostRules</c> takes an
/// <c>EffectiveConfig</c>): the shortest token the entropy test judges, the longest argument judged at all, the cut of a shown line.</summary>
public sealed record RedactionLimits(int SecretMinChars, int MaxJudgedArgChars, int ShownChars);

/// <summary>E15.S7 — STUB (the RED commit): answers what the old rule's neighbours would, so each test fails for the symptom it names.</summary>
public static class CommandLineRedaction
{
    public static IReadOnlyList<string> Redact(IReadOnlyList<string> argv, RedactionLimits limits) => argv;

    public static string Shown(IReadOnlyList<string> argv, RedactionLimits limits)
    {
        var line = string.Join(' ', Redact(argv, limits));
        return line.Length <= limits.ShownChars ? line : line[..limits.ShownChars];
    }
}
