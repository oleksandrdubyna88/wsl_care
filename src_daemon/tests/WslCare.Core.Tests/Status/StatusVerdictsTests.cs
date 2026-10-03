using FluentAssertions;

using WslCare.Core.Collectors;
using WslCare.Core.Collectors.Procfs;
using WslCare.Core.Config;
using WslCare.Core.Health;
using WslCare.Core.Hosting;
using WslCare.Core.Records;
using WslCare.Core.Status;
using WslCare.Core.Thresholds;
using WslCare.TestSupport;

namespace WslCare.Core.Tests.Status;

/// <summary>
/// The verdicts of <c>status --json</c> (plan §15g B1): the thresholds a fast sample decides are <see cref="ThresholdRules"/>'
/// own, evaluated NOW over the sample with the effective configuration; every threshold only a full run can judge is carried
/// exactly as the newest full run recorded it, with its run and age — or <c>unknown</c> with the reason. The ids and their
/// order are the ones a full run writes. Memory snapshots are SYNTHETIC, in the collector's own types.
/// </summary>
public sealed class StatusVerdictsTests : IDisposable
{
    private const long Gib = 1L << 30;
    private static readonly DateTimeOffset Now = new(2026, 10, 3, 12, 0, 0, TimeSpan.Zero);
    private readonly SandboxHost _sandbox = new("status-verdicts");

    public void Dispose() => _sandbox.Dispose();

    private EffectiveConfig Config(string userJson = "")
    {
        if (userJson.Length > 0)
        {
            _sandbox.WriteUserConfig(userJson);
        }

        return ConfigLoader.Load(_sandbox.Paths, _sandbox.Files).Config;
    }

    /// <summary>A synthetic snapshot; <paramref name="availablePercent"/> null = <c>MemAvailable</c> not in the file.</summary>
    private static MemorySnapshot Memory(double? availablePercent, long order7 = 3900, long order4 = 4000, long pageCacheGib = 2, long inactiveAnonGib = 1) =>
        new(
            Reading.Of(46 * Gib),
            availablePercent is { } a ? Reading.Of((long)(46 * Gib * a / 100)) : Reading.Missing<long>("MemAvailable is not in meminfo"),
            availablePercent is { } p ? Reading.Of(p) : Reading.Missing<double>("MemAvailable is not in meminfo"),
            Reading.Of(1 * Gib),
            Reading.Of(2 * Gib),
            Reading.Of(inactiveAnonGib * Gib),
            Reading.Of(1 * Gib),
            Reading.Of(pageCacheGib * Gib),
            Reading.Of(12 * Gib),
            Reading.Of(0L),
            Reading.Of(new Fragmentation("Normal", order4, order7, order4 * 65536, order7 * 524288)),
            new PressureSet(Reading.Of(new Pressure(new PressureLine(0, 0, 0, 0), Reading.Missing<PressureLine>("none"))), Reading.Missing<Pressure>("n/a"), Reading.Missing<Pressure>("n/a")));

    private static ProbeSample Sample(MemorySnapshot memory) =>
        new(
            HostSide.Wsl,
            Now,
            TimeSpan.FromMilliseconds(40),
            Reading.Of(new VmSample(
                Reading.Of(memory),
                Reading.Missing<ProcessSnapshot>("not read"),
                Reading.Missing<ContainerSet>("not read"),
                new Unattributed.NotComputed("not read"),
                Reading.Of(new VolumeUsage("/", 1000, 130, 870)))),
            Reading.Missing<HostSample>("the Windows binary's"));

    private static Verdict Find(IReadOnlyList<Verdict> verdicts, string id) => verdicts.Single(v => v.Id == id);

    private static readonly Reading<RecordedVerdicts> NoFullRun = Reading.Missing<RecordedVerdicts>(LastFullRun.NoFullRunYet);

    [Fact]
    public void The_2026_10_01_evening_is_critical_on_fragmentation_and_warns_on_cache_and_inactive_anon_while_MemAvailable_stays_unknown()
    {
        // The dump of research/2026-10-02_wsl_resource_baseline.md: no free block >= 64 KiB, 19 GB cache, 21 GB inactive anon;
        // MemAvailable was not recorded, so it is not invented.
        var verdicts = StatusVerdicts.From(Sample(Memory(null, order7: 0, order4: 0, pageCacheGib: 19, inactiveAnonGib: 21)), NoFullRun, Config(), Now);

        Find(verdicts, "memory.fragmentation").Level.Should().Be(Level.Critical);
        Find(verdicts, "memory.pageCache").Level.Should().Be(Level.Warn);
        Find(verdicts, "memory.inactiveAnon").Level.Should().Be(Level.Warn);
        Find(verdicts, "memory.available").Should().Match<Verdict>(v => v.Level == Level.Unknown && v.Reason.Contains("MemAvailable"));
    }

    [Fact]
    public void A_fresh_boot_is_ok_on_every_threshold_the_sample_decides()
    {
        var verdicts = StatusVerdicts.From(Sample(Memory(97)), NoFullRun, Config(), Now);

        verdicts.Where(v => v.Basis!.Source == VerdictSource.Sample).Should().HaveCount(8).And.OnlyContain(v => v.Level == Level.Ok);
    }

    [Fact]
    public void A_threshold_changed_in_the_user_layer_changes_the_verdict_status_shows()
    {
        var shipped = Find(StatusVerdicts.From(Sample(Memory(60)), NoFullRun, Config(), Now), "memory.available");
        var raised = Find(StatusVerdicts.From(Sample(Memory(60)), NoFullRun, Config("""{ "thresholds": { "memAvailableWarnPercent": 70 } }"""), Now), "memory.available");

        shipped.Level.Should().Be(Level.Ok);
        raised.Level.Should().Be(Level.Warn);
        raised.Limit.Should().Contain("warn < 70 %");
    }

