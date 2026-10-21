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

    public static string Script => HealthCommands.ModulePathPin;

    public static ToolCommand DefenderCpu =>
        new(HealthCommands.PowerShell, Name, ["-NoProfile", "-NonInteractive", "-Command", Script], Ceiling, Cap)
        {
            Summary = "powershell.exe (the Defender CPU script)",
        };
}
