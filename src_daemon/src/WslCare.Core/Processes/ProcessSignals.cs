using WslCare.Core.Config;
using System.Globalization;
using System.Runtime.InteropServices;

using WslCare.Core.Collectors;
using WslCare.Core.Collectors.Procfs;
using WslCare.Core.Files;

namespace WslCare.Core.Processes;

/// <summary>Which process: its pid AND its start (field 22 of <c>/proc/[pid]/stat</c>, clock ticks since boot) — a pid alone
/// can be reused, a pid with its start cannot (plan §15 #6).</summary>
public sealed record ProcessIdentity(int Pid, long StartTicks);

/// <summary>What asking one process to end produced — a closed set; only <see cref="Ended"/> means a process went.</summary>
public abstract record SignalOutcome
{
    private SignalOutcome()
    {
    }

    /// <summary>It ended: on <c>SIGTERM</c>, or — when it outlived the grace — on <c>SIGKILL</c>.</summary>
    public sealed record Ended(bool NeededKill) : SignalOutcome;

    /// <summary>No process has that pid any more: already gone, which is not a failure (plan §15a #0).</summary>
    public sealed record AlreadyGone : SignalOutcome;

    /// <summary>The pid now names ANOTHER process (its start differs): nothing was signalled.</summary>
    public sealed record NotTheSame(string Reason) : SignalOutcome;

    /// <summary>Killed, and still not ended within the wait (uninterruptible I/O): reported, never waited on forever.</summary>
    public sealed record StillRunning(string Reason) : SignalOutcome;

    /// <summary>This sender signals nothing (the sandbox, the Windows binary, a context that was not wired).</summary>
    public sealed record Refused(string Reason) : SignalOutcome;

    /// <summary>The operating system refused or failed a step; the reason names it.</summary>
    public sealed record Failed(string Reason) : SignalOutcome;
}

/// <summary>
/// The ONE way the product signals a process (plan §5 A11): <c>SIGTERM</c>, then <c>SIGKILL</c> after the grace — by
/// <see cref="ProcessIdentity"/>, never by name (the never-list refuses <c>pkill</c> / <c>killall</c> / <c>taskkill</c>
/// as commands; this seam is the only other road, and an architecture test keeps the signalling calls in this file).
/// </summary>
public interface IProcessSignals
{
    /// <summary><c>SIGTERM</c> to every process, ONE shared deadline of <paramref name="grace"/> across all of them, then
    /// <c>SIGKILL</c> to the survivors (gate finding #9); the outcomes in the order of <paramref name="processes"/>.</summary>
    Task<IReadOnlyList<SignalOutcome>> TerminateAllAsync(IReadOnlyList<ProcessIdentity> processes, TimeSpan grace, CancellationToken cancellationToken);

    /// <summary><c>SIGTERM</c> ONLY (plan E14 S7b.2: an interop relay is never killed): one <c>SIGTERM</c> each, ONE shared grace,
    /// and a survivor reported <see cref="SignalOutcome.StillRunning"/> — never a <c>SIGKILL</c>. A sender that does not
    /// implement it REFUSES, so no sender can reach the escalating call through it by accident.</summary>
    Task<IReadOnlyList<SignalOutcome>> TerminateOnlyAsync(IReadOnlyList<ProcessIdentity> processes, TimeSpan grace, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<SignalOutcome>>([.. processes.Select(_ => new SignalOutcome.Refused(NoTermOnlyMode))]);

    /// <summary>Why a sender without a SIGTERM-only mode signals nothing through it.</summary>
    const string NoTermOnlyMode = "this sender has no SIGTERM-only mode, so it signals nothing (it never falls back to SIGKILL)";
}

/// <summary>One process through <see cref="IProcessSignals.TerminateAllAsync"/>.</summary>
public static class ProcessSignalsExtensions
{
    public static async Task<SignalOutcome> TerminateAsync(this IProcessSignals signals, ProcessIdentity process, TimeSpan grace, CancellationToken cancellationToken) =>
        (await signals.TerminateAllAsync([process], grace, cancellationToken).ConfigureAwait(false))[0];
}

/// <summary>A sender that signals nothing and says why — the sandbox (<c>WSL_CARE_ROOT</c>: a fixture's pids are not this
/// machine's), the Windows binary, and the default of a context nobody wired.</summary>
public sealed class RefusingProcessSignals(string reason) : IProcessSignals
{
    public static readonly RefusingProcessSignals NotWired = new("no signal sender is wired into this run");

