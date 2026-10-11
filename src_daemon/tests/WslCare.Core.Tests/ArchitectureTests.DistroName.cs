using System.Text.RegularExpressions;

using FluentAssertions;

using WslCare.Core.Health;

namespace WslCare.Core.Tests;

/// <summary>E15.S2's code round (2026-10-11): the daemon believes a recorded distro name only when it matches WSL's name rule, and the
/// extension refuses a <c>wslCare.distro</c> setting by the same rule — two copies, one in C# and one in TypeScript. This check holds
/// their bodies equal, so a character one side starts to allow cannot leave the other side refusing it.</summary>
public sealed partial class ArchitectureTests
{
    [GeneratedRegex(@"export const DISTRO_NAME = /\^(?<body>.+)\$/;", RegexOptions.CultureInvariant)]
    private static partial Regex ExtensionDistroName();

    [Fact]
    public void The_daemon_and_the_extension_share_one_distro_name_rule()
    {
        var root = Metadata("WslCare.SourceRoot");
        var distros = Path.GetFullPath(Path.Combine(root, "..", "..", "src_vs_code", "src", "wsl", "distros.ts"));
        File.Exists(distros).Should().BeTrue($"the extension's rule is read from {distros}");

        var match = ExtensionDistroName().Match(File.ReadAllText(distros));
        match.Success.Should().BeTrue("distros.ts still declares DISTRO_NAME as one anchored regex literal");

        var daemonBody = DistroIdentity.NamePatternText[2..^2];
        DistroIdentity.NamePatternText.Should().StartWith(@"\A").And.EndWith(@"\z");
        daemonBody.Should().Be(match.Groups["body"].Value, "the daemon and the extension must accept exactly the same distro names");
    }
}
