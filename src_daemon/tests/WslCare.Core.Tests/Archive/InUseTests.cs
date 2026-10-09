using FluentAssertions;

using WslCare.Core.Agents;
using WslCare.Core.Archive;
using WslCare.Core.Config;
using WslCare.Core.Files;
using WslCare.Core.Tests.Agents;
using WslCare.TestSupport;

namespace WslCare.Core.Tests.Archive;

/// <summary>
/// Plan §15r D2.2 (E9.S1) — the open-file scan over a <c>/proc</c> in a sandbox: each process's <c>fd/*</c> read as links (never
/// followed — the targets here do not even exist), and a Claude Code process's <c>cwd</c> named as its project folder; and the
/// selection's own syscalls: no session file opened (inotify, Linux).
/// </summary>
public sealed class InUseTests : IDisposable
{
    private const string LinuxOnly = "symbolic links in a sandboxed /proc and inotify are the Linux kernel's: the Linux legs";

    private readonly LinuxSandbox _sandbox = new("archive-in-use");

    public void Dispose() => _sandbox.Dispose();

    private void Process(int pid, string cmdline, string cwd, params string[] open)
    {
        _sandbox.Write($"/proc/{pid}/cmdline", cmdline);
        var fd = _sandbox.Paths.DistroPath($"/proc/{pid}/fd");
        Directory.CreateDirectory(fd);
        for (var i = 0; i < open.Length; i++)
        {
            File.CreateSymbolicLink(Path.Combine(fd, (i + 3).ToString(System.Globalization.CultureInfo.InvariantCulture)), open[i]);
        }

        File.CreateSymbolicLink(_sandbox.Paths.DistroPath($"/proc/{pid}/cwd"), cwd);
    }

    [Fact]
    public void The_scan_reads_the_open_files_and_the_projects_claude_code_works_in()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), LinuxOnly);
        Process(42, "claude\0--resume\0", "/home/me/git/x", "/home/me/.claude/projects/p/s1.jsonl", "socket:[123]");
        Process(43, "bash\0", "/home/me/git/y", "/home/me/notes.txt");

        var seen = InUse.Scan(_sandbox.Paths, _sandbox.Files, CancellationToken.None, UncheckedWindowsSide.NotWindows);

        seen.State.Should().Be(InUseState.Complete);
        seen.OpenFiles.Should().Contain(["/home/me/.claude/projects/p/s1.jsonl", "/home/me/notes.txt"]);
        seen.ClaudeProjects.Should().Equal("-home-me-git-x");
        seen.Note.Should().BeEmpty();
    }

    /// <summary>E9.S5: on the Windows side the scan is the Windows side's view — the Restart Manager asked per unit — never a set read
    /// from /proc.</summary>
    [Fact]
    public void On_windows_the_scan_is_the_windows_sides_view()
    {
        var paths = new WslCare.Core.Hosting.WindowsHostPaths(new WslCare.Core.Hosting.WindowsEnvironment(@"C:\Users\me", @"C:\Users\me\AppData\Roaming", @"C:\Users\me\AppData\Local", @"C:\ProgramData", @"C:\Users\me\AppData\Local\Temp"));
        var asked = InUseView.Complete(new HashSet<string>(StringComparer.Ordinal), new HashSet<string>(StringComparer.OrdinalIgnoreCase)) with { HeldBy = _ => "held" };

        var seen = InUse.Scan(paths, _sandbox.Files, CancellationToken.None, new GivenSide(asked));

        seen.Should().BeSameAs(asked);
    }

    private sealed class GivenSide(InUseView view) : IWindowsSide
    {
        public InUseView View(TimeSpan ceiling) => view;
    }

    /// <summary>Plan §15q H3 carried to the archive: the selection lists names and stats entries — not one session file is opened,
    /// companions included (the retention setting is the one file it may read, and this tree has none).</summary>
    [Fact]
    public void The_selection_opens_no_file_of_an_agent()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), LinuxOnly);
        var old = DateTimeOffset.UtcNow.AddDays(-40);
        _sandbox.Sized("/home/me/.claude/projects/p/s1.jsonl", 100, old);
        _sandbox.Sized("/home/me/.claude/projects/p/s1/subagents/agent-a.jsonl", 30, old);
        _sandbox.Sized("/home/me/.claude/file-history/s1/v1", 30, old);
        var root = _sandbox.Paths.DistroPath("/home/me/.claude");
        using var watch = InotifyWatch.Over(root);
        var config = ConfigLoader.Load([(ConfigLoader.DefaultsFile, new FileReadResult.Content(ConfigLoader.EmbeddedDefaults()))]).Config;

        var claude = Selection.Select(new SelectionInput(_sandbox.Paths, _sandbox.Files, config, DateTimeOffset.UtcNow, TimeZoneInfo.Utc, InUseView.Complete(new HashSet<string>(StringComparer.Ordinal), new HashSet<string>(StringComparer.OrdinalIgnoreCase)), _ => null))
            .Single(s => s.Entry.Id == "claude-code");

        claude.Due.Should().ContainSingle().Which.Files.Should().HaveCount(3, "the selection did list the session and its companions");
        watch.FileEvents().Should().NotContain(e => !e.EndsWith("settings.json", StringComparison.Ordinal), "a session is found by listings and stats — no file of it is opened (plan §15r E9.S1)");
    }
}
