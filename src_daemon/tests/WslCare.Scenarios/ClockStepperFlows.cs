using System.Text.Json;

using FluentAssertions;

using WslCare.Cli;
using WslCare.Core.Collect;
using WslCare.Core.Json;
using WslCare.Core.Systemd;
using WslCare.Core.Thresholds;
using WslCare.FakeTool;
using WslCare.TestSupport;

namespace WslCare.Scenarios;

/// <summary>
/// research/2026-10-12_wsl_clock_fight.md end to end: the BUILT CLI's <c>collect</c> over the journal answers CAPTURED on
/// 2026-10-12 with Docker Desktop running — 476 clock changes in 4 h (its <c>/initd</c> steps the shared clock every 30 s)
/// and 2 backward jumps this boot — judges <c>clock.jumps</c> ok (the floor of a periodic stepper, no journal damage);
/// the same clock changes beside the 28 backward jumps captured on 2026-10-08 warn on both verdicts. The verdict levels
/// are the distro's (on Windows the health sample is the distro's to read, so only the ids are checked there).
/// </summary>
public sealed class ClockStepperFlows
{
    private const string Stepper = "clock-stepper-2026-10-12";
    private const string Fight = "windows-time-2026-10-08";

    private static ScenarioHome Home(string purpose, string jumpsFixture)
    {
        var home = CollectFlows.Captured(purpose, first: h =>
        {
            h.Script(SystemdCommands.Journalctl, SystemdCommands.TimeJumpsBackThisBoot.Arguments, 0, $"health/{jumpsFixture}/journalctl-time-jumps.out");
            // The journal searches carry an instant: matched by prefix, ahead of the 2026-10-02 answer Captured adds.
            h.Answer(new FakeAnswer(SystemdCommands.Journalctl, ["--since"], 0, ScenarioHome.Fixture($"health/{Stepper}/journalctl-search-clock.out"), string.Empty) { Prefix = true });
        });
        var proc = Path.Combine(home.SandboxRoot, "proc");
        Directory.CreateDirectory(proc);
        File.Copy(ScenarioHome.Fixture($"health/{jumpsFixture}/proc-uptime.txt"), Path.Combine(proc, "uptime"), overwrite: true);
        return home;
    }

    private static IReadOnlyList<Verdict> Thresholds(ChildResult result)
    {
        result.Exit.Should().Be((int)ExitCode.Ok, result.Stderr);
        var report = JsonSerializer.Deserialize(result.Stdout, WslCareJsonContext.Default.CollectReport) ?? throw new InvalidOperationException($"collect printed no report: {result.Stderr}");
        return report.Detail!.Thresholds;
    }

    [Fact]
    public async Task Docker_desktop_s_30_second_steps_without_journal_damage_do_not_warn()
    {
        using var home = Home("collect-clock-stepper", Stepper);

        var thresholds = Thresholds(await home.RunAsync("collect", "--json"));

        thresholds.Select(t => t.Id).Should().Contain(["clock.jumps", ClockVerdicts.FightId]);
        if (home.Paths.Side == Core.Hosting.HostSide.Wsl)
        {
            thresholds.Single(t => t.Id == ClockVerdicts.FightId).Should().Match<Verdict>(v => v.Level == Level.Ok && v.Value == "2 in 4 h");
            thresholds.Single(t => t.Id == "clock.jumps").Should().Match<Verdict>(v =>
                v.Level == Level.Ok && v.Value.StartsWith("476 in 4.0 h", StringComparison.Ordinal) && v.Reason.Contains("periodic stepper", StringComparison.Ordinal));
        }
    }

    [Fact]
    public async Task The_same_steps_while_journald_rotates_its_file_warn_on_both_verdicts()
    {
        using var home = Home("collect-clock-fight", Fight);

        var thresholds = Thresholds(await home.RunAsync("collect", "--json"));

        thresholds.Select(t => t.Id).Should().Contain(["clock.jumps", ClockVerdicts.FightId]);
        if (home.Paths.Side == Core.Hosting.HostSide.Wsl)
        {
            thresholds.Single(t => t.Id == ClockVerdicts.FightId).Should().Match<Verdict>(v => v.Level == Level.Warn && v.Reason.Contains("Docker Desktop's /initd", StringComparison.Ordinal));
            thresholds.Single(t => t.Id == "clock.jumps").Should().Match<Verdict>(v => v.Level == Level.Warn && v.Reason.Contains("clock.fight counts 28", StringComparison.Ordinal));
        }
    }
}
