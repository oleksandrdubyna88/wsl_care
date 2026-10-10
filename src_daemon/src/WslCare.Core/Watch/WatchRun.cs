using System.Globalization;
using System.Text.Json;

using WslCare.Core.Actions;
using WslCare.Core.Actions.Engine;
using WslCare.Core.Actions.Suspects;
using WslCare.Core.Collectors;
using WslCare.Core.Collectors.Procfs;
using WslCare.Core.Config;
using WslCare.Core.Files;
using WslCare.Core.Files.Deletion;
using WslCare.Core.Hosting;
using WslCare.Core.Json;
using WslCare.Core.Mcp;
using WslCare.Core.Processes;
using WslCare.Core.Records;

namespace WslCare.Core.Watch;

/// <summary>Everything one watch run reaches the machine through (plan E14 S2b).</summary>
/// <param name="Timer">The watch timer's mark (<c>watch --timer</c>): the only watch that may let A19 act.</param>
public sealed record WatchContext(
    IHostPaths Paths,
    IFileSystem Files,
    ICommandRunner Commands,
    TimeProvider Clock,
    IHostProbe Probe,
    ConfigLoadResult Loaded,
    IProcessTable Processes,
    int ProcessId,
    ActionRegistry Actions,
    bool Timer)
{
    /// <summary>How A19 signals a process — the real sender only inside the distro, outside a sandbox.</summary>
    public IProcessSignals Signals { get; init; } = RefusingProcessSignals.NotWired;

    /// <summary>The MCP servers' CPU window, when an instance has no baseline.</summary>
    public Func<TimeSpan, CancellationToken, Task> Wait { get; init; } = static (delay, token) => Task.Delay(delay, token);

    /// <summary>What cancelled the run, in words.</summary>
    public Func<string> InterruptCause { get; init; } = static () => "a signal";
}

/// <summary>How one watch run ended.</summary>
public enum WatchOutcome
{
    /// <summary>Another run held the run lock: nothing was sampled, nothing done.</summary>
    Busy,

    /// <summary>The evidence was sampled; A19 did not act (why is the result's reason).</summary>
    Sampled,

    /// <summary>The evidence was sampled and A19 ran as a recorded <c>act</c>.</summary>
    Acted,
}

/// <summary>One watch run: how it ended and why, whether root's CPU ledger and the agents' CPU history were recorded (empty
/// <paramref name="History"/> = recorded), which processes A19 was asked to stop, and the act run when there was one.</summary>
public sealed record WatchResult(WatchOutcome Outcome, string Reason, McpCpuBaseline Ledger, string History)
{
    /// <summary>The processes (<c>pid:start</c>) this run handed to A19 — none unless it acted.</summary>
    public IReadOnlyList<string> Tried { get; init; } = [];

    /// <summary>The act run, when A19 ran (one) — none otherwise.</summary>
    public IReadOnlyList<ActResult> Acts { get; init; } = [];
}

/// <summary>
/// The watch timer's run (plan E14 S2b; <c>wsl-care watch --timer</c>, every <c>mcpWatchdog.periodMinutes</c>). Under THE run lock,
/// taken without waiting: one probe sample, root's MCP CPU ledger written (A19's busy evidence: an interval reading every few
/// minutes, which the 4-hour timer never gives) and the agents' CPU history recorded (A19's, A18's and A3's idle evidence). Then —
/// only from the timer, not observe-only, <c>auto.A19</c> on and the timer's dry-run decision real — A19's live preview; the targets
/// this watch has not tried before are written to <see cref="WatchTries"/> and only THEN handed to the engine as a recorded
/// <c>act</c> bound to exactly them. A watch that stops nothing writes no history line; during a dry run it only samples, and the
/// full run's pass keeps recording what A19 would do.
/// </summary>
public static class WatchRun
{
    private static readonly ActionId A19 = ActionId.Find("A19") ?? throw new InvalidOperationException("this build holds no A19");

    /// <summary>The actions the watch lets act, in this order (plan E14 S7b.2: A21 after A19) — each by its own <c>auto</c> switch,
    /// as ONE recorded act bound to the targets this watch has not tried before.</summary>
    private static readonly IReadOnlyList<ActionId> Acting =
        [A19, ActionId.Find("A21") ?? throw new InvalidOperationException("this build holds no A21")];

    public const string NotTheDistro = "the watch is the distro's: the Windows binary keeps no MCP CPU ledger";

    public const string NoTimer = "without --timer the watch records the evidence only; A19 acts from the watch timer (wsl-care-watch.timer)";

