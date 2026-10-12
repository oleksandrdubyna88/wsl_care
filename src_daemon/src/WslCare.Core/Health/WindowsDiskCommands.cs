using System.Globalization;

using WslCare.Core.Config;
using WslCare.Core.Processes;

namespace WslCare.Core.Health;

/// <summary>
/// The second fixed interop script (PLAN_disk_health.md §3.1, E15.S1): the SLOW Windows facts a full run reads — the
/// distros' virtual disks and Docker's data disk, the lettered volumes with their disk numbers, the physical disks, the
/// reliability counters' refusal, the System log's storage events aggregated per UTC day, provider, id and disk, commit and
/// Memory Compression, the machine's identity and the display adapters' problem codes. Like the clock probe
/// (<see cref="HealthCommands.WindowsClock"/>) it has NO slot: the only values in it are two configuration INTEGERS
/// (<c>disk.eventsSinceDays</c>, <c>disk.eventsMaxRead</c>), clamped to their keys' ranges, and it prints TAGGED lines the
/// parser reads by tag (<see cref="WindowsDiskParsers"/>), the last one <see cref="EndLine"/> so a cut answer is seen.
/// </summary>
/// <remarks>
/// <para>Only <c>collect</c> starts it (never <c>status</c>, never the watch), after the clock probe, under its own ceiling
/// (<c>health.windowsDiskTimeoutSeconds</c>); the runner kills its tree past it. It is one line of ASCII with no double quote
/// and no control character (the never-list refuses one), and it names none of the never-list's forbidden words.</para>
/// <para>Which PowerShell (E15.S1b): the engine <see cref="PwshEngine"/> chose from the clock probe's look — the MSI
/// PowerShell 7 (<see cref="WindowsDiskUnderPwsh"/>, the bare <see cref="HealthCommands.Pwsh"/>, found in
/// <c>Program Files/PowerShell/7</c> on the system drive) when the probe verified it, else Windows PowerShell 5.1
/// (<see cref="WindowsDisk"/>, <see cref="HealthCommands.PowerShell"/>), found as the clock probe's is. Both run the SAME
/// arguments. Never the Store build: it is an app-execution alias under the USER's profile, a file the Windows user can
/// replace, and admitting it would let the root daemon start a user-writable program (E15.S1 as built,
/// PLAN_disk_health.md).</para>
/// </remarks>
public static class WindowsDiskCommands
{
    /// <summary>The fixture and log name of the command.</summary>
    public const string Name = "powershell-windows-disk";

    /// <summary>The log name of the same script under the MSI PowerShell 7 (E15.S1b).</summary>
    public const string PwshName = "pwsh-windows-disk";

    public const string DistroTag = "distro=";
    public const string DistrosErrorTag = "distros.error=";
    public const string DockerTag = "docker=";
    public const string DockerErrorTag = "docker.error=";
    public const string VolumeTag = "volume=";
    public const string VolumesErrorTag = "volumes.error=";
    public const string DiskTag = "disk=";
    public const string DisksErrorTag = "disks.error=";
    public const string ReliabilityTag = "reliability=";
    public const string ReliabilityReadTag = "reliability.read=";
    public const string ReliabilityRefusedTag = "reliability.refused=";
    public const string EventsReadTag = "events.read=";
    public const string EventGroupsTag = "events.groups=";

    /// <summary>The size field of a file the script did NOT open because it is not on a local fixed drive (code round: a UNC or
    /// mapped path named by the registry would make Windows send the user's credentials to that host).</summary>
    public const string NotLocalMark = "remote";

    /// <summary>The prefix of a volume's disk-number field when <c>Get-Partition</c> failed — the exception's type follows.</summary>
    public const string FailedMark = "!";
    public const string EventsErrorTag = "events.error=";
    public const string EventTag = "event=";
    public const string CommitTag = "commit=";
    public const string CommitErrorTag = "commit.error=";
    public const string CompressionTag = "compression=";
    public const string CompressionErrorTag = "compression.error=";
    public const string EditionTag = "windows.edition=";
    public const string VersionTag = "windows.version=";
    public const string BuildTag = "windows.build=";
    public const string InstallDateTag = "windows.installDate=";
    public const string AccountTag = "windows.account=";
    public const string WindowsErrorTag = "windows.error=";
    public const string MachineGuidTag = "machine.guid=";
    public const string MachineErrorTag = "machine.error=";
    public const string AdapterTag = "adapter=";
    public const string AdaptersErrorTag = "adapters.error=";

