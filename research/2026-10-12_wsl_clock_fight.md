# Who sets the WSL clock: Docker Desktop's 30-second step, WSL's chronyd, timesyncd, and the journal that keeps under a day — measured 2026-10-12

> Measured 2026-10-12 10:09–11:28Z on the owner's machine, READ-ONLY. Inside WSL: script files in the session's scratch
> directory (not committed), run as `wsl.exe -d <distro> -- sh <script>`, unprivileged except the journal reads and one
> read of `/dev/ptp0` (`-u root`, read-only); one script each in `Ubuntu-26.04` and `docker-desktop` listed processes and
> read configuration. On Windows: unelevated PowerShell 7 (`w32tm /query`, `w32tm /stripchart`, the System event log, the
> registry read, Docker Desktop's own log files). Nothing was started, stopped, disabled, stepped or reconfigured.
>
> - **Machine:** Windows 11 Pro 10.0.26300.9550, time zone *Romance Standard Time* (CEST, UTC+2), `RealTimeIsUniversal=1`
>   since 2026-10-09; WSL 2.7.10.0, kernel 6.18.33.2-microsoft-standard-WSL2, `networkingMode=mirrored`; three distros
>   running in the ONE WSL VM: `Ubuntu` (24.04.1, systemd 255.4, the daemon's), `Ubuntu-26.04`, `docker-desktop`
>   (Docker Desktop 4.81.0). Windows booted 07:41:47Z; the WSL VM's kernel started 07:43:23Z.
> - **Subject:** the installed daemon's full run of 10:02:22Z — `clock.jumps` *328 in 2.1 h (638 per 4 h)*, `clock.fight`
>   *95 in 4 h*, `journal.history` *0.0 days (oldest entry 2026-10-12 07:49Z)*, A16 skipped (timesyncd synchronised).
> - **Related:** [2026-10-08_windows_time_stopped.md](2026-10-08_windows_time_stopped.md) (the first clock fight, Windows
>   2 h slow), [2026-10-02_wsl_resource_baseline.md](2026-10-02_wsl_resource_baseline.md) *Finding 4*,
>   [module_daemon.md](module_daemon.md) *The Windows Time guard*.

## 0. The answer in five lines

1. **Every 30.0 s (guest monotonic) Docker Desktop's `/initd`, inside the `docker-desktop` distro, steps the ONE kernel clock
   that every distro shares** (`settimeofday`) to the time it gets from Docker's Windows backend (`GET /time`). That is the
   `clock.jumps` count: 147 of 147 *Clock change detected* lines in the window the Docker log covers match a `GET /time`
   reply within 0.5 s (median 0.4 ms). It is a floor of ≈ 480 per 4 h for as long as Docker Desktop runs.
2. **The steps are large (−0.11 to −0.13 s) because two other time-keepers pull the same clock apart between them:** WSL's
   own VM-level `chronyd` disciplines it to the Hyper-V host clock (`/dev/ptp0`, every 8 s), Ubuntu's `systemd-timesyncd`
   disciplines it to NTP, and **Windows is 0.17–0.19 s ahead of NTP**. Neither wins; Docker resets the result every 30 s.
3. **The first hour of the boot was worse:** 93 of the Docker steps were followed 32–783 ms later by a SECOND step, and
   journald logged ≈ 95 *Time jumped backwards* — that is the `clock.fight` 95. After 08:44Z it nearly stopped (3 lines
   more by 10:10Z, 2 by 11:23Z).
4. **journald rotates its file whenever a new entry's wall time is earlier than the last entry's, and keeps at most 100
   files (`SystemMaxFiles`, default 100).** The journal directory holds exactly 100 files of 8 MiB; the oldest are deleted
   BY COUNT. The daemon read 1.6 days of history at 08:03:54Z and 0.0 days at 10:02:22Z: the first hour's rotations
   erased everything older than 07:49Z.
5. **Ranked fixes (§ 5):** F1 (advice only, decided 2026-10-12) leave the clock to WSL's own `chronyd` —
   `timedatectl set-ntp false` in `Ubuntu`, reversible;
   F2 (YES, the owner 2026-10-12) keep Windows closer to NTP — the guard's existing
   `wslCare.windowsTime.guard.everyHours` 4 → 1; F3 Docker Desktop has no setting for it — upstream issue, stop it when unused, and the daemon names it in its verdicts.

## 1. Who can set the clock — the inventory (10:09:59Z unless stated)

All three distros run in one VM with one kernel and one `CLOCK_REALTIME`; a step made in any of them is a step in all.

| # | Time-keeper | Where it runs | What it does | Source |
|---|---|---|---|---|
| K1 | **`chronyd`, started by WSL's own init** | the WSL VM's root, invisible to every distro's `ps` | `refclock PHC /dev/ptp0 poll 3 dpoll -2 offset 0`, `makestep 1.0 3`, `rtcsync` — disciplines the kernel clock to the Hyper-V host clock, polling every 2³ = 8 s; steps only in its first three updates | [`StartTimeSyncAgent`, microsoft/WSL tag 2.7.10, `src/linux/init/main.cpp`](https://github.com/microsoft/WSL/blob/2.7.10/src/linux/init/main.cpp) ("Start the time sync agent (chronyd) to keep guest clock in sync with the host"). Present in every open-source tag since 2.5.8, the first; when it was added before that could not be confirmed |
| K2 | **`systemd-timesyncd`** | `Ubuntu` (24.04), active and enabled | NTP client of `ntp.ubuntu.com` (the fallback; no server configured); slews (`ADJ_OFFSET`, `STA_PLL`) an offset under 0.4 s, STEPS (`ADJ_SETOFFSET`) one of 0.4 s or more; on any clock change it polls at once and resets to its minimum interval (`poll_resync`) | Ubuntu's drop-in `/usr/lib/systemd/system/systemd-timesyncd.service.d/wsl.conf` re-enables it on WSL "so WSL clock is synced on resume from suspend of the host"; the 0.4 s threshold is `NTP_MAX_ADJUST` in [systemd v255 `src/timesync/timesyncd-manager.c`](https://github.com/systemd/systemd/blob/v255/src/timesync/timesyncd-manager.c) |
| K3 | **Docker Desktop's `/initd`** | `docker-desktop` (pid 41, CapEff all capabilities) | steps the clock with `settimeofday` to the backend's time, every 30 s (§ 3) | the binary links `syscall.Settimeofday`; its strings hold `…/cmd/initd/desktop/time.go`, `(*Services).startTime` with two closures, and the messages `backend time: %w`, `settimeofday: %w`, `delta %s, rtt %s`; Docker's VM log records `"component":"time","msg":"delta 3.497697ms, rtt 33.496124ms"` once, at 07:43:23Z |
| K4 | **`hv_utils` TimeSync** (kernel) | the kernel | steps to the host time on a host `SYNC` message, and — because WSL boots it with `hv_utils.timesync_implicit=1` — on a `SAMPLE` while the guest is **5 s or more BEHIND** the host | `/proc/cmdline`; [WSL `WslCoreVm.cpp`](https://github.com/microsoft/WSL/blob/2.7.10/src/windows/service/exe/WslCoreVm.cpp) ("Enable timesync workaround to sync on resume from sleep in modern standby"); `hv_implicit_sync` in [`drivers/hv/hv_util.c`, linux-msft-wsl-6.18.y](https://github.com/microsoft/WSL2-Linux-Kernel/blob/linux-msft-wsl-6.18.y/drivers/hv/hv_util.c). Not seen active here: no sample had the guest behind the host by more than 0.04 s |
| — | `chronyd` 4.8 in `Ubuntu-26.04` | `Ubuntu-26.04` | PASSIVE: runs with `-x` (does not control the clock) | its `chronyd-starter.sh` adds `-x` when `systemd-detect-virt --container` succeeds — it prints `wsl` — and `/etc/default/chrony` has `SYNC_IN_CONTAINER="no"`. Its `chronyc tracking` read the shared clock *0.139 s fast of NTP* |
| — | none | `Ubuntu` | no chrony, ntpd or `hwclock` installed (so A16's `hwclock -s` could not run here either) | `command -v` |

`timedatectl` in `Ubuntu`: `NTP=yes`, `NTPSynchronized=yes`, `PollIntervalUSec=32s` (its minimum, held there by the steps),
packet count 297 at 10:10Z, *Offset: −168.835 ms*, *Frequency: −131.881 ppm*. The clock source is `tsc`; `/dev/ptp0` is
`hyperv`.

## 2. The steps, measured — what moves the clock, which way, how far

### 2a. 200 s of `CLOCK_REALTIME − CLOCK_MONOTONIC` and `adjtimex` (read-only, `modes=0`), 10:11:42–10:15:01Z

| # | Observed | Value |
|---|---|---|
| S1 | steps (a jump of realtime − monotonic over 2 ms between 50 ms samples) | **7 in 200 s, every 30.00–30.06 s of monotonic time; each −0.1116 to −0.1328 s; sum −0.846 s** |
| S2 | the kernel at each step | `adjtimex` state 5 (`TIME_ERROR`), status `0x40` (`STA_UNSYNC`), offset 0 — the signature of a `settimeofday` / `ADJ_SETOFFSET` |
| S3 | 50–150 ms after each step | status `0x2001` (`STA_PLL`\|`STA_NANO`), time constant 1, offset −156.7 to −170.0 ms — timesyncd's slew (constant = log2(32 s) − 4), i.e. right after a step the clock is ≈ 0.17 s AHEAD of NTP |
| S4 | every ≈ 8 s between steps | status reset to 0, frequency rewritten (−78 to +146 ppm) and `tick` rewritten (10 051 to 10 097 µs, nominal 10 000) — the cadence of K1's 8-s poll; timesyncd acts only right after a step (S3), and no other process in `Ubuntu` or `Ubuntu-26.04` controls the clock (the writer was not traced) |
| S5 | the net rate | `CLOCK_MONOTONIC` ran **+4 022 ppm** against `CLOCK_MONOTONIC_RAW` over the 200 s (`tick` ≈ +0.95 % against timesyncd's slew) |

### 2b. 100 s of `CLOCK_REALTIME − PHC(/dev/ptp0)` — the distro against the Hyper-V host clock, ≈ 10:20Z

| Step | realtime − host before | after | the host clock itself |
|---|---|---|---|
| 1 | +0.1125 s | −0.0026 s | −0.0005 s (no jump) |
| 2 | +0.1050 s | −0.0011 s | −0.0004 s |
| 3 | +0.1170 s | −0.0157 s | −0.0004 s |

Between steps the distro drifts from ≈ −0.03 s (timesyncd pulling toward NTP, 0.17 s below the host) to ≈ +0.11 s (K1's
`tick` overshooting back toward the host). **Each step puts the distro back on the host clock; the host clock does not
move.** The 0.4 ms "step" of the PHC column is sampling noise.

### 2c. Windows against NTP (unelevated `w32tm`)

| When | `time.windows.com` | `ntp.ubuntu.com` | `w32tm /query /status` |
|---|---|---|---|
| 10:15:10Z | −0.1707, −0.1683, −0.1686 s | −0.1715, −0.1718, −0.1688 s | Automatic/Running, *Source: time.windows.com,0x9*, *Last Successful Sync Time* 08:00:05Z, *Poll Interval: 10 (1024s)*, *Last Sync Error: 2 (… only stale time data was available)*, *Root Dispersion 8.295 s* |
| 11:28:19Z | −0.1880, −0.1902, −0.1916 s | — | last successful sync still 08:00:05Z |

The stripchart's sign is the same as timesyncd's: Windows is **ahead** of NTP by 0.17–0.19 s, the same 0.17 s timesyncd
measures right after each step (S3) — the step target is Windows' clock. Registry (read): `SpecialPollInterval` 16 384 s,
`UpdateInterval` 360 000, `PhaseCorrectRate` 1, `MaxAllowedPhaseOffset` 1. The 08:00:05Z sync is the Windows Time guard's
4-hourly run (its task last ran 08:00:01Z, result 0); `Kernel-General` event 1 logged three sub-millisecond Windows steps
today (07:43:11Z, 07:47:09Z, 08:00:05Z).

## 3. The 30-second step is Docker Desktop's

| # | Observed | Value |
|---|---|---|
| D1 | Docker's Windows backend log (`%LOCALAPPDATA%\Docker\log\host\com.docker.backend.exe.log*`) | a `GET /time` request answered every **29.85–29.91 s of Windows time** (= 30.0 s of the guest's fast monotonic clock); 416 since 2026-10-11 22:01Z, as far as the rotated files reach |
| D2 | the match, 09:53:58–11:07:01Z (the window both logs cover) | **147 of 147** systemd-resolved *Clock change detected* lines in `Ubuntu` fall within 0.5 s of a `GET /time` reply; median journal − reply **+0.4 ms** (e.g. 09:53:58.534 / .533, 09:54:28.411 / .410). The two lines left over at 10:02:22Z are the daemon's own log lines quoting the phrase (its counter reads `--unit=systemd-resolved` only, so it does not count them) |
| D3 | the binary | § 1 K3 |
| D4 | upstream | [docker/for-win#15000](https://github.com/docker/for-win/issues/15000) (OPEN): another user's `bpftrace` capture attributes the 30-s `settimeofday` to `comm=initd` with the Hyper-V setter `hv_set_host_time` called zero times, shows the steps stop on `DockerCli.exe -Shutdown` and return on restart, on Docker Desktop 4.77.0 and 4.89.0; there the steps were 1–2 ms. Same symptom: [microsoft/WSL#13777](https://github.com/microsoft/WSL/issues/13777) (open). The systemd side: [systemd/systemd#43028](https://github.com/systemd/systemd/issues/43028) (closed — a stepper making small continuous corrections should slew with `adjtimex`, not step) |

**What this licenses:** on this machine and day, every counted clock change was a Docker step; the count is a property of
Docker Desktop running, not of the clocks' health. **What it does not:** this task did not trace the syscall (no
`bpftrace` — it would change kernel tracing state), so "initd calls `settimeofday`" rests on the upstream trace, the
binary's strings and the 147/147 timing match; whether `/initd` ever skips a step when the delta is small is not settled
here (upstream: 1–2 ms steps are still made).

## 4. The first hour, and why the journal keeps under a day

| # | Observed (this boot, journal of `Ubuntu`, read 10:10Z) | Value |
|---|---|---|
| J1 | systemd-resolved clock changes, 07:49–10:10Z | 380: **285 intervals of 29–31 s** (Docker), **93 second changes 32–783 ms after a Docker step**, all in monotonic 0–3 600 s (≈ 07:43–08:44Z) |
| J2 | journald *Time jumped backwards, rotating* | 98 (pid 51) and 95 (pid 65); all but 3 in monotonic 0–3 600 s; 3 in 5 400–7 200 s |
| J3 | the journal directory | **exactly 100 files** (96 archived `system@…`, the active `system.journal`, and 3 `user-1000` files, one of them active), 8 MiB each, 367 MB in all; the oldest archive 09:49 local = 07:49Z |
| J4 | the daemon's own reading | 08:03:54Z: `journal.history` *1.6 days (oldest entry 2026-10-10 07:48Z)*; 10:02:22Z: *0.0 days (oldest entry 2026-10-12 07:49Z)*. The days are counted from the run's window start (`HealthSample.Since`, the previous run's start), not from the run itself — `src_daemon/src/WslCare.Core/Thresholds/ThresholdRules.cs:238` `health.Since - oldest` — which is why 08:03:54Z minus 10-10 07:48Z (2.0 days) printed 1.6 (§ 8) |
| J5 | this boot's own start | the VM 07:43:23Z, timesyncd started 07:44:23Z — yet the oldest entry is 07:49:22Z: even the boot's first six minutes are gone |

- **Measured mechanism of the loss:** journald rotates its files when the wall clock jumped backwards RELATIVE TO ITS LAST
  ENTRY — a step back smaller than the time since that entry rotates nothing
  ([v255 `journald-server.c`](https://github.com/systemd/systemd/blob/v255/src/journal/journald-server.c)), and
  `SystemMaxFiles=` "defaults to 100" ([journald.conf(5)](https://www.freedesktop.org/software/systemd/man/255/journald.conf.html));
  ≈ 95 rotations of the system file, plus the user file's, in one hour pushed every older file out by COUNT, not by size.
- **Inferred, not measured — the second steps of J1:** timesyncd steps only an offset of 0.4 s or more, so after each
  Docker step to Windows' time the distro was ≥ 0.4 s from NTP, and the backward journald jumps say timesyncd stepped it
  BACK — Windows was then ≥ 0.4 s ahead of NTP. A plausible cause is the boot: the RTC (1 s resolution, read as UTC since
  `RealTimeIsUniversal=1`) and a w32time that slews a sub-second offset slowly (`UpdateInterval` 360 000,
  `PhaseCorrectRate` 1). Windows' offset during 07:43–08:44Z was not measured (timesyncd's own lines for that hour were
  among the erased files).
- **Re-reading 2026-10-08** ([2026-10-08_windows_time_stopped.md](2026-10-08_windows_time_stopped.md) M10): the
  "≈ every 33 s" rhythm of that day fits a Docker step to a Windows clock 2 h slow followed by timesyncd's step forward.
  That day's Docker logs are rotated away, so this stays a hypothesis.

## 5. Fixes, ranked

### F1 — leave the clock to WSL's own `chronyd`: `timedatectl set-ntp false` in `Ubuntu` (recommended)

- **What it changes:** stops and disables `systemd-timesyncd` (reversible: `timedatectl set-ntp true`). K1 is then the only
  discipline of the shared clock and Docker's step has almost nothing left to correct. Ubuntu 26.04 already behaves this
  way inside WSL (its chronyd is `-x`, § 1), and Microsoft's own init runs `chronyd` precisely "to keep guest clock in sync
  with the host".
- **Risk:** the distro then keeps **Windows' accuracy, not NTP's** — today 0.17–0.19 s (§ 2c), on 2026-10-08 two hours.
  That is already what K1 (every 8 s) and K3 (every 30 s) enforce; timesyncd only fights it. A Windows clock that is wrong stays
  visible through `clock.reference`. Second risk: K1 is invisible from the distro, so "it runs" cannot be read directly —
  hence the live check below. Third: `timedatectl`'s `NTPSynchronized` comes from the kernel's `STA_UNSYNC`, which K1
  clears (status 0, S4); predicted to stay `yes` — and A16's first skip depends on it, so it must be re-read after F1.
- **How wsl_care would apply it — advice first, then an action with a panel button.** Preconditions, each a fact the
  preview reads: `/dev/ptp0` is `hyperv`; the fight is measured (backward jumps or a step size above a setting); and a
  **live check that something other than timesyncd holds the clock on the host** — sample `CLOCK_REALTIME − PHC` and
  the `adjtimex` frequency for `clock.handover.sampleSeconds` (proposed 60) and require writes on the ≈ 8-s cadence K1
  makes (S4) **and** every sample's `|realtime − PHC|` within `clock.handover.maxPreOffsetMillis` — a clock already far
  from the host is refused, the failed precondition named in the preview, and nothing is changed. The dry run shows the
  command, the samples and the rollback. After the change the action samples again for `clock.handover.verifySeconds`
  (proposed 120) and, if `|realtime − PHC|` exceeds `clock.handover.maxOffsetMillis` or no 8-s writes are seen, runs
  `timedatectl set-ntp true` and reports the failure. A second button gives the clock back to timesyncd. Every number is
  a setting; nothing runs from the timer by default.
- **The two offset limits are NOT measured yet, and must be before the action ships** ("measured before recommended").
  What exists: with timesyncd fighting, `realtime − PHC` ranged −0.04 to +0.117 s (§ 2b), so the PRE limit has to sit
  above that fight or F1 could never start — a starting point of 250 ms (≈ 2 × the measured maximum) is a guess to be
  replaced. The POST limit can only come from an after-measurement of the handed-over clock (§ 6 predicts ≤ 5 ms of
  step; the upstream report measured 1–2 ms): take its observed maximum over a day and add a stated margin. Until then
  F1 is advice only.
- **Decided 2026-10-12 (coordinator): F1 stays ADVICE.** No action and no button is built until both offset limits are
  measured; the action described above is the design it would follow then, not work that is scheduled.

### F2 — keep Windows closer to NTP: the guard's resync every hour

- **What it changes:** `wslCare.windowsTime.guard.everyHours` 4 → 1 (an existing setting, 1–168; applies when the guard is
  reinstalled, one UAC prompt). Windows' own poll is `SpecialPollInterval` 16 384 s (4.55 h); it had not synced since the
  guard's 08:00:05Z resync (3.5 h at 11:28Z), reported *only stale time data* at 10:15Z, and was 0.19 s ahead by then.
  The fight's step size (§ 2) is set by exactly that offset, and after F1 the distro inherits it.
- **Risk:** low — the same task, run more often; four times as many SYSTEM-task runs a day. Not measured: how far Windows
  drifts in one hour (predicted: under 0.08 s — the observed drift was 0.076 s per hour over 08:00–10:15Z and 0.016 s per
  hour over 10:15–11:28Z).
- **How wsl_care would apply it:** advice in the `clock.fight` / `clock.reference` reasons, pointing at the setting and the
  guard's existing *Install* button. A direct Windows `SpecialPollInterval` change would be a new elevated action and is
  not proposed.
- **Decided 2026-10-12 (the owner): F2 is YES.** `wslCare.windowsTime.guard.everyHours` is set to 1 (by the
  coordinator); it applies once the owner reinstalls the guard with its *Install* button (one UAC prompt). Measure it
  with § 2c's `w32tm /stripchart` an hour after a guard run and with the guard task's last-run time.

### F3 — Docker Desktop: no setting, so stop it when unused and name it

- Docker Desktop 4.81.0 has no time setting (its `settings-store.json` has none; the upstream issue asks Docker to slew
  instead of stepping). The floor of ≈ 480 clock changes per 4 h — and systemd-resolved flushing its DNS cache at each —
  lasts as long as Docker Desktop runs.
- **How wsl_care would apply it:** advice only. The product change belongs to the verdicts: `clock.jumps` should say
  *"Docker Desktop's `/initd` steps the clock every 30 s while it runs (docker/for-win#15000)"* when the `docker-desktop`
  distro is running, and report the steps' SIZE (a short realtime − monotonic watcher) beside their count. What the
  journal loses is judged by the rotations themselves (`clock.fight`) and `journal.history`, never inferred from the
  step size: measured here, the 0.12-s steps of 08:44–11:23Z left 5 rotation lines in ≈ 2.7 h, and steps of a few ms are
  predicted to leave fewer — a prediction for § 6, not a measurement.

### Not recommended

- Raising `SystemMaxFiles` — treats the symptom; archived files stay 8 MiB each here (J3), so 1 000 files could hold 8 GiB.
- `hwclock -s` (A16's tool) — absent in this distro, and a step is the problem, not the cure.
- `hv_utils.timesync_implicit=0` (via `.wslconfig` `kernelCommandLine`) — not the path that steps (K4 never fired; the
  upstream trace shows `hv_set_host_time` at zero).
- Microsoft's [wsl-config](https://learn.microsoft.com/en-us/windows/wsl/wsl-config) documents no clock-synchronisation
  setting (only `[time] useWindowsTimezone`); K1 cannot be switched off or retuned from Windows.

## 6. How success is measured — predictions written before any fix

| Metric | Now | After F1 (predicted) | After F1 + F2 (predicted) | Without Docker Desktop |
|---|---|---|---|---|
| `clock.fight` (journald backward jumps, last 4 h of the boot) | 95 at 10:02Z; 5 lines (pids 51, 65) over 08:44–11:23Z | ≤ 1 | ≤ 1 | ≤ 1 |
| step size (realtime − monotonic watcher, 200 s) | −0.11 to −0.13 s | ≤ 5 ms (upstream: 1–2 ms) | ≤ 5 ms | no steps |
| `clock.jumps` | 638 per 4 h; ≈ 480 per 4 h in steady state | ≈ 480 per 4 h — UNCHANGED, Docker's floor | ≈ 480 | ≈ 0 |
| `journal.history` | 0.0 days | grows ≈ 1 day per day, `ok` after `thresholds.journalHistoryWarnDays` (7) | same | same |
| distro − NTP | ≈ +0.17 s right after each step, −0.03..+0.11 s against the host between | = Windows − NTP (≈ +0.17..0.19 s) | under ≈ 0.08 s | unchanged |

Re-measure with the same read-only scripts (§ 2a, 2b) and the daemon's next full runs; a prediction that fails is a finding.

## 7. What this record does NOT settle

- Why `/initd` steps unconditionally every 30 s (its source is not public); whether it skips tiny deltas.
- Windows' offset during the first hour (07:43–08:44Z) and so the cause of the 93 second steps (§ 4, inferred).
- The two journald process ids (51, 65) in one boot of `Ubuntu`; only 65 runs now.
- Whether WSL releases before 2.5.8 started K1 (closed source).
- F1 and F2 are untested on this machine; § 6 is a prediction.

## 8. Found on the way — for the product (proposals, not done here)

- `clock.fight`'s reason names *"Hyper-V's time sync sets the host's clock, timesyncd sets NTP's"*: on this machine the
  host-side writers are K1 (continuous) and K3 (the 30-s step); the reason should name the ones present.
- `clock.jumps`' warn line (100 per 4 h) is below Docker's floor, so it warns on every run while Docker Desktop runs, about
  something the owner cannot change from wsl_care (F3).
- `journal.history` counts its days from `HealthSample.Since` (the previous run's start,
  `src_daemon/src/WslCare.Core/Thresholds/ThresholdRules.cs:238`), not from the sample's own instant, so it under-reads
  by the time since the last run (1.6 instead of 2.0 days at 08:03:54Z).
  The golden contract's description says "now minus the captured oldest journal entry".
- `journal.history`'s reason ("clock jumps erase it") can name the mechanism: rotation per backward jump, deletion by file
  count (`SystemMaxFiles` 100).
- The shared rules resolver (`.agents/conventions/tools/rules.mjs check`) reports instruction loading INCOMPLETE on `main`:
  `research/architecture.md` is over its 256 KiB per-file limit. Not touched here.
