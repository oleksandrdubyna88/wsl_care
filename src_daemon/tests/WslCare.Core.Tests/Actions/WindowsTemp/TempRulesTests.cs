using FluentAssertions;

using WslCare.Core.Actions.WindowsTemp;
using WslCare.Core.Config;
using WslCare.Core.Files;
using WslCare.Core.Hosting;

namespace WslCare.Core.Tests.Actions.WindowsTemp;

/// <summary>WR-T1: the pure rules — the <c>claude</c> guard, the root, the name patterns and the <c>temp.*</c> keys.</summary>
public sealed class TempRulesTests
{
    private static readonly PathRules Rules = PathRules.Windows;
    private const string Root = @"C:\Users\user\AppData\Local\Temp";

    [Theory]
    [InlineData("claude")]
    [InlineData("CLAUDE")]
    [InlineData("Claude")]
    [InlineData(@"claude\")]
    [InlineData("claude/")]
    [InlineData("claude.")]
    [InlineData("claude ")]
    [InlineData(@"C:\Users\user\AppData\Local\Temp\claude")]
    [InlineData(@"c:\users\USER\appdata\local\temp\CLAUDE\")]
    public void The_claude_guard_compares_the_name_and_the_full_path_ignoring_case_and_separators(string spelling)
    {
        TempGuards.IsClaude(Rules, Root, spelling).Should().BeTrue();
    }

    [Theory]
    [InlineData("claude-code")]
    [InlineData("claudex")]
    [InlineData("coai-claude")]
    [InlineData(@"coai-a\claude")]
    [InlineData("")]
    public void A_name_that_only_contains_claude_is_not_the_guarded_folder(string spelling)
    {
        TempGuards.IsClaude(Rules, Root, spelling).Should().BeFalse();
    }

    private static TempRootResult Resolve(string local, string temp, string profile = @"C:\Users\user") =>
        TempRoots.Resolve(new WindowsHostPaths(new WindowsEnvironment(profile, profile + @"\AppData\Roaming", local, @"C:\ProgramData", temp)));

    [Fact]
    public void The_root_is_local_app_data_temp_when_the_temp_path_names_the_same_folder()
    {
        Resolve(@"C:\Users\user\AppData\Local", Root).Should().BeOfType<TempRootResult.Resolved>().Which.Root.Should().Be(Root);
    }

    [Fact]
    public void A_different_spelling_of_the_same_folder_is_the_same_folder()
    {
        Resolve(@"C:\Users\user\AppData\Local", @"c:\users\USER\AppData\LOCAL\temp\").Should().BeOfType<TempRootResult.Resolved>();
    }

    [Fact]
    public void A_temp_path_naming_another_folder_refuses_the_run_with_the_reason()
    {
        Resolve(@"C:\Users\user\AppData\Local", @"C:\Users\user\AppData\Local\Temp\2").Should().BeOfType<TempRootResult.Refused>()
            .Which.Reason.Should().Contain("different folders");
    }

    [Fact]
    public void A_root_outside_the_profile_refuses_the_run()
    {
        Resolve(@"D:\Redirected\Local", @"D:\Redirected\Local\Temp").Should().BeOfType<TempRootResult.Refused>()
            .Which.Reason.Should().Contain("profile");
    }

    [Fact]
    public void A_drive_root_or_the_profile_itself_is_never_a_root()
    {
        Resolve(@"C:\", @"C:\Temp").Should().BeOfType<TempRootResult.Refused>();
    }

    [Theory]
    [InlineData("coai-*", "coai-abc", true)]
    [InlineData("coai-*", "COAI-abc", true)]
    [InlineData("coai-*", "xcoai-abc", false)]
    [InlineData("stage-?", "stage-1", true)]
    [InlineData("stage-?", "stage-12", false)]
    [InlineData("*", "anything", true)]
    [InlineData("a*b*c", "aXXbYYc", true)]
    [InlineData("a*b*c", "aXXbYY", false)]
    public void A_pattern_matches_a_whole_name_ignoring_case(string pattern, string name, bool expected)
    {
        TempGlob.Matches(pattern, name).Should().Be(expected);
    }

    [Theory]
    [InlineData("*")]
    [InlineData("cl*")]
    [InlineData("claud?")]
    [InlineData("claude")]
    [InlineData("test*")]
    [InlineData("testsToDelete-*")]
    [InlineData("testsToDelete-a*")]
    [InlineData("?estsToDelete-?y*")]
    [InlineData("tests*")]
    [InlineData("a*")]
    [InlineData("?o-*")]
    [InlineData("*-x")]
    [InlineData("c")]
    [InlineData("a/b")]
    [InlineData(@"a\b")]
    [InlineData("")]
    public void The_key_refuses_a_pattern_that_could_select_claude_the_test_sweep_or_a_path(string pattern)
    {
        new TextRule.TempGlobName().Problem(pattern).Should().NotBeEmpty();
    }

    [Fact]
    public void The_default_allow_list_is_the_owners_decision_of_2026_10_12_and_every_member_passes_the_rule()
    {
        var defaults = TempWorld.Load("{}").TextList(ConfigKeys.Temp.AllowGlobs);

        defaults.Should().Equal("coai-*", "wsl-care-test-*", "stage-*", "mindex-*", "git-commit-*", "docrag-*", "bge-seed-*");
        defaults.Should().OnlyContain(g => TempGlob.Problem(g).Length == 0);
    }

    [Fact]
    public void The_temp_numbers_have_their_ranges_defaults_and_directions()
    {
        var config = TempWorld.Load("{}");

        (ConfigKeys.Temp.OlderThanDays.Min, ConfigKeys.Temp.OlderThanDays.Max).Should().Be((1, 365));
        ConfigKeys.Temp.OlderThanDays.Trust.Safe.Should().Be(SafeDirection.Higher);
        (config.Int(ConfigKeys.Temp.OlderThanDays), config.Int(ConfigKeys.Temp.ScanMaxNodes), config.Int(ConfigKeys.Temp.ScanMaxSeconds), config.Int(ConfigKeys.Temp.MaxItemsPerRun))
            .Should().Be((7, 200000, 120, 5000));
        new[] { ConfigKeys.Temp.ScanMaxNodes, ConfigKeys.Temp.ScanMaxSeconds, ConfigKeys.Temp.MaxItemsPerRun }
            .Should().OnlyContain(k => k.Trust.Safe == SafeDirection.Lower && !k.Trust.MachineOnly);
        config.Bool(ConfigKeys.Auto.A23).Should().BeTrue();
    }

    [Fact]
    public void A_user_layer_allow_list_that_matches_claude_makes_the_layer_invalid()
    {
        var loaded = ConfigLoader.Load(
        [
            (ConfigLoader.DefaultsFile, new FileReadResult.Content(ConfigLoader.EmbeddedDefaults())),
            (new ConfigLayerFile(ConfigLayer.User, "user.json"), new FileReadResult.Content(System.Text.Encoding.UTF8.GetBytes("""{ "temp": { "allowGlobs": ["cl*"] } }"""))),
        ]);

        loaded.IsObserveOnly.Should().BeTrue();
    }
}
