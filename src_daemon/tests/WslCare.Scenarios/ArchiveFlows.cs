using System.Text.Json;

using FluentAssertions;

using WslCare.Cli;
using WslCare.Core.Archive;
using WslCare.Core.Hosting;
using WslCare.Core.Json;
using WslCare.TestSupport;

namespace WslCare.Scenarios;

/// <summary>
/// E9.S0 end to end over the BUILT CLI (plan §15r D1, D7): <c>archive check-base</c> places a Windows drive path at its drvfs mount
/// and judges it as this user; it refuses root with its own exit code; <c>config set archive.baseFolder</c> writes the user layer
/// behind the same rules — an accepted folder written, a folder inside an agent's refused with its rule.
/// </summary>
public sealed class ArchiveFlows
{
    private const string LinuxOnly = "the distribution's mount table places a Windows drive path: the Linux legs (the Windows side has its own flow)";

    /// <summary>The owner's network drive as WSL 2.7.10 mounted it (2026-10-04).</summary>
    private const string MountInfo =
        "523 504 8:96 / / rw,relatime - ext4 /dev/sdg rw,discard,errors=remount-ro,data=ordered\n" +
        "479 523 0:154 / /mnt/v rw,relatime - 9p V: rw,aname=drvfs;path=V:;uid=1000;gid=1000;metadata;symlinkroot=/mnt/,cache=0x5,access=client,msize=65536,trans=fd,rfd=3,wfd=3\n";

    private static void Distro(ScenarioHome home, params string[] folders)
    {
        var paths = (LinuxHostPaths)home.Paths;
        var mountInfo = paths.DistroPath("/proc/self/mountinfo");
        Directory.CreateDirectory(Path.GetDirectoryName(mountInfo)!);
        File.WriteAllText(mountInfo, MountInfo);
        foreach (var folder in folders)
        {
            Directory.CreateDirectory(paths.DistroPath(folder));
        }
    }

    private static BaseFolderReport Report(ChildResult result) =>
        JsonSerializer.Deserialize(result.Stdout, WslCareJsonContext.Default.BaseFolderReport) ?? throw new InvalidOperationException($"check-base printed null: {result.Stderr}");

    [Fact]
    public async Task Check_base_places_a_windows_drive_path_at_its_mount_and_accepts_the_folder()
    {
        using var home = new ScenarioHome("archive-check");
        Assert.SkipWhen(home.Paths.Side == HostSide.Windows, LinuxOnly);
        Distro(home, "/mnt/v/ai-archive");

        var result = await home.RunAsync("archive", "check-base", @"V:\ai-archive", "--json");

        result.Exit.Should().Be((int)ExitCode.Ok, result.Stderr);
        var report = Report(result);
        report.Accepted.Should().BeTrue(report.Refusal);
        report.Folder.Should().Be("/mnt/v/ai-archive");
        report.Mount!.MountPoint.Should().Be("/mnt/v");
    }

    [Fact]
    public async Task Check_base_as_root_is_refused_with_its_own_exit_code()
    {
        using var home = new ScenarioHome("archive-root") { ClaimsRoot = true };

        var result = await home.RunAsync("archive", "check-base", "/mnt/v/ai-archive", "--json");

        result.Exit.Should().Be((int)ExitCode.NotAsRoot);
        result.Stdout.Should().BeEmpty();
        CliStderr.Of(result).Messages.Should().ContainSingle().Which.Should().Contain("not as uid 0");
    }

    [Fact]
    public async Task Config_set_writes_an_accepted_base_and_refuses_one_inside_an_agents_folder()
    {
        using var home = new ScenarioHome("archive-config");
        Assert.SkipWhen(home.Paths.Side == HostSide.Windows, LinuxOnly);
        Distro(home, "/mnt/v/ai-archive", "/home/me/.claude/archive");

        var refused = await home.RunAsync("config", "set", "archive.baseFolder", "/home/me/.claude/archive");
        var written = await home.RunAsync("config", "set", "archive.baseFolder", "/mnt/v/ai-archive");

        refused.Exit.Should().Be((int)ExitCode.Usage);
        CliStderr.Of(refused).Messages.Should().ContainSingle().Which.Should().Contain($"refused ({BaseFolderRule.Overlap})");
        written.Exit.Should().Be((int)ExitCode.Ok, written.Stderr);
        File.ReadAllText(home.Paths.UserConfigFile).Should().Contain("/mnt/v/ai-archive").And.NotContain(".claude/archive");
    }

    /// <summary>E9.S1: the preview over the built CLI lists a due session with its companion, keeps a younger one out, and leaves
    /// every file as it was.</summary>
    [Fact]
    public async Task Preview_lists_a_due_session_with_its_companions_and_writes_nothing()
    {
        using var home = new ScenarioHome("archive-preview");
        Assert.SkipWhen(home.Paths.Side == HostSide.Windows, LinuxOnly);
        var paths = (LinuxHostPaths)home.Paths;
        var old = DateTime.UtcNow.AddDays(-20);
        foreach (var (file, written) in new[] { ("/home/me/.claude/projects/p/old.jsonl", old), ("/home/me/.claude/projects/p/old/subagents/a.jsonl", old), ("/home/me/.claude/projects/p/new.jsonl", DateTime.UtcNow.AddDays(-1)) })
        {
            var path = paths.DistroPath(file);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, "{}\n");
            File.SetLastWriteTimeUtc(path, written);
        }

        var stamps = Directory.EnumerateFiles(paths.DistroPath("/home/me/.claude"), "*", SearchOption.AllDirectories).ToDictionary(f => f, File.GetLastWriteTimeUtc);

        var result = await home.RunAsync("archive", "preview", "--agent", "claude-code", "--json");

        result.Exit.Should().Be((int)ExitCode.Ok, result.Stderr);
        var claude = JsonSerializer.Deserialize(result.Stdout, WslCareJsonContext.Default.ArchivePreviewReport)!.Agents.Single();
        claude.Units.Should().ContainSingle().Which.Should().Match<ArchiveUnitReport>(u => u.Key == "projects/p/old.jsonl" && u.Files == 2);
        claude.Younger.Should().Be(1);
        Directory.EnumerateFiles(paths.DistroPath("/home/me/.claude"), "*", SearchOption.AllDirectories).ToDictionary(f => f, File.GetLastWriteTimeUtc)
            .Should().BeEquivalentTo(stamps, "the preview moves, writes and touches nothing");
    }

    [Fact]
    public async Task Preview_as_root_is_refused_with_its_own_exit_code()
    {
        using var home = new ScenarioHome("archive-preview-root") { ClaimsRoot = true };

        var result = await home.RunAsync("archive", "preview", "--json");

        result.Exit.Should().Be((int)ExitCode.NotAsRoot);
        result.Stdout.Should().BeEmpty();
    }

    [Fact]
    public async Task On_windows_check_base_accepts_a_drive_folder_and_refuses_a_linux_path()
    {
        using var home = new ScenarioHome("archive-windows");
        Assert.SkipUnless(home.Paths.Side == HostSide.Windows, "the Windows binary's own rules: the Windows legs");
        var folder = Path.Combine(home.SandboxRoot, "archive");
        Directory.CreateDirectory(folder);

        var accepted = Report(await home.RunAsync("archive", "check-base", folder, "--json"));
        var linux = Report(await home.RunAsync("archive", "check-base", "/mnt/v/ai-archive", "--json"));

        accepted.Accepted.Should().BeTrue(accepted.Refusal);
        accepted.Side.Should().Be("windows");
        linux.Rule.Should().Be(BaseFolderRule.Shape);
    }
}
