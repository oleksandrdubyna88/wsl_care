using System.Text;

using FluentAssertions;

using WslCare.Core.Config;
using WslCare.Core.Files;
using WslCare.Core.Worktrees;

namespace WslCare.Core.Tests.Config;

/// <summary>E19.S4: the four <c>worktrees.git*</c> keys — ranges, defaults in the embedded <c>default.json</c>, and who may set the one that
/// names a program to run.</summary>
public sealed class WorktreeGitKeysTests
{
    private static ConfigLoadResult Load(string machine = "{}", string user = "{}") => ConfigLoader.Load(
    [
        (ConfigLoader.DefaultsFile, new FileReadResult.Content(ConfigLoader.EmbeddedDefaults())),
        (new ConfigLayerFile(ConfigLayer.Machine, "machine.json"), new FileReadResult.Content(Encoding.UTF8.GetBytes(machine))),
        (new ConfigLayerFile(ConfigLayer.User, "user.json"), new FileReadResult.Content(Encoding.UTF8.GetBytes(user))),
    ]);

    [Theory]
    [InlineData("worktrees.gitTimeoutSeconds", 5, 600, 30)]
    [InlineData("worktrees.gitNamesPerCall", 1, 256, 64)]
    [InlineData("worktrees.gitCommandLineMaxChars", 1000, 30000, 7000)]
    public void A_git_number_is_an_ordinary_key_with_its_range_and_default(string name, int min, int max, int fallback)
    {
        var key = ConfigKeys.Find(name);

        key.Should().BeOfType<ConfigKey.IntKey>();
        var number = (ConfigKey.IntKey)key!;
        (number.Min, number.Max).Should().Be((min, max));
        number.Trust.MachineOnly.Should().BeFalse();
        Load().Config.Int(number).Should().Be(fallback);
    }

    [Fact]
    public void The_git_path_is_a_machine_only_absolute_path_that_defaults_to_the_supported_install_only()
    {
        ConfigKeys.Worktrees.GitPath.Trust.MachineOnly.Should().BeTrue("an administrator names a program to run");
        ConfigKeys.Worktrees.GitPath.Rule.Should().BeOfType<TextRule.AbsolutePathOrEmpty>();
        Load().Config.Text(ConfigKeys.Worktrees.GitPath).Should().BeEmpty();
    }

    [Fact]
    public void A_git_path_in_the_machine_layer_is_taken_and_one_in_the_user_layer_is_not()
    {
        var machine = Load(machine: """{ "worktrees": { "gitPath": "D:\\tools\\git\\cmd\\git.exe" } }""");
        var user = Load(user: """{ "worktrees": { "gitPath": "D:\\evil\\git.exe" } }""");

        machine.Config.Text(ConfigKeys.Worktrees.GitPath).Should().Be(@"D:\tools\git\cmd\git.exe");
        user.Config.Text(ConfigKeys.Worktrees.GitPath).Should().BeEmpty("a user layer may not name a program the scan runs");
        user.Notices.Should().Contain(n => n.Key == "worktrees.gitPath");
    }

    [Theory]
    [InlineData(@"relative\git.exe")]
    [InlineData(@"..\git.exe")]
    [InlineData(@"D:\tools\..\git.exe")]
    public void A_git_path_that_is_not_an_absolute_path_is_refused(string path)
    {
        var json = "{ \"worktrees\": { \"gitPath\": \"" + path.Replace("\\", "\\\\", StringComparison.Ordinal) + "\" } }";

        Load(machine: json).IsObserveOnly.Should().BeTrue();
    }

    [Fact]
    public void The_limits_the_questions_read_are_the_configuration_in_force()
    {
        var loaded = Load(machine: """{ }""", user: """{ "worktrees": { "gitTimeoutSeconds": 45, "gitNamesPerCall": 10, "gitCommandLineMaxChars": 2000 } }""");

        GitLimits.From(loaded.Config).Should().Be(new GitLimits(TimeSpan.FromSeconds(45), 10, 2000));
        GitLimits.From(Load().Config).Should().Be(new GitLimits(TimeSpan.FromSeconds(30), 64, 7000));
    }
}
