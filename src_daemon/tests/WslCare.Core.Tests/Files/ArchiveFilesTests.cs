using System.Security.Cryptography;
using System.Text;

using FluentAssertions;

using WslCare.Core.Files;
using WslCare.Core.Files.Deletion;
using WslCare.TestSupport;

namespace WslCare.Core.Tests.Files;

/// <summary>
/// Plan §15r E9.S2a — the archive's seam over the real disk, on the OS that runs the test (Linux: descriptors, <c>openat</c>,
/// <c>renameat2</c>; Windows: handles checked for where they really are, <c>CREATE_NEW</c>, a rename and a POSIX delete through
/// one handle): a source opened
/// through no link, regular, of one link; a destination tree that follows no link; creates that never replace; renames that
/// never replace an agent's file; a removal that hashes before it removes, and leaves the file when it stops mid-hash.
/// </summary>
public sealed partial class ArchiveFilesTests : IDisposable
{
    private const string Layout = "/home/me/.claude";
    private const string Base = "/mnt/v/ai-archive";

    private readonly LinuxSandbox _sandbox = new("archive-files");
    private readonly List<(ArchiveFileStep Step, string Path)> _steps = [];
    private Action<ArchiveFileStep, string> _fault = static (_, _) => { };

    public ArchiveFilesTests()
    {
        Files = new PhysicalFileSystem(_sandbox.Paths, PhysicalFileSystem.ReadLinkTarget, static (_, _) => { }, (step, path) =>
        {
            _steps.Add((step, path));
            _fault(step, path);
        });
        Directory.CreateDirectory(On(Base));
    }

    private PhysicalFileSystem Files { get; }

    public void Dispose() => _sandbox.Dispose();

    private string On(string distro) => Path.GetFullPath(_sandbox.Paths.DistroPath(distro));

    private string Write(string distro, string content) => Path.GetFullPath(_sandbox.Write(distro, content));

