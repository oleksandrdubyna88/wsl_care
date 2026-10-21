using System.Text.Json;

using FluentAssertions;

using WslCare.Core.Collectors;
using WslCare.Core.Config;
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
            new ReclaimableReport(false, "git has not confirmed the candidates (the scan had no git to ask)"));

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

    /// <summary>E19.S3 Fable review, recommended 4: a file that parses but lacks what the status reads (a hand-made or damaged one) is
    /// unreadable with a reason — it must not make status throw.</summary>
    [Theory]
    [InlineData("""{"schemaVersion":1}""")]
    [InlineData("""{"schemaVersion":1,"roots":[null],"top":[],"reclaimable":{"available":false,"reason":"x"}}""")]
    [InlineData("""{"schemaVersion":1,"roots":[{"path":"D:\\w"}],"top":[],"reclaimable":{"available":false,"reason":"x"}}""")]
    [InlineData("""{"schemaVersion":1,"roots":[],"reclaimable":null}""")]
    public void A_report_missing_what_the_status_reads_is_unreadable_not_a_crash(string json)
    {
        Directory.CreateDirectory(_paths.StateDirectory);
        File.WriteAllText(Path.Combine(_paths.StateDirectory, "worktrees.json"), json);

        var read = Store().Read();

        read.Should().BeOfType<Reading<WorktreeReport>.Unavailable>().Which.Reason.Should().Contain("not a worktree report");
    }

    /// <summary>Coai plan round (E19.S3): the report is read back with <c>records.maxStateFileBytes</c>' cap, so one written larger would be unreadable
    /// for ever. The write trims what is only detail (the named retentions and the top list) and, when that is not enough, refuses and says so — it
    /// never leaves a file the reader will refuse.</summary>
    private static (IDisposable Scope, int Cap) SmallCap()
    {
        var defaults = new FileReadResult.Content(ConfigLoader.EmbeddedDefaults());
        var machine = new FileReadResult.Content(System.Text.Encoding.UTF8.GetBytes("{ \"records\": { \"maxStateFileBytes\": 65536 } }"));
        var loaded = ConfigLoader.Load([(ConfigLoader.DefaultsFile, defaults), (new ConfigLayerFile(ConfigLayer.Machine, @"C:\ProgramData\wsl-care\config.json"), machine)]);
        loaded.IsObserveOnly.Should().BeFalse(string.Join("; ", loaded.Errors.Select(e => e.Display)));
        return (Tuning.Use(loaded.Config), loaded.Config.Int(ConfigKeys.Records.MaxStateFileBytes));
    }

    [Fact]
    public void A_report_over_the_read_cap_is_trimmed_to_its_counts_and_still_readable()
    {
        var (scope, cap) = SmallCap();
        using (scope)
        {
            var heavy = Sample().Roots[0] with { NamedRetentions = [.. Enumerable.Range(0, 1000).Select(i => new NamedRetention($"wt{i}", "bin", new string('r', 200)))] };
            var report = Sample() with { Roots = [heavy] };

            var failed = Store().TryWrite(report);

            failed.Should().BeEmpty("what is only detail is trimmed, the counts are kept");
            new FileInfo(Store().File).Length.Should().BeLessThanOrEqualTo(cap);
            var back = ((Reading<WorktreeReport>.Available)Store().Read()).Value;
            back.Roots[0].NamedRetentions.Should().BeEmpty("the named retentions were the detail that went");
            back.Roots[0].Folders.Should().Be(1);
        }
    }

    [Fact]
    public void A_report_that_is_too_large_even_trimmed_is_refused_and_no_unreadable_file_is_left()
    {
        var (scope, cap) = SmallCap();
        using (scope)
        {
            var wide = Sample().Roots[0] with { Unknown = new UnknownTotals(1, new string('u', 10_000)) };
            var report = Sample() with { Roots = [.. Enumerable.Repeat(wide, 12)] };

            var failed = Store().TryWrite(report);

            failed.Should().Contain("the report is too large to keep").And.Contain(cap.ToString(System.Globalization.CultureInfo.InvariantCulture));
            File.Exists(Store().File).Should().BeFalse("a file the reader would refuse is never written");
        }
    }

    [Fact]
    public void An_old_good_report_stays_when_a_too_large_new_one_is_refused()
    {
        var (scope, _) = SmallCap();
        using (scope)
        {
            Store().TryWrite(Sample()).Should().BeEmpty();
            var wide = Sample().Roots[0] with { Unknown = new UnknownTotals(1, new string('u', 10_000)) };

            Store().TryWrite(Sample() with { Roots = [.. Enumerable.Repeat(wide, 12)] }).Should().NotBeEmpty();

            Store().Read().Should().BeOfType<Reading<WorktreeReport>.Available>("the previous report is still the one a reader sees");
        }
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
