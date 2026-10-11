# PLAN — the Windows Time launcher elevates PowerShell 7 by the same resolver as *Enable <GPU>*, and the panel names each engine

> Status: **plan only, nothing implemented yet (2026-10-11).** Scope: the VS Code extension (`src_vs_code/`) — the engine
> resolver of story G1 moved out of `src/gpu/` into one shared module, the Windows Time launcher's three user-context elevated
> runs (*Start Windows Time*, *Install the Windows Time guard*, *Remove the Windows Time guard*) moved onto it, and one line
> per part in the panel that names the engine. The SYSTEM task stays on Windows PowerShell 5.1. The daemon is not changed.
>
> Related: [PLAN_gpu_health.md](../research/PLAN_gpu_health.md) (the resolver, §11 *The owner's decision … and the path*),
> [PLAN_windows_time_guard.md](../research/PLAN_windows_time_guard.md) (the launcher, D7),
> [PLAN_windows_time_task.md](../research/PLAN_windows_time_task.md) (the guard's task, D3–D8),
> [2026-10-10_gpu_disabled.md](../research/2026-10-10_gpu_disabled.md) (G8, G9), [module_vs_code.md](../research/module_vs_code.md).

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

**The module.** `src/powershell/elevatedEngine.ts` (new folder `powershell/`, the name says what it is about). It imports the
launcher's `powerShellOf` and `quotablePath` from `windowsTimeFix.ts`; `windowsTimeFix.ts` and `guardFlow.ts` import only
**types** from it (`import type`), so no runtime import cycle exists. The GPU files import from it instead of defining.

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
of the three runs ends with " Elevated program: <label>." (as G1's `enableSentence`). A failed exit 13 on the Store engine
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

## 10. Definition of Done

- [ ] One resolver module; the GPU code imports it; the structure test proves one definition.
- [ ] *Start Windows Time*, install and remove elevate `engineOf`'s engine; 5.1 only without a verified PowerShell 7.
- [ ] The engine is read again after the modal; a change runs nothing and writes no pending record.
- [ ] The install imports ScheduledTasks by absolute path; exit 33 has its sentence.
- [ ] Modals and outcomes name the engine; the Store note on exit 13.
- [ ] The panel names each part's engine, as text.
- [ ] The SYSTEM task stays on 5.1; its ASCII test unchanged and green.
- [ ] Tests: unit, page, scenario; break-its red then restored; lint, typecheck, `plan-lifecycle` clean.
- [ ] Docs updated; the plan promoted to `research/` with its as-built record.
