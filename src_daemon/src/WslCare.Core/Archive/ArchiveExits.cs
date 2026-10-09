namespace WslCare.Core.Archive;

/// <summary>
/// The exit codes of the archive's children — ONE contract (plan §15r E9.S4 own review round C-1): the command line's
/// <c>ExitCode</c> takes its values from here, and root (A13, A20) reads a child's exit by the same names. A child that answers —
/// done, stopped, refused, unreachable, busy — prints its answer line whatever its exit; root believes the line only with one of
/// <see cref="Answering"/>.
/// </summary>
public static class ArchiveExits
{
    /// <summary>The verb did its work, or stopped at a limit, or there is no base.</summary>
    public const int Ok = 0;

    /// <summary>Refused, unreachable, stopped on a fault — or a restore that refused a session.</summary>
    public const int RunFailed = 1;

    /// <summary>Another archive run of this side holds its lock; nothing was done (<c>EX_TEMPFAIL</c>: try again later).</summary>
    public const int Busy = 75;

    /// <summary>Every exit a child prints its answer line with.</summary>
    public static IReadOnlyList<int> Answering { get; } = [Ok, RunFailed, Busy];
}
