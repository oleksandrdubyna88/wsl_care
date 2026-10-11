# PLAN — the Windows Time launcher elevates PowerShell 7 by the same resolver as *Enable <GPU>*, and the panel names each engine

> Status: **IMPLEMENTED, 2026-10-11.** What shipped differently is in §12 (the as-built record): D2 replaced by its own engine
> read (`EngineHost`, §11 R3), the shipped G1 hardened (§11 R1), the stamp line (R4). The acceptance gate (POST_DEPLOY
> item 9): *Remove* and *Install* passed under PowerShell 7 on the owner's machine, 2026-10-11; the open tail is only
> *Start Windows Time* under PowerShell 7, pressed the next time the service is found stopped. Scope: the VS Code extension
> (`src_vs_code/`) — the engine resolver of story G1 moved out of `src/gpu/` into one shared module, the Windows Time
> launcher's three user-context elevated runs (*Start Windows Time*, *Install the Windows Time guard*, *Remove the Windows
> Time guard*) moved onto it, and one line per part in the panel that names the engine. The SYSTEM task stays on Windows
> PowerShell 5.1. The daemon is not changed. **§11 overrides the sections above it where they differ.**
>
> Related: [PLAN_gpu_health.md](PLAN_gpu_health.md) (the resolver, §11 *The owner's decision … and the path*),
> [PLAN_windows_time_guard.md](PLAN_windows_time_guard.md) (the launcher, D7),
> [PLAN_windows_time_task.md](PLAN_windows_time_task.md) (the guard's task, D3–D8),
> [2026-10-10_gpu_disabled.md](2026-10-10_gpu_disabled.md) (G8, G9), [module_vs_code.md](module_vs_code.md).

## 1. Goal

The owner's rule (2026-10-10, option 1; the path 2026-10-11): **every user-context elevated launch runs PowerShell 7 when it
is installed, and Windows PowerShell 5.1 only when it is not; the SYSTEM guard task stays on 5.1; the panel says which engine
each part uses.**

Today only *Enable <GPU>* follows the rule. The Windows Time launcher's three elevated runs still start 5.1 every time
(`elevatedRequest` with no `elevated` argument, `src/windowsTime/windowsTimeFix.ts:97`, called from `fixRequest` :110 and
`runGuardOp` `src/windowsTime/guardFlow.ts:158`), and the panel names no engine anywhere.

When this is done:

1. *Start Windows Time* and the guard's install and remove elevate **the engine `engineOf` chooses** — the MSI PowerShell 7
   the read verified, else the Store package by its resolved, signature-checked path, else 5.1. The alias is never started.
2. There is **one** resolver. The GPU code and the Windows Time code import the same functions; no second copy exists.
3. The engine is chosen from a read, **and checked again right before the launch** (a second read after the modal). When the
   two differ, nothing runs and the person is told to press again — as G1 does (`src/gpu/gpuEnable.ts:216`).
4. Each modal and each outcome sentence names the engine. Exit 13 on the Store engine adds G1's Store note.
5. The panel shows, per part: the engine of *Start Windows Time* and the guard's install / remove; that the guard's task runs
   as SYSTEM in Windows PowerShell 5.1; and the engine of *Enable <GPU>* when that button shows.
6. The guard's install works under PowerShell 7 (see §2, fact F1).

## 2. Facts (measured 2026-10-11, unelevated, read-only, on the owner's machine)

- **F1 — `Register-ScheduledTask` is not there under PowerShell 7 with the cut module path.** With
  `$env:PSModulePath = $PSHOME + '\Modules'` (the guard's `MODULES`, `src/windowsTime/guardTask.ts:91`) the Store pwsh
  7.6.6 has `Set-Service`, `Start-Service`, `Test-Path`, `Remove-Item`, `Get-ChildItem` and `Get-Item`, but **not**
  `Register-ScheduledTask` (nor `Get-PnpDevice`, as G1 found, research G6). So today's install script would exit 30 under
  pwsh 7. `Import-Module -Name (Join-Path ([Environment]::SystemDirectory) 'WindowsPowerShell\v1.0\Modules\ScheduledTasks')`
  loads it (ScheduledTasks 1.0.0.0, Manifest) and `Register-ScheduledTask` is then present; `Get-ScheduledTask -TaskPath
  '\wsl-care\'` answered 1 task. This is G1's `PnpDevice` pattern (`src/gpu/gpuScripts.ts`, `enableScript`).
- **F2 — the COM road is the same.** `Schedule.Service` under pwsh 7: a missing folder throws `HResult` -2147024894
  (0x80070002), the value `NOT_FOUND_HRESULT` reads (`src/windowsTime/guardScripts.ts`), so the removal's "absent is already
  removed" holds.
- **F3 — the Store version moves.** `$PSHOME` was `…\Microsoft.PowerShell_7.6.6.0_x64__8wekyb3d8bbwe` (7.6.4 on
  2026-10-10). G1 already resolves it at every read; nothing is pinned.
- **F4 — the elevated run under pwsh 7 is not measured here** (it raises UAC). The coordinator measured that an elevated start
  of the Store pwsh by its resolved path works (2026-10-11, Authenticode Valid, Microsoft); G1 ships it. The guard install
  under elevated pwsh 7 is the owner's first press (§9).

## 3. What exists (reuse first, verified at `05b35a29`)

| What | Where | Reused how |
|---|---|---|
| The probe: `SIGNED_HELPER`, `PWSH_PROBE` (Store package by `Get-AppxPackage`, the MSI by the known-folder Program Files, one verdict) | `src/gpu/gpuScripts.ts:42`, `:52` | moved to the shared module; the GPU query keeps embedding it |
| The probe's tags `pwsh.store`, `pwsh.path`, `pwsh.msi`, `pwsh.msiPath` | `src/gpu/gpuScripts.ts:28` (inside the GPU `TAGS`) | the four move to the shared module; `TAGS` spreads them, so the GPU's tag names do not change |
| The choice: `PwshProbe`, `PwshFacts`, `Engine`, `msiPwsh`, `storePwsh`, `sevenOf`, `engineOf`, `elevatedPowerShellFor`, `FileInfo`/`FileInfoOf` | `src/gpu/gpuScripts.ts:128`–`:265` | moved unchanged |
| The probe's parser `isProbeKind`, `probeOf` | `src/gpu/gpuState.ts:109`, `:114` | moved; becomes `pwshFactsFromTags(tags)`; `adaptersOf` calls it |
| Node's `lstat` of a program the launch might elevate, `fileInfoOf` | `src/gpu/gpuWiring.ts:64` | moved |
| The Store note on exit 13 | `src/gpu/gpuEnable.ts:56` | moved; G1's `failedWords` and the Windows Time sentences use it |
| The launcher: `outerScript`, `elevatedRequest(env, inner, timeoutMs, elevated?)` | `src/windowsTime/windowsTimeFix.ts:69`, `:97` | unchanged; Windows Time now passes `elevated` |
| The guard host's one-query-at-a-time `refresh()` | `src/windowsTime/guardHost.ts:76`, `query` :117 | carries the probe's facts beside the guard state |
| The SYSTEM task's program `TASK_POWERSHELL` and its ASCII test | `src/windowsTime/guardTask.ts:49` | unchanged |

**The module.** `src/powershell/elevatedEngine.ts` (new folder `powershell/`, the name says what it is about). *As built (§11
R2): the launcher moved to `src/powershell/elevatedLauncher.ts` too, and the engine module imports it, so the graph is
launcher ← engine ← {gpu, windowsTime} with no cycle.* The GPU files import from it instead of defining.

## 4. Design

**D1 — the shared module, no behaviour change for the GPU.** Pure move of §3's rows, plus two small exports that both
callers need:

- `pwshFactsFromTags(tags: ReadonlyMap<string, string>): PwshFacts` — the parser, taking any tagged answer.
- `elevatedArg(engine: Engine): string | undefined` — `undefined` for `windows`, else `engine.file` (the one place that maps
  an engine to `elevatedRequest`'s last argument; `enableRequestFor` uses it too).
- `STORE_NOTE` and `withStoreNote(sentence, launchFailed, engine)`.

The GPU's tests stay green unchanged except for import paths; a structure test asserts that `PWSH_PROBE`, `SIGNED_HELPER`
and `storePwsh` are defined in ONE file.

**D2 — the facts for Windows Time come from the guard's status query.** `PWSH_PROBE` is added to the guard's unelevated
`QUERY_SCRIPT` (`src/windowsTime/guardScripts.ts:66`), **before** the Task Scheduler read (that read ends with `exit 0` in its
`catch`, so a probe after it would not run for an absent task), with `$ProgressPreference = 'SilentlyContinue'` (G5). The
host keeps `pwsh: PwshFacts | undefined` beside its `GuardState` (the `GuardState` union is not widened). Why this query and
not a new one: it already runs when the panel opens, on refresh and after every elevated run (`guardHost.ts:14`), and both
Windows Time actions live in that section. Cost: + 0.9–1.3 s per guard read (G9), inside the 30 s
`wslCare.timeouts.windowsTimeGuardQuerySeconds`. The query stays on 5.1 (unelevated; the rule is about elevated launches).

**D3 — the engine is chosen from the last read and checked again after the modal.** Both flows:

1. `engine = engineOf(env, fileInfo, host.pwsh())` — before the modal.
2. The modal names it (D5).
3. After the confirm: `await host.refresh()`, then `now = engineOf(env, fileInfo, host.pwsh())`. When `now.file !==
   engine.file`: nothing runs, the person reads "Nothing ran: the elevated program changed while the question was open
   (<old> is now <new>). Press the button again." (G1's sentence, shared).
4. Else `elevatedRequest(env, inner, timeoutMs, elevatedArg(engine))`.

For the guard this re-read happens after the existing busy check and before `writePending` (`guardFlow.ts:129`), so a
refused launch leaves no pending record. *Start Windows Time* gains the guard host as a dependency (`FixDeps.engine` and
`FixDeps.reread`); in Test mode the recorder answers both.

**D4 — the guard's install imports ScheduledTasks by absolute path (F1).** A new line after `MODULES` in `installScript`:
`try { Import-Module -Name (Join-Path ([Environment]::SystemDirectory) 'WindowsPowerShell\v1.0\Modules\ScheduledTasks')
-ErrorAction Stop } catch { exit 33 }`, with `OP_EXIT.importFailed = 33` and its sentence "the elevated PowerShell could not
load its ScheduledTasks module". It runs under 5.1 too (same path, same module). The removal and *Start Windows Time* need no
import (F1, F2). The SYSTEM task's script is not touched.

**D5 — the words.** The modals say "One PowerShell runs ELEVATED — <engine label> — Windows asks you first (UAC)…" instead of
"One Windows PowerShell…" (`fixPrompt` :158, `installPrompt`/`removePrompt` `guardFlow.ts:75`, :94). Every outcome sentence
of the three runs that ran or may have run (done, failed, timed out) ends with " Elevated program: <label>." (as G1's
`enableSentence`); a declined, not-started or not-run outcome names none. A failed exit 13 on the Store engine
adds the Store note.

**D6 — the panel names each engine.**

- The Windows Time guard part (`div[data-guard]`) gains `p[data-guard-engine]`: "Elevated runs here (Start Windows Time,
  install / remove the guard): <label>." and "The guard's task runs as SYSTEM in Windows PowerShell 5.1." Before the first
  read: "Elevated runs here: Windows PowerShell 5.1 until a read verifies PowerShell 7." `GuardView` gains `engine: string`.
- Each GPU alert that carries an *Enable* button gains `p[data-gpu-engine]` "Enable runs elevated in: <label>."
  `GpuAlert` gains `engine: string` (`src/gpu/gpuState.ts:170`), empty when the alert has no button.
- Both rendered as TEXT (the page's `element()`), never HTML.

**D7 — what does not change.** The launcher stays 5.1 (`outerScript`'s unelevated half); the SYSTEM task stays
`TASK_POWERSHELL` with its ASCII test; the read-back comparison (`guardState.summariesMatch`) is about the task and is not
touched; the alias is never started; nothing from the page, the daemon or a setting's text reaches a script.

## 5. Build order

1. RED: the structure test "one resolver" (fails while the GPU files define it). Move the module (D1). GREEN; the whole
   suite green with only import changes.
2. RED: `guardScripts` — the query prints the probe before the task read; the install imports ScheduledTasks by absolute path
   and exits 33 when it cannot. Implement D2 (script + parser + host field) and D4.
3. RED: `guardFlow` / `windowsTimeFix` — the request carries the verified engine; a changed engine after the modal runs
   nothing and writes no pending record; 5.1 when no read. Implement D3.
4. RED: the words (D5) — modals and sentences name the engine; exit 13 on Store adds the note.
5. RED: the panel (D6) — view model and page tests. Implement.
6. Docs: `module_vs_code.md` (the resolver's new home, the Windows Time engine), `architecture.md` if the module map changes,
   `module_tests.md` rows, `research/PLAN_gpu_health.md` gets no edit (it is a record). Promote this plan when done.

## 6. Test plan

- **Unit (node --test):** the shared module (moved GPU tests); `pwshFactsFromTags`; the guard query's order (probe before
  the Task Scheduler read; no word of the tripwire's mutating list); the install's import line and exit 33; the three
  requests with each engine (MSI, Store, 5.1, and a Store path of the wrong shape → 5.1); the changed-engine refusal for each
  flow, with no pending record and no run; the sentences.
- **Page (pageHarness):** the two engine lines as text; no GPU engine line without a button; the SYSTEM line.
- **Extension-host scenarios:** the Windows Time recorder and guard recorder see the elevated program in the request.
- **Windows leg:** the existing probe test (G1's) still answers on the CI Windows runner.
- **Break-it on product code:** the engine dropped from the request; the probe moved after the task read; the import line
  removed; the re-read skipped; the SYSTEM task moved to pwsh (the ASCII/5.1 test must go red).
- **No test starts an elevated process**, a real `pwsh`, or a real `Get-AppxPackage`: the runner is the recorder or a fake.

## 7. Growth surfaces

- Any further elevated launch (a later Windows-side action) takes `engineOf` + `elevatedArg` + the re-read; nothing else.
- When the MSI install appears on a machine, it wins with no change (G1's order).

## 8. Open, not part of this story

- **A21's `processes.fdScanMilliseconds` stays at the provisional 10000 ms.** The coordinator's relay list at 12:28Z showed no
  interop relays at all (the creds-side orphan fix has probably removed them), so the scan cost and the pipes-vs-sockets
  question cannot be measured yet. Recorded in `todo/PLAN_twenty_sessions_all_day.md` S7b.2b as open.

## 9. Residuals (said, not solved)

- **The guard install under ELEVATED pwsh 7 is not measured** (F4): ScheduledTasks loads and `Register-ScheduledTask -Xml` is
  present unelevated; the elevated registration is the owner's first press. If it fails, the outcome says "Task Scheduler did
  not register the task" or "could not load its ScheduledTasks module" with "Elevated program: PowerShell 7 (…)", and nothing
  is half-done (the register is the last step). A POST_DEPLOY item records it.
- **The guard read costs about one more second** per read (D2). It does not poll; it runs on open, refresh and after a run.
- **The launcher's own `SystemRoot` trust** is unchanged (a residual since the Windows Time story).
- **The user's persisted environment** (own code review, finding 1): the elevated child's environment is built from the
  user's profile, and variables there act BEFORE the script's first statement pins `$env:PSModulePath` — to the best of our
  knowledge `DOTNET_STARTUP_HOOKS` for any .NET host such as `pwsh.exe`, and `COR_ENABLE_PROFILING` / `COR_PROFILER_PATH`
  for the .NET Framework host `powershell.exe`, so the 5.1 road had the same shape before this story. Under the same-user
  threat model R1 defends against, this is the larger residual, and it is not fixable inside a script; not measured here.
- **The first press may wait** up to `engineProbeSeconds` (20 s) for the engine read before its modal, with no progress
  notification (own code review, note 11); a read normally answers in 1–2 s.
- **Two probes per panel open** (the GPU query's and the engine read, §11 R3): about 1–2 s of one core each, no polling.
- **G1's order after its modal** (busy check, then the re-read): a window of about one second; not changed here (§11 R5).
- **PowerShell 7's startup update check** may make one outbound HTTP request from the elevated child; the child's fresh
  environment cannot carry `POWERSHELL_UPDATECHECK=Off`.
- **Registry virtualisation under the Store engine** (§11 R4) is unmeasured; the stamp line makes a virtualised removal visible.

## 10. Definition of Done

- [x] One resolver module; the GPU code imports it; the structure test proves one definition.
- [x] *Start Windows Time*, install and remove elevate `engineOf`'s engine; 5.1 only without a verified PowerShell 7.
- [x] The engine is read again after the modal; a change runs nothing and writes no pending record.
- [x] The install imports ScheduledTasks by absolute path; exit 33 has its sentence.
- [x] Modals and outcomes name the engine; the Store note on exit 13.
- [x] The panel names each part's engine, as text.
- [x] The SYSTEM task stays on 5.1; its ASCII test unchanged and green.
- [x] Tests: unit, page, scenario; break-its red then restored; lint, typecheck, `plan-lifecycle` clean.
- [x] Docs updated; the plan promoted to `research/` with its as-built record.
- [x] §11's R1–R12 built, each with its test (R1 RED on a spoofed `ProgramFiles`; R4 the stamp line).

## 11. Revision 1 — the gates (2026-10-11)

**The coai plan round** (session `0e5668f2`, 1 reviewer, `proceed`, 2 gating findings):

| Finding | Decision |
|---|---|
| The guard install under elevated pwsh 7 is unverified until the owner's first press | accepted, narrowed: I cannot raise UAC (only the owner presses). The first Install and Remove under the Store engine are an explicit **acceptance gate** recorded as a POST_DEPLOY item; §2 F4 and §9 say what is and is not proven |
| An unreadable probe selects 5.1 as if PowerShell 7 were absent | rejected — G1's shipped policy and the owner's rule fall back to 5.1 whenever PowerShell 7 is not VERIFIED ("nothing is refused"); one resolver means one policy; refusing would block *Start Windows Time*, the urgent fix for a slow clock, on a probe failure. Instead the label says so: "Windows PowerShell 5.1 (…) — PowerShell 7 could not be checked" |

**My own review (Fable, read-only, after D1/D2/D4 landed)** — four Major, eight Minor, two notes. Each verified against the
worktree; all accepted (R5 partly):

- **R1 (Major, security) — the Store road was anchored on the ENVIRONMENT's `ProgramFiles`, and the GPU query has no module-path
  cut.** `storeShape` is built from `env.ProgramFiles` (`elevatedEngine.ts`, `programFilesOf`/`storePwsh`), and
  `gpuScripts.ts`'s `QUERY_SCRIPT` does not pin `$env:PSModulePath` (the guard query does). A same-user attacker could set a
  user-level `ProgramFiles`, plant an `Appx` / `Microsoft.PowerShell.Security` module in Documents (autoloaded first), and
  have a copied, genuinely signed `pwsh.exe` with planted DLLs elevated. **Fix (a deliberate behaviour change of shipped G1,
  made here):** (a) every query that embeds the probe pins the module path FIRST (`MODULES`); (b) the probe prints a fifth tag,
  `pwsh.programFiles` = `[Environment]::GetFolderPath('ProgramFiles')` (the known-folder API, the registry's), the Store shape
  is built from THAT folder, and the environment's `ProgramFiles` must name the same folder (case-insensitive), as
  `msiVerified` already requires — else 5.1 runs. RED first: a spoofed `ProgramFiles` with a matching fake package path.
- **R2 (Major) — the import cycle.** `elevatedEngine.ts` value-imports `powerShellOf`/`quotablePath` from `windowsTimeFix.ts`,
  and D3 needs `windowsTimeFix.ts` to import engine values. **Fix:** the launcher (`QUOTABLE_PATH`, `quotablePath`,
  `encoded`, `outerScript`, `powerShellOf`, `NO_POWERSHELL`, `elevatedRequest`, and the launcher's own exits 0/13/1223 as
  `LAUNCH_EXIT`) moves to `src/powershell/elevatedLauncher.ts`; the graph is launcher ← engine ← {gpu, windowsTime}.
  `windowsTimeFix.ts` keeps its `EXIT` (spreading `LAUNCH_EXIT`, values unchanged).
- **R3 (Major) — no read yet / a heavy re-read.** With D2 the first press before any guard read always refused ("the program
  changed"), and the pre-launch re-read was a whole Task Scheduler query whose faults would block the fix. **Fix — D2 is
  replaced:** a probe-only read in the shared module (`ENGINE_QUERY` = `MODULES` + progress off + `TAG_HELPER` + the probe +
  `exit 0`, unelevated 5.1) behind a small **`EngineHost`** (`src/powershell/engineHost.ts`): `engine()` (the last read's —
  reads first when there is none) and `reread()` (always a NEW read, R6), its timeout the new key
  `wslCare.timeouts.engineProbeSeconds` (default 20, 5–120). The panel's refresh reads it; the Windows Time flows use it;
  *Start Windows Time* no longer depends on the guard host. The guard query goes back to its pre-D2 form (no probe; `guardPwshOf`
  removed). GPU keeps its embedded probe (its re-read also re-reads the adapters); two probes per panel open is accepted (§9).
- **R4 (Major) — the Store package virtualises some registry writes.** Microsoft documents that the Store PowerShell runs in
  an application sandbox whose changes to some registry and filesystem locations do not persist outside it; `REMOVE_SCRIPT`
  deletes the stamp under `HKLM\SOFTWARE\wsl-care` — unmeasured under the Store engine, and the read-back checks only the task.
  **Fix:** the unelevated guard query prints `stamp=present|absent` (5.1, the real registry); the panel line says when the
  stamp is still there with the task gone, and offers *Remove* again. The first Remove under the Store engine is part of the
  acceptance gate. Task Scheduler (CIM/COM) and the SCM (`Set-Service`/`Start-Service`) are APIs, not virtualised.
- **R5 (Minor) — the order after the modal.** **Fix in the Windows Time flows:** modal → re-read → take the time → the busy
  check → write the pending record → launch. G1's shipped order (busy check before its re-read, a window of about one second)
  is recorded as a residual (§9), not changed here.
- **R6 (Minor)** — `reread()` must never be answered by a read already in flight: it awaits that one, then starts its own.
  Pinned by a test.
- **R7 (Minor)** — a re-read that did not answer, and so names a program OTHER than the modal's, says "the read of the
  elevated program right before the launch did not answer (<reason>)" — never "the program changed" — and nothing runs. A
  re-read that did not answer while the modal already named Windows PowerShell 5.1 names that same System32 program, which runs:
  it is exactly what the person confirmed (made exact after the coai code round, below).
- **R8 (Minor)** — the probe is wrapped in `try { … } catch { }` in the shared module, so a probe fault cannot blank the GPU
  state; the engine read reads exit 0 only.
- **R9 (Minor)** — tests: the Windows-leg command lists (done), the probe-only query really RUNS on the Windows leg and answers
  `pwsh.store`/`pwsh.msi`/`pwsh.programFiles`; the test-only `installRequest`/`removeRequest` are deleted and the length bound
  is checked through `elevatedRequest` with the longest (Store) engine path; the tests for R1 and R7.
- **R10 (Minor)** — this status line and §3 were stale against what landed (`TAG_HELPER`, `engineChangedSentence` moved too).
  Fixed here.
- **R11 (Minor)** — the engine and its label are computed ONCE per read (in the host), never in a view; `fileInfo` is a host
  dependency; views stay pure.
- **R12 (Minor)** — `startWindowsTime` would exceed complexity 4: the confirmed half becomes `launchedFix()`, as G1's
  `launched()`.
- **Notes:** pwsh 7 checks for updates at startup (outbound HTTP; the elevated child gets a fresh environment, so
  `POWERSHELL_UPDATECHECK` cannot be set from here) — named in §9; the install script gains `$ProgressPreference =
  'SilentlyContinue'` after `MODULES` and the import.

**Build order from here:** R2 (launcher move) → R1 → R8 → R3 (EngineHost, guard query back, stamp R4) → D3 with R5–R7 and
R12 → D5 → D6 with R11 → docs.

## 12. As built (2026-10-11) — what shipped differently

Built RED first where a defect could be watched (R1's spoofed `ProgramFiles` elevated a planted path before the fix; the
"one resolver" and "no cycle" structure tests failed before their moves); the flows were written before their tests, so each
of those tests was proven by a break-it on PRODUCT code, every one red, then restored: the engine dropped from the fix
request; the guard's re-read replaced by the pre-modal read; an unanswered re-read worded as "changed"; the busy check moved
before the re-read (R5); the import line removed (D4); the probe moved after the Task Scheduler read (D2, before R3 removed
it); `reread()` sharing a read in flight (R6); the page dropping the guard's engine line, the guard host ignoring the last
read, the GPU host naming no engine (D6).

**Deviations from §4:**

- **D2 replaced (§11 R3).** The engine is NOT read by the guard's status query: `src/powershell/engineHost.ts` reads it with
  a probe of its own (`ENGINE_QUERY`), behind `EngineHost`, read by the panel's refresh and by both Windows Time flows. The
  guard query went back to its pre-D2 form, plus `stamp=present|absent` (R4).
- **The launcher moved too (R2):** `src/powershell/elevatedLauncher.ts`; `windowsTimeFix.ts` keeps `EXIT` by spreading
  `LAUNCH_EXIT`.
- **The strict tagged-answer reader moved** from `gpuState.ts` to `src/powershell/taggedAnswer.ts` (reuse-first: the engine
  read needed it), with an "ignored tags" parameter for the GPU's `adapter=` marker. The guard's own first-wins reader is a
  different contract and stayed.
- **Shipped G1 changed on purpose (R1):** the GPU query pins the module path first; the probe prints `pwsh.programFiles`;
  `storePwsh` takes the known-folder Program Files and requires the environment to agree. G1's own changed-engine sentence and
  outcome suffix now come from the shared module (`engineChangedSentence`, `byEngine`).
- **No `checkedEngineOf`:** the "could not be checked" label is made where a read fails (`engineHost.ts`'s `failedRead`), so
  `engineOf` and G1's labels are unchanged.
- **No extension-host scenario** (§6 promised one): the host suite has no Windows Time scenario at all, and the flows are
  pinned by the unit tests through the same request shapes the recorders see (own code review, finding 4). The Test-mode
  engine recorder is held in the extension's parts, not exposed on the test API.
- **`GpuAlert.engine` is optional** (the page renders it only when it is a non-empty string), so the unknown alert and the
  existing fixtures need no field.
- **The engine line before any read** says "checking which PowerShell…" rather than "5.1 until a read verifies PowerShell 7":
  the panel's refresh starts that read at once.
- **The guard's busy refusal before any read** names no engine (`NO_ENGINE`).
- **A drive path in the Test-mode default answer** was caught by the bundle scan (`vsixCheck`): `NO_PWSH_ANSWER` carries the
  two `absent` verdicts and no path.
- **New key:** `wslCare.timeouts.engineProbeSeconds` (default 20, 5–120).

### The code round (coai, 2026-10-11 on the rebased branch: `proceed`, all 4 codex reviewers answered)

| Finding | Decision | Done |
|---|---|---|
| Nit: the Test-mode recorder pushes into its arrays | rejected — the extension's standard Test-mode double (every recorder accumulates in place, read by the scenarios; G1's code round 4 rejected the same) | — |
| Major ×2 (one point): a failed re-read is treated as unchanged when both reads fall back to 5.1 | rejected — the program that runs is exactly the one the modal named and the person confirmed (System32 Windows PowerShell 5.1); the re-read stops an OTHER program; refusing would block the clock fix on a probe failure, against the one fallback policy shared with G1 | §11 R7's wording made exact |
| Minor: a PARTIAL probe answer read as a complete check | accepted | RED first: a probe that printed one verdict, or a signed package without its known folder, now reads "could not be checked: the read printed an incomplete answer"; GREEN; the break-it (incompleteness ignored) red, restored |

### The own code review (Fable, read-only, after the code round: no Critical, no Major, six Minor, eight Notes)

| Finding | Decision | Done |
|---|---|---|
| 1 Minor: the user's persisted environment (`DOTNET_STARTUP_HOOKS`, `COR_PROFILER*`) acts before the module pin | accepted as a residual | §9 — not fixable in a script, the 5.1 road had the same shape |
| 2 Minor: "the environment must agree" was not pinned by a test | accepted | a test with the real package and another `ProgramFiles`; break-it (the agreement dropped) red |
| 3 Minor: duplicated failing-exit and not-answered logic | accepted | `failedExit` (launcher) for both flows; `process/notAnswered.ts` for the GPU, guard and engine queries (words unchanged); `NO_ENGINE` exported; the test helper shared |
| 4 Minor: no extension-host scenario | accepted as a deviation | recorded above; the unit tests pin the same request shapes |
| 5 Minor: the module flowchart was not regenerated | accepted | `module_vs_code.md`: the `powershell/` node and its edges |
| 6 Minor: "every outcome names the engine" overclaimed | accepted | D5 and the module doc say done, failed and timed-out |
| 7 Note: a test attribution, §3 stale, the DoD boxes, two stacked JSDoc blocks | accepted | fixed |
| 8 Note: `engine()` kept a failed read | accepted | it reads again; test + break-it red |
| 9 Note: "printed no answer" when only the known folder was printed | accepted | "no PowerShell 7 verdict" |
| 10 Note: a throwing listener or ceiling could reject | accepted | both guarded; tests + break-its red |
| 11 Note: no progress before the first modal | accepted as a residual | §9 |
| 12 Note: `factFields` returned an untyped record | accepted | typed again |
| 13 Note: missing tests | accepted | a throwing runner and ceiling, `engine()` sharing a read in flight, a timed-out guard run naming its engine, exit 13 on the MSI engine without the Store note |
| 14 Note: two reads may name two versions for one refresh | accepted | said in the module doc |

**Tests:** extension 1134 pass (1 skipped), lint and typecheck clean. One load-sensitive runner timing test (*a child past its
ceiling is reported timed out within the ceiling plus the kill grace*) failed once under the full parallel suite and passed
alone and on the next full run — not touched by this story.

**The acceptance gate, seen 2026-10-11** (POST_DEPLOY item 9; extension 0.5.0 installed, the attested build, 11 files):
under PowerShell 7.6.6.0 from the Store (`C:\Program Files\WindowsApps\Microsoft.PowerShell_7.6.6.0_x64__8wekyb3d8bbwe\pwsh.exe`) the guard's *Remove* succeeded and its *Install* succeeded. The
panel then read "Windows Time guard: installed — not run yet" and "Elevated runs here (Start Windows Time, install / remove
the guard): PowerShell 7 (…7.6.6.0…). The guard's task runs as SYSTEM in Windows PowerShell 5.1." Both questions are
answered: the ScheduledTasks module registers under elevated pwsh 7 (the System32 import works), and no virtualised stamp
was reported after *Remove*.

**Open tail:** *Start Windows Time* under PowerShell 7. It could not be pressed: the service runs Automatic, so the button
is (correctly) hidden. It is pressed the next time the service is found stopped.
