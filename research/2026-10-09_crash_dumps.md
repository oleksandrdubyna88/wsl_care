# Crash dumps on the owner's machine — WinDbg (cdb) read, 2026-10-09

> Measured 2026-10-09 (the coordinator's read of the owner's minidumps). Context: the Windows clock 2 h slow after boots —
> [2026-10-08_windows_time_stopped.md](2026-10-08_windows_time_stopped.md), [2026-10-08_who_stops_windows_time.md](2026-10-08_who_stops_windows_time.md);
> committed memory on the evening of 2026-10-07 — [2026-10-07_evening_overload.md](2026-10-07_evening_overload.md).

Source: C:\Windows\Minidump, copied elevated (owner approved 2026-10-09), analysed with
`cdb -z <dmp> -c "!analyze -v"` against the Microsoft symbol server. Read-only.

| Dump (local time) | Bugcheck | Bucket | Uptime |
|---|---|---|---|
| 2026-08-25 12:41 | 0x1E KMODE_EXCEPTION_NOT_HANDLED (c0000005) | no symbols for that kernel build — not analysable | 2:59 |
| 2026-09-29 14:51 | 0x19C WIN32K_POWER_WATCHDOG_TIMEOUT, P1=0x50 | 0x19C_DRVSETMONITORPOWERSTATE_HANG_dxgmms2!VidMmTransitionToState | 5:19 |
| 2026-10-02 15:36 | 0x19C, P1=0x50 | same as above | 6:06 |
| 2026-10-07 22:43 | 0x154 UNEXPECTED_STORE_EXCEPTION | 0x154_c0000006_c000000e_IMAGE_stornvme.sys, process MemCompression | 13:09 |
| 2026-10-08 13:29 | 0x19C, P1=0x50 | 0x19C_DRVSETMONITORPOWERSTATE_HANG_dxgkrnl!DXGADAPTER::AcquireCoreResourceExclusiveWithTracking | 3:46 |

## 0x19C ×3 — the display driver hangs while the monitor wakes or sleeps
Stack: win32kbase!DrvSetMonitorPowerState → dxgkrnl!DxgkPowerOnOffMonitor → DxgkQueryConnectionChanges →
AcquireCoreSync → waits forever (on 10-08 the stack is win32kbase!PowerOnMonitor, i.e. waking the screen). The adapter's
core lock is held by the GPU driver side (amdkmdag.sys; AMD driver 32.0.31041.1004, dated 2026-08-17, for both the
Radeon 890M and the Radeon AI PRO R9700). Windows gives up after the watchdog and bugchecks.
Kernel-Power 506 (entering Modern Standby) is logged right before such periods.

## 0x154 — compressed memory could not be read back
nt!SmPageRead → RtlDecompressBufferLz4 faults in MemCompression with STATUS_IN_PAGE_ERROR (c0000006) and
STATUS_NO_SUCH_DEVICE (c000000e) on stornvme: a page of the memory-compression store, paged out, could not be read from
the NVMe (SOLIDIGM SB5PH27X038T, firmware G70YG252, health Healthy). It happened on the evening of 2026-10-07, when
inside WSL `Committed_AS` was 104 % of `MemTotal` and Windows had 100.8 of its 156.4 GB commit limit committed
([2026-10-07_evening_overload.md](2026-10-07_evening_overload.md), L3 and W7; the note this record was made from cited
the 2026-10-06 measurements for it, corrected on import) — the pagefile was in heavy use. No
stornvme/disk error for disk 0 is logged around it.

## A red herring found on the way
47 × `disk` event 11 "controller error on \Device\Harddisk1\DR1" in 45 days (26 on 10-07, 16 on 10-08). Harddisk1 is
the USB keyboard's 30 MB storage function (ClawsKey ClawsKeyboard), not the NVMe.

## Chain to the clock problem (a hypothesis: who wrote UTC into the RTC is not measured)

The Linux on the disk is the strongest CANDIDATE, not a proven writer — [2026-10-08_who_stops_windows_time.md](2026-10-08_who_stops_windows_time.md)
leaves the writer open. The chain as read:
BSOD → firmware auto-boots the broken Linux on the disk → Linux writes UTC into the RTC → Windows (no
RealTimeIsUniversal) reads it as local time → 2 h slow. RealTimeIsUniversal=1 set 2026-10-09 15:27Z (owner approved);
W32Time set Automatic + running + resync the same minute (it had been found Stopped/Manual again at 15:26Z with a
correct clock).

## Advice given to the owner (not done by the product)
- 0x19C: update / clean-reinstall the AMD graphics driver; as a stop-gap, avoid monitor sleep / Modern Standby.
- 0x154: Solidigm tool — SMART + firmware; Windows Memory Diagnostic; keep committed memory below RAM (wsl-care's
  memory.committed warning, S5).
