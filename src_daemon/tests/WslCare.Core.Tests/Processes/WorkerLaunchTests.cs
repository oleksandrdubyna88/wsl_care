using FluentAssertions;

using WslCare.Core.Processes;

namespace WslCare.Core.Tests.Processes;

/// <summary>WR-D3, the second coai code round (finding 2): a <see cref="WorkerLaunch"/> owns its arguments — the constructor copies them,
/// so no caller's list can change what the supervisor starts after the launch was built.</summary>
public sealed class WorkerLaunchTests
{
    [Fact]
    public void Changing_the_source_list_after_construction_does_not_change_the_arguments()
    {
        var source = new List<string> { "watch", "--timer" };
        var launch = new WorkerLaunch("C:\\Users\\user\\AppData\\Local\\wsl-care\\bin\\0123\\wsl-care.exe", source, TimeSpan.FromMinutes(10), "mcpWatchdog.runLimitMinutes");

        source.Add("--confirm");
        source[0] = "act";

        launch.Arguments.Should().Equal(["watch", "--timer"], "the launch holds its own copy of the closed table's words");
        launch.Arguments.Should().NotBeAssignableTo<List<string>>("the copy is not a list anyone can add to");
    }
}
