using FluentAssertions;

using WslCare.Core.Collectors;
using WslCare.Core.Health;

namespace WslCare.Core.Tests.Health;

/// <summary>E14 S7a: the Windows binary's vmmem advice — TEXT only, never acted on. Only when <c>vmmemWSL</c> holds more than
/// <c>wslConfig.vmmemAdviceGb</c>; <c>autoMemoryReclaim=dropcache</c> is advised only when <c>.wslconfig</c> does not set it, and
/// <c>wsl --shutdown</c> is named as the last resort it is (coai plan round 2026-10-09, finding 5).</summary>
public sealed class VmmemAdviceTests
{
    private const long Gib = 1L << 30;

    private static readonly Reading<HostMemory> Host = Reading.Of(new HostMemory(92 * Gib, 40 * Gib));

    private static Reading<WslConfigAudit> File(string reclaim) =>
        Reading.Of(new WslConfigAudit(@"C:\Users\user\.wslconfig", true, new WslConfigSettings("", "", reclaim, ""), []));

    [Fact]
    public void Vmmem_advice_names_the_reclaim_setting_and_wsl_shutdown()
    {
        var measured = VmmemAdvice.For(Reading.Of(34 * Gib), Host, File(""), adviceGb: 24);
        var reclaiming = VmmemAdvice.For(Reading.Of(34 * Gib), Host, File("dropCache"), adviceGb: 24);
        var small = VmmemAdvice.For(Reading.Of(10 * Gib), Host, File(""), adviceGb: 24);
        var unread = VmmemAdvice.For(Reading.Missing<long>("no vmmemWSL process"), Host, File(""), adviceGb: 24);

        measured.Should().Contain("vmmemWSL holds 34.0 GiB of the host's 92.0 GiB").And.Contain("[experimental] autoMemoryReclaim=dropcache")
            .And.Contain("never written").And.Contain("wsl --shutdown").And.Contain("last resort");
        reclaiming.Should().NotContain("add [experimental]").And.Contain("already set").And.Contain("wsl --shutdown");
        small.Should().BeEmpty("under the key: nothing to advise");
        unread.Should().BeEmpty();
    }
}
