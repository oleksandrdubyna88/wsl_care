using FluentAssertions;

using WslCare.Core.Processes;
using WslCare.TestSupport;

namespace WslCare.Scenarios;

/// <summary>
/// The extension package's file name in its three languages (E7.S5a, research/PLAN_bundle_windows_binary.md §3.3; the coai plan
/// round 52122128): the POSIX <c>vsix_name</c> of .github/scripts/lib/extension-package.sh (the workflow, the asset check,
/// POST_DEPLOY item 6), the C# <see cref="ReleaseFiles.ExtensionVsix"/> (these tests) and check-vsix.mjs's default name
/// (<c>--print-default-name</c>, the build's own check) — asked for the SAME version, they must give the SAME name, and it is
/// vsce's targeted form <c>&lt;name&gt;-&lt;target&gt;-&lt;version&gt;.vsix</c> with the manifest's own name.
/// </summary>
public sealed class ExtensionPackageNameTests
{
    private const string Version = "1.2.3";

    private static string PackageLib => ReleaseFiles.Script(Path.Combine("lib", "extension-package.sh")).Replace('\\', '/');

    private static async Task<string> PosixAsync(string function, string version)
    {
        var result = await ChildProcess.RunAsync(ReleaseScripts.Bash, ["-c", $". \"$1\" && {function} \"$2\"", "name", PackageLib, version], new Dictionary<string, string?>());
        result.Exit.Should().Be(0, result.Stderr);
        return result.Stdout.Trim();
    }

    private static async Task<string> NodeAsync(string version)
    {
        var node = ExecutableResolver.Resolve("node") is ResolvedExecutable.Found found ? found.Path : string.Empty;
        Assert.SkipWhen(node.Length == 0, "no node on PATH here; every CI image carries one");
        var script = Path.Combine(ReleaseFiles.Root, "src_vs_code", "scripts", "check-vsix.mjs");
        var result = await ChildProcess.RunAsync(node, [script, "--print-default-name", version], new Dictionary<string, string?>());
        result.Exit.Should().Be(0, result.Stdout + result.Stderr);
        return result.Stdout.Trim();
    }

    [Fact]
    public async Task The_three_implementations_name_the_same_targeted_package_for_the_same_version()
    {
        Assert.SkipWhen(ReleaseScripts.Bash.Length == 0, ReleaseScripts.NoBash);

        var posix = await PosixAsync("vsix_name", Version);
        var node = await NodeAsync(Version);
        var csharp = ReleaseFiles.ExtensionVsix(Version);

        posix.Should().Be(csharp, "the workflow and these tests name one file");
        node.Should().Be(csharp, "the build's own check names it the same");
        csharp.Should().Be($"{ReleaseFiles.ExtensionName}-win32-x64-{Version}.vsix", "vsce 4.0.0's targeted name: <name>-<target>-<version>.vsix");
    }

    [Fact]
    public async Task The_POSIX_name_is_the_manifests_own_and_the_universal_name_is_named_apart()
    {
        Assert.SkipWhen(ReleaseScripts.Bash.Length == 0, ReleaseScripts.NoBash);

        (await PosixAsync("vsix_universal_name", Version)).Should().Be($"{ReleaseFiles.ExtensionName}-{Version}.vsix");
        File.ReadAllText(ReleaseFiles.Script(Path.Combine("lib", "extension-package.sh"))).Should().Contain($"EXTENSION_NAME='{ReleaseFiles.ExtensionName}'", "the lib's name is src_vs_code/package.json's");
    }
}
