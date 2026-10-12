using System.Text;

using FluentAssertions;

using WslCare.Core.Actions.Engine;
using WslCare.Core.Config;
using WslCare.Core.Files;
using WslCare.Core.Records;
using WslCare.TestSupport;

namespace WslCare.Core.Tests.Actions;

/// <summary>
/// WR-D1 (PLAN_windows_runner.md §3.2, §3.5): <see cref="DryRunWindow.Peek"/> says what a timer run NOW would decide — "dryRun on",
/// "the first week until …", "not started: the first run starts it" — and never writes the stamp, which only
/// <see cref="DryRunWindow.Decide"/> does, at the first timer run that reaches a gated body.
/// </summary>
public sealed class DryRunPeekTests : IDisposable
{
    private static readonly DateTimeOffset Now = FixedTimeProvider.DefaultNow;

    private readonly SandboxHost _sandbox = new("dry-run-peek");

    public void Dispose() => _sandbox.Dispose();

    private static EffectiveConfig Config(bool dryRun) => ConfigLoader.Load(
    [
        (ConfigLoader.DefaultsFile, new FileReadResult.Content(ConfigLoader.EmbeddedDefaults())),
        (new ConfigLayerFile(ConfigLayer.User, "user.json"), new FileReadResult.Content(Encoding.UTF8.GetBytes($$"""{ "dryRun": {{(dryRun ? "true" : "false")}} }"""))),
    ]).Config;

    private string Stamp => DryRunWindow.File(_sandbox.Paths);

    private void WriteStamp(string text)
    {
        Directory.CreateDirectory(_sandbox.Paths.StateDirectory);
        File.WriteAllText(Stamp, text);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Peek_never_writes_the_stamp(bool dryRun)
    {
        var peek = DryRunWindow.Peek(_sandbox.Paths, _sandbox.Files, Config(dryRun), Now);

        File.Exists(Stamp).Should().BeFalse("a peek reads the stamp and never writes it — only the first timer run does");
        peek.Week.Should().Be(DryWeek.NotStarted);
        peek.DryRun.Should().BeTrue("a timer run now would start the week, so it would be dry either way");
        peek.Setting.Should().Be(dryRun);
        peek.Until.Should().BeNull();
        peek.Reason.Should().Contain("not started").And.Contain("first");
    }

    [Fact]
    public void A_peek_leaves_an_unreadable_stamp_as_it_found_it_and_says_the_week_restarts()
    {
        WriteStamp("{ not json");

        var peek = DryRunWindow.Peek(_sandbox.Paths, _sandbox.Files, Config(dryRun: false), Now);

        File.ReadAllText(Stamp).Should().Be("{ not json", "only the timer's Decide rewrites an unreadable stamp");
        peek.Week.Should().Be(DryWeek.Restarts);
        peek.DryRun.Should().BeTrue();
        peek.Reason.Should().Contain("restarts");
    }

    [Fact]
    public void A_running_week_is_named_with_its_end()
    {
        WriteStamp($$"""{ "schemaVersion": 1, "at": "{{Now.AddDays(-3):O}}" }""");

        var peek = DryRunWindow.Peek(_sandbox.Paths, _sandbox.Files, Config(dryRun: false), Now);

        peek.Week.Should().Be(DryWeek.Running);
        peek.Until.Should().Be(Now.AddDays(4));
        peek.DryRun.Should().BeTrue();
        peek.Reason.Should().Contain("until").And.Contain(Now.AddDays(4).UtcDateTime.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture));
    }

    [Fact]
    public void A_passed_week_with_the_setting_off_is_a_real_run_and_the_setting_on_keeps_it_dry()
    {
        WriteStamp($$"""{ "schemaVersion": 1, "at": "{{Now.AddDays(-8):O}}" }""");

        var off = DryRunWindow.Peek(_sandbox.Paths, _sandbox.Files, Config(dryRun: false), Now);
        var on = DryRunWindow.Peek(_sandbox.Paths, _sandbox.Files, Config(dryRun: true), Now);

        off.Week.Should().Be(DryWeek.Passed);
        off.DryRun.Should().BeFalse();
        on.Week.Should().Be(DryWeek.Passed);
        on.DryRun.Should().BeTrue("two locks on one door: the setting alone keeps the run dry");
        on.Reason.Should().Contain("dryRun is on");
    }

    /// <summary>The peek and the decision cannot drift: for every state, what the peek says a timer run would decide is what that
    /// run then decides.</summary>
    [Theory]
    [InlineData("", false)]
    [InlineData("", true)]
    [InlineData("{ not json", false)]
    [InlineData("-3", false)]
    [InlineData("-8", false)]
    [InlineData("-8", true)]
    public void A_peek_says_what_the_timer_s_decision_then_decides(string stamp, bool dryRun)
    {
        if (stamp.Length > 0)
        {
            WriteStamp(stamp.StartsWith('-') ? $$"""{ "schemaVersion": 1, "at": "{{Now.AddDays(int.Parse(stamp, System.Globalization.CultureInfo.InvariantCulture)):O}}" }""" : stamp);
        }

        var config = Config(dryRun);
        var peek = DryRunWindow.Peek(_sandbox.Paths, _sandbox.Files, config, Now);
        using (Tuning.Use(config))
        {
            var decision = DryRunWindow.Decide(RunTrigger.Timer, config, _sandbox.Paths, _sandbox.Files, Now);

            peek.DryRun.Should().Be(decision.DryRun, decision.Reason);
        }
    }
}
