using FluentAssertions;

using WslCare.Core.Actions;
using WslCare.Core.Actions.WindowsTemp;
using WslCare.Core.Records;

namespace WslCare.Core.Tests.Actions.WindowsTemp;

/// <summary>
/// WR-T1 (PLAN_windows_runner.md §3.12, §6): what A23 selects — over an in-memory <c>%TEMP%</c>. Nothing here is the real folder.
/// </summary>
public sealed class WindowsTempSelectionTests : IDisposable
{
    private static readonly DateTimeOffset Old = TempWorld.Now.AddDays(-10);
    private static readonly DateTimeOffset Fresh = TempWorld.Now.AddDays(-1);

    private readonly TempWorld _world = new();

    public void Dispose() => _world.Dispose();

    /// <summary>The action under the product's settings, or with an allow-list the CONFIGURATION would refuse (<c>*</c>) — what proves
    /// a guard does not rest on the key's validation.</summary>
    private static WindowsTempAction Action(Func<TempSettings, TempSettings>? change = null) =>
        new(c => change is null ? TempSettings.From(c.Config) : change(TempSettings.From(c.Config)));

    private async Task<ActionPreview> Preview(WindowsTempAction? action = null, ActionContext? context = null)
    {
        var a = action ?? Action();
        var c = context ?? _world.Context();
        return await a.PreviewAsync(c, _world.Commands(a, c), CancellationToken.None);
    }

    private static long Fact(ActionPreview preview, string name) => preview.Facts.GetValueOrDefault(name);

    [Fact]
    public async Task Nothing_under_temp_claude_is_ever_selected_whatever_the_allow_list()
    {
        _world.Dir(@"claude\session\deep", Old).File(@"claude\session\deep\log.txt", Old);
        var everything = Action(s => s with { AllowGlobs = ["*"] });
        var context = _world.Context();

        var preview = await everything.PreviewAsync(context, _world.Commands(everything, context), CancellationToken.None);
        var run = await everything.RunAsync(context, preview, _world.Commands(everything, context), CancellationToken.None);

        Fact(preview, TempFacts.Eligible).Should().Be(0, "claude is excluded whatever the allow-list says");
        Fact(preview, TempFacts.Excluded).Should().Be(1);
        run.Count.Should().Be(0);
        _world.Exists(@"claude\session\deep\log.txt").Should().BeTrue();
        _world.DeleteAsked.Should().BeEmpty("not even a delete was asked for");
        _world.Listed.Should().NotContain(p => p.Contains("claude", StringComparison.OrdinalIgnoreCase) && !p.EndsWith("Temp", StringComparison.OrdinalIgnoreCase), "the guard comes before any traversal");
    }

    [Fact]
    public async Task An_entry_of_another_owner_is_excluded()
    {
        _world.Dir("coai-theirs", Old, owner: TempWorld.OtherSid).File(@"coai-theirs\f", Old, owner: TempWorld.OtherSid);

        var preview = await Preview();

        Fact(preview, TempFacts.Eligible).Should().Be(0);
        Fact(preview, TempFacts.Excluded).Should().Be(1);
    }

    [Fact]
    public async Task An_unknown_current_account_owns_nothing_so_nothing_is_selected()
    {
        _world.Dir("coai-mine", Old).File(@"coai-mine\f", Old);

        var preview = await Preview(context: _world.Context(sid: string.Empty));

        Fact(preview, TempFacts.Eligible).Should().Be(0);
        Fact(preview, TempFacts.Excluded).Should().Be(1);
    }

    [Fact]
    public async Task A_tree_holding_a_reparse_point_is_retained_and_never_followed()
    {
        _world.Dir(@"coai-a\sub", Old).File(@"coai-a\sub\f", Old).Link(@"coai-a\sub\junction", Old);

        var preview = await Preview();

        Fact(preview, TempFacts.Eligible).Should().Be(0);
        Fact(preview, TempFacts.Unknown).Should().Be(1, "a tree that holds a reparse point is retained as unknown");
        _world.Listed.Should().NotContain(p => p.EndsWith("junction", StringComparison.OrdinalIgnoreCase), "the walk never enters a reparse point");
    }

    [Fact]
    public async Task A_reparse_point_candidate_is_retained_unknown_and_never_listed()
    {
        _world.Link("coai-link", Old);

        var preview = await Preview();

        Fact(preview, TempFacts.Unknown).Should().Be(1);
        Fact(preview, TempFacts.Eligible).Should().Be(0);
        _world.Listed.Should().NotContain(p => p.EndsWith("coai-link", StringComparison.OrdinalIgnoreCase), "its attributes are read before any child enumeration");
    }

    [Fact]
    public async Task An_old_parent_with_a_fresh_nested_file_is_retained()
    {
        _world.Dir("coai-a", Old).Dir(@"coai-a\sub", Old).File(@"coai-a\sub\fresh.txt", Fresh);

        var preview = await Preview();

        Fact(preview, TempFacts.Eligible).Should().Be(0);
        Fact(preview, TempFacts.Young).Should().Be(1, "the NEWEST descendant decides the age, not the folder's own time");
    }

    [Fact]
    public async Task A_tree_scanned_past_its_budget_is_unknown_and_retained()
    {
        _world.Dir("coai-big", Old);
        for (var i = 0; i < 10; i++)
        {
            _world.File($@"coai-big\f{i}", Old);
        }

        var preview = await Preview(Action(s => s with { ScanMaxNodes = 5 }));

        Fact(preview, TempFacts.Eligible).Should().Be(0);
        Fact(preview, TempFacts.Unknown).Should().Be(1, "a tree not scanned to its end within the budget is unknown, retained");
    }

