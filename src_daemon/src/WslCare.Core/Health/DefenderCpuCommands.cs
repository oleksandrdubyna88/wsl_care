using WslCare.Core.Config;
using WslCare.Core.Processes;

namespace WslCare.Core.Health;

/// <summary>
/// The THIRD fixed interop script (E15.S10, PLAN_disk_health.md): one filtered CIM read of Windows Defender's engine in the raw
/// performance counters' process class. It has NO slot — no value in it varies — and prints ONE tagged line
/// (<see cref="Tag"/>, <see cref="NoneTag"/> or <see cref="ErrorTag"/>) by the one UTF-8 road every interop script prints by.
/// </summary>
public static class DefenderCpuCommands
{
    /// <summary>The fixture and log name of the command.</summary>
    public const string Name = "powershell-defender-cpu";

    /// <summary><c>defender=&lt;PercentProcessorTime&gt;|&lt;Timestamp_Sys100NS&gt;|&lt;IDProcess&gt;|&lt;ElapsedTime&gt;</c>.</summary>
    public const string Tag = "defender=";

    /// <summary>No process named <c>MsMpEng</c> — Defender is off or replaced by another product.</summary>
    public const string NoneTag = "defender.none=";

    /// <summary>The read failed: the exception's type follows.</summary>
    public const string ErrorTag = "defender.error=";

    public static TimeSpan Ceiling => Tuning.Current.Seconds(ConfigKeys.Health.DefenderCpuTimeoutSeconds);

    private static int Cap => Tuning.Current.Int(ConfigKeys.Health.OutputCapBytes);

    /// <summary>
    /// The script: the module path pinned FIRST (<see cref="HealthCommands.ModulePathPin"/>), no progress bar, every error terminating inside
    /// its <c>try</c>; ONE filtered CIM read of the raw process class (<c>-Filter</c> saves transfer, not enumeration — measured 2026-10-16,
    /// the same 0.36–0.48 s warm — and names the one process it wants), printing <see cref="Tag"/>, <see cref="NoneTag"/> or
    /// <see cref="ErrorTag"/>; written by the one UTF-8 road. ASCII only, no double quote: the filter is a single-quoted string whose
    /// own quotes are doubled. Windows PowerShell 5.1 only — CIM is built in, so the reason the disk script runs under the MSI pwsh (the
    /// Storage cmdlets, E15.S1b) does not apply, and a pwsh variant would add a member to the closed set for no measured gain.
    /// </summary>
    public static string Script { get; } =
        HealthCommands.ModulePathPin + "; "
        + $$"""$ProgressPreference='SilentlyContinue'; $ErrorActionPreference='Stop'; $t='{{NoneTag}}'; try { $p=@(Get-CimInstance -ClassName Win32_PerfRawData_PerfProc_Process -Filter 'Name=''MsMpEng''' | Select-Object -First 1); if($p.Count -gt 0){ $t='{{Tag}}'+$p[0].PercentProcessorTime+'|'+$p[0].Timestamp_Sys100NS+'|'+$p[0].IDProcess+'|'+$p[0].ElapsedTime } } catch { $t='{{ErrorTag}}'+$_.Exception.GetType().Name }; """
        + HealthCommands.Utf8WriteStart + "$t+[char]10" + HealthCommands.Utf8WriteEnd;

    /// <summary>The one command: <c>powershell.exe -NoProfile -NonInteractive -Command &lt;script&gt;</c>.</summary>
    public static ToolCommand DefenderCpu =>
        new(HealthCommands.PowerShell, Name, ["-NoProfile", "-NonInteractive", "-Command", Script], Ceiling, Cap)
        {
            Summary = "powershell.exe (the Defender CPU script)",
        };
}
