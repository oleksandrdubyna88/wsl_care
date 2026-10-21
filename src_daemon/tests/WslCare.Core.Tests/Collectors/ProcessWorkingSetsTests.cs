using System.ComponentModel;

using FluentAssertions;

using WslCare.Core.Collectors;

namespace WslCare.Core.Tests.Collectors;

/// <summary>
/// E15.S5, plan round: the Windows binary's working-set readers (<c>vmmemWSL</c>, <c>Memory Compression</c>) fail ALONE. No
/// process, a process that exits between the listing and the read, a refused read, or a listing that fails make THIS figure
/// unavailable with the exception named — the status goes on. Every listed process is released however the read ends. Each
/// failure is planted with a scripted process; nothing here lists the machine's processes.
/// </summary>
public sealed class ProcessWorkingSetsTests
{
    private const string Name = "Memory Compression";
    private const string NoneRunning = "no Memory Compression process: Windows does not compress memory now";

    /// <summary>A listed process whose working set is a number, or a planted failure.</summary>
    private sealed class Listed(Func<long> workingSet) : IDisposable
    {
        public bool Released { get; private set; }

        public long WorkingSet => workingSet();

        public void Dispose() => Released = true;
    }

    private static Reading<long> Read(params Listed[] processes) =>
        ProcessWorkingSets.Sum(Name, () => processes, p => p.WorkingSet, NoneRunning);

    [Fact]
    public void The_working_sets_of_every_process_of_the_name_are_summed_and_released()
    {
        Listed[] processes = [new(() => 4L << 30), new(() => 2L << 30)];

        Read(processes).Should().Be(Reading.Of(6L << 30));
        processes.Should().OnlyContain(p => p.Released);
    }

    [Fact]
    public void No_process_of_the_name_is_unavailable_with_the_reason_never_zero()
    {
        Read().Should().Be(Reading.Missing<long>(NoneRunning));
    }

    [Fact]
    public void A_process_that_exits_between_the_listing_and_the_read_costs_only_this_figure_and_names_the_exception()
    {
        Listed[] processes = [new(() => 1L << 30), new(() => throw new InvalidOperationException("Process has exited, so the requested information is not available."))];

        var reading = Read(processes);

        reading.Should().BeOfType<Reading<long>.Unavailable>().Which.Reason.Should()
            .Be("the working set of Memory Compression could not be read: InvalidOperationException: Process has exited, so the requested information is not available.");
        processes.Should().OnlyContain(p => p.Released, "a process listed is released however the read ends");
    }

    [Fact]
    public void A_refused_read_costs_only_this_figure_and_names_the_exception()
    {
        var reading = Read(new Listed(() => throw new Win32Exception(5, "Access is denied.")));

        reading.Should().BeOfType<Reading<long>.Unavailable>().Which.Reason.Should()
            .Be("the working set of Memory Compression could not be read: Win32Exception: Access is denied.");
    }

    [Fact]
    public void A_listing_that_fails_costs_only_this_figure_and_names_the_exception()
    {
        var reading = ProcessWorkingSets.Sum<Listed>(Name, () => throw new InvalidOperationException("Couldn't get process information from performance counter."), p => p.WorkingSet, NoneRunning);

        reading.Should().BeOfType<Reading<long>.Unavailable>().Which.Reason.Should().Contain("InvalidOperationException").And.Contain("performance counter");
    }
}
