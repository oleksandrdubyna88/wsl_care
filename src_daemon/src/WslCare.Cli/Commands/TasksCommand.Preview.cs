using WslCare.Core.Processes.Redaction;
using System.Globalization;
using System.Text;
using System.Text.Json;

using WslCare.Core.Actions;
using WslCare.Core.Actions.Engine;
using WslCare.Core.Actions.WindowsTemp;
using WslCare.Core.Config;
using WslCare.Core.Hosting;
using WslCare.Core.Json;
using WslCare.Core.Records;

namespace WslCare.Cli.Commands;

/// <summary>
/// <c>tasks preview cleanup [--json]</c> (WR-T2 of PLAN_windows_runner.md §3.12 "In the panel"): what the Windows <c>%TEMP%</c> cleanup
/// (A23) would do now, built from the SAME plan the engine's preview reads (<see cref="TempPreviews"/>), in this process - read-only
/// work bounded by A23's own budgets, as <c>tasks render</c> is. It deletes nothing, moves no cursor, starts no dry-run week (it
/// PEEKS A23's), writes no file and asks for no lock, so it answers while a watch or a cleanup holds the run lock and blocks neither.
/// </summary>
/// <remarks>Refusals, in order, before anything is read: the distro's binary (2); an elevated process (81, the sentence
/// <c>act</c> uses). A configuration in error does NOT refuse it (as <c>act --preview</c> does not): the answer says
/// <c>observeOnly</c> with the errors. Exit 0 when answered - a refused root answers its refusal with zero counts; 130 when interrupted.</remarks>
internal static partial class TasksCommand
{
    public static int Preview(Request.TasksPreview request, CliHost host, ConfigLoadResult loaded, TextWriter stdout, TextWriter stderr, CancellationToken cancellationToken)
    {
        if (host.Paths is not WindowsHostPaths paths)
        {
            Output.Note(stderr, NotWindows);
            return (int)ExitCode.Usage;
        }

        if (host.Privilege.IsRoot)
        {
            // Refused for the PREVIEW's own reason: an elevated process sees the entries the Administrators group owns, so its picture of what the
            // unelevated cleanup would delete would be misleading (the owner check is the user's SID).
            Output.Note(stderr, $"tasks preview {request.Task}: an elevated (administrator) process is refused ({host.Privilege.Basis}): it sees entries owned by the Administrators group, so its picture of what the cleanup would delete as you would be misleading; run it from a terminal that is not elevated. Nothing was read.");
            return (int)ExitCode.NotAsRoot;
        }

        var report = Previewed(host, paths, loaded, cancellationToken);
        return Output.Answer(stdout, request.Json ? JsonSerializer.Serialize(report, WslCareJsonContext.Default.TempPreviewReport) : PreviewText(report));
    }

    private static TempPreviewReport Previewed(CliHost host, WindowsHostPaths paths, ConfigLoadResult loaded, CancellationToken cancellationToken)
    {
        var config = loaded.Config;
        var now = host.Clock.GetUtcNow();
        // A person's preview: the context carries the Cli trigger, but TempPreviews.Report sets BOTH triggers itself - Timer for the plan the task
        // will make (the counts), Cli for the second plan that also scans the names the allow-list does not cover (the "would be eligible" groups).
        // Nothing is recorded, so the trigger's other meaning does not apply.
        var context = new ActionContext(paths, host.Files, host.Clock, config, RunTrigger.Cli, new TargetUserResult.None("the Windows binary has no target user")) { UserSid = host.UserSid() };
        var dry = DryRunWindow.Peek(paths, host.Files, config, now, WindowsTempAction.StampName);
        return TempPreviews.Report(context, TempSettings.From(config), dry, cancellationToken) with
        {
            ProductVersion = Program.VersionText,
            ObserveOnly = loaded.IsObserveOnly,
            ConfigurationErrors = [.. loaded.Errors.Select(e => e.Display)],
        };
    }

    private static string PreviewText(TempPreviewReport report)
    {
        var c = report.Counts;
        var text = new StringBuilder()
            .AppendLine(ShownText.Printable(string.Create(CultureInfo.InvariantCulture, $"tasks preview {report.Task}: {Shown(report.Root)}; cutoff {report.CutoffUtc} (temp.olderThanDays {report.OlderThanDays}); eligible {c.Eligible}, would be eligible if allowed {c.WouldBeEligible}, young {c.Young}, excluded {c.Excluded}, unknown {c.Unknown}, changed {c.Changed}, not allowed {c.NotAllowed}, deferred {c.Deferred}; {report.EligibleBytes} logical bytes")))
            .AppendLine(ShownText.Printable($"dry run (A23's own week): {(report.Dry.DryRun ? "yes" : "no")} - {report.Dry.Reason}"))
            .AppendLine(ShownText.Printable(string.Create(CultureInfo.InvariantCulture, $"pass: started {Shown(report.Pass.Started)}, {report.Pass.Decided} of {report.Pass.Total} names decided, {report.Pass.PassesCompleted} completed; the next run starts after {Shown(report.Pass.Last)}{(report.Pass.InProgress.Length > 0 ? $"; an interrupted deletion of {report.Pass.InProgress} is taken first" : string.Empty)}")));
        if (report.ObserveOnly)
        {
            text.AppendLine(ShownText.Printable($"the configuration is in error ({string.Join("; ", report.ConfigurationErrors)}): a run would be refused until it is fixed"));
        }

        if (report.Refusal.Length > 0)
        {
            text.AppendLine(ShownText.Printable($"refused: {report.Refusal}"));
        }

        Groups(text, report.Groups.Where(g => g.Verdict == "eligible"), string.Empty, report.GroupsNotShown.Eligible);
        Groups(text, report.Groups.Where(g => g.Verdict == "wouldBeEligible"), "would be eligible if allowed (widen temp.allowGlobs with \"wsl-care.exe config set ...\"):", report.GroupsNotShown.WouldBeEligible);
        if (report.WouldBeEligibleLimited)
        {
            text.AppendLine("the scan of the names the allow-list does not cover ran out of the scan budget (temp.scanMaxNodes / temp.scanMaxSeconds): the would-be-eligible figures are a lower bound");
        }

        foreach (var excluded in report.ExcludedByReason)
        {
            text.AppendLine(ShownText.Printable(string.Create(CultureInfo.InvariantCulture, $"excluded {excluded.Count}: {excluded.Reason}")));
        }

        return text.ToString().TrimEnd('\n', '\r');
    }

    private static void Groups(StringBuilder text, IEnumerable<TempPreviewGroup> groups, string heading, int notShown)
    {
        var any = false;
        foreach (var group in groups)
        {
            if (!any && heading.Length > 0)
            {
                text.AppendLine(ShownText.Printable(heading));
            }

            any = true;
            text.AppendLine(ShownText.Printable(string.Create(CultureInfo.InvariantCulture, $"{group.Verdict} {group.Pattern}: {group.Count} ({group.Bytes} bytes), showing {group.Paths.Count}:")));
            foreach (var path in group.Paths)
            {
                text.AppendLine(ShownText.Printable($"    {path}"));
            }

            if (group.More > 0)
            {
                text.AppendLine(string.Create(CultureInfo.InvariantCulture, $"    ... and {group.More} more"));
            }
        }

        if (notShown > 0)
        {
            text.AppendLine(string.Create(CultureInfo.InvariantCulture, $"... and {notShown} more group(s) not shown"));
        }
    }
}