    /// <summary>The script's LAST line: an answer without it was cut and is not read.</summary>
    public const string EndLine = "end=1";

    /// <summary>The System log's providers the events are read from (research/2026-10-10_disk.md D4).</summary>
    public static IReadOnlyList<string> EventProviders { get; } = ["disk", "stornvme", "Ntfs", "volmgr", "storahci"];

    /// <summary>The storage event ids read: 7 bad block, 11 controller error, 51 paging error, 129 reset, 153 retried, 157 surprise removal.</summary>
    public static IReadOnlyList<int> EventIds { get; } = [7, 11, 51, 129, 153, 157];

    /// <summary>The FileAttributes bit of a sparse file (0x200) — a number, so the script names no attribute word.</summary>
    private const int SparseAttribute = 512;

    /// <summary>How many days back the events are read, from the configuration in force, clamped to the key's range.</summary>
    public static int EventsSinceDays => Clamped(ConfigKeys.Disk.EventsSinceDays);

    /// <summary>At most this many events are read, from the configuration in force, clamped to the key's range.</summary>
    public static int EventsMaxRead => Clamped(ConfigKeys.Disk.EventsMaxRead);

    /// <summary>At most this many event groups are printed, from the configuration in force, clamped to the key's range.</summary>
    public static int EventGroupsMax => Clamped(ConfigKeys.Disk.EventGroupsMax);

    public static TimeSpan Ceiling => Tuning.Current.Seconds(ConfigKeys.Health.WindowsDiskTimeoutSeconds);

    private static int Cap => Tuning.Current.Int(ConfigKeys.Health.OutputCapBytes);

    /// <summary>The script as the configuration in force builds it — what <see cref="WindowsDisk"/> runs, what the never-list's
    /// closed set and the command template compare an argv with.</summary>
    public static string Script => ScriptFor(EventsSinceDays, EventsMaxRead, EventGroupsMax);

    /// <summary>The one command: <c>powershell.exe -NoProfile -NonInteractive -Command &lt;script&gt;</c>.</summary>
    public static ToolCommand WindowsDisk =>
        new(HealthCommands.PowerShell, Name, ["-NoProfile", "-NonInteractive", "-Command", Script], Ceiling, Cap)
        {
            Summary = "powershell.exe (the Windows disk script)",
        };

    /// <summary>
    /// The SAME command under the MSI PowerShell 7 (E15.S1b): <see cref="WindowsDisk"/> with only the program, the name and the
    /// summary changed — its <see cref="ToolCommand.Arguments"/> are the very list <see cref="WindowsDisk"/> built, so the two can
    /// never run different scripts. The program is the BARE <see cref="HealthCommands.Pwsh"/>, which the policy judges and the
    /// resolver finds in its one fixed folder on the system drive (<c>Program Files/PowerShell/7</c>).
    /// </summary>
    public static ToolCommand WindowsDiskUnderPwsh =>
        WindowsDisk with
        {
            Executable = HealthCommands.Pwsh,
            Name = PwshName,
            Summary = "pwsh.exe (the Windows disk script, under the MSI PowerShell 7)",
        };

