namespace WslCare.Cli;

/// <summary>
/// Everything the CLI writes that is not a log line: an answer on stdout; a refusal, a note, an
/// internal error or an interruption on stderr. Answers go to stdout and nothing else does, because
/// the extension parses stdout.
/// </summary>
/// <remarks>The ONE road to a stream (the console sink is the one road for log lines): every stderr
/// message passes through <see cref="Message"/>, which applies <see cref="CommandLine.Printable"/>, so
/// no message — whatever it quotes, typed or read from a file or a path — can carry a newline, a
/// carriage return or an escape sequence that would split or repaint it. <c>OutputRoadTests</c> fails
/// the build on a stream write anywhere else in the CLI.</remarks>
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
        stderr.WriteLine(Message(message));
        return (int)ExitCode.Usage;
    }

    /// <summary>Something the person should know that is not the answer — stderr, so stdout stays parseable.</summary>
    public static void Note(TextWriter stderr, string message) => stderr.WriteLine(Message(message));

    /// <summary>ONE line for a defect this binary should have handled, and the internal code.</summary>
    public static int Internal(TextWriter stderr, string message)
    {
        stderr.WriteLine(Message($"internal error: {message}"));
        return (int)ExitCode.Internal;
    }

    /// <summary>ONE line for a run stopped by a signal, and the interrupted code.</summary>
    public static int Interrupted(TextWriter stderr)
    {
        stderr.WriteLine(Message("interrupted."));
        return (int)ExitCode.Interrupted;
    }

    private static string Message(string text) => $"{CommandLine.BinaryName}: {CommandLine.Printable(text)}";
}
