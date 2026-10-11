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

    /// <summary>
    /// The coai plan round's finding: age alone does not prove a root abandoned — a process alive for more than a day, or writes
    /// deep inside that never touch the root's own time. The marker beside each root names its process by pid AND start time;
    /// an old root whose process still lives is kept, and one whose marker names a process that is gone is swept.
    /// </summary>
    [Fact]
    public void An_old_root_whose_process_still_lives_is_kept_and_one_whose_process_is_gone_is_swept()
    {
        using var temp = new TempRoot("leftovers-owner");
        var now = DateTime.UtcNow;
        var live = Root(temp, $"wsl-care-test-long-{new string('d', 32)}", now.AddDays(-2));
        File.WriteAllText(live + TempRoot.MarkerSuffix, TempRoot.MarkerOf(Environment.ProcessId, CurrentStartTicks()));
        var gone = Root(temp, $"wsl-care-test-long-{new string('e', 32)}", now.AddDays(-2));
        File.WriteAllText(gone + TempRoot.MarkerSuffix, TempRoot.MarkerOf(Environment.ProcessId, CurrentStartTicks() - 1));

        var swept = TempRoot.SweepLeftovers(temp.Path, now, TempRoot.LeftoverAge);

        Directory.Exists(live).Should().BeTrue("its marker names this very process, which is alive — the root may be in use");
        Directory.Exists(gone).Should().BeFalse("its marker names a start time no live process has: the process is gone");
        File.Exists(gone + TempRoot.MarkerSuffix).Should().BeFalse("a swept root's marker goes with it");
        swept.Should().Be(1);
    }

    private static long CurrentStartTicks() => System.Diagnostics.Process.GetCurrentProcess().StartTime.ToUniversalTime().Ticks;

    /// <summary>A root as the constructor would name it, with a file in it, written <paramref name="lastWriteUtc"/>.</summary>
    private static string Root(TempRoot temp, string name, DateTime lastWriteUtc)
    {
        var path = temp.Dir(name);
        File.WriteAllText(Path.Combine(path, "left.txt"), "left behind");
        Directory.SetLastWriteTimeUtc(path, lastWriteUtc);
        return path;
    }
}
