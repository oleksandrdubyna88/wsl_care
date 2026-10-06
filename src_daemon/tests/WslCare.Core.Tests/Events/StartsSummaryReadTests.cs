using FluentAssertions;

using WslCare.Core.Config;
using WslCare.Core.Events;
using WslCare.Core.Files;
using WslCare.TestSupport;

namespace WslCare.Core.Tests.Events;

/// <summary>
/// E7.S2b/S2c review C-M5: the follower recomputes its 24-hour summary every few minutes as root — it reads the day files that can
/// hold the last 24 hours, never every kept day; and the retention of those files is root's, machine-layer only, at most 90 days.
/// </summary>
public sealed class StartsSummaryReadTests : IDisposable
{
    private static readonly DateTimeOffset Now = FixedTimeProvider.DefaultNow;
    private readonly SandboxHost _sandbox = new("starts-summary");

    public void Dispose() => _sandbox.Dispose();

    private sealed class CountingFiles(IFileSystem inner) : DelegatingFileSystem(inner)
    {
        public List<string> Read { get; } = [];

        public override FileReadResult ReadFile(string path)
        {
            Read.Add(Path.GetFileName(path));
            return base.ReadFile(path);
        }
    }

    [Fact]
    public void The_summary_reads_only_the_day_files_of_the_last_24_hours()
    {
        var store = new ContainerStartsStore(_sandbox.Paths, _sandbox.Files);
        store.Append(new CoverageLine.Covered(Now.AddDays(-10)));
        store.Append(new CoverageLine.Covered(Now.AddDays(-1).AddHours(2)));
        store.Append(new CoverageLine.Covered(Now.AddMinutes(-5)));
        var counting = new CountingFiles(_sandbox.Files);

        new ContainerStartsStore(_sandbox.Paths, counting).WriteSummary(Now).IsAllowed.Should().BeTrue();

        counting.Read.Where(f => f.EndsWith(".jsonl", StringComparison.Ordinal)).Should().BeEquivalentTo(["2026-10-01.jsonl", "2026-10-02.jsonl"], "the 10-day-old file is outside the 24-hour window");
    }

    [Fact]
    public void The_starts_retention_is_machine_only_and_at_most_90_days()
    {
        ConfigKeys.Events.StartsRetentionDays.Trust.MachineOnly.Should().BeTrue();
        ConfigKeys.Events.StartsRetentionDays.Max.Should().Be(90);
    }
}
