using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

using FluentAssertions;

using WslCare.Cli;
using WslCare.Core.Config;
using WslCare.Core.Files;
using WslCare.Core.Hosting;
using WslCare.Core.Worktrees;
using WslCare.TestSupport;

namespace WslCare.Cli.Tests;

/// <summary>
/// E19.S3 (PLAN_disk_hygiene_windows.md §3.3, E19 stories §4): <c>worktrees roots set</c> (the folders on STDIN, all-or-nothing, a verdict per
/// path), <c>worktrees roots clear</c> and <c>worktrees scan</c> through the whole program in-process over a Windows layout under a
/// <c>testsToDelete-</c> folder — the Windows binary's verbs, refused naming that side on the distro's. The scan starts no process.
/// </summary>
public sealed class WorktreesCommandTests
{
    private const string WindowsOnly = "a CliHost over a Windows layout is built on Windows only (its probe); the Linux legs run the refusal";

    private static JsonNode Json(string stdout) => JsonNode.Parse(stdout)!;

    private static string Forward(string path) => path.Replace('\\', '/');

    // ---------- the distro's binary ----------

    [Theory]
    [InlineData("worktrees", "roots", "set", "--json")]
    [InlineData("worktrees", "roots", "clear", "--json")]
    [InlineData("worktrees", "scan", "--json")]
    [InlineData("worktrees", "scan")]
    public void The_distro_binary_refuses_the_worktrees_verbs_naming_the_windows_side(params string[] argv)
    {
        using var root = new TempRoot("worktrees-linux");
        var paths = new LinuxHostPaths(LinuxEnvironment.Sandboxed(root.Path));
        var clock = new FixedTimeProvider();
        var runner = new RecordingCommandRunner();
        var host = new CliHost(paths, new PhysicalFileSystem(paths), clock, runner) { Probe = new FakeProbe(HostSide.Wsl, clock), StandardInput = () => new MemoryStream(Encoding.UTF8.GetBytes("D:\\work\n")) };

        var (exit, stdout, stderr) = CliRun.Over(host, argv);

        exit.Should().Be((int)ExitCode.Usage);
        stdout.Should().BeEmpty();
        stderr.Should().Contain("Windows binary").And.Contain("worktrees");
        runner.Requests.Should().BeEmpty();
        Directory.EnumerateFileSystemEntries(root.Path, "*", SearchOption.AllDirectories).Should().BeEmpty("a refused verb writes nothing");
    }

    // ---------- roots set ----------

