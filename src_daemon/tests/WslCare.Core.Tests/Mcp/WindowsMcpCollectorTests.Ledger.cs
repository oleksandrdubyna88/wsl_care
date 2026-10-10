using FluentAssertions;

using WslCare.Core.Collectors;
using WslCare.Core.Mcp;
using WslCare.Core.Status;
using WslCare.Core.Tests.Collectors;
using WslCare.TestSupport;

namespace WslCare.Core.Tests.Mcp;

/// <summary>
/// E14 S7b.1, READ-ONLY: the Windows side's CPU ledger — S1's ledger as it is, keyed by pid AND creation time, on Windows' unbiased
/// interrupt clock and its own boot counter. An instance with a baseline in bounds is measured over the real interval since it (no
/// window wait); the rest across the window, and recorded. A reused pid, another boot or an unreadable boot id is no baseline; an
/// elevated status reads the ledger and writes nothing.
/// </summary>
public sealed partial class WindowsMcpCollectorTests
{
    private const string BootId = "windows-117";

    /// <summary>Windows' boot counter and its unbiased clock, scripted.</summary>
    private sealed class FakeBoot(string bootId) : IWindowsBoot
    {
        public string Id { get; set; } = bootId;

        public long Milliseconds { get; set; } = 3_600_000;

        public Reading<string> BootId() => Id.Length > 0 ? Reading.Of(Id) : Reading.Missing<string>("the BootId value could not be read");

        public long UnbiasedMilliseconds() => Milliseconds;
    }

    /// <summary>A ledger place in a sandbox folder (the Windows state directory's stand-in), the boot, and a count of window waits.</summary>
    private sealed class LedgerHarness : IDisposable
    {
        private readonly SyntheticProcTree _tree = new();

        public FakeBoot Boot { get; } = new(BootId);

        public int Waits { get; private set; }

        public string Directory => _tree.Paths.UserStateDirectory;

        public string File => _tree.Paths.Rules.Join(Directory, McpCpuLedger.FileName);

        public WindowsCpuLedger Ledger(bool writes = true) => new(_tree.Files, new McpCpuLedgerPlace.WindowsState(Directory, File, writes), Boot);

        public async Task<WindowsMcpSample> Sample(FakeTable table, bool writes = true)
        {
            var clock = new ManualTimeProvider(Now) { SteppedTimestamps = true };
            var reading = await new WindowsMcpCollector(table, clock, (window, _) =>
            {
                Waits++;
                table.SecondRead = true;
                clock.Advance(window);
                return Task.CompletedTask;
            }, Ledger(writes)).SampleAsync(Defaults(), CancellationToken.None);
            return reading.Should().BeOfType<Reading<WindowsMcpSample>.Available>().Subject.Value;
        }

        public bool LedgerExists => _tree.Files.FileExists(File);

        public void Dispose() => _tree.Dispose();
    }

    /// <summary>One idle coai-mcp.exe under claude.exe, <paramref name="cpuSeconds"/> of CPU used, created <paramref name="ageMinutes"/> before now.</summary>
    private static FakeTable OneServer(double cpuSeconds, double ageMinutes = 120) =>
        new FakeTable().Process(100, 1, "claude.exe").Process(200, 100, "coai-mcp.exe", ageMinutes: ageMinutes, cpuSeconds: cpuSeconds);

    [Fact]
    public async Task A_windows_instance_without_one_is_measured_across_the_window_and_recorded()
    {
        using var h = new LedgerHarness();

        var sample = await h.Sample(OneServer(10));

        sample.Instances.Single().CpuBasis.Should().Be(McpCpuBasis.Window);
        h.Waits.Should().Be(1);
        sample.Baseline.Recorded.Should().BeTrue(sample.Baseline.Reason);
        h.LedgerExists.Should().BeTrue("this sample's reading is the next one's baseline");
    }

