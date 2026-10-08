using FluentAssertions;

using WslCare.FakeTool;
using WslCare.TestSupport;

namespace WslCare.Scenarios;

/// <summary>
/// The fake tools' argv log is read by a scenario WHILE a fake may be appending to it under its exclusive open. Main's win-x64
/// leg (run 37793636777, 2026-10-08) failed <c>ProgressWaitTests.A_child_killed_at_its_silence_or_its_cap_has_exited_when_the_timeout_arrives</c>
/// with <c>IOException: … fake-calls.jsonl … being used by another process</c> instead of its timeout: the progress probe read the
/// log at the instant the fake held it. The reader waits for the writer, as the writer waits for another writer.
/// </summary>
public sealed class FakeCallLogTests
{
    [Fact]
    public async Task A_read_while_a_fake_holds_the_log_waits_for_the_line_instead_of_failing()
    {
        using var root = new TempRoot("fake-call-log");
        var path = Path.Combine(root.Path, "fake-calls.jsonl");
        FakeCallLog.Append(path, new FakeCall("docker", ["version"]));

        IReadOnlyList<FakeCall> calls;
        using (var held = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.None))
        {
            var reading = Task.Run(() => FakeCallLog.ReadAll(path), TestContext.Current.CancellationToken);
            await Task.Delay(TimeSpan.FromMilliseconds(300), TestContext.Current.CancellationToken);
            reading.IsCompleted.Should().BeFalse("the log is held by a writer: the read waits rather than failing");
            held.Dispose();
            calls = await reading;
        }

        calls.Should().ContainSingle().Which.Tool.Should().Be("docker");
    }
}
