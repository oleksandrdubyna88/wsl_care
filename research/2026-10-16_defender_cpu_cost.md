# Defender's CPU counters: what one read costs (E15.S10, measured 2026-10-16)

> Status: measurement record for E15.S10 of [PLAN_disk_health.md](../todo/PLAN_disk_health.md). Read-only: nothing on the
> machine was changed; no Defender setting was read or written beyond the performance counter below.

## The question

E15.S10 adds a THIRD fixed interop script that reads `MsMpEng`'s raw CPU counters once per full run. The plan asked for numbers
before the ceiling is set: the script cold and warm, filtered and unfiltered, under Windows PowerShell 5.1 (the 2026-10-10 note,
[2026-10-10_defender.md](2026-10-10_defender.md) F3, used pwsh 7).

## The command (as the script will run it, minus the output road)

```powershell
Get-CimInstance -ClassName Win32_PerfRawData_PerfProc_Process -Filter "Name='MsMpEng'" |
  Select-Object -First 1 |
  ForEach-Object { 'defender=' + $_.PercentProcessorTime + '|' + $_.Timestamp_Sys100NS + '|' + $_.IDProcess + '|' + $_.ElapsedTime }
```

The unfiltered variant drops `-Filter` and selects with `Where-Object { $_.Name -eq 'MsMpEng' }`. The measuring script is
`defender-cost.ps1` (session scratchpad): each variant three times in a FRESH `powershell.exe -NoProfile -NonInteractive` (cold:
the process start and the CIM session are paid every time), then three times inside one process (warm).

## The machine

Windows 11 Pro, the owner's machine, 684 processes at the time, heavily loaded (the agent fleet running). The MSI pwsh 7 is NOT
installed here (only the Store build), so pwsh 7 was not measured — the script runs under 5.1 only (see the design).

## Results (milliseconds)

| Engine | Variant | Cold (3 fresh processes) | Warm (3 reads in one process) |
|---|---|---|---|
| Windows PowerShell 5.1 | `-Filter "Name='MsMpEng'"` | 8 125 · 2 034 · 1 354 | 1 710 · 403 · 364 |
| Windows PowerShell 5.1 | unfiltered + `Where-Object` | 1 249 · 2 145 · 1 221 | 845 · 481 · 452 |

- The first cold read (8.1 s) paid the WMI provider's own start; later cold reads cost 1.2–2.1 s, almost all of it the
  `powershell.exe` start and the CIM session.
- Filtered and unfiltered cost about the same once warm (0.36–0.48 s): the performance provider enumerates every process either
  way, so `-Filter` saves transfer, not enumeration. The filtered form is kept: it carries less over the pipe and names the one
  process it wants.

## A reading, and what it says

One line from the runs above: `defender=993513593750|134366539776100740|6964|134365236258808755`.

- `PercentProcessorTime` 993 513 593 750 — cumulative CPU time over all cores, in 100 ns: 99 351 s.
- `Timestamp_Sys100NS` 134 366 539 776 100 740 — the sample's instant (FILETIME, 100 ns since 1601).
- `IDProcess` 6964.
- `ElapsedTime` 134 365 236 258 808 755 — on this raw class it is the process START instant on the same clock, not elapsed
  seconds (F3): the process had run 130 351 s (36.2 h).

So MsMpEng averaged 99 351 / 130 351 = **0.76 cores** since it started, on a development machine with no folder exclusions.
Two reads of the same process (the same pid AND the same start instant) a full run apart give the average between them:
ΔPercentProcessorTime / ΔTimestamp_Sys100NS cores.

## What the design takes from this

- `health.defenderCpuTimeoutSeconds`: default **15**, range 5–60. The worst cold read measured 8.1 s on a loaded machine; 15 leaves
  room for the first read after a boot. `RunBudget` sums every `*TimeoutSeconds` key (`Config/RunBudget.cs:22`), so the full run's
  budget grows by the new ceiling with no further change.
- The script runs under Windows PowerShell 5.1 only: it needs no module (CIM is built in), and a pwsh variant would add a fourth
  member to the never-list's closed set for no measured gain.
- `thresholds.defenderCoresWarnPercent` default **100** (% of one core, i.e. 1.0 core; the design's decimal `thresholds.defenderCoresWarn` became a whole-number key): 0.76 measured on a heavy development day stays quiet; a sustained full core
  is the symptom the owner reported (plan §1).
