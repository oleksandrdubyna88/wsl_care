using FluentAssertions;

using WslCare.Core.Collectors;
using WslCare.Core.Config;
using WslCare.Core.Files;
using WslCare.Core.Mcp;
using WslCare.TestSupport;

namespace WslCare.Core.Tests.Mcp;

/// <summary>
/// E14 S7a, READ-ONLY: the Windows side's MCP servers (<c>coai-mcp.exe</c>, <c>creds-mcp.exe</c>, the user's programs) over a FAKE
/// process table shaped as measured on 2026-10-09 — <c>claude.exe</c> → <c>coai-mcp.exe</c>, and 35 <c>creds-mcp.exe</c> under one
/// VS Code <c>wsl.exe</c> connection. Counted by exe name without case; an orphan is a parent gone or a parent created AFTER the
/// child (a reused pid, the ancestors checked too); CPU over the window, matched by pid AND creation time.
/// </summary>
public sealed class WindowsMcpCollectorTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 9, 12, 20, 0, TimeSpan.Zero);

    private static EffectiveConfig Defaults() =>
        ConfigLoader.Load([(ConfigLoader.DefaultsFile, new FileReadResult.Content(ConfigLoader.EmbeddedDefaults()))]).Config;

    /// <summary>A scripted table: the list, and each pid's details at the first and the second read.</summary>
    private sealed class FakeTable : IWindowsProcessTable
    {
        private readonly Dictionary<int, WindowsProcessDetails> _first = [];
        private readonly Dictionary<int, WindowsProcessDetails> _second = [];
        private readonly HashSet<int> _secondReadStarted = [];

        public List<WindowsProcessEntry> Entries { get; } = [];

        public bool SecondRead { get; set; }

        public FakeTable Process(int pid, int parent, string exe, double ageMinutes = 120, double cpuSeconds = 10, double cpuSecondsLater = double.NaN, long privateBytes = 10_000_000, bool unopenable = false, double? recreatedAgeMinutes = null)
        {
            Entries.Add(new WindowsProcessEntry(pid, parent, exe));
            if (!unopenable)
            {
                _first[pid] = Details(Now.AddMinutes(-ageMinutes), cpuSeconds, privateBytes);
                _second[pid] = Details(recreatedAgeMinutes is { } again ? Now.AddMinutes(-again) : Now.AddMinutes(-ageMinutes), double.IsNaN(cpuSecondsLater) ? cpuSeconds : cpuSecondsLater, privateBytes);
            }

            return this;
        }

        private static WindowsProcessDetails Details(DateTimeOffset created, double cpuSeconds, long privateBytes) =>
            new(Reading.Of(created), Reading.Of(TimeSpan.FromSeconds(cpuSeconds)), Reading.Of(privateBytes * 2), Reading.Of(privateBytes), Reading.Of(1));

        public Reading<IReadOnlyList<WindowsProcessEntry>> List() => Reading.Of<IReadOnlyList<WindowsProcessEntry>>([.. Entries]);

        public WindowsProcessDetails Details(int pid) =>
            (SecondRead ? _second : _first).TryGetValue(pid, out var details) ? details : WindowsProcessDetails.Unopenable($"OpenProcess({pid}) failed: access denied");
    }

    private static async Task<WindowsMcpSample> Sample(FakeTable table, EffectiveConfig? config = null)
    {
        var clock = new ManualTimeProvider(Now) { SteppedTimestamps = true };
        var reading = await new WindowsMcpCollector(table, clock, (window, _) =>
        {
            table.SecondRead = true;
            clock.Advance(window);
            return Task.CompletedTask;
        }).SampleAsync(config ?? Defaults(), CancellationToken.None);
        return reading.Should().BeOfType<Reading<WindowsMcpSample>.Available>().Subject.Value;
    }

    [Fact]
    public async Task The_windows_mcp_instances_are_counted_by_exe_name_without_case()
    {
        var table = new FakeTable()
            .Process(100, 1, "claude.exe")
            .Process(200, 100, "COAI-MCP.EXE")
            .Process(201, 100, "creds-mcp.exe")
            .Process(202, 100, "node.exe")
            .Process(203, 100, "creds-mcp-helper.exe");

        var sample = await Sample(table);

        sample.Instances.Select(i => (i.Pid, i.Server)).Should().BeEquivalentTo([(200, "coai-mcp"), (201, "creds-mcp")]);
    }

    [Fact]
    public async Task An_instance_whose_parent_is_gone_or_was_created_after_it_is_orphaned()
    {
        var table = new FakeTable()
            .Process(300, 999, "creds-mcp.exe", ageMinutes: 600)
            .Process(400, 1, "explorer.exe", ageMinutes: 5)
            .Process(301, 400, "creds-mcp.exe", ageMinutes: 600)
            .Process(100, 1, "claude.exe", ageMinutes: 900)
            .Process(302, 100, "creds-mcp.exe", ageMinutes: 600);

        var sample = await Sample(table);

        Owner(sample, 300).Should().Match<WindowsMcpOwner>(o => o.Kind == WindowsMcpOwnerKind.Orphaned && o.Detail.Contains("gone"));
        Owner(sample, 301).Should().Match<WindowsMcpOwner>(o => o.Kind == WindowsMcpOwnerKind.Orphaned && o.Detail.Contains("created after"), "pid 400 is a NEWER process than its child: the parent's pid was reused");
        Owner(sample, 302).Kind.Should().Be(WindowsMcpOwnerKind.Agent);
        sample.OrphanedCount.Should().Be(2);
    }

    [Fact]
    public async Task Owners_are_agent_interop_orphaned_or_other_and_a_reused_ancestor_is_no_owner()
    {
        var table = new FakeTable()
            .Process(100, 1, "claude.exe", ageMinutes: 900)
            .Process(110, 100, "cmd.exe", ageMinutes: 800)
            .Process(200, 110, "coai-mcp.exe", ageMinutes: 700)
            .Process(500, 1, "wsl.exe", ageMinutes: 900)
            .Process(600, 1, "Code.exe", ageMinutes: 900)
            .Process(610, 600, "coai-mcp.exe", ageMinutes: 700)
            .Process(120, 1, "claude.exe", ageMinutes: 5)
            .Process(130, 120, "cmd.exe", ageMinutes: 800)
            .Process(210, 130, "coai-mcp.exe", ageMinutes: 700);
        for (var i = 0; i < 35; i++)
        {
            table.Process(700 + i, 500, "creds-mcp.exe", ageMinutes: 300 + i);
        }

        var sample = await Sample(table);

        Owner(sample, 200).Should().Be(new WindowsMcpOwner(WindowsMcpOwnerKind.Agent, 110, "cmd.exe", "claude.exe (pid 100)"), "the first ancestor that is an agent, through cmd.exe");
        Owner(sample, 700).Kind.Should().Be(WindowsMcpOwnerKind.Interop, "a WSL connection's interop child; its caller is in the distro and cannot be seen from here");
        Owner(sample, 610).Should().Match<WindowsMcpOwner>(o => o.Kind == WindowsMcpOwnerKind.Other && o.ParentName == "Code.exe");
        Owner(sample, 210).Kind.Should().Be(WindowsMcpOwnerKind.Other, "claude.exe pid 120 is NEWER than the cmd.exe below it: a reused ancestor pid owns nothing (coai plan round 2026-10-09, finding 6)");
        sample.Owners.Should().ContainSingle(o => o.Kind == WindowsMcpOwnerKind.Interop).Which.Should().Be(new WindowsMcpOwnerGroup(WindowsMcpOwnerKind.Interop, "wsl.exe (pid 500)", 35));
    }

    [Fact]
    public async Task Cpu_over_the_window_decides_idle_and_an_unopenable_process_is_unavailable_never_0()
    {
        var table = new FakeTable()
            .Process(100, 1, "claude.exe")
            .Process(200, 100, "coai-mcp.exe", ageMinutes: 120, cpuSeconds: 10)
            .Process(201, 100, "coai-mcp.exe", ageMinutes: 120, cpuSeconds: 10, cpuSecondsLater: 10.5)
            .Process(202, 100, "coai-mcp.exe", unopenable: true)
            .Process(203, 100, "coai-mcp.exe", ageMinutes: 120, recreatedAgeMinutes: 0.01)
            .Process(204, 100, "coai-mcp.exe", ageMinutes: 2, cpuSeconds: 10);

        var sample = await Sample(table);

        var idle = sample.Instances.Single(i => i.Pid == 200);
        idle.CpuPercent.Should().Be(Reading.Of(0.0));
        idle.Idle.Should().BeTrue("no CPU over the window and two hours old");
        sample.Instances.Single(i => i.Pid == 201).CpuPercent.Should().Be(Reading.Of(50.0), "0.5 s of CPU over the 1 s window");
        sample.Instances.Single(i => i.Pid == 202).Should().Match<WindowsMcpInstance>(i => !i.CpuPercent.IsAvailable && !i.PrivateBytes.IsAvailable && !i.Idle && i.CpuPercent.ReasonOrEmpty.Contains("access denied"));
        sample.Instances.Single(i => i.Pid == 203).CpuPercent.IsAvailable.Should().BeFalse("another process holds the pid at the second read (coai plan round, finding 2)");
        sample.Instances.Single(i => i.Pid == 204).Idle.Should().BeFalse("younger than mcpServers.idleMinAgeMinutes: starting, not idle");
        sample.IdleCount.Should().Be(1);
        sample.WindowMilliseconds.Should().Be(1000);
    }

    [Fact]
    public async Task Instances_are_listed_largest_first_up_to_the_cap_and_the_user_programs_count_too()
    {
        var table = new FakeTable().Process(100, 1, "claude.exe");
        for (var i = 0; i < 5; i++)
        {
            table.Process(200 + i, 100, "coai-mcp.exe", privateBytes: (i + 1) * 1_000_000);
        }

        // The user's program holds the least, so the three largest are the coai-mcp instances.
        table.Process(300, 100, "my-server.exe", privateBytes: 500_000);
        var config = ConfigLoader.Load([
            (ConfigLoader.DefaultsFile, new FileReadResult.Content(ConfigLoader.EmbeddedDefaults())),
            (new ConfigLayerFile(ConfigLayer.Machine, "/etc/wsl-care/config.json"), new FileReadResult.Content(System.Text.Encoding.UTF8.GetBytes("{ \"mcpServers\": { \"maxInstances\": 3, \"programs\": [\"my-server\"] } }")))]).Config;

        var sample = await Sample(table, config);

        sample.Count.Should().Be(6);
        sample.Listed.Select(i => i.Pid).Should().Equal(204, 203, 202);
        sample.Instances.Should().Contain(i => i.Server == "my-server");
    }

    [Fact]
    public async Task A_pid_the_snapshot_names_twice_is_one_instance_never_a_crash()
    {
        // coai code round 2026-10-09, finding 9: a torn snapshot may list a pid twice; the sample must still answer.
        var table = new FakeTable()
            .Process(100, 1, "claude.exe")
            .Process(200, 100, "creds-mcp.exe")
            .Process(200, 100, "creds-mcp.exe");

        var sample = await Sample(table);

        sample.Instances.Should().ContainSingle(i => i.Pid == 200);
    }

    [Fact]
    public async Task A_parent_that_cannot_be_opened_cannot_prove_a_reuse_so_the_instance_is_not_orphaned()
    {
        var table = new FakeTable()
            .Process(400, 1, "Code.exe", unopenable: true)
            .Process(200, 400, "creds-mcp.exe", ageMinutes: 600);

        var sample = await Sample(table);

        Owner(sample, 200).Should().Be(new WindowsMcpOwner(WindowsMcpOwnerKind.Other, 400, "Code.exe", "Code.exe (pid 400)"), "an unreadable creation time is not taken as a reuse (own code review 2026-10-09)");
    }

    private static WindowsMcpOwner Owner(WindowsMcpSample sample, int pid) => sample.Instances.Single(i => i.Pid == pid).Owner;
}
