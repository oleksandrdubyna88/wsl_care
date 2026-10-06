using FluentAssertions;

using WslCare.Core.Agents;
using WslCare.Core.Files;
using WslCare.TestSupport;

namespace WslCare.Core.Tests.Files;

/// <summary>
/// Plan §15q E7.S2d, consultation C-1: a listing bounded by an entry cap, the caller's deadline and its cancellation — asked at
/// every entry — that answers a folder it cannot read as UNREADABLE, never as an empty folder; and the session listing that
/// reads through it, whose scan is then never complete over a folder it lost.
/// </summary>
public sealed class BoundedListingTests : IDisposable
{
    private readonly LinuxSandbox _sandbox = new("bounded-listing");

    private TempRoot Root => _sandbox.Root;

    public void Dispose() => _sandbox.Dispose();

    private static ListingBounds Bounds(int max, bool outOfTime = false) => new(max, () => outOfTime, CancellationToken.None);

    private string FiveFiles()
    {
        foreach (var name in new[] { "a", "b", "c", "d", "e" })
        {
            Root.File($"five/{name}.log", "x");
        }

        return Root.Under("five");
    }

    [Fact]
    public void The_listing_stops_at_its_cap_and_says_so()
    {
        var folder = FiveFiles();
        var files = _sandbox.Files;

        var cut = files.ListEntries(folder, Bounds(3)).Should().BeOfType<EntryListing.Listed>().Subject;
        var whole = files.ListEntries(folder, Bounds(5)).Should().BeOfType<EntryListing.Listed>().Subject;

        cut.Entries.Should().HaveCount(3);
        cut.Complete.Should().BeFalse();
        cut.Note.Should().Contain("stopped after 3 entries");
        whole.Complete.Should().BeTrue("five entries under a cap of five is the whole folder");
        whole.Entries.Select(e => e.Name).Should().Equal("a.log", "b.log", "c.log", "d.log", "e.log");
    }

    [Fact]
    public void The_listing_stops_at_its_deadline()
    {
        var listing = _sandbox.Files.ListEntries(FiveFiles(), Bounds(100, outOfTime: true));

        listing.Should().BeOfType<EntryListing.Listed>().Which.Complete.Should().BeFalse("the deadline is asked at every entry, the first included");
    }

    [Fact]
    public void A_missing_folder_is_an_empty_complete_listing()
    {
        _sandbox.Files.ListEntries(Root.Under("absent"), Bounds(10))
            .Should().Be(new EntryListing.Listed([], true, string.Empty));
    }

    [Fact]
    public void A_folder_that_cannot_be_read_is_unreadable_never_empty()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "a folder with no read permission is a Linux mode; Windows ACLs are another test");
        Assert.SkipWhen(RegularFiles.EffectiveUid() == 0, "root reads a folder whatever its mode");
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        var folder = FiveFiles();
        File.SetUnixFileMode(folder, UnixFileMode.None);
        try
        {
            var files = _sandbox.Files;
            files.ListEntries(folder, Bounds(10)).Should().BeOfType<EntryListing.Unreadable>();
            files.ListEntries(folder).Should().BeEmpty("the unbounded listing keeps its old answer for its old callers");
        }
        finally
        {
            File.SetUnixFileMode(folder, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    /// <summary>A folder the physical file system could not read: empty when listed unbounded, unreadable when bounded.</summary>
    private sealed class OneUnreadable(IFileSystem inner, string unreadable) : DelegatingFileSystem(inner), IFileSystem
    {
        public override IReadOnlyList<FileEntry> ListEntries(string path) => Same(path) ? [] : base.ListEntries(path);

        EntryListing IFileSystem.ListEntries(string path, ListingBounds bounds) =>
            Same(path) ? new EntryListing.Unreadable($"{path}: permission denied") : Inner.ListEntries(path, bounds);

        private bool Same(string path) => string.Equals(Path.GetFullPath(path), Path.GetFullPath(unreadable), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void An_unreadable_folder_makes_the_session_scan_incomplete_never_a_complete_count()
    {
        Root.File("agent/projects/p/one.jsonl", "x");
        Root.File("agent/projects/q/two.jsonl", "x");
        var files = new OneUnreadable(_sandbox.Files, Root.Under("agent/projects/q"));

        var scan = SessionGlob.Find(files, Root.Under("agent"), "projects/*/*.jsonl", new HashSet<string>(StringComparer.Ordinal), static () => false);

        scan.Sessions.Should().ContainSingle("the readable folder's session is still found");
        scan.Complete.Should().BeFalse("a folder the listing lost may hold sessions: the count is a lower bound");
        scan.Note.Should().Contain("permission denied");
    }
}
