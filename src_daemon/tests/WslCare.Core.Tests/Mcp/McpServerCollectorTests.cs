using System.Globalization;

using FluentAssertions;

using WslCare.Core.Collectors;
using WslCare.Core.Collectors.Procfs;
using WslCare.Core.Config;
using WslCare.Core.Files;
using WslCare.Core.Mcp;
using WslCare.Core.Tests.Collectors;
using WslCare.Core.Thresholds;
using WslCare.TestSupport;

namespace WslCare.Core.Tests.Mcp;

/// <summary>
/// Plan §15q E7.S2d: the MCP server instances of the AI agents over a synthetic <c>/proc</c> shaped as measured on 2026-10-06 —
/// the VS Code server's <c>node</c> → the Claude Code extension's native <c>claude</c> → <c>coai-mcp</c> — with the server's run
/// logs per the family contract. Names come from the fixture identity (<c>me</c>), never a real account.
/// </summary>
public sealed class McpServerCollectorTests : IDisposable
{
    private const string Coai = "/home/me/.vscode-server/data/User/globalStorage/remsoftdev.connect-other-ais/coai-mcp";
    private const string Claude = "/home/me/.vscode-server/extensions/anthropic.claude-code-2.1.0-linux-x64/resources/native-binary/claude";
    private const string Logs = "/home/me/.local/share/coai-mcp/logs";
    private const int Hz = 100;

