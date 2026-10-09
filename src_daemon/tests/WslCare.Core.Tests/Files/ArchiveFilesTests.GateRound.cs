using FluentAssertions;

using WslCare.Core.Files;
using WslCare.TestSupport;

namespace WslCare.Core.Tests.Files;

/// <summary>
/// Plan §15r <i>E9.S2a gate round</i> — what the coai code round on the seam found: a folder swapped for a link between its check
/// and the open (Windows: the open is by path), a destination folder swapped while the archive creates in it, a Windows folder
/// flush that never reached the disk, a Windows source of another account, and a folder handle only <see cref="PhysicalFileSystem"/>
/// could make. The swap rows run on both systems: Linux acts through descriptors, so the swap reaches nothing there either.
/// </summary>
public sealed partial class ArchiveFilesTests
{
    private const string MyAccount = "S-1-5-21-1000-2000-3000-1001";

    /// <summary>Finding 3: the folder holding a quarantined file is renamed away and a link to another folder put at its name AFTER
    /// the way was checked — the removal must never hash-and-remove the file the link leads to (same name, same bytes, outside the
    /// agent's folder, never judged by the policy).</summary>
    [Fact]
    public void A_folder_swapped_for_a_link_after_its_check_never_removes_the_file_the_link_leads_to()
    {
        var quarantined = Write($"{Layout}/projects/p/s12.jsonl.wsl-care-q-r1", "twelve");
        var copy = Write($"{Base}/claude-code/s12.jsonl", "twelve");
        var elsewhere = Write("/srv/elsewhere/s12.jsonl.wsl-care-q-r1", "twelve");
        Assert.SkipUnless(CanLink(), "this account may not create a directory link here");
        var swapped = SwapAtTheCheck(On($"{Layout}/projects/p"), On("/srv/elsewhere"));

        var removal = Files.RemoveVerified(On(Layout), quarantined, Sha("twelve"), copy, Removal);

        swapped().Should().BeTrue("own review round M6: the swap happened between the check and the act");
        File.Exists(elsewhere).Should().BeTrue($"the file behind the link was never judged (the removal answered {removal})");
        File.ReadAllText(elsewhere).Should().Be("twelve");
        removal.Should().NotBeOfType<VerifiedRemoval.Removed>("the act follows the judged real path, and a link on it refuses (Linux: O_NOFOLLOW from the root; Windows: not in place)");
        File.Exists(On($"{Layout}/projects/p-moved/s12.jsonl.wsl-care-q-r1")).Should().BeTrue("the judged file was not removed through a refused act either");
    }

    /// <summary>Finding 3, the copy's side: after the same swap the source opener must never hand out the bytes the link leads to.</summary>
    [Fact]
    public void A_folder_swapped_for_a_link_after_its_check_never_opens_the_file_the_link_leads_to()
    {
        var session = Write($"{Layout}/projects/q/s13.jsonl", "the agent's");
        Write("/srv/elsewhere-q/s13.jsonl", "not the agent's");
        Assert.SkipUnless(CanLink(), "this account may not create a directory link here");
        var swapped = SwapAtTheCheck(On($"{Layout}/projects/q"), On("/srv/elsewhere-q"));

        var opened = Files.OpenSource(On(Layout), session);

        swapped().Should().BeTrue("own review round M6: the swap happened between the check and the open");
        opened.Should().BeOfType<SourceOpen.Refused>("the bytes behind the link are never copied as the session's").Which.Why.Should().Contain("link");
    }

