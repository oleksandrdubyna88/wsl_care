using System.Runtime.Versioning;

using FluentAssertions;

using WslCare.TestSupport;

using static WslCare.Scenarios.ReleaseExtensionCheckout;

namespace WslCare.Scenarios;

/// <summary>
/// The guard's package.json refusal is PRINTED. release-extension-guard.sh reads the top-level "version" and "publisher" of
/// src_vs_code/package.json through <c>manifest_field</c>, which refuses when a key is missing or appears twice. Until
/// 2026-10-08 the function ran inside a command substitution (<c>recorded="$(manifest_field version)"</c>), so the refusal's
/// <c>::error::</c> line became the variable's value and was thrown away, and the guard exited 1 with nothing in the log.
/// Each flow is asserted by exit code AND by the refusal text on stdout, where the runner reads workflow commands.
/// </summary>
[SupportedOSPlatform("linux")]
public sealed class ReleaseExtensionManifestFlows
{
    private static void Linux() => Assert.SkipUnless(OperatingSystem.IsLinux(), ReleaseScripts.LinuxOnly);

    /// <summary>A package.json the guard must refuse, by the shape it is broken in, and the key it must name.</summary>
    private static (string Json, string Key) Broken(string shape) => shape switch
    {
        "no-version" => ($"{{\n  \"name\": \"{ReleaseFiles.ExtensionName}\",\n  \"publisher\": \"wsl-care-dev\",\n  \"scripts\": {{\n    \"version\": \"not-the-top-level-one\"\n  }}\n}}\n", "version"),
        "no-publisher" => ($"{{\n  \"name\": \"{ReleaseFiles.ExtensionName}\",\n  \"version\": \"0.1.0\"\n}}\n", "publisher"),
        "two-publishers" => ($"{{\n  \"name\": \"{ReleaseFiles.ExtensionName}\",\n  \"version\": \"0.1.0\",\n  \"publisher\": \"wsl-care-dev\",\n  \"publisher\": \"another\"\n}}\n", "publisher"),
        _ => throw new ArgumentOutOfRangeException(nameof(shape), shape, "known shapes: no-version, no-publisher, two-publishers"),
    };

    /// <summary>The positive beside the refusals, on the same fixture: a well-formed package.json is read, and both values
    /// reach the outputs — so a refusal below is about the broken key, never about the fixture.</summary>
    [Fact]
    public async Task A_well_formed_package_json_is_read_and_both_values_reach_the_outputs()
    {
        Linux();
        using var root = new TempRoot("ext-guard-manifest-ok");
        var checkout = Make(root, version: "0.1.0", publisher: "wsl-care-dev");

        var result = await ReleaseScripts.RunAsync("release-extension-guard.sh", ["extension-v0.1.0"], checkout.Dir, checkout.Env);

        result.Exit.Should().Be(0, result.Stdout + result.Stderr);
        result.StdoutLines.Should().Contain("version=0.1.0").And.Contain("publisher=wsl-care-dev");
    }

    [Theory]
    [InlineData("no-version")]
    [InlineData("no-publisher")]
    [InlineData("two-publishers")]
    public async Task A_package_json_without_a_single_top_level_key_is_refused_with_its_message_printed(string shape)
    {
        Linux();
        using var root = new TempRoot("ext-guard-manifest");
        var checkout = Make(root);
        var (json, key) = Broken(shape);
        await File.WriteAllTextAsync(Path.Combine(checkout.Dir, "src_vs_code", "package.json"), json, TestContext.Current.CancellationToken);

        var result = await ReleaseScripts.RunAsync("release-extension-guard.sh", ["extension-v0.1.0"], checkout.Dir, checkout.Env);

        result.Exit.Should().Be(1, $"{shape}: {result.Stdout}{result.Stderr}");
        result.Stdout.Should().Contain($"::error::extension release guard: src_vs_code/package.json carries no single top-level \"{key}\"", $"{shape}: the refusal names its cause in the log (stderr: '{result.Stderr}')");
        File.Exists(checkout.GhLog).Should().BeFalse("refused before GitHub is asked");
    }
}
