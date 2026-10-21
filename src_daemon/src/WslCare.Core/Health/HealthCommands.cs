using WslCare.Core.Config;
using WslCare.Core.Processes;

namespace WslCare.Core.Health;

/// <summary>
/// The health commands that are neither systemd's nor Docker's, in ONE place — read-only, each with its ceiling:
/// the Windows clock probe (a SLOW process, so only <c>collect</c> starts it — plan §15b #5), <c>snap list --all</c>
/// for A9's disabled revisions, the clock reference's HEAD, and the closed set of fixed PowerShell scripts
/// (<see cref="FixedScripts"/>: the clock probe, since E15.S1 <see cref="WindowsDiskCommands.WindowsDisk"/> and since E15.S10
/// <see cref="DefenderCpuCommands.DefenderCpu"/>).
/// </summary>
public static class HealthCommands
{
    /// <summary>The name WSL interop reaches PowerShell by from inside the distro; on Windows the same file name.</summary>
    public const string PowerShell = "powershell.exe";

    /// <summary>The MSI PowerShell 7's file name (E15.S1b) — a bare name the policy judges; the resolver finds it ONLY in its one
    /// fixed folder on the system drive (<c>WindowsSystemDrive.Programs</c>, <c>FixedFolderOnly</c>), never on PATH, never under a
    /// user's profile.</summary>
    public const string Pwsh = "pwsh.exe";

    public const string Snap = "snap";

    public const string Curl = "curl";

    /// <summary>The tag of the probe's line carrying the Windows Time service's state (<c>Running</c>, <c>Stopped</c>, …).</summary>
    public const string TimeServiceStatusTag = "w32time.status=";

    /// <summary>The tag of the probe's line carrying the service's start type (<c>Automatic</c>, <c>Manual</c>, <c>Disabled</c>).</summary>
    public const string TimeServiceStartTypeTag = "w32time.startType=";

    /// <summary>E15.S1b: Program Files as the known-folder API names it (<c>[Environment]::GetFolderPath('ProgramFiles')</c>).</summary>
    public const string PwshKnownFolderTag = "pwsh.knownFolder=";

    /// <summary>E15.S1b: Program Files as the environment names it (<c>$env:ProgramFiles</c>) — it must agree with the known folder.</summary>
    public const string PwshEnvProgramFilesTag = "pwsh.envProgramFiles=";

    /// <summary>E15.S1b: what is at <c>&lt;known folder&gt;\PowerShell\7\pwsh.exe</c> — <c>absent</c>, <c>link</c>, <c>directory</c>,
    /// <c>empty</c> or <c>file</c>.</summary>
    public const string PwshMsiTag = "pwsh.msi=";

    /// <summary>E15.S1b: <c>signed</c> when <c>Get-AuthenticodeSignature</c> reads Valid from <c>CN=Microsoft Corporation</c>,
    /// else <c>unsigned:&lt;status&gt;|&lt;subject&gt;</c>; empty when there is no file.</summary>
    public const string PwshSignatureTag = "pwsh.signature=";

    /// <summary>The four pwsh tags in the order the probe prints them — the ONE list the parser reads the trailing block by and the
    /// live test checks (coai security round): a tag added to the probe is checked everywhere at once. Declared BEFORE
    /// <see cref="ClockTags"/>: static members initialise in textual order.</summary>
    public static IReadOnlyList<string> PwshTags { get; } = [PwshKnownFolderTag, PwshEnvProgramFilesTag, PwshMsiTag, PwshSignatureTag];

    /// <summary>Every TAGGED line the clock probe prints — read by tag, never counted as a position (an empty profile must not
    /// let a tag shift into its place).</summary>
    public static IReadOnlyList<string> ClockTags { get; } = [TimeServiceStatusTag, TimeServiceStartTypeTag, .. PwshTags];

    /// <summary>
    /// Where the MSI installs PowerShell 7, relative to Program Files, in Windows' own spelling — the ONE definition (coai security
    /// round) the probe's look, <see cref="PwshEngine.MsiFile"/> and the resolver's folder (<c>WindowsSystemDrive.Programs</c>)
    /// are all built from, so the file verified, the file judged and the file started cannot name different places.
    /// </summary>
    public const string PwshMsiFolder = "PowerShell\\7";

    /// <summary>The module path pinned to <c>$PSHOME\Modules</c> by an assignment — no command to autoload — so every cmdlet an
    /// interop script uses comes from the administrator-only module folder, never from a user's Documents (the coordinator,
    /// 2026-10-11, after the #104 Fable review). The SAME text as the extension's <c>MODULES</c>
    /// (<c>src_vs_code/src/powershell/elevatedEngine.ts</c>), held equal by an architecture test. No double quote, so the disk
    /// script's ASCII rule holds.</summary>
    public const string ModulePathPin = "$env:PSModulePath = $PSHOME + '\\Modules'";

