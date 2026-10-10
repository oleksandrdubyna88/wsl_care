using System.Text.Json;

using FluentAssertions;

using WslCare.Cli;
using WslCare.Core.Actions.Suspects;
using WslCare.Core.Files;
using WslCare.Core.Hosting;
using WslCare.Core.Json;

namespace WslCare.Scenarios;

/// <summary>
/// E14 S7b.2 over the BUILT CLI: the captured tree with one <c>creds-mcp.exe</c> interop relay added — exe <c>/init</c>, argv
/// <c>/init /mnt/c/…/creds-mcp.exe</c>, re-parented to root's <c>Relay(9801)</c>, its stdio two pipes nobody else holds (real symlinks:
/// the Linux legs). The timer's full run records the relay in the CPU history A21 judges by; a button's <c>act A21 --preview</c> right
/// after recognises it and KEEPS it — one sample is no measured idle time — writing no state. Nothing is ever signalled: the sandbox's
/// signal sender refuses.
/// </summary>
public sealed class InteropRelayStopFlows
{
    private const int Relay = 9900;
    private const int SessionInit = 9800;

    [Fact]
    public async Task The_timer_records_a_relay_and_a_preview_after_it_recognises_and_keeps_it_on_a_first_sighting()
    {
        using var home = LogsFlows.TimerHome("a21-timer");
        Assert.SkipWhen(home.Paths.Side == HostSide.Windows, "A21 is the distro's action, over the captured /proc with real links: the Linux legs");
        var bootId = Path.Combine(home.SandboxRoot, "proc", "sys", "kernel", "random", "boot_id");
        Directory.CreateDirectory(Path.GetDirectoryName(bootId)!);
        File.WriteAllText(bootId, "6d1c1c5e-0000-4000-8000-000000000021\n");
        Directory.CreateDirectory(Path.GetDirectoryName(home.Paths.UserConfigFile)!);
        File.WriteAllText(home.Paths.UserConfigFile, """{ "dryRun": false, "mcpWatchdog": { "orphanIdleMinutes": 1 } }""");
        Process(home, SessionInit, parent: 1, uid: 0, comm: "Relay(9801)", argv: ["/init"]);
        Process(home, Relay, parent: SessionInit, uid: 1000, comm: "init", argv: ["/init", "/mnt/c/Users/user/AppData/Local/Programs/creds/creds-mcp.exe"]);
        Link(home, $"proc/{Relay}/exe", "/init");
        Link(home, $"proc/{Relay}/fd/0", "pipe:[99000]");
        Link(home, $"proc/{Relay}/fd/1", "pipe:[99001]");

        var collect = await home.RunAsync("collect", "--timer", "--json");

        collect.Exit.Should().Be((int)ExitCode.Ok, collect.Stderr);
        var pass = JsonSerializer.Deserialize(collect.Stdout, WslCareJsonContext.Default.CollectReport)!.Detail!.TimerPass!;
        pass.Actions.Should().ContainSingle(a => a.Id == "A21", "auto.A21 is on by default, so the timer selects it").Which.Run.Should().BeNull("a first sighting is no measured idle time");
        var files = new PhysicalFileSystem(home.Paths) { OwnersAreThisProcess = true, TrustedStateOwner = RegularFiles.EffectiveUid() };
        AgentCpuHistory.Read(home.Paths, files).Entries.Select(e => e.Pid).Should().Contain(Relay, "the timer's run records the relay by identity");
        var before = await File.ReadAllBytesAsync(AgentCpuHistory.File(home.Paths), TestContext.Current.CancellationToken);

        home.ClaimsRoot = true;
        var preview = await home.RunAsync("act", "A21", "--preview", "--json");

        preview.Exit.Should().Be((int)ExitCode.Ok, preview.Stderr);
        var a21 = JsonSerializer.Deserialize(preview.Stdout, WslCareJsonContext.Default.ActReport)!.Actions.Single();
        a21.Preview!.Count.Should().Be(0, "one recorded sample is no measured idle time");
        a21.Preview.Basis.Should().Contain("1 relay(s) of user kept").And.Contain(InteropRelayStop.UsedCpu);
        (await File.ReadAllBytesAsync(AgentCpuHistory.File(home.Paths), TestContext.Current.CancellationToken)).Should().Equal(before, "a preview writes no state");
    }

    private static void Process(ScenarioHome home, int pid, int parent, int uid, string comm, IReadOnlyList<string> argv)
    {
        var dir = Path.Combine(home.SandboxRoot, "proc", pid.ToString(System.Globalization.CultureInfo.InvariantCulture));
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "stat"), $"{pid} ({comm}) S {parent} {pid} {pid} 0 -1 0 0 0 0 0 250 250 0 0 20 0 1 0 100 0 0\n");
        File.WriteAllText(Path.Combine(dir, "status"), $"Name:\t{comm}\nState:\tS (sleeping)\nPPid:\t{parent}\nUid:\t{uid}\t{uid}\t{uid}\t{uid}\nKthread:\t0\nVmRSS:\t2000 kB\nRssAnon:\t2000 kB\nRssFile:\t0 kB\nRssShmem:\t0 kB\n");
        File.WriteAllText(Path.Combine(dir, "comm"), comm + "\n");
        File.WriteAllText(Path.Combine(dir, "cgroup"), "0::/init.scope\n");
        File.WriteAllBytes(Path.Combine(dir, "cmdline"), System.Text.Encoding.UTF8.GetBytes(string.Join('\0', argv) + "\0"));
    }

    private static void Link(ScenarioHome home, string relative, string target)
    {
        var path = Path.Combine(home.SandboxRoot, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.CreateSymbolicLink(path, target);
    }
}
