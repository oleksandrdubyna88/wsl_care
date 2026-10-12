using System.Diagnostics;
using System.Runtime.InteropServices;

using FluentAssertions;

using Microsoft.Win32.SafeHandles;

using WslCare.Core.Collectors;
using WslCare.Core.Mcp;
using WslCare.Core.Processes;
using WslCare.TestSupport;

namespace WslCare.Core.Tests.Processes;

/// <summary>
/// WR-D3 on the Windows legs (PLAN_windows_runner.md §3.3, §6): the real job, read back through Win32 — its handle is not inheritable,
/// it is kill-on-close with no breakaway — and the real supervisor over the test's OWN helper tree: a Windows PowerShell worker that
/// starts a <c>ping.exe</c> grandchild and writes both pids. After the deadline (and after a cancellation) both processes, held by
/// their own handles from the moment they were seen — pid AND creation time — must have exited. Nothing else is started, signalled or
/// looked up: whatever is still alive at the end of a test is one of these two, ended through the handle the test holds.
/// </summary>
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
public sealed partial class Win32JobControlTests
{
    private const string WindowsOnly = "the Job Object is Windows'; the distro's binary supervises no task";
    private const uint HandleFlagInherit = 0x1;
    private const int ExtendedLimitInformationClass = 9;
    private const uint KillOnJobClose = 0x2000;
    private const uint BreakawayOk = 0x0800;
    private const uint SilentBreakawayOk = 0x1000;

