using FluentAssertions;

using WslCare.Core.Events;
using WslCare.Core.Records;
using WslCare.TestSupport;

namespace WslCare.Core.Tests.Records;

/// <summary>
/// The line-appended files (<c>history.jsonl</c>, the container-starts day files) read while a writer may be mid-append (gate
/// finding #6): a trailing line with no terminating newline is a write still in progress — or the torn remains of one that
/// died — and is IGNORED by every reader, never counted as corruption; and the next append never glues itself onto it.
/// </summary>
public sealed class LineFileTests : IDisposable
{
    private static readonly DateTimeOffset At = new(2026, 10, 2, 10, 0, 0, TimeSpan.Zero);

    private readonly SandboxHost _sandbox = new("line-files");

    public void Dispose() => _sandbox.Dispose();

    private static RunRecord Record(int pid) => new(1, RunId.New(At, pid), RunTrigger.Timer, At, At.AddSeconds(5), RunOutcome.Completed, []);

    [Fact]
    public void A_half_written_last_history_line_is_ignored_not_counted_as_an_unparseable_line()
    {
        new RunRecordWriter(_sandbox.Paths, _sandbox.Files).Append(Record(1));
        File.AppendAllText(RunHistory.File(_sandbox.Paths), "{\"schemaVersion\":1,\"runId\":\"20261002T100000Z-2\",\"trig");

        var read = RunHistory.Read(_sandbox.Paths, _sandbox.Files);

        read.Records.Should().ContainSingle().Which.RunId.Text.Should().Be("20261002T100000Z-1");
        read.Unparseable.Should().Be(0, "a line still being written is not a corrupt line");
    }

    [Fact]
    public void A_complete_line_that_does_not_parse_is_still_counted()
    {
        new RunRecordWriter(_sandbox.Paths, _sandbox.Files).Append(Record(1));
        File.AppendAllText(RunHistory.File(_sandbox.Paths), "{ torn\n");

        RunHistory.Read(_sandbox.Paths, _sandbox.Files).Unparseable.Should().Be(1);
    }

    [Fact]
    public void The_append_after_a_torn_last_line_starts_on_a_line_of_its_own()
    {
        // A writer killed mid-append leaves bytes with no newline; the next append must not become their continuation.
        Directory.CreateDirectory(_sandbox.Paths.StateDirectory);
        File.WriteAllText(RunHistory.File(_sandbox.Paths), "{\"schemaVersion\":1,\"runId\":\"20261002T1");

        new RunRecordWriter(_sandbox.Paths, _sandbox.Files).Append(Record(3));

        var read = RunHistory.Read(_sandbox.Paths, _sandbox.Files);
        read.Records.Should().ContainSingle().Which.RunId.Text.Should().Be("20261002T100000Z-3");
        read.Unparseable.Should().Be(1, "the torn remains are a corrupt line now that a newline ended them");
    }

    [Fact]
    public void A_half_written_last_container_starts_line_is_ignored()
    {
        var store = new ContainerStartsStore(_sandbox.Paths, _sandbox.Files);
        store.Append(new CoverageLine.Covered(At));
        var day = Directory.GetFiles(store.Directory, "*.jsonl").Single();
        var whole = File.ReadAllText(day).TrimEnd('\n');
        File.AppendAllText(day, whole); // the same marker again, every byte of it but its newline

        store.ReadAll().Should().ContainSingle("a line without its newline is still being written");
    }
}
