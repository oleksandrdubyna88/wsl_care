using System.Text.Json;

using FluentAssertions;

using WslCare.Cli;
using WslCare.Core.Actions.Engine;
using WslCare.Core.Json;
using WslCare.TestSupport;

namespace WslCare.Scenarios;

/// <summary>
/// WR-D3 end to end over the BUILT CLI (PLAN_windows_runner.md §3.2, §3.3): on the Windows binary <c>tasks run watch --json</c> really
/// supervises — it creates its OWN built binary suspended inside a kill-on-close job as <c>watch --timer --json</c>, resumes it there
/// and waits. Since WR-D4 that worker is the Windows watch: inside the SAME <see cref="ScenarioHome"/> sandbox (the worker inherits
/// <c>WSL_CARE_ROOT</c>) it samples into the sandboxed ledger, writes no history line, and its answer is the supervisor's stdout (WR-D3
/// review n4: this flow never starts a watch outside the sandbox). <c>tasks run archive</c> (WR-D5) supervises the archive body: the dry window previews, a real one runs
/// <c>archive run</c>, both inside the same sandbox. On the distro's binary the verb is refused with exit 2.
/// </summary>
public sealed class WindowsTasksRunFlows
{
    /// <summary>The REAL supervisor over the built binary on every Windows leg. Unelevated, the worker samples; on an ELEVATED runner
    /// (CI's Windows runner is one) the worker refuses itself with 81 and the supervisor passes that through — so CI still runs the
    /// supervisor end to end (own review M1).</summary>
    [Fact]
    public async Task The_built_binary_supervises_its_own_watch_which_samples_into_the_sandbox()
    {
        WindowsWatchFlows.OnWindowsAsTheTestExecutable();
        using var home = WindowsWatchFlows.Home("tasks-run-watch");

        var result = await home.RunAsync("tasks", "run", "watch", "--json");

        if (Environment.IsPrivilegedProcess)
        {
            ElevatedWorkerRefused(home, result);
            return;
        }

        result.Exit.Should().Be((int)ExitCode.Ok, "the supervised worker's own code: the Windows watch sampled; {0}", result.Stderr);
        result.Stderr.Should().Contain("tasks run watch: the worker exited 0");
        WindowsWatchFlows.SampledOnly(home, JsonSerializer.Deserialize(result.Stdout, WslCareJsonContext.Default.WatchReport)!);
    }

