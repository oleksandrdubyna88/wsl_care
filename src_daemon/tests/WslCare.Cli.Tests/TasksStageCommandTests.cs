using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

using FluentAssertions;

using WslCare.Cli;
using WslCare.Core.Hosting;
using WslCare.Core.Scheduler;
using WslCare.TestSupport;

namespace WslCare.Cli.Tests;

/// <summary>
/// WR-D2 (PLAN_windows_runner.md §3.2): <c>tasks stage [--remove] [--json]</c> through the whole program, in-process — the Windows
/// binary's verb (refused on the distro's, as <c>tasks render</c> is), the copy made from the host's own-path seam (a sandbox file,
/// never this test process's exe), the answer naming the digest, the path, what was swept and what was kept. Everything is under a
/// TempRoot; nothing real is read, staged or removed.
/// </summary>
public sealed class TasksStageCommandTests
{
    private const string WindowsOnly = "a CliHost over a Windows layout is built on Windows only (its probe); the Linux legs run the refusal";

    private static readonly byte[] ExeBytes = Encoding.UTF8.GetBytes("not a real executable: the bytes the CLI stages");

    private static readonly string Digest = Convert.ToHexStringLower(SHA256.HashData(ExeBytes));

    [Theory]
    [InlineData("--json")]
    [InlineData("--remove")]
    public void The_stage_is_refused_on_the_linux_binary(string flag)
    {
        using var root = new TempRoot("tasks-stage-linux");
        var paths = new LinuxHostPaths(LinuxEnvironment.Sandboxed(root.Path));
        var clock = new FixedTimeProvider();
        var host = new CliHost(paths, new Core.Files.PhysicalFileSystem(paths), clock, new RecordingCommandRunner())
        {
            Probe = new FakeProbe(HostSide.Wsl, clock),
            ProcessPath = () => "/usr/local/bin/wsl-care",
        };

        var (exit, stdout, stderr) = CliRun.Over(host, "tasks", "stage", flag);

        exit.Should().Be((int)ExitCode.Usage);
        stdout.Should().BeEmpty();
        stderr.Should().Contain("tasks").And.Contain("Windows binary");
        Directory.EnumerateFileSystemEntries(root.Path, "*", SearchOption.AllDirectories).Should().BeEmpty("a refused stage writes nothing");
    }

