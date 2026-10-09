using System.Text;
using System.Text.Json;

using FluentAssertions;

using WslCare.Core.Actions;
using WslCare.Core.Archive;
using WslCare.Core.Collectors;
using WslCare.Core.Config;
using WslCare.Core.Files;
using WslCare.Core.Json;
using WslCare.Core.Processes;
using WslCare.Core.Records;
using WslCare.TestSupport;

namespace WslCare.Core.Tests.Archive;

/// <summary>
/// Plan §15r D6, E9.S4 (A20, the restore button; A19 is the idle MCP servers' since E14 S2a): a button only, bound to the entry
/// ids its modal showed — judged again against a fresh list — run as the target user through the product's own binary, its
/// answer judged, its count bounded so its one argument stays far below the kernel's limit (the S4 plan round's finding 0).
/// </summary>
public sealed class RestoreActionTests : IDisposable
{
    private const string Boot = "6d1c1c5e-0000-4000-8000-0000000000a2";
    private const string Base = "/mnt/v/ai-archive";
    private const string Removed1 = "0000000000000001";
    private const string Removed2 = "0000000000000002";
    private const string StillThere = "0000000000000003";
    private const string Unverified = "0000000000000004";
    private const string NeverListed = "00000000000000ff";
    private static readonly DateTimeOffset Now = new(2026, 10, 6, 12, 0, 0, TimeSpan.Zero);

    private readonly LinuxSandbox _sandbox = new("a20");
    private readonly ManualTimeProvider _clock = new(Now);

    public RestoreActionTests()
    {
        _sandbox.Write("/proc/sys/kernel/random/boot_id", Boot + "\n");
        _sandbox.Write("/etc/pam.d/runuser", ArchiveActionTests.SafeRunuserStack);
    }

    public void Dispose() => _sandbox.Dispose();

    private static EffectiveConfig Config(string machine = "") =>
        ConfigLoader.Load([
            (ConfigLoader.DefaultsFile, new FileReadResult.Content(ConfigLoader.EmbeddedDefaults())),
            (new ConfigLayerFile(ConfigLayer.Machine, "/etc/wsl-care/config.json"), machine.Length == 0 ? new FileReadResult.Missing() : new FileReadResult.Content(Encoding.UTF8.GetBytes(machine))),
            (new ConfigLayerFile(ConfigLayer.User, "user.json"), new FileReadResult.Content(Encoding.UTF8.GetBytes($$"""{ "archive": { "baseFolder": "{{Base}}" } }"""))),
        ]).Config;

    private ActionContext Context(ShownList? shown = null, EffectiveConfig? config = null) =>
        new(_sandbox.Paths, _sandbox.Files, _clock, config ?? Config(), RunTrigger.Manual, new TargetUserResult.Found(new TargetUser("me", 1000, "/home/me"), "test"))
        {
            Processes = _ => Reading.Of(new ProcessSnapshot(0, 0, 0, 0, 0, [], [])),
            ShownEntries = shown ?? ShownList.None,
            RunId = "20261006T120000Z-42",
            RunStarted = Now,
        };

    private static ArchiveListEntry Entry(string id, string status, bool verified = true) =>
        new(id, "claude-code", $"projects/p/{id}.jsonl", "2026/09", status, verified, 2, 200, Now.AddDays(-40));

    private static string ListJson(params ArchiveListEntry[] entries) =>
        JsonSerializer.Serialize(new ArchiveListReport(SchemaVersion.Current, "wsl", "wsl-host-distro", Base, RunOutcomes.Done, string.Empty, entries, 0, []), WslCareJsonContext.Compact.ArchiveListReport);

    private static readonly string FourEntries = ListJson(Entry(Removed1, ArchiveIndex.Events.SourceRemoved), Entry(Removed2, ArchiveIndex.Events.SourceRemoved), Entry(StillThere, ArchiveIndex.Events.Archived), Entry(Unverified, ArchiveIndex.Events.SourceRemoved, verified: false));

    private static string RestoreJson(params RestoredSession[] sessions) =>
        JsonSerializer.Serialize(
            ArchiveActionTests.RunReport(copied: 0, removedBytes: 0) with { Agents = [], Restore = sessions.Aggregate(RestoreReport.Empty, (report, s) => report.With(s)) },
            WslCareJsonContext.Compact.ArchiveRunReport);

