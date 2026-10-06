using System.Text.Json;

using FluentAssertions;

using WslCare.Cli;
using WslCare.Core.Archive;
using WslCare.Core.Json;
using WslCare.Core.Hosting;
using WslCare.TestSupport;

namespace WslCare.Cli.Tests;

/// <summary>
/// E9.S0 in-process over the distro's layout (plan §15r D1, D7): <c>archive check-base</c> answers the base rules as this user and
/// refuses root (exit 81); <c>config set archive.baseFolder</c> writes the user layer only when the same rules accept the folder.
/// </summary>
public sealed class ArchiveCommandTests : IDisposable
{
    private const string MountInfo =
        "523 504 8:96 / / rw,relatime - ext4 /dev/sdg rw,discard,errors=remount-ro,data=ordered\n" +
        "479 523 0:154 / /mnt/v rw,relatime - 9p V: rw,aname=drvfs;path=V:;uid=1000;gid=1000;metadata;symlinkroot=/mnt/,cache=0x5,access=client,msize=65536,trans=fd,rfd=3,wfd=3\n";

    private readonly LinuxSandbox _sandbox = new("archive-cmd");

    public ArchiveCommandTests() => _sandbox.Write("/proc/self/mountinfo", MountInfo);

    public void Dispose() => _sandbox.Dispose();

    private CliHost Host(bool root = false) =>
        new CliHost(_sandbox.Paths, _sandbox.Files, new FixedTimeProvider(), new RecordingCommandRunner())
        {
            Privilege = new ProcessPrivilege(root, "a test says so"),
        };

    private void Folder(string distro) => Directory.CreateDirectory(_sandbox.Paths.DistroPath(distro));

    [Fact]
    public void Check_base_answers_an_accepted_folder_with_its_mount_as_json()
    {
        Folder("/mnt/v/ai-archive");

        var check = CliRun.Over(Host(), "archive", "check-base", @"V:\ai-archive", "--json");

        check.Exit.Should().Be((int)ExitCode.Ok, check.Stderr);
        var report = JsonSerializer.Deserialize(check.Stdout, WslCareJsonContext.Default.BaseFolderReport)!;
        report.Accepted.Should().BeTrue(report.Refusal);
        report.Folder.Should().Be("/mnt/v/ai-archive");
        report.Mount!.Type.Should().Be("9p");
        check.Stdout.Should().Contain("\"schemaVersion\": 1").And.Contain("\"accepted\": true");
    }

    [Fact]
    public void Check_base_answers_a_refused_folder_with_its_rule_and_creates_nothing()
    {
        var check = CliRun.Over(Host(), "archive", "check-base", "/mnt/v/ai-archive");

        check.Exit.Should().Be((int)ExitCode.Ok, "a refused folder is an answer");
        check.Stdout.Should().StartWith($"refused ({BaseFolderRule.Missing})");
        Directory.Exists(_sandbox.Paths.DistroPath("/mnt/v/ai-archive")).Should().BeFalse();
    }

    [Fact]
    public void Check_base_as_root_is_refused_with_its_own_exit_code()
    {
        Folder("/mnt/v/ai-archive");

        var check = CliRun.Over(Host(root: true), "archive", "check-base", "/mnt/v/ai-archive", "--json");

        check.Exit.Should().Be((int)ExitCode.NotAsRoot);
        check.Stdout.Should().BeEmpty();
        check.Stderr.Should().Contain("not as uid 0");
    }

    [Theory]
    [InlineData("-x")]
    [InlineData("/mnt/v\nx")]
    public void Check_base_refuses_an_argument_that_is_no_path_before_anything_is_looked_at(string path)
    {
        CliRun.Over(Host(), "archive", "check-base", path).Exit.Should().Be((int)ExitCode.Usage);
    }

    /// <summary>§15r D1: the user layer may name the base now — behind the base rules, judged by the user's own process.</summary>
    [Fact]
    public void Config_set_of_the_base_folder_writes_an_accepted_folder_into_the_user_layer()
    {
        Folder("/mnt/v/ai-archive");

        var set = CliRun.Over(Host(), "config", "set", "archive.baseFolder", "/mnt/v/ai-archive");

        set.Exit.Should().Be((int)ExitCode.Ok, set.Stderr);
        File.ReadAllText(_sandbox.Paths.UserConfigFile).Should().Contain("/mnt/v/ai-archive");
    }

