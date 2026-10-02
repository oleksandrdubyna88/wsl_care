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
}

/// <summary>One thing the command line accepts: how it is spelt, what it does, what it asks for.</summary>
/// <param name="Usage">The spelling shown in the help text.</param>
/// <param name="Summary">One line saying what it does.</param>
/// <param name="Spellings">Every argument that selects it, <paramref name="Usage"/> included.</param>
/// <param name="Answer">The request it produces.</param>
internal sealed record Command(string Usage, string Summary, IReadOnlyList<string> Spellings, Request Answer);

/// <summary>
/// Argument parsing, kept pure so the shapes are a unit test rather than something discovered by
/// running the binary with the wrong words. Mirrors the hand-rolled parser of the family's
/// <c>creds</c> CLI: no command-line package, nothing reflective, nothing for AOT to trip on.
/// </summary>
/// <remarks>
/// <para><see cref="Commands"/> is the ONE register of what this binary accepts. The parser and the
/// help text are both derived from it, so a command cannot be accepted and undocumented, or
/// documented and refused. The verbs of plan §6 (<c>status</c>, <c>collect</c>, <c>act</c>, …)
/// arrive in later stories as entries here.</para>
/// </remarks>
internal static class CommandLine
{
    internal const string BinaryName = "wsl-care";

    internal static readonly IReadOnlyList<Command> Commands =
    [
        new("--help", "print this text", ["--help", "-h", "help"], new Request.Help()),
        new("--version", "print the version of this build", ["--version"], new Request.Version()),
    ];

    internal static Request Parse(IReadOnlyList<string> argv)
    {
        if (argv.Count == 0)
        {
            return new Request.Help();
        }

        var command = Commands.FirstOrDefault(c => c.Spellings.Contains(argv[0], StringComparer.Ordinal));
        return command switch
        {
            null => new Request.Failed(
                $"unknown verb or option \"{Printable(argv[0])}\". Run \"{BinaryName} --help\" to see what exists."),
            _ when argv.Count > 1 => new Request.Failed(
                $"\"{BinaryName} {command.Usage}\" takes no further arguments."),
            _ => command.Answer,
        };
    }

    /// <summary>The help text, derived from <see cref="Commands"/>.</summary>
    internal static string HelpText { get; } = BuildHelpText();

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
            .Append("This build answers only the commands above; the collectors and cleanups arrive in later releases.")
            .ToString();
    }

    /// <summary>
    /// User text echoed back in an error message, with control characters replaced, so a refusal
    /// stays ONE line on stderr whatever was typed — a newline or an escape sequence in an argument
    /// would otherwise split or repaint the message.
    /// </summary>
    private static string Printable(string text) =>
        string.Create(text.Length, text, static (span, source) =>
        {
            for (var i = 0; i < source.Length; i++)
            {
                span[i] = char.IsControl(source[i]) ? '?' : source[i];
            }
        });
}
