using WslCare.Core.Config;
using System.ComponentModel;
using System.Diagnostics;

namespace WslCare.Core.Processes;

/// <summary>
/// The real <see cref="ICommandRunner"/>: the ONLY file under <c>src_daemon/src</c> that may call
/// <c>Process.Start</c> — the architecture test holds every other file to that.
/// </summary>
/// <remarks>
/// <para>Argv in, typed outcome out. The policy is asked first; a refusal never reaches the operating
/// system. Both streams are read concurrently into bounded buffers from the moment the process
/// starts, so a chatty child never blocks on a full pipe. The ceiling is enforced with a linked
/// token: when it fires, the WHOLE process tree is killed before the method returns — a timeout
/// that merely stopped waiting would promote the child to an orphan holding locks and memory, which
/// on a daemon that runs every four hours forever is a leak with a schedule.</para>
/// <para>After a kill the pipe reads are given a short grace to drain; a grandchild that somehow
/// survived and still holds the pipe cannot make this method hang, because the buffers are read by
/// snapshot and the stuck reads are observed rather than awaited.</para>
/// <para><see cref="StreamAsync"/> is the same launcher for a child whose stdout never ends on its own
/// (<c>docker events</c>): lines go to a callback as they arrive, each cut at the output cap.</para>
/// <para><b>No runner without the policy (E3.S1).</b> The public constructor takes a <see cref="Policy.CommandPolicy"/> —
/// a sealed type whose never-list no caller can switch off — and refuses a null one. The only other way in is
/// <see cref="UnguardedForItsOwnTests"/>, internal, for this file's own tests, whose subject's children are shells; a
/// test holds every product file to never calling it.</para>
/// </remarks>
public sealed class ProcessCommandRunner : ICommandRunner
{
    /// <summary>How long the stream readers may take to finish after the process is gone.</summary>
    private static TimeSpan DrainGrace => Tuning.Current.Milliseconds(ConfigKeys.Commands.DrainGraceMilliseconds);

    private readonly Func<CommandRequest, CommandVerdict> _review;
    private readonly Func<string, CancellationToken, ResolvedExecutable> _resolve;

    public ProcessCommandRunner(Policy.CommandPolicy policy)
        : this(PolicyOf(policy).Review, static (name, token) => ExecutableResolver.Resolve(name, token))
    {
    }

    private ProcessCommandRunner(Func<CommandRequest, CommandVerdict> review, Func<string, CancellationToken, ResolvedExecutable> resolve)
    {
        _review = review;
        _resolve = resolve;
    }

    private static Policy.CommandPolicy PolicyOf(Policy.CommandPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        return policy;
    }

    /// <summary>The runner with a review of the test's own — ONLY for the runner's tests, whose subject's child is a
    /// shell (a process that spawns a grandchild), which the never-list rightly refuses. Never called from product code.</summary>
    internal static ProcessCommandRunner UnguardedForItsOwnTests(Func<CommandRequest, CommandVerdict> review) =>
        new(review, static (name, token) => ExecutableResolver.Resolve(name, token));

    /// <summary>The runner under a real <paramref name="policy"/> with a lookup of the test's own — ONLY for the tests that
    /// prove the policy judges the bare program while the launcher starts the file the lookup found.</summary>
    internal static ProcessCommandRunner WithResolverForItsOwnTests(Policy.CommandPolicy policy, Func<string, CancellationToken, ResolvedExecutable> resolve) =>
        new(PolicyOf(policy).Review, resolve);

