using WslCare.Core.Config;
using WslCare.Core.Processes;

namespace WslCare.Core.Health;

/// <summary>
/// The health commands that are neither systemd's nor Docker's, in ONE place — read-only, each with its ceiling:
/// the Windows clock probe (a SLOW process, so only <c>collect</c> starts it — plan §15b #5), <c>snap list --all</c>
/// for A9's disabled revisions, the clock reference's HEAD, and the closed set of fixed PowerShell scripts
/// (<see cref="FixedScripts"/>: the clock probe and, since E15.S1, <see cref="WindowsDiskCommands.WindowsDisk"/>).
/// </summary>
public static class HealthCommands
{
    /// <summary>The name WSL interop reaches PowerShell by from inside the distro; on Windows the same file name.</summary>
    public const string PowerShell = "powershell.exe";

    /// <summary>The MSI PowerShell 7's file name (E15.S1b) — a bare name the policy judges; the resolver finds it on PATH or in its
    /// one fixed folder on the system drive (<c>WindowsSystemDrive.Programs</c>), never under a user's profile.</summary>
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

    /// <summary>Every TAGGED line the clock probe prints — read by tag, never counted as a position (an empty profile must not
    /// let a tag shift into its place).</summary>
    public static IReadOnlyList<string> ClockTags { get; } =
        [TimeServiceStatusTag, TimeServiceStartTypeTag, PwshKnownFolderTag, PwshEnvProgramFilesTag, PwshMsiTag, PwshSignatureTag];

    /// <summary>
    /// What the probe prints, one per line: Windows' UTC now, the PowerShell process's own start (UTC, the same
    /// clock), and <c>%USERPROFILE%</c>. The first two give the launch latency WITHOUT any skew in it, which is
    /// what lets the skew subtract it (plan §15b #5); the profile is how the distro finds <c>.wslconfig</c> and
    /// Docker Desktop's <c>daemon.json</c> through <c>/mnt</c> without a walk. Since PLAN_windows_time_guard.md D1 two more
    /// TAGGED lines follow — the Windows Time service's status and start type, read AFTER both instants were taken, so the
    /// latency is unchanged; tagged because an empty value must not shift the lines the parser reads by position. Since
    /// E15.S1b four more tagged lines follow those (<see cref="PwshLook"/>): what the probe — Windows PowerShell 5.1, the
    /// trusted System32 path — saw of the MSI PowerShell 7, so no third PowerShell start is needed to choose the engine the
    /// disk script runs under.
    /// </summary>
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
    /// nothing is started. It runs AFTER both instants and the service query (the latency is unchanged).
    /// </summary>
    public const string PwshLook =
        "$f = ''; $e = ''; $m = ''; $g = ''; try { $f = [string][Environment]::GetFolderPath('ProgramFiles'); $e = [string]$env:ProgramFiles; "
        + "$i = Get-Item -LiteralPath ($f + '\\PowerShell\\7\\pwsh.exe') -Force -ErrorAction SilentlyContinue; "
        + "if ($null -eq $i) { $m = 'absent' } elseif (-not [string]::IsNullOrEmpty($i.LinkType)) { $m = 'link' } elseif ($i -isnot [IO.FileInfo]) { $m = 'directory' } elseif ($i.Length -le 0) { $m = 'empty' } "
        + "else { $m = 'file'; $a = Get-AuthenticodeSignature -LiteralPath $i.FullName; if ($a.Status -eq 'Valid' -and $a.SignerCertificate.Subject -like 'CN=Microsoft Corporation,*') { $g = 'signed' } else { $g = 'unsigned:' + $a.Status + '|' + $a.SignerCertificate.Subject } } } "
        + "catch { $m = 'error:' + $_.Exception.GetType().Name }";

    /// <summary>The four pwsh lines as the probe prints them, after the Windows Time lines.</summary>
    private const string PwshLines =
        " + '" + PwshKnownFolderTag + "' + $f + \"`n\" + '" + PwshEnvProgramFilesTag + "' + $e + \"`n\" + '" + PwshMsiTag + "' + $m + \"`n\" + '" + PwshSignatureTag + "' + $g + \"`n\"";

    public const string ClockScript =
        ModulePathPin + "; "
        + "$n = [DateTime]::UtcNow.ToString('o'); $p = (Get-Process -Id $PID).StartTime.ToUniversalTime().ToString('o'); "
        + "$s = Get-Service -Name w32time -ErrorAction SilentlyContinue; "
        + PwshLook + "; "
        + "[Console]::Out.Write($n + \"`n\" + $p + \"`n\" + $env:USERPROFILE + \"`n\" + '" + TimeServiceStatusTag + "' + $s.Status + \"`n\" + '" + TimeServiceStartTypeTag + "' + $s.StartType + \"`n\"" + PwshLines + ")";

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
    /// The CLOSED set of PowerShell commands the product starts (PLAN_disk_health.md §3.1, E15.S1): the clock probe and the
    /// Windows disk script, each a fixed argv with no slot. The never-list refuses every PowerShell argv that is not EXACTLY one
    /// of these (program and arguments), so a third script, a variant of one of these, or <c>-File</c> never runs. Built from the
    /// configuration in force, as the commands themselves are.
    /// </summary>
    public static IReadOnlyList<ToolCommand> FixedScripts => [WindowsClock, WindowsDiskCommands.WindowsDisk];

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
