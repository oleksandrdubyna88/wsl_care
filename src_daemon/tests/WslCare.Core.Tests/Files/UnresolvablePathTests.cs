using System.Text;

using FluentAssertions;

using WslCare.Core.Files;
using WslCare.Core.Files.Deletion;
using WslCare.Core.Hosting;
using WslCare.TestSupport;

namespace WslCare.Core.Tests.Files;

/// <summary>
/// Fail closed: a component whose link status cannot be read is never taken for a plain name, so
/// every destructive operation through it is refused by <see cref="DeletionRule.Unresolvable"/> and
/// nothing is deleted, moved or written.
/// </summary>
/// <remarks>The seam tests hand <see cref="PhysicalFileSystem"/> the REAL link reader for every
/// component except one, which throws exactly what an access denial throws. The last test denies a
/// real directory, because the seam proves the decision and only the disk proves the real reader
/// reports the failure at all (measured 2026-10-02: <c>FileInfo.LinkTarget</c> alone answers
/// <c>null</c> for an unreadable component, on both families).</remarks>
public sealed class UnresolvablePathTests
{
    [Fact]
    public void A_delete_through_a_component_that_cannot_be_inspected_is_refused_and_nothing_is_deleted()
    {
        using var host = new SandboxHost("unresolvable-delete");
        var (root, locked) = Layout(host);
        var file = host.Root.File("work/locked/a.txt", "x");

        var verdict = FailingAt(host, locked).DeleteFile(file, new DeletionScope(root, "test"));

        File.Exists(file).Should().BeTrue("a path whose real location is unknown is never deleted");
        Unresolvable(verdict, locked);
    }

    [Fact]
    public void A_directory_delete_through_a_component_that_cannot_be_inspected_is_refused()
    {
        using var host = new SandboxHost("unresolvable-delete-dir");
        var (root, locked) = Layout(host);
        var inner = host.Root.File("work/locked/inner/a.txt", "x");

        var verdict = FailingAt(host, locked).DeleteDirectory(Path.GetDirectoryName(inner)!, new DeletionScope(root, "test"));

        File.Exists(inner).Should().BeTrue();
        Unresolvable(verdict, locked);
    }

    [Fact]
    public void A_move_whose_source_or_destination_cannot_be_inspected_is_refused_and_nothing_moves()
    {
        using var host = new SandboxHost("unresolvable-move");
        var (root, locked) = Layout(host);
        var inside = host.Root.File("work/locked/a.txt", "x");
        var plain = host.Root.File("work/plain.txt", "y");
        var files = FailingAt(host, locked);

        var fromLocked = files.MoveFile(inside, Path.Combine(root, "moved.txt"), new DeletionScope(root, "test"));
        var intoLocked = files.MoveFile(plain, Path.Combine(locked, "moved.txt"), new DeletionScope(root, "test"));

        File.Exists(inside).Should().BeTrue();
        File.Exists(plain).Should().BeTrue();
        File.Exists(Path.Combine(root, "moved.txt")).Should().BeFalse();
        File.Exists(Path.Combine(locked, "moved.txt")).Should().BeFalse();
        Unresolvable(fromLocked, locked);
        Unresolvable(intoLocked, locked);
    }

    [Fact]
    public void An_atomic_write_through_a_component_that_cannot_be_inspected_is_refused_and_writes_nothing()
    {
        using var host = new SandboxHost("unresolvable-write");
        var (root, locked) = Layout(host);
        var target = host.Root.File("work/locked/config.json", "old");

        var verdict = FailingAt(host, locked).WriteFileAtomically(target, Encoding.UTF8.GetBytes("new"), new DeletionScope(root, "config-set"));

        File.ReadAllText(target).Should().Be("old");
        Directory.GetFiles(locked).Should().ContainSingle("no temporary file was created either");
        Unresolvable(verdict, locked);
    }

    [Fact]
    public void A_declared_root_that_cannot_be_inspected_is_refused_as_unresolvable_rather_than_judged_by_its_spelling()
    {
        // The target itself resolves; only the ROOT passes through the uninspectable component — a
        // root of unknown real location cannot say what is inside it.
        using var host = new SandboxHost("unresolvable-root");
        var (_, locked) = Layout(host);
        var file = host.Root.File("work/plain.txt", "x");

        var verdict = FailingAt(host, locked).DeleteFile(file, new DeletionScope(locked, "test"));

        File.Exists(file).Should().BeTrue();
        Unresolvable(verdict, locked);
    }

    [Fact]
    public void The_same_seam_still_allows_what_does_not_pass_through_the_uninspectable_component()
    {
        // The positive companion: the fixture is accepted where it should be, so the refusals above are
        // refusals of the component, not of a fixture the code rejects for some other reason.
        using var host = new SandboxHost("unresolvable-elsewhere");
        var (root, locked) = Layout(host);
        var file = host.Root.File("work/plain.txt", "x");

        var verdict = FailingAt(host, locked).DeleteFile(file, new DeletionScope(root, "test"));

        verdict.IsAllowed.Should().BeTrue();
        File.Exists(file).Should().BeFalse();
    }

    [Fact]
    public async Task A_directory_this_account_may_not_read_refuses_a_delete_under_it_as_unresolvable()
    {
        using var host = new SandboxHost("unresolvable-acl");
        var (root, locked) = Layout(host);
        var file = host.Root.File("work/locked/a.txt", "x");
        DeletionVerdict verdict;
        await using (var denial = await AccessDenial.TryDenyAsync(locked))
        {
            Assert.SkipWhen(denial is null, "this account cannot be denied access to its own directory (root or elevated)");
            verdict = host.Files.DeleteFile(file, new DeletionScope(root, "test"));
        }

        File.Exists(file).Should().BeTrue("the denial was lifted and the file is still there");
        verdict.Should().BeOfType<DeletionVerdict.Refused>().Which.Rule.Should().Be(DeletionRule.Unresolvable);
    }

    /// <summary><c>work/</c> is the declared root, <c>work/locked/</c> the component that cannot be inspected.</summary>
    private static (string Root, string Locked) Layout(SandboxHost host) => (host.Root.Dir("work"), host.Root.Dir("work/locked"));

    /// <summary>The real reader everywhere except <paramref name="uninspectable"/>, which throws as an ACL denial does.</summary>
    private static PhysicalFileSystem FailingAt(SandboxHost host, string uninspectable) =>
        new(
            host.Paths,
            path => PathRules.ForThisOs.PathEquals(path, uninspectable)
                ? throw new UnauthorizedAccessException($"Access to the path '{path}' is denied.")
                : PhysicalFileSystem.ReadLinkTarget(path),
            static (_, _) => { });

    private static void Unresolvable(DeletionVerdict verdict, string component)
    {
        var refused = verdict.Should().BeOfType<DeletionVerdict.Refused>().Subject;
        refused.Rule.Should().Be(DeletionRule.Unresolvable);
        refused.Reason.Should().Contain(component, "the refusal names the component that could not be inspected");
    }
}
