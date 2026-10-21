using FluentAssertions;

using WslCare.Core.Collectors;
using WslCare.Core.Config;
using WslCare.Core.Health;
using WslCare.Core.Thresholds;
using WslCare.TestSupport;

namespace WslCare.Core.Tests.Thresholds;

/// <summary>
/// E15.S2's three verdicts over hand-built facts, at their edges: <c>disk.vhdxGap</c> (the D2/D3 gap warns, with the sparse
/// advice as TEXT), <c>disk.vhdxGrowth</c> (unknown until two measured sizes exist), <c>disk.hostFree</c> (warns below the floor
/// or below the KNOWN possible growth; while any file's possible growth is unknown it is <c>unknown</c>, never <c>ok</c> — the
/// plan round, 2026-10-11) — and the four keys.
/// </summary>
public sealed class DiskRulesTests : IDisposable
{
    private const long Gib = 1L << 30;
    private static readonly DateTimeOffset Now = new(2026, 10, 11, 12, 0, 0, TimeSpan.Zero);
    private readonly SandboxHost _sandbox = new("disk-rules");

    public void Dispose() => _sandbox.Dispose();

    private EffectiveConfig Config(string userJson = "")
    {
        if (userJson.Length > 0)
        {
            _sandbox.WriteUserConfig(userJson);
        }

        return ConfigLoader.Load(_sandbox.Paths, _sandbox.Files).Config;
    }

    private static VirtualDiskFacts Disk(
        string name,
        long size,
        Reading<long> used,
        MappedBy by = MappedBy.Mount,
        bool sparse = false,
        Reading<double>? growth = null,
        Reading<long>? possible = null) =>
        new(
            name,
            $@"C:\vhd\{name}\ext4.vhdx",
            Reading.Of(size),
            Reading.Of(sparse),
            Reading.Of("C"),
            by,
            by == MappedBy.DockerDf ? "Docker's own totals — an estimate, not the filesystem" : "this distro's own /",
            used,
            used.Map(u => size - u),
            growth ?? Reading.Missing<double>("one measurement so far: growth needs two measured sizes"),
            possible ?? Reading.Missing<long>("not mounted here"));

    private static VirtualDiskFacts Unmapped(string name, long size, string why = "Ubuntu-26.04 is not this distro; its file system is not mounted here") =>
        Disk(name, size, Reading.Missing<long>(why), MappedBy.None, possible: Reading.Missing<long>(why)) with { Note = why };

    private static HostingVolumeFacts Volume(long freeGib, long knownGib, params string[] unknown) =>
        new("C", Reading.Of(2400 * Gib), Reading.Of(freeGib * Gib), ["Ubuntu", .. unknown], Reading.Missing<double>("no growth yet"), knownGib * Gib, unknown);

    private static Reading<DiskFacts> Facts(IReadOnlyList<VirtualDiskFacts> disks, params HostingVolumeFacts[] volumes) =>
        Reading.Of(new DiskFacts(Now, Reading.Of("Ubuntu"), disks, volumes));

    private IReadOnlyList<Verdict> Evaluate(Reading<DiskFacts> facts, string config = "") => DiskRules.Evaluate(facts, Config(config));

    private static Verdict Find(IReadOnlyList<Verdict> verdicts, string id) => verdicts.Single(v => v.Id == id);

    /// <summary>D2/D3: Ubuntu's VHDX 237.7 GiB on the host, 194.0 GiB used by its <c>/</c>.</summary>
    private static VirtualDiskFacts MeasuredUbuntu(bool sparse = false) => Disk("Ubuntu", 255_195_086_848, Reading.Of(208_337_252_352L), sparse: sparse);

    [Fact]
    public void The_measured_gap_of_43_6_GiB_warns_with_the_compaction_advice_and_never_advises_a_sparse_vhd()
    {
        var gap = Find(Evaluate(Facts([MeasuredUbuntu()])), DiskRules.GapId);

        gap.Level.Should().Be(Level.Warn);
        gap.Value.Should().Be("Ubuntu: 237.7 − 194.0 = 43.6 GiB", "file − guest = gap, named by the file");
        gap.Limit.Should().Contain("20 GiB").And.Contain("disk.vhdxGapWarnGib");
        gap.Reason.Should().NotContain("--set-sparse true", "WSL disabled sparse VHD after data-corruption reports, and a sparse VHDX cannot be compacted (research/2026-10-02_competitor_survey.md; the never-list's sparse-vhd rule)");
        gap.Reason.Should().Contain("wsl --shutdown").And.Contain("compact").And.Contain("wsl-care never compacts");
        gap.Reason.Should().Contain("sparse VHD is not advised");
        gap.Reason.Should().Contain($"Optimize-VHD -Path '{MeasuredUbuntu().Path}'", "the advice names the file it is about, usable as written (plan round)")
            .And.Contain($"select vdisk file=\"{MeasuredUbuntu().Path}\"").And.Contain("attach vdisk readonly").And.Contain("detach vdisk")
            .And.Contain("as an administrator");
    }

