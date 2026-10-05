using FluentAssertions;

using WslCare.Core.Agents;
using WslCare.Core.Files;
using WslCare.TestSupport;

namespace WslCare.Core.Tests.Agents;

/// <summary>
/// The agent walk (plan §15q D1, D2, R2.3; H1–H3): sizes and counts only — <c>memory</c> never entered for ANY agent, an
/// entry's own prefixes never entered, no link followed, a change of filesystem stopped at and named (review C1), one total
/// budget across every agent (review M7), sessions counted only over a confirmed layout and by listing alone.
/// </summary>
public sealed class AgentWalkTests : IDisposable
{
    private readonly LinuxSandbox _sandbox = new("agent-walk");

    public void Dispose() => _sandbox.Dispose();

    private static AgentEntry Entry(string id) => AgentCatalogue.Agents.Single(a => a.Id == id);

    private string Home(string relative) => _sandbox.Paths.DistroPath("/home/me/" + relative);

    private string Plant(string relative, int bytes, DateTimeOffset? written = null)
    {
        var path = _sandbox.Write("/home/me/" + relative, new string('x', bytes));
        File.SetLastWriteTimeUtc(path, (written ?? new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero)).UtcDateTime);
        return path;
    }

    private AgentsSample Walk(params AgentTarget[] targets) =>
        new AgentWalk(_sandbox.Files, new FixedTimeProvider(), _sandbox.Paths.Home).Measure(targets, AgentWalk.CollectBudget, withNames: true, CancellationToken.None);

    private AgentTarget Claude() => new(Entry("claude-code"), [Home(".claude")], Home(".claude"));

    [Fact]
    public void Memory_is_never_entered_for_any_agent_and_the_size_says_it_excludes_it()
    {
        Plant(".claude/projects/p/s1.jsonl", 100);
        Plant(".claude/projects/p/memory/notes.md", 10_000);
        var manual = new AgentEntry("manual", "Manual", ["x"], [], ["~/.manual"], [], [], [], null, string.Empty, false);
        Plant(".manual/memory/huge.bin", 50_000);
        Plant(".manual/data.bin", 7);

        var sample = Walk(Claude(), new AgentTarget(manual, [Home(".manual")], string.Empty));

        var claude = sample.Find("claude-code")!;
        claude.TotalBytes.Should().Be(100, "nothing under memory/ is counted, or even entered");
        claude.Folders.Single().Excluded.Should().Contain("memory (never entered)");
        sample.Find("manual")!.TotalBytes.Should().Be(7, "memory/ is never entered for ANY agent, whatever its entry says (plan §15q H2)");
    }

    [Fact]
    public void An_entrys_prefix_is_never_entered_so_one_agent_is_not_counted_inside_another()
    {
        Plant(".gemini/tmp/proj/chats/session-1.jsonl", 40);
        Plant(".gemini/antigravity-cli/conversations/c1.db", 9_000);
        var gemini = new AgentTarget(Entry("gemini-cli"), [Home(".gemini")], Home(".gemini"));

        var size = Walk(gemini).Find("gemini-cli")!;

        size.TotalBytes.Should().Be(40);
        size.Folders.Single().Excluded.Should().Contain("antigravity* (never entered)");
        size.Sessions.Count.Should().Be(1);
    }

    [Fact]
    public void A_link_inside_an_agent_folder_is_neither_counted_nor_entered()
    {
        Plant(".claude/projects/p/s1.jsonl", 10);
        var elsewhere = _sandbox.Write("/data/big.bin", new string('x', 20_000));
        if (!DirectoryLinks.TryCreate(Home(".claude/linked"), Path.GetDirectoryName(elsewhere)!))
        {
            Assert.Skip("this account can create neither a symbolic link nor a junction");
        }

        Walk(Claude()).Find("claude-code")!.TotalBytes.Should().Be(10);
    }

    /// <summary>Review C1: a folder on another filesystem than the agent folder's (a nested bind mount onto /mnt/c) is not
    /// entered, and named — the device seam lets the rule be a unit test on any operating system.</summary>
    [Fact]
    public void A_folder_on_another_filesystem_is_not_entered_and_is_named()
    {
        Plant(".claude/projects/p/s1.jsonl", 10);
        Plant(".claude/model-cache/blob.bin", 30_000);
        var mounted = Home(".claude/model-cache");
        (uint, uint)? DeviceOf(string path) => Path.GetFullPath(path) == Path.GetFullPath(mounted) ? (0u, 159u) : (8u, 48u);

        var measure = TreeWalk.Measure(Home(".claude"), new TreeLimits(1000, TimeSpan.FromMinutes(1)), AgentWalk.RulesFor(Entry("claude-code")), DeviceOf, CancellationToken.None);

        var measured = measure.Should().BeOfType<TreeMeasure.Measured>().Subject;
        measured.Bytes.Should().Be(10);
        measured.Excluded.Should().Contain("model-cache (different filesystem)");
    }

    /// <summary>Review M7: ONE budget for the whole walk; what it does not reach says so, never 0.</summary>
    [Fact]
    public void The_walk_stops_at_its_total_budget_and_names_what_it_did_not_reach()
    {
        Plant(".claude/projects/p/s1.jsonl", 10);
        Plant(".codex/sessions/2026/09/01/rollout-1.jsonl", 20);
        var stepping = new SteppingTimeProvider(TimeSpan.FromMinutes(2));
        var codex = new AgentTarget(Entry("codex"), [Home(".codex")], Home(".codex"));

        var sample = new AgentWalk(_sandbox.Files, stepping, _sandbox.Paths.Home).Measure([Claude(), codex], TimeSpan.FromMinutes(3), withNames: false, CancellationToken.None);

        sample.Find("claude-code")!.Folders.Single().Reason.Should().BeEmpty("the first folder is walked inside the budget");
        var late = sample.Find("codex")!;
        late.Folders.Single().Reason.Should().Be(AgentWalk.NotReached);
        late.Folders.Single().Complete.Should().BeFalse();
        late.Sessions.Counted.Should().BeFalse();
    }

    [Fact]
    public void Sessions_of_a_confirmed_layout_are_counted_by_their_names_with_dates_and_the_largest_five()
    {
        Plant(".claude/projects/a/one.jsonl", 100, new DateTimeOffset(2026, 8, 1, 0, 0, 0, TimeSpan.Zero));
        Plant(".claude/projects/b/two.jsonl", 300, new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero));
        Plant(".claude/projects/b/notes.txt", 5);
        Plant(".claude/projects/b/memory/three.jsonl", 900);
        Plant(".claude/projects/memory/four.jsonl", 900); // a folder named memory where the layout's * would match it: never entered

        var size = Walk(Claude()).Find("claude-code")!;

        size.Sessions.Should().Be(new SessionFigures(true, 2, new DateTimeOffset(2026, 8, 1, 0, 0, 0, TimeSpan.Zero), new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero), 300, true, string.Empty));
        size.Largest.Should().Equal(new SessionName("projects/b/two.jsonl", 300), new SessionName("projects/a/one.jsonl", 100));
    }

    [Fact]
    public void An_unconfirmed_layout_is_not_counted_and_says_so_and_a_missing_folder_is_not_zero()
    {
        Plant(".copilot/session-state/x", 5);
        var copilot = new AgentTarget(Entry("copilot-cli"), [Home(".copilot"), Home(".copilot-missing")], string.Empty);

        var size = Walk(copilot).Find("copilot-cli")!;

        size.Sessions.Counted.Should().BeFalse();
        size.Sessions.Reason.Should().Contain("monitor only");
        size.Folders[1].Exists.Should().BeFalse();
        size.Folders[1].Reason.Should().Contain("does not exist");
    }

    [Fact]
    public void A_persisted_sample_carries_no_session_name()
    {
        Plant(".claude/projects/a/one.jsonl", 100);

        var persisted = new AgentWalk(_sandbox.Files, new FixedTimeProvider(), _sandbox.Paths.Home).Measure([Claude()], AgentWalk.CollectBudget, withNames: false, CancellationToken.None);
        persisted.Agents.Should().OnlyContain(a => a.Largest == null, "the five largest sessions by name are a live answer only (plan §15q D1)");
        persisted.Find("claude-code")!.Sessions.LargestBytes.Should().Be(100, "the size of the largest is a number, kept");
    }

    /// <summary>Every timestamp is <paramref name="step"/> after the last — a walk that asks the time twice has spent two steps.</summary>
    private sealed class SteppingTimeProvider(TimeSpan step) : TimeProvider
    {
        private long _ticks;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override long GetTimestamp() => Interlocked.Add(ref _ticks, step.Ticks);

        public override DateTimeOffset GetUtcNow() => FixedTimeProvider.DefaultNow;
    }
}
