using FluentAssertions;

using WslCare.Core.Collectors;
using WslCare.Core.Config;
using WslCare.Core.Health;
using WslCare.Core.Thresholds;
using WslCare.TestSupport;

namespace WslCare.Core.Tests.Thresholds;

/// <summary>
/// E19.S1 (PLAN_disk_hygiene_windows.md §3.2): <c>disk.volumeFree</c> over every lettered Windows volume, at its edges — OR per
/// volume with critical first, percent alone on a volume under the absolute-size floor, a size floor and an ignore list for what is
/// not judged, <c>critical &gt; warn &gt; unknown &gt; ok</c> across volumes, the worst volume named first, and an unread figure
/// never <c>ok</c>.
/// </summary>
public sealed class VolumeFreeRulesTests : IDisposable
{
    private const long Gib = 1L << 30;
    private readonly SandboxHost _sandbox = new("volume-free-rules");

    public void Dispose() => _sandbox.Dispose();

    private EffectiveConfig Config(string userJson = "")
    {
        if (userJson.Length > 0)
        {
            _sandbox.WriteUserConfig(userJson);
        }

        return ConfigLoader.Load(_sandbox.Paths, _sandbox.Files).Config;
    }

    private static WindowsVolume Volume(string letter, double sizeGib, double freeGib, string fileSystem = "NTFS") =>
        new(letter, Reading.Of(0), fileSystem, Reading.Of((long)(sizeGib * Gib)), Reading.Of((long)(freeGib * Gib)), string.Empty);

    private static WindowsVolume Unread(string letter, double sizeGib, string why) =>
        new(letter, Reading.Of(0), "ReFS", Reading.Of((long)(sizeGib * Gib)), Reading.Missing<long>(why), string.Empty);

    private static Reading<IReadOnlyList<WindowsVolume>> Volumes(params WindowsVolume[] volumes) => Reading.Of<IReadOnlyList<WindowsVolume>>(volumes);

    private VolumeFreeAssessment Evaluate(Reading<IReadOnlyList<WindowsVolume>> volumes, string config = "") =>
        VolumeFreeRules.Evaluate(volumes, VolumeFreeLimits.From(Config(config)));

    private VolumeFreeAssessment Evaluate(params WindowsVolume[] volumes) => Evaluate(Volumes(volumes));

    private static VolumeJudgement Of(VolumeFreeAssessment assessment, string letter) => assessment.Volumes.Single(v => v.Letter == letter);

    [Fact]
    public void A_volume_below_the_critical_gib_is_critical_whatever_its_percent()
    {
        // 9 GiB of 200 is 4.5 % — above the critical 3 % — yet under the critical 10 GiB: OR, never AND.
        var assessment = Evaluate(Volume("D", 200, 9));

        assessment.Verdict.Level.Should().Be(Level.Critical);
        Of(assessment, "D").Level.Should().Be(Level.Critical);
    }

    [Fact]
    public void A_volume_below_the_critical_percent_is_critical_whatever_its_gib()
    {
        // 50 GiB of 2400 is 2.1 % — far above the critical 10 GiB, under the critical 3 %.
        Evaluate(Volume("C", 2400, 50)).Verdict.Level.Should().Be(Level.Critical);
    }

    [Fact]
    public void A_volume_below_the_warn_percent_warns_whatever_its_gib()
    {
        // 200 GiB of 2400 is 8.3 %: above the 30 GiB warn floor, under the warn 10 %, above the critical 3 %.
        var assessment = Evaluate(Volume("C", 2400, 200));

        assessment.Verdict.Level.Should().Be(Level.Warn);
        Of(assessment, "C").Level.Should().Be(Level.Warn);
    }

    [Fact]
    public void A_volume_below_the_warn_gib_warns_whatever_its_percent()
    {
        // 25 GiB of 150 is 16.7 %: above the warn 10 %, under the warn 30 GiB, above the critical 10 GiB.
        Evaluate(Volume("D", 150, 25)).Verdict.Level.Should().Be(Level.Warn);
    }

    [Fact]
    public void A_volume_with_room_on_both_floors_is_ok()
    {
        var assessment = Evaluate(Volume("C", 2400, 1000));

        assessment.Verdict.Level.Should().Be(Level.Ok);
        Of(assessment, "C").JudgedBy.Should().Be("gib-or-percent");
    }