    [Fact]
    public async Task A_windows_instance_with_a_ledger_point_in_bounds_is_measured_over_the_interval()
    {
        using var h = new LedgerHarness();
        await h.Sample(OneServer(10));
        h.Boot.Milliseconds += 300_000;

        var later = await h.Sample(OneServer(40));

        var instance = later.Instances.Single();
        instance.CpuBasis.Should().Be(McpCpuBasis.Interval, "a point five minutes old lies between the interval's bounds");
        instance.CpuOver.Should().Be(TimeSpan.FromMinutes(5));
        instance.CpuPercent.Should().Be(Reading.Of(10.0), "30 s of CPU over 300 s is 10 % of one core");
        h.Waits.Should().Be(1, "only the first sample waited the window — the second had a baseline for its one instance");
    }

    [Fact]
    public async Task A_reused_pid_never_takes_another_processs_baseline()
    {
        using var h = new LedgerHarness();
        await h.Sample(OneServer(10, ageMinutes: 120));
        h.Boot.Milliseconds += 300_000;

        var later = await h.Sample(OneServer(40, ageMinutes: 3));

        later.Instances.Single().CpuBasis.Should().Be(McpCpuBasis.Window, "pid 200 now names a process created three minutes ago — not the one the ledger knows");
    }

    [Theory]
    [InlineData("windows-118")]
    [InlineData("")]
    public async Task Another_boot_or_an_unreadable_boot_id_is_no_baseline(string bootNow)
    {
        using var h = new LedgerHarness();
        await h.Sample(OneServer(10));
        h.Boot.Milliseconds += 300_000;
        h.Boot.Id = bootNow;

        var later = await h.Sample(OneServer(40));

        later.Instances.Single().CpuBasis.Should().Be(McpCpuBasis.Window);
        if (bootNow.Length == 0)
        {
            later.Baseline.Recorded.Should().BeFalse("no reading can name its process across samples without a boot id");
            later.Baseline.Reason.Should().Contain("boot id");
        }
    }

    [Fact]
    public async Task An_elevated_status_reads_the_ledger_and_writes_nothing()
    {
        using var h = new LedgerHarness();

        var elevated = await h.Sample(OneServer(10), writes: false);

        elevated.Baseline.Recorded.Should().BeFalse();
        h.LedgerExists.Should().BeFalse("an elevated status never writes the user's ledger");

        await h.Sample(OneServer(10));
        h.Boot.Milliseconds += 300_000;
        var read = await h.Sample(OneServer(40), writes: false);
        read.Instances.Single().CpuBasis.Should().Be(McpCpuBasis.Interval, "it still reads the baseline the user's own status recorded");
    }

    [Fact]
    public async Task The_windows_block_carries_cpu_basis_and_the_baseline()
    {
        using var h = new LedgerHarness();
        await h.Sample(OneServer(10));
        h.Boot.Milliseconds += 300_000;

        var report = WindowsMcpServersReport.From(Reading.Of(await h.Sample(OneServer(40))));

        report.Instances!.Single().CpuBasis.Should().Be("interval");
        report.Instances!.Single().CpuIntervalSeconds.Value.Should().Be(300);
        report.CpuBaseline.Should().BeEquivalentTo(new McpCpuBaselineReport(h.File, true, null));
    }

    [Fact]
    public void The_real_boot_id_and_unbiased_clock_read_on_windows()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows' boot counter and unbiased clock exist on Windows only");
#pragma warning disable CA1416 // guarded by the skip above
        var boot = new Win32Boot();
        var first = boot.UnbiasedMilliseconds();
        var id = boot.BootId();
        var second = boot.UnbiasedMilliseconds();
#pragma warning restore CA1416

        id.Should().BeOfType<Reading<string>.Available>().Which.Value.Should().MatchRegex("^windows-[0-9]+$");
        first.Should().BePositive();
        second.Should().BeGreaterThanOrEqualTo(first, "the unbiased interrupt time never goes back");
    }
}