    public static readonly RefusingProcessSignals Sandboxed = new("sandboxed (WSL_CARE_ROOT): the process table is a fixture, so no real process is ever signalled");

    public string Reason => reason;

    public Task<IReadOnlyList<SignalOutcome>> TerminateAllAsync(IReadOnlyList<ProcessIdentity> processes, TimeSpan grace, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<SignalOutcome>>([.. processes.Select(_ => new SignalOutcome.Refused(reason))]);
}

/// <summary>The native calls the pidfd sender makes — a seam so a test can make one of them fail or a process never end;
/// <see cref="LibcPidfdCalls"/> is the only implementation the product builds.</summary>
internal interface IPidfdCalls
{
    /// <summary><c>pidfd_open</c>: the descriptor, or <c>-errno</c>.</summary>
    int Open(int pid);

    /// <summary><c>pidfd_send_signal</c>: 0, or the errno.</summary>
    int Signal(int fd, int signal);

    /// <summary><c>poll</c> of the descriptors for readability (a pidfd is readable once its process ended): how many are
    /// ready (their <paramref name="ready"/> flags set), 0 when none within <paramref name="milliseconds"/>, or
    /// <c>-errno</c>.</summary>
    int Poll(IReadOnlyList<int> fds, bool[] ready, int milliseconds);

    void Close(int fd);
}

/// <summary>
/// The Linux sender: a <c>pidfd</c> (<c>pidfd_open</c>, Linux 5.3; glibc 2.36 — Ubuntu 24.04 has 2.39) pins each process
/// FIRST, its start is then compared with the identity, and both signals go through that descriptor
/// (<c>pidfd_send_signal</c>), so the pid can never be reused between the check and the signal: if the process the
/// descriptor pins has already ended, a new holder of the pid has a later start and is refused as
/// <see cref="SignalOutcome.NotTheSame"/>. The ends are awaited on the descriptors themselves (one <c>poll</c> over all of
/// them → readable when a process exits), in short slices so a cancellation is seen.
/// </summary>
/// <remarks>
/// <para><b>One grace for all</b> (gate finding #9): <c>SIGTERM</c> goes to every pinned process, ONE deadline of the grace is
/// waited across all of them, then the survivors get <c>SIGKILL</c> together and one <see cref="KillWait"/> — three
/// processes that ignore <c>SIGTERM</c> take one grace, not three.</para>
/// <para><b>A poll that fails is not an end</b> (independent review of E3, item 6): any error but <c>EINTR</c> settles every
/// process still waited on as <see cref="SignalOutcome.Failed"/> — whether it ended is unknown, so nothing more is sent on
/// that guess.</para>
/// <para>Reads <c>/proc/[pid]/stat</c> under the proc root — always the REAL <c>/proc</c>: the CLI builds this sender only
/// outside a sandbox.</para>
/// </remarks>
public sealed class PidfdProcessSignals : IProcessSignals
{
    /// <summary>How long the processes get to end after <c>SIGKILL</c> before they are reported still running.</summary>
    public static TimeSpan KillWait => Tuning.Current.Seconds(ConfigKeys.Processes.KillWaitSeconds);

    private const int SigTerm = 15;
    private const int SigKill = 9;
    private const int Esrch = 3;
    private const int Eintr = 4;
    private static int SliceMilliseconds => Tuning.Current.Int(ConfigKeys.Processes.SignalSliceMilliseconds);

    private readonly IFileSystem _files;
    private readonly string _procRoot;
    private readonly IPidfdCalls _calls;
    private readonly TimeProvider _clock;

    public PidfdProcessSignals(IFileSystem files, string procRoot)
        : this(files, procRoot, new LibcPidfdCalls(), TimeProvider.System)
    {
    }

    /// <summary>The seams, for tests only: the native calls and the clock the waits are measured on.</summary>
    internal PidfdProcessSignals(IFileSystem files, string procRoot, IPidfdCalls calls, TimeProvider clock)
    {
        _files = files;
        _procRoot = procRoot;
        _calls = calls;
        _clock = clock;
    }

