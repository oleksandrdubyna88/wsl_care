using System.Diagnostics;
using System.Globalization;

namespace WslCare.TestSupport;

/// <summary>
/// The job supervisor's test helper tree (WR-D3, Windows only): Windows PowerShell — the worker — starts <c>ping.exe</c> — the
/// grandchild — and writes one line, <c>&lt;worker pid&gt; &lt;worker start&gt; &lt;grandchild pid&gt; &lt;grandchild start&gt;</c>, each start as
/// <c>StartTime.ToFileTimeUtc()</c>, then sleeps. The test hands <see cref="Executable"/> and <see cref="Arguments"/> to the supervisor.
/// </summary>
/// <remarks>
/// <para>A process is HELD only when it is the one the line names: its pid is opened, and the creation time read through THAT handle
/// must equal the recorded one. A pid that is gone, or that now belongs to another process (a reused pid), is never held and never
/// ended — <see cref="SeenAsync"/> answers why, and the test fails with it. Every later check, and the clean-up in
/// <see cref="Dispose"/>, goes through the held handles only: pid AND creation time, never a pid alone, never an image name.</para>
/// <para>The test must keep the ceiling of <see cref="SeenAsync"/> SHORTER than the run's deadline, so the line is read while the
/// supervisor's job still holds both processes.</para>
/// </remarks>
public sealed class JobHelperTree(string pidFile) : IDisposable
{
    private readonly List<(Process Process, long StartFileTimeUtc)> _held = [];

    public static string Executable => Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");

    public IReadOnlyList<string> Arguments =>
    [
        "-NoProfile", "-NonInteractive", "-Command",
        "$psi = New-Object System.Diagnostics.ProcessStartInfo 'ping.exe'; $psi.Arguments = '-n 600 127.0.0.1'; "
            + "$psi.UseShellExecute = $false; $psi.CreateNoWindow = $true; $psi.RedirectStandardOutput = $true; "
            + "$g = [System.Diagnostics.Process]::Start($psi); "
            + "$s = (Get-Process -Id $PID).StartTime.ToFileTimeUtc(); $gs = $g.StartTime.ToFileTimeUtc(); "
            + $"Set-Content -LiteralPath '{pidFile.Replace("'", "''", StringComparison.Ordinal)}' -Value \"$PID $s $($g.Id) $gs\"; Start-Sleep -Seconds 600",
    ];

    /// <summary>Waits (at most <paramref name="ceiling"/>) for the helper's line, then holds both processes it names. Empty when both
    /// are held; otherwise why not — nothing of what could not be verified is held.</summary>
    public async Task<string> SeenAsync(TimeSpan ceiling)
    {
        if (!await RunningChild.WaitUntilAsync(() => Fields().Length == 4, ceiling))
        {
            return $"the helper worker did not write its line within {ceiling.TotalSeconds:0} s";
        }

        var fields = Fields();
        foreach (var at in new[] { 0, 2 })
        {
            if (Hold(int.Parse(fields[at], CultureInfo.InvariantCulture), long.Parse(fields[at + 1], CultureInfo.InvariantCulture)) is { Length: > 0 } problem)
            {
                return problem;
            }
        }

        return string.Empty;
    }

    public Task<bool> AllExitedAsync(TimeSpan ceiling) => RunningChild.WaitUntilAsync(() => _held.Count == 2 && _held.All(h => h.Process.HasExited), ceiling);

    public IReadOnlyList<int> ExitCodes() => [.. _held.Select(h => h.Process.ExitCode)];

    public string Describe() => string.Join(", ", _held.Select(h => $"pid {h.Process.Id} start {h.StartFileTimeUtc} exited {h.Process.HasExited}"));

    /// <summary>Ends what is still alive of THIS tree through the handles held since it was verified — a failed test leaves nothing.</summary>
    public void Dispose()
    {
        foreach (var (process, _) in _held)
        {
            if (!process.HasExited)
            {
                process.Kill();
                process.WaitForExit(TimeSpan.FromSeconds(10));
            }

            process.Dispose();
        }
    }

    private string[] Fields()
    {
        try
        {
            return File.Exists(pidFile) ? File.ReadAllText(pidFile).Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) : [];
        }
        catch (IOException)
        {
            // The helper is still writing it.
            return [];
        }
    }

    /// <summary>Holds the process <paramref name="pid"/> only if its creation time, read through the handle just opened, is
    /// <paramref name="startFileTimeUtc"/>; empty when held, else why not.</summary>
    private string Hold(int pid, long startFileTimeUtc)
    {
        Process process;
        try
        {
            process = Process.GetProcessById(pid);
            _ = process.SafeHandle;
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return $"pid {pid} was gone before it could be held ({e.Message}); nothing was held or ended for it";
        }

        var start = process.StartTime.ToFileTimeUtc();
        if (start != startFileTimeUtc)
        {
            process.Dispose();
            return $"pid {pid} now belongs to another process (created {start}, the helper recorded {startFileTimeUtc}); it was not held and will not be ended";
        }

        _held.Add((process, startFileTimeUtc));
        return string.Empty;
    }
}
