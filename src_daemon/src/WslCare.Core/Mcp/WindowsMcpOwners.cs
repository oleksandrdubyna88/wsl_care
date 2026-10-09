using System.Globalization;

using WslCare.Core.Collectors;

namespace WslCare.Core.Mcp;

/// <summary>
/// Who holds a Windows MCP instance (E14 S7a) — PURE over one snapshot and the creation times it is handed. Windows keeps a
/// process's parent pid after the parent exits and reuses pids, so a parent is only the parent when it was created BEFORE the
/// child, and an ancestor only an ancestor when it was created before the process below it (coai plan round 2026-10-09, finding 6).
/// An unreadable creation time cannot prove a reuse, so it is not taken as one.
/// </summary>
/// <remarks>Interop: a <c>wsl.exe</c> parent is a WSL connection, and the distro process that asked for the server is not visible
/// from Windows — such an instance is never orphaned while that connection lives, however long ago its caller ended (finding 0).</remarks>
public static class WindowsMcpOwners
{
    /// <summary>What an orphaned instance's group is named after.</summary>
    public const string NoParent = "a parent that is gone";

    public static WindowsMcpOwner Of(WindowsProcessEntry child, Reading<DateTimeOffset> childCreated, IReadOnlyDictionary<int, WindowsProcessEntry> byPid, Func<int, Reading<DateTimeOffset>> created)
    {
        if (!byPid.TryGetValue(child.ParentPid, out var parent) || parent.Pid == child.Pid)
        {
            return new(WindowsMcpOwnerKind.Orphaned, child.ParentPid, string.Empty, Invariant($"its parent (pid {child.ParentPid}) is gone"));
        }

        var parentCreated = created(parent.Pid);
        return Newer(parentCreated, childCreated)
            ? new(WindowsMcpOwnerKind.Orphaned, parent.Pid, parent.ExeName, Invariant($"its parent (pid {parent.Pid}) is gone: that pid now belongs to {parent.ExeName}, created after it"))
            : Live(parent, parentCreated, byPid, created);
    }

    /// <summary>The instances per owner, the largest group first: the "35 under one wsl.exe" line.</summary>
    public static IReadOnlyList<WindowsMcpOwnerGroup> Groups(IReadOnlyList<WindowsMcpInstance> instances) =>
    [
        .. instances
            .GroupBy(i => (i.Owner.Kind, Parent: GroupName(i.Owner)))
            .Select(g => new WindowsMcpOwnerGroup(g.Key.Kind, g.Key.Parent, g.Count()))
            .OrderByDescending(g => g.Count)
            .ThenBy(g => g.Kind)
            .ThenBy(g => g.Parent, StringComparer.Ordinal),
    ];

    /// <summary>A live parent: an agent, a WSL connection, an agent further up, or another program.</summary>
    private static WindowsMcpOwner Live(WindowsProcessEntry parent, Reading<DateTimeOffset> parentCreated, IReadOnlyDictionary<int, WindowsProcessEntry> byPid, Func<int, Reading<DateTimeOffset>> created) =>
        WindowsMcpCatalogue.IsAgent(parent.ExeName) ? new(WindowsMcpOwnerKind.Agent, parent.Pid, parent.ExeName, Label(parent))
        : WindowsMcpCatalogue.IsWsl(parent.ExeName) ? new(WindowsMcpOwnerKind.Interop, parent.Pid, parent.ExeName, $"{Label(parent)}: a WSL interop child; its caller in the distro is not visible from Windows")
        : new Walk(byPid, created, [parent.Pid]).AgentAbove(parent, parentCreated) is { } agent ? new(WindowsMcpOwnerKind.Agent, parent.Pid, parent.ExeName, Label(agent))
        : new(WindowsMcpOwnerKind.Other, parent.Pid, parent.ExeName, Label(parent));

    /// <summary>One walk up from a live parent: the snapshot, the creation times, and the pids already passed (a loop a torn snapshot
    /// could hold ends the walk).</summary>
    private sealed record Walk(IReadOnlyDictionary<int, WindowsProcessEntry> ByPid, Func<int, Reading<DateTimeOffset>> Created, HashSet<int> Visited)
    {
        /// <summary>The first agent above <paramref name="below"/>; the walk ends at a gone ancestor, at one created after the process
        /// below it (a reused pid owns nothing), and at a pid already passed.</summary>
        public WindowsProcessEntry? AgentAbove(WindowsProcessEntry below, Reading<DateTimeOffset> belowCreated) =>
            Up(below) is { } up ? Step(up, Created(up.Pid), belowCreated) : null;

        private WindowsProcessEntry? Step(WindowsProcessEntry up, Reading<DateTimeOffset> upCreated, Reading<DateTimeOffset> belowCreated) =>
            Newer(upCreated, belowCreated) ? null
            : WindowsMcpCatalogue.IsAgent(up.ExeName) ? up
            : AgentAbove(up, upCreated);

        private WindowsProcessEntry? Up(WindowsProcessEntry below) =>
            ByPid.TryGetValue(below.ParentPid, out var up) && Visited.Add(up.Pid) ? up : null;
    }

    /// <summary>Proven newer: both creation times read and the first after the second.</summary>
    private static bool Newer(Reading<DateTimeOffset> candidate, Reading<DateTimeOffset> than) =>
        candidate is Reading<DateTimeOffset>.Available { Value: var a } && than is Reading<DateTimeOffset>.Available { Value: var b } && a > b;

    private static string GroupName(WindowsMcpOwner owner) => owner.Kind switch
    {
        WindowsMcpOwnerKind.Orphaned => NoParent,
        WindowsMcpOwnerKind.Agent => owner.Detail,
        _ => Label(owner.ParentName, owner.ParentPid),
    };

    private static string Label(WindowsProcessEntry process) => Label(process.ExeName, process.Pid);

    private static string Label(string exeName, int pid) => Invariant($"{exeName} (pid {pid})");

    private static string Invariant(FormattableString text) => text.ToString(CultureInfo.InvariantCulture);
}
