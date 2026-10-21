using System.Text.Json.Nodes;

using FluentAssertions;

using WslCare.Cli;
using WslCare.Core.Actions.Engine;
using WslCare.Core.Files;
using WslCare.Core.Files.Deletion;
using WslCare.Core.Hosting;
using WslCare.TestSupport;

namespace WslCare.Cli.Tests;

/// <summary>
/// WR-T2 (PLAN_windows_runner.md §3.12 "In the panel"): <c>tasks preview cleanup [--json]</c> through the whole program, in-process, over
/// a sandbox <c>%TEMP%</c> under a <see cref="TempRoot"/> - never the real one. The verb is read-only: it deletes nothing, moves no
/// cursor, starts no dry-run week, writes no file, asks for no lock, and bounds the paths it lists.
/// </summary>
public sealed class TasksPreviewCommandTests : IDisposable
{
    private const string WindowsOnly = "a CliHost over a Windows layout and its owners: the Windows legs (the refusal is the other test)";

    private readonly SandboxHost _sandbox = new("tasks-preview");

    public void Dispose() => _sandbox.Dispose();

    private string Temp => _sandbox.Paths.TempDirectory;

    private string Make(string relative, int daysOld, params (string File, int Days)[] files) => TempTrees.Make(Temp, relative, daysOld, files);

    private CliHost Host(IFileSystem? files = null, bool elevated = false)
    {
        Directory.CreateDirectory(Temp);
        var fs = files ?? _sandbox.Files;
        var clock = TimeProvider.System;
        return new CliHost(_sandbox.Paths, fs, clock, new RecordingCommandRunner())
        {
            Probe = new FakeProbe(HostSide.Windows, clock),
            Privilege = new ProcessPrivilege(elevated, "a test says so"),
            UserSid = () => _sandbox.Files.OwnerSidOf(Temp),
        };
    }

    private IReadOnlyList<string> Snapshot() =>
        [.. Directory.EnumerateFileSystemEntries(_sandbox.Root.Path, "*", SearchOption.AllDirectories)
            .Select(p => $"{p}|{(File.Exists(p) ? new FileInfo(p).Length : -1)}|{File.GetLastWriteTimeUtc(p).Ticks}")
            .Order(StringComparer.Ordinal)];

    // ---------- the parser ----------

    [Theory]
    [InlineData(new[] { "tasks", "preview", "cleanup" }, false)]
    [InlineData(new[] { "tasks", "preview", "cleanup", "--json" }, true)]
    public void The_closed_set_of_one_parses(string[] argv, bool json)
    {
        var request = CommandLine.Parse(argv).Should().BeOfType<Request.TasksPreview>().Subject;

        request.Task.Should().Be(Core.Scheduler.WindowsTask.Cleanup);
        request.Json.Should().Be(json);
    }

    public static TheoryData<string[]> Refused => new()
    {
        { ["tasks", "preview"] },
        { ["tasks", "preview", "watch"] },
        { ["tasks", "preview", "archive"] },
        { ["tasks", "preview", "cleanup", "--confirm"] },
        { ["tasks", "preview", "cleanup", "--timer"] },
        { ["tasks", "preview", "A23"] },
        { ["tasks", "preview", "--json", "cleanup"] },
    };

    [Theory]
    [MemberData(nameof(Refused))]
    public void Anything_else_is_refused_naming_cleanup(string[] argv)
    {
        CommandLine.Parse(argv).Should().BeOfType<Request.Failed>().Which.Message.Should().Contain("tasks preview").And.Contain("cleanup");
    }

    // ---------- refusals ----------

