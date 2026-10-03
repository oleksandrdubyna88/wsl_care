using FluentAssertions;

using WslCare.Core.Actions;
using WslCare.Core.Actions.BuildServers;
using WslCare.Core.Collectors;
using WslCare.Core.Records;

namespace WslCare.Core.Tests.Actions;

/// <summary>
/// A3 (E3.S3): <c>dotnet build-server shutdown</c> as the target user — the servers it previews (the
/// <c>dotnet-build-servers</c> family, the target user's only), its refusal while a <c>dotnet build|test|run</c> is alive (a
/// button too), the re-check just before the command, the skip when <c>dotnet</c> is not installed or nothing runs, the
/// timer's <c>buildServers.idleHours</c> trigger, and the measured result (which servers are gone after).
/// </summary>
public sealed class BuildServerTests : IDisposable
{
    private readonly UserWorld _world = new("a3");
    private readonly BuildServerShutdown _action = new();

    public void Dispose() => _world.Dispose();

    private static ProcessEntry Server(int pid, double ageHours, string user = "me") =>
        UserWorld.Process(pid, "/usr/share/dotnet/dotnet /usr/share/dotnet/sdk/10.0.100/MSBuild.dll /nodemode:1 /nodeReuse:true", family: BuildServerShutdown.Family, ageHours: ageHours, user: user);

    [Fact]
    public async Task The_preview_lists_the_target_users_servers_only_and_the_timer_fires_on_one_older_than_idle_hours()
    {
        _world.Tool("dotnet");
        _world.Processes.AddRange([Server(10, 5), Server(11, 1), Server(12, 9, user: "other"), UserWorld.Process(13, "node server.js", family: "node")]);
        var context = _world.Context(RunTrigger.Timer);

        var preview = await _action.PreviewAsync(context, _world.Commands(_action, context), CancellationToken.None);

        preview.Skip.Should().BeEmpty();
        preview.Refusal.Should().BeEmpty();
        preview.Items.Select(i => i.Name).Should().Equal("10 p10", "11 p11");
        preview.Facts[BuildServerShutdown.IdleServersFact].Should().Be(1, "only pid 10 is 4 h old or older (buildServers.idleHours)");
        _action.Trigger(preview, context.Config).Fired.Should().BeTrue();
        _action.Trigger(preview with { Facts = new Dictionary<string, long> { [BuildServerShutdown.IdleServersFact] = 0 } }, context.Config).Fired.Should().BeFalse();
        _world.Runner.Requests.Should().BeEmpty("a preview reads the process table only");
    }

    [Theory]
    [InlineData("dotnet build -c Release")]
    [InlineData("/usr/bin/dotnet test tests/x.csproj")]
    [InlineData("dotnet run --project src/app")]
    public async Task A_living_dotnet_build_test_or_run_refuses_a3_for_a_button_too(string build)
    {
        _world.Tool("dotnet");
        _world.Processes.AddRange([Server(10, 5), UserWorld.Process(20, build)]);
        var context = _world.Context(RunTrigger.Manual);

        var preview = await _action.PreviewAsync(context, _world.Commands(_action, context), CancellationToken.None);

        preview.Refusal.Should().Contain("a dotnet build is alive").And.Contain(build);
    }

    [Fact]
    public async Task A_build_that_starts_between_the_preview_and_the_run_stops_the_run_before_any_command()
    {
        _world.Tool("dotnet");
        _world.Processes.Add(Server(10, 5));
        var context = _world.Context(RunTrigger.Cli);
        var commands = _world.Commands(_action, context);
        var preview = await _action.PreviewAsync(context, commands, CancellationToken.None);
        _world.Processes.Add(UserWorld.Process(21, "dotnet test"));

        var run = await _action.RunAsync(context, preview, commands, CancellationToken.None);

        run.Count.Should().Be(0);
        run.Succeeded.Should().BeTrue("nothing failed: nothing was asked");
        run.Notes.Should().ContainSingle(n => n.Contains("started since the preview"));
        _world.Runner.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task The_run_asks_as_the_target_user_and_counts_exactly_the_servers_gone_after()
    {
        _world.Tool("dotnet");
        _world.Processes.AddRange([Server(10, 5), Server(11, 5)]);
        _world.OnTool("dotnet", () => _world.Processes.RemoveAll(p => p.Pid == 10));
        var context = _world.Context(RunTrigger.Cli);
        var commands = _world.Commands(_action, context);

        var run = await _action.RunAsync(context, await _action.PreviewAsync(context, commands, CancellationToken.None), commands, CancellationToken.None);

        _world.Wrapped.Should().Equal("dotnet build-server shutdown");
        run.Removed.Select(r => r.Name).Should().Equal("10 p10");
        run.NotRemoved.Should().ContainSingle(n => n.Name == "11 p11" && n.Note.Contains("still running"));
        run.FreedBytes.Should().BeNull("A3 frees memory, not disk");
        run.Succeeded.Should().BeTrue();
    }

    [Fact]
    public async Task No_dotnet_for_the_target_user_or_no_server_is_a_skip_never_a_failure()
    {
        _world.Processes.Add(Server(10, 5));
        var context = _world.Context(RunTrigger.Cli);

        (await _action.PreviewAsync(context, _world.Commands(_action, context), CancellationToken.None)).Skip.Should().Contain("dotnet is not installed");

        _world.Tool("dotnet");
        _world.Processes.Clear();
        (await _action.PreviewAsync(context, _world.Commands(_action, context), CancellationToken.None)).Skip.Should().Contain("no .NET build server");
    }

    [Fact]
    public async Task An_unreadable_process_table_makes_the_preview_unavailable()
    {
        _world.Tool("dotnet");
        var context = _world.Context(RunTrigger.Cli) with { Processes = _ => Reading.Missing<ProcessSnapshot>("procfs gone") };

        var preview = await _action.PreviewAsync(context, _world.Commands(_action, context), CancellationToken.None);

        preview.Available.Should().BeFalse();
    }

    [Theory]
    [InlineData("dotnet build", true)]
    [InlineData("dotnet watch run", true)]
    [InlineData("/usr/share/dotnet/dotnet /usr/share/dotnet/sdk/10.0.100/MSBuild.dll /nodemode:1", false)]
    [InlineData("dotnet VBCSCompiler.dll -pipename:x", false)]
    [InlineData("node dotnet build", false)]
    public void A_build_is_dotnet_with_a_build_verb_never_a_server_itself(string commandLine, bool build) =>
        BuildServerShutdown.IsDotnetBuild(commandLine).Should().Be(build);
}
