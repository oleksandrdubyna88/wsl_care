using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

using FluentAssertions;

using WslCare.Cli;
using WslCare.Core.Actions.Engine;
using WslCare.Core.Hosting;
using WslCare.Core.Scheduler;
using WslCare.TestSupport;

namespace WslCare.Cli.Tests;

/// <summary>
/// WR-D1 (PLAN_windows_runner.md §3.2, §6): <c>tasks render &lt;watch|archive&gt; [--json]</c> through the whole program, in-process —
/// the Windows binary's verb (refused on the distro's, as <c>watch</c> is refused on Windows), its answer naming the user's SID and
/// this process's own path, <c>registrable</c> only for a verified staged copy, and the Windows dry state read without a write.
/// The SID and the process path come through the host's seams; nothing real is read, registered or started.
/// </summary>
public sealed class TasksCommandTests
{
    private const string Sid = "S-1-5-21-1000000000-2000000000-3000000000-1001";

    private const string WindowsOnly = "a CliHost over a Windows layout is built on Windows only (its probe); the Linux legs run the refusal";

    private static readonly byte[] ExeBytes = Encoding.UTF8.GetBytes("not a real executable: the bytes the render hashes");

    private static readonly string Digest = Convert.ToHexStringLower(SHA256.HashData(ExeBytes));

    [Fact]
    public void The_render_is_refused_on_the_linux_binary()
    {
        using var root = new TempRoot("tasks-render-linux");
        var paths = new LinuxHostPaths(LinuxEnvironment.Sandboxed(root.Path));
        var clock = new FixedTimeProvider();
        var host = new CliHost(paths, new Core.Files.PhysicalFileSystem(paths), clock, new RecordingCommandRunner())
        {
            Probe = new FakeProbe(HostSide.Wsl, clock),
            UserSid = () => Sid,
            ProcessPath = () => "/usr/local/bin/wsl-care",
        };

        var (exit, stdout, stderr) = CliRun.Over(host, "tasks", "render", "watch", "--json");

        exit.Should().Be((int)ExitCode.Usage);
        stdout.Should().BeEmpty();
        stderr.Should().Contain("tasks").And.Contain("Windows binary");
        Directory.EnumerateFileSystemEntries(root.Path, "*", SearchOption.AllDirectories).Should().BeEmpty("a refused render writes nothing");
    }

