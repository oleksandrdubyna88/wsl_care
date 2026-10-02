using System.Reflection;
using System.Reflection.Emit;

using FluentAssertions;

using WslCare.Core;

namespace WslCare.Core.Tests;

/// <summary>
/// The version a build reports — that it is the daemon's version file, and that an unstamped
/// assembly says so instead of reading as a version.
/// </summary>
public sealed class ProductVersionTests
{
    [Fact]
    public void A_daemon_assembly_is_stamped_with_the_version_in_the_daemon_version_file()
    {
        var expected = File.ReadAllText(VersionFile()).Trim();

        var version = ProductVersion.Of(typeof(ProductVersion).Assembly);

        version.IsStamped.Should().BeTrue();
        // The SDK may append "+<commit>"; anything else after the number is a different version.
        version.Text.Should().Match(
            text => text == expected || text.StartsWith(expected + "+", StringComparison.Ordinal),
            $"the assembly must carry {expected} from src_daemon/version.txt, not the SDK's 1.0.0 default");
    }

    [Fact]
    public void An_assembly_without_a_stamp_reports_unknown_rather_than_an_empty_or_zero_version()
    {
        // A dynamic assembly carries no attributes at all: the one honest example of "never stamped".
        var unstamped = AssemblyBuilder.DefineDynamicAssembly(new AssemblyName("Unstamped"), AssemblyBuilderAccess.Run);

        var version = ProductVersion.Of(unstamped);

        version.IsStamped.Should().BeFalse();
        version.Text.Should().Be(ProductVersion.Unstamped);
    }

    private static string VersionFile()
    {
        var path = typeof(ProductVersionTests).Assembly
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .Single(a => a.Key == "WslCare.VersionFile")
            .Value;
        path.Should().NotBeNullOrWhiteSpace("the test project stamps the version file's path at build time");
        File.Exists(path).Should().BeTrue($"the version file MSBuild stamped from should exist at {path}");
        return path!;
    }
}
