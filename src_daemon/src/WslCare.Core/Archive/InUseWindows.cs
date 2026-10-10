using System.Diagnostics;
using System.Globalization;

using WslCare.Core.Collectors;
using WslCare.Core.Collectors.Procfs;
using WslCare.Core.Mcp;

namespace WslCare.Core.Archive;

/// <summary>Whether the Windows open-file check stalled in THIS process (the E9.S5 gate round, finding 2): once a native question
/// stalls, its worker cannot be stopped, so no later view of the process asks again — every later unit stays.</summary>
public sealed class StallLatch
{
    private volatile bool _stalled;

    /// <summary>The process's own latch — what every real view shares.</summary>
    public static StallLatch Process { get; } = new();

    public bool Stalled => _stalled;

    public void Stall() => _stalled = true;
}

/// <summary>How the Windows view asks (E9.S5 and its gate round, findings 4, 11, 15): each question within <paramref name="Ceiling"/>
/// (<c>archive.inUseScanSeconds</c>), all of them within <paramref name="Budget"/> (the time the caller has), until
/// <paramref name="Token"/> is cancelled.</summary>
public sealed record WindowsAsk(TimeSpan Ceiling, TimeSpan Budget, CancellationToken Token)
{
    public StallLatch Latch { get; init; } = StallLatch.Process;
}

/// <summary>
/// Plan §15r D2.2, E9.S5 — what is in use on the WINDOWS side: the Restart Manager is ASKED, unit by unit, who holds its files
/// (<see cref="IRestartManager"/>, never an open of a file), and the process table says whether Claude Code runs at all.
/// </summary>
/// <remarks>
/// <para><b>Fails closed.</b> A held file keeps its unit; a Restart Manager that could not be asked keeps it; a question past its
/// ceiling keeps it — and, the native worker being unstoppable, every later question in this process (<see cref="StallLatch"/>); a
/// scan past its budget or cancelled keeps every unit not yet asked.</para>
/// <para><b>Claude Code's working folder cannot be read on Windows</b>: while a Claude Code runs there, a Claude Code session moves
/// only when every file of it was untouched for <c>archive.windowsIdleDays</c> (the E9.S5 amendment, owner decision 2026-10-09:
/// <see cref="WindowsIdle"/>, <see cref="InUseView.ClaudeIdle"/>); before it, a live Claude Code kept EVERY Claude Code session in
/// place. Whether one runs is asked again at every question (the gate round, finding 6: phase 2 asks minutes after the selection), and BEFORE the
/// Restart Manager (finding 12). Claude Code runs as <c>claude.exe</c>, or as <c>node.exe</c> with its <c>claude-code</c> package on
/// the command line; a <c>node.exe</c> of this session whose command line cannot be read cannot be told apart, so it counts as running
/// (the own review, 3) — the idle rule then applies as to a known one.</para>
/// <para><b>Not seen</b> (said in <see cref="Note"/>): the Restart Manager leaves out a process it may not query (another account's,
/// an elevated one) without saying so.</para>
/// </remarks>
public static class InUseWindows
{
    private const string ClaudeProgram = "claude";
    private const string NodeProgram = "node";
    private const string ClaudePackage = "claude-code";

    /// <summary>What the view says of itself: what it asks, and what it cannot see.</summary>
    public const string Note = "on Windows the Restart Manager is asked who holds each session's files (it leaves out a process it may not query: another account's, an elevated one); Claude Code's working folder cannot be read here, so while a Claude Code runs only a Claude Code session idle for archive.windowsIdleDays moves";

    /// <summary>A view whose questions each wait at most <paramref name="ceiling"/>, with a latch of its own (a test's).</summary>
    public static InUseView View(IRestartManager restartManager, IWindowsProcessTable processes, TimeSpan ceiling) =>
        View(restartManager, processes, new WindowsAsk(ceiling, TimeSpan.MaxValue, CancellationToken.None) { Latch = new StallLatch() });

