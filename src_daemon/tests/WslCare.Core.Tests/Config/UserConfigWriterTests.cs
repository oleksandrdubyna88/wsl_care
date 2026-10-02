using FluentAssertions;

using WslCare.Core.Config;
using WslCare.TestSupport;

namespace WslCare.Core.Tests.Config;

/// <summary>
/// <c>config set</c> / <c>config reset</c> write ONLY the user layer, atomically, and repair a broken
/// one on the way (plan §15a #1).
/// </summary>
public sealed class UserConfigWriterTests
{
    private static UserConfigWriter Writer(SandboxHost host) => new(host.Paths, host.Files, new FixedTimeProvider());

    [Fact]
    public void Set_creates_the_user_file_with_the_key_nested_and_the_loader_reads_it_back_from_the_user_layer()
    {
        using var host = new SandboxHost("writer-set");

        var result = Writer(host).Set(ConfigKeys.Volumes.AnonymousMaxGb, new ConfigValue.Int(25));

        var written = result.Should().BeOfType<UserConfigWriteResult.Written>().Subject;
        written.File.Should().Be(host.Paths.UserConfigFile);
        written.KeyWasPresent.Should().BeFalse();
        host.ReadUserConfig().Should().Contain("\"volumes\"").And.Contain("\"anonymousMaxGb\": 25");
        var loaded = ConfigLoader.Load(host.Paths, host.Files);
        loaded.Should().BeOfType<ConfigLoadResult.Valid>();
        loaded.Config.Entry(ConfigKeys.Volumes.AnonymousMaxGb).Should().Be(new ConfigEntry(ConfigKeys.Volumes.AnonymousMaxGb, new ConfigValue.Int(25), ConfigLayer.User));
    }

    [Fact]
    public void Set_keeps_the_other_valid_keys_and_overwrites_the_same_key()
    {
        using var host = new SandboxHost("writer-keep");
        host.WriteUserConfig("""{ "dryRun": false, "volumes": { "anonymousMaxGb": 25 } }""");

        var result = Writer(host).Set(ConfigKeys.Volumes.AnonymousMaxGb, new ConfigValue.Int(30));

        result.Should().BeOfType<UserConfigWriteResult.Written>().Which.KeyWasPresent.Should().BeTrue();
        var config = ConfigLoader.Load(host.Paths, host.Files).Config;
        config.Bool(ConfigKeys.DryRun).Should().BeFalse();
        config.Int(ConfigKeys.Volumes.AnonymousMaxGb).Should().Be(30);
    }

    [Fact]
    public void Set_on_a_file_with_invalid_keys_drops_them_names_them_and_leaves_a_valid_file()
    {
        using var host = new SandboxHost("writer-repair");
        host.WriteUserConfig("""{ "dryRun": false, "volumes": { "anonymousMaxGB": 1 }, "npm": { "maxCacheGb": -5 } }""");
        ConfigLoader.Load(host.Paths, host.Files).IsObserveOnly.Should().BeTrue("the fixture must be invalid, or this test proves nothing");

        var result = Writer(host).Set(ConfigKeys.Journal.KeepDays, new ConfigValue.Int(10));

        var written = result.Should().BeOfType<UserConfigWriteResult.Written>().Subject;
        written.DroppedKeys.Should().BeEquivalentTo("volumes.anonymousMaxGB", "npm.maxCacheGb");
        written.MovedAsideTo.Should().BeEmpty();
        var loaded = ConfigLoader.Load(host.Paths, host.Files);
        loaded.Should().BeOfType<ConfigLoadResult.Valid>("set must leave a valid user layer behind");
        loaded.Config.Bool(ConfigKeys.DryRun).Should().BeFalse("the valid key was kept");
        loaded.Config.Int(ConfigKeys.Journal.KeepDays).Should().Be(10);
    }

