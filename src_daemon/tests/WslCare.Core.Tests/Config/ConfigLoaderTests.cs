using System.Text;

using FluentAssertions;

using WslCare.Core.Config;
using WslCare.Core.Files;
using WslCare.TestSupport;

namespace WslCare.Core.Tests.Config;

/// <summary>
/// The three layers (plan §6) and what an invalid one does to the run (plan §15a #1): observe-only,
/// the error named with file and line, the user's valid keys still in force, no default sneaking back.
/// </summary>
public sealed class ConfigLoaderTests
{
    private static readonly ConfigLayerFile Machine = new(ConfigLayer.Machine, "/etc/wsl-care/config.json");
    private static readonly ConfigLayerFile User = new(ConfigLayer.User, "/home/me/.config/wsl-care/config.json");

    private static (ConfigLayerFile, FileReadResult) Defaults() => (ConfigLoader.DefaultsFile, new FileReadResult.Content(ConfigLoader.EmbeddedDefaults()));

    private static (ConfigLayerFile, FileReadResult) Layer(ConfigLayerFile file, string json) => (file, new FileReadResult.Content(Encoding.UTF8.GetBytes(json)));

    [Fact]
    public void Defaults_alone_are_valid_and_every_value_comes_from_the_default_layer()
    {
        var result = ConfigLoader.Load([Defaults(), (Machine, new FileReadResult.Missing()), (User, new FileReadResult.Missing())]);

        result.Should().BeOfType<ConfigLoadResult.Valid>();
        result.Config.Entries.Should().OnlyContain(e => e.Layer == ConfigLayer.Default);
    }

    [Fact]
    public void The_user_layer_overrides_the_machine_layer_which_overrides_the_defaults_and_each_value_names_its_layer()
    {
        var result = ConfigLoader.Load(
        [
            Defaults(),
            Layer(Machine, """{ "volumes": { "anonymousMaxGb": 30, "anonymousMaxCount": 200 } }"""),
            Layer(User, """{ "volumes": { "anonymousMaxGb": 40 }, "dryRun": false }"""),
        ]);

        result.Should().BeOfType<ConfigLoadResult.Valid>();
        result.Config.Entry(ConfigKeys.Volumes.AnonymousMaxGb).Should().Be(new ConfigEntry(ConfigKeys.Volumes.AnonymousMaxGb, new ConfigValue.Int(40), ConfigLayer.User));
        result.Config.Entry(ConfigKeys.Volumes.AnonymousMaxCount).Should().Be(new ConfigEntry(ConfigKeys.Volumes.AnonymousMaxCount, new ConfigValue.Int(200), ConfigLayer.Machine));
        result.Config.Entry(ConfigKeys.DryRun).Layer.Should().Be(ConfigLayer.User);
        result.Config.Entry(ConfigKeys.Volumes.AnonymousOlderThanDays).Layer.Should().Be(ConfigLayer.Default);
    }

    [Fact]
    public void An_unknown_key_makes_the_run_observe_only_names_file_and_line_and_keeps_the_valid_keys_of_the_same_file()
    {
        var result = ConfigLoader.Load(
        [
            Defaults(),
            (Machine, new FileReadResult.Missing()),
            Layer(User, "{\n  \"auto\": { \"A4\": false },\n  \"volumes\": { \"anonymousMaxGB\": 40 }\n}"),
        ]);

        var observeOnly = result.Should().BeOfType<ConfigLoadResult.ObserveOnly>().Subject;
        var error = observeOnly.Errors.Should().ContainSingle().Subject;
        error.File.Should().Be(User);
        error.Line.Should().Be(3);
        error.Message.Should().Contain("volumes.anonymousMaxGB").And.Contain("unknown key");
        result.Config.Bool(ConfigKeys.Auto.A4).Should().BeFalse("the valid key of the broken file still applies — a default must never re-enable an action the user switched off");
        result.Config.Int(ConfigKeys.Volumes.AnonymousMaxGb).Should().Be(20);
    }

