# The Windows Time service stopped, Windows 2 h slow, and two clocks fighting inside WSL — measured 2026-10-08

> The incident that started [PLAN_windows_time_guard.md](../todo/PLAN_windows_time_guard.md), kept in one place so no
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
