using System.Text;

using FluentAssertions;

using WslCare.Core.Actions.Engine;
using WslCare.Core.Archive;
using WslCare.Core.Config;
using WslCare.Core.Files;
using WslCare.TestSupport;

namespace WslCare.Core.Tests.Archive;

/// <summary>
/// Plan §15r E9.S2b — one <c>archive run</c> end to end over the sandboxed distro layout: no base, a refused base, a base mounted
/// differently than at its first run, a busy lock, an unreachable base and a full one each stop before anything is copied; a due
/// session is copied by one run and removed by a run a day later, the lease and the lock released, the last run recorded.
/// </summary>
public sealed partial class ArchiveRunTests : IDisposable
{
    private const string Base = "/mnt/v/ai-archive";
    private static readonly DateTimeOffset Now = new(2026, 10, 6, 12, 0, 0, TimeSpan.Zero);

    private readonly LinuxSandbox _sandbox = new("archive-run");
    private readonly ManualTimeProvider _clock = new(Now);

    public ArchiveRunTests() => Directory.CreateDirectory(On(Base));

    public void Dispose() => _sandbox.Dispose();

    private string On(string distro) => Path.GetFullPath(_sandbox.Paths.DistroPath(distro));

    private static (ConfigLayerFile, FileReadResult) Defaults() => (ConfigLoader.DefaultsFile, new FileReadResult.Content(ConfigLoader.EmbeddedDefaults()));

    private EffectiveConfig Config(string extra = "") =>
        ConfigLoader.Load([
            Defaults(),
            (new ConfigLayerFile(ConfigLayer.Machine, "/etc/wsl-care/config.json"), new FileReadResult.Missing()),
            (new ConfigLayerFile(ConfigLayer.User, "user.json"), new FileReadResult.Content(Encoding.UTF8.GetBytes($$"""{ "archive": { "baseFolder": "{{On(Base).Replace("\\", "\\\\", StringComparison.Ordinal)}}"{{extra}} } }"""))),
        ]).Config;

    private static BaseFolderReport Accepted(string folder, string mountType = "9p") =>
        new(1, "wsl", folder, true, folder, string.Empty, string.Empty, new BaseMountReport("/mnt/v", mountType, "drvfs"), [], []);

    private ArchiveRunInput Input(EffectiveConfig config, BaseFolderReport? judged = null, string runId = "r1") =>
        new(_sandbox.Paths, _sandbox.Files, _sandbox.Files, config, _clock, new GoneProcesses(), TimeZoneInfo.Utc, judged ?? Accepted(On(Base)), runId, TimeSpan.FromMinutes(30), static _ => null, TestContext.Current.CancellationToken)
        {
            OnlyAgent = "claude-code",
            InUseScan = static _ => InUseView.Complete(new HashSet<string>(StringComparer.Ordinal), new HashSet<string>(StringComparer.OrdinalIgnoreCase)),
            Me = new LeaseRecord(1, "host", "boot-1", 4242, 1000, Now, string.Empty, Now),
        };

    private string Session(string id, string content = "the transcript")
    {
        var path = _sandbox.Sized($"/home/me/.claude/projects/p/{id}.jsonl", 0, Now.AddDays(-40));
        File.WriteAllText(path, content);
        File.SetLastWriteTimeUtc(path, Now.AddDays(-40).UtcDateTime);
        return path;
    }

    private IEnumerable<string> BaseFiles => Directory.EnumerateFiles(On(Base), "*", SearchOption.AllDirectories);

    [Fact]
    public void Without_a_base_folder_the_run_answers_no_base_and_touches_nothing()
    {
        Session("s1");
        var config = ConfigLoader.Load([Defaults()]).Config;

        var report = ArchiveRun.Run(Input(config));

        report.Outcome.Should().Be(RunOutcomes.NoBase);
        BaseFiles.Should().BeEmpty();
    }

    [Fact]
    public void A_base_its_rules_refuse_stops_the_run_before_any_copy()
    {
        Session("s1");
        var refused = Accepted(On(Base)) with { Accepted = false, Rule = "not-writable", Refusal = "this user may not write it" };

        ArchiveRun.Run(Input(Config(), refused)).Outcome.Should().Be(RunOutcomes.Refused);

        BaseFiles.Should().BeEmpty();
    }