    [Fact]
    public void An_out_of_range_or_mistyped_value_is_an_error_with_its_line_and_the_default_stays()
    {
        var result = ConfigLoader.Load(
        [
            Defaults(),
            Layer(Machine, "{\n  \"thresholds\": {\n    \"memAvailableWarnPercent\": 250,\n    \"swapWarnGb\": \"four\"\n  }\n}"),
            (User, new FileReadResult.Missing()),
        ]);

        var observeOnly = result.Should().BeOfType<ConfigLoadResult.ObserveOnly>().Subject;
        observeOnly.Errors.Should().HaveCount(2);
        observeOnly.Errors.Select(e => e.Line).Should().Equal(3, 4);
        observeOnly.Errors[0].Message.Should().Contain("from 0 to 100").And.Contain("got 250");
        observeOnly.Errors[1].Message.Should().Contain("swapWarnGb").And.Contain("the text \"four\"");
        result.Config.Int(ConfigKeys.Thresholds.MemAvailableWarnPercent).Should().Be(25);
    }

    [Fact]
    public void A_user_file_that_is_not_json_is_observe_only_and_the_machine_layer_still_applies()
    {
        var result = ConfigLoader.Load(
        [
            Defaults(),
            Layer(Machine, """{ "dryRun": false }"""),
            Layer(User, "{ \"dryRun\": tru"),
        ]);

        var observeOnly = result.Should().BeOfType<ConfigLoadResult.ObserveOnly>().Subject;
        observeOnly.Errors.Should().ContainSingle().Which.File.Layer.Should().Be(ConfigLayer.User);
        observeOnly.Errors[0].Display.Should().StartWith(User.Path + ":1: ");
        result.Config.Bool(ConfigKeys.DryRun).Should().BeFalse();
    }

    [Fact]
    public void An_unreadable_layer_is_an_error_on_the_whole_file_not_a_crash()
    {
        var result = ConfigLoader.Load([Defaults(), (Machine, new FileReadResult.Unreadable("permission denied")), (User, new FileReadResult.Missing())]);

        var observeOnly = result.Should().BeOfType<ConfigLoadResult.ObserveOnly>().Subject;
        var error = observeOnly.Errors.Should().ContainSingle().Subject;
        error.Line.Should().Be(0);
        error.Message.Should().Contain("permission denied");
        error.Display.Should().Be(Machine.Path + ": cannot be read: permission denied");
    }

    [Fact]
    public void Loading_from_a_sandboxed_host_reads_the_two_files_the_host_names()
    {
        using var host = new SandboxHost("loader");
        host.WriteMachineConfig("""{ "npm": { "maxCacheGb": 9 } }""");
        host.WriteUserConfig("""{ "journal": { "keepDays": 10 } }""");

        var result = ConfigLoader.Load(host.Paths, host.Files);

        result.Should().BeOfType<ConfigLoadResult.Valid>();
        result.Config.Int(ConfigKeys.Npm.MaxCacheGb).Should().Be(9);
        result.Config.Int(ConfigKeys.Journal.KeepDays).Should().Be(10);
        result.Config.Entry(ConfigKeys.Journal.KeepDays).Layer.Should().Be(ConfigLayer.User);
    }

    // Retro gate over PR #4 (consultant): an object where a setting's value belongs flattened to nothing, so the layer read as
    // VALID and the setting silently kept the value below it — a schema-invalid layer must be observe-only (plan §15a #1).
    [Theory]
    [InlineData("""{ "auto": { "A4": {} } }""", "auto.A4")]
    [InlineData("""{ "dryRun": {} }""", "dryRun")]
    public void An_object_where_a_settings_value_belongs_makes_the_layer_invalid(string json, string key)
    {
        var result = ConfigLoader.Load([Defaults(), (Machine, new FileReadResult.Missing()), Layer(User, json)]);

        result.IsObserveOnly.Should().BeTrue("an empty object is not a value of {0}", key);
    }
}