    [Fact]
    public void An_elevated_process_is_refused_before_anything_is_read()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), WindowsOnly);
        var old = Make("coai-old", 30, ("a.txt", 30));
        var before = Snapshot();

        var (exit, stdout, stderr) = CliRun.Over(Host(elevated: true), "tasks", "preview", "cleanup", "--json");

        exit.Should().Be((int)ExitCode.NotAsRoot);
        stdout.Should().BeEmpty();
        stderr.Should().Contain("Administrators").And.Contain("misleading").And.Contain("Nothing was read.").And.NotContain("act ", "the preview is refused for its own reason, not act's");
        Directory.Exists(_sandbox.Paths.StateDirectory).Should().BeFalse();
        Directory.Exists(old).Should().BeTrue();
        Snapshot().Should().Equal(before);
    }

    [Fact]
    public void The_preview_is_refused_on_the_linux_binary()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "the distro's binary: the Linux legs");
        using var root = new TempRoot("tasks-preview-linux");
        var paths = new LinuxHostPaths(LinuxEnvironment.Sandboxed(root.Path));
        var clock = new FixedTimeProvider();
        var host = new CliHost(paths, new PhysicalFileSystem(paths), clock, new RecordingCommandRunner()) { Probe = new FakeProbe(HostSide.Wsl, clock) };

        var (exit, stdout, stderr) = CliRun.Over(host, "tasks", "preview", "cleanup", "--json");

        exit.Should().Be((int)ExitCode.Usage);
        stdout.Should().BeEmpty();
        stderr.Should().Contain("Windows binary");
        Directory.EnumerateFileSystemEntries(root.Path, "*", SearchOption.AllDirectories).Should().BeEmpty("a refused preview writes nothing");
    }

    // ---------- the answer ----------

    [Fact]
    public void The_preview_writes_nothing_under_the_sandbox_and_answers_with_the_figures()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), WindowsOnly);
        var old = Make("coai-old", 30, ("a.txt", 30));
        Make("coai-young", 30, ("fresh.txt", 0));
        Make("sometool-old", 30, ("x.txt", 30));
        Make("claude", 30, ("log.txt", 30));
        var before = Snapshot();

        var (exit, stdout, stderr) = CliRun.Over(Host(), "tasks", "preview", "cleanup", "--json");

        exit.Should().Be((int)ExitCode.Ok, stderr);
        Snapshot().Should().Equal(before, "a preview writes nothing: no cursor, no stamp, no state directory");
        Directory.Exists(_sandbox.Paths.StateDirectory).Should().BeFalse();
        var answer = JsonNode.Parse(stdout)!;
        answer["task"]!.GetValue<string>().Should().Be("cleanup");
        answer["productVersion"]!.GetValue<string>().Should().NotBeNullOrEmpty();
        answer["root"]!.GetValue<string>().Should().Be(Temp);
        answer["refusal"]!.GetValue<string>().Should().BeEmpty();
        answer["counts"]!["eligible"]!.GetValue<long>().Should().Be(1);
        answer["counts"]!["young"]!.GetValue<long>().Should().Be(1);
        answer["counts"]!["wouldBeEligible"]!.GetValue<long>().Should().Be(1);
        var groups = answer["groups"]!.AsArray();
        groups.Select(g => (string)g!["pattern"]!).Should().Contain(["coai-*", "sometool-*"]);
        groups.Single(g => (string)g!["pattern"]! == "coai-*")!["paths"]!.AsArray().Select(p => (string)p!).Should().Equal(old);
        answer["excludedByReason"]!.AsArray().Select(r => (string)r!["reason"]!).Should().Contain(r => r.Contains("claude", StringComparison.OrdinalIgnoreCase));
        answer.ToJsonString().Should().NotContain("first-timer-run");
    }

    [Fact]
    public void The_preview_never_asks_for_the_run_lock_and_answers_while_it_is_held()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), WindowsOnly);
        Make("coai-old", 30, ("a.txt", 30));
        var recorder = new LockRecorder(_sandbox.Files);

        int exit;
        string stdout;
        using (_sandbox.Files.TryLockExclusive(_sandbox.Paths.RunLockFile) is ExclusiveLock.Held held ? held.Handle : throw new InvalidOperationException("the test could not take the run lock"))
        {
            (exit, stdout, _) = CliRun.Over(Host(recorder), "tasks", "preview", "cleanup", "--json");
        }

        exit.Should().Be((int)ExitCode.Ok);
        JsonNode.Parse(stdout)!["counts"]!["eligible"]!.GetValue<long>().Should().Be(1);
        recorder.Requests.Should().BeEmpty("a preview takes no lock - neither the run lock nor any other");
    }

    [Fact]
    public void The_report_names_the_numbers_in_force_and_a23s_own_dry_week()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), WindowsOnly);
        var first = JsonNode.Parse(CliRun.Over(Host(), "tasks", "preview", "cleanup", "--json").Stdout)!;
        Directory.CreateDirectory(_sandbox.Paths.StateDirectory);
        File.WriteAllText(DryRunWindow.File(_sandbox.Paths, "A23"), $$"""{ "schemaVersion": 1, "at": "{{DateTimeOffset.UtcNow.AddDays(-2):O}}" }""");
        var second = JsonNode.Parse(CliRun.Over(Host(), "tasks", "preview", "cleanup", "--json").Stdout)!;

        first["olderThanDays"]!.GetValue<int>().Should().Be(7);
        first["allowGlobs"]!.AsArray().Select(g => (string)g!).Should().Equal("coai-*", "wsl-care-test-*", "stage-*", "mindex-*", "git-commit-*", "docrag-*", "bge-seed-*");
        first["maxPathsPerGroup"]!.GetValue<int>().Should().Be(20);
        DateTimeOffset.Parse((string)first["cutoffUtc"]!, System.Globalization.CultureInfo.InvariantCulture).Should().BeCloseTo(DateTimeOffset.UtcNow.AddDays(-7), TimeSpan.FromMinutes(5));
        first["dry"]!["week"]!.GetValue<string>().Should().Be(DryWeek.NotStarted);
        second["dry"]!["week"]!.GetValue<string>().Should().Be(DryWeek.Running);
    }

    [Fact]
    public void An_observe_only_configuration_still_previews_and_says_so()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), WindowsOnly);
        Make("coai-old", 30, ("a.txt", 30));
        _sandbox.WriteUserConfig("""{ "notAKeyOfThisProduct": 1 }""");

        var (exit, stdout, stderr) = CliRun.Over(Host(), "tasks", "preview", "cleanup", "--json");

        exit.Should().Be((int)ExitCode.Ok, stderr);
        var answer = JsonNode.Parse(stdout)!;
        answer["observeOnly"]!.GetValue<bool>().Should().BeTrue();
        answer["configurationErrors"]!.AsArray().Should().NotBeEmpty();
        answer["counts"]!["eligible"]!.GetValue<long>().Should().Be(1, "a preview that deletes nothing may say what it sees");
    }

    [Fact]
    public void The_text_answer_lists_groups_their_first_paths_and_the_remainder()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), WindowsOnly);
        for (var i = 0; i < 25; i++)
        {
            Make($"coai-{i:00}", 30, ("a.txt", 30));
        }

        var (exit, stdout, stderr) = CliRun.Over(Host(), "tasks", "preview", "cleanup");

        exit.Should().Be((int)ExitCode.Ok, stderr);
        var lines = CliRun.Lines(stdout);
        lines[0].Should().StartWith("tasks preview cleanup:").And.Contain(Temp);
        lines.Should().Contain(l => l.StartsWith("eligible coai-*: 25", StringComparison.Ordinal) && l.EndsWith("showing 20:", StringComparison.Ordinal));
        lines.Should().Contain(l => l.Trim() == "... and 5 more");
        lines.Count(l => l.TrimStart().StartsWith(Temp, StringComparison.OrdinalIgnoreCase)).Should().Be(20, "the paths are bounded");
    }

    [Fact]
    public void The_text_and_the_json_say_when_the_would_be_eligible_scan_ran_out_of_the_scan_budget()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), WindowsOnly);
        for (var i = 0; i < 600; i++)
        {
            Make($"tool-{i:000}", 30, ("a.txt", 30));
        }

        _sandbox.WriteUserConfig("""{ "temp": { "scanMaxNodes": 1000 } }""");

        var json = JsonNode.Parse(CliRun.Over(Host(), "tasks", "preview", "cleanup", "--json").Stdout)!;
        var (exit, text, _) = CliRun.Over(Host(), "tasks", "preview", "cleanup");

        json["wouldBeEligibleLimited"]!.GetValue<bool>().Should().BeTrue();
        exit.Should().Be((int)ExitCode.Ok);
        text.Should().Contain("ran out of the scan budget").And.Contain("temp.scanMaxNodes");
    }

    private sealed class LockRecorder(IFileSystem inner) : DelegatingFileSystem(inner)
    {
        public List<string> Requests { get; } = [];

        public override ExclusiveLock TryLockExclusive(string lockPath)
        {
            Requests.Add(lockPath);
            return base.TryLockExclusive(lockPath);
        }
    }
}
