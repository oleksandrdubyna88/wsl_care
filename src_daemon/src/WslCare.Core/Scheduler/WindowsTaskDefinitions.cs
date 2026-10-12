using System.Globalization;

using WslCare.Core.Config;

namespace WslCare.Core.Scheduler;

/// <summary>Who a task runs as and what it starts: the current user's SID and this process's own (staged) path.</summary>
/// <param name="UserSid">The SID the principal and the logon trigger name (<c>WindowsIdentity.GetCurrent().User</c> in the host).</param>
/// <param name="Command">The ABSOLUTE path of the exe the task's action starts.</param>
public sealed record TaskIdentity(string UserSid, string Command);

/// <summary>One configured number a task definition used, by its key.</summary>
public sealed record TaskNumber(string Key, int Value);

/// <summary>A rendered task: its XML, the numbers it used, its execution limit and the canonical summary lines Task Scheduler's
/// read-back of it must print.</summary>
public sealed record WindowsTaskDefinition(WindowsTask Task, string Xml, IReadOnlyList<TaskNumber> Numbers, int ExecutionTimeLimitMinutes, IReadOnlyList<string> Summary);

/// <summary>
/// WR-D1 (PLAN_windows_runner.md §3.4): the task XML of <c>\wsl-care\watch</c> and <c>\wsl-care\archive</c>, a pure function of the
/// configuration, the user's SID and the staged exe's path — the precedent is <see cref="Systemd.UnitDropIns"/>, the systemd units
/// rendered from configuration.
/// </summary>
/// <remarks>
/// <para><b>The principal</b> is the current user (<c>InteractiveToken</c>, <c>LeastPrivilege</c>): nothing elevated, nothing as
/// SYSTEM, and the task runs only while that user is logged on (§3.4, §4). The logon trigger names the same SID — an unelevated
/// account may register a logon trigger for itself only.</para>
/// <para><b>The cadence</b> is ONE logon trigger with a delay and an indefinite repetition: the watch every
/// <c>mcpWatchdog.periodMinutes</c> after <c>windowsTasks.watchLogonDelaySeconds</c>, the archive every
/// <c>windowsTasks.archiveEveryHours</c> after <c>windowsTasks.archiveLogonDelayMinutes</c>. A delay of 0 writes no
/// <c>Delay</c> element, as the guard's task does. <c>ExecutionTimeLimit</c> is the supervisor's deadline plus
/// <see cref="NumberRules.RunMarginMinutes"/> — the outer net; the job is the real one (§3.3).</para>
/// <para><b>The summary</b> is the shape the extension's <c>SUMMARY_FUNCTION</c> prints over Task Scheduler's read-back
/// (<c>src_vs_code/src/windowsTime/guardTask.ts</c>): <c>principal=&lt;sid&gt; logon=3 level=0</c> (InteractiveToken = 3, LeastPrivilege
/// = 0), one line per trigger, the settings line (IgnoreNew = 2), the action line. The logon line carries ONE field the guard's
/// does not: <c>interval=</c>, the repetition — the cadence is the number this plan most needs a stale registration to show
/// (WR-E2 widens <c>SUMMARY_FUNCTION</c>'s logon case to print <c>$t.Repetition.Interval</c>; the guard's logon line then
/// ends <c>interval=</c>). Durations are written as the XML writes them (<c>PT60S</c>); Task Scheduler may store
/// <c>PT1M</c>, so the extension compares them by value (<c>durationSeconds</c>).</para>
/// </remarks>
public static class WindowsTaskDefinitions
{
    /// <summary>What every task's action passes after <c>tasks run</c>: the supervisor answers in JSON.</summary>
    private const string RunArguments = "tasks run {0} --json";

    public static WindowsTaskDefinition Render(WindowsTask task, EffectiveConfig config, TaskIdentity identity)
    {
        var cadence = CadenceOf(task, config);
        var limitMinutes = cadence.DeadlineMinutes + NumberRules.RunMarginMinutes;
        var arguments = string.Format(CultureInfo.InvariantCulture, RunArguments, task.Name);
        return new WindowsTaskDefinition(
            task,
            Xml(task, identity, cadence, limitMinutes, arguments),
            cadence.Numbers,
            limitMinutes,
            Summary(identity, cadence, limitMinutes, arguments));
    }

    /// <summary>A task's trigger delay and repetition (as ISO 8601 durations), its deadline, and the keys they came from.</summary>
    private sealed record Cadence(string Delay, string Interval, int DeadlineMinutes, IReadOnlyList<TaskNumber> Numbers);

    private static Cadence CadenceOf(WindowsTask task, EffectiveConfig config) => task switch
    {
        _ when task == WindowsTask.Watch => Of(config, ConfigKeys.WindowsTasks.WatchLogonDelaySeconds, "S", ConfigKeys.McpWatchdog.PeriodMinutes, "M", ConfigKeys.McpWatchdog.RunLimitMinutes),
        _ when task == WindowsTask.Archive => Of(config, ConfigKeys.WindowsTasks.ArchiveLogonDelayMinutes, "M", ConfigKeys.WindowsTasks.ArchiveEveryHours, "H", ConfigKeys.Timer.RunLimitMinutes),
        _ => throw new ArgumentOutOfRangeException(nameof(task), task.Name, $"not a task of this plan; one of: {string.Join(", ", WindowsTask.All)}"),
    };

