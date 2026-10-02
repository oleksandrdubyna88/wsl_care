using System.Text;

using FluentAssertions;

using WslCare.Core.Files;
using WslCare.Core.Files.Deletion;
using WslCare.TestSupport;

namespace WslCare.Core.Tests.Files;

/// <summary>
/// <see cref="PhysicalFileSystem.WriteFileAtomically"/> writes where it was ALLOWED to write: the
/// temporary file is made inside the resolved, approved parent and judged before it exists, and the
/// destination is resolved again immediately before the rename, so a link swapped in after the
/// decision is refused instead of followed.
/// </summary>
/// <remarks>The swap happens inside the file system's own step callback — the moment a racing
/// process would act — by moving a directory out of the declared root and leaving a link to it in
/// its place (a junction where this account may not create symlinks). The residual window, between
/// the last check and the rename itself, is described on the method and in
/// <c>research/architecture.md</c>; no test can stage it without a handle-relative rename.</remarks>
public sealed class AtomicWriteRevalidationTests
{
    private static readonly byte[] NewContent = Encoding.UTF8.GetBytes("new");

    [Fact]
    public void A_link_swapped_in_after_the_target_was_approved_is_refused_and_nothing_lands_outside_the_root()
    {
        using var host = new SandboxHost("atomic-swap-approved");
        SkipUnlessLinksWork(host);
        var root = host.Root.Dir("cfg");
        var sub = host.Root.Dir("cfg/sub");
        var target = host.Root.File("cfg/sub/config.json", "old");
        var movedOut = Path.Combine(host.Root.Dir("outside"), "sub");
        var files = SwappingAt(host, AtomicWriteStep.TargetApproved, () => ReplaceWithLink(sub, movedOut));

        var verdict = files.WriteFileAtomically(target, NewContent, new DeletionScope(root, "config-set"));

        File.ReadAllText(Path.Combine(movedOut, "config.json")).Should().Be("old", "the write must not follow the swapped link out of the declared root");
        Directory.GetFiles(movedOut).Should().ContainSingle("no temporary file was created through the swapped link either");
        verdict.Should().BeOfType<DeletionVerdict.Refused>().Which.Rule.Should().Be(DeletionRule.OutsideDeclaredRoot);
    }

    [Fact]
    public void A_link_swapped_in_after_the_temporary_file_was_written_is_refused_before_the_rename()
    {
        using var host = new SandboxHost("atomic-swap-temp");
        SkipUnlessLinksWork(host);
        var root = host.Root.Dir("cfg");
        var sub = host.Root.Dir("cfg/sub");
        var target = host.Root.File("cfg/sub/config.json", "old");
        var movedOut = Path.Combine(host.Root.Dir("outside"), "sub");
        var files = SwappingAt(host, AtomicWriteStep.TempWritten, () => ReplaceWithLink(sub, movedOut));

        var verdict = files.WriteFileAtomically(target, NewContent, new DeletionScope(root, "config-set"));

        File.ReadAllText(Path.Combine(movedOut, "config.json")).Should().Be("old", "the rename must not follow the swapped link out of the declared root");
        verdict.Should().BeOfType<DeletionVerdict.Refused>().Which.Rule.Should().Be(DeletionRule.OutsideDeclaredRoot);
    }

    [Fact]
    public void A_target_replaced_by_a_link_inside_the_root_after_the_temp_was_written_is_refused_as_changed_and_the_temp_removed()
    {
        // The policy alone would allow the new place (it is inside the root); what is refused is that it
        // is not the place that was approved.
        using var host = new SandboxHost("atomic-swap-target");
        SkipUnlessLinksWork(host);
        var root = host.Root.Dir("cfg");
        var target = host.Root.File("cfg/config.json", "old");
        var elsewhere = host.Root.Dir("cfg/elsewhere");
        var files = SwappingAt(host, AtomicWriteStep.TempWritten, () =>
        {
            File.Delete(target);
            DirectoryLinks.TryCreate(target, elsewhere).Should().BeTrue("the link-ability probe passed a moment ago");
        });

        var verdict = files.WriteFileAtomically(target, NewContent, new DeletionScope(root, "config-set"));

        verdict.Should().BeOfType<DeletionVerdict.Refused>().Which.Rule.Should().Be(DeletionRule.PathChanged);
        Directory.GetFiles(root).Should().BeEmpty("the temporary file was removed, and nothing replaced the link");
        Directory.EnumerateFileSystemEntries(elsewhere).Should().BeEmpty("nothing was written through the link");
    }

    [Fact]
    public void The_temporary_file_is_created_inside_the_resolved_parent_not_beside_the_spelled_path()
    {
        using var host = new SandboxHost("atomic-temp-place");
        SkipUnlessLinksWork(host);
        var real = host.Root.Dir("cfg-real");
        var spelled = host.Root.Under("cfg-link");
        DirectoryLinks.TryCreate(spelled, real).Should().BeTrue();
        var temps = new List<string>();
        var files = new PhysicalFileSystem(host.Paths, PhysicalFileSystem.ReadLinkTarget, (step, path) =>
        {
            if (step == AtomicWriteStep.TempWritten)
            {
                temps.Add(path);
            }
        });

        var verdict = files.WriteFileAtomically(Path.Combine(spelled, "config.json"), NewContent, new DeletionScope(spelled, "config-set"));

        temps.Should().ContainSingle().Which.Should().StartWith(real + Path.DirectorySeparatorChar, "the temporary file is built in the approved REAL parent");
        verdict.IsAllowed.Should().BeTrue();
        File.ReadAllText(Path.Combine(real, "config.json")).Should().Be("new");
        Directory.GetFiles(real).Should().ContainSingle("the temporary file was renamed onto the target");
    }

    /// <summary>A file system that runs <paramref name="swap"/> once, at <paramref name="at"/>.</summary>
    private static PhysicalFileSystem SwappingAt(SandboxHost host, AtomicWriteStep at, Action swap) =>
        new(host.Paths, PhysicalFileSystem.ReadLinkTarget, (step, _) =>
        {
            if (step == at)
            {
                swap();
            }
        });

    /// <summary>Moves <paramref name="directory"/> to <paramref name="movedTo"/> and leaves a link to it in its place.</summary>
    private static void ReplaceWithLink(string directory, string movedTo)
    {
        Directory.Move(directory, movedTo);
        DirectoryLinks.TryCreate(directory, movedTo).Should().BeTrue("the link-ability probe passed a moment ago");
    }

    private static void SkipUnlessLinksWork(SandboxHost host) =>
        Assert.SkipUnless(
            DirectoryLinks.TryCreate(host.Root.Under("link-probe"), host.Root.Dir("link-probe-target")),
            "this account can create neither a symbolic link nor a junction");
}
