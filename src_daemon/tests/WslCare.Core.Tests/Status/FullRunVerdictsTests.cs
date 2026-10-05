using System.Text;

using FluentAssertions;

using WslCare.Core.Collectors;
using WslCare.Core.Records;
using WslCare.Core.Status;
using WslCare.Core.Thresholds;
using WslCare.TestSupport;

namespace WslCare.Core.Tests.Status;

/// <summary>
/// The verdicts of the newest FULL run read back from its detail (plan §15g B1): a full run is a <c>collect</c> line — it
/// carries the slow parts — that names its detail; an <c>act</c> line after it is not one. Every way of not having them is
/// a reason, never an empty list. The detail files here are written in the shape <c>collect</c> writes (camel case, the
/// head and <c>thresholds</c>); the in-process CLI test reads a detail the real <c>collect</c> wrote.
/// </summary>
public sealed class FullRunVerdictsTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 10, 3, 12, 0, 0, TimeSpan.Zero);
    private readonly SandboxHost _sandbox = new("full-run-verdicts");

    public void Dispose() => _sandbox.Dispose();

    private Reading<RecordedVerdicts> Read() => FullRunVerdicts.Read(_sandbox.Paths, _sandbox.Files, RunHistory.Read(_sandbox.Paths, _sandbox.Files));

    private RunId FullRun(DateTimeOffset startedAt, int pid, string level = "warn", bool writeDetail = true, bool nameDetail = true)
    {
        var id = RunId.New(startedAt, pid);
        var relative = RunDetailStore.RelativePath(id);
        if (writeDetail)
        {
            var json = $$"""
                {"schemaVersion":1,"runId":"{{id.Text}}","trigger":"timer","startedAt":"{{startedAt:O}}","endedAt":"{{startedAt.AddSeconds(40):O}}",
                 "sample":{"ignored":true},"thresholds":[{"id":"clock.jumps","level":"{{level}}","value":"875 in 4.0 h (875 per 4 h)","limit":"warn > 100 per 4 h","reason":"since the last run"}]}
                """;
            RunDetailStore.Write(_sandbox.Paths, _sandbox.Files, id, Encoding.UTF8.GetBytes(json));
        }

        new RunRecordWriter(_sandbox.Paths, _sandbox.Files).Append(
            new RunRecord(SchemaVersion.Current, id, RunTrigger.Timer, startedAt, startedAt.AddSeconds(40), RunOutcome.Completed, [], RunKind.Collect)
            {
                Slow = new SlowParts(),
                Detail = nameDetail ? relative : null,
            });
        return id;
    }

    private void ActRun(DateTimeOffset startedAt)
    {
        var id = RunId.New(startedAt, 99);
        new RunRecordWriter(_sandbox.Paths, _sandbox.Files).Append(
            new RunRecord(SchemaVersion.Current, id, RunTrigger.Manual, startedAt, startedAt.AddSeconds(5), RunOutcome.Completed, [new ActionRecord("A10", 0, 0)], RunKind.Act) { Detail = RunDetailStore.RelativePath(id) });
    }

    [Fact]
    public void The_newest_full_run_answers_with_its_verdicts_its_id_and_its_end_and_a_later_act_line_is_not_a_full_run()
    {
        FullRun(Now.AddHours(-8), 1, level: "ok");
        var newest = FullRun(Now.AddHours(-4), 2, level: "warn");
        ActRun(Now.AddHours(-1));

        var read = Read();

        var recorded = read.Should().BeOfType<Reading<RecordedVerdicts>.Available>().Subject.Value;
        recorded.RunId.Should().Be(newest);
        recorded.EvaluatedAt.Should().Be(Now.AddHours(-4).AddSeconds(40));
        recorded.Verdicts.Should().ContainSingle().Which.Should().Be(new Verdict("clock.jumps", Level.Warn, "875 in 4.0 h (875 per 4 h)", "warn > 100 per 4 h", "since the last run"));
    }

    [Fact]
    public void Before_any_full_run_the_reason_says_how_to_record_one()
    {
        ActRun(Now.AddHours(-1));

        Read().ReasonOrEmpty.Should().Be(LastFullRun.NoFullRunYet);
    }

    [Fact]
    public void A_full_run_whose_line_names_no_detail_is_passed_over_for_the_newest_that_does()
    {
        var older = FullRun(Now.AddHours(-8), 1);
        FullRun(Now.AddHours(-4), 2, writeDetail: false, nameDetail: false);

        Read().Should().BeOfType<Reading<RecordedVerdicts>.Available>().Which.Value.RunId.Should().Be(older);
    }

    [Fact]
    public void A_named_detail_that_is_gone_is_a_reason_naming_the_file()
    {
        var id = FullRun(Now.AddHours(-4), 2, writeDetail: false);

        Read().ReasonOrEmpty.Should().Contain(RunDetailStore.RelativePath(id)).And.Contain("could not be read");
    }

    [Fact]
    public void A_detail_that_does_not_parse_is_a_reason_naming_the_file()
    {
        var id = FullRun(Now.AddHours(-4), 2);
        File.WriteAllText(RunDetailStore.Absolute(_sandbox.Paths, RunDetailStore.RelativePath(id)), "{ torn");

        Read().ReasonOrEmpty.Should().Contain(RunDetailStore.RelativePath(id)).And.Contain("does not parse");
    }

    [Fact]
    public void An_unreadable_history_is_its_own_reason()
    {
        var read = FullRunVerdicts.Read(_sandbox.Paths, _sandbox.Files, new HistoryRead([], 0, "history.jsonl could not be read: denied"));

        read.ReasonOrEmpty.Should().Be("history.jsonl could not be read: denied");
    }
}