    [Fact]
    public void The_suggested_powershell_command_carries_the_path_as_a_literal_that_expands_nothing()
    {
        var hostile = Disk("it's $(Remove-Item x)", 255_195_086_848, Reading.Of(208_337_252_352L));

        var reason = Find(Evaluate(Facts([hostile])), DiskRules.GapId).Reason;

        reason.Should().Contain(@"Optimize-VHD -Path 'C:\vhd\it''s $(Remove-Item x)\ext4.vhdx'",
            "a single-quoted PowerShell literal with the apostrophe doubled expands no $(...) or $env: in the administrator's session (code round)");
        reason.Should().NotContain("Optimize-VHD -Path \"");
    }

    [Fact]
    public void Dockers_data_disk_keeps_its_estimate_label_and_gets_no_compaction_advice()
    {
        var gap = Find(Evaluate(Facts([Disk("docker-data", 120 * Gib, Reading.Of(60 * Gib), MappedBy.DockerDf)])), DiskRules.GapId);

        gap.Level.Should().Be(Level.Warn, "the level is unchanged by the advice text");
        gap.Reason.Should().Contain("estimate").And.NotContain("compact vdisk").And.NotContain("--set-sparse");
    }

    [Fact]
    public void A_sparse_file_gets_no_compaction_advice_and_no_sparse_switch()
    {
        var reason = Find(Evaluate(Facts([MeasuredUbuntu(sparse: true)])), DiskRules.GapId).Reason;

        reason.Should().NotContain("--set-sparse");
        reason.Should().NotContain("compact vdisk", "a sparse VHDX cannot be compacted");
    }

    [Fact]
    public void A_gap_under_the_limit_is_ok_and_the_setting_moves_the_edge()
    {
        var small = Disk("Ubuntu", 100 * Gib, Reading.Of(90 * Gib));

        Find(Evaluate(Facts([small])), DiskRules.GapId).Level.Should().Be(Level.Ok);
        Find(Evaluate(Facts([small]), """{ "disk": { "vhdxGapWarnGib": 5 } }"""), DiskRules.GapId).Level.Should().Be(Level.Warn);
    }

    [Fact]
    public void Dockers_gap_says_it_is_dockers_estimate()
    {
        var docker = Disk(WindowsDiskParsers.DockerDataDiskName, 132_338_679_808, Reading.Of(95_357_600_000L), MappedBy.DockerDf);

        var gap = Find(Evaluate(Facts([docker])), DiskRules.GapId);

        gap.Level.Should().Be(Level.Warn);
        gap.Reason.Should().Contain("estimate").And.NotContain("--set-sparse");
    }

    [Fact]
    public void With_nothing_mapped_the_gap_is_unknown_and_says_why()
    {
        var gap = Find(Evaluate(Facts([Unmapped("Ubuntu", 237 * Gib, DistroIdentity.NotKnownYet)])), DiskRules.GapId);

        gap.Level.Should().Be(Level.Unknown);
        gap.Value.Should().BeEmpty();
        gap.Reason.Should().Contain("Ubuntu").And.Contain(DistroIdentity.NotKnownYet);
    }

    [Fact]
    public void Growth_is_unknown_until_two_measured_sizes_exist()
    {
        var growth = Find(Evaluate(Facts([MeasuredUbuntu()])), DiskRules.GrowthId);

        growth.Level.Should().Be(Level.Unknown);
        growth.Reason.Should().Contain("two measured sizes");
    }

    [Fact]
    public void A_mapped_disk_whose_gap_could_not_be_read_makes_the_gap_unknown_never_ok()
    {
        var unreadRoot = Disk("Ubuntu", 200 * Gib, Reading.Missing<long>("the distro's / was not read: statvfs failed"));
        var docker = Disk("docker-data", 120 * Gib, Reading.Of(110 * Gib), MappedBy.DockerDf);

        var gap = Find(Evaluate(Facts([unreadRoot, docker])), DiskRules.GapId);

        gap.Level.Should().Be(Level.Unknown, "Docker's small gap says nothing about the distro disk that could not be judged (code round)");
        gap.Reason.Should().Contain("Ubuntu").And.Contain("statvfs failed");
        gap.Value.Should().Contain("docker-data", "the measured disk is still shown");
    }

