# PLAN — the Windows Time task: restart `w32time` by itself when software stops it

> Status: **IMPLEMENTED, 2026-10-08.** Scope as shipped: the extension's Windows Time guard — `src_vs_code/src/windowsTime/guard*.ts`,
> two commands, six settings, the *Health* section's line and buttons, the tripwire's new refusals; nothing in the daemon.
> Extracted from story 2 (D9) of [PLAN_windows_time_guard.md](PLAN_windows_time_guard.md), whose story 1 shipped.
> **Deviations from the plan as extracted** (each in D8 with its source): the 7036 trigger is replaced by the
> Time-Service's own event 258 (7036 is not logged on this build — measured, research §1); the action is plain
> `-Command` text, not `-EncodedCommand`; the install creates `\wsl-care` through `Schedule.Service` before
> `Register-ScheduledTask`; the task carries a read ACE for Authenticated Users (without it the panel could not see it —
> measured, research T21); the elevated run is persisted in `globalState` and held past a timeout; "this version" is the
> whole canonical summary of the registered definition, not its arguments. **Open tail:** registering, running as SYSTEM
> and the event trigger firing are not exercised by any test — they are the owner's one-time check after installing it
> (`POST_DEPLOY.md` item 9); the owner questions of §7 are open — Q1 and Q4 answered the same day, and Q4's fifth trigger
> (SCM 7040 for `W32Time`) shipped as §11 records.
>
> Related: [2026-10-08_windows_time_stopped.md](2026-10-08_windows_time_stopped.md) (the incident),
> [2026-10-08_windows_time_guard_trigger.md](2026-10-08_windows_time_guard_trigger.md) (build step 1's
> measurement — it changed the event trigger), [PLAN_windows_care.md](../todo/PLAN_windows_care.md) §2 *Admin work* and §8a (the
> generic elevated channel — NOT this), [module_vs_code.md](module_vs_code.md) (*Start Windows Time*, the
> one-click fix this automates).
>
> **Owner decisions, 2026-10-08:** build the task now; `www.microsoft.com` stays the daemon's reference; a wrong Windows
> clock making `doctor` unhealthy is accepted; the two switches stay separate (`clock.manualStartWarns` in the daemon,
> `wslCare.windowsTime.setAutomaticStart` in the extension, default ON = the action sets StartType Automatic).

## 1. Symptom

On 2026-10-08 the Windows Time service `w32time` was found Stopped (StartType Manual) and Windows 7 200 s slow; the owner
says AMD driver software stops it. Story 1 of the guard detects it (`clock.timeService`, `clock.reference`) and gives a
one-click fix (*Start Windows Time*, which also sets StartType Automatic). But Automatic does NOT restart a service that
other software stops, so the fault returns between full runs and needs a person each time.

Build step 1's measurement added two facts (research record §1–§3): the Service Control Manager writes **no event 7036 at
all** on this build (0 in three days, while `w32time` stopped three times), and Windows comes up **2 h slow after a boot**
(twice on 2026-10-08, also 10-05 and 09-29) — corrected each time only seconds after a START of `w32time`; a service that
had run since boot, and Windows' own boot sync task, left it wrong.

## 2. Goal — what is true when done

1. A person can install, from the extension, ONE scheduled task `\wsl-care\windows-time-guard` that (re)starts `w32time`
   and resyncs with no person in the loop — after seeing exactly what will be registered, through ONE UAC prompt.
2. The task can do nothing else: its action is fixed in its own definition, runs as SYSTEM, and only sets the start type
   (per the setting), starts `w32time` and resyncs, with a rate limit so it never loops against software that stops the
   service again.
3. A person can remove it the same way, and removal leaves nothing of it behind.
4. The panel shows, after any reload, whether the guard is installed, whether it is THIS version's definition, and its
   last run's result — read from Task Scheduler, never from a flag of the window.
5. Every number is a setting; no test registers a task, starts a service or raises UAC.

## 3. Design

### D1 — the task definition is ONE pure function of the settings (`windowsTime/windowsTimeGuard.ts`)

`guardTaskXml(options)` returns the XML Task Scheduler registers. Measured with Task Scheduler's own parser
(`Schedule.Service.NewTask(0).XmlText = <xml>` — parsed in memory, never registered, unelevated): a draft of exactly this
shape parses and reads back principal `SYSTEM` / logon type 5 (service account) / run level highest, four triggers, the
action and the settings; an unknown element is refused `0x80041316`.