    [Fact]
    public void A_floor_is_a_strict_limit_so_a_volume_exactly_on_it_does_not_breach()
    {
        // exactly 30 GiB free of 300 (10.0 %): neither the warn GiB nor the warn percent is undercut.
        Evaluate(Volume("D", 300, 30)).Verdict.Level.Should().Be(Level.Ok);
    }

    [Fact]
    public void A_small_mostly_empty_volume_is_ok()
    {
        // A 16 GiB volume with 12 GiB free (75 %) and a 9 GiB one with 2 GiB free (22 %): under the 100 GiB absolute-size floor they
        // are judged by percent alone — the GiB floors would warn the first forever and make the second critical forever.
        var assessment = Evaluate(Volume("G", 16, 12), Volume("H", 9, 2));

        assessment.Verdict.Level.Should().Be(Level.Ok);
        Of(assessment, "G").JudgedBy.Should().Be("percent");
        Of(assessment, "H").JudgedBy.Should().Be("percent");
    }

    [Fact]
    public void A_small_volume_that_is_nearly_full_still_breaches_by_percent()
    {
        // 90 GiB with 2 GiB free is 2.2 % — under the critical 3 %, judged by percent alone.
        Evaluate(Volume("G", 90, 2)).Verdict.Level.Should().Be(Level.Critical);
    }

    [Fact]
    public void The_absolute_size_floor_is_inclusive_a_volume_of_exactly_that_size_gets_the_gib_floors()
    {
        // 100 GiB with 20 GiB free is 20 %: ok by percent, but under the 30 GiB warn floor — it warns only if the GiB floors apply.
        Of(Evaluate(Volume("D", 100, 20)), "D").Level.Should().Be(Level.Warn);
        Of(Evaluate(Volume("D", 99.9, 20)), "D").Level.Should().Be(Level.Ok, "just under 100 GiB: percent alone");
    }

    [Fact]
    public void The_value_names_the_worst_volume_first()
    {
        var assessment = Evaluate(Volume("C", 2400, 988.8), Volume("D", 195, 4.1));

        assessment.Verdict.Value.Should().Be("D: 4.1 GiB free (2.1 %) — critical; C: 988.8 GiB free (41.2 %) — ok");
        assessment.Verdict.Level.Should().Be(Level.Critical);
    }

    [Fact]
    public void A_critical_volume_with_a_larger_free_share_still_outranks_a_warn_volume_with_a_smaller_one()
    {
        // C: 80 of 2400 GiB is 3.3 % (warn: above the critical 3 %); E: 9 of 150 GiB is 6 % (critical by GiB). Critical beats warn
        // whatever the shares — a rank that read critical as warn would name C: first and say warn.
        var assessment = Evaluate(Volume("C", 2400, 80), Volume("E", 150, 9));

        assessment.Verdict.Level.Should().Be(Level.Critical);
        assessment.Verdict.Value.Should().StartWith("E: 9.0 GiB free (6.0 %) — critical; C: 80.0 GiB free (3.3 %) — warn");
    }

    [Fact]
    public void An_unread_volume_ranks_above_ok_so_it_is_named_before_an_ok_one()
    {
        var assessment = Evaluate(Volume("C", 2400, 1000), Unread("D", 195, "not read"));

        assessment.Verdict.Level.Should().Be(Level.Unknown);
        assessment.Verdict.Value.Should().StartWith("D: free not read — unknown; C:");
    }

    [Fact]
    public void A_volume_smaller_than_the_floor_is_not_judged()
    {
        // The keyboard's USB stick: 29.7 MiB, full — never a warning (D1).
        var stick = new WindowsVolume("E", Reading.Of(1), "FAT", Reading.Of(31_170_560L), Reading.Of(0L), "TITAN L");

        var assessment = Evaluate(Volume("C", 2400, 1000), stick);

        assessment.Verdict.Level.Should().Be(Level.Ok);
        assessment.Verdict.Value.Should().NotContain("E:");
        var judged = Of(assessment, "E");
        judged.JudgedBy.Should().StartWith("not judged: ").And.Contain("disk.volumeFreeMinSizeGib");
        judged.InVerdict.Should().BeFalse();
        assessment.Verdict.Reason.Should().Contain("E: not judged");
    }

