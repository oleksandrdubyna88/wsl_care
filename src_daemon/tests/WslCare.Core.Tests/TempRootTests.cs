using FluentAssertions;

using WslCare.TestSupport;

namespace WslCare.Core.Tests;

/// <summary>
/// <see cref="TempRoot"/> is harness code under test. A root whose delete fails at dispose stays behind — an archive run that
/// abandons a stuck base check keeps its lock for the rest of the process by design (<c>ArchiveRun.Locked</c>), and a child of a
/// test may still hold a file — and the measured result on the owner's machine (2026-10-11) was hundreds of
/// <c>wsl-care-test-*</c> folders in <c>%TEMP%</c> within four days. So a later test process removes the leftovers of earlier
/// ones: only this harness's own names, only when older than <see cref="TempRoot.LeftoverAge"/>, never anything else.
/// </summary>
public sealed class TempRootTests
{
    private static readonly string Digest = new('a', 32);

    [Fact]
    public void A_root_left_behind_by_an_earlier_process_is_swept_and_nothing_else_is()
    {
        using var temp = new TempRoot("leftovers");
        var now = DateTime.UtcNow;
        var old = Root(temp, $"wsl-care-test-archive-run-{Digest}", now.AddDays(-2));
        var fresh = Root(temp, $"wsl-care-test-archive-run-{new string('b', 32)}", now.AddHours(-1));
        var foreign = Root(temp, $"stage-{new string('c', 32)}", now.AddDays(-2));
        var noDigest = Root(temp, "wsl-care-test-archive-run", now.AddDays(-2));

        var swept = TempRoot.SweepLeftovers(temp.Path, now, TempRoot.LeftoverAge);

        Directory.Exists(old).Should().BeFalse("a root of this harness older than the leftover age is an earlier process's");
        swept.Should().Be(1);
        Directory.Exists(fresh).Should().BeTrue("a young root may belong to a test still running in another process");
        Directory.Exists(foreign).Should().BeTrue("another tool's folder is never this harness's to remove");
        Directory.Exists(noDigest).Should().BeTrue("only the exact shape the constructor writes is swept");
    }

    /// <summary>A root as the constructor would name it, with a file in it, written <paramref name="lastWriteUtc"/>.</summary>
    private static string Root(TempRoot temp, string name, DateTime lastWriteUtc)
    {
        var path = temp.Dir(name);
        File.WriteAllText(Path.Combine(path, "left.txt"), "left behind");
        Directory.SetLastWriteTimeUtc(path, lastWriteUtc);
        return path;
    }
}
