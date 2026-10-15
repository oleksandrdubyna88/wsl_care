using FluentAssertions;

using WslCare.Core.Actions;
using WslCare.Core.Actions.WindowsTemp;

namespace WslCare.Core.Tests.Actions.WindowsTemp;

/// <summary>
/// WR-T1 (PLAN_windows_runner.md §3.12, §6): what A23 does with what it selected — the re-validated delete, the walk that never
/// follows a link, the cursor, and recovery after a kill. Over an in-memory <c>%TEMP%</c>.
/// </summary>
public sealed class WindowsTempRunTests : IDisposable
{
    private static readonly DateTimeOffset Old = TempWorld.Now.AddDays(-10);
    private static readonly DateTimeOffset Fresh = TempWorld.Now.AddDays(-1);

    private readonly TempWorld _world = new();

    public void Dispose() => _world.Dispose();

    private static WindowsTempAction Action(Func<TempSettings, TempSettings>? change = null) =>
        new(c => change is null ? TempSettings.From(c.Config) : change(TempSettings.From(c.Config)));

    private async Task<(ActionPreview Preview, ActionRun Run)> Both(WindowsTempAction? action = null, Action<ActionPreview>? between = null)
    {
        var a = action ?? Action();
        var context = _world.Context();
        var preview = await a.PreviewAsync(context, _world.Commands(a, context), TestContext.Current.CancellationToken);
        between?.Invoke(preview);
        var run = await a.RunAsync(context, preview, _world.Commands(a, context), TestContext.Current.CancellationToken);
        return (preview, run);
    }

    private static long Fact(ActionPreview preview, string name) => preview.Facts.GetValueOrDefault(name);

    [Fact]
    public async Task An_eligible_tree_is_deleted_through_the_policy_and_counted()
    {
        _world.Dir(@"coai-a\sub", Old).File(@"coai-a\sub\f", Old, 300).File(@"coai-a\g", Old, 200);
        _world.Dir("coai-young", Fresh).File(@"coai-young\f", Fresh);

        var (_, run) = await Both();

        run.Succeeded.Should().BeTrue();
        run.Count.Should().Be(1);
        run.FreedBytes.Should().Be(500);
        _world.Exists("coai-a").Should().BeFalse();
        _world.Exists("coai-young").Should().BeTrue();
        run.Removed.Select(r => r.Name).Should().Equal(_world.Full("coai-a"));
    }

    [Fact]
    public async Task A_sharing_violation_is_kept_and_counted()
    {
        _world.Dir("coai-a", Old).File(@"coai-a\open.log", Old).File(@"coai-a\closed.log", Old).Hold(@"coai-a\open.log");

        var (_, run) = await Both();

        run.Count.Should().Be(0, "a tree with a file in use is not deleted whole");
        run.NotRemoved.Should().ContainSingle().Which.Note.Should().Contain("in use");
        _world.Exists(@"coai-a\open.log").Should().BeTrue();
        _world.DeleteAsked.Count(p => p.EndsWith("open.log", StringComparison.OrdinalIgnoreCase)).Should().Be(1, "never retried in the same run");
        run.Notes.Should().Contain(n => n.Contains("kept in use 1", StringComparison.Ordinal));
        run.Succeeded.Should().BeTrue("a file in use is an outcome, not a failure");
    }

    [Fact]
    public async Task The_candidate_is_revalidated_just_before_the_delete()
    {
        _world.Dir("coai-a", Old).File(@"coai-a\f", Old);

        var (preview, run) = await Both(between: _ => _world.File(@"coai-a\written-since.txt", Fresh));

        preview.Count.Should().Be(1, "it was eligible when previewed");
        run.Count.Should().Be(0);
        _world.Exists("coai-a").Should().BeTrue();
        _world.DeleteAsked.Should().BeEmpty("the re-validation came before the first delete");
        run.NotRemoved.Should().ContainSingle().Which.Note.Should().Contain("changed");
    }

    [Fact]
    public async Task A_candidate_whose_owner_changed_since_the_preview_is_not_deleted()
    {
        _world.Dir("coai-a", Old).File(@"coai-a\f", Old);

        var (_, run) = await Both(between: _ => _world.Dir("coai-a", Old, owner: TempWorld.OtherSid));

        run.Count.Should().Be(0);
        _world.DeleteAsked.Should().BeEmpty();
    }

