using System.Text;
using System.Text.Json;

using WslCare.Core.Agents;
using WslCare.Core.Archive;
using WslCare.Core.Json;

namespace WslCare.Cli.Commands;

/// <summary>
/// <c>archive check-base &lt;path&gt; [--json]</c> (plan §15r D7, E9.S0): the base folder rules, judged by THIS user's process — the
/// one that will move the sessions there (D1) — and never as root, whose answer would be about root's access, not the user's.
/// Read-only: nothing is created but the probe file a write test needs, inside the folder, deleted on close. A refused folder is
/// an answer (exit 0, <c>accepted: false</c> with its rule), not a failure of the verb.
/// </summary>
internal static class ArchiveCommand
{
    internal const string RootRefusal =
        "archive check-base runs as the user whose sessions the archive moves, not as uid 0 — that user's own process writes the archive (plan §15r D1): run it as that user. Nothing was looked at.";

    public static int CheckBase(Request.ArchiveCheckBase request, CliHost host, TextWriter stdout, TextWriter stderr)
    {
        if (host.Privilege.IsRoot)
        {
            Output.Note(stderr, RootRefusal);
            return (int)ExitCode.NotAsRoot;
        }

        var report = Judge(host, request.Path);
        return Output.Answer(stdout, request.Json ? JsonSerializer.Serialize(report, WslCareJsonContext.Default.BaseFolderReport) : Render(report));
    }

    internal const string PreviewRootRefusal =
        "archive preview runs as the user whose sessions the archive moves, not as uid 0 — that user's own process will move them (plan §15r D1): run it as that user. Nothing was looked at.";

    /// <summary><c>archive preview [--agent &lt;id&gt;] [--json]</c> (plan §15r E9.S1): the selection over this side now, as this user,
    /// under the measure-now budget — read-only.</summary>
    public static int Preview(Request.ArchivePreview request, CliHost host, Core.Config.ConfigLoadResult loaded, TextWriter stdout, TextWriter stderr, CancellationToken cancellationToken)
    {
        if (host.Privilege.IsRoot)
        {
            Output.Note(stderr, PreviewRootRefusal);
            return (int)ExitCode.NotAsRoot;
        }

        var started = host.Clock.GetTimestamp();
        var input = new SelectionInput(host.Paths, host.Files, loaded.Config, host.Clock.GetUtcNow(), TimeZoneInfo.Local, InUse.Scan(host.Paths, host.Files, cancellationToken), Environment.GetEnvironmentVariable)
        {
            OnlyAgent = request.Agent,
            OutOfTime = () => host.Clock.GetElapsedTime(started) > AgentWalk.MeasureNowBudget,
            Token = cancellationToken,
        };
        var report = ArchivePreview.From(input, Selection.Select(input), SideName.OfThisProcess(host.Paths.Side));
        return Output.Answer(stdout, request.Json ? JsonSerializer.Serialize(report, WslCareJsonContext.Default.ArchivePreviewReport) : RenderPreview(report));
    }

    private static string RenderPreview(ArchivePreviewReport report)
    {
        var text = new StringBuilder().AppendLine(CommandLine.Printable($"wsl-care archive preview ({report.Side}, {report.SideFolder}, months in {report.Zone}){(report.BaseFolder.Length == 0 ? " — no archive.baseFolder set" : $" → {report.BaseFolder}")}"));
        foreach (var agent in report.Agents)
        {
            text.AppendLine(CommandLine.Printable(string.Create(System.Globalization.CultureInfo.InvariantCulture, $"  {agent.Name,-14} due {agent.DueUnits} ({agent.DueBytes / 1048576.0:0.0} MiB) from day {agent.EffectiveAgeDays}; kept {agent.Skipped.Sum(s => s.Count)}; younger {agent.Younger}{(agent.Note.Length > 0 ? $" — {agent.Note}" : string.Empty)}")));
            foreach (var warning in agent.Warnings)
            {
                text.AppendLine(CommandLine.Printable($"    warning: {warning}"));
            }
        }

        return text.Append(CommandLine.Printable(report.InUse.Note.Length == 0 ? string.Empty : $"  note: {report.InUse.Note}")).ToString().TrimEnd('\r', '\n', ' ');
    }

    /// <summary>The base rules over this host — shared with <c>config set archive.baseFolder</c>.</summary>
    internal static BaseFolderReport Judge(CliHost host, string path) =>
        BaseFolderRules.Judge(host.Paths, host.Files, ExtraAgentRules.CleanupRoots(host.Actions, host.Paths.Home, host.Paths.Rules), path);

    private static string Render(BaseFolderReport report)
    {
        var text = new StringBuilder();
        text.AppendLine(CommandLine.Printable(report.Accepted
            ? $"accepted: {report.Folder}{Where(report.Mount)}"
            : $"refused ({report.Rule}): {report.Refusal}"));
        foreach (var warning in report.Warnings)
        {
            text.AppendLine(CommandLine.Printable($"  warning: {warning}"));
        }

        foreach (var note in report.Notes)
        {
            text.AppendLine(CommandLine.Printable($"  note: {note}"));
        }

        return text.ToString().TrimEnd('\r', '\n');
    }

    private static string Where(BaseMountReport? mount) => mount is null ? string.Empty : $" ({mount.Type} at {mount.MountPoint})";
}
