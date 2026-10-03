using System.Globalization;
using System.Text.Json;

using WslCare.Core.Files;
using WslCare.Core.Files.Deletion;
using WslCare.Core.Hosting;
using WslCare.Core.Json;
using WslCare.Core.Records;

namespace WslCare.Core.Actions.Engine;

/// <summary>
/// <c>{state}/running.json</c> (plan §6 <i>Durable running state</i>, §15 #6): which run is acting, which action it is on,
/// its pid AND the process's start time, and a heartbeat rewritten every 5 s. The extension derives <i>Cleaning…</i>
/// from it, so a reload mid-cleanup still shows the truth.
/// </summary>
/// <param name="ProcessStartUtc">When the process with <paramref name="Pid"/> started, as the operating system reports it:
/// a reused pid has a different start, so it is <i>mismatched</i>, never mistaken for the run.</param>
public sealed record RunningFile(
    int SchemaVersion,
    RunId RunId,
    RunTrigger Trigger,
    IReadOnlyList<string> Actions,
    string Current,
    int Pid,
    DateTimeOffset ProcessStartUtc,
    DateTimeOffset StartedAt,
    DateTimeOffset HeartbeatAt);

/// <summary>What <c>running.json</c> says about the machine now — a closed set (plan §15 #6, §15a #0).</summary>
public abstract record RunningStatus
{
    private RunningStatus()
    {
    }

    /// <summary>No run is acting.</summary>
    public sealed record None : RunningStatus;

    /// <summary>A live process with the recorded start, heartbeat fresh: a run IS acting.</summary>
    public sealed record Live(RunningFile File) : RunningStatus;

    /// <summary>A live process with the recorded start, heartbeat stale: <i>wedged</i> — no new run starts, nothing is
    /// killed automatically (a button offers to stop it, E6).</summary>
    public sealed record Wedged(RunningFile File, TimeSpan HeartbeatAge) : RunningStatus;

    /// <summary>The pid is gone, or is a different process now: the run died — swept with an <c>interrupted</c> record.</summary>
    public sealed record Dead(RunningFile File, string Why) : RunningStatus;

    /// <summary>It cannot be told: the pid cannot be inspected. Treated like wedged — refuse, kill nothing — because a guess
    /// here is a second run beside a live one.</summary>
    public sealed record Unknown(string Reason) : RunningStatus;

    /// <summary>The file cannot be read or does not parse, even after brief retries (gate finding #7) — its own state, never
    /// taken for a wedged live process: refuse, kill nothing, and name the file and the reason.</summary>
    public sealed record Unreadable(string Reason) : RunningStatus;
}

/// <summary>How an unreadable or unparsable <c>running.json</c> is read again before any verdict: a writer replacing it (temp +
/// rename) can race a reader on Windows, and one bad read must not decide a run (gate finding #7).</summary>
/// <param name="Retries">Reads after the first, each after <paramref name="Delay"/>.</param>
/// <param name="Pause">The wait itself — real sleep, or a test's own.</param>
public sealed record RunningReadRetry(int Retries, TimeSpan Delay, Action<TimeSpan> Pause)
{
    /// <summary>Three more reads, 100 ms apart: at most 300 ms before a verdict.</summary>
    public static readonly RunningReadRetry Default = new(3, TimeSpan.FromMilliseconds(100), Thread.Sleep);
}

/// <summary>Reads, writes and judges <c>running.json</c>.</summary>
public static class RunningState
{
    public const string FileName = "running.json";

    /// <summary>Plan §6: the heartbeat is rewritten every 5 s.</summary>
    public static readonly TimeSpan HeartbeatPeriod = TimeSpan.FromSeconds(5);

    /// <summary>Plan §6: a reader treats a heartbeat older than 30 s as not beating.</summary>
    public static readonly TimeSpan StaleAfter = TimeSpan.FromSeconds(30);

    /// <summary>How far two readings of one process's start may differ and still be the same process — the operating
    /// system derives it from the boot time and a 10 ms tick, and two readers can disagree by less than this.</summary>
    public static readonly TimeSpan StartTolerance = TimeSpan.FromSeconds(2);

    private const string Action = "running-state";

    public static string File(IHostPaths paths) => paths.Rules.Join(paths.StateDirectory, FileName);

    public static RunningStatus Read(IHostPaths paths, IFileSystem files, IProcessTable processes, DateTimeOffset now) =>
        Read(paths, files, processes, now, RunningReadRetry.Default);

