using FluentAssertions;

using WslCare.Core.Archive;

namespace WslCare.Core.Tests.Archive;

/// <summary>Plan §15r E9.S3 — <c>archive restore</c> and <c>archive list</c> end to end over the sandboxed layout: a session archived and
/// removed is listed, restored by its month, its session path or its entry id under the side's lock and lease, listed as restored;
/// the list reads only the months asked, skips a torn line, marks an unverified entry and names an index it cannot read.</summary>
public sealed partial class ArchiveRunTests
{
    private string Month1 => "2026-08";

    /// <summary>A session copied by one run and removed by a run a day later; its source path.</summary>
    private string ArchivedAndRemoved(string id)
    {
        var source = Session(id);
        ArchiveRun.Run(Input(Config())).Agents.Single().Copied.Should().Be(1);
        _clock.Advance(TimeSpan.FromHours(25));
        ArchiveRun.Run(Input(Config(), runId: "r2")).Agents.Single().Removed.Should().Be(1);
        File.Exists(source).Should().BeFalse();
        return source;
    }

    private ArchiveListReport List(string month = "", string runId = "") =>
        ArchiveList.List(Input(Config()), new ArchiveListRequest("claude-code", month, runId));

    [Theory]
    [InlineData("month")]
    [InlineData("session")]
    [InlineData("entry")]
    public void A_removed_session_is_restored_by_its_month_its_path_or_its_entry_id(string by)
    {
        var source = ArchivedAndRemoved("s1");
        var entryId = List().Entries.Single().EntryId;
        var asked = by switch
        {
            "month" => new RestoreRequest([], "claude-code", Month1, string.Empty, false),
            "session" => new RestoreRequest([], "claude-code", string.Empty, "projects/p/s1.jsonl", false),
            _ => new RestoreRequest([entryId], string.Empty, string.Empty, string.Empty, false),
        };

        var report = ArchiveRun.Run(Input(Config(), runId: "r3") with { Restore = asked });

        report.Outcome.Should().Be(RunOutcomes.Done, report.Stop);
        report.Restore.Restored.Should().Be(1, string.Join("; ", report.Restore.Sessions.Select(s => s.Note)));
        File.ReadAllText(source).Should().Be("the transcript");
        List().Entries.Single().Status.Should().Be(ArchiveIndex.Events.Restored);
        File.Exists(On($"{Base}/.wsl-care/sides/{SideName.OfThisProcess(Core.Hosting.HostSide.Wsl)}.lease")).Should().BeFalse("the lease goes with the restore");
    }

    /// <summary>D6/E9.S3: the restored session is young again (the restore time), so the next run neither copies nor removes it.</summary>
    [Fact]
    public void A_restored_session_is_left_alone_by_the_next_run()
    {
        var source = ArchivedAndRemoved("s1");
        ArchiveRun.Run(Input(Config(), runId: "r3") with { Restore = new RestoreRequest([], "claude-code", string.Empty, "projects/p/s1.jsonl", false) }).Restore.Restored.Should().Be(1);
        _clock.Advance(TimeSpan.FromHours(25));

        var next = ArchiveRun.Run(Input(Config(), runId: "r4"));

        next.Agents.Single().Should().Match<AgentRunReport>(a => a.Copied == 0 && a.Removed == 0);
        File.ReadAllText(source).Should().Be("the transcript");
    }

    [Fact]
    public void The_list_reads_only_the_months_asked_and_says_each_entrys_status()
    {
        ArchivedAndRemoved("s1");

        List(Month1).Entries.Should().ContainSingle().Which.Should().Match<ArchiveListEntry>(e => e.Status == ArchiveIndex.Events.SourceRemoved && e.Verified && e.Files == 1 && e.Key == "projects/p/s1.jsonl");
        List("2026-07").Entries.Should().BeEmpty();
        List(runId: "r2").Entries.Should().ContainSingle("the run that removed it touched it");
        List(runId: "20990101T000000Z-1").Entries.Should().BeEmpty();
    }

    [Fact]
    public void The_list_skips_a_torn_line_and_marks_an_unverified_entry()
    {
        ArchivedAndRemoved("s1");
        var index = Directory.EnumerateFiles(On(Base), ArchiveIndex.FileName, SearchOption.AllDirectories).Single();
        File.WriteAllText(index, File.ReadAllText(index).Replace("\"r1\"", "\"rX\"", StringComparison.Ordinal) + "{\"v\":1,\"ev");

        var listed = List();

        listed.SkippedLines.Should().Be(1);
        listed.Entries.Single().Verified.Should().BeFalse("its archived line was edited: this side did not sign it");
    }

    [Fact]
    public void Without_a_base_the_list_answers_no_base_and_reads_nothing()
    {
        var config = ConfigLoaderDefaults();

        ArchiveList.List(Input(config), new ArchiveListRequest(string.Empty, string.Empty, string.Empty)).Outcome.Should().Be(RunOutcomes.NoBase);
    }

    private static Core.Config.EffectiveConfig ConfigLoaderDefaults() => Core.Config.ConfigLoader.Load([Defaults()]).Config;
}