    /// <summary>D7: the base recorded as a 9p share at its first run; mounted as anything else later, the run refuses.</summary>
    [Fact]
    public void A_base_whose_recorded_mount_is_gone_is_never_written()
    {
        ArchiveRun.Run(Input(Config())).Outcome.Should().Be(RunOutcomes.Done);
        Session("s1");

        var report = ArchiveRun.Run(Input(Config(), Accepted(On(Base), mountType: "ext4")));

        report.Outcome.Should().Be(RunOutcomes.Refused);
        report.Stop.Should().Contain("not mounted as when it was first used");
        BaseFiles.Should().NotContain(f => f.EndsWith("s1.jsonl", StringComparison.Ordinal));
    }

    [Fact]
    public void A_second_run_while_the_side_is_locked_answers_busy()
    {
        var state = new ArchiveState(_sandbox.Paths, _sandbox.Files);
        state.EnsureFolder();
        using var held = (_sandbox.Files.TryLockExclusive(state.LockFile) as ExclusiveLock.Held)!.Handle;

        ArchiveRun.Run(Input(Config())).Outcome.Should().Be(RunOutcomes.Busy);
    }

    [Fact]
    public void An_unreachable_base_stops_the_run_and_defers_the_reconcile()
    {
        Session("s1");

        var report = ArchiveRun.Run(Input(Config()) with { Reachable = static (_, _) => false });

        report.Outcome.Should().Be(RunOutcomes.Unreachable);
        BaseFiles.Should().BeEmpty();
    }

    [Fact]
    public void A_full_or_read_only_base_stops_the_run_before_any_copy()
    {
        Session("s1");

        var report = ArchiveRun.Run(Input(Config(""", "minFreeGb": 100000""")));

        report.Outcome.Should().Be(RunOutcomes.Stopped);
        report.Stop.Should().Contain("minFreeGb");
        BaseFiles.Where(f => f.EndsWith(".jsonl", StringComparison.Ordinal) && !f.EndsWith("index.jsonl", StringComparison.Ordinal)).Should().BeEmpty();
    }

    [Fact]
    public void A_due_session_is_copied_by_one_run_and_removed_by_a_run_a_day_later()
    {
        var source = Session("s1");

        var first = ArchiveRun.Run(Input(Config()));

        first.Outcome.Should().Be(RunOutcomes.Done, first.Stop);
        first.Agents.Single().Copied.Should().Be(1);
        File.Exists(source).Should().BeTrue();
        BaseFiles.Should().Contain(f => f.EndsWith("s1.jsonl", StringComparison.Ordinal));
        new ArchiveState(_sandbox.Paths, _sandbox.Files).Summary().Months.Should().ContainSingle().Which.Should().Match<SummaryRow>(r => r.Agent == "claude-code" && r.Sessions == 1 && r.Files == 1, "summary.json counts what was archived");
        File.Exists(On($"{Base}/.wsl-care/sides/{SideName.OfThisProcess(Core.Hosting.HostSide.Wsl)}.lease")).Should().BeFalse("the lease goes with the run");

        _clock.Advance(TimeSpan.FromHours(25));
        var second = ArchiveRun.Run(Input(Config(), runId: "r2"));

        second.Outcome.Should().Be(RunOutcomes.Done, second.Stop);
        second.Agents.Single().Removed.Should().Be(1);
        File.Exists(source).Should().BeFalse();
        new ArchiveState(_sandbox.Paths, _sandbox.Files).LastRun()!.RunId.Should().Be("r2");
    }

    /// <summary>The budget stops the run BETWEEN sessions only: a run whose first session took longer than its budget copies no second
    /// one, and never leaves a session half copied.</summary>
    [Fact]
    public void A_run_cut_by_its_budget_leaves_only_whole_sessions()
    {
        Session("s1");
        Session("s2", "the second transcript");
        var input = Input(Config()) with { Budget = TimeSpan.FromMinutes(15), Progress = _ => _clock.Advance(TimeSpan.FromMinutes(20)) };

        var report = ArchiveRun.Run(input);

        report.Outcome.Should().Be(RunOutcomes.Stopped);
        report.Stop.Should().Contain("budget");
        report.Agents.Single().Copied.Should().Be(1);
        BaseFiles.Count(f => f.EndsWith(".jsonl", StringComparison.Ordinal) && !f.EndsWith("index.jsonl", StringComparison.Ordinal)).Should().Be(1, "the second session was never started");
    }

    private sealed class GoneProcesses : IProcessTable
    {
        public ProcessLookup Lookup(int pid) => new ProcessLookup.Gone();
    }
}