    [Fact]
    public void A_stage_answers_the_digest_the_path_and_what_it_swept()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), WindowsOnly);
        using var world = new StageWorld("tasks-stage-json");
        var older = world.PlantOlder();

        var (exit, stdout, stderr) = CliRun.Over(world.Host(), "tasks", "stage", "--json");

        exit.Should().Be((int)ExitCode.Ok, stderr);
        var answer = JsonNode.Parse(stdout)!;
        answer["outcome"]!.GetValue<string>().Should().Be(StageOutcomes.Staged, answer["reason"]!.GetValue<string>());
        answer["digest"]!.GetValue<string>().Should().Be(Digest);
        answer["path"]!.GetValue<string>().Should().Be(Path.Combine(world.Root, Digest, StagedCopies.ExeName));
        answer["version"]!.GetValue<string>().Should().Be(StagedCopies.ReleaseOf(Program.VersionText));
        answer["source"]!.GetValue<string>().Should().Be(world.Source);
        answer["swept"]!.AsArray().Select(n => (string)n!["path"]!).Should().Equal(older);
        answer["kept"]!.AsArray().Should().BeEmpty();
        File.ReadAllBytes(answer["path"]!.GetValue<string>()).Should().Equal(ExeBytes);
    }

    [Fact]
    public void The_text_answer_names_the_outcome_the_digest_and_the_path()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), WindowsOnly);
        using var world = new StageWorld("tasks-stage-text");
        var older = world.PlantOlder();

        var (exit, stdout, stderr) = CliRun.Over(world.Host(), "tasks", "stage");

        exit.Should().Be((int)ExitCode.Ok, stderr);
        var lines = CliRun.Lines(stdout);
        lines[0].Should().StartWith($"tasks stage: staged {Digest}").And.Contain(Path.Combine(world.Root, Digest, StagedCopies.ExeName));
        lines.Should().Contain(l => l.StartsWith("swept: ", StringComparison.Ordinal) && l.Contains(older, StringComparison.Ordinal));
    }

    /// <summary>The keep rule in the answer: the previous verified copy (the one a registered task may still name) is retained and
    /// named, in the JSON and in the text.</summary>
    [Fact]
    public void The_answer_names_the_previous_verified_copy_it_retains()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), WindowsOnly);
        using var world = new StageWorld("tasks-stage-retained");
        var previous = world.PlantVerified();

        var (exit, stdout, stderr) = CliRun.Over(world.Host(), "tasks", "stage", "--json");
        var (_, text, _) = CliRun.Over(world.Host(), "tasks", "stage");

        exit.Should().Be((int)ExitCode.Ok, stderr);
        var retained = JsonNode.Parse(stdout)!["retained"]!.AsArray().Should().ContainSingle().Subject!;
        ((string)retained["path"]!).Should().Be(previous);
        ((string)retained["why"]!).Should().Contain("verified copy");
        CliRun.Lines(text).Should().Contain(l => l.StartsWith("retained: ", StringComparison.Ordinal) && l.Contains(previous, StringComparison.Ordinal));
        Directory.Exists(previous).Should().BeTrue();
    }

    /// <summary>m4 of the review: a held run lock makes the stage and the removal answer busy with exit 75, doing nothing.</summary>
    [Theory]
    [InlineData("--json")]
    [InlineData("--remove")]
    public void A_held_run_lock_answers_busy_with_exit_75(string flag)
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), WindowsOnly);
        using var world = new StageWorld("tasks-stage-busy");
        var older = world.PlantOlder();
        var host = world.Host();

        int exit;
        string stdout;
        using (host.Files.TryLockExclusive(host.Paths.RunLockFile) is Core.Files.ExclusiveLock.Held held ? held.Handle : throw new InvalidOperationException("the test could not take the lock"))
        {
            (exit, stdout, _) = CliRun.Over(host, "tasks", "stage", flag);
        }

        exit.Should().Be((int)ExitCode.Busy);
        stdout.Should().Contain("busy").And.Contain("run lock");
        Directory.Exists(older).Should().BeTrue("a busy run removes nothing");
        Directory.Exists(Path.Combine(world.Root, Digest)).Should().BeFalse("a busy stage makes nothing");
    }

    [Fact]
    public void A_refused_stage_exits_1_and_says_why()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), WindowsOnly);
        using var world = new StageWorld("tasks-stage-refused");

        var (exit, stdout, _) = CliRun.Over(world.Host() with { ProcessPath = () => string.Empty }, "tasks", "stage", "--json");

        exit.Should().Be((int)ExitCode.RunFailed);
        var answer = JsonNode.Parse(stdout)!;
        answer["outcome"]!.GetValue<string>().Should().Be(StageOutcomes.Refused);
        answer["reason"]!.GetValue<string>().Should().Contain("own path");
        Directory.Exists(world.Root).Should().BeFalse("a refused stage makes nothing");
    }

    [Fact]
    public void Remove_answers_what_it_removed_and_the_root_with_exit_0()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), WindowsOnly);
        using var world = new StageWorld("tasks-stage-remove");
        var older = world.PlantOlder();

        var (exit, stdout, stderr) = CliRun.Over(world.Host(), "tasks", "stage", "--remove", "--json");

        exit.Should().Be((int)ExitCode.Ok, stderr);
        var answer = JsonNode.Parse(stdout)!;
        answer["root"]!.GetValue<string>().Should().Be(world.Root);
        answer["removed"]!.AsArray().Select(n => (string)n!["path"]!).Should().Equal(older);
        answer["kept"]!.AsArray().Should().BeEmpty();
        answer["rootRemoved"]!.GetValue<bool>().Should().BeTrue();
        Directory.Exists(world.Root).Should().BeFalse();
    }

    [Fact]
    public void Remove_keeps_and_names_an_executing_copy_and_still_exits_0()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), WindowsOnly);
        using var world = new StageWorld("tasks-stage-remove-kept");
        var older = world.PlantOlder();

        int exit;
        string stdout;
        using (new FileStream(Path.Combine(older, StagedCopies.ExeName), FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            (exit, stdout, _) = CliRun.Over(world.Host(), "tasks", "stage", "--remove");
        }

        exit.Should().Be((int)ExitCode.Ok, "a kept copy is named, not an error (the next stage or remove retries)");
        CliRun.Lines(stdout).Should().Contain(l => l.StartsWith("kept: ", StringComparison.Ordinal) && l.Contains(older, StringComparison.Ordinal) && l.Contains("in use", StringComparison.Ordinal));
        Directory.Exists(older).Should().BeTrue();
    }

    /// <summary>A Windows layout under a temporary root, the product's file system over it, and the exe the host says is its own.</summary>
    private sealed class StageWorld : IDisposable
    {
        private readonly TempRoot _temp;
        private readonly WindowsHostPaths _paths;
        private readonly Core.Files.PhysicalFileSystem _files;

        public StageWorld(string purpose)
        {
            _temp = new TempRoot(purpose);
            _paths = new WindowsHostPaths(WindowsEnvironment.Sandboxed(_temp.Path));
            _files = new Core.Files.PhysicalFileSystem(_paths) { OwnersAreThisProcess = true };
            Root = StagedCopies.Root(_paths);
            Source = _temp.File("Users/user/.vscode/extensions/x/bin/wsl-care.exe", string.Empty);
            File.WriteAllBytes(Source, ExeBytes);
        }

        public string Root { get; }

        public string Source { get; }

        /// <summary>An older verified-looking digest folder; its path.</summary>
        public string PlantOlder()
        {
            var digest = new string('a', 64);
            var folder = Path.Combine(Root, digest);
            Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, StagedCopies.ExeName), "an older copy");
            File.WriteAllText(Path.Combine(folder, StageRecord.FileName), $$"""{ "version": "0.3.9", "sha256": "{{digest}}", "stagedAtUtc": "2026-10-01T08:00:00+00:00", "source": "C:\\ext\\bin\\wsl-care.exe" }""");
            return folder;
        }

        /// <summary>A VERIFIED older digest folder (bytes that hash to its name, an agreeing stage.json); its path.</summary>
        public string PlantVerified()
        {
            var bytes = Encoding.UTF8.GetBytes("the copy the installed tasks still name");
            var digest = Convert.ToHexStringLower(SHA256.HashData(bytes));
            var folder = Path.Combine(Root, digest);
            Directory.CreateDirectory(folder);
            File.WriteAllBytes(Path.Combine(folder, StagedCopies.ExeName), bytes);
            File.WriteAllText(Path.Combine(folder, StageRecord.FileName), $$"""{ "version": "0.3.9", "sha256": "{{digest}}", "stagedAtUtc": "2026-10-05T08:00:00+00:00", "source": "C:\\ext\\bin\\wsl-care.exe" }""");
            return folder;
        }

        public CliHost Host()
        {
            var clock = new FixedTimeProvider();
            return new CliHost(_paths, _files, clock, new RecordingCommandRunner())
            {
                Probe = new FakeProbe(HostSide.Windows, clock),
                ProcessPath = () => Source,
            };
        }

        public void Dispose() => _temp.Dispose();
    }
}
