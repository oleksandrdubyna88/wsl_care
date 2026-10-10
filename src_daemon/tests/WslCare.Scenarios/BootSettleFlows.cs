using System.Diagnostics;
using System.Text.Json;

using FluentAssertions;

using WslCare.Core.Collect;
using WslCare.Core.Json;
using WslCare.TestSupport;

namespace WslCare.Scenarios;

/// <summary>
/// PLAN_boot_settle.md end to end over the BUILT CLI (coai code round aebbaafd): <c>collect --timer</c>, as its unit starts it, on a
/// machine that has been up 58 s under a one-minute <c>timer.bootDelayMinutes</c> — it says it waits, waits the 2 s that are left,
/// then measures and records the wait in the run's detail. The captured <c>/proc</c>'s pressure is calm, so there is no busy wait.
/// </summary>
public sealed class BootSettleFlows
{
    [Fact]
    public async Task A_timer_run_on_a_fresh_boot_says_it_waits_waits_and_records_the_wait()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "the settle step reads the distro's /proc: the Linux legs");
        using var home = CollectFlows.Captured("boot-settle");
        ProcfsFixture.CopyTo(home.SandboxRoot);
        File.WriteAllText(Path.Combine(home.SandboxRoot, "proc", "uptime"), "58.00 100.00\n");
        Directory.CreateDirectory(Path.GetDirectoryName(home.Paths.MachineConfigFile)!);
        File.WriteAllText(home.Paths.MachineConfigFile, """{ "timer": { "bootDelayMinutes": 1, "busyWaitMinutes": 0 } }""");
        var clock = Stopwatch.StartNew();

        var collect = await home.RunAsync("collect", "--timer", "--json");

        collect.Exit.Should().Be(0, collect.Stderr);
        clock.Elapsed.Should().BeGreaterThanOrEqualTo(TimeSpan.FromSeconds(2), "the 2 s left of the boot delay were waited, for real");
        collect.Stderr.Should().Contain("waiting 1 min for the boot to settle", "a waiting run says so in its log");
        var report = JsonSerializer.Deserialize(collect.Stdout, WslCareJsonContext.Default.CollectReport)!;
        report.Detail!.Settled.Should().NotBeNull().And.Match<RunSettledReport>(s => s.BootWaitSeconds == 2 && s.BusyWaitSeconds == 0 && !s.BusyAtEnd);
    }
}
