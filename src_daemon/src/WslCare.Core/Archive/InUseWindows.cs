using System.Globalization;

using WslCare.Core.Collectors;
using WslCare.Core.Mcp;

namespace WslCare.Core.Archive;

/// <summary>
/// Plan §15r D2.2, E9.S5 — what is in use on the WINDOWS side: the Restart Manager is ASKED, unit by unit, who holds its files
/// (<see cref="IRestartManager"/>, never an open of a file), and the process table says whether Claude Code runs at all.
/// </summary>
/// <remarks>
/// <para><b>Fails closed.</b> A held file keeps its unit, naming the holder; a Restart Manager that could not be asked keeps it, naming
/// why; a question that does not answer within <c>archive.inUseScanSeconds</c> keeps it — and every question after it, since a
/// stalled Restart Manager answers no later one either (the plan round's finding 5: phase 2's re-check is bounded the same way).</para>
/// <para><b>Claude Code's working directory is not read on Windows</b> (no supported call reads another process's current folder): a
/// live Claude Code there keeps EVERY Claude Code session in place, and a process table that cannot be read does too (the plan
/// round's findings 0 and 3). Claude Code runs as <c>claude.exe</c>, or as <c>node.exe</c> with its <c>claude-code</c> package on the
/// command line.</para>
/// </remarks>
public static class InUseWindows
{
    private const string ClaudeProgram = "claude";
    private const string NodeProgram = "node";
    private const string ClaudePackage = "claude-code";

    /// <summary>What the view says of itself: what it asks, and what it cannot see.</summary>
    public const string Note = "on Windows the Restart Manager is asked who holds each session's files; Claude Code's working folder cannot be read here, so a live Claude Code keeps every Claude Code session in place";

    /// <summary>The Windows view: complete as a mechanism, every unit's files asked of <paramref name="restartManager"/>.</summary>
    public static InUseView View(IRestartManager restartManager, IWindowsProcessTable processes, TimeSpan ceiling) =>
        new(new HashSet<string>(StringComparer.Ordinal), new HashSet<string>(StringComparer.OrdinalIgnoreCase), InUseState.Complete, Note)
        {
            HeldBy = new Asker(restartManager, ceiling).Held,
            ClaudeRunning = ClaudeRunning(processes),
        };

    /// <summary>Why no Claude Code session may move on this side now; empty when no Claude Code runs.</summary>
    public static string ClaudeRunning(IWindowsProcessTable processes) => processes.List() switch
    {
        Reading<IReadOnlyList<WindowsProcessEntry>>.Available { Value: var all } => Running(processes, all),
        Reading<IReadOnlyList<WindowsProcessEntry>>.Unavailable missing => $"the Windows process table could not be read ({missing.Reason}), so whether Claude Code runs is not known; no Claude Code session moves",
        _ => throw new System.Diagnostics.UnreachableException("Reading is a closed set"),
    };

    private static string Running(IWindowsProcessTable processes, IReadOnlyList<WindowsProcessEntry> all) =>
        all.FirstOrDefault(p => IsClaude(processes, p)) is { } claude
            ? string.Create(CultureInfo.InvariantCulture, $"Claude Code runs on Windows ({claude.ExeName}, pid {claude.Pid}); its working folder cannot be read here, so no Claude Code session moves while it runs")
            : string.Empty;

    private static bool IsClaude(IWindowsProcessTable processes, WindowsProcessEntry process) =>
        WindowsMcpCatalogue.ProgramOf(process.ExeName) switch
        {
            var program when program.Equals(ClaudeProgram, StringComparison.OrdinalIgnoreCase) => true,
            var program when program.Equals(NodeProgram, StringComparison.OrdinalIgnoreCase) => RunsClaude(processes.CommandLine(process.Pid)),
            _ => false,
        };

    /// <summary>A <c>node.exe</c> whose command line names the <c>claude-code</c> package; one whose command line cannot be read is
    /// another account's (this account's own processes are always readable through a query-only handle), so it is not counted.</summary>
    private static bool RunsClaude(Reading<string> commandLine) =>
        commandLine is Reading<string>.Available { Value: var line } && line.Contains(ClaudePackage, StringComparison.OrdinalIgnoreCase);

    /// <summary>Asks the Restart Manager, each question bounded; once one stalled, every later one is answered at once as stalled.</summary>
    private sealed class Asker(IRestartManager restartManager, TimeSpan ceiling)
    {
        private volatile bool _stalled;

        public string Held(IReadOnlyList<string> files)
        {
            if (_stalled)
            {
                return Stalled(ceiling);
            }

            var question = Task.Run(() => restartManager.Holders(files), CancellationToken.None);
            return ((IAsyncResult)question).AsyncWaitHandle.WaitOne(ceiling) ? Said(Result(question)) : Stall();
        }

        private string Stall()
        {
            _stalled = true;
            return Stalled(ceiling);
        }
    }

    /// <summary>An ended question's answer — a fault of the call itself is a Restart Manager that could not be asked.</summary>
    private static RmAnswer Result(Task<RmAnswer> question) =>
        question.IsCompletedSuccessfully ? question.Result : new RmAnswer.Failed(question.Exception?.InnerException?.Message ?? "the question failed");

    private static string Said(RmAnswer answer) => answer switch
    {
        RmAnswer.Free => string.Empty,
        RmAnswer.Held held => $"a file of it is held open by {string.Join(", ", held.Holders)}",
        RmAnswer.Failed failed => $"the Restart Manager could not be asked ({failed.Why}); it stays until it can",
        _ => throw new System.Diagnostics.UnreachableException("RmAnswer is a closed set"),
    };

    private static string Stalled(TimeSpan ceiling) =>
        string.Create(CultureInfo.InvariantCulture, $"the Restart Manager did not answer within archive.inUseScanSeconds ({ceiling.TotalSeconds:0} s); what it was not asked may be open, so it stays");
}
