namespace WslCare.Core.Scheduler;

/// <summary>
/// One of the two per-user scheduled tasks of the Windows binary (PLAN_windows_runner.md §3.4) — a CLOSED set: the constructor
/// is private, so a task name a person typed is either one of <see cref="All"/> or refused naming them.
/// </summary>
public sealed record WindowsTask
{
    /// <summary>The Task Scheduler folder both tasks live in.</summary>
    public const string Folder = "\\wsl-care";

    private WindowsTask(string name, string dryWindowStamp = "")
    {
        Name = name;
        DryWindowStamp = dryWindowStamp;
    }

    /// <summary>The name of the task's OWN dry-run week stamp (<c>first-timer-run-&lt;name&gt;.json</c>), or empty — the watch and the
    /// archive read the common one. The cleanup task's is A23's (WR-T2: the week that gates DELETION is A23's, not the watch's).</summary>
    public string DryWindowStamp { get; }

    /// <summary>The task's name in its folder, and the word <c>tasks render</c> / <c>tasks run</c> take.</summary>
    public string Name { get; }

    /// <summary>The task's full path in Task Scheduler (<c>\wsl-care\watch</c>).</summary>
    public string TaskPath => $"{Folder}\\{Name}";

    /// <summary><c>\wsl-care\watch</c>: the Windows watch, every <c>mcpWatchdog.periodMinutes</c>.</summary>
    public static readonly WindowsTask Watch = new("watch");

    /// <summary><c>\wsl-care\archive</c>: the Windows side's AI-session archive, every <c>windowsTasks.archiveEveryHours</c>.</summary>
    public static readonly WindowsTask Archive = new("archive");

    /// <summary><c>\wsl-care\cleanup</c>: the Windows <c>%TEMP%</c> cleanup (A23, WR-T2), every <c>windowsTasks.cleanupEveryHours</c>.</summary>
    public static readonly WindowsTask Cleanup = new("cleanup", Actions.WindowsTemp.WindowsTempAction.StampName);

    /// <summary>Every task, in the order a help text names them. Declared after the three on purpose: static fields initialise in
    /// textual order.</summary>
    public static IReadOnlyList<WindowsTask> All { get; } = [Watch, Archive, Cleanup];

    /// <summary>The task of that name, or <c>null</c> — a name is user input and an unknown one is an expected answer.</summary>
    public static WindowsTask? Find(string name) => All.FirstOrDefault(t => string.Equals(t.Name, name, StringComparison.Ordinal));

    public override string ToString() => Name;
}
