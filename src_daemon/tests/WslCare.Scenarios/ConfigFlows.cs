using System.Text.Json;

using FluentAssertions;

using WslCare.Cli;
using WslCare.Core.Config;
using WslCare.Core.Json;

namespace WslCare.Scenarios;

/// <summary>
/// The <c>config</c> verbs end to end: the BUILT CLI over a temporary home, the files it writes read
/// back where the product's own layout says they are, and the fakes' argv log proving no config verb
/// shells out.
/// </summary>
public sealed class ConfigFlows
{
    private static readonly ConfigKey.IntKey Key = ConfigKeys.Volumes.AnonymousMaxGb;

    [Fact]
    public async Task Config_get_on_a_fresh_home_lists_every_key_from_the_default_layer()
    {
        using var home = new ScenarioHome("cfg-get");

        var result = await home.RunAsync("config", "get");

        result.Exit.Should().Be((int)ExitCode.Ok);
        var stderr = CliStderr.Of(result);
        stderr.Messages.Should().BeEmpty("a valid configuration gives no note");
        stderr.Unexplained.Should().BeEmpty("stderr carries only messages and log lines");
        result.StdoutLines.Should().HaveCount(ConfigKeys.All.Count, "one line per key in the register");
        result.StdoutLines.Should().OnlyContain(line => line.TrimEnd().EndsWith("(default)", StringComparison.Ordinal));
        foreach (var key in ConfigKeys.All)
        {
            result.StdoutLines.Should().Contain(line => line.StartsWith(key.Name + " ", StringComparison.Ordinal), $"{key.Name} is in the register");
        }
    }

    [Fact]
    public async Task An_accepted_config_set_writes_the_user_layer_and_config_get_then_names_user()
    {
        using var home = new ScenarioHome("cfg-set");
        var value = AcceptedValue();

        var set = await home.RunAsync("config", "set", Key.Name, value);
        var get = await home.RunAsync("config", "get", Key.Name);
        var json = Report(await home.RunAsync("config", "get", Key.Name, "--json"));

        set.Exit.Should().Be((int)ExitCode.Ok, set.Stderr);
        set.StdoutLines.Should().ContainSingle().Which.Should().Contain($"= {value}").And.EndWith("(user)");
        File.Exists(home.Paths.UserConfigFile).Should().BeTrue("the user layer lands where the sandboxed layout says");
        get.Exit.Should().Be((int)ExitCode.Ok);
        get.StdoutLines.Should().ContainSingle().Which.Should().StartWith(Key.Name).And.Contain($"= {value}").And.EndWith("(user)");
        var entry = json.Values.Should().ContainSingle().Subject;
        entry.Layer.Should().Be(ConfigLayer.User);
        entry.Value.GetInt32().ToString(System.Globalization.CultureInfo.InvariantCulture).Should().Be(value);
        json.ObserveOnly.Should().BeFalse();
    }

    [Fact]
    public async Task A_refused_config_set_prints_one_message_line_exits_with_the_usage_code_and_leaves_the_user_file_unchanged()
    {
        using var home = new ScenarioHome("cfg-refused");
        await home.RunAsync("config", "set", Key.Name, AcceptedValue());
        var before = File.ReadAllBytes(home.Paths.UserConfigFile);
        var refusedValue = RefusedValue();

        var refused = await home.RunAsync("config", "set", Key.Name, refusedValue);

        refused.Exit.Should().Be((int)ExitCode.Usage);
        refused.Stdout.Should().BeEmpty();
        var stderr = CliStderr.Of(refused);
        stderr.Messages.Should().ContainSingle("a refusal is ONE message line").Which.Should().Contain(Key.Name).And.Contain(refusedValue);
        stderr.Unexplained.Should().BeEmpty("everything else on stderr is a log line");
        File.ReadAllBytes(home.Paths.UserConfigFile).Should().Equal(before, "a refused value writes nothing");
    }