    [Fact]
    public void Set_on_a_file_that_is_not_json_moves_it_aside_with_a_utc_stamp_and_writes_a_fresh_one()
    {
        using var host = new SandboxHost("writer-broken");
        host.WriteUserConfig("{ this is not json");

        var result = Writer(host).Set(ConfigKeys.DryRun, new ConfigValue.Bool(false));

        var written = result.Should().BeOfType<UserConfigWriteResult.Written>().Subject;
        written.MovedAsideTo.Should().Be(host.Paths.UserConfigFile + ".broken-20261002T120000Z");
        File.ReadAllText(written.MovedAsideTo).Should().Be("{ this is not json", "the user's text is never discarded");
        var loaded = ConfigLoader.Load(host.Paths, host.Files);
        loaded.Should().BeOfType<ConfigLoadResult.Valid>();
        loaded.Config.Bool(ConfigKeys.DryRun).Should().BeFalse();
    }

    [Fact]
    public void Two_repairs_in_the_same_second_keep_both_broken_files_and_both_succeed()
    {
        // The stamp has whole-second precision and the clock is frozen, so both repairs ask for the
        // same aside name; the second must find a free one rather than fail or overwrite the first.
        using var host = new SandboxHost("writer-broken-twice");
        var writer = Writer(host);
        host.WriteUserConfig("{ first broken text");
        var first = writer.Set(ConfigKeys.DryRun, new ConfigValue.Bool(false));
        host.WriteUserConfig("{ second broken text");

        var second = writer.Set(ConfigKeys.DryRun, new ConfigValue.Bool(true));

        var firstAside = first.Should().BeOfType<UserConfigWriteResult.Written>().Subject.MovedAsideTo;
        var secondAside = second.Should().BeOfType<UserConfigWriteResult.Written>().Subject.MovedAsideTo;
        firstAside.Should().Be(host.Paths.UserConfigFile + ".broken-20261002T120000Z", "the first aside keeps the plain UTC stamp");
        secondAside.Should().NotBe(firstAside).And.StartWith(host.Paths.UserConfigFile + ".broken-20261002T120000Z", "the stamp stays readable for a person");
        File.ReadAllText(firstAside).Should().Be("{ first broken text", "an existing aside file is never overwritten");
        File.ReadAllText(secondAside).Should().Be("{ second broken text");
        ConfigLoader.Load(host.Paths, host.Files).Config.Bool(ConfigKeys.DryRun).Should().BeTrue("the second set still wrote its value");
    }

    [Fact]
    public void Reset_removes_the_key_and_says_whether_it_was_there()
    {
        using var host = new SandboxHost("writer-reset");
        host.WriteUserConfig("""{ "dryRun": false, "volumes": { "anonymousMaxGb": 25 } }""");

        var removed = Writer(host).Reset(ConfigKeys.Volumes.AnonymousMaxGb);
        var absent = Writer(host).Reset(ConfigKeys.Npm.MaxCacheGb);

        removed.Should().BeOfType<UserConfigWriteResult.Written>().Which.KeyWasPresent.Should().BeTrue();
        absent.Should().BeOfType<UserConfigWriteResult.Written>().Which.KeyWasPresent.Should().BeFalse();
        var config = ConfigLoader.Load(host.Paths, host.Files).Config;
        config.Entry(ConfigKeys.Volumes.AnonymousMaxGb).Layer.Should().Be(ConfigLayer.Default);
        config.Entry(ConfigKeys.DryRun).Layer.Should().Be(ConfigLayer.User);
        host.ReadUserConfig().Should().NotContain("volumes");
    }

    [Fact]
    public void Reset_on_a_missing_file_writes_an_empty_object_rather_than_failing()
    {
        using var host = new SandboxHost("writer-reset-missing");

        var result = Writer(host).Reset(ConfigKeys.DryRun);

        result.Should().BeOfType<UserConfigWriteResult.Written>().Which.KeyWasPresent.Should().BeFalse();
        host.ReadUserConfig().Trim().Should().Be("{}");
    }

    [Fact]
    public void The_write_leaves_no_temporary_file_in_the_config_directory()
    {
        using var host = new SandboxHost("writer-tmp");

        Writer(host).Set(ConfigKeys.DryRun, new ConfigValue.Bool(false));

        Directory.GetFiles(Path.GetDirectoryName(host.Paths.UserConfigFile)!).Should().ContainSingle()
            .Which.Should().Be(host.Paths.UserConfigFile);
    }
}