    [Fact]
    public void An_ignored_letter_is_not_judged()
    {
        var assessment = Evaluate(Volumes(Volume("C", 2400, 1000), Volume("D", 195, 4.1)), """{ "disk": { "volumeFreeIgnoreLetters": ["D"] } }""");

        assessment.Verdict.Level.Should().Be(Level.Ok, "the critical D: is on the ignore list");
        Of(assessment, "D").JudgedBy.Should().StartWith("not judged: ").And.Contain("disk.volumeFreeIgnoreLetters");
        Of(assessment, "D").InVerdict.Should().BeFalse();
    }

    [Fact]
    public void An_unread_free_is_unknown_never_ok()
    {
        var assessment = Evaluate(Volume("C", 2400, 1000), Unread("D", 195, "Get-Volume gave no free space"));

        assessment.Verdict.Level.Should().Be(Level.Unknown, "one unread volume never lets the rest read ok");
        assessment.Verdict.Reason.Should().Contain("Get-Volume gave no free space");
        Of(assessment, "D").Level.Should().Be(Level.Unknown);
        Of(assessment, "D").JudgedBy.Should().StartWith("not judged: ").And.Contain("Get-Volume gave no free space");
        assessment.Verdict.Value.Should().Contain("D: free not read");
    }

    [Fact]
    public void An_unread_size_is_unknown_never_ok()
    {
        var unread = new WindowsVolume("D", Reading.Of(0), "ReFS", Reading.Missing<long>("size not reported"), Reading.Of(5 * Gib), "git");

        var assessment = Evaluate(unread);

        assessment.Verdict.Level.Should().Be(Level.Unknown);
        assessment.Verdict.Reason.Should().Contain("size not reported");
    }

    [Fact]
    public void An_unread_volume_beside_a_critical_one_leaves_it_critical()
    {
        var assessment = Evaluate(Unread("F", 195, "not read"), Volume("D", 195, 4.1));

        assessment.Verdict.Level.Should().Be(Level.Critical);
        assessment.Verdict.Value.Should().StartWith("D: 4.1 GiB free (2.1 %) — critical");
    }

    [Fact]
    public void A_warn_beside_an_unread_volume_stays_a_warn()
    {
        Evaluate(Unread("F", 195, "not read"), Volume("D", 195, 20)).Verdict.Level.Should().Be(Level.Warn, "critical > warn > unknown > ok");
    }

    [Fact]
    public void An_unavailable_sample_is_unknown_with_its_reason()
    {
        var assessment = Evaluate(Reading.Missing<IReadOnlyList<WindowsVolume>>("the Windows disk script did not answer within 30 s"));

        assessment.Verdict.Level.Should().Be(Level.Unknown);
        assessment.Verdict.Id.Should().Be("disk.volumeFree");
        assessment.Verdict.Reason.Should().Contain("did not answer within 30 s");
        assessment.Verdict.Value.Should().BeEmpty("a figure that could not be read is not rendered as one");
        assessment.Volumes.Should().BeEmpty();
    }

    [Fact]
    public void A_sample_with_no_judged_volume_is_unknown_not_ok()
    {
        var assessment = Evaluate(Volumes(Volume("C", 2400, 1000)), """{ "disk": { "volumeFreeIgnoreLetters": ["C"] } }""");

        assessment.Verdict.Level.Should().Be(Level.Unknown, "nothing was judged, so nothing is vouched for");
        assessment.Verdict.Reason.Should().Contain("no lettered volume is judged");
    }

    [Fact]
    public void The_limit_text_names_both_floors()
    {
        Evaluate(Volume("C", 2400, 1000)).Verdict.Limit.Should().Be(
            "warn < 30 GiB or < 10 %, critical < 10 GiB or < 3 % (GiB floors only on volumes of 100 GiB or more; volumes under 8 GiB not judged)");
    }

    [Fact]
    public void The_limit_text_is_built_from_the_keys_in_force()
    {
        const string config = """{ "disk": { "volumeFreeWarnGib": 50, "volumeFreeCriticalGib": 20, "volumeFreeWarnPercent": 15, "volumeFreeCriticalPercent": 5, "volumeFreeMinSizeGib": 16, "volumeFreeAbsoluteMinSizeGib": 200 } }""";

        Evaluate(Volumes(Volume("C", 2400, 1000)), config).Verdict.Limit.Should().Be(
            "warn < 50 GiB or < 15 %, critical < 20 GiB or < 5 % (GiB floors only on volumes of 200 GiB or more; volumes under 16 GiB not judged)");
    }

