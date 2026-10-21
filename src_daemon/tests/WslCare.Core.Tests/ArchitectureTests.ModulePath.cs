using System.Text.RegularExpressions;

using FluentAssertions;

using WslCare.Core.Health;

namespace WslCare.Core.Tests;

/// <summary>The coordinator, 2026-10-11: the daemon's interop scripts pin the PowerShell module path with the SAME statement as the
/// extension — two copies, one in C# and one in TypeScript; this check holds them equal, so one side cannot loosen the pin alone.
/// Since WR-E3's code round (coai session efc512d4) the extension's pin lives in `powershell/modulePath.ts`, shared by the elevated
/// engine and the unelevated registration (the elevated module re-exports it).</summary>
public sealed partial class ArchitectureTests
{
    [GeneratedRegex("""export const MODULES = "(?<pin>[^"]+)";""", RegexOptions.CultureInvariant)]
    private static partial Regex ExtensionModulesPin();

    [Fact]
    public void The_daemon_and_the_extension_pin_the_module_path_with_one_statement()
    {
        var root = Metadata("WslCare.SourceRoot");
        var modulePath = Path.GetFullPath(Path.Combine(root, "..", "..", "src_vs_code", "src", "powershell", "modulePath.ts"));
        File.Exists(modulePath).Should().BeTrue($"the extension's pin is read from {modulePath}");

        var match = ExtensionModulesPin().Match(File.ReadAllText(modulePath));
        match.Success.Should().BeTrue("modulePath.ts declares MODULES as one string literal");

        var extensionPin = match.Groups["pin"].Value.Replace(@"\\", @"\", StringComparison.Ordinal);
        HealthCommands.ModulePathPin.Should().Be(extensionPin, "the daemon and the extension pin the module path the same way");
    }
}
