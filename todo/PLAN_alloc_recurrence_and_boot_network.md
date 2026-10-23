# PLAN — allocation failures that come back between full runs, and Windows network services that fail at boot

> Status: **plan only, nothing implemented yet, 2026-10-22.** coai plan round (session 95db59ad): good_enough, 2 of 2
> reviewers, all 8 findings accepted and written in (the journal cursor, the ledger's budget, a share-of-MemTotal threshold,
> the boot boundary by record id, the persisted cooldown, display-only panel rows). Scope: the daemon's memory actions A1/A2 and the watch run
> (`src_daemon/src/WslCare.Core/Actions/Memory/`, `Watch/`), the Windows-side health reading (`Health/`), the thresholds
> (`Thresholds/`), the extension's panel lines for the two new verdicts. Found by the owner-requested review of the Windows and
> Linux logs on 2026-10-21. Related docs: [module_daemon.md](../research/module_daemon.md),
> [module_tests.md](../research/module_tests.md), [architecture.md](../research/architecture.md),
> [PLAN_twenty_sessions_all_day.md](PLAN_twenty_sessions_all_day.md) (the watch run, E14 S2b).

## 1. What was seen (measured 2026-10-21, the owner's machine)

**Case A — the WSL kernel fails high-order allocations again within an hour after A2.**

- The kernel log (`journalctl --dmesg`) holds 6 `page allocation failure` reports on 2026-10-21: order 7
  (`GFP_KERNEL|__GFP_ZERO`, 512 KiB contiguous) at 15:28, 17:53, 18:53, 18:56, 18:57 UTC, all in a kworker running
  `vmbus_add_channel_work` (workqueue `hv_pri_chan`: Hyper-V adding a VMBus channel), and one order 4 (`GFP_NOFS|__GFP_COMP`)
  in a user process at 18:56 UTC.
- The daemon SAW it: the full run at 18:03 UTC set `kernel.allocationFailures` **critical, value 2**
  (`Thresholds/ThresholdRules.cs:137`), and A2 ran at once on the event: "A2 ran: ran at once, without waiting for idle:
  event: 2 page allocation failure(s) in the kernel log since the last run".
- It did not hold: four more failures followed 50–54 minutes later, between full runs (the timer runs every 4 h; the next
  run at 22:01 UTC). Free order-7 blocks in zone Normal fell from 1626 (10:01 UTC) and 2482 (14:04 UTC) to 21 (19:25 UTC,
  `/proc/buddyinfo`); `memory.fragmentation` read warn ("23 order-7 blocks, 605 order-4", `ThresholdRules.cs:178`).
- Memory at that moment: 46 GiB total, 0.4 GiB free, **25 GiB page cache**, 25 GiB available (55 %),
  `Committed_AS` 31.6 GiB of `CommitLimit` 36.1 GiB, memory PSI near 0. So: fragmentation by a large clean page cache, not a
  shortage.
- A1 (`sync; sysctl -w vm.drop_caches=1`) did not run: its trigger is the page cache above 12 GiB **while less than 30 % is
  available** (`Actions/Memory/CacheDrop.cs:15-16`); 55 % was available, so A2 compacted without A1 first, and the cache
  refilled the gaps (`Actions/Memory/Compaction.cs:15-19`: A2 runs after A1, or at once on the event).
- The watch run (every `mcpWatchdog.periodMinutes`, `Systemd/UnitDropIns.cs:77-79`) lets only A19 and A21 act
  (`Watch/WatchRun.cs:92-95`); it reads no kernel signal, so nothing reacts between full runs.

What a person loses: when VMBus cannot allocate a channel's ring buffer, the Hyper-V device that asked for it fails (file
access to `/mnt/c`, hvsocket for interop, the GPU's dxg). The symptom is a `wsl.exe` command or an interop call that hangs
or fails "for no reason", hours before the next full run looks again.

**Case B — the Windows Host Network Service failed to start at boot.**