    /// <summary>
    /// The probe's look at <c>&lt;known folder&gt;\PowerShell\7\pwsh.exe</c> (E15.S1b, the rules of the extension's
    /// <c>SIGNED_HELPER</c> in <c>elevatedEngine.ts</c>): Program Files as the KNOWN-FOLDER API names it and as the environment
    /// names it (the judgement refuses a disagreement); the item there — <c>absent</c>, a <c>link</c> (any <c>LinkType</c>), a
    /// <c>directory</c>, <c>empty</c>, or a <c>file</c> — and for a file its Authenticode signature: <c>signed</c> only when
    /// <c>Get-AuthenticodeSignature</c> (from the pinned module path) reads <c>Valid</c> from <c>CN=Microsoft Corporation</c>,
    /// else <c>unsigned:&lt;status&gt;|&lt;subject&gt;</c>. Inside its own <c>try</c>: a fault here prints
    /// <c>error:&lt;type&gt;</c> and never ends the probe — the clock reading matters more than the engine choice. Read-only:
    /// nothing is started. It runs AFTER the clock lines were written and flushed.
    /// </summary>
    public const string PwshLook =
        "$f = ''; $e = ''; $m = ''; $g = ''; try { $f = [string][Environment]::GetFolderPath('ProgramFiles'); $e = [string]$env:ProgramFiles; "
        + "$i = Get-Item -LiteralPath ($f + '\\" + PwshMsiFolder + "\\" + Pwsh + "') -Force -ErrorAction SilentlyContinue; "
        + "if ($null -eq $i) { $m = 'absent' } elseif (-not [string]::IsNullOrEmpty($i.LinkType)) { $m = 'link' } elseif ($i -isnot [IO.FileInfo]) { $m = 'directory' } elseif ($i.Length -le 0) { $m = 'empty' } "
        + "else { $m = 'file'; $a = Get-AuthenticodeSignature -LiteralPath $i.FullName; if ($a.Status -eq 'Valid' -and $a.SignerCertificate.Subject -like 'CN=Microsoft Corporation,*') { $g = 'signed' } else { $g = 'unsigned:' + $a.Status + '|' + $a.SignerCertificate.Subject } } } "
        + "catch { $m = 'error:' + $_.Exception.GetType().Name }";

    /// <summary>
    /// The ONE printing road of the interop scripts (security review, Fable S2), written around a PowerShell string expression:
    /// <c>Utf8WriteStart + &lt;text&gt; + Utf8WriteEnd</c> turns the text into UTF-8 bytes and writes them, flushed, to the RAW stdout
    /// stream. Never <c>[Console]::Out</c>: Windows PowerShell 5.1 encodes it in the OEM code page through a BEST-FIT fallback —
    /// measured 2026-10-15 in a fresh console: IBM437, <c>System.Text.InternalEncoderBestFitFallback</c>, U+25D9 → 0x0A (a real
    /// line feed), U+266A → 0x0D, U+263A → 0x01 — which re-creates a line break AFTER the sanitiser ran, and turns a Cyrillic
    /// profile into <c>?</c>. UTF-8 has no best fit, and the daemon decodes UTF-8. <c>OutputEncoding</c> is not set: it can throw
    /// with no console attached. No double quote.
    /// </summary>
    public const string Utf8WriteStart = "$ob=[Text.Encoding]::UTF8.GetBytes(";

    /// <summary>The end of <see cref="Utf8WriteStart"/>'s road: the write and the flush.</summary>
    public const string Utf8WriteEnd = "); $os=[Console]::OpenStandardOutput(); $os.Write($ob,0,$ob.Length); $os.Flush()";

    /// <summary>The tail that prints a value CLEAN (code round, security): every control character replaced by <c>?</c>, so a value
    /// from the environment or the file system can never end its line and start a forged tagged one.</summary>
    private const string Clean = " -replace '[\\x00-\\x1f]','?')";

    /// <summary>The three positional lines and the two Windows Time lines, WRITTEN and FLUSHED before the pwsh look (code round):
    /// a look that hangs past the ceiling then costs the engine choice, never the clock reading.</summary>
    private const string ClockLines =
        Utf8WriteStart + "$n + [char]10 + $p + [char]10 + ($env:USERPROFILE" + Clean + " + [char]10 + '" + TimeServiceStatusTag + "' + ([string]$s.Status" + Clean
        + " + [char]10 + '" + TimeServiceStartTypeTag + "' + ([string]$s.StartType" + Clean + " + [char]10" + Utf8WriteEnd;

    /// <summary>The four pwsh lines as the probe prints them, after the look, each value clean, by the same UTF-8 road.</summary>
    private const string PwshLines =
        Utf8WriteStart + "'" + PwshKnownFolderTag + "' + ($f" + Clean + " + [char]10 + '" + PwshEnvProgramFilesTag + "' + ($e" + Clean + " + [char]10 + '"
        + PwshMsiTag + "' + ($m" + Clean + " + [char]10 + '" + PwshSignatureTag + "' + ($g" + Clean + " + [char]10" + Utf8WriteEnd;

