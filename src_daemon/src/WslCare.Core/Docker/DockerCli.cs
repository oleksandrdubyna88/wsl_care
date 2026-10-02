using System.Globalization;

using WslCare.Core.Processes;

namespace WslCare.Core.Docker;

/// <summary>Why Docker could not answer (plan §15b #7) — a closed set the panel can word and a test can name.</summary>
public enum DockerFailure
{
    /// <summary>No <c>docker</c> executable could be started: not installed, or not on <c>PATH</c>.</summary>
    NotInstalled,

    /// <summary>The CLI is there and no daemon answers: the socket or pipe does not exist, Docker Desktop is stopped.</summary>
    DaemonStopped,

    /// <summary>A socket is there and refused us: permission denied (not in the <c>docker</c> group), connection refused.</summary>
    SocketRefused,

    /// <summary>The command did not answer within its ceiling (its process tree was killed), or Docker reported its own timeout.</summary>
    TimedOut,

    /// <summary>Docker answered with an error this classification does not know; the reason quotes it.</summary>
    CommandFailed,

    /// <summary>The command policy refused the argv; nothing was started.</summary>
    Refused,

    /// <summary>Docker answered, and the answer could not be read (cut by the output cap, or not the expected shape).</summary>
    Unparseable,
}

/// <summary>A failure and the sentence that explains it — the reason every dependent figure carries.</summary>
public sealed record DockerProblem(DockerFailure Kind, string Reason);

/// <summary>What one <c>docker</c> command produced: its standard output, or why there is none.</summary>
public abstract record DockerAnswer
{
    private DockerAnswer()
    {
    }

    public sealed record Answered(string Stdout) : DockerAnswer;

    public sealed record Failed(DockerProblem Problem) : DockerAnswer;
}

/// <summary>
/// Runs a docker <see cref="ToolCommand"/> through the ONE process launcher (<see cref="ICommandRunner"/>:
/// argv, ceiling, tree kill) and turns the outcome into an answer or a classified problem.
/// </summary>
public sealed class DockerCli(ICommandRunner runner)
{
    private const int ReasonQuoteChars = 300;

    public async Task<DockerAnswer> RunAsync(ToolCommand command, CancellationToken cancellationToken) =>
        Classify(command, await runner.RunAsync(command.ToRequest(), cancellationToken).ConfigureAwait(false));

    /// <summary>The outcome of <paramref name="command"/> as an answer or a problem — pure, so every
    /// failure kind is a unit test.</summary>
    public static DockerAnswer Classify(ToolCommand command, CommandOutcome outcome) => outcome switch
    {
        CommandOutcome.Exited { ExitCode: 0, Stdout.Truncated: true } => Fail(DockerFailure.Unparseable, $"{command.Shown} printed more than {command.OutputCapChars} characters; the answer was cut and is not read"),
        CommandOutcome.Exited { ExitCode: 0 } exited => new DockerAnswer.Answered(exited.Stdout.Text),
        CommandOutcome.Exited { Stdout.Truncated: false } exited when OnlyVanished(command, exited.Stderr.Text) => new DockerAnswer.Answered(exited.Stdout.Text),
        CommandOutcome.Exited exited => new DockerAnswer.Failed(FromStderr(command, exited.ExitCode, exited.Stderr.Text)),
        CommandOutcome.TimedOut timedOut => Fail(DockerFailure.TimedOut, $"{command.Shown} did not answer within {Seconds(timedOut.Timeout)} s; its process tree was killed"),
        CommandOutcome.FailedToStart failed => Fail(DockerFailure.NotInstalled, $"docker could not be started: not installed, or not on PATH ({failed.Reason})"),
        CommandOutcome.Refused refused => Fail(DockerFailure.Refused, $"the command policy refused {command.Shown}: {refused.Reason}"),
        _ => throw new System.Diagnostics.UnreachableException("CommandOutcome is a closed set"),
    };

    /// <summary>
    /// <c>container inspect</c> over a list answers for every container that still exists and exits 1 naming
    /// each one that does not ("Error response from daemon: No such container: &lt;id&gt;"). A container removed
    /// between the listing and the inspect — measured 2026-10-02, while another session was replacing its
    /// containers — is gone, which is the one thing a cleanup preview needs to know about it; the answer for
    /// the others is read rather than failing every row that depends on it.
    /// </summary>
    private static bool OnlyVanished(ToolCommand command, string stderr)
    {
        var lines = stderr.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
        return command.Arguments is ["container", "inspect", ..] && lines.Count > 0
            && lines.All(l => l.Contains("No such container:", StringComparison.Ordinal));
    }

    /// <summary>
    /// A non-zero exit, classified by what Docker printed. The phrases are Docker's own — measured
    /// 2026-10-02 against Docker 29.6.1 on Linux and Windows (a socket that does not exist, a socket this
    /// user may not open, a named pipe that does not exist, a TCP endpoint nobody answers) — plus the
    /// wording of Docker before 29 ("Cannot connect to the Docker daemon"), which the installed base still
    /// prints. An unknown message is <see cref="DockerFailure.CommandFailed"/> with the message quoted:
    /// never mistaken for a stopped daemon, never dropped.
    /// </summary>
    public static DockerProblem FromStderr(ToolCommand command, int exitCode, string stderr)
    {
        var quote = Quote(stderr);
        var said = quote.Length > 0 ? $": {quote}" : $" (exit {exitCode.ToString(CultureInfo.InvariantCulture)}, nothing on stderr)";
        return Kind(stderr) switch
        {
            DockerFailure.SocketRefused => new(DockerFailure.SocketRefused, $"the Docker socket refused this user{said}"),
            DockerFailure.DaemonStopped => new(DockerFailure.DaemonStopped, $"no Docker daemon answers (Docker Desktop stopped, or the socket is missing){said}"),
            DockerFailure.TimedOut => new(DockerFailure.TimedOut, $"Docker timed out connecting to its daemon{said}"),
            _ => new(DockerFailure.CommandFailed, $"{command.Shown} failed{said}"),
        };
    }

    /// <summary>First match wins. Measured: no real message carries phrases of two kinds, so the order is a
    /// tie-break for a future one, not a correction.</summary>
    private static readonly IReadOnlyList<(DockerFailure Kind, string[] Phrases)> Phrases =
    [
        (DockerFailure.SocketRefused, ["permission denied", "connection refused"]),
        (DockerFailure.DaemonStopped, ["failed to connect to the docker api", "cannot connect to the docker daemon", "is the docker daemon running", "error during connect"]),
        (DockerFailure.TimedOut, ["i/o timeout", "context deadline exceeded"]),
    ];

    private static DockerFailure Kind(string stderr) =>
        Phrases.FirstOrDefault(p => p.Phrases.Any(phrase => stderr.Contains(phrase, StringComparison.OrdinalIgnoreCase)), (DockerFailure.CommandFailed, [])).Kind;

    /// <summary>The first non-empty line of what Docker printed, cut — a reason is a sentence, not a log.</summary>
    internal static string Quote(string stderr)
    {
        var line = stderr.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0) ?? string.Empty;
        return line.Length <= ReasonQuoteChars ? line : line[..ReasonQuoteChars] + "...";
    }

    private static DockerAnswer.Failed Fail(DockerFailure kind, string reason) => new(new DockerProblem(kind, reason));

    private static string Seconds(TimeSpan span) => span.TotalSeconds.ToString("0", CultureInfo.InvariantCulture);
}