    private static string Sha(string content) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(content)));

    private DeletionScope BaseScope => new(On(Base), "A13");

    private DeletionScope Quarantine => new(On(Layout), "A13", DeletionPermit.ArchiveQuarantine);

    private DeletionScope Removal => new(On(Layout), "A13", DeletionPermit.ArchiveRemoval);

    private BeneathFolder Folder(params string[] levels) =>
        Files.OpenFolderBeneath(On(Base), levels, BaseScope).Should().BeOfType<FolderBeneath.Ready>().Subject.Folder;

    [Fact]
    public void Copying_into_the_archive_never_replaces_a_file()
    {
        using var folder = Folder("claude-code", "2026", "09", "wsl-host-ubuntu");
        Write($"{Base}/claude-code/2026/09/wsl-host-ubuntu/old.jsonl", "already archived");

        using (var created = Files.CreateExclusive(folder, "new.jsonl", BaseScope).Should().BeOfType<ExclusiveFile.Created>().Subject.Stream)
        {
            created.Write(Encoding.UTF8.GetBytes("first"));
        }

        Files.CreateExclusive(folder, "new.jsonl", BaseScope).Should().BeOfType<ExclusiveFile.Exists>();
        Files.CreateExclusive(folder, "old.jsonl", BaseScope).Should().BeOfType<ExclusiveFile.Exists>();
        File.ReadAllText(Path.Combine(folder.Path, "new.jsonl")).Should().Be("first");
        File.ReadAllText(Path.Combine(folder.Path, "old.jsonl")).Should().Be("already archived");
        Files.ReadBack(folder, "new.jsonl").Should().Be(new FileHash.Hashed(Sha("first"), 5));
    }

    [Fact]
    public void A_link_on_any_destination_component_is_never_followed()
    {
        var elsewhere = On($"{Base}/elsewhere");
        Directory.CreateDirectory(elsewhere);
        Assert.SkipUnless(DirectoryLinks.TryCreate(On($"{Base}/claude-code"), elsewhere), "this account may not create a directory link here");

        var opened = Files.OpenFolderBeneath(On(Base), ["claude-code", "2026"], BaseScope);

        opened.Should().BeOfType<FolderBeneath.Refused>().Which.Why.Should().Contain("claude-code");
        Directory.EnumerateFileSystemEntries(elsewhere).Should().BeEmpty("nothing was created through the link");
    }

    /// <summary>Own review round m1: on Windows too — a junction at the final name needs no privilege, and the create is by path.</summary>
    [Fact]
    public void A_link_at_the_final_name_is_never_written_through()
    {
        using var folder = Folder("codex");
        var target = On($"{Base}/codex-target");
        Directory.CreateDirectory(target);
        var link = Path.Combine(folder.Path, "rollout.jsonl");
        Assert.SkipUnless(OperatingSystem.IsLinux() ? MakeFileLink(link, Path.Combine(target, "target.txt")) : DirectoryLinks.TryCreate(link, target), "this account may not create a link here");

        Files.CreateExclusive(folder, "rollout.jsonl", BaseScope).Should().BeOfType<ExclusiveFile.Exists>();
        Directory.EnumerateFileSystemEntries(target).Should().BeEmpty("nothing was created through the link");
    }

    private static bool MakeFileLink(string link, string target)
    {
        File.CreateSymbolicLink(link, target);
        return true;
    }

    [Fact]
    public async Task A_link_fifo_or_multi_linked_file_in_a_session_is_never_copied()
    {
        var plain = Write($"{Layout}/projects/p/plain.jsonl", "hello");
        var twice = Write($"{Layout}/projects/p/twice.jsonl", "x");
        var other = On($"{Layout}/projects/p/twice-too.jsonl");
        var linked = OperatingSystem.IsWindows()
            ? await ChildProcess.RunAsync("cmd.exe", ["/c", "mklink", "/H", other, twice], new Dictionary<string, string?>())
            : await ChildProcess.RunAsync("ln", [twice, other], new Dictionary<string, string?>());
        linked.Exit.Should().Be(0, linked.Stderr);

        using (var opened = Files.OpenSource(On(Layout), plain).Should().BeOfType<SourceOpen.Opened>().Subject.Stream)
        {
            new StreamReader(opened).ReadToEnd().Should().Be("hello");
        }

        Files.OpenSource(On(Layout), twice).Should().BeOfType<SourceOpen.Refused>().Which.Why.Should().Contain("link");
        Files.OpenSource(On(Layout), On($"{Layout}/projects/p/gone.jsonl")).Should().BeOfType<SourceOpen.Gone>();
    }

    [Fact]
    public async Task A_fifo_or_a_link_in_a_session_is_never_opened_for_copying()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "FIFOs and file links are the distro's: the Linux legs");
        var fifo = On($"{Layout}/projects/p/pipe.jsonl");
        Directory.CreateDirectory(Path.GetDirectoryName(fifo)!);
        (await ChildProcess.RunAsync("mkfifo", [fifo], new Dictionary<string, string?>())).Exit.Should().Be(0);
        var real = Write("/srv/secret.txt", "not the agent's");
        var link = On($"{Layout}/projects/p/link.jsonl");
        File.CreateSymbolicLink(link, real);

        Files.OpenSource(On(Layout), fifo).Should().BeOfType<SourceOpen.Refused>().Which.Why.Should().Contain("not a regular file");
        Files.OpenSource(On(Layout), link).Should().BeOfType<SourceOpen.Refused>();
    }

    [Fact]
    public void A_folder_link_on_the_way_to_a_session_file_is_never_followed()
    {
        var real = On("/srv/other-projects");
        Directory.CreateDirectory(real);
        Write("/srv/other-projects/s.jsonl", "elsewhere");
        Directory.CreateDirectory(On($"{Layout}/projects"));
        Assert.SkipUnless(DirectoryLinks.TryCreate(On($"{Layout}/projects/linked"), real), "this account may not create a directory link here");

        Files.OpenSource(On(Layout), On($"{Layout}/projects/linked/s.jsonl")).Should().BeOfType<SourceOpen.Refused>().Which.Why.Should().Contain("linked");
    }

    /// <summary>Review B1: the agent re-created the session's name while it sat under its quarantine name — renaming back must keep
    /// the agent's file, never replace it.</summary>
    [Fact]
    public void An_agent_file_created_at_the_original_name_during_removal_is_never_replaced()
    {
        var session = Write($"{Layout}/projects/p/s1.jsonl", "archived bytes");

        Files.QuarantineRename(On(Layout), session, "s1.jsonl.wsl-care-q-r1", Quarantine).Should().BeOfType<NoReplaceRename.Renamed>();
        Write($"{Layout}/projects/p/s1.jsonl", "the agent wrote this meanwhile");
        var back = Files.RenameBack(On(Layout), On($"{Layout}/projects/p/s1.jsonl.wsl-care-q-r1"), "s1.jsonl", Quarantine);

        back.Should().BeOfType<NoReplaceRename.NameTaken>();
        File.ReadAllText(session).Should().Be("the agent wrote this meanwhile");
        File.ReadAllText(On($"{Layout}/projects/p/s1.jsonl.wsl-care-q-r1")).Should().Be("archived bytes");
    }

    [Fact]
    public void A_quarantine_rename_never_replaces_an_existing_quarantined_file_and_renames_back_when_free()
    {
        var session = Write($"{Layout}/projects/p/s2.jsonl", "two");
        Write($"{Layout}/projects/p/s2.jsonl.wsl-care-q-r1", "an earlier run's");

        Files.QuarantineRename(On(Layout), session, "s2.jsonl.wsl-care-q-r1", Quarantine).Should().BeOfType<NoReplaceRename.NameTaken>();
        File.ReadAllText(On($"{Layout}/projects/p/s2.jsonl.wsl-care-q-r1")).Should().Be("an earlier run's");
        Files.QuarantineRename(On(Layout), session, "s2.jsonl.wsl-care-q-r2", Quarantine).Should().BeOfType<NoReplaceRename.Renamed>();
        Files.RenameBack(On(Layout), On($"{Layout}/projects/p/s2.jsonl.wsl-care-q-r2"), "s2.jsonl", Quarantine).Should().BeOfType<NoReplaceRename.Renamed>();
        File.ReadAllText(session).Should().Be("two");
    }

    [Fact]
    public void A_quarantined_file_is_removed_only_when_it_hashes_equal_to_its_archived_copy()
    {
        var quarantined = Write($"{Layout}/projects/p/s3.jsonl.wsl-care-q-r1", "the session");
        var copy = Write($"{Base}/claude-code/s3.jsonl", "the session");

        Files.RemoveVerified(On(Layout), quarantined, Sha("something else"), copy, Removal).Should().BeOfType<VerifiedRemoval.Kept>();
        File.Exists(quarantined).Should().BeTrue("a file whose bytes differ from the copy is never removed");
        Files.RemoveVerified(On(Layout), quarantined, Sha("the session"), copy, Removal).Should().BeOfType<VerifiedRemoval.Removed>();
        File.Exists(quarantined).Should().BeFalse();
        Files.RemoveVerified(On(Layout), quarantined, Sha("the session"), copy, Removal).Should().BeOfType<VerifiedRemoval.Gone>();
    }

    /// <summary>Review B2: a removal stopped while it hashes — an exception here, a kill in a real run — leaves the file: the delete
    /// disposition is set only after the hash is equal (never <c>DeleteOnClose</c>, which deletes on ANY close).</summary>
    [Fact]
    public void A_kill_mid_hash_on_windows_leaves_the_source_present()
    {
        var quarantined = Write($"{Layout}/projects/p/s4.jsonl.wsl-care-q-r1", new string('z', 300_000));
        var copy = Write($"{Base}/claude-code/s4.jsonl", new string('z', 300_000));
        _fault = (step, _) =>
        {
            if (step == ArchiveFileStep.RemovalHashChunk)
            {
                throw new OperationCanceledException("killed mid-hash");
            }
        };

        FluentActions.Invoking(() => Files.RemoveVerified(On(Layout), quarantined, Sha(new string('z', 300_000)), copy, Removal)).Should().Throw<OperationCanceledException>();

        File.Exists(quarantined).Should().BeTrue("the removal stopped before its disposition was set");
        File.ReadAllText(quarantined).Should().HaveLength(300_000);
    }

    [Fact]
    public void A_removal_or_rename_the_policy_refuses_touches_nothing()
    {
        var session = Write($"{Layout}/projects/p/s5.jsonl", "five");
        var memory = Write($"{Layout}/projects/p/memory/notes.md", "memory");
        var copy = Write($"{Base}/claude-code/s5.jsonl", "five");

        Files.RemoveVerified(On(Layout), session, Sha("five"), copy, Removal).Should().BeOfType<VerifiedRemoval.Refused>().Which.Why.Should().Contain("quarantine");
        Files.QuarantineRename(On(Layout), memory, "notes.md.wsl-care-q-r1", Quarantine).Should().BeOfType<NoReplaceRename.Refused>().Which.Why.Should().Contain("memory");
        Files.RemoveVerified(On(Layout), session, Sha("five"), copy, new DeletionScope(On(Layout), "A13")).Should().BeOfType<VerifiedRemoval.Refused>();
        File.Exists(session).Should().BeTrue();
        File.Exists(memory).Should().BeTrue();
    }

    [Fact]
    public void Only_an_empty_folder_is_removed_never_the_agent_folder_itself()
    {
        Write($"{Layout}/projects/p/s6/subagents/a.jsonl", "a");
        Directory.CreateDirectory(On($"{Layout}/projects/p/s7/subagents"));

        Files.RemoveEmptyFolder(On(Layout), On($"{Layout}/projects/p/s6/subagents"), Removal).Should().BeOfType<VerifiedRemoval.Kept>();
        Files.RemoveEmptyFolder(On(Layout), On($"{Layout}/projects/p/s7/subagents"), Removal).Should().BeOfType<VerifiedRemoval.Removed>();
        Files.RemoveEmptyFolder(On(Layout), On(Layout), new DeletionScope(On("/home/me"), "A13", DeletionPermit.ArchiveRemoval)).Should().BeOfType<VerifiedRemoval.Refused>();
        Directory.Exists(On($"{Layout}/projects/p/s6/subagents")).Should().BeTrue();
        Directory.Exists(On($"{Layout}/projects/p/s7/subagents")).Should().BeFalse();
        Directory.Exists(On(Layout)).Should().BeTrue();
    }

    /// <summary>Risk consult 9/9.2: a descriptor opened BEFORE the removal — a writer the open-file scan may not have seen (a child
    /// that inherited it by fork after the scan's snapshot) — makes the removal keep the file, and an append through it lands
    /// in a file that still exists. Linux: the write lease is refused while any other open exists; Windows: the one handle's
    /// sharing mode refuses an open writer.</summary>
    [Fact]
    public void A_quarantined_file_another_open_descriptor_holds_is_never_removed()
    {
        var quarantined = Write($"{Layout}/projects/p/s9.jsonl.wsl-care-q-r1", "nine");
        var copy = Write($"{Base}/claude-code/s9.jsonl", "nine");
        using var writer = new FileStream(quarantined, FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
        _fault = (step, _) =>
        {
            if (step == ArchiveFileStep.RemovalHashed)
            {
                writer.Seek(0, SeekOrigin.End);
                writer.Write(Encoding.UTF8.GetBytes(" and more"));
                writer.Flush();
            }
        };

        Files.RemoveVerified(On(Layout), quarantined, Sha("nine"), copy, Removal).Should().BeOfType<VerifiedRemoval.Kept>();

        File.Exists(quarantined).Should().BeTrue("a writer holds it open");
    }

    /// <summary>The same with the writer in ANOTHER process (a shell holding the file open on descriptor 3) — Linux.</summary>
    [Fact]
    public async Task A_quarantined_file_another_process_holds_open_is_never_removed()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "a shell holding a descriptor: the Linux legs");
        var quarantined = Write($"{Layout}/projects/p/s10.jsonl.wsl-care-q-r1", "ten");
        var copy = Write($"{Base}/claude-code/s10.jsonl", "ten");
        using var holder = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("sh", ["-c", "exec 3>>\"$0\"; sleep 30", quarantined]) { UseShellExecute = false })!;
        await Task.Delay(TimeSpan.FromMilliseconds(500), TestContext.Current.CancellationToken);

        try
        {
            Files.RemoveVerified(On(Layout), quarantined, Sha("ten"), copy, Removal).Should().BeOfType<VerifiedRemoval.Kept>().Which.Why.Should().Contain("open");
            File.Exists(quarantined).Should().BeTrue();
        }
        finally
        {
            holder.Kill();
        }
    }

    /// <summary>Risk consult 9/9.2, its two named holes: only a CHILD keeps the writer (the shell that opened it is gone), and only
    /// a writable shared MAPPING keeps the file (every descriptor closed) — the write lease is refused for both.</summary>
    [Theory]
    [InlineData("child", "sh", "-c", "sleep 30 3>>\"$0\" &")]
    [InlineData("mapping", "python3", "-c", "import mmap,sys,time\nf=open(sys.argv[1],'r+b'); m=mmap.mmap(f.fileno(),0); f.close(); print('ready', flush=True); time.sleep(30)")]
    public async Task A_quarantined_file_a_forked_child_or_a_shared_mapping_holds_is_never_removed(string holder, string program, string flag, string script)
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "fork-inherited descriptors and shared mappings: the Linux legs");
        var quarantined = Write($"{Layout}/projects/p/s11-{holder}.jsonl.wsl-care-q-r1", "eleven");
        var copy = Write($"{Base}/claude-code/s11-{holder}.jsonl", "eleven");
        var start = new System.Diagnostics.ProcessStartInfo(program, [flag, script, quarantined]) { UseShellExecute = false, RedirectStandardOutput = true };
        System.Diagnostics.Process? running;
        try
        {
            running = System.Diagnostics.Process.Start(start);
        }
        catch (System.ComponentModel.Win32Exception)
        {
            running = null;
        }

        Assert.SkipWhen(running is null, $"{program} is not on this machine");
        using var process = running!;
        await Task.Delay(TimeSpan.FromMilliseconds(800), TestContext.Current.CancellationToken);

        try
        {
            Files.RemoveVerified(On(Layout), quarantined, Sha("eleven"), copy, Removal).Should().BeOfType<VerifiedRemoval.Kept>().Which.Why.Should().Contain("open");
            File.Exists(quarantined).Should().BeTrue();
        }
        finally
        {
            process.Kill(entireProcessTree: true);
        }
    }

    /// <summary>Risk consult 9/9.2: a new folder's ENTRY is durable only when the folder holding it is flushed — every newly created
    /// level's parent is synced, and the destination folder's own flush reports its result (Windows: <c>FlushFileBuffers</c> on the
    /// held folder handle, gate round finding 4).</summary>
    [Fact]
    public void Every_new_destination_level_is_synced_into_its_parent_and_a_flush_says_whether_it_held()
    {
        using var folder = Folder("gemini-cli", "2026", "10");

        Files.FlushFolder(folder).Should().BeOfType<FolderFlush.Done>();
        _steps.Where(s => s.Step == ArchiveFileStep.FolderLevelSynced).Should().HaveCount(3, "each of the three new levels' entry was flushed in its parent (Windows too: gate round finding 4)");
        _steps.Should().Contain(s => s.Step == ArchiveFileStep.FolderFlushed);
    }

    /// <summary>The fault seam is asked between the primitive steps, so a later story can stop the protocol at each of them.</summary>
    [Fact]
    public void The_fault_seam_is_asked_between_every_primitive_step_of_a_removal()
    {
        var quarantined = Write($"{Layout}/projects/p/s8.jsonl.wsl-care-q-r1", "eight");
        var copy = Write($"{Base}/claude-code/s8.jsonl", "eight");

        Files.RemoveVerified(On(Layout), quarantined, Sha("eight"), copy, Removal).Should().BeOfType<VerifiedRemoval.Removed>();

        _steps.Select(s => s.Step).Should().ContainInOrder(ArchiveFileStep.RemovalOpened, ArchiveFileStep.RemovalHashChunk, ArchiveFileStep.RemovalHashed, ArchiveFileStep.Removed);
    }
}
