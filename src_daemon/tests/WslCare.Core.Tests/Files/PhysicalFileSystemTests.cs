using System.Text;

using FluentAssertions;

using WslCare.Core.Files;
using WslCare.Core.Files.Deletion;
using WslCare.TestSupport;

namespace WslCare.Core.Tests.Files;

/// <summary>
/// The real file system over a sandboxed host: a spelled path is resolved before it is judged, so
/// <c>..</c> and links cannot smuggle a target out of the declared root.
/// </summary>
public sealed class PhysicalFileSystemTests
{
    [Fact]
    public void A_file_inside_the_declared_root_is_deleted()
    {
        using var host = new SandboxHost("fs-delete");
        var root = host.Root.Dir("work");
        var file = host.Root.File("work/a.txt", "x");

        var verdict = host.Files.DeleteFile(file, new DeletionScope(root, "test"));

        verdict.IsAllowed.Should().BeTrue();
        File.Exists(file).Should().BeFalse();
    }

    [Fact]
    public void Dot_dot_traversal_out_of_the_root_is_refused_and_nothing_is_deleted()
    {
        using var host = new SandboxHost("fs-dotdot");
        var root = host.Root.Dir("work");
        var victim = host.Root.File("other/keep.txt", "x");
        var spelled = Path.Combine(root, "..", "other", "keep.txt");

        var verdict = host.Files.DeleteFile(spelled, new DeletionScope(root, "test"));

        verdict.Should().BeOfType<DeletionVerdict.Refused>().Which.Rule.Should().Be(DeletionRule.OutsideDeclaredRoot);
        File.Exists(victim).Should().BeTrue();
    }

    [Fact]
    public void A_path_under_the_sandboxed_agent_root_is_refused_even_though_the_root_was_declared_around_it()
    {
        using var host = new SandboxHost("fs-agent");
        var claude = host.Paths.AgentRoots.First(r => r.EndsWith(".claude", StringComparison.Ordinal));
        var session = Path.Combine(claude, "projects", "p1", "s.jsonl");
        Directory.CreateDirectory(Path.GetDirectoryName(session)!);
        File.WriteAllText(session, "{}");

        var verdict = host.Files.DeleteFile(session, new DeletionScope(host.Paths.Home + Path.DirectorySeparatorChar + ".claude", "test"));

        verdict.Should().BeOfType<DeletionVerdict.Refused>().Which.Rule.Should().Be(DeletionRule.AgentFolder);
        File.Exists(session).Should().BeTrue();
    }

    [Fact]
    public void A_link_inside_the_root_pointing_at_a_protected_folder_is_refused()
    {
        using var host = new SandboxHost("fs-link");
        var root = host.Root.Dir("work");
        var claude = host.Paths.AgentRoots.First(r => r.EndsWith(".claude", StringComparison.Ordinal));
        var projects = Path.Combine(claude, "projects");
        Directory.CreateDirectory(projects);
        File.WriteAllText(Path.Combine(projects, "s.jsonl"), "{}");
        var link = Path.Combine(root, "sessions");
        Assert.SkipUnless(DirectoryLinks.TryCreate(link, projects), "this account can create neither a symbolic link nor a junction");

        var verdict = host.Files.DeleteFile(Path.Combine(link, "s.jsonl"), new DeletionScope(root, "test"));

        verdict.Should().BeOfType<DeletionVerdict.Refused>().Which.Rule.Should().Be(DeletionRule.AgentFolder);
        File.Exists(Path.Combine(projects, "s.jsonl")).Should().BeTrue();
    }

