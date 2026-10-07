using System.IO.MemoryMappedFiles;
using System.Text;

using FluentAssertions;

using WslCare.Core.Files;
using WslCare.TestSupport;

namespace WslCare.Core.Tests.Files;

/// <summary>
/// Plan §15r <i>E9.S2a own review round</i> — the two own reviews of the seam (correctness, security): a closed answer where
/// Windows threw, the archive's own copy removed only by its identity, the archived copy hashed by the seam before a source is
/// removed, a closed folder refused, the real path followed from the file system's root, only a regular file renamed on Linux,
/// Windows names NTFS holds as themselves, and the Windows counterparts of the Linux-only tests.
/// </summary>
public sealed partial class ArchiveFilesTests
{
    /// <summary>Correctness M1 + M2: the archive's own copy is removed — and while the create's stream is still open it is kept
    /// (Windows) or removed (Linux), never an exception.</summary>
    [Fact]
    public void The_archives_own_copy_is_removed_and_an_open_one_never_throws()
    {
        using var folder = Folder("codex", "own");
        var created = Files.CreateExclusive(folder, "c.jsonl", BaseScope).Should().BeOfType<ExclusiveFile.Created>().Subject;

        var whileOpen = Files.RemoveOwnCopy(folder, "c.jsonl", created.Identity, BaseScope);

        if (OperatingSystem.IsWindows())
        {
            whileOpen.Should().BeOfType<VerifiedRemoval.Kept>().Which.Why.Should().Contain("open");
            created.Stream.Dispose();
            Files.RemoveOwnCopy(folder, "c.jsonl", created.Identity, BaseScope).Should().BeOfType<VerifiedRemoval.Removed>();
        }
        else
        {
            whileOpen.Should().BeOfType<VerifiedRemoval.Removed>("an open file may be unlinked on Linux");
            created.Stream.Dispose();
        }

        File.Exists(Path.Combine(folder.Path, "c.jsonl")).Should().BeFalse();
        Files.RemoveOwnCopy(folder, "c.jsonl", created.Identity, BaseScope).Should().BeOfType<VerifiedRemoval.Gone>();
    }

    /// <summary>Correctness M2: after the copy this run created was replaced by ANOTHER file of the same name (an archived copy a
    /// later step reused), its removal keeps that file.</summary>
    [Fact]
    public void The_archives_own_copy_is_removed_only_while_its_name_names_that_file()
    {
        using var folder = Folder("codex", "own2");
        var created = Files.CreateExclusive(folder, "c.jsonl", BaseScope).Should().BeOfType<ExclusiveFile.Created>().Subject;
        created.Stream.Dispose();
        var path = Path.Combine(folder.Path, "c.jsonl");
        File.Delete(path);
        File.WriteAllText(path, "an archived session, the only copy");

        Files.RemoveOwnCopy(folder, "c.jsonl", created.Identity, BaseScope).Should().BeOfType<VerifiedRemoval.Kept>().Which.Why.Should().Contain("not the copy this run created");

        File.ReadAllText(path).Should().Be("an archived session, the only copy");
    }

    /// <summary>Correctness M1, security m4: a level that cannot be created — a FILE has its name — is refused, never thrown.</summary>
    [Fact]
    public void A_destination_level_that_cannot_be_created_is_refused_never_thrown()
    {
        Write($"{Base}/gemini-cli/2026", "a file where a level goes");

        FluentActions.Invoking(() => Files.OpenFolderBeneath(On(Base), ["gemini-cli", "2026", "10"], BaseScope))
            .Should().NotThrow().Which.Should().BeOfType<FolderBeneath.Refused>().Which.Why.Should().Contain("2026");
    }

    /// <summary>Correctness M1: a level whose create is DENIED (the parent refuses new folders) is refused, never thrown.</summary>
    [Fact]
    public void A_destination_level_whose_create_is_denied_is_refused_never_thrown()
    {
        var parent = On($"{Base}/antigravity");
        Directory.CreateDirectory(parent);
        using var denied = DenyNewFolders(parent);
        Assert.SkipUnless(denied.Applied, "this account cannot take its own right to create folders away here");

        FluentActions.Invoking(() => Files.OpenFolderBeneath(On(Base), ["antigravity", "2026"], BaseScope))
            .Should().NotThrow().Which.Should().BeOfType<FolderBeneath.Refused>().Which.Why.Should().Contain("2026");
    }

