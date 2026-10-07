using System.Text.Json;
using System.Text.Json.Nodes;

using FluentAssertions;

using WslCare.Cli;
using WslCare.Core.Archive;
using WslCare.Core.Hosting;
using WslCare.Core.Json;
using WslCare.TestSupport;

namespace WslCare.Scenarios;

/// <summary>
/// Plan §15r E9.S2b against the BUILT CLI: <c>archive run</c>, <c>archive status</c>, <c>archive reconcile --scan</c> — as root each is
/// refused; without a base the run answers <c>no-base</c>; on the distribution's legs a due session is copied by one run and removed
/// by a later one; and the run is KILLED (by its own pid, <c>WSL_CARE_TEST_ARCHIVE_KILL</c>, sandbox only) at each of the 14 points,
/// also with the base gone while the next run reconciles — after which the next runs finish it and nothing is lost.
/// </summary>
public sealed class ArchiveRunFlows
{
    private const string LinuxOnly = "the archive moves on the distribution's legs (the Windows side has no open-file check before E9.S5)";

    private const string MountInfo =
        "523 504 8:96 / / rw,relatime - ext4 /dev/sdg rw,discard,errors=remount-ro,data=ordered\n" +
        "479 523 0:154 / /mnt/v rw,relatime - 9p V: rw,aname=drvfs;path=V:;uid=1000;gid=1000;metadata;symlinkroot=/mnt/,cache=0x5,access=client,msize=65536,trans=fd,rfd=3,wfd=3\n";

    private const string Main = "/home/me/.claude/projects/p/s1.jsonl";
    private const string Companion = "/home/me/.claude/projects/p/s1/subagents/a.jsonl";

