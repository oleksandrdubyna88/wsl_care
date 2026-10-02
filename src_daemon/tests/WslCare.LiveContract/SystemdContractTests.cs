using FluentAssertions;

using WslCare.Core.Collectors;
using WslCare.Core.Systemd;

namespace WslCare.LiveContract;

/// <summary>The real <c>systemctl</c> and <c>journalctl</c> against the product's parsers (plan §15b #6).</summary>
public sealed class SystemdContractTests
{
    /// <summary>journald runs on every systemd host; the unit is the contract's fixed subject.</summary>
    private const string Journald = "systemd-journald.service";

    [Fact]
    public async Task Systemctl_show_of_journald_reads_as_an_active_unit_with_an_activation_time()
    {
        var unit = Available(SystemdUnit.Parse(await Live.SystemdAsync(SystemdCommands.ShowUnit(Journald))));

        unit.Id.Should().Be(Journald);
        unit.Exists.Should().BeTrue();
        unit.ActiveState.Should().Be("active");
        unit.Restarts.IsAvailable.Should().BeTrue();
        unit.ActiveEnteredAt.Should().BeOfType<Reading<DateTimeOffset>.Available>().Which.Value.Should().BeBefore(DateTimeOffset.UtcNow);
    }

    [Fact]
    public async Task Systemctl_show_of_a_unit_that_does_not_exist_reads_as_not_found()
    {
        var unit = Available(SystemdUnit.Parse(await Live.SystemdAsync(SystemdCommands.ShowUnit("wsl-care-live-contract-no-such.service"))));

        unit.Exists.Should().BeFalse();
        unit.ActiveEnteredAt.IsAvailable.Should().BeFalse("a unit that never ran has no activation time");
    }

    [Fact]
    public async Task Journalctl_disk_usage_reads_as_a_positive_byte_count()
    {
        var bytes = Available(JournalDiskUsage.Parse(await Live.SystemdAsync(SystemdCommands.JournalDiskUsage)));

        bytes.Should().BePositive();
    }

    private static T Available<T>(Reading<T> reading)
    {
        reading.Should().BeOfType<Reading<T>.Available>("the product's parser reads the real answer ({0})", reading.ReasonOrEmpty);
        return ((Reading<T>.Available)reading).Value;
    }
}
