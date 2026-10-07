using System.Diagnostics;
using System.Text;

namespace WslCare.TestSupport;

/// <summary>What a child process left behind: its exit code and both streams, whole.</summary>
public sealed record ChildResult(int Exit, string Stdout, string Stderr)
{
    /// <summary>Lines as a terminal shows them: ANY line break counts, not only this platform's.</summary>
    public IReadOnlyList<string> StderrLines => Lines(Stderr);

    public IReadOnlyList<string> StdoutLines => Lines(Stdout);

    private static string[] Lines(string text) => text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
}

/// <summary>
/// Starts a BUILT executable as a separate process and waits for it under a ceiling — the one
/// launcher the process-level tests share (<c>BuiltBinaryTests</c>, the scenario harness), so the
/// timeout and tree-kill behaviour is written once.
/// </summary>
/// <remarks>Test support, not product code: the architecture test that confines
/// <c>Process.Start</c> to <c>ProcessCommandRunner</c> scans <c>src_daemon/src</c> only.</remarks>
public static class ChildProcess
{
    /// <summary>The ceiling every test child gets unless it asks for another.</summary>
    public static readonly TimeSpan DefaultCeiling = TimeSpan.FromSeconds(30);

    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);

    /// <summary>
    /// The path of an executable the SDK copied beside the test assembly — a referenced
    /// <c>Exe</c> project's apphost — with <c>.exe</c> on Windows; asserts it is there.
    /// </summary>
    public static string BesideTheTests(string name)
    {
        var path = Path.Combine(AppContext.BaseDirectory, name + (OperatingSystem.IsWindows() ? ".exe" : string.Empty));
        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"the referenced executable's apphost should have been copied to {path}; is the ProjectReference there?", path);
        }

        return path;
    }

    /// <summary>How often a <see cref="ProgressWait"/> reads its mark.</summary>
    private static readonly TimeSpan ProgressPoll = TimeSpan.FromMilliseconds(100);

    /// <summary>How long a killed child's tree is given to be gone, and a child's output to reach its end once the child has
    /// exited or been killed. Its last bytes are in the pipe by then, so a few seconds is generous; past them something the child
    /// started still holds the pipe, and the run says so instead of waiting for it to the cap.</summary>
    private static readonly TimeSpan Settle = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Runs <paramref name="executable"/> with <paramref name="args"/> as an argv list (never a
    /// shell string). <paramref name="environment"/> entries are set on top of this process's
    /// environment; a <c>null</c> value removes the variable. On the ceiling the WHOLE tree is
    /// killed, the child awaited, and a <see cref="TimeoutException"/> names the executable. With <paramref name="progress"/>
    /// the ceiling is that wait's — silence, or its cap — and <paramref name="ceiling"/> must be left out. A child that exits
    /// while something it started still holds its output ends in a <see cref="TimeoutException"/> naming it too.
    /// </summary>
    public static async Task<ChildResult> RunAsync(
        string executable,
        IReadOnlyList<string> args,
        IReadOnlyDictionary<string, string?> environment,
        string workingDirectory = "",
        TimeSpan? ceiling = null,
        ProgressWait? progress = null)
    {
        var limit = LimitOf(ceiling, progress);
        var display = $"{executable} {string.Join(' ', args)}";
        using var process = Process.Start(StartInfo(executable, args, environment, workingDirectory))
            ?? throw new InvalidOperationException($"could not start {executable}");
        // Nothing is typed into a scenario: a child that waits on stdin sees end-of-file, not a hang.
        process.StandardInput.Close();
        using var deadline = new CancellationTokenSource(limit);
        var output = ChildOutput.Read(process);
        var silenced = progress is null ? Task.FromResult(false) : WatchAsync(progress, process, deadline);
        if (!await ExitedAsync(process, deadline))
        {
            var gone = await KillAsync(process, output);
            throw new TimeoutException(GaveUp(display, await silenced, gone, progress, limit));
        }

        await silenced;
        return await DrainAsync(process, output, display);
    }

    /// <summary>The wait's ceiling: the progress wait's cap, the ceiling asked for, or <see cref="DefaultCeiling"/> — never two.</summary>
    private static TimeSpan LimitOf(TimeSpan? ceiling, ProgressWait? progress) => (ceiling, progress) switch
    {
        ({ }, { }) => throw new ArgumentException("a child is waited on a ceiling OR on its progress, not both", nameof(progress)),
        (_, { } wait) => wait.Cap,
        ({ } given, _) => given,
        _ => DefaultCeiling,
    };

    private static ProcessStartInfo StartInfo(string executable, IReadOnlyList<string> args, IReadOnlyDictionary<string, string?> environment, string workingDirectory)
    {
        var start = new ProcessStartInfo(executable)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            // UTF-8 on both families: on Windows the default is the console's OEM code page, which
            // would turn any non-ASCII byte of a child's output into something else before a test saw it.
            StandardOutputEncoding = Utf8,
            StandardErrorEncoding = Utf8,
            // Empty is the default, and means this process's own directory.
            WorkingDirectory = workingDirectory,
        };
        SetEnvironment(start.Environment, environment);
        foreach (var arg in args)
        {
            start.ArgumentList.Add(arg);
        }

        return start;
    }

    /// <summary>Each entry set on top of the inherited environment; a <c>null</c> value removes the variable.</summary>
    private static void SetEnvironment(IDictionary<string, string?> into, IReadOnlyDictionary<string, string?> environment)
    {
        foreach (var (name, value) in environment)
        {
            if (value is null)
            {
                into.Remove(name);
            }
            else
            {
                into[name] = value;
            }
        }
    }

    /// <summary>True when the child exited before <paramref name="until"/> fired; false when the wait was given up.</summary>
    private static async Task<bool> ExitedAsync(Process process, CancellationTokenSource until)
    {
        try
        {
            await process.WaitForExitAsync(until.Token);
            return true;
        }
        catch (OperationCanceledException) when (until.IsCancellationRequested)
        {
            return false;
        }
    }

    /// <summary>A timeout that only stops WAITING leaves the child running: the whole tree is killed, then the child is awaited
    /// and both reads settled — each for <see cref="Settle"/> — before the caller hears of it, so a scenario that then removes its
    /// temporary home does not race a dying child in it (the retro round over PR #15). True when the child was gone in time.</summary>
    private static async Task<bool> KillAsync(Process process, ChildOutput output)
    {
        process.Kill(entireProcessTree: true);
        using var settle = new CancellationTokenSource(Settle);
        var gone = await ExitedAsync(process, settle);
        await output.SettleAsync();
        return gone;
    }

    private static string GaveUp(string display, bool silenced, bool gone, ProgressWait? progress, TimeSpan limit)
    {
        var why = silenced
            ? $"{display} made no progress for {progress!.Silence.TotalSeconds:0} s"
            : $"{display} did not exit within {limit.TotalSeconds:0} s";
        return gone ? why : $"{why}; it was still not gone {Settle.TotalSeconds:0} s after its tree was killed";
    }

    /// <summary>The child exited: its output is read to its end within <see cref="Settle"/>. Past that, something the child
    /// started holds the pipe open, and the run ends in a timeout naming the command — never a partial result a test could pass
    /// on (a read returns nothing before end-of-file anyway), and never the wait to the cap that a held pipe used to cost, which
    /// surfaced as an <see cref="OperationCanceledException"/> naming nothing.</summary>
    private static async Task<ChildResult> DrainAsync(Process process, ChildOutput output, string display)
    {
        if (!await output.SettleAsync())
        {
            throw new TimeoutException($"{display} exited, but its output was still open {Settle.TotalSeconds:0} s later: a process it started still holds it");
        }

        return new ChildResult(process.ExitCode, await output.Stdout, await output.Stderr);
    }

    /// <summary>The two reads of a child's output, started together so neither pipe fills while the other is read. They take no
    /// token: a read ends at end-of-file, when the child and whatever it started are gone, and every wait on one is bounded by
    /// <see cref="Settle"/> instead.</summary>
    private sealed record ChildOutput(Task<string> Stdout, Task<string> Stderr)
    {
        public static ChildOutput Read(Process process) =>
            new(process.StandardOutput.ReadToEndAsync(), process.StandardError.ReadToEndAsync());

        /// <summary>Waits for both reads for at most <see cref="Settle"/>; true when both ended. A fault of either is observed
        /// whenever it comes — now, or when a read still held open ends after the run gave up on it.</summary>
        public async Task<bool> SettleAsync()
        {
            var both = Task.WhenAll(Stdout, Stderr);
            // Reading Exception marks the faults observed; this continuation cannot itself fault.
            _ = both.ContinueWith(static t => t.Exception, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            return await Task.WhenAny(both, Task.Delay(Settle)) == both;
        }
    }

    /// <summary>Reads the mark every <see cref="ProgressPoll"/> while the child runs; when it has not changed for the wait's
    /// silence, ends the wait (cancels <paramref name="deadline"/>) and answers true. False when the child exited or the cap
    /// fired first.</summary>
    private static async Task<bool> WatchAsync(ProgressWait progress, Process process, CancellationTokenSource deadline)
    {
        using var poll = new PeriodicTimer(ProgressPoll);
        var quiet = new QuietSpell(progress.Mark());
        while (await poll.WaitForNextTickAsync(CancellationToken.None) && Running(process, deadline))
        {
            if (quiet.LastedFor(progress.Mark()) >= progress.Silence)
            {
                await deadline.CancelAsync();
                return true;
            }
        }

        return false;
    }

    private static bool Running(Process process, CancellationTokenSource deadline) => !process.HasExited && !deadline.IsCancellationRequested;

    /// <summary>How long a mark has stayed the same: restarted whenever a new one is seen.</summary>
    private sealed class QuietSpell(long first)
    {
        private readonly Stopwatch _since = Stopwatch.StartNew();
        private long _mark = first;

        public TimeSpan LastedFor(long mark)
        {
            if (mark != _mark)
            {
                _mark = mark;
                _since.Restart();
            }

            return _since.Elapsed;
        }
    }
}
