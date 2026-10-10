using FluentAssertions;

using WslCare.Core.Archive;
using WslCare.Core.Files;
using WslCare.Core.Mcp;
using WslCare.TestSupport;

namespace WslCare.Core.Tests.Archive;

/// <summary>
/// Plan §15r, *Two fail-closed guards after the live gate* (owner questions 1 and 2, decided 2026-10-10). G1: a Claude Code session
/// whose id is on a live Claude Code command line is kept — whatever the idle rule says. G2: an archived copy a share may answer from
/// the Offline Files cache is not trusted (the pure answer here; phase 2 in <see cref="ArchiveRunTests"/>).
/// </summary>
public sealed class ArchiveGuardsTests : IDisposable
{
    private const string Session = "0f8fad5b-d9cb-469f-a165-70867728950e";
    private const string Key = $"projects/p/{Session}.jsonl";

    private readonly LinuxSandbox _sandbox = new("archive-guards");

    public void Dispose() => _sandbox.Dispose();

    private static InUseView WindowsView(string commandLine, string exe = "claude.exe") =>
        InUseWindows.View(new GivenAnswers(new RmAnswer.Free()), new GivenTable([new WindowsProcessEntry(40, 4, exe)], commandLine), TimeSpan.FromSeconds(5))
            with
        { ClaudeIdle = static _ => string.Empty };

    private static string Problem(InUseView view, string key = Key) => Liveness.Problem(view, p => p, "claude-code", @"C:\Users\me\.claude", key, [key]);

    // ---- G1 ----

    [Theory]
    [InlineData($"claude --resume {Session}")]
    [InlineData($"claude -r {Session}")]
    [InlineData($"claude --session-id={Session}")]
    [InlineData($"\"C:\\Program Files\\nodejs\\node.exe\" claude-code\\cli.js --resume {Session}")]
    public void A_session_on_a_live_claude_command_line_is_kept_even_when_idle(string commandLine)
    {
        var exe = commandLine.Contains("node.exe", StringComparison.Ordinal) ? "node.exe" : "claude.exe";

        Problem(WindowsView(commandLine, exe)).Should().Contain("on its command line").And.Contain("pid 40");
    }

    [Fact]
    public void The_id_is_matched_whatever_its_case()
    {
        Problem(WindowsView($"claude --resume {Session.ToUpperInvariant()}")).Should().Contain("on its command line");
    }

    [Fact]
    public void Another_session_or_another_programs_command_line_keeps_nothing_by_this_rule()
    {
        Problem(WindowsView("claude --resume 11111111-2222-3333-4444-555555555555")).Should().BeEmpty("the idle rule let it through");
        Problem(WindowsView($"notepad.exe {Session}.jsonl", "notepad.exe")).Should().BeEmpty("only a Claude Code process counts");
    }

    [Fact]
    public void The_distro_scan_keeps_a_session_on_a_claude_command_line_too()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "a sandboxed /proc is the Linux kernel's: the Linux legs");
        _sandbox.Write("/proc/42/cmdline", $"claude\0--resume\0{Session}\0");
        Directory.CreateDirectory(_sandbox.Paths.DistroPath("/proc/42/fd"));
        File.CreateSymbolicLink(_sandbox.Paths.DistroPath("/proc/42/cwd"), "/home/me/git/elsewhere");

        var seen = InUse.Scan(_sandbox.Paths, _sandbox.Files, CancellationToken.None, UncheckedWindowsSide.NotWindows);

        seen.ClaudeOnCommandLine(Key).Should().Contain("on its command line").And.Contain("pid 42");
        seen.ClaudeOnCommandLine("projects/p/11111111-2222-3333-4444-555555555555.jsonl").Should().BeEmpty();
    }

    // ---- G2 ----

    [Theory]
    [InlineData(0x10u, 0, true, "")]
    [InlineData(0x12u, 0, true, "Offline Files")]
    [InlineData(0x0u, 87, true, "could not be asked")]
    [InlineData(0x2u, 0, false, "")]
    [InlineData(0x0u, 87, false, "")]
    public void A_share_that_may_answer_from_the_offline_cache_is_not_trusted(uint flags, int error, bool remote, string named)
    {
        var problem = NetworkPaths.OfflineCacheProblem(flags, error, remote);

        if (named.Length == 0)
        {
            problem.Should().BeEmpty();
        }
        else
        {
            problem.Should().Contain(named);
        }
    }
}