    /// <summary>A home whose base is the network drive's folder (placed by the sandbox's mount table) and a due Claude Code session.</summary>
    private static async Task<ScenarioHome> Archived(string purpose)
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), LinuxOnly);
        var home = new ScenarioHome(purpose);
        var paths = (LinuxHostPaths)home.Paths;
        var mountInfo = paths.DistroPath("/proc/self/mountinfo");
        Directory.CreateDirectory(Path.GetDirectoryName(mountInfo)!);
        await File.WriteAllTextAsync(mountInfo, MountInfo, TestContext.Current.CancellationToken);
        Directory.CreateDirectory(paths.DistroPath("/mnt/v/ai-archive"));
        (await home.RunAsync("config", "set", "archive.baseFolder", "/mnt/v/ai-archive")).Exit.Should().Be((int)ExitCode.Ok);
        foreach (var (file, content) in new[] { (Main, "the transcript"), (Companion, "a subagent") })
        {
            var path = paths.DistroPath(file);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllTextAsync(path, content, TestContext.Current.CancellationToken);
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddDays(-20));
        }

        return home;
    }

    /// <summary>The answer — the LAST line, always (review m1: no heartbeat may follow it).</summary>
    private static ArchiveRunReport Answer(ChildResult result)
    {
        var last = result.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries).Last();
        last.Should().NotStartWith("{\"progress\"", "the answer is the last line of archive run --json");
        return JsonSerializer.Deserialize(last, WslCareJsonContext.Default.ArchiveRunReport) ?? throw new InvalidOperationException(result.Stderr);
    }

    /// <summary>The removal is due: every in-flight entry's <c>archivedAtUtc</c> moved two days back (a run a day later, without waiting).</summary>
    private static void ADayLater(ScenarioHome home)
    {
        var inflight = Path.Combine(new ArchiveState(home.Paths, new Core.Files.PhysicalFileSystem(home.Paths)).Folder, "inflight.json");
        if (!File.Exists(inflight))
        {
            return;
        }

        var node = JsonNode.Parse(File.ReadAllText(inflight))!;
        foreach (var entry in node["entries"]!.AsArray())
        {
            if ((string?)entry!["state"] == InflightStates.Archived)
            {
                entry["archivedAtUtc"] = DateTimeOffset.UtcNow.AddDays(-2).ToString("O");
            }
        }

        File.WriteAllText(inflight, node.ToJsonString());
    }

    [Fact]
    public async Task Run_status_and_scan_as_root_are_refused_with_their_own_exit_code()
    {
        using var home = new ScenarioHome("archive-run-root") { ClaimsRoot = true };

        foreach (var args in new[] { new[] { "archive", "run", "--json" }, ["archive", "status", "--json"], ["archive", "reconcile", "--scan", "--json"], ["archive", "restore", "--entry", "0123456789abcdef", "--json"], ["archive", "list", "--json"] })
        {
            var result = await home.RunAsync(args);
            result.Exit.Should().Be((int)ExitCode.NotAsRoot, string.Join(' ', args));
            result.Stdout.Should().BeEmpty();
        }
    }

    [Fact]
    public async Task Without_a_base_the_run_answers_no_base_and_status_answers_free()
    {
        using var home = new ScenarioHome("archive-run-nobase");
        Assert.SkipWhen(Environment.IsPrivilegedProcess, "root (or an elevated Windows account, the CI runner's) is refused by every archive verb (81): that refusal is the row above");

        var run = await home.RunAsync("archive", "run", "--json");
        var status = await home.RunAsync("archive", "status", "--json");

        run.Exit.Should().Be((int)ExitCode.Ok, run.Stderr);
        Answer(run).Outcome.Should().Be(RunOutcomes.NoBase);
        status.Exit.Should().Be((int)ExitCode.Ok, status.Stderr);
        JsonSerializer.Deserialize(status.Stdout, WslCareJsonContext.Default.ArchiveStatusReport)!.Lock.State.Should().Be("free");
    }

    [Fact]
    public async Task A_due_session_is_copied_by_one_run_and_removed_by_a_later_one()
    {
        using var home = await Archived("archive-run-cycle");
        Assert.SkipWhen(home.Paths.Side == HostSide.Windows, LinuxOnly);
        var paths = (LinuxHostPaths)home.Paths;

        var first = await home.RunAsync("archive", "run", "--agent", "claude-code", "--json");

        first.Exit.Should().Be((int)ExitCode.Ok, first.Stderr);
        Answer(first).Agents.Single().Copied.Should().Be(1, Answer(first).Stop);
        first.Stdout.Should().Contain("{\"progress\":\"file\"", "a progress line per file, naming nothing");
        File.Exists(paths.DistroPath(Main)).Should().BeTrue("phase 1 touches nothing at the source");
        var status = JsonSerializer.Deserialize((await home.RunAsync("archive", "status", "--json")).Stdout, WslCareJsonContext.Default.ArchiveStatusReport)!;
        status.Inflight.Should().ContainSingle().Which.State.Should().Be(InflightStates.Archived);

        ADayLater(home);
        var second = await home.RunAsync("archive", "run", "--agent", "claude-code", "--json");

        second.Exit.Should().Be((int)ExitCode.Ok, second.Stderr);
        Answer(second).Agents.Single().Removed.Should().Be(1, Answer(second).Stop);
        File.Exists(paths.DistroPath(Main)).Should().BeFalse();
        Directory.EnumerateFiles(paths.DistroPath("/mnt/v/ai-archive"), "s1.jsonl", SearchOption.AllDirectories).Should().ContainSingle();
    }

    public static TheoryData<string, bool, bool> KillPoints()
    {
        var phaseOne = new[] { MoveSteps.Intent, MoveSteps.CopyChunk, MoveSteps.FinalFlushed, "ExclusiveCreated", MoveSteps.BetweenFiles, "Appended", MoveSteps.IndexFlushed };
        var phaseTwo = new[] { MoveSteps.QuarantineStart, "Renamed", "RemovalHashed", "Removed", MoveSteps.FoldersStart, MoveSteps.CloseStart, MoveSteps.Closed };
        var data = new TheoryData<string, bool, bool>();
        foreach (var unreachable in new[] { false, true })
        {
            foreach (var point in phaseOne)
            {
                data.Add(point, false, unreachable);
            }

            foreach (var point in phaseTwo)
            {
                data.Add(point, true, unreachable);
            }
        }

        return data;
    }

    /// <summary>Plan §15r *Test plan*: the BUILT child killed at each point; with <paramref name="baseGone"/> the base is missing for
    /// the next run (it refuses and touches nothing) and back for the one after. Then the runs finish the session: nothing is lost,
    /// nothing stays under a quarantine name, nothing is left on its way.</summary>
    [Theory]
    [MemberData(nameof(KillPoints))]
    public async Task A_run_killed_at_each_point_is_finished_by_the_next_runs_and_loses_nothing(string point, bool inRemoval, bool baseGone)
    {
        using var home = await Archived("archive-run-kill");
        Assert.SkipWhen(home.Paths.Side == HostSide.Windows, LinuxOnly);
        var paths = (LinuxHostPaths)home.Paths;
        if (inRemoval)
        {
            (await home.RunAsync("archive", "run", "--agent", "claude-code", "--json")).Exit.Should().Be((int)ExitCode.Ok);
            ADayLater(home);
        }

        home.Extra[CliHost.ArchiveKillVariable] = point;
        var killed = await home.RunAsync("archive", "run", "--agent", "claude-code", "--json");
        home.Extra.Remove(CliHost.ArchiveKillVariable);
        killed.Exit.Should().NotBe((int)ExitCode.Ok, $"the run was killed at {point}");

        if (baseGone)
        {
            var parked = paths.DistroPath("/mnt/v/ai-archive-away");
            Directory.Move(paths.DistroPath("/mnt/v/ai-archive"), parked);
            var refused = await home.RunAsync("archive", "run", "--agent", "claude-code", "--json");
            Answer(refused).Outcome.Should().Be(RunOutcomes.Refused, "a base that is not there is never written and nothing is removed");
            Directory.Move(parked, paths.DistroPath("/mnt/v/ai-archive"));
        }

        for (var run = 0; run < 3; run++)
        {
            ADayLater(home);
            var finishing = await home.RunAsync("archive", "run", "--agent", "claude-code", "--json");
            finishing.Exit.Should().Be((int)ExitCode.Ok, finishing.Stderr + finishing.Stdout);
        }

        Directory.EnumerateFiles(paths.DistroPath("/home/me/.claude"), "*" + ArchiveNames.QuarantineMark + "*", SearchOption.AllDirectories).Should().BeEmpty();
        File.Exists(paths.DistroPath(Main)).Should().BeFalse("the session was moved in the end");
        var archived = Directory.EnumerateFiles(paths.DistroPath("/mnt/v/ai-archive"), "*", SearchOption.AllDirectories).Select(File.ReadAllText).ToList();
        archived.Should().Contain("the transcript").And.Contain("a subagent");
        JsonSerializer.Deserialize((await home.RunAsync("archive", "status", "--json")).Stdout, WslCareJsonContext.Default.ArchiveStatusReport)!.Inflight.Should().BeEmpty();
    }

    [Fact]
    public async Task The_scan_reindexes_a_copy_no_index_line_names_and_touches_nothing_at_the_source()
    {
        using var home = await Archived("archive-run-scan");
        Assert.SkipWhen(home.Paths.Side == HostSide.Windows, LinuxOnly);
        var paths = (LinuxHostPaths)home.Paths;
        var stray = paths.DistroPath($"/mnt/v/ai-archive/claude-code/2026/09/{SideName.OfThisProcess(HostSide.Wsl)}/projects/p/old.jsonl");
        Directory.CreateDirectory(Path.GetDirectoryName(stray)!);
        await File.WriteAllTextAsync(stray, "a copy a crash left", TestContext.Current.CancellationToken);

        var result = await home.RunAsync("archive", "reconcile", "--scan", "--json");

        result.Exit.Should().Be((int)ExitCode.Ok, result.Stderr);
        Answer(result).Scan.Recovered.Should().Be(1);
        File.ReadAllText(Path.Combine(Path.GetDirectoryName(stray)!, "..", "..", "index.jsonl")).Should().Contain("\"event\":\"recovered\"");
        File.Exists(paths.DistroPath(Main)).Should().BeTrue();
    }

    /// <summary>Plan §15r E9.S3 through the built CLI: a session archived and removed is listed, restored by its session path (exit 0,
    /// both files back with the archived bytes), listed as restored; a second restore finds it already there; a file planted on the
    /// share and re-indexed by a scan is listed unverified and refused without --accept-unverified (exit 1).</summary>
    [Fact]
    public async Task A_removed_session_is_listed_restored_and_listed_restored_through_the_built_cli()
    {
        using var home = await Archived("archive-restore-cycle");
        Assert.SkipWhen(home.Paths.Side == HostSide.Windows, LinuxOnly);
        var paths = (LinuxHostPaths)home.Paths;
        (await home.RunAsync("archive", "run", "--agent", "claude-code", "--json")).Exit.Should().Be((int)ExitCode.Ok);
        ADayLater(home);
        (await home.RunAsync("archive", "run", "--agent", "claude-code", "--json")).Exit.Should().Be((int)ExitCode.Ok);
        File.Exists(paths.DistroPath(Main)).Should().BeFalse();

        var listed = JsonSerializer.Deserialize((await home.RunAsync("archive", "list", "--agent", "claude-code", "--json")).Stdout, WslCareJsonContext.Default.ArchiveListReport)!;
        listed.Entries.Should().ContainSingle().Which.Status.Should().Be(ArchiveIndex.Events.SourceRemoved);

        var restore = await home.RunAsync("archive", "restore", "--agent", "claude-code", "--session", "projects/p/s1.jsonl", "--json");

        restore.Exit.Should().Be((int)ExitCode.Ok, restore.Stdout + restore.Stderr);
        Answer(restore).Restore.Restored.Should().Be(1);
        File.ReadAllText(paths.DistroPath(Main)).Should().Be("the transcript");
        File.ReadAllText(paths.DistroPath(Companion)).Should().Be("a subagent");
        JsonSerializer.Deserialize((await home.RunAsync("archive", "list", "--json")).Stdout, WslCareJsonContext.Default.ArchiveListReport)!
            .Entries.Single().Status.Should().Be(ArchiveIndex.Events.Restored);
        Answer(await home.RunAsync("archive", "restore", "--entry", listed.Entries[0].EntryId, "--json")).Restore.AlreadyThere.Should().Be(1);

        var stray = paths.DistroPath($"/mnt/v/ai-archive/claude-code/{listed.Entries[0].Month}/{SideName.OfThisProcess(HostSide.Wsl)}/projects/p/planted.jsonl");
        Directory.CreateDirectory(Path.GetDirectoryName(stray)!);
        await File.WriteAllTextAsync(stray, "a file someone put on the share", TestContext.Current.CancellationToken);
        (await home.RunAsync("archive", "reconcile", "--scan", "--json")).Exit.Should().Be((int)ExitCode.Ok);
        var planted = JsonSerializer.Deserialize((await home.RunAsync("archive", "list", "--json")).Stdout, WslCareJsonContext.Default.ArchiveListReport)!
            .Entries.Single(e => e.Key == "projects/p/planted.jsonl");
        planted.Verified.Should().BeFalse("a recovered line is never verified (D4)");

        var refused = await home.RunAsync("archive", "restore", "--entry", planted.EntryId, "--json");

        refused.Exit.Should().Be((int)ExitCode.RunFailed);
        Answer(refused).Restore.Sessions.Single().Note.Should().Contain("--accept-unverified");
        File.Exists(paths.DistroPath("/home/me/.claude/projects/p/planted.jsonl")).Should().BeFalse("nothing planted on the share reaches the agent's folder");
    }
}