    [Fact]
    public async Task An_old_folder_outside_the_allow_list_is_previewed_as_would_be_eligible_and_never_deleted()
    {
        _world.Dir("sometool-1", Old).File(@"sometool-1\f", Old, 500);
        var action = Action();
        var context = _world.Context();

        var preview = await action.PreviewAsync(context, _world.Commands(action, context), CancellationToken.None);
        var run = await action.RunAsync(context, preview, _world.Commands(action, context), CancellationToken.None);

        Fact(preview, TempFacts.WouldBeEligible).Should().Be(1);
        Fact(preview, TempFacts.Eligible).Should().Be(0);
        preview.Facts.Keys.Should().Contain(k => k.StartsWith(TempFacts.GroupPrefix, StringComparison.Ordinal) && k.Contains("sometool-*", StringComparison.Ordinal));
        run.Count.Should().Be(0);
        _world.Exists("sometool-1").Should().BeTrue();
        _world.DeleteAsked.Should().BeEmpty();
    }

    [Fact]
    public async Task The_timer_does_not_scan_what_the_allow_list_does_not_cover()
    {
        _world.Dir("sometool-1", Old).File(@"sometool-1\f", Old);

        var preview = await Preview(context: _world.Context(trigger: RunTrigger.Timer));

        Fact(preview, TempFacts.NotAllowed).Should().Be(1);
        _world.Listed.Should().NotContain(p => p.EndsWith("sometool-1", StringComparison.OrdinalIgnoreCase), "the timer spends its budget on what it may delete");
    }

    [Fact]
    public async Task A_name_of_the_test_leftover_sweep_is_never_selected_and_is_named_so()
    {
        _world.Dir("testsToDelete-abc", Old).File(@"testsToDelete-abc\f", Old);

        var preview = await Preview(Action(s => s with { AllowGlobs = ["*"] }));

        Fact(preview, TempFacts.Eligible).Should().Be(0);
        Fact(preview, TempFacts.Excluded).Should().Be(1);
        preview.Skip.Should().BeEmpty();
        preview.What.Should().Contain("test-leftover sweep");
    }

    [Theory]
    [InlineData(".git", true)]
    [InlineData(".git", false)]
    public async Task A_git_working_folder_is_retained_whatever_the_allow_list(string marker, bool markerIsFolder)
    {
        _world.Dir("coai-worktree", Old).File(@"coai-worktree\src.txt", Old);
        if (markerIsFolder)
        {
            _world.Dir($@"coai-worktree\{marker}", Old).File($@"coai-worktree\{marker}\HEAD", Old);
        }
        else
        {
            _world.File($@"coai-worktree\{marker}", Old);
        }

        var preview = await Preview();

        Fact(preview, TempFacts.Eligible).Should().Be(0);
        Fact(preview, TempFacts.Excluded).Should().Be(1);
        preview.What.Should().Contain("git working folder");
    }

    [Fact]
    public async Task An_eligible_folder_and_an_eligible_file_are_targets_and_a_young_one_is_not()
    {
        _world.Dir("coai-old", Old).File(@"coai-old\f", Old, 300);
        _world.File("stage-old.log", Old, 700);
        _world.Dir("coai-new", Fresh).File(@"coai-new\f", Fresh);

        var preview = await Preview();

        preview.Count.Should().Be(2);
        preview.Bytes.Should().Be(1000);
        preview.Targets.Select(t => t.Name).Should().Equal(_world.Full("coai-old"), _world.Full("stage-old.log"));
        Fact(preview, TempFacts.Young).Should().Be(1);
        Fact(preview, TempFacts.Total).Should().Be(3);
    }

    [Fact]
    public async Task Eligible_entries_stop_at_the_per_run_maximum_and_the_rest_are_deferred()
    {
        for (var i = 0; i < 5; i++)
        {
            _world.Dir($"coai-{i}", Old).File($@"coai-{i}\f", Old);
        }

        var preview = await Preview(Action(s => s with { MaxItemsPerRun = 2 }));

        preview.Count.Should().Be(2);
        Fact(preview, TempFacts.Deferred).Should().Be(3);
    }

    [Fact]
    public async Task A_folder_with_more_top_level_entries_than_the_maximum_is_refused_whole()
    {
        for (var i = 0; i < 6; i++)
        {
            _world.File($"coai-{i}.log", Old);
        }

        var preview = await Preview(Action(s => s with { MaxTopLevelEntries = 3 }));

        preview.Refusal.Should().Contain("temp.maxTopLevelEntries");
        preview.Count.Should().Be(0);
    }

    [Fact]
    public async Task The_trigger_fires_only_when_something_is_eligible()
    {
        var action = Action();
        var empty = await Preview(action);
        _world.Dir("coai-a", Old).File(@"coai-a\f", Old);
        var one = await Preview(action);

        action.Trigger(empty, _world.Context().Config).Fired.Should().BeFalse();
        action.Trigger(one, _world.Context().Config).Fired.Should().BeTrue();
    }

    [Fact]
    public void The_action_runs_on_the_windows_side_only_and_takes_no_command()
    {
        var action = new WindowsTempAction();

        action.Id.Text.Should().Be("A23");
        action.Sides.Should().Equal(WslCare.Core.Hosting.HostSide.Windows);
        action.Commands.Should().BeEmpty();
        action.Id.ButtonOnly.Should().BeFalse();
    }
}
