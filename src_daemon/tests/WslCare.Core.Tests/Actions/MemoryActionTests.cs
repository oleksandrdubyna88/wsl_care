using FluentAssertions;

using WslCare.Core.Actions;
using WslCare.Core.Actions.Engine;
using WslCare.Core.Actions.Memory;
using WslCare.Core.Collectors;
using WslCare.Core.Config;
using WslCare.Core.Processes;
using WslCare.Core.Processes.Policy;
using WslCare.Core.Records;
using WslCare.TestSupport;

namespace WslCare.Core.Tests.Actions;

/// <summary>
/// A1 (<c>sync</c>, then <c>sysctl -w vm.drop_caches=1</c>) and A2 (<c>sysctl -w vm.compact_memory=1</c>), E3.S3: their live
/// previews from a sandboxed <c>/proc</c>, their triggers at the plan §4.1 edges, their measured runs, and — through the
/// engine — the two rules that make A2 special: it runs AFTER A1 in the same run, and its EVENT (no free order-7 block, an
/// allocation failure) does not wait for an idle machine while A1, on the same busy machine, is deferred. sysctl and sync
/// are a recording runner under the PRODUCT policy; their "effect" is the test rewriting <c>/proc</c>.
/// </summary>
public sealed class MemoryActionTests : IDisposable
{
    private const long Gib = 1024L * 1024;
    private const int Pid = 4242;

    private readonly LinuxSandbox _sandbox = new("memory");
    private readonly RecordingCommandRunner _runner = new() { Policy = CommandPolicy.Product };

    public MemoryActionTests()
    {
        _sandbox.Write("/etc/passwd", "root:x:0:0::/root:/bin/bash\nme:x:1000:1000::/home/me:/bin/bash\n");
        _sandbox.Load(0.1, 0.1, 0.1, cpus: 4);
    }

    public void Dispose() => _sandbox.Dispose();

    private (ActionContext Context, ActionCommands Commands) For(ICleanupAction action, string userConfig = "{}", Func<ActionId, bool>? ranEarlier = null)
    {
        _sandbox.Write("/home/me/.config/wsl-care/config.json", userConfig);
        var config = ConfigLoader.Load(_sandbox.Paths, _sandbox.Files).Config;
        var context = new ActionContext(_sandbox.Paths, _sandbox.Files, new FixedTimeProvider(), config, RunTrigger.Timer, new TargetUserResult.None("not needed"))
        {
            RanEarlier = ranEarlier ?? (static _ => false),
        };
        return (context, new ActionCommands(action, _runner, context.TargetUser, []));
    }

    private ActionEngine Engine(params ICleanupAction[] actions) =>
        new(new EngineContext(_sandbox.Paths, _sandbox.Files, _runner, new FixedTimeProvider(), new LinuxProbe(_sandbox.Files, _sandbox.Paths, new FixedTimeProvider()),
            ConfigLoader.Load(_sandbox.Paths, _sandbox.Files), new FakeProcessTable().Alive(Pid, FixedTimeProvider.DefaultNow.AddMinutes(-1)), Pid, new ActionRegistry(actions)));

    private void MemoryPressure(double avg60) =>
        _sandbox.Write("/proc/pressure/memory", FormattableString.Invariant($"some avg10=1.00 avg60={avg60:0.00} avg300=1.00 total=1000\nfull avg10=0.00 avg60=0.00 avg300=0.00 total=0\n"));

