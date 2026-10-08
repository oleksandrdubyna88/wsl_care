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

    private ProcessSnapshot Snapshot() => Snapshot(Now);

    private ProcessSnapshot Snapshot(DateTimeOffset at) =>
        new ProcessCollector(_tree.Files, _tree.Paths, new FixedTimeProvider(at))
            .Read(Reading.Of(new KernelFacts(Hz, 4096)), new ContainerSet([]), CancellationToken.None)
            .Should().BeOfType<Reading<ProcessSnapshot>.Available>().Subject.Value;

    /// <summary>The embedded defaults — what the product runs under with no layer.</summary>
    private static EffectiveConfig Defaults() => Configured();

    /// <summary>An unprivileged status's ledger: this account's own state folder in the sandbox (plan E14 S1).</summary>
    private McpCpuLedgerPlace OwnLedger => McpCpuLedgerPlace.ForStatus(_tree.Paths, root: false);

    private McpSample Sample(Func<TimeSpan, CancellationToken, Task>? wait = null, EffectiveConfig? config = null, IFileSystem? files = null, TimeProvider? clock = null, ProcessSnapshot? snapshot = null, McpCpuLedgerPlace? ledger = null)
    {
        var collector = new McpServerCollector(files ?? _tree.Files, _tree.Paths, clock ?? new FixedTimeProvider(Now), wait ?? ((_, _) => Task.CompletedTask), ledger ?? OwnLedger);
        var result = collector.SampleAsync(Reading.Of(snapshot ?? Snapshot()), config ?? Defaults(), CancellationToken.None).GetAwaiter().GetResult();
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
        Defaults().TextList(ConfigKeys.McpServers.Watched).Should().Equal(McpServerCatalogue.Names, "every catalogued server is watched by default");
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

    [Fact]
    public void A_server_that_bursts_every_minute_is_measured_busy_over_the_interval_not_idle_in_a_quiet_second()
    {
        // Measured 2026-10-07 19:40Z (research/2026-10-07_evening_overload.md M1-M3): coai-mcp burned in bursts about once a
        // minute — up to 1.69 cores together in a 5 s slice — while daemon 0.2.0's status read every instance 0 % and idle,
        // because its one 1 s window fell between bursts. Here: a whole core for 10 s of every 60 s (16.7 % on average), the
        // bursts at seconds 30-40 of each minute after the first sample; both samples' own 1 s windows fall in quiet seconds.
        Session(200, 300, TimeSpan.FromHours(1), cpuTicks: 1000);
        Log(300, Now - TimeSpan.FromHours(1), Now - TimeSpan.FromMinutes(15));
        var clock = new ManualTimeProvider(Now) { SteppedTimestamps = true };
        var quiet = Advancing(clock);

        var first = Sample(wait: quiet, clock: clock, snapshot: Snapshot(clock.GetUtcNow())).Instances.Single();
        clock.Advance(TimeSpan.FromSeconds(120));
        _tree.Stat(300, 200, "coai-mcp", 0, StartTicksFor(TimeSpan.FromHours(1)), 1000 + (2 * 10 * Hz));
        var second = Sample(wait: quiet, clock: clock, snapshot: Snapshot(clock.GetUtcNow())).Instances.Single();

        first.Kind.Should().Be(McpKind.Idle, "a first sighting has only the window, and its second was quiet");
        second.CpuPercent.Should().Be(Reading.Of(16.7), "2 bursts × 10 s × 100 ticks over the 120 s since the previous sample: 100 × 20 ÷ 120");
        second.Kind.Should().Be(McpKind.BusyWithoutActivity, "a sixth of a core with no log write for 17 minutes is the measured state, not idle");
    }

    /// <summary>A window that moves <paramref name="clock"/> by its length and changes no tick — a quiet second.</summary>
    private static Func<TimeSpan, CancellationToken, Task> Advancing(ManualTimeProvider clock, List<TimeSpan>? waits = null) => (window, _) =>
    {
        waits?.Add(window);
        clock.Advance(window);
        return Task.CompletedTask;
    };

    /// <summary>Plan E14 S1: a stepped clock, and one sample of pid 300 at its current instant with the ticks <paramref name="cpuTicks"/>.</summary>
    private McpInstance SampleAt(ManualTimeProvider clock, long cpuTicks, List<TimeSpan>? waits = null, IFileSystem? files = null)
    {
        _tree.Stat(300, 200, "coai-mcp", 0, StartTicksFor(TimeSpan.FromHours(1)), cpuTicks);
        return Sample(wait: Advancing(clock, waits), clock: clock, snapshot: Snapshot(clock.GetUtcNow()), files: files).Instances.Single(i => i.Process.Pid == 300);
    }

    private string LedgerFile => _tree.Paths.Rules.Join(_tree.Paths.UserStateDirectory, McpCpuLedger.FileName);

    [Fact]
    public void A_first_sighting_falls_back_to_the_window_and_says_so()
    {
        Session(200, 300, TimeSpan.FromHours(1));

        var sample = Sample(wait: Burn(300));
        var instance = sample.Instances.Single();

        instance.CpuBasis.Should().Be(McpCpuBasis.Window, "no point of this identity exists yet");
        instance.CpuOver.Should().Be(TimeSpan.FromSeconds(1));
        instance.CpuPercent.Should().Be(Reading.Of(50.0));
        sample.Baseline.Should().Be(new McpCpuBaseline(LedgerFile, true, string.Empty), "an unprivileged status keeps its readings in its own state folder");
        File.Exists(LedgerFile).Should().BeTrue();
    }

    [Fact]
    public void No_wait_when_every_instance_has_a_baseline()
    {
        Session(200, 300, TimeSpan.FromHours(1), cpuTicks: 1000);
        var clock = new ManualTimeProvider(Now) { SteppedTimestamps = true };
        var waits = new List<TimeSpan>();

        SampleAt(clock, 1000, waits);
        clock.Advance(TimeSpan.FromSeconds(121));
        var second = SampleAt(clock, 1600, waits);

        waits.Should().Equal([TimeSpan.FromSeconds(1)], "only the first sighting paid the window; the second had its baseline");
        second.CpuBasis.Should().Be(McpCpuBasis.Interval);
        second.CpuOver.Should().Be(TimeSpan.FromSeconds(121), "from the first sample's second read to now");
        second.CpuPercent.Should().Be(Reading.Of(5.0), "600 ticks at 100 ticks/s over 121 s: 100 × 6 ÷ 121 = 4.96, one decimal 5.0");
    }

    [Fact]
    public void Two_callers_a_second_apart_both_measure_over_the_interval()
    {
        // Review of the two-point rule: two VS Code windows polling status every 120 s, one second apart, must not starve each other.
        Session(200, 300, TimeSpan.FromHours(1), cpuTicks: 1000);
        var clock = new ManualTimeProvider(Now) { SteppedTimestamps = true };

        SampleAt(clock, 1000);
        SampleAt(clock, 1000);
        clock.Advance(TimeSpan.FromSeconds(120));
        var a = SampleAt(clock, 2200);
        var b = SampleAt(clock, 2200);

        a.CpuBasis.Should().Be(McpCpuBasis.Interval);
        b.CpuBasis.Should().Be(McpCpuBasis.Interval, "the second caller finds the OLDER point, still at least the minimum old");
        b.CpuOver.Should().BeGreaterThanOrEqualTo(TimeSpan.FromSeconds(120));
    }

    [Theory]
    [InlineData("boot")]
    [InlineData("pid")]
    [InlineData("older than the maximum")]
    public void A_baseline_of_another_boot_a_reused_pid_or_older_than_the_maximum_is_not_used(string change)
    {
        Session(200, 300, TimeSpan.FromHours(1), cpuTicks: 1000);
        var clock = new ManualTimeProvider(Now) { SteppedTimestamps = true };
        SampleAt(clock, 1000);
        clock.Advance(change == "older than the maximum" ? TimeSpan.FromMinutes(21) : TimeSpan.FromSeconds(121));
        switch (change)
        {
            case "boot":
                _tree.BootId("6d1c1c5e-0000-4000-8000-000000000002");
                break;
            case "pid":
                // The same pid, another process: started 10 s ago.
                _tree.Process(300, 200, "/a", 30_000, words: [Coai], startTicks: StartTicksFor(TimeSpan.FromHours(1)) + (3600 * Hz), cpuTicks: 5000);
                break;
        }

        var instance = Sample(wait: Advancing(clock), clock: clock, snapshot: Snapshot(clock.GetUtcNow())).Instances.Single();

        instance.CpuBasis.Should().Be(McpCpuBasis.Window, $"a point of {change} is no baseline");
    }

    [Fact]
    public void A_four_hour_average_does_not_make_a_now_quiet_server_busy_without_activity()
    {
        // Review finding 3: a server that burned for an hour, three hours ago, and is quiet now — an average over four hours
        // (the root timer's interval) is above the idle line, and with no log write for ten minutes it would read "busy without
        // activity". The maximum interval (20 min) keeps the kind about NOW: the window answers instead.
        Session(200, 300, TimeSpan.FromHours(1), cpuTicks: 1000);
        Log(300, Now - TimeSpan.FromHours(1), Now - TimeSpan.FromMinutes(15));
        var clock = new ManualTimeProvider(Now) { SteppedTimestamps = true };
        SampleAt(clock, 1000);
        clock.Advance(TimeSpan.FromHours(4));

        var instance = SampleAt(clock, 1000 + (3600 * Hz));

        instance.CpuBasis.Should().Be(McpCpuBasis.Window);
        instance.Kind.Should().Be(McpKind.Idle, "it is quiet now; an hour of CPU three hours ago is not activity now");
    }

    [Fact]
    public void A_wall_clock_jump_does_not_change_the_rate()
    {
        Session(200, 300, TimeSpan.FromHours(1), cpuTicks: 1000);
        var clock = new ManualTimeProvider(Now) { SteppedTimestamps = true };
        SampleAt(clock, 1000);
        clock.Advance(TimeSpan.FromSeconds(120));
        clock.JumpWallClock(TimeSpan.FromHours(3));

        var instance = SampleAt(clock, 1000 + (2 * 10 * Hz));

        instance.CpuBasis.Should().Be(McpCpuBasis.Interval);
        instance.CpuPercent.Should().Be(Reading.Of(16.7), "the monotonic clock is the denominator: 120 s passed, not 3 h");
    }

    [Theory]
    [InlineData("{ not json")]
    [InlineData("{\"schemaVersion\": 1, \"bootId\": \"\", \"entries\": []}")]
    // Own code review, finding 1: JSON that parses but has the wrong shape — a null entry, this instance's entry with no points
    // or a null point, another schema — must read as "no baseline", never crash status and the timer for good.
    [InlineData("{\"schemaVersion\": 1, \"bootId\": \"" + SyntheticProcTree.FirstBootId + "\", \"entries\": [null]}")]
    [InlineData("{\"schemaVersion\": 1, \"bootId\": \"" + SyntheticProcTree.FirstBootId + "\", \"entries\": [{\"pid\": 300, \"startTicks\": START}]}")]
    [InlineData("{\"schemaVersion\": 1, \"bootId\": \"" + SyntheticProcTree.FirstBootId + "\", \"entries\": [{\"pid\": 300, \"startTicks\": START, \"points\": [null]}]}")]
    // The cadence consultation (2026-10-08): a point whose monotonic reading is absurd overflowed the age into an exception.
    [InlineData("{\"schemaVersion\": 1, \"bootId\": \"" + SyntheticProcTree.FirstBootId + "\", \"entries\": [{\"pid\": 300, \"startTicks\": START, \"points\": [{\"cpuTicks\": 1, \"wall\": \"2026-10-03T00:00:00+00:00\", \"monotonicMs\": -1000000000000000}]}]}")]
    [InlineData("{\"schemaVersion\": 1, \"bootId\": \"" + SyntheticProcTree.FirstBootId + "\", \"entries\": [{\"pid\": 300, \"startTicks\": START, \"points\": [{\"cpuTicks\": -5, \"wall\": \"2026-10-03T00:00:00+00:00\", \"monotonicMs\": 1}]}]}")]
    public void An_unreadable_or_malformed_ledger_is_no_baseline_never_a_failed_sample(string content)
    {
        Session(200, 300, TimeSpan.FromHours(1));
        Directory.CreateDirectory(Path.GetDirectoryName(LedgerFile)!);
        File.WriteAllText(LedgerFile, content.Replace("START", StartTicksFor(TimeSpan.FromHours(1)).ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal));

        var sample = Sample(wait: Burn(300));

        sample.Instances.Single().CpuBasis.Should().Be(McpCpuBasis.Window);
        sample.Baseline.Recorded.Should().BeTrue("the ledger is rewritten from this sample");
    }

    [Fact]
    public void A_ledger_of_another_schema_version_is_no_baseline()
    {
        Session(200, 300, TimeSpan.FromHours(1), cpuTicks: 1000);
        var clock = new ManualTimeProvider(Now) { SteppedTimestamps = true };
        SampleAt(clock, 1000);
        File.WriteAllText(LedgerFile, File.ReadAllText(LedgerFile).Replace("\"schemaVersion\":1", "\"schemaVersion\":99", StringComparison.Ordinal).Replace("\"schemaVersion\": 1", "\"schemaVersion\": 99", StringComparison.Ordinal));
        clock.Advance(TimeSpan.FromSeconds(121));

        SampleAt(clock, 2000).CpuBasis.Should().Be(McpCpuBasis.Window, "a file of another schema is not read as this one's");
    }

    [Fact]
    public void Without_the_kernels_tick_rate_the_cpu_is_unmeasured_on_either_basis()
    {
        // Own code review, finding 2: the interval path said "interval" for a figure it could not compute.
        Session(200, 300, TimeSpan.FromHours(1), cpuTicks: 1000);
        var clock = new ManualTimeProvider(Now) { SteppedTimestamps = true };
        SampleAt(clock, 1000);
        clock.Advance(TimeSpan.FromSeconds(121));
        File.Delete(_tree.Paths.DistroPath("/proc/self/auxv"));

        var instance = SampleAt(clock, 2000);

        instance.CpuPercent.IsAvailable.Should().BeFalse();
        instance.CpuBasis.Should().Be(McpCpuBasis.None, "a figure that was not measured has no basis");
    }

    [Fact]
    public void Without_the_kernels_tick_rate_no_window_is_waited()
    {
        // coai code round 2026-10-08, finding 7: a first sighting waited the window although no rate could come of it.
        Session(200, 300, TimeSpan.FromHours(1));
        File.Delete(_tree.Paths.DistroPath("/proc/self/auxv"));
        var waited = false;

        var instance = Sample(wait: (_, _) =>
        {
            waited = true;
            return Task.CompletedTask;
        }).Instances.Single();

        waited.Should().BeFalse("without the tick rate the window cannot produce a figure");
        instance.CpuBasis.Should().Be(McpCpuBasis.None);
        instance.CpuPercent.ReasonOrEmpty.Should().NotBeEmpty();
    }

    [Fact]
    public void A_temp_file_that_cannot_be_swept_does_not_stop_the_ledger_being_written()
    {
        // coai code round 2026-10-08, finding 2: the sweep is housekeeping; an error removing one orphan aborted the write.
        Session(200, 300, TimeSpan.FromHours(1));
        var folder = Path.GetDirectoryName(LedgerFile)!;
        Directory.CreateDirectory(folder);
        var orphan = Path.Combine(folder, $"{McpCpuLedger.FileName}.{new string('d', 32)}.tmp");
        File.WriteAllText(orphan, "{");
        File.SetLastWriteTimeUtc(orphan, (Now - TimeSpan.FromHours(1)).UtcDateTime);

        var sample = Sample(wait: Burn(300), files: new FailingDeletes(_tree.Files));

        sample.Baseline.Recorded.Should().BeTrue(sample.Baseline.Reason);
        File.Exists(LedgerFile).Should().BeTrue();
    }

    /// <summary>Every delete throws, as a file held open by another process does on Windows.</summary>
    private sealed class FailingDeletes(IFileSystem inner) : DelegatingFileSystem(inner)
    {
        public override Core.Files.Deletion.DeletionVerdict DeleteFile(string path, Core.Files.Deletion.DeletionScope scope) =>
            throw new IOException($"{path} is in use");
    }

    [Fact]
    public void A_full_ledger_stays_inside_its_read_cap_and_keeps_the_newest_processes()
    {
        // Own code review, finding 3: a ledger larger than records.maxStateFileBytes reads as empty for ever — every sample a window.
        var widest = new McpCpuPoint(long.MaxValue, DateTimeOffset.MaxValue, long.MaxValue);
        McpCpuFile Of(int entries) => new(Core.SchemaVersion.Current, SyntheticProcTree.FirstBootId,
            [.. Enumerable.Range(0, entries).Select(i => new McpCpuEntry(int.MaxValue - i, long.MaxValue - i, [widest, widest]))]);
        var perEntry = McpCpuLedger.Serialise(Of(2)).Length - McpCpuLedger.Serialise(Of(1)).Length;
        var cap = ConfigKeys.Records.MaxStateFileBytes.Min;
        var readings = Enumerable.Range(0, McpCpuLedger.MaxEntries(cap) + 5)
            .Select(i => new McpCpuReading(1000 + i, i, new McpCpuPoint(i, Now, 1)))
            .ToList();

        var next = McpCpuLedger.Next(McpCpuFile.Empty, SyntheticProcTree.FirstBootId, readings, McpSettings.From(Defaults()).Bounds, McpCpuLedger.MaxEntries(cap));

        perEntry.Should().BeLessThanOrEqualTo(McpCpuLedger.BytesPerEntry, "the bound the cap is divided by");
        McpCpuLedger.Serialise(Of(McpCpuLedger.MaxEntries(cap))).Length.Should().BeLessThanOrEqualTo(cap, "a full ledger of the widest entries is still read back");
        next.Entries.Should().HaveCount(McpCpuLedger.MaxEntries(cap));
        next.Entries.Min(e => e.StartTicks).Should().Be(5, "past the cap the OLDEST processes go (no baseline: the window answers)");
    }

    [Fact]
    public void A_ledger_that_is_a_link_is_not_written_through()
    {
        // The cadence consultation (2026-10-08): the read refused a linked ledger, but the atomic writer resolved the link and
        // replaced its target — a sibling file in the same folder passed the scope check.
        Assert.SkipUnless(OperatingSystem.IsLinux(), "a file link needs no privilege on Linux");
        Session(200, 300, TimeSpan.FromHours(1));
        var folder = Path.GetDirectoryName(LedgerFile)!;
        Directory.CreateDirectory(folder);
        var sentinel = Path.Combine(folder, "sentinel.txt");
        File.WriteAllText(sentinel, "keep me");
        File.CreateSymbolicLink(LedgerFile, sentinel);

        var sample = Sample(wait: Burn(300));

        File.ReadAllText(sentinel).Should().Be("keep me", "the ledger's place is a link, and a link is never written through");
        sample.Baseline.Recorded.Should().BeFalse();
        sample.Baseline.Reason.Should().Contain("link");
    }

    [Fact]
    public void A_write_sweeps_the_ledgers_own_orphaned_temp_files_and_nothing_else()
    {
        // coai plan round finding 3 (2026-10-08): a status killed between the atomic write's temp file and its rename (the
        // extension's 20 s ceiling) left the temp file for ever — a run that dies must leave a state the next one sweeps.
        Session(200, 300, TimeSpan.FromHours(1));
        var folder = Path.GetDirectoryName(LedgerFile)!;
        Directory.CreateDirectory(folder);
        string Planted(string name, TimeSpan age)
        {
            var path = Path.Combine(folder, name);
            File.WriteAllText(path, "{");
            File.SetLastWriteTimeUtc(path, (Now - age).UtcDateTime);
            return path;
        }

        var orphan = Planted($"{McpCpuLedger.FileName}.{new string('a', 32)}.tmp", TimeSpan.FromMinutes(5));
        var inFlight = Planted($"{McpCpuLedger.FileName}.{new string('b', 32)}.tmp", TimeSpan.FromSeconds(1));
        var foreign = Planted($"other.json.{new string('c', 32)}.tmp", TimeSpan.FromHours(5));
        var lookalike = Planted($"{McpCpuLedger.FileName}.not-a-guid.tmp", TimeSpan.FromHours(5));

        Sample(wait: Burn(300)).Baseline.Recorded.Should().BeTrue();

        File.Exists(orphan).Should().BeFalse("a temp of this ledger older than the minimum interval is a write that died");
        File.Exists(inFlight).Should().BeTrue("a young one may be a concurrent writer's, about to be renamed");
        File.Exists(foreign).Should().BeTrue("only the ledger's own temp names are swept");
        File.Exists(lookalike).Should().BeTrue("only the atomic writer's exact temp shape is swept");
    }

    [Fact]
    public void An_unchanged_ledger_is_not_rewritten()
    {
        Session(200, 300, TimeSpan.FromHours(1), cpuTicks: 1000);
        var clock = new ManualTimeProvider(Now) { SteppedTimestamps = true };
        var writes = new CountingWrites(_tree.Files);
        SampleAt(clock, 1000, files: writes);

        var second = Sample(wait: Advancing(clock), clock: clock, snapshot: Snapshot(clock.GetUtcNow()), files: writes);

        writes.Count.Should().Be(1, "a point younger than the minimum changes nothing, so the second sample writes nothing");
        second.Baseline.Recorded.Should().BeTrue("what it would write is already there");
    }

    [Fact]
    public void A_place_that_may_not_write_records_nothing_and_says_why()
    {
        Session(200, 300, TimeSpan.FromHours(1));
        var root = McpCpuLedgerPlace.ForStatus(_tree.Paths, root: true);
        var readOnlyCollect = McpCpuLedgerPlace.ForCollect(_tree.Paths, mayRecord: false, "read-only: run as root to record");

        var asRoot = Sample(wait: Burn(300), ledger: root);
        var unprivilegedCollect = Sample(wait: Burn(300), ledger: readOnlyCollect);

        asRoot.Baseline.Should().Be(McpCpuBaseline.NotRecorded(_tree.Paths.Rules.Join(_tree.Paths.StateDirectory, McpCpuLedger.FileName), McpCpuLedger.ReadOnlyRoot));
        unprivilegedCollect.Baseline.Reason.Should().Contain("read-only");
        Directory.Exists(_tree.Paths.StateDirectory).Should().BeFalse("status as root and a read-only collect write no ledger");
        File.Exists(LedgerFile).Should().BeFalse();
    }

    [Fact]
    public void The_root_timer_records_its_ledger_in_the_state_directory()
    {
        Session(200, 300, TimeSpan.FromHours(1));

        var sample = Sample(wait: Burn(300), ledger: McpCpuLedgerPlace.ForCollect(_tree.Paths, mayRecord: true, "unused"));

        sample.Baseline.Recorded.Should().BeTrue(sample.Baseline.Reason);
        File.Exists(_tree.Paths.Rules.Join(_tree.Paths.StateDirectory, McpCpuLedger.FileName)).Should().BeTrue();
        File.Exists(LedgerFile).Should().BeFalse("root never writes into the user's home");
    }

    /// <summary>Counts the private atomic writes — the ledger's only write.</summary>
    private sealed class CountingWrites(IFileSystem inner) : DelegatingFileSystem(inner)
    {
        public int Count { get; private set; }

        public override Core.Files.Deletion.DeletionVerdict WritePrivateFileAtomically(string path, ReadOnlySpan<byte> content, Core.Files.Deletion.DeletionScope scope)
        {
            Count++;
            return base.WritePrivateFileAtomically(path, content, scope);
        }
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
    public void A_pid_reused_between_the_snapshot_and_the_first_read_has_cpu_unavailable()
    {
        // Final-round-3 finding 6: the two CPU reads were compared with each other only — a server that exited after the
        // snapshot, its pid taken by another process before the first read, had THAT process's CPU reported as its own.
        Session(200, 300, TimeSpan.FromHours(1));
        var snapshot = Snapshot();
        _tree.Stat(300, 200, "coai-mcp", 0, StartTicksFor(TimeSpan.FromSeconds(5)), 5000);

        var instance = Sample(wait: Burn(300), snapshot: snapshot).Instances.Single();

        instance.CpuPercent.IsAvailable.Should().BeFalse("the first read is another process than the one the snapshot saw");
        instance.CpuPercent.ReasonOrEmpty.Should().Contain("after the snapshot");
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
        var starts = sample.Servers.Single(s => s.Name == "coai-mcp").Starts;

        starts.Count.Should().Be(Reading.Of(34));
        starts.Basis.Should().Be(McpStartsBasis.LogNames);
        McpVerdicts.From(Reading.Of(sample), Defaults()).Single(v => v.Id == McpVerdicts.Starts).Level.Should().Be(Level.Warn);
    }

    [Fact]
    public void Each_start_in_the_window_is_listed_with_its_time_pid_last_write_and_whether_it_runs()
    {
        // The owner's correction (2026-10-07): the storm of 2026-10-06 began at 16:50Z, BEFORE the 0.43.0 binary's mtime of
        // 16:54Z — a count per window cannot say when churn began; each start must carry its own time.
        Session(200, 300, TimeSpan.FromMinutes(2));
        Log(300, Now - TimeSpan.FromMinutes(2), Now);
        Log(1001, Now - TimeSpan.FromMinutes(6), Now - TimeSpan.FromMinutes(6) + TimeSpan.FromSeconds(30));
        Log(1002, Now - TimeSpan.FromMinutes(20), Now - TimeSpan.FromMinutes(19));

        var starts = Sample().Servers.Single(s => s.Name == "coai-mcp").Starts;

        starts.Recent.Select(s => (s.At, s.Pid, s.LastWrite, s.Running)).Should().Equal(
            [
                (Now - TimeSpan.FromMinutes(2), 300, Now, true),
                (Now - TimeSpan.FromMinutes(6), 1001, Now - TimeSpan.FromMinutes(6) + TimeSpan.FromSeconds(30), false),
            ],
            "newest first, only the starts inside the window; a dead start that lived 30 s is the shape of a connect timeout");
    }

    [Fact]
    public void The_listed_starts_are_capped_by_their_key_and_the_count_is_not()
    {
        for (var i = 0; i < 5; i++)
        {
            Log(1000 + i, Now - TimeSpan.FromMinutes(1 + i), Now);
        }

        var starts = Sample(config: Configured(("mcpServers", "{\"maxStartsListed\": 2}"))).Servers.Single(s => s.Name == "coai-mcp").Starts;

        starts.Count.Should().Be(Reading.Of(5));
        starts.Recent.Select(s => s.Pid).Should().Equal(1000, 1001);
    }

    [Fact]
    public void A_midnight_file_of_a_live_process_started_after_midnight_is_a_start()
    {
        // Plan round finding 1: pid 300 also has a file from an earlier run yesterday; the process running now started after
        // midnight, so its 00-00-00 file is a START, not a continuation.
        Session(200, 300, TimeSpan.FromMinutes(5));
        Log(300, Now.Date.AddHours(-5), Now.Date.AddHours(-4));
        Log(300, new DateTimeOffset(Now.Date, TimeSpan.Zero), Now);

        Sample().Servers.Single(s => s.Name == "coai-mcp").Starts.Count.Should().Be(Reading.Of(1));
    }

    [Fact]
    public void A_midnight_file_of_a_live_process_that_outlived_the_day_is_a_continuation()
    {
        Session(200, 300, TimeSpan.FromHours(5));
        Log(300, Now.Date.AddHours(-5), Now.Date.AddSeconds(-1));
        Log(300, new DateTimeOffset(Now.Date, TimeSpan.Zero), Now);

        Sample().Servers.Single(s => s.Name == "coai-mcp").Starts.Count.Should().Be(Reading.Of(0));
    }

    [Fact]
    public void A_cut_listing_makes_starts_unavailable_not_partial()
    {
        for (var i = 0; i < 120; i++)
        {
            Log(1000 + i, Now - TimeSpan.FromSeconds(i), Now);
        }

        var starts = Sample(config: Configured(("mcpServers", "{\"maxLogEntries\": 100}"))).Servers.Single(s => s.Name == "coai-mcp").Starts;

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

        var starts = Sample(files: files).Servers.Single(s => s.Name == "coai-mcp").Starts;

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
        McpVerdicts.From(Reading.Of(sample), config).Single(v => v.Id == McpVerdicts.Cpu).Value.Should().Contain("over the 1 listed", "final code round 2/3: the CPU total covers the listed instances only");
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

        Sample().Servers.Single(s => s.Name == "coai-mcp").Starts.Count.Should().Be(Reading.Of(1), "the run that began before midnight started once; its midnight segment is not a second start");
    }

    [Fact]
    public void A_linked_log_root_is_not_listed()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "a folder link needs no privilege on Linux");
        _tree.FileAt("/data/elsewhere/2026-10-03/coai-mcp-00-04-00-7.log", Now);
        Directory.CreateDirectory(_tree.Paths.DistroPath("/home/me/.local/share/coai-mcp"));
        Directory.CreateSymbolicLink(_tree.Paths.DistroPath(Logs), _tree.Paths.DistroPath("/data/elsewhere"));

        var starts = Sample().Servers.Single(s => s.Name == "coai-mcp").Starts;

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
