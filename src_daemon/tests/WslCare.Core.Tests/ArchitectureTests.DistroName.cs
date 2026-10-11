using System.Text.RegularExpressions;

using FluentAssertions;

using WslCare.Core.Health;

namespace WslCare.Core.Tests;

/// <summary>E15.S2's code round (2026-10-11): the daemon believes a recorded distro name only when it matches WSL's name rule, and the
/// extension refuses a <c>wslCare.distro</c> setting by the same rule — two copies, one in C# and one in TypeScript. This check holds
/// their bodies equal, so a character one side starts to allow cannot leave the other side refusing it. E15.S2b adds the third copy:
/// <c>install.sh --distro-name</c> refuses a name by the same rule before it hands the name to the first full run.</summary>
public sealed partial class ArchitectureTests
{
    [GeneratedRegex(@"export const DISTRO_NAME = /\^(?<body>.+)\$/;", RegexOptions.CultureInvariant)]
    private static partial Regex ExtensionDistroName();

    /// <summary>install.sh's <c>readonly DISTRO_NAME_PATTERN='^…$'</c>: an extended regular expression, at column 0.</summary>
    [GeneratedRegex(@"^readonly DISTRO_NAME_PATTERN='\^(?<body>[^']+)\$'$", RegexOptions.CultureInvariant | RegexOptions.Multiline)]
    private static partial Regex InstallerDistroName();

    [Fact]
    public void The_daemon_and_the_extension_share_one_distro_name_rule()
    {
        var root = Metadata("WslCare.SourceRoot");
        var distros = Path.GetFullPath(Path.Combine(root, "..", "..", "src_vs_code", "src", "wsl", "distros.ts"));
        File.Exists(distros).Should().BeTrue($"the extension's rule is read from {distros}");

        var match = ExtensionDistroName().Match(File.ReadAllText(distros));
        match.Success.Should().BeTrue("distros.ts still declares DISTRO_NAME as one anchored regex literal");

        DistroIdentity.NamePatternText.Should().StartWith(@"\A").And.EndWith(@"\z");
        DaemonBody().Should().Be(match.Groups["body"].Value, "the daemon and the extension must accept exactly the same distro names");
    }

    /// <summary>The body is compared as text: the same characters in ERE and in .NET mean the same thing here, because the
    /// installer's <c>matches</c> refuses every byte outside <c>[0-9A-Za-z._-]</c> before grep reads the value (a newline included),
    /// so <c>^…$</c> anchors one line exactly as <c>\A…\z</c> anchors the string. The Linux scenarios run the same names through
    /// both (<c>InstallDistroNameFlows</c>).</summary>
    [Fact]
    public void The_installer_and_the_daemon_share_one_distro_name_rule()
    {
        var root = Metadata("WslCare.SourceRoot");
        var installer = Path.GetFullPath(Path.Combine(root, "..", "..", "install.sh"));
        File.Exists(installer).Should().BeTrue($"the installer's rule is read from {installer}");

        var match = InstallerDistroName().Match(File.ReadAllText(installer).ReplaceLineEndings("\n"));
        match.Success.Should().BeTrue("install.sh declares DISTRO_NAME_PATTERN as one anchored extended regular expression");

        DaemonBody().Should().Be(match.Groups["body"].Value, "install.sh --distro-name and the daemon must accept exactly the same distro names");
    }

    private static string DaemonBody() => DistroIdentity.NamePatternText[2..^2];
}