    [Fact]
    public void A_known_gap_over_the_limit_still_warns_while_another_mapped_gap_is_unknown()
    {
        var unreadRoot = Disk("Ubuntu", 200 * Gib, Reading.Missing<long>("statvfs failed"));
        var docker = Disk("docker-data", 120 * Gib, Reading.Of(60 * Gib), MappedBy.DockerDf);

        Find(Evaluate(Facts([unreadRoot, docker])), DiskRules.GapId).Level.Should().Be(Level.Warn);
    }

    [Fact]
    public void Without_this_distros_name_dockers_small_gap_alone_never_reads_ok()
    {
        var notKnown = "this distro's name is not known yet — any wsl-care command started through wsl.exe records it";
        var facts = Reading.Of(new DiskFacts(Now, Reading.Missing<string>(notKnown), [Unmapped("Ubuntu", 237 * Gib, notKnown), Disk("docker-data", 120 * Gib, Reading.Of(110 * Gib), MappedBy.DockerDf)], []));

        var gap = Find(Evaluate(facts), DiskRules.GapId);

        gap.Level.Should().Be(Level.Unknown, "this distro's own disk — the one that matters most — was not judged, so the gap cannot be ok (own review)");
        gap.Reason.Should().Contain("wsl.exe");
    }

    [Fact]
    public void A_disk_that_is_not_mounted_here_does_not_make_the_gap_unknown()
    {
        var gap = Find(Evaluate(Facts([Disk("Ubuntu", 200 * Gib, Reading.Of(190 * Gib)), Unmapped("Ubuntu-26.04", 70 * Gib)])), DiskRules.GapId);

        gap.Level.Should().Be(Level.Ok, "a distro mapped `none` by design is outside the gap verdict");
    }

    [Fact]
    public void A_disk_without_a_growth_figure_makes_the_growth_unknown_never_ok()
    {
        var measured = Disk("Ubuntu", 200 * Gib, Reading.Of(190 * Gib), growth: Reading.Of(1.0 * Gib));
        var fresh = Disk("docker-data", 120 * Gib, Reading.Of(110 * Gib), MappedBy.DockerDf);

        var growth = Find(Evaluate(Facts([measured, fresh])), DiskRules.GrowthId);

        growth.Level.Should().Be(Level.Unknown, "a newly seen file has no growth yet, so the verdict cannot say every file is within the limit (code round)");
        growth.Reason.Should().Contain("docker-data").And.Contain("two measured sizes");
        growth.Value.Should().Contain("Ubuntu");
    }

    [Fact]
    public void A_known_growth_over_the_limit_still_warns_while_another_disk_has_no_growth()
    {
        var fast = Disk("Ubuntu", 200 * Gib, Reading.Of(190 * Gib), growth: Reading.Of(30.0 * Gib));
        var fresh = Disk("docker-data", 120 * Gib, Reading.Of(110 * Gib), MappedBy.DockerDf);

        Find(Evaluate(Facts([fast, fresh])), DiskRules.GrowthId).Level.Should().Be(Level.Warn);
    }

    [Theory]
    [InlineData(9.9, Level.Ok)]
    [InlineData(10.1, Level.Warn)]
    public void Growth_warns_above_the_setting(double gibPerDay, Level expected)
    {
        var disk = Disk("Ubuntu", 200 * Gib, Reading.Of(190 * Gib), growth: Reading.Of(gibPerDay * Gib));

        var growth = Find(Evaluate(Facts([disk])), DiskRules.GrowthId);

        growth.Level.Should().Be(expected);
        growth.Value.Should().Contain("Ubuntu").And.Contain("GiB/day");
        growth.Limit.Should().Contain("disk.vhdxGrowthWarnGibPerDay");
    }

    [Fact]
    public void Host_free_warns_below_the_100_GiB_floor()
    {
        var free = Find(Evaluate(Facts([MeasuredUbuntu()], Volume(freeGib: 99, knownGib: 10))), DiskRules.HostFreeId);

        free.Level.Should().Be(Level.Warn);
        free.Value.Should().Contain("C:").And.Contain("99.0 GiB");
        free.Limit.Should().Contain("100 GiB").And.Contain("disk.hostFreeWarnGib");
    }

    [Fact]
    public void Host_free_warns_below_the_known_possible_growth()
    {
        var free = Find(Evaluate(Facts([MeasuredUbuntu()], Volume(freeGib: 500, knownGib: 769))), DiskRules.HostFreeId);

        free.Level.Should().Be(Level.Warn);
        free.Reason.Should().Contain("769.0 GiB");
    }

