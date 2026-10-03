using System.ComponentModel;
using System.Diagnostics;

namespace WslCare.Core.Actions.Engine;

/// <summary>What the operating system says about one pid.</summary>
public abstract record ProcessLookup
{
    private ProcessLookup()
    {
    }

    /// <summary>A process with that pid runs, and started then.</summary>
    public sealed record Alive(DateTimeOffset StartUtc) : ProcessLookup;

    /// <summary>No process has that pid.</summary>
    public sealed record Gone : ProcessLookup;

    /// <summary>It could not be inspected (access denied, …) — never read as gone.</summary>
    public sealed record Unknown(string Reason) : ProcessLookup;
}

/// <summary>The operating system's process table, for the <c>running.json</c> liveness check; tests answer from a script.</summary>
public interface IProcessTable
{
    ProcessLookup Lookup(int pid);
}

/// <summary>
/// The real table: <see cref="Process.GetProcessById(int)"/> and its start time — inspected, never started or killed
/// (the architecture test keeps <c>Process.Start</c> in the one launcher).
/// </summary>
/// <remarks>The REAL process table, also under <c>WSL_CARE_ROOT</c>: a pid is a fact about this machine, not about the
/// sandbox's fixture <c>/proc</c>.</remarks>
public sealed class SystemProcessTable : IProcessTable
{
    public ProcessLookup Lookup(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return process.HasExited ? new ProcessLookup.Gone() : new ProcessLookup.Alive(new DateTimeOffset(process.StartTime.ToUniversalTime(), TimeSpan.Zero));
        }
        catch (ArgumentException)
        {
            return new ProcessLookup.Gone();
        }
        catch (InvalidOperationException)
        {
            // It exited between the lookup and the read.
            return new ProcessLookup.Gone();
        }
        catch (Exception e) when (e is Win32Exception or NotSupportedException or UnauthorizedAccessException)
        {
            return new ProcessLookup.Unknown(e.Message);
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
