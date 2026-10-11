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
public static class TasksRender
{
    /// <summary>What a render without the user's SID says: nothing can name the principal, so nothing is registered.</summary>
    public const string NoSid = "the current user's SID could not be read, so no principal can be named";

    public static TasksRenderReport Report(WindowsTask task, EffectiveConfig config, TaskIdentity identity, StagedCheck staged, DryRunPeek dry)
    {
        var definition = WindowsTaskDefinitions.Render(task, config, identity);
        var registrable = staged.Registrable && identity.UserSid.Length > 0;
        return new TasksRenderReport(
            Core.SchemaVersion.Current,
            task.Name,
            task.TaskPath,
            registrable,
            staged.Registrable && !registrable ? NoSid : staged.Reason,
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