    /// <summary>Takes the right to create sub-folders in <paramref name="folder"/> away from this account until disposed.</summary>
    private static DeniedFolder DenyNewFolders(string folder) =>
        OperatingSystem.IsWindows() ? DenyOnWindows(folder)
        : OperatingSystem.IsLinux() ? DenyOnLinux(folder)
        : new DeniedFolder(false, static () => { });

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static DeniedFolder DenyOnWindows(string folder)
    {
        var security = new DirectoryInfo(folder).GetAccessControl();
        var me = System.Security.Principal.WindowsIdentity.GetCurrent().User!;
        var rule = new System.Security.AccessControl.FileSystemAccessRule(me, System.Security.AccessControl.FileSystemRights.CreateDirectories, System.Security.AccessControl.AccessControlType.Deny);
        security.AddAccessRule(rule);
        new DirectoryInfo(folder).SetAccessControl(security);
        return new DeniedFolder(true, () => Allow(folder, rule));
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static void Allow(string folder, System.Security.AccessControl.FileSystemAccessRule rule)
    {
        var back = new DirectoryInfo(folder).GetAccessControl();
        back.RemoveAccessRule(rule);
        new DirectoryInfo(folder).SetAccessControl(back);
    }

    [System.Runtime.Versioning.SupportedOSPlatform("linux")]
    private static DeniedFolder DenyOnLinux(string folder)
    {
        File.SetUnixFileMode(folder, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        return new DeniedFolder(RegularFiles.EffectiveUid() != 0, () => File.SetUnixFileMode(folder, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute));
    }

    private sealed class DeniedFolder(bool applied, Action undo) : IDisposable
    {
        public bool Applied { get; } = applied;

        public void Dispose() => undo();
    }

    /// <summary>Security m4: the agent deletes the file (Claude Code's own sweep) between the check and the act — every verb answers
    /// <c>Gone</c>, never an exception.</summary>
    [Fact]
    public void A_file_the_agent_deletes_after_the_check_is_gone_never_thrown()
    {
        var session = Write($"{Layout}/projects/g/s17.jsonl", "seventeen");
        var quarantined = Write($"{Layout}/projects/g/s18.jsonl.wsl-care-q-r1", "eighteen");
        var copy = Write($"{Base}/claude-code/s18.jsonl", "eighteen");
        Directory.CreateDirectory(On($"{Layout}/projects/g/s19/subagents"));
        var where = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["s17.jsonl"] = On($"{Layout}/projects/g/s17.jsonl"),
            ["s18.jsonl.wsl-care-q-r1"] = quarantined,
            ["subagents"] = On($"{Layout}/projects/g/s19/subagents"),
        };
        _fault = (step, name) =>
        {
            if (step == ArchiveFileStep.PathChecked)
            {
                DeleteAgain(where[name]);
            }
        };

        Files.OpenSource(On(Layout), session).Should().BeOfType<SourceOpen.Gone>();
        Write($"{Layout}/projects/g/s17.jsonl", "again");
        Files.QuarantineRename(On(Layout), session, "s17.jsonl.wsl-care-q-r1", Quarantine).Should().BeOfType<NoReplaceRename.Gone>();
        Files.RemoveVerified(On(Layout), quarantined, Sha("eighteen"), copy, Removal).Should().BeOfType<VerifiedRemoval.Gone>();
        Files.RemoveEmptyFolder(On(Layout), On($"{Layout}/projects/g/s19/subagents"), Removal).Should().BeOfType<VerifiedRemoval.Gone>();
    }

    private static void DeleteAgain(string path)
    {
        if (Directory.Exists(path))
        {
            Directory.Delete(path);
        }
        else
        {
            File.Delete(path);
        }
    }

    /// <summary>Security M2 (= correctness M4): the seam opens and hashes the ARCHIVED COPY before it touches the source — a copy that
    /// is missing or whose bytes changed keeps the source, whatever hash the caller passes.</summary>
    [Fact]
    public void A_quarantined_file_whose_archived_copy_is_missing_or_changed_is_never_removed()
    {
        var quarantined = Write($"{Layout}/projects/c/s20.jsonl.wsl-care-q-r1", "twenty");
        var copy = On($"{Base}/claude-code/s20.jsonl");

        Files.RemoveVerified(On(Layout), quarantined, Sha("twenty"), copy, Removal).Should().BeOfType<VerifiedRemoval.Kept>().Which.Why.Should().Contain("missing");
        Write($"{Base}/claude-code/s20.jsonl", "a share that lost the write");
        Files.RemoveVerified(On(Layout), quarantined, Sha("twenty"), copy, Removal).Should().BeOfType<VerifiedRemoval.Kept>().Which.Why.Should().Contain("differ");
        File.Exists(quarantined).Should().BeTrue();

        File.WriteAllText(copy, "twenty");
        Files.RemoveVerified(On(Layout), quarantined, Sha("twenty"), copy, Removal).Should().BeOfType<VerifiedRemoval.Removed>();
        _steps.Select(s => s.Step).Should().ContainInOrder(ArchiveFileStep.ArchivedCopyHashed, ArchiveFileStep.RemovalOpened, ArchiveFileStep.Removed);
    }

    /// <summary>The source's OWN hash still decides once the archived copy passed (the copy check must not shadow it): an archived
    /// copy equal to the expected hash, a quarantined file whose bytes changed — kept.</summary>
    [Fact]
    public void A_quarantined_file_whose_bytes_differ_from_its_equal_archived_copy_is_never_removed()
    {
        var quarantined = Write($"{Layout}/projects/c/s26.jsonl.wsl-care-q-r1", "the agent appended after the copy");
        var copy = Write($"{Base}/claude-code/s26.jsonl", "the session");

        Files.RemoveVerified(On(Layout), quarantined, Sha("the session"), copy, Removal).Should().BeOfType<VerifiedRemoval.Kept>().Which.Why.Should().Contain("differ");

        File.ReadAllText(quarantined).Should().Be("the agent appended after the copy");
    }

    /// <summary>Security M1: a folder the caller already closed is refused by every verb — on Linux its descriptor number, reused by
    /// the next open, never steers a create or a removal into another folder.</summary>
    [Fact]
    public void A_closed_destination_folder_is_refused_and_its_descriptor_is_never_reused()
    {
        var closed = Folder("codex", "closed");
        var created = Files.CreateExclusive(closed, "x.jsonl", BaseScope).Should().BeOfType<ExclusiveFile.Created>().Subject;
        created.Stream.Dispose();
        closed.Dispose();
        using var other = Folder("codex", "other");
        File.WriteAllText(Path.Combine(other.Path, "x.jsonl"), "another folder's file");

        Files.CreateExclusive(closed, "y.jsonl", BaseScope).Should().BeOfType<ExclusiveFile.Refused>().Which.Why.Should().Contain("closed");
        Files.RemoveOwnCopy(closed, "x.jsonl", created.Identity, BaseScope).Should().BeOfType<VerifiedRemoval.Refused>();
        Files.ReadBack(closed, "x.jsonl").Should().BeOfType<FileHash.Unreadable>();
        Files.FlushFolder(closed).Should().BeOfType<FolderFlush.Failed>();

        File.ReadAllText(Path.Combine(other.Path, "x.jsonl")).Should().Be("another folder's file");
        File.Exists(Path.Combine(other.Path, "y.jsonl")).Should().BeFalse();
        File.Exists(Path.Combine(closed.Path, "x.jsonl")).Should().BeTrue("a refused removal removed nothing");
    }

    /// <summary>Security m1: an ANCESTOR of the layout root (the home holding it) is swapped for a link after the check — the act
    /// follows the judged real path from the file system's root and refuses; the file behind the link stays.</summary>
    [Fact]
    public void A_layout_root_swapped_for_a_link_after_its_check_never_removes_the_file_the_link_leads_to()
    {
        var layout = On("/home/me/.codex");
        var quarantined = Write("/home/me/.codex/sessions/s21.jsonl.wsl-care-q-r1", "twenty-one");
        var copy = Write($"{Base}/codex/s21.jsonl", "twenty-one");
        var elsewhere = Write("/srv/elsewhere-home/.codex/sessions/s21.jsonl.wsl-care-q-r1", "twenty-one");
        Assert.SkipUnless(CanLink(), "this account may not create a directory link here");
        var swapped = SwapAtTheCheck(On("/home/me"), On("/srv/elsewhere-home"));

        var removal = Files.RemoveVerified(layout, quarantined, Sha("twenty-one"), copy, new(layout, "A13", Core.Files.Deletion.DeletionPermit.ArchiveRemoval));

        swapped().Should().BeTrue("the swap happened between the check and the act");
        File.Exists(elsewhere).Should().BeTrue($"the file behind the swapped root was never judged (the removal answered {removal})");
        removal.Should().NotBeOfType<VerifiedRemoval.Removed>();
    }

    /// <summary>Security m2: on Linux, as on Windows, only a regular file is quarantined — never a folder, never a link.</summary>
    [Fact]
    public void A_quarantine_rename_never_renames_a_folder_or_a_link()
    {
        var folder = On($"{Layout}/projects/d/s22.jsonl");
        Directory.CreateDirectory(folder);

        Files.QuarantineRename(On(Layout), folder, "s22.jsonl.wsl-care-q-r1", Quarantine).Should().BeOfType<NoReplaceRename.Refused>();
        Directory.Exists(folder).Should().BeTrue();
        if (OperatingSystem.IsLinux())
        {
            var link = On($"{Layout}/projects/d/s23.jsonl");
            File.CreateSymbolicLink(link, Write("/srv/outside.txt", "outside"));
            Files.QuarantineRename(On(Layout), link, "s23.jsonl.wsl-care-q-r1", Quarantine).Should().BeOfType<NoReplaceRename.Refused>();
            File.Exists(On($"{Layout}/projects/d/s23.jsonl.wsl-care-q-r1")).Should().BeFalse();
        }
    }

    /// <summary>Security m3: on Windows a name with <c>:</c> is an alternate stream of ANOTHER file — a create-only restore must never
    /// add one to an agent's file, and a "quarantined" stream is never removed.</summary>
    [Fact]
    public void On_windows_a_name_naming_an_alternate_stream_or_a_device_is_refused()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "alternate data streams and device names are NTFS's");
        using var folder = Folder("claude-code", "ads");
        File.WriteAllText(Path.Combine(folder.Path, "s.jsonl"), "an existing file");
        var agentFile = Write($"{Layout}/projects/e/s24.jsonl", "the agent's");