- **Principal** `S-1-5-18` (SYSTEM), `HighestAvailable`. Actions context `System`.
- **Triggers** (four):
  - `BootTrigger`, `Delay` = `delaySeconds` — the boot clock is the measured 2 h fault (§1);
  - `LogonTrigger` (any user), same delay;
  - `TimeTrigger` from a fixed past `StartBoundary` with `Repetition/Interval` = `everyHours`, no duration (indefinite);
    `StartWhenAvailable`, so a run missed while asleep runs at wake;
  - `EventTrigger` on **`Microsoft-Windows-Time-Service/Operational`, provider `Microsoft-Windows-Time-Service`, event
    258** (*"W32time service is stopping"*), same delay (the service is still stopping when 258 is written).
    **Deviation from the plan as extracted:** the 7036 trigger is DROPPED — measured absent on this build (research §1);
    258 is matched by provider and number only, so no localised text is involved (research §2, T8: the exact
    `QueryList` matched 25 of 25 recorded stops). 258 misses a killed service host and a disabled channel; the periodic
    trigger covers both.
- **Settings:** `MultipleInstancesPolicy IgnoreNew` (one run at a time), runs on battery, `ExecutionTimeLimit` =
  `timeLimitMinutes`, enabled.
- **Action** — `Exec`: `%SystemRoot%\System32\WindowsPowerShell\v1.0\powershell.exe` (expanded by Task Scheduler in the
  SYSTEM context, as Windows' own tasks write `%windir%`), arguments `-NoProfile -NonInteractive -EncodedCommand <base64
  of guardScript(options)>`. No script file exists, so there is nothing a user could edit; changing the action needs an
  administrator, as changing any SYSTEM task does.

### D2 — the guard's script reuses *Start Windows Time*'s lines and adds the rate limit

`guardScript(options)` is story 1's script with one block inserted BEFORE the start — the same `SET_AUTOMATIC` line while
`setAutomaticStart` is on and the same `BODY` (`Start-Service` → exit 11, `w32tm /resync /force` three tries → exit 12),
exported from `windowsTimeFix.ts` rather than copied (`common.reuse-first`, widening move 2). Inserted:

```powershell
$key = 'HKLM:\SOFTWARE\wsl-care\windows-time-guard'
function Test-WslCareStartAllowed([long]$Now, [long]$Last, [long]$Window) { $since = $Now - $Last; -not (($since -ge 0) -and ($since -lt $Window)) }
$service = Get-Service -Name w32time -ErrorAction SilentlyContinue
if ($null -eq $service) { exit 11 }
if ($service.Status -ne 'Running') {
  $now = [DateTime]::UtcNow.Ticks
  $last = [long]0; try { $last = [long](Get-ItemPropertyValue -Path $key -Name lastStartUtcTicks -ErrorAction Stop) } catch { $last = [long]0 }
  if (-not (Test-WslCareStartAllowed $now $last <minMinutesBetweenStarts in ticks>)) { exit 20 }
  try { if (-not (Test-Path -Path $key)) { New-Item -Path $key -Force -ErrorAction Stop | Out-Null }; Set-ItemProperty -Path $key -Name lastStartUtcTicks -Value ([string]$now) -ErrorAction Stop } catch { exit 21 }
}
```

- **The rate limit is on STARTS** (the loop the plan feared: software stops it → 258 → the guard starts it → …). A running
  service is only resynced, which no trigger reacts to. The stamp is written BEFORE the start, so a failing start is also
  not retried inside the window — the periodic run retries after it.
- **The stamp lives in `HKLM\SOFTWARE`**, which only administrators and SYSTEM may write (users read). A file was rejected
  for the same reason story 1 rejected a result file: a SYSTEM write into a user-writable folder follows a link the user
  can plant. A stamp in the future (the clock was stepped back) counts as elapsed, never as a lock.
- Exit codes are one closed set shared with story 1: 0 done, 10 set-Automatic failed, 11 start failed, 12 resync failed,
  **20 rate-limited**, **21 stamp unwritable**. The panel reads them back as the task's last result (D5).

### D3 — install and remove: ONE elevated PowerShell each, through story 1's launcher

Story 1's `outerScript(powerShell, inner)` (`Process.Start` with `runas`, 1223 = declined, 13 = launch failed) is reused
as it is; the request builder's path check is extracted from `fixRequest` into one `elevatedRequest(env, inner,
timeoutMs)` both features call.

- **Install inner script:** `$env:PSModulePath = "$PSHOME\Modules"`, the XML in a single-quoted here-string (nothing in it
  is expanded; a test asserts no line of the XML starts with `'@`), then `Register-ScheduledTask -TaskPath '\wsl-care\'
  -TaskName 'windows-time-guard' -Xml $xml -Force` → exit 30 on failure. `-Force` replaces an older definition, so
  install is also *update*. No file is written or read: the XML the person was shown travels inside the encoded command,
  so nothing can be swapped between showing and registering.
- **Remove inner script:** through `Schedule.Service` — delete the task (absent = already removed, not a failure; any
  other error exit 31), delete the folder `\wsl-care\` only when it holds no other task or folder, delete
  `HKLM:\SOFTWARE\wsl-care\windows-time-guard`, then `HKLM:\SOFTWARE\wsl-care` only when empty (exit 32 on a cleanup
  failure). The service's start type is NOT reverted: it was the person's fix, made by story 1 or by the guard while
  the setting was on, and the modal says so.
- **Length bound:** the elevated command line carries the whole definition, nested base64 included. Measured: an
  unelevated `ShellExecute` carried 32 583 argument characters intact; `CreateProcess` caps the whole line at 32 767; the
  elevated path was not measured (it raises UAC). One named constant, `ELEVATED_COMMAND_LINE_MAX = 16 000` characters, is
  the ceiling the request is held under (a test computes the real length for the largest settings).

### D4 — the flows: show the exact text first, then one run, then read the truth back

- **Install the Windows Time guard** (`wslCare.installWindowsTimeGuard`): build the request (or say why none can be) →
  open the ELEVATED SCRIPT — the XML inside it — in a read-only editor tab (a `TextDocumentContentProvider` under its own
  scheme; nothing the person types there can reach the request) → a modal that names the triggers, the action's script
  verbatim, the rate limit and that one UAC prompt follows → only its confirm runs the request → a closed outcome
  (`done` · `declined` · `failed` with the code's sentence · `timedOut` · `notStarted`) → the status is read again.
- **Remove the Windows Time guard** (`wslCare.removeWindowsTimeGuard`): the remove script in the modal (short enough),
  confirm, one run, the same outcomes, the status read again.
- Both are palette commands and buttons on the panel's *Health* section, beside the guard's state line. One flow at a
  time per window (a second click while one runs starts nothing).
- **Test mode:** a recorder replaces the shown document, the modal, the runner and the status reader, as story 1's does.

### D5 — the status: a read-only query, durable by construction

`guardQuery` — an UNELEVATED fixed PowerShell through the real runner: `Schedule.Service` → `GetFolder('\wsl-care')
.GetTask('windows-time-guard')`; `0x80070002` = not installed (measured for both a missing folder and a missing task);
another HRESULT = unreadable with that code. When present it prints tagged lines — `enabled`, `lastRunUtc` (ISO, or
`never` for Task Scheduler's 1899 zero date), `lastResult`, and the registered `Arguments` — and the extension compares
those arguments with what the CURRENT settings would install:

| state | line on the panel | buttons |
|---|---|---|
| not installed | *Windows Time guard: not installed* | Install |
| installed, same action | *installed — last run <local time>: <outcome sentence of the exit code>* (or *not run yet*) | Remove |
| installed, different action | *installed, but not as the current settings would install it (another version or setting) — install again to update* | Install, Remove |
| disabled in Task Scheduler | *installed but disabled in Task Scheduler* | Install, Remove |
| unreadable / not Windows | *unknown — <reason>* | none |

It is asked when the panel opens or is refreshed and after an install/remove finishes — never on the status poll — with a
ceiling (`timeouts.windowsTimeGuardQuerySeconds`) and one query in flight. The truth is Task Scheduler's; the window
holds only the in-flight label (*Installing the Windows Time guard…*), so a reload re-reads it (`common.durable-status`).
An elevated install outlives a reload of the window (it is the UAC broker's child); the next query shows its result.

### D6 — every number a setting (application scope, in `settings/numbers.ts`, held equal to `package.json`)

| key | default | range | used for |
|---|---|---|---|
| `wslCare.windowsTime.guard.everyHours` | 4 | 1–168 | the periodic trigger |
| `wslCare.windowsTime.guard.minMinutesBetweenStarts` | 10 | 1–1440 | the rate limit |
| `wslCare.windowsTime.guard.delaySeconds` | 60 | 0–3600 | the boot, logon and event triggers' delay |
| `wslCare.windowsTime.guard.timeLimitMinutes` | 5 | 1–60 | the task's `ExecutionTimeLimit` |
| `wslCare.timeouts.windowsTimeGuardSeconds` | 180 | 30–3600 | an install / remove (it waits for the person at UAC) |
| `wslCare.timeouts.windowsTimeGuardQuerySeconds` | 30 | 5–300 | the status query |

The task bakes the first four in at install; changing one later makes the status say *install again to update* (D5).
The resync's three tries and two seconds stay story 1's constants, shared by both features.

### D7 — the test tripwire grows, and is never weakened

`noRealWsl.ts` additionally refuses, in every test process: `schtasks` / `schtasks.exe`, and a PowerShell whose
arguments name `ScheduledTask` or `Schedule.Service`. The parse checks (PowerShell's parser for the scripts, Task
Scheduler's in-memory `NewTask(0).XmlText` for the XML) pass their text in FILES, and each check's script is itself
asserted to contain no `Register`, `Delete`, `Start-Service`, `Set-Service` or `RunAs`. Break-it checks mutate PRODUCT
code only; the tripwire, the recorders and the fakes are never disabled to prove anything.

### D8 — plan round 1 (amendments; they SUPERSEDE D1–D7 where the two differ)

coai session `c04e5adc`, `review_plan`: **both reviewers answered** (codex, gemini), verdict `good_enough` (rounds
exhausted, 7 gating against a threshold of 6), 7 findings — 6 accepted, 1 rejected; and an own plan review (Opus, 12
findings — 11 accepted, 1 rejected). Every rejection measured, research record §5.

| # | Source | Finding | Decision → change |
|---|---|---|---|
| g0 | gemini, Blocking | the `\wsl-care` folder may not exist when registering | accepted → install goes through `Schedule.Service`: `GetFolder('\wsl-care')`, else `CreateFolder('wsl-care', <sddl>)`, then `RegisterTask(name, xml, 6 create-or-update, $null, $null, 5 service account, <sddl>)` — one API for install, remove and the query |
| g1, c5, o2 | gemini, codex, own | comparing only `Arguments` misses `everyHours` / `delaySeconds` / `timeLimitMinutes` and an administrator's edits | accepted → the query prints a canonical SUMMARY of the registered definition (principal id / logon type / run level; per trigger type, enabled, delay, interval, subscription; time limit, instances policy; action path and arguments; the descriptor); `guardSummary(options)` computes the expected one in TS; equal → *current*. The Windows-leg test runs the query's summary code over Task Scheduler's in-memory parse of `guardTaskXml(options)` and asserts it equals `guardSummary(options)` — so the two cannot drift |
| g2 | gemini | remove fails when the stamp key was never created | accepted → each removal step is "if present" |
| g3 | gemini | the Operational channel is disabled by default | rejected — measured enabled here (685 records since 2026-08-31); enabling it is a Windows setting outside the owner's action list; the periodic trigger covers a disabled channel. The query reports `channel=enabled\|disabled` and the panel line says *the stop event is not logged here — only the periodic run* when disabled |
| c4, o11 | codex, own | a timed-out elevated run can overlap a retry; a reload offers Install again while UAC is open | accepted → the flow persists `{ op, startedAtUtc, deadlineUtc }` in `globalState` BEFORE the launch; while it stands (until the flow's own answer, or `deadline = start + 2 × windowsTimeGuardSeconds` after a timeout or a reload) both buttons are disabled with *Waiting for the elevated PowerShell (install)…*; a reload reads it back; a past deadline is swept at activation |
| c6 | codex | release checks never run the task for real | accepted → `POST_DEPLOY.md`'s Windows Time item gains a manual, owner-run step: install, *Run* the task once in Task Scheduler, read its last result 0 and `w32time` Running, read the panel's line — and Defender's ASR events 1121/1122 none (o6) |
| o1 | own, Blocking | a running service is only resynced, and the boot fault happened while it ran | rejected — T18: every correction followed an EXPLICIT resync (event 266 reason 0) 40 ms before the step, while two reason-2 notifications did not correct; T19: `MaxPos/NegPhaseCorrection` 54 000 s admit the 7 200 s step. The owner fixed the action list (start type, start, resync); a restart is not in it. Recorded as a residual: resync on a long-running service is inferred, not observed — the post-deploy boot check observes it |
| o3 | own | an elevated registration's default descriptor may hide the task from the unelevated reader | accepted, and MEASURED (T21): SYSTEM tasks under the default root descriptor are not even listed to an unelevated reader — the panel would say *not installed*. → `RegistrationInfo/SecurityDescriptor` `D:(A;;FA;;;SY)(A;;FA;;;BA)(A;;FR;;;AU)` in the XML; `0x80070005` has its own row (*installed but not readable from this account*). **As built:** the descriptor travels in the XML only — the folder keeps its inherited descriptor, which the unelevated reader can open (T21), and `Register-ScheduledTask -Xml` registers the definition's own descriptor (code round k5, gemini/codex g9) |
| o4 | own | the tripwire matches nouns in plain text, blind to `-EncodedCommand`, and would refuse the plan's own in-memory parse | accepted → the tripwire DECODES `-EncodedCommand` / `-enc` / `-ec` / `-e` before matching, and matches MUTATING words — `Register-ScheduledTask`, `Unregister-ScheduledTask`, `RegisterTask`, `RegisterTaskDefinition`, `DeleteTask`, `DeleteFolder`, `CreateFolder`, `Start-Service`, `Stop-Service`, `Restart-Service`, `Set-Service`, `Set-ItemProperty`, `New-Item`, `Remove-Item`, `w32tm`, and `schtasks` as a program — so `NewTask(0)` reads and the query stay allowed; tests with encoded input |
| o5 | own | the panel has no place for a non-daemon line | accepted → `PanelView.windowsTimeGuard: { line, level, buttons: ('installWindowsTimeGuard' \| 'removeWindowsTimeGuard')[], busy }`, built by a pure `guardView(state, pending)`; the page renders it as an extra of the *Health* section (beside the cleanup extras); two new BARE messages; the host holds the guard state (not the daemon `OutcomeStore`) |
| o6, o8 | own | `-EncodedCommand` in a SYSTEM task reads as an obfuscation indicator, and nests a second base64 | accepted → the task's action is `-Command <the script as ONE line>`: no double quote, no run of two spaces, no newline, so `CommandLineToArgvW` + PowerShell's join return it byte for byte (a test simulates that round trip; the Windows leg parses it). `&` is XML-escaped in the XML. The install launch keeps story 1's `-EncodedCommand` (one layer only) |
| o7 | own | the shown document must be exactly what is registered | accepted → the document is the inner install script byte for byte (a test decodes the request and compares); one URI per flow, served from a snapshot, never re-rendered |
| o9 | own | story 1's sentences name *the Windows Time fix*; exit 11 overloaded; Task Scheduler's own result codes | accepted → the sentences take the feature's name and timeout key; 22 = *the Windows Time service does not exist*; the last-result reader also knows `0x41301` running, `0x41303` not run yet, `0x41306` stopped by its time limit, `0x8004131F` an instance already running; anything else *exit N* |
| o10 | own | the wall-clock rate limit across clock steps; the order of `SET_AUTOMATIC` | accepted → order fixed (start type FIRST, so a Disabled service is set Automatic even when the start is rate-limited); tests for a stamp 2 h ahead (elapsed) and 2 h behind (elapsed), at the window's edge; the residual stated: a +2 h step by the guard's own resync shortens the window once |
| o12 | own | the evening stops might be Windows' own stop trigger | accepted, measured (T20): `w32time` has two start triggers and no stop trigger |
| o8b | own | a payload PowerShell cannot read exits 1 | accepted → exit 1 of the elevated install/remove reads *the elevated PowerShell failed before its first step (exit 1)* |

## 4. Growth and interruption

- **Task Scheduler:** one task, one folder; its history is Windows' own (the Task Scheduler operational log, off by
  default). Removed by *Remove*.
- **Registry:** one key, one string value (the stamp, 18 characters), overwritten per start — never grows. Removed by
  *Remove*.
- **The extension:** stores nothing new; the shown document is in memory.
- **Interrupted:** a killed install/remove is the runner's ceiling + tree kill of the UNELEVATED launcher (the elevated
  child, the UAC broker's, finishes on its own); no in-flight state is persisted, so there is nothing to sweep — the next
  query reads what Task Scheduler holds. A guard run killed by its `ExecutionTimeLimit` leaves at most a stamp, which only
  delays the next start by the window.

## 5. Build order

1. ✅ Measure the trigger (read-only) — [2026-10-08_windows_time_guard_trigger.md](2026-10-08_windows_time_guard_trigger.md).
2. ✅ RED tests, then `guardTask.ts` / `guardScripts.ts` (D1–D3): the XML parsed by Task Scheduler's own parser on the Windows leg (four
   triggers, SYSTEM, the action decodes to the guard script); the guard script parsed by PowerShell's parser and its
   commands in order; the rate-limit function EXTRACTED from the script's AST and executed alone over its edge cases
   (nothing else of the script runs); the install/remove scripts parsed; the length bound; exit-code sentences.
3. ✅ RED tests, then the flows (D4) and the query (D5) with recorders — order (document → modal → run → query), a decline
   runs nothing, every exit one outcome, the status table's every row, the tripwire refusing `schtasks` and
   `Schedule.Service`.
4. ✅ Wiring: commands, settings, the panel's *Health* line and buttons, the closed message set, `package.json`, the flow
   catalogue rows (`module_tests.md`).
5. ✅ Docs: `module_vs_code.md`, `module_tests.md`, a pointer in `architecture.md` (near its cap), `README.md`,
   `POST_DEPLOY.md` (at its cap of 12: the Windows Time item is extended to read the task back, not a 13th), this plan
   promoted, the neighbouring plans' boundary lines.

## 6. Test plan

- **Unit (node, every OS):** the XML's shape over the constant (elements, counts, the single subscription, the decoded
  action equals `guardScript(options)`); each setting reaches its element; the rate-limit block sits before
  `Start-Service`; `SET_AUTOMATIC` present only while the setting is on; the install script carries the XML verbatim and
  no line starts `'@`; the request is ONE absolute `powershell.exe -Command <outer>` whose `-EncodedCommand` decodes to
  the install script; the length bound; every exit → one outcome and sentence; the status parser over each tagged answer
  (absent, unreadable, enabled/disabled, never run, same/different arguments); the flows' order with recorders; the
  panel line and buttons per state; the bare messages; the tripwire's new refusals.
- **Windows leg only (parsed, never executed):** PowerShell's parser over the guard, install, remove and query scripts
  (zero errors, the expected command names); Task Scheduler's parser over the XML (`NewTask(0).XmlText`, nothing
  registered) reading back the principal, the triggers' types/delays/interval/subscription and the action; the
  rate-limit function, extracted from the AST, run alone: never started, inside the window, at its edge, past it, a
  stamp in the future.
