using FluentAssertions;

using WslCare.Core.Collectors;
using WslCare.Core.Collectors.Procfs;
using WslCare.Core.Config;
using WslCare.Core.Thresholds;

namespace WslCare.Core.Tests.Thresholds;

/// <summary>
/// E14 S6: is the machine too busy to START heavy work now? One pure rule, <see cref="MachineBusy.Judge"/>: cpu, io and memory
/// PSI <c>some avg60</c>, each against its key. BUSY when any crosses; CALM only when all three were read and none crosses;
/// UNKNOWN otherwise — an unread pressure is never calm (coai plan round 2026-10-09, finding 2).
/// </summary>
public sealed class MachineBusyTests
{
    private static readonly BusyLimits Limits = new(Cpu: 20, Io: 10, Memory: 10);

    private static Reading<Pressure> Psi(double avg60, double avg10 = 0) => Reading.Of(new Pressure(new PressureLine(avg10, avg60, 0, 0), Reading.Missing<PressureLine>("no full line")));

    private static Reading<Pressure> Unread(string why) => Reading.Missing<Pressure>(why);

    [Theory]
    [InlineData(31, 1, 0, "cpu", "thresholds.cpuPressureWarnPercent")]
    [InlineData(4, 12, 0, "io", "thresholds.ioPressureWarnPercent")]
    [InlineData(4, 1, 11, "memory", "thresholds.memoryPressureWarn")]
    public void A_cpu_io_or_memory_pressure_above_its_key_makes_the_machine_busy_naming_the_key(double cpu, double io, double memory, string resource, string key)
    {
        var judged = MachineBusy.Judge(new PressureSet(Psi(memory), Psi(io), Psi(cpu)), Limits);

        judged.State.Should().Be(BusyState.Busy);
        var reason = judged.Reasons.Should().ContainSingle().Subject;
        reason.Should().Be(new BusyReason(resource, MachineBusy.Window, resource switch { "cpu" => cpu, "io" => io, _ => memory }, resource switch { "cpu" => 20, "io" => 10, _ => 10 }, key));
    }

    [Fact]
    public void Every_read_pressure_under_its_key_is_calm_and_none_read_is_unknown()
    {
        var calm = MachineBusy.Judge(new PressureSet(Psi(0), Psi(1.69), Psi(4.18)), Limits);
        var atTheLimit = MachineBusy.Judge(new PressureSet(Psi(10), Psi(10), Psi(20)), Limits);
        var none = MachineBusy.Judge(new PressureSet(Unread("no PSI"), Unread("no PSI"), Unread("no PSI")), Limits);

        calm.State.Should().Be(BusyState.Calm, "the captured calm tree: cpu 4.18, io 1.69, memory 0");
        calm.Reasons.Should().BeEmpty();
        atTheLimit.State.Should().Be(BusyState.Calm, "above the key is busy; at it is not");
        none.State.Should().Be(BusyState.Unknown);
        none.Unread.Should().HaveCount(3).And.OnlyContain(u => u.Contains("no PSI"));
    }

    [Fact]
    public void One_unread_pressure_makes_an_otherwise_calm_machine_unknown_but_a_crossed_one_is_still_busy()
    {
        var partial = MachineBusy.Judge(new PressureSet(Unread("/proc/pressure/memory does not exist"), Psi(1), Psi(2)), Limits);
        var crossed = MachineBusy.Judge(new PressureSet(Unread("/proc/pressure/memory does not exist"), Psi(1), Psi(40)), Limits);

        partial.State.Should().Be(BusyState.Unknown, "memory was not measured: never calm by absence");
        partial.Unread.Should().ContainSingle().Which.Should().StartWith("memory: ");
        crossed.State.Should().Be(BusyState.Busy, "one measured pressure over its key is enough");
    }

    [Fact]
    public void The_limits_come_from_their_keys_and_a_value_outside_0_to_100_is_refused()
    {
        var defaults = ConfigLoader.Load([(ConfigLoader.DefaultsFile, new Core.Files.FileReadResult.Content(ConfigLoader.EmbeddedDefaults()))]).Config;

        BusyLimits.From(defaults).Should().Be(new BusyLimits(Cpu: 20, Io: 10, Memory: 10));
        ConfigValidation.Parse(ConfigKeys.Thresholds.CpuPressureWarnPercent, "101").Should().BeOfType<ValueCheck.Invalid>();
        ConfigValidation.Parse(ConfigKeys.Thresholds.IoPressureWarnPercent, "-1").Should().BeOfType<ValueCheck.Invalid>();
        ConfigValidation.Parse(ConfigKeys.Thresholds.CpuPressureWarnPercent, "100").Should().BeOfType<ValueCheck.Ok>();
    }
}
