using System.Text;

using FluentAssertions;

using WslCare.Core.Config;
using WslCare.Core.Files;

namespace WslCare.Core.Tests.Worktrees;

/// <summary>
/// E19.S3 (PLAN_disk_hygiene_windows.md §3.9, E19 stories §3 decisions 3, 8, 11): the <c>worktrees.*</c> keys — their defaults, their
/// trust (<c>roots</c> is the USER layer's, judged at every use, like <c>archive.baseFolder</c>; the lists that loosen what a cleanup may
/// delete are the machine layer's) and the shape of a root.
/// </summary>
public sealed class WorktreeKeysTests
{
    private static readonly ConfigLayerFile User = new(ConfigLayer.User, @"C:\Users\me\AppData\Roaming\wsl-care\config.json");

    private static EffectiveConfig Defaults() => ConfigLoader.Load([(ConfigLoader.DefaultsFile, new FileReadResult.Content(ConfigLoader.EmbeddedDefaults()))]).Config;

    private static ConfigLoadResult WithUser(string json) =>
        ConfigLoader.Load([(ConfigLoader.DefaultsFile, new FileReadResult.Content(ConfigLoader.EmbeddedDefaults())), (User, new FileReadResult.Content(Encoding.UTF8.GetBytes(json)))]);

    private static ConfigValue DefaultOf(string name) => Defaults().Entry(ConfigKeys.Find(name) ?? throw new InvalidOperationException($"{name} is not a key")).Value;

    [Theory]
    [InlineData("worktrees.maxRoots", 8)]
    [InlineData("worktrees.idleDays", 3)]
    [InlineData("worktrees.artifactSkewMinutes", 10)]
    [InlineData("worktrees.scanMaxEntries", 5_000_000)]
    [InlineData("worktrees.scanMaxSeconds", 900)]
    [InlineData("worktrees.reportTop", 20)]
    [InlineData("worktrees.reportMaxAgeDays", 7)]
    [InlineData("worktrees.reclaimableWarnGib", 20)]
    public void A_worktree_number_is_a_key_with_the_value_the_plan_names(string name, int expected)
    {
        ConfigKeys.Find(name).Should().BeOfType<ConfigKey.IntKey>($"{name} is a number the plan makes a key");
        DefaultOf(name).Should().Be(new ConfigValue.Int(expected));
    }

    [Fact]
    public void The_lists_default_to_the_plans_values()
    {
        ((ConfigValue.TextList)DefaultOf("worktrees.roots")).Values.Should().BeEmpty("no user path is hard-coded");
        ((ConfigValue.TextList)DefaultOf("worktrees.wslAutomountRoots")).Values.Should().Equal("/mnt/");
        ((ConfigValue.TextList)DefaultOf("worktrees.projectFileExtensions")).Values.Should().Equal(".csproj", ".fsproj", ".vbproj", ".esproj", ".sqlproj", ".vcxproj");
        ((ConfigValue.TextList)DefaultOf("worktrees.buildArtifactPatterns")).Values.Should().Contain([".dll", ".exe", ".pdb", ".json", "none", "obj:.cs"], "the measured list (M6): extensions, extensionless files, generated sources under obj");
    }

    [Fact]
    public void The_roots_key_is_the_user_layers_and_a_bad_user_value_is_a_notice_not_an_error()
    {
        var roots = (ConfigKey.TextListKey)ConfigKeys.Find("worktrees.roots")!;

        roots.Trust.MachineOnly.Should().BeFalse("the user-run verb writes it (E19 stories §3 decision 3)");
        roots.Trust.RootEffective.Should().BeFalse();
        var loaded = WithUser("""{ "worktrees": { "roots": ["D:\\work", "relative"] } }""");
        loaded.IsObserveOnly.Should().BeFalse("a hand-edited bad root never puts the daemon observe-only; it is refused where it is used");
        loaded.Notices.Should().ContainSingle(n => n.Key == "worktrees.roots");
        loaded.Config.Entry(roots).Value.Should().Be(new ConfigValue.TextList([]), "the invalid value is not taken");
    }

    [Fact]
    public void A_valid_user_root_list_is_taken()
    {
        var loaded = WithUser("""{ "worktrees": { "roots": ["D:\\work", "E:\\repos\\team"] } }""");

        loaded.Errors.Should().BeEmpty();
        loaded.Config.Entry(ConfigKeys.Find("worktrees.roots")!).Value.Should().Be(new ConfigValue.TextList([@"D:\work", @"E:\repos\team"]));
    }

    [Theory]
    [InlineData("worktrees.maxRoots")]
    [InlineData("worktrees.buildArtifactPatterns")]
    [InlineData("worktrees.wslAutomountRoots")]
    public void A_key_that_widens_what_a_cleanup_may_delete_is_the_machine_layers(string name)
    {
        ConfigKeys.Find(name)!.Trust.MachineOnly.Should().BeTrue($"{name} loosens what is deleted");
    }

    [Fact]
    public void The_idle_threshold_and_the_budgets_only_tighten_from_the_user_layer()
    {
        ConfigKeys.Find("worktrees.idleDays")!.Trust.Safe.Should().Be(SafeDirection.Higher, "a shorter idle loosens what is deleted");
        ConfigKeys.Find("worktrees.artifactSkewMinutes")!.Trust.Safe.Should().Be(SafeDirection.Lower, "a larger skew admits more files as artifacts");
        ConfigKeys.Find("worktrees.scanMaxEntries")!.Trust.Safe.Should().Be(SafeDirection.Lower);
        ConfigKeys.Find("worktrees.scanMaxSeconds")!.Trust.Safe.Should().Be(SafeDirection.Lower);
        ConfigKeys.Find("worktrees.projectFileExtensions")!.Trust.Safe.Should().Be(SafeDirection.Subset, "a new extension widens the rule");
    }

    [Fact]
    public void The_project_extensions_are_a_closed_set()
    {
        var key = (ConfigKey.TextListKey)ConfigKeys.Find("worktrees.projectFileExtensions")!;

        key.Allowed.Should().Equal(".csproj", ".fsproj", ".vbproj", ".esproj", ".sqlproj", ".vcxproj");
    }

    [Theory]
    [InlineData(@"D:\work", true)]
    [InlineData(@"d:\work\sub dir\x", true)]
    [InlineData(@"D:\", true)]
    [InlineData("", false)]
    [InlineData("work", false)]
    [InlineData("D:work", false)]
    [InlineData("/mnt/d/work", false)]
    [InlineData("/work", false)]
    [InlineData(@"\\server\share\work", false)]
    [InlineData(@"\\?\D:\work", false)]
    [InlineData(@"\\.\D:\work", false)]
    [InlineData(@"D:\work\..\x", false)]
    [InlineData(@"D:\work\.\x", false)]
    [InlineData(@"D:\work\\x", false)]
    [InlineData("D:\\work\u0007x", false)]
    [InlineData("D:\\work\nx", false)]
    public void A_root_member_is_a_drive_letter_path_and_nothing_else(string value, bool accepted)
    {
        var rule = new TextRule.WindowsLocalPath();

        (rule.Problem(value).Length == 0).Should().Be(accepted, rule.Problem(value));
    }

    [Fact]
    public void A_root_member_longer_than_the_path_key_bound_is_refused()
    {
        var rule = new TextRule.WindowsLocalPath();
        var long1025 = @"D:\" + new string('a', TextRule.AbsolutePathOrEmpty.MaxLength);

        rule.Problem(long1025).Should().NotBeEmpty();
        rule.Problem(long1025[..TextRule.AbsolutePathOrEmpty.MaxLength]).Should().BeEmpty();
    }
}