- System log, 2026-10-21 15:32:31 local (the restart at 15:32:14): Service Control Manager **7038** "The hns service was
  unable to log on as NT AUTHORITY\SYSTEM … The request is not supported" and **7000** "The Host Network Service service
  failed to start due to the following error: The service did not start due to a logon failure."
- Around it, new in the 30-day window: Hyper-V-VmSwitch **15** "Failed to restore configuration for port …" (22 events, both
  boots of 2026-10-21), DistributedCOM **10010** (servers that did not register in time) and **10016**.
- At 21:00 local `hns`, `WinNat` and `vmcompute` were Running (StartType Manual): hns started later on demand. No OS update
  was installed between 2026-10-14 and 2026-10-21 (only Defender signatures), so it is not a patch regression.
- What a person loses: until hns comes up, WSL's NAT network and Docker's networks are not restored after boot (the VmSwitch
  port errors); a distro started in that window has no network or a stale one.
- The daemon reads no Service Control Manager event and no hns state today (the Windows clock probe reads only `w32time`,
  `Health/HealthCommands.cs:27`, `Health/ClockStanding.cs:13`).

**Check C (not a story) — a USB keyboard's tiny disk raises `disk` controller errors.**
`\Device\Harddisk1\DR1` is the ClawsKey keyboard's 0 GB FAT12 volume (E:), not the NVMe system disk: 30 `disk` id 11
events since 2026-10-20. The E15.S1 event reader counts provider `disk` id 11 (`Health/WindowsDiskCommands.cs:78-81`) and
groups by `HarddiskN` (`:188-193`). The story below confirms that a removable USB disk's events never raise the system
disk's verdict, with a test, and fixes it if they do.

## 2. Goal

1. Case A: an allocation failure between full runs is noticed within one watch period, and the response is the one that
   holds: give the clean page cache back (A1), then compact (A2) — not A2 alone over a 25 GiB cache.
2. Case B: a failed start of the network services at the last boot is a verdict with its reason and the current state, so
   the panel says "networking for WSL may be missing after boot" instead of nothing.
3. Nothing new is written without the owner's switches: every action still passes `dryRun`, its `auto.*` switch and the
   never-list; the product never writes `.wslconfig` and never restarts a Windows service by itself.

## 3. Stories

### AH-1 — the watch notices an allocation failure (daemon, Linux side)

- The watch run reads the kernel log since its last run (the same search as the full run's
  `Health/HealthCollector.cs:68-69,105`: `page allocation failure|invoked oom-killer`, `--dmesg`), records the count in the
  watch's own ledger, and exposes it in `status` as the sample-sourced half of `kernel.allocationFailures` (basis `watch`).
- The full run's verdict stays "since the last FULL run"; the watch never moves its cursor (two cursors, named).
- Bounded: the same `health.kernelLinesKept` / `health.kernelLineChars` keys; one search per watch run.
- **The cursor (plan round, findings 0 and 7).** The watch's cursor is the journal's own position: the boot id plus the
  realtime timestamp of the last entry it read (journald keeps the kernel log on disk, so a wrapped ring buffer does not
  lose what journald already stored). A cursor of an OLDER boot id is reset to this boot's first entry; a cursor older
  than the journal's earliest kept entry (vacuumed) makes that sample **unknown** ("the journal no longer holds the range
  since the last watch"), never 0. Tests for both.
- **The ledger's budget (plan round, findings 1 and 5).** One record per watch run that found a failure or ran an action
  (a run that found none writes nothing); at the default period that is at most 288 records a day in a storm, about 200
  bytes each. Retention: records older than 24 h are removed by the next watch run (a rolling window — the recurrence
  rule of AH-3 needs only 24 h), so the file stays under ~60 KiB. A record is written whole with the existing atomic
  write; an interrupted run leaves either the old file or the new one, never a half record, so there is no in-flight
  state to sweep.
- Tests RED first: a watch over a fake kernel log with two failures records 2 and leaves the full-run cursor unchanged; a
  watch with none records 0; an unreadable log is "unknown", never 0.