    /// <summary>The script for <paramref name="sinceDays"/> and <paramref name="maxRead"/>, each clamped to its key's range and
    /// written as an invariant integer — the only values that vary.</summary>
    public static string ScriptFor(int sinceDays, int maxRead, int groupsMax)
    {
        var groups = Math.Clamp(groupsMax, ConfigKeys.Disk.EventGroupsMax.Min, ConfigKeys.Disk.EventGroupsMax.Max).ToString(CultureInfo.InvariantCulture);
        var days = Math.Clamp(sinceDays, ConfigKeys.Disk.EventsSinceDays.Min, ConfigKeys.Disk.EventsSinceDays.Max).ToString(CultureInfo.InvariantCulture);
        var max = Math.Clamp(maxRead, ConfigKeys.Disk.EventsMaxRead.Min, ConfigKeys.Disk.EventsMaxRead.Max).ToString(CultureInfo.InvariantCulture);
        return string.Join("; ", [Prelude, Distros, Docker, Volumes, Disks, Reliability, .. Events(days, max, groups), Commit, Compression, Identity, Machine, Adapters, Print]);
    }

    private static int Clamped(ConfigKey.IntKey key) => (int)Math.Clamp(Tuning.Current.Int(key), key.Min, key.Max);

    private static string Quoted(IEnumerable<string> values) => string.Join(',', values.Select(v => $"'{v}'"));

    private static string Numbers(IEnumerable<int> values) => string.Join(',', values.Select(v => v.ToString(CultureInfo.InvariantCulture)));

    /// <summary>
    /// The Storage module (<c>Get-Volume</c>, <c>Get-Partition</c>, <c>Get-PhysicalDisk</c>, <c>Get-StorageReliabilityCounter</c>)
    /// imported by its ABSOLUTE System32 path — <c>[Environment]::SystemDirectory</c> is GetSystemDirectory, not a variable the
    /// caller sets — the extension's road for <c>PnpDevice</c> (research G6), joined with <c>[IO.Path]::Combine</c> as the rest of
    /// this script joins paths (<c>Join-Path</c> resolves the drive and throws on an unmounted letter). E15.S1b, measured 2026-10-12: with the module path
    /// pinned to <c>$PSHOME\Modules</c>, pwsh 7 has no Storage module of its own and every Storage section failed with
    /// <c>CommandNotFoundException</c>; under Windows PowerShell 5.1 it is the same module the pinned path already names. A
    /// failed import is not fatal and has no line of its own: each section that needs Storage then fails inside its own
    /// <c>try</c> and prints its own error line (<c>volumes.error=CommandNotFoundException</c>), so the other sections still
    /// answer.
    /// </summary>
    private const string StorageImport =
        @"try { Import-Module -Name ([IO.Path]::Combine([Environment]::SystemDirectory,'WindowsPowerShell\v1.0\Modules\Storage')) -ErrorAction Stop } catch { }";

    /// <summary>The module path pinned FIRST (<see cref="HealthCommands.ModulePathPin"/>: no cmdlet may autoload a module from a
    /// user's Documents), then the Storage import (<see cref="StorageImport"/>), no progress bar, every error terminating inside
    /// its try, the output list, and the file helper: size, sparse bit, last write (UTC), path — or three empty fields and the
    /// path when the file is not there.</summary>
    private static readonly string Prelude =
        HealthCommands.ModulePathPin + "; " + StorageImport + "; " + $$"""$ProgressPreference='SilentlyContinue'; $ErrorActionPreference='Stop'; $o=New-Object System.Collections.Generic.List[string]; function V([string]$f){ $t=''; if($f -match '^[A-Za-z]:\\'){ $t=[string]([IO.DriveInfo]::new($f.Substring(0,1))).DriveType }; if($t -ne 'Fixed'){ return '{{NotLocalMark}}|||'+$f }; $i=Get-Item -LiteralPath $f -Force -ErrorAction SilentlyContinue; if($i){ [string]$i.Length+'|'+[string][int](($i.Attributes -band {{SparseAttribute}}) -ne 0)+'|'+$i.LastWriteTimeUtc.ToString('o')+'|'+$f } else { '|||'+$f } }""";