    /// <summary>
    /// What the probe prints, one per line: Windows' UTC now, the PowerShell process's own start (UTC, the same
    /// clock), and <c>%USERPROFILE%</c>. The first two give the launch latency WITHOUT any skew in it, which is
    /// what lets the skew subtract it (plan §15b #5); the profile is how the distro finds <c>.wslconfig</c> and
    /// Docker Desktop's <c>daemon.json</c> through <c>/mnt</c> without a walk. Since PLAN_windows_time_guard.md D1 two more
    /// TAGGED lines follow — the Windows Time service's status and start type, read AFTER both instants were taken, so the
    /// latency is unchanged; tagged because an empty value must not shift the lines the parser reads by position. Since
    /// E15.S1b four more tagged lines follow those (<see cref="PwshLook"/>): what the probe — Windows PowerShell 5.1, the
    /// trusted System32 path — saw of the MSI PowerShell 7, so no third PowerShell start is needed to choose the engine the
    /// disk script runs under. Since the E15.S1b code round every printed value is clean and the clock lines are flushed first.
    /// </summary>
    public const string ClockScript =
        ModulePathPin + "; "
        + "$n = [DateTime]::UtcNow.ToString('o'); $p = (Get-Process -Id $PID).StartTime.ToUniversalTime().ToString('o'); "
        + "$s = Get-Service -Name w32time -ErrorAction SilentlyContinue; "
        + ClockLines + "; "
        + PwshLook + "; "
        + PwshLines;

    /// <summary>Measured 2026-10-02 from WSL Ubuntu: 0.85–1.1 s per start, of which 0.56–0.83 s before the script ran.</summary>
    public static TimeSpan ClockCeiling => Tuning.Current.Seconds(ConfigKeys.Health.WindowsClockTimeoutSeconds);

    public static TimeSpan SnapCeiling => Tuning.Current.Seconds(ConfigKeys.Health.SnapTimeoutSeconds);

    private static int Cap => Tuning.Current.Int(ConfigKeys.Health.OutputCapBytes);

    public static ToolCommand WindowsClock =>
        new(PowerShell, "powershell-clock", ["-NoProfile", "-NonInteractive", "-Command", ClockScript], ClockCeiling, Cap)
        {
            Summary = "powershell.exe (the Windows clock probe)",
        };

    /// <summary>
    /// The CLOSED set of PowerShell commands the product starts (PLAN_disk_health.md §3.1, E15.S1; E15.S1b): the clock probe
    /// (always Windows PowerShell 5.1), the Windows disk script under 5.1, and the SAME disk script under the bare
    /// <see cref="Pwsh"/> (the MSI PowerShell 7), and — E15.S10 — the Defender CPU script under 5.1 only — each a fixed argv with no slot.
    /// The never-list refuses every PowerShell argv that is not EXACTLY one of these (program and arguments), so a fifth script, a variant
    /// of one of these, the clock probe or the Defender script under pwsh, pwsh by another spelling or path, or <c>-File</c> never runs.
    /// Built from the configuration in force, as the commands themselves are.
    /// </summary>
    public static IReadOnlyList<ToolCommand> FixedScripts => [WindowsClock, WindowsDiskCommands.WindowsDisk, WindowsDiskCommands.WindowsDiskUnderPwsh, DefenderCpuCommands.DefenderCpu];

    public static ToolCommand SnapList => new(Snap, "snap-list-all", ["list", "--all"], SnapCeiling, Cap);

    /// <summary>The clock reference's ceiling (<c>clock.referenceTimeoutSeconds</c>) — curl's own <c>--max-time</c> too.</summary>
    public static int ReferenceSeconds => Tuning.Current.Int(ConfigKeys.Clock.ReferenceTimeoutSeconds);

    /// <summary>
    /// One HEAD to <paramref name="url"/> for its <c>Date</c> header — the independent clock reference
    /// (PLAN_windows_time_guard.md D2). curl runs as root under the timer, so: <c>--disable</c> FIRST (no <c>.curlrc</c> can add
    /// an <c>output</c> or <c>write-out</c>), HTTPS only and no redirect, no cache, and the address after <c>--url</c>, where it
    /// is never read as an option. The template that admits it is <see cref="Processes.Policy.ReadCommandTemplates.ClockReference"/>.
    /// </summary>
    public static ToolCommand ClockReference(string url, int seconds) =>
        new(
            Curl,
            "curl-head-date",
            [
                "--disable", "--silent", "--show-error", "--head", "--max-redirs", "0", "--proto", "=https", "--proto-redir", "=https",
                "--max-time", seconds.ToString(System.Globalization.CultureInfo.InvariantCulture), "--header", "Cache-Control: no-cache", "--url", url,
            ],
            TimeSpan.FromSeconds(seconds),
            Cap)
        {
            Summary = $"curl --head {url} (the clock reference)",
        };
}