    [Fact]
    public void Config_set_of_a_base_the_rules_refuse_names_the_rule_and_writes_nothing()
    {
        Folder("/home/me/.claude/archive");

        var set = CliRun.Over(Host(), "config", "set", "archive.baseFolder", "/home/me/.claude/archive");

        set.Exit.Should().Be((int)ExitCode.Usage);
        set.Stderr.Should().Contain($"refused ({BaseFolderRule.Overlap})").And.Contain("Nothing was written");
        File.Exists(_sandbox.Paths.UserConfigFile).Should().BeFalse();
    }

    /// <summary>E9.S0 review round S2: the base is judged as the process that will write it — a root process would judge it with
    /// root's reach and write root's user layer. Refused (81) before the folder, its mount or any parent is looked at.</summary>
    [Fact]
    public void Config_set_of_the_base_folder_as_root_is_refused_and_nothing_is_looked_at()
    {
        Folder("/mnt/v/ai-archive");

        var set = CliRun.Over(Host(root: true), "config", "set", "archive.baseFolder", "/mnt/v/ai-archive");

        set.Exit.Should().Be((int)ExitCode.NotAsRoot, set.Stderr);
        set.Stderr.Should().Contain("not as uid 0").And.Contain("Nothing was written");
        File.Exists(_sandbox.Paths.UserConfigFile).Should().BeFalse();
    }

    [Fact]
    public void Config_set_of_an_empty_base_folder_clears_it_without_a_judgement()
    {
        var set = CliRun.Over(Host(), "config", "set", "archive.baseFolder", "");

        set.Exit.Should().Be((int)ExitCode.Ok, set.Stderr);
    }

    /// <summary>E9.S1: the preview answers per agent what is due, oldest first, as this user — the fixed clock of the host is the
    /// "now" the ages are taken against.</summary>
    [Fact]
    public void Preview_answers_the_due_sessions_per_agent_as_json()
    {
        var now = new FixedTimeProvider().GetUtcNow();
        _sandbox.Sized("/home/me/.claude/projects/p/old.jsonl", 100, now.AddDays(-20));
        _sandbox.Sized("/home/me/.claude/projects/p/new.jsonl", 100, now.AddDays(-2));

        var preview = CliRun.Over(Host(), "archive", "preview", "--json");

        preview.Exit.Should().Be((int)ExitCode.Ok, preview.Stderr);
        var report = JsonSerializer.Deserialize(preview.Stdout, WslCareJsonContext.Default.ArchivePreviewReport)!;
        var claude = report.Agents.Single(a => a.Id == "claude-code");
        claude.DueUnits.Should().Be(1);
        claude.Units.Should().ContainSingle().Which.Key.Should().Be("projects/p/old.jsonl");
        claude.Younger.Should().Be(1);
        claude.EffectiveAgeDays.Should().Be(14);
        report.Agents.Select(a => a.Id).Should().Equal("claude-code", "codex", "gemini-cli", "antigravity");
        report.SideFolder.Should().StartWith("wsl-");
    }

    [Fact]
    public void Preview_of_one_agent_answers_that_agent_and_an_unknown_one_is_refused()
    {
        var one = CliRun.Over(Host(), "archive", "preview", "--agent", "codex", "--json");
        var unknown = CliRun.Over(Host(), "archive", "preview", "--agent", "copilot-cli");

        JsonSerializer.Deserialize(one.Stdout, WslCareJsonContext.Default.ArchivePreviewReport)!.Agents.Should().ContainSingle().Which.Id.Should().Be("codex");
        unknown.Exit.Should().Be((int)ExitCode.Usage);
        unknown.Stderr.Should().Contain("claude-code");
    }

    [Fact]
    public void Preview_as_root_is_refused_with_its_own_exit_code()
    {
        var preview = CliRun.Over(Host(root: true), "archive", "preview", "--json");

        preview.Exit.Should().Be((int)ExitCode.NotAsRoot);
        preview.Stdout.Should().BeEmpty();
    }
}
