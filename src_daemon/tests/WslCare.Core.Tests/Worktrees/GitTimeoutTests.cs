using System.Diagnostics;

using FluentAssertions;

using WslCare.Core.Processes;
using WslCare.Core.Processes.Policy;
using WslCare.Core.Worktrees;
using WslCare.TestSupport;

namespace WslCare.Core.Tests.Worktrees;

/// <summary>
/// E19.S4 (coai plan round, 2026-10-21): when <c>worktrees.gitTimeoutSeconds</c> runs out, the shared launcher kills git AND everything git started —
/// not only the process it launched. The fake <c>git.exe</c> here is a batch file that starts a long-lived child and hangs beside it; it is started by
/// the product runner under the product policy, through the resolver seam, exactly as the real one is.
/// </summary>
public sealed class GitTimeoutTests
{
    [Fact]
    public async Task A_git_that_hangs_with_a_child_is_killed_with_its_whole_tree_before_the_call_returns()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "a batch file stands in for git.exe: the Windows legs");
        using var folder = new ThrowAwayFolder("git-timeout");
        var pidFile = folder.Under("child.pid");
        // git runs with NO PATH (its clean environment), so the fake names its programs by full path.
        var system = Environment.GetFolderPath(Environment.SpecialFolder.System);
        var powershell = Path.Combine(system, "WindowsPowerShell", "v1.0", "powershell.exe");
        var ping = Path.Combine(system, "ping.exe");
        var fake = folder.Write(
            "fakegit.cmd",
            "@echo off\r\n"
            + $"\"{powershell}\" -NoProfile -NonInteractive -Command \"$p = Start-Process -FilePath '{ping}' -ArgumentList '-n','90','127.0.0.1' -PassThru -WindowStyle Hidden; Set-Content -Path '{pidFile}' -Value $p.Id; Wait-Process -Id $p.Id\"\r\n");
        var runner = ProcessCommandRunner.WithResolverForItsOwnTests(CommandPolicy.Product, (_, _) => new ResolvedExecutable.Found(fake));
        var questions = new GitQuestions(runner, new GitLimits(TimeSpan.FromSeconds(8), 64, 7000), name => GitEnvironment.ThisProcess(name), _ => true);
        var question = new GitQuestion(folder.Path, ["bin"], folder.Parent, new GitFolder.Repository(folder.Path));

        var watch = Stopwatch.StartNew();
        var answers = await questions.AskAsync(question, CancellationToken.None);
        watch.Stop();

        answers.Single().Verdict.Should().Be(new GitVerdict.Retained("git did not answer within 8 s (worktrees.gitTimeoutSeconds)"));
        watch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(60), "the call ended at its ceiling, not at the child's own 90 s");
        File.Exists(pidFile).Should().BeTrue("the fake git started its child before it hung");
        var child = int.Parse(File.ReadAllText(pidFile).Trim(), System.Globalization.CultureInfo.InvariantCulture);
        // The tree kill has returned; the kernel finishes ending the process asynchronously, so allow it a moment — never more than the launcher's own grace.
        for (var attempt = 0; attempt < 20 && IsAlive(child); attempt++)
        {
            await Task.Delay(100, TestContext.Current.CancellationToken);
        }

        IsAlive(child).Should().BeFalse($"the child {child} of the hung git must be dead: the launcher kills the whole tree, not only the process it started");
    }

    private static bool IsAlive(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }
}
