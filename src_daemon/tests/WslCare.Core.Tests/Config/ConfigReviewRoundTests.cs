using System.Text;

using FluentAssertions;

using WslCare.Core.Config;
using WslCare.Core.Files;
using WslCare.TestSupport;

namespace WslCare.Core.Tests.Config;

/// <summary>Plan §15q, the E7.S0 review round (own reviews: security, correctness) — the configuration findings.</summary>
public sealed class ConfigReviewRoundTests : IDisposable
{
    private static readonly ConfigLayerFile Machine = new(ConfigLayer.Machine, "/etc/wsl-care/config.json");
    private static readonly ConfigLayerFile User = new(ConfigLayer.User, "/home/me/.config/wsl-care/config.json");

    private readonly LinuxSandbox _sandbox = new("config-review-round");

    public void Dispose() => _sandbox.Dispose();

    private static (ConfigLayerFile, FileReadResult) Defaults() => (ConfigLoader.DefaultsFile, new FileReadResult.Content(ConfigLoader.EmbeddedDefaults()));

    private static (ConfigLayerFile, FileReadResult) Layer(ConfigLayerFile file, string json) => (file, new FileReadResult.Content(Encoding.UTF8.GetBytes(json)));

    /// <summary>S5: .NET's <c>$</c> matches before a final newline, so <c>^…$</c> took "Ubuntu\n" — the WHOLE value must match.</summary>
    [Theory]
    [InlineData("Ubuntu\n")]
    [InlineData("x\n")]
    public void A_pattern_key_refuses_a_value_with_a_trailing_newline(string value) =>
        ConfigValidation.Parse(ConfigKeys.Distro, value).Should().BeOfType<ValueCheck.Invalid>();

    /// <summary>C4: a machine-only key in the user layer is ignored with a notice BEFORE it is validated — an old, invalid value
    /// there must not make the whole run observe-only.</summary>
    [Fact]
    public void An_invalid_machine_only_value_in_the_user_layer_is_a_notice_not_an_error()
    {
        var result = ConfigLoader.Load([Defaults(), (Machine, new FileReadResult.Missing()), Layer(User, """{ "walk": { "maxEntries": "lots" } }""")]);

        result.IsObserveOnly.Should().BeFalse();
        result.Notices.Should().ContainSingle().Which.Key.Should().Be("walk.maxEntries");
    }

    /// <summary>S6: a linked <c>~/.config/wsl-care</c> must not lead root's read anywhere — no link anywhere below the home, and
    /// nothing about a file outside the user's tree (its owner, its mode) in the reason.</summary>
    [Fact]
    public void A_link_on_the_way_to_the_user_layer_is_refused_and_nothing_beyond_it_is_described()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "a link needs no privilege on Linux; run in WSL or on the Linux legs");
        var elsewhere = _sandbox.Write("/root/wsl-care/config.json", """{ "dryRun": false }""");
        var config = _sandbox.Paths.DistroPath("/home/me/.config");
        Directory.CreateDirectory(config);
        Directory.CreateSymbolicLink(Path.Combine(config, "wsl-care"), Path.GetDirectoryName(elsewhere)!);

        var result = ConfigLoader.Load(_sandbox.Paths, _sandbox.Files);

        result.Should().BeOfType<ConfigLoadResult.ObserveOnly>();
        result.Errors.Should().ContainSingle().Which.Message.Should().Contain("link").And.NotContain("mode").And.NotContain("uid");
        result.Config.Bool(ConfigKeys.DryRun).Should().BeTrue();
    }

    /// <summary>C3: every refusal of the user layer says how to fix it, not only why.</summary>
    [Fact]
    public void A_linked_user_layer_is_refused_with_how_to_fix_it()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "a link needs no privilege on Linux; run in WSL or on the Linux legs");
        var target = _sandbox.Write("/home/me/dotfiles/wsl-care.json", """{ "dryRun": false }""");
        var layer = _sandbox.Paths.DistroPath("/home/me/.config/wsl-care/config.json");
        Directory.CreateDirectory(Path.GetDirectoryName(layer)!);
        File.CreateSymbolicLink(layer, target);

        var result = ConfigLoader.Load(_sandbox.Paths, _sandbox.Files);

        result.Errors.Should().ContainSingle().Which.Message.Should().Contain("replace the link with a regular file");
    }
}
