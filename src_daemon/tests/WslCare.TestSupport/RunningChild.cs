using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace WslCare.TestSupport;

/// <summary>
/// A BUILT executable started and left running — for a verb that ends only on a signal (<c>events follow</c>). The test
/// waits for a condition, sends the signal to THIS child's pid (never by image name), and collects the exit. Disposal
/// kills the child's tree if it is still there, so a failed test leaves nothing behind.
/// </summary>
public sealed partial class RunningChild : IDisposable
{
    private const int SigTerm = 15;
    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);

    private readonly Process _process;
    private readonly Task<string> _stdout;
    private readonly Task<string> _stderr;

    private RunningChild(Process process)
    {
        _process = process;
        _stdout = process.StandardOutput.ReadToEndAsync();
        _stderr = process.StandardError.ReadToEndAsync();
    }

    public int Pid => _process.Id;

    public bool HasExited => _process.HasExited;

    public static RunningChild Start(string executable, IReadOnlyList<string> args, IReadOnlyDictionary<string, string?> environment, string workingDirectory)
    {
        var start = new ProcessStartInfo(executable)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Utf8,
            StandardErrorEncoding = Utf8,
            WorkingDirectory = workingDirectory,
        };
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

        var process = Process.Start(start) ?? throw new InvalidOperationException($"could not start {executable}");
        process.StandardInput.Close();
        return new RunningChild(process);
    }

    /// <summary>Polls <paramref name="condition"/> every 100 ms until it holds or <paramref name="ceiling"/> passes; false then.</summary>
    public static async Task<bool> WaitUntilAsync(Func<bool> condition, TimeSpan ceiling)
    {
        var watch = Stopwatch.StartNew();
        while (watch.Elapsed < ceiling)
        {
            if (condition())
            {
                return true;
            }

            await Task.Delay(100);
        }

        return condition();
    }

    /// <summary>SIGTERM to this child's pid — what systemd sends on <c>systemctl stop</c>. Linux only.</summary>
    public void Terminate()
    {
        if (!OperatingSystem.IsLinux())
        {
            throw new PlatformNotSupportedException("SIGTERM is sent on Linux; the Windows binary is stopped otherwise");
        }

        if (Kill(_process.Id, SigTerm) != 0)
        {
            throw new InvalidOperationException($"kill({_process.Id}, SIGTERM) failed with errno {Marshal.GetLastPInvokeError()}");
        }
    }

    /// <summary>The exit and both streams, within <paramref name="ceiling"/>; on the ceiling the tree is killed and this throws.</summary>
    public async Task<ChildResult> WaitAsync(TimeSpan ceiling)
    {
        using var deadline = new CancellationTokenSource(ceiling);
        try
        {
            await _process.WaitForExitAsync(deadline.Token);
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested)
        {
            _process.Kill(entireProcessTree: true);
            throw new TimeoutException($"the child {_process.Id} did not exit within {ceiling.TotalSeconds:0} s");
        }

        return new ChildResult(_process.ExitCode, await _stdout, await _stderr);
    }

    public void Dispose()
    {
        try
        {
            if (!_process.HasExited)
            {
                _process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
            // Gone between the check and the kill.
        }

        _process.Dispose();
    }

    [LibraryImport("libc", EntryPoint = "kill", SetLastError = true)]
    private static partial int Kill(int pid, int signal);
}