    public async Task<IReadOnlyList<SignalOutcome>> TerminateAllAsync(IReadOnlyList<ProcessIdentity> processes, TimeSpan grace, CancellationToken cancellationToken)
    {
        var slots = processes.Select(Pin).ToList();
        try
        {
            await WaitAsync(slots, grace, neededKill: false, cancellationToken).ConfigureAwait(false);
            foreach (var slot in slots.Where(s => s.Pending))
            {
                slot.Outcome = Kill(slot);
            }

            await WaitAsync(slots, KillWait, neededKill: true, cancellationToken).ConfigureAwait(false);
            return [.. slots.Select(s => s.Outcome ?? new SignalOutcome.StillRunning($"pid {s.Process.Pid} did not end within {KillWait.TotalSeconds:0} s of SIGKILL (uninterruptible I/O?)"))];
        }
        finally
        {
            foreach (var slot in slots.Where(s => s.Fd >= 0))
            {
                _calls.Close(slot.Fd);
            }
        }
    }

    /// <summary>SIGTERM ONLY (plan E14 S7b.2): the same pins and the same one grace, and the survivors reported still running —
    /// no SIGKILL, no kill wait.</summary>
    public async Task<IReadOnlyList<SignalOutcome>> TerminateOnlyAsync(IReadOnlyList<ProcessIdentity> processes, TimeSpan grace, CancellationToken cancellationToken)
    {
        var slots = processes.Select(Pin).ToList();
        try
        {
            await WaitAsync(slots, grace, neededKill: false, cancellationToken).ConfigureAwait(false);
            return [.. slots.Select(s => s.Outcome ?? new SignalOutcome.StillRunning(string.Create(CultureInfo.InvariantCulture, $"pid {s.Process.Pid} did not end within {grace.TotalSeconds:0} s of SIGTERM; no SIGKILL is sent")))];
        }
        finally
        {
            foreach (var slot in slots.Where(s => s.Fd >= 0))
            {
                _calls.Close(slot.Fd);
            }
        }
    }

    /// <summary>One process pinned, checked and sent <c>SIGTERM</c> — or settled at once (gone, another process, a failure).</summary>
    private Slot Pin(ProcessIdentity process)
    {
        var fd = _calls.Open(process.Pid);
        return fd >= 0
            ? new Slot(process, fd) { Outcome = Check(fd, process) ?? Send(fd, SigTerm, process.Pid) }
            : new Slot(process, -1) { Outcome = -fd == Esrch ? new SignalOutcome.AlreadyGone() : new SignalOutcome.Failed($"pidfd_open({process.Pid}) failed with errno {-fd}") };
    }

    /// <summary><c>SIGKILL</c> through the pin: <c>null</c> when sent (still waited on); gone in between counts as ended.</summary>
    private SignalOutcome? Kill(Slot slot) => Send(slot.Fd, SigKill, slot.Process.Pid) switch
    {
        SignalOutcome.AlreadyGone => new SignalOutcome.Ended(NeededKill: false),
        var other => other,
    };

    /// <summary>The start the pinned process reports, against the identity; <c>null</c> when they agree.</summary>
    private SignalOutcome? Check(int fd, ProcessIdentity process)
    {
        var path = $"{_procRoot}/{process.Pid.ToString(CultureInfo.InvariantCulture)}/stat";
        var stat = ProcText.Read(_files, path).Bind(text => ProcStat.Parse(text, path));
        return stat switch
        {
            Reading<ProcStat>.Available { Value.StartTicks: var start } when start == process.StartTicks => null,
            Reading<ProcStat>.Available { Value.StartTicks: var start } =>
                new SignalOutcome.NotTheSame($"pid {process.Pid} started at tick {start}, not {process.StartTicks}: it is another process now"),
            _ when _calls.Poll([fd], new bool[1], 0) > 0 => new SignalOutcome.AlreadyGone(),
            _ => new SignalOutcome.Failed($"pid {process.Pid}: {stat.ReasonOrEmpty}"),
        };
    }

