using FluentAssertions;

using WslCare.Core.Collectors;
using WslCare.Core.Config;
using WslCare.Core.Health;
using WslCare.Core.Records;
using WslCare.Core.Tests.Health;
using WslCare.Core.Thresholds;
using WslCare.TestSupport;

namespace WslCare.Core.Tests.Thresholds;

/// <summary>
/// E15.S5: Windows memory pressure, ONE pure rule on both roads — <c>host.commit</c> (warn above
/// <c>thresholds.hostCommitWarnPercent</c>, critical above <c>thresholds.hostCommitCriticalPercent</c>), <c>host.compression</c>
/// (warn above <c>thresholds.hostCompressionWarnGb</c> GiB) and <c>host.vmmem</c> (warn above the existing
/// <c>wslConfig.vmmemAdviceGb</c>, with the vmmem advice as its reason). An unread figure is <c>unknown</c> with its reason, never
/// <c>ok</c>. Facts are hand-built at the edges; the full-run road reads the Windows disk script's CAPTURED answer.
/// </summary>
public sealed class HostRulesTests : IDisposable
{
    private const long Gib = 1L << 30;
    private readonly SandboxHost _sandbox = new("host-rules");

    public void Dispose() => _sandbox.Dispose();

    private EffectiveConfig Config(string userJson = "")
    {
        if (userJson.Length > 0)
        {
            _sandbox.WriteUserConfig(userJson);
        }

        return ConfigLoader.Load(_sandbox.Paths, _sandbox.Files).Config;
    }

    private static readonly Reading<WslConfigAudit> WslConfig =
        Reading.Of(new WslConfigAudit(@"C:\Users\user\.wslconfig", true, new WslConfigSettings("", "", "", ""), []));

    private static HostMemoryFacts Facts(
        Reading<long>? charge = null,
        Reading<long>? limit = null,
        Reading<long>? compression = null,
        Reading<long>? vmmem = null) =>
        new(
            charge ?? Reading.Of(50 * Gib),
            limit ?? Reading.Of(100 * Gib),
            compression ?? Reading.Of(2 * Gib),
            vmmem ?? Reading.Of(10 * Gib),
            Reading.Of(92 * Gib),
            WslConfig);

    /// <summary>A commit charge of <paramref name="permille"/> ‰ of a 1000-byte limit.</summary>
    private static HostMemoryFacts Commit(long permille) => Facts(Reading.Of(permille), Reading.Of(1000L));

    private Verdict Judge(HostMemoryFacts facts, string id) => HostRules.Verdicts(facts, Config()).Single(v => v.Id == id);

    /// <summary>The verdict of <paramref name="id"/>, after saying that the list holds it — a missing id is a failure that names the id.</summary>
    private static Verdict Only(IReadOnlyList<Verdict> verdicts, string id)
    {
        verdicts.Select(v => v.Id).Should().Contain(id, "the verdict list judges " + id);
        return verdicts.Single(v => v.Id == id);
    }

    [Fact]
    public void The_four_verdicts_in_their_order()
    {
        HostRules.Verdicts(Facts(), Config()).Select(v => v.Id).Should().Equal("host.commit", "host.compression", "host.vmmem", "host.defenderCpu");
    }

    /// <summary>E15.S10 §B: the Windows disk script reads vmmem now (E15.S5's open tail), so the full run judges all of them — and the
    /// two roads are ONE list again, not a list and a shorter list.</summary>
    [Fact]
    public void The_full_run_road_judges_the_same_list_as_the_windows_road()
    {
        var facts = Facts();

        HostRules.FullRunVerdicts(facts, Config()).Select(v => v.Id).Should().Equal("host.commit", "host.compression", "host.vmmem", "host.defenderCpu");
        HostRules.FullRunVerdicts(facts, Config()).Should().Equal(HostRules.Verdicts(facts, Config()), "one list on both roads");
    }

    [Theory]
    [InlineData(860, Level.Warn)]
    [InlineData(910, Level.Critical)]
    [InlineData(799, Level.Ok)]
    [InlineData(800, Level.Ok)]
    [InlineData(801, Level.Warn)]
    [InlineData(900, Level.Warn)]
    [InlineData(901, Level.Critical)]
    public void The_commit_charge_warns_above_80_percent_of_the_limit_and_is_critical_above_90(long permille, Level level)
    {
        var verdict = Judge(Commit(permille), "host.commit");

        verdict.Level.Should().Be(level, verdict.Value);
        verdict.Limit.Should().Be("warn > 80 %, critical > 90 % of the commit limit (thresholds.hostCommitWarnPercent, thresholds.hostCommitCriticalPercent)");
    }