    [Fact]
    public void Deleting_the_link_itself_rather_than_through_it_is_judged_on_the_link_target_too()
    {
        // Removing the link entry is harmless to the target, but the policy cannot know a caller will
        // not recurse through it; a link INTO a protected place is refused whole.
        using var host = new SandboxHost("fs-link-itself");
        var root = host.Root.Dir("work");
        var git = host.Paths.GitRoots[0];
        Directory.CreateDirectory(git);
        var link = Path.Combine(root, "repos");
        Assert.SkipUnless(DirectoryLinks.TryCreate(link, git), "this account can create neither a symbolic link nor a junction");

        var verdict = host.Files.DeleteDirectory(link, new DeletionScope(root, "test"));

        verdict.Should().BeOfType<DeletionVerdict.Refused>().Which.Rule.Should().Be(DeletionRule.GitFolder);
        Directory.Exists(git).Should().BeTrue();
    }

    [Fact]
    public void An_atomic_write_replaces_the_content_and_leaves_no_temporary_file_behind()
    {
        using var host = new SandboxHost("fs-atomic");
        var dir = host.Root.Dir("cfg");
        var file = host.Root.File("cfg/config.json", "old");

        var verdict = host.Files.WriteFileAtomically(file, Encoding.UTF8.GetBytes("new"), new DeletionScope(dir, "config-set"));

        verdict.IsAllowed.Should().BeTrue();
        File.ReadAllText(file).Should().Be("new");
        Directory.GetFiles(dir).Should().ContainSingle();
    }

    [Fact]
    public void An_atomic_write_outside_its_declared_root_is_refused_before_anything_is_written()
    {
        using var host = new SandboxHost("fs-atomic-refused");
        var dir = host.Root.Dir("cfg");
        var elsewhere = host.Root.Under("elsewhere/config.json");

        var verdict = host.Files.WriteFileAtomically(elsewhere, Encoding.UTF8.GetBytes("new"), new DeletionScope(dir, "config-set"));

        verdict.Should().BeOfType<DeletionVerdict.Refused>();
        File.Exists(elsewhere).Should().BeFalse();
    }

    [Fact]
    public void A_move_into_the_root_from_inside_it_is_allowed_and_performed()
    {
        using var host = new SandboxHost("fs-move");
        var dir = host.Root.Dir("cfg");
        var file = host.Root.File("cfg/config.json", "{");

        var verdict = host.Files.MoveFile(file, Path.Combine(dir, "config.json.broken"), new DeletionScope(dir, "config-set"));

        verdict.IsAllowed.Should().BeTrue();
        File.Exists(file).Should().BeFalse();
        File.ReadAllText(Path.Combine(dir, "config.json.broken")).Should().Be("{");
    }

    [Fact]
    public void Reading_distinguishes_missing_from_unreadable()
    {
        using var host = new SandboxHost("fs-read");
        var dirInsteadOfFile = host.Root.Dir("cfg/config.json");

        host.Files.ReadFile(host.Root.Under("cfg/nothing.json")).Should().BeOfType<FileReadResult.Missing>();
        host.Files.ReadFile(dirInsteadOfFile).Should().BeOfType<FileReadResult.Unreadable>();
        host.Files.ReadFile(host.Root.File("cfg/real.json", "{}")).Should().BeOfType<FileReadResult.Content>()
            .Which.Bytes.Should().Equal(Encoding.UTF8.GetBytes("{}"));
    }

    [Fact]
    public void Append_line_writes_whole_lines_and_a_concurrent_appender_waits_for_the_lock()
    {
        using var host = new SandboxHost("fs-append");
        var file = host.Root.Under("state/history.jsonl");
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        var lines = Enumerable.Range(0, 40).Select(i => $"{{\"n\":{i},\"pad\":\"{new string('x', 2000)}\"}}").ToList();

        Parallel.ForEach(lines, line => host.Files.AppendLine(file, line, TimeSpan.FromSeconds(10)));

        var written = File.ReadAllLines(file);
        written.Should().HaveCount(40);
        written.Should().BeEquivalentTo(lines, "every line must arrive whole, none interleaved with another");
    }

