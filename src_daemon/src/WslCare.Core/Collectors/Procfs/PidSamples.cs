using System.Globalization;

using WslCare.Core.Files;
using WslCare.Core.Hosting;

namespace WslCare.Core.Collectors.Procfs;

/// <summary>One pid as <c>/proc</c> answers now: who it is (its start ticks), the CPU ticks it used, its terminal, its owner.</summary>
public sealed record PidSample(int Pid, long StartTicks, long CpuTicks, int Tty, int Uid);

/// <summary>
/// Reads one pid's <c>stat</c> and <c>status</c> — the measurement A11, A18's CPU history and the MCP servers' CPU window share
/// (plan §15q E7.S2d, coai code round finding 1: extracted from A11 so a collector does not depend on an action).
/// </summary>
public static class PidSamples
{
    /// <summary>How far two readings of one process's start may differ and still be the same process: the kernel's tick and the
    /// boot time's rounding (the run state's liveness check and the MCP servers' log rule use it).</summary>
    public static readonly TimeSpan StartTolerance = TimeSpan.FromSeconds(2);

    /// <summary>The pid's start, CPU ticks, terminal and owner as <c>/proc</c> answers now; <c>null</c> when it cannot be read
    /// (gone — or never a target: an unread process is not signalled).</summary>
    public static PidSample? Read(IFileSystem files, LinuxHostPaths linux, int pid)
    {
        var dir = $"{linux.ProcRoot}/{pid.ToString(CultureInfo.InvariantCulture)}";
        var stat = ProcText.Read(files, $"{dir}/stat").Bind(t => ProcStat.Parse(t, $"{dir}/stat"));
        var status = ProcText.Read(files, $"{dir}/status").Bind(t => ProcStatus.Parse(t, $"{dir}/status"));
        return Reading.Combine(stat, status, (s, st) => new PidSample(pid, s.StartTicks, s.CpuTicks, s.TtyNumber, st.Uid)) is Reading<PidSample>.Available { Value: var sample } ? sample : null;
    }
}