    /// <summary>Finding 3, widened to the rename: after the same swap the quarantine rename must never rename the file behind the link.</summary>
    [Fact]
    public void A_folder_swapped_for_a_link_after_its_check_never_renames_the_file_the_link_leads_to()
    {
        var session = Write($"{Layout}/projects/r/s14.jsonl", "fourteen");
        var elsewhere = Write("/srv/elsewhere-r/s14.jsonl", "not the agent's");
        Assert.SkipUnless(CanLink(), "this account may not create a directory link here");
        var swapped = SwapAtTheCheck(On($"{Layout}/projects/r"), On("/srv/elsewhere-r"));

        var renamed = Files.QuarantineRename(On(Layout), session, "s14.jsonl.wsl-care-q-r1", Quarantine);

        swapped().Should().BeTrue("own review round M6: the swap happened between the check and the rename");
        renamed.Should().BeOfType<NoReplaceRename.Refused>();

        File.Exists(elsewhere).Should().BeTrue($"the file behind the link keeps its name (the rename answered {renamed})");
        File.Exists(On("/srv/elsewhere-r/s14.jsonl.wsl-care-q-r1")).Should().BeFalse();
    }

    /// <summary>Finding 3, widened to the empty-folder removal: after the same swap it must never remove the folder behind the link.</summary>
    [Fact]
    public void A_folder_swapped_for_a_link_after_its_check_never_removes_the_empty_folder_the_link_leads_to()
    {
        Directory.CreateDirectory(On($"{Layout}/projects/f/s15/subagents"));
        Directory.CreateDirectory(On("/srv/elsewhere-f/subagents"));
        Assert.SkipUnless(CanLink(), "this account may not create a directory link here");
        var swapped = SwapAtTheCheck(On($"{Layout}/projects/f/s15"), On("/srv/elsewhere-f"));

        var removed = Files.RemoveEmptyFolder(On(Layout), On($"{Layout}/projects/f/s15/subagents"), Removal);

        swapped().Should().BeTrue("own review round M6: the swap happened between the check and the removal");
        removed.Should().NotBeOfType<VerifiedRemoval.Removed>();

        Directory.Exists(On("/srv/elsewhere-f/subagents")).Should().BeTrue($"the folder behind the link stays (the removal answered {removed})");
    }

    /// <summary>Findings 3 and 4: the destination folder, held open by the archive, is swapped for a link to another folder — a create
    /// in it must never land behind the link (Windows: the held handle refuses the rename; Linux: the create goes through the descriptor).</summary>
    [Fact]
    public void A_destination_folder_swapped_for_a_link_while_held_never_receives_a_file_through_the_link()
    {
        using var folder = Folder("codex", "2026", "11");
        var elsewhere = On("/srv/elsewhere-dest");
        Directory.CreateDirectory(elsewhere);
        Assert.SkipUnless(CanLink(), "this account may not create a directory link here");
        var swapped = TrySwap(folder.Path, elsewhere);
        if (OperatingSystem.IsWindows())
        {
            swapped.Should().BeFalse("the held folder handle refuses its rename, so the policy's verdict on the path stays true until the create");
        }

        if (Files.CreateExclusive(folder, "r.jsonl", BaseScope) is ExclusiveFile.Created created)
        {
            created.Stream.Dispose();
        }

        Directory.EnumerateFileSystemEntries(elsewhere).Should().BeEmpty($"nothing is created behind a link (the swap {(swapped ? "happened" : "was refused")})");
    }

    /// <summary>Finding 5: a Windows session file is copied only when THIS account owns it, as on Linux; an owner that could not be
    /// read refuses too.</summary>
    [Fact]
    public void A_windows_session_file_another_account_owns_is_never_copied()
    {
        var mine = new ArchiveSourceRules.WindowsSource(true, 0x20, 1, MyAccount);

        ArchiveSourceRules.WindowsProblem("s.jsonl", mine, MyAccount).Should().BeEmpty();
        ArchiveSourceRules.WindowsProblem("s.jsonl", mine with { Owner = "S-1-5-32-544" }, MyAccount).Should().Contain("not owned by this account");
        ArchiveSourceRules.WindowsProblem("s.jsonl", mine with { Owner = string.Empty }, string.Empty).Should().Contain("not owned by this account");
        ArchiveSourceRules.WindowsProblem("s.jsonl", mine with { Links = 2 }, MyAccount).Should().Contain("2 links");
        ArchiveSourceRules.WindowsProblem("s.jsonl", mine with { Attributes = 0x400 }, MyAccount).Should().Contain("not a regular file");
        ArchiveSourceRules.WindowsProblem("s.jsonl", mine with { Ok = false }, MyAccount).Should().Contain("could not be read");
    }