    public async Task<CommandOutcome> RunAsync(CommandRequest request, CancellationToken cancellationToken)
    {
        if (Refusal(request) is { } refused)
        {
            return refused;
        }

        cancellationToken.ThrowIfCancellationRequested();
        using var process = new Process();
        var started = Stopwatch.StartNew();
        var (notStarted, startedFrom) = Start(process, request, cancellationToken);
        if (notStarted is not null)
        {
            return notStarted;
        }

        var stdout = new OutputCapture(request.OutputCapChars);
        var stderr = new OutputCapture(request.OutputCapChars);
        var reads = Task.WhenAll(stdout.DrainAsync(process.StandardOutput), stderr.DrainAsync(process.StandardError));
        Observe(reads);

        using var ceiling = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        ceiling.CancelAfter(request.Timeout);
        try
        {
            await process.WaitForExitAsync(ceiling.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            await KillAndReapAsync(process, reads).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            return new CommandOutcome.TimedOut(stdout.Snapshot(), stderr.Snapshot(), request.Timeout) { StartedFrom = startedFrom };
        }

        await DrainAsync(reads).ConfigureAwait(false);
        return new CommandOutcome.Exited(process.ExitCode, stdout.Snapshot(), stderr.Snapshot(), started.Elapsed) { StartedFrom = startedFrom };
    }

    public async Task<CommandOutcome> StreamAsync(CommandRequest request, Action<string> onStdoutLine, CancellationToken cancellationToken)
    {
        if (Refusal(request) is { } refused)
        {
            return refused;
        }

        cancellationToken.ThrowIfCancellationRequested();
        using var process = new Process();
        var started = Stopwatch.StartNew();
        var (notStarted, startedFrom) = Start(process, request, cancellationToken);
        if (notStarted is not null)
        {
            return notStarted;
        }

        var stderr = new OutputCapture(request.OutputCapChars);
        var errors = stderr.DrainAsync(process.StandardError);
        Observe(errors);
        var lines = PumpLinesAsync(process.StandardOutput, onStdoutLine, request.OutputCapChars);
        Observe(lines);

        using var ceiling = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        ceiling.CancelAfter(request.Timeout);
        var ended = false;
        try
        {
            // stdout ends when the child closes it — normally when it exits.
            await lines.WaitAsync(ceiling.Token).ConfigureAwait(false);
            await process.WaitForExitAsync(ceiling.Token).ConfigureAwait(false);
            ended = true;
        }
        catch (OperationCanceledException) when (ceiling.IsCancellationRequested)
        {
            // The ceiling or the caller: decided below, after the finally has killed and reaped the tree.
        }
        finally
        {
            // EVERY way out but a normal end — the ceiling, the caller's cancellation, a callback that threw (gate finding
            // #1/#5) — kills the whole tree and waits for the child to be gone before anything propagates.
            if (!ended)
            {
                await KillAndReapAsync(process, Task.WhenAll(errors, lines)).ConfigureAwait(false);
            }
        }

        if (!ended)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return new CommandOutcome.TimedOut(CapturedText.Empty, stderr.Snapshot(), request.Timeout) { StartedFrom = startedFrom };
        }

        await DrainAsync(errors).ConfigureAwait(false);
        return new CommandOutcome.Exited(process.ExitCode, CapturedText.Empty, stderr.Snapshot(), started.Elapsed) { StartedFrom = startedFrom };
    }

    private CommandOutcome.Refused? Refusal(CommandRequest request) =>
        _review(request) is CommandVerdict.Refused refused ? new CommandOutcome.Refused(refused.Reason) : null;

    /// <summary>Starts the process from the FULL path <see cref="ExecutableResolver"/> found — the operating system is never
    /// handed a bare name to search for, and that path is started as found, never looked up again; the outcome when there is
    /// none or the operating system would not start it (<c>null</c> when it runs), and the file started when it came from
    /// the Windows system drive (empty otherwise).</summary>
    private (CommandOutcome.FailedToStart? NotStarted, string StartedFrom) Start(Process process, CommandRequest request, CancellationToken cancellationToken) =>
        _resolve(request.Argv[0], cancellationToken) switch
        {
            ResolvedExecutable.Found found => (StartAt(process, request, found.Path), found.OnTheSystemDrive ? found.Path : string.Empty),
            ResolvedExecutable.NotFound missing => (new CommandOutcome.FailedToStart(missing.Reason), string.Empty),
            _ => throw new UnreachableException("ResolvedExecutable is a closed set"),
        };

    private static CommandOutcome.FailedToStart? StartAt(Process process, CommandRequest request, string executable)
    {
        process.StartInfo = StartInfo(request, executable);
        try
        {
            return process.Start() ? null : new CommandOutcome.FailedToStart($"the operating system did not start {executable}");
        }
        catch (Win32Exception e)
        {
            return new CommandOutcome.FailedToStart($"{executable}: {e.Message}");
        }
    }

