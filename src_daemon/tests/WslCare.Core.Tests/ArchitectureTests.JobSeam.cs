using System.Text.RegularExpressions;

using FluentAssertions;

namespace WslCare.Core.Tests;

/// <summary>
/// WR-D3 (PLAN_windows_runner.md §3.3 point 6): the signal scan widened to the Win32 calls that end a process through a job or by its
/// handle — <c>TerminateJobObject</c>, <c>TerminateProcess</c>, <c>TerminateThread</c> (each also as its <c>Nt</c>/<c>Zw</c> native form), <c>AssignProcessToJobObject</c> — and to the one that creates a process
/// outside the child runner, <c>CreateProcess*</c>. The supervisor's Win32 half (<c>Processes/JobSupervisor.Win32.cs</c>) is the only
/// file allowed to spell them, as a call or as an entry point; a planted instance and the seam's own declarations keep the scanner
/// honest.
/// </summary>
public sealed partial class ArchitectureTests
{
    [GeneratedRegex(@"""(?:(?:Nt|Zw)?Terminate(?:JobObject|Process|Thread)|AssignProcessToJobObject|CreateProcess\w*)""|\b(?:(?:Nt|Zw)?Terminate(?:JobObject|Process|Thread)|AssignProcessToJobObject|CreateProcess\w*)\s*\(", RegexOptions.CultureInvariant)]
    private static partial Regex JobCall();

    [Fact]
    public void Only_the_supervisor_ends_a_process_through_a_job_or_by_its_handle()
    {
        var seam = Metadata("WslCare.JobSeam");
        var offenders = SourceFiles()
            .Where(file => !string.Equals(file, seam, StringComparison.OrdinalIgnoreCase))
            .SelectMany(file => JobCall().Matches(File.ReadAllText(file)).Select(m => $"{file}:{LineOf(File.ReadAllText(file), m.Index)}: {m.Value}"))
            .ToList();

        offenders.Should().BeEmpty("the job supervisor ends only the job it made and the worker it created suspended into it (§3.3, §4)");
    }

    [Fact]
    public void The_scan_finds_TerminateJobObject_outside_the_supervisor()
    {
        const string planted = """
            [LibraryImport("kernel32.dll", EntryPoint = "TerminateJobObject")]
            private static partial bool End(nint job, uint code);
            _ = TerminateProcess (handle, 1);
            AssignProcessToJobObject(job, process);
            CreateProcessW(null, line);
            [LibraryImport("ntdll.dll", EntryPoint = "NtTerminateProcess")]
            _ = ZwTerminateJobObject(job, 1);
            TerminateThread(thread, 0);
            """;

        JobCall().Matches(planted).Select(m => LineOf(planted, m.Index)).Should().Equal(1, 3, 4, 5, 6, 7, 8);
        JobCall().IsMatch("the supervisor terminates the job (TerminateJobObject) at the deadline").Should().BeFalse("prose names the call without calling it");
        JobCall().Matches(File.ReadAllText(Metadata("WslCare.JobSeam"))).Select(m => m.Value.TrimEnd('(', ' ')).Should()
            .Contain(["TerminateJobObject", "TerminateProcess", "\"CreateProcessW\""], "the scanner still finds the seam's own declarations");
    }
}
