using System.Text.Json.Nodes;

using FluentAssertions;

using WslCare.Cli;
using WslCare.TestSupport;

namespace WslCare.Scenarios;

/// <summary>
/// WR-T2 end to end over the BUILT CLI (PLAN_windows_runner.md §3.12 "In the panel"): <c>tasks preview cleanup --json</c> on the Windows
/// binary whose layout is a sandbox (<c>WSL_CARE_ROOT</c>) - its <c>%TEMP%</c> is a folder under the scenario's <see cref="TempRoot"/>, and
/// the real one is never resolved. The verb is read-only: the sandbox is byte-for-byte what it was, every path in the answer lies under it.
/// An elevated process is refused (81); the distro's binary refuses the verb (2).
/// </summary>
public sealed class WindowsTempPreviewFlows
{
    /// <summary>Everything under the sandbox except the state folder (the run log the CLI keeps for every run - the family logging rule: one file per run under
    /// <c>wsl-care\logs</c>) - the log is the process' own, not the verb's.</summary>
    private static IReadOnlyList<string> Snapshot(string root) =>
        [.. Directory.EnumerateFileSystemEntries(root, "*", SearchOption.AllDirectories)
            .Where(p => !p.Contains("\\AppData\\Local\\wsl-care", StringComparison.OrdinalIgnoreCase))
            .Select(p => File.Exists(p) ? $"{p}|{new FileInfo(p).Length}|{File.GetLastWriteTimeUtc(p).Ticks}" : p)
            .Order(StringComparer.Ordinal)];

    [Fact]
    public async Task The_built_binary_previews_the_sandbox_temp_and_writes_nothing()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "the Windows binary's verb: the Windows legs (the refusal is the other test)");
        Assert.SkipWhen(Environment.IsPrivilegedProcess, "this account is elevated: the child would be refused, which the next test shows");
        using var home = new ScenarioHome("temp-preview");
        var temp = home.Paths.TempDirectory;
        temp.Should().StartWith(home.SandboxRoot, "the scenario's %TEMP% is a sandbox folder, never the real one");
        var old = TempTrees.Make(temp, "coai-old", 30, ("a.txt", 30));
        TempTrees.Make(temp, "coai-young", 30, ("fresh.txt", 0));
        TempTrees.Make(temp, "sometool-old", 30, ("x.txt", 30));
        TempTrees.Make(temp, "claude", 30, ("log.txt", 30));
        var before = Snapshot(home.SandboxRoot);

        var result = await home.RunAsync("tasks", "preview", "cleanup", "--json");

        result.Exit.Should().Be((int)ExitCode.Ok, result.Stderr);
        var after = Snapshot(home.SandboxRoot);
        after.Except(before).Should().BeEmpty("a preview writes nothing and deletes nothing: added or changed");
        before.Except(after).Should().BeEmpty("a preview writes nothing and deletes nothing: removed or changed");
        File.Exists(Path.Combine(home.Paths.StateDirectory, "temp-cursor.json")).Should().BeFalse();
        Directory.EnumerateFiles(home.SandboxRoot, "first-timer-run*.json", SearchOption.AllDirectories).Should().BeEmpty();
        var answer = JsonNode.Parse(result.Stdout)!;
        answer["counts"]!["eligible"]!.GetValue<long>().Should().Be(1);
        answer["counts"]!["young"]!.GetValue<long>().Should().Be(1);
        answer["counts"]!["wouldBeEligible"]!.GetValue<long>().Should().Be(1);
        var groups = answer["groups"]!.AsArray();
        groups.Single(g => (string)g!["pattern"]! == "coai-*")!["paths"]!.AsArray().Select(p => (string)p!).Should().Equal(old);
        groups.Select(g => (string)g!["pattern"]!).Should().Contain("sometool-*");
        answer["excludedByReason"]!.AsArray().Select(r => (string)r!["reason"]!).Should().Contain(r => r.Contains("claude", StringComparison.OrdinalIgnoreCase));
        groups.SelectMany(g => g!["paths"]!.AsArray()).Select(p => (string)p!).Should().OnlyContain(p => p.StartsWith(home.SandboxRoot, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task The_built_binary_refuses_an_elevated_preview_with_81()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "the Windows binary's verb");
        using var home = new ScenarioHome("temp-preview-elevated") { ClaimsRoot = true };
        var old = TempTrees.Make(home.Paths.TempDirectory, "coai-old", 30, ("a.txt", 30));

        var result = await home.RunAsync("tasks", "preview", "cleanup", "--json");

        result.Exit.Should().Be((int)ExitCode.NotAsRoot);
        result.Stdout.Should().BeEmpty();
        result.Stderr.Should().Contain("Administrators").And.NotContain("act ");
        Directory.Exists(old).Should().BeTrue();
    }

    [Fact]
    public async Task The_distro_binary_refuses_the_preview_with_exit_2()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "the distro's binary: the Linux legs");
        using var home = new ScenarioHome("temp-preview-linux");

        var result = await home.RunAsync("tasks", "preview", "cleanup", "--json");

        result.Exit.Should().Be((int)ExitCode.Usage);
        result.Stdout.Should().BeEmpty();
        result.Stderr.Should().Contain("Windows binary");
    }
}
