namespace WslCare.Core.Processes.Redaction;

/// <summary>
/// E15.S7 design §2.1: the order of judgement for ONE argument, in the context of the argument before it and the program. The first arm
/// that decides wins; nothing is judged twice and a value is never partly redacted (<c>name=abc&lt;redacted&gt;</c> does not exist).
/// An argument over <c>processes.maxJudgedArgChars</c> is replaced whole and unjudged: a 4 KiB positional is a script or a blob, and a
/// bounded input is what keeps every pattern linear.
/// </summary>
internal static class ArgumentJudge
{
    private const string Redacted = CommandLineRedaction.Redacted;

    /// <summary>What an arm says: nothing (<see cref="Pass"/>), or the text the argument becomes. <paramref name="Blob"/> marks a verdict
    /// on a blob that holds a secret word next to a separator — its extent cannot be delimited, so under an assignment the WHOLE
    /// argument goes, not just the value.</summary>
    private readonly record struct Verdict(bool Decided, string Text, bool Blob = false)
    {
        public static Verdict Pass => default;

        public static Verdict Is(string text) => new(true, text);

        public static Verdict Cut(string text, bool blob = false) => new(true, text, blob);
    }

    /// <summary>The text the argument is shown as (before <see cref="ShownText.Printable"/>).</summary>
    public static string Judge(string argument, string previous, string program, RedactionLimits limits) =>
        argument.Length > limits.MaxJudgedArgChars
            ? Redacted
            : Arms(argument, previous, program, limits).Where(v => v.Decided).Select(v => v.Text).FirstOrDefault(argument);

    private static IEnumerable<Verdict> Arms(string argument, string previous, string program, RedactionLimits limits)
    {
        yield return AfterASecretFlag(previous);
        yield return ProgramSpecific(argument, previous, program);
        yield return Assignment(argument, limits);
        yield return Positional(argument, limits);
    }

    /// <summary>Arm 1: the previous argument is a secret FLAG (<c>--password x</c>). Today's rule, over the widened word list.</summary>
    private static Verdict AfterASecretFlag(string previous) =>
        previous.StartsWith('-') && !previous.Contains('=') && SecretShapes.NamesASecret(previous) ? Verdict.Is(Redacted) : Verdict.Pass;

    /// <summary>Arm 2: the program's own password flags (<c>sshpass -p</c>, <c>mysql -pSECRET</c>, <c>curl -u user:pass</c>).</summary>
    private static Verdict ProgramSpecific(string argument, string previous, string program) =>
        SecretShapes.IsProgramPasswordValue(program, previous, argument) ? Verdict.Is(Redacted)
        : SecretShapes.IsAttachedPassword(program, argument) ? Verdict.Is("-p" + Redacted)
        : Verdict.Pass;

    /// <summary>Arm 3: <c>name=value</c> — with or without leading dashes, the name of <c>[A-Za-z0-9_.-]</c>, so <c>a==b</c> and <c>=x</c>
    /// are not assignments. A secret NAME loses its value; otherwise the VALUE is judged as a positional, and a value that fails
    /// gives <c>name=&lt;redacted&gt;</c> — or the whole argument when the failure is a blob (design arm 7, "reached through arm 3").</summary>
    private static Verdict Assignment(string argument, RedactionLimits limits)
    {
        var equals = argument.IndexOf('=');
        if (!IsAssignment(argument, equals))
        {
            return Verdict.Pass;
        }

        var name = argument[..equals];
        return SecretShapes.NamesASecret(name) ? Verdict.Is(name + "=" + Redacted) : JudgedValue(argument, name, Judged(argument[(equals + 1)..], limits));
    }

    private static bool IsAssignment(string argument, int equals) =>
        equals > 0 && !(equals + 1 < argument.Length && argument[equals + 1] == '=') && argument[..equals].All(IsNameCharacter);

    private static bool IsNameCharacter(char c) => char.IsAsciiLetterOrDigit(c) || c is '_' or '.' or '-';

    private static Verdict JudgedValue(string argument, string name, Verdict value) =>
        value.Blob ? Verdict.Is(Redacted)
        : value.Text != argument[(name.Length + 1)..] ? Verdict.Is(name + "=" + Redacted)
        : Verdict.Is(argument);

    /// <summary>Arms 4–8 for an argument that is no flag value and no assignment.</summary>
    private static Verdict Positional(string argument, RedactionLimits limits) => Verdict.Is(Judged(argument, limits).Text);

    /// <summary>Arms 4–8 in order, the first that decides: a URL, a creds shape, a path, a blob with a secret word, high entropy;
    /// else the text is kept.</summary>
    private static Verdict Judged(string text, RedactionLimits limits) =>
        ShapeArms(text, limits).Where(v => v.Decided).DefaultIfEmpty(Verdict.Is(text)).First();

    private static IEnumerable<Verdict> ShapeArms(string text, RedactionLimits limits)
    {
        yield return UrlArm(text);
        yield return SecretShapes.IsCredsShaped(text) ? Verdict.Is(Redacted) : Verdict.Pass;
        yield return SafeShapes.IsPath(text) ? Verdict.Is(text) : Verdict.Pass;
        yield return SecretShapes.HoldsASecretBeforeASeparator(text) ? Verdict.Cut(Redacted, blob: true) : Verdict.Pass;
        yield return HighEntropy.Looks(text, limits.SecretMinChars) ? Verdict.Is(Redacted) : Verdict.Pass;
    }

    /// <summary>Arm 4: a URL decides everything about itself (it never reaches the entropy test).</summary>
    private static Verdict UrlArm(string text) => UrlRedaction.IsUrl(text) ? Verdict.Is(UrlRedaction.Shown(text)) : Verdict.Pass;
}
