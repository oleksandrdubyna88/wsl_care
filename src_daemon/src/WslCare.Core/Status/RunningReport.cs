using System.Globalization;

using WslCare.Core.Actions.Engine;
using WslCare.Core.Files;
using WslCare.Core.Hosting;
using WslCare.Core.Records;

namespace WslCare.Core.Status;

/// <summary>The closed set of <see cref="RunningReport.State"/> names (plan §15j M3).</summary>
public static class RunningStateName
{
    /// <summary>No run is acting and none is queued.</summary>
    public const string None = "none";

    /// <summary>A run was accepted (a request file, E6.S1) and has not started yet.</summary>
    public const string Queued = "queued";

    /// <summary>A run is acting: its process is alive and its heartbeat fresh.</summary>
    public const string Live = "live";

    /// <summary>A run's process is alive but its heartbeat is stale — nothing is killed; a person (or E6.S1's
    /// <c>act --stop</c>) decides.</summary>
    public const string Wedged = "wedged";

    /// <summary>The run's process is gone (or is another process now) and no run has swept its <c>running.json</c> yet —
    /// REPORTED, never cleaned: the next root run sweeps it with an <c>interrupted</c> record.</summary>
    public const string Dead = "dead";

    /// <summary>The run's process cannot be inspected.</summary>
    public const string Unknown = "unknown";

    /// <summary><c>running.json</c> (or a request file, while nothing else is in flight) cannot be read or does not parse.</summary>
    public const string Unreadable = "unreadable";

    public static IReadOnlyList<string> All { get; } = [None, Queued, Live, Wedged, Dead, Unknown, Unreadable];
}

/// <summary>
/// <c>status --json</c>'s <c>running</c> block (plan §6, §15f #7, §15j M3): what the extension derives "Cleaning…" from,
/// so a reload mid-cleanup still shows the truth — the extension never reads <c>running.json</c> raw. Every member but
/// <see cref="State"/> is absent where the state does not have it (the context writes no nulls).
/// </summary>
/// <param name="State">One of <see cref="RunningStateName"/>.</param>
/// <param name="Reason">What the state means here, in words; absent for <c>none</c>.</param>
public sealed record RunningReport(string State, string? Reason)
{
    public string? RunId { get; init; }

    /// <summary>The action ids the run (or the request) holds — <c>["collect"]</c> for a full run.</summary>
    public IReadOnlyList<string>? Actions { get; init; }

    /// <summary>The action it is on now (empty before the first).</summary>
    public string? Current { get; init; }

    public string? Trigger { get; init; }

    /// <summary>The run's process — named so a wedged run outside the units can be stopped by hand (§15j M4).</summary>
    public int? Pid { get; init; }

    public DateTimeOffset? StartedAt { get; init; }

    public DateTimeOffset? HeartbeatAt { get; init; }

    /// <summary>The heartbeat's age at the answer, whole seconds.</summary>
    public double? HeartbeatAgeSeconds { get; init; }

    /// <summary>When the queued request was written.</summary>
    public DateTimeOffset? QueuedAt { get; init; }

    /// <summary>How many requests are waiting (queued: the oldest is the one described).</summary>
    public int? Queued { get; init; }
}

/// <summary>
/// The <c>running</c> block, read — READ-ONLY by construction: it judges <c>running.json</c> with
/// <see cref="RunningState.Read(IHostPaths, IFileSystem, IProcessTable, DateTimeOffset, RunningReadRetry)"/> (which writes
/// nothing) and lists the request files with <see cref="RunRequests.List"/>; it never calls the sweep. <c>status</c> is
/// unprivileged (plan §15b #3), so a dead run is REPORTED, never cleaned.
/// </summary>
public static class RunningReports
{
    /// <summary>Why a request of an earlier boot is reported dead (coai E6 code round #6).</summary>
    public const string EarlierBootReason = "written in an earlier boot; systemd never started it - the next root run records it interrupted";

    /// <summary>The block: <c>running.json</c> judged; with no holder, the requests — and when they show nothing (or a file
    /// vanished as it was read), <c>running.json</c> ONCE more, because states move request → running.json → history line and
    /// a request that just disappeared has just become a run (E6.S0 review D4). <paramref name="history"/> is what the caller
    /// already read: a dead holder whose run has a line is not "dead, nothing recorded it" (review D3).</summary>
    public static RunningReport Read(IHostPaths paths, IFileSystem files, IProcessTable processes, DateTimeOffset now, RunningReadRetry retry, HistoryRead history) =>
        OfHolder(RunningState.Read(paths, files, processes, now, retry), history) ?? AfterRequests(paths, files, processes, now, retry, history);

    private static RunningReport AfterRequests(IHostPaths paths, IFileSystem files, IProcessTable processes, DateTimeOffset now, RunningReadRetry retry, HistoryRead history) =>
        FromRequests(RunRequests.Peek(paths, files), processes.Boot()) is { State: not RunningStateName.None } fromRequests
            ? fromRequests
            : OfHolder(RunningState.Read(paths, files, processes, now, retry), history) ?? new RunningReport(RunningStateName.None, null);