    /// <summary>Five minutes past a UTC midnight, so a window of ten minutes reaches back into yesterday's folder.</summary>
    private static readonly DateTimeOffset Now = new(2026, 10, 3, 0, 5, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Boot = DateTimeOffset.FromUnixTimeSeconds(SyntheticProcTree.BootUnixSeconds);

    private readonly SyntheticProcTree _tree = new SyntheticProcTree().MemInfo(1000, 0);

    public void Dispose() => _tree.Dispose();

    private static long StartTicksFor(TimeSpan age) => (long)((Now - Boot - age).TotalSeconds * Hz);

    /// <summary>node → claude → coai-mcp, the measured shape; the server <paramref name="age"/> old with <paramref name="cpuTicks"/> used.</summary>
    private McpServerCollectorTests Session(int claudePid, int serverPid, TimeSpan age, long cpuTicks = 1000)
    {
        _tree.Process(100, 1, "/a", 10, words: ["/home/me/.vscode-server/bin/abc/node", "--dns-result-order=ipv4first"], startTicks: StartTicksFor(TimeSpan.FromHours(9)))
            .Process(claudePid, 100, "/a", 10, words: [Claude, "--output-format", "stream-json"], startTicks: StartTicksFor(TimeSpan.FromHours(2)))
            .Process(serverPid, claudePid, "/a", 30_000, words: [Coai], startTicks: StartTicksFor(age), cpuTicks: cpuTicks);
        return this;
    }

    private ProcessSnapshot Snapshot() =>
        new ProcessCollector(_tree.Files, _tree.Paths, new FixedTimeProvider(Now))
            .Read(Reading.Of(new KernelFacts(Hz, 4096)), new ContainerSet([]), CancellationToken.None)
            .Should().BeOfType<Reading<ProcessSnapshot>.Available>().Subject.Value;

    /// <summary>The embedded defaults — what the product runs under with no layer.</summary>
    private static EffectiveConfig Defaults() => Configured();

    private McpSample Sample(Func<TimeSpan, CancellationToken, Task>? wait = null, EffectiveConfig? config = null, IFileSystem? files = null, TimeProvider? clock = null)
    {
        var collector = new McpServerCollector(files ?? _tree.Files, _tree.Paths, clock ?? new FixedTimeProvider(Now), wait ?? ((_, _) => Task.CompletedTask));
        var result = collector.SampleAsync(Reading.Of(Snapshot()), config ?? Defaults(), CancellationToken.None).GetAwaiter().GetResult();
        return result.Should().BeOfType<Reading<McpSample>.Available>().Subject.Value;
    }

    private void Log(int pid, DateTimeOffset namedAt, DateTimeOffset lastWrite) =>
        _tree.FileAt(string.Create(CultureInfo.InvariantCulture, $"{Logs}/{namedAt:yyyy-MM-dd}/coai-mcp-{namedAt:HH-mm-ss}-{pid}.log"), lastWrite);

    [Fact]
    public void An_mcp_server_under_a_claude_session_is_an_instance_with_its_owner_agent()
    {
        Session(200, 300, TimeSpan.FromHours(1));

        var sample = Sample();

        sample.Count.Should().Be(1);
        var instance = sample.Instances.Single();
        instance.Server.Should().Be("coai-mcp");
        instance.Owner.Should().BeOfType<McpOwner.Agent>().Which.Should().Match<McpOwner.Agent>(a => a.Pid == 200 && a.Name == "Claude Code" && a.CommandLine.Contains("native-binary/claude"));
        sample.HeldBytes.Should().Be(30_000 * 1024, "held is RssAnon + RssShmem, the product's one memory measure");
    }

    [Fact]
    public void An_mcp_server_whose_agent_died_is_an_orphaned_instance()
    {
        _tree.Process(301, 1, "/a", 10, words: [Coai], startTicks: StartTicksFor(TimeSpan.FromHours(1)));

        Sample().Instances.Single().Owner.Should().BeOfType<McpOwner.Orphaned>();
    }

    [Fact]
    public void An_mcp_server_under_another_host_is_not_an_instance_only_counted()
    {
        _tree.Process(400, 1, "/a", 10, words: ["/usr/bin/bash"], startTicks: StartTicksFor(TimeSpan.FromHours(3)))
            .Process(401, 400, "/a", 10, words: [Coai], startTicks: StartTicksFor(TimeSpan.FromHours(1)));

        var sample = Sample();

        sample.Count.Should().Be(0);
        sample.NotUnderAgent.Should().Be(1);
    }

    [Fact]
    public void A_server_name_as_an_argument_of_another_program_is_not_an_instance()
    {
        Session(200, 300, TimeSpan.FromHours(1));
        _tree.Process(500, 200, "/a", 10, words: ["/usr/bin/printf", "coai-mcp"], startTicks: StartTicksFor(TimeSpan.FromMinutes(1)));

        Sample().Instances.Select(i => i.Process.Pid).Should().Equal(300);
    }

    [Fact]
    public void An_unwatched_server_is_not_counted()
    {
        Session(200, 300, TimeSpan.FromHours(1));

        Sample(config: Configured(("mcpServers", "{\"watched\": []}"))).Count.Should().Be(0, "mcpServers.watched lists no server");
        Defaults().TextList(ConfigKeys.McpServers.Watched).Should().ContainSingle().Which.Should().Be("coai-mcp", "every catalogued server is watched by default");
        Sample().Count.Should().Be(1);
    }

    [Fact]
    public void Cpu_percent_is_measured_across_the_window_from_two_stat_reads()
    {
        Session(200, 300, TimeSpan.FromHours(1), cpuTicks: 1000);
        var waited = TimeSpan.Zero;

        var sample = Sample(wait: (window, _) =>
        {
            waited = window;
            _tree.Stat(300, 200, "coai-mcp", 0, StartTicksFor(TimeSpan.FromHours(1)), 1050);
            return Task.CompletedTask;
        });

        waited.Should().Be(TimeSpan.FromMilliseconds(1000), "the default window");
        sample.Instances.Single().CpuPercent.Should().Be(Reading.Of(50.0), "50 ticks at 100 ticks/s across 1 s is half a core: 100 × 0.5 ÷ 1.0");
        sample.CpuCores.Should().Be(Reading.Of(0.5));
    }

    [Theory]
    [InlineData("gone")]
    [InlineData("reused")]
    public void A_pid_reused_or_gone_during_the_window_has_cpu_unavailable_never_zero(string change)
    {
        Session(200, 300, TimeSpan.FromHours(1));

        var sample = Sample(wait: (_, _) =>
        {
            if (change == "gone")
            {
                _tree.Exit(300);
            }
            else
            {
                _tree.Stat(300, 200, "coai-mcp", 0, StartTicksFor(TimeSpan.FromSeconds(1)), 1000);
            }

            return Task.CompletedTask;
        });

        var instance = sample.Instances.Single();
        instance.CpuPercent.IsAvailable.Should().BeFalse();
        instance.Kind.Should().Be(McpKind.Unknown);
        sample.CpuCores.IsAvailable.Should().BeFalse("no instance's CPU was measured: not 0 cores");
    }

    [Fact]
    public void No_wait_when_no_instance_runs()
    {
        var called = false;

        Sample(wait: (_, _) =>
        {
            called = true;
            return Task.CompletedTask;
        }).Count.Should().Be(0);

        called.Should().BeFalse("a machine without MCP servers pays no window");
    }

    [Fact]
    public void Busy_without_activity_needs_a_known_log_older_than_the_window()
    {
        Session(200, 300, TimeSpan.FromHours(1));
        Session(210, 310, TimeSpan.FromHours(1));
        Session(220, 320, TimeSpan.FromHours(1));
        Log(300, Now - TimeSpan.FromHours(1), Now - TimeSpan.FromMinutes(15));
        Log(310, Now - TimeSpan.FromHours(1), Now - TimeSpan.FromMinutes(1));

        var sample = Sample(wait: Burn(300, 310, 320));

        Kind(sample, 300).Should().Be(McpKind.BusyWithoutActivity, "busy, and its log was last written 15 minutes ago");
        Kind(sample, 310).Should().Be(McpKind.Busy, "busy with a log written a minute ago");
        Kind(sample, 320).Should().Be(McpKind.Busy, "no log file of its pid: activity unknown, never 'without activity'");
        sample.Instances.Single(i => i.Process.Pid == 320).LastLogWrite.ReasonOrEmpty.Should().Contain("no log file of pid 320");
    }

    [Fact]
    public void A_log_of_an_earlier_process_with_the_same_pid_is_not_its_activity()
    {
        Session(200, 300, TimeSpan.FromMinutes(30));
        Log(300, Now - TimeSpan.FromHours(3), Now - TimeSpan.FromHours(2));

        var instance = Sample(wait: Burn(300)).Instances.Single();

        instance.LastLogWrite.IsAvailable.Should().BeFalse("that file was named two and a half hours before this process started");
        instance.Kind.Should().Be(McpKind.Busy);
    }

    [Fact]
    public void The_restart_storm_counts_34_starts_in_10_minutes()
    {
        // Measured 2026-10-06: a start took 20-32 s at a whole core, the agent's 30 s connect timeout killed it and started it
        // again — 34 starts in ten minutes. Here 34 runs named within the window (some before midnight, in yesterday's folder),
        // 3 older ones, and one 00-00-00 continuation of an earlier day's run that must not count.
        for (var i = 0; i < 34; i++)
        {
            Log(1000 + i, Now - TimeSpan.FromSeconds(15 + (i * 17)), Now - TimeSpan.FromSeconds(i * 17));
        }

        for (var i = 0; i < 3; i++)
        {
            Log(2000 + i, Now - TimeSpan.FromMinutes(20 + i), Now - TimeSpan.FromMinutes(19 + i));
        }

        Log(3000, Now.Date.AddHours(-9), Now.Date.AddSeconds(-1));
        Log(3000, new DateTimeOffset(Now.Date, TimeSpan.Zero), Now - TimeSpan.FromMinutes(1));

        var sample = Sample();
        var starts = sample.Servers.Single().Starts;

        starts.Count.Should().Be(Reading.Of(34));
        starts.Basis.Should().Be(McpStartsBasis.LogNames);
        McpVerdicts.From(Reading.Of(sample), Defaults()).Single(v => v.Id == McpVerdicts.Starts).Level.Should().Be(Level.Warn);
    }

    [Fact]
    public void A_midnight_file_of_a_live_process_started_after_midnight_is_a_start()
    {
        // Plan round finding 1: pid 300 also has a file from an earlier run yesterday; the process running now started after
        // midnight, so its 00-00-00 file is a START, not a continuation.
        Session(200, 300, TimeSpan.FromMinutes(5));
        Log(300, Now.Date.AddHours(-5), Now.Date.AddHours(-4));
        Log(300, new DateTimeOffset(Now.Date, TimeSpan.Zero), Now);

        Sample().Servers.Single().Starts.Count.Should().Be(Reading.Of(1));
    }

    [Fact]
    public void A_midnight_file_of_a_live_process_that_outlived_the_day_is_a_continuation()
    {
        Session(200, 300, TimeSpan.FromHours(5));
        Log(300, Now.Date.AddHours(-5), Now.Date.AddSeconds(-1));
        Log(300, new DateTimeOffset(Now.Date, TimeSpan.Zero), Now);

        Sample().Servers.Single().Starts.Count.Should().Be(Reading.Of(0));
    }

    [Fact]
    public void A_cut_listing_makes_starts_unavailable_not_partial()
    {
        for (var i = 0; i < 120; i++)
        {
            Log(1000 + i, Now - TimeSpan.FromSeconds(i), Now);
        }

        var starts = Sample(config: Configured(("mcpServers", "{\"maxLogEntries\": 100}"))).Servers.Single().Starts;

        starts.Count.IsAvailable.Should().BeFalse("a listing cut at its cap is no count");
        starts.Count.ReasonOrEmpty.Should().Contain("incomplete");
    }

    /// <summary>A folder the process may not traverse: <c>Directory.Exists</c> answers false for it and for everything below it (as
    /// the real one does on EACCES), its bounded listing is unreadable.</summary>
    private sealed class Untraversable(IFileSystem inner, string folder) : DelegatingFileSystem(inner), IFileSystem
    {
        public override bool DirectoryExists(string path) => !AtOrBelow(path) && base.DirectoryExists(path);

        public override IReadOnlyList<FileEntry> ListEntries(string path) => AtOrBelow(path) ? [] : base.ListEntries(path);

        EntryListing IFileSystem.ListEntries(string path, ListingBounds bounds) =>
            AtOrBelow(path) ? new EntryListing.Unreadable($"{path}: permission denied") : Inner.ListEntries(path, bounds);

        private bool AtOrBelow(string path)
        {
            var full = Path.GetFullPath(path);
            var root = Path.GetFullPath(folder);
            return full.Equals(root, StringComparison.OrdinalIgnoreCase) || full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) || full.StartsWith(root + "/", StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void An_untraversable_log_root_makes_the_starts_unavailable_never_zero()
    {
        // coai code round finding 2: Directory.Exists answers false for a folder it cannot traverse, and the log root then read
        // as "no logs" — 0 starts, mcp.starts ok — during a storm nobody could see.
        for (var i = 0; i < 12; i++)
        {
            Log(1000 + i, Now - TimeSpan.FromSeconds(10 + i), Now);
        }

        var files = new Untraversable(_tree.Files, _tree.Paths.DistroPath("/home/me/.local/share/coai-mcp"));

        var starts = Sample(files: files).Servers.Single().Starts;

        starts.Count.IsAvailable.Should().BeFalse("a log root that could not be reached is no count — not zero starts");
        starts.Count.ReasonOrEmpty.Should().Contain("permission denied");
    }

    [Fact]
    public void Figures_over_a_capped_list_say_they_cover_only_the_listed_instances()
    {
        // coai code round finding 3: with more instances than mcpServers.maxInstances, the idle and busy counts covered the
        // listed ones only while the count covered all — the verdict must say so.
        Session(200, 300, TimeSpan.FromHours(1));
        Session(210, 310, TimeSpan.FromHours(1));
        var config = Configured(("mcpServers", "{\"maxInstances\": 1}"));

        var sample = Sample(config: config);
        var verdict = McpVerdicts.From(Reading.Of(sample), config).Single(v => v.Id == McpVerdicts.Instances);

        sample.Count.Should().Be(2);
        sample.Instances.Should().HaveCount(1);
        verdict.Value.Should().Contain("over the 1 listed");
    }

    [Fact]
    public void A_long_cpu_window_does_not_move_the_process_start_its_log_is_still_its_own()
    {
        // Own review M1: the start was computed as now − age with a now taken AFTER the window, so a 3 s window moved the start
        // past the log's name and the instance lost its own log — busy, never busy without activity.
        Session(200, 300, TimeSpan.FromHours(1));
        Log(300, Now - TimeSpan.FromHours(1), Now - TimeSpan.FromMinutes(15));
        var clock = new ManualTimeProvider(Now);
        var burn = Burn(300);

        var instance = Sample(
            wait: (window, token) =>
            {
                clock.Advance(window);
                return burn(window, token);
            },
            config: Configured(("mcpServers", "{\"cpuWindowMilliseconds\": 3000}")),
            clock: clock).Instances.Single();

        instance.LastLogWrite.IsAvailable.Should().BeTrue("the file named at the process's start is its own, whatever the window");
        instance.Kind.Should().Be(McpKind.BusyWithoutActivity);
    }

    [Fact]
    public void A_midnight_file_whose_pid_now_belongs_to_another_program_is_not_a_start()
    {
        // Own review m1: a dead server's run crossed midnight (a file yesterday and a 00-00-00 file today); its pid now belongs
        // to an unrelated process started after midnight. That is a continuation, not a start.
        _tree.Process(300, 1, "/a", 10, words: ["/usr/bin/sleep", "60"], startTicks: StartTicksFor(TimeSpan.FromMinutes(1)));
        Log(300, Now.Date.AddMinutes(-3), Now.Date.AddSeconds(-1));
        Log(300, new DateTimeOffset(Now.Date, TimeSpan.Zero), Now - TimeSpan.FromMinutes(2));

        Sample().Servers.Single().Starts.Count.Should().Be(Reading.Of(1), "the run that began before midnight started once; its midnight segment is not a second start");
    }

    [Fact]
    public void A_linked_log_root_is_not_listed()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "a folder link needs no privilege on Linux");
        _tree.FileAt("/data/elsewhere/2026-10-03/coai-mcp-00-04-00-7.log", Now);
        Directory.CreateDirectory(_tree.Paths.DistroPath("/home/me/.local/share/coai-mcp"));
        Directory.CreateSymbolicLink(_tree.Paths.DistroPath(Logs), _tree.Paths.DistroPath("/data/elsewhere"));

        var starts = Sample().Servers.Single().Starts;

        starts.Count.IsAvailable.Should().BeFalse("a log root reached through a link is not where it seems");
    }

    [Fact]
    public void Starts_without_a_log_layout_are_a_lower_bound_marked_liveYounger()
    {
        Session(200, 300, TimeSpan.FromMinutes(3));
        Session(210, 310, TimeSpan.FromHours(1));
        var noLogs = new McpServerEntry("coai-mcp", ["coai-mcp"], new McpLogLayout.None());
        var settings = McpSettings.From(Defaults()) with { Watched = [noLogs] };
        var snapshot = Snapshot();
        var found = McpInstances.Find(snapshot.All, settings.Watched);

        var summary = new McpJudge(settings, Now, Now, snapshot.All).Summary(noLogs, found.Instances, Reading.Missing<McpLogs>(McpRunLogs.NoLayout("coai-mcp")));

        summary.Starts.Count.Should().Be(Reading.Of(1), "one live instance is younger than the 10-minute window");
        summary.Starts.Basis.Should().Be(McpStartsBasis.LiveYounger);
    }

    [Theory]
    [InlineData(1.9, 60, McpKind.Idle)]
    [InlineData(1.9, 9, McpKind.Starting)]
    [InlineData(2.0, 60, McpKind.Busy)]
    [InlineData(40.0, 60, McpKind.BusyWithoutActivity)]
    public void Each_kind_at_its_edge(double cpuPercent, int ageMinutes, McpKind expected)
    {
        var settings = McpSettings.From(Defaults());
        var process = WslCare.Core.Tests.Actions.UserWorld.Process(300, Coai) with { Age = Reading.Of(TimeSpan.FromMinutes(ageMinutes)) };
        var lastWrite = expected == McpKind.BusyWithoutActivity ? Now - TimeSpan.FromMinutes(11) : Now;

        new McpJudge(settings, Now, Now, [process]).Kind(process, Reading.Of(cpuPercent), Reading.Of(lastWrite)).Should().Be(expected);
        new McpJudge(settings, Now, Now, [process]).Kind(process, Reading.Missing<double>("gone"), Reading.Of(lastWrite)).Should().Be(McpKind.Unknown);
    }

    [Fact]
    public void The_three_verdicts_warn_above_their_keys()
    {
        for (var i = 0; i < 13; i++)
        {
            Session(2000 + i, 3000 + i, TimeSpan.FromHours(1));
        }

        var sample = Sample(wait: Burn([.. Enumerable.Range(3000, 13)]));
        var verdicts = McpVerdicts.From(Reading.Of(sample), Defaults());

        verdicts.Select(v => v.Id).Should().Equal(McpVerdicts.Instances, McpVerdicts.Cpu, McpVerdicts.Starts);
        verdicts[0].Level.Should().Be(Level.Warn, "13 instances is above the default 12");
        verdicts[0].Limit.Should().Contain("mcpServers.warnInstances");
        verdicts[1].Level.Should().Be(Level.Warn, "13 × 50 % is 6.5 cores, above one");
        verdicts[2].Level.Should().Be(Level.Ok, "no log file was named in the window");
        McpVerdicts.From(Reading.Missing<McpSample>(McpServerCollector.WindowsNotYet), Defaults())
            .Should().OnlyContain(v => v.Level == Level.Unknown && v.Reason == McpServerCollector.WindowsNotYet);
    }

    /// <summary>A wait during which each pid uses 50 ticks (half a core over the 1 s window).</summary>
    private Func<TimeSpan, CancellationToken, Task> Burn(params int[] pids) => (_, _) =>
    {
        foreach (var pid in pids)
        {
            var stat = File.ReadAllText(_tree.Paths.DistroPath($"/proc/{pid}/stat"));
            var parsed = ProcStat.Parse(stat, "stat").Should().BeOfType<Reading<ProcStat>.Available>().Subject.Value;
            _tree.Stat(pid, pid - 100, "coai-mcp", 0, parsed.StartTicks, parsed.CpuTicks + 50);
        }

        return Task.CompletedTask;
    };

    private static McpKind Kind(McpSample sample, int pid) => sample.Instances.Single(i => i.Process.Pid == pid).Kind;

    /// <summary>The effective configuration with a machine layer holding <paramref name="groups"/>.</summary>
    private static EffectiveConfig Configured(params (string Group, string Json)[] groups)
    {
        var json = "{" + string.Join(",", groups.Select(g => $"\"{g.Group}\": {g.Json}")) + "}";
        var loaded = ConfigLoader.Load(
        [
            (ConfigLoader.DefaultsFile, new FileReadResult.Content(ConfigLoader.EmbeddedDefaults())),
            (new ConfigLayerFile(ConfigLayer.Machine, "/etc/wsl-care/config.json"), new FileReadResult.Content(System.Text.Encoding.UTF8.GetBytes(json))),
        ]);
        loaded.Errors.Should().BeEmpty();
        return loaded.Config;
    }
}
