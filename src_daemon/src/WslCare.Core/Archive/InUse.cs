using System.Diagnostics;

using WslCare.Core.Collectors;
using WslCare.Core.Collectors.Procfs;
using WslCare.Core.Config;
using WslCare.Core.Files;
using WslCare.Core.Hosting;

namespace WslCare.Core.Archive;

/// <summary>What is in use right now, as this process may see it (plan §15r D2.2, E9.S1).</summary>
/// <param name="OpenFiles">Every file a process of this account holds open, as the distribution spells it.</param>
/// <param name="ClaudeProjects">The Claude Code project folders a live Claude Code process works in (its working directory
/// encoded as Claude names its project folder).</param>
/// <param name="Checked">Whether the check ran at all; <c>false</c> on a side it is not built for yet.</param>
/// <param name="Note">What the check could not see, or why it did not run; empty when it saw everything it looks at.</param>
public sealed record InUseView(IReadOnlySet<string> OpenFiles, IReadOnlySet<string> ClaudeProjects, bool Checked, string Note)
{
    public static InUseView NotChecked(string why) =>
        new(new HashSet<string>(StringComparer.Ordinal), new HashSet<string>(StringComparer.OrdinalIgnoreCase), false, why);
}

/// <summary>
/// Plan §15r D2.2: which files of a session are open, and which Claude Code projects an agent is working in — read from
/// <c>/proc</c>: each process's <c>fd/*</c> links (read as links, never followed) and, for a process the catalogue attributes to
/// Claude Code, its <c>cwd</c>. Only this account's processes can be read (another's <c>fd</c> folder refuses) — and the agents
/// are this account's. Bounded by <c>archive.inUseScanSeconds</c>; a scan cut by its ceiling says so, and the selection then
/// trusts nothing it did not see. Said honestly (§15r review M9): Claude Code keeps no transcript open between appends, so the
/// descriptor scan rarely sees it — the working-directory check, the age and phase 2's no-replace renames are the guards.
/// </summary>
public static class InUse
{
    private const string ClaudeCode = "claude-code";

    /// <summary>The Windows side asks the Restart Manager (E9.S5); until then it says so.</summary>
    public const string NotOnWindowsYet = "which files are open is not checked on Windows yet (the Restart Manager query is E9.S5); the run will check before it moves anything";

    public static TimeSpan Ceiling => Tuning.Current.Seconds(ConfigKeys.Archive.InUseScanSeconds);

    public static InUseView Scan(IHostPaths paths, IFileSystem files, CancellationToken cancellationToken) =>
        paths is LinuxHostPaths linux ? ScanProc(linux, files, cancellationToken) : InUseView.NotChecked(NotOnWindowsYet);

    private static InUseView ScanProc(LinuxHostPaths paths, IFileSystem files, CancellationToken cancellationToken)
    {
        var open = new HashSet<string>(StringComparer.Ordinal);
        var projects = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var watch = Stopwatch.StartNew();
        var pids = files.ListDirectories(paths.ProcRoot).Where(d => Path.GetFileName(d).All(char.IsAsciiDigit)).ToList();
        foreach (var pid in pids)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (watch.Elapsed > Ceiling)
            {
                return new InUseView(open, projects, true, $"the open-file scan stopped at its {Ceiling.TotalSeconds:0} s ceiling; what it did not see counts as in use");
            }

            Read(files, pid, open, projects);
        }

        return new InUseView(open, projects, true, string.Empty);
    }

    private static void Read(IFileSystem files, string pid, HashSet<string> open, HashSet<string> projects)
    {
        foreach (var fd in files.ListEntries(Path.Combine(pid, "fd")).Where(e => e.Kind == EntryKind.Link))
        {
            if (files.ReadLink(Path.Combine(pid, "fd", fd.Name)) is LinkReadResult.Target target)
            {
                open.Add(target.Path);
            }
        }

        if (IsClaudeCode(files, pid) && files.ReadLink(Path.Combine(pid, "cwd")) is LinkReadResult.Target cwd)
        {
            projects.Add(ArchiveNames.ClaudeProjectOf(cwd.Path));
        }
    }

    private static bool IsClaudeCode(IFileSystem files, string pid) =>
        ProcText.Bytes(files, Path.Combine(pid, "cmdline")) is Reading<byte[]>.Available cmdline
        && Agents.AgentProcesses.AgentOfPrograms(CommandLineText.ProgramNames(CommandLineText.Arguments(cmdline.Value))) is { Id: ClaudeCode };
}