### AH-2 — the watch's response: A1 then A2 on an allocation failure (daemon)

- On an allocation failure found by the watch, the watch lets the memory actions act, in order: A1 when the page cache is
  above `memory.allocFailureCachePercent` of MemTotal (new key, default 25 %, with a floor of 2 GiB) whatever the
  available share, then A2 — each still behind its own `auto.*` switch, `dryRun`, and the engine's gates. A1's existing
  trigger (`CacheDrop.cs:15-16`) is unchanged on the full run; this is a second trigger, named in A1's preview reason.
- **Why a share and not 8 GiB (plan round, finding 2).** The only measurement is one machine (46 GiB, 25 GiB cache = 54 %);
  an absolute 8 GiB would be unreachable on an 8 GiB WSL and trivial on a 128 GiB one. A share of MemTotal scales; 25 % is
  below the measured 54 % with a margin, and the floor keeps a tiny VM from dropping a few hundred MiB. Tests at 8, 46 and
  128 GiB MemTotal pin the boundary; POST_DEPLOY records the real figure at the next failure, and the default is revisited
  with that measurement.
- A cooldown: at most one A1+A2 pair per `memory.allocFailureCooldownMinutes` (new key, default 30) so a storm of failures
  is not a storm of cache drops. **The cooldown is persisted (plan round, finding 4):** the time of the last pair and the
  recurrence count live in the watch ledger on disk (AH-1), read at the start of every watch run — each watch run is its
  own process, so an in-memory timestamp would be lost. A test: two watch PROCESSES five minutes apart over the same
  ledger → the second does not drop the cache again.
- A2's measured effect (order-7 blocks before/after, already measured) is kept, and the NEXT watch run records whether
  failures recurred within the cooldown — the recurrence count is a fact in the ledger, the input for the advice in AH-3.
- Tests RED first: failure + 25 GiB cache + 55 % available → A1 then A2 recorded (dry: "would"); failure + 2 GiB cache → A2
  only; a second failure inside the cooldown → no second pair, the recurrence counted; `auto.A1` off → A2 only, the reason
  says so; `dryRun` → nothing runs, both "would" lines recorded.

### AH-3 — the verdict and advice when failures keep coming back (daemon + extension)

- New verdict `kernel.allocationFailuresRecurring`: warn when failures recurred within the cooldown after an A1+A2 pair at
  least `memory.allocFailureRecurWarn` times (new key, default 2) in 24 h. Reason names the measured cache and order-7
  figures.
- Advice text (advice only, the product never writes `.wslconfig`): WSL's `autoMemoryReclaim` in `%UserProfile%\.wslconfig`
  (`gradual` / `dropCache`) returns the page cache continuously; with the exact lines to add and the `wsl --shutdown` it
  needs. The extension shows it on the memory card; no button changes `.wslconfig`.
- Tests RED first: two recurrences in 24 h → warn with the advice; one → ok; the advice never appears for a single failure
  that A1+A2 cured.

### AH-4 — the Windows network services at boot (daemon, Windows side, plus the panel line)

- The Windows reading (the same pwsh road as the disk events, `Health/WindowsDiskCommands.cs:192`: bounded `Get-WinEvent`,
  `NoMatchingEventsFound` = none) reads, since the last boot only: Service Control Manager 7000, 7009, 7023, 7031, 7038 whose
  service is one of `hns`, `WinNat`, `vmcompute`, `WSLService`/`LxssManager`; and the count of Hyper-V-VmSwitch 15. Plus the
  current `Status`/`StartType` of those services (as `w32time` is read, `HealthCommands.cs:27`).
