using WslCare.Core.Collectors;
using WslCare.Core.Actions.Engine;
using WslCare.Core.Config;
using WslCare.Core.Processes;

namespace WslCare.Core.Scheduler;

/// <summary>
/// WR-D3 (PLAN_windows_runner.md §3.2, §3.3 point 2): what <c>tasks run &lt;task&gt;</c> starts under the supervisor — a CLOSED table
/// keyed by the task, never free text. The worker is this process's OWN executable (the staged copy the task names), and its
/// arguments are spelt here and nowhere else: the supervisor spells <c>--timer</c>, the extension never does.
/// </summary>
/// <remarks>
/// <list type="table">
/// <item><term><c>watch</c></term><description><c>&lt;self&gt; watch --timer</c> (+ <c>--json</c> when the supervisor was asked for it — the
/// task's action always is), deadline <c>mcpWatchdog.runLimitMinutes</c>.</description></item>
/// <item><term><c>archive</c></term><description>the archive body (WR-D5, <see cref="ArchiveBody"/>, §3.5): <c>&lt;self&gt; archive preview</c> while the
/// timer's dry-run window is open, <c>&lt;self&gt; archive run</c> once it is not (+ <c>--json</c>), deadline <c>timer.runLimitMinutes</c>. The
/// dry decision is taken only for this task — a <c>watch</c> never starts the archive's week.</description></item>
/// <item><term><c>cleanup</c></term><description>the Windows <c>%TEMP%</c> cleanup (WR-T2, §3.12): <c>&lt;self&gt; act A23 --timer --confirm</c> (+ <c>--json</c>),
/// deadline <c>timer.runLimitMinutes</c>. <c>--timer</c> so A23's own dry week and its <c>auto</c> switch gate the run; <c>--confirm</c>
/// because the parser refuses an <c>act</c> without a mode. The dry decision lives in the WORKER (the engine, on A23's own stamp); this
/// table never calls <c>decide</c>, so the supervisor cannot start any week.</description></item>
/// </list>
/// </remarks>
public static class TaskWorkers
{
    private const string JsonFlag = "--json";

    /// <summary>One entry of the closed table: a task, its worker's arguments (a function of the archive body's choice, which only the
    /// archive entry ever calls), and the key its deadline is read from. The deadline's duration and its key come from this ONE entry
    /// (the coai code round on WR-D3, finding 2), so what a run says about its deadline can never name another key.</summary>
    private sealed record Entry(WindowsTask Task, Func<Func<DryRunDecision>, IReadOnlyList<string>> Arguments, ConfigKey.IntKey Deadline);

    private static readonly IReadOnlyList<Entry> Table =
    [
        new(WindowsTask.Watch, static _ => ["watch", "--timer"], ConfigKeys.McpWatchdog.RunLimitMinutes),
        new(WindowsTask.Archive, static decide => ArchiveBody.ArgumentsFor(decide()), ConfigKeys.Timer.RunLimitMinutes),
        new(WindowsTask.Cleanup, static _ => ["act", Actions.WindowsTemp.WindowsTempAction.IdText, "--timer", "--confirm"], ConfigKeys.Timer.RunLimitMinutes),
    ];

    /// <summary>The launch of <paramref name="task"/>'s worker, or why there is none.</summary>
    /// <param name="self">This process's own executable path — the worker is always the same binary.</param>
    /// <param name="json">Whether the supervisor was asked for <c>--json</c>; the worker then answers in JSON on the same stdout.</param>
    /// <param name="decide">The archive body's dry-run decision; asked for ONLY by the archive task (taking it may write the first-week stamp).
    /// The decision → argv mapping stays here in Core (<see cref="ArchiveBody.ArgumentsFor"/>), so no caller can hand this table free argv.</param>
    public static Reading<WorkerLaunch> For(WindowsTask task, string self, bool json, EffectiveConfig config, Func<DryRunDecision> decide) => EntryOf(task) switch
    {
        _ when !Path.IsPathFullyQualified(self) => Reading.Missing<WorkerLaunch>($"this process's own path could not be read as an absolute path (\"{self}\"), so no worker can be named; nothing was started"),
        var entry => Reading.Of(new WorkerLaunch(self, [.. entry.Arguments(decide), .. Json(json)], TimeSpan.FromMinutes(config.Int(entry.Deadline)), entry.Deadline.Name)),
    };

    /// <summary>The deadline's key of each task, from its table entry.</summary>
    public static ConfigKey.IntKey DeadlineKey(WindowsTask task) => EntryOf(task).Deadline;

    private static Entry EntryOf(WindowsTask task) =>
        Table.FirstOrDefault(e => e.Task == task) ?? throw new ArgumentOutOfRangeException(nameof(task), task.Name, $"not a task of this plan; one of: {string.Join(", ", WindowsTask.All)}");

    private static IReadOnlyList<string> Json(bool json) => json ? [JsonFlag] : [];
}
