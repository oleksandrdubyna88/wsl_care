using System.Runtime.Versioning;

using FluentAssertions;

using WslCare.FakeTool;
using WslCare.TestSupport;

namespace WslCare.Scenarios;

/// <summary>What every <c>Install*Flows</c> class checks the same way: the Linux-only skip (also the scripted clock's tests',
/// the installer's harness), an install's success or the step it failed at, an untouched prefix, and the verifying <c>gh</c>
/// calls.</summary>
[SupportedOSPlatform("linux")]
internal static class InstallChecks
{
    internal const string LinuxOnly = "install.sh and its harness (the scripted clock on its PATH) are POSIX sh over GNU coreutils and tar: covered on the Linux legs (and by hand in WSL)";

    internal static readonly string[] EnableOurUnits = ["enable", "--now", "wsl-care.timer", "wsl-care-watch.timer", "wsl-care-events.service"];

    internal static void Linux() => Assert.SkipUnless(OperatingSystem.IsLinux(), LinuxOnly);

    internal static void Succeeded(ChildResult result) => result.Exit.Should().Be(0, $"the install should succeed:\n{result.Stdout}\n{result.Stderr}");

    internal static void FailedAt(ChildResult result, string step)
    {
        result.Exit.Should().Be(1, $"the install should fail:\n{result.Stdout}\n{result.Stderr}");
        result.Stderr.Should().Contain($"FAILED at step \"{step}\"");
    }

    internal static void NothingChanged(InstallWorld world, IReadOnlyDictionary<string, string> before)
    {
        world.Tree().Should().BeEquivalentTo(before, "nothing under the prefix may change");
        world.CallsOf("systemctl").Should().BeEmpty("no unit was touched");
        world.CallsOf("apt-get").Should().BeEmpty("no package was installed");
        world.StubInvocations.Should().BeEmpty("the binary was never placed, so never run");
        Directory.EnumerateFileSystemEntries(world.Temp).Should().BeEmpty("the temporary folder is removed on every exit");
    }

    /// <summary>The <c>gh attestation verify</c> calls that verify (not the preflight's <c>--help</c>).</summary>
    internal static IReadOnlyList<FakeCall> Verifications(InstallWorld world) =>
        [.. world.CallsOf("gh").Where(c => c.Argv is ["attestation", "verify", ..] && !c.Argv.Contains("--help"))];
}