    private static RecordingCommandRunner Children(string? list = null, string? restore = null, CommandOutcome? restoreOutcome = null)
    {
        var runner = new RecordingCommandRunner().Script(argv => argv.Count > 6 && argv[6] == "list", RecordingCommandRunner.Exited(0, list ?? FourEntries));
        runner.Stream(new StreamScript(["{\"progress\":\"file\",\"files\":1,\"bytes\":200,\"seconds\":1}", restore ?? RestoreJson(new RestoredSession(Removed1, "claude-code", "projects/p/x.jsonl", "2026/09", RestoreOutcomes.Restored, 2, 200, "2 file(s) created"))], restoreOutcome ?? RecordingCommandRunner.Exited(0)));
        return runner;
    }

    private static async Task<(ActionPreview Preview, ActionRun Run, RecordingCommandRunner Runner)> PreviewAndRun(ActionContext context, RecordingCommandRunner? runner = null)
    {
        var action = new RestoreAction();
        var children = runner ?? Children();
        var commands = ArchiveActionTests.Commands(action, children, context);
        var preview = await action.PreviewAsync(context, commands, CancellationToken.None);
        var run = await action.RunAsync(context, preview, commands, CancellationToken.None);
        return (preview, run, children);
    }

    [Fact]
    public void A20_is_a_button_only_and_never_the_timers()
    {
        var action = new RestoreAction();

        action.Id.Text.Should().Be("A20");
        action.Id.ButtonOnly.Should().BeTrue();
        ActionId.Find("A19")!.ButtonOnly.Should().BeFalse("A19 is main's idle-MCP-server stop, a timer action too (E14 S2a)");
    }

    /// <summary>The preview offers what is restorable — removed at the source, verified — and the button passes back exactly those ids.</summary>
    [Fact]
    public async Task The_preview_offers_only_the_verified_entries_removed_at_their_source()
    {
        var action = new RestoreAction();
        var context = Context();

        var preview = await action.PreviewAsync(context, ArchiveActionTests.Commands(action, Children(), context), CancellationToken.None);

        preview.Available.Should().BeTrue(preview.Reason);
        action.Shown(preview).Should().Equal(Removed1, Removed2);
        preview.Count.Should().Be(2);
        JsonSerializer.Serialize(preview, WslCareJsonContext.Default.ActionPreview).Should().NotContain("projects/p/", "root's records name no session");
    }

    /// <summary>The S4 code round, finding 4: A20 asks the child for the restorable entries only (bounded by archive.maxRestoreEntries),
    /// so its answer stays inside the cap however large the archive grows — and says how many it left out.</summary>
    [Fact]
    public async Task A20_asks_only_for_the_restorable_entries_and_says_how_many_were_left_out()
    {
        var list = JsonSerializer.Serialize(
            new ArchiveListReport(SchemaVersion.Current, "wsl", "wsl-host-distro", Base, RunOutcomes.Done, string.Empty, [Entry(Removed1, ArchiveIndex.Events.SourceRemoved)], 0, []) { Omitted = 7 },
            WslCareJsonContext.Compact.ArchiveListReport);
        var runner = Children(list: list);

        var (preview, _, _) = await PreviewAndRun(Context(ShownList.Of([Removed1])), runner);

        runner.Requests.First().Argv.Skip(5).Should().Equal("archive", "list", "--restorable", "--entry", Removed1, "--json");
        preview.What.Should().Contain("7 more");
    }

    /// <summary>The E10.S0 own review, finding 1: the preview asks the child for EXACTLY the shown entries (<c>--entry</c>, sorted),
    /// so an entry older than the newest <c>archive.maxRestoreEntries</c> restorable ones is never lost to the list's window.</summary>
    [Fact]
    public async Task A20_asks_the_child_for_exactly_the_shown_entries_so_none_is_lost_to_the_newest_window()
    {
        var (_, _, runner) = await PreviewAndRun(Context(ShownList.Of([Removed2, Removed1])));

        runner.Requests.First().Argv.Skip(5).Should().Equal("archive", "list", "--restorable", "--entry", $"{Removed1},{Removed2}", "--json");
    }

