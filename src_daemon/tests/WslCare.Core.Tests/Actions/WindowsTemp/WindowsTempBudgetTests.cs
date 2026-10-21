using FluentAssertions;

using WslCare.Core.Actions;
using WslCare.Core.Actions.WindowsTemp;
using WslCare.Core.Files;
using WslCare.TestSupport;

namespace WslCare.Core.Tests.Actions.WindowsTemp;

/// <summary>
/// WR-T1, the coai code round (56c596c6), findings 11 and 8: the top-level listing and the planning scans spend ONE budget, and a
/// plan that has reached its cap defers the rest of the names arithmetically, without visiting them.
/// </summary>
public sealed class WindowsTempBudgetTests : IDisposable
{
    private static readonly DateTimeOffset Old = TempWorld.Now.AddDays(-10);

    private readonly TempWorld _world = new();

    public void Dispose() => _world.Dispose();

    [Fact]
    public async Task Time_spent_listing_the_top_level_counts_against_the_scan_seconds()
    {
        foreach (var name in new[] { "coai-a", "coai-b" })
        {
            _world.Dir(name, Old).File($@"{name}\f", Old);
        }

        var clock = new ManualTimeProvider(TempWorld.Now) { SteppedTimestamps = true };
        _world.BeforeList = path =>
        {
            // The top-level listing takes 119 s of the 120 s budget; each candidate's own listing then takes 2 s.
            clock.Advance(TimeSpan.FromSeconds(path.EndsWith("Temp", StringComparison.OrdinalIgnoreCase) ? 119 : 2));
        };
        var action = new WindowsTempAction();
        var context = _world.Context(clock: clock);

        var preview = await action.PreviewAsync(context, _world.Commands(action, context), TestContext.Current.CancellationToken);

        preview.Facts[TempFacts.Decided].Should().Be(1, "the listing left the planner one second, which the first candidate's scan used up");
        preview.Facts[TempFacts.Deferred].Should().Be(1);
    }

    private sealed class CountingList(IReadOnlyList<FileEntry> inner) : IReadOnlyList<FileEntry>
    {
        public int Reads { get; private set; }

        public int Count => inner.Count;

        public FileEntry this[int index]
        {
            get
            {
                Reads++;
                return inner[index];
            }
        }

        public IEnumerator<FileEntry> GetEnumerator()
        {
            foreach (var entry in inner)
            {
                Reads++;
                yield return entry;
            }
        }

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }

    [Fact]
    public void Once_the_per_run_cap_is_reached_the_rest_of_the_names_are_deferred_without_being_visited()
    {
        for (var i = 0; i < 6; i++)
        {
            _world.Dir($"coai-{i}", Old).File($@"coai-{i}\f", Old);
        }

        var settings = TempSettings.From(_world.Context().Config) with { MaxItemsPerRun = 2 };
        var context = _world.Context();
        var names = new CountingList([.. _world.ListEntries(TempWorld.Root)]);
        var budget = new TempBudget(settings.ScanMaxNodes, settings.ScanMaxTime, context.Clock);
        var judge = new TempJudge(_world, context.Paths.Rules, TempWorld.Root, TempWorld.Sid, settings, TempWorld.Now, scanDisallowed: true);
        var run = new TempPlanner.Run(context, settings, TempWorld.Root, judge, budget, TestContext.Current.CancellationToken);

        var decided = run.DecideAll(names, TempCheckpoint.None);

        decided.Should().Be(2);
        run.Deferred.Should().Be(4, "the deferred count is unchanged");
        names.Reads.Should().BeLessThanOrEqualTo(2, "no per-name work after the cap: the other four are counted, not visited");
    }
}