    /// <summary>The Windows view: complete as a mechanism, every unit's files asked of <paramref name="restartManager"/>.</summary>
    public static InUseView View(IRestartManager restartManager, IWindowsProcessTable processes, WindowsAsk ask)
    {
        var asker = new Asker(ask);
        return new InUseView(new HashSet<string>(StringComparer.Ordinal), new HashSet<string>(StringComparer.OrdinalIgnoreCase), InUseState.Complete, Note)
        {
            HeldBy = files => asker.Asked(() => restartManager.Holders(files), Said),
            ClaudeRunning = () => asker.Asked(() => ClaudeRunning(processes), static running => running),
            ClaudeOnCommandLine = key => Liveness.ClaudeSessionOf(key) is { Length: > 0 } id ? asker.Asked(() => OnCommandLine(processes, id), static said => said) : string.Empty,
            Bounded = question => asker.Asked(question, static said => said),
        };
    }

    /// <summary>The guards after the live gate, G1 (owner question 1, 2026-10-10): why the session <paramref name="id"/> stays — a live
    /// Claude Code process names it on its command line (<c>--resume</c>, <c>-r</c>, <c>--session-id</c>, a transcript path: the id is
    /// matched anywhere, whatever its case); empty when none does. A positive keep only: an id on no command line proves nothing.</summary>
    public static string OnCommandLine(IWindowsProcessTable processes, string id) =>
        processes.List() is Reading<IReadOnlyList<WindowsProcessEntry>>.Available { Value: var all }
            ? all.Select(p => Naming(processes, p, id)).FirstOrDefault(v => v.Length > 0, string.Empty)
            : string.Empty;

    private static string Naming(IWindowsProcessTable processes, WindowsProcessEntry process, string id) =>
        ClaudeEvidence(process) is { } evidence && processes.CommandLine(process.Pid) is Reading<string>.Available { Value: var line } && Names(line, id, evidence)
            ? Liveness.OnItsCommandLine(process.Pid)
            : string.Empty;

    /// <summary>What a process's line must also carry to be Claude Code: nothing for <c>claude.exe</c>, the package for <c>node.exe</c>
    /// (the guards' own review: a node line naming a transcript path is no Claude Code); <c>null</c> — not found — for any other program.</summary>
    private static string? ClaudeEvidence(WindowsProcessEntry process) => CommandLineText.FileNameOf(process.ExeName) switch
    {
        var program when program.Equals(ClaudeProgram, StringComparison.OrdinalIgnoreCase) => string.Empty,
        var program when program.Equals(NodeProgram, StringComparison.OrdinalIgnoreCase) => ClaudePackage,
        _ => null,
    };

    private static bool Names(string line, string id, string evidence) =>
        line.Contains(id, StringComparison.OrdinalIgnoreCase) && line.Contains(evidence, StringComparison.OrdinalIgnoreCase);

    /// <summary>Why no Claude Code session may move on this side now; empty when no Claude Code runs.</summary>
    public static string ClaudeRunning(IWindowsProcessTable processes) => processes.List() switch
    {
        Reading<IReadOnlyList<WindowsProcessEntry>>.Available { Value: var all } => Running(processes, all, processes.Details(Environment.ProcessId).SessionId),
        Reading<IReadOnlyList<WindowsProcessEntry>>.Unavailable missing => $"the Windows process table could not be read ({missing.Reason}), so whether Claude Code runs is not known; only a Claude Code session idle for archive.windowsIdleDays moves",
        _ => throw new UnreachableException("Reading is a closed set"),
    };

    private static string Running(IWindowsProcessTable processes, IReadOnlyList<WindowsProcessEntry> all, Reading<int> mySession) =>
        all.Select(p => Verdict(processes, p, mySession)).FirstOrDefault(v => v.Length > 0, string.Empty);

    /// <summary>Why <paramref name="process"/> keeps the Claude Code sessions; empty when it is not Claude Code.</summary>
    private static string Verdict(IWindowsProcessTable processes, WindowsProcessEntry process, Reading<int> mySession) =>
        CommandLineText.FileNameOf(process.ExeName) switch
        {
            var program when program.Equals(ClaudeProgram, StringComparison.OrdinalIgnoreCase) => Runs(process),
            var program when program.Equals(NodeProgram, StringComparison.OrdinalIgnoreCase) => NodeVerdict(processes, process, mySession),
            _ => string.Empty,
        };

