namespace WslCare.Core.Processes;

/// <summary>
/// The one place a process starts (plan §8). Actions, collectors and probes call this; tests swap in
/// a recorder that answers scripted outcomes and keeps every argv it was given.
/// </summary>
public interface ICommandRunner
{
    /// <summary>
    /// Runs the command to its end, its timeout, or the caller's cancellation.
    /// </summary>
    /// <exception cref="OperationCanceledException">The CALLER cancelled; the process tree has
    /// already been killed when this is thrown.</exception>
    Task<CommandOutcome> RunAsync(CommandRequest request, CancellationToken cancellationToken);
}

/// <summary>What the policy says about one argv.</summary>
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

/// <summary>
/// The filter every argv passes before a process starts — the hook the never-list of plan §5 hangs
/// on. E3.S1 brings the rules (no <c>prune</c> of everything, no <c>echo 3</c>, no shell strings);
/// E1.S2 brings the seam and the proof that a refusal prevents the start.
/// </summary>
public interface ICommandPolicy
{
    CommandVerdict Review(IReadOnlyList<string> argv);
}

/// <summary>
/// The policy of this build: everything is allowed, because nothing in this build runs a command.
/// E3.S1 replaces it with the never-list and a property test that no input gets past it.
/// </summary>
public sealed class AllowAllCommandPolicy : ICommandPolicy
{
    public CommandVerdict Review(IReadOnlyList<string> argv) => CommandVerdict.Allowed;
}
