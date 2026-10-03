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
    Task<SignalOutcome> TerminateAsync(ProcessIdentity process, TimeSpan grace, CancellationToken cancellationToken);
}

/// <summary>A sender that signals nothing and says why — the sandbox (<c>WSL_CARE_ROOT</c>: a fixture's pids are not this
/// machine's), the Windows binary, and the default of a context nobody wired.</summary>
public sealed class RefusingProcessSignals(string reason) : IProcessSignals
{
    public static readonly RefusingProcessSignals NotWired = new("no signal sender is wired into this run");

    public static readonly RefusingProcessSignals Sandboxed = new("sandboxed (WSL_CARE_ROOT): the process table is a fixture, so no real process is ever signalled");

    public string Reason => reason;

    public Task<SignalOutcome> TerminateAsync(ProcessIdentity process, TimeSpan grace, CancellationToken cancellationToken) =>
        Task.FromResult<SignalOutcome>(new SignalOutcome.Refused(reason));
}

/// <summary>
/// The Linux sender: a <c>pidfd</c> (<c>pidfd_open</c>, Linux 5.3; glibc 2.36 — Ubuntu 24.04 has 2.39) pins the process
/// FIRST, its start is then compared with the identity, and both signals go through that descriptor
/// (<c>pidfd_send_signal</c>), so the pid can never be reused between the check and the signal: if the process the
/// descriptor pins has already ended, a new holder of the pid has a later start and is refused as
/// <see cref="SignalOutcome.NotTheSame"/>. Its end is awaited on the descriptor itself (<c>poll</c> → readable when the
/// process exits), in short slices so a cancellation is seen.
/// </summary>
/// <remarks>Reads <c>/proc/[pid]/stat</c> under <paramref name="procRoot"/> — always the REAL <c>/proc</c>: the CLI builds this
/// sender only outside a sandbox.</remarks>
public sealed partial class PidfdProcessSignals(IFileSystem files, string procRoot) : IProcessSignals
{
    /// <summary>How long a process gets to end after <c>SIGKILL</c> before it is reported still running.</summary>
    public static readonly TimeSpan KillWait = TimeSpan.FromSeconds(5);

    private const int SigTerm = 15;
    private const int SigKill = 9;
    private const short PollIn = 1;
    private const int Esrch = 3;
    private const int Eintr = 4;
    private const int SliceMilliseconds = 200;

    public async Task<SignalOutcome> TerminateAsync(ProcessIdentity process, TimeSpan grace, CancellationToken cancellationToken)
    {
        var fd = Native.PidfdOpen(process.Pid, 0);
        if (fd < 0)
        {
            var errno = Marshal.GetLastPInvokeError();
            return errno == Esrch ? new SignalOutcome.AlreadyGone() : new SignalOutcome.Failed($"pidfd_open({process.Pid}) failed with errno {errno}");
        }

        try
        {
            return await PinnedAsync(fd, process, grace, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _ = Native.Close(fd);
        }
    }

    private async Task<SignalOutcome> PinnedAsync(int fd, ProcessIdentity process, TimeSpan grace, CancellationToken cancellationToken)
    {
        if (Check(fd, process) is { } early)
        {
            return early;
        }

        if (Send(fd, SigTerm, process.Pid) is { } termFailed)
        {
            return termFailed;
        }

        if (await EndedWithinAsync(fd, grace, cancellationToken).ConfigureAwait(false))
        {
            return new SignalOutcome.Ended(NeededKill: false);
        }

        if (Send(fd, SigKill, process.Pid) is { } killFailed)
        {
            return killFailed is SignalOutcome.AlreadyGone ? new SignalOutcome.Ended(NeededKill: false) : killFailed;
        }

        return await EndedWithinAsync(fd, KillWait, cancellationToken).ConfigureAwait(false)
            ? new SignalOutcome.Ended(NeededKill: true)
            : new SignalOutcome.StillRunning($"pid {process.Pid} did not end within {KillWait.TotalSeconds:0} s of SIGKILL (uninterruptible I/O?)");
    }

    /// <summary>The start the pinned process reports, against the identity; <c>null</c> when they agree.</summary>
    private SignalOutcome? Check(int fd, ProcessIdentity process)
    {
        var path = $"{procRoot}/{process.Pid.ToString(CultureInfo.InvariantCulture)}/stat";
        var stat = ProcText.Read(files, path).Bind(text => ProcStat.Parse(text, path));
        return stat switch
        {
            Reading<ProcStat>.Available { Value.StartTicks: var start } when start == process.StartTicks => null,
            Reading<ProcStat>.Available { Value.StartTicks: var start } =>
                new SignalOutcome.NotTheSame($"pid {process.Pid} started at tick {start}, not {process.StartTicks}: it is another process now"),
            _ when Exited(fd) => new SignalOutcome.AlreadyGone(),
            _ => new SignalOutcome.Failed($"pid {process.Pid}: {stat.ReasonOrEmpty}"),
        };
    }

    private static SignalOutcome? Send(int fd, int signal, int pid)
    {
        if (Native.PidfdSendSignal(fd, signal, IntPtr.Zero, 0) == 0)
        {
            return null;
        }

        var errno = Marshal.GetLastPInvokeError();
        return errno == Esrch ? new SignalOutcome.AlreadyGone() : new SignalOutcome.Failed($"pidfd_send_signal(pid {pid}, {signal}) failed with errno {errno}");
    }

    private static async Task<bool> EndedWithinAsync(int fd, TimeSpan wait, CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + wait;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var left = (int)Math.Clamp((deadline - DateTime.UtcNow).TotalMilliseconds, 0, SliceMilliseconds);
            if (Poll(fd, left))
            {
                return true;
            }

            if (DateTime.UtcNow >= deadline)
            {
                return false;
            }

            await Task.Yield();
        }
    }

    private static bool Exited(int fd) => Poll(fd, 0);

    /// <summary>Whether the pinned process has ended (its descriptor is readable), waiting at most <paramref name="milliseconds"/>.</summary>
    private static bool Poll(int fd, int milliseconds)
    {
        var entry = new PollFd { Fd = fd, Events = PollIn };
        var ready = Native.Poll(ref entry, 1, milliseconds);
        return ready > 0 || (ready < 0 && Marshal.GetLastPInvokeError() != Eintr);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PollFd
    {
        public int Fd;
        public short Events;
        public short Revents;
    }

    /// <summary>glibc, by its full soname: the only library this file loads, and only on Linux (the Windows binary never
    /// builds this sender).</summary>
    private static partial class Native
    {
        private const string Libc = "libc.so.6";

        [LibraryImport(Libc, EntryPoint = "pidfd_open", SetLastError = true)]
        internal static partial int PidfdOpen(int pid, uint flags);

        [LibraryImport(Libc, EntryPoint = "pidfd_send_signal", SetLastError = true)]
        internal static partial int PidfdSendSignal(int pidfd, int signal, IntPtr info, uint flags);

        [LibraryImport(Libc, EntryPoint = "poll", SetLastError = true)]
        internal static partial int Poll(ref PollFd fds, nuint count, int timeoutMilliseconds);

        [LibraryImport(Libc, EntryPoint = "close", SetLastError = true)]
        internal static partial int Close(int fd);
    }
}
