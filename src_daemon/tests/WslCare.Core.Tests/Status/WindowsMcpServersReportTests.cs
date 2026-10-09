using FluentAssertions;

using WslCare.Core.Collectors;
using WslCare.Core.Mcp;
using WslCare.Core.Status;

namespace WslCare.Core.Tests.Status;

/// <summary>
/// E14 S7a, the <c>windowsMcpServers</c> wire block. coai code round 2026-10-09 (findings 2, 3, 6) and the own review: an instance
/// whose memory could not be read is never summed as 0 — the totals say how many instances they cover, and are unavailable when
/// none was read.
/// </summary>
public sealed class WindowsMcpServersReportTests
{
    private static readonly DateTimeOffset Created = new(2026, 10, 9, 10, 0, 0, TimeSpan.Zero);

    private static WindowsMcpInstance Read(int pid, long privateBytes, double cpu) =>
        new(pid, "creds-mcp", new WindowsMcpOwner(WindowsMcpOwnerKind.Interop, 500, "wsl.exe", "wsl.exe (pid 500)"), Reading.Of(Created), Reading.Of(cpu),
            Reading.Of(privateBytes * 2), Reading.Of(privateBytes), Reading.Of(1), cpu < 2);

    private static WindowsMcpInstance Unread(int pid)
    {
        const string why = "OpenProcess failed: access denied";
        return new(pid, "creds-mcp", new WindowsMcpOwner(WindowsMcpOwnerKind.Orphaned, 999, string.Empty, "its parent (pid 999) is gone"),
            Reading.Missing<DateTimeOffset>(why), Reading.Missing<double>(why), Reading.Missing<long>(why), Reading.Missing<long>(why), Reading.Missing<int>(why), false);
    }

    private static WindowsMcpServersReport Report(params WindowsMcpInstance[] instances) =>
        WindowsMcpServersReport.From(Reading.Of(new WindowsMcpSample(1000, instances, instances, [])));

    [Fact]
    public void Memory_no_instance_could_be_read_is_unavailable_never_0()
    {
        var report = Report(Unread(1), Unread(2));

        report.MemoryRead.Should().Be(0);
        report.Held!.Available.Should().BeFalse("two instances exist and neither's memory was read: 0 bytes would be a lie");
        report.Held.Reason.Should().Contain("access denied");
        report.WorkingSet!.Available.Should().BeFalse();
        report.Servers!.Single().WorkingSet.Available.Should().BeFalse();
        report.CpuCores!.Available.Should().BeFalse();
    }

    [Fact]
    public void A_partial_sum_says_how_many_instances_it_covers()
    {
        var report = Report(Read(1, 10_000_000, 0), Read(2, 30_000_000, 50), Unread(3));

        report.Count.Should().Be(3);
        report.MemoryRead.Should().Be(2);
        report.Held.Should().Be(new ByteFigure(true, 40_000_000, null));
        report.WorkingSet.Should().Be(new ByteFigure(true, 80_000_000, null));
        report.CpuCores.Should().Be(new NumberFigure(true, 0.5, null));
        report.OrphanedCount.Should().Be(1);
        report.Instances!.Single(i => i.Pid == 3).Should().Match<WindowsMcpInstanceReport>(i => i.Created == null && i.SessionId == null && !i.PrivateBytes.Available);
        report.Instances!.Single(i => i.Pid == 1).Owner.Kind.Should().Be("interop", "the wire names the kind in words");
    }

    [Fact]
    public void No_instance_is_an_available_zero_and_an_unread_table_is_unavailable_with_its_reason()
    {
        var none = Report();
        var unread = WindowsMcpServersReport.From(Reading.Missing<WindowsMcpSample>("the Windows process table could not be read: x"));

        none.Should().Match<WindowsMcpServersReport>(r => r.Available && r.Count == 0 && r.Held!.Available && r.Held.Bytes == 0);
        unread.Should().Match<WindowsMcpServersReport>(r => !r.Available && r.Reason!.Contains("could not be read") && r.Count == null);
    }
}
