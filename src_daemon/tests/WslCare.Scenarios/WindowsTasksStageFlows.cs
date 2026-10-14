using System.Security.Cryptography;
using System.Text.Json.Nodes;

using FluentAssertions;

using WslCare.Cli;
using WslCare.Core.Hosting;
using WslCare.Core.Scheduler;
using WslCare.TestSupport;

namespace WslCare.Scenarios;

/// <summary>
/// WR-D2 end to end over the BUILT CLI (PLAN_windows_runner.md §3.2, §3.10): on the Windows binary <c>tasks stage --json</c> copies
/// the built exe into the sandbox's <c>%LOCALAPPDATA%\wsl-care\bin\&lt;digest&gt;\</c>, byte for byte, records <c>stage.json</c> and
/// sweeps an older digest folder; a second stage reuses the verified copy; <c>tasks stage --remove</c> keeps and names a copy whose exe
/// is held open (the test's own handle standing in for a running image — never a running copy of the product) and, once it is let
/// go, removes it and the root. On the distro's binary the verb is refused with exit 2.
/// </summary>
/// <remarks>The built CLI is a framework-dependent apphost (<c>wsl-care.exe</c> beside <c>wsl-care.dll</c>): its copy alone cannot
/// start (measured 2026-10-12: "The application to execute does not exist: …\wsl-care.dll", exit 154), so this flow cannot run
/// <c>tasks render</c> FROM the staged copy — the shipped exe is a Native AOT single file, which can. What the built CLI can do is
/// tested: the stage, the reuse, the sweep, the removal.</remarks>
public sealed class WindowsTasksStageFlows
{
    [Fact]
    public async Task The_built_binary_stages_its_own_exe_reuses_it_and_removes_every_copy()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "the Windows binary's verb: the Windows legs (the refusal is the other test)");
        using var home = new ScenarioHome("tasks-stage");
        var root = StagedCopies.Root((WindowsHostPaths)home.Paths);
        var older = PlantOlder(root);
        var built = ChildProcess.BesideTheTests(CommandLine.BinaryName);
        var digest = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(built)));
        var release = StagedCopies.ReleaseOf((await home.RunAsync("--version")).Stdout.Trim());

        var first = await home.RunAsync("tasks", "stage", "--json");

        first.Exit.Should().Be((int)ExitCode.Ok, first.Stderr);
        var staged = JsonNode.Parse(first.Stdout)!;
        staged["outcome"]!.GetValue<string>().Should().Be(StageOutcomes.Staged, staged["reason"]!.GetValue<string>());
        staged["digest"]!.GetValue<string>().Should().Be(digest);
        var exe = staged["path"]!.GetValue<string>();
        exe.Should().Be(Path.Combine(root, digest, StagedCopies.ExeName));
        File.ReadAllBytes(exe).Should().Equal(File.ReadAllBytes(built), "the copy is the built exe byte for byte");
        staged["version"]!.GetValue<string>().Should().Be(release);
        staged["swept"]!.AsArray().Select(n => (string)n!["path"]!).Should().Equal(older);
        Directory.Exists(older).Should().BeFalse();
        File.ReadAllText(Path.Combine(root, digest, StageRecord.FileName)).Should().Contain(digest).And.Contain(release);

        var second = await home.RunAsync("tasks", "stage", "--json");

        second.Exit.Should().Be((int)ExitCode.Ok, second.Stderr);
        JsonNode.Parse(second.Stdout)!["outcome"]!.GetValue<string>().Should().Be(StageOutcomes.Reused);

        ChildResult kept;
        using (new FileStream(exe, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            kept = await home.RunAsync("tasks", "stage", "--remove", "--json");
        }

        kept.Exit.Should().Be((int)ExitCode.Ok, kept.Stderr);
        var keptAnswer = JsonNode.Parse(kept.Stdout)!;
        keptAnswer["kept"]!.AsArray().Select(n => (string)n!["digest"]!).Should().Equal(digest);
        keptAnswer["rootRemoved"]!.GetValue<bool>().Should().BeFalse();
        File.Exists(exe).Should().BeTrue("a copy whose exe is held is kept whole");

        var removed = await home.RunAsync("tasks", "stage", "--remove", "--json");

        removed.Exit.Should().Be((int)ExitCode.Ok, removed.Stderr);
        var removedAnswer = JsonNode.Parse(removed.Stdout)!;
        removedAnswer["removed"]!.AsArray().Select(n => (string)n!["digest"]!).Should().Equal(digest);
        removedAnswer["rootRemoved"]!.GetValue<bool>().Should().BeTrue();
        Directory.Exists(root).Should().BeFalse();
    }

    [Theory]
    [InlineData("--json")]
    [InlineData("--remove")]
    public async Task The_distro_binary_refuses_the_stage_with_exit_2_and_nothing_on_stdout(string flag)
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "the distro's binary: the Linux legs");
        using var home = new ScenarioHome("tasks-stage-linux");

        var result = await home.RunAsync("tasks", "stage", flag);

        result.Exit.Should().Be((int)ExitCode.Usage);
        result.Stdout.Should().BeEmpty();
        result.Stderr.Should().Contain("Windows binary");
    }

    /// <summary>An older digest folder with a recorded copy; its path.</summary>
    private static string PlantOlder(string root)
    {
        var digest = new string('a', 64);
        var folder = Path.Combine(root, digest);
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, StagedCopies.ExeName), "an older copy");
        File.WriteAllText(Path.Combine(folder, StageRecord.FileName), $$"""{ "version": "0.3.9", "sha256": "{{digest}}", "stagedAtUtc": "2026-10-01T08:00:00+00:00", "source": "C:\\Users\\user\\.vscode\\extensions\\x\\bin\\wsl-care.exe" }""");
        return folder;
    }
}
