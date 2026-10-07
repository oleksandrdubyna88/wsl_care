using FluentAssertions;

using WslCare.Cli;
using WslCare.Core.Hosting;
using WslCare.TestSupport;

namespace WslCare.Cli.Tests;

/// <summary>
/// Plan §15r D6, E9.S3 — <c>archive restore</c> and <c>archive list</c> as the command line takes them: exactly one way to name what is
/// restored (entry ids of 16 hex; an agent with a month <c>yyyy-MM</c>; an agent with a plain relative session path), the list's
/// optional filters checked, root refused with its own exit code (81) and nothing touched.
/// </summary>
public sealed class ArchiveRestoreCommandTests : IDisposable
{
    private readonly LinuxSandbox _sandbox = new("archive-restore-cmd");

    public void Dispose() => _sandbox.Dispose();

    private CliHost Host(bool root = false) =>
        new CliHost(_sandbox.Paths, _sandbox.Files, new FixedTimeProvider(), new RecordingCommandRunner())
        {
            Privilege = new ProcessPrivilege(root, "a test says so"),
        };

    [Theory]
    [InlineData("archive", "restore")]
    [InlineData("archive", "restore", "--entry", "0123456789abcdef", "--agent", "claude-code", "--month", "2026-09")]
    [InlineData("archive", "restore", "--entry", "NOT-HEX")]
    [InlineData("archive", "restore", "--entry", "0123456789abcdef,zz")]
    [InlineData("archive", "restore", "--month", "2026-09")]
    [InlineData("archive", "restore", "--agent", "claude-code", "--month", "2026-13")]
    [InlineData("archive", "restore", "--agent", "claude-code", "--month", "26-09")]
    [InlineData("archive", "restore", "--agent", "no-such-agent", "--month", "2026-09")]
    [InlineData("archive", "restore", "--agent", "claude-code", "--session", "../outside.jsonl")]
    [InlineData("archive", "restore", "--agent", "claude-code", "--session", "/home/me/.claude/projects/p/s.jsonl")]
    [InlineData("archive", "restore", "--agent", "claude-code", "--month", "2026-09", "--accept-unverified")]
    [InlineData("archive", "restore", "--agent", "claude-code", "--session", "projects/p/s.jsonl", "--accept-unverified")]
    [InlineData("archive", "list", "--month", "september")]
    [InlineData("archive", "list", "--run", "not-a-run")]
    [InlineData("archive", "list", "--agent", "a,b")]
    public void A_restore_or_list_named_wrongly_is_a_usage_error_naming_the_rule(params string[] args)
    {
        var run = CliRun.Over(Host(), args);

        run.Exit.Should().Be((int)ExitCode.Usage, run.Stdout);
        run.Stderr.Should().Contain($"archive {args[1]}");
    }

    [Theory]
    [InlineData("restore", "--entry", "0123456789abcdef")]
    [InlineData("list")]
    public void As_root_restore_and_list_are_refused_with_their_own_exit_code(params string[] args)
    {
        var run = CliRun.Over(Host(root: true), ["archive", .. args]);

        run.Exit.Should().Be((int)ExitCode.NotAsRoot);
        run.Stderr.Should().Contain("not as uid 0");
    }

    [Fact]
    public void Without_a_base_restore_and_list_answer_no_base_and_succeed()
    {
        var restore = CliRun.Over(Host(), "archive", "restore", "--agent", "claude-code", "--month", "2026-09", "--json");
        var list = CliRun.Over(Host(), "archive", "list", "--json");

        restore.Exit.Should().Be((int)ExitCode.Ok, restore.Stderr);
        restore.Stdout.Should().Contain("\"outcome\":\"no-base\"");
        list.Exit.Should().Be((int)ExitCode.Ok, list.Stderr);
        list.Stdout.Should().Contain("\"outcome\": \"no-base\"");
    }

    /// <summary>E9.S3 own review round C-9: an elevated Windows user IS the user whose sessions move — the refusal tells them to run it
    /// from a terminal that is not elevated, never "not as uid 0 … as that user".</summary>
    [Fact]
    public void The_root_refusal_speaks_the_words_of_its_side()
    {
        Commands.ArchiveRunCommand.RootRefusalFor(HostSide.Windows, "restore").Should().Contain("not elevated").And.NotContain("uid 0");
        Commands.ArchiveRunCommand.RootRefusalFor(HostSide.Wsl, "restore").Should().Contain("not as uid 0");
    }
}