    /// <summary>Finding 0's extraction keeps every Linux row: regular, one link, this account's.</summary>
    [Fact]
    public void A_linux_session_file_is_copied_only_when_regular_of_one_link_and_this_accounts()
    {
        var mine = BeneathWrites.LinuxStatus.None with { Known = true, Type = BeneathWrites.LinuxStatus.Regular, Links = 1, Owner = 1000 };

        ArchiveSourceRules.LinuxProblem("s.jsonl", mine, 1000).Should().BeEmpty();
        ArchiveSourceRules.LinuxProblem("s.jsonl", mine with { Owner = 0 }, 1000).Should().Contain("owned by uid 0");
        ArchiveSourceRules.LinuxProblem("s.jsonl", mine with { Links = 3 }, 1000).Should().Contain("3 links");
        ArchiveSourceRules.LinuxProblem("s.jsonl", mine with { Type = 0x1000 }, 1000).Should().Contain("not a regular file");
        ArchiveSourceRules.LinuxProblem("s.jsonl", mine with { Known = false }, 1000).Should().Contain("could not be read");
    }

    /// <summary>Finding 2: <see cref="BeneathFolder"/> is implementation-neutral (a fake seam can make one), and THIS seam refuses a
    /// folder it did not open — it never trusts a path it did not hold.</summary>
    [Fact]
    public void A_folder_handle_this_seam_did_not_open_is_refused_never_trusted()
    {
        var path = On($"{Base}/foreign");
        Directory.CreateDirectory(path);
        using var foreign = new ForeignFolder(path);

        Files.CreateExclusive(foreign, "x.jsonl", BaseScope).Should().BeOfType<ExclusiveFile.Refused>().Which.Why.Should().Contain("did not open");
        Files.ReadBack(foreign, "x.jsonl").Should().BeOfType<FileHash.Unreadable>();
        Files.RemoveOwnCopy(foreign, "x.jsonl", new FileIdentity(1, 2), BaseScope).Should().BeOfType<VerifiedRemoval.Refused>();
        Files.FlushFolder(foreign).Should().BeOfType<FolderFlush.Failed>();
        Directory.EnumerateFileSystemEntries(path).Should().BeEmpty();
    }

    private sealed class ForeignFolder(string path) : BeneathFolder(path)
    {
        protected override void Dispose(bool disposing)
        {
        }
    }

    /// <summary>Whether this account may make a directory link in the sandbox (Windows: a symbolic link or a junction).</summary>
    private bool CanLink()
    {
        var target = On("/srv/link-probe-target");
        Directory.CreateDirectory(target);
        var probe = On("/srv/link-probe");
        var made = DirectoryLinks.TryCreate(probe, target);
        if (made)
        {
            Directory.Delete(probe);
        }

        return made;
    }

    /// <summary>At the seam's <see cref="ArchiveFileStep.PathChecked"/> step (once): <paramref name="folder"/> renamed away and a link
    /// to <paramref name="target"/> put at its name. The answer says whether it fired — own review round M6: a test whose premise
    /// never happened must not pass.</summary>
    private Func<bool> SwapAtTheCheck(string folder, string target)
    {
        var done = false;
        _fault = (step, _) =>
        {
            if (step == ArchiveFileStep.PathChecked && !done)
            {
                done = true;
                TrySwap(folder, target).Should().BeTrue("the swap is the test's premise");
            }
        };
        return () => done;
    }

    private static bool TrySwap(string folder, string target)
    {
        try
        {
            Directory.Move(folder, folder + "-moved");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }

        return DirectoryLinks.TryCreate(folder, target);
    }
}
