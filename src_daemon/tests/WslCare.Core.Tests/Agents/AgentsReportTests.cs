using FluentAssertions;

using WslCare.Core.Agents;
using WslCare.Core.Config;
using WslCare.TestSupport;

namespace WslCare.Core.Tests.Agents;

/// <summary>
/// The wire figures of <c>agents list</c> (plan §15q D1, M7): an agent whose folder was not measured — the budget did not reach
/// it, the rules refused a manual agent — has NO total, with that folder's reason; never a part shown as the whole, never 0.
/// A folder that does not exist holds nothing. Growth is taken only between two whole measurements.
/// </summary>
public sealed class AgentsReportTests : IDisposable
{
    private readonly LinuxSandbox _sandbox = new("agents-report");

    public void Dispose() => _sandbox.Dispose();

    private static AgentEntry Claude => AgentCatalogue.Agents.Single(a => a.Id == "claude-code");

    private AgentReport Report(AgentsSample now, AgentsSample? before = null)
    {
        var presence = new AgentPresence(Claude, [AgentDiscovery.Folder], [], string.Empty, "not asked", ["/a", "/b"], string.Empty);
        return AgentsReports.From("wsl", FixedTimeProvider.DefaultNow, [presence], new AgentSizes.Now(now), before, ConfigLoader.Load(_sandbox.Paths, _sandbox.Files).Config).Agents.Single();
    }

    private static AgentsSample Sample(params AgentFolderSize[] folders) =>
        new(FixedTimeProvider.DefaultNow, [new AgentSize(Claude.Id, folders, SessionFigures.NotCounted("n/a"))]);

    [Fact]
    public void A_folder_the_budget_did_not_reach_makes_the_total_unavailable_with_its_reason()
    {
        var report = Report(Sample(new AgentFolderSize("/a", true, 100, 1, true, [], string.Empty), new AgentFolderSize("/b", true, 0, 0, false, [], AgentWalk.NotReached)));

        report.TotalBytes.Available.Should().BeFalse("100 bytes of one folder are not the agent's size");
        report.TotalBytes.Reason.Should().Be(AgentWalk.NotReached);
    }

    [Fact]
    public void A_folder_that_does_not_exist_counts_as_nothing_and_a_cut_walk_as_its_lower_bound()
    {
        var report = Report(Sample(new AgentFolderSize("/a", true, 100, 1, false, [], "stopped after 2000000 entries; a lower bound"), new AgentFolderSize("/b", false, 0, 0, true, [], "/b does not exist")));

        report.TotalBytes.Should().Be(new Core.Status.ByteFigure(true, 100, "stopped after 2000000 entries; a lower bound"), "a lower bound says so (E7.S1/S2 review R2)");
    }

    [Fact]
    public void Growth_is_taken_only_between_two_whole_measurements()
    {
        var whole = Sample(new AgentFolderSize("/a", true, 100, 1, true, [], string.Empty), new AgentFolderSize("/b", true, 50, 1, true, [], string.Empty));
        var partial = Sample(new AgentFolderSize("/a", true, 10, 1, true, [], string.Empty), new AgentFolderSize("/b", true, 0, 0, false, [], AgentWalk.NotReached));

        Report(whole, partial).GrowthBytes.Available.Should().BeFalse();
        Report(partial, whole).GrowthBytes.Available.Should().BeFalse();
        Report(whole, whole).GrowthBytes.Bytes.Should().Be(0);
    }
}
