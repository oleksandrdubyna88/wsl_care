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
    DateTimeOffset HeartbeatAt)
{
    /// <summary>Linux: the process's <c>starttime</c> (clock ticks after boot, <c>/proc/[pid]/stat</c> field 22) — its
    /// identity within <see cref="BootId"/>, which no wall-clock step moves (E6.S0 review D1). Absent from older writers.</summary>
    public long? StartTicks { get; init; }

    /// <summary>The boot the run started in (<c>/proc/sys/kernel/random/boot_id</c>); absent where it is unknown.</summary>
    public string? BootId { get; init; }

    /// <summary>The system-wide monotonic clock (milliseconds since boot) at the last heartbeat — the age a reader in the
    /// SAME boot computes, whatever the wall clock did meanwhile (E6.S0 review D1).</summary>
    public long? HeartbeatMonotonicMs { get; init; }
}

/// <summary>What <c>running.json</c> says about the machine now — a closed set (plan §15 #6, §15a #0).</summary>
public abstract record RunningStatus
{
    private RunningStatus()
    {
    }

    /// <summary>No run is acting.</summary>
    public sealed record None : RunningStatus;

    /// <summary>A live process with the recorded start, heartbeat fresh: a run IS acting.</summary>
    public sealed record Live(RunningFile File) : RunningStatus
    {
        /// <summary>How old its heartbeat is, as judged (monotonic within one boot, else the wall clock).</summary>
        public TimeSpan HeartbeatAge { get; init; }
    }

    /// <summary>A live process with the recorded start, heartbeat stale: <i>wedged</i> — no new run starts, nothing is
    /// killed automatically (a button offers to stop it, E6).</summary>
    public sealed record Wedged(RunningFile File, TimeSpan HeartbeatAge) : RunningStatus;

    /// <summary>The pid is gone, or is a different process now: the run died — swept with an <c>interrupted</c> record.</summary>
    public sealed record Dead(RunningFile File, string Why) : RunningStatus;

    /// <summary>It cannot be told: the pid cannot be inspected. Treated like wedged — refuse, kill nothing — because a guess
    /// here is a second run beside a live one. The file is kept, so a reader still knows WHICH run it is (E6.S0 review S2).</summary>
    public sealed record Unknown(RunningFile File, string Reason) : RunningStatus;

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

    /// <summary>The file a run writes for ITSELF (E6.S0 review D1): its boot-relative start ticks and the boot id beside the
    /// wall-clock start, and the monotonic stamp beside the heartbeat — what a reader in another process judges by when the
    /// wall clock moved. Left absent where the table cannot tell (Windows), so readers fall back as before.</summary>
    public static RunningFile Identified(RunningFile file, IProcessTable processes)
    {
        var boot = processes.Boot();
        var ticks = processes.Lookup(file.Pid) is ProcessLookup.Alive alive ? alive.StartTicks : null;
        return file with
        {
            StartTicks = ticks,
            BootId = boot.Known ? boot.BootId : null,
            HeartbeatMonotonicMs = boot.Known ? boot.MonotonicMilliseconds : null,
        };
    }

    /// <summary>The same file with a fresh heartbeat: the wall clock AND, when the file names its boot, the monotonic clock.</summary>
    public static RunningFile WithHeartbeat(RunningFile file, DateTimeOffset now, IProcessTable processes) =>
        file with { HeartbeatAt = now, HeartbeatMonotonicMs = file.BootId is null ? null : processes.Boot().MonotonicMilliseconds };

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

    private static RunningStatus Judge(RunningFile file, IProcessTable processes, DateTimeOffset now)
    {
        var boot = processes.Boot();
        return processes.Lookup(file.Pid) switch
        {
            ProcessLookup.Gone => new RunningStatus.Dead(file, $"pid {file.Pid} is gone"),
            ProcessLookup.Alive alive when Mismatch(file, alive, boot) is { Length: > 0 } why => new RunningStatus.Dead(file, why),
            ProcessLookup.Alive => Beat(file, HeartbeatAge(file, boot, now)),
            ProcessLookup.Unknown u => new RunningStatus.Unknown(file, $"pid {file.Pid} of run {file.RunId} cannot be inspected ({u.Reason})"),
            _ => throw new System.Diagnostics.UnreachableException("ProcessLookup is a closed set"),
        };
    }

    /// <summary>
    /// Why the live process with the file's pid is NOT the run; empty when it is (E6.S0 review D1). Within one boot the identity
    /// is the boot id plus the EXACT boot-relative start ticks — neither moves when the wall clock steps, which on this machine
    /// it does hundreds of times in four hours (and A16 steps it itself), while .NET's <c>Process.StartTime</c> is derived from
    /// a boot time computed off the wall clock and moves with it. Another boot is another process. Only a file or a reader
    /// that cannot tell (an older writer, the Windows binary) falls back to the wall-clock start within its tolerance.
    /// </summary>
    private static string Mismatch(RunningFile file, ProcessLookup.Alive alive, BootClock boot) =>
        boot.Known && file.BootId is { Length: > 0 } bootId && file.StartTicks is { } ticks && alive.StartTicks is { } now
            ? ByTicks(file, bootId, ticks, now, boot)
            : ByWallClock(file, alive);

    private static string ByTicks(RunningFile file, string bootId, long ticks, long now, BootClock boot) =>
        bootId != boot.BootId ? $"pid {file.Pid} belongs to another boot (the run's was {bootId})"
        : ticks != now ? string.Create(CultureInfo.InvariantCulture, $"pid {file.Pid} is a different process now (started at boot tick {now}, the run's at {ticks})")
        : string.Empty;

    private static string ByWallClock(RunningFile file, ProcessLookup.Alive alive) =>
        (alive.StartUtc - file.ProcessStartUtc).Duration() > StartTolerance
            ? $"pid {file.Pid} is a different process now (started {alive.StartUtc:O}, the run's started {file.ProcessStartUtc:O})"
            : string.Empty;

    /// <summary>The heartbeat's age: on the system-wide MONOTONIC clock when the file and the reader share a boot (a wall-clock
    /// step does not age it), else on the wall clock.</summary>
    private static TimeSpan HeartbeatAge(RunningFile file, BootClock boot, DateTimeOffset now) =>
        boot.Known && file.BootId == boot.BootId && file.HeartbeatMonotonicMs is { } stamp
            ? TimeSpan.FromMilliseconds(Math.Max(0, boot.MonotonicMilliseconds - stamp))
            : now - file.HeartbeatAt;

    private static RunningStatus Beat(RunningFile file, TimeSpan age) =>
        age > StaleAfter ? new RunningStatus.Wedged(file, age) : new RunningStatus.Live(file) { HeartbeatAge = age };
}