    /// <summary>E14 S5, SHADOW only: A1 and A2 record the memory pressure and SAY whether the pressure rule (memory PSI some avg60 above
    /// <c>thresholds.memoryPressureWarn</c>, the S6 rule) would have fired — the evidence the S8 soak needs before it may become a
    /// trigger — and fire exactly as before.</summary>
    [Fact]
    public async Task A1_and_A2_say_whether_memory_pressure_would_have_fired_and_fire_as_before()
    {
        var a1 = new CacheDrop();
        var a2 = new Compaction();
        _sandbox.Memory(totalKib: 40 * Gib, availableKib: 20 * Gib, cachedKib: 1 * Gib, order7Blocks: 80);
        var (context1, commands1) = For(a1);
        var (context2, commands2) = For(a2);

        MemoryPressure(25);
        var pressed1 = await a1.PreviewAsync(context1, commands1, CancellationToken.None);
        var pressed2 = await a2.PreviewAsync(context2, commands2, CancellationToken.None);
        MemoryPressure(1);
        var calm1 = await a1.PreviewAsync(context1, commands1, CancellationToken.None);
        _sandbox.Write("/proc/pressure/memory", "garbage\n");
        var unread1 = await a1.PreviewAsync(context1, commands1, CancellationToken.None);

        pressed1.Facts[MemoryPressureShadow.Fact].Should().Be(2500, "avg60 25.00 in hundredths");
        a1.Trigger(pressed1, context1.Config).Should().Match<TriggerDecision>(d => !d.Fired && d.Reason.Contains(MemoryPressureShadow.WouldFire), "50 % available and a small cache: A1 does not fire, the pressure rule would have");
        a2.Trigger(pressed2, context2.Config).Should().Match<TriggerDecision>(d => !d.Fired && d.Reason.Contains(MemoryPressureShadow.WouldFire));
        a1.Trigger(calm1, context1.Config).Should().Match<TriggerDecision>(d => !d.Fired && d.Reason.Contains(MemoryPressureShadow.WouldNotFire));
        unread1.Facts.Should().NotContainKey(MemoryPressureShadow.Fact);
        a1.Trigger(unread1, context1.Config).Reason.Should().Contain(MemoryPressureShadow.NotRead);
    }

    [Fact]
    public async Task A1_previews_the_page_cache_and_fires_below_the_act_threshold_or_on_a_big_cache_with_little_available()
    {
        var action = new CacheDrop();
        _sandbox.Memory(totalKib: 40 * Gib, availableKib: 4 * Gib, cachedKib: 6 * Gib, order7Blocks: 100);
        var (context, commands) = For(action);

        var low = await action.PreviewAsync(context, commands, CancellationToken.None);

        low.Available.Should().BeTrue();
        low.Bytes.Should().BeNull("memory, not disk: a dry run must not count it as would-free bytes");
        low.Items.Single().Bytes.Should().Be(6 * Gib * 1024, "the page cache is the most A1 can give back");
        low.Facts[CacheDrop.AvailablePermilleFact].Should().Be(100, "4 of 40 GiB is 10.0 %");
        action.Trigger(low, context.Config).Fired.Should().BeTrue("10 % is below thresholds.memAvailableActPercent (15)");

        _sandbox.Memory(totalKib: 40 * Gib, availableKib: 11 * Gib, cachedKib: 13 * Gib, order7Blocks: 100);
        var bigCache = await action.PreviewAsync(context, commands, CancellationToken.None);
        action.Trigger(bigCache, context.Config).Fired.Should().BeTrue("a 13 GiB cache with 27.5 % available is the second trigger");

        _sandbox.Memory(totalKib: 40 * Gib, availableKib: 13 * Gib, cachedKib: 13 * Gib, order7Blocks: 100);
        var healthy = await action.PreviewAsync(context, commands, CancellationToken.None);
        action.Trigger(healthy, context.Config).Should().Match<TriggerDecision>(d => !d.Fired && d.Reason.Contains("32.5 %"), "32.5 % available is above both edges");
        _runner.Requests.Should().BeEmpty("a preview reads /proc and starts nothing");
    }

    [Fact]
    public async Task A1_runs_sync_then_the_drop_with_the_literal_value_1_and_measures_the_cache_before_and_after()
    {
        var action = new CacheDrop();
        _sandbox.Memory(totalKib: 40 * Gib, availableKib: 4 * Gib, cachedKib: 6 * Gib, order7Blocks: 100);
        _runner.ScriptEffect(argv => argv is ["sysctl", "-w", "vm.drop_caches=1"], _ =>
        {
            _sandbox.Memory(totalKib: 40 * Gib, availableKib: 9 * Gib, cachedKib: 1 * Gib, order7Blocks: 100);
            return RecordingCommandRunner.Exited(0, "vm.drop_caches = 1");
        });
        var (context, commands) = For(action);

        var run = await action.RunAsync(context, await action.PreviewAsync(context, commands, CancellationToken.None), commands, CancellationToken.None);

        _runner.Commands.Should().Equal("sync", "sysctl -w vm.drop_caches=1");
        run.Succeeded.Should().BeTrue();
        run.FreedBytes.Should().BeNull("A1 frees memory, not disk");
        (run.BeforeBytes, run.AfterBytes).Should().Be((6 * Gib * 1024, 1 * Gib * 1024));
        run.Notes.Should().ContainSingle(n => n.Contains("10.0 % before, 22.5 % after"));
    }

