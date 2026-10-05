using System.ComponentModel;
using System.Diagnostics;

using WslCare.Core.Collectors;
using WslCare.Core.Collectors.Procfs;

namespace WslCare.Core.Actions.Engine;

/// <summary>What the operating system says about one pid.</summary>
public abstract record ProcessLookup
{
    private ProcessLookup()
    {
    }

    /// <summary>A process with that pid runs, and started then.</summary>
    /// <param name="StartUtc">Its start on the WALL clock — what .NET derives from the boot time, so it moves when the clock
    /// steps (E6.S0 review D1): display only where <see cref="StartTicks"/> is known.</param>
    public sealed record Alive(DateTimeOffset StartUtc) : ProcessLookup
    {
        /// <summary>Linux: <c>starttime</c>, field 22 of <c>/proc/[pid]/stat</c> — clock ticks after boot, which no clock step
        /// moves: the process's identity within one boot. <c>null</c> where it cannot be read (Windows).</summary>
        public long? StartTicks { get; init; }
    }

    /// <summary>No process has that pid.</summary>
    public sealed record Gone : ProcessLookup;

    /// <summary>It could not be inspected (access denied, …) — never read as gone.</summary>
    public sealed record Unknown(string Reason) : ProcessLookup;
}

/// <summary>This boot, and the system-wide monotonic clock now (E6.S0 review D1): a <c>running.json</c> written by another
/// process is judged by these when the wall clock has moved since. An empty <paramref name="BootId"/> means unknown — the
/// readers then fall back to the wall clock.</summary>
/// <param name="BootId">Linux: <c>/proc/sys/kernel/random/boot_id</c>.</param>
/// <param name="MonotonicMilliseconds">Milliseconds since boot that no clock step moves (<see cref="Environment.TickCount64"/>,
/// system-wide on Linux and Windows).</param>
public sealed record BootClock(string BootId, long MonotonicMilliseconds)
{
    public static readonly BootClock Unknown = new(string.Empty, 0);

    public bool Known => BootId.Length > 0;
}

/// <summary>The operating system's process table, for the <c>running.json</c> liveness check; tests answer from a script.</summary>
public interface IProcessTable
{
    ProcessLookup Lookup(int pid);

    /// <summary>This boot and its monotonic clock; unknown by default (a table that cannot tell).</summary>
    BootClock Boot() => BootClock.Unknown;
}

/// <summary>
/// The real table: <see cref="Process.GetProcessById(int)"/> and its start time — inspected, never started or killed
/// (the architecture test keeps <c>Process.Start</c> in the one launcher) — and on Linux the boot-relative start ticks and
/// the boot id, read from the REAL <c>/proc</c>.
/// </summary>
/// <remarks>The REAL process table, also under <c>WSL_CARE_ROOT</c>: a pid is a fact about this machine, not about the
/// sandbox's fixture <c>/proc</c>.</remarks>
public sealed class SystemProcessTable : IProcessTable
{
    private const string BootIdFile = "/proc/sys/kernel/random/boot_id";

    public ProcessLookup Lookup(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return process.HasExited ? new ProcessLookup.Gone() : new ProcessLookup.Alive(new DateTimeOffset(process.StartTime.ToUniversalTime(), TimeSpan.Zero)) { StartTicks = StartTicks(pid) };
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException)
        {
            // No such pid — or it exited between the lookup and the read.
            return new ProcessLookup.Gone();
        }
        catch (Exception e) when (e is Win32Exception or NotSupportedException or UnauthorizedAccessException)
        {
            return new ProcessLookup.Unknown(e.Message);
        }
    }

    public BootClock Boot() => OperatingSystem.IsLinux() && ReadText(BootIdFile).Trim() is { Length: > 0 } id
        ? new BootClock(id, Environment.TickCount64)
        : BootClock.Unknown;

    /// <summary>Field 22 of <c>/proc/[pid]/stat</c>, through the collectors' own parser; <c>null</c> off Linux or when unread.</summary>
    private static long? StartTicks(int pid) =>
        OperatingSystem.IsLinux() && ProcStat.Parse(ReadText($"/proc/{pid}/stat"), $"/proc/{pid}/stat") is Reading<ProcStat>.Available { Value: var stat } ? stat.StartTicks : null;

    private static string ReadText(string path)
    {
        try
        {
            return File.ReadAllText(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return string.Empty;
        }
    }
}

/// <summary>A table that can tell no pid — the safe default of a context built without the real one: a <c>running.json</c>
/// judged against it is never swept as dead (an unknown pid refuses like wedged, E3.S1).</summary>
public sealed class UnknownProcessTable : IProcessTable
{
    public static readonly UnknownProcessTable Instance = new();

    public ProcessLookup Lookup(int pid) => new ProcessLookup.Unknown("no process table was given to this run");
}
