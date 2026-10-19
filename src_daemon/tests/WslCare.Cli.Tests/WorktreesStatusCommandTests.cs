using System.Text.Json;
using System.Text.Json.Nodes;

using FluentAssertions;

using WslCare.Cli;
using WslCare.Core.Hosting;
using WslCare.Core.Json;
using WslCare.Core.Worktrees;
using WslCare.TestSupport;

namespace WslCare.Cli.Tests;

/// <summary>
/// E19.S3: the Windows binary's <c>status</c> carries <c>worktrees</c> — the last scan's report, summarised, with the basis
/// <c>{source: scan, scannedAt, ageSeconds}</c> — and the verdict <c>worktrees.reclaimable</c>, which in this story is always
/// <c>unknown</c>. The distro's <c>status</c> carries neither.
/// </summary>
public sealed class WorktreesStatusCommandTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 15, 12, 0, 0, TimeSpan.Zero);

    private const string WindowsOnly = "a CliHost over a Windows layout is built on Windows only (its probe)";

    private static WorktreeReport Report(DateTimeOffset scannedAt) =>
        new(1, scannedAt, 4, true,
        [new RootReport(@"D:\w", null, 3, 2, 0, 1, 0, 0, 0, 0, 0, 900, new IdleOver(3, 1, 400), new BuildOutputTotals(new Tally(2, 5_000), new Tally(1, 3_000), new Tally(0, 0), new Tally(0, 0), []), new UnknownTotals(0, string.Empty), [])],
        [], new ReclaimableReport(false, "git has not confirmed the candidates yet (E19.S4)"));

    private static JsonNode Status(ToDeleteRoot root, string? userConfig, WorktreeReport? report, out string stdout) =>
        StatusOverText(root, userConfig, report is null ? null : JsonSerializer.Serialize(report, WslCareJsonContext.Default.WorktreeReport), out stdout);

    /// <summary>The Windows <c>status</c> over a <c>worktrees.json</c> holding exactly <paramref name="reportText"/> (none when null).</summary>
    private static JsonNode StatusOverText(ToDeleteRoot root, string? userConfig, string? reportText, out string stdout)
    {
        var paths = new WindowsHostPaths(WindowsEnvironment.Sandboxed(root.Path));
        var files = new Core.Files.PhysicalFileSystem(paths) { OwnersAreThisProcess = true };
        if (userConfig is not null)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(paths.UserConfigFile)!);
            File.WriteAllText(paths.UserConfigFile, userConfig);
        }

        if (reportText is not null)
        {
            Directory.CreateDirectory(paths.StateDirectory);
            File.WriteAllText(Path.Combine(paths.StateDirectory, "worktrees.json"), reportText);
        }

        var clock = new FixedTimeProvider(Now);
        var host = new CliHost(paths, files, clock, new RecordingCommandRunner()) { Probe = new FakeProbe(HostSide.Windows, clock), Wait = (_, _) => Task.CompletedTask };
        var (exit, answer, stderr) = CliRun.Over(host, "status", "--json");
        exit.Should().Be((int)ExitCode.Ok, stderr);
        stdout = answer;
        return JsonNode.Parse(answer)!;
    }

    private static JsonNode Verdict(JsonNode status) => status["verdicts"]!.AsArray().Single(v => v!["id"]!.GetValue<string>() == "worktrees.reclaimable")!;

    [Fact]
    public void With_no_root_the_status_says_unknown_and_how_to_choose_folders()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), WindowsOnly);
        using var root = new ToDeleteRoot("status-noroot");

        var status = Status(root, null, null, out _);

        Verdict(status)["level"]!.GetValue<string>().Should().Be("unknown");
        Verdict(status)["reason"]!.GetValue<string>().Should().Be("no root configured — choose folders (wsl-care.exe worktrees roots set)");
        status["worktrees"]!["available"]!.GetValue<bool>().Should().BeFalse();
    }

    [Fact]
    public void A_scan_is_read_with_its_basis_and_the_verdict_stays_unknown()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), WindowsOnly);
        using var root = new ToDeleteRoot("status-scan");
        var work = root.Dir("work");

        var status = Status(root, new JsonObject { ["worktrees"] = new JsonObject { ["roots"] = new JsonArray(JsonValue.Create(work)) } }.ToJsonString(), Report(Now.AddHours(-2)), out var stdout);

        var section = status["worktrees"]!;
        section["available"]!.GetValue<bool>().Should().BeTrue();
        section["basis"]!["source"]!.GetValue<string>().Should().Be("scan");
        section["basis"]!["ageSeconds"]!.GetValue<double>().Should().Be(7200);
        section["basis"]!["scannedAt"]!.GetValue<DateTimeOffset>().Should().Be(Now.AddHours(-2));
        section["stale"]!.GetValue<bool>().Should().BeFalse();
        section["report"]!["reclaimable"]!["available"]!.GetValue<bool>().Should().BeFalse();
        Verdict(status)["level"]!.GetValue<string>().Should().Be("unknown");
        Verdict(status)["reason"]!.GetValue<string>().Should().Contain("git has not confirmed the candidates yet (E19.S4)");
        stdout.Should().NotContain("reclaimableBytes");
    }

    [Fact]
    public void A_scan_older_than_the_setting_says_it_is_stale()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), WindowsOnly);
        using var root = new ToDeleteRoot("status-stale");
        var work = root.Dir("work");

        var status = Status(root, new JsonObject { ["worktrees"] = new JsonObject { ["roots"] = new JsonArray(JsonValue.Create(work)) } }.ToJsonString(), Report(Now.AddDays(-10)), out _);

        status["worktrees"]!["stale"]!.GetValue<bool>().Should().BeTrue();
        Verdict(status)["reason"]!.GetValue<string>().Should().Contain("10 days old");
    }

    /// <summary>Fable review of E19.S3, recommendation 4, end to end: a <c>worktrees.json</c> a crash or a person left in any shape never takes
    /// the whole <c>status</c> down — the extension's poll reads it — it says the report could not be read, and the verdict stays unknown.</summary>
    [Theory]
    [InlineData("""{"schemaVersion":1}""", "it lacks its roots")]
    [InlineData("""{"schemaVersion":1,"scannedAt":"2026-10-15T10:00:00+00:00","roots":[""", "is not a worktree report")]
    [InlineData("""{"schemaVersion":1,"scannedAt":"2026-10-15T10:00:00+00:00","entries":4,"complete":true,"roots":[null],"top":[],"reclaimable":{"available":false,"reason":"x"}}""", "it lacks a root")]
    [InlineData("not json at all", "is not a worktree report")]
    public void A_corrupt_report_makes_status_say_it_could_not_be_read_never_fail(string text, string why)
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), WindowsOnly);
        using var root = new ToDeleteRoot("status-corrupt");
        var work = root.Dir("work");

        var status = StatusOverText(root, new JsonObject { ["worktrees"] = new JsonObject { ["roots"] = new JsonArray(JsonValue.Create(work)) } }.ToJsonString(), text, out _);

        status["worktrees"]!["available"]!.GetValue<bool>().Should().BeFalse();
        status["worktrees"]!["reason"]!.GetValue<string>().Should().Contain(why).And.Contain("run wsl-care.exe worktrees scan");
        Verdict(status)["level"]!.GetValue<string>().Should().Be("unknown");
    }

    /// <summary>The same review: the structural counts are summed in long and never overflow into an exception — a crafted report with byte figures
    /// near the top of a long still gives an answer.</summary>
    [Fact]
    public void A_report_with_huge_figures_still_gives_a_status()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), WindowsOnly);
        using var root = new ToDeleteRoot("status-huge");
        var work = root.Dir("work");
        var huge = new BuildOutputTotals(new Tally(int.MaxValue, long.MaxValue), new Tally(0, 0), new Tally(0, 0), new Tally(0, 0), []);
        var one = new RootReport(@"D:\w", null, int.MaxValue, int.MaxValue, int.MaxValue, 1, 0, 0, 0, 0, 0, 900, new IdleOver(3, 1, 400), huge, new UnknownTotals(0, string.Empty), []);
        var report = new WorktreeReport(1, Now.AddHours(-1), 4, true, [one, one with { Path = @"D:\v" }], [], new ReclaimableReport(false, "git has not confirmed the candidates yet (E19.S4)"));

        var status = Status(root, new JsonObject { ["worktrees"] = new JsonObject { ["roots"] = new JsonArray(JsonValue.Create(work)) } }.ToJsonString(), report, out _);

        status["worktrees"]!["available"]!.GetValue<bool>().Should().BeTrue();
        Verdict(status)["level"]!.GetValue<string>().Should().Be("unknown");
    }

    [Fact]
    public void The_distros_status_carries_neither_the_section_nor_the_verdict()
    {
        using var root = new TempRoot("status-distro");
        var paths = new LinuxHostPaths(LinuxEnvironment.Sandboxed(root.Path));
        var clock = new FixedTimeProvider(Now);
        var host = new CliHost(paths, new Core.Files.PhysicalFileSystem(paths), clock, new RecordingCommandRunner()) { Probe = new FakeProbe(HostSide.Wsl, clock), Wait = (_, _) => Task.CompletedTask };

        var (exit, stdout, stderr) = CliRun.Over(host, "status", "--json");

        exit.Should().Be((int)ExitCode.Ok, stderr);
        var status = JsonNode.Parse(stdout)!;
        status["worktrees"].Should().BeNull();
        status["verdicts"]!.AsArray().Select(v => v!["id"]!.GetValue<string>()).Should().NotContain("worktrees.reclaimable");
    }
}