    /// <summary>A <c>node.exe</c> whose command line names the <c>claude-code</c> package runs Claude Code; one whose command line cannot
    /// be read cannot be told apart — unless it is another session's (another account's), which is not this archive's Claude Code.</summary>
    private static string NodeVerdict(IWindowsProcessTable processes, WindowsProcessEntry process, Reading<int> mySession) => processes.CommandLine(process.Pid) switch
    {
        Reading<string>.Available { Value: var line } => line.Contains(ClaudePackage, StringComparison.OrdinalIgnoreCase) ? Runs(process) : string.Empty,
        Reading<string>.Unavailable missing when InMySession(processes, process, mySession) =>
            string.Create(CultureInfo.InvariantCulture, $"a node.exe of this session (pid {process.Pid}) could not be read ({missing.Reason}), so whether it is Claude Code is not known; only a Claude Code session idle for archive.windowsIdleDays moves"),
        _ => string.Empty,
    };

    /// <summary>Whether <paramref name="process"/> runs in this process's session — or either session is unknown (then it counts).</summary>
    private static bool InMySession(IWindowsProcessTable processes, WindowsProcessEntry process, Reading<int> mySession) =>
        mySession is not Reading<int>.Available { Value: var mine } || processes.Details(process.Pid).SessionId is not Reading<int>.Available { Value: var theirs } || theirs == mine;

    private static string Runs(WindowsProcessEntry process) =>
        string.Create(CultureInfo.InvariantCulture, $"Claude Code runs on Windows ({process.ExeName}, pid {process.Pid}); its working folder cannot be read here, so only a Claude Code session idle for archive.windowsIdleDays moves while it runs");

    /// <summary>Asks one native question at a time, each in a worker it can abandon: within the question's ceiling and what is left of
    /// the budget, never after a stall of this process, never after a cancellation. Every way it does not answer keeps the unit.</summary>
    private sealed class Asker(WindowsAsk ask)
    {
        private readonly Stopwatch _watch = Stopwatch.StartNew();

        public string Asked<T>(Func<T> question, Func<T, string> said)
        {
            if (Stop() is { Length: > 0 } stop)
            {
                return stop;
            }

            var room = Room();
            var running = Task.Run(question, CancellationToken.None);
            return Ended(running, room) ? Answer(running, said) : TimedOut(room);
        }

        private string Stop() =>
            ask.Latch.Stalled ? Stalled(ask.Ceiling)
            : ask.Token.IsCancellationRequested ? Cancelled
            : _watch.Elapsed >= ask.Budget ? Spent
            : string.Empty;

        private TimeSpan Room()
        {
            var left = ask.Budget == TimeSpan.MaxValue ? ask.Ceiling : ask.Budget - _watch.Elapsed;
            return left < ask.Ceiling ? left : ask.Ceiling;
        }

        /// <summary>Whether the question answered within <paramref name="room"/> — a cancellation ends the wait at once.</summary>
        private bool Ended(Task question, TimeSpan room) =>
            WaitHandle.WaitAny([((IAsyncResult)question).AsyncWaitHandle, ask.Token.WaitHandle], room) == 0;

        /// <summary>A question that used its whole ceiling stalled the process's latch; one cut short by the budget or a cancellation did not.</summary>
        private string TimedOut(TimeSpan room)
        {
            if (ask.Token.IsCancellationRequested)
            {
                return Cancelled;
            }

            if (room < ask.Ceiling)
            {
                return Spent;
            }

            ask.Latch.Stall();
            return Stalled(ask.Ceiling);
        }
    }

    /// <summary>An ended question's answer — a fault of the call itself is a question that could not be asked.</summary>
    private static string Answer<T>(Task<T> question, Func<T, string> said) =>
        question.IsCompletedSuccessfully ? said(question.Result) : $"the Windows open-file check could not be asked ({question.Exception?.InnerException?.Message ?? "the question failed"}); it stays until it can";

    private static string Said(RmAnswer answer) => answer switch
    {
        RmAnswer.Free => string.Empty,
        RmAnswer.Held held => $"a file of it is held open by {string.Join(", ", held.Holders)}",
        RmAnswer.Failed failed => $"the Restart Manager could not be asked ({failed.Why}); it stays until it can",
        _ => throw new UnreachableException("RmAnswer is a closed set"),
    };

    private const string Cancelled = "the open-file check was stopped before it asked; what it did not ask may be open, so it stays";

    private const string Spent = "the open-file check passed the time it had; what it did not ask may be open, so it stays";

    private static string Stalled(TimeSpan ceiling) =>
        string.Create(CultureInfo.InvariantCulture, $"the Windows open-file check did not answer within archive.inUseScanSeconds ({ceiling.TotalSeconds:0} s); what it was not asked may be open, so it stays");
}
