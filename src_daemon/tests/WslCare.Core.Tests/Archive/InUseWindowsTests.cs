using System.Diagnostics;

using FluentAssertions;

using WslCare.Core.Archive;
using WslCare.Core.Collectors;
using WslCare.Core.Mcp;
using WslCare.TestSupport;

namespace WslCare.Core.Tests.Archive;

/// <summary>
/// Plan §15r D2.2, E9.S5 — the Windows side's open-file check: the Restart Manager ASKED per unit (never an open of a session file),
/// failing closed on a holder, an error or a stall; and a live Claude Code on Windows — whose working folder cannot be read — keeping
/// every Claude Code session in place.
/// </summary>
public sealed class InUseWindowsTests : IDisposable
{
    private const string WindowsOnly = "the Restart Manager is Windows'";

    private readonly TempRoot _root = new("in-use-windows");

    public void Dispose() => _root.Dispose();

    // ---- the extended-length path the Restart Manager is handed ---------------------------------------------------------------

    /// <summary>The plan round's finding 6: a share takes <c>\\?\UNC\</c>, never <c>\\?\\\server</c>; a drive path <c>\\?\</c>.</summary>
    [Theory]
    [InlineData(@"C:\Users\me\.claude\projects/p/s1.jsonl", @"\\?\C:\Users\me\.claude\projects\p\s1.jsonl")]
    [InlineData(@"\\nas\share\archive\claude-code\s1.jsonl", @"\\?\UNC\nas\share\archive\claude-code\s1.jsonl")]
    [InlineData(@"\\?\C:\already\extended", @"\\?\C:\already\extended")]
    [InlineData(@"V:\a\\b\.\c\", @"\\?\V:\a\b\c")]
    public void A_path_is_handed_over_in_its_extended_length_form(string path, string extended) =>
        ExtendedPath.Of(path).Should().Be(extended);

    // ---- the real Restart Manager ----------------------------------------------------------------------------------------------

    /// <summary>A file this process holds open — with NO sharing, so any open of it by the product would fail — is named held, and by
    /// this process; a file nobody holds is free.</summary>
    [Fact]
    public void The_restart_manager_names_the_process_that_holds_a_file_and_never_opens_it()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), WindowsOnly);
        var held = _root.File("held.jsonl", "a transcript");
        var free = _root.File("free.jsonl", "another");
        using var holder = new FileStream(held, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

        var heldAnswer = Ask(held);
        var freeAnswer = Ask(free);

        heldAnswer.Should().BeOfType<RmAnswer.Held>().Which.Holders.Should().ContainSingle(h => h.Contains($"pid {Environment.ProcessId}", StringComparison.Ordinal));
        freeAnswer.Should().BeOfType<RmAnswer.Free>();
    }