    private static Cadence Of(EffectiveConfig config, ConfigKey.IntKey delay, string delayUnit, ConfigKey.IntKey every, string everyUnit, ConfigKey.IntKey deadline) =>
        new(
            Duration(config.Int(delay), delayUnit),
            Duration(config.Int(every), everyUnit),
            config.Int(deadline),
            [new(delay.Name, config.Int(delay)), new(every.Name, config.Int(every)), new(deadline.Name, config.Int(deadline))]);

    /// <summary><c>PT&lt;n&gt;&lt;unit&gt;</c>; empty for 0 — "no delay", as Task Scheduler and the guard spell it.</summary>
    private static string Duration(int value, string unit) =>
        value == 0 ? string.Empty : string.Create(CultureInfo.InvariantCulture, $"PT{value}{unit}");

    private static IReadOnlyList<string> Summary(TaskIdentity identity, Cadence cadence, int limitMinutes, string arguments) =>
    [
        $"principal={identity.UserSid} logon=3 level=0",
        $"trigger=logon enabled=True delay={cadence.Delay} interval={cadence.Interval}",
        string.Create(CultureInfo.InvariantCulture, $"settings limit=PT{limitMinutes}M instances=2 whenAvailable=True battery=False enabled=True"),
        $"action type=0 path={identity.Command} args={arguments}",
    ];

    private static string Xml(WindowsTask task, TaskIdentity identity, Cadence cadence, int limitMinutes, string arguments)
    {
        var sid = XmlText(identity.UserSid);
        var delay = cadence.Delay.Length > 0 ? $"<Delay>{cadence.Delay}</Delay>" : string.Empty;
        return string.Join('\n',
        [
            "<?xml version=\"1.0\" encoding=\"UTF-16\"?>",
            "<Task version=\"1.4\" xmlns=\"http://schemas.microsoft.com/windows/2004/02/mit/task\">",
            "  <RegistrationInfo>",
            "    <Author>wsl-care</Author>",
            $"    <Description>{XmlText(DescriptionOf(task))}</Description>",
            "  </RegistrationInfo>",
            "  <Principals>",
            $"    <Principal id=\"Author\"><UserId>{sid}</UserId><LogonType>InteractiveToken</LogonType><RunLevel>LeastPrivilege</RunLevel></Principal>",
            "  </Principals>",
            "  <Triggers>",
            $"    <LogonTrigger><Repetition><Interval>{cadence.Interval}</Interval><StopAtDurationEnd>false</StopAtDurationEnd></Repetition><Enabled>true</Enabled><UserId>{sid}</UserId>{delay}</LogonTrigger>",
            "  </Triggers>",
            .. SettingsLines(limitMinutes),
            "  <Actions Context=\"Author\">",
            $"    <Exec><Command>{XmlText(identity.Command)}</Command><Arguments>{XmlText(arguments)}</Arguments></Exec>",
            "  </Actions>",
            "</Task>",
        ]);
    }

    /// <summary>§3.4's settings, in the order Task Scheduler exports them.</summary>
    private static IReadOnlyList<string> SettingsLines(int limitMinutes) =>
    [
        "  <Settings>",
        "    <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>",
        "    <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>",
        "    <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>",
        "    <StartWhenAvailable>true</StartWhenAvailable>",
        "    <IdleSettings><StopOnIdleEnd>false</StopOnIdleEnd><RestartOnIdle>false</RestartOnIdle></IdleSettings>",
        "    <Enabled>true</Enabled>",
        "    <Hidden>false</Hidden>",
        "    <RunOnlyIfIdle>false</RunOnlyIfIdle>",
        "    <WakeToRun>false</WakeToRun>",
        string.Create(CultureInfo.InvariantCulture, $"    <ExecutionTimeLimit>PT{limitMinutes}M</ExecutionTimeLimit>"),
        // 7 is below normal: the family's local-CPU-budget rule for unattended work.
        "    <Priority>7</Priority>",
        "  </Settings>",
    ];

    private static string DescriptionOf(WindowsTask task) => task switch
    {
        _ when task == WindowsTask.Watch => "Samples the Windows MCP servers into wsl-care's CPU ledger every few minutes while you are logged on: the staged wsl-care.exe (tasks run watch) under its supervisor, as you, never elevated. Installed and removed by the AI OS Care extension.",
        _ => "Archives the Windows side's AI sessions at logon and every few hours while you are logged on: the staged wsl-care.exe (tasks run archive) under its supervisor, as you, never elevated. Installed and removed by the AI OS Care extension.",
    };

    /// <summary>Element text: <c>&amp;</c>, <c>&lt;</c> and <c>&gt;</c> escaped (the guard's <c>xmlText</c>).</summary>
    private static string XmlText(string text) =>
        text.Replace("&", "&amp;", StringComparison.Ordinal).Replace("<", "&lt;", StringComparison.Ordinal).Replace(">", "&gt;", StringComparison.Ordinal);
}
