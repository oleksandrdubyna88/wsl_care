using FluentAssertions;

using WslCare.Core.Health;

namespace WslCare.Core.Tests.Health;

/// <summary>
/// The coordinator, 2026-10-11 (after the #104 Fable review: a user-level process can shadow a cmdlet through
/// <c>Documents\…\Modules</c>): every fixed interop script the root daemon starts pins <c>$env:PSModulePath</c> to
/// <c>$PSHOME\Modules</c> as its FIRST statement, before any cmdlet can autoload a module — the same rule as the extension's
/// elevated engine (<c>MODULES</c> in <c>src_vs_code/src/powershell/elevatedEngine.ts</c>).
/// </summary>
public sealed class InteropModulePathTests
{
    public static TheoryData<string> Scripts() => new(HealthCommands.FixedScripts.Select(c => c.Name));

    [Theory]
    [MemberData(nameof(Scripts))]
    public void Every_fixed_interop_script_pins_the_module_path_before_anything_else(string name)
    {
        var script = HealthCommands.FixedScripts.Single(c => c.Name == name).Argv[^1];

        script.Should().StartWith(HealthCommands.ModulePathPin + "; ", "no cmdlet may run, and so autoload a module, before the module path is pinned");
    }

    [Fact]
    public void The_pin_assigns_the_administrator_only_module_folder_and_nothing_else()
    {
        HealthCommands.ModulePathPin.Should().Be("$env:PSModulePath = $PSHOME + '\\Modules'");
        HealthCommands.ModulePathPin.Should().NotContain("\"", "the disk script carries no double quote");
    }
}