    [Fact]
    public void A_reader_holding_the_history_open_never_blocks_an_append()
    {
        // The flaky RunRetentionTests race, made deterministic: retention reads history.jsonl again right after its
        // rewrite released the lock, while a racing run appends. On Windows a reader that denies writers
        // (File.ReadAllBytes shares Read only) made the append's FileMode.Append open throw a sharing violation - the
        // run's history line was lost. Linux has no mandatory share modes, so there this holds trivially.
        using var host = new SandboxHost("fs-read-shared");
        var file = host.Root.File("state/history.jsonl", "{\"n\":0}\n");

        using (var reader = PhysicalFileSystem.OpenForReading(file))
        {
            var append = () => host.Files.AppendLine(file, "{\"n\":1}", TimeSpan.FromSeconds(5));
            append.Should().NotThrow("a reader of the history must never make a run lose its line");
            reader.ReadByte().Should().Be('{', "the reader keeps reading what it opened");
        }

        File.ReadAllText(file).Should().Be("{\"n\":0}\n{\"n\":1}\n");
    }

    [Fact]
    public async Task An_atomic_replace_waits_out_a_reader_that_holds_the_file_for_a_moment()
    {
        // The heartbeat's half of the same flake: the engine test read running.json while the heartbeat replaced it. On
        // Windows a rename onto a file another handle holds is refused even when that handle shares delete, so a reader's
        // microseconds made the heartbeat's write fail. The rename now waits a reader out (up to 2 s). Trivial on Linux.
        using var host = new SandboxHost("fs-replace-reader");
        var state = host.Root.Dir("state");
        var file = host.Root.File("state/running.json", "{\"n\":0}");
        var reader = PhysicalFileSystem.OpenForReading(file);
        var token = TestContext.Current.CancellationToken;
        var release = Task.Run(
            async () =>
            {
                await Task.Delay(TimeSpan.FromMilliseconds(200), token);
                await reader.DisposeAsync();
            },
            token);

        var verdict = host.Files.WriteFileAtomically(file, Encoding.UTF8.GetBytes("{\"n\":1}"), new DeletionScope(state, "test"));
        await release;

        verdict.IsAllowed.Should().BeTrue();
        File.ReadAllText(file).Should().Be("{\"n\":1}", "the replace landed once the reader let go");
    }

    [Fact]
    public void Append_line_gives_up_with_a_timeout_when_the_lock_is_held()
    {
        using var host = new SandboxHost("fs-append-lock");
        var file = host.Root.Under("state/history.jsonl");
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        using var held = new FileStream(file + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);

        var act = () => host.Files.AppendLine(file, "{}", TimeSpan.FromMilliseconds(300));

        act.Should().Throw<TimeoutException>().WithMessage("*.lock*");
    }

    /// <summary>CI run 37202261532 (2026-10-04): <c>status --json</c> listed the container cgroups in the order the disk
    /// answered — an ext4 directory reads in the order of a hash seeded per filesystem, so every runner image answered its
    /// own order and the golden made in WSL went stale on both Linux legs. A listing is in ordinal order on every disk;
    /// the names here are made in an order that is neither, and mix case, so NTFS's case-insensitive order is not it either.</summary>
    [Fact]
    public void Directories_and_files_are_listed_in_ordinal_order_whatever_order_the_disk_answers()
    {
        using var host = new SandboxHost("fs-list-order");
        var root = host.Root.Dir("listed");
        string[] names = ["gamma9", "beta", "_under", "Epsilon", "zeta", "Alpha", "gamma10", "Delta", "c0ffee", "9lives", "omega", "Kappa"];
        foreach (var name in names)
        {
            host.Root.Dir($"listed/{name}");
            host.Root.File($"listed/{name}.txt", name);
        }

        string[] ordinal = [.. names.Order(StringComparer.Ordinal)];

        host.Files.ListDirectories(root).Select(Path.GetFileName).Should().Equal(ordinal);
        host.Files.ListFiles(root).Select(Path.GetFileNameWithoutExtension).Should().Equal(ordinal);
    }
}
