using System.Text.Json;

using FluentAssertions;

using WslCare.Cli;
using WslCare.Core.Actions.Engine;
using WslCare.Core.Json;
using WslCare.Core.Records;
using WslCare.TestSupport;

namespace WslCare.Scenarios;

/// <summary>
/// WR-T1 end to end over the BUILT CLI (PLAN_windows_runner.md §3.12): A23, the Windows <c>%TEMP%</c> cleanup, on the Windows binary
/// whose layout is a sandbox (<c>WSL_CARE_ROOT</c>) — its <c>%TEMP%</c> is a folder under the scenario's <see cref="TempRoot"/>, and
/// the real one is never resolved. The first Windows-side action through the real engine: an unelevated <c>act A23 --confirm</c>
/// deletes the old allowed tree, keeps a young one, a tree outside the allow-list and the <c>claude</c> folder, records the run where
/// the existing readers look, and an elevated process is refused. The distro's binary refuses A23 naming the Windows host.
/// </summary>
public sealed class WindowsTempFlows
{
    private static string Make(string temp, string relative, int daysOld, string file) => TempTrees.Make(temp, relative, daysOld, (file, daysOld));

    private static ActReport Report(ChildResult result) =>
        JsonSerializer.Deserialize(result.Stdout, WslCareJsonContext.Default.ActReport) ?? throw new InvalidOperationException($"act printed no report: {result.Stderr}");

    [Fact]
    public async Task An_unelevated_confirm_deletes_the_old_allowed_tree_and_keeps_everything_else_and_records_the_run()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "A23 is the Windows binary's action: the Windows legs (the refusal is the other test)");
        Assert.SkipWhen(Environment.IsPrivilegedProcess, "this account is elevated: the child would be refused, which the next test shows");
        using var home = new ScenarioHome("temp-a23");
        var temp = home.Paths.TempDirectory;
        temp.Should().StartWith(home.SandboxRoot, "the scenario's %TEMP% is a sandbox folder, never the real one");
        var old = Make(temp, "coai-old", 30, "a.txt");
        var young = Make(temp, "coai-young", 0, "b.txt");
        var other = Make(temp, "sometool-old", 30, "c.txt");
        var claude = Make(temp, "claude", 30, "log.txt");

        var result = await home.RunAsync("act", "A23", "--confirm", "--json");

        result.Exit.Should().Be((int)ExitCode.Ok, result.Stderr);
        var report = Report(result);
        report.Actions.Should().ContainSingle().Which.Status.Should().Be(ActionStatus.Ran);
        Directory.Exists(old).Should().BeFalse("old, owned, allowed");
        Directory.Exists(young).Should().BeTrue();
        Directory.Exists(other).Should().BeTrue("not on the allow-list");
        Directory.Exists(claude).Should().BeTrue("claude is never touched");
        report.RunId.Should().NotBeNullOrEmpty();
        File.Exists(RunHistory.File(home.Paths)).Should().BeTrue("the run is recorded where the existing readers look");
        File.Exists(Path.Combine(home.Paths.StateDirectory, "temp-cursor.json")).Should().BeTrue();

        var shown = await home.RunAsync("runs", "show", report.RunId!);
        shown.Exit.Should().Be((int)ExitCode.Ok, shown.Stderr);
        shown.Stdout.Should().Contain("A23");
    }

    [Fact]
    public async Task The_timer_is_dry_in_its_first_week_and_deletes_nothing()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "the Windows binary's action");
        Assert.SkipWhen(Environment.IsPrivilegedProcess, "this account is elevated");
        using var home = new ScenarioHome("temp-a23-dry");
        var old = Make(home.Paths.TempDirectory, "coai-old", 30, "a.txt");

        var result = await home.RunAsync("act", "A23", "--timer", "--confirm", "--json");

        result.Exit.Should().Be((int)ExitCode.Ok, result.Stderr);
        Report(result).Actions.Should().ContainSingle().Which.Status.Should().Be(ActionStatus.DryRun);
        Directory.Exists(old).Should().BeTrue();
    }

    [Fact]
    public async Task An_elevated_process_is_refused_and_deletes_nothing()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "the Windows binary's action");
        using var home = new ScenarioHome("temp-a23-elevated") { ClaimsRoot = true };
        var old = Make(home.Paths.TempDirectory, "coai-old", 30, "a.txt");

        var result = await home.RunAsync("act", "A23", "--confirm", "--json");

        result.Exit.Should().Be((int)ExitCode.NotAsRoot);
        result.Stderr.Should().Contain("NOT elevated");
        Directory.Exists(old).Should().BeTrue();
    }

    [Fact]
    public async Task The_distro_binary_refuses_a23_naming_the_windows_host()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "the distro's binary: the Linux legs");
        using var home = new ScenarioHome("temp-a23-linux") { ClaimsRoot = true };

        var result = await home.RunAsync("act", "A23", "--preview");

        result.Exit.Should().Be((int)ExitCode.Usage);
        result.Stderr.Should().Contain("A23 runs on the Windows host, not on this side (the WSL binary)");
    }
}
