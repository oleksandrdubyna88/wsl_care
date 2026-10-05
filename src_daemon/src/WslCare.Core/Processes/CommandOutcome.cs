namespace WslCare.Core.Processes;

/// <summary>A captured stream: what was kept, and whether the cap cut it.</summary>
public sealed record CapturedText(string Text, bool Truncated)
{
    public static readonly CapturedText Empty = new(string.Empty, Truncated: false);
}

/// <summary>
/// How a command ended — a closed set, so every caller's <c>switch</c> is complete by inspection
/// and no caller reads a message substring to find out what happened.
/// </summary>
/// <remarks>A timeout is OUR decision and travels here as <see cref="TimedOut"/>; the caller's
/// cancellation is not an outcome at all — the runner kills the tree and lets
/// <see cref="OperationCanceledException"/> fly, so the two are never confused (reliability rule:
/// a timeout is not a cancellation).</remarks>
public abstract record CommandOutcome
{
    private CommandOutcome()
    {
    }

    /// <summary>The file the launcher started when it was found on the Windows system drive rather than on <c>PATH</c>
    /// (<see cref="ResolvedExecutable.Found.OnTheSystemDrive"/>) — reported beside the bare program the policy judged;
    /// empty otherwise.</summary>
    public string StartedFrom { get; init; } = string.Empty;

    /// <summary>The process ran to its end.</summary>
    public sealed record Exited(int ExitCode, CapturedText Stdout, CapturedText Stderr, TimeSpan Elapsed) : CommandOutcome;

    /// <summary>The ceiling passed; the whole process tree was killed. The streams hold what arrived before.</summary>
    public sealed record TimedOut(CapturedText Stdout, CapturedText Stderr, TimeSpan Timeout) : CommandOutcome;

    /// <summary>The operating system would not start it — no such executable, permission denied.</summary>
    public sealed record FailedToStart(string Reason) : CommandOutcome;

    /// <summary>The <see cref="ICommandPolicy"/> said no; nothing was started.</summary>
    public sealed record Refused(string Reason) : CommandOutcome;
}
