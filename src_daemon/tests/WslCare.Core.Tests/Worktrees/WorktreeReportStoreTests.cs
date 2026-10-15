using System.Text.Json;

using FluentAssertions;

using WslCare.Core.Collectors;
using WslCare.Core.Files;
using WslCare.Core.Hosting;
using WslCare.Core.Json;
using WslCare.Core.Worktrees;
using WslCare.TestSupport;

namespace WslCare.Core.Tests.Worktrees;

/// <summary>
/// E19.S3 (PLAN_disk_hygiene_windows.md §3.3 "The report"): <c>worktrees.json</c> under the state directory — written atomically through
/// the deletion policy's scope (a reader sees the old file or the new, never half), read back with a reason when it cannot be, and the
/// temp file a killed scan left beside it removed at the next start — that file only, and only a stale one.
/// </summary>
public sealed class WorktreeReportStoreTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 10, 15, 12, 0, 0, TimeSpan.Zero);

    private readonly ToDeleteRoot _root = new("report-store");

    private readonly IHostPaths _paths;

    private readonly PhysicalFileSystem _files;

    public WorktreeReportStoreTests()
    {
        _paths = HostPaths.ForThisMachine(_root.Path);
        _files = new PhysicalFileSystem(_paths) { TrustedStateOwner = RegularFiles.EffectiveUid(), OwnersAreThisProcess = true };
    }

    public void Dispose() => _root.Dispose();

    private WorktreeReportStore Store() => new(_paths, _files);

    private static WorktreeReport Sample() =>
        new(Core.SchemaVersion.Current, Now, 3.5, true, [new RootReport(@"D:\w", null, 1, 1, 0, 0, 0, 0, 0, 0, 0, 10, new IdleOver(3, 0, 0), new BuildOutputTotals(new Tally(0, 0), new Tally(0, 0), new Tally(0, 0), new Tally(0, 0), []), new UnknownTotals(0, string.Empty), [])], [],
            new ReclaimableReport(false, "git has not confirmed the candidates yet (E19.S4)"));

    private string Temp(string suffix) => Path.Combine(_paths.StateDirectory, $"worktrees.json.{suffix}.tmp");

    [Fact]
    public void A_report_is_written_under_the_state_directory_and_read_back_whole()
    {
        var store = Store();

        store.TryWrite(Sample()).Should().BeEmpty("a write that worked has no reason to give");

        store.File.Should().Be(Path.Combine(_paths.StateDirectory, "worktrees.json"));
        File.Exists(store.File).Should().BeTrue();
        Directory.EnumerateFiles(_paths.StateDirectory, "*.tmp").Should().BeEmpty("the atomic write leaves no temp file");
        store.Read().Should().BeOfType<Reading<WorktreeReport>.Available>().Which.Value.Should().BeEquivalentTo(Sample());
    }

    [Fact]
    public void A_second_write_replaces_the_first_whole()
    {
        var store = Store();
        store.TryWrite(Sample());

        store.TryWrite(Sample() with { ElapsedSeconds = 99 });

        ((Reading<WorktreeReport>.Available)store.Read()).Value.ElapsedSeconds.Should().Be(99);
    }

    [Fact]
    public void No_report_yet_is_a_reason_not_an_empty_report()
    {
        Store().Read().Should().BeOfType<Reading<WorktreeReport>.Unavailable>().Which.Reason.Should().Contain("worktrees.json");
    }

    [Fact]
    public void A_file_that_is_not_a_report_is_a_reason()
    {
        Directory.CreateDirectory(_paths.StateDirectory);
        File.WriteAllText(Path.Combine(_paths.StateDirectory, "worktrees.json"), "{ not json");

        Store().Read().Should().BeOfType<Reading<WorktreeReport>.Unavailable>().Which.Reason.Should().Contain("not a worktree report");
    }

    [Fact]
    public void A_report_of_a_schema_this_build_does_not_know_is_a_reason()
    {
        Directory.CreateDirectory(_paths.StateDirectory);
        File.WriteAllText(Path.Combine(_paths.StateDirectory, "worktrees.json"), JsonSerializer.Serialize(Sample() with { SchemaVersion = Core.SchemaVersion.Current + 1 }, WslCareJsonContext.Default.WorktreeReport));

        Store().Read().Should().BeOfType<Reading<WorktreeReport>.Unavailable>().Which.Reason.Should().Contain("schema");
    }

    [Fact]
    public void A_write_that_cannot_happen_is_a_reason_and_never_an_exception()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_paths.StateDirectory)!);
        File.WriteAllText(_paths.StateDirectory, "the state directorys name is taken by a file");

        Store().TryWrite(Sample()).Should().NotBeEmpty();
    }

    [Fact]
    public void A_killed_scans_temp_file_is_removed_at_the_next_start()
    {
        Directory.CreateDirectory(_paths.StateDirectory);
        var stale = Temp(new string('a', 32));
        var young = Temp(new string('b', 32));
        var notOurs = Temp("not-a-guid");
        var other = Path.Combine(_paths.StateDirectory, "volume-seen.json." + new string('c', 32) + ".tmp");
        var report = Path.Combine(_paths.StateDirectory, "worktrees.json");
        foreach (var path in new[] { stale, young, notOurs, other, report })
        {
            File.WriteAllText(path, "x");
        }

        File.SetLastWriteTimeUtc(stale, (Now - TimeSpan.FromHours(3)).UtcDateTime);
        File.SetLastWriteTimeUtc(young, (Now - TimeSpan.FromSeconds(5)).UtcDateTime);
        File.SetLastWriteTimeUtc(notOurs, (Now - TimeSpan.FromHours(3)).UtcDateTime);
        File.SetLastWriteTimeUtc(other, (Now - TimeSpan.FromHours(3)).UtcDateTime);

        var removed = Store().SweepStaleTemp(Now, TimeSpan.FromHours(1));

        removed.Should().Be(1);
        File.Exists(stale).Should().BeFalse();
        File.Exists(young).Should().BeTrue("a scan that is writing right now owns it");
        File.Exists(notOurs).Should().BeTrue("only the temp name the atomic write makes: worktrees.json.<32 hex>.tmp");
        File.Exists(other).Should().BeTrue("another file's temp is not ours");
        File.Exists(report).Should().BeTrue("the report itself is never touched");
    }

    [Fact]
    public void Sweeping_with_no_state_directory_is_nothing_to_do()
    {
        Store().SweepStaleTemp(Now, TimeSpan.FromHours(1)).Should().Be(0);
    }
}