    [Fact]
    public async Task A1_whose_sync_fails_never_asks_for_the_drop()
    {
        var action = new CacheDrop();
        _sandbox.Memory(totalKib: 40 * Gib, availableKib: 4 * Gib, cachedKib: 6 * Gib, order7Blocks: 100);
        _runner.Script(["sync"], 1, string.Empty, "sync: error syncing");
        var (context, commands) = For(action);

        var run = await action.RunAsync(context, await action.PreviewAsync(context, commands, CancellationToken.None), commands, CancellationToken.None);

        run.Succeeded.Should().BeFalse();
        run.Failure.Should().Contain("sync exited 1");
        _runner.Commands.Should().Equal("sync");
    }

    [Fact]
    public async Task A1_without_a_readable_meminfo_is_unavailable_never_zero()
    {
        var action = new CacheDrop();
        var (context, commands) = For(action);

        var preview = await action.PreviewAsync(context, commands, CancellationToken.None);

        preview.Available.Should().BeFalse();
        preview.Reason.Should().Contain("meminfo");
        action.Trigger(preview, context.Config).Fired.Should().BeFalse();
    }

    [Fact]
    public async Task A2_is_urgent_on_an_order_7_shortage_or_an_allocation_failure_and_fires_after_a1_otherwise_not()
    {
        var action = new Compaction();
        _sandbox.Memory(totalKib: 40 * Gib, availableKib: 20 * Gib, cachedKib: 1 * Gib, order7Blocks: 0);
        var (context, commands) = For(action);

        var shortage = await action.PreviewAsync(context, commands, CancellationToken.None);
        shortage.Urgent.Should().Contain("no free order-7 block");
        action.Trigger(shortage, context.Config).Fired.Should().BeTrue();

        _sandbox.Memory(totalKib: 40 * Gib, availableKib: 20 * Gib, cachedKib: 1 * Gib, order7Blocks: 80);
        var quiet = await action.PreviewAsync(context, commands, CancellationToken.None);
        quiet.Urgent.Should().BeEmpty();
        action.Trigger(quiet, context.Config).Fired.Should().BeFalse("80 free order-7 blocks, no failure, A1 did not run");

        var (afterA1, afterCommands) = For(action, ranEarlier: id => id.Text == "A1");
        action.Trigger(await action.PreviewAsync(afterA1, afterCommands, CancellationToken.None), afterA1.Config).Should()
            .Match<TriggerDecision>(d => d.Fired && d.Reason.Contains("A1 ran"), "plan 5: A2 after A1");

        _runner.Script(argv => argv is ["journalctl", "--since", _, "--no-pager", "--quiet", "--output=cat", "--dmesg", "--grep=page allocation failure"],
            RecordingCommandRunner.Exited(0, "kworker/u8:2: page allocation failure: order:7, mode:0x40cc0\n"));
        var failure = await action.PreviewAsync(context, commands, CancellationToken.None);
        failure.Urgent.Should().Contain("1 page allocation failure");
        failure.Facts[Compaction.AllocationFailuresFact].Should().Be(1);
    }

    [Fact]
    public async Task A2_runs_the_compaction_and_measures_the_free_order_7_blocks_before_and_after()
    {
        var action = new Compaction();
        _sandbox.Memory(totalKib: 40 * Gib, availableKib: 20 * Gib, cachedKib: 1 * Gib, order7Blocks: 0);
        _runner.ScriptEffect(argv => argv is ["sysctl", "-w", "vm.compact_memory=1"], _ =>
        {
            _sandbox.Memory(totalKib: 40 * Gib, availableKib: 20 * Gib, cachedKib: 1 * Gib, order7Blocks: 64);
            return RecordingCommandRunner.Exited(0);
        });
        var (context, commands) = For(action);

        var run = await action.RunAsync(context, await action.PreviewAsync(context, commands, CancellationToken.None), commands, CancellationToken.None);

        run.Succeeded.Should().BeTrue();
        run.Count.Should().Be(1);
        run.FreedBytes.Should().BeNull("defragmentation frees nothing");
        run.AfterBytes.Should().Be(64L * 4096 * 128, "64 blocks of 2^7 pages of 4 KiB");
        run.Notes.Should().ContainSingle(n => n.Contains("free order-7 blocks 0 before, 64 after"));
        _runner.Commands.Should().Contain("sysctl -w vm.compact_memory=1");
    }

