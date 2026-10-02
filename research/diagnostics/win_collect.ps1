# Read-only Windows diagnostics for the "laggy after 3 months" question. Nothing is changed.
$ErrorActionPreference = 'SilentlyContinue'
function S($t) { "`n===== $t =====" }
function SizeGB($p) { if (Test-Path -LiteralPath $p) { $s = (Get-ChildItem -LiteralPath $p -Recurse -File -Force -ErrorAction SilentlyContinue | Measure-Object Length -Sum).Sum; [math]::Round($s/1GB, 2) } else { $null } }
function Cnt($p) { if (Test-Path -LiteralPath $p) { (Get-ChildItem -LiteralPath $p -Force -ErrorAction SilentlyContinue).Count } else { $null } }

S "OS"
$os = Get-CimInstance Win32_OperatingSystem
"Caption: $($os.Caption) $($os.Version) build $($os.BuildNumber)"
"InstallDate: $($os.InstallDate)   LastBoot: $($os.LastBootUpTime)   Uptime: $((Get-Date) - $os.LastBootUpTime)"
"RAM total {0:N1} GB, free {1:N1} GB; commit {2:N1}/{3:N1} GB" -f ($os.TotalVisibleMemorySize/1MB), ($os.FreePhysicalMemory/1MB), (($os.TotalVirtualMemorySize-$os.FreeVirtualMemory)/1MB), ($os.TotalVirtualMemorySize/1MB)
"Is admin: " + ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)

S "Perf counters (now)"
(Get-Counter '\Memory\Available MBytes','\Memory\Committed Bytes','\Memory\Pool Nonpaged Bytes','\Memory\Pool Paged Bytes','\Memory\Standby Cache Normal Priority Bytes','\Memory\Modified Page List Bytes','\Process(_Total)\Handle Count','\System\Processes','\System\Threads','\Processor(_Total)\% Processor Time','\PhysicalDisk(_Total)\Avg. Disk Queue Length').CounterSamples | ForEach-Object { "{0,-60} {1,16:N0}" -f $_.Path, $_.CookedValue }

S "Top 25 processes by private memory"
Get-Process | Sort-Object PrivateMemorySize64 -Descending | Select-Object -First 25 Name, Id, @{n='PrivGB';e={[math]::Round($_.PrivateMemorySize64/1GB,2)}}, @{n='WSGB';e={[math]::Round($_.WorkingSet64/1GB,2)}}, Handles, @{n='Threads';e={$_.Threads.Count}}, @{n='CPUs';e={[math]::Round($_.CPU,0)}}, StartTime | Format-Table -AutoSize | Out-String -Width 200

S "Process counts by name (top 25) and memory per group"
Get-Process | Group-Object Name | Sort-Object Count -Descending | Select-Object -First 25 Count, Name, @{n='PrivGB';e={[math]::Round(($_.Group | Measure-Object PrivateMemorySize64 -Sum).Sum/1GB,2)}}, @{n='Handles';e={($_.Group | Measure-Object Handles -Sum).Sum}} | Format-Table -AutoSize | Out-String -Width 200

S "Processes with > 5000 handles"
Get-Process | Where-Object Handles -gt 5000 | Sort-Object Handles -Descending | Select-Object Name, Id, Handles, StartTime | Format-Table -AutoSize

