using System.Text.Json;

using FluentAssertions;

using WslCare.Core.Collectors;
using WslCare.Core.Json;
using WslCare.Core.Records;
using WslCare.TestSupport;

namespace WslCare.Core.Tests.Records;

/// <summary>
/// The slow parts — <c>docker stats</c>, the Windows clock — come back from the last full run WITH
/// their age (plan §6, §15b #5), or as unavailable with the reason; never as zeros.
/// </summary>
public sealed class LastFullRunTests
{
    private static readonly DateTimeOffset Now = FixedTimeProvider.DefaultNow;

    private static RunRecord Run(DateTimeOffset at, SlowParts? slow, int pid = 1) =>
        new RunRecord(SchemaVersion.Current, RunId.New(at, pid), RunTrigger.Timer, at, at.AddSeconds(30), RunOutcome.Completed, []) { Slow = slow };

    private static ContainerStatsSample Stats(DateTimeOffset at, string unavailable = "") =>
        new(at, [new ContainerStat("0e456d1dc8c0", "pg", 712_196_096, 1.5)], unavailable);

    private static LastSlowParts Read(SandboxHost host) => LastFullRun.Read(host.Paths, host.Files, new FixedTimeProvider(Now));

    [Fact]
    public void Before_any_full_run_both_parts_are_unavailable_and_say_how_to_record_one()
    {
        using var host = new SandboxHost("slow-none");

        var parts = Read(host);

        parts.ContainerStats.Should().BeOfType<Reading<AgedPart<ContainerStatsSample>>.Unavailable>().Which.Reason.Should().Be(LastFullRun.NoFullRunYet);
        parts.WindowsClock.IsAvailable.Should().BeFalse();
    }

    [Fact]
    public void A_full_runs_docker_stats_come_back_with_the_run_id_and_their_age()
    {
        using var host = new SandboxHost("slow-aged");
        var writer = new RunRecordWriter(host.Paths, host.Files);
        var sampled = Now.AddMinutes(-90);
        writer.Append(Run(sampled, new SlowParts { ContainerStats = Stats(sampled) }));

        var part = Read(host).ContainerStats.Should().BeOfType<Reading<AgedPart<ContainerStatsSample>>.Available>().Subject.Value;

        part.Age.Should().Be(TimeSpan.FromMinutes(90));
        part.RunId.Should().Be(RunId.New(sampled, 1));
        part.Value.Containers.Should().ContainSingle().Which.MemoryBytes.Should().Be(712_196_096);
    }

    [Fact]
    public void A_newer_run_that_sampled_nothing_slow_does_not_hide_the_older_sample_and_a_torn_line_is_skipped()
    {
        using var host = new SandboxHost("slow-newest");
        var writer = new RunRecordWriter(host.Paths, host.Files);
        writer.Append(Run(Now.AddHours(-8), new SlowParts { ContainerStats = Stats(Now.AddHours(-8)) }, pid: 1));
        writer.Append(Run(Now.AddHours(-4), new SlowParts { ContainerStats = Stats(Now.AddHours(-4)) }, pid: 2));
        writer.Append(Run(Now.AddHours(-1), slow: null, pid: 3));
        File.AppendAllText(writer.HistoryFile, "{\"schemaVersion\":1,\"runId\":\"2026");

        var part = Read(host).ContainerStats.Should().BeOfType<Reading<AgedPart<ContainerStatsSample>>.Available>().Subject.Value;

        part.Age.Should().Be(TimeSpan.FromHours(4), "the newest run that carries the part");
    }

    [Fact]
    public void When_the_newest_run_could_not_sample_a_part_that_is_the_answer_with_its_reason()
    {
        using var host = new SandboxHost("slow-failed");
        var writer = new RunRecordWriter(host.Paths, host.Files);
        writer.Append(Run(Now.AddHours(-8), new SlowParts { ContainerStats = Stats(Now.AddHours(-8)) }, pid: 1));
        writer.Append(Run(Now.AddHours(-1), new SlowParts { ContainerStats = Stats(Now.AddHours(-1), "the Docker socket refused the connection") }, pid: 2));

        Read(host).ContainerStats.Should().BeOfType<Reading<AgedPart<ContainerStatsSample>>.Unavailable>()
            .Which.Reason.Should().Contain("socket refused").And.Contain(RunId.New(Now.AddHours(-1), 2).Text);
    }

    [Fact]
    public void A_history_written_before_slow_parts_existed_reads_as_no_sample_yet()
    {
        using var host = new SandboxHost("slow-old");
        new RunRecordWriter(host.Paths, host.Files).Append(Run(Now.AddHours(-1), slow: null));

        var line = File.ReadAllText(new RunRecordWriter(host.Paths, host.Files).HistoryFile);
        line.Should().NotContain("slow", "a run with no slow parts writes no key, so E1's lines and E2's agree");
        Read(host).WindowsClock.Should().BeOfType<Reading<AgedPart<WindowsClockSample>>.Unavailable>()
            .Which.Reason.Should().Contain("no recorded full run has sampled the Windows clock");
    }

    [Fact]
    public void Slow_parts_round_trip_through_the_compact_context()
    {
        var record = Run(Now, new SlowParts { WindowsClock = new WindowsClockSample(Now, 1.25, 0.4, string.Empty) });

        var back = JsonSerializer.Deserialize(JsonSerializer.Serialize(record, WslCareJsonContext.Compact.RunRecord), WslCareJsonContext.Compact.RunRecord);

        back!.Slow!.WindowsClock.Should().Be(record.Slow!.WindowsClock);
        back.Slow.ContainerStats.Should().BeNull();
    }
}