    [Fact]
    public void Status_answers_every_id_a_full_run_records_in_the_same_order()
    {
        var config = Config();
        var unread = HealthSample.Unavailable(Now, "not read", new WindowsClockSample(Now, 0, 0, "not read"), Reading.Missing<string>("n/a"), Reading.Missing<WslConfigAudit>("n/a"));
        var fullRun = ThresholdRules.Evaluate(
            new ThresholdInputs(Reading.Of(Memory(50)), Reading.Of(new VolumeUsage("/", 1000, 130, 870)), unread, TimeSpan.FromHours(4), Reading.Missing<WindowsClockSample>("n/a"), [], Reading.Missing<long>("n/a"), Reading.Missing<long>("n/a")),
            config);

        var status = StatusVerdicts.From(Sample(Memory(50)), NoFullRun, config, Now);

        status.Select(v => v.Id).Should().Equal(fullRun.Select(v => v.Id), "the same records and ids collect writes (plan §15g B1)");
        status.Should().HaveCountGreaterThan(20);
    }

    [Fact]
    public void The_sample_verdicts_are_evaluated_now_and_say_so()
    {
        var verdicts = StatusVerdicts.From(Sample(Memory(60)), NoFullRun, Config(), Now);

        var sample = verdicts.Where(v => v.Basis!.Source == VerdictSource.Sample).ToList();
        sample.Select(v => v.Id).Should().Equal("memory.available", "memory.pageCache", "memory.inactiveAnon", "memory.swap", "memory.fragmentation", "memory.pressure", "wslconfig.memory", "disk.root");
        sample.Should().OnlyContain(v => v.Basis == new VerdictBasis(VerdictSource.Sample, null, Now, 0));
        Find(verdicts, "wslconfig.memory").Value.Should().Contain(StatusVerdicts.WslConfigReadByFullRun, "the audit is a full run's; the level is the memory's");
    }

    [Fact]
    public void Full_run_verdicts_are_carried_as_recorded_with_the_run_and_their_age_and_the_sample_ones_are_not()
    {
        var runId = RunId.New(Now.AddHours(-3), 4242);
        var endedAt = Now.AddHours(-3).AddSeconds(40);
        var jumps = new Verdict("clock.jumps", Level.Warn, "875 in 4.0 h (875 per 4 h)", "warn > 100 per 4 h", "systemd-resolved's \"Clock change detected\" since the last run");
        var volumes = new Verdict("docker.A4", Level.Ok, "3 volumes, 0.64 GB", "warn > 100 volumes or > 20 GB", "unattached anonymous volumes old enough for A4");
        var staleMemory = new Verdict("memory.available", Level.Critical, "3.0 %", "warn < 25 %, critical < 15 %", "recorded three hours ago");
        var recorded = Reading.Of(new RecordedVerdicts(runId, endedAt, [staleMemory, jumps, volumes]));

        var verdicts = StatusVerdicts.From(Sample(Memory(60)), recorded, Config(), Now);

        var basis = new VerdictBasis(VerdictSource.FullRun, runId.Text, endedAt, 3 * 3600 - 40);
        Find(verdicts, "clock.jumps").Should().Be(jumps with { Basis = basis });
        Find(verdicts, "docker.A4").Should().Be(volumes with { Basis = basis });
        Find(verdicts, "memory.available").Should().Match<Verdict>(v => v.Level == Level.Ok && v.Basis!.Source == VerdictSource.Sample, "the sample re-evaluates what it can decide; a recorded memory verdict is three hours old");
    }

    [Fact]
    public void Without_a_full_run_every_full_run_verdict_is_unknown_with_the_reason_and_the_limit_in_force()
    {
        var config = Config("""{ "npm": { "maxCacheGb": 9 } }""");

        var verdicts = StatusVerdicts.From(Sample(Memory(60)), NoFullRun, config, Now);

        var carried = verdicts.Where(v => v.Basis!.Source == VerdictSource.FullRun).ToList();
        carried.Select(v => v.Id).Should().Equal(ThresholdRules.UnreadFullRun(config).Select(v => v.Id));
        carried.Should().OnlyContain(v => v.Level == Level.Unknown && v.Reason == LastFullRun.NoFullRunYet && v.Value.Length == 0 && v.Basis!.RunId == null && v.Basis.AgeSeconds == null);
        Find(verdicts, "npm.cache").Limit.Should().Be("warn > 9 GB", "the limit the configuration puts in force, from the rule itself");
    }

    [Fact]
    public void A_full_run_that_recorded_no_verdict_for_an_id_leaves_it_unknown_naming_that_run()
    {
        var runId = RunId.New(Now.AddHours(-1), 7);
        var recorded = Reading.Of(new RecordedVerdicts(runId, Now.AddHours(-1), [new Verdict("clock.jumps", Level.Ok, "3 in 4.0 h (3 per 4 h)", "warn > 100 per 4 h", "…")]));

        var npm = Find(StatusVerdicts.From(Sample(Memory(60)), recorded, Config(), Now), "npm.cache");

        npm.Level.Should().Be(Level.Unknown);
        npm.Reason.Should().Contain(runId.Text).And.Contain("npm.cache");
        npm.Basis.Should().Be(new VerdictBasis(VerdictSource.FullRun, runId.Text, Now.AddHours(-1), 3600));
    }
}
