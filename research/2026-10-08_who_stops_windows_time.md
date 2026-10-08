# Who reconfigures the Windows Time service, who stops it, and why Windows boots 2 h slow — measured 2026-10-08

> The question the two earlier records of the day left open
> ([2026-10-08_windows_time_stopped.md](2026-10-08_windows_time_stopped.md) §3,
> [2026-10-08_windows_time_guard_trigger.md](2026-10-08_windows_time_guard_trigger.md) §3): WHO set `w32time`'s start type to
> *disabled* and back at ≈ 06:00Z and ≈ 09:50Z, who stops the service in the evenings, and why the clock is 2 h behind after
> some boots. Every read below is **read-only and unelevated** (`Get-WinEvent`, `Get-ItemProperty`, `Get-CimInstance`,
> `Get-ScheduledTask`/`Export-ScheduledTask`, `Get-Partition`, `sc.exe qc|sdshow|qtriggerinfo`, reading two system binaries'
> bytes, and one WSL probe script of `ls`/`cat`/`timedatectl`). No service, registry value, driver, task, firmware setting or
> process was changed, started, stopped or ended; no UAC prompt was raised. Nothing recommended in §7 was applied.
>
> - **Machine:** the same as the two earlier records — Windows 11 Pro 10.0.26300 (kernel build 26100, QFE 9549), time zone
>   *Romance Standard Time* (CEST, UTC+2 on this date), a mini PC with an AMD Ryzen AI 9 HX 370 (vendor string *Micro Computer
>   (HK) Tech Limited*, model *AI Series*), AMI firmware 1.06 dated 2026-01-05, VBS on (`HypervisorPresent = True`), WSL 2
>   (kernel 6.18.33.2-microsoft-standard-WSL2). The owner's account is written *the owner's account*, its SID
>   `S-1-5-21-…-1001`; the machine name is left out (this repository is public).
> - **When:** 2026-10-08, ≈ 16:20–16:50Z, on `origin/main` at `a7df216`.
> - **Clock caveat that governs every table:** an event is stamped with Windows' clock AT THE TIME, and on 2026-10-08 that clock
>   was 2 h slow from each boot until its correction. So "06:00Z" and "09:50Z" are Windows' wrong clock (true time ≈ 08:00Z and
>   ≈ 11:50Z), and the events of two boots interleave when sorted by time. Every sequence below is ordered by **RecordId**,
>   never by timestamp.
> - **Hypotheses carried in, before these reads** (the brief and the incident record's §3): (H1) AMD driver software stops or
>   reconfigures `w32time`; (H2) the firmware clock (RTC) holds UTC while Windows reads it as local time.

## 1. The start-type changes: Service Control Manager event 7040

| # | Read | Value |
|---|---|---|
| W1 | every SCM event in the System log (oldest record 2026-10-05T13:20Z, circular) | 32: **7040 × 28**, 7026 × 4; no 7036 (as the trigger record found) |
| W2 | the 24 that are not W32Time | `IsolationSession` and `BITS` (Background Intelligent Transfer Service) flipping between auto and demand start, every one by `S-1-5-18` (SYSTEM) |
| W3 | the four for W32Time (`param4 = W32Time`), RecordId order | rec 257828 **06:00:19.368** demand start → **disabled**; rec 257829 **06:00:20.965** disabled → demand start — both `ProcessId 2308` (that boot's `services.exe`), `UserID S-1-5-21-…-1001`. rec 258308 **09:50:40.018** demand start → disabled; rec 258310 **09:50:41.674** disabled → demand start — `ProcessId 2096` (the current boot's `services.exe`, `Get-Process services`), same UserID |
| W4 | how far apart each pair is | 1.60 s and 1.66 s |
| W5 | the same minutes in other logs, RecordId order (06:00 pair) | 7040 → disabled · 7040 → demand · Time-Service 272/**257** start 06:00:21.000 · **266 reason 0** 06:00:21.043 (*"An explicit time resynchronization request from an administrator"*) · 264 · **261 the +7 200.59 s step** (Kernel-General 1 at true 08:00:23.19Z). The service had been stopped since the evening before, so there is no 258 |
| W6 | (09:50 pair) | 7040 → disabled 09:50:40.018 · **258 stop** 09:50:40.022 · 7040 → demand 09:50:41.674 · 257 start 09:50:41.713 · **266 reason 0** 09:50:41.750 · **261 the +7 199.61 s step** (true 11:50:41.40Z) |
| W7 | `w32time` service DACL (`sc.exe sdshow w32time`) | change-config (`DC`) only for Administrators (`BA`) and the service's own SID; Interactive Users (`IU`) and Service Users (`SU`) may only query it — so the 7040 pairs were made by an ELEVATED process in the owner's account (an elevated token keeps the user's SID) |

The SCM records only the caller's account, never its process (`ProcessId` is `services.exe` itself). The next section is
how the process was found.

## 2. What ran in those seconds — Windows Settings' *Sync now*

| # | Read | Value |
|---|---|---|
| S1 | `Microsoft-Windows-Application-Experience/Program-Telemetry` (span 2026-09-17 … now, 1 540 records): its 500/505 *"Compatibility fix applied to …"* is written when a listed process starts | `C:\Windows\ImmersiveControlPanel\SystemSettings.exe` (Windows **Settings**) started **06:00:09.392** (10 s before W3's first flip), then `DllHost.exe` (the COM surrogate an elevated COM object runs in) at 06:00:12.857; and **09:50:25.267** in the second boot (15 s before; RecordId 5473, after that log's boot boundary at 5463 → 5464) |
| S2 | the same log at the 2026-10-05 correction (the System log no longer reaches it) | Settings started **06:03:50.774**; Time-Service 257 start 06:03:59.697, 266 **reason 0** 06:03:59.746, then the **+7 200.06 s** step — the same shape 9 s after a Settings start |
| S3 | the 2026-09-29 correction | 258 stop 13:44:45.512, 257 start 13:44:47.753, 266 reason 0 13:44:47.803, +7 200.13 s. The nearest Settings start is 12:12:47 — Settings can stay open, so this one is consistent but **not attributable** |
| S4 | Settings starts on ordinary days | one or two a day since 09-17, usually within an hour of the morning boot — a Settings start is NOT unusual by itself; what ties S1–S2 to the flips is the seconds, the account (W7) and S5–S6 |
| S5 | `C:\Windows\System32\SettingsHandlers_ForceSync.dll` — version resource and UTF-16 strings | *"System Settings Sync Time Handler Implementation"* (10.0.26100.8117); strings `ForceTimeSync %d`, `StartW32Time`, `SyncW32Time`, the `W32Time\Parameters` and `W32Time\Config` keys |
| S6 | `C:\Windows\System32\SystemSettingsAdminFlows.exe` — its manifest and strings | `requestedExecutionLevel level="requireAdministrator"`, **`<autoElevate>true</autoElevate>`**; strings `ForceTimeSync`, `StartW32Time`, `SyncW32Time`, `w32time`, and the import **`ChangeServiceConfigW`** |
| S7 | UAC policy (`HKLM\…\Policies\System`) | `EnableLUA 1`, `ConsentPromptBehaviorAdmin 5`, `PromptOnSecureDesktop 1` — an auto-elevating Windows binary is elevated **without a prompt** for an administrator |
| S8 | PowerShell in the same seconds (`Windows PowerShell` 400/403 with `HostApplication`, `Microsoft-Windows-PowerShell/Operational` 40961) | engines start every ≈ 30 s (the agents' own probes: `[DateTime]::UtcNow …`, a `RealTimeIsUniversal` read, `Get-AppxPackage`, `Get-WinUserLanguageList`); **none** starts between 06:00:18.29 and 06:00:22, or between 09:50:26.6 and 09:50:42. No PowerShell 7 log exists. So no NEW PowerShell ran a `Set-Service`; an elevated console already open, `sc.exe`, the Services console or another native program would leave no trace here |
| S9 | what could not be read | the Security log (`UnauthorizedAccessException` — no process-creation 4688 to read); `Microsoft-Windows-TaskScheduler/Operational` is **disabled** (`IsEnabled False`), so task runs are not logged at all; no Sysmon. Tasks visible to the unelevated reader: none ran in those minutes (`Get-ScheduledTaskInfo` last-run times), with the trigger record's caveat that SYSTEM tasks in their own folders are not visible (its T21) |

**Verdict on WHO changed the start type — strongly consistent with the Windows Settings app's Sync now** (Time & language →
Date & time) in the owner's session, and not proven. The Settings handler for that button hands its work to
`SystemSettingsAdminFlows.exe` (S5–S6), which elevates without a prompt on this UAC policy (S7) and carries the API that writes
a 7040 (`ChangeServiceConfigW`) and the strings `StartW32Time`/`SyncW32Time`; a Settings start came 9–15 s before each of the
three attributable corrections (S1, S2); and the observed sequence — start type → disabled, a stop of the running service (W6;
a start-type change alone stops nothing, so the stop is a separate request), → demand start, a start, an explicit resync
(reason 0), the +7 200 s step, all in under 2 s — is a repair flow. Whoever ran it, **that flip is the REPAIR, not the fault**:
every 2 h correction in the log record (W5, W6, S2; S3 consistent) is one, and each came AFTER the clock was already wrong.

**What this does not settle:** the binaries' strings and imports show what the Settings flow CAN do, not that it ran; no
trace was taken (reproducing one Sync now under a process trace would show whether its sequence matches W3–W6 — capability,
still not attribution). Not excluded by anything readable here: an elevated console already open, `sc.exe`/`net`/`w32tm` from
a script, the Services console, another native tool in the owner's account (S8, S9). Whether a person or something else drove
it is not recorded anywhere an unelevated reader can see. The 06:00Z pair falls in the minute the incident record calls the
owner's manual restart (its O8); a plain `Start-Service` writes no 7040, so that minute was not only a `Start-Service`.

**H1 (AMD) for the flips — not supported.** The two AMD services run as `LocalSystem` (§6), so a change by them is logged as
`S-1-5-18`, as W2's 24 are; the four W32Time changes carry the owner's SID. An AMD program running elevated in the owner's
session is not excluded by the log, but nothing in §6 points at one.

## 3. The evening stops are the service stopping itself

From `Microsoft-Windows-Time-Service/Operational` (oldest record 2026-08-31), every 257 start, 258 stop, 261 set and 266 resync
since 09-27, RecordId order:

| Start (257) | Stop (258) | Stop − start | What precedes the stop |
|---|---|---|---|
| 09-27 16:09:21.045 | 20:42:25.181 | 16 384.1 s | the service's own 261 set, 2 ms earlier |
| 09-28 15:30:07.761 | 20:03:27.887 | 16 400.1 s | 261, 3 ms earlier |
| 09-30 16:33:14.157 | 21:06:34.283 | 16 400.1 s | 261, 3 ms earlier |
| 10-02 17:31:56.587 | 22:05:03.597 | 16 387.0 s | 261 ×3 + 266 reason 3, ≤ 5 ms earlier |
| 10-03 16:04:08.139 | 20:37:28.276 | 16 400.1 s | 261, 3 ms earlier |
| 10-04 16:12:40.857 | 20:46:01.012 | 16 400.2 s | 261, 3 ms earlier |
| 10-06 15:31:29.397 | 20:04:33.529 | 16 384.1 s | 261, 2 ms earlier |
| 10-07 15:34:11.033 | 20:07:31.254 | 16 400.2 s | 261, 5 ms earlier |

- Every evening stop comes **one `SpecialPollInterval` (16 384 s, `W32Time\TimeProviders\NtpClient`) after the start**, a few
  milliseconds after the service's own periodic set, with return code `Success`. `sc.exe qtriggerinfo w32time` lists two START
  triggers and no stop trigger (as the trigger record's T20). Nothing outside the service is involved: it stops itself after
  its periodic sync when it was started on demand.
- The afternoon starts (15:30–17:32Z) are consistent with `\Microsoft\Windows\Time Synchronization\SynchronizeTime` (action
  `%windir%\system32\sc.exe start w32time task_started`; last run today 15:35:31Z, result 1056 = *already running*); its
  trigger is not readable here, so this is consistency, not proof.
- A run started by a Sync now (09-29 13:44, 10-05 06:03), or one that a shutdown ends before the interval passes (10-01 21:15),
  shows no evening stop.
- **H1 (AMD) for the stops — ruled out**: the stops are the service's own. "Stopped" after a boot is the state it stopped itself
  into the evening before; the 10-08 05:43Z boot had it stopped until the repair flow at 06:00:21 (no 257 in between).

## 4. The firmware clock: the RTC held UTC at both measured slow boots

| # | Read | Value |
|---|---|---|
| F1 | `HKLM\SYSTEM\CurrentControlSet\Control\TimeZoneInformation` | `RealTimeIsUniversal` **absent**; `TimeZoneKeyName Romance Standard Time`, `Bias −60`, `ActiveTimeBias −120`, `DaylightBias −60`, `DynamicDaylightTimeDisabled 0` (the DWORDs read back as unsigned: 4294967236 = −60, 4294967176 = −120) |
| F2 | how Windows WRITES the RTC: every Kernel-General 1 (*system time changed*) in the System log | `CmosTime` = `NewTime` **+ 2 h** each time, `TimeZoneBias −120`, `RealTimeIsUniversal false`, `SystemInCmosMode false`, `ProcessName …\svchost.exe` (the Time service's host). E.g. 10-08 08:00:23.19Z → `CmosTime 10:00:23.19`, `TimeDeltaInMs 7200590`; 11:50:41.40Z → `CmosTime 13:50:41.40`, `TimeDeltaInMs 7199607`; the small evening sets on 10-05, 10-06, 10-07 likewise. Windows keeps the RTC in **local** time |
| F3 | every step larger than 60 s in the Time-Service log since 08-31 (47 sets) | exactly four: 09-29 +7 200.1 s, 10-05 +7 200.1 s, 10-08 +7 200.6 s, 10-08 +7 199.6 s. Boots in the same span (Kernel-Boot/Operational 85): **54** — so 4 of 54 boots came up 2 h slow |

What the firmware handed Windows at each boot the System log still holds (Kernel-Boot 238 *"EFI time zone bias: 2047 …
Firmware time: …"* — 2047 is EFI's *unspecified time zone*, so the printed value is the RTC's raw wall time, whatever the `Z` in
the message says):

| Boot — Windows' clock at start (Kernel-General 12) | RTC value read (Kernel-Boot 238) | True UTC at that moment | So the RTC held | The shutdown before it | Corrected later |
|---|---|---|---|---|---|
| 10-06 07:20:04.5Z | 09:20:02 | 07:20:04.5 (never corrected) | **local** (UTC + 2 h) | clean: User32 1074 *power off* from the Start menu 10-05 21:57:41Z, Kernel-General 13 at 21:58:00; Kernel-Boot 20 *"last shutdown's success status was true"* | no |
| 10-07 07:33:45.5Z | 09:33:43 | 07:33:45.5 | **local** | clean: 1074 at 10-06 21:49:36Z, 13 at 21:49:55 | no |
| 10-08 05:43:02.5Z | 07:43:01 | 07:43:03.1 (+7 200.59 s) | **UTC** | **unexpected**: EventLog 6008 *"previous system shutdown at 10:29:10 PM on 10/7/2026"* (local = 20:29:10Z); WER 1001 **bugcheck 0x154**; Kernel-Boot 20 *"…was false"*; no Kernel-General 13 | +7 200.59 s, by the repair flow (§2) |
| 10-08 09:43:02.5Z | 11:43:00 | 11:43:02.1 (+7 199.61 s) | **UTC** | **unexpected**: 6008 *"1:28:25 PM on 10/8/2026"* (= 11:28:25Z, 15 min earlier); WER 1001 **bugcheck 0x19C**; no 13 | +7 199.61 s, by the repair flow (§2) |

**Verdict on the firmware clock — H2 confirmed for the two boots the log still holds.** At each slow boot the RTC held UTC
(within 1–3 s), Windows read it as local time (F1, F2), and the 2 h offset is exactly the CEST offset — the 7 200.42 s of the
incident record. After the clean shutdowns the RTC held local time, as Windows itself had written it (F2). Every RTC write
Windows LOGGED is local (F2), so the UTC value came from a write that is **not in Windows' time-change log** — another operating
system, the firmware, or a Windows path that does not log (the log is not an exhaustive inventory of RTC writes). The
interpretation of event 238 rests on UEFI's rule that time zone 2047 means the fields are local wall time; that Windows
serialises the unmodified RTC read into it is assumed, not proven — a controlled boot with the firmware setup clock read
against a UTC reference would confirm it. Windows' boot manager logged
`There are 0x1 boot options` and `bootmgr spent 0 ms waiting for user input` at every boot.

**Who wrote UTC — not settled; candidates to investigate, none proven:**

| Candidate | For | Against / unknown |
|---|---|---|
| **A native Linux installation on the system disk.** `Get-Partition` on disk 0 lists, besides the EFI, MSR, Windows, Recovery and two data partitions, **two Linux-filesystem partitions** (`0fc63daf-8483-4772-8e79-3d69d8477de4`, ≈ 150 GB and ≈ 552 GB) and **a Linux swap partition** (`0657fd6d-a4ab-43c4-84e5-0933c84b4f4f`, ≈ 32 GB) | Linux keeps the RTC in UTC by default and writes it — the textbook dual-boot 2 h; explains all four slow boots if Linux ran before each; the 15 min between the 11:28Z crash and the 11:43Z boot and the night before leave room for it | partition types prove Linux STORAGE, not a bootable installation, its RTC policy, or that it ran; that is read from the installation itself — its `/etc/adjtime` (`UTC` or `LOCAL` on the third line) and `journalctl --list-boots` (a boot in the gap before each slow Windows boot). Firmware boot entries (`bcdedit /enum firmware`, elevated) would show it is bootable, not that it ran; Windows' own boot manager has a single entry |
| **The firmware on the reset after a bugcheck** | both UTC boots followed a bugcheck, both local boots a clean power-off | the 09-29 and 10-05 slow boots have **no** unexpected-shutdown record (Reliability Monitor, span 2026-09-18 … now, holds both 10-08 6008s and nothing else), so a bugcheck is not needed for a slow boot; and firmware has no source of UTC to write |
| **The WSL guest** | its timesyncd syncs to UTC (`RTC in local TZ: no`) | its `/dev/rtc0` (`rtc_cmos`) is the utility VM's emulated device — a write reaches the VM's virtual RTC, not the board's. This is an architecture claim, not measured here |

## 5. Also read, not explained

| # | Read | Value |
|---|---|---|
| X1 | two bugchecks in one day | 0x154 (UNEXPECTED_STORE_EXCEPTION) on 10-07 20:29Z and 0x19C (WIN32K_POWER_WATCHDOG_TIMEOUT, parameter 1 = 0x50) on 10-08 11:28Z, dumps `C:\WINDOWS\Minidump\100826-22500-01.dmp` and `100826-17359-01.dmp` (the folder is not readable unelevated). A separate defect from the clock; its cause is not read here |
| X2 | Secure Time Seeding | `W32Time\Config UtilizeSslTimeData = 1` (on), `LastKnownGoodTime` = 2026-10-08T16:23:47Z (agrees with now). Not implicated: the bad value came from the RTC (§4) |
| X3 | the other time services | Cellular Time (`autotimesvc`), Auto Time Zone Updater (`tzautoupdate`) and Hyper-V Time Synchronization (`vmictimesync`) all **Stopped, Manual**; `W32Time\Parameters Type NTP`, `NtpServer time.windows.com,0x9` |
| X4 | the trigger record's T11 (a service running from boot did not step the 2 h for 7 min) | unchanged; every step in F3 followed an explicit resync (reason 0) |

## 6. The AMD software on this machine, and what touches the time service

| Kind | Found (read-only) | Touches `w32time`? |
|---|---|---|
| Packages (Uninstall keys, both hives) | AMD Software 26.8.1, AMD Settings, AMD DVR, AMD WVR64, AMD Install Manager (installed 2026-10-02), AMD Chipset Software 8.08.12.551 with its GPIO2, I2C, Interface, MicroPEP, PSP and SFH1.1 drivers, Branding64 | no |
| Services | **AMD Crash Defender Service** (`amdfendrsr.exe`) and **AMD External Events Utility** (`atiesrxx.exe`), both Auto, Running, `LocalSystem` | a change by them would carry `S-1-5-18`; none of W3 does |
| Drivers | `amdfendr`, `amdfendrmgr` (Crash Defender), `amdgpio2`, `amdi2c`, `AmdMicroPEP`, `AmdPPM`, `amdpsp`, `AMDSAFD`, `amdsfhkmdf`, `amdwps`, `AMDXE`, `AtiHDAudioService`, the display driver | no service-control path is visible from a driver's registration |
| Scheduled tasks (`Export-ScheduledTask`) | `\StartCN` → `"C:\Program Files\AMD\CNext\CNext\cncmd.exe" startwithdelay`; `\StartDVR` → `"…\CNext\RSServCmd.exe"`; both a LogonTrigger, principal the Users group (`S-1-5-32-545`), author *Advanced Micro Devices* | no |
| Every visible task's action matched against `w32t`, `sc config`, `Set-Service`, `tzutil`, `hwclock`, `Set-Date`, `net stop/start` | only Windows' own `SynchronizeTime` (`sc.exe start w32time task_started`) and `UPnPHostConfig` (`sc.exe config upnphost start= auto`) | only Windows' own |
| Scripts and config under `C:\Program Files\AMD`, `…(x86)\AMD`, `C:\ProgramData\AMD` (`*.ps1 *.bat *.cmd *.xml *.json *.ini *.vbs`) searched for `w32time`, `w32tm`, `Set-Service`, `sc config` | no match | no |
| Run / RunOnce (HKLM, WOW6432Node, HKCU) | one AMD entry, `AMDNoiseSuppression.exe`; nothing time-related | no |

**Verdict on H1:** no AMD component is seen touching the Windows Time service — not the start type (§2), not the stops (§3),
not the clock (§4). What remains open for AMD is X1: whether the AMD display or chipset drivers are behind the two bugchecks,
which a minidump analysis would answer.

## 7. Recommended fixes for the owner — recommendations only, nothing here was applied

1. **Make every writer of the RTC agree on UTC** (the 2 h itself). If the Linux installation on this disk is booted, even
   occasionally, this is the fix:
   - In Windows (elevated), tell Windows the RTC holds UTC:
     `reg add HKLM\SYSTEM\CurrentControlSet\Control\TimeZoneInformation /v RealTimeIsUniversal /t REG_DWORD /d 1 /f`
     (REG_DWORD, the commonly documented type). **Accept it only on these checks**, in order: the value reads back as REG_DWORD 1;
     after a FULL restart and a resync, a NEW Kernel-General 1 event reads `RealTimeIsUniversal=true` with `CmosTime` equal to
     `NewTime` (F2's fields); and the boot after that is right BEFORE any resync (Kernel-Boot 238's firmware time equals the true
     UTC of the boot, and no +7 200 s step follows). A missing event is inconclusive; `false` means diagnose before trying
     anything else — another value type is an experiment needing its own restart and the same checks. The firmware setup screen
     will then show UTC.
   - The alternative is the Linux side: `timedatectl set-local-rtc 1` in the native installation (systemd discourages it — DST
     changes while Linux runs are not handled).
   - **Setting the BIOS clock to local time once does not hold**: the next UTC write puts the 2 h back.
   If the owner does NOT boot that Linux: do not change `RealTimeIsUniversal` yet; at the next slow boot compare Kernel-Boot 238
   with the shutdown before it (§4's columns) — that narrows the candidates, it does not name a writer — and check the firmware setup and a newer firmware than
   1.06 for anything that sets the clock.
2. **Nothing to fix about the disable/re-enable pairs.** They are a repair of the clock — by the evidence, Settings' Sync now (§2). The Windows Time
   guard's planned 7040 trigger will therefore fire twice on every Sync now; the guard's action is idempotent, so that is
   expected, not a fault.
3. **Keep the guard's at-startup and at-logon resync.** Every correction on record followed an explicit resync (X4); a service
   left to itself did not step the 2 h for minutes.
4. **The two bugchecks** (X1): read the minidumps elevated (`!analyze -v` in WinDbg) before changing anything; if they point at
   `amdkmdag`/the display stack or the storage path, update the AMD graphics and chipset drivers and the firmware. This is the
   one place AMD software is still a suspect, and it is a crash, not the clock.
5. **So the next "who" is a read, not an inference:** enable `Microsoft-Windows-TaskScheduler/Operational` (every task run
   logged) and process-creation auditing with command lines (Security 4688). Both are owner decisions, made elevated.

The incident record's O8 ("the owner's manual `Start-Service`") is refined by §2: the log for that minute shows the disable/re-enable repair flow, which a plain `Start-Service` does not write.

## Appendix — the reads, so they can be repeated

All unelevated Windows PowerShell 5.1; times converted with `.ToUniversalTime()`; sorted by `RecordId`.

```powershell
Get-WinEvent -FilterHashtable @{LogName='System'; ProviderName='Service Control Manager'}            # 7040, its EventData param1..4
Get-WinEvent -FilterHashtable @{LogName='System'; ProviderName='Microsoft-Windows-Kernel-General'; Id=1,12,13}
Get-WinEvent -FilterHashtable @{LogName='System'; ProviderName='Microsoft-Windows-Kernel-Boot'}       # 18, 20, 32, 238
Get-WinEvent -FilterHashtable @{LogName='System'; ProviderName='EventLog','User32','Microsoft-Windows-WER-SystemErrorReporting'}
Get-WinEvent -FilterHashtable @{LogName='Microsoft-Windows-Time-Service/Operational'; Id=257,258,261,266}
Get-WinEvent -LogName 'Microsoft-Windows-Application-Experience/Program-Telemetry'                   # 500/505 per process start
Get-WinEvent -FilterHashtable @{LogName='Windows PowerShell'; StartTime=$f; EndTime=$t}               # 400 HostApplication
Get-WinEvent -FilterHashtable @{LogName='Microsoft-Windows-Kernel-Boot/Operational'; Id=85}           # one per boot
Get-CimInstance Win32_ReliabilityRecords                                                              # 6008 / 1001 history
Get-ItemProperty HKLM:\SYSTEM\CurrentControlSet\Control\TimeZoneInformation
Get-ItemProperty HKLM:\SYSTEM\CurrentControlSet\Services\W32Time\{Parameters,Config,TimeProviders\NtpClient}
Get-Partition | Select-Object DiskNumber, PartitionNumber, GptType, Size
Get-ScheduledTask | ForEach-Object Actions;  Export-ScheduledTask -TaskName StartCN -TaskPath '\'
sc.exe sdshow w32time;  sc.exe qtriggerinfo w32time
# the two binaries: [IO.File]::ReadAllBytes(...) decoded as UTF-16 and ASCII, strings matched against w32time|ForceTimeSync|ChangeServiceConfig|autoElevate
```

The WSL read was one script file run as `wsl.exe -d Ubuntu -- /bin/sh <absolute path>`: `ls -l /dev/rtc*`, the `name` and
`hctosys` of `/sys/class/rtc/rtc0`, `timedatectl`, `uname -r`, `date -u`.
