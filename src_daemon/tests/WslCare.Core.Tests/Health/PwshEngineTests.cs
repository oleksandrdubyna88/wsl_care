using FluentAssertions;

using WslCare.Core.Collectors;
using WslCare.Core.Config;
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
        var engine = PwshEngine.Choose(Sample(knownFolder, env, msi, signature));

        engine.Kind.Should().Be(InteropEngineKind.WindowsPowerShell);
        engine.Program.Should().Be(HealthCommands.PowerShell);
        engine.Reason.Should().Contain(reason);
    }

    [Fact]
    public void Everything_verified_gives_the_msi_pwsh_by_its_bare_name_which_only_its_fixed_folder_answers()
    {
        var engine = PwshEngine.Choose(Sample(Verified));

        engine.Kind.Should().Be(InteropEngineKind.Msi);
        engine.Program.Should().Be(HealthCommands.Pwsh, "the policy judges the bare name; the resolver answers it from the fixed folder only");
        engine.Reason.Should().Contain("verified").And.Contain("CN=Microsoft Corporation").And.Contain(PwshEngine.MsiFile);
    }

    [Fact]
    public void The_environment_may_differ_in_case_only()
    {
        PwshEngine.Choose(Sample(ProgramFiles, @"c:\PROGRAM FILES", "file", "signed")).Kind.Should().Be(InteropEngineKind.Msi, "Windows compares folder names without case");
        PwshEngine.Choose(Sample(ProgramFiles, @"C:\Program Files\", "file", "signed")).Kind.Should().Be(InteropEngineKind.WindowsPowerShell, "a trailing separator is another spelling, and the environment must name the SAME folder");
    }

    [Fact]
    public void A_failed_probe_and_a_probe_from_before_the_tags_give_windows_powershell_saying_the_probe_did_not_verify_pwsh()
    {
        var failed = PwshEngine.Choose(new WindowsClockSample(Now, 0, 0, "powershell.exe could not be started: no interop"));
        var older = PwshEngine.Choose(new WindowsClockSample(Now, 0.1, 0.5, string.Empty));

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

        PwshEngine.Choose(new WindowsClockSample(Now, 0.1, 0.5, string.Empty) { Pwsh = captured.Pwsh }).Should()
            .Match<InteropEngine>(e => e.Kind == InteropEngineKind.WindowsPowerShell && e.Reason.Contains("absent"), "the owner's machine has no MSI pwsh");
        PwshEngine.Choose(new WindowsClockSample(Now, 0.1, 0.5, string.Empty) { Pwsh = verified.Pwsh }).Kind.Should().Be(InteropEngineKind.Msi);
    }

    /// <summary>Code round: the program is DERIVED from the kind, and the msi program is a name the resolver takes from its fixed
    /// folder ONLY — never from PATH, where the Store package's alias lives (<c>PwshFixedFolderTests</c> holds the
    /// resolver's half).</summary>
    [Fact]
    public void The_program_follows_the_kind_and_the_msi_one_is_never_looked_up_on_path()
    {
        var msi = PwshEngine.Choose(Sample(Verified));
        var windows = PwshEngine.Choose(Sample(ProgramFiles, ProgramFiles, "absent", string.Empty));

        WindowsSystemDrive.FixedFolderOnly.Should().Contain(msi.Program, "the verified program is started from its fixed folder only");
        WindowsSystemDrive.Programs[msi.Program].Should().Be("Program Files/PowerShell/7");
        WindowsSystemDrive.FixedFolderOnly.Should().NotContain(windows.Program, "Windows PowerShell 5.1 keeps PATH first");
        new InteropEngine(InteropEngineKind.Msi, "x").Program.Should().Be(HealthCommands.Pwsh);
        new InteropEngine(InteropEngineKind.WindowsPowerShell, "x").Program.Should().Be(HealthCommands.PowerShell);
    }

    /// <summary>Code round: the probe's values are environment-controlled; a reason quotes them cut to
    /// <c>records.maxReasonChars</c>, as <c>ClockParsers</c> cuts a quoted answer.</summary>
    [Fact]
    public void A_hostile_value_is_quoted_cut_to_the_reason_limit()
    {
        var huge = "D:\\" + new string('x', 5000);

        var engine = PwshEngine.Choose(Sample(huge, huge, "file", "signed"));

        engine.Kind.Should().Be(InteropEngineKind.WindowsPowerShell);
        engine.Reason.Length.Should().BeLessThan(2 * Tuning.Current.Int(ConfigKeys.Records.MaxReasonChars) + 300, "each quoted value is cut");
        engine.Reason.Should().Contain("...");
    }
}
