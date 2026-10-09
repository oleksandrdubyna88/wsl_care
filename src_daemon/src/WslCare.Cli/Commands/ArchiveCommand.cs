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
    /// within the listing budget derived from <c>archive.previewTimeoutSeconds</c> — the open-file scan, the layouts and every
    /// companion walk share it (E9.S1 review round m1) — read-only. Its <c>--json</c> answer is ONE line (the S4 own review round C-8:
    /// root reads a child's answer as its last line, and an indented answer's last line is a lone brace).</summary>
    public static int Preview(Request.ArchivePreview request, CliHost host, Core.Config.ConfigLoadResult loaded, TextWriter stdout, TextWriter stderr, CancellationToken cancellationToken)
    {
        if (host.Privilege.IsRoot)
        {
            Output.Note(stderr, PreviewRootRefusal);
            return (int)ExitCode.NotAsRoot;
        }

        var started = host.Clock.GetTimestamp();
        TimeSpan Left() => ArchivePreview.ListingBudget(loaded.Config) - host.Clock.GetElapsedTime(started);
        var input = new SelectionInput(host.Paths, host.Files, loaded.Config, host.Clock.GetUtcNow(), TimeZoneInfo.Local, InUse.Scan(host.Paths, host.Files, Left(), cancellationToken), Environment.GetEnvironmentVariable)
        {
            OnlyAgent = request.Agent,
            TimeLeft = Left,
            Token = cancellationToken,
        };
        var report = ArchivePreview.From(input, Selection.Select(input), SideName.OfThisProcess(host.Paths.Side));
        return Output.Answer(stdout, request.Json ? JsonSerializer.Serialize(report, WslCareJsonContext.Compact.ArchivePreviewReport) : RenderPreview(report));
    }

    private static string RenderPreview(ArchivePreviewReport report)
    {
        var text = new StringBuilder().AppendLine(CommandLine.Printable($"wsl-care archive preview ({report.Side}, {report.SideFolder}, months in {report.Zone}){BaseOf(report)}"));
        foreach (var agent in report.Agents)
        {
            AppendAgent(text, agent);
        }

        return text.Append(CommandLine.Printable(report.InUse.Note.Length == 0 ? string.Empty : $"  note: {report.InUse.Note}")).ToString().TrimEnd('\r', '\n', ' ');
    }

    private static string BaseOf(ArchivePreviewReport report) => report.BaseFolder.Length == 0 ? " — no archive.baseFolder set" : $" → {report.BaseFolder}";

    private static void AppendAgent(StringBuilder text, AgentPreviewReport agent)
    {
        var note = agent.Note.Length > 0 ? $" — {agent.Note}" : string.Empty;
        text.AppendLine(CommandLine.Printable(string.Create(System.Globalization.CultureInfo.InvariantCulture, $"  {agent.Name,-14} due {agent.DueUnits} ({agent.DueBytes / 1048576.0:0.0} MiB) from day {agent.EffectiveAgeDays}; kept {agent.Skipped.Sum(s => s.Count)}; younger {agent.Younger}{note}")));
        foreach (var warning in agent.Warnings)
        {
            text.AppendLine(CommandLine.Printable($"    warning: {warning}"));
        }
    }

    /// <summary>The base rules over this host — shared with <c>config set archive.baseFolder</c>. Inside the distribution the Windows
    /// profile the last full run found joins them (E9.S0 review round S1): a base on a Windows drive stays clear of its places.</summary>
    internal static BaseFolderReport Judge(CliHost host, string path) =>
        BaseFolderRules.Judge(
            host.Paths,
            host.Files,
            new BaseFolderContext(ExtraAgentRules.CleanupRoots(host.Actions, host.Paths.Home, host.Paths.Rules), WindowsProfile(host)) { WindowsAgentFolders = WindowsAgentFolders(host) },
            path);

    /// <summary>E9.S1 review round M3: inside the distribution, the data folders of the manual agents the WINDOWS side walks.</summary>
    private static IReadOnlyList<string> WindowsAgentFolders(CliHost host) =>
        host.Paths is Core.Hosting.LinuxHostPaths
            ? [.. host.LoadConfig().Config.Agents(Core.Config.ConfigKeys.AiAgents.Extra).Where(a => a.Side == ExtraAgentShape.Windows).SelectMany(a => a.DataFolders)]
            : [];

    /// <summary>The Windows profile of the last full run that measured the Windows clock; empty when none has (or on Windows).</summary>
    private static string WindowsProfile(CliHost host) =>
        host.Paths is Core.Hosting.LinuxHostPaths
            ? Core.Records.LastFullRun.Read(host.Paths, host.Files, host.Clock).WindowsClock.Map(a => a.Value.Profile).ValueOr(string.Empty)
            : string.Empty;

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

    private static string Where(BaseMountReport mount) => mount.Known ? $" ({mount.Type} at {mount.MountPoint})" : string.Empty;
}
