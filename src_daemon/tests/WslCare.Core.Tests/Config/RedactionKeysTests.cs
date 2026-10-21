using System.Text;

using FluentAssertions;

using WslCare.Core.Config;
using WslCare.Core.Files;

namespace WslCare.Core.Tests.Config;

/// <summary>E15.S7 §4: the three numbers of the redaction rule are configuration keys, and every one is MACHINE-ONLY with
/// <see cref="SafeDirection.Lower"/> — a smaller number redacts more or shows less, and a user layer must not loosen what root
/// writes into a history other accounts can read.</summary>
public sealed class RedactionKeysTests
{
    private static readonly ConfigLayerFile User = new(ConfigLayer.User, "/home/me/.config/wsl-care/config.json");

    private static (ConfigLayerFile, FileReadResult) Defaults() => (ConfigLoader.DefaultsFile, new FileReadResult.Content(ConfigLoader.EmbeddedDefaults()));

    public static TheoryData<string, int, int, int> Keys => new()
    {
        { "processes.secretMinChars", 12, 64, 20 },
        { "processes.shownSafeArgs", 0, 8, 2 },
        { "processes.maxJudgedArgChars", 1024, 65536, 4096 },
    };

    [Theory]
    [MemberData(nameof(Keys))]
    public void Each_redaction_number_is_a_machine_only_key_with_its_range_and_default_and_a_smaller_value_is_the_safe_direction(string name, int min, int max, int fallback)
    {
        var key = ConfigKeys.Find(name).Should().BeOfType<ConfigKey.IntKey>($"{name} is a number the owner may change — in the machine layer").Subject;

        (key.Min, key.Max).Should().Be((min, max));
        key.Trust.Should().Be(new KeyTrust(SafeDirection.Lower, MachineOnly: true));
        Tuning.Default.Int(key).Should().Be(fallback);
    }

    [Fact]
    public void A_user_layer_that_loosens_the_redaction_is_ignored_with_a_notice_for_each_key_because_they_are_machine_only()
    {
        var json = """{ "processes": { "secretMinChars": 64, "shownSafeArgs": 8, "maxJudgedArgChars": 65536 } }""";

        var result = ConfigLoader.Load([Defaults(), (User, new FileReadResult.Content(Encoding.UTF8.GetBytes(json)))]);

        result.Notices.Select(n => n.Key).Should().BeEquivalentTo(["processes.secretMinChars", "processes.shownSafeArgs", "processes.maxJudgedArgChars"]);
        result.Notices.Should().OnlyContain(n => n.Message.Contains("machine layer", StringComparison.Ordinal));
        ((ConfigKey.IntKey)ConfigKeys.Find("processes.secretMinChars")!).Should().Match<ConfigKey.IntKey>(k => result.Config.Int(k) == 20);
        ((ConfigKey.IntKey)ConfigKeys.Find("processes.shownSafeArgs")!).Should().Match<ConfigKey.IntKey>(k => result.Config.Int(k) == 2);
    }
}
