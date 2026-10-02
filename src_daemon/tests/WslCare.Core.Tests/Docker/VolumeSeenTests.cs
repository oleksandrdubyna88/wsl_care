using FluentAssertions;

using WslCare.Core.Collectors;
using WslCare.Core.Docker;
using WslCare.TestSupport;

namespace WslCare.Core.Tests.Docker;

/// <summary><c>volume-seen.json</c> (plan §5, §15 #0/#4, §15b #3): first sighting kept, new ones stamped now,
/// vanished ones dropped; written atomically when the state directory is writable, read-only when it is not.</summary>
public sealed class VolumeSeenTests
{
    private static readonly DateTimeOffset Monday = new(2026, 9, 28, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Friday = new(2026, 10, 2, 12, 0, 0, TimeSpan.Zero);
    private static readonly string A = new('a', 64);
    private static readonly string B = new('b', 64);
    private static readonly string C = new('c', 64);

    [Fact]
    public void A_name_still_unattached_keeps_its_first_sighting_a_new_one_is_stamped_now_and_a_vanished_one_is_dropped()
    {
        var monday = VolumeSeenRecord.Empty.Observe([A, B], Monday);

        var friday = monday.Observe([B, C], Friday);

        friday.Volumes.Should().Equal(new VolumeSighting(B, Monday), new VolumeSighting(C, Friday));
        friday.FirstSeen(A).IsAvailable.Should().BeFalse("A was removed or attached again: its age restarts if it comes back");
        friday.UpdatedAt.Should().Be(Friday);
    }

    [Fact]
    public void The_record_round_trips_through_the_state_directory_and_leaves_no_temporary_file()
    {
        using var sandbox = new SandboxHost("volume-seen");
        var store = new VolumeSeenStore(sandbox.Paths, sandbox.Files);
        var record = VolumeSeenRecord.Empty.Observe([A], Monday);

        store.TryWrite(record).Should().BeOfType<VolumeSeenWrite.Written>();
        var load = store.Read();

        load.Problem.Should().BeEmpty();
        load.Record.FirstSeen(A).Should().Be(Reading.Of(Monday));
        Directory.GetFiles(sandbox.Paths.StateDirectory).Select(Path.GetFileName).Should().Equal(VolumeSeenStore.FileName);
    }

    [Fact]
    public void No_record_yet_is_an_empty_record_and_a_broken_one_is_reported_not_silently_empty()
    {
        using var sandbox = new SandboxHost("volume-seen-broken");
        var store = new VolumeSeenStore(sandbox.Paths, sandbox.Files);

        store.Read().Should().Be(new VolumeSeenLoad(VolumeSeenRecord.Empty, string.Empty));
        sandbox.Root.File(Path.GetRelativePath(sandbox.Root.Path, store.File), "{ not json");
        var broken = store.Read();

        broken.Record.Volumes.Should().BeEmpty();
        broken.Problem.Should().Contain("is not a volume-seen record");
    }

    [Fact]
    public async Task An_unwritable_state_directory_is_read_only_the_write_is_refused_by_the_os_and_nothing_is_written()
    {
        using var sandbox = new SandboxHost("volume-seen-denied");
        Directory.CreateDirectory(sandbox.Paths.StateDirectory);
        var store = new VolumeSeenStore(sandbox.Paths, sandbox.Files);
        await using var denial = await AccessDenial.TryDenyAsync(sandbox.Paths.StateDirectory);
        Assert.SkipWhen(denial is null, "this account is not bound by a directory denial (root or elevated)");

        var write = store.TryWrite(VolumeSeenRecord.Empty.Observe([A], Friday));

        write.Should().BeOfType<VolumeSeenWrite.NotWritten>().Which.Reason.Should().StartWith("read-only:");
        await denial!.DisposeAsync();
        File.Exists(store.File).Should().BeFalse();
    }
}
