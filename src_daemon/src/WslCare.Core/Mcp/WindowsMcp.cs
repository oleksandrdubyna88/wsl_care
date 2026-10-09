using WslCare.Core.Collectors;

namespace WslCare.Core.Mcp;

/// <summary>One process of the Windows process table: what a Toolhelp snapshot names for every process.</summary>
public sealed record WindowsProcessEntry(int Pid, int ParentPid, string ExeName);

/// <summary>What a query-only handle to ONE process answers — each figure unavailable with its reason when the process could not be
/// opened (another account's, a protected one, gone), never 0.</summary>
public sealed record WindowsProcessDetails(Reading<DateTimeOffset> Created, Reading<TimeSpan> CpuTime, Reading<long> WorkingSet, Reading<long> PrivateBytes, Reading<int> SessionId)
{
    public static WindowsProcessDetails Unopenable(string why) =>
        new(Reading.Missing<DateTimeOffset>(why), Reading.Missing<TimeSpan>(why), Reading.Missing<long>(why), Reading.Missing<long>(why), Reading.Missing<int>(why));
}

/// <summary>The Windows process table (E14 S7a) — a seam: the real one is <see cref="Win32ProcessTable"/>, a test scripts its own.
/// READ-ONLY by construction: it lists, and it opens one process at a time for query rights only.</summary>
public interface IWindowsProcessTable
{
    Reading<IReadOnlyList<WindowsProcessEntry>> List();

    WindowsProcessDetails Details(int pid);
}

/// <summary>A process table that reads nothing — what a host built by a test, and every host off Windows, holds: the real table is
/// wired only by the Windows binary's own host, so a test's answer never depends on the processes of the machine it runs on.</summary>
public sealed class UnreadWindowsProcessTable(string why) : IWindowsProcessTable
{
    public Reading<IReadOnlyList<WindowsProcessEntry>> List() => Reading.Missing<IReadOnlyList<WindowsProcessEntry>>(why);

    public WindowsProcessDetails Details(int pid) => WindowsProcessDetails.Unopenable(why);
}

/// <summary>Who holds a Windows MCP instance.</summary>
/// <param name="Kind"><see cref="Agent"/>, <see cref="Interop"/>, <see cref="Orphaned"/> or <see cref="Other"/>.</param>
/// <param name="ParentPid">The direct parent's pid (as the snapshot names it).</param>
/// <param name="ParentName">The direct parent's exe name; empty when it is gone.</param>
/// <param name="Detail">The agent and its pid, the connection, or why it is orphaned.</param>
public sealed record WindowsMcpOwner(string Kind, int ParentPid, string ParentName, string Detail)
{
    public const string Agent = "agent";
    public const string Interop = "interop";
    public const string Orphaned = "orphaned";
    public const string Other = "other";
}

/// <summary>One Windows MCP server instance.</summary>
public sealed record WindowsMcpInstance(int Pid, string Server, WindowsMcpOwner Owner, Reading<DateTimeOffset> Created, Reading<double> CpuPercent, Reading<long> WorkingSet, Reading<long> PrivateBytes, Reading<int> SessionId, bool Idle);

/// <summary>How many instances one owner holds — the "35 under one wsl.exe" line.</summary>
public sealed record WindowsMcpOwnerGroup(string Kind, string Parent, int Count);

/// <summary>The Windows side's MCP servers in one sample.</summary>
public sealed record WindowsMcpSample(int WindowMilliseconds, IReadOnlyList<WindowsMcpInstance> Instances, IReadOnlyList<WindowsMcpInstance> Listed, IReadOnlyList<WindowsMcpOwnerGroup> Owners)
{
    public int Count => Instances.Count;

    public int IdleCount => Instances.Count(i => i.Idle);

    public int OrphanedCount => Instances.Count(i => i.Owner.Kind == WindowsMcpOwner.Orphaned);
}