    [Fact]
    public void The_commit_value_is_the_charge_of_the_limit_and_the_share()
    {
        var verdict = Judge(Facts(Reading.Of(86 * Gib), Reading.Of(100 * Gib)), "host.commit");

        verdict.Value.Should().Be("86.0 of 100.0 GiB = 86.0 %");
        verdict.Level.Should().Be(Level.Warn);
    }

    [Fact]
    public void A_commit_limit_of_zero_or_an_unread_charge_is_unknown_never_ok()
    {
        var zero = Judge(Facts(limit: Reading.Of(0L)), "host.commit");
        var unread = Judge(Facts(charge: Reading.Missing<long>("commit: the script printed commit.error=CimException")), "host.commit");

        zero.Should().Match<Verdict>(v => v.Level == Level.Unknown && v.Value.Length == 0 && v.Reason == "Windows reports a commit limit of 0");
        unread.Should().Match<Verdict>(v => v.Level == Level.Unknown && v.Reason == "commit: the script printed commit.error=CimException");
    }

    [Fact]
    public void Compression_warns_above_8_GiB()
    {
        var above = Judge(Facts(compression: Reading.Of((long)(8.1 * Gib))), "host.compression");
        var at = Judge(Facts(compression: Reading.Of(8 * Gib)), "host.compression");
        var unread = Judge(Facts(compression: Reading.Missing<long>("no Memory Compression process")), "host.compression");

        above.Should().Match<Verdict>(v => v.Level == Level.Warn && v.Value == "8.10 GiB" && v.Limit == "warn > 8 GiB (thresholds.hostCompressionWarnGb)");
        at.Level.Should().Be(Level.Ok);
        unread.Should().Match<Verdict>(v => v.Level == Level.Unknown && v.Reason == "no Memory Compression process");
    }

    [Fact]
    public void Vmmem_above_the_advice_key_warns_and_its_reason_is_the_advice_text()
    {
        var facts = Facts(vmmem: Reading.Of(34 * Gib));

        var verdict = Judge(facts, "host.vmmem");

        verdict.Level.Should().Be(Level.Warn);
        verdict.Value.Should().Be("34.00 GiB");
        verdict.Limit.Should().Be("warn > 24 GiB (wslConfig.vmmemAdviceGb)");
        verdict.Reason.Should().Be(VmmemAdvice.For(facts.VmmemBytes, Reading.Of(new HostMemory(92 * Gib, 40 * Gib)), WslConfig, 24), "one number for one idea: the advice and the verdict cannot disagree");
        verdict.Reason.Should().StartWith("vmmemWSL holds 34.0 GiB of the host's 92.0 GiB.");
    }

    [Fact]
    public void Vmmem_at_the_advice_key_is_ok_and_unread_vmmem_is_unknown_with_its_reason()
    {
        Judge(Facts(vmmem: Reading.Of(24 * Gib)), "host.vmmem").Level.Should().Be(Level.Ok);
        Judge(Facts(vmmem: Reading.Missing<long>(HostMemoryFacts.VmmemReadByWindows)), "host.vmmem")
            .Should().Match<Verdict>(v => v.Level == Level.Unknown && v.Reason == HostMemoryFacts.VmmemReadByWindows);
    }

    [Fact]
    public void A_key_changed_in_the_user_layer_changes_the_verdict()
    {
        var config = Config("""{ "thresholds": { "hostCommitWarnPercent": 60, "hostCompressionWarnGb": 4 } }""");

        var verdicts = HostRules.Verdicts(Facts(Reading.Of(70 * Gib), Reading.Of(100 * Gib), Reading.Of(5 * Gib)), config);

        verdicts.Single(v => v.Id == "host.commit").Level.Should().Be(Level.Warn);
        verdicts.Single(v => v.Id == "host.compression").Level.Should().Be(Level.Warn);
    }