    /// <summary>Every registered distro from <c>HKCU\…\Lxss</c>: its virtual disk's file (<c>BasePath</c> without the <c>\\?\</c>
    /// prefix, <c>VhdFileName</c> or <c>ext4.vhdx</c>) and its name, LAST (a free-text field takes the rest of the line).</summary>
    private static readonly string Distros =
        $$"""try { foreach($k in @(Get-ChildItem -LiteralPath 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Lxss')){ $p=Get-ItemProperty -LiteralPath $k.PSPath; $b=([string]$p.BasePath) -replace '^\\\\\?\\',''; $n=[string]$p.VhdFileName; if($n -eq ''){ $n='ext4.vhdx' }; $o.Add('{{DistroTag}}'+(V ([IO.Path]::Combine($b,$n)))+'|'+$p.DistributionName) } } catch { $o.Add('{{DistrosErrorTag}}'+$_.Exception.GetType().Name) }""";

    /// <summary>Docker Desktop's data disk at its default path — it is no registered distro, so Lxss does not list it (D10).</summary>
    private static readonly string Docker =
        $$"""try { $o.Add('{{DockerTag}}'+(V ([IO.Path]::Combine([string]$env:LOCALAPPDATA,'Docker\wsl\disk\docker_data.vhdx')))) } catch { $o.Add('{{DockerErrorTag}}'+$_.Exception.GetType().Name) }""";

    /// <summary>Each lettered volume: letter, disk number (<c>Get-Partition</c>; empty when it has none), filesystem, size, free,
    /// label LAST.</summary>
    private static readonly string Volumes =
        $$"""try { foreach($v in @(Get-Volume | Where-Object { $_.DriveLetter })){ $d=''; try { $d=[string](Get-Partition -DriveLetter $v.DriveLetter).DiskNumber } catch { $d='{{FailedMark}}'+$_.Exception.GetType().Name }; $o.Add('{{VolumeTag}}'+$v.DriveLetter+'|'+$d+'|'+$v.FileSystem+'|'+$v.Size+'|'+$v.SizeRemaining+'|'+$v.FileSystemLabel) } } catch { $o.Add('{{VolumesErrorTag}}'+$_.Exception.GetType().Name) }""";

    /// <summary>Each physical disk: number, bus, health, operational status, firmware, model LAST.</summary>
    private static readonly string Disks =
        $$"""try { foreach($p in @(Get-PhysicalDisk)){ $o.Add('{{DiskTag}}'+$p.DeviceId+'|'+$p.BusType+'|'+$p.HealthStatus+'|'+$p.OperationalStatus+'|'+$p.FirmwareVersion+'|'+$p.FriendlyName) } } catch { $o.Add('{{DisksErrorTag}}'+$_.Exception.GetType().Name) }""";

    /// <summary>The reliability counters, attempted: unelevated they are refused (D4), and the refusal is the answer recorded.</summary>
    private static readonly string Reliability =
        $$"""try { foreach($r in @(Get-PhysicalDisk | Get-StorageReliabilityCounter)){ $o.Add('{{ReliabilityTag}}'+$r.DeviceId+'|'+$r.Temperature+'|'+$r.Wear+'|'+$r.ReadErrorsTotal+'|'+$r.WriteErrorsTotal+'|'+$r.PowerOnHours) }; $o.Add('{{ReliabilityReadTag}}1') } catch { $o.Add('{{ReliabilityRefusedTag}}'+$_.Exception.GetType().Name) }""";

    /// <summary>The storage events, newest first and at most <paramref name="max"/>, AGGREGATED inside the script per UTC day,
    /// provider, id and <c>HarddiskN</c> (from the event's data, else its message) — one line per event was 64 of 75 lines (D10).
    /// "No events" is a count of 0, never an error.</summary>
    private static IEnumerable<string> Events(string days, string max, string groups) =>
    [
        $$"""$e=$null; try { $e=@(Get-WinEvent -MaxEvents {{max}} -FilterHashtable @{ LogName='System'; ProviderName={{Quoted(EventProviders)}}; Id={{Numbers(EventIds)}}; StartTime=(Get-Date).AddDays(-{{days}}) }) } catch { if($_.FullyQualifiedErrorId -like 'NoMatchingEventsFound*'){ $e=@() } else { $o.Add('{{EventsErrorTag}}'+$_.Exception.GetType().Name) } }""",
        $$"""if($null -ne $e){ $o.Add('{{EventsReadTag}}'+$e.Count); $gs=@($e | Group-Object { $t=(@($_.Properties | ForEach-Object { [string]$_.Value }) -join ' '); $h=[regex]::Match($t,'Harddisk\d+').Value; if($h -eq ''){ $h=[regex]::Match([string]$_.Message,'Harddisk\d+').Value }; $_.TimeCreated.ToUniversalTime().ToString('yyyy-MM-dd',[Globalization.CultureInfo]::InvariantCulture)+'|'+$_.ProviderName+'|'+$_.Id+'|'+$h } | Sort-Object Name -Descending); $o.Add('{{EventGroupsTag}}'+$gs.Count); foreach($g in @($gs | Select-Object -First {{groups}})){ $o.Add('{{EventTag}}'+$g.Name+'|'+$g.Count) } }""",
    ];

