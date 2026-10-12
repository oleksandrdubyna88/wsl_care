using WslCare.Core.Collectors;
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
/// <item><term><c>archive</c></term><description>not built in this version: the archive body (dry → <c>archive preview</c>, real →
/// <c>archive run</c>, §3.5) is WR-D5's. Refused, naming that, before any job or process exists; its deadline will be
/// <c>timer.runLimitMinutes</c>.</description></item>
/// </list>
/// </remarks>
public static class TaskWorkers
{
    /// <summary>What <c>tasks run archive</c> answers until WR-D5 builds the archive body.</summary>
    public const string ArchiveNotBuilt = "the archive task's body (a dry week previews, a real run archives; PLAN_windows_runner.md §3.5) is not built in this version (WR-D5); nothing was started";

    private const string JsonFlag = "--json";

    /// <summary>The launch of <paramref name="task"/>'s worker, or why there is none.</summary>
    /// <param name="self">This process's own executable path — the worker is always the same binary.</param>
    /// <param name="json">Whether the supervisor was asked for <c>--json</c>; the worker then answers in JSON on the same stdout.</param>
    public static Reading<WorkerLaunch> For(WindowsTask task, string self, bool json, EffectiveConfig config) => task switch
    {
        _ when !Path.IsPathFullyQualified(self) => Reading.Missing<WorkerLaunch>($"this process's own path could not be read as an absolute path (\"{self}\"), so no worker can be named; nothing was started"),
        _ when task == WindowsTask.Watch => Reading.Of(new WorkerLaunch(self, ["watch", "--timer", .. Json(json)], TimeSpan.FromMinutes(config.Int(ConfigKeys.McpWatchdog.RunLimitMinutes)))),
        _ when task == WindowsTask.Archive => Reading.Missing<WorkerLaunch>(ArchiveNotBuilt),
        _ => throw new ArgumentOutOfRangeException(nameof(task), task.Name, $"not a task of this plan; one of: {string.Join(", ", WindowsTask.All)}"),
    };

    /// <summary>The deadline's key of each task — what a refusal and the timeout line name.</summary>
    public static ConfigKey.IntKey DeadlineKey(WindowsTask task) => task == WindowsTask.Watch ? ConfigKeys.McpWatchdog.RunLimitMinutes : ConfigKeys.Timer.RunLimitMinutes;

    private static IReadOnlyList<string> Json(bool json) => json ? [JsonFlag] : [];
}
