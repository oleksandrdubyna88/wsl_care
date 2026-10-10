using FluentAssertions;

using WslCare.Core.Archive;
using WslCare.Core.Files;

namespace WslCare.Core.Tests.Files;

/// <summary>
/// Plan §15r, *E9 live gate step 8, first run (2026-10-10)*: on the owner's NAS, `archive run` refused both spellings of the base. A
/// mapped drive's files are answered by <c>GetFinalPathNameByHandle</c> under their UNC root, which the in-place check read as a link
/// (N1); <c>FlushFileBuffers</c> on a folder handle answers <c>ERROR_INVALID_FUNCTION</c> over SMB (N2); and the lease's refusal had
/// dropped the folder's own reason (N3).
/// </summary>
public sealed class NetworkBaseTests
{
    private const string Share = @"\\nas\work";

    private static string RootOf(string drive) => drive.Equals("V:", StringComparison.OrdinalIgnoreCase) ? Share : string.Empty;

    [Fact]
    public void A_mapped_drives_folder_answered_under_its_network_root_is_in_place()
    {
        NetworkPaths.InPlace(@"\\nas\work\archive\base", @"V:\archive\base", RootOf).Should().BeTrue();
        NetworkPaths.InPlace(@"V:\archive\base", @"V:\archive\base", RootOf).Should().BeTrue("the plain answer still holds");
    }

    [Fact]
    public void A_real_link_or_another_share_is_still_not_in_place()
    {
        NetworkPaths.InPlace(@"\\nas\work\elsewhere", @"V:\archive\base", RootOf).Should().BeFalse("a link inside the share leads elsewhere");
        NetworkPaths.InPlace(@"\\nas\other\archive\base", @"V:\archive\base", RootOf).Should().BeFalse();
        NetworkPaths.InPlace(@"\\nas\work\archive\base", @"C:\archive\base", RootOf).Should().BeFalse("C: is not a network drive");
        NetworkPaths.InPlace(string.Empty, @"V:\archive\base", RootOf).Should().BeFalse("an answer the system could not give");
    }

    [Theory]
    [InlineData(0, false, true)]
    [InlineData(0, true, true)]
    [InlineData(1, true, true)]
    [InlineData(1, false, false)]
    [InlineData(5, true, false)]
    public void Over_smb_a_folder_flush_the_redirector_does_not_offer_counts_as_done_and_nothing_else_does(int error, bool remote, bool done)
    {
        (NetworkPaths.FolderFlushed(error, remote) == 0).Should().Be(done);
    }

    [Fact]
    public void A_path_is_remote_when_it_is_a_share_or_on_a_network_drive()
    {
        NetworkPaths.IsRemote(@"\\nas\work\base", RootOf).Should().BeTrue();
        NetworkPaths.IsRemote(@"V:\base", RootOf).Should().BeTrue();
        NetworkPaths.IsRemote(@"C:\base", RootOf).Should().BeFalse();
        NetworkPaths.IsRemote(@"\\?\C:\base", RootOf).Should().BeFalse("an extended-length local path is not a share");
    }

    // ---- the own review round ----

    private static DriveMapping MappingOf(string drive) => drive.ToUpperInvariant() switch
    {
        "V:" => new DriveMapping(true, Share),
        "W:" => new DriveMapping(true, @"\\wsl.localhost\Ubuntu\home\me"),
        "L:" => new DriveMapping(true, @"\\localhost\work"),
        "A:" => new DriveMapping(true, @"\\nas\D$"),
        "D:" => new DriveMapping(true, string.Empty),
        _ => DriveMapping.Local,
    };

    /// <summary>Own review 1 (Medium): before the fix every mapped drive refused at the act, so the share-alias rule only ever had to see a
    /// UNC spelling. A drive mapped to the distribution's own files or to an administrative share is now refused by that rule, through
    /// its mapping; a remote drive whose share cannot be read is refused too.</summary>
    [Fact]
    public void A_mapped_drive_is_judged_by_the_share_it_maps_to()
    {
        BaseFolderRules.MappedShareProblem(@"W:\archive", MappingOf).Should().Contain("distribution's own files");
        BaseFolderRules.MappedShareProblem(@"L:\archive", MappingOf).Should().Contain("names this machine");
        BaseFolderRules.MappedShareProblem(@"A:\archive", MappingOf).Should().Contain("administrative share");
        BaseFolderRules.MappedShareProblem(@"D:\archive", MappingOf).Should().Contain("cannot be read");
        BaseFolderRules.MappedShareProblem(@"V:\archive", MappingOf).Should().BeEmpty("the NAS's own share is a share of its own");
        BaseFolderRules.MappedShareProblem(@"C:\archive", MappingOf).Should().BeEmpty("a local drive");
    }

    /// <summary>Own review 4 and 6: a root with a trailing separator, a mapping to a subfolder, a lower-case drive letter.</summary>
    [Fact]
    public void A_trailing_separator_a_subfolder_mapping_and_a_lower_case_drive_still_match()
    {
        NetworkPaths.InPlace(@"\\nas\work\archive\base", @"V:\archive\base", static _ => @"\\nas\work\").Should().BeTrue();
        NetworkPaths.InPlace(@"\\nas\work\sub\archive", @"V:\archive", static _ => @"\\nas\work\sub").Should().BeTrue();
        NetworkPaths.InPlace(@"\\nas\work\archive", @"v:\archive", RootOf).Should().BeTrue();
    }

    [Fact]
    public void An_extended_length_unc_spelling_is_not_remote_and_so_refuses()
    {
        NetworkPaths.IsRemote(@"\\?\UNC\nas\work\base", RootOf).Should().BeFalse("such a spelling is refused, never swapped");
    }
}