    /// <summary>The row's 300-character path: asked through the extended-length form, it answers as any other.</summary>
    [Fact]
    public void A_file_under_a_300_character_path_is_asked_like_any_other()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), WindowsOnly);
        var deep = Path.Combine(_root.Path, string.Join('\\', Enumerable.Repeat(new string('d', 50), 6)));
        Directory.CreateDirectory(ExtendedPath.Of(deep));
        var held = Path.Combine(deep, "s1.jsonl");
        held.Length.Should().BeGreaterThan(300);
        using var holder = new FileStream(ExtendedPath.Of(held), FileMode.Create, FileAccess.ReadWrite, FileShare.None);

        var answer = Ask(held);

        answer.Should().BeOfType<RmAnswer.Held>(answer.ToString());
    }

    // ---- the Windows view fails closed ------------------------------------------------------------------------------------------

    [Fact]
    public void A_held_file_an_error_or_a_thrown_question_keeps_the_unit_naming_why()
    {
        InUseWindows.View(new GivenAnswers(new RmAnswer.Held(["claude.exe (pid 7)"])), NoClaude(), TimeSpan.FromSeconds(5)).HeldBy(["C:\\x"])
            .Should().Contain("held open by claude.exe (pid 7)");
        InUseWindows.View(new GivenAnswers(new RmAnswer.Failed("RmRegisterResources answered error 5")), NoClaude(), TimeSpan.FromSeconds(5)).HeldBy(["C:\\x"])
            .Should().Contain("could not be asked").And.Contain("error 5");
        InUseWindows.View(new Throwing(), NoClaude(), TimeSpan.FromSeconds(5)).HeldBy(["C:\\x"])
            .Should().Contain("could not be asked");
        InUseWindows.View(new GivenAnswers(new RmAnswer.Free()), NoClaude(), TimeSpan.FromSeconds(5)).HeldBy(["C:\\x"])
            .Should().BeEmpty();
    }

    /// <summary>The plan round's finding 5: a question that does not answer within its ceiling keeps the unit — and every later
    /// question is answered at once as stalled, the stalled Restart Manager not asked again; phase 2's re-check is bounded the same way.</summary>
    [Fact]
    public void A_stalled_restart_manager_keeps_this_unit_and_every_later_one_without_asking_again()
    {
        using var release = new ManualResetEventSlim(false);
        var stalled = new Stalled(release);
        var view = InUseWindows.View(stalled, NoClaude(), TimeSpan.FromMilliseconds(200));
        var watch = Stopwatch.StartNew();

        var first = view.HeldBy(["C:\\a"]);
        var second = view.HeldBy(["C:\\b"]);
        release.Set();

        first.Should().Contain("did not answer within archive.inUseScanSeconds");
        second.Should().Be(first);
        SpinWait.SpinUntil(() => stalled.Asked > 0, TimeSpan.FromSeconds(10)).Should().BeTrue("the first question did start (a slow runner may start it late)");
        Thread.Sleep(TimeSpan.FromMilliseconds(300));
        stalled.Asked.Should().Be(1, "a stalled Restart Manager is not asked again");
        watch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(30));
    }

    // ---- a live Claude Code on Windows ------------------------------------------------------------------------------------------

    /// <summary>The plan round's findings 0 and 3: Claude Code's working folder cannot be read on Windows, so a live Claude Code —
    /// <c>claude.exe</c>, or <c>node.exe</c> running its package — keeps every Claude Code session; so does a table that cannot be read.</summary>
    [Theory]
    [InlineData("claude.exe", "", "Claude Code runs on Windows")]
    [InlineData("node.exe", @"node C:\Users\me\AppData\Roaming\npm\node_modules\@anthropic-ai\claude-code\cli.js", "Claude Code runs on Windows")]
    [InlineData("node.exe", @"node C:\tools\playwright-mcp\cli.js", "")]
    [InlineData("Code.exe", "", "")]
    public void A_live_claude_code_on_windows_keeps_every_claude_code_session(string exe, string commandLine, string reason)
    {
        var running = InUseWindows.ClaudeRunning(new GivenTable([new WindowsProcessEntry(40, 4, exe)], commandLine));

        if (reason.Length == 0)
        {
            running.Should().BeEmpty();
        }
        else
        {
            running.Should().Contain(reason).And.Contain("pid 40");
        }
    }

    [Fact]
    public void A_process_table_that_cannot_be_read_keeps_every_claude_code_session()
    {
        InUseWindows.ClaudeRunning(new UnreadWindowsProcessTable("access denied")).Should().Contain("could not be read").And.Contain("access denied");
    }

    /// <summary>The selection asks the view: a held unit stays <c>in-use</c> with the holder; with Claude Code running, a Claude Code unit
    /// stays <c>agent-working-here</c> while another agent's unit is not held back by it.</summary>
    [Fact]
    public void The_liveness_check_asks_the_windows_view()
    {
        var held = InUseWindows.View(new GivenAnswers(new RmAnswer.Held(["codex.exe (pid 9)"])), NoClaude(), TimeSpan.FromSeconds(5));
        var claude = InUseWindows.View(new GivenAnswers(new RmAnswer.Free()), new GivenTable([new WindowsProcessEntry(40, 4, "claude.exe")], string.Empty), TimeSpan.FromSeconds(5));

        Liveness.Problem(held, p => p, "codex", "C:\\Users\\me\\.codex\\sessions", "2026/08/01/rollout.jsonl", ["2026/08/01/rollout.jsonl"]).Should().Contain("codex.exe (pid 9)");
        Liveness.Problem(claude, p => p, "claude-code", "C:\\Users\\me\\.claude", "projects/p/s1.jsonl", ["projects/p/s1.jsonl"]).Should().Contain("Claude Code runs on Windows");
        Liveness.Problem(claude, p => p, "codex", "C:\\Users\\me\\.codex\\sessions", "2026/08/01/rollout.jsonl", ["2026/08/01/rollout.jsonl"]).Should().BeEmpty();
    }

    /// <summary>The real Restart Manager on Windows (the tests that call it skip elsewhere).</summary>
    private static RmAnswer Ask(string file) => OperatingSystem.IsWindows() ? new RestartManager().Holders([file]) : new RmAnswer.Failed(WindowsOnly);

    // ---- the gate round over E9.S5 and the own review ----------------------------------------------------------------------

    /// <summary>Gate finding 6 / own review 1: the view is built once per run, phase 2 asks it minutes later — a Claude Code started
    /// in between must still keep the Claude Code sessions, so whether it runs is asked at each question, never frozen.</summary>
    [Fact]
    public void A_claude_code_started_after_the_view_was_built_is_seen_by_the_next_question()
    {
        var table = new ChangingTable();
        var view = InUseWindows.View(new GivenAnswers(new RmAnswer.Free()), table, TimeSpan.FromSeconds(5));

        var before = Liveness.Problem(view, p => p, "claude-code", "C:\\Users\\me\\.claude", "projects/p/s1.jsonl", ["projects/p/s1.jsonl"]);
        table.Processes = [new WindowsProcessEntry(40, 4, "claude.exe")];
        var after = Liveness.Problem(view, p => p, "claude-code", "C:\\Users\\me\\.claude", "projects/p/s1.jsonl", ["projects/p/s1.jsonl"]);

        before.Should().BeEmpty();
        after.Should().Contain("Claude Code runs on Windows");
    }

    /// <summary>Gate finding 12: with Claude Code running every Claude Code unit stays anyway — the Restart Manager is not asked for it.</summary>
    [Fact]
    public void With_claude_code_running_its_units_are_not_asked_of_the_restart_manager()
    {
        var counting = new Counting();
        var view = InUseWindows.View(counting, new GivenTable([new WindowsProcessEntry(40, 4, "claude.exe")], string.Empty), TimeSpan.FromSeconds(5));

        Liveness.Problem(view, p => p, "claude-code", "C:\\Users\\me\\.claude", "projects/p/s1.jsonl", ["projects/p/s1.jsonl"]).Should().Contain("Claude Code runs");

        counting.Asked.Should().Be(0);
    }

    /// <summary>Gate finding 9: the file system's list of users names every process with a handle — this one's own attribute handle
    /// included. A long-path file nobody else holds is free; one a CHILD process holds is held, by that child.</summary>
    [Fact]
    public void Past_max_path_the_product_does_not_count_its_own_look_and_names_another_holder()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), WindowsOnly);
        var deep = Path.Combine(_root.Path, string.Join('\\', Enumerable.Repeat(new string('e', 50), 6)));
        Directory.CreateDirectory(ExtendedPath.Of(deep));
        var free = Path.Combine(deep, "free.jsonl");
        var held = Path.Combine(deep, "held.jsonl");
        File.WriteAllText(ExtendedPath.Of(free), "free");
        File.WriteAllText(ExtendedPath.Of(held), "held");
        using var holder = HoldingChild.Hold(ExtendedPath.Of(held));

        Ask(free).Should().BeOfType<RmAnswer.Free>();
        Ask(held).Should().BeOfType<RmAnswer.Held>().Which.Holders.Should().ContainSingle(h => h == $"a process (pid {holder.Pid})");
    }

    /// <summary>Own review 5: the Restart Manager's application name is often a window title — a document's or a tab's name — and the
    /// reason lands in the records: a holder is named by its pid only.</summary>
    [Fact]
    public void A_holder_is_named_by_its_pid_only()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), WindowsOnly);
        var held = _root.File("titled.jsonl", "a transcript");
        using var holder = HoldingChild.Hold(held);

        Ask(held).Should().BeOfType<RmAnswer.Held>().Which.Holders.Should().Equal($"a process (pid {holder.Pid})");
    }

    /// <summary>Gate finding 4: the questions of a scan share the time the caller has — once it is spent, every later unit stays without
    /// a question; finding 15: a cancelled scan asks nothing more either.</summary>
    [Fact]
    public void A_spent_budget_or_a_cancellation_keeps_every_later_unit_without_asking()
    {
        var counting = new Counting();
        using var cancel = new CancellationTokenSource();
        var spent = InUseWindows.View(counting, NoClaude(), new WindowsAsk(TimeSpan.FromSeconds(5), TimeSpan.Zero, CancellationToken.None) { Latch = new StallLatch() });
        var cancelled = InUseWindows.View(counting, NoClaude(), new WindowsAsk(TimeSpan.FromSeconds(5), TimeSpan.FromMinutes(5), cancel.Token) { Latch = new StallLatch() });
        cancel.Cancel();

        spent.HeldBy(["C:\\x"]).Should().Contain("passed the time it had");
        cancelled.HeldBy(["C:\\x"]).Should().Contain("was stopped");
        SpinWait.SpinUntil(() => counting.Asked > 0, TimeSpan.FromMilliseconds(500)).Should().BeFalse("no question is started once the time is spent or the scan stopped");
    }

    /// <summary>Gate finding 2: a stalled native worker cannot be stopped — a LATER view of the same process (the next preview, the next
    /// run) does not ask again either: the latch is the process's.</summary>
    [Fact]
    public void A_stall_is_remembered_by_every_later_view_of_the_process()
    {
        using var release = new ManualResetEventSlim(false);
        var latch = new StallLatch();
        var stalled = new Stalled(release);
        var first = InUseWindows.View(stalled, NoClaude(), new WindowsAsk(TimeSpan.FromMilliseconds(200), TimeSpan.FromMinutes(5), CancellationToken.None) { Latch = latch });
        var later = InUseWindows.View(stalled, NoClaude(), new WindowsAsk(TimeSpan.FromMilliseconds(200), TimeSpan.FromMinutes(5), CancellationToken.None) { Latch = latch });

        first.HeldBy(["C:\\a"]).Should().Contain("did not answer");
        later.HeldBy(["C:\\b"]).Should().Contain("did not answer");
        release.Set();

        SpinWait.SpinUntil(() => stalled.Asked > 0, TimeSpan.FromSeconds(10)).Should().BeTrue("the first question did start (a slow runner may start it late)");
        Thread.Sleep(TimeSpan.FromMilliseconds(300));
        stalled.Asked.Should().Be(1);
    }

    /// <summary>Own review 3: a node.exe of THIS session whose command line cannot be read cannot be told apart from Claude Code — it
    /// keeps the Claude Code sessions; one of another session (another account) does not.</summary>
    [Theory]
    [InlineData(1, "could not be read")]
    [InlineData(2, "")]
    public void An_unreadable_node_of_this_session_keeps_every_claude_code_session(int itsSession, string reason)
    {
        var running = InUseWindows.ClaudeRunning(new SessionTable(itsSession));

        if (reason.Length == 0)
        {
            running.Should().BeEmpty();
        }
        else
        {
            running.Should().Contain(reason).And.Contain("pid 41");
        }
    }

    private sealed class SessionTable(int nodeSession) : IWindowsProcessTable
    {
        public Reading<IReadOnlyList<WindowsProcessEntry>> List() => Reading.Of<IReadOnlyList<WindowsProcessEntry>>([new WindowsProcessEntry(41, 4, "node.exe")]);

        public WindowsProcessDetails Details(int pid) =>
            WindowsProcessDetails.Unopenable("not opened") with { SessionId = Reading.Of(pid == 41 ? nodeSession : 1) };

        public Reading<string> CommandLine(int pid) => Reading.Missing<string>("access denied");
    }

    private sealed class ChangingTable : IWindowsProcessTable
    {
        public IReadOnlyList<WindowsProcessEntry> Processes { get; set; } = [new WindowsProcessEntry(10, 4, "explorer.exe")];

        public Reading<IReadOnlyList<WindowsProcessEntry>> List() => Reading.Of(Processes);

        public WindowsProcessDetails Details(int pid) => WindowsProcessDetails.Unopenable("not asked");
    }

    private sealed class Counting : IRestartManager
    {
        private int _asked;

        public int Asked => Volatile.Read(ref _asked);

        public RmAnswer Holders(IReadOnlyList<string> files)
        {
            Interlocked.Increment(ref _asked);
            return new RmAnswer.Free();
        }
    }

    private static GivenTable NoClaude() => new([new WindowsProcessEntry(10, 4, "explorer.exe")], string.Empty);

    private sealed class Throwing : IRestartManager
    {
        public RmAnswer Holders(IReadOnlyList<string> files) => throw new InvalidOperationException("the native call threw");
    }

    private sealed class Stalled(ManualResetEventSlim release) : IRestartManager
    {
        private int _asked;

        public int Asked => _asked;

        public RmAnswer Holders(IReadOnlyList<string> files)
        {
            Interlocked.Increment(ref _asked);
            release.Wait();
            return new RmAnswer.Free();
        }
    }
}
