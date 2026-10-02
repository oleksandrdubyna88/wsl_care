namespace WslCare.Core.Processes;

/// <summary>
/// The one place a process starts (plan §8). Actions, collectors and probes call this; tests swap in
/// a recorder that answers scripted outcomes and keeps every argv it was given.
/// </summary>
/// <remarks>The real implementation, <see cref="ProcessCommandRunner"/>, cannot be built without a
/// <see cref="Policy.CommandPolicy"/> — the never-list and the declared templates every argv passes before a start.</remarks>
public interface ICommandRunner
{
    /// <summary>
    /// Runs the command to its end, its timeout, or the caller's cancellation.
    /// </summary>
    /// <exception cref="OperationCanceledException">The CALLER cancelled; the process tree has
    /// already been killed when this is thrown.</exception>
    Task<CommandOutcome> RunAsync(CommandRequest request, CancellationToken cancellationToken);

    /// <summary>
    /// Runs a command whose standard output is a STREAM (<c>docker events</c>, E2.S3): every line is handed to
    /// <paramref name="onStdoutLine"/> as it arrives, in order, instead of being collected. The same ceiling,
    /// policy and tree kill as <see cref="RunAsync"/>; the outcome's stdout is empty (the lines went to the
    /// callback), stderr is captured as usual.
    /// </summary>
    /// <remarks>A line longer than <see cref="CommandRequest.OutputCapChars"/> is cut there — a child that never
    /// ends a line cannot grow this process without bound. stderr is drained concurrently into a bounded capture, so a
    /// chatty child never blocks on it. Every way out but a normal end — the ceiling, the caller's cancellation, a callback
    /// that throws — kills the whole process tree and waits for the child to be gone (in a <c>finally</c>) before the
    /// outcome or the exception arrives.</remarks>
    /// <exception cref="OperationCanceledException">The CALLER cancelled; the process tree has
    /// already been killed when this is thrown.</exception>
    Task<CommandOutcome> StreamAsync(CommandRequest request, Action<string> onStdoutLine, CancellationToken cancellationToken);
}

/// <summary>What the policy says about one request.</summary>
public abstract record CommandVerdict
{
    private CommandVerdict()
    {
    }

    public static readonly CommandVerdict Allowed = new AllowedVerdict();

    public static CommandVerdict Refuse(string reason) => new Refused(reason);

    public bool IsAllowed => this is AllowedVerdict;

    public sealed record Refused(string Reason) : CommandVerdict;

    private sealed record AllowedVerdict : CommandVerdict;
}
