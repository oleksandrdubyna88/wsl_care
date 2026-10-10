using System.Text;

using FluentAssertions;

using WslCare.Core.Archive;
using WslCare.Core.Config;
using WslCare.Core.Files;
using WslCare.Core.Mcp;
using WslCare.TestSupport;

namespace WslCare.Core.Tests.Archive;

/// <summary>
/// Plan §15r, the E9.S5 amendment (owner decision 2026-10-09): while Claude Code runs on Windows — or whether it runs cannot be told —
/// a Claude Code session whose files were ALL untouched for <c>archive.windowsIdleDays</c> may move; one touched within the window stays;
/// a file dated after this machine's clock by more than <c>archive.clockSkewMinutes</c> keeps it (which clock is wrong is not guessed);
/// the Restart Manager's answer still keeps a held one. The times are read fresh at every question.
/// </summary>
public sealed class WindowsIdleTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 10, 9, 12, 0, 0, TimeSpan.Zero);
    private const string Under = "projects/p";

    private readonly string _root = Directory.CreateTempSubdirectory("wsl-care-idle-").FullName;
    private readonly ManualTimeProvider _clock = new(Now);

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private static EffectiveConfig Config(string archive = "") =>
        ConfigLoader.Load([
            (ConfigLoader.DefaultsFile, new FileReadResult.Content(ConfigLoader.EmbeddedDefaults())),
            (new ConfigLayerFile(ConfigLayer.User, "user.json"), new FileReadResult.Content(Encoding.UTF8.GetBytes($$"""{ "archive": { {{archive}} } }"""))),
        ]).Config;

    /// <summary>A file of the unit, last written <paramref name="age"/> before now (a negative age is in the future).</summary>
    private string File(string name, TimeSpan age)
    {
        var path = Path.Combine(_root, Under, name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        System.IO.File.WriteAllText(path, "a transcript");
        System.IO.File.SetLastWriteTimeUtc(path, (Now - age).UtcDateTime);
        return name;
    }

    private WindowsIdle Idle(string archive = "") => WindowsIdle.Of(Stat, Config(archive), _clock);

    /// <summary>The file system's answer, as the real one gives it: a time, missing, or unreadable.</summary>
    private static FileSizeResult Stat(string path) =>
        System.IO.File.Exists(path) ? new FileSizeResult.Measured(new FileInfo(path).Length, new DateTimeOffset(System.IO.File.GetLastWriteTimeUtc(path), TimeSpan.Zero)) : new FileSizeResult.Missing();

    private static InUseView ClaudeView(IRestartManager? restartManager = null) =>
        InUseWindows.View(restartManager ?? new GivenAnswers(new RmAnswer.Free()), new GivenTable([new WindowsProcessEntry(40, 4, "claude.exe")], string.Empty), TimeSpan.FromSeconds(5));

    private string Problem(InUseView view, params string[] names) =>
        Liveness.Problem(view, p => p, "claude-code", Path.Combine(_root, Under), $"{Under}/{names[0]}", names);

    [Fact]
    public void While_claude_runs_a_session_idle_for_the_window_may_move()
    {
        var name = File("s1.jsonl", TimeSpan.FromDays(8));

        Problem(ClaudeView().WithIdle(Idle()), name).Should().BeEmpty();
    }

    [Fact]
    public void When_whether_claude_runs_cannot_be_told_the_same_idle_rule_applies()
    {
        var name = File("s1.jsonl", TimeSpan.FromDays(8));
        var unknown = InUseWindows.View(new GivenAnswers(new RmAnswer.Free()), new UnreadWindowsProcessTable("access denied"), TimeSpan.FromSeconds(5));

        Problem(unknown.WithIdle(Idle()), name).Should().BeEmpty();
        Problem(unknown.WithIdle(Idle()), File("s2.jsonl", TimeSpan.FromDays(2))).Should().Contain("could not be read").And.Contain("archive.windowsIdleDays");
    }

    [Fact]
    public void A_session_touched_within_the_window_stays_naming_the_window()
    {
        var name = File("s1.jsonl", TimeSpan.FromDays(6));

        Problem(ClaudeView().WithIdle(Idle()), name).Should().Contain("Claude Code runs on Windows").And.Contain("archive.windowsIdleDays").And.Contain("7 days");
    }

    /// <summary>The plan round's finding 1: ALL files must be idle — one recent file keeps a unit whose other files are old.</summary>
    [Fact]
    public void One_recent_file_keeps_a_unit_whose_other_files_are_old()
    {
        var old = File("s1.jsonl", TimeSpan.FromDays(40));
        var recent = File("s1/subagents/a.jsonl", TimeSpan.FromDays(1));

        Problem(ClaudeView().WithIdle(Idle()), old, recent).Should().Contain("archive.windowsIdleDays");
    }

    [Fact]
    public void A_file_dated_after_this_machines_clock_beyond_the_skew_keeps_the_session_naming_the_clock()
    {
        var name = File("s1.jsonl", -TimeSpan.FromHours(1));

        Problem(ClaudeView().WithIdle(Idle()), name).Should().Contain("after this machine's clock").And.Contain("archive.clockSkewMinutes");
    }

    [Fact]
    public void A_file_dated_after_the_clock_within_the_skew_counts_as_touched_now()
    {
        var name = File("s1.jsonl", -TimeSpan.FromMinutes(5));

        Problem(ClaudeView().WithIdle(Idle()), name).Should().Contain("archive.windowsIdleDays").And.NotContain("after this machine's clock");
    }

    [Fact]
    public void The_window_and_the_skew_are_settings()
    {
        var name = File("s1.jsonl", TimeSpan.FromDays(8));
        var future = File("s2.jsonl", -TimeSpan.FromMinutes(30));

        Problem(ClaudeView().WithIdle(Idle("\"windowsIdleDays\": 10")), name).Should().Contain("10 days");
        Problem(ClaudeView().WithIdle(Idle("\"clockSkewMinutes\": 60")), future).Should().NotContain("after this machine's clock");
    }

    [Fact]
    public void A_time_that_cannot_be_read_keeps_the_session()
    {
        var unreadable = new WindowsIdle(static _ => new FileSizeResult.Unreadable("access denied"), _clock, TimeSpan.FromDays(7), TimeSpan.FromMinutes(10));

        Problem(ClaudeView().WithIdle(unreadable), "s1.jsonl").Should().Contain("could not be read");
    }

    /// <summary>The own review, finding 1: the REAL file system said "missing" for a file it could not stat (access denied), so an
    /// unreadable recent file beside old ones read idle. Linux legs: a folder this account may not search (the Windows ACL equivalent
    /// needs another account to set up).</summary>
    [Fact]
    public void The_real_file_system_answers_unreadable_not_missing_for_a_file_it_may_not_stat()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "a folder this account may not search is set up with a Unix mode: the Linux legs");
        var name = File("locked/s1.jsonl", TimeSpan.FromDays(40));

        var stat = OperatingSystem.IsWindows() ? new FileSizeResult.Missing() : StatBehindALockedFolder(name);

        stat.Should().BeOfType<FileSizeResult.Unreadable>();
    }

    [System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
    private FileSizeResult StatBehindALockedFolder(string name)
    {
        var locked = Path.Combine(_root, Under, "locked");
        System.IO.File.SetUnixFileMode(locked, UnixFileMode.None);
        try
        {
            using var sandbox = new LinuxSandbox("idle-stat");
            return sandbox.Files.FileSize(Path.Combine(_root, Under, name));
        }
        finally
        {
            System.IO.File.SetUnixFileMode(locked, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    /// <summary>The own review, finding 5: a unit none of whose names exists has nothing an agent could be using — it is idle (phase 2's
    /// resume of an entry whose files are all gone closes instead of waiting forever). A missing name beside an old file is skipped.</summary>
    [Fact]
    public void Missing_names_are_skipped_and_a_unit_with_none_left_is_idle()
    {
        var old = File("s1.jsonl", TimeSpan.FromDays(40));

        Problem(ClaudeView().WithIdle(Idle()), "gone.jsonl", old).Should().BeEmpty();
        Problem(ClaudeView().WithIdle(Idle()), "gone.jsonl").Should().BeEmpty();
    }

    /// <summary>The plan's RED list: past the commit point the files carry their quarantine names — one touched recently keeps the entry.</summary>
    [Fact]
    public void A_quarantine_name_touched_recently_keeps_a_resumed_entry()
    {
        var quarantined = File($"s1.jsonl{ArchiveNames.QuarantineMark}r7", TimeSpan.FromDays(1));

        Problem(ClaudeView().WithIdle(Idle()), "s1.jsonl", quarantined).Should().Contain("archive.windowsIdleDays");
    }

    /// <summary>The own review, finding 4: the tolerance must change the DECISION, not only the message — a session is idle only past
    /// the window AND the tolerance, so a clock a few minutes ahead does not let a just-in-window session go.</summary>
    [Fact]
    public void The_idle_window_is_measured_with_the_clock_tolerance()
    {
        var almost = File("s1.jsonl", TimeSpan.FromDays(7) + TimeSpan.FromMinutes(5));
        var past = File("s2.jsonl", TimeSpan.FromDays(7) + TimeSpan.FromMinutes(15));

        Problem(ClaudeView().WithIdle(Idle()), almost).Should().Contain("archive.windowsIdleDays");
        Problem(ClaudeView().WithIdle(Idle()), past).Should().BeEmpty();
    }

    [Fact]
    public void A_held_idle_session_is_still_kept_by_the_restart_managers_answer()
    {
        var name = File("s1.jsonl", TimeSpan.FromDays(8));

        Problem(ClaudeView(new GivenAnswers(new RmAnswer.Held(["a process (pid 9)"]))).WithIdle(Idle()), name).Should().Contain("pid 9");
    }

    /// <summary>The times are read at the QUESTION, not when the view was built: phase 2 asks a day after the selection.</summary>
    [Fact]
    public void The_times_are_read_at_each_question()
    {
        var name = File("s1.jsonl", TimeSpan.FromDays(8));
        var view = ClaudeView().WithIdle(Idle());
        Problem(view, name).Should().BeEmpty();

        System.IO.File.SetLastWriteTimeUtc(Path.Combine(_root, Under, name), Now.AddHours(-1).UtcDateTime);

        Problem(view, name).Should().Contain("archive.windowsIdleDays");
    }

    /// <summary>A view that says Claude runs but was never given the idle rule keeps every Claude Code session — the default fails closed.</summary>
    [Fact]
    public void A_view_without_the_idle_rule_keeps_every_claude_session()
    {
        var name = File("s1.jsonl", TimeSpan.FromDays(400));

        Problem(ClaudeView(), name).Should().Contain("Claude Code runs on Windows").And.Contain("not judged");
    }

    /// <summary>The code round, finding 2: a profile on a redirected share can hang a stat — the idle question is asked like every Windows
    /// question, within <c>archive.inUseScanSeconds</c> and the caller's budget, and a question that does not answer keeps the unit.</summary>
    [Fact]
    public void A_stat_that_does_not_answer_keeps_the_session_within_the_ceiling()
    {
        using var never = new ManualResetEventSlim();
        var hanging = new WindowsIdle(_ => { never.Wait(TimeSpan.FromSeconds(20)); return new FileSizeResult.Missing(); }, _clock, TimeSpan.FromDays(7), TimeSpan.FromMinutes(10));
        var view = InUseWindows.View(new GivenAnswers(new RmAnswer.Free()), new GivenTable([new WindowsProcessEntry(40, 4, "claude.exe")], string.Empty), TimeSpan.FromMilliseconds(300));

        var started = DateTime.UtcNow;
        var answer = Problem(view.WithIdle(hanging), "s1.jsonl");
        never.Set();

        (DateTime.UtcNow - started).Should().BeLessThan(TimeSpan.FromSeconds(10));
        answer.Should().Contain("did not answer within archive.inUseScanSeconds");
    }

    [Fact]
    public void Another_agents_unit_is_never_asked_about_idleness()
    {
        var view = ClaudeView().WithIdle(new WindowsIdle(static _ => throw new InvalidOperationException("asked"), _clock, TimeSpan.FromDays(7), TimeSpan.FromMinutes(10)));

        Liveness.Problem(view, p => p, "codex", _root, "2026/08/01/rollout.jsonl", ["2026/08/01/rollout.jsonl"]).Should().BeEmpty();
    }

    [Fact]
    public void The_two_keys_have_their_defaults_and_ranges()
    {
        var config = Config();

        config.Int(ConfigKeys.Archive.WindowsIdleDays).Should().Be(7);
        config.Int(ConfigKeys.Archive.ClockSkewMinutes).Should().Be(10);
        (ConfigKeys.Archive.WindowsIdleDays.Min, ConfigKeys.Archive.WindowsIdleDays.Max).Should().Be((1, 365));
        (ConfigKeys.Archive.ClockSkewMinutes.Min, ConfigKeys.Archive.ClockSkewMinutes.Max).Should().Be((0, 1440));
    }
}