    private SignalOutcome? Send(int fd, int signal, int pid) => _calls.Signal(fd, signal) switch
    {
        0 => null,
        Esrch => new SignalOutcome.AlreadyGone(),
        var errno => new SignalOutcome.Failed($"pidfd_send_signal(pid {pid}, {signal}) failed with errno {errno}"),
    };

    /// <summary>Waits ONE deadline across every slot still pending, settling each as its descriptor turns readable.</summary>
    private async Task WaitAsync(IReadOnlyList<Slot> slots, TimeSpan wait, bool neededKill, CancellationToken cancellationToken)
    {
        var deadline = _clock.GetUtcNow() + wait;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var pending = slots.Where(s => s.Pending).ToList();
            if (pending.Count == 0 || Settle(pending, deadline, neededKill))
            {
                return;
            }

            await Task.Yield();
        }
    }

    /// <summary>One poll slice over <paramref name="pending"/>; whether the deadline has passed.</summary>
    private bool Settle(IReadOnlyList<Slot> pending, DateTimeOffset deadline, bool neededKill)
    {
        var left = (int)Math.Clamp((deadline - _clock.GetUtcNow()).TotalMilliseconds, 0, SliceMilliseconds);
        var ready = new bool[pending.Count];
        var count = _calls.Poll([.. pending.Select(s => s.Fd)], ready, left);
        var failure = count < 0 && -count != Eintr ? $"poll failed with errno {-count}" : string.Empty;
        for (var i = 0; i < pending.Count; i++)
        {
            pending[i].Settle(failure, ready[i], neededKill);
        }

        return _clock.GetUtcNow() >= deadline;
    }

    /// <summary>One process during a call: its pin and, once known, its outcome (none while it is still waited on).</summary>
    private sealed class Slot(ProcessIdentity process, int fd)
    {
        public ProcessIdentity Process { get; } = process;

        public int Fd { get; } = fd;

        public SignalOutcome? Outcome { get; set; }

        public bool Pending => Outcome is null;

        public void Settle(string pollFailure, bool ended, bool neededKill) =>
            Outcome = pollFailure.Length > 0 ? new SignalOutcome.Failed($"{pollFailure}: whether pid {Process.Pid} ended is unknown")
                : ended ? new SignalOutcome.Ended(neededKill)
                : null;
    }
}

/// <summary>glibc, by its full soname: the only library this file loads, and only on Linux (the Windows binary never builds
/// this sender).</summary>
internal sealed partial class LibcPidfdCalls : IPidfdCalls
{
    private const short PollIn = 1;
    private const string Libc = "libc.so.6";

    public int Open(int pid)
    {
        var fd = PidfdOpen(pid, 0);
        return fd >= 0 ? fd : -Marshal.GetLastPInvokeError();
    }

    public int Signal(int fd, int signal) => PidfdSendSignal(fd, signal, IntPtr.Zero, 0) == 0 ? 0 : Marshal.GetLastPInvokeError();

    public int Poll(IReadOnlyList<int> fds, bool[] ready, int milliseconds)
    {
        var entries = fds.Select(fd => new PollFd { Fd = fd, Events = PollIn }).ToArray();
        var count = PollNative(entries, (nuint)entries.Length, milliseconds);
        if (count < 0)
        {
            return -Marshal.GetLastPInvokeError();
        }

        for (var i = 0; i < entries.Length; i++)
        {
            ready[i] = entries[i].Revents != 0;
        }

        return count;
    }

    public void Close(int fd) => _ = CloseNative(fd);

    [StructLayout(LayoutKind.Sequential)]
    private struct PollFd
    {
        public int Fd;
        public short Events;
        public short Revents;
    }

    [LibraryImport(Libc, EntryPoint = "pidfd_open", SetLastError = true)]
    private static partial int PidfdOpen(int pid, uint flags);

    [LibraryImport(Libc, EntryPoint = "pidfd_send_signal", SetLastError = true)]
    private static partial int PidfdSendSignal(int pidfd, int signal, IntPtr info, uint flags);

    [LibraryImport(Libc, EntryPoint = "poll", SetLastError = true)]
    private static partial int PollNative([In, Out] PollFd[] fds, nuint count, int timeoutMilliseconds);

    [LibraryImport(Libc, EntryPoint = "close", SetLastError = true)]
    private static partial int CloseNative(int fd);
}