    /// <summary>Commit charge and commit limit, in bytes (the raw counters, not the formatted ones).</summary>
    private static readonly string Commit =
        $$"""try { $m=Get-CimInstance -ClassName Win32_PerfRawData_PerfOS_Memory; $o.Add('{{CommitTag}}'+$m.CommittedBytes+'|'+$m.CommitLimit) } catch { $o.Add('{{CommitErrorTag}}'+$_.Exception.GetType().Name) }""";

    /// <summary>The Memory Compression process's working set, in bytes.</summary>
    private static readonly string Compression =
        $$"""try { $o.Add('{{CompressionTag}}'+@(Get-Process -Name 'Memory Compression')[0].WorkingSet64) } catch { $o.Add('{{CompressionErrorTag}}'+$_.Exception.GetType().Name) }""";

    /// <summary>The edition, the display version, the build with its update revision, the install date (unix seconds) and the
    /// account the script runs as (its SID) — interop runs it as the Windows user who started the VM, whose hive HKCU is.</summary>
    private static readonly string Identity =
        $$"""try { $w=Get-ItemProperty -LiteralPath 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion'; $o.Add('{{EditionTag}}'+$w.EditionID); $o.Add('{{VersionTag}}'+$w.DisplayVersion); $o.Add('{{BuildTag}}'+$w.CurrentBuild+'.'+$w.UBR); $o.Add('{{InstallDateTag}}'+$w.InstallDate); $o.Add('{{AccountTag}}'+[Security.Principal.WindowsIdentity]::GetCurrent().User.Value) } catch { $o.Add('{{WindowsErrorTag}}'+$_.Exception.GetType().Name) }""";

    /// <summary>The machine's id — every statistics record will carry it (PLAN_disk_health.md §3.2).</summary>
    private static readonly string Machine =
        $$"""try { $o.Add('{{MachineGuidTag}}'+(Get-ItemProperty -LiteralPath 'HKLM:\SOFTWARE\Microsoft\Cryptography').MachineGuid) } catch { $o.Add('{{MachineErrorTag}}'+$_.Exception.GetType().Name) }""";

    /// <summary>Each display adapter: its device manager problem code, its PNP device id, its name LAST (for the GPU story, G2).</summary>
    private static readonly string Adapters =
        $$"""try { foreach($a in @(Get-CimInstance -ClassName Win32_VideoController)){ $o.Add('{{AdapterTag}}'+$a.ConfigManagerErrorCode+'|'+$a.PNPDeviceID+'|'+$a.Name) } } catch { $o.Add('{{AdaptersErrorTag}}'+$_.Exception.GetType().Name) }""";

    /// <summary>The end line, then everything in ONE write, LF-separated, as UTF-8 bytes on the raw stdout stream: Windows
    /// PowerShell 5.1 writes <c>Console.Out</c> in the OEM code page, so a Cyrillic volume label or user name would reach the
    /// daemon's UTF-8 reader as U+FFFD (own review). <c>OutputEncoding</c> is not set — it can throw with no console attached.</summary>
    private static readonly string Print =
        $$"""$o.Add('{{EndLine}}'); $u=[Text.Encoding]::UTF8.GetBytes(($o -join [char]10)+[char]10); $s=[Console]::OpenStandardOutput(); $s.Write($u,0,$u.Length); $s.Flush()""";
}