- **Not exercised by any test:** registering, running or removing the task, and the elevated launch — the owner's live
  check after release (`POST_DEPLOY.md`).
- **Whole suite:** `npm test` (and `npm run lint`, `tsc`).

## 7. Owner questions

- **Q1** — defaults `everyHours` 4, `minMinutesBetweenStarts` 10, `delaySeconds` 60: keep? — **answered: keep** (owner,
  2026-10-08).
- **Q2** — the measured 2 h after every boot (research §3) makes the boot/logon runs the guard's main job. Worth also
  investigating the firmware clock (the incident record's Q5), so the guard is a safety net rather than the fix?
- **Q3** — the two *demand → disabled → demand* changes of `w32time`'s start type at 06:00Z and 09:50Z (research T13)
  were made from your account: were both yours (a tool you ran), or is that the software that stops it?
- **Q4** — a fifth trigger on event 7040 (start type changed, keyed by the service name `W32Time`, which is not
  localised — measured matchable) would undo a *disabled* within a minute. Not built (the owner listed four); want it? —
  **answered: yes**, built (§11).

## 8. Definition of Done

- [x] Plan round (coai `review_plan`) — `good_enough`, findings resolved (D8).
- [x] The trigger measured and recorded in `research/` — 7036 absent; 258 matches.
- [x] Install / remove / status shipped with tests; no test registers a task, starts a service or raises UAC (§9).
- [x] Break-it checks recorded, each mutating product code only (§9).
- [x] Docs and `POST_DEPLOY.md` updated; the plan promoted.
- [ ] Code round (coai `review_code`) proceeded; the pull request merged.

## 9. As built — the tests and the break-it checks (2026-10-08)

*Counts as of the first commit; the code round below adds to them.*

- **Suites:** `npm test` 830 tests, 829 pass, 1 skipped (the pre-existing non-Windows skip), 0 fail; `npm run lint` and a
  clean `tsc` green. New: `windowsTimeGuardTask.test.ts`, `windowsTimeGuardState.test.ts`, `windowsTimeGuardFlow.test.ts`,
  `xmlTree.test.ts` (the strict XML reader's own tests), three page tests in `panelPage.test.ts`, three tripwire tests in
  `noRealWsl.test.ts`.
- **The Windows leg** reads, never runs: PowerShell's parser over the action, install, remove and query scripts (no
  error; the commands each one runs, by name); Task Scheduler's parser in memory (`NewTask(0).XmlText`, nothing registered)
  over three option sets, whose read-back through the query's own summary code equals `guardSummary(options)` line for
  line, and whose descriptor reads back as written; the rate limit's one function extracted from the action's syntax tree
  and run alone over seven cases (BigInt ticks — a real tick count is past `Number`'s exact integers, which is what made the
  first draft of that test pass a one-tick case for the wrong reason).
- **Break-it, product code only** (the tripwire, the recorders and the fakes untouched): seventeen mutations, each run, each
  RED with the test named for it — the start type moved after the rate limit; the trigger back on 7036; the principal
  LocalService; the window's edge `-lt` → `-le`; the read ACE dropped (first STILL GREEN — the test compared the constant
  with itself; a test of the descriptor's ACEs was added and went RED); the summary forgetting the delay; the install
  skipping the folder; the removal without `Test-Path`; a pending run not holding the buttons; a negative HRESULT read
  signed; a changed setting not reading as "install again"; a timeout clearing the pending run; the modal skipped; the
  document not shown; the guard messages taking a payload; the page dropping the line; the host querying twice at once.
  Restored: green.

## 10. Code round (2026-10-08)

coai session `c04e5adc`, `review_code` over `28c123e`: **all 8 reviewers answered** (codex and gemini, four roles each),
verdict `proceed`, 12 findings — 5 accepted, 7 rejected with reasons; and an own code review (Opus, feature-dev
code-reviewer, 7 findings — 5 accepted, 1 rejected, 1 a record correction).

| # | Source | Finding | Decision → change |
|---|---|---|---|
| k2 | own, Major | `Join-Path` in the first line is looked up before the module path is pinned — a user-writable module folder is searched first, inside an elevated or SYSTEM PowerShell | accepted → `MODULES = $env:PSModulePath = $PSHOME + '\Modules'`: no command; every guard script starts with it (test); the parser's command lists no longer begin with `Join-Path` |
| k3 | own, Minor | the busy check runs only before the modal — two windows' modals can both launch | accepted → re-read right before the pending record is written; `busy`, nothing run |
| k4 | own, Minor | a refresh during a query in flight joined a query that predates the caller's change | accepted → one MORE query after the flight, never more than one |
| c3 | gemini, Major | a reload holds both buttons until the deadline although Task Scheduler already shows the end | accepted → a query that shows the pending run's end (installed as these settings / gone) clears it |
| c1, c5 | gemini | a changed guard setting does not re-derive the line | accepted → `onDidChangeConfiguration('wslCare.windowsTime')` re-derives it at once |
| c11 | codex, Minor | a refresh shows the previous line as current | accepted → *(checking again…)* while asked; the first read is *checking…* |
| c7, k7 | codex, own | an `as Snapshot` cast in a fixture | accepted → typed literal |
| k6 | own, Minor | the tripwire judged only the program handed to it: a quoted path, `cmd /c`, `shell: true`, and words like `New-ItemProperty`, `sc stop`, `net start` got through | accepted → the tripwire STRENGTHENED (never weakened): quotes stripped, `cmd /c` and `shell: true` judged by the program they run, the word list widened; tests for each |
| k1 | own, Major | the "not installed" check may read a PowerShell wrapper's HResult, not the COM error | rejected — measured in Windows PowerShell 5.1 (T23): `FileNotFoundException`, `0x80070002`; and the Windows-leg test now RUNS the real, read-only query unelevated and asserts a closed answer (`guard=absent` here) |
| k5 | own, Minor | the folder's descriptor did not match the plan's text | record corrected (o3 above, research §5) — the code was right by T21 |
| c0 | gemini, Major | the guard line vanishes when the daemon is down | rejected — `viewModel.ts` `sections()` maps every section whatever the status; the health section always exists |
| c4 | gemini, Major | opening the panel never queries | rejected — `resolveWebviewView` and visibility call `refresh`, which `extension.ts` wires to `guard.refresh()` first |
| c2 | gemini, Minor | the runner inside the UI seam | rejected — story 1's shape: the progress wraps the run, and Test mode replaces it at the same seam |
| c6 | codex, Blocking | the recorder mutates its arrays | rejected — the Test-mode recorders are append-only logs held through the test API (the house pattern of `installUi.ts`, `windowsTimeUi.ts`, `cleanRecorder.ts`) |
| c8 | codex, Minor | `DurableStore` imported from the cleanup module | rejected here — `logsPage/logsController.ts` already does the same; moving it is a neighbouring refactor, proposed in the pull request |
| c9 | codex, Major | the new folder lacks read permission | rejected — T21: the unelevated reader opens a folder under the same inherited descriptor; only tasks hide, which the task's ACE fixes |
| c10 | codex, Major | a UAC prompt answered after the deadline can overlap a later run | rejected as a residual: the late answer runs exactly what its prompt showed; install converges, removal is "if present"; the panel re-reads Task Scheduler |

**Break-it for the round's fixes (product code only):** the module path through `Join-Path`; no busy check after the
modal; a refresh in flight sharing the stale query; a finished pending run not cleared; a changed setting telling nobody;
no checking note — six mutations, each RED with its test, restored green.

## 11. As built — the fifth trigger: the start type changed (2026-10-08, later)

Owner Q4 answered *yes*, Q1 *keep the defaults*. What shipped (`guardTask.ts`, `guardState.ts`):

- **A fifth trigger**, a second `EventTrigger` after the 258 one with the same `delaySeconds`: `START_TYPE_QUERY` =
  `<QueryList><Query Id="0" Path="System"><Select Path="System">*[System[Provider[@Name='Service Control Manager'] and
  EventID=7040] and EventData[Data[@Name='param4']='W32Time']]</Select></Query></QueryList>` — measured read-only against
  the real System log: exactly the four W32Time 7040s, and `'w32time'` matches the same four (the event log compares
  case-insensitively); `param4` is the service KEY name, `param1`..`param3` are localised
  ([2026-10-08_windows_time_guard_trigger.md](2026-10-08_windows_time_guard_trigger.md) §6). The System log is always on,
  so this trigger does not depend on the channel the 258 trigger needs.
- **No self-re-trigger loop:** the action's start-type line (story 1's `SET_AUTOMATIC`, verbatim) runs only
  `if ($service.StartType -ne 'Automatic')`. The guard's own change is then at most one 7040; the run it triggers changes
  nothing. Whether the SCM logs a 7040 for an UNCHANGED type is not measured (it would mean changing a service), so the
  action does not depend on it. With `setAutomaticStart` off there is no start-type line at all and a *disabled* stays
  disabled — the owner's switch.