    /// <summary>The E10.S0 own review, findings 1 and 2: more shown entries than the ceiling IN FORCE are refused in the preview,
    /// before the child is asked — never narrowed to a silent subset.</summary>
    [Fact]
    public async Task More_shown_entries_than_the_ceiling_in_force_are_refused_before_the_child_is_asked()
    {
        var ids = Enumerable.Range(1, 3).Select(i => i.ToString("x16", System.Globalization.CultureInfo.InvariantCulture)).ToArray();
        var context = Context(ShownList.Of(ids), Config("""{ "archive": { "maxRestoreEntries": 2 } }"""));

        var (preview, run, runner) = await PreviewAndRun(context);

        preview.Refusal.Should().Contain("archive.maxRestoreEntries").And.Contain("3");
        run.Succeeded.Should().BeFalse();
        runner.Requests.Should().NotContain(r => r.Argv.Count > 6 && r.Argv[6] == "list", "nothing is asked of the child for a list it cannot take");
    }

    [Fact]
    public async Task A20_restores_only_the_entries_its_modal_showed()
    {
        var (_, run, runner) = await PreviewAndRun(Context(ShownList.Of([Removed1, NeverListed, StillThere])));

        run.Succeeded.Should().BeTrue(run.Failure);
        var stream = runner.Requests.Single(r => r.Argv.Count > 6 && r.Argv[6] == "restore");
        stream.Argv.Skip(5).Should().Equal("archive", "restore", "--entry", Removed1, "--json");
        run.Count.Should().Be(1);
        run.Removed.Should().ContainSingle().Which.Name.Should().Be(Removed1);
        JsonSerializer.Serialize(run, WslCareJsonContext.Default.ActionRun).Should().NotContain("projects/p/");
    }

    [Fact]
    public async Task A20_without_the_shown_entries_refuses_and_starts_no_restore()
    {
        var (preview, run, runner) = await PreviewAndRun(Context());

        preview.Refusal.Should().Contain("--entry");
        run.Succeeded.Should().BeFalse();
        runner.Requests.Should().NotContain(r => r.Argv.Count > 6 && r.Argv[6] == "restore");
    }

    [Fact]
    public async Task Shown_entries_of_which_none_is_still_restorable_restore_nothing()
    {
        var (_, run, runner) = await PreviewAndRun(Context(ShownList.Of([StillThere, Unverified])));

        run.Count.Should().Be(0);
        runner.Requests.Should().NotContain(r => r.Argv.Count > 6 && r.Argv[6] == "restore");
    }

    /// <summary>The S4 plan round's finding 0: the ids reach the child as ONE argument — more than archive.maxRestoreEntries are refused
    /// before anything starts, so that argument stays far below the kernel's per-argument limit.</summary>
    [Fact]
    public async Task More_shown_entries_than_one_restore_takes_are_refused_before_anything_starts()
    {
        var ids = Enumerable.Range(1, 3).Select(i => i.ToString("x16", System.Globalization.CultureInfo.InvariantCulture)).ToArray();
        var list = ListJson([.. ids.Select(id => Entry(id, ArchiveIndex.Events.SourceRemoved))]);
        var context = Context(ShownList.Of(ids), Config("""{ "archive": { "maxRestoreEntries": 2 } }"""));

        var (_, run, runner) = await PreviewAndRun(context, Children(list: list));

        run.Succeeded.Should().BeFalse();
        run.Failure.Should().Contain("archive.maxRestoreEntries");
        runner.Requests.Should().NotContain(r => r.Argv.Count > 6 && r.Argv[6] == "restore");
    }

    [Fact]
    public async Task A_refused_session_fails_A20_naming_how_many()
    {
        var answer = RestoreJson(new RestoredSession(Removed1, "claude-code", "projects/p/x.jsonl", "2026/09", RestoreOutcomes.Refused, 0, 0, "a live file of that name has other bytes"));

        var (_, run, _) = await PreviewAndRun(Context(ShownList.Of([Removed1])), Children(restore: answer, restoreOutcome: RecordingCommandRunner.Exited(1)));

        run.Succeeded.Should().BeFalse();
        run.Failure.Should().Contain("1 session(s) were refused");
        run.Failure.Should().NotContain("projects/p/");
    }

