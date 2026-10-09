using System.Diagnostics;

using WslCare.Core.Collectors;
using WslCare.Core.Collectors.Procfs;
using WslCare.Core.Config;
using WslCare.Core.Files;
using WslCare.Core.Hosting;

namespace WslCare.Core.Archive;

/// <summary>How far the open-file check got — a closed set (E9.S1 review round M1): only a COMPLETE scan lets a due unit move.</summary>
public enum InUseState
{
    /// <summary>Every process of this account was read.</summary>
    Complete,

    /// <summary>The scan stopped at its ceiling (or the listing's time): what it did not reach may be open.</summary>
    Cut,

    /// <summary>The check did not run.</summary>
    NotChecked,
}

/// <summary>What is in use right now, as this process may see it (plan §15r D2.2, E9.S1).</summary>
/// <param name="OpenFiles">Every file a process of this account holds open, as the distribution spells it.</param>
/// <param name="ClaudeProjects">The Claude Code project folders a live Claude Code process works in (its working directory
/// encoded as Claude names its project folder).</param>
/// <param name="State">How far the check got; anything but <see cref="InUseState.Complete"/> keeps every due unit in place.</param>
/// <param name="Note">What the check could not see, or why it did not run; empty when complete.</param>
public sealed record InUseView(IReadOnlySet<string> OpenFiles, IReadOnlySet<string> ClaudeProjects, InUseState State, string Note)
{
    /// <summary>The per-unit question (E9.S5): the unit's files, as full paths on this side's disk → why one of them is held, or empty.
    /// The distro's view answers from its <c>/proc</c> scan alone (nothing here); the Windows view asks the Restart Manager.</summary>
    public Func<IReadOnlyList<string>, string> HeldBy { get; init; } = static _ => string.Empty;

    /// <summary>Why no Claude Code session may move on this side now (E9.S5: a live Claude Code on Windows, whose working folder cannot
    /// be read, or a process table that cannot be read); empty when nothing says so.</summary>
    public string ClaudeRunning { get; init; } = string.Empty;

    public static InUseView Complete(IReadOnlySet<string> openFiles, IReadOnlySet<string> claudeProjects) => new(openFiles, claudeProjects, InUseState.Complete, string.Empty);

    public static InUseView Cut(IReadOnlySet<string> openFiles, IReadOnlySet<string> claudeProjects, string note) => new(openFiles, claudeProjects, InUseState.Cut, note);

    public static InUseView NotChecked(string why) =>
        new(new HashSet<string>(StringComparer.Ordinal), new HashSet<string>(StringComparer.OrdinalIgnoreCase), InUseState.NotChecked, why);
}

/// <summary>
/// Plan §15r D2.2: which files of a session are open, and which Claude Code projects an agent is working in — read from
/// <c>/proc</c>: each process's <c>fd/*</c> links (read as links, never followed) and, for a process the catalogue attributes to
/// Claude Code, its <c>cwd</c>. Only this account's processes can be read (another's <c>fd</c> folder refuses) — and the agents
/// are this account's. Bounded by <c>archive.inUseScanSeconds</c> and by the time the caller has left; a scan cut by either says
/// so, and the selection then keeps every due unit (E9.S1 review round M1). Said honestly (§15r review M9): Claude Code keeps no
/// transcript open between appends, so the descriptor scan rarely sees it — the working-directory check, the age and phase 2's
/// no-replace renames are the guards.
/// </summary>
public static class InUse
{
    private const string ClaudeCode = "claude-code";

    public static TimeSpan Ceiling => Tuning.Current.Seconds(ConfigKeys.Archive.InUseScanSeconds);

    /// <summary>The scan within <see cref="Ceiling"/>.</summary>
    public static InUseView Scan(IHostPaths paths, IFileSystem files, CancellationToken cancellationToken, IWindowsSide windows) =>
        Scan(paths, files, Ceiling, cancellationToken, windows);

    /// <summary>The scan within <paramref name="ceiling"/> (the caller's time left, never above <see cref="Ceiling"/>): the distro's
    /// <c>/proc</c>, or on Windows the view that asks the Restart Manager per unit (E9.S5), each question within <see cref="Ceiling"/>.</summary>
    public static InUseView Scan(IHostPaths paths, IFileSystem files, TimeSpan ceiling, CancellationToken cancellationToken, IWindowsSide windows) =>
        paths is LinuxHostPaths linux ? ScanProc(linux, files, ceiling < Ceiling ? ceiling : Ceiling, cancellationToken) : windows.View(Ceiling);

    private static InUseView ScanProc(LinuxHostPaths paths, IFileSystem files, TimeSpan ceiling, CancellationToken cancellationToken)
    {
        var open = new HashSet<string>(StringComparer.Ordinal);
        var projects = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var watch = Stopwatch.StartNew();
        var pids = files.ListDirectories(paths.ProcRoot).Where(d => Path.GetFileName(d).All(char.IsAsciiDigit)).ToList();
        foreach (var pid in pids)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (watch.Elapsed > ceiling)
            {
                return InUseView.Cut(open, projects, $"the open-file scan was cut at {ceiling.TotalSeconds:0.#} s; what it did not reach may be open, so every due session stays where it is");
            }

            Read(files, pid, open, projects);
        }

        return InUseView.Complete(open, projects);
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
