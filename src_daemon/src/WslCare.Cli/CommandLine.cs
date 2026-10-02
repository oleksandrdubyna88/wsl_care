using System.Text;

namespace WslCare.Cli;

/// <summary>What the user asked for, or why the arguments could not be read.</summary>
/// <remarks>A closed set: the constructor is private, so every case is one of the nested records
/// and a <c>switch</c> over a request can be checked for completeness by reading this file.</remarks>
internal abstract record Request
{
    private Request()
    {
    }

    internal sealed record Help : Request;

    internal sealed record Version : Request;

    internal sealed record Failed(string Message) : Request;

    /// <summary><c>config get [key] [--json]</c>: every key, or one; as text, or as the JSON report.</summary>
    internal sealed record ConfigGet(string Key, bool Json) : Request;

    /// <summary><c>config set &lt;key&gt; &lt;value&gt;</c>: the value as typed; validated by the command.</summary>
    internal sealed record ConfigSet(string Key, string Value) : Request;

    /// <summary><c>config reset &lt;key&gt;</c>: remove the key from the user layer.</summary>
    internal sealed record ConfigReset(string Key) : Request;
}

/// <summary>One thing the command line accepts: how it is spelt, what it does, how it is parsed.</summary>
/// <param name="Spellings">Every word sequence that selects it (<c>["config","get"]</c>; <c>["-h"]</c>).</param>
/// <param name="Usage">The spelling shown in the help text, placeholders included.</param>
/// <param name="Summary">One line saying what it does.</param>
/// <param name="Example">A complete argv that must parse — what the derived test and the scenario register use.</param>
/// <param name="ParseRest">Reads the arguments after the spelling.</param>
internal sealed record Command(
    IReadOnlyList<IReadOnlyList<string>> Spellings,
    string Usage,
    string Summary,
    IReadOnlyList<string> Example,
    Func<IReadOnlyList<string>, Request> ParseRest);

/// <summary>One spelling and the command it selects — the unit the parser matches on.</summary>
internal sealed record Spelt(Command Command, IReadOnlyList<string> Spelling);

/// <summary>
/// Argument parsing, kept pure so the shapes are a unit test rather than something discovered by
/// running the binary with the wrong words. Mirrors the hand-rolled parser of the family's
/// <c>creds</c> CLI: no command-line package, nothing reflective, nothing for AOT to trip on.
/// </summary>
/// <remarks>
/// <para><see cref="Commands"/> is the ONE register of what this binary accepts. The parser and the
/// help text are both derived from it, so a command cannot be accepted and undocumented, or
/// documented and refused. The remaining verbs of plan §6 (<c>status</c>, <c>collect</c>,
/// <c>act</c>, …) arrive in later stories as entries here.</para>
/// </remarks>
internal static class CommandLine
{
    internal const string BinaryName = "wsl-care";

    private const string JsonFlag = "--json";

    internal static readonly IReadOnlyList<Command> Commands =
    [
        new([["--help"], ["-h"], ["help"]], "--help", "print this text", ["--help"], NoMore("--help", new Request.Help())),
        new([["--version"]], "--version", "print the version of this build", ["--version"], NoMore("--version", new Request.Version())),
        new([["config", "get"]], "config get [key] [--json]", "print the effective settings (or one) and the layer each came from", ["config", "get"], ParseConfigGet),
        new([["config", "set"]], "config set <key> <value>", "validate one setting and write it into the user layer", ["config", "set", "dryRun", "false"], ParseConfigSet),
        new([["config", "reset"]], "config reset <key>", "remove one setting from the user layer", ["config", "reset", "dryRun"], ParseConfigReset),
    ];

    /// <summary>Every spelling of <see cref="Commands"/> with its command, longest first — ordered once,
    /// so a parse is a single pass that stops at the first prefix match. Declared after
    /// <see cref="Commands"/> on purpose: static fields initialise in textual order.</summary>
    private static readonly IReadOnlyList<Spelt> SpellingsLongestFirst = LongestFirst(Commands);

    internal static Request Parse(IReadOnlyList<string> argv)
    {
        if (argv.Count == 0)
        {
            return new Request.Help();
        }

        var (command, spelling) = Match(argv, SpellingsLongestFirst);
        if (command is not null)
        {
            return command.ParseRest(argv.Skip(spelling.Count).ToList());
        }

        var subVerbs = SubVerbsOf(argv[0]);
        return subVerbs.Count > 0
            ? new Request.Failed($"\"{BinaryName} {Printable(argv[0])}\" needs one of: {string.Join(", ", subVerbs)}.")
            : new Request.Failed($"unknown verb or option \"{Printable(argv[0])}\". Run \"{BinaryName} --help\" to see what exists.");
    }

