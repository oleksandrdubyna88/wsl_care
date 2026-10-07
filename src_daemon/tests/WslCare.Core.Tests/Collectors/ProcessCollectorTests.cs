using FluentAssertions;

using WslCare.Core.Collectors;
using WslCare.Core.Collectors.Procfs;
using WslCare.Core.Files;
using WslCare.TestSupport;

namespace WslCare.Core.Tests.Collectors;

/// <summary>The process table (plan §4.2) over the captured 2026-10-02 tree, and over synthetic trees for the edges.</summary>
public sealed class ProcessCollectorTests
{
    private const long KiB = 1024;

    private static readonly KernelFacts Kernel = new(100, 4096);

    private static ProcessSnapshot FromFixture()
    {
        var paths = ProcfsFixture.PathsAt(ProcfsFixture.Root);
        var files = ProcfsFixture.LinkOverlay(new PhysicalFileSystem(paths), ProcfsFixture.Root);
        var containers = ContainerCgroups.Read(files, paths.CgroupRoot).ValueOr(new ContainerSet([]));
        return new ProcessCollector(files, paths, new FixedTimeProvider(ProcfsFixture.CapturedAt))
            .Read(Reading.Of(Kernel), containers, CancellationToken.None)
            .Should().BeOfType<Reading<ProcessSnapshot>.Available>().Subject.Value;
    }

    private static ProcessEntry Pid(ProcessSnapshot snapshot, int pid) =>
        snapshot.Top.Concat(snapshot.MntWalkers).First(p => p.Pid == pid);

    [Fact]
    public void The_top_30_are_ranked_by_rss_anon_plus_rss_shmem_largest_first()
    {
        var snapshot = FromFixture();

        snapshot.ProcessCount.Should().Be(51, "the fixture holds 51 process directories, none of them a kernel thread or a container member");
        snapshot.Top.Should().HaveCount(ProcessCollector.TopCount);
        snapshot.Top.Select(p => p.HeldBytes).Should().BeInDescendingOrder();
        snapshot.Top[0].Pid.Should().Be(5949, "pylance holds 5 303 864 kB of RssAnon, the most in the fixture");
        snapshot.Top[0].HeldBytes.Should().Be(5_303_864 * KiB);
    }

    [Fact]
    public void Each_entry_carries_owner_age_cpu_state_cwd_and_a_shown_command_line()
    {
        var snapshot = FromFixture();
        var server = Pid(snapshot, 1169);

        server.User.Should().Be("user");
        server.ParentPid.Should().Be(571);
        server.State.Should().Be('S');
        server.CpuSeconds.Should().Be(Reading.Of((9123 + 4169) / 100.0));
        var started = DateTimeOffset.FromUnixTimeSeconds(1_790_948_339).AddSeconds(1948 / 100.0);
        server.Age.Should().Be(Reading.Of(ProcfsFixture.CapturedAt - started), "proc(5): starttime is clock ticks after btime");
        server.Cwd.Should().Be(Reading.Of(ProcfsFixture.Links["proc/1169/cwd"]));
        server.CommandLine.Should().StartWith("/home/user/.vscode-server/bin/").And.Contain("bootstrap-fork").And.HaveLength(CommandLineText.ShownLength);
        server.HasTty.Should().BeTrue("its tty_nr is 34816 (a pts)");
    }

    [Fact]
    public void A_cwd_that_could_not_be_read_is_unavailable_with_the_reason_never_an_empty_path()
    {
        var snapshot = FromFixture();

        ProcfsFixture.Links.Should().NotContainKey("proc/1/cwd", "pid 1 is root's, unreadable to the capturing user");
        snapshot.Top.Should().NotContain(p => p.Pid == 1, "pid 1 holds little; it is outside the top 30 and still in the table");
        snapshot.All.Single(p => p.Pid == 1).Cwd.Should().BeOfType<Reading<string>.Unavailable>().Which.Reason.Should().Contain("cwd");
    }

    [Fact]
    public void Families_follow_the_catalogue_order_and_every_process_lands_in_one()
    {
        var all = FromFixtureAll();

        all.Single(p => p.Pid == 7203).Family.Should().Be("ai-agents", "Claude Code inside a VS Code extension is an agent first");
        all.Single(p => p.Pid == 5814).Family.Should().Be("ai-agents", "agy, Antigravity");
        all.Single(p => p.Pid == 6612).Family.Should().Be("vscode-server", "Microsoft.CodeAnalysis.LanguageServer");
        all.Single(p => p.Pid == 4137).Family.Should().Be("docker-desktop-proxy");
        all.Single(p => p.Pid == 7472).Family.Should().Be("node");
        all.Single(p => p.Pid == 1).Family.Should().Be(ProcessFamilies.Other);
        var snapshot = FromFixture();
        snapshot.Families.Sum(f => f.Count).Should().Be(snapshot.ProcessCount);
        snapshot.Families.Sum(f => f.HeldBytes).Should().Be(snapshot.HeldBytesTotal);
    }

