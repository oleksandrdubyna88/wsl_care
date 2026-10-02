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
/// </remarks>
public sealed class ProcessCommandRunner(ICommandPolicy policy) : ICommandRunner
{
    /// <summary>How long the stream readers may take to finish after the process is gone.</summary>
    private static readonly TimeSpan DrainGrace = TimeSpan.FromSeconds(2);

    public async Task<CommandOutcome> RunAsync(CommandRequest request, CancellationToken cancellationToken)
    {
        if (policy.Review(request.Argv) is CommandVerdict.Refused refused)
        {
            return new CommandOutcome.Refused(refused.Reason);
        }

        cancellationToken.ThrowIfCancellationRequested();
        using var process = new Process { StartInfo = StartInfo(request) };
        var started = Stopwatch.StartNew();
        try
        {
            if (!process.Start())
            {
                return new CommandOutcome.FailedToStart($"the operating system did not start {request.Argv[0]}");
            }
        }
        catch (Win32Exception e)
        {
            return new CommandOutcome.FailedToStart($"{request.Argv[0]}: {e.Message}");
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
            Kill(process);
            await DrainAsync(reads).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            return new CommandOutcome.TimedOut(stdout.Snapshot(), stderr.Snapshot(), request.Timeout);
        }

        await DrainAsync(reads).ConfigureAwait(false);
        return new CommandOutcome.Exited(process.ExitCode, stdout.Snapshot(), stderr.Snapshot(), started.Elapsed);
    }

    private static ProcessStartInfo StartInfo(CommandRequest request)
    {
        var info = new ProcessStartInfo(request.Argv[0])
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

    /// <summary>Wait for the readers, but never past the grace: a pipe a survivor holds open must not hold us.</summary>
    private static async Task DrainAsync(Task reads)
    {
        await Task.WhenAny(reads, Task.Delay(DrainGrace)).ConfigureAwait(false);
    }

    /// <summary>A read that faults after we stopped waiting is still observed — never an unobserved task fault.</summary>
    private static void Observe(Task reads) =>
        _ = reads.ContinueWith(static t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously);

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