S "Startup commands (Run keys, Startup folders)"
Get-CimInstance Win32_StartupCommand | Select-Object Name, Location, @{n='Cmd';e={$_.Command.Substring(0,[math]::Min(110,$_.Command.Length))}} | Format-Table -AutoSize -Wrap | Out-String -Width 220
"StartupApproved disabled entries:"; foreach ($k in 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run','HKLM:\Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run') { $i = Get-ItemProperty $k; if ($i) { $i.PSObject.Properties | Where-Object { $_.Name -notlike 'PS*' -and $_.Value[0] -ne 2 } | ForEach-Object { "  $($_.Name)" } } }

S "Non-Microsoft auto-start services (running / stopped)"
Get-CimInstance Win32_Service | Where-Object { $_.StartMode -eq 'Auto' -and $_.PathName -notmatch '\\Windows\\(system32|SysWOW64)\\(svchost|lsass|services)' -and $_.PathName -notmatch 'Microsoft|Windows Defender' } | Select-Object Name, State, @{n='Path';e={$_.PathName.Substring(0,[math]::Min(90,$_.PathName.Length))}} | Sort-Object State, Name | Format-Table -AutoSize | Out-String -Width 220

S "Non-Microsoft scheduled tasks (enabled)"
Get-ScheduledTask | Where-Object { $_.State -ne 'Disabled' -and $_.TaskPath -notlike '\Microsoft\*' } | ForEach-Object { $i = $_ | Get-ScheduledTaskInfo; [pscustomobject]@{ Task = ($_.TaskPath + $_.TaskName); Last = $i.LastRunTime; Result = $i.LastTaskResult; Next = $i.NextRunTime } } | Format-Table -AutoSize | Out-String -Width 220

S "Installed programs: count, newest 25"
$apps = foreach ($k in 'HKLM:\Software\Microsoft\Windows\CurrentVersion\Uninstall\*','HKLM:\Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\*','HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\*') { Get-ItemProperty $k | Where-Object DisplayName | Select-Object DisplayName, DisplayVersion, InstallDate, @{n='MB';e={[math]::Round($_.EstimatedSize/1KB,0)}} }
"count: $($apps.Count)"; $apps | Sort-Object InstallDate -Descending | Select-Object -First 25 | Format-Table -AutoSize | Out-String -Width 200
"duplicates (several versions installed):"; $apps | Group-Object { ($_.DisplayName -replace '[\d\.]+','').Trim() } | Where-Object Count -gt 2 | Select-Object Count, Name -First 15 | Format-Table -AutoSize

S "Volumes"
Get-Volume | Where-Object DriveLetter | Select-Object DriveLetter, FileSystemLabel, FileSystemType, @{n='SizeGB';e={[math]::Round($_.Size/1GB,0)}}, @{n='FreeGB';e={[math]::Round($_.SizeRemaining/1GB,0)}}, HealthStatus | Format-Table -AutoSize
"Dev Drive query:"; foreach ($d in 'C:','D:','F:') { "$d " + ((fsutil devdrv query $d 2>&1) -join ' ') }
"Physical disks:"; Get-PhysicalDisk | Select-Object FriendlyName, MediaType, BusType, @{n='GB';e={[math]::Round($_.Size/1GB,0)}}, HealthStatus | Format-Table -AutoSize

S "Folder sizes (GB) and counts"
$h = $env:USERPROFILE; $la = $env:LOCALAPPDATA; $ra = $env:APPDATA
$paths = [ordered]@{
 'User TEMP' = $env:TEMP; 'Windows\Temp' = "$env:windir\Temp"; 'SoftwareDistribution\Download' = "$env:windir\SoftwareDistribution\Download"
 'Windows\Installer' = "$env:windir\Installer"; 'Windows\Logs' = "$env:windir\Logs"; 'LiveKernelReports' = "$env:windir\LiveKernelReports"; 'Minidump' = "$env:windir\Minidump"
 'CrashDumps (user)' = "$la\CrashDumps"; 'WER (ProgramData)' = 'C:\ProgramData\Microsoft\Windows\WER'; 'WER (user)' = "$la\Microsoft\Windows\WER"
 'Delivery Optimization' = "$env:windir\ServiceProfiles\NetworkService\AppData\Local\Microsoft\Windows\DeliveryOptimization"
 'Search index' = 'C:\ProgramData\Microsoft\Search\Data'; 'Windows.old' = 'C:\Windows.old'; 'Downloads' = "$h\Downloads"
 'npm-cache' = "$la\npm-cache"; 'NuGet packages' = "$h\.nuget\packages"; 'NuGet v3-cache' = "$la\NuGet\v3-cache"; 'pip cache' = "$la\pip\Cache"
 'yarn cache' = "$la\Yarn\Cache"; 'pnpm store' = "$la\pnpm"; 'ms-playwright' = "$la\ms-playwright"; 'Docker (local)' = "$la\Docker"
 'VS Code CachedData' = "$ra\Code\CachedData"; 'VS Code workspaceStorage' = "$ra\Code\User\workspaceStorage"; 'VS Code globalStorage' = "$ra\Code\User\globalStorage"
 'VS Code logs' = "$ra\Code\logs"; 'VS Code CachedExtensionVSIXs' = "$ra\Code\CachedExtensionVSIXs"; 'VS Code Cache' = "$ra\Code\Cache"; 'VS Code extensions' = "$h\.vscode\extensions"
 'Edge cache' = "$la\Microsoft\Edge\User Data\Default\Cache"; 'Chrome cache' = "$la\Google\Chrome\User Data\Default\Cache"
 'Teams' = "$la\Packages\MSTeams_8wekyb3d8bbwe"; 'JetBrains' = "$la\JetBrains"; 'Ollama models' = 'F:\OllamaModels'
 '.claude' = "$h\.claude"; 'AnthropicClaude' = "$la\AnthropicClaude"; '.codex' = "$h\.codex"; '.gemini' = "$h\.gemini"
}
foreach ($k in $paths.Keys) { $p = $paths[$k]; $g = SizeGB $p; if ($null -ne $g) { "{0,-32} {1,9:N2} GB {2,8} entries  {3}" -f $k, $g, (Cnt $p), $p } }
"TEMP files older than 7 days: " + (Get-ChildItem $env:TEMP -Recurse -File -Force | Where-Object LastWriteTime -lt (Get-Date).AddDays(-7) | Measure-Object Length -Sum | ForEach-Object { "{0} files, {1:N2} GB" -f $_.Count, ($_.Sum/1GB) })
"VS Code extension folders: $(Cnt "$h\.vscode\extensions"); old versions (same id, several versions):"; Get-ChildItem "$h\.vscode\extensions" -Directory | Group-Object { $_.Name -replace '-\d+\.\d+\.\d+.*$','' } | Where-Object Count -gt 1 | Select-Object Count, Name -First 15 | Format-Table -AutoSize
"hiberfil / pagefile / swapfile:"; Get-ChildItem C:\hiberfil.sys, C:\pagefile.sys, C:\swapfile.sys -Force | Select-Object Name, @{n='GB';e={[math]::Round($_.Length/1GB,1)}} | Format-Table -AutoSize
"Recycle bin (C:):"; SizeGB 'C:\$Recycle.Bin'

S "Defender"
$mp = Get-MpComputerStatus; if ($mp) { $mp | Select-Object AMRunningMode, RealTimeProtectionEnabled, IsTamperProtected, QuickScanEndTime, FullScanEndTime, AntivirusSignatureLastUpdated | Format-List }
$pref = Get-MpPreference; if ($pref) { "ExclusionPath: " + ($pref.ExclusionPath -join '; '); "ExclusionProcess: " + ($pref.ExclusionProcess -join '; '); "ScanAvgCPULoadFactor: $($pref.ScanAvgCPULoadFactor)"; "EnableLowCpuPriority: $($pref.EnableLowCpuPriority)" }
Get-Process MsMpEng | Select-Object Name, @{n='CPUs';e={[math]::Round($_.CPU,0)}}, @{n='WSMB';e={[math]::Round($_.WorkingSet64/1MB,0)}} | Format-Table -AutoSize

S "Search indexer"
Get-Service WSearch | Select-Object Status, StartType | Format-Table -AutoSize
Get-ChildItem 'C:\ProgramData\Microsoft\Search\Data\Applications\Windows' -Force -File | Select-Object Name, @{n='GB';e={[math]::Round($_.Length/1GB,2)}} | Format-Table -AutoSize

S "Power"
powercfg /getactivescheme; "Hibernate/FastStartup:"; (Get-ItemProperty 'HKLM:\SYSTEM\CurrentControlSet\Control\Session Manager\Power').HiberbootEnabled
"Game mode / visual effects (VisualFXSetting):"; (Get-ItemProperty 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Explorer\VisualEffects').VisualFXSetting
"Memory compression / prefetch:"; Get-MMAgent | Format-List

S "GPU / drivers"
Get-CimInstance Win32_VideoController | Select-Object Name, DriverVersion, DriverDate, @{n='VRAM_GB';e={[math]::Round($_.AdapterRAM/1GB,1)}} | Format-Table -AutoSize | Out-String -Width 200
"Problem devices:"; Get-PnpDevice -PresentOnly | Where-Object Status -ne 'OK' | Select-Object Class, FriendlyName, Status | Format-Table -AutoSize | Out-String -Width 200

S "Event log: System errors+warnings since log start, by provider/id"
Get-WinEvent -FilterHashtable @{LogName='System'; Level=1,2,3} | Group-Object ProviderName, Id | Sort-Object Count -Descending | Select-Object -First 25 Count, Name, @{n='Last';e={$_.Group[0].TimeCreated}}, @{n='Msg';e={$m=$_.Group[0].Message; if($m){$m.Substring(0,[math]::Min(140,$m.Length)) -replace "`r?`n",' '}}} | Format-Table -AutoSize -Wrap | Out-String -Width 260
S "Event log: Application errors by provider/id"
Get-WinEvent -FilterHashtable @{LogName='Application'; Level=1,2} | Group-Object ProviderName, Id | Sort-Object Count -Descending | Select-Object -First 20 Count, Name, @{n='Last';e={$_.Group[0].TimeCreated}}, @{n='Msg';e={$m=$_.Group[0].Message; if($m){$m.Substring(0,[math]::Min(140,$m.Length)) -replace "`r?`n",' '}}} | Format-Table -AutoSize -Wrap | Out-String -Width 260
S "Display driver resets (4101), disk/ntfs, WHEA"
Get-WinEvent -FilterHashtable @{LogName='System'; Id=4101} | Measure-Object | ForEach-Object { "TDR 4101: $($_.Count)" }
Get-WinEvent -FilterHashtable @{LogName='System'; ProviderName='disk','Ntfs','stornvme','storahci','Microsoft-Windows-WHEA-Logger'} | Group-Object ProviderName, Id | Select-Object Count, Name | Format-Table -AutoSize

S "Diagnostics-Performance (boot/shutdown/standby degradation)"
Get-WinEvent -LogName 'Microsoft-Windows-Diagnostics-Performance/Operational' -MaxEvents 400 | Group-Object Id | Select-Object Count, Name, @{n='Last';e={$_.Group[0].TimeCreated}} | Format-Table -AutoSize
Get-WinEvent -FilterHashtable @{LogName='Microsoft-Windows-Diagnostics-Performance/Operational'; Id=100} -MaxEvents 15 | ForEach-Object { $x=[xml]$_.ToXml(); $d=@{}; $x.Event.EventData.Data | ForEach-Object { $d[$_.Name]=$_.'#text' }; "{0}  boot {1,6} ms  main-path {2,6} ms  post-boot {3,6} ms" -f $_.TimeCreated, $d['BootTime'], $d['MainPathBootTime'], $d['BootPostBootTime'] }
"Degrading apps/services (101-110, 200-203):"
Get-WinEvent -FilterHashtable @{LogName='Microsoft-Windows-Diagnostics-Performance/Operational'; Id=101,102,103,106,109,110,203} -MaxEvents 300 | ForEach-Object { $x=[xml]$_.ToXml(); $d=@{}; $x.Event.EventData.Data | ForEach-Object { $d[$_.Name]=$_.'#text' }; "{0}|{1}" -f $_.Id, ($d['FriendlyName'] + $d['Name'] + $d['FileName']) } | Group-Object | Sort-Object Count -Descending | Select-Object -First 20 Count, Name | Format-Table -AutoSize | Out-String -Width 200

S "Reliability stability index (last 30 days)"
Get-CimInstance Win32_ReliabilityStabilityMetrics | Sort-Object TimeGenerated -Descending | Select-Object -First 30 | ForEach-Object { "{0:yyyy-MM-dd} {1:N2}" -f $_.TimeGenerated, $_.SystemStabilityIndex }

S "Pending reboot / update state"
"RebootPending (CBS): " + (Test-Path 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Component Based Servicing\RebootPending')
"WU RebootRequired: " + (Test-Path 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\WindowsUpdate\Auto Update\RebootRequired')