    [Fact]
    public void Roots_set_reads_stdin_validates_and_writes_nothing_on_a_refusal()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), WindowsOnly);
        using var world = new Roots("roots-refusal");
        world.Work("good");

        var (exit, stdout, stderr) = world.Run(world.Stdin(world.Path("good"), "relative-path"), "worktrees", "roots", "set", "--json");

        exit.Should().Be((int)ExitCode.Usage);
        stderr.Should().Contain("Nothing was written.");
        var report = Json(stdout);
        report["written"]!.GetValue<bool>().Should().BeFalse();
        report["roots"]!.AsArray().Select(r => r!["accepted"]!.GetValue<bool>()).Should().Equal(true, false);
        world.StoredRoots().Should().BeEmpty("one refusal leaves the key as it was; the good folder was not written either");
        File.Exists(world.Paths.UserConfigFile).Should().BeFalse("not even the layer file was created");
    }

    [Fact]
    public void Roots_set_answers_a_verdict_per_path()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), WindowsOnly);
        using var world = new Roots("roots-verdicts");
        world.Work("one");
        world.Work("two");

        var (exit, stdout, stderr) = world.Run(world.Stdin(world.Path("one"), "", world.Path("two").Replace('\\', '/') + "/"), "worktrees", "roots", "set", "--json");

        exit.Should().Be((int)ExitCode.Ok, stderr);
        var report = Json(stdout);
        report["schemaVersion"]!.GetValue<int>().Should().Be(1);
        report["written"]!.GetValue<bool>().Should().BeTrue();
        var roots = report["roots"]!.AsArray();
        roots.Select(r => r!["given"]!.GetValue<string>()).Should().Equal(world.Path("one"), world.Path("two").Replace('\\', '/') + "/");
        roots.Select(r => r!["path"]!.GetValue<string>()).Should().Equal([world.Path("one"), world.Path("two")], "the spelling the key stores");
        roots.Should().OnlyContain(r => r!["accepted"]!.GetValue<bool>() && r["rule"]!.GetValue<string>() == "");
        world.StoredRoots().Should().Equal(world.Path("one"), world.Path("two"));
    }

    [Fact]
    public void Roots_set_without_json_prints_a_line_per_path()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), WindowsOnly);
        using var world = new Roots("roots-text");
        world.Work("one");

        var (exit, stdout, _) = world.Run(world.Stdin(world.Path("one"), "nope"), "worktrees", "roots", "set");

        exit.Should().Be((int)ExitCode.Usage);
        CliRun.Lines(stdout).Should().HaveCount(2);
        stdout.Should().Contain("accepted").And.Contain(world.Path("one")).And.Contain("refused").And.Contain("nope");
    }

    [Fact]
    public void Roots_set_with_nothing_on_stdin_is_refused_and_writes_nothing()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), WindowsOnly);
        using var world = new Roots("roots-empty");

        var (exit, stdout, stderr) = world.Run(world.Stdin(), "worktrees", "roots", "set", "--json");

        exit.Should().Be((int)ExitCode.Usage);
        stdout.Should().BeEmpty();
        stderr.Should().Contain("stdin").And.Contain("worktrees roots clear").And.Contain("Nothing was written.");
        File.Exists(world.Paths.UserConfigFile).Should().BeFalse();
    }

    [Fact]
    public void Roots_set_with_a_stdin_past_the_cap_or_with_no_end_is_refused_and_writes_nothing()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), WindowsOnly);
        using var world = new Roots("roots-cap");
        world.Work("one");
        var huge = new MemoryStream(Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat(world.Path("one") + "\n", 100_000))));

        var (bigExit, _, bigErr) = world.Run(huge, "worktrees", "roots", "set", "--json");
        var (hangExit, _, hangErr) = world.Run(new NeverEndingStream(), TimeSpan.FromMilliseconds(200), "worktrees", "roots", "set", "--json");

        bigExit.Should().Be((int)ExitCode.Usage);
        bigErr.Should().Contain("larger than").And.Contain("Nothing was written.");
        hangExit.Should().Be((int)ExitCode.Usage);
        hangErr.Should().Contain("no end").And.Contain("Nothing was written.");
        world.StoredRoots().Should().BeEmpty();
    }

    [Fact]
    public void Roots_set_judges_every_rule_not_only_the_shape()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), WindowsOnly);
        using var world = new Roots("roots-rules");
        world.Work("outer");
        world.Work("outer/inner");

        var (exit, stdout, _) = world.Run(world.Stdin(world.Path("outer"), world.Path("outer/inner"), world.Sandbox.Path), "worktrees", "roots", "set", "--json");

        exit.Should().Be((int)ExitCode.Usage);
        Json(stdout)["roots"]!.AsArray().Select(r => r!["rule"]!.GetValue<string>()).Should().Equal("", RootRule.Overlap, RootRule.Profile);
    }

    // ---------- roots clear ----------

    [Fact]
    public void Roots_clear_empties_the_key_and_says_so()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), WindowsOnly);
        using var world = new Roots("roots-clear");
        world.Work("one");
        world.Run(world.Stdin(world.Path("one")), "worktrees", "roots", "set", "--json").Exit.Should().Be((int)ExitCode.Ok);

        var (exit, stdout, stderr) = world.Run(world.Stdin(), "worktrees", "roots", "clear", "--json");

        exit.Should().Be((int)ExitCode.Ok, stderr);
        Json(stdout)["roots"]!.AsArray().Should().BeEmpty();
        world.StoredRoots().Should().BeEmpty();
    }

    [Fact]
    public void Roots_clear_with_nothing_set_is_still_ok()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), WindowsOnly);
        using var world = new Roots("roots-clear-empty");

        var (exit, _, stderr) = world.Run(world.Stdin(), "worktrees", "roots", "clear");

        exit.Should().Be((int)ExitCode.Ok, stderr);
    }

    // ---------- the config duplicate ----------

    [Fact]
    public void Config_set_of_the_roots_key_is_judged_by_the_same_rules()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), WindowsOnly);
        using var world = new Roots("roots-config");
        world.Work("one");

        var (badExit, _, badErr) = world.Run(world.Stdin(), "config", "set", "worktrees.roots", world.Sandbox.Path);
        var (okExit, _, okErr) = world.Run(world.Stdin(), "config", "set", "worktrees.roots", world.Path("one"));

        badExit.Should().Be((int)ExitCode.Usage);
        badErr.Should().Contain("profile").And.Contain("Nothing was written.");
        okExit.Should().Be((int)ExitCode.Ok, okErr);
        world.StoredRoots().Should().Equal(world.Path("one"));
    }

    // ---------- the shared request ----------

    private static (string[] Set, string Stdin, string[] Paths) Fixture()
    {
        var directory = typeof(WorktreesCommandTests).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().Single(a => a.Key == "WslCare.ContractsDirectory").Value!;
        using var json = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, "requests", "worktrees-roots-set-stdin.json")));
        string[] Words(string name) => [.. json.RootElement.GetProperty(name).EnumerateArray().Select(e => e.GetString()!)];
        return (Words("set"), json.RootElement.GetProperty("stdin").GetString()!, Words("paths"));
    }

    [Fact]
    public void The_shared_request_parses_and_names_its_paths_on_stdin_never_on_argv()
    {
        var (set, stdin, paths) = Fixture();

        CommandLine.Parse(set).Should().Be(new Request.WorktreesRootsSet(true));
        set.Should().NotContain(paths);
        StdinList.Lines(stdin, _ => true).Lines.Should().Equal(paths);
        stdin.Should().EndWith("\n", "every line is ended");
    }

    [Fact]
    public void The_shared_request_is_read_by_the_verb_as_exactly_its_paths_one_verdict_each()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), WindowsOnly);
        using var world = new Roots("roots-shared");
        var (set, stdin, paths) = Fixture();

        var (_, stdout, _) = world.Run(new MemoryStream(Encoding.UTF8.GetBytes(stdin)), set);

        Json(stdout)["roots"]!.AsArray().Select(r => r!["given"]!.GetValue<string>()).Should().Equal(paths);
    }

    // ---------- scan ----------

    [Fact]
    public void A_scan_with_no_root_answers_an_empty_report_and_says_how_to_choose_folders()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), WindowsOnly);
        using var world = new Roots("scan-none");

        var (exit, stdout, stderr) = world.Run(world.Stdin(), "worktrees", "scan", "--json");

        exit.Should().Be((int)ExitCode.Ok, stderr);
        Json(stdout)["roots"]!.AsArray().Should().BeEmpty();
        stderr.Should().Contain("no root configured").And.Contain("worktrees roots set");
    }

    [Fact]
    public void A_scan_reports_the_roots_writes_the_report_and_starts_no_process()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), WindowsOnly);
        using var world = new Roots("scan-report");
        world.Work("main/.git/worktrees/wt1");
        world.File("work/main/.git/worktrees/wt1/gitdir", Forward(world.Path("wt1/.git")) + "\n");
        world.File("work/wt1/.git", "gitdir: " + Forward(world.Path("main/.git/worktrees/wt1")) + "\n");
        world.File("work/wt1/App.csproj", "<Project/>");
        world.File("work/wt1/bin/Debug/App.dll", new string('d', 300));
        world.File("work/docs/readme.txt", "hello");
        world.Run(world.Stdin(world.Work("")), "worktrees", "roots", "set", "--json").Exit.Should().Be((int)ExitCode.Ok);

        var (exit, stdout, stderr) = world.Run(world.Stdin(), "worktrees", "scan", "--json");

        exit.Should().Be((int)ExitCode.Ok, stderr);
        var report = Json(stdout);
        var root = report["roots"]!.AsArray().Single()!;
        root["path"]!.GetValue<string>().Should().Be(world.Path(""));
        root["worktrees"]!.GetValue<int>().Should().Be(1);
        root["mainClones"]!.GetValue<int>().Should().Be(1);
        root["notGit"]!.GetValue<int>().Should().Be(1);
        root["buildOutput"]!["all"]!["bytes"]!.GetValue<long>().Should().Be(300);
        report["reclaimable"]!["available"]!.GetValue<bool>().Should().BeFalse();
        var file = Path.Combine(world.Paths.StateDirectory, "worktrees.json");
        File.Exists(file).Should().BeTrue("the scan writes its report");
        Json(File.ReadAllText(file))["scannedAt"]!.GetValue<string>().Should().Be(report["scannedAt"]!.GetValue<string>());
        world.Runner.Requests.Should().BeEmpty("the scan starts no process — git's questions are a later story's");
    }

    [Fact]
    public void A_scan_without_json_prints_a_summary_per_root()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), WindowsOnly);
        using var world = new Roots("scan-text");
        world.File("work/docs/readme.txt", "hello");
        world.Run(world.Stdin(world.Work("")), "worktrees", "roots", "set", "--json");

        var (exit, stdout, _) = world.Run(world.Stdin(), "worktrees", "scan");

        exit.Should().Be((int)ExitCode.Ok);
        stdout.Should().Contain(world.Path("")).And.Contain("folders");
    }

    [Fact]
    public void A_root_that_broke_a_rule_since_it_was_set_is_refused_at_the_scan_with_its_reason()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), WindowsOnly);
        using var world = new Roots("scan-hand-edited");
        Directory.CreateDirectory(Path.GetDirectoryName(world.Paths.UserConfigFile)!);
        File.WriteAllText(world.Paths.UserConfigFile, new JsonObject { ["worktrees"] = new JsonObject { ["roots"] = new JsonArray(JsonValue.Create(world.Sandbox.Path)) } }.ToJsonString());

        var (exit, stdout, stderr) = world.Run(world.Stdin(), "worktrees", "scan", "--json");

        exit.Should().Be((int)ExitCode.Ok, stderr);
        Json(stdout)["roots"]!.AsArray().Single()!["refused"]!.GetValue<string>().Should().Contain("profile");
    }

    // ---------- the world ----------

    /// <summary>A Windows layout over a <c>testsToDelete-</c> folder, a recording runner that must stay empty, and the program run over it.</summary>
    private sealed class Roots : IDisposable
    {
        public Roots(string purpose)
        {
            Sandbox = new ToDeleteRoot(purpose);
            Paths = new WindowsHostPaths(WindowsEnvironment.Sandboxed(Sandbox.Path));
            Files = new PhysicalFileSystem(Paths) { OwnersAreThisProcess = true };
            Sandbox.Dir("work");
        }

        public ToDeleteRoot Sandbox { get; }

        public WindowsHostPaths Paths { get; }

        public PhysicalFileSystem Files { get; }

        public RecordingCommandRunner Runner { get; } = new();

        /// <summary>The folder <c>work\&lt;relative&gt;</c> (created); the root itself for an empty name.</summary>
        public string Work(string relative) => relative.Length == 0 ? Sandbox.Dir("work") : Sandbox.Dir($"work/{relative}");

        /// <summary>Where <c>work\&lt;relative&gt;</c> is, as the key spells it.</summary>
        public string Path(string relative) => relative.Length == 0 ? Sandbox.Under("work") : Sandbox.Under($"work/{relative}");

        public string File(string relative, string content) => Sandbox.File(relative, content);

        public Stream Stdin(params string[] lines) => new MemoryStream(Encoding.UTF8.GetBytes(string.Concat(lines.Select(l => l + "\n"))));

        public (int Exit, string Stdout, string Stderr) Run(Stream stdin, params string[] argv) => Run(stdin, TimeSpan.Zero, argv);

        public (int Exit, string Stdout, string Stderr) Run(Stream stdin, TimeSpan ceiling, params string[] argv)
        {
            var clock = new FixedTimeProvider(DateTimeOffset.UtcNow);
            var host = new CliHost(Paths, Files, clock, Runner) { Probe = new FakeProbe(HostSide.Windows, clock), StandardInput = () => stdin, StdinCeiling = ceiling };
            return CliRun.Over(host, argv);
        }

        /// <summary>What the user layer holds for the roots key now (re-read from disk).</summary>
        public IReadOnlyList<string> StoredRoots() =>
            ConfigLoader.Load(Paths, Files).Config.Entry(ConfigKeys.Find("worktrees.roots")!).Value is ConfigValue.TextList list ? list.Values : [];

        public void Dispose() => Sandbox.Dispose();
    }
}
