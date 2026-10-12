using FluentAssertions;

using WslCare.Core.Collectors;
using WslCare.Core.Health;
using WslCare.Core.Processes;
using WslCare.Core.Processes.Policy;
using WslCare.TestSupport;

namespace WslCare.Core.Tests.Health;

/// <summary>
/// E15.S1b, story S1 (PLAN_disk_health.md, <i>E15.S1b design</i> §1): the clock probe — Windows PowerShell 5.1, the trusted
/// System32 path — verifies the MSI PowerShell 7 and prints what it saw as four TAGGED lines AFTER its two instants and the
/// Windows Time lines, so the launch latency it measures is unchanged; the parser reads them by tag, never by position, over
/// the answer captured on 2026-10-12 (<see cref="PwshEngineFixture"/>).
/// </summary>
public sealed class PwshProbeTests
{
    private static T Value<T>(Reading<T> reading)
    {
        reading.Should().BeOfType<Reading<T>.Available>(reading.ReasonOrEmpty);
        return ((Reading<T>.Available)reading).Value;
    }

    [Fact]
    public void The_captured_answer_reads_the_four_pwsh_tags_after_the_instants_the_profile_and_the_service()
    {
        var probe = Value(HealthParsers.WindowsClock(PwshEngineFixture.Answer));

        probe.Profile.Should().Be(@"C:\Users\user");
        (probe.PrintedAt - probe.ProcessStartedAt).TotalSeconds.Should().BeApproximately(3.4786, 0.0001, "the latency is the two instants' difference, the look adds nothing to it");
        Value(probe.TimeService).Should().Be(new WindowsTimeService("Running", "Automatic"));
        Value(probe.Pwsh).Should().Be(new PwshProbe(@"C:\Program Files", @"C:\Program Files", "absent", string.Empty), "the owner's machine has no MSI pwsh (SOURCE.txt)");
    }

    [Fact]
    public void An_answer_from_before_the_tags_says_the_probe_printed_no_pwsh_line()
    {
        var probe = Value(HealthParsers.WindowsClock(TimeGuardFixture.Read("powershell-clock.out")));

        probe.Pwsh.IsAvailable.Should().BeFalse("the 2026-10-08 probe knew five lines");
        probe.Pwsh.ReasonOrEmpty.Should().Be(WindowsClockAnswer.NoPwshLine);
        Value(probe.TimeService).Should().Be(new WindowsTimeService("Running", "Manual"), "the older tags still read");
    }

    [Fact]
    public void The_pwsh_tags_never_shift_the_three_positional_lines()
    {
        const string twoInstants = "2026-10-12T07:58:47Z\n2026-10-12T07:58:44Z\n";

        HealthParsers.WindowsClock(twoInstants + "\npwsh.knownFolder=C:\\Program Files\npwsh.envProgramFiles=C:\\Program Files\npwsh.msi=absent\npwsh.signature=\n")
            .IsAvailable.Should().BeFalse("two instants and no profile: a pwsh tag is not a profile");
        Value(HealthParsers.WindowsClock(twoInstants + "C:\\Users\\user\npwsh.msi=file\n")).Profile.Should().Be(@"C:\Users\user");
        HealthCommands.ClockTags.Should().Contain([HealthCommands.PwshKnownFolderTag, HealthCommands.PwshEnvProgramFilesTag, HealthCommands.PwshMsiTag, HealthCommands.PwshSignatureTag]);
    }

    [Fact]
    public void An_empty_tag_is_an_empty_value_and_one_tag_is_enough_while_none_is_no_pwsh_line()
    {
        Value(ClockParsers.PwshProbe(["pwsh.msi=", "pwsh.signature="])).Should().Be(new PwshProbe(string.Empty, string.Empty, string.Empty, string.Empty));
        Value(ClockParsers.PwshProbe(["w32time.status=Running", "pwsh.msi=file"])).Msi.Should().Be("file");
        ClockParsers.PwshProbe(["w32time.status=Running", "w32time.startType=Manual"]).ReasonOrEmpty.Should().Be(WindowsClockAnswer.NoPwshLine);
    }

    [Fact]
    public void The_derived_verified_answer_reads_a_signed_file_and_the_capture_is_untouched_otherwise()
    {
        var verified = Value(Value(HealthParsers.WindowsClock(PwshEngineFixture.Verified)).Pwsh);

        verified.Should().Be(new PwshProbe(@"C:\Program Files", @"C:\Program Files", "file", "signed"));
        PwshEngineFixture.WithPwsh("link", string.Empty).Should().Contain("pwsh.msi=link\npwsh.signature=\n");
    }

    [Fact]
    public void The_script_takes_both_instants_and_the_service_before_it_looks_at_pwsh_inside_a_guard_by_the_extensions_rules()
    {
        var script = HealthCommands.ClockScript;
        var look = script.IndexOf("GetFolderPath('ProgramFiles')", StringComparison.Ordinal);

        look.Should().BeGreaterThan(script.IndexOf("Get-Service", StringComparison.Ordinal), "the instants and the service query come first: the latency is unchanged (design §1)");
        script.LastIndexOf("try {", StringComparison.Ordinal).Should().BeLessThan(look).And.BeGreaterThan(0, "the look is guarded");
        script.IndexOf("} catch {", StringComparison.Ordinal).Should().BeGreaterThan(look, "a fault in the look never ends the probe");
        script.Should().Contain("\\PowerShell\\7\\pwsh.exe'").And.Contain("Get-AuthenticodeSignature").And.Contain("'CN=Microsoft Corporation,*'").And.Contain("LinkType");
        script.Should().Contain("$env:ProgramFiles", "the environment's folder is printed so the judgement can see it disagree");
        foreach (var tag in new[] { HealthCommands.PwshKnownFolderTag, HealthCommands.PwshEnvProgramFilesTag, HealthCommands.PwshMsiTag, HealthCommands.PwshSignatureTag })
        {
            script.IndexOf("'" + tag + "'", StringComparison.Ordinal).Should().BeGreaterThan(script.IndexOf("'" + HealthCommands.TimeServiceStartTypeTag + "'", StringComparison.Ordinal), "{0} is printed after the Windows Time lines", tag);
        }

        script.Should().StartWith(HealthCommands.ModulePathPin + "; ", "Get-AuthenticodeSignature comes from the pinned module path");
        NeverList.FirstBroken(HealthCommands.WindowsClock.Argv).Should().BeNull("the probe is still the closed set's first member");
    }

    [Fact]
    public async Task The_clock_sample_carries_what_the_probe_said_about_pwsh_or_why_not()
    {
        var clock = new FixedTimeProvider(PwshEngineFixture.CapturedAt);
        var runner = new RecordingCommandRunner().Script(HealthCommands.WindowsClock.Argv, 0, PwshEngineFixture.Answer);

        var sample = await HealthCollector.MeasureWindowsClockAsync(runner, clock, TestContext.Current.CancellationToken);

        sample.Measured.Should().BeTrue(sample.Unavailable);
        Value(sample.Pwsh).Msi.Should().Be("absent");
        var failed = await HealthCollector.MeasureWindowsClockAsync(new RecordingCommandRunner { Default = new CommandOutcome.FailedToStart("no interop") }, clock, TestContext.Current.CancellationToken);
        failed.Pwsh.ReasonOrEmpty.Should().Contain("the Windows clock was not observed").And.Contain("no interop");
    }
}
