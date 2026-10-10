# The Windows Time service stopped, Windows 2 h slow, and two clocks fighting inside WSL — measured 2026-10-08

> The incident that started [PLAN_windows_time_guard.md](PLAN_windows_time_guard.md), kept in one place so no
> number lives only in a chat transcript. Each row says HOW it was measured and by whom. No agent started, stopped or
> changed a Windows service: the owner restarted `w32time` by hand; every agent read below is read-only.
>
> - **Machine:** Windows 11 Pro 10.0.26300, time zone *Romance Standard Time* (CEST, UTC+2 on this date); WSL 2 with
>   Ubuntu 24.04, systemd 255, systemd-timesyncd against `ntp.ubuntu.com`.
> - **Subject:** the installed daemon `daemon-v0.2.0` (its `clock.drift` reading and `journal.history` warning), the
>   live contract (`POST_DEPLOY.md` item 4).
> - **Related:** [2026-10-02_wsl_resource_baseline.md](2026-10-02_wsl_resource_baseline.md) (the clock jumps first
>   counted — 875 in ≈ 3.9 h), [architecture.md](architecture.md) *The Windows clock*.

## 1. What the owner and the coordinator observed, before the restart

| # | Observed | Value | How |
|---|---|---|---|
| O1 | `w32time` state | **Stopped**, StartType **Manual**; `w32tm /query /status` → *"The service has not been started"* | owner, PowerShell |
| O2 | Windows' clock | **7 200 s slow** (UTC), the time zone right; the GitHub `Date` header and the WSL clock agreed with each other | coordinator: `Date` header vs `date -u` vs `[DateTime]::UtcNow` |
| O3 | the daemon's `clock.drift` | **−7 200.42 s** on ONE observation; no warning (the rule needs two ≥ 5 min apart) | the installed daemon's full run |
| O4 | `POST_DEPLOY.md` item 4 (live contract) | the clock probe failed at **7 198 s**, with the message *"a skew of a minute would be a broken clock, not this probe"* — no cause named | coordinator, `WslCare.LiveContract` |
| O5 | journald | *"Time jumped backwards, rotating"* at each pull, ≈ every 33 s; the journal reached less than an hour (`journal.history` −0.0 days) | coordinator, `journalctl` |
| O6 | systemd-resolved | *"Clock change detected"* ≈ **661 per 4 h** — the `clock.jumps` warning of every run since the first baseline | the daemon's `clock.jumps` |
| O7 | the owner's account of the cause | AMD driver software stops `w32time` | owner (not measured here) |
| O8 | after the owner's manual `Start-Service` | the clocks agree; StartType is still **Manual** | owner |

> **Later reading of O8** ([2026-10-08_who_stops_windows_time.md](2026-10-08_who_stops_windows_time.md) §2): the event log
> for that minute shows a repair flow strongly consistent with the Windows Settings app's *Sync now* (start type → disabled →
> demand start, start, explicit resync),
> not a plain `Start-Service`, which writes no 7040. The row above is kept as it was reported.

## 2. Read-only measurements by this task, after the restart (2026-10-08, 08:16–08:18Z)

