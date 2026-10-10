namespace WslCare.Core.Archive;

/// <summary>
/// Plan §15r D2.2 — whether an agent may be working on a unit right now, from one open-file scan: the scan was complete, no file of
/// the unit is open, and (Claude Code) no live Claude Code works in the unit's project. The selection keeps a due unit by it, and
/// phase 2 asks it again before it touches anything (security review M-2: Claude keeps no transcript open, so the lease and the
/// share mode see nothing of an agent that resumes the session by its path).
/// </summary>
public static class Liveness
{
    public const string ClaudeCode = "claude-code";

    /// <summary>Why the scan does not let anything move; empty when it was complete.</summary>
    public static string ScanProblem(InUseView view) => view.State switch
    {
        InUseState.Complete => string.Empty,
        InUseState.Cut => $"the open-file scan was cut ({view.Note}); what it did not reach may be open",
        _ => $"which files are open was not checked ({view.Note})",
    };

    /// <summary>The first of <paramref name="relatives"/> (below <paramref name="under"/>) a process holds open; empty when none is.
    /// <paramref name="distro"/> spells a path as the scan saw it.</summary>
    public static string OpenFile(InUseView view, Func<string, string> distro, string under, IEnumerable<string> relatives) =>
        relatives.FirstOrDefault(r => view.OpenFiles.Contains(distro(Path.Combine(under, r)))) ?? string.Empty;

    /// <summary>Why a file of the unit is held: one the scan saw open, or what the view's per-unit question answers (the Restart Manager
    /// on Windows, E9.S5); empty when none is.</summary>
    public static string Held(InUseView view, Func<string, string> distro, string under, IReadOnlyList<string> relatives) =>
        OpenFile(view, distro, under, relatives) is { Length: > 0 } open ? $"{open} is open in a process"
        : view.HeldBy([.. relatives.Select(r => Path.Combine(under, r))]);

    /// <summary>Why an agent may be working in the unit: a live Claude Code on a side that cannot read its working folder (E9.S5) while
    /// a file of the unit is not idle (the E9.S5 amendment, owner decision 2026-10-09: <paramref name="files"/> are its full paths), or
    /// one working in the unit's project; empty otherwise.</summary>
    public static string AgentWorking(InUseView view, string agent, string key, IReadOnlyList<string> files) =>
        agent == ClaudeCode && ClaudeKeeps(view, files) is { Length: > 0 } keeps ? keeps
        : ProjectOf(agent, key) is { Length: > 0 } project && view.ClaudeProjects.Contains(project) ? $"Claude Code is working in the project {project}"
        : string.Empty;

    /// <summary>Claude Code may run where its working folder cannot be read, and the unit is not idle: both sentences; empty when either
    /// is silent. Whether Claude runs is asked first — the idle question reads the files' times.</summary>
    private static string ClaudeKeeps(InUseView view, IReadOnlyList<string> files) =>
        view.ClaudeRunning() is { Length: > 0 } running && view.ClaudeIdle(files) is { Length: > 0 } busy ? $"{running}; {busy}" : string.Empty;

    /// <summary>The Claude Code project a unit of <paramref name="agent"/> lies in (<c>projects/&lt;project&gt;/…</c>); empty otherwise.</summary>
    public static string ProjectOf(string agent, string key)
    {
        var segments = key.Split('/');
        return agent == ClaudeCode && segments.Length > 1 ? segments[1] : string.Empty;
    }

    /// <summary>Why the unit must not be touched now; empty when no agent can be working on it.</summary>
    public static string Problem(InUseView view, Func<string, string> distro, string agent, string under, string key, IEnumerable<string> relatives) =>
        ScanProblem(view) is { Length: > 0 } scan ? scan
        : AgentWorking(view, agent, key, [.. relatives.Select(r => Path.Combine(under, r))]) is { Length: > 0 } working ? working
        : Held(view, distro, under, [.. relatives]);
}
