using WslCare.Core.Actions.Engine;
using WslCare.Core.Config;

namespace WslCare.Core.Scheduler;

/// <summary>
/// <c>tasks render &lt;watch|archive&gt; --json</c> (WR-D1 of PLAN_windows_runner.md §3.2): the task XML, the numbers it used, the
/// canonical summary lines the extension compares Task Scheduler's read-back with, whether the XML may be registered, and the
/// Windows dry-run state — read-only. The contract the extension's WR-E1..E3 read; its golden is
/// <c>contracts/golden/windows-tasks-render.json</c>.
/// </summary>
/// <param name="Registrable">True only when this process is a verified staged copy AND the user's SID is known — the extension
/// registers nothing otherwise.</param>
/// <param name="Reason">Why it is registrable, or the first reason it is not.</param>
/// <param name="Digest">The staged copy's digest; empty when this process is not one.</param>
/// <param name="Version">The version the staged copy's <c>stage.json</c> names; empty when there is none to read.</param>
public sealed record TasksRenderReport(
    int SchemaVersion,
    string Task,
    string TaskPath,
    bool Registrable,
    string Reason,
    string UserSid,
    string Command,
    string Digest,
    string Version,
    IReadOnlyList<TaskNumber> Numbers,
    int ExecutionTimeLimitMinutes,
    IReadOnlyList<string> Summary,
    string Xml,
    DryRunPeek Dry);

/// <summary>The report of one render, from its pure parts — so the CLI and the golden build it the same way.</summary>
public static partial class TasksRender
{
    /// <summary>What a render without the user's SID says: nothing can name the principal, so nothing is registered.</summary>
    public const string NoSid = "the current user's SID could not be read, so no principal can be named";

    /// <summary>What an elevated render says: the tasks run as the user and are registered unelevated.</summary>
    public const string Elevated = "an elevated process: the tasks run as the user and are registered unelevated; run the render from a terminal that is not elevated";

    private static string WhyNot(string sid, bool elevated) => true switch
    {
        _ when sid.Length == 0 => NoSid,
        _ when elevated => Elevated,
        _ => $"the principal must be a user account; {sid} is not one, so nothing is registered",
    };

    /// <summary>Whether <paramref name="sid"/> is the SID of a user account: a local or domain account (<c>S-1-5-21-n-n-n-rid</c>) or an
    /// Entra ID account (<c>S-1-12-1-n-n-n-n</c>). Everything else - SYSTEM, the service accounts, a group, a service SID, empty - is not,
    /// so a task is never registered for a principal that is not a person (an allow-list: an unforeseen service SID is refused).</summary>
    public static bool IsUserAccount(string sid) => UserSid().IsMatch(sid);

    [System.Text.RegularExpressions.GeneratedRegex(@"^(S-1-5-21(-[0-9]{1,10}){4}|S-1-12-1(-[0-9]{1,10}){4})$", System.Text.RegularExpressions.RegexOptions.CultureInvariant)]
    private static partial System.Text.RegularExpressions.Regex UserSid();

    public static TasksRenderReport Report(WindowsTask task, EffectiveConfig config, TaskIdentity identity, StagedCheck staged, DryRunPeek dry, bool elevated)
    {
        var definition = WindowsTaskDefinitions.Render(task, config, identity);
        var registrable = staged.Registrable && identity.UserSid.Length > 0 && !elevated && IsUserAccount(identity.UserSid);
        return new TasksRenderReport(
            Core.SchemaVersion.Current,
            task.Name,
            task.TaskPath,
            registrable,
            staged.Registrable && !registrable ? WhyNot(identity.UserSid, elevated) : staged.Reason,
            identity.UserSid,
            identity.Command,
            staged.Digest,
            staged.Version,
            definition.Numbers,
            definition.ExecutionTimeLimitMinutes,
            definition.Summary,
            definition.Xml,
            dry);
    }
}