    /// <summary>The same function on both bases: equal figures through the Windows binary's sample and through the full run's
    /// disk script give the same verdicts — vmmem too since E15.S10 §B (the disk script reads it).</summary>
    [Fact]
    public void Equal_figures_on_both_roads_give_the_same_verdicts()
    {
        var config = Config();
        var probe = new HostSample(
            Reading.Of(new HostMemory(92 * Gib, 40 * Gib)),
            Reading.Missing<VolumeUsage>("not measured"),
            Reading.Of(18 * Gib),
            Reading.Of(new HostCommit(87 * Gib, 100 * Gib)),
            Reading.Of(9 * Gib));
        var captured = VirtualDisksTests.Captured();
        var disk = captured with
        {
            Facts = captured.Facts.Map(f => f with { CommitBytes = Reading.Of(87 * Gib), CommitLimitBytes = Reading.Of(100 * Gib), CompressionBytes = Reading.Of(9 * Gib), VmmemBytes = Reading.Of(18 * Gib) }),
        };

        var windows = HostRules.Verdicts(HostMemoryFacts.FromProbe(probe, WslConfig), config);
        var fullRun = HostRules.FullRunVerdicts(HostMemoryFacts.FromWindowsDisk(disk, WslConfig), config);

        fullRun.Should().Equal(windows, "the same function over the same figures: commit, compression and vmmem alike");
        Only(windows, "host.vmmem").Value.Should().Be("18.00 GiB");
        windows.Single(v => v.Id == "host.commit").Level.Should().Be(Level.Warn);
        windows.Single(v => v.Id == "host.compression").Level.Should().Be(Level.Warn);
    }

    /// <summary>The full run judges the CAPTURED script answer (2026-10-10: 110 976 245 760 of 156 356 161 536 B committed, Memory
    /// Compression 10 582 515 712 B) inside <see cref="ThresholdRules.Evaluate"/>, after the disk verdicts.</summary>
    [Fact]
    public void The_full_run_judges_the_captured_disk_script_answer_after_the_disk_verdicts()
    {
        var config = Config();
        var unread = HealthSample.Unavailable(WindowsDiskFixture.CapturedAt, "not read", new WindowsClockSample(WindowsDiskFixture.CapturedAt, 0, 0, "not read"), Reading.Missing<string>("n/a"), Reading.Missing<WslConfigAudit>("n/a"));
        var health = unread with { WindowsDisk = VirtualDisksTests.Captured() };

        var verdicts = ThresholdRules.Evaluate(
            new ThresholdInputs(Reading.Missing<MemorySnapshot>("n/a"), Reading.Missing<VolumeUsage>("n/a"), health, TimeSpan.FromHours(4), Reading.Missing<WindowsClockSample>("n/a"), [], Reading.Missing<long>("n/a"), Reading.Missing<long>("n/a")),
            config);

        var ids = verdicts.Select(v => v.Id).ToList();
        // E19.S1: disk.volumeFree is the last disk verdict, right behind disk.hostFree; the host.* verdicts follow it.
        ids.IndexOf("disk.volumeFree").Should().Be(ids.IndexOf("disk.hostFree") + 1);
        ids.IndexOf("host.commit").Should().Be(ids.IndexOf("disk.volumeFree") + 1);
        verdicts.Single(v => v.Id == "host.commit").Should().Match<Verdict>(v => v.Level == Level.Ok && v.Value == "103.4 of 145.6 GiB = 71.0 %");
        verdicts.Single(v => v.Id == "host.compression").Should().Match<Verdict>(v => v.Level == Level.Warn && v.Value == "9.86 GiB");
        Only(verdicts, "host.vmmem").Should().Match<Verdict>(v => v.Level == Level.Ok && v.Value == "12.50 GiB", "the disk script reads vmmemWSL now (E15.S10 §B): an invented, plausible 13 421 772 800 B");
        ids.IndexOf("host.vmmem").Should().Be(ids.IndexOf("host.compression") + 1);
        Only(verdicts, "host.defenderCpu").Level.Should().Be(Level.Unknown, "the inputs carry no Defender usage");
        ids.IndexOf("host.defenderCpu").Should().Be(ids.IndexOf("host.vmmem") + 1);
    }

    /// <summary>E15.S10 §B: the full run's road reads the Windows disk script's <c>vmmem=</c> line instead of saying the Windows binary reads it.</summary>
    [Fact]
    public void The_full_run_road_reads_the_disk_scripts_vmmem_line()
    {
        var facts = HostMemoryFacts.FromWindowsDisk(VirtualDisksTests.Captured(), WslConfig);

        facts.VmmemBytes.Should().BeOfType<Reading<long>.Available>(facts.VmmemBytes.ReasonOrEmpty).Which.Value.Should().Be(13_421_772_800, "the captured answer carries an invented, plausible vmmem line (12.5 GiB)");
    }

    [Fact]
    public void A_vmmem_the_disk_script_could_not_read_is_unknown_with_its_reason_on_the_full_run_road()
    {
        var captured = VirtualDisksTests.Captured();
        var disk = captured with { Facts = captured.Facts.Map(f => f with { VmmemBytes = Reading.Missing<long>("the Windows disk script could not read the vmmemWSL working set: ProcessCommandException") }) };

        var vmmem = Only(HostRules.FullRunVerdicts(HostMemoryFacts.FromWindowsDisk(disk, WslConfig), Config()), "host.vmmem");

        vmmem.Should().Match<Verdict>(v => v.Level == Level.Unknown && v.Value.Length == 0 && v.Reason.Contains("ProcessCommandException", StringComparison.Ordinal));
    }