    /// <summary>The help text, derived from <see cref="Commands"/>.</summary>
    internal static string HelpText { get; } = BuildHelpText();

    /// <summary>Every spelling of <paramref name="commands"/>, longest first; a stable sort, so spellings of
    /// equal length keep the register's order.</summary>
    internal static IReadOnlyList<Spelt> LongestFirst(IEnumerable<Command> commands) =>
        [.. commands.SelectMany(c => c.Spellings.Select(s => new Spelt(c, s))).OrderByDescending(spelt => spelt.Spelling.Count)];

    /// <summary>The longest spelling that is a prefix of <paramref name="argv"/>, and its command: the
    /// first match in <paramref name="longestFirst"/>, which is ordered by length already.</summary>
    internal static (Command? Command, IReadOnlyList<string> Spelling) Match(IReadOnlyList<string> argv, IReadOnlyList<Spelt> longestFirst)
    {
        foreach (var spelt in longestFirst)
        {
            if (StartsWith(argv, spelt.Spelling))
            {
                return (spelt.Command, spelt.Spelling);
            }
        }

        return (null, []);
    }

    private static bool StartsWith(IReadOnlyList<string> argv, IReadOnlyList<string> spelling) =>
        spelling.Count <= argv.Count && spelling.Zip(argv).All(pair => string.Equals(pair.First, pair.Second, StringComparison.Ordinal));

    /// <summary>The second words of every multi-word spelling that begins with <paramref name="verb"/>.</summary>
    private static IReadOnlyList<string> SubVerbsOf(string verb) =>
        [.. Commands.SelectMany(c => c.Spellings)
            .Where(s => s.Count > 1 && string.Equals(s[0], verb, StringComparison.Ordinal))
            .Select(s => s[1])
            .Distinct(StringComparer.Ordinal)];

    private static Func<IReadOnlyList<string>, Request> NoMore(string usage, Request answer) =>
        rest => rest.Count == 0 ? answer : new Request.Failed($"\"{BinaryName} {usage}\" takes no further arguments.");

    private static Request ParseConfigGet(IReadOnlyList<string> rest)
    {
        var key = string.Empty;
        var json = false;
        foreach (var token in rest)
        {
            switch (token)
            {
                case JsonFlag:
                    json = true;
                    break;
                case var option when option.StartsWith('-'):
                    return new Request.Failed($"\"{BinaryName} config get\" does not know the option \"{Printable(option)}\"; it takes an optional key and {JsonFlag}.");
                case var _ when key.Length > 0:
                    return new Request.Failed($"\"{BinaryName} config get\" takes at most one key; got \"{Printable(key)}\" and \"{Printable(token)}\".");
                default:
                    key = token;
                    break;
            }
        }

        return new Request.ConfigGet(key, json);
    }

    private static Request ParseConfigSet(IReadOnlyList<string> rest) =>
        rest.Count == 2
            ? new Request.ConfigSet(rest[0], rest[1])
            : new Request.Failed($"\"{BinaryName} config set\" needs exactly a key and a value: {BinaryName} config set <key> <value>.");

    private static Request ParseConfigReset(IReadOnlyList<string> rest) =>
        rest.Count == 1
            ? new Request.ConfigReset(rest[0])
            : new Request.Failed($"\"{BinaryName} config reset\" needs exactly one key: {BinaryName} config reset <key>.");

    private static string BuildHelpText()
    {
        var width = Commands.Max(c => c.Usage.Length);
        var text = new StringBuilder()
            // ASCII only: a Windows console on an OEM code page turns an em dash into '?'.
            .AppendLine($"{BinaryName}: keeps the WSL VM on this machine from degrading over the working day.")
            .AppendLine()
            .AppendLine("Usage:");
        foreach (var command in Commands)
        {
            text.AppendLine($"  {BinaryName} {command.Usage.PadRight(width)}  {command.Summary}");
        }

        return text
            .AppendLine()
            .AppendLine("Settings are read from three layers, each overriding the last: the embedded defaults, the machine")
            .AppendLine("file, and the user file that \"config set\" writes. \"config get\" names the layer behind every value.")
            .AppendLine()
            .Append("This build answers only the commands above; the collectors and cleanups arrive in later releases.")
            .ToString();
    }

    /// <summary>
    /// User text echoed back in an error message, with control characters replaced, so a refusal
    /// stays ONE line on stderr whatever was typed — a newline or an escape sequence in an argument
    /// would otherwise split or repaint the message.
    /// </summary>
    internal static string Printable(string text) =>
        string.Create(text.Length, text, static (span, source) =>
        {
            for (var i = 0; i < source.Length; i++)
            {
                span[i] = char.IsControl(source[i]) ? '?' : source[i];
            }
        });
}