    private static ProcessStartInfo StartInfo(CommandRequest request, string executable)
    {
        var info = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = false,
            CreateNoWindow = true,
            WorkingDirectory = request.WorkingDirectory,
        };
        foreach (var argument in request.Argv.Skip(1))
        {
            info.ArgumentList.Add(argument);
        }

        if (request.Environment is CommandEnvironment.Clean clean)
        {
            // Nothing of this process's environment reaches the child: exactly the variables the request names.
            info.Environment.Clear();
            foreach (var (name, value) in clean.Variables)
            {
                info.Environment[name] = value;
            }
        }

        return info;
    }

    /// <summary>The WHOLE tree, always: a child that spawned a grandchild (a shell, a docker CLI waiting
    /// on its daemon) must not leave it behind holding the pipe and the locks.</summary>
    private static void Kill(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
            // It exited between the timeout and the kill. That is the outcome we wanted.
        }
    }

    /// <summary>The tree killed, the child waited for (never past the grace), then the readers drained: when this returns the
    /// child is gone, not merely signalled.</summary>
    private static async Task KillAndReapAsync(Process process, Task reads)
    {
        Kill(process);
        // CancellationToken.None on purpose: the caller may already have cancelled, and this wait is what makes the kill
        // observable; DrainGrace is its ceiling.
        await Task.WhenAny(process.WaitForExitAsync(CancellationToken.None), Task.Delay(DrainGrace)).ConfigureAwait(false);
        await DrainAsync(reads).ConfigureAwait(false);
    }

    /// <summary>Wait for the readers, but never past the grace: a pipe a survivor holds open must not hold us.</summary>
    private static async Task DrainAsync(Task reads)
    {
        await Task.WhenAny(reads, Task.Delay(DrainGrace)).ConfigureAwait(false);
    }

    /// <summary>A read that faults after we stopped waiting is still observed — never an unobserved task fault.</summary>
    private static void Observe(Task reads) =>
        _ = reads.ContinueWith(static t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously);

    /// <summary>Lines of <paramref name="reader"/> to <paramref name="onLine"/>, each cut at <paramref name="cap"/>
    /// characters; a last line without a line end is delivered too.</summary>
    private static async Task PumpLinesAsync(StreamReader reader, Action<string> onLine, int cap)
    {
        var buffer = new char[8192];
        var line = new System.Text.StringBuilder();
        int read;
        while ((read = await reader.ReadAsync(buffer, 0, buffer.Length).ConfigureAwait(false)) > 0)
        {
            for (var i = 0; i < read; i++)
            {
                Take(buffer[i], line, onLine, cap);
            }
        }

        if (line.Length > 0)
        {
            onLine(line.ToString().TrimEnd('\r'));
        }
    }

    private static void Take(char c, System.Text.StringBuilder line, Action<string> onLine, int cap)
    {
        if (c == '\n')
        {
            onLine(line.ToString().TrimEnd('\r'));
            line.Clear();
        }
        else if (line.Length < cap)
        {
            line.Append(c);
        }
    }

    /// <summary>A bounded, thread-safe accumulator for one stream.</summary>
    private sealed class OutputCapture(int cap)
    {
        private readonly System.Text.StringBuilder _text = new();
        private readonly object _gate = new();
        private bool _truncated;

        public async Task DrainAsync(StreamReader reader)
        {
            var buffer = new char[8192];
            int read;
            while ((read = await reader.ReadAsync(buffer, 0, buffer.Length).ConfigureAwait(false)) > 0)
            {
                Append(buffer.AsSpan(0, read));
            }
        }

        public CapturedText Snapshot()
        {
            lock (_gate)
            {
                return new CapturedText(_text.ToString(), _truncated);
            }
        }

        private void Append(ReadOnlySpan<char> chunk)
        {
            lock (_gate)
            {
                var room = cap - _text.Length;
                if (chunk.Length > room)
                {
                    _truncated = true;
                    chunk = chunk[..Math.Max(room, 0)];
                }

                _text.Append(chunk);
            }
        }
    }
}
