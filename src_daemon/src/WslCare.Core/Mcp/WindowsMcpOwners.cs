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
    public const string NoParent = "no live parent";

    public static WindowsMcpOwner Of(WindowsProcessEntry child, Reading<DateTimeOffset> childCreated, IReadOnlyDictionary<int, WindowsProcessEntry> byPid, Func<int, Reading<DateTimeOffset>> created)
    {
        if (!byPid.TryGetValue(child.ParentPid, out var parent) || parent.Pid == child.Pid)
        {
            return new(WindowsMcpOwner.Orphaned, child.ParentPid, string.Empty, Invariant($"its parent (pid {child.ParentPid}) is gone"));
        }

        var parentCreated = created(parent.Pid);
        return Newer(parentCreated, childCreated)
            ? new(WindowsMcpOwner.Orphaned, parent.Pid, parent.ExeName, Invariant($"its parent (pid {parent.Pid}) is gone: that pid now belongs to {parent.ExeName}, created after it"))
            : Live(parent, parentCreated, byPid, created);
    }

    /// <summary>The instances per owner, the largest group first: the "35 under one wsl.exe" line.</summary>
    public static IReadOnlyList<WindowsMcpOwnerGroup> Groups(IReadOnlyList<WindowsMcpInstance> instances) =>
    [
        .. instances
            .GroupBy(i => (i.Owner.Kind, Parent: GroupName(i.Owner)))
            .Select(g => new WindowsMcpOwnerGroup(g.Key.Kind, g.Key.Parent, g.Count()))
            .OrderByDescending(g => g.Count)
            .ThenBy(g => g.Kind, StringComparer.Ordinal)
            .ThenBy(g => g.Parent, StringComparer.Ordinal),
    ];

    /// <summary>A live parent: an agent, a WSL connection, an agent further up, or another program.</summary>
    private static WindowsMcpOwner Live(WindowsProcessEntry parent, Reading<DateTimeOffset> parentCreated, IReadOnlyDictionary<int, WindowsProcessEntry> byPid, Func<int, Reading<DateTimeOffset>> created) =>
        WindowsMcpCatalogue.IsAgent(parent.ExeName) ? new(WindowsMcpOwner.Agent, parent.Pid, parent.ExeName, Label(parent))
        : WindowsMcpCatalogue.IsWsl(parent.ExeName) ? new(WindowsMcpOwner.Interop, parent.Pid, parent.ExeName, $"{Label(parent)}: a WSL interop child; its caller in the distro is not visible from Windows")
        : AgentAbove(parent, parentCreated, byPid, created) is { } agent ? new(WindowsMcpOwner.Agent, parent.Pid, parent.ExeName, Label(agent))
        : new(WindowsMcpOwner.Other, parent.Pid, parent.ExeName, Label(parent));

    /// <summary>The first agent above <paramref name="from"/>; the walk ends at a gone ancestor, at one created after the process below
    /// it (a reused pid owns nothing), and at a loop a torn snapshot could hold.</summary>
    private static WindowsProcessEntry? AgentAbove(WindowsProcessEntry from, Reading<DateTimeOffset> fromCreated, IReadOnlyDictionary<int, WindowsProcessEntry> byPid, Func<int, Reading<DateTimeOffset>> created)
    {
        var visited = new HashSet<int> { from.Pid };
        var (below, belowCreated) = (from, fromCreated);
        while (byPid.TryGetValue(below.ParentPid, out var up) && visited.Add(up.Pid))
        {
            var upCreated = created(up.Pid);
            if (Newer(upCreated, belowCreated))
            {
                return null;
            }

            if (WindowsMcpCatalogue.IsAgent(up.ExeName))
            {
                return up;
            }

            (below, belowCreated) = (up, upCreated);
        }

        return null;
    }

    /// <summary>Proven newer: both creation times read and the first after the second.</summary>
    private static bool Newer(Reading<DateTimeOffset> candidate, Reading<DateTimeOffset> than) =>
        candidate is Reading<DateTimeOffset>.Available { Value: var a } && than is Reading<DateTimeOffset>.Available { Value: var b } && a > b;

    private static string GroupName(WindowsMcpOwner owner) => owner.Kind switch
    {
        WindowsMcpOwner.Orphaned => NoParent,
        WindowsMcpOwner.Agent => owner.Detail,
        _ => Label(owner.ParentName, owner.ParentPid),
    };

    private static string Label(WindowsProcessEntry process) => Label(process.ExeName, process.Pid);

    private static string Label(string exeName, int pid) => Invariant($"{exeName} (pid {pid})");

    private static string Invariant(FormattableString text) => text.ToString(CultureInfo.InvariantCulture);
}
