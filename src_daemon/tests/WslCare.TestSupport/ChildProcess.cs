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

    /// <summary>
    /// Runs <paramref name="executable"/> with <paramref name="args"/> as an argv list (never a
    /// shell string). <paramref name="environment"/> entries are set on top of this process's
    /// environment; a <c>null</c> value removes the variable. On the ceiling the WHOLE tree is
    /// killed and a <see cref="TimeoutException"/> names the executable. With <paramref name="progress"/>
    /// the ceiling is that wait's — silence, or its cap — and <paramref name="ceiling"/> must be left out.
    /// </summary>
    public static async Task<ChildResult> RunAsync(
        string executable,
        IReadOnlyList<string> args,
        IReadOnlyDictionary<string, string?> environment,
        string workingDirectory = "",
        TimeSpan? ceiling = null,
        ProgressWait? progress = null)
    {
        if (ceiling is not null && progress is not null)
        {
            throw new ArgumentException("a child is waited on a ceiling OR on its progress, not both", nameof(progress));
        }

        var limit = progress?.Cap ?? ceiling ?? DefaultCeiling;
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
        };
        if (workingDirectory.Length > 0)
        {
            start.WorkingDirectory = workingDirectory;
        }

        foreach (var (name, value) in environment)
        {
            if (value is null)
            {
                start.Environment.Remove(name);
            }
            else
            {
                start.Environment[name] = value;
            }
        }

        foreach (var arg in args)
        {
            start.ArgumentList.Add(arg);
        }

        using var process = Process.Start(start)
            ?? throw new InvalidOperationException($"could not start {executable}");
        // Nothing is typed into a scenario: a child that waits on stdin sees end-of-file, not a hang.
        process.StandardInput.Close();
        using var deadline = new CancellationTokenSource(limit);
        var stdout = process.StandardOutput.ReadToEndAsync(deadline.Token);
        var stderr = process.StandardError.ReadToEndAsync(deadline.Token);
        var silenced = progress is null ? Task.FromResult(false) : WatchAsync(progress, process, deadline);
        try
        {
            await process.WaitForExitAsync(deadline.Token);
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested)
        {
            // A timeout that only stops WAITING leaves the child running; kill the whole tree.
            process.Kill(entireProcessTree: true);
            var display = $"{executable} {string.Join(' ', args)}";
            throw new TimeoutException(await silenced
                ? $"{display} made no progress for {progress!.Silence.TotalSeconds:0} s"
                : $"{display} did not exit within {limit.TotalSeconds:0} s");
        }

        await silenced;
        return new ChildResult(process.ExitCode, await stdout, await stderr);
    }

    /// <summary>Reads the mark every <see cref="ProgressPoll"/> while the child runs; when it has not changed for the wait's
    /// silence, ends the wait (cancels <paramref name="deadline"/>) and answers true. False when the child exited or the cap
    /// fired first.</summary>
    private static async Task<bool> WatchAsync(ProgressWait progress, Process process, CancellationTokenSource deadline)
    {
        using var poll = new PeriodicTimer(ProgressPoll);
        var quiet = new QuietSpell(progress.Mark());
        while (await poll.WaitForNextTickAsync(CancellationToken.None) && !process.HasExited && !deadline.IsCancellationRequested)
        {
            if (quiet.LastedFor(progress.Mark()) >= progress.Silence)
            {
                await deadline.CancelAsync();
                return true;
            }
        }

        return false;
    }

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