    /// <summary>What the file says now — read again up to <see cref="RunningReadRetry.Retries"/> times while it cannot be read or
    /// does not parse (gate finding #7), and only then judged: a file that never reads is <see cref="RunningStatus.Unreadable"/>.</summary>
    public static RunningStatus Read(IHostPaths paths, IFileSystem files, IProcessTable processes, DateTimeOffset now, RunningReadRetry retry)
    {
        var loaded = Load(paths, files);
        for (var attempt = 0; attempt < retry.Retries && loaded is Loaded.Bad; attempt++)
        {
            retry.Pause(retry.Delay);
            loaded = Load(paths, files);
        }

        return loaded switch
        {
            Loaded.Absent => new RunningStatus.None(),
            Loaded.Bad bad => new RunningStatus.Unreadable(string.Create(CultureInfo.InvariantCulture, $"{File(paths)} {bad.Why} (read {retry.Retries + 1} times, {retry.Delay.TotalMilliseconds:0} ms apart); remove it by hand once no wsl-care act runs")),
            Loaded.Parsed parsed => Judge(parsed.File, processes, now),
            _ => throw new System.Diagnostics.UnreachableException("Loaded is a closed set"),
        };
    }

    /// <summary>One read of the file: absent, parsed, or bad (unreadable or not a running record) with why.</summary>
    private abstract record Loaded
    {
        private Loaded()
        {
        }

        public sealed record Absent : Loaded;

        public sealed record Parsed(RunningFile File) : Loaded;

        public sealed record Bad(string Why) : Loaded;
    }

    private static Loaded Load(IHostPaths paths, IFileSystem files) => files.ReadFile(File(paths)) switch
    {
        FileReadResult.Missing => new Loaded.Absent(),
        FileReadResult.Unreadable u => new Loaded.Bad($"cannot be read ({u.Reason})"),
        FileReadResult.Content content => Parse(content.Bytes),
        _ => throw new System.Diagnostics.UnreachableException("FileReadResult is a closed set"),
    };
    /// <summary>Which run the file names, unjudged — <c>null</c> when there is no file or it does not parse.</summary>
    public static RunId? RunIdIn(IHostPaths paths, IFileSystem files) => Load(paths, files) is Loaded.Parsed parsed ? parsed.File.RunId : null;

    /// <summary>Written atomically (temp + rename) inside the state directory.</summary>
    public static DeletionVerdict Write(IHostPaths paths, IFileSystem files, RunningFile running)
    {
        files.CreateDirectory(paths.StateDirectory);
        return files.WriteFileAtomically(File(paths), JsonSerializer.SerializeToUtf8Bytes(running, WslCareJsonContext.Default.RunningFile), Scope(paths));
    }

    public static DeletionVerdict Remove(IHostPaths paths, IFileSystem files) =>
        files.FileExists(File(paths)) ? files.DeleteFile(File(paths), Scope(paths)) : DeletionVerdict.Allowed;

    private static DeletionScope Scope(IHostPaths paths) => new(paths.StateDirectory, Action);

    private static Loaded Parse(byte[] bytes)
    {
        try
        {
            return JsonSerializer.Deserialize(bytes, WslCareJsonContext.Default.RunningFile) is { RunId: not null, Actions: not null, Current: not null } file
                ? new Loaded.Parsed(file)
                : new Loaded.Bad("does not parse: it is not a running record");
        }
        catch (JsonException e)
        {
            return new Loaded.Bad($"does not parse ({e.Message})");
        }
    }

    private static RunningStatus Judge(RunningFile file, IProcessTable processes, DateTimeOffset now) => processes.Lookup(file.Pid) switch
    {
        ProcessLookup.Gone => new RunningStatus.Dead(file, $"pid {file.Pid} is gone"),
        ProcessLookup.Alive alive when (alive.StartUtc - file.ProcessStartUtc).Duration() > StartTolerance =>
            new RunningStatus.Dead(file, $"pid {file.Pid} is a different process now (started {alive.StartUtc:O}, the run's started {file.ProcessStartUtc:O})"),
        ProcessLookup.Alive => Beat(file, now),
        ProcessLookup.Unknown u => new RunningStatus.Unknown($"pid {file.Pid} of run {file.RunId} cannot be inspected ({u.Reason})"),
        _ => throw new System.Diagnostics.UnreachableException("ProcessLookup is a closed set"),
    };
    private static RunningStatus Beat(RunningFile file, DateTimeOffset now)
    {
        var age = now - file.HeartbeatAt;
        return age > StaleAfter ? new RunningStatus.Wedged(file, age) : new RunningStatus.Live(file);
    }
}
