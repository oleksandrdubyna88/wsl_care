using System.Text.Json;

using FluentAssertions;

using WslCare.Cli;
using WslCare.Core.Actions.Engine;
using WslCare.Core.Actions.Suspects;
using WslCare.Core.Files;
using WslCare.Core.Hosting;
using WslCare.Core.Json;

namespace WslCare.Scenarios;

/// <summary>
/// E7.S2b over the BUILT CLI (plan §15q, owner decision 2026-10-05): the timer's full run — every <c>auto</c> on, dry run off,
/// the idle window at its minimum — never selects A18, yet records the AI-agent CPU history A18 judges by; and a button's
/// <c>act A18 --preview</c> right after ends nothing (one sample is no measured idle time) and writes no state.
/// </summary>
public sealed class AgentOrphansFlows
{
    private const string AllOn = """
        { "dryRun": false, "processes": { "aiAgentsIdleHours": 1 },
          "auto": { "A1": true, "A2": true, "A3": true, "A4": true, "A5": true, "A5Testcontainers": true, "A6": true, "A6Unused": true, "A7": true,
                    "A8": true, "A9": true, "A10": true, "A11": true, "A12": true, "A13": true, "A14": true, "A15": true, "A16": true, "A17": true } }
        """;

    [Fact]
    public async Task The_timer_never_runs_A18_but_records_the_cpu_history_and_a_preview_after_it_ends_nothing()
    {
        using var home = LogsFlows.TimerHome("a18-timer");
        Assert.SkipWhen(home.Paths.Side == HostSide.Windows, "A18 is the distro's action, over the captured /proc: the Linux legs");
        var bootId = Path.Combine(home.SandboxRoot, "proc", "sys", "kernel", "random", "boot_id");
        Directory.CreateDirectory(Path.GetDirectoryName(bootId)!);
        File.WriteAllText(bootId, "6d1c1c5e-0000-4000-8000-000000000001\n");
        Directory.CreateDirectory(Path.GetDirectoryName(home.Paths.UserConfigFile)!);
        File.WriteAllText(home.Paths.UserConfigFile, AllOn);

        var collect = await home.RunAsync("collect", "--timer", "--json");

        collect.Exit.Should().Be((int)ExitCode.Ok, collect.Stderr);
        var pass = JsonSerializer.Deserialize(collect.Stdout, WslCareJsonContext.Default.CollectReport)!.Detail!.TimerPass!;
        pass.Ran.Should().BeTrue(pass.Reason);
        pass.Actions.Should().NotContain(a => a.Id == "A18", "the timer never even selects a button-only action");
        var history = AgentCpuHistory.Read(home.Paths, new PhysicalFileSystem(home.Paths) { OwnersAreThisProcess = true, TrustedStateOwner = Core.Files.RegularFiles.EffectiveUid() });
        history.BootId.Should().NotBeEmpty("the timer's run recorded the AI-agent CPU history");
        var before = await File.ReadAllBytesAsync(AgentCpuHistory.File(home.Paths), TestContext.Current.CancellationToken);

        home.ClaimsRoot = true;
        var preview = await home.RunAsync("act", "A18", "--preview", "--json");

        preview.Exit.Should().Be((int)ExitCode.Ok, preview.Stderr);
        var a18 = JsonSerializer.Deserialize(preview.Stdout, WslCareJsonContext.Default.ActReport)!.Actions.Single();
        a18.Preview!.Count.Should().Be(0, "one recorded sample is no measured idle time");
        (await File.ReadAllBytesAsync(AgentCpuHistory.File(home.Paths), TestContext.Current.CancellationToken)).Should().Equal(before, "a preview writes no state");
    }
}
