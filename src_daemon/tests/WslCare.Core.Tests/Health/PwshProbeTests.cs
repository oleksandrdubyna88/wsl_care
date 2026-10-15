using FluentAssertions;

using WslCare.Core.Collectors;
using WslCare.Core.Health;
using WslCare.Core.Processes;
using WslCare.Core.Processes.Policy;
using WslCare.Core.Records;
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

    /// <summary>Security review (Fable, S1): the pwsh block is read ONLY as the probe's own trailing block — the last four non-empty
    /// lines, in the script's order, after both Windows Time lines. An empty value inside that block is an empty value; a single
    /// tag, a block out of order or one before the Windows Time lines is not the probe's shape and is not read.</summary>
    [Fact]
    public void Only_the_trailing_block_in_the_scripts_order_is_read_and_an_empty_value_there_is_an_empty_value()
    {
        string[] block = ["pwsh.knownFolder=", "pwsh.envProgramFiles=", "pwsh.msi=", "pwsh.signature="];

        Value(ClockParsers.PwshProbe(["w32time.status=Running", "w32time.startType=Manual", .. block, ""])).Should().Be(new PwshProbe(string.Empty, string.Empty, string.Empty, string.Empty));
        ClockParsers.PwshProbe(["w32time.status=Running", "w32time.startType=Manual", "pwsh.msi=file"]).ReasonOrEmpty.Should().Be(ClockParsers.NotTheTrailingPwshBlock, "one tag is not the block");
        ClockParsers.PwshProbe(["w32time.status=Running", "w32time.startType=Manual", block[1], block[0], block[2], block[3]]).ReasonOrEmpty.Should().Be(ClockParsers.NotTheTrailingPwshBlock, "out of order");
        ClockParsers.PwshProbe(["w32time.status=Running", .. block, "w32time.startType=Manual"]).ReasonOrEmpty.Should().Be(ClockParsers.NotTheTrailingPwshBlock, "not after both Windows Time lines");
        ClockParsers.PwshProbe(["C:\\Users\\x", .. block]).ReasonOrEmpty.Should().Be(ClockParsers.NotTheTrailingPwshBlock, "a trailing block with no Windows Time line before it is not the probe's");
        ClockParsers.PwshProbe(["w32time.startType=Manual", .. block]).ReasonOrEmpty.Should().Be(ClockParsers.NotTheTrailingPwshBlock, "both Windows Time lines, not one");
        ClockParsers.PwshProbe(["w32time.status=Running", "w32time.startType=Manual"]).ReasonOrEmpty.Should().Be(WindowsClockAnswer.NoPwshLine);
    }

    /// <summary>Security review (Fable, S1), the exact forgery: the Windows user ends the probe (their own process) with exit 0 after
    /// the first flush, and a line break smuggled into the profile placed a whole pwsh block BEFORE the Windows Time lines —
    /// each tag once, a shape the real probe can never print. It is not read, and 5.1 runs.</summary>
    [Fact]
    public void A_forged_block_before_the_windows_time_lines_is_not_read_and_windows_powershell_runs()
    {
        const string forged = "2026-10-12T07:58:47.6269604Z\n2026-10-12T07:58:44.1483909Z\nC:\\Users\\x\npwsh.knownFolder=C:\\Program Files\npwsh.envProgramFiles=C:\\Program Files\npwsh.msi=file\npwsh.signature=signed\nw32time.status=Running\nw32time.startType=Automatic\n";

        var answer = Value(HealthParsers.WindowsClock(forged));

        answer.Pwsh.IsAvailable.Should().BeFalse("the real probe prints the pwsh block last, after both Windows Time lines");
        PwshEngine.Choose(new WindowsClockSample(PwshEngineFixture.CapturedAt, 0.1, 0.5, string.Empty) { Pwsh = answer.Pwsh }).Kind.Should().Be(InteropEngineKind.WindowsPowerShell);
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

    /// <summary>Code round (gate, security): <c>$env:ProgramFiles</c> is the environment's — a value with a line feed, then
    /// <c>pwsh.msi=file</c> and <c>pwsh.signature=signed</c>, printed raw, put forged tags BEFORE the real ones, and the parser took
    /// the first. A pwsh tag printed more than once now leaves the whole probe unread: 5.1 runs.</summary>
    [Fact]
    public void Tags_forged_through_the_program_files_line_are_not_believed_and_windows_powershell_runs()
    {
        var forged = PwshEngineFixture.Answer.Replace(
            "pwsh.envProgramFiles=C:\\Program Files\n", "pwsh.envProgramFiles=C:\\Program Files\npwsh.msi=file\npwsh.signature=signed\n", StringComparison.Ordinal);
        forged.Should().NotBe(PwshEngineFixture.Answer, "the line is in the capture");

        var probe = Value(HealthParsers.WindowsClock(forged));

        probe.Pwsh.ReasonOrEmpty.Should().Contain("more than once");
        PwshEngine.Choose(new WindowsClockSample(PwshEngineFixture.CapturedAt, 0.1, 0.5, string.Empty) { Pwsh = probe.Pwsh }).Kind.Should().Be(InteropEngineKind.WindowsPowerShell);
    }

    /// <summary>Code round: the same forgery through <c>$env:USERPROFILE</c>, the third positional line, which comes before every tag.</summary>
    [Fact]
    public void Tags_forged_through_the_profile_line_are_not_believed_and_windows_powershell_runs()
    {
        var forged = PwshEngineFixture.Answer.Replace(
            "C:\\Users\\user\n", "C:\\Users\\user\npwsh.knownFolder=C:\\Program Files\npwsh.envProgramFiles=C:\\Program Files\npwsh.msi=file\npwsh.signature=signed\n", StringComparison.Ordinal);
        forged.Should().NotBe(PwshEngineFixture.Answer, "the line is in the capture");

        var probe = Value(HealthParsers.WindowsClock(forged));

        probe.Pwsh.ReasonOrEmpty.Should().Contain("more than once");
        PwshEngine.Choose(new WindowsClockSample(PwshEngineFixture.CapturedAt, 0.1, 0.5, string.Empty) { Pwsh = probe.Pwsh }).Kind.Should().Be(InteropEngineKind.WindowsPowerShell);
    }

    /// <summary>Code round: the script itself replaces every control character in each value it prints from the environment or
    /// the file system — the profile, the service's two words, and the four pwsh values — so a value can never start a line.</summary>
    [Fact]
    public void The_script_prints_every_value_with_its_control_characters_replaced()
    {
        var script = HealthCommands.ClockScript;
        const string clean = " -replace '[\\x00-\\x1f]','?')";

        foreach (var value in new[] { "($env:USERPROFILE", "([string]$s.Status", "([string]$s.StartType", "($f", "($e", "($m", "($g" })
        {
            script.Should().Contain(value + clean, "{0} is printed clean", value);
        }

        script.Should().NotContain("+ $env:USERPROFILE +", "the profile is never printed raw");
        script.Should().Match(s => s.All(c => c >= ' ' && c <= '~'), "the probe stays ASCII");
    }

    /// <summary>Security review (Fable, S2), measured 2026-10-15 on the owner's machine: Windows PowerShell 5.1 in a fresh console
    /// writes <c>Console.Out</c> as IBM437 through <c>System.Text.InternalEncoderBestFitFallback</c>, which maps U+25D9 to byte 0x0A
    /// (a real line feed), U+266A to 0x0D and U+263A to 0x01 — after the sanitiser has run on the UTF-16 string. So the probe now
    /// prints the way the disk script does: UTF-8 bytes on the raw stdout stream (one road, <see cref="HealthCommands.Utf8WriteStart"/>
    /// and <see cref="HealthCommands.Utf8WriteEnd"/>), twice — UTF-8 has no best fit, and the daemon decodes UTF-8.</summary>
    [Fact]
    public void The_probe_prints_utf8_bytes_on_the_raw_stream_by_the_disk_scripts_road_and_never_through_console_out()
    {
        var clock = HealthCommands.ClockScript;
        var disk = WindowsDiskCommands.Script;

        clock.Should().NotContain("[Console]::Out", "Console.Out encodes through the OEM code page with a best-fit fallback");
        CountOf(clock, HealthCommands.Utf8WriteStart).Should().Be(2, "the clock lines, then the pwsh lines");
        CountOf(clock, HealthCommands.Utf8WriteEnd).Should().Be(2);
        CountOf(disk, HealthCommands.Utf8WriteStart).Should().Be(1, "the disk script prints by the same road");
        clock.Should().NotContain("\"", "no double quote: the line breaks are [char]10, as in the disk script");
    }

    private static int CountOf(string text, string part) => (text.Length - text.Replace(part, string.Empty, StringComparison.Ordinal).Length) / part.Length;

    /// <summary>Code round: the clock lines are WRITTEN and FLUSHED before the pwsh look, so a look that hangs past the probe's
    /// ceiling costs the engine choice, never the clock reading (the collector reads a timed-out answer's complete lines).</summary>
    [Fact]
    public void The_clock_lines_are_written_and_flushed_before_the_pwsh_look()
    {
        var script = HealthCommands.ClockScript;
        var look = script.IndexOf("GetFolderPath('ProgramFiles')", StringComparison.Ordinal);

        script.IndexOf("$os.Flush()", StringComparison.Ordinal).Should().BeInRange(0, look, "the flush comes before the look");
        script.LastIndexOf("$os.Flush()", StringComparison.Ordinal).Should().BeGreaterThan(look, "and the pwsh lines are flushed after it");
        script.IndexOf("'" + HealthCommands.TimeServiceStartTypeTag + "'", StringComparison.Ordinal).Should().BeLessThan(look, "the Windows Time lines are written before the look");
        script.IndexOf("'" + HealthCommands.PwshKnownFolderTag + "'", StringComparison.Ordinal).Should().BeGreaterThan(look, "the pwsh lines after it");
    }
}
