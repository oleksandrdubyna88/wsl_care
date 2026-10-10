# The Radeon AI PRO R9700 disabled (code 22) and Ollama on the CPU — 2026-10-10

> Measured 2026-10-10 14:20–14:35Z on the owner's machine, read-only (no elevation, nothing enabled), with
> PowerShell 7.6.4 (`pwsh -NoProfile -File <script>`; the scripts were scratch files, not committed). What
> [PLAN_gpu_health.md](../todo/PLAN_gpu_health.md) starts from.

## G1 — the adapters as Windows reports them

`Get-CimInstance Win32_VideoController` (unelevated):

| Name | Status | ConfigManagerErrorCode | PNPDeviceID | DriverVersion |
|---|---|---|---|---|
| AMD Radeon(TM) 890M Graphics | OK | 0 | `PCI\VEN_1002&DEV_150E&SUBSYS_B0201F4C&REV_C1\4&27F515C0&0&0041` | 32.0.31041.1004 |
| AMD Radeon AI PRO R9700 | Error | **22** | `PCI\VEN_1002&DEV_7551&SUBSYS_B0201F4C&REV_C0\6&2A3DA1BE&0&00000019` | 32.0.31041.1004 |

`Get-PnpDevice -Class Display` agrees: the R9700 is `Status Error`, `Problem CM_PROB_DISABLED`, `ProblemDescription`
"This device is disabled. (Code 22).", `Present True`. `DEVPKEY_Device_ProblemCode` 22,
`DEVPKEY_Device_LastArrivalDate` the same instant as the 890M's (this boot). A disabled adapter **stays in
`Win32_VideoController`** — the query that lists it also lists the broken one, so one CIM read is enough.

## G2 — what it costs: Ollama chose the CPU

`%LOCALAPPDATA%\Ollama\server.log` (101 167 bytes) and its rotations, the last `msg="inference compute"` line of each:

| File (last write, UTC) | Ollama's line (local time +02:00 as Ollama prints it) |
|---|---|
| `server.log` (2026-10-10 14:31) | 2026-10-10 11:49 — `library=cpu … name=cpu` |
| `server-1.log` (2026-10-09 23:19) | 2026-10-10 01:18 — `library=cpu` |
| `server-2.log` (2026-10-09 09:17) | 2026-10-09 11:17 — `library=ROCm compute=gfx1201 … description="AMD Radeon AI PRO R9700"` |
| `server-3.log` | 2026-10-09 11:14 — `library=ROCm … R9700` |
| `server-4.log`, `server-5.log` | 2026-10-08 11:50 / 11:48 — `library=cpu` |

So the card was gone on 2026-10-08, back on 2026-10-09 morning, gone again by 2026-10-10 01:18 local. Ollama starts
without an error either way and runs on the CPU — which is why the owner saw "Ollama would not start" without a reason.
The log's line is the one place the consequence is written down.

## G3 — who disabled it: not found

- `Microsoft-Windows-Kernel-PnP/Configuration` and `Microsoft-Windows-DeviceSetupManager/Admin`, last 4 days, lines
  naming `DEV_7551` or `R9700`: **none**.
- `System`, last 4 days: two `Kernel-PnP` **225** at 2026-10-08 11:09:04Z — "The application …\vmwp.exe (pid 28432) /
  …\vmcompute.exe (pid 10612) stopped the removal or ejection for the device PCI\VEN_1002&DEV_7551…". Something asked
  Windows to remove or eject the card while the WSL VM's worker held it (GPU paravirtualisation); what asked is not in
  the log. `Microsoft-Windows-UserPnp` in `System`, 60 days: none.
- Checked: those three channels, by message text. NOT checked: the AMD software's own logs, the Security log (needs
  elevation), Device Manager's history UI. Code 22 is "disabled" — a state Windows keeps across a restart, so a restart
  does not bring the card back; enabling it does.
- An earlier episode (2026-08-11, the operator's notes): the same card at code 31, Ollama on the CPU the same way.

## G4 — PowerShell on this machine

- `pwsh` is the Store package: `C:\Program Files\WindowsApps\Microsoft.PowerShell_7.6.4.0_x64__8wekyb3d8bbwe\pwsh.exe`.
  `%ProgramFiles%\PowerShell\7\pwsh.exe` (the MSI install): **absent**. Windows PowerShell 5.1
  (`%SystemRoot%\System32\WindowsPowerShell\v1.0\powershell.exe`): present.
- So on THIS machine an elevated run that prefers the MSI `pwsh` falls back to 5.1; the `pwsh` branch is not exercised
  here (recorded in the plan's residuals).

## G5 — the read the extension would make, timed

The plan's D1 query (`Get-CimInstance -ClassName Win32_VideoController`, tagged lines) through
`%SystemRoot%\System32\WindowsPowerShell\v1.0\powershell.exe -NoProfile -NonInteractive -EncodedCommand …` printed both
adapters (`code.1=22`) and `adapters=2`. Six runs, wall time: 993, 896, 1464, 6193, 974, 573 ms. Windows PowerShell 5.1
also wrote "Preparing modules for first use" progress records as CLIXML on stderr (silenced by `$ProgressPreference`).
`-File` is refused by this machine's execution policy (`UnauthorizedAccess`); the extension never uses `-File`.

`Get-PnpDevice -InstanceId <the R9700's id>` in both 5.1 and 7.6.4: `Class` = `Display`; `ConfigManagerErrorCode` is the
enum `PnpDevice.Problem` (it prints `CM_PROB_DISABLED`); `[int]` of it is 22 and `-eq 22` is True. The `PnpDevice` module is
`C:\WINDOWS\system32\WindowsPowerShell\v1.0\Modules\PnpDevice\PnpDevice.psd1`.

## G6 — the enable script's read half under both PowerShells (after the plan round)

The plan's elevated script up to (not including) `Enable-PnpDevice` — `$PSModulePath` cut to `$PSHOME\Modules`, `PnpDevice`
imported by its absolute path, `Get-PnpDevice -InstanceId <the R9700's id>` — run UNELEVATED as `-EncodedCommand`:

| PowerShell | Answer | Exit |
|---|---|---|
| `pwsh` 7.6.4 (the Store package; the same engine as the MSI one the plan prefers) | `edition=Core class=Display code=22`, `Enable-PnpDevice` resolved from `PnpDevice` | 0 |
| Windows PowerShell 5.1.26100.9549 | `edition=Desktop class=Display code=22`, `Enable-PnpDevice` resolved from `PnpDevice` | 0 |

So the import by path and the re-read work under both editions with the module path cut. `Enable-PnpDevice` itself was not
run (it changes the device; the owner presses the button).

## G7 — what the alert's read costs (the built query, 2026-10-10 15:15Z)

The shipped `QUERY_SCRIPT`, run ten times as the extension runs it (`powershell.exe -NoProfile -NonInteractive
-EncodedCommand`, started by `Start-Process -Wait` from `pwsh` so the child's CPU could be read): wall 500–1 208 ms, CPU
375–656 ms of one core (median ≈ 450 ms), 11 lines each time (both adapters and the count). At the default
`wslCare.gpu.checkMinutes` (15) that is 96 reads a day, about 45 s of one core per day, plus one read per panel open or
*Refresh*.
