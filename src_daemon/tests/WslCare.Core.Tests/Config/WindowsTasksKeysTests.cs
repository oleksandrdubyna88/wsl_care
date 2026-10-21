using System.Text;

using FluentAssertions;

using WslCare.Core.Config;
using WslCare.Core.Files;

namespace WslCare.Core.Tests.Config;

/// <summary>
/// WR-D1 (PLAN_windows_runner.md §3.6): the three new <c>windowsTasks.*</c> keys — their ranges, their defaults in the embedded
/// <c>default.json</c>, their trust (a user layer may only lengthen them; none machine-only) — and the existing rule that holds the
/// watch's cadence under the CPU baseline's maximum.
/// </summary>
public sealed class WindowsTasksKeysTests
{
    private static ConfigLoadResult Load(string machine = "{}", string user = "{}") => ConfigLoader.Load(
    [
        (ConfigLoader.DefaultsFile, new FileReadResult.Content(ConfigLoader.EmbeddedDefaults())),
        (new ConfigLayerFile(ConfigLayer.Machine, "machine.json"), new FileReadResult.Content(Encoding.UTF8.GetBytes(machine))),
        (new ConfigLayerFile(ConfigLayer.User, "user.json"), new FileReadResult.Content(Encoding.UTF8.GetBytes(user))),
    ]);

    [Theory]
    [InlineData("windowsTasks.watchLogonDelaySeconds", 0, 3600, 60)]
    [InlineData("windowsTasks.archiveLogonDelayMinutes", 0, 720, 20)]
    [InlineData("windowsTasks.archiveEveryHours", 1, 24, 4)]
    [InlineData("windowsTasks.cleanupLogonDelayMinutes", 0, 720, 30)]
    [InlineData("windowsTasks.cleanupEveryHours", 1, 168, 24)]
    public void A_windows_task_number_is_a_user_settable_key_with_its_range_and_default(string name, int min, int max, int fallback)
    {
        var key = ConfigKeys.Find(name);

        key.Should().BeOfType<ConfigKey.IntKey>($"{name} is a number of the Windows tasks (plan §3.6)");
        var number = (ConfigKey.IntKey)key!;
        (number.Min, number.Max).Should().Be((min, max));
        number.Trust.Safe.Should().Be(SafeDirection.Higher, "a longer delay or period only does less");
        number.Trust.MachineOnly.Should().BeFalse("an unelevated runner's own user layer is the natural place (plan Q2)");
        Load().Config.Int(number).Should().Be(fallback);
    }

    [Fact]
    public void A_user_layer_value_reaches_the_configuration()
    {
        var loaded = Load(user: """{ "windowsTasks": { "watchLogonDelaySeconds": 120, "archiveLogonDelayMinutes": 45, "archiveEveryHours": 8, "cleanupLogonDelayMinutes": 45, "cleanupEveryHours": 48 } }""");

        loaded.Notices.Should().BeEmpty();
        loaded.Config.Int(ConfigKeys.WindowsTasks.WatchLogonDelaySeconds).Should().Be(120);
        loaded.Config.Int(ConfigKeys.WindowsTasks.ArchiveLogonDelayMinutes).Should().Be(45);
        loaded.Config.Int(ConfigKeys.WindowsTasks.ArchiveEveryHours).Should().Be(8);
        loaded.Config.Int(ConfigKeys.WindowsTasks.CleanupLogonDelayMinutes).Should().Be(45);
        loaded.Config.Int(ConfigKeys.WindowsTasks.CleanupEveryHours).Should().Be(48);
    }

    /// <summary>WR-T2 (RX): the cleanup task's deadline is <c>timer.runLimitMinutes</c>, and the defaults leave it room over A23's scans.</summary>
    [Fact]
    public void The_cleanup_deadline_covers_twice_the_scan_budget_at_the_defaults()
    {
        var config = Load().Config;

        (config.Int(ConfigKeys.Timer.RunLimitMinutes) * 60).Should().BeGreaterThanOrEqualTo(2 * config.Int(ConfigKeys.Temp.ScanMaxSeconds) + 60);
        Load().IsObserveOnly.Should().BeFalse();
    }

    [Fact]
    public void A_run_limit_inside_twice_the_scan_budget_breaks_the_rule()
    {
        var inside = Load(machine: """{ "timer": { "runLimitMinutes": 60 }, "temp": { "scanMaxSeconds": 3600 } }""");
        var exactly = Load(machine: """{ "timer": { "runLimitMinutes": 60 }, "temp": { "scanMaxSeconds": 1770 } }""");

        inside.IsObserveOnly.Should().BeTrue("a deadline inside the scans would end every cleanup before its first delete");
        inside.Errors.Should().Contain(e => e.Message.Contains("timer.runLimitMinutes", StringComparison.Ordinal) && e.Message.Contains("temp.scanMaxSeconds", StringComparison.Ordinal));
        // timer.runLimitMinutes 60 also breaks the older worst-case rule of a timer run, so the boundary is read off THIS rule's own error.
        exactly.Errors.Should().NotContain(e => e.Message.Contains("temp.scanMaxSeconds", StringComparison.Ordinal), "2 x 1770 + 60 s is exactly 60 minutes");
    }

    /// <summary>Plan §3.6 (held, not new): the watch task repeats every <c>mcpWatchdog.periodMinutes</c>, and that period must stay
    /// under <c>mcpServers.cpuIntervalMaxMinutes</c> or no sample finds a CPU baseline (<c>NumberRules</c>).</summary>
    [Fact]
    public void A_period_at_or_above_the_interval_maximum_breaks_the_rule()
    {
        var at = Load(machine: """{ "mcpServers": { "cpuIntervalMaxMinutes": 10 }, "mcpWatchdog": { "periodMinutes": 10 } }""");
        var under = Load(machine: """{ "mcpServers": { "cpuIntervalMaxMinutes": 10 }, "mcpWatchdog": { "periodMinutes": 9 } }""");

        at.IsObserveOnly.Should().BeTrue("a period at the interval maximum leaves the next sample without a baseline");
        at.Errors.Should().Contain(e => e.Message.Contains("mcpWatchdog.periodMinutes", StringComparison.Ordinal) && e.Message.Contains("mcpServers.cpuIntervalMaxMinutes", StringComparison.Ordinal));
        under.IsObserveOnly.Should().BeFalse(string.Join("; ", under.Errors.Select(e => e.Display)));
    }
}