    [Fact]
    public async Task A_link_that_appears_during_the_delete_walk_stops_the_candidate()
    {
        _world.Dir("coai-a", Old).File(@"coai-a\a-first.txt", Old).Dir(@"coai-a\sub", Old).File(@"coai-a\sub\inner.txt", Old);
        _world.AfterDelete = deleted =>
        {
            if (deleted.EndsWith("a-first.txt", StringComparison.OrdinalIgnoreCase))
            {
                _world.BecomeLink(@"coai-a\sub");
            }
        };

        var (_, run) = await Both();

        run.Count.Should().Be(0);
        run.NotRemoved.Should().ContainSingle().Which.Note.Should().Contain("a link appeared");
        _world.DeleteAsked.Should().NotContain(p => p.EndsWith("inner.txt", StringComparison.OrdinalIgnoreCase), "nothing beyond the link is touched");
        _world.Exists("coai-a").Should().BeTrue();
    }

    // ---------- the Fable own review ----------

    [Fact]
    public async Task A_file_that_appears_after_the_last_delete_survives_and_the_folder_is_kept_not_empty()
    {
        _world.Dir("coai-a", Old).File(@"coai-a\f", Old);
        _world.AfterDelete = deleted =>
        {
            if (deleted.EndsWith(@"\f", StringComparison.OrdinalIgnoreCase))
            {
                _world.File(@"coai-a\fresh.txt", Fresh);
            }
        };

        var (_, run) = await Both();

        run.Count.Should().Be(0, "the folder was not empty when it came to be removed");
        _world.Exists(@"coai-a\fresh.txt").Should().BeTrue("the removal is never recursive: what appeared after the listing is not deleted unjudged");
        _world.Exists("coai-a").Should().BeTrue();
        run.Notes.Should().Contain(n => n.Contains("kept in use 1", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_fresh_file_created_during_the_interruption_retains_the_candidate()
    {
        _world.Dir("coai-half", Fresh, created: Old).File(@"coai-half\left.txt", Old);
        _world.File(@"coai-half\copied-in.txt", Old, created: TempWorld.Now.AddMinutes(-5));
        InProgress("coai-half", Old, TempWorld.Now.AddHours(-1));

        var (preview, _) = await Both();

        preview.Count.Should().Be(0, "a FILE created after the checkpoint (write time kept old by a copy) did not exist when the candidate was judged");
        _world.Exists("coai-half").Should().BeTrue();
    }

    [Fact]
    public async Task A_path_that_resolves_elsewhere_stops_the_candidate_before_anything_beyond_it_is_deleted()
    {
        _world.Dir("coai-a", Old).File(@"coai-a\a-first.txt", Old).Dir(@"coai-a\sub", Old).File(@"coai-a\sub\inner.txt", Old);
        _world.AfterDelete = deleted =>
        {
            if (deleted.EndsWith("a-first.txt", StringComparison.OrdinalIgnoreCase))
            {
                _world.Redirected[_world.Full(@"coai-a\sub")] = @"C:\elsewhere\sub";
            }
        };

        var (_, run) = await Both();

        run.Count.Should().Be(0);
        run.NotRemoved.Should().ContainSingle().Which.Note.Should().Contain("a link appeared");
        _world.DeleteAsked.Should().NotContain(p => p.EndsWith("inner.txt", StringComparison.OrdinalIgnoreCase), "the real path of the folder is not where it was judged");
        _world.Exists(@"coai-a\sub\inner.txt").Should().BeTrue();
    }

    [Fact]
    public async Task A_file_whose_real_path_is_elsewhere_is_not_deleted()
    {
        _world.Dir("coai-a", Old).File(@"coai-a\a-first.txt", Old).File(@"coai-a\b.txt", Old);
        _world.AfterDelete = deleted =>
        {
            if (deleted.EndsWith("a-first.txt", StringComparison.OrdinalIgnoreCase))
            {
                _world.Redirected[_world.Full(@"coai-a\b.txt")] = @"C:\elsewhere\b.txt";
            }
        };

        var (_, run) = await Both();

        run.Count.Should().Be(0);
        _world.DeleteAsked.Should().NotContain(p => p.EndsWith("b.txt", StringComparison.OrdinalIgnoreCase));
        run.NotRemoved.Should().ContainSingle().Which.Note.Should().Contain("a link appeared");
    }

    [Fact]
    public async Task Resuming_an_interrupted_candidate_never_moves_the_cursor_back_to_its_name()
    {
        _world.Dir("coai-a", Old, created: Old).File(@"coai-a\left.txt", Old);
        _world.Dir("coai-n", Old).File(@"coai-n\f", Old);
        _world.WriteCursor(TempCursor.Fresh with { Last = "coai-m", InProgress = new TempCheckpoint("coai-a", Old, TempWorld.Now.AddHours(-1), TempWorld.Sid) });
        string? lastWhileSecondRuns = null;
        _world.AfterDelete = deleted =>
        {
            if (deleted.EndsWith(@"coai-n\f", StringComparison.OrdinalIgnoreCase))
            {
                lastWhileSecondRuns = _world.Cursor().Last;
            }
        };

        await Both();

        _world.Exists("coai-a").Should().BeFalse("the resumed candidate was finished");
        lastWhileSecondRuns.Should().Be("coai-m", "finishing an earlier pass' candidate must not drag the cursor back to its name");
    }

    [Fact]
    public async Task A_target_whose_stat_cannot_be_read_is_unreadable_not_already_gone()
    {
        _world.File("stage-a.log", Old, 10);

        var (_, run) = await Both(between: _ => _world.Unreadable.Add(_world.Full("stage-a.log")));

        run.Count.Should().Be(0);
        run.NotRemoved.Should().ContainSingle().Which.Note.Should().Contain("unreadable").And.NotContain("already gone");
        run.Notes.Should().Contain(n => n.Contains("unreadable 1", StringComparison.Ordinal) && n.Contains("already gone 0", StringComparison.Ordinal));
        _world.Exists("stage-a.log").Should().BeTrue();
    }

    [Fact]
    public async Task The_revalidation_scans_share_one_budget_so_the_delete_phase_is_bounded()
    {
        foreach (var name in new[] { "coai-a", "coai-b" })
        {
            _world.Dir(name, Old).File($@"{name}\f1", Old).File($@"{name}\f2", Old);
        }

        var calls = 0;
        var action = new WindowsTempAction(c => calls++ == 0 ? TempSettings.From(c.Config) : TempSettings.From(c.Config) with { ScanMaxNodes = 3 });
        var context = _world.Context();
        var preview = await action.PreviewAsync(context, _world.Commands(action, context), TestContext.Current.CancellationToken);
        var run = await action.RunAsync(context, preview, _world.Commands(action, context), TestContext.Current.CancellationToken);

        preview.Count.Should().Be(2);
        run.Count.Should().Be(1, "the first candidate's re-validation spent the run's whole budget");
        _world.Exists("coai-b").Should().BeTrue("the second could not be re-validated within it, so it is retained");
    }

    [Fact]
    public async Task A_candidate_folder_that_resolves_elsewhere_at_delete_time_is_never_listed_or_deleted()
    {
        _world.Dir("coai-a", Old).File(@"coai-a\f", Old);

        var (_, run) = await Both(between: _ => _world.Redirected[_world.Full("coai-a")] = @"C:\elsewhere\coai-a");

        run.Count.Should().Be(0);
        run.NotRemoved.Should().ContainSingle().Which.Note.Should().Contain("a link appeared");
        _world.DeleteAsked.Should().BeEmpty("the candidate folder itself is judged before it is listed");
        _world.Exists(@"coai-a\f").Should().BeTrue();
    }

    [Fact]
    public async Task A_file_that_appears_in_a_not_yet_listed_subfolder_after_the_scan_is_never_deleted_and_stops_the_candidate()
    {
        _world.Dir("coai-a", Old).Dir(@"coai-a\sub", Old).File(@"coai-a\sub\f", Old);

        var listings = 0;
        var (_, run) = await Both(between: _ => _world.AfterList = listed =>
        {
            // The first listing of the candidate after the preview is the re-validation scan; the second is the delete walk's.
            if (listed.EndsWith("coai-a", StringComparison.OrdinalIgnoreCase) && ++listings == 2)
            {
                _world.File(@"coai-a\sub\fresh.txt", Fresh);
            }
        });

        run.Count.Should().Be(0);
        _world.Exists(@"coai-a\sub\fresh.txt").Should().BeTrue("a file newer than the age limit is never judged by the old scan");
        _world.Exists("coai-a").Should().BeTrue();
        run.NotRemoved.Should().ContainSingle().Which.Note.Should().Contain("changed while deleting");
    }

    [Fact]
    public async Task The_run_detail_counts_outcomes_and_logical_bytes()
    {
        _world.Dir("coai-a", Old).File(@"coai-a\f", Old, 1000);
        _world.Dir("stage-b", Old).File(@"stage-b\f", Old, 24);
        _world.Dir("coai-young", Fresh).File(@"coai-young\f", Fresh);

        var (_, run) = await Both();

        run.Count.Should().Be(2);
        run.FreedBytes.Should().Be(1024);
        run.FreedBasis.Should().Contain("logical").And.Contain("not disk");
        run.Notes.Should().Contain(n => n.Contains("deleted 2", StringComparison.Ordinal) && n.Contains("logical bytes 1024", StringComparison.Ordinal));
        run.Notes.Should().Contain(n => n.Contains("coai-*", StringComparison.Ordinal) && n.Contains("stage-*", StringComparison.Ordinal), "per group");
    }

    // ---------- the cursor ----------

    [Fact]
    public async Task The_cursor_resumes_after_the_last_decided_candidate()
    {
        foreach (var name in new[] { "coai-a", "coai-b", "coai-c", "coai-d", "coai-e" })
        {
            _world.Dir(name, Old).File($@"{name}\f", Old);
        }

        var two = Action(s => s with { MaxItemsPerRun = 2 });
        await Both(two);
        var afterFirst = _world.Cursor();
        await Both(two);

        afterFirst.Last.Should().Be("coai-b");
        _world.Exists("coai-a").Should().BeFalse();
        _world.Exists("coai-b").Should().BeFalse();
        _world.Exists("coai-c").Should().BeFalse("the second run took up where the first stopped");
        _world.Exists("coai-d").Should().BeFalse();
        _world.Exists("coai-e").Should().BeTrue();
        _world.Cursor().Last.Should().Be("coai-d");
    }

    [Fact]
    public async Task An_entry_created_before_the_cursor_is_reached_after_the_wrap()
    {
        _world.Dir("coai-d", Old).File(@"coai-d\f", Old);
        _world.WriteCursor(TempCursor.Fresh with { Last = "coai-c", PassStarted = Old, PassDecided = 3 });
        _world.Dir("coai-b", Old).File(@"coai-b\f", Old);

        var one = Action(s => s with { MaxItemsPerRun = 1 });
        await Both(one);
        _world.Exists("coai-d").Should().BeFalse("names after the cursor come first");
        _world.Exists("coai-b").Should().BeTrue();

        await Both(one);

        _world.Exists("coai-b").Should().BeFalse("the run wrapped to the first name");
        _world.Cursor().PassesCompleted.Should().Be(1, "the pass reached the end and began again");
    }

    [Fact]
    public async Task A_run_with_nothing_eligible_still_moves_the_cursor()
    {
        foreach (var name in new[] { "coai-a", "coai-b", "coai-c", "coai-d", "coai-e" })
        {
            _world.Dir(name, Fresh).File($@"{name}\f", Fresh);
        }

        var tight = Action(s => s with { ScanMaxNodes = 4 });
        var (first, _) = await Both(tight);
        var cursor = _world.Cursor();
        var (second, _) = await Both(tight);

        Fact(first, TempFacts.Eligible).Should().Be(0);
        Fact(first, TempFacts.Deferred).Should().BeGreaterThan(0, "the budget stopped the run");
        cursor.Last.Should().Be("coai-b", "the cursor moved to the last candidate decided, though none was deleted");
        _world.Cursor().Last.Should().Be("coai-d", "the next run carried on from there");
        Fact(second, TempFacts.Decided).Should().BeGreaterThan(0);
    }

    // ---------- recovery after a kill ----------

    private void InProgress(string name, DateTimeOffset newestFile, DateTimeOffset checkpointAt) =>
        _world.WriteCursor(TempCursor.Fresh with { InProgress = new TempCheckpoint(name, newestFile, checkpointAt, TempWorld.Sid) });

    [Fact]
    public async Task An_in_progress_candidate_is_taken_first_and_judged_by_its_file_times_not_its_folder_times()
    {
        _world.Dir("coai-a", Old).File(@"coai-a\f", Old);
        _world.Dir("coai-half", Fresh, created: Old).File(@"coai-half\left.txt", Old);
        InProgress("coai-half", Old, TempWorld.Now.AddHours(-1));

        var (preview, run) = await Both();

        preview.Targets.First().Name.Should().Be(_world.Full("coai-half"), "the interrupted candidate comes before the cursor moves on");
        preview.Targets.First().Note.Should().Contain("interrupted");
        _world.Exists("coai-half").Should().BeFalse("its folder time is fresh from the half delete, its files are old");
        run.Count.Should().Be(2);
        _world.Cursor().InProgress.IsNone.Should().BeTrue();
    }

    [Fact]
    public async Task A_file_newer_than_the_checkpoint_retains_the_interrupted_candidate()
    {
        _world.Dir("coai-half", Fresh, created: Old).File(@"coai-half\new.txt", Fresh);
        InProgress("coai-half", Old, TempWorld.Now.AddHours(-1));

        var (preview, run) = await Both();

        preview.Count.Should().Be(0);
        run.Count.Should().Be(0);
        _world.Exists("coai-half").Should().BeTrue();
        _world.Cursor().InProgress.IsNone.Should().BeTrue("the marker is cleared once the candidate is decided");
    }

    [Fact]
    public async Task A_fresh_subfolder_created_during_the_interruption_retains_the_candidate()
    {
        _world.Dir("coai-half", Fresh, created: Old).File(@"coai-half\left.txt", Old);
        _world.Dir(@"coai-half\made-later", Old, created: TempWorld.Now.AddMinutes(-5));
        InProgress("coai-half", Old, TempWorld.Now.AddHours(-1));

        var (preview, _) = await Both();

        preview.Count.Should().Be(0, "a folder CREATED after the checkpoint did not exist when the candidate was judged");
        _world.Exists("coai-half").Should().BeTrue();
    }

    [Fact]
    public async Task The_checkpoint_is_written_before_the_first_delete_inside_the_candidate()
    {
        _world.Dir("coai-a", Old).File(@"coai-a\f", Old);
        TempCheckpoint? seen = null;
        _world.AfterDelete = _ => seen ??= _world.Cursor().InProgress;

        await Both();

        seen.Should().NotBeNull();
        seen!.Name.Should().Be("coai-a");
        seen.NewestFileWrite.Should().Be(Old);
        seen.Owner.Should().Be(TempWorld.Sid);
    }

    [Fact]
    public async Task An_interrupted_run_reads_interrupted_not_completed()
    {
        _world.Dir("coai-a", Old).File(@"coai-a\f1", Old).File(@"coai-a\f2", Old);
        _world.Dir("coai-b", Old).File(@"coai-b\f", Old);
        using var cts = new CancellationTokenSource();
        _world.AfterDelete = _ => cts.Cancel();

        var action = Action();
        var context = _world.Context();
        var preview = await action.PreviewAsync(context, _world.Commands(action, context), CancellationToken.None);
        var run = await action.RunAsync(context, preview, _world.Commands(action, context), cts.Token);

        run.Interrupted.Should().BeTrue("the engine records the action interrupted, never ran");
        _world.Exists("coai-b").Should().BeTrue("nothing after the signal was touched");
        _world.Cursor().InProgress.Name.Should().Be("coai-a", "the half-deleted candidate is the next run's first");
    }

    [Fact]
    public async Task The_root_and_the_state_are_never_a_target_of_a_delete()
    {
        _world.Dir("coai-a", Old).File(@"coai-a\f", Old);

        await Both();

        _world.DeleteAsked.Should().OnlyContain(p => p.StartsWith(_world.Full("coai-a"), StringComparison.OrdinalIgnoreCase));
        _world.Refusals.Should().BeEmpty();
    }
}
