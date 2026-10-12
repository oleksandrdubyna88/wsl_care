using WslCare.Core.Collectors;
using WslCare.Core.Records;

namespace WslCare.Core.Health;

/// <summary>Which PowerShell the Windows disk script runs under (E15.S1b).</summary>
public enum InteropEngineKind
{
    /// <summary>Windows PowerShell 5.1 — <c>powershell.exe</c>, System32's, the clock probe's own engine.</summary>
    WindowsPowerShell,

    /// <summary>The MSI PowerShell 7 under <c>C:\Program Files\PowerShell\7</c>, verified by the clock probe.</summary>
    Msi,
}

/// <summary>The engine chosen for the disk script: which, the program the daemon starts, and why.</summary>
/// <param name="Program">For 5.1 the bare name the policy judges (<see cref="HealthCommands.PowerShell"/>, found on PATH or in
/// System32 on the drive); for the MSI pwsh the file as the distro sees it (<c>/mnt/c/Program Files/PowerShell/7/pwsh.exe</c>) —
/// the file the resolver's system-drive fallback starts for <see cref="HealthCommands.Pwsh"/>.</param>
/// <param name="Reason">What the judgement rested on, in a sentence a person reads in the run detail.</param>
public sealed record InteropEngine(InteropEngineKind Kind, string Program, string Reason);

/// <summary>
/// The PURE judgement of E15.S1b (PLAN_disk_health.md, <i>E15.S1b design</i> §2): from what the clock probe said
/// (<see cref="PwshProbe"/>), the MSI PowerShell 7 ONLY when every one of these holds — the known folder is EXACTLY
/// <see cref="KnownFolder"/> (the one folder the daemon maps, on the assumed system drive, to <c>&lt;mount&gt;/Program Files</c>);
/// the environment's <c>ProgramFiles</c> equals it (case-insensitive — it must agree, never decide); the item is a real
/// <c>file</c>; and its signature is <c>signed</c> (Authenticode Valid from <c>CN=Microsoft Corporation</c>). Anything else, a
/// probe that was not measured and a probe from before the tags: Windows PowerShell 5.1, with the reason. Never the Store
/// package, never the alias: the only path this type can answer is under <see cref="KnownFolder"/>.
/// </summary>
public static class PwshEngine
{
    /// <summary>The one Program Files the daemon maps — the known-folder API's answer on a 64-bit Windows on <c>C:</c>.</summary>
    public const string KnownFolder = @"C:\Program Files";

    /// <summary>Where the MSI installs PowerShell 7 — the folder <c>WindowsSystemDrive.Programs</c> names for <c>pwsh.exe</c>.</summary>
    public const string MsiFile = KnownFolder + @"\PowerShell\7\pwsh.exe";

    /// <summary>The value of <c>pwsh.msi=</c> that is a real file, and of <c>pwsh.signature=</c> that is Microsoft's.</summary>
    private const string File = "file";

    private const string Signed = "signed";

    /// <summary>The engine for <paramref name="sample"/>; the MSI pwsh's program is placed under <paramref name="automountRoot"/>
    /// (<c>/mnt/</c>) exactly as the Windows profile is (<see cref="HealthCollector.InDistro"/>).</summary>
    public static InteropEngine Choose(WindowsClockSample sample, string automountRoot) => sample.Pwsh switch
    {
        Reading<PwshProbe>.Available { Value: var probe } => Judge(probe, automountRoot),
        var missing => WindowsPowerShell($"the clock probe did not verify pwsh: {missing.ReasonOrEmpty}"),
    };

    /// <summary>The first refusal, in the order of the design — the folder, the environment, the file, the signature — else the MSI pwsh.</summary>
    private static InteropEngine Judge(PwshProbe probe, string automountRoot)
    {
        string[] problems = [FolderProblem(probe), EnvironmentProblem(probe), FileProblem(probe), SignatureProblem(probe)];
        var refusal = problems.FirstOrDefault(p => p.Length > 0) ?? string.Empty;
        return refusal.Length > 0 ? WindowsPowerShell(refusal) : Msi(automountRoot);
    }

    private static InteropEngine WindowsPowerShell(string reason) => new(InteropEngineKind.WindowsPowerShell, HealthCommands.PowerShell, reason);

    private static InteropEngine Msi(string automountRoot) =>
        new(
            InteropEngineKind.Msi,
            HealthCollector.InDistro(MsiFile, automountRoot).ValueOr(MsiFile),
            $"the clock probe verified {MsiFile}: a real file, Authenticode Valid from CN=Microsoft Corporation, under the known folder {KnownFolder}, which the environment's ProgramFiles names too");

    /// <summary>EXACTLY the one folder the daemon maps (ordinal): the known-folder API's own spelling, nothing else.</summary>
    private static string FolderProblem(PwshProbe probe) =>
        string.Equals(probe.KnownFolder, KnownFolder, StringComparison.Ordinal)
            ? string.Empty
            : $"the known folder ProgramFiles is \"{probe.KnownFolder}\", not {KnownFolder} — the one folder the daemon maps to the system drive";

    /// <summary>The environment must AGREE with the known folder (case-insensitive, as Windows compares folder names); it never decides.</summary>
    private static string EnvironmentProblem(PwshProbe probe) =>
        string.Equals(probe.EnvProgramFiles, probe.KnownFolder, StringComparison.OrdinalIgnoreCase)
            ? string.Empty
            : $"the environment's ProgramFiles (\"{probe.EnvProgramFiles}\") does not name the known folder ({probe.KnownFolder})";

    private static string FileProblem(PwshProbe probe) =>
        probe.Msi == File ? string.Empty : $"{MsiFile} is {Item(probe.Msi)}";

    private static string Item(string msi) => msi switch
    {
        "absent" => "absent",
        "link" => "a link, which is never followed there",
        "directory" => "a directory, not a file",
        "empty" => "an empty file",
        "" => "not reported: the probe printed pwsh.msi empty",
        _ => $"not readable ({msi})",
    };

    private static string SignatureProblem(PwshProbe probe) =>
        probe.Signature == Signed
            ? string.Empty
            : $"{MsiFile} is not signed by Microsoft: {(probe.Signature.Length > 0 ? probe.Signature : "the probe printed no signature")}";
}
