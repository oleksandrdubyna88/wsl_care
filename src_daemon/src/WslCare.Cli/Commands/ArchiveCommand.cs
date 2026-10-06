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