    [Fact]
    public void The_job_handle_is_not_inheritable()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), WindowsOnly);
        using var job = Created();

        GetHandleInformation(job.Handle, out var flags).Should().BeTrue();

        (flags & HandleFlagInherit).Should().Be(0u, "no process the supervisor creates may hold the job's handle (§4)");
    }

    [Fact]
    public void The_job_is_kill_on_close_and_lets_no_child_break_away()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), WindowsOnly);
        using var job = Created();

        QueryInformationJobObject(job.Handle, ExtendedLimitInformationClass, out var limits, (uint)Marshal.SizeOf<ExtendedLimits>(), out _).Should().BeTrue();

        limits.Basic.LimitFlags.Should().Be(KillOnJobClose, "kill-on-close, and neither BREAKAWAY_OK nor SILENT_BREAKAWAY_OK");
        (limits.Basic.LimitFlags & (BreakawayOk | SilentBreakawayOk)).Should().Be(0u);
    }

    [Fact]
    public async Task A_child_and_its_grandchild_are_gone_after_the_deadline()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), WindowsOnly);
        using var root = new TempRoot("job-deadline");
        using var tree = new HelperTree(Path.Combine(root.Path, "pids.txt"));
        var supervisor = new JobSupervisor(new Win32JobControl(), new Win32Boot());

        var run = Task.Run(() => supervisor.Run(tree.Launch(TimeSpan.FromSeconds(20)), CancellationToken.None));
        await tree.SeenAsync();
        var outcome = await run.WaitAsync(TimeSpan.FromSeconds(60), TestContext.Current.CancellationToken);

        outcome.Should().Be(new WorkerOutcome.DeadlinePassed(TimeSpan.FromSeconds(20)));
        (await tree.AllExitedAsync()).Should().BeTrue($"the worker and its grandchild are gone after the deadline: {tree.Describe()}");
        tree.ExitCodes().Should().AllBeEquivalentTo(unchecked((int)JobSupervisor.EndedExitCode), "both were ended by the job, not by themselves");
    }

    [Fact]
    public async Task A_cancelled_run_closes_the_job_and_its_whole_tree_goes_with_it()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), WindowsOnly);
        using var root = new TempRoot("job-cancel");
        using var tree = new HelperTree(Path.Combine(root.Path, "pids.txt"));
        using var cancel = new CancellationTokenSource();
        var supervisor = new JobSupervisor(new Win32JobControl(), new Win32Boot());

        var run = Task.Run(() => supervisor.Run(tree.Launch(TimeSpan.FromMinutes(5)), cancel.Token));
        await tree.SeenAsync();
        await cancel.CancelAsync();
        var outcome = await run.WaitAsync(TimeSpan.FromSeconds(60), TestContext.Current.CancellationToken);

        outcome.Should().Be(new WorkerOutcome.Cancelled());
        (await tree.AllExitedAsync()).Should().BeTrue($"closing the kill-on-close job ends the worker and its grandchild: {tree.Describe()}");
    }

    [Fact]
    public void A_worker_that_exits_by_itself_answers_its_own_code()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), WindowsOnly);
        var cmd = Path.Combine(Environment.SystemDirectory, "cmd.exe");

        var outcome = new JobSupervisor(new Win32JobControl(), new Win32Boot()).Run(new WorkerLaunch(cmd, ["/d", "/c", "exit 7"], TimeSpan.FromSeconds(60)), CancellationToken.None);

        outcome.Should().Be(new WorkerOutcome.Exited(7));
    }

    private static Win32JobControl.Job Created() =>
        new Win32JobControl().CreateJob().Should().BeOfType<Reading<IJobHandle>.Available>().Subject.Value.Should().BeOfType<Win32JobControl.Job>().Subject;

    /// <summary>The test's own helper tree: Windows PowerShell (the worker) starts <c>ping.exe</c> (the grandchild), writes
    /// "&lt;worker pid&gt; &lt;grandchild pid&gt;" to a file, and sleeps. Each process is OPENED by the test the moment its pid is read and held
    /// by that handle, with its creation time — so every later check, and the clean-up, is of THAT process, never of a reused pid.</summary>
    private sealed class HelperTree(string pidFile) : IDisposable
    {
        private readonly List<(Process Process, DateTime Started)> _seen = [];

        public WorkerLaunch Launch(TimeSpan deadline)
        {
            var powershell = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");
            var script = "$psi = New-Object System.Diagnostics.ProcessStartInfo 'ping.exe'; $psi.Arguments = '-n 600 127.0.0.1'; "
                + "$psi.UseShellExecute = $false; $psi.CreateNoWindow = $true; $psi.RedirectStandardOutput = $true; "
                + "$g = [System.Diagnostics.Process]::Start($psi); "
                + $"Set-Content -LiteralPath '{pidFile.Replace("'", "''", StringComparison.Ordinal)}' -Value \"$PID $($g.Id)\"; Start-Sleep -Seconds 600";
            return new WorkerLaunch(powershell, ["-NoProfile", "-NonInteractive", "-Command", script], deadline);
        }

        public async Task SeenAsync()
        {
            (await RunningChild.WaitUntilAsync(() => File.Exists(pidFile) && File.ReadAllText(pidFile).Trim().Split(' ').Length == 2, TimeSpan.FromSeconds(30)))
                .Should().BeTrue("the helper worker writes its pid and its grandchild's within 30 s");
            foreach (var pid in File.ReadAllText(pidFile).Trim().Split(' ').Select(int.Parse))
            {
                var process = Process.GetProcessById(pid);
                _ = process.SafeHandle;
                _seen.Add((process, process.StartTime));
            }
        }

        public Task<bool> AllExitedAsync() => RunningChild.WaitUntilAsync(() => _seen.Count == 2 && _seen.All(s => s.Process.HasExited), TimeSpan.FromSeconds(10));

        public IReadOnlyList<int> ExitCodes() => [.. _seen.Select(s => s.Process.ExitCode)];

        public string Describe() => string.Join(", ", _seen.Select(s => $"pid {s.Process.Id} started {s.Started:O} exited {s.Process.HasExited}"));

        /// <summary>Ends what is still alive of THIS tree through the handles held since it was seen — a failed test leaves nothing.</summary>
        public void Dispose()
        {
            foreach (var (process, _) in _seen)
            {
                if (!process.HasExited)
                {
                    process.Kill();
                    process.WaitForExit(TimeSpan.FromSeconds(10));
                }

                process.Dispose();
            }
        }
    }

    /// <summary>The test's OWN <c>JOBOBJECT_EXTENDED_LIMIT_INFORMATION</c> (a structure of another assembly is not blittable to the source
    /// generator), field for field: the basic limits, <c>IO_COUNTERS</c>, the four sizes.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct ExtendedLimits
    {
        public BasicLimits Basic;
        public ulong Io1;
        public ulong Io2;
        public ulong Io3;
        public ulong Io4;
        public ulong Io5;
        public ulong Io6;
        public nuint ProcessMemoryLimit;
        public nuint JobMemoryLimit;
        public nuint PeakProcessMemoryUsed;
        public nuint PeakJobMemoryUsed;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BasicLimits
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public nuint MinimumWorkingSetSize;
        public nuint MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public nuint Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetHandleInformation(SafeWaitHandle handle, out uint flags);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool QueryInformationJobObject(SafeWaitHandle job, int informationClass, out ExtendedLimits information, uint length, out uint returned);
}