    [Fact]
    public async Task On_a_busy_machine_the_timer_defers_a1_but_the_order_7_event_runs_a2_at_once()
    {
        _sandbox.Load(7.9, 7.9, 7.9, cpus: 4);
        _sandbox.Memory(totalKib: 40 * Gib, availableKib: 4 * Gib, cachedKib: 6 * Gib, order7Blocks: 0);
        _sandbox.Write("/home/me/.config/wsl-care/config.json", "{ \"dryRun\": false }");
        _sandbox.Write("/var/lib/wsl-care/first-timer-run.json", "{\"schemaVersion\":1,\"at\":\"2026-09-01T00:00:00+00:00\"}");

        var result = await Engine(new CacheDrop(), new Compaction()).ExecuteAsync(new ActRequest([ActionId.Find("A1")!, ActionId.Find("A2")!], RunTrigger.Timer, Execute: true), CancellationToken.None);

        var actions = result.Should().BeOfType<ActResult.Done>().Subject.Detail.Actions;
        actions.Select(a => $"{a.Id}:{a.Status}").Should().Equal("A1:deferred", "A2:ran");
        actions[0].Reason.Should().Contain("CPU");
        actions[1].Reason.Should().Contain("without waiting for idle").And.Contain("order-7");
        _runner.Commands.Should().Contain("sysctl -w vm.compact_memory=1").And.NotContain("sysctl -w vm.drop_caches=1");
    }

    [Fact]
    public async Task On_an_idle_machine_the_timer_runs_a1_and_then_a2_because_a1_ran()
    {
        _sandbox.Memory(totalKib: 40 * Gib, availableKib: 4 * Gib, cachedKib: 6 * Gib, order7Blocks: 80);
        _sandbox.Write("/home/me/.config/wsl-care/config.json", "{ \"dryRun\": false }");
        _sandbox.Write("/var/lib/wsl-care/first-timer-run.json", "{\"schemaVersion\":1,\"at\":\"2026-09-01T00:00:00+00:00\"}");

        var result = await Engine(new CacheDrop(), new Compaction()).ExecuteAsync(new ActRequest([ActionId.Find("A2")!, ActionId.Find("A1")!], RunTrigger.Timer, Execute: true), CancellationToken.None);

        var actions = result.Should().BeOfType<ActResult.Done>().Subject.Detail.Actions;
        actions.Select(a => $"{a.Id}:{a.Status}").Should().Equal("A1:ran", "A2:ran");
        _runner.Commands.Where(c => c.StartsWith("sysctl", StringComparison.Ordinal)).Should().Equal("sysctl -w vm.drop_caches=1", "sysctl -w vm.compact_memory=1");
    }

    [Fact]
    public async Task In_the_dry_run_week_the_event_is_recorded_as_dry_run_and_nothing_runs()
    {
        _sandbox.Load(7.9, 7.9, 7.9, cpus: 4);
        _sandbox.Memory(totalKib: 40 * Gib, availableKib: 20 * Gib, cachedKib: 1 * Gib, order7Blocks: 0);

        var result = await Engine(new Compaction()).ExecuteAsync(new ActRequest([ActionId.Find("A2")!], RunTrigger.Timer, Execute: true), CancellationToken.None);

        result.Should().BeOfType<ActResult.Done>().Subject.Detail.Actions.Single().Status.Should().Be(ActionStatus.DryRun, "the event skips the idle gate, never the dry-run week");
        _runner.Commands.Should().NotContain(c => c.StartsWith("sysctl", StringComparison.Ordinal));
    }
}
