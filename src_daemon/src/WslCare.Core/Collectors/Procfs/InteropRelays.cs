using System.Globalization;
using System.Text.RegularExpressions;

using WslCare.Core.Config;
using WslCare.Core.Files;
using WslCare.Core.Hosting;
using WslCare.Core.Mcp;

namespace WslCare.Core.Collectors.Procfs;

/// <summary>A relay's parent as A21 judges it: its pid, its <c>comm</c> and its REAL uid (-1 when unreadable).</summary>
public sealed record RelayParent(int Pid, string Comm, int Uid);

/// <summary>One of a relay's fd 0, 1 and 2: the link's target (<c>pipe:[…]</c>, <c>socket:[…]</c>, a path), or empty with why.</summary>
public sealed record StdioLink(int Fd, string Target, string Problem)
{
    public bool IsPipe => Target.StartsWith("pipe:[", StringComparison.Ordinal);

    public bool IsSocket => Target.StartsWith("socket:[", StringComparison.Ordinal);
}

/// <summary>The <c>/proc</c> facts one interop relay is judged on (plan E14 S7b.2): who it is, which catalogued Windows program it
/// relays, its parent, and its stdio.</summary>
public sealed record RelayFacts(int Pid, string Program, RelayParent Parent, IReadOnlyList<StdioLink> Stdio);

/// <summary>
/// The WSL interop relays of Windows MCP servers (plan E14 S7b.2; measured in <c>research/2026-10-09_interop_relays.md</c>): a
/// Windows program started from the distro runs as a Linux process whose <c>/proc/&lt;pid&gt;/exe</c> is <c>/init</c> and whose
/// argv is <c>/init &lt;the program's path&gt; …</c>. Ending that relay ends its Windows child. Read-only: the exe link, the raw
/// argv, the parent's <c>comm</c> and uid, and fd 0–2's links.
/// </summary>
public static partial class InteropRelays
{
    /// <summary>The relay's exe and argv[0]: WSL's init (binfmt_misc <c>WSLInterop</c>).</summary>
    public const string Init = "/init";

    private const int StdioCount = 3;

    /// <summary>The CATALOGUE servers a relay may be of (plan E14 S7b.2 item 1): <c>creds-mcp</c>, and <c>coai-mcp</c> when
    /// <c>mcpServers.watched</c> holds it — never the user's <c>mcpServers.programs</c> (a name the user chose may be another
    /// program, and every client-gone relay is an orphan by construction).</summary>
    public static IReadOnlyList<McpServerEntry> Servers(EffectiveConfig config) =>
        [.. WindowsMcpCatalogue.Servers(config).Where(s => s.Origin != McpServerOrigin.UserProgram)];

    /// <summary>The first pass, on the snapshot alone: argv's program is <c>init</c> and its next word one of <paramref name="servers"/>'
    /// programs, compared without case as the Windows side compares (<see cref="WindowsMcpCatalogue.ServerOf"/>); <c>null</c> for none.
    /// <see cref="Read"/> confirms it from the exe link and the raw argv.</summary>
    public static McpServerEntry? ServerOf(ProcessEntry process, IReadOnlyList<McpServerEntry> servers) =>
        process.Programs is ["init", var program, ..] ? Server(program, servers) : null;

    /// <summary>The program a relay's argv names — <c>argv[0]</c> is <c>/init</c> and <c>argv[1]</c> ends in <c>.exe</c> (any case;
    /// no <c>/mnt/</c> prefix is required, <c>[automount] root=</c> moves it) — as a file name without <c>.exe</c>; empty otherwise.</summary>
    public static string ProgramOf(IReadOnlyList<string> argv) =>
        argv is [Init, var exe, ..] && exe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? CommandLineText.FileNameOf(exe) : string.Empty;

