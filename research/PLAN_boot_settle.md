# PLAN — the 4-hour run waits for the machine to settle after a boot

> Status: **IMPLEMENTED, 2026-10-10.** Scope: `wsl-care collect --timer`, `RunBudget`, `NumberRules`, the timer keys, the shipped
> `wsl-care.service`. **Deviations** (coai code round `aebbaafd`, all four gating findings acted on):
>
> - **The lock.** A manual run that took the lock during the settle wait would have turned the timer run away as busy, because
>   its lock wait was only `requests.lockWaitSeconds`. A settled timer run now waits for the lock for what is left of
>   `timer.busyWaitMinutes`, never less than before (`RunSettle.LockWaitAfter`). That stays inside the bound the budget counts.
> - **A waiting run says so** in its log before each wait (`collect --timer: waiting 13 min for the boot to settle …`). A
>   persisted "settling" state was REJECTED: the wait holds no lock and changes nothing, and a second running-state protocol
>   (sweep, staleness, the extension's rendering) for it is a follow-up the owner may ask for.
> - **A scenario over the built CLI** (`BootSettleFlows`) was added. The captured `/proc` says 177 s of uptime, so the other
>   timer flows turn the settle off in their machine layer (`LogsFlows.SettleOff`).
> - **`timer.runLimitMinutes` 240 → 276**, by exactly the waits, so D8's archive slack is unchanged.
> - **Open tail:** the defaults are provisional. After a week of `settled` records, revisit them with figures in `research/`.
>   `timer.busyCheckSeconds` is `SafeDirection.None` (machine-only), as planned.
>
> Related docs: [module_daemon.md](module_daemon.md), [architecture.md](architecture.md),
> [2026-10-08_windows_time_stopped.md](2026-10-08_windows_time_stopped.md) (the 2026-10-10 boot),
> [PLAN_twenty_sessions_all_day.md](../todo/PLAN_twenty_sessions_all_day.md) § S6 (the "machine busy" signal reused here).

## 1. Symptom

**What happened on 2026-10-10.** The WSL VM came up at 07:43Z, started by Docker Desktop's WSL integration. `wsl-care.timer`'s
catch-up fired at once, and `wsl-care.service` — the full collect, with its folder walks and Docker disk figures — ran
07:44–07:48Z. The journal says "Consumed 1min 55s CPU". That was exactly while Docker started 12 containers and the vhdx read
about 12 GiB in 5 minutes, so the run competed with the boot for the disk and the CPU.

**Why it fires at once.** The timer is a CALENDAR timer with `Persistent=true` (`src_daemon/systemd/wsl-care.timer:19-23`).
systemd fires a missed slot as soon as the timer is activated at boot, delayed only by `RandomizedDelaySec` (5 min at most, by
default). Nothing makes the run wait for the boot to finish.

**The same boot's clock.** The guest came up with Windows' 2 h-ahead time, and timesyncd stepped it back. The journal stamps
the same run *Starting* 11:44:02+02:00 and *Finished* 09:48:49+02:00, as recorded in the Windows Time research file.

## 2. Goal

A timer run that starts within `timer.bootDelayMinutes` of boot first waits until the machine has been up that long. Then,
at any time and not only after a boot, it waits while S6's busy signal says the machine is busy, for at most
`timer.busyWaitMinutes`. After that it runs anyway.

Constraints:
- The run never skips its work because of the wait. A run that never ran would be worse than a slow one.
- The wait holds no lock and writes no running state, so `status`, the watch timer and the buttons are not blocked.
- Every number is a key. `0` turns each wait off.

## 3. Design

1. **Keep the timer as it is** (`Persistent=true`, `OnCalendar`). systemd keeps deciding WHETHER a slot was missed; it fires
   once, not once per missed slot. Only the service waits. The alternative — `OnBootSec=` with `Persistent=false` — was
   rejected: it would run after EVERY boot, even ten minutes after the last run, and it would lose the "only if missed" rule.
2. **The settle step, `collect --timer` only, before the run lock is taken** (`CollectCommand.cs:46` takes the lock wait).
   1. **Boot settle.** Uptime comes from `/proc/uptime`, through `HealthParsers.Uptime` (`HealthParsers.cs:84`). While it is
      below `timer.bootDelayMinutes`, the step sleeps until it is not. An unreadable uptime means no wait, and the record
      says so.
   2. **Busy settle.** `MachineBusy.Judge` (`Thresholds/MachineBusy.cs:51`) answers busy or not. While it says busy, the
      step sleeps for `timer.busyCheckSeconds`, then asks again, for at most `timer.busyWaitMinutes` in total. An unread
      signal ("cannot say") means go, as S6 defines it.
   3. Each sleep is the host's injectable wait (`CliHost.Wait`), so tests advance a clock rather than sleep.
   4. A run started by hand (`collect` without `--timer`) or by `act` never waits.
3. **The record.** The run detail gains `settled {bootWaitSeconds, busyWaitSeconds, busyAtEnd, reason}`. The field is
   additive; the goldens are regenerated.
4. **The budget.** `TimeoutStartSec` (`timer.runLimitMinutes`) must cover the waits as well as the work.
   - `RunBudget.TimerRunWorstCase` (`RunBudget.cs:27`) adds `timer.bootDelayMinutes + timer.busyWaitMinutes + timer.busyCheckSeconds`.
   - The `NumberRules` rule that holds `timer.runLimitMinutes` at or above the worst case (`NumberRules.cs:86-88`) keeps
     holding: a layer that lengthens a wait without lengthening the limit is refused by name.
   - The installer's drop-in already writes `TimeoutStartSec` from the key (`UnitDropIns.cs:59`).
5. **Keys** (`ConfigKeys.Timer`):
   | key | range | default | trust |
   |---|---|---|---|
   | `timer.bootDelayMinutes` | 0–120 | **15** | Higher — waiting longer is the safe direction |
   | `timer.busyWaitMinutes` | 0–120 | **20** | Higher |
   | `timer.busyCheckSeconds` | 10–600 | **60** | Display |

   Each gets a row in `contracts/config-keys.json`.

   **The default `timer.runLimitMinutes` rises by 36 min** so the rule holds with the defaults: 15 + 20 + 1. The exact figure
   comes from `TimerRunWorstCase` and is stated in the code round.

   **Where the defaults come from — measured 2026-10-10, read-only** (coai plan round `aebbaafd`, finding accepted).
   - The source is the boot's journal on the MONOTONIC clock (`journalctl -b -o short-monotonic`), because the wall clock was
     stepped. timesyncd's *Clock change detected* came at 91 s after boot.
   - `wsl-care.service` finished at 296 s, which matches 07:44–07:48Z.
   - By the coordinator's Windows-side counters, the boot's heavy phase lasted about 5 minutes: Docker Desktop starting
     12 containers in its own VM, and about 12 GiB of vhdx reads. Docker is not in this distro's journal.
   - So `timer.bootDelayMinutes` = 15 is about three times the one measured busy boot.
   - `timer.busyWaitMinutes` = 20 has NO measurement behind it: PSI keeps no history, so how long a busy spell lasts is not
     known yet.
   - **Both defaults are provisional.** The `settled` record measures every timer run — how long it waited, and whether it was
     still busy at the end. After a week of records the defaults are revisited, with the figures in `research/` (an owner
     question at that point, not now). A record that ends `busyAtEnd: true` is the evidence that 20 is too short.

## 4. Never

- Never skip the run's work because of a wait.
- Never hold the run lock, or write `running.json`, while waiting.
- Never wait on a manual run or an `act`.
- Never wait past `timer.busyWaitMinutes`.
- Never read an unreadable signal as "busy".

## 5. Tests (RED first)

- `A_timer_run_within_the_boot_delay_waits_until_the_machine_has_been_up_that_long`
- `A_timer_run_after_the_boot_delay_does_not_wait`
- `A_busy_machine_holds_the_run_in_steps_up_to_the_busy_wait_then_it_runs_anyway`
- `An_unread_busy_signal_or_uptime_is_no_wait`
- `A_manual_collect_never_waits`
- `The_wait_holds_no_lock_and_writes_no_running_state`
- `The_record_says_how_long_it_waited_and_why`
- `A_run_limit_below_the_worst_case_with_the_waits_is_refused_by_name` (NumberRules)

Each waiting test runs on an injected clock and an injected wait, never a real sleep. A scenario flow over the built CLI uses a
fixture `/proc/uptime` and pressure files.

## 6. Build order

1. Keys and the `NumberRules` / `RunBudget` change, with their tests.
2. The settle step and its record.
3. Wire it into `collect --timer`.
4. Goldens and contracts.
5. Docs: `module_daemon.md`, `architecture.md`, `module_tests.md`.

## 7. Definition of Done

- [ ] Every test above was seen RED for the right reason, then green. Break-it on product code turns them red again: the wait
      taken after the lock; the busy bound ignored.
- [ ] Windows suites green locally; CI's Linux legs green.
- [ ] `default.json`, `contracts/config-keys.json` and the goldens regenerated.
- [ ] Docs updated.
- [ ] After a release and the owner's install: the first boot's journal shows the run starting at or after boot + 15 min.
