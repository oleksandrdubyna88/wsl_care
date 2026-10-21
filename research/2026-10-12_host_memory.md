# Windows memory pressure on the owner's machine — measured 2026-10-12

> READ-ONLY, unelevated, PowerShell 7.6 (a scratch script, not committed). 4.4 h after boot, while several AI-agent sessions
> built and tested in parallel (Linux load average about 16). What the E15.S5 defaults in
> [PLAN_disk_health.md](../todo/PLAN_disk_health.md) start from.

| Reading | Road | Value |
|---|---|---|
| `Win32_PerfRawData_PerfOS_Memory` `CommittedBytes` / `CommitLimit` | the distro's full run (the Windows disk script) | 107 509 157 888 / 156 356 161 536 B = **68.8 %** |
| `GlobalMemoryStatusEx` `ullTotalPageFile − ullAvailPageFile` / `ullTotalPageFile` | the Windows binary | 107 499 827 200 / 156 356 161 536 B = **68.8 %** |
| Physical memory (`ullTotalPhys`) | — | 98 374 103 040 B (91.6 GiB) |
| `Memory Compression` working set (one process) | both | 6 479 380 480 B = **6.0 GiB** |
| `vmmemWSL` working set | the Windows binary | 20 244 062 208 B = **18.9 GiB** |

**What this shows.**
- **The two roads read the same quantity.** The perf counter and `MEMORYSTATUSEX` agree to 0.01 %; the difference is the
  milliseconds between the two reads. So ONE rule over either road is sound.
- **The E15.S5 defaults are provisional starting values.** Commit warns above 80 % and is critical above 90 %; compression warns
  above 8 GiB. A busy afternoon reads 68.8 % and 6.0 GiB, so the defaults stay quiet on a normal heavy day. The 2026-10-10 capture
  showed a 110.98 GB charge, so the evening limit is near.
- **Calibration** comes from the records: every full run carries these figures in its detail. E17's statistics give the
  distribution per machine, and the defaults move when the records say so.