| # | Measured | Value | How |
|---|---|---|---|
| M1 | `w32time` | `Running`, StartType `Manual` | `Get-Service w32time` (unelevated) |
| M2 | Windows' NTP state | *Leap Indicator: 3 (not synchronized)*, *Root Dispersion: 7208.3608222s*, *Last Successful Sync Time: 10/8/2026 8:00:22 AM*, source `time.windows.com` | `w32tm /query /status` |
| M3 | Windows against NTP now | −0.0169 s and −0.0200 s | `w32tm /stripchart /computer:time.windows.com /samples:2 /dataonly` |
| M4 | Windows against WSL now | WSL `08:17:08.850Z`, Windows `08:17:08.880Z` taken right after — agree within 0.03 s | the end of the WSL probe script, then `[DateTime]::UtcNow` |
| M5 | the trigger start | `w32time` is trigger-started (`DOMAIN JOINED STATUS`, a `CUSTOM SYSTEM STATE CHANGE EVENT`) | `sc.exe qtriggerinfo w32time` |
| M6 | `RealTimeIsUniversal` | not set (the value is absent) | `HKLM\SYSTEM\CurrentControlSet\Control\TimeZoneInformation` |
| M7 | timesyncd | `NTP=yes`, `NTPSynchronized=yes`; last sample `Offset: -18.401ms`, `Poll interval: 32s (min: 32s; max 34min 8s)`, `Packet count: 63` — still at its MINIMUM interval | `timedatectl show`, `timedatectl timesync-status`, `timedatectl show-timesync` |
| M8 | the boot's first timesyncd line | at **07:46:13 CEST**: *"Initial clock synchronization to Thu 2026-10-08 09:46:14.547046 CEST"* — the distro booted on the host's clock, 2 h behind | `journalctl --unit=systemd-timesyncd` |
| M9 | the boot's journal span | boot 0: first entry `07:45:56 CEST` (the host's wrong clock) — the previous boot ended `22:42:47 CEST` the day before | `journalctl --list-boots` |
| M10 | journald's backward jumps (last 24 h of wall time) | **82** under `--unit=systemd-journald`, **163** under `--identifier=systemd-journald`; the last five 09:58:04, 09:58:37, 09:59:10, 09:59:42, **10:00:15 CEST** (≈ 33 s apart) — seven seconds before `w32time`'s recorded sync (M2) | `journalctl --grep='Time jumped backwards' --output=short-iso` |
| M11 | systemd-resolved's clock changes (same window) | **191** | `journalctl --unit=systemd-resolved --grep='Clock change detected'` |
| M12 | the HTTP `Date` as a reference, from Windows (`curl.exe -sSI -H "Cache-Control: no-cache"`, Windows minus server at the request's midpoint) | github.com **+0.88 s**, api.github.com **+3.11 s**, www.google.com −0.61 s (RTT 2.36 s), www.cloudflare.com +0.69 s, www.microsoft.com +0.88 s, github.com again **+4.03 s** — github.com returned the SAME `Date` (08:17:49) twice, 3 s apart; an earlier pair from Windows PowerShell read github.com 4.9–6.5 s behind | `curl.exe`, `Invoke-WebRequest` |
| M13 | the HTTP `Date` from WSL | github.com `08:17:08` against WSL `08:17:08.85` | `curl -sSI` in the probe script |

The WSL reads were one script file (read-only: `date`, `timedatectl`, `systemctl is-active`, `journalctl`, `curl -I`)
run as `wsl.exe -d Ubuntu -- /bin/sh <absolute path of the script>`; the Windows reads were unelevated PowerShell.

## 3. What this licenses, and what it does not

- **The mechanism of the symptoms is observed:** Windows' clock was 2 h behind; the distro started on it (M8); Hyper-V's
  time sync pulled the distro back to it and timesyncd stepped it forward, ≈ every 33 s (M10) until `w32time` synced
  (M2, M10's last line). Each pull is one journald rotation, which is why the journal reached less than an hour.
- **A16 as built would have made it worse:** it steps the distro toward the host (`hwclock -s`). The plan's D6 is the fix.
- **github.com's `Date` is not a reference to the second:** up to 6.5 s stale through its edge (M12). The plan uses a
  30 s tolerance and `https://www.microsoft.com` by default.
- **NOT settled — why Windows was 2 h behind.** The offset was 7 200.42 s, the time zone's offset to 0.42 s. That is the
  signature of a firmware clock (RTC) holding UTC while Windows reads it as local time — something that writes the RTC
  in UTC (a firmware or driver update, another operating system) would produce exactly this at the next boot, and a
  stopped `w32time` (O1) would leave it uncorrected. The owner's AMD explanation (O7) covers the stopped service, not
  the 2 h. Neither the RTC nor the AMD software was measured here (plan owner question Q5).
- **NOT settled — whether "Stopped" is a fault by itself.** `w32time` is trigger-started on this machine (M5), so Windows
  may stop it when idle. The plan reports Stopped as a warning unless the clock is actually wrong.
- `--unit` and `--identifier` count different numbers of journald's own lines (M10); the plan counts `--unit` as a rate.

## 4. Caused by this task — an elevated PowerShell window, and what UAC is on this machine (2026-10-08, 09:29Z)

| # | Observed | Value | How |
|---|---|---|---|
| U1 | a break-it check of the extension's test tripwire | to prove the test *"no test can start an elevated PowerShell"* has teeth, the tripwire's RunAs refusal was neutralised for one run. That test's first line then really ran `Start-Process powershell -Verb RunAs` from the test process | the agent's own break-it script (an error: a tripwire is the one guard whose break-it must never run for real) |
| U2 | its result | a window **"Administrator: Windows PowerShell"** (pid 20180, started 11:29:46 local) — interactive, idle; its command line is unreadable from an unelevated process, which is what an elevated process looks like | `Get-Process -Id 20180`, `Win32_Process` |
| U3 | what did NOT run | the test stopped at its first failed assertion, BEFORE the line that hands the real fix to the runner; `w32time` read `Running, Manual` right after — the fix's `Set-Service` did not run | `Get-Service w32time` at 11:30:06 local |
| U4 | UAC on this machine | `EnableLUA = 1`, `ConsentPromptBehaviorAdmin = 5` (Windows' default: *prompt for consent for non-Windows binaries*). Whether a prompt was shown and answered, or not shown, was not observed by the agent | `HKLM\…\Policies\System` |

What it licenses: the window is the owner's to close (an unelevated process cannot end it, and nothing here ends a process
it cannot name by pid and start). A tripwire's break-it check must replace the launcher with a recording stand-in, never let
the real one through. And the
modal in the extension is the confirmation the PRODUCT controls: whether Windows also shows a UAC prompt is the machine's
UAC policy (U4), not the extension's.

## 5. Found stopped again, and the crash dumps behind the slow boots (2026-10-09)

- **2026-10-09 15:26Z:** `w32time` found **Stopped / Manual** again, with a CORRECT clock (the coordinator, on the owner's
  machine); set **Automatic + running** elevated at 15:27Z. The same minute `RealTimeIsUniversal=1` was set (owner approved).
- **2026-10-10 07:43Z boot:** the first boot after `RealTimeIsUniversal=1` came up **2 h AHEAD** (expected once: the RTC held
  local time, now read as UTC). `w32time` was Running / Automatic but reported *Source: Local CMOS Clock, not synchronized*
  until the coordinator's elevated `w32tm /resync /force` at 07:51Z put it right. The guard's boot trigger (1 min delay)
  would have resynced it, but it had never run: `LastTaskResult` 267011 (*has not yet run*) at 07:52Z — the owner installed it
  after that boot. Its first run was the timed trigger at 08:00:01Z, `LastTaskResult` 0, and `w32tm /query /status` then read
  *Source: time.windows.com*, last sync 08:00:03Z (read unelevated at 08:02Z). The guest came up with the same 2 h-ahead time and timesyncd stepped it back: the journal stamps
  `wsl-care.service` *Starting* 11:44:02+02:00 and *Finished* 09:48:49+02:00. (Separately: AI OS Care 0.3.0 read the freshly
  installed guard as "not as the current settings would install it" — Task Scheduler stores the delay `PT60S` as `PT1M`;
  fixed in 0.3.1, durations compared by value.)
- What the owner's minidumps say about the 2 h slow boots — three `0x19C` display-driver hangs at monitor power changes, one
  `0x154` compressed-memory read failure, the BSOD → Linux on the disk → RTC in UTC chain:
  [2026-10-09_crash_dumps.md](2026-10-09_crash_dumps.md).