    [Fact]
    public async Task A_malformed_restore_answer_records_nothing_from_it()
    {
        var (_, run, _) = await PreviewAndRun(Context(ShownList.Of([Removed1])), Children(restore: "{\"schemaVersion\": 1, nonsense"));

        run.Succeeded.Should().BeFalse();
        run.Count.Should().Be(0);
        run.Removed.Should().BeEmpty();
    }

    // ---- the E9.S4 own review round --------------------------------------------------------------------------------------

    /// <summary>C-2 (and C-1 for busy): a restore that never ran — the child stopped before it, its restore part empty — FAILS, naming
    /// the child's outcome in root's words; it is never a success that restored nothing.</summary>
    [Theory]
    [InlineData(RunOutcomes.Unreachable, ArchiveExits.RunFailed, "did not answer within archive.reachabilitySeconds")]
    [InlineData(RunOutcomes.Refused, ArchiveExits.RunFailed, "refused")]
    [InlineData(RunOutcomes.Busy, ArchiveExits.Busy, "holds its lock")]
    public async Task A_restore_that_never_ran_fails_naming_why(string outcome, int exit, string reason)
    {
        var answer = System.Text.Json.JsonSerializer.Serialize(
            ArchiveActionTests.RunReport(copied: 0, removedBytes: 0, outcome: outcome) with { Agents = [], Stop = "a child's own words, never copied" },
            Json.WslCareJsonContext.Compact.ArchiveRunReport);

        var (_, run, _) = await PreviewAndRun(Context(ShownList.Of([Removed1])), Children(restore: answer, restoreOutcome: RecordingCommandRunner.Exited(exit)));

        run.Succeeded.Should().BeFalse(outcome);
        run.Failure.Should().Contain(reason).And.NotContain("never copied");
        run.Count.Should().Be(0);
    }

    /// <summary>S-m3: a restore's answer names each session by entry id, agent, month and outcome — each judged before root writes it:
    /// an outcome the archive answers, a month <c>yyyy/MM</c>, an agent the archive moves, a run id that is one.</summary>
    public static TheoryData<string, string> ForeignRestores => new()
    {
        { "an outcome the archive does not answer", Restore(new RestoredSession(Removed1, "claude-code", "projects/p/x.jsonl", "2026/09", "restored /etc/shadow", 2, 200, "n")) },
        { "a month of another shape", Restore(new RestoredSession(Removed1, "claude-code", "projects/p/x.jsonl", "../../etc", RestoreOutcomes.Restored, 2, 200, "n")) },
        { "an agent the archive does not move", Restore(new RestoredSession(Removed1, "../evil", "projects/p/x.jsonl", "2026/09", RestoreOutcomes.Restored, 2, 200, "n")) },
        { "a run id that is no run id", Restore(new RestoredSession(Removed1, "claude-code", "projects/p/x.jsonl", "2026/09", RestoreOutcomes.Restored, 2, 200, "n")).Replace("20261006T120000Z-42", "not a run", StringComparison.Ordinal) },
    };

    [Theory]
    [MemberData(nameof(ForeignRestores))]
    public async Task A_restore_answer_carrying_a_string_root_does_not_know_is_not_believed(string why, string answer)
    {
        var (_, run, _) = await PreviewAndRun(Context(ShownList.Of([Removed1])), Children(restore: answer));

        run.Succeeded.Should().BeFalse(why);
        run.Removed.Should().BeEmpty(why);
    }

    /// <summary>S-m3: a listed entry's month and status are judged too — the preview names them in root's records.</summary>
    [Theory]
    [InlineData("2026/09 /etc/shadow", ArchiveIndex.Events.SourceRemoved)]
    [InlineData("2026/13", ArchiveIndex.Events.SourceRemoved)]
    [InlineData("2026/09", "sourceRemoved\n/etc/shadow")]
    public async Task A_listed_entry_of_another_shape_is_not_believed(string month, string status)
    {
        var list = ListJson(Entry(Removed1, ArchiveIndex.Events.SourceRemoved) with { Month = month, Status = status });

        var (preview, _, _) = await PreviewAndRun(Context(ShownList.Of([Removed1])), Children(list: list));

        preview.Available.Should().BeFalse(month + " " + status);
    }

    private static string Restore(RestoredSession session) => RestoreJson(session);
}
