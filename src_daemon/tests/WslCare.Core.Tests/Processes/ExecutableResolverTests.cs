using FluentAssertions;

using WslCare.Core.Processes;
using WslCare.TestSupport;

namespace WslCare.Core.Tests.Processes;

/// <summary>
/// The product decides which file a bare tool name means, on <c>PATH</c> alone (CI run 37045304356: GitHub's
/// <c>System32\docker.exe</c> won over the scenario's fake because the operating system searched before PATH).
/// Real files in a temporary root; the Windows rules are also checked on Linux, since they are about names.
/// </summary>
public sealed class ExecutableResolverTests : IDisposable
{
    private static readonly bool Windows = OperatingSystem.IsWindows();
    private readonly TempRoot _root = new("resolver");

    public void Dispose() => _root.Dispose();

    /// <summary>A file the platform would start, named <paramref name="tool"/> (with <c>.exe</c> on Windows).</summary>
    private string Tool(string folder, string tool, string extension = "")
    {
        var name = tool + (extension.Length > 0 ? extension : Windows ? ".exe" : string.Empty);
        var path = _root.File($"{folder}/{name}", "not really a program");
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        return path;
    }

    private string PathOf(params string[] folders) => string.Join(Path.PathSeparator, folders.Select(f => f.Length == 0 ? f : _root.Under(f)));

    [Fact]
    public void A_bare_name_resolves_to_the_full_path_of_the_first_path_entry_that_holds_it()
    {
        Tool("second", "wc-tool");
        var expected = Tool("first", "wc-tool");

        var resolved = ExecutableResolver.Resolve("wc-tool", PathOf("empty-dir-not-created", "first", "second"), Windows);

        resolved.Should().Be(new ResolvedExecutable.Found(expected));
    }

    [Fact]
    public void A_name_on_no_path_entry_is_not_found_with_a_reason_naming_it_and_the_search()
    {
        Tool("elsewhere", "wc-tool");

        var resolved = ExecutableResolver.Resolve("wc-tool", PathOf("first", "second"), Windows);

        var reason = resolved.Should().BeOfType<ResolvedExecutable.NotFound>().Which.Reason;
        reason.Should().Contain("wc-tool").And.Contain("PATH").And.Contain("2 directories searched");
    }

    [Fact]
    public void A_copy_reachable_only_through_the_current_directory_is_never_chosen()
    {
        // "." and a RELATIVE entry both mean "wherever this process happens to stand" — the very lookup the CI run
        // fell into. The decoy is reachable through both; the absolute entry's copy is the only answer.
        var decoyFolder = _root.Dir("decoy");
        Tool("decoy", "wc-tool");
        var relative = Path.GetRelativePath(Directory.GetCurrentDirectory(), decoyFolder);
        // Another drive has no relative spelling (GetRelativePath answers the absolute path): then "." alone stands for it.
        string[] cwdEntries = Path.IsPathFullyQualified(relative) ? ["."] : [".", relative];
        var real = Tool("real", "wc-tool");

        ExecutableResolver.Resolve("wc-tool", string.Join(Path.PathSeparator, cwdEntries), Windows)
            .Should().BeOfType<ResolvedExecutable.NotFound>().Which.Reason.Should().Contain("no absolute directory");
        ExecutableResolver.Resolve("wc-tool", string.Join(Path.PathSeparator, [.. cwdEntries, _root.Under("real")]), Windows)
            .Should().Be(new ResolvedExecutable.Found(real));
    }

    [Fact]
    public void Empty_path_entries_are_skipped_rather_than_read_as_the_current_directory()
    {
        var real = Tool("real", "wc-tool");

        var resolved = ExecutableResolver.Resolve("wc-tool", PathOf(string.Empty, "real", string.Empty), Windows);

        resolved.Should().Be(new ResolvedExecutable.Found(real));
        ExecutableResolver.Resolve("wc-tool", $"{Path.PathSeparator}{Path.PathSeparator}", Windows).Should().BeOfType<ResolvedExecutable.NotFound>();
        ExecutableResolver.Resolve("wc-tool", null, Windows).Should().BeOfType<ResolvedExecutable.NotFound>();
    }

    [Fact]
    public void An_absolute_path_passes_through_unchanged_and_a_relative_one_is_refused()
    {
        var absolute = Path.Combine(_root.Path, "anything-at-all");

        ExecutableResolver.Resolve(absolute, PathOf("first"), Windows).Should().Be(new ResolvedExecutable.Found(absolute));
        ExecutableResolver.Resolve("./wc-tool", PathOf("first"), Windows).Should().BeOfType<ResolvedExecutable.NotFound>().Which.Reason.Should().Contain("relative path");
        ExecutableResolver.Resolve("bin\\wc-tool", PathOf("first"), windows: true).Should().BeOfType<ResolvedExecutable.NotFound>();
    }

    [Fact]
    public void On_windows_only_exe_and_com_are_candidates_never_a_cmd_or_bat_that_would_need_a_shell()
    {
        Tool("scripts", "wc-tool", ".cmd");
        Tool("scripts", "wc-other", ".bat");
        var com = Tool("com", "wc-com", ".com");

        ExecutableResolver.Resolve("wc-tool", PathOf("scripts"), windows: true).Should().BeOfType<ResolvedExecutable.NotFound>();
        ExecutableResolver.Resolve("wc-tool.cmd", PathOf("scripts"), windows: true).Should().BeOfType<ResolvedExecutable.NotFound>();
        ExecutableResolver.Resolve("wc-other", PathOf("scripts"), windows: true).Should().BeOfType<ResolvedExecutable.NotFound>();
        ExecutableResolver.Resolve("wc-com", PathOf("com"), windows: true).Should().Be(new ResolvedExecutable.Found(com));
    }

    [Fact]
    public void A_name_that_already_carries_exe_is_looked_up_as_spelled_on_windows()
    {
        var exe = Tool("bin", "wc-tool", ".exe");

        ExecutableResolver.Resolve("wc-tool.exe", PathOf("bin"), windows: true).Should().Be(new ResolvedExecutable.Found(exe));
    }

    [Fact]
    public void On_linux_a_file_without_an_execute_bit_is_skipped_for_the_next_entry()
    {
        Assert.SkipWhen(Windows, "the execute bit is a Linux rule; Windows starts any .exe");
        var plain = _root.File("plain/wc-tool", "not executable");
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(plain, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }

        var real = Tool("real", "wc-tool");

        ExecutableResolver.Resolve("wc-tool", PathOf("plain", "real"), windows: false).Should().Be(new ResolvedExecutable.Found(real));
    }

    [Fact]
    public async Task The_runner_reports_a_name_found_on_no_path_entry_as_a_failure_to_start_that_says_path_was_searched()
    {
        var outcome = await new ProcessCommandRunner(new AllowAllCommandPolicy()).RunAsync(new CommandRequest(["wsl-care-no-such-binary-7f3a"], TimeSpan.FromSeconds(5)), CancellationToken.None);

        outcome.Should().BeOfType<CommandOutcome.FailedToStart>().Which.Reason.Should().Contain("wsl-care-no-such-binary-7f3a").And.Contain("PATH");
    }
}
