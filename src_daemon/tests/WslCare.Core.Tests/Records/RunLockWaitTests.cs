using System.Text;

using FluentAssertions;

using WslCare.Core.Config;
using WslCare.Core.Files;
using WslCare.Core.Records;
using WslCare.TestSupport;

namespace WslCare.Core.Tests.Records;

/// <summary>
/// THE run lock's bounded wait for an ACCEPTED detached run (retro round over PR #11, O1), on a manual clock. PR43 gate round #4:
/// the loop slept a whole jitter interval past its deadline and then took the lock AFTER <c>requests.lockWaitSeconds</c>. Each
/// pause is capped to what is left of the wait, and no try is made once the deadline has passed.
/// </summary>
public sealed class RunLockWaitTests : IDisposable
{
    private static readonly TimeSpan Wait = TimeSpan.FromMilliseconds(10);

    private readonly SandboxHost _sandbox = new("run-lock-wait");

    public void Dispose() => _sandbox.Dispose();

    /// <summary>A pause that takes exactly as long on the manual clock as it was asked, and remembers what it was asked.</summary>
    private static Func<TimeSpan, CancellationToken, Task> Pausing(ManualTimeProvider clock, List<TimeSpan> asked) => (delay, _) =>
    {
        asked.Add(delay);
        clock.Advance(delay);
        return Task.CompletedTask;
    };

    /// <summary>A jitter of 20–21 ms between tries — twice the 10 ms wait, so an uncapped pause overruns the deadline.</summary>
    private static IDisposable WideJitter()
    {
        var loaded = ConfigLoader.Load(
        [
            (ConfigLoader.DefaultsFile, new FileReadResult.Content(ConfigLoader.EmbeddedDefaults())),
            (new ConfigLayerFile(ConfigLayer.Machine, "/etc/wsl-care/config.json"), new FileReadResult.Content(Encoding.UTF8.GetBytes("""{ "files": { "lockJitterMinMilliseconds": 20, "lockJitterMaxMilliseconds": 21 } }"""))),
        ]);
        loaded.Errors.Should().BeEmpty();
        return Tuning.Use(loaded.Config);
    }

    [Fact]
    public async Task The_wait_never_pauses_past_its_deadline_nor_takes_the_lock_after_it()
    {
        using var jitter = WideJitter();
        var clock = new ManualTimeProvider(FixedTimeProvider.DefaultNow) { SteppedTimestamps = true };
        var started = clock.GetTimestamp();
        // The holder lets go 15 ms in: after the 10 ms wait, so the run must NOT have the lock.
        var files = new LockFreeAfter(_sandbox.Files, () => clock.GetElapsedTime(started) >= TimeSpan.FromMilliseconds(15));
        var asked = new List<TimeSpan>();

        var taken = await RunLock.TakeAsync(_sandbox.Paths, files, Wait, clock, Pausing(clock, asked), TestContext.Current.CancellationToken);

        taken.Should().BeOfType<ExclusiveLock.Busy>("the lock was free only after requests.lockWaitSeconds had passed");
        clock.GetElapsedTime(started).Should().BeLessThanOrEqualTo(Wait, "no pause reaches past the deadline");
        asked.Should().OnlyContain(d => d <= Wait);
    }

    /// <summary>The companion: a holder that lets go INSIDE the wait hands the lock over.</summary>
    [Fact]
    public async Task A_lock_freed_within_the_wait_is_taken()
    {
        using var jitter = WideJitter();
        var clock = new ManualTimeProvider(FixedTimeProvider.DefaultNow) { SteppedTimestamps = true };
        var started = clock.GetTimestamp();
        var files = new LockFreeAfter(_sandbox.Files, () => clock.GetElapsedTime(started) >= TimeSpan.FromMilliseconds(5));

        var taken = await RunLock.TakeAsync(_sandbox.Paths, files, Wait, clock, Pausing(clock, []), TestContext.Current.CancellationToken);

        taken.Should().BeOfType<ExclusiveLock.Held>();
        ((ExclusiveLock.Held)taken).Handle.Dispose();
    }

    /// <summary>The run lock is held by "another process" until <paramref name="free"/> says it is let go.</summary>
    private sealed class LockFreeAfter(IFileSystem inner, Func<bool> free) : DelegatingFileSystem(inner)
    {
        public override ExclusiveLock TryLockExclusive(string lockPath) =>
            free() ? new ExclusiveLock.Held(new MemoryStream()) : new ExclusiveLock.Busy($"{lockPath} is held (test)");
    }
}