    public static async Task<WatchResult> RunAsync(WatchContext c, CancellationToken cancellationToken)
    {
        if (c.Paths is not LinuxHostPaths linux)
        {
            return new WatchResult(WatchOutcome.Sampled, NotTheDistro, McpCpuBaseline.NotRecorded(string.Empty, NotTheDistro), NotTheDistro);
        }

        var sampled = await SampleUnderLockAsync(c, linux, cancellationToken).ConfigureAwait(false);
        return sampled.Busy.Length > 0
            ? new WatchResult(WatchOutcome.Busy, sampled.Busy, McpCpuBaseline.NotRecorded(string.Empty, sampled.Busy), sampled.Busy)
            : await ActAsync(c, linux, sampled, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>What the sampling under the lock produced — or why it did not run (another run held the lock).</summary>
    private sealed record Sampled(string Busy, McpCpuBaseline Ledger, string History, IReadOnlyList<ProcessEntry> Processes);

    private static async Task<Sampled> SampleUnderLockAsync(WatchContext c, LinuxHostPaths linux, CancellationToken cancellationToken)
    {
        switch (await RunLock.TakeAsync(c.Paths, c.Files, TimeSpan.Zero, cancellationToken).ConfigureAwait(false))
        {
            case ExclusiveLock.Busy busy:
                return new Sampled($"another run holds the run lock (a full run or an act): {busy.Reason}", McpCpuBaseline.NotRecorded(string.Empty, busy.Reason), string.Empty, []);
            case ExclusiveLock.Held held:
                using (held.Handle)
                {
                    return await SampleAsync(c, linux, cancellationToken).ConfigureAwait(false);
                }

            default:
                throw new System.Diagnostics.UnreachableException("ExclusiveLock is a closed set");
        }
    }

    /// <summary>One probe sample; root's ledger written; the agents' CPU history recorded.</summary>
    private static async Task<Sampled> SampleAsync(WatchContext c, LinuxHostPaths linux, CancellationToken cancellationToken)
    {
        var sample = c.Probe.Sample(cancellationToken);
        var place = McpCpuLedgerPlace.ForCollect(c.Paths, mayRecord: true, string.Empty);
        var mcp = await McpSampling.SampleAsync(c.Paths, c.Files, c.Clock, c.Wait, place, sample, c.Loaded.Config, cancellationToken).ConfigureAwait(false);
        var ledger = mcp is Reading<McpSample>.Available { Value: var s } ? s.Baseline : McpCpuBaseline.NotRecorded(place.FileOrEmpty, mcp.ReasonOrEmpty);
        var processes = sample.Vm.Bind(vm => vm.Processes);
        var history = processes is Reading<ProcessSnapshot>.Available { Value: var snapshot }
            ? AgentCpuHistory.Record(linux, c.Files, snapshot.All, SampleTime.Of(c.Clock), c.Loaded.Config)
            : $"the process table could not be read: {processes.ReasonOrEmpty}";
        return new Sampled(string.Empty, ledger, history, processes.Map(p => p.All).ValueOr([]));
    }

    /// <summary>A19 from the watch, when every gate is open — the targets not tried before, written first, then the recorded act.</summary>
    private static async Task<WatchResult> ActAsync(WatchContext c, LinuxHostPaths linux, Sampled sampled, CancellationToken cancellationToken)
    {
        var sampledOnly = new WatchResult(WatchOutcome.Sampled, string.Empty, sampled.Ledger, sampled.History);
        if (WhyNotAct(c) is { Length: > 0 } why)
        {
            return sampledOnly with { Reason = why };
        }

        var engine = new ActionEngine(new EngineContext(c.Paths, c.Files, c.Commands, c.Clock, c.Probe, c.Loaded, c.Processes, c.ProcessId, c.Actions)
        {
            Signals = c.Signals,
            InterruptCause = c.InterruptCause,
            Wait = c.Wait,
        });
        var acting = Enabled(c);
        var targets = await TargetsAsync(engine, acting, cancellationToken).ConfigureAwait(false);
        var boot = BootIdentity.Read(linux, c.Files);
        var tried = WatchTries.Read(c.Paths, c.Files);
        var before = tried.BootId == boot ? tried.Tried : [];
        List<string> fresh = [.. targets.Where(t => !before.Contains(t, StringComparer.Ordinal))];
        return fresh.Count == 0
            ? sampledOnly with { Reason = Off(c) + NothingNew(acting, targets) }
            : await TryAsync(c, engine, new Tries(boot, before, fresh, sampled.Processes, acting), sampledOnly, cancellationToken).ConfigureAwait(false);
    }

    private static string NothingNew(IReadOnlyList<ActionId> acting, IReadOnlyList<string> targets) =>
        targets.Count == 0 ? $"{Names(acting)} has no target" : $"{Names(acting)}'s {targets.Count} target(s) were already tried by the watch; the full run's pass may try again";

    private static string Names(IReadOnlyList<ActionId> ids) => string.Join(" and ", ids.Select(id => id.Text));

    /// <summary>The watched actions whose own switch is on and that this build holds, in <see cref="Acting"/>'s order.</summary>
    private static List<ActionId> Enabled(WatchContext c) =>
        [.. Acting.Where(id => c.Actions.Find(id) is not null && id.Timer is TimerSwitch.Auto auto && c.Loaded.Config.Bool(auto.Key))];

    /// <summary>Which watched switches are off, said first (empty when none is).</summary>
    private static string Off(WatchContext c) =>
        string.Concat(Acting.Except(Enabled(c)).Select(id => $"{(id.Timer is TimerSwitch.Auto auto ? auto.Key.Name : id.Text)} is off: the watch does not let {id.Text} act; "));

    /// <summary>What one try is made of: the boot, what was tried before, what is new, the processes alive now, and the actions.</summary>
    private sealed record Tries(string Boot, IReadOnlyList<string> Before, IReadOnlyList<string> Fresh, IReadOnlyList<ProcessEntry> Processes, IReadOnlyList<ActionId> Acting);

    /// <summary>The tries written, then the recorded act bound to exactly the new targets — and the tries given back when the act
    /// never ran (another run took the lock in between, a wedged or unreadable running state), so a lost race uses up nothing.</summary>
    private static async Task<WatchResult> TryAsync(WatchContext c, ActionEngine engine, Tries tries, WatchResult sampledOnly, CancellationToken cancellationToken)
    {
        // coai plan round 2026-10-09, finding 1: the tries are on disk BEFORE the act, so a watch killed between the two never
        // tries the same process again.
        if (Write(c, tries.Boot, [.. tries.Before, .. tries.Fresh], tries.Processes) is { Length: > 0 } unwritten)
        {
            return sampledOnly with { Reason = $"{Names(tries.Acting)} was not run: the watch could not record what it tries ({unwritten}), so it could not keep from trying them again" };
        }

        var act = await engine.ExecuteAsync(new ActRequest(tries.Acting, RunTrigger.Timer, Execute: true) { ShownProcesses = ShownList.Of(tries.Fresh) }, cancellationToken).ConfigureAwait(false);
        if (act is not ActResult.Done)
        {
            // coai code round 2026-10-09, finding 2: a give-back that cannot be written is said — those processes stay "tried".
            return sampledOnly with { Reason = $"{Names(tries.Acting)} did not run: {NotRun(act)}; {GiveBack(Write(c, tries.Boot, tries.Before, tries.Processes))}" };
        }

        return sampledOnly with
        {
            Outcome = WatchOutcome.Acted,
            Reason = string.Create(CultureInfo.InvariantCulture, $"{Off(c)}{Names(tries.Acting)} ran on {tries.Fresh.Count} process(es) the watch had not tried"),
            Tried = tries.Fresh,
            Acts = [act],
        };
    }

    private static string Write(WatchContext c, string boot, IReadOnlyList<string> tried, IReadOnlyList<ProcessEntry> processes) =>
        WatchTries.Write(c.Paths, c.Files, new WatchTriesFile(Core.SchemaVersion.Current, boot, Live(tried, processes, c.Loaded.Config)));

    private static string GiveBack(string unwritten) => unwritten.Length == 0
        ? "the watch tries again next time"
        : $"the tries could not be given back ({unwritten}), so the watch does not try those processes again; the full run's pass still may";

    private static string NotRun(ActResult act) => act switch
    {
        ActResult.Busy busy => busy.Reason,
        ActResult.Wedged wedged => wedged.Reason,
        ActResult.StateUnreadable unreadable => unreadable.Reason,
        _ => act.GetType().Name,
    };

    /// <summary>Why the watch does not let A19 act this time; empty when every gate is open.</summary>
    private static string WhyNotAct(WatchContext c) => c switch
    {
        { Timer: false } => NoTimer,
        { Loaded.IsObserveOnly: true } => ActionEngine.ObserveOnlyReason,
        { Loaded.UserLayerUnread: true } => $"{c.Loaded.UserLayerSkipped}; the watch lets A19 act only while every switch can be seen",
        _ when Enabled(c).Count == 0 => $"{Off(c)}so no watched action may act",
        _ => DryReason(c),
    };

    /// <summary>The timer's dry-run decision, the same two locks as the full run's; empty when it says real.</summary>
    private static string DryReason(WatchContext c) =>
        DryRunWindow.Decide(RunTrigger.Timer, c.Loaded.Config, c.Paths, c.Files, c.Clock.GetUtcNow()) is { DryRun: true } dry
            ? $"a dry run ({dry.Reason}): the watch only samples, and the full run's pass records what A19 would do"
            : string.Empty;

    /// <summary>The live targets of <paramref name="acting"/>, as <c>pid:start</c> (each action's keys are its own processes).</summary>
    private static async Task<IReadOnlyList<string>> TargetsAsync(ActionEngine engine, IReadOnlyList<ActionId> acting, CancellationToken cancellationToken) =>
        await engine.PreviewAsync(new ActRequest(acting, RunTrigger.Timer, Execute: false), cancellationToken).ConfigureAwait(false) is ActResult.Previewed previewed
            ? [.. previewed.Actions.SelectMany(a => a.Preview?.Targets ?? []).Select(t => SuspectSignals.Shown(t.Key)).Distinct(StringComparer.Ordinal)]
            : [];

    /// <summary>The tries still alive in <paramref name="processes"/> (the same pid AND start), the newest kept up to
    /// <c>mcpServers.maxInstances</c>.</summary>
    private static List<string> Live(IReadOnlyList<string> tried, IReadOnlyList<ProcessEntry> processes, EffectiveConfig config)
    {
        var alive = processes.Select(p => p.StartTicks is Reading<long>.Available { Value: var start } ? string.Create(CultureInfo.InvariantCulture, $"{p.Pid}:{start}") : string.Empty)
            .Where(k => k.Length > 0)
            .ToHashSet(StringComparer.Ordinal);
        return [.. tried.Where(alive.Contains).Distinct(StringComparer.Ordinal).Reverse().Take(config.Int(ConfigKeys.McpServers.MaxInstances)).Reverse()];
    }
}

/// <summary><c>{state}/mcp-watch.json</c> (plan E14 S2b): the processes (<c>pid:start</c>) the watch has handed to A19 in this boot.</summary>
public sealed record WatchTriesFile(int SchemaVersion, string BootId, IReadOnlyList<string> Tried)
{
    public static readonly WatchTriesFile Empty = new(Core.SchemaVersion.Current, string.Empty, []);
}

/// <summary>The watch's own memory of what it tried (plan E14 S2b): root's state, written atomically, read back with the state
/// rules; a missing, unreadable or malformed file is "nothing tried yet".</summary>
public static class WatchTries
{
    public const string FileName = "mcp-watch.json";

    public static string File(IHostPaths paths) => paths.Rules.Join(paths.StateDirectory, FileName);

    public static WatchTriesFile Read(IHostPaths paths, IFileSystem files) =>
        files.ReadStateFile(File(paths), RootFileCaps.State) is FileReadResult.Content content ? Parse(content.Bytes) : WatchTriesFile.Empty;

    private static WatchTriesFile Parse(byte[] bytes)
    {
        try
        {
            return JsonSerializer.Deserialize(bytes, WslCareJsonContext.Compact.WatchTriesFile) is { BootId.Length: > 0, Tried: not null } file ? file : WatchTriesFile.Empty;
        }
        catch (JsonException)
        {
            return WatchTriesFile.Empty;
        }
    }

    /// <summary>Empty when written; otherwise why not.</summary>
    public static string Write(IHostPaths paths, IFileSystem files, WatchTriesFile tries)
    {
        try
        {
            files.CreateDirectory(paths.StateDirectory);
            var json = JsonSerializer.SerializeToUtf8Bytes(tries, WslCareJsonContext.Compact.WatchTriesFile);
            return files.WritePrivateFileAtomically(File(paths), json, new DeletionScope(paths.StateDirectory, "mcp-watch")) is DeletionVerdict.Refused refused ? refused.Reason : string.Empty;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return e.Message;
        }
    }
}

/// <summary>The answer of <c>watch --json</c> (plan E14 S2b).</summary>
/// <param name="Outcome"><c>busy</c>, <c>sampled</c> or <c>acted</c>.</param>
/// <param name="LedgerReason">Why root's CPU ledger was not recorded; absent when it was.</param>
/// <param name="HistoryReason">Why the agents' CPU history was not recorded; absent when it was.</param>
/// <param name="Act">The act run A19 made, as <c>act --json</c> answers it; absent when it did not act.</param>
public sealed record WatchReport(int SchemaVersion, string Outcome, string Reason, bool LedgerRecorded, string? LedgerReason, string? HistoryReason, IReadOnlyList<string> Tried, ActReport? Act)
{
    public static WatchReport From(WatchResult result) => new(
        Core.SchemaVersion.Current,
        result.Outcome switch
        {
            WatchOutcome.Busy => "busy",
            WatchOutcome.Acted => "acted",
            _ => "sampled",
        },
        result.Reason,
        result.Ledger.Recorded,
        result.Ledger.Recorded ? null : result.Ledger.Reason,
        result.History.Length == 0 ? null : result.History,
        result.Tried,
        result.Acts.Count == 0 ? null : ActReport.From(result.Acts[0]));
}