        Files.CreateExclusive(folder, "s.jsonl:x", BaseScope).Should().BeOfType<ExclusiveFile.Refused>();
        Files.CreateExclusive(folder, "CON", BaseScope).Should().BeOfType<ExclusiveFile.Refused>();
        Files.CreateExclusive(folder, "trailing. ", BaseScope).Should().BeOfType<ExclusiveFile.Refused>();
        Files.RemoveVerified(On(Layout), agentFile + ":x.wsl-care-q-r1", Sha("x"), Write($"{Base}/claude-code/x", "x"), Removal).Should().BeOfType<VerifiedRemoval.Refused>();
        File.ReadAllText(agentFile).Should().Be("the agent's");
    }

    /// <summary>Correctness m1: the Windows counterpart of the shared-mapping row — a writable mapping whose stream was disposed keeps
    /// the file (the removal's handle shares read only, which the mapping's section refuses).</summary>
    [Fact]
    public void On_windows_a_quarantined_file_a_shared_mapping_holds_is_never_removed()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "the Linux legs run the forked-child and mapping theory with a real python");
        var quarantined = Write($"{Layout}/projects/m/s25.jsonl.wsl-care-q-r1", "twenty-five");
        var copy = Write($"{Base}/claude-code/s25.jsonl", "twenty-five");
        MemoryMappedFile mapping;
        using (var stream = new FileStream(quarantined, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete))
        {
            mapping = MemoryMappedFile.CreateFromFile(stream, null, 0, MemoryMappedFileAccess.ReadWrite, HandleInheritability.None, leaveOpen: true);
        }

        using (mapping)
        using (var view = mapping.CreateViewAccessor())
        {
            Files.RemoveVerified(On(Layout), quarantined, Sha("twenty-five"), copy, Removal).Should().BeOfType<VerifiedRemoval.Kept>();
            File.Exists(quarantined).Should().BeTrue();
            view.ReadByte(0).Should().Be((byte)Encoding.UTF8.GetBytes("t")[0]);
        }
    }
}
