namespace WslCare.Core.Archive;

/// <summary>
/// Why a run stopped before its work was done — a closed set (correctness review M9): a LIMIT (the budget, the session cap, the base's
/// free space, a stop asked for) leaves the rest for the next run and exits 0; a FAULT (a base that does not keep what it is given, the
/// local state or the month index that could not be written, a base that failed mid-copy) exits non-zero. Never told by the text.
/// </summary>
public static class StopKinds
{
    public const string None = "";
    public const string Budget = "budget";
    public const string SessionCap = "session-cap";
    public const string FreeSpace = "free-space";
    public const string Cancelled = "cancelled";
    public const string Verification = "verification-failed";
    public const string StateWrite = "state-write";
    public const string IndexWrite = "index-write";
    public const string BaseFailed = "base-failed";

    private static readonly IReadOnlySet<string> Faults = new HashSet<string>(StringComparer.Ordinal) { Verification, StateWrite, IndexWrite, BaseFailed };

    /// <summary>Whether a stop of this kind is a fault (the run exits non-zero), not a limit.</summary>
    public static bool IsFault(string kind) => Faults.Contains(kind);
}

/// <summary>A run's stop: its kind (<see cref="StopKinds"/>) and the sentence; <see cref="None"/> when the run was not stopped.</summary>
public sealed record RunStop(string Kind, string Why)
{
    public static RunStop None { get; } = new(StopKinds.None, string.Empty);

    public bool Stopped => Kind.Length > 0;
}
