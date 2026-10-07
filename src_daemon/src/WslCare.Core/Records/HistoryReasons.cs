namespace WslCare.Core.Records;

/// <summary>One reason a reader may meet on a history line, by the writer that puts it there.</summary>
/// <param name="Writer">The daemon type and member that writes it.</param>
/// <param name="Prefix">How the reason begins — every reason that writer composes starts with it.</param>
public sealed record ReasonPrefix(string Writer, string Prefix);

/// <summary>
/// The reasons that tell a reader a history line is NOT a full check's when the line carries no <c>kind</c> — a daemon older
/// than plan §15o, or a line whose kind cannot be known (an unusable request, an orphan whose detail cannot be read). A line
/// WITH a kind is told by its kind alone; these prefixes are the fallback for one without. Emitted as
/// <c>contracts/history-reasons.json</c> (held equal by the scenario harness's <c>ContractFilesTests</c>), so the extension's
/// follower reads the daemon's own words instead of keeping a copy (coai plan round on §15o, #1).
/// </summary>
public static class HistoryReasons
{
    /// <summary>How the line of an unusable request's run begins (<c>RequestSweep.Unusable</c> writes it) — a contract
    /// (<c>contracts/history-reasons.json</c>, plan §15o): a reader tells this kind-less line by it. Here, beside the list it
    /// belongs to, so the records layer depends on no engine type (PR #16 retro round, gate G1).</summary>
    public const string UnusableRequestPrefix = "refused: its request could not be used";

    /// <summary>How the reason of every OTHER line that carries no kind begins (PR #44 gate round): the swept line of a dead
    /// <c>running.json</c> holder whose kind is not known (an unknown kind, or an older file that is not the exact full-check
    /// shape — <c>RunningSweep.SweepDead</c>), and the terminal line of a request whose kind is not known
    /// (<c>RunRequestFile.TerminalLine</c>). So every kind-less writer is marked and no reader takes such a line for a full check.</summary>
    public const string KindNotKnownPrefix = "its kind is not known";

    public static IReadOnlyList<ReasonPrefix> NotAFullCheckWithoutKind { get; } =
    [
        new("RequestSweep.Unusable", UnusableRequestPrefix),
        new("RunReconcile.InterruptedLine", RunReconcile.InterruptedReason),
        new("RunReconcile.InterruptedLine", RunReconcile.UnreadableDetailReason),
        new("RunningSweep.SweepDead, RunRequestFile.TerminalLine", KindNotKnownPrefix),
    ];

    /// <summary>A kind-less writer's reason: <see cref="KindNotKnownPrefix"/> before it, so the line is marked.</summary>
    public static string KindNotKnown(string reason) => $"{KindNotKnownPrefix}; {reason}";

    /// <summary>Whether <paramref name="reason"/> begins with one of <see cref="NotAFullCheckWithoutKind"/>.</summary>
    public static bool MarksNotAFullCheck(string reason) =>
        NotAFullCheckWithoutKind.Any(p => reason.StartsWith(p.Prefix, StringComparison.Ordinal));
}