- **"Within a minute" is narrowed** (plan-round finding, accepted): `MultipleInstancesPolicy` stays `IgnoreNew`, so a 7040
  that lands while a guard run is already running is ignored; the next trigger (another event, the timed run, boot,
  logon) undoes it. A run takes seconds, so the window is that run's length. `Queue` was not taken: Settings' *Sync now*
  writes two 7040s and often a 258 within two seconds
  ([2026-10-08_who_stops_windows_time.md](2026-10-08_who_stops_windows_time.md) §1), which `IgnoreNew` turns into one run.
- **The summary** gains the trigger's line in definition order, so an install from before reads *installed, but not as the
  current settings would install it — install it again* through the existing comparison; no new state.
- **The disabled-channel note** now reads *"…so a stop is caught by the timed, boot and logon runs only"* — a start-type
  change is still caught.
- **The install modal** names the fifth trigger and says the start type is set Automatic only when it is not already.
- **The event trigger firing is still not exercised by any test** (that needs a registered task and a changed service):
  `POST_DEPLOY.md` item 9 now has the owner's one-time check — set Manual elevated while the service runs (a 7040 and no
  258), then read Automatic again and the task's last run after the change (plan-round finding, accepted).

Tests (RED first, observed): 6 of the 30 guard tests failed against the unchanged product with the real symptoms — four
triggers where five were expected, one event-trigger line in the summary, a four-trigger install reading as current, an
unconditional start-type line, the old channel note. Then green. New on the Windows leg (read-only): both subscriptions
are queries the event log ACCEPTS (`Get-WinEvent -FilterXml -MaxEvents 1`: a match or "no events", never "invalid"); the
existing in-memory Task Scheduler parse now reads back five triggers equal to `guardSummary`. Break-it, product code only
(no tripwire, recorder or fake touched): the fifth trigger dropped from the XML → 3 red (the parsed definition, the zero
delay, Task Scheduler's read-back); its summary line dropped → 3 red (the read-back, the previous-version state, the summary
order); the start-type condition removed → 1 red. Restored: green. Whole suite: `npm test` 843 tests, 842 pass, 1 skipped
(the pre-existing non-Windows skip), 0 fail; `npm run lint` and `tsc --noEmit` green.