    [Fact]
    public void The_reason_says_which_rule_judged_each_volume()
    {
        var assessment = Evaluate(Volume("C", 2400, 1000), Volume("G", 16, 12));

        assessment.Verdict.Reason.Should().Contain("C: judged by GiB or percent").And.Contain("G: judged by percent alone");
    }

    [Fact]
    public void The_recorded_d1_sample_warns_on_D_and_does_not_judge_E()
    {
        // The 2026-10-10 capture: E: FAT 29.7 MiB, D: ReFS 195.3 GiB with 23.1 GiB free (11.8 %), C: NTFS 41 % free, F: ReFS 62 % free.
        var facts = WindowsDiskParsers.Parse(WindowsDiskFixture.Answer, 5000, 60);

        var assessment = Evaluate(facts.Bind(f => f.Volumes));

        assessment.Verdict.Level.Should().Be(Level.Warn, "D: has 23.1 GiB free, under the 30 GiB warn floor of a 195 GiB volume");
        Of(assessment, "D").Level.Should().Be(Level.Warn);
        Of(assessment, "C").Level.Should().Be(Level.Ok);
        Of(assessment, "F").Level.Should().Be(Level.Ok);
        Of(assessment, "E").InVerdict.Should().BeFalse("29.7 MiB is under the 8 GiB size floor");
        Of(assessment, "E").JudgedBy.Should().StartWith("not judged: ");
        assessment.Verdict.Value.Should().StartWith("D: 23.1 GiB free (11.8 %) — warn");
    }

    [Fact]
    public void An_unread_full_run_leaves_the_volume_verdict_unknown_with_a_reason()
    {
        var verdict = ThresholdRules.UnreadFullRun(Config()).Single(v => v.Id == VolumeFreeRules.Id);

        verdict.Level.Should().Be(Level.Unknown);
        verdict.Reason.Should().NotBeEmpty();
        verdict.Limit.Should().Contain("warn < 30 GiB or < 10 %");
    }

    [Fact]
    public void The_seven_keys_are_display_keys_with_their_ranges_and_starting_values()
    {
        Int("disk.volumeFreeWarnGib").Should().Match<ConfigKey.IntKey>(k => k.Min == 1 && k.Max == 10240 && !k.Trust.MachineOnly);
        Int("disk.volumeFreeCriticalGib").Should().Match<ConfigKey.IntKey>(k => k.Min == 1 && k.Max == 10240 && !k.Trust.MachineOnly);
        Int("disk.volumeFreeWarnPercent").Should().Match<ConfigKey.IntKey>(k => k.Min == 1 && k.Max == 90 && !k.Trust.MachineOnly);
        Int("disk.volumeFreeCriticalPercent").Should().Match<ConfigKey.IntKey>(k => k.Min == 1 && k.Max == 90 && !k.Trust.MachineOnly);
        Int("disk.volumeFreeMinSizeGib").Should().Match<ConfigKey.IntKey>(k => k.Min == 1 && k.Max == 1024 && !k.Trust.MachineOnly);
        Int("disk.volumeFreeAbsoluteMinSizeGib").Should().Match<ConfigKey.IntKey>(k => k.Min == 8 && k.Max == 10240 && !k.Trust.MachineOnly);
        ConfigKeys.Find("disk.volumeFreeIgnoreLetters").Should().BeOfType<ConfigKey.TextListKey>()
            .Which.Should().Match<ConfigKey.TextListKey>(k => k.Allowed.Count == 0 && k.MaxMembers == 26 && k.Member is TextRule.Matching && !k.Trust.MachineOnly);
        VolumeFreeLimits.From(Config()).Should().BeEquivalentTo(new VolumeFreeLimits(30, 10, 10, 3, 8, 100, []));
    }

    [Theory]
    [InlineData("d")]
    [InlineData("DD")]
    [InlineData("1")]
    [InlineData("")]
    public void An_ignore_member_that_is_not_one_capital_letter_is_refused(string member)
    {
        var key = ConfigKeys.Find("disk.volumeFreeIgnoreLetters").Should().BeOfType<ConfigKey.TextListKey>().Subject;

        key.Member.Problem(member).Should().NotBeEmpty();
        key.Member.Problem("D").Should().BeEmpty();
    }

    private static ConfigKey.IntKey Int(string name) => ConfigKeys.Find(name).Should().BeOfType<ConfigKey.IntKey>().Subject;
}