    /// <summary>The block of a holder of <c>running.json</c> (<c>runs show</c> asks it too); <c>null</c> when there is none.</summary>
    public static RunningReport? OfHolder(RunningStatus status, HistoryRead history) => status switch
    {
        RunningStatus.None => null,
        RunningStatus.Live live => Of(RunningStateName.Live, $"run {live.File.RunId} is acting ({Doing(live.File)})", live.File, live.HeartbeatAge),
        RunningStatus.Wedged w => Of(RunningStateName.Wedged, RunningSweep.WedgedReason(w), w.File, w.HeartbeatAge),
        RunningStatus.Dead dead => Dead(dead, history),
        RunningStatus.Unknown unknown => Of(RunningStateName.Unknown, $"{unknown.Reason}; nothing was killed", unknown.File, null),
        RunningStatus.Unreadable unreadable => new RunningReport(RunningStateName.Unreadable, unreadable.Reason),
        _ => throw new System.Diagnostics.UnreachableException("RunningStatus is a closed set"),
    };

    /// <summary>A dead holder. When its run HAS a history line it recorded itself and died before removing its file: nothing
    /// is in flight, so the state is <c>none</c>, the reason naming the left-over file (review D3 — chosen over "dead with
    /// the true reason" because the panel asks "is something running", and nothing is); otherwise <c>dead</c>.</summary>
    private static RunningReport Dead(RunningStatus.Dead dead, HistoryRead history) =>
        history.Records.LastOrDefault(r => r.RunId == dead.File.RunId) is { } line
            ? new RunningReport(RunningStateName.None, $"run {dead.File.RunId} recorded itself as {OutcomeName(line.Outcome)}; only its running.json is left (the next root run removes it)")
            : Of(RunningStateName.Dead, $"run {dead.File.RunId} died: {dead.Why}; nothing recorded it yet - the next root run (collect or act) sweeps it as interrupted", dead.File, null);

    /// <summary>The outcome as the history line spells it (<c>completed</c>, <c>observeOnly</c>, …).</summary>
    private static string OutcomeName(RunOutcome outcome) => outcome.ToString() is var name ? char.ToLowerInvariant(name[0]) + name[1..] : string.Empty;

    /// <summary>The block of a run that holds <c>running.json</c>. The pid and the heartbeat's age only where the process IS
    /// the run — live or wedged (<paramref name="heartbeatAge"/> given): a dead run's pid is gone or another process's, an
    /// uninspectable one cannot be acted on (E6.S0 review S2 / S3; M4 tells a person to stop a wedged run by its pid).</summary>
    public static RunningReport Of(string state, string reason, RunningFile file, TimeSpan? heartbeatAge) =>
        new(state, reason)
        {
            RunId = file.RunId.Text,
            Actions = file.Actions,
            Current = file.Current,
            Trigger = TriggerName(file.Trigger),
            Pid = heartbeatAge is null ? null : file.Pid,
            StartedAt = file.StartedAt,
            HeartbeatAt = file.HeartbeatAt,
            HeartbeatAgeSeconds = heartbeatAge is { } age ? Math.Max(0, Math.Round(age.TotalSeconds)) : null,
        };

    /// <summary>The block of a queued request.</summary>
    public static RunningReport OfRequest(RunRequestFile request, int waiting) =>
        new(RunningStateName.Queued, Waiting(request, waiting))
        {
            RunId = request.RunId.Text,
            Actions = request.Actions,
            Trigger = TriggerName(request.Trigger),
            QueuedAt = request.CreatedAt,
            Queued = waiting,
        };

    /// <summary>Whether a request was written in another boot than this one — its job died with that boot, so systemd never
    /// starts it (coai E6 code round #6). False when either side cannot tell.</summary>
    public static bool OfAnEarlierBoot(RunRequestFile request, BootClock boot) =>
        request.BootId.Length > 0 && boot.Known && request.BootId != boot.BootId;

    /// <summary>No run holds <c>running.json</c>: the oldest request by name (<see cref="RunRequests.Peek"/>, the next one only when
    /// the oldest cannot be used) is queued — or DEAD when an earlier boot wrote it (#6; read-only: the next root run's sweep
    /// records it interrupted); unusable ones make the state unreadable, naming the first; none at all is <c>none</c>. The count
    /// is every request filed.</summary>
    private static RunningReport FromRequests((IReadOnlyList<RunRequestRead> Oldest, int Count) requests, BootClock boot)
    {
        if (requests.Oldest.OfType<RunRequestRead.Parsed>().FirstOrDefault() is { } parsed)
        {
            return OfAnEarlierBoot(parsed.File, boot)
                ? OfRequest(parsed.File, requests.Count) with { State = RunningStateName.Dead, Reason = EarlierBootReason }
                : OfRequest(parsed.File, requests.Count);
        }

        return requests.Oldest.OfType<RunRequestRead.Bad>().FirstOrDefault() is { } bad
            ? new RunningReport(RunningStateName.Unreadable, $"the request {bad.Path} {bad.Why}; nothing else is in flight")
            : new RunningReport(RunningStateName.None, null);
    }

    private static string Waiting(RunRequestFile request, int waiting) =>
        string.Create(CultureInfo.InvariantCulture, $"run {request.RunId} ({string.Join(",", request.Actions)}) was accepted and has not started yet{(waiting > 1 ? $"; {waiting} requests are waiting" : string.Empty)}");

    private static string Doing(RunningFile file) => file.Current.Length > 0 ? file.Current : "before its first action";

    /// <summary>The trigger as the JSON writes it everywhere else (<c>timer</c>, <c>manual</c>, <c>cli</c>).</summary>
    public static string TriggerName(RunTrigger trigger) => trigger switch
    {
        RunTrigger.Timer => "timer",
        RunTrigger.Manual => "manual",
        _ => "cli",
    };
}
