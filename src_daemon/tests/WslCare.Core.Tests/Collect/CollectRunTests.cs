using System.Text.Json;

using FluentAssertions;

using WslCare.Core.Collect;
using WslCare.Core.Config;
using WslCare.Core.Files;
using WslCare.Core.Files.Deletion;
using WslCare.Core.Json;
using WslCare.Core.Processes;
using WslCare.Core.Records;
using WslCare.TestSupport;

namespace WslCare.Core.Tests.Collect;

/// <summary>
/// Plan §15b #1 and #3 for <c>collect</c>: the write order (detail, then the history line naming it), a failed write
/// is a <c>failed</c> run with its reason, the startup reconcile, one run at a time, and an unprivileged run that
/// measures and writes NOTHING. Docker and the tools are a recording runner that starts nothing (every tool "not
/// installed"), the probe a fixed one: these tests are about what is recorded, not what is measured.
/// </summary>
public sealed class CollectRunTests : IDisposable
{
    private readonly SandboxHost _sandbox = new("collect");
    private readonly FixedTimeProvider _clock = new();

    public void Dispose() => _sandbox.Dispose();

    private CollectContext Context(IFileSystem files, int pid = 4242) =>
        new(_sandbox.Paths, files, new RecordingCommandRunner { Default = new CommandOutcome.FailedToStart("not installed in this test") }, _clock,
            new FakeProbe(_sandbox.Paths.Side, _clock), ConfigLoader.Load(_sandbox.Paths, _sandbox.Files), pid, RunTrigger.Timer);

    private IReadOnlyList<RunRecord> History() => RunHistory.Read(_sandbox.Paths, _sandbox.Files).Records;

    [Fact]
    public async Task The_detail_is_written_first_then_the_history_line_that_names_it()
    {
        var files = new OrderRecordingFileSystem(_sandbox.Files);

        var result = await CollectRun.RunAsync(Context(files), CancellationToken.None);

        result.Recording.Should().Be(Recording.Recorded, result.Reason);
        var detailWrite = files.Writes.FindIndex(w => w.StartsWith("atomic:", StringComparison.Ordinal) && w.Contains(RunDetailStore.Folder, StringComparison.Ordinal));
        var historyAppend = files.Writes.FindIndex(w => w.StartsWith("append:", StringComparison.Ordinal) && w.EndsWith(RunRecordWriter.FileName, StringComparison.Ordinal));
        detailWrite.Should().BeGreaterThanOrEqualTo(0);
        historyAppend.Should().BeGreaterThan(detailWrite, "the history line is written only after its detail is on disk");
        var line = History().Should().ContainSingle().Subject;
        line.Detail.Should().Be(result.DetailFile);
        line.Outcome.Should().Be(RunOutcome.Completed);
        var detail = JsonSerializer.Deserialize(File.ReadAllBytes(RunDetailStore.Absolute(_sandbox.Paths, result.DetailFile)), WslCareJsonContext.Default.RunDetail);
        detail!.RunId.Should().Be(line.RunId);
        detail.Thresholds.Should().NotBeEmpty();
    }

    [Fact]
    public async Task A_detail_that_cannot_be_written_makes_the_run_failed_with_the_reason_on_its_history_line()
    {
        var files = new RefusingDetailWrites(_sandbox.Files);

        var result = await CollectRun.RunAsync(Context(files), CancellationToken.None);

        result.Recording.Should().Be(Recording.Failed);
        result.Reason.Should().Contain("the run detail could not be written").And.Contain("disk full (test)");
        var line = History().Should().ContainSingle().Subject;
        line.Outcome.Should().Be(RunOutcome.Failed, "never a silent success");
        line.Reason.Should().Contain("disk full (test)");
        line.Detail.Should().BeNull();
    }

    [Fact]
    public async Task A_history_line_that_cannot_be_written_fails_the_run_and_the_next_run_records_it_as_interrupted()
    {
        var first = await CollectRun.RunAsync(Context(new FailingHistoryAppends(_sandbox.Files), pid: 1), CancellationToken.None);

        first.Recording.Should().Be(Recording.Failed);
        first.Reason.Should().Contain("the history line could not be written");
        History().Should().BeEmpty();
        File.Exists(RunDetailStore.Absolute(_sandbox.Paths, first.DetailFile)).Should().BeTrue("the detail was written first");

        var second = await CollectRun.RunAsync(Context(_sandbox.Files, pid: 2), CancellationToken.None);

        second.Detail!.Housekeeping.Reconcile.Interrupted.Should().Equal(first.Detail!.RunId.Text);
        var lines = History();
        lines.Should().HaveCount(2);
        lines[0].Should().Match<RunRecord>(r => r.RunId == first.Detail.RunId && r.Outcome == RunOutcome.Interrupted && r.Detail == first.DetailFile);
        lines[0].Reason.Should().Be(RunReconcile.InterruptedReason);
    }

    [Fact]
    public async Task A_history_line_whose_detail_is_gone_is_reported_as_detail_lost_and_left_as_it_is()
    {
        var first = await CollectRun.RunAsync(Context(_sandbox.Files, pid: 1), CancellationToken.None);
        File.Delete(RunDetailStore.Absolute(_sandbox.Paths, first.DetailFile));

        var second = await CollectRun.RunAsync(Context(_sandbox.Files, pid: 2), CancellationToken.None);

        second.Detail!.Housekeeping.Reconcile.DetailLost.Should().Equal(first.Detail!.RunId.Text);
        History()[0].Detail.Should().Be(first.DetailFile, "the line is not rewritten");
        RunHistory.Entries(_sandbox.Paths, _sandbox.Files).Select(e => e.Detail).Should().Equal(DetailState.Lost, DetailState.Present);
    }

