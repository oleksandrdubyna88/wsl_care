using FluentAssertions;

using WslCare.Core.Collectors;
using WslCare.Core.Health;
using WslCare.Core.Processes;
using WslCare.Core.Records;
using WslCare.TestSupport;

namespace WslCare.Core.Tests.Health;

/// <summary>
/// E15.S1b, story S2 (PLAN_disk_health.md, <i>E15.S1b design</i> §2): the PURE judgement — the MSI PowerShell 7 ONLY when the
/// known folder is exactly <c>C:\Program Files</c>, the environment agrees, the item is a real file and it is signed by
/// Microsoft; everything else, a failed probe and a probe from before the tags give Windows PowerShell 5.1 with the reason.
/// Never the Store package or the alias.
/// </summary>
public sealed class PwshEngineTests
{
    private const string ProgramFiles = @"C:\Program Files";

    private static readonly DateTimeOffset Now = PwshEngineFixture.CapturedAt;

    private static WindowsClockSample Sample(PwshProbe probe) => new(Now, 0.1, 0.5, string.Empty) { Pwsh = Reading.Of(probe) };

    private static WindowsClockSample Sample(string knownFolder, string env, string msi, string signature) => Sample(new PwshProbe(knownFolder, env, msi, signature));

    private static PwshProbe Verified => new(ProgramFiles, ProgramFiles, "file", "signed");

    [Theory]
    [InlineData(ProgramFiles, @"D:\Evil", "file", "signed", @"D:\Evil")]
    [InlineData(@"D:\PF", @"D:\PF", "file", "signed", @"D:\PF")]
    [InlineData(@"C:\Program Files (x86)", @"C:\Program Files (x86)", "file", "signed", "(x86)")]
    [InlineData(@"c:\program files", @"c:\program files", "file", "signed", @"c:\program files")]
    [InlineData("", "", "", "", "known folder")]
    [InlineData(ProgramFiles, ProgramFiles, "absent", "", "absent")]
    [InlineData(ProgramFiles, ProgramFiles, "link", "", "link")]
    [InlineData(ProgramFiles, ProgramFiles, "empty", "", "empty")]
    [InlineData(ProgramFiles, ProgramFiles, "directory", "", "directory")]
    [InlineData(ProgramFiles, ProgramFiles, "error:ItemNotFoundException", "", "error:ItemNotFoundException")]
    [InlineData(ProgramFiles, ProgramFiles, "", "", "pwsh.msi")]
    [InlineData(ProgramFiles, ProgramFiles, "file", "unsigned:NotSigned|", "NotSigned")]
    [InlineData(ProgramFiles, ProgramFiles, "file", "unsigned:HashMismatch|CN=Microsoft Corporation, O=Microsoft Corporation", "HashMismatch")]
    [InlineData(ProgramFiles, ProgramFiles, "file", "unsigned:Valid|CN=Contoso Ltd, O=Contoso", "Contoso")]
    [InlineData(ProgramFiles, ProgramFiles, "file", "", "signature")]
    public void Anything_short_of_a_verified_msi_pwsh_is_windows_powershell_with_the_reason(string knownFolder, string env, string msi, string signature, string reason)
    {
        var engine = PwshEngine.Choose(Sample(knownFolder, env, msi, signature), "/mnt/");

        engine.Kind.Should().Be(InteropEngineKind.WindowsPowerShell);
        engine.Program.Should().Be(HealthCommands.PowerShell);
        engine.Reason.Should().Contain(reason);
    }

    [Fact]
    public void Everything_verified_gives_the_msi_pwsh_at_its_path_under_the_automount_root()
    {
        var engine = PwshEngine.Choose(Sample(Verified), "/mnt/");

        engine.Kind.Should().Be(InteropEngineKind.Msi);
        engine.Program.Should().Be("/mnt/c/Program Files/PowerShell/7/pwsh.exe");
        engine.Reason.Should().Contain("verified").And.Contain("CN=Microsoft Corporation").And.Contain(PwshEngine.MsiFile);
        PwshEngine.Choose(Sample(Verified), "/win/").Program.Should().Be("/win/c/Program Files/PowerShell/7/pwsh.exe", "the automount root of /etc/wsl.conf, as the profile's");
    }

    [Fact]
    public void The_environment_may_differ_in_case_only()
    {
        PwshEngine.Choose(Sample(ProgramFiles, @"c:\PROGRAM FILES", "file", "signed"), "/mnt/").Kind.Should().Be(InteropEngineKind.Msi, "Windows compares folder names without case");
        PwshEngine.Choose(Sample(ProgramFiles, @"C:\Program Files\", "file", "signed"), "/mnt/").Kind.Should().Be(InteropEngineKind.WindowsPowerShell, "a trailing separator is another spelling, and the environment must name the SAME folder");
    }

    [Fact]
    public void A_failed_probe_and_a_probe_from_before_the_tags_give_windows_powershell_saying_the_probe_did_not_verify_pwsh()
    {
        var failed = PwshEngine.Choose(new WindowsClockSample(Now, 0, 0, "powershell.exe could not be started: no interop"), "/mnt/");
        var older = PwshEngine.Choose(new WindowsClockSample(Now, 0.1, 0.5, string.Empty), "/mnt/");

        failed.Kind.Should().Be(InteropEngineKind.WindowsPowerShell);
        failed.Reason.Should().Contain("the clock probe did not verify pwsh").And.Contain("no interop");
        older.Kind.Should().Be(InteropEngineKind.WindowsPowerShell);
        older.Reason.Should().Contain("the clock probe did not verify pwsh").And.Contain(WindowsClockAnswer.NoPwshLine);
    }

    [Fact]
    public void The_captured_probe_answer_gives_windows_powershell_and_the_derived_verified_one_the_msi_pwsh()
    {
        var captured = HealthParsers.WindowsClock(PwshEngineFixture.Answer).Should().BeOfType<Reading<WindowsClockAnswer>.Available>().Which.Value;
        var verified = HealthParsers.WindowsClock(PwshEngineFixture.Verified).Should().BeOfType<Reading<WindowsClockAnswer>.Available>().Which.Value;

        PwshEngine.Choose(new WindowsClockSample(Now, 0.1, 0.5, string.Empty) { Pwsh = captured.Pwsh }, "/mnt/").Should()
            .Match<InteropEngine>(e => e.Kind == InteropEngineKind.WindowsPowerShell && e.Reason.Contains("absent"), "the owner's machine has no MSI pwsh");
        PwshEngine.Choose(new WindowsClockSample(Now, 0.1, 0.5, string.Empty) { Pwsh = verified.Pwsh }, "/mnt/").Kind.Should().Be(InteropEngineKind.Msi);
    }

    [Fact]
    public void Never_the_store_package_or_the_alias_and_the_path_is_the_resolvers_own_folder()
    {
        var store = Sample(ProgramFiles, ProgramFiles, "file", "signed");
        var engine = PwshEngine.Choose(store, "/mnt/");

        engine.Program.Should().NotContainAny("WindowsApps", "AppData", "8wekyb3d8bbwe");
        PwshEngine.MsiFile.Should().Be(@"C:\Program Files\PowerShell\7\pwsh.exe");
        engine.Program.Should().Be("/mnt/c/" + WindowsSystemDrive.Programs[HealthCommands.Pwsh] + "/" + HealthCommands.Pwsh,
            "the engine names the very file the resolver's system-drive fallback starts for pwsh.exe");
    }
}
