using System.Text.Json;

using FluentAssertions;

using WslCare.Core.Actions;
using WslCare.Core.Actions.Engine;
using WslCare.Core.Config;
using WslCare.Core.Json;
using WslCare.Core.Records;
using WslCare.TestSupport;

namespace WslCare.Core.Tests.Actions;

/// <summary>
/// Plan §15q R1.4: a run that acted under a value someone set — a user's layer read by the root timer — says so in its detail:
/// every setting not from the embedded defaults, with the layer that set it, and every user value it did not take.
/// </summary>
public sealed class ConfigProvenanceTests : IDisposable
{
    private readonly LinuxSandbox _sandbox = new("config-provenance");
    private readonly List<string> _journal = [];

    public void Dispose() => _sandbox.Dispose();

    [Fact]
    public async Task An_act_run_detail_names_every_setting_a_layer_above_the_defaults_set_and_its_layer()
    {
        _sandbox.Write("/home/me/.config/wsl-care/config.json", """{ "containers": { "stoppedOlderThanDays": 3 }, "archive": { "baseFolder": "/srv/archive" } }""");

        var detail = await RunA10(ConfigLoader.Load(_sandbox.Paths, _sandbox.Files));

        detail.Config.Should().ContainSingle().Which.Should().Match<ConfigValueReport>(v =>
            v.Key == "containers.stoppedOlderThanDays" && v.Value.GetInt32() == 3 && v.Layer == ConfigLayer.User);
        detail.ConfigNotices.Should().ContainSingle().Which.Key.Should().Be("archive.baseFolder");
        var written = System.Text.Json.Nodes.JsonNode.Parse(JsonSerializer.Serialize(detail, WslCareJsonContext.Default.ActRunDetail))!;
        written["config"]!.AsArray().Should().ContainSingle();
        written["configNotices"]!.AsArray().Should().ContainSingle();
    }

    [Fact]
    public async Task A_run_under_the_defaults_records_exactly_what_it_recorded_before()
    {
        var detail = await RunA10(ConfigLoader.Load(_sandbox.Paths, _sandbox.Files));

        detail.Config.Should().BeNull();
        detail.ConfigNotices.Should().BeNull();
        var written = System.Text.Json.Nodes.JsonNode.Parse(JsonSerializer.Serialize(detail, WslCareJsonContext.Default.ActRunDetail))!.AsObject();
        written.ContainsKey("config").Should().BeFalse();
        written.ContainsKey("configNotices").Should().BeFalse();
    }

    private async Task<ActRunDetail> RunA10(ConfigLoadResult loaded)
    {
        var engine = new ActionEngine(new EngineContext(_sandbox.Paths, _sandbox.Files, new RecordingCommandRunner(), new FixedTimeProvider(), new FakeProbe(_sandbox.Paths.Side, new FixedTimeProvider()), loaded,
            new FakeProcessTable().Alive(77, FixedTimeProvider.DefaultNow.AddMinutes(-1)), 77, new ActionRegistry([new ScriptedAction("A10", _journal)])));

        var result = await engine.ExecuteAsync(new ActRequest([ActionId.Find("A10")!], RunTrigger.Cli, Execute: true), CancellationToken.None);

        return result.Should().BeOfType<ActResult.Done>().Subject.Detail;
    }
}
