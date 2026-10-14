using System.Globalization;
using System.Text;
using System.Text.Json;

using WslCare.Core.Actions.Engine;
using WslCare.Core.Config;
using WslCare.Core.Hosting;
using WslCare.Core.Json;
using WslCare.Core.Scheduler;

namespace WslCare.Cli.Commands;

/// <summary>
/// <c>tasks render &lt;watch|archive&gt; [--json]</c> (WR-D1 of PLAN_windows_runner.md §3.2): the Windows binary's per-user task XML
/// from its configuration, its own path and the user's SID (<see cref="WindowsTaskDefinitions"/>), whether it may be registered
/// (<see cref="StagedCopies.Check"/>), and the Windows dry state (<see cref="DryRunWindow.Peek"/>). Read-only: nothing is registered,
/// nothing is written — the extension registers what this answers, and only when it says <c>registrable</c>. And
/// <c>tasks stage [--remove] [--json]</c> (WR-D2): the staged copy itself made, verified and swept, or every copy removed
/// (<see cref="Staging"/>).
/// </summary>
/// <remarks>Exit codes of the render: 0 answered (registrable or not — the answer says which and why); 2 the distro's binary (the
/// tasks are the Windows binary's, as <c>watch</c> is the distro's); 78 a configuration layer in error — no task is rendered from a
/// configuration the daemon itself refuses (the <c>units dropin</c> rule). The stage's are on <see cref="Stage"/>, the run's on
/// the supervisor's file.</remarks>
internal static partial class TasksCommand
{
    public static int Render(Request.TasksRender request, CliHost host, ConfigLoadResult loaded, TextWriter stdout, TextWriter stderr)
    {
        if (Refusal(host, loaded, request.Task) is { } refusal)
        {
            Output.Note(stderr, refusal.Text);
            return (int)refusal.Exit;
        }

        var report = Report(request.Task, host, (WindowsHostPaths)host.Paths, loaded.Config);
        return Output.Answer(stdout, request.Json ? JsonSerializer.Serialize(report, WslCareJsonContext.Default.TasksRenderReport) : Text(report));
    }

    /// <summary><c>tasks stage [--remove] [--json]</c> (WR-D2): the copy of this process's own exe made and verified, the older copies
    /// swept (<see cref="Staging.Stage"/>), or every copy removed (<see cref="Staging.Remove"/>). Exit 0 staged, reused or removed — a
    /// copy kept because its exe still runs is named, never an error; 1 (<see cref="ExitCode.RunFailed"/>) when no verified copy
    /// was staged (the answer says why; no <c>stage.json</c> was written, nothing swept); 75 (<see cref="ExitCode.Busy"/>) when another
    /// run holds the run lock (nothing was done; try again — never waited on); 2 on the distro's binary. Not refused
    /// while a configuration layer is in error: it renders nothing from the configuration, and the extension's <i>Remove</i> must
    /// still clear the copies.</summary>
    public static int Stage(Request.TasksStage request, CliHost host, ConfigLoadResult loaded, TextWriter stdout, TextWriter stderr)
    {
        if (host.Paths is not WindowsHostPaths paths)
        {
            Output.Note(stderr, NotWindows);
            return (int)ExitCode.Usage;
        }

        var place = StagingPlace.For(paths);
        var maxStageBytes = loaded.Config.Int(ConfigKeys.Records.MaxStateFileBytes);
        return request.Remove
            ? Removed(Staging.Remove(new StagingAccess(place, host.Files, host.ArchiveFiles(), maxStageBytes, host.Clock.GetUtcNow())), request.Json, stdout)
            : Staged(Staging.Stage(new StageInput(place, host.ProcessPath(), Program.VersionText, host.Clock.GetUtcNow(), maxStageBytes, host.Files, host.ArchiveFiles())), request.Json, stdout);
    }

    private const string NotWindows = $"\"{CommandLine.BinaryName} tasks\" is the Windows binary's: the per-user scheduled tasks run the staged wsl-care.exe on the Windows host; nothing was done";

    private static int Staged(StageReport report, bool json, TextWriter stdout)
    {
        _ = Output.Answer(stdout, json ? JsonSerializer.Serialize(report, WslCareJsonContext.Default.StageReport) : StageText(report));
        return report.Outcome switch
        {
            StageOutcomes.Refused => (int)ExitCode.RunFailed,
            StageOutcomes.Busy => (int)ExitCode.Busy,
            _ => (int)ExitCode.Ok,
        };
    }

    private static int Removed(StageRemoveReport report, bool json, TextWriter stdout)
    {
        _ = Output.Answer(stdout, json ? JsonSerializer.Serialize(report, WslCareJsonContext.Default.StageRemoveReport) : RemoveText(report));
        return report.Outcome == StageOutcomes.Busy ? (int)ExitCode.Busy : (int)ExitCode.Ok;
    }

