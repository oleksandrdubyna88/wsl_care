using System.Text.RegularExpressions;

using FluentAssertions;

using WslCare.Core.Health;

namespace WslCare.Core.Tests;

/// <summary>The coordinator, 2026-10-11: the daemon's interop scripts pin the PowerShell module path with the SAME statement as the
/// extension's elevated engine — two copies, one in C# and one in TypeScript; this check holds them equal, so one side cannot
/// loosen the pin alone.</summary>
public sealed partial class ArchitectureTests
{
    [GeneratedRegex("""export const MODULES = "(?<pin>[^"]+)";""", RegexOptions.CultureInvariant)]
    private static partial Regex ExtensionModulesPin();

    [Fact]
    public void The_daemon_and_the_extension_pin_the_module_path_with_one_statement()
    {
        var root = Metadata("WslCare.SourceRoot");
        var engine = Path.GetFullPath(Path.Combine(root, "..", "..", "src_vs_code", "src", "powershell", "elevatedEngine.ts"));
        File.Exists(engine).Should().BeTrue($"the extension's pin is read from {engine}");

        var match = ExtensionModulesPin().Match(File.ReadAllText(engine));
        match.Success.Should().BeTrue("elevatedEngine.ts still declares MODULES as one string literal");

        var extensionPin = match.Groups["pin"].Value.Replace(@"\\", @"\", StringComparison.Ordinal);
        HealthCommands.ModulePathPin.Should().Be(extensionPin, "the daemon and the extension pin the module path the same way");
    }
}
