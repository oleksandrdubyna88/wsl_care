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
/// nothing is written — the extension registers what this answers, and only when it says <c>registrable</c>.
/// </summary>
/// <remarks>Exit codes: 0 answered (registrable or not — the answer says which and why); 2 the distro's binary (the tasks are the
/// Windows binary's, as <c>watch</c> is the distro's); 78 a configuration layer in error — no task is rendered from a configuration
/// the daemon itself refuses (the <c>units dropin</c> rule).</remarks>
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

    /// <summary>Why nothing is rendered here — the distro's binary, or a configuration in error.</summary>
    private static (ExitCode Exit, string Text)? Refusal(CliHost host, ConfigLoadResult loaded, WindowsTask task) => host switch
    {
        { Paths: not WindowsHostPaths } => (ExitCode.Usage, $"\"{CommandLine.BinaryName} tasks\" is the Windows binary's: the per-user scheduled tasks run the staged wsl-care.exe on the Windows host; nothing was done"),
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
