namespace WslCare.Cli;

/// <summary>
/// The three things a verb may write: an answer on stdout, a refusal on stderr, a note on stderr.
/// Answers go to stdout and nothing else does, because the extension parses stdout.
/// </summary>
internal static class Output
{
    public static int Answer(TextWriter stdout, string text)
    {
        stdout.WriteLine(text);
        return (int)ExitCode.Ok;
    }

    /// <summary>ONE line on stderr, prefixed with the binary's name, and the usage code.</summary>
    public static int Refuse(TextWriter stderr, string message)
    {
        stderr.WriteLine($"{CommandLine.BinaryName}: {CommandLine.Printable(message)}");
        return (int)ExitCode.Usage;
    }

    /// <summary>Something the person should know that is not the answer — stderr, so stdout stays parseable.</summary>
    public static void Note(TextWriter stderr, string message) =>
        stderr.WriteLine($"{CommandLine.BinaryName}: {CommandLine.Printable(message)}");
}
