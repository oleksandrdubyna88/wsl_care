using FluentAssertions;

using WslCare.Core.Collectors;
using WslCare.Core.Config;
using WslCare.Core.Health;
using WslCare.Core.Records;
using WslCare.Core.Thresholds;
using WslCare.TestSupport;

namespace WslCare.Core.Tests.Thresholds;

/// <summary>
/// <c>systemd.failedUnits</c> warns only for REAL failures (the coordinator, 2026-10-12, after the first live full run): a unit in
/// <c>systemd.ignoredFailedUnits</c> — by default <c>getty@tty1.service</c>, which hits its start limit on every WSL boot because
/// WSL has no tty1 — is not counted, and the reason still names it, so nothing is hidden.
/// </summary>
public sealed class FailedUnitsVerdictTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 10, 12, 10, 1, 0, TimeSpan.Zero);
    private readonly SandboxHost _sandbox = new("failed-units");

    public void Dispose() => _sandbox.Dispose();

    private EffectiveConfig Config(string userJson = "")
    {
        if (userJson.Length > 0)
        {
            _sandbox.WriteUserConfig(userJson);
        }

        return ConfigLoader.Load(_sandbox.Paths, _sandbox.Files).Config;
    }

    private static HealthSample Health(params string[] failed)
    {
        var unread = HealthSample.Unavailable(Now.AddHours(-4), "not read in this test", new WindowsClockSample(Now, 0, 0, "not read in this test"), Reading.Missing<string>("n/a"), Reading.Missing<WslConfigAudit>("not read in this test"));
        return unread with { FailedUnits = Reading.Of<IReadOnlyList<FailedUnit>>([.. failed.Select(u => new FailedUnit(u, $"{u} description"))]) };
    }

    private Verdict Evaluate(HealthSample health, string config = "") =>
        ThresholdRules.Evaluate(
            new ThresholdInputs(
                Reading.Missing<MemorySnapshot>("not read"),
                Reading.Missing<VolumeUsage>("not read"),
                health,
                TimeSpan.FromHours(4),
                Reading.Missing<WindowsClockSample>("no earlier run"),
                [],
                Reading.Missing<long>("not read"),
                Reading.Missing<long>("not read")),
            Config(config)).Single(v => v.Id == "systemd.failedUnits");

    [Fact]
    public void The_wsl_getty_on_tty1_alone_is_ignored_by_default_and_still_named()
    {
        var verdict = Evaluate(Health("getty@tty1.service"));

        verdict.Level.Should().Be(Level.Ok, "WSL has no tty1, so getty@tty1.service hits its start limit on every boot — not a real failure (live run 2026-10-12)");
        verdict.Value.Should().Be("0");
        verdict.Reason.Should().Contain("getty@tty1.service").And.Contain("systemd.ignoredFailedUnits");
    }

    [Fact]
    public void A_real_failure_beside_an_ignored_one_warns_and_counts_only_the_real_one()
    {
        var verdict = Evaluate(Health("getty@tty1.service", "docker.service"));

        verdict.Level.Should().Be(Level.Warn);
        verdict.Value.Should().Be("1");
        verdict.Reason.Should().Contain("docker.service").And.Contain("getty@tty1.service");
    }

    [Fact]
    public void Emptying_the_list_makes_the_getty_unit_count_again()
    {
        var verdict = Evaluate(Health("getty@tty1.service"), """{ "systemd": { "ignoredFailedUnits": [] } }""");

        verdict.Level.Should().Be(Level.Warn, "the ignore list is a setting, and an empty list ignores nothing");
    }

    [Fact]
    public void No_failed_unit_is_ok_and_an_unread_list_is_unknown()
    {
        Evaluate(Health()).Level.Should().Be(Level.Ok);
        var unread = Health() with { FailedUnits = Reading.Missing<IReadOnlyList<FailedUnit>>("systemctl did not answer") };
        Evaluate(unread).Level.Should().Be(Level.Unknown);
    }

    [Fact]
    public void The_key_defaults_to_the_getty_unit_and_takes_only_systemd_unit_names()
    {
        Config().TextList(ConfigKeys.Systemd.IgnoredFailedUnits).Should().Equal("getty@tty1.service");
        ConfigKeys.Systemd.IgnoredFailedUnits.Member.Problem("serial-getty@ttyS0.service").Should().BeEmpty();
        ConfigKeys.Systemd.IgnoredFailedUnits.Member.Problem("snap.lxd.daemon.unix.socket").Should().BeEmpty();
        foreach (var bad in new[] { "getty", "*.service", "a b.service", "../x.service", "x.service;rm", string.Empty })
        {
            ConfigKeys.Systemd.IgnoredFailedUnits.Member.Problem(bad).Should().NotBeEmpty($"{bad} is no systemd unit name");
        }
    }
}
