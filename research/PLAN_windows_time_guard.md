# PLAN — the Windows Time guard: see which clock is wrong, never step WSL to a wrong host, start `w32time`

> Status: **IMPLEMENTED, 2026-10-08 (story 1).** Story 2 — the SYSTEM scheduled task (D9) — is NOT built: extracted to
> [PLAN_windows_time_task.md](../todo/PLAN_windows_time_task.md). Scope as shipped: the daemon's clock probe, health
> collectors, thresholds, `doctor`, A16 and the live contract (`src_daemon/`); one extension command and panel button
> (`src_vs_code/`); docs and `POST_DEPLOY.md`. Deviations from the text below are in §12 (the code round) — the largest:
> the elevated launcher is `Process.Start` with the `runas` verb, not `Start-Process` (D7), and A16 asks the
> reference only when the clocks disagree (D6).
>
> Related: [PLAN_wsl_care_daemon.md](../todo/PLAN_wsl_care_daemon.md) (§4.5, §5 A16, §15 #10, §17 live-gate item H),
> [PLAN_windows_care.md](../todo/PLAN_windows_care.md) (§2 admin work, §8a the elevated channel),
> [architecture.md](architecture.md), [module_daemon.md](module_daemon.md) § *The Windows Time guard*,
> [module_vs_code.md](module_vs_code.md), the incident record
> [2026-10-08_windows_time_stopped.md](2026-10-08_windows_time_stopped.md).
>
> **Plan round 1 (coai session `b8084f2d`, codex + gemini, both answered, verdict `proceed`, 6 findings: 5 accepted,
> 1 rejected; one consultation, codex, closed `solved`) and an own plan review (Opus, 12 findings, all accepted) are
> folded in below; §11 lists each and what it changed. The owner decided Q1 on 2026-10-08: the action ALSO sets StartType
> Automatic, as a config switch, default ON. The coordinator added on 2026-10-08: the live contract's clock check must
> REPORT the likely cause, not only fail.**

## 1. Symptom (measured 2026-10-08, before any solution)

- The Windows Time service `w32time` was **Stopped**, StartType **Manual** (`w32tm /query /status` → "The service has
  not been started"). Windows ran **2 h slow**: the GitHub `Date` header and the WSL clock agreed, Windows' UTC was
  7 200 s behind, the time zone was right.
- Inside WSL two time-keepers fought: Hyper-V's time sync set the distro's clock to the (wrong) host time, and
  systemd-timesyncd set it forward to NTP's — about every 33 s. journald logged *"Time jumped backwards, rotating"* at
  each pull (the journal reached less than an hour; the daemon's `journal.history` warned −0.0 days), and
  systemd-resolved logged *"Clock change detected"* ≈ 661 times per 4 h — the clock-jump warning seen since the first
  baseline.
- The daemon's `clock.drift` saw **−7 200.42 s** on ONE observation (its rule needs two ≥ 5 min apart, so it did not
  warn). `POST_DEPLOY.md` item 4's live clock probe failed with 7 198 s — with a message that named no cause
  (`HealthContractTests.cs:112`: *"a skew of a minute would be a broken clock, not this probe"*).
- The owner says AMD driver software stops `w32time`, and asked: *"add a check that STARTS this service if it is
  stopped"*. After a manual start the clocks agree, but StartType is still Manual, so it will recur.
- **A defect found while reading the code:** A16 steps the WSL clock toward the host (`hwclock -s` = the Hyper-V host's
  RTC; `chronyc makestep` steps toward chrony's own sources, which inside WSL may be the host) —
  `src_daemon/src/WslCare.Core/Actions/Clock/ClockFix.cs:58-60`, `:111-112`. In this incident the host IS the wrong
  clock: A16 would have set WSL 2 h wrong. Its only gates are "timesyncd reports synchronised" and "within the limit"
  (`ClockFix.cs:183-194`); during the fight timesyncd may read unsynchronised at the instant of the preview, and then
  A16 steps. Today only `dryRun` (default `true`) stopped it.

Measured again by this task, read-only, at 08:16–08:18Z (the record will carry the full numbers): Windows and WSL agree
now (−0.03 s), `w32tm /stripchart` against time.windows.com reads −0.017 s, but `w32tm /query /status` still says
*Leap Indicator 3 (not synchronized), Root Dispersion 7208.36 s*; `timedatectl` reports `NTPSynchronized=yes`,
timesyncd's last NTP sample −18.4 ms at its MINIMUM poll interval (32 s); the journal of this boot holds 82
"Time jumped backwards" lines under `--unit=systemd-journald` (163 under `--identifier=systemd-journald`), the last at
08:00:15Z — seven seconds before w32time's recorded sync; the boot's first timesyncd line is *"Initial clock
synchronization to … 09:46:14 CEST"* written at 07:46:13 CEST, i.e. the distro booted on the host's clock 2 h behind.
**And github.com is a poor reference:** five HEADs from Windows read its `Date` 0.9–6.5 s behind the other hosts
(the same `Date` returned twice 3 s apart — an edge cache), while www.microsoft.com and www.cloudflare.com agreed with
NTP within the header's one-second resolution.

## 2. Goal and what must be true when done

1. Every full run and `doctor` say, in a sentence that names the fix: whether `w32time` runs and how it starts, WHICH
   clock is wrong (a closed verdict: `windowsSlow` · `windowsFast` · `wslWrong` · `agree` · `unknown`), and whether the
   two time-keepers are fighting. Every threshold is a configuration key.
2. A16 never steps the distro toward the host's clock unless an independent reference shows the step brings the distro
   CLOSER to true time; while timesyncd keeps the distro on NTP, A16 skips with *"the Windows clock is wrong, not
   WSL's"*.
3. A person can fix Windows from the extension in one click: a command and a panel button that show the exact
   commands, then run them through ONE elevated PowerShell (the UAC prompt is the person's confirmation). The fix also
   sets StartType Automatic while `wslCare.windowsTime.setAutomaticStart` is on (default; owner, 2026-10-08).
4. The live contract's clock check fails with the likely cause and the fix in its message.
5. The automatic path (a SYSTEM scheduled task that restarts `w32time`) is designed here and built as story 2.
6. RED-first tests for every rule, the incident as fixtures; docs; a `POST_DEPLOY.md` item.

## 3. Design

### D1 — one probe, two more TAGGED lines (no second PowerShell command)

The Windows clock probe is the ONE PowerShell command the product may start (`NeverList.cs:39`, `:79-81` compares the
argv to `HealthCommands.WindowsClock.Arguments`). It already runs every full run and in A16's preview, by absolute path
under systemd. So the service state rides that probe: `HealthCommands.ClockScript`
(`src_daemon/src/WslCare.Core/Health/HealthCommands.cs:24-25`) takes both instants FIRST, then
`Get-Service -Name w32time -ErrorAction SilentlyContinue`, and prints two more lines, TAGGED so that an empty value can
never shift the others (`ProcText.Lines` drops empty lines, `ProcText.cs:36`): `w32time.status=<Status>` and
`w32time.startType=<StartType>`. The launch-latency subtraction (`HealthCollector.cs:169-178`) is unchanged.
`HealthParsers.WindowsClock` (`HealthParsers.cs:101-108`) keeps its three positional lines and reads the tags when
present into a closed `WindowsTimeService(Status, StartType)` — unknown with the reason when a tag is absent (an older
fake, an old fixture) or empty. The never-list needs no widening: the probe is still one fixed argv, derived.

`WindowsClockSample` (`Records/SlowParts.cs:18`) gains an optional `TimeService` (init, absent on older lines), so the
history line and the run detail carry it; `ClockReport` (`Collect/HealthReport.cs:33`) gains `timeService`.

### D2 — the independent reference (two read commands, declared as templates WITH slots)

`ClockReferences.MeasureAsync` — ONE function, used by the full run and A16's preview:

1. **The HTTP `Date` of `clock.referenceUrl`** (default `https://www.microsoft.com`; empty = off):
   `curl --disable --silent --show-error --head --max-redirs 0 --proto =https --proto-redir =https --max-time <n>
   --header "Cache-Control: no-cache" --url <url>`. `--disable` FIRST (no root `.curlrc` can add an `output` or
   `write-out`), `--url` keeps the value from ever being read as an option. It is a PARAMETERISED template in
   `ReadCommandTemplates` (`Processes/Policy/ReadCommandTemplates.cs:21-45` is the shape), not `CommandTemplate.Fixed`:
   `Fixed(Func<ToolCommand>)` freezes the argv at its first call and re-reads only the limits
   (`CommandTemplate.cs:81-85`), so a config value in the argv would make the product refuse its own curl. Two slots:
   `url` — a new `SlotKind.HttpsUrl` holding EXACTLY the key's rule (`https://host[:port][/path]`, host of letters,
   digits, `.` and `-`; path of `A-Za-z0-9._~/-`; no space, quote, `=`, `?`, `#` or control character — `=` matters to
   the never-list's protected-path rule, `NeverList.cs:171`) — and `seconds` — an integer slot `1–30`. The key is
   MACHINE-layer only (root runs it). The reference instant is the `Date` + 0.5 s (the header truncates to seconds)
   against the midpoint of the request on the distro's clock.
2. **Else timesyncd**, only when it is trustworthy NOW: `timedatectl show` says `NTPSynchronized=yes`
   (`Systemd/SystemdCommands.cs:61`, already read) AND `timedatectl timesync-status` (new fixed read; added to
   `SystemdCommands.ReadVerbs`, `SystemdCommands.cs:67-70`) shows a last NTP `Offset:` within the tolerance — then the
   distro's clock IS the reference (reference − WSL = 0). A large last offset is itself evidence of the fight:
   *"timesyncd's last NTP sample found the distro's clock 7 200.4 s off — something else set it"*.
3. Else `unknown`, naming both reasons.

**A clock that jumps DURING the measurement voids it.** The probe and the reference are seconds apart, and in the fight
the distro's clock moves 7 200 s about every 33 s. So `MeasureAsync` reads the wall clock and the monotonic clock
(`TimeProvider.GetUtcNow` / `GetTimestamp`) before the probe and after the reference; when the wall clock advanced more
than `T` away from the monotonic one, the standing is `unknown` (*"the distro's clock jumped by N s during the
measurement"*).

A missing `curl`, a refused connection or an unparsable header is that source's reason, never a zero.

### D3 — the closed verdict: which clock is wrong

`ClockStanding.Judge(windowsMinusWsl W, referenceMinusWsl R, tolerance T)` — pure. `W − R` is Windows minus the
reference; `R` is the reference minus the distro. Windows is judged FIRST, so a distro Hyper-V has just dragged to the
wrong host time still names Windows — and the judgement then says the distro is off too:

| condition | standing | `distroAlsoOff` |
|---|---|---|
| no Windows observation, no reference, or a jump during the measurement | `unknown` (the reason) | — |
| `W − R < −T` | `windowsSlow` | `|R| > T` |
| `W − R > T` | `windowsFast` | `|R| > T` |
| otherwise, `|R| > T` | `wslWrong` | — |
| otherwise | `agree` | — |

`T` = `clock.referenceToleranceSeconds` (default 30: the measured github.com edge staleness was 6.5 s, the header's
resolution 1 s; the incident was 7 200 s — this verdict finds gross errors, the existing 5 s `clock.drift` stays the
fine one). Safe direction: LOWER. When `distroAlsoOff`, every sentence says so (*"the distro's clock is off too: Hyper-V's
time sync set it to the host's"*) — never *"not WSL's"*.

### D4 — three new verdicts, in `ThresholdRules.FromFullRun` (`Thresholds/ThresholdRules.cs:128-140`)

| id | ok | warn | critical | unknown |
|---|---|---|---|---|
| `clock.timeService` | Running and (Automatic, or Manual while `clock.manualStartWarns` is off) | Running with StartType Manual (*"Windows' default; Windows or other software can stop it again — the fix sets Automatic, which does NOT restart it after a stop: only story 2's task does"*); or Stopped while `clock.reference` is `agree` / `unknown` (*"stopped; on a workgroup PC Windows starts it by a trigger, so this alone is not a fault — the clock still agrees"*) | Stopped / Disabled while `clock.reference` names Windows (*"stopped, and the Windows clock is N s off: start the Windows Time service and resync — the extension's **Start Windows Time** (one UAC prompt), or as administrator `Start-Service w32time; w32tm /resync /force`"*) | not printed / not read |
| `clock.reference` | `agree` | `wslWrong` (*"… while Windows agrees — A16 or timesyncd corrects the distro"*) | `windowsSlow` / `windowsFast` (*"the Windows clock is N s slow against <source>; …the same fix…"*, plus the `distroAlsoOff` sentence) | `unknown` |
| `clock.fight` | journald's *"Time jumped backwards"* in the last 4 h of THIS boot ≤ `thresholds.timeJumpsBackWarnPer4h` (default 10) | above it — *"the distro's clock was set backwards N times in 4 h: two time-keepers disagree (Hyper-V's sets the host's clock, timesyncd sets NTP's); clock.reference names the wrong one; journald rotates at each jump, which is why the journal history is short"* | — | journal unread |

**The jump count is windowed on the MONOTONIC clock**, because the lines it counts carry the wall time AFTER the jump
back — 2 h in the past — so `--since <last run>` (`ReadCommandTemplates.cs:41`) would not see them in exactly the
incident. A new fixed read: `journalctl --boot --no-pager --quiet --output=short-monotonic --unit=systemd-journald
--grep=Time jumped backwards`; each line starts `[ <seconds since boot> ]`; the count is the lines at or after
`uptime − 4 h` (`/proc/uptime`, already read). `--unit` is chosen over `--identifier` (82 vs 163 on 2026-10-08): it is a
rate against a threshold, not a census; recorded as a residual, as is `CLOCK_MONOTONIC` against `/proc/uptime`'s
`CLOCK_BOOTTIME` (they differ by a suspend; a WSL VM pauses both).

`status` carries all three from the newest full run with no change (`Status/FullRunVerdicts.cs:23-32` reads every
recorded threshold), so the panel's *Verdicts* row shows them (`src_vs_code/src/panel/fieldMap.ts:158`).

### D5 — `doctor` reads the newest full run, it does not probe

`doctor` gains `windowsTime` and `clockReference` (`Doctor/DoctorRun.cs:66-71`), each derived from the newest full run's
recorded verdict (`FullRunVerdicts.Read`, one file read) with its age: critical → `problem`, warn → `ok` with
*"warning: …"*, ok → `ok`, unknown/none → `unknown`. `DoctorReport.Healthy` fails only on `problem`
(`DoctorRun.cs:84`), so ONLY a wrong Windows clock makes `doctor` unhealthy — a stopped-but-correct `w32time` (the
workgroup default) never fails `install.sh`'s final verification (`install.sh:1031-1050`). Why not a live probe:
`doctor` is bounded by the extension's worst-case table (`src_vs_code/src/client/worstCases.ts:60`, the setting's
minimum 119 s, `src_vs_code/src/settings/numbers.ts:29`); a probe (20 s) and a HEAD (10 s) would move a contract with
two implementations for no new fact — `collect` IS the full run, `install.sh` runs one before `doctor`, and *Run full
check now* refreshes it.

### D6 — A16 steps only when a reference proves the step helps (RED first)

In `ClockFix.SkipAsync` (`Actions/Clock/ClockFix.cs:183-194`) the preview measures the reference (D2) with its live
Windows observation and judges (D3) — **as built, only when the live offset is above `clock.maxDriftSeconds`**: clocks
that agree send no request to the network (code round, coai #12). A16 steps the distro to the host (`hwclock -s`) or toward chrony's sources
(`chronyc makestep`): both are trusted only when the host is the closer clock. The gates, in order:

1. timesyncd synchronised AND the live offset above `clock.maxDriftSeconds` → **Skip**: *"the Windows clock is wrong,
   not WSL's: timesyncd keeps the distro on NTP and Windows is N s off it"* (the old text named no culprit) — **as
   built, only while the reference does not say the distro is off too**; then the skip keeps plan §15 #10 and quotes the
   reference instead (code round, coai #7 / own #5).
2. `windowsSlow` / `windowsFast` → **Skip**: *"the Windows clock is N s slow against <source>; stepping the distro to
   the host's clock would set it wrong — fix Windows (Start Windows Time)"* (+ the `distroAlsoOff` sentence).
3. `unknown` → **Skip**: *"the clocks disagree and no independent reference can say which is wrong (<reasons>); A16
   steps only when a reference shows the distro is the wrong one"*. **This costs a real recovery path, deliberately:**
   a distro lagging after a VM resume on an OFFLINE machine is no longer stepped (it was the case `hwclock -s` was the
   workaround for; Hyper-V's own time sync normally corrects it). Recorded as a residual.
4. a known reference: step only when it brings the distro closer — `|W − R| < |R|` (after the step the distro is
   `W − R` off; before it, `R`) — AND `|R| > clock.maxDriftSeconds`. Otherwise **Skip** (*"the distro is N s off the
   reference and Windows M s: stepping to Windows would not bring it closer"*). This also closes the hole the
   consultant named: `W = 12, R = 0` reads `agree` and must NOT step.
5. then the existing gates, unchanged (within the limit, once per drift, once an hour).

A skip stops a button too (`Actions/Engine/ActionEngine.cs:353`, before the trigger gate). A16 declares the new read
templates in its `Commands` (`ClockFix.cs:72`).

### D7 — the START action, story 1: a button through ONE elevated PowerShell

A new extension module `src_vs_code/src/windowsTime/` (the Windows side's first action; PLAN_windows_care.md §8a allows
"a button that runs elevated only from an interactive UAC prompt"):

- **The command is a module constant**, never from the daemon or the page (nothing from WSL may become an admin
  command on Windows). Two variants, chosen by `wslCare.windowsTime.setAutomaticStart` (default `true`, application
  scope — the owner's decision). Nothing is found through `PATH` or a user's module folder, and each failure has its
  own exit code because an elevated child's streams cannot be read across the integrity boundary (`Start-Process
  -Verb RunAs` refuses redirection):

  ```powershell
  $env:PSModulePath = "$PSHOME\Modules"
  try { Set-Service -Name w32time -StartupType Automatic -ErrorAction Stop } catch { exit 10 }   # only while the setting is on
  try { Start-Service -Name w32time -ErrorAction Stop } catch { exit 11 }
  $w32tm = Join-Path $env:SystemRoot 'System32\w32tm.exe'
  for ($i = 0; $i -lt 3; $i++) { & $w32tm /resync /force | Out-Null; if ($LASTEXITCODE -eq 0) { exit 0 }; Start-Sleep -Seconds 2 }
  exit 12
  ```

  The outer process is `%SystemRoot%\System32\WindowsPowerShell\v1.0\powershell.exe -NoProfile -NonInteractive
  -Command <outer>`. **As built** (code round, own #1 — `Start-Process -Verb RunAs` rethrows the refusal as an
  InvalidOperationException carrying only its message, so 1223 could never be read): `<outer>` builds a
  `ProcessStartInfo` for the same absolute `powershell.exe` with `Verb = 'runas'`, `UseShellExecute`, a hidden
  window and `-NoProfile -NonInteractive -EncodedCommand <base64>`, calls `[System.Diagnostics.Process]::Start`, reads
  the `Win32Exception` 1223 as declined (any other launch failure 13), waits and exits with the child's code. The
  plan's first shape, kept for the record:
  `try { $p = Start-Process -FilePath <abs powershell.exe> -Verb RunAs -Wait -PassThru -WindowStyle Hidden
  -ArgumentList '-NoProfile','-NonInteractive','-EncodedCommand','<base64>' } catch { if (<1223 in the exception or its
  inner one>) { exit 1223 }; exit 13 }; exit $p.ExitCode`. The inner script travels as `-EncodedCommand` (UTF-16LE
  base64 of the constant), so no quoting layer exists. A result FILE was considered for the error text and rejected:
  an elevated write to a fixed path in the user's `%TEMP%` follows a link the user's own processes can plant.
- **Flow:** a modal shows the inner script verbatim and says one UAC prompt follows; only its confirm button continues;
  the runner (`src_vs_code/src/process/runner.ts`, the ONE launcher) starts the outer PowerShell with a ceiling
  `wslCare.timeouts.windowsTimeFixSeconds` (default 180: it waits for a person at the UAC prompt); the answer is closed —
  `done` (0) · `declined` (1223) · `failed` with the sentence of 10 / 11 / 12 / 13 or the bare code · `timedOut`. After
  `done`, the host starts *Run full check now* (the existing `CleanupHost.runFullCheck`), so the verdicts — the
  persisted truth — turn green without a manual refresh.
- **Where it appears:** a palette command `wslCare.startWindowsTime`, and a panel action button *Start Windows Time*
  (`PageAction`, `src_vs_code/src/panel/view.ts:43`; `messages.ts:33` accepts it BARE) shown while the newest status
  carries `clock.timeService` warn/critical or `clock.reference` `windowsSlow`/`windowsFast`.
- **Durable status** (`common.durable-status`): the in-flight label (*Starting the Windows Time service…*) is optimistic
  only; the source of truth is the service state the next full run reads, and the button re-derives from it after a
  reload. An elevated child outlives an extension-host reload (it is the UAC broker's child, not ours); the follow-up
  full check then shows its result.
- **Test mode:** a recorder replaces the runner for this flow, as *Install daemon* does; the test tripwire
  (`src_vs_code/src/test/support/noRealWsl.ts:19-34`, today wsl.exe only) also refuses a real PowerShell spawn; no test
  starts PowerShell elevated or touches a service.

### D8 — the live contract REPORTS the cause (coordinator, 2026-10-08)

`HealthContractTests.The_windows_clock_probe_prints_two_instants_and_the_profile_from_inside_the_distro`
(`src_daemon/tests/WslCare.LiveContract/HealthContractTests.cs:94-113`) keeps its 60 s bound, but when it is crossed the
test measures the reference with the PRODUCT's `ClockReferences.MeasureAsync`, judges with `ClockStanding.Judge`, and
fails with the product's own sentence: *"the Windows clock disagrees with <source> by N s — is the Windows Time service
running? (w32time: Stopped, StartType Manual) — fix: Start Windows Time, or as administrator `Start-Service w32time;
w32tm /resync /force`"* (or *"the distro's clock is N s off …"*). New live checks: the probe's two tagged lines are
present, `timesync-status` parses, the curl `Date` parses, the monotonic journal search parses. The message text comes
from the product (one road in), so the release checklist and `status` say the same thing.

### D9 — story 2 (planned, NOT built here): the scheduled task

> **Extracted** on 2026-10-08 to [PLAN_windows_time_task.md](../todo/PLAN_windows_time_task.md), which owns it now; the
> text below is the design as this plan left it.

The extension command *Install the Windows Time guard* shows the task XML and asks UAC once to register
`\wsl-care\windows-time-guard`: principal `SYSTEM`, triggers at startup, at logon, every `N` hours, and on the System
log's event 7036 for W32Time entering the stopped state. The 7036 `Data` text is LOCALISED; the event's `Binary` field
holds the service key name (UTF-16 hex of `W32Time`), which is not — measure which one an `EventTrigger` XPath can match
before relying on it. The action is the fixed `powershell.exe -NoProfile -NonInteractive -EncodedCommand <the D7 inner
script without Set-Service>` in the task definition itself — no script file a user could edit — and it carries a rate
limit (at most one start per `M` minutes) so it never loops against software that stops the service again. It only
(re)starts `w32time` and resyncs. Uninstall removes it. It stays a separate story because the repository has NO Windows
installer or task infrastructure today (PLAN_windows_care.md §7 step 3 is unbuilt) and its trust boundary (a SYSTEM task
triggered by any user-visible event) deserves its own plan round.

## 4. Boundaries with the neighbouring plans

| Item | Built by | The other plan's part |
|---|---|---|
| the Windows clock probe's service lines, `clock.timeService` / `clock.reference` / `clock.fight`, A16's brake, the live contract's message | this plan | PLAN_wsl_care_daemon.md §4.5 / §5 A16 own the probe and A16; their sections point here |
| the elevated *Start Windows Time* button (UAC, no task) | this plan, story 1 | PLAN_windows_care.md §8a: the first Windows action; the elevated-task channel stays its own |
| the SYSTEM scheduled task | this plan, story 2 (unbuilt) | PLAN_windows_care.md §2 *Admin work* / §7 step 3 — the generic installer and elevated task stay there; this task is a single-purpose one and must not grow into that channel |

Disjoint otherwise: nothing here touches W-A1…W-A15. The order: this plan first — it is self-contained and urgent;
PLAN_windows_care.md's installer later absorbs story 2's task or leaves it single-purpose, its decision.

## 5. Growth

- History line: +≈ 60 B per full run (the service status in `slow.windowsClock`) × 6 runs/day × 90 days retention ≈
  32 KB. Run detail: +≈ 600 B per full run, same 90-day retention (`RunRetention`). No new file, directory or process.
- The extension stores nothing new (the fix's result is a notification; the truth is the next full run).
- Interrupted: a killed probe, HEAD or search is the ordinary command ceiling + tree kill (`ProcessCommandRunner`); no
  in-flight state is persisted, so there is nothing to sweep.

## 6. Configuration keys (all in `default.json`, `contracts/config-keys.json` regenerated)

| key | default | range / rule | trust |
|---|---|---|---|
| `clock.referenceUrl` | `https://www.microsoft.com` | empty or the `HttpsUrl` rule (D2) | machine layer only |
| `clock.referenceToleranceSeconds` | 30 | 5–3600 | lower |
| `clock.referenceTimeoutSeconds` | 10 | 1–30 | lower, machine only |
| `clock.manualStartWarns` | true | bool | display |
| `thresholds.timeJumpsBackWarnPer4h` | 10 | 0–1 000 000 | display |
| extension `wslCare.windowsTime.setAutomaticStart` | true | bool | application |
| extension `wslCare.timeouts.windowsTimeFixSeconds` | 180 | 30–3600 | application |

## 7. Build order

1. Research record `research/2026-10-08_windows_time_stopped.md` (the measurements above, and the hypothesis it does
   NOT settle: the 7 200.42 s equals the CEST offset to 0.42 s — the signature of an RTC holding UTC read as local
   time — unproven).
2. RED tests (daemon): the tagged probe lines; `ClockStanding.Judge` table; the `timesync-status` offset parser; the
   curl `Date` parser; the monotonic journal count; the three verdicts at their edges; A16 — the incident shape
   (Windows −7 200 s on two observations, timesyncd NOT synchronised) currently STEPS with the reference agreeing with
   WSL AND with the reference unknown — and the synchronised case's reason; the `W = 12, R = 0` case.
3. Daemon code: D1, D2 (the `HttpsUrl` slot kind, the templates, `ReadVerbs`), D3, D4, D6, D5, D8; keys; the golden
   contracts and `config-keys.json` regenerated by their own writers; fixtures for the new commands from the 2026-10-08
   capture (anonymised through `FixtureIdentity`); the harness's fake tool list gains `curl` and the test configuration
   points `clock.referenceUrl` at a value the fake answers, so no run reaches the network
   (`FakeToolProtocol.cs:33`, `CollectFlows.cs:151`).
4. Extension: D7 module + tests RED first (the command constant, the encoded command decodes to it, the outcome mapping,
   the flow order, the button's derivation, the bare message, the tripwire), then the wiring, `package.json`, the
   numbers table.
5. Docs: `module_daemon.md`, `module_vs_code.md`, `module_tests.md` rows, a POINTER in `architecture.md` (near its
   256 KiB resolver cap — no section), the plan cross-references, `POST_DEPLOY.md` (items 8 and 9 — the same release,
   downloaded then attested — merged into one, a new item for the Windows clock; still 12).

## 8. Test plan

- **Daemon unit (RED first):** `HealthParsers.WindowsClock` with 3 lines, the two tags (Running/Manual, Stopped/Manual),
  an empty tag, an empty profile; `ClockStanding.Judge` — every row of D3 at ±T and the incident (W = −7 200.42,
  R = 0 → `windowsSlow`; W = 0, R = +7 200 → `windowsSlow` with `distroAlsoOff`); a jump during the measurement →
  `unknown`; the systemd timespan parser (`-18.401ms`, `+2h 420.512ms`, `+1min 3.5s`, `+335us`, garbage); the `Date`
  parser (RFC 1123, a missing header, two `Date` lines); the monotonic count (lines on both sides of `uptime − 4 h`, a
  boot younger than 4 h); each verdict at its edge and its unknown; `clock.timeService` Stopped is warn with `agree`,
  critical with `windowsSlow`; `doctor`'s two checks from a recorded detail (critical → problem, warn → ok, no full run →
  unknown).
- **A16:** the incident shape steps today (RED: `hwclock -s` in the recorded commands) — with the reference agreeing
  with the distro and with the reference unknown — and skips after; the synchronised case names *"the Windows clock is
  wrong, not WSL's"*; `W = 12, R = 0` does not step; a distro 12 s behind Windows with the reference agreeing with
  Windows DOES step (the one case A16 is for); break-it: delete each new gate line and watch its test go red.
- **Policy:** the curl and `timesync-status` and journal argv pass `CommandPolicy.Product`; a curl with a URL outside
  the `HttpsUrl` rule (a `?`, a `-` start, `http://`) is refused; after a config change of the URL and the timeout the
  product's own curl still passes; the probe's argv still passes the never-list and a foreign PowerShell still does not.
- **Scenario harness:** `collect --json` with the fake powershell answering the tagged lines and fake curl/timedatectl/
  journalctl → the three verdicts in the detail and in `status`; goldens regenerated and diff-read.
- **Live contract** (inside WSL, the release checklist): D8's checks; the failure sentence asserted in a unit test
  against a scripted runner (the live run cannot be made to fail on demand).
- **Extension:** `windowsTimeFix.test.ts` (constant, encoding round-trip, outcomes for 0/1223/10/11/12/13/other,
  timeout, order: modal before runner, nothing run on decline); the PowerShell texts parsed by PowerShell's own parser on
  Windows (zero parse errors, the expected command names) — execution is refused on purpose: it would change this
  machine's services; `viewModel` shows the button only for the named verdicts; `messages.ts` accepts the bare type and
  nothing with a payload; the tripwire refuses a PowerShell spawn; the contributed-command row in `module_tests.md`.
- **Whole suites:** every daemon test executable, the scenario harness, `npm test`.

## 9. Owner questions

- **Q1** — decided (owner, 2026-10-08): the action sets StartType Automatic; the switch
  `wslCare.windowsTime.setAutomaticStart` stays, default ON. Open only: the daemon's report keeps its own switch,
  `clock.manualStartWarns` (default ON) — a different behaviour (what is SAID, not what is DONE). Keep it?
- **Q2** — the HTTP reference defaults to `https://www.microsoft.com` (github.com measured up to 6.5 s stale). A root
  daemon then makes one HTTPS HEAD per full run (every 4 h) plus one per A16 preview. Keep it on, choose another host,
  or default it off (timesyncd only — and then A16 skips more often, D6 gate 3)?
- **Q3** — a WRONG Windows clock (not a stopped service) makes `doctor` unhealthy and so fails `install.sh`'s final
  verification on such a machine, with the reason. Accept?
- **Q4** — story 2 (the SYSTEM task, 7036 trigger) next, as its own plan round?
- **Q5** — the 2 h equals the time-zone offset: worth a one-off read of the firmware clock / `RealTimeIsUniversal`
  (absent on this machine) before blaming AMD software alone?

## 10. Definition of Done

- [x] Plan reviewed (coai `review_plan`), findings resolved.
- [x] Every rule of D1–D8 has a test seen RED for the real symptom, then green; break-it checks recorded.
- [x] The daemon suites, the scenario harness and the extension suite green; goldens and contracts regenerated.
- [x] `research/2026-10-08_windows_time_stopped.md`, `module_daemon.md`, `module_vs_code.md`, `module_tests.md` updated;
      `architecture.md` carries a pointer only; both neighbouring plans name the boundary.
- [x] `POST_DEPLOY.md` has the Windows Time item, still ≤ 12 items (8 and 9 merged, said why).
- [x] Code round (coai `review_code`); story 2 extracted to its own plan. The merge is the pull request's.

## 11. Plan round 1 — what changed

| # | Source | Finding | Decision → change |
|---|---|---|---|
| c0 | codex, Major | "not WSL's" is false when both clocks are wrong (W = 0, R = +7 200) | accepted → `distroAlsoOff` in D3; the text in D4/D6 |
| c1 | codex, Major | `clock.fight`'s 4 h rate undefined across a delayed run | accepted → a rolling 4 h window on the monotonic clock (D4) |
| c2 | gemini, Blocking | `unknown` falls back to stepping to the wrong host | accepted after a consultation (codex, `solved`): A16 steps only with a reference that proves the step helps (D6 gates 3–4); the lost offline recovery is a stated residual |
| c3 | gemini, Major | `doctor` from cached verdicts fails `install.sh` / stays stale | rejected: `collect` IS the full run and `install.sh` runs one before `doctor`; no full run is `unknown`, which never fails `healthy` |
| c4 | gemini, Major | the UAC refusal throws, so it would read as exit 1 | accepted → the outer try/catch exits 1223 (D7) |
| c5 | gemini, Major | the elevated child's stderr cannot be read | accepted, with another fix: distinct exit codes 10–13; the proposed result file rejected (an elevated write to a user-plantable path) |
| o1 | own, Blocking | `CommandTemplate.Fixed` freezes the argv — a config value there is refused | accepted → slotted template, `SlotKind.HttpsUrl` (D2) |
| o2 | own, Major | `unknown` keeps stepping | = c2 |
| o3 | own, Major | the clock can jump between probe and reference | accepted → wall vs monotonic check voids the measurement (D2) |
| o4 | own, Major | Stopped is the workgroup default; critical would fail healthy installs | accepted → Stopped is warn unless `clock.reference` names Windows (D4, D5) |
| o5 | own, Major | backward-jumped lines are older than `--since` | accepted → `--boot` + monotonic window (D4) |
| o6 | own, Minor | curl as root: `.curlrc`, redirects, `=` in the URL | accepted → `--disable` first, `--max-redirs 0`, `--proto-redir`, the rule (D2) |
| o7 | own, Minor | harness contracts not named | accepted → build step 3 names them |
| o8 | own, Minor | positional lines shift on an empty value | accepted → tagged lines (D1) |
| o9 | own, Minor | PATH / module autoload in the elevated script; tripwire wsl.exe only | accepted → absolute `w32tm.exe`, `$PSHOME\Modules`, the tripwire (D7) |
| o10 | own, Minor | "the fix sets Automatic" suggests no recurrence | accepted → the warn text says only story 2 prevents it |
| o11 | own, Minor | POST_DEPLOY item unspecified; the record linked as if it existed | accepted → build step 5 names the merge; the link says "written in build step 1" |
| o12 | own, Minor | 7036 `Data` is localised; no rate limit | accepted → D9 |
| — | coordinator | the live contract must name the cause | → D8 |

## 12. Code round — what changed, and the deviations as shipped (2026-10-08)

coai session `b8084f2d`, `review_code` over the branch: **all 8 reviewers answered** (codex and gemini, four roles each),
verdict `proceed`, 14 findings — 8 accepted, 6 rejected with reasons; and one own review (Opus, feature-dev
code-reviewer, 7 findings, all accepted).

| # | Source | Finding | Decision → change |
|---|---|---|---|
| k1, k10 | codex + gemini | `timesync-status` with a field printed twice threw (`ToDictionary`) | accepted → read by field, twice is unavailable; RED first |
| k3–k5 | gemini | cyclomatic complexity of the timespan parser | accepted → split into small pure functions; `SystemdTimespan.Seconds` answers a `Reading<double>` |
| k6 | gemini | `Jumped` need not be public | accepted |
| k9 | gemini | the run detail re-judged the clocks during serialisation | accepted → the judgement is taken ONCE in `HealthCollector` and stored on `HealthSample`; the detail and the verdicts project it |
| k11 | gemini | POST_DEPLOY item 9 passed on a distro clock warning | accepted → it also refuses a `clockReference` "warning:"; a flow for it |
| k12 | gemini | A16's preview asked the reference even when the clocks agree | accepted → asked only past `clock.maxDriftSeconds`; RED first |
| k0 | codex | nullable `TimeService` on the wire records | rejected: the JSON-edge records keep nullable members for fields older lines lack (`WindowsProfile`); business logic reads `TimeServiceReading` |
| k2 | codex | the boot-wide journal search grows with uptime | rejected: bounded by the search ceiling and output cap (past the cap: unavailable, never partial); near zero outside a fight |
| k7 | gemini | the timesyncd gate preempts the reference | rejected as asked (plan §15 #10 keeps a synchronised clock unstepped); its sentence now follows the reference |
| k8 | gemini | ambient `Tuning.Current` | rejected: the daemon's documented convention (`Config/Tuning.cs`) |
| k13 | gemini | `timedatectl show` asked twice | rejected: milliseconds, only on the fallback path |
| o1 | own | `Start-Process -Verb RunAs` hides 1223 | accepted → `Process.Start` with `runas` (D7 above) |
| o2 | own | goldens not regenerated | accepted → written by the linux-x64 CI leg (`ci-daemon.yml` uploads them after a failure) and taken in |
| o3 | own | an empty service tag read as "not running" | accepted → unknown; an empty start type skips the manual rule; RED first |
| o4 | own | two A16 tests passed through a skip the engine would stop at | accepted → they script a reference and assert the preview has no skip |
| o5 | own | the synchronised skip could say "not WSL's" while the distro is off | accepted (k7's sentence); RED first |
| o6 | own | no scenario over the new answers | accepted → `CollectFlows.Collect_judges_the_windows_time_service_which_clock_is_wrong_and_the_clock_fight` |
| o7 | own | the status line | accepted → this promotion |

**Also found while building:** a break-it check of the test tripwire, run for real, opened an elevated PowerShell window
on the owner's machine (the fix did NOT run) — [2026-10-08_windows_time_stopped.md](2026-10-08_windows_time_stopped.md)
§4. **Residuals as shipped:** the offline-resume recovery A16 gave up (D6 gate 3); the journald count is `--unit`, not
`--identifier`; `CLOCK_MONOTONIC` against `/proc/uptime`'s `CLOCK_BOOTTIME`; the elevated run itself is exercised only
by PowerShell's parser, never executed by a test; the live contract's new checks run only at the release checklist.

**Final code round** (`review_code` again over `859a3cb`): all 8 reviewers answered, `proceed`, 4 findings — 1 accepted
(the synchronised skip, with NO reference answering, now says the diagnosis rests on timesyncd alone: *"no other
reference could confirm it"*; RED first), 3 rejected with reasons (an unknown standing already is its own state and its
zero offsets are never read; the service names are Windows' own display strings read through one predicate each; a
stale timesyncd sample cannot blame Windows, because Hyper-V moves the distro TO the host's time and timesyncd re-polls at
once after any clock change).