    [Fact]
    public void Processes_whose_cwd_or_arguments_are_under_mnt_are_listed_as_walkers()
    {
        var walkers = FromFixture().MntWalkers.Select(p => p.Pid).ToList();

        walkers.Should().Contain(560, "its cwd is /mnt/c/Users/.../Microsoft VS Code");
        walkers.Should().Contain(5252, "an argument is /mnt/c/.../creds-mcp.exe");
        walkers.Should().Contain(4137, "an argument is /mnt/wsl/docker-desktop");
        walkers.Should().NotContain(5949, "pylance works under the home directory");
    }

    [Fact]
    public void A_child_of_init_or_of_systemd_user_is_orphaned_and_one_of_a_shell_is_not()
    {
        using var tree = new SyntheticProcTree()
            .MemInfo(1000, 0)
            .Process(404, 1, "/user.slice/u.scope", 10, argv: "/usr/lib/systemd/systemd --user")
            .Process(500, 404, "/user.slice/u.scope", 20, argv: "/usr/bin/dotnet build-server")
            .Process(600, 700, "/user.slice/u.scope", 30, argv: "/usr/bin/node x.js")
            .Process(700, 1, "/user.slice/u.scope", 5, argv: "/bin/bash", tty: 34816);

        var entries = Collect(tree).Top;

        entries.Single(p => p.Pid == 500).Orphaned.Should().BeTrue("reparented to systemd --user");
        entries.Single(p => p.Pid == 404).Orphaned.Should().BeTrue("its parent is pid 1");
        entries.Single(p => p.Pid == 600).Orphaned.Should().BeFalse("its parent is a shell");
        entries.Single(p => p.Pid == 700).HasTty.Should().BeTrue();
    }

    [Fact]
    public void A_process_that_vanished_between_listing_and_reading_is_counted_as_vanished_not_as_a_failure()
    {
        using var tree = new SyntheticProcTree().MemInfo(1000, 0).Process(10, 1, "/a", 10);
        Directory.CreateDirectory(Path.Combine(tree.Paths.ProcRoot, "11"));

        var snapshot = Collect(tree);

        snapshot.ProcessCount.Should().Be(1);
        snapshot.Vanished.Should().Be(1);
    }

    [Fact]
    public void Without_a_procfs_the_table_is_unavailable_with_the_path()
    {
        using var root = new TempRoot("noproc");
        var paths = ProcfsFixture.PathsAt(root.Path);

        new ProcessCollector(new PhysicalFileSystem(paths), paths, new FixedTimeProvider())
            .Read(Reading.Of(Kernel), new ContainerSet([]), CancellationToken.None)
            .Should().BeOfType<Reading<ProcessSnapshot>.Unavailable>().Which.Reason.Should().Contain("proc");
    }

    [Fact]
    public void Without_the_clock_tick_age_and_cpu_are_unavailable_and_memory_still_reads()
    {
        using var tree = new SyntheticProcTree().MemInfo(1000, 0).Process(10, 1, "/a", 10);

        var entry = new ProcessCollector(tree.Files, tree.Paths, new FixedTimeProvider())
            .Read(Reading.Missing<KernelFacts>("no auxv"), new ContainerSet([]), CancellationToken.None)
            .ValueOr(null!).Top.Single();

        entry.Age.IsAvailable.Should().BeFalse();
        entry.CpuSeconds.IsAvailable.Should().BeFalse();
        entry.HeldBytes.Should().Be(10 * KiB);
    }

    [Fact]
    public void A_program_path_with_spaces_or_past_the_display_cut_is_still_recognised()
    {
        // Plan §15q E7.S2d, consultation C-2: the shown command line is display text — redacted, cut to
        // processes.shownCommandChars — so a program whose path holds spaces, or is longer than the cut, lost its name when the
        // agent attribution split that text on spaces.
        var deep = "/home/me/" + string.Join('/', Enumerable.Repeat("very-long-folder-name", 12));
        using var tree = new SyntheticProcTree().MemInfo(1000, 0)
            .Process(10, 1, "/a", 10, words: ["/home/me/My Agent Tools/claude", "--resume"])
            .Process(11, 1, "/a", 10, words: [deep + "/claude"]);

        var agents = Collect(tree).All.OrderBy(p => p.Pid).Select(p => WslCare.Core.Agents.AgentProcesses.AgentOf(p)?.Id).ToList();

        agents.Should().Equal("claude-code", "claude-code");
    }

    [Fact]
    public void A_cancelled_token_stops_the_walk()
    {
        using var tree = new SyntheticProcTree().MemInfo(1000, 0).Process(10, 1, "/a", 10);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        var act = () => new ProcessCollector(tree.Files, tree.Paths, new FixedTimeProvider()).Read(Reading.Of(Kernel), new ContainerSet([]), cancelled.Token);

        act.Should().Throw<OperationCanceledException>();
    }

    /// <summary>Every counted process of the fixture, not only the top 30.</summary>
    private static IReadOnlyList<ProcessEntry> FromFixtureAll() => FromFixture().All;

    private static ProcessSnapshot Collect(SyntheticProcTree tree) =>
        new ProcessCollector(tree.Files, tree.Paths, new FixedTimeProvider())
            .Read(Reading.Of(Kernel), new ContainerSet([]), CancellationToken.None)
            .Should().BeOfType<Reading<ProcessSnapshot>.Available>().Subject.Value;
}