    [Fact]
    public async Task Config_reset_removes_the_key_from_the_user_layer_and_get_names_default_again()
    {
        using var home = new ScenarioHome("cfg-reset");
        await home.RunAsync("config", "set", Key.Name, AcceptedValue());

        var reset = await home.RunAsync("config", "reset", Key.Name);
        var get = await home.RunAsync("config", "get", Key.Name);

        reset.Exit.Should().Be((int)ExitCode.Ok, reset.Stderr);
        reset.StdoutLines.Should().ContainSingle().Which.Should().EndWith("(default)");
        get.StdoutLines.Should().ContainSingle().Which.Should().EndWith("(default)");
        File.ReadAllText(home.Paths.UserConfigFile).Should().NotContain("anonymousMaxGb");
    }

    [Fact]
    public async Task A_broken_user_layer_is_reported_observe_only_and_config_set_repairs_it()
    {
        using var home = new ScenarioHome("cfg-broken");
        Directory.CreateDirectory(Path.GetDirectoryName(home.Paths.UserConfigFile)!);
        File.WriteAllText(home.Paths.UserConfigFile, "{ \"dryRun\": false,\n  this is not json\n");

        var text = await home.RunAsync("config", "get");
        var broken = Report(await home.RunAsync("config", "get", "--json"));
        var repair = await home.RunAsync("config", "set", "dryRun", "false");
        var repaired = Report(await home.RunAsync("config", "get", "dryRun", "--json"));

        text.Exit.Should().Be((int)ExitCode.Ok, "a broken layer never stops the daemon (plan §15a #1)");
        text.Stderr.Should().Contain("observe-only");
        broken.ObserveOnly.Should().BeTrue();
        broken.ConfigError.Should().ContainSingle().Which.File.Should().Be(home.Paths.UserConfigFile);
        repair.Exit.Should().Be((int)ExitCode.Ok, repair.Stderr);
        repair.Stderr.Should().Contain("moved to");
        repaired.ObserveOnly.Should().BeFalse();
        repaired.ConfigError.Should().BeEmpty();
        repaired.Values.Should().ContainSingle().Which.Layer.Should().Be(ConfigLayer.User);
        Directory.GetFiles(Path.GetDirectoryName(home.Paths.UserConfigFile)!, "config.json.broken-*").Should().ContainSingle("the unparseable file is kept aside, not overwritten");
    }

    [Fact]
    public async Task No_config_verb_starts_any_tool()
    {
        // Every config verb of the register, by its example, then the refused and repair paths — in one
        // home, so a single argv log sees all of them.
        using var home = new ScenarioHome("cfg-no-tools");
        var configVerbs = CommandLine.Commands.Where(c => c.Spellings.Any(s => s[0] == "config")).ToList();
        configVerbs.Should().HaveCountGreaterThanOrEqualTo(3, "get, set and reset are registered");

        foreach (var verb in configVerbs)
        {
            (await home.RunAsync(verb.Example)).Exit.Should().Be((int)ExitCode.Ok, string.Join(' ', verb.Example));
        }

        await home.RunAsync("config", "set", Key.Name, RefusedValue());
        File.WriteAllText(home.Paths.UserConfigFile, "not json");
        await home.RunAsync("config", "get", "--json");
        await home.RunAsync("config", "set", "dryRun", "true");

        home.Calls.Should().BeEmpty("config verbs read and write files and never shell out; FakeToolFlows proves this log fills when a tool IS started");
    }

    /// <summary>A value inside the key's range, checked against the real validator.</summary>
    private static string AcceptedValue()
    {
        var value = (Key.Min + 1).ToString(System.Globalization.CultureInfo.InvariantCulture);
        ConfigValidation.Parse(Key, value).Should().BeOfType<ValueCheck.Ok>("the fixture must be one the product accepts");
        return value;
    }

    /// <summary>One past the key's maximum, read from the register rather than guessed, and checked
    /// against the real validator so the "refused" flow is refused for the reason it names.</summary>
    private static string RefusedValue()
    {
        var value = ((long)Key.Max + 1).ToString(System.Globalization.CultureInfo.InvariantCulture);
        ConfigValidation.Parse(Key, value).Should().BeOfType<ValueCheck.Invalid>("the fixture must be one the product refuses");
        return value;
    }

    private static ConfigReport Report(TestSupport.ChildResult result)
    {
        result.Exit.Should().Be((int)ExitCode.Ok, result.Stderr);
        return JsonSerializer.Deserialize(result.Stdout, WslCareJsonContext.Default.ConfigReport)
            ?? throw new InvalidOperationException("config get --json printed null");
    }
}