    [Fact]
    public async Task An_unprivileged_run_measures_and_reports_but_writes_nothing_and_says_read_only()
    {
        var files = new OrderRecordingFileSystem(new NotWritable(_sandbox.Files));
        var context = Context(files);

        var result = await CollectRun.RunAsync(context, CancellationToken.None);

        result.Recording.Should().Be(Recording.ReadOnly);
        result.Reason.Should().StartWith(CollectRun.ReadOnlyNote);
        result.Detail!.Thresholds.Should().NotBeEmpty("it still measured");
        ((FakeProbe)context.Probe).Samples.Should().Be(1);
        files.Writes.Should().BeEmpty("no reconcile, no retention, no detail, no history line, no first sighting");
        Directory.Exists(_sandbox.Paths.StateDirectory).Should().BeFalse();
    }

    [Fact]
    public async Task A_second_run_while_one_holds_the_lock_is_busy_and_does_nothing()
    {
        _sandbox.Files.CreateDirectory(_sandbox.Paths.StateDirectory);
        var held = (ExclusiveLock.Held)RunLock.TryTake(_sandbox.Paths, _sandbox.Files);
        using (held.Handle)
        {
            var context = Context(_sandbox.Files);

            var result = await CollectRun.RunAsync(context, CancellationToken.None);

            result.Recording.Should().Be(Recording.Busy);
            result.Detail.Should().BeNull();
            ((FakeProbe)context.Probe).Samples.Should().Be(0);
        }

        History().Should().BeEmpty();
    }

    [Fact]
    public async Task The_history_line_carries_the_warnings_and_the_slow_parts_status_reads_back()
    {
        var result = await CollectRun.RunAsync(Context(_sandbox.Files), CancellationToken.None);

        var line = History().Single();
        line.Warnings.Should().NotBeNull();
        line.Warnings!.Should().OnlyContain(w => w.Level != "ok");
        line.Slow!.ContainerStats.Should().NotBeNull("docker stats was attempted, and its reason is recorded");
        line.Slow.WindowsClock.Should().NotBeNull();
        LastFullRun.Read(_sandbox.Paths, _sandbox.Files, _clock).ContainerStats.ReasonOrEmpty.Should().Contain(result.Detail!.RunId.Text);
    }

    /// <summary>The real file system, with every write recorded in order: <c>atomic:{path}</c>, <c>append:{path}</c>,
    /// <c>rewrite:{path}</c>, <c>delete:{path}</c>, <c>mkdir:{path}</c>.</summary>
    private sealed class OrderRecordingFileSystem(IFileSystem inner) : DelegatingFileSystem(inner)
    {
        public List<string> Writes { get; } = [];

        public override DeletionVerdict WriteFileAtomically(string path, ReadOnlySpan<byte> content, DeletionScope scope)
        {
            Writes.Add($"atomic:{path}");
            return base.WriteFileAtomically(path, content, scope);
        }

        public override void AppendLine(string path, string line, TimeSpan lockTimeout)
        {
            Writes.Add($"append:{path}");
            base.AppendLine(path, line, lockTimeout);
        }

        public override DeletionVerdict RewriteLines(string path, Func<IReadOnlyList<string>, IReadOnlyList<string>> keep, DeletionScope scope, TimeSpan lockTimeout)
        {
            Writes.Add($"rewrite:{path}");
            return base.RewriteLines(path, keep, scope, lockTimeout);
        }

        public override DeletionVerdict DeleteFile(string path, DeletionScope scope)
        {
            Writes.Add($"delete:{path}");
            return base.DeleteFile(path, scope);
        }

        public override void CreateDirectory(string path)
        {
            Writes.Add($"mkdir:{path}");
            base.CreateDirectory(path);
        }

        public override ExclusiveLock TryLockExclusive(string lockPath)
        {
            Writes.Add($"lock:{lockPath}");
            return base.TryLockExclusive(lockPath);
        }
    }

    private sealed class RefusingDetailWrites(IFileSystem inner) : DelegatingFileSystem(inner)
    {
        public override DeletionVerdict WriteFileAtomically(string path, ReadOnlySpan<byte> content, DeletionScope scope) =>
            path.Contains(RunDetailStore.Folder, StringComparison.Ordinal) ? throw new IOException("disk full (test)") : base.WriteFileAtomically(path, content, scope);
    }

    private sealed class FailingHistoryAppends(IFileSystem inner) : DelegatingFileSystem(inner)
    {
        public override void AppendLine(string path, string line, TimeSpan lockTimeout) =>
            throw new IOException("the history file is on a disk that went away (test)");
    }

    /// <summary>What an unprivileged process meets on the installed layout: the probe answers no.</summary>
    private sealed class NotWritable(IFileSystem inner) : DelegatingFileSystem(inner)
    {
        public override WriteAccess ProbeWriteAccess(string directory) => new WriteAccess.NotWritable($"{directory}: permission denied (test)");

        public override DeletionVerdict WriteFileAtomically(string path, ReadOnlySpan<byte> content, DeletionScope scope) =>
            throw new UnauthorizedAccessException($"{path}: permission denied (test)");
    }
}
