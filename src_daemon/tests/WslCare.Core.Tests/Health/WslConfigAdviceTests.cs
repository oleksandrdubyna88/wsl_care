using FluentAssertions;

using WslCare.Core.Collectors;
using WslCare.Core.Health;
using WslCare.Core.Records;

namespace WslCare.Core.Tests.Health;

/// <summary>
/// E14 S5: the <c>.wslconfig</c> advice of a full run — SHOWN, NEVER WRITTEN. One line per setting the file does not already
/// say, in the section WSL reads it from (<c>autoMemoryReclaim</c> is <c>[experimental]</c>'s, coai plan round 2026-10-09,
/// finding 0), with what the file says now and the measurement it rests on (finding 4).
/// </summary>
public sealed class WslConfigAdviceTests
{
    private static readonly WslConfigRecommendation Recommended = new(MemoryGb: 36, SwapGb: 16);

    private static WslConfigAudit Present(string memory = "", string swap = "", string reclaim = "") =>
        new("/mnt/c/Users/user/.wslconfig", true, new WslConfigSettings(memory, swap, reclaim, string.Empty), []);

    [Fact]
    public void The_wslconfig_advice_names_only_what_differs_with_what_it_says_now()
    {
        var evening = WslConfigAdvice.For(Present(memory: "48GB", swap: "12GB", reclaim: "gradual"), Recommended);
        var absent = WslConfigAdvice.For(new WslConfigAudit("/mnt/c/Users/user/.wslconfig", false, new WslConfigSettings("", "", "", ""), []), Recommended);
        var followed = WslConfigAdvice.For(Present(memory: "36gb", swap: "16GB", reclaim: "DropCache"), Recommended);

        evening.Should().Equal(
            new WslConfigAdviceLine("wsl2", "memory=36GB", "48GB", WslConfigAdvice.MemoryBasis),
            new WslConfigAdviceLine("wsl2", "swap=16GB", "12GB", WslConfigAdvice.SwapBasis),
            new WslConfigAdviceLine("experimental", "autoMemoryReclaim=dropcache", "gradual", WslConfigAdvice.ReclaimBasis));
        absent.Should().HaveCount(3).And.OnlyContain(l => l.Now == WslConfigAdvice.NotSet);
        followed.Should().BeEmpty("the file already says all three (compared without case)");
        WslConfigAdvice.SwapBasis.Should().Contain("2026-10-07_evening_overload.md", "every advised value names its measurement");
        WslConfigAdvice.ReclaimBasis.Should().Contain("2026-10-02_wsl_resource_baseline.md");
    }

    [Fact]
    public void The_full_runs_report_carries_the_advice_from_the_keys()
    {
        var now = new DateTimeOffset(2026, 10, 9, 12, 0, 0, TimeSpan.Zero);
        var sample = HealthSample.Unavailable(now, "not read", new WindowsClockSample(now, 0, 0, "not read"), Reading.Missing<string>("n/a"), Reading.Of(Present(memory: "48GB")));

        var report = Core.Collect.HealthReports.From(sample).WslConfig;

        report.Advice.Select(a => a.Line).Should().Equal("memory=36GB", "swap=16GB", "autoMemoryReclaim=dropcache");
        Core.Collect.HealthReports.From(HealthSample.Unavailable(now, "not read", new WindowsClockSample(now, 0, 0, "not read"), Reading.Missing<string>("n/a"), Reading.Missing<WslConfigAudit>("no profile"))).WslConfig.Advice
            .Should().BeEmpty("no advice about a file that was not read");
    }

    /// <summary>Own code review 2026-10-09: a file that EXISTS but could not be read is not "not set" — it may say all three. No
    /// advice is given about what was not read.</summary>
    [Fact]
    public void An_unreadable_wslconfig_gets_no_advice()
    {
        var unreadable = new WslConfigAudit("/mnt/c/Users/user/.wslconfig", true, new WslConfigSettings("", "", "", ""), ["/mnt/c/Users/user/.wslconfig could not be read: I/O error"]) { Read = false };

        WslConfigAdvice.For(unreadable, Recommended).Should().BeEmpty();
    }

    [Fact]
    public void A_swap_recommendation_of_0_advises_no_swap_line()
    {
        WslConfigAdvice.For(Present(memory: "36GB", swap: "12GB", reclaim: "dropcache"), Recommended with { SwapGb = 0 }).Should().BeEmpty();
    }
}