    [Fact]
    public void A_verified_staged_copy_renders_registrable_xml_naming_the_sid_and_its_own_path()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), WindowsOnly);
        using var world = new WindowsWorld("tasks-render-staged");
        var exe = world.Stage();

        var (exit, stdout, stderr) = CliRun.Over(world.Host(exe), "tasks", "render", "watch", "--json");

        exit.Should().Be((int)ExitCode.Ok, stderr);
        var answer = JsonNode.Parse(stdout)!;
        answer["registrable"]!.GetValue<bool>().Should().BeTrue(answer["reason"]!.GetValue<string>());
        answer["task"]!.GetValue<string>().Should().Be("watch");
        answer["taskPath"]!.GetValue<string>().Should().Be("\\wsl-care\\watch");
        answer["digest"]!.GetValue<string>().Should().Be(Digest);
        answer["userSid"]!.GetValue<string>().Should().Be(Sid);
        answer["command"]!.GetValue<string>().Should().Be(exe);
        answer["xml"]!.GetValue<string>().Should().Contain($"<UserId>{Sid}</UserId>").And.Contain($"<Command>{exe}</Command>");
        answer["summary"]!.AsArray().Select(n => (string)n!).Should().StartWith($"principal={Sid} logon=3 level=0").And.EndWith($"action type=0 path={exe} args=tasks run watch --json");
        answer["numbers"]!.AsArray().Select(n => (string)n!["key"]!).Should().Equal("windowsTasks.watchLogonDelaySeconds", "mcpWatchdog.periodMinutes", "mcpWatchdog.runLimitMinutes");
        answer["executionTimeLimitMinutes"]!.GetValue<int>().Should().Be(20);
        answer["dry"]!["week"]!.GetValue<string>().Should().Be(DryWeek.NotStarted);
        answer["dry"]!["dryRun"]!.GetValue<bool>().Should().BeTrue();
        File.Exists(DryRunWindow.File(world.Paths)).Should().BeFalse("the render peeks at the dry state and never starts the week");
    }

    /// <summary>The coai plan round on WR-D1: the real command line for BOTH tasks — the archive task from a verified copy names its
    /// own cadence keys and its own arguments.</summary>
    [Fact]
    public void A_verified_staged_copy_renders_the_archive_task_with_its_own_numbers()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), WindowsOnly);
        using var world = new WindowsWorld("tasks-render-archive");
        var exe = world.Stage();

        var (exit, stdout, stderr) = CliRun.Over(world.Host(exe), "tasks", "render", "archive", "--json");

        exit.Should().Be((int)ExitCode.Ok, stderr);
        var answer = JsonNode.Parse(stdout)!;
        answer["registrable"]!.GetValue<bool>().Should().BeTrue(answer["reason"]!.GetValue<string>());
        answer["taskPath"]!.GetValue<string>().Should().Be("\\wsl-care\\archive");
        answer["xml"]!.GetValue<string>().Should().Contain("<Arguments>tasks run archive --json</Arguments>");
        answer["numbers"]!.AsArray().Select(n => (string)n!["key"]!).Should().Equal("windowsTasks.archiveLogonDelayMinutes", "windowsTasks.archiveEveryHours", "timer.runLimitMinutes");
    }

    [Fact]
    public void A_render_from_outside_the_staging_root_answers_but_is_not_registrable()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), WindowsOnly);
        using var world = new WindowsWorld("tasks-render-outside");
        var extensionExe = world.Root.File("Users/me/.vscode/extensions/x/bin/wsl-care.exe", "bytes");

        var (exit, stdout, stderr) = CliRun.Over(world.Host(extensionExe), "tasks", "render", "archive", "--json");

        exit.Should().Be((int)ExitCode.Ok, stderr);
        var answer = JsonNode.Parse(stdout)!;
        answer["registrable"]!.GetValue<bool>().Should().BeFalse();
        answer["reason"]!.GetValue<string>().Should().Contain("not a staged copy");
        answer["digest"]!.GetValue<string>().Should().BeEmpty();
        answer["xml"]!.GetValue<string>().Should().Contain("<Arguments>tasks run archive --json</Arguments>", "the XML is still shown, for the person to read");
    }

    [Fact]
    public void A_render_without_the_user_sid_is_not_registrable()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), WindowsOnly);
        using var world = new WindowsWorld("tasks-render-no-sid");
        var exe = world.Stage();

        var (exit, stdout, _) = CliRun.Over(world.Host(exe) with { UserSid = () => string.Empty }, "tasks", "render", "watch", "--json");

        exit.Should().Be((int)ExitCode.Ok);
        var answer = JsonNode.Parse(stdout)!;
        answer["registrable"]!.GetValue<bool>().Should().BeFalse();
        answer["reason"]!.GetValue<string>().Should().Be(TasksRender.NoSid);
    }

    [Fact]
    public void The_text_answer_says_whether_it_is_registrable_and_shows_the_summary_and_the_xml()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), WindowsOnly);
        using var world = new WindowsWorld("tasks-render-text");
        var exe = world.Stage();

        var (exit, stdout, stderr) = CliRun.Over(world.Host(exe), "tasks", "render", "watch");

        exit.Should().Be((int)ExitCode.Ok, stderr);
        var lines = CliRun.Lines(stdout);
        lines[0].Should().StartWith("tasks render watch: registrable");
        lines.Should().Contain($"  principal={Sid} logon=3 level=0")
            .And.Contain(l => l.StartsWith("dry run:", StringComparison.Ordinal) && l.Contains("not started", StringComparison.Ordinal))
            .And.Contain(l => l.Contains("mcpWatchdog.periodMinutes=5", StringComparison.Ordinal))
            .And.Contain(l => l.StartsWith("<Task ", StringComparison.Ordinal));
    }

    /// <summary>WR-T2 (coai plan round 4663beda, finding 0): a principal that is not a user account, or an elevated process, is never
    /// registrable — for every task. The Entra ID shape (<c>S-1-12-1-…</c>) is a real user and stays registrable.</summary>
    [Theory]
    [InlineData("watch", "S-1-5-18", false, "must be a user account")]
    [InlineData("cleanup", "S-1-5-18", false, "must be a user account")]
    [InlineData("cleanup", "S-1-5-19", false, "must be a user account")]
    [InlineData("cleanup", "S-1-5-32-544", false, "must be a user account")]
    [InlineData("archive", "S-1-5-80-0-0-0-0-0", false, "must be a user account")]
    [InlineData("cleanup", "S-1-12-1-1-2-3-4", true, "")]
    [InlineData("cleanup", Sid, true, "")]
    public void A_principal_that_is_not_a_user_account_is_not_registrable(string task, string sid, bool registrable, string reasonPart)
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), WindowsOnly);
        using var world = new WindowsWorld("tasks-render-sid");
        var exe = world.Stage();

        var (exit, stdout, stderr) = CliRun.Over(world.Host(exe) with { UserSid = () => sid }, "tasks", "render", task, "--json");

        exit.Should().Be((int)ExitCode.Ok, stderr);
        var answer = JsonNode.Parse(stdout)!;
        answer["registrable"]!.GetValue<bool>().Should().Be(registrable, answer["reason"]!.GetValue<string>());
        if (!registrable)
        {
            answer["reason"]!.GetValue<string>().Should().Contain(reasonPart).And.Contain(sid);
        }
    }

    [Theory]
    [InlineData("watch")]
    [InlineData("cleanup")]
    public void An_elevated_render_is_not_registrable_but_still_shows_the_xml(string task)
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), WindowsOnly);
        using var world = new WindowsWorld("tasks-render-elevated");
        var exe = world.Stage();

        var (exit, stdout, stderr) = CliRun.Over(world.Host(exe) with { Privilege = new ProcessPrivilege(true, "this process is elevated") }, "tasks", "render", task, "--json");

        exit.Should().Be((int)ExitCode.Ok, stderr);
        var answer = JsonNode.Parse(stdout)!;
        answer["registrable"]!.GetValue<bool>().Should().BeFalse();
        answer["reason"]!.GetValue<string>().Should().Contain("elevated");
        answer["xml"]!.GetValue<string>().Should().Contain($"<Arguments>tasks run {task} --json</Arguments>");
    }

    [Fact]
    public void A_verified_staged_copy_renders_the_cleanup_task_registrable_with_its_numbers_and_a23s_dry_state()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), WindowsOnly);
        using var world = new WindowsWorld("tasks-render-cleanup");
        var exe = world.Stage();
        Directory.CreateDirectory(world.Paths.StateDirectory);
        var stamp = DryRunWindow.File(world.Paths, "A23");
        File.WriteAllText(stamp, $$"""{ "schemaVersion": 1, "at": "{{FixedTimeProvider.DefaultNow.AddDays(-2):O}}" }""");
        var before = File.ReadAllText(stamp);

        var (exit, stdout, stderr) = CliRun.Over(world.Host(exe), "tasks", "render", "cleanup", "--json");
        var (_, watchOut, _) = CliRun.Over(world.Host(exe), "tasks", "render", "watch", "--json");

        exit.Should().Be((int)ExitCode.Ok, stderr);
        var answer = JsonNode.Parse(stdout)!;
        answer["registrable"]!.GetValue<bool>().Should().BeTrue(answer["reason"]!.GetValue<string>());
        answer["taskPath"]!.GetValue<string>().Should().Be("\\wsl-care\\cleanup");
        answer["numbers"]!.AsArray().Select(n => (string)n!["key"]!).Should().Equal("windowsTasks.cleanupLogonDelayMinutes", "windowsTasks.cleanupEveryHours", "timer.runLimitMinutes");
        answer["executionTimeLimitMinutes"]!.GetValue<int>().Should().Be(286);
        answer["xml"]!.GetValue<string>().Should().Contain("<Arguments>tasks run cleanup --json</Arguments>");
        answer["dry"]!["week"]!.GetValue<string>().Should().Be(DryWeek.Running, "the cleanup's dry line describes A23's week, which gates deletion");
        JsonNode.Parse(watchOut)!["dry"]!["week"]!.GetValue<string>().Should().Be(DryWeek.NotStarted, "the watch reads the common stamp");
        File.ReadAllText(stamp).Should().Be(before);
        File.Exists(DryRunWindow.File(world.Paths)).Should().BeFalse("a render never starts a week");
    }

    /// <summary>As <c>units dropin</c>: no task is rendered from a configuration the daemon itself refuses.</summary>
    [Fact]
    public void A_render_is_refused_while_the_configuration_is_in_error()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), WindowsOnly);
        using var world = new WindowsWorld("tasks-render-invalid");
        world.WriteMachineConfig("""{ "mcpServers": { "cpuIntervalMaxMinutes": 10 }, "mcpWatchdog": { "periodMinutes": 10 } }""");

        var (exit, stdout, stderr) = CliRun.Over(world.Host(world.Stage()), "tasks", "render", "watch", "--json");

        exit.Should().Be((int)ExitCode.ObserveOnly);
        stdout.Should().BeEmpty();
        stderr.Should().Contain("mcpWatchdog.periodMinutes").And.Contain("no task is rendered");
    }

    /// <summary>A Windows layout under a temporary root, the product's file system over it, a staged copy on request.</summary>
    private sealed class WindowsWorld : IDisposable
    {
        public WindowsWorld(string purpose)
        {
            Root = new TempRoot(purpose);
            Paths = new WindowsHostPaths(WindowsEnvironment.Sandboxed(Root.Path));
            Files = new Core.Files.PhysicalFileSystem(Paths) { OwnersAreThisProcess = true };
        }

        public TempRoot Root { get; }

        public WindowsHostPaths Paths { get; }

        public Core.Files.PhysicalFileSystem Files { get; }

        /// <summary>A copy at <c>&lt;stagingRoot&gt;\&lt;digest&gt;\wsl-care.exe</c> with an agreeing stage.json; its path.</summary>
        public string Stage()
        {
            var folder = Path.Combine(StagedCopies.Root(Paths), Digest);
            Directory.CreateDirectory(folder);
            var exe = Path.Combine(folder, StagedCopies.ExeName);
            File.WriteAllBytes(exe, ExeBytes);
            var version = StagedCopies.ReleaseOf(Program.VersionText);
            File.WriteAllText(Path.Combine(folder, StageRecord.FileName), $$"""{ "version": "{{version}}", "sha256": "{{Digest}}", "stagedAtUtc": "2026-10-11T08:00:00+00:00", "source": "C:\\ext\\bin\\wsl-care.exe" }""");
            return exe;
        }

        public void WriteMachineConfig(string json)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Paths.MachineConfigFile)!);
            File.WriteAllText(Paths.MachineConfigFile, json);
        }

        public CliHost Host(string processPath)
        {
            var clock = new FixedTimeProvider();
            return new CliHost(Paths, Files, clock, new RecordingCommandRunner())
            {
                Probe = new FakeProbe(HostSide.Windows, clock),
                UserSid = () => Sid,
                ProcessPath = () => processPath,
                // The Windows CI runner is ELEVATED: a render test pins its privilege and never inherits the runner's (the guard is the product's).
                Privilege = new ProcessPrivilege(false, "a test says this process is not elevated"),
            };
        }

        public void Dispose() => Root.Dispose();
    }
}