    /// <summary>The facts of <paramref name="process"/> when it IS a relay of one of <paramref name="servers"/> — its exe link is
    /// <c>/init</c> and its raw argv names the program; <c>null</c> otherwise, or when it is gone.</summary>
    public static RelayFacts? Read(IFileSystem files, LinuxHostPaths linux, ProcessEntry process, IReadOnlyList<McpServerEntry> servers)
    {
        var dir = Dir(linux, process.Pid);
        if (files.ReadLink($"{dir}/exe") is not LinkReadResult.Target { Path: Init })
        {
            return null;
        }

        var argv = ProcText.Bytes(files, $"{dir}/cmdline").Map(bytes => CommandLineText.Arguments(bytes)).ValueOr([]);
        var program = ProgramOf(argv);
        return program.Length == 0 || Server(program, servers) is null
            ? null
            : new RelayFacts(process.Pid, program, Parent(files, linux, process.ParentPid), [.. Enumerable.Range(0, StdioCount).Select(fd => Stdio(files, dir, fd))]);
    }

    /// <summary>The <c>n</c> of a <c>Relay(n)</c> comm — the pid of the ONE command that session init was made for (measured:
    /// <c>Relay(220668)</c> is pid 220666 with child 220668) — or -1 when the comm is not exactly that shape.</summary>
    public static int RelayChild(string comm) =>
        RelayComm().Match(comm) is { Success: true } match && int.TryParse(match.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var child) ? child : -1;

    /// <summary>A REAPER a client-gone relay is re-parented to (plan E14 S7b.2 item 2): root's pid 1, or a root process whose comm is
    /// exactly <c>Relay(&lt;digits&gt;)</c> (the WSL session init) or <c>SessionLeader</c>. A non-root process of either name is not one.</summary>
    public static bool IsReaper(RelayParent parent) =>
        parent.Uid == 0 && (parent.Pid == 1 || RelayChild(parent.Comm) >= 0 || string.Equals(parent.Comm, "SessionLeader", StringComparison.Ordinal));

    /// <summary>The relay is its <c>Relay(n)</c>'s own command (its pid is <c>n</c>): <c>wsl.exe</c> started it, and its Windows-side
    /// caller may be alive.</summary>
    public static bool BornThere(RelayFacts relay) => RelayChild(relay.Parent.Comm) == relay.Pid;

    private static McpServerEntry? Server(string program, IReadOnlyList<McpServerEntry> servers) =>
        servers.FirstOrDefault(s => s.Programs.Contains(program, StringComparer.OrdinalIgnoreCase));

    private static string Dir(LinuxHostPaths linux, int pid) => $"{linux.ProcRoot}/{pid.ToString(CultureInfo.InvariantCulture)}";

    private static RelayParent Parent(IFileSystem files, LinuxHostPaths linux, int pid)
    {
        var dir = Dir(linux, pid);
        var comm = ProcText.Read(files, $"{dir}/comm").Map(text => text.TrimEnd('\n', '\r')).ValueOr(string.Empty);
        var uid = ProcText.Read(files, $"{dir}/status").Bind(text => ProcStatus.Parse(text, $"{dir}/status")).Map(status => status.Uid).ValueOr(-1);
        return new RelayParent(pid, comm, uid);
    }

    private static StdioLink Stdio(IFileSystem files, string dir, int fd) => files.ReadLink($"{dir}/fd/{fd.ToString(CultureInfo.InvariantCulture)}") switch
    {
        LinkReadResult.Target target => new StdioLink(fd, target.Path, string.Empty),
        LinkReadResult.Unreadable unreadable => new StdioLink(fd, string.Empty, $"fd {fd} could not be read: {unreadable.Reason}"),
        _ => new StdioLink(fd, string.Empty, $"fd {fd} is not open"),
    };

    /// <summary><c>Relay(&lt;digits&gt;)</c>, the whole comm (<c>\z</c>: .NET's <c>$</c> also matches before a final newline).</summary>
    [GeneratedRegex(@"^Relay\(([0-9]{1,10})\)\z", RegexOptions.CultureInvariant)]
    private static partial Regex RelayComm();
}
