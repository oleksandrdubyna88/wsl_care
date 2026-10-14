using FluentAssertions;

using WslCare.Core.Files;
using WslCare.Core.Mcp;
using WslCare.TestSupport;

namespace WslCare.Core.Tests.Mcp;

/// <summary>
/// WR-D4 of PLAN_windows_runner.md (§3.2, §3.9, §11 finding 1): the Windows ledger's writers — the Windows watch and the Windows
/// <c>status</c> — read-modify-write it under ONE ledger lock, <c>mcp-cpu.lock</c> beside the ledger, taken without waiting. The file is
/// read only AFTER the lock is held; a sample that finds the lock held answers from the window and records nothing, naming why — so no
/// write ever replaces a sample it did not read.
/// </summary>
public sealed partial class WindowsMcpCollectorTests
{
    /// <summary>The real file system, except that the FIRST try of the ledger lock lets <paramref name="other"/> run first — another
    /// writer that samples and writes the ledger in full just before this one's try.</summary>
    private sealed class AnotherWriterFirst(IFileSystem inner, string lockFile, Action other) : DelegatingFileSystem(inner)
    {
        private bool _done;

        public override ExclusiveLock TryLockExclusive(string lockPath)
        {
            if (!_done && string.Equals(lockPath, lockFile, StringComparison.Ordinal))
            {
                _done = true;
                other();
            }

            return base.TryLockExclusive(lockPath);
        }
    }

    [Fact]
    public async Task A_held_ledger_lock_records_nothing_and_says_why()
    {
        using var h = new LedgerHarness();
        await h.Sample(OneServer(10));
        var before = File.ReadAllBytes(h.File);
        h.Boot.Milliseconds += 300_000;
        var held = h.Files.TryLockExclusive(h.LockFile).Should().BeOfType<ExclusiveLock.Held>().Subject;
        WindowsMcpSample sample;
        using (held.Handle)
        {
            sample = await h.Sample(OneServer(40));
        }

        sample.Baseline.Recorded.Should().BeFalse("another writer holds the ledger lock");
        sample.Baseline.Reason.Should().Contain("ledger lock").And.Contain("records nothing");
        sample.Instances.Single().CpuBasis.Should().Be(McpCpuBasis.Window, "a sample that does not hold the lock never reads the ledger: it answers from the window");
        File.ReadAllBytes(h.File).Should().Equal(before, "nothing was written over the other writer's ledger");
    }

    [Fact]
    public async Task The_ledger_is_read_after_its_lock_is_held()
    {
        using var h = new LedgerHarness();
        var start = h.Boot.Milliseconds;
        // Writer A (another process's status) samples pid 200 and writes its point right before writer B tries the lock; B reads
        // five minutes later on the unbiased clock.
        var interleaved = new AnotherWriterFirst(h.Files, h.LockFile, () =>
        {
            h.Sample(OneServer(10)).GetAwaiter().GetResult();
            h.Boot.Milliseconds = start + 300_000;
        });

        var b = await h.Sample(OneServer(40), through: interleaved);

        b.Baseline.Recorded.Should().BeTrue(b.Baseline.Reason);
        b.Instances.Single().CpuBasis.Should().Be(McpCpuBasis.Interval, "B read the ledger after taking its lock, so it saw A's point");
        var ledger = McpCpuLedger.Read(h.Files, new McpCpuLedgerPlace.WindowsState(h.Directory, h.File, Writes: true), 1 << 20);
        ledger.Entries.Single().Points.Select(p => p.MonotonicMs).Should().Equal([start, start + 300_000], "neither writer dropped the other's sample");
    }
}
