using System.Diagnostics;

using FluentAssertions;

using WslCare.Core.Processes;
using WslCare.Core.Processes.Policy;
using WslCare.Core.Worktrees;
using WslCare.TestSupport;

namespace WslCare.Core.Tests.Worktrees;

/// <summary>
/// E19.S4 (coai plan round, 2026-10-21): when <c>worktrees.gitTimeoutSeconds</c> runs out, the shared launcher kills git AND everything git started —
/// not only the process it launched. The fake <c>git.exe</c> here is a batch file whose only job is to run a long-lived child (a uniquely named copy of
/// <c>ping.exe</c>, so the child is found by name and by nothing another test runs) and hang beside it; it is started by the product runner under the
/// product policy, through the resolver seam, exactly as the real one is.
/// </summary>
/// <remarks>The first version had the batch start the child through PowerShell and wait for a pid file: on the GitHub Windows runner PowerShell's cold start
/// outlasted the 8 s ceiling, the kill came before the child existed and the pid file never appeared — a race in the TEST, not a gap in the launcher. The
/// child is now a direct child of the batch, the ceiling is generous, and the test proves the child existed (it waits for it while the call is pending) before
/// it asserts the child is gone.</remarks>
public sealed class GitTimeoutTests
{
    private static readonly TimeSpan Ceiling = TimeSpan.FromSeconds(20);

    [Fact]
    public async Task A_git_that_hangs_with_a_child_is_killed_with_its_whole_tree_before_the_call_returns()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "a batch file stands in for git.exe: the Windows legs");
        using var folder = new ThrowAwayFolder("git-timeout");
        var childName = "pinger-" + Guid.NewGuid().ToString("N");
        File.Copy(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "ping.exe"), folder.Under(childName + ".exe"));
        var fake = folder.Write("fakegit.cmd", $"@echo off\r\n\"{folder.Under(childName + ".exe")}\" -n 90 127.0.0.1 >nul\r\n");
        var runner = ProcessCommandRunner.WithResolverForItsOwnTests(CommandPolicy.Product, (_, _) => new ResolvedExecutable.Found(fake));
        var questions = new GitQuestions(runner, new GitLimits(Ceiling, 64, 7000), name => GitEnvironment.ThisProcess(name), _ => true);
        var question = new GitQuestion(folder.Path, ["bin"], folder.Parent, new GitFolder.Repository(folder.Path));

        var watch = Stopwatch.StartNew();
        var pending = Task.Run(() => questions.AskAsync(question, CancellationToken.None), TestContext.Current.CancellationToken);
        var childPid = await WaitForChildAsync(childName, TimeSpan.FromSeconds(15));
        childPid.Should().BeGreaterThan(0, $"the fake git must have started its child {childName} before the ceiling could fire, or this proves nothing");
        var answers = await pending;
        watch.Stop();

        answers.Single().Verdict.Should().Be(new GitVerdict.Retained("git did not answer within 20 s (worktrees.gitTimeoutSeconds)"));
        watch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(60), "the call ended at its ceiling, not at the child's own 90 s");
        // The tree kill has returned; the kernel finishes ending the process asynchronously, so allow it a moment — never more than the launcher's own grace.
        for (var attempt = 0; attempt < 20 && IsAlive(childPid); attempt++)
        {
            await Task.Delay(100, TestContext.Current.CancellationToken);
        }

        IsAlive(childPid).Should().BeFalse($"the child {childPid} of the hung git must be dead: the launcher kills the whole tree, not only the process it started");
    }

    /// <summary>The pid of the one process of that unique name, once it exists; 0 when it never did within the bound.</summary>
    private static async Task<int> WaitForChildAsync(string name, TimeSpan within)
    {
        var until = Stopwatch.StartNew();
        while (until.Elapsed < within)
        {
            var found = Process.GetProcessesByName(name);
            if (found.Length > 0)
            {
                var pid = found[0].Id;
                foreach (var process in found)
                {
                    process.Dispose();
                }

                return pid;
            }

            await Task.Delay(100, TestContext.Current.CancellationToken);
        }

        return 0;
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