    /// <summary>The elevated leg on ANY Windows run: the sandbox's privilege claim makes the worker elevated, so the supervisor's own
    /// pass-through of 81 is checked on an unelevated machine too.</summary>
    [Fact]
    public async Task The_supervised_watch_of_an_elevated_run_is_refused_with_81_and_writes_nothing()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "the Windows binary's verb: the Windows legs");
        using var home = WindowsWatchFlows.Home("tasks-run-watch-elevated");
        home.ClaimsRoot = true;

        ElevatedWorkerRefused(home, await home.RunAsync("tasks", "run", "watch", "--json"));
    }

    /// <summary>The supervisor started the worker and passed its refusal through: 81, the worker's own line, nothing written.</summary>
    private static void ElevatedWorkerRefused(ScenarioHome home, ChildResult result)
    {
        result.Exit.Should().Be((int)ExitCode.NotAsRoot, "the elevated worker refuses itself and the supervisor passes its code through; {0}", result.Stderr);
        result.Stderr.Should().Contain("tasks run watch: the worker exited 81").And.Contain("NOT elevated");
        result.Stdout.Should().BeEmpty();
        File.Exists(home.Paths.Rules.Join(home.Paths.StateDirectory, Core.Mcp.McpCpuLedger.FileName)).Should().BeFalse("the refused worker sampled nothing");
        File.Exists(Core.Records.RunHistory.File(home.Paths)).Should().BeFalse();
    }

    /// <summary>WR-D5 over the BUILT CLI: <c>tasks run archive --json</c> with the dry-run window open (the default) supervises the
    /// binary's own <c>archive preview --json</c> inside the sandbox — a due session in the sandbox's home stays where it is, the base
    /// stays empty, the run log says what a real run would move, and the first-week stamp lands in the sandbox. An ELEVATED runner (CI)
    /// has its worker refuse itself with 81, which the supervisor passes through.</summary>
    [Fact]
    public async Task The_built_binary_supervises_a_dry_archive_which_previews_and_moves_nothing()
    {
        WindowsWatchFlows.OnWindowsAsTheTestExecutable();
        using var home = new ScenarioHome("tasks-run-archive-dry");
        var (session, baseFolder) = ADueSession(home);

        var result = await home.RunAsync("tasks", "run", "archive", "--json");

        result.Stderr.Should().Contain("tasks run archive: a dry run").And.Contain("starting").And.Contain("archive preview --json");
        if (!Environment.IsPrivilegedProcess)
        {
            File.Exists(Core.Actions.Engine.DryRunWindow.File(home.Paths)).Should().BeTrue("the first task run starts the dry-run week, inside the sandbox");
        }

        File.ReadAllText(session).Should().Be("the transcript", "a dry body moves nothing");
        Directory.GetFileSystemEntries(baseFolder).Should().BeEmpty("a preview writes nothing to the base");
        if (Environment.IsPrivilegedProcess)
        {
            result.Exit.Should().Be((int)ExitCode.NotAsRoot, result.Stderr);
            result.Stderr.Should().Contain("tasks run archive: the worker exited 81");
            File.Exists(Core.Actions.Engine.DryRunWindow.File(home.Paths)).Should().BeFalse("an elevated supervisor starts no dry-run week");
            return;
        }

        result.Exit.Should().Be((int)ExitCode.Ok, result.Stderr);
        result.Stderr.Should().Contain("tasks run archive: the worker exited 0").And.Contain("would move 1 session(s), 14 bytes");
        var preview = JsonSerializer.Deserialize(result.Stdout, WslCareJsonContext.Default.ArchivePreviewReport)!;
        preview.Agents.Single(a => a.Id == "claude-code").DueUnits.Should().Be(1);
    }

    /// <summary>The same with the window closed (the machine layer says <c>dryRun</c> false and the week's stamp is eight days old,
    /// both in the sandbox): the worker is <c>archive run --json</c>, whose answer is the last line of stdout.</summary>
    [Fact]
    public async Task The_built_binary_supervises_a_real_archive_which_runs_archive_run()
    {
        WindowsWatchFlows.OnWindowsAsTheTestExecutable();
        using var home = new ScenarioHome("tasks-run-archive-real");
        _ = ADueSession(home);
        Directory.CreateDirectory(Path.GetDirectoryName(home.Paths.MachineConfigFile)!);
        File.WriteAllText(home.Paths.MachineConfigFile, """{ "dryRun": false }""");
        Directory.CreateDirectory(home.Paths.StateDirectory);
        File.WriteAllBytes(Core.Actions.Engine.DryRunWindow.File(home.Paths), JsonSerializer.SerializeToUtf8Bytes(new Core.Actions.Engine.FirstTimerRun(Core.SchemaVersion.Current, DateTimeOffset.UtcNow.AddDays(-8)), WslCareJsonContext.Default.FirstTimerRun));

        var result = await home.RunAsync("tasks", "run", "archive", "--json");

        if (Environment.IsPrivilegedProcess)
        {
            // An elevated supervisor previews whatever the window says and touches no stamp (own review m1); its worker then refuses with 81.
            result.Exit.Should().Be((int)ExitCode.NotAsRoot, result.Stderr);
            result.Stderr.Should().Contain("archive preview --json").And.Contain("an elevated process");
            return;
        }

        result.Stderr.Should().Contain("starting").And.Contain("archive run --json").And.NotContain("a dry run");

        var answer = result.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries).Last();
        var report = JsonSerializer.Deserialize(answer, WslCareJsonContext.Default.ArchiveRunReport)!;
        result.Exit.Should().Be((int)ExitCode.Ok, result.Stderr);
        report.Outcome.Should().Be(Core.Archive.RunOutcomes.Done, report.Stop);
        report.Agents.Single(a => a.Id == "claude-code").Copied.Should().Be(1);
        result.Stderr.Should().Contain("tasks run archive: the worker exited 0").And.Contain("tasks run archive: a real run");
    }

    /// <summary>A Claude Code session in the sandbox's home, forty days old, and an archive base in the sandbox.</summary>
    private static (string Session, string BaseFolder) ADueSession(ScenarioHome home)
    {
        var session = Path.Combine(home.Paths.Home, ".claude", "projects", "p", "s1.jsonl");
        Directory.CreateDirectory(Path.GetDirectoryName(session)!);
        File.WriteAllText(session, "the transcript");
        File.SetLastWriteTimeUtc(session, DateTime.UtcNow.AddDays(-40));
        var baseFolder = Directory.CreateDirectory(Path.Combine(home.SandboxRoot, "archive-base")).FullName;
        Directory.CreateDirectory(Path.GetDirectoryName(home.Paths.UserConfigFile)!);
        File.WriteAllText(home.Paths.UserConfigFile, new System.Text.Json.Nodes.JsonObject { ["archive"] = new System.Text.Json.Nodes.JsonObject { ["baseFolder"] = baseFolder } }.ToJsonString());
        return (session, baseFolder);
    }

    /// <summary>WR-T2 over the BUILT CLI in a sandbox <c>%TEMP%</c>: the real supervisor starts the built binary's <c>act A23 --timer --confirm
    /// --json</c> inside a kill-on-close job. In A23's first (dry) week it previews and deletes nothing, and starts A23's own week, never the
    /// common one. On an elevated account the worker refuses itself with 81.</summary>
    [Fact]
    public async Task The_built_binary_supervises_a_dry_cleanup_which_previews_and_deletes_nothing()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "the Windows binary's verb: the Windows legs");
        using var home = new ScenarioHome("tasks-run-cleanup-dry");
        home.Paths.TempDirectory.Should().StartWith(home.SandboxRoot, "the scenario's %TEMP% is a sandbox folder, never the real one");
        var old = TempTrees.Make(home.Paths.TempDirectory, "coai-old", 30, ("a.txt", 30));

        var result = await home.RunAsync("tasks", "run", "cleanup", "--json");

        result.Stderr.Should().Contain("starting").And.Contain("act A23 --timer --confirm --json").And.Contain("A23's dry-run window");
        Directory.Exists(old).Should().BeTrue("a dry week deletes nothing");
        if (Environment.IsPrivilegedProcess)
        {
            result.Exit.Should().Be((int)ExitCode.NotAsRoot, result.Stderr);
            result.Stderr.Should().Contain("the worker exited 81");
            File.Exists(DryRunWindow.File(home.Paths, "A23")).Should().BeFalse();
            return;
        }

        result.Exit.Should().Be((int)ExitCode.Ok, result.Stderr);
        result.Stderr.Should().Contain("the worker exited 0");
        File.Exists(DryRunWindow.File(home.Paths, "A23")).Should().BeTrue();
        File.Exists(DryRunWindow.File(home.Paths)).Should().BeFalse("never the common stamp");
    }

    [Fact]
    public async Task The_built_binary_supervises_a_real_cleanup_which_deletes_the_old_allowed_tree()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "the Windows binary's verb: the Windows legs");
        using var home = new ScenarioHome("tasks-run-cleanup-real");
        home.Paths.TempDirectory.Should().StartWith(home.SandboxRoot);
        var temp = home.Paths.TempDirectory;
        var old = TempTrees.Make(temp, "coai-old", 30, ("a.txt", 30));
        var other = TempTrees.Make(temp, "sometool-old", 30, ("x.txt", 30));
        var claude = TempTrees.Make(temp, "claude", 30, ("log.txt", 30));
        Directory.CreateDirectory(Path.GetDirectoryName(home.Paths.MachineConfigFile)!);
        File.WriteAllText(home.Paths.MachineConfigFile, """{ "dryRun": false }""");
        Directory.CreateDirectory(home.Paths.StateDirectory);
        File.WriteAllText(DryRunWindow.File(home.Paths, "A23"), $$"""{ "schemaVersion": 1, "at": "{{DateTimeOffset.UtcNow.AddDays(-8):O}}" }""");

        var result = await home.RunAsync("tasks", "run", "cleanup", "--json");

        if (Environment.IsPrivilegedProcess)
        {
            result.Exit.Should().Be((int)ExitCode.NotAsRoot, result.Stderr);
            Directory.Exists(old).Should().BeTrue();
            return;
        }

        result.Exit.Should().Be((int)ExitCode.Ok, result.Stderr);
        Directory.Exists(old).Should().BeFalse();
        Directory.Exists(other).Should().BeTrue("not on the allow-list");
        Directory.Exists(claude).Should().BeTrue("claude is never touched");
    }

    [Fact]
    public async Task The_distro_binary_refuses_the_run_with_exit_2_and_nothing_on_stdout()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "the distro's binary: the Linux legs");
        using var home = new ScenarioHome("tasks-run-linux");

        var result = await home.RunAsync("tasks", "run", "watch", "--json");

        result.Exit.Should().Be((int)ExitCode.Usage);
        result.Stdout.Should().BeEmpty();
        result.Stderr.Should().Contain("Windows binary");
    }
}