    private static Reading<DefenderUsage> Cores(double cores) => Reading.Of(new DefenderUsage(cores, TimeSpan.FromHours(4)));

    private Verdict JudgeDefender(Reading<DefenderUsage> usage, EffectiveConfig? config = null) =>
        Only(HostRules.FullRunVerdicts(Facts() with { Defender = usage }, config ?? Config()), "host.defenderCpu");

    /// <summary>E15.S10 RED 10: 1.01 cores warns, 1.0 is ok, no span is unknown with the reason, the key moves the edge.</summary>
    [Theory]
    [InlineData(1.01, Level.Warn)]
    [InlineData(1.0, Level.Ok)]
    [InlineData(0.76, Level.Ok)]
    [InlineData(3.2, Level.Warn)]
    [InlineData(0.0, Level.Ok)]
    public void Defender_above_one_core_warns_and_one_core_exactly_is_ok(double cores, Level level)
    {
        var verdict = JudgeDefender(Cores(cores));

        verdict.Level.Should().Be(level, verdict.Value);
        verdict.Limit.Should().Be("warn > 100 % of one core (thresholds.defenderCoresWarnPercent)");
        verdict.Value.Should().StartWith(cores.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture) + " cores");
    }

    [Fact]
    public void No_span_is_unknown_with_the_reason_never_ok()
    {
        var first = JudgeDefender(Reading.Missing<DefenderUsage>(DefenderLedgerRead.NoBaseline));
        var restarted = JudgeDefender(Reading.Missing<DefenderUsage>(DefenderCpu.Restarted));
        var failed = JudgeDefender(Reading.Missing<DefenderUsage>("powershell.exe (the Defender CPU script) did not answer within 15 s; its process tree was killed"));

        first.Should().Match<Verdict>(v => v.Level == Level.Unknown && v.Value.Length == 0 && v.Reason == DefenderLedgerRead.NoBaseline);
        restarted.Should().Match<Verdict>(v => v.Level == Level.Unknown && v.Reason == DefenderCpu.Restarted);
        failed.Level.Should().Be(Level.Unknown);
        failed.Reason.Should().Contain("did not answer within 15 s");
        first.Limit.Should().Be("warn > 100 % of one core (thresholds.defenderCoresWarnPercent)", "the limit in force is shown even when nothing was read");
    }

    [Fact]
    public void The_key_moves_the_defender_edge()
    {
        var config = Config("""{ "thresholds": { "defenderCoresWarnPercent": 150 } }""");

        JudgeDefender(Cores(1.4), config).Level.Should().Be(Level.Ok);
        JudgeDefender(Cores(1.5), config).Level.Should().Be(Level.Ok);
        JudgeDefender(Cores(1.51), config).Level.Should().Be(Level.Warn);
        JudgeDefender(Cores(1.51), config).Limit.Should().Be("warn > 150 % of one core (thresholds.defenderCoresWarnPercent)");
    }

    [Fact]
    public void The_windows_binarys_road_says_the_distro_reads_defender()
    {
        var probe = new HostSample(
            Reading.Of(new HostMemory(92 * Gib, 40 * Gib)),
            Reading.Missing<VolumeUsage>("not measured"),
            Reading.Of(18 * Gib),
            Reading.Of(new HostCommit(50 * Gib, 100 * Gib)),
            Reading.Of(2 * Gib));

        var verdict = Only(HostRules.Verdicts(HostMemoryFacts.FromProbe(probe, WslConfig), Config()), "host.defenderCpu");

        verdict.Should().Match<Verdict>(v => v.Level == Level.Unknown && v.Reason == HostMemoryFacts.DefenderReadByTheDistro);
    }

    [Fact]
    public void The_keys_default_to_the_provisional_80_90_8_and_one_core()
    {
        var config = Config();

        config.Int(ConfigKeys.Thresholds.HostCommitWarnPercent).Should().Be(80);
        config.Int(ConfigKeys.Thresholds.HostCommitCriticalPercent).Should().Be(90);
        config.Int(ConfigKeys.Thresholds.HostCompressionWarnGb).Should().Be(8);
        config.Int(ConfigKeys.Thresholds.DefenderCoresWarnPercent).Should().Be(100);
    }
}