    private static string StageText(StageReport report)
    {
        var head = report.Outcome is StageOutcomes.Refused or StageOutcomes.Busy
            ? $"tasks stage: {report.Outcome}: {report.Reason}"
            : $"tasks stage: {report.Outcome} {report.Digest} at {report.Path} (release {report.Version}): {report.Reason}";
        var text = Folders(new StringBuilder().AppendLine(CommandLine.Printable(head)), "swept", report.Swept, report.Kept);
        foreach (var folder in report.Retained)
        {
            text.AppendLine(CommandLine.Printable($"retained: {folder.Path} ({folder.Why})"));
        }

        return text.ToString().TrimEnd('\n', '\r');
    }

    private static string RemoveText(StageRemoveReport report)
    {
        var head = report.Outcome == StageOutcomes.Busy
            ? $"tasks stage --remove: busy: {report.Reason}"
            : $"tasks stage --remove: {report.Removed.Count} removed, {report.Kept.Count} kept; {report.Root} {(report.RootRemoved ? "removed" : "stays")}";
        var text = Folders(new StringBuilder().AppendLine(CommandLine.Printable(head)), "removed", report.Removed, report.Kept);
        foreach (var other in report.Untouched)
        {
            text.AppendLine(CommandLine.Printable($"untouched (not a staging folder): {other}"));
        }

        return text.ToString().TrimEnd('\n', '\r');
    }

    /// <summary>One line per folder gone (swept or removed) and per folder kept, each with its reason.</summary>
    private static StringBuilder Folders(StringBuilder text, string goneLabel, IReadOnlyList<StagingFolder> gone, IReadOnlyList<StagingFolder> kept)
    {
        foreach (var folder in gone)
        {
            text.AppendLine(CommandLine.Printable($"{goneLabel}: {folder.Path} ({folder.Why})"));
        }

        foreach (var folder in kept)
        {
            text.AppendLine(CommandLine.Printable($"kept: {folder.Path} ({folder.Why})"));
        }

        return text;
    }

    /// <summary>Why nothing is rendered here — the distro's binary, or a configuration in error.</summary>
    private static (ExitCode Exit, string Text)? Refusal(CliHost host, ConfigLoadResult loaded, WindowsTask task) => host switch
    {
        { Paths: not WindowsHostPaths } => (ExitCode.Usage, NotWindows),
        _ when loaded.IsObserveOnly => (ExitCode.ObserveOnly, $"tasks render {task}: the configuration is in error ({string.Join("; ", loaded.Errors.Select(e => e.Display))}); no task is rendered from it"),
        _ => null,
    };

    private static TasksRenderReport Report(WindowsTask task, CliHost host, WindowsHostPaths paths, EffectiveConfig config)
    {
        var identity = new TaskIdentity(host.UserSid(), host.ProcessPath());
        var staged = StagedCopies.Check(StagedCopies.Root(paths), identity.Command, Program.VersionText, host.Files, host.ArchiveFiles(), config.Int(ConfigKeys.Records.MaxStateFileBytes));
        var dry = DryRunWindow.Peek(paths, host.Files, config, host.Clock.GetUtcNow());
        return TasksRender.Report(task, config, identity, staged, dry);
    }

    /// <summary>What a person reads: the verdict, the task, the numbers, the dry state, the summary, then the XML itself.</summary>
    private static string Text(TasksRenderReport report)
    {
        var verdict = report.Registrable ? $"registrable ({report.Reason})" : $"not registrable: {report.Reason}";
        var numbers = string.Join(", ", report.Numbers.Select(n => string.Create(CultureInfo.InvariantCulture, $"{n.Key}={n.Value}")));
        var text = new StringBuilder()
            .AppendLine(CommandLine.Printable($"tasks render {report.Task}: {verdict}"))
            .AppendLine(CommandLine.Printable($"task {report.TaskPath} as {Shown(report.UserSid)}: {Shown(report.Command)} tasks run {report.Task} --json"))
            .AppendLine(CommandLine.Printable(string.Create(CultureInfo.InvariantCulture, $"numbers: {numbers}; execution time limit {report.ExecutionTimeLimitMinutes} min")))
            .AppendLine(CommandLine.Printable($"dry run: {(report.Dry.DryRun ? "yes" : "no")} - {report.Dry.Reason}"))
            .AppendLine("summary (what Task Scheduler's read-back must print):");
        foreach (var line in report.Summary)
        {
            text.AppendLine(CommandLine.Printable($"  {line}"));
        }

        return text.AppendLine("xml:").Append(report.Xml).ToString();
    }

    private static string Shown(string value) => value.Length > 0 ? value : "(unknown)";
}