- New verdict `host.networkServicesAtBoot`: warn when one of them failed to start at the last boot (the reason names the
  service, the event id and the message's first line, and whether it is Running now); ok when none failed; unknown when the
  log or the service state could not be read. VmSwitch 15 is a supporting figure in the reason, not its own trigger.
- No action: the product does not restart a Windows service. The advice says how (an elevated `Restart-Service hns`, or a
  restart), and that a later on-demand start already happened when it did.
- Bounded: `Get-WinEvent -MaxEvents` from a new key `health.bootServiceEventsMax` (default 50).
- **The boot boundary (plan round, finding 3, blocking).** Not a bare `StartTime = LastBootUpTime`: SCM events of early
  boot can carry a time at or before the recorded boot time, and the CIM time and the event filter can differ in zone. The
  boundary is the newest Kernel-General id 12 event ("The operating system started", System log) — its `RecordId`; the
  service events kept are those with a higher `RecordId` in the same log. The query itself still uses a `StartTime` of
  that event's time minus 5 minutes, so it stays bounded, and the record id decides. No Kernel-General 12 readable →
  unknown. Tests: a 7038 a few seconds BEFORE the boot event's time but with a higher record id counts; a 7038 of the
  previous boot (lower record id) does not.
- Tests RED first (parser and rule, fixtures sanitised — no host or user name): 7038+7000 for hns at this boot → warn naming
  hns and "Running now"; the same events at an EARLIER boot → ok; none → ok; `Get-WinEvent` failing → unknown, never ok; a
  7000 for an unrelated service → ok.

### AH-5 — check C: a removable disk's events never raise the system disk's verdict (daemon)

- A test over the E15.S1 reader: disk id 11 events on `Harddisk1` where disk 1 is a USB removable with no system volume →
  the system disk's verdict is unchanged; the removable's events are reported under its own disk, or as "a removable
  device", never as the system disk's. Fix the reader only if the test is red.

### The panel (plan round, finding 6)

AH-3 and AH-4 add DISPLAY-ONLY rows to the panel (the verdict line, its reason, the advice text). They register no new VS
Code command, so `src_vs_code/src/panel/commandButtons.ts` and its test do not change; if a later story adds a command
(say, a "show me the `.wslconfig` lines" copy button), that story declares its button there, as the project requires.

## 4. Build order

1. AH-5 (a test first; it may need no product change).
2. AH-1 (the watch reads the kernel log) → AH-2 (A1 then A2 from the watch) → AH-3 (recurrence verdict and advice).
3. AH-4 (independent of AH-1..3; may run in parallel on its own branch).
Each story: coai plan round on this plan's section, RED-first tests, break-its (never one that weakens a tripwire), Fable
review for AH-2 (it widens when a root action runs) and AH-4 (a new pwsh read of the event log), coai code round, docs
(`module_daemon.md`, `module_tests.md`, `architecture.md` when the watch's actions change), full suites, a merge on all
green.

## 5. Test plan

- Unit: the kernel-log search and the two cursors (AH-1); the trigger order, the cooldown, the switches and `dryRun`
  (AH-2); the recurrence rule and the advice text (AH-3); the SCM/VmSwitch parser, the boot boundary, the rule (AH-4); the
  removable-disk attribution (AH-5).
- Scenario: a watch run over a fake world with a failure and a 25 GiB cache records A1+A2 "would" in dry mode.
- Contracts: `config-keys.json` and the status golden regenerated (the new keys and verdicts), never merged by hand.
- Live (POST_DEPLOY, the owner's machine): after the release, `status` shows `host.networkServicesAtBoot` for the last boot
  and `kernel.allocationFailures` with a `watch` basis.

## 6. Definition of Done

- [ ] AH-1..AH-5 built, each RED first, with break-its, docs and a merge on all green.
- [ ] Fable SHIP for AH-2 and AH-4.
- [ ] The two new verdicts and the new keys are in `config-keys.json`, the status golden and the panel.
- [ ] No new write to `.wslconfig`, no restart of a Windows service, every memory action still behind `dryRun` and `auto.*`.
- [ ] POST_DEPLOY has the live checks of §5.
- [ ] This plan promoted to `research/` with its status `IMPLEMENTED <date>` and its deviations.