    [Fact]
    public void Host_free_is_ok_only_when_every_file_on_the_volume_has_a_known_possible_growth()
    {
        Find(Evaluate(Facts([MeasuredUbuntu()], Volume(freeGib: 1010, knownGib: 769))), DiskRules.HostFreeId).Level.Should().Be(Level.Ok);
    }

    [Fact]
    public void Host_free_with_150_GiB_free_20_GiB_known_and_one_unknown_file_reads_unknown_never_ok()
    {
        var facts = Facts([MeasuredUbuntu(), Unmapped("Ubuntu-26.04", 70 * Gib)], Volume(freeGib: 150, knownGib: 20, "Ubuntu-26.04"));

        var free = Find(Evaluate(facts), DiskRules.HostFreeId);

        free.Level.Should().Be(Level.Unknown);
        free.Value.Should().Contain("150.0 GiB", "the value still shows the free space");
        free.Reason.Should().Contain("the possible growth of 1 file is not known").And.Contain("Ubuntu-26.04").And.Contain("above the floor").And.Contain("20.0 GiB");
    }

    [Fact]
    public void Host_free_counts_every_file_whose_growth_is_unknown()
    {
        var facts = Facts([MeasuredUbuntu(), Unmapped("Ubuntu-26.04", 70 * Gib), Unmapped("docker-desktop", 1 * Gib)], Volume(freeGib: 1010, knownGib: 769, "Ubuntu-26.04", "docker-desktop"));

        Find(Evaluate(facts), DiskRules.HostFreeId).Reason.Should().Contain("the possible growth of 2 files is not known");
    }

    [Fact]
    public void A_floor_breach_still_warns_while_some_growth_is_unknown()
    {
        var facts = Facts([MeasuredUbuntu(), Unmapped("Ubuntu-26.04", 70 * Gib)], Volume(freeGib: 50, knownGib: 10, "Ubuntu-26.04"));

        Find(Evaluate(facts), DiskRules.HostFreeId).Level.Should().Be(Level.Warn);
    }

    [Fact]
    public void An_unread_disk_sample_leaves_every_disk_rules_verdict_unknown_with_its_reason()
    {
        var verdicts = Evaluate(Reading.Missing<DiskFacts>("the Windows disk script did not answer within 30 s"));

        verdicts.Select(v => v.Id).Should().Equal(DiskRules.GapId, DiskRules.GrowthId, DiskRules.HostFreeId);
        verdicts.Should().OnlyContain(v => v.Level == Level.Unknown && v.Reason.Contains("did not answer within 30 s"));
    }

    [Fact]
    public void The_disk_verdicts_follow_disk_discard_in_order()
    {
        var ids = ThresholdRules.UnreadFullRun(Config()).Select(v => v.Id).ToList();

        // E19.S1 widened the list: the three E15.S2 verdicts, then disk.volumeFree — consecutive, right behind disk.discard.
        ids.SkipWhile(id => id != "disk.discard").Take(5).Should().Equal("disk.discard", DiskRules.GapId, DiskRules.GrowthId, DiskRules.HostFreeId, VolumeFreeRules.Id);
    }

    [Fact]
    public void The_four_keys_are_display_keys_with_their_ranges_and_starting_values()
    {
        var defaults = Config();

        ConfigKeys.Disk.VhdxGapWarnGib.Should().Match<ConfigKey.IntKey>(k => k.Name == "disk.vhdxGapWarnGib" && k.Min == 1 && k.Max == 1024 && !k.Trust.MachineOnly);
        ConfigKeys.Disk.VhdxGrowthWarnGibPerDay.Should().Match<ConfigKey.IntKey>(k => k.Name == "disk.vhdxGrowthWarnGibPerDay" && k.Min == 1 && k.Max == 1024 && !k.Trust.MachineOnly);
        ConfigKeys.Disk.HostFreeWarnGib.Should().Match<ConfigKey.IntKey>(k => k.Name == "disk.hostFreeWarnGib" && k.Min == 1 && k.Max == 10240 && !k.Trust.MachineOnly);
        ConfigKeys.Disk.GrowthMinHours.Should().Match<ConfigKey.IntKey>(k => k.Name == "disk.growthMinHours" && k.Min == 1 && k.Max == 48 && !k.Trust.MachineOnly);
        defaults.Int(ConfigKeys.Disk.VhdxGapWarnGib).Should().Be(20);
        defaults.Int(ConfigKeys.Disk.VhdxGrowthWarnGibPerDay).Should().Be(10);
        defaults.Int(ConfigKeys.Disk.HostFreeWarnGib).Should().Be(100);
        defaults.Int(ConfigKeys.Disk.GrowthMinHours).Should().Be(1);
    }
}
