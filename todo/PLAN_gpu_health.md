# PLAN — a GPU that dropped out of Windows is impossible to miss, and a disabled one is one button away (story G1)

> Status: **plan only, nothing implemented yet, 2026-10-10.** Scope: the VS Code extension (`src_vs_code/`) — an
> unelevated read of Windows' display adapters, a per-adapter verdict, a top-of-panel alert, a status-bar mark, and ONE
> elevated action (*Enable <GPU>*, code 22 only) through the existing Windows Time launcher. The daemon is not changed.
>
> Story G1 of the disk-and-memory epic (`todo/PLAN_disk_health.md`, written in parallel on its own branch; the owner moved
> this story FIRST on 2026-10-10 because the card is disabled now and he wants to try the button). Evidence:
> [2026-10-10_gpu_disabled.md](../research/2026-10-10_gpu_disabled.md). Related: [module_vs_code.md](../research/module_vs_code.md),
> [PLAN_windows_time_guard.md](../research/PLAN_windows_time_guard.md) (the elevated launcher this reuses).

## 1. Symptom

On 2026-10-10 the owner's local models did not work and nothing said why. Measured the same afternoon (research G1–G3):

- **The AMD Radeon AI PRO R9700 is disabled** — `Win32_VideoController` `Status Error`, `ConfigManagerErrorCode 22`;
  `Get-PnpDevice` `CM_PROB_DISABLED`, "This device is disabled. (Code 22)". The integrated 890M works (code 0).
- **Ollama silently chose the CPU**: its `server.log` says `msg="inference compute" … library=cpu` at its last start
  (2026-10-10 11:49 local), where on 2026-10-09 it said `library=ROCm … "AMD Radeon AI PRO R9700"`.
- It has happened before (2026-10-08 the card gone, 2026-10-09 back, 2026-08-11 code 31). Code 22 survives a restart, so
  "restart Windows" — the usual advice — does not bring it back; enabling it does.
- Nothing in AI OS Care looks at the GPU at all today. A grep for `Win32_VideoController`, `PnpDevice`, `GPU`, `Ollama`
  over `src_vs_code/src` and `src_daemon/src` (2026-10-10) finds no reader.

## 2. Goal and what must be true when it is done

1. Every display adapter Windows lists is judged: code 0 is fine and says nothing; **any other code is a critical alert at
   the TOP of the panel** — "GPU <name> is not working (code N: <meaning>)" — with the fix in plain words, whether it needs
   a restart, and the consequence ("local models (Ollama) run on the CPU or do not start"), plus Ollama's own last
   *inference compute* line when its log can be read.
2. **The status bar shows it too** (the panel may be closed): a `GPU off` mark and the error colour while any adapter is
   critical — the owner's "impossible to miss".
3. **Code 22 has a button**, *Enable <name>*, in the alert. It ASKS first (a modal that shows the exact elevated script),
   then ONE elevated PowerShell (one UAC prompt) runs `Enable-PnpDevice` for exactly that device, re-reads it, and the panel
   says "enabled — code 0" or "still code N — restart Windows". Nothing else runs elevated; nothing runs on a decline.
4. Every other code gets advice only (no button).
5. No test can start the real elevated launcher or change a device; the tripwire is WIDENED to refuse the device verbs
   (never weakened).

## 3. Why the extension reads it, not the daemon (the decision, and its boundary)

The daemon that computes verdicts runs INSIDE WSL; the Windows binary (`wsl-care.exe`, whose probe is
`src_daemon/src/WslCare.Core/Collectors/WindowsProbe.cs:26-41`) is not reachable from the extension until E7.S5a bundles it
([PLAN_bundle_windows_binary.md](PLAN_bundle_windows_binary.md), plan only). The extension runs on Windows
(`extensionKind: ["ui"]`) and already reads Windows itself where the daemon cannot: the Windows Time guard's state comes from
Task Scheduler through an unelevated PowerShell query (`src/windowsTime/guardScripts.ts:66-80`, `queryRequest` `:103-107`).
G1 follows that precedent exactly.

Two more reasons it must be the extension's own read even after E7.S5a:

- **An administrator's command never takes a value from WSL** (`src/windowsTime/windowsTimeFix.ts:14-16`). The instance id
  the elevated script acts on must come from Windows itself, read by the extension in the same window that elevates.
- The alert is about the Windows host, which has no daemon run records yet.

**The boundary, named on both sides** (`common.planning-docs`): the daemon-side verdict in `status --json` / `doctor`
(so the CLI and the run history carry it) is the epic's later story G2 (`todo/PLAN_disk_health.md`), which also moves the
code → meaning table into `contracts/` with ONE writer so the two sides cannot drift. Until then the table has one home,
`src/gpu/gpuProblems.ts`. The same table stands in `todo/PLAN_disk_health.md` (plan round: a boundary named on one side
only is not one):

| Item | Built by | The other plan's part |
|---|---|---|
| the extension's unelevated adapter read, the alert, the bar mark, *Enable <GPU>* | **G1 (this plan), FIRST** | G2 reuses the read for the button's precondition; it builds no second read |
| the code → meaning table | G1, in `src/gpu/gpuProblems.ts` | G2 moves it into `contracts/` with one writer; G1's tests then compare against it |
| a GPU verdict in `status --json` / `doctor` and the run history | G2 (needs the Windows binary, E7.S5a, and a `NeverList` change) | G1 says nothing in `status` / `doctor` |
| GPU as a sidebar summary row and a Windows-side tab (#87) | the epic's panel stories | G1's alert stays the top element |

Disjoint: G1 writes no daemon code and no contract file; G2 writes no extension read of Windows.

## 4. What exists already (reuse first, verified at `832470d`)

| Capability | Where | G1 uses it for |
|---|---|---|
| ONE elevated run: unelevated 5.1 launcher → `Process.Start` `runas` → inner script as `-EncodedCommand`; 1223 = declined, 13 = launch failed | `src/windowsTime/windowsTimeFix.ts:28` (`EXIT`), `:47-49` (`encoded`), `:60-69` (`outerScript`), `:85-92` (`elevatedRequest`) | the enable — **widened, not copied** (D4) |
| the absolute Windows PowerShell from `SystemRoot`, refused when PowerShell could not quote it | `windowsTimeFix.ts:52` (`QUOTABLE_PATH`), `:75-79` (`powerShellOf`); `src/wsl/wslExecutable.ts:27` | the query and the launcher |
| an unelevated read-only query printing tagged lines, parsed into ONE closed state | `guardScripts.ts:66-80,103-107`; `guardState.ts:40-105` (`tagged`, `parseGuardAnswer`, `guardStateOf`) | the adapter query (D1) |
| a host that queries on panel open / refresh / after its op, one query in flight, a re-run when asked during one | `src/windowsTime/guardHost.ts:40-135` (`WindowsTimeGuardHost`) | `GpuHost` (same shape) |
| a persisted pending elevated run, so a reload shows "waiting…" (`common.durable-status`) | `src/windowsTime/guardPending.ts:12-57` | the enable's in-flight state (D5) |
| Test mode: a RECORDER answers the modal and every run; no PowerShell starts | `src/windowsTime/windowsTimeUi.ts:19-60` | `gpuUi.ts` |
| the tripwire: refuses `wsl.exe`, any PowerShell with `RunAs`, and PowerShell texts with a mutating word (decoded payloads included) | `src/test/support/noRealWsl.ts:36-41` (`isElevatedPowerShell`), `:52` (`MACHINE_TOOLS`), `:59` (`MUTATING`), `:73-84` (`revealed`) | widened with the device verbs (D7) |
| PowerShell's own parser over the scripts, never executing them (Windows leg) | `src/test/windowsTimeFix.test.ts:179-191` | the enable and query scripts |
| the panel's buttons, each posting a closed message; the page builds DOM with `textContent` only | `media/panel.js:50-60` (`header`), `:129-139` (`actionButton`), `:245-249` (`EXTRAS`); `src/panel/messages.ts:23-112` | the alert and its button |
| every contributed command has a button (owner rule 2026-10-09) | `src/panel/commandButtons.ts:19-28`, `commandButtons.test.ts` | `wslCare.enableGpu` |
| the view model and the status bar | `src/panel/view.ts:47` (`PAGE_ACTIONS`), `:90-98` (`PanelView`); `src/panel/viewModel.ts:122-124`; `src/statusBar/statusBarModel.ts:101` (`barView`) | the alert, the bar mark |

## 5. Design

**D1 — the query (unelevated, read-only).** One fixed script run by the absolute Windows PowerShell 5.1, built like
`queryRequest`: `Get-CimInstance -ClassName Win32_VideoController` and, per adapter, tagged lines with an index —
`adapter=<i>`, `name.<i>=`, `id.<i>=` (`PNPDeviceID`), `code.<i>=` (`ConfigManagerErrorCode`), `driver.<i>=`. It prints
`adapters=<n>` last, so a cut-off answer is detectable; an exit ≠ 0, a timeout or a malformed answer is ONE `unknown`
state with its reason, never "no GPU". It changes nothing; a test asserts no word of the (widened) mutating list is in it.
Ceiling: `wslCare.timeouts.gpuQuerySeconds` (default 20, 5–120).

**When it runs.** At activation (so the status bar knows without the panel), on panel open and *Refresh*, after an enable
answered, and every `wslCare.gpu.checkMinutes` (default 15; 0 = only on open / refresh). One query in flight; a request
during one runs one more after it (the guard host's rule). Cost, measured 2026-10-10 (research G5): 0.57–1.46 s wall per query (one outlier of 6.2 s in six runs),
96 a day at 15 min — its CPU time is measured in the build (§7) and stated in the README. The script sets
`$ProgressPreference = 'SilentlyContinue'`: without it 5.1 writes "Preparing modules for first use" as CLIXML on stderr.

**D2 — Ollama's last word (read-only, bounded).** When `%LOCALAPPDATA%\Ollama\server.log` exists, the host reads its last
`wslCare.gpu.ollamaLogTailKiB` (default 256, 16–4096) and takes the LAST line containing `msg="inference compute"`:
`library=` and `description=` (or `name=`) and the line's `time=`. Shown as "Ollama's last start (11:49) chose: CPU" or
"… chose: ROCm on AMD Radeon AI PRO R9700". A missing or unreadable file is no line (and says nothing — Ollama may not be
installed). Text goes through `safeText`; nothing from the log reaches a command.

**D3 — the verdict per adapter (pure, `src/gpu/gpuState.ts`).** Code 0 → `ok`, no alert. Any other code → `critical`
with: the meaning (`src/gpu/gpuProblems.ts`, Microsoft's documented Device Manager codes — 22 "disabled", 31 "Windows
cannot load the driver", 43 "Windows stopped it because it reported problems", 10 "cannot start", 28 "no driver
installed", 45 "not connected", … and "code N" for an unlisted one), the fix kind, and whether it needs a restart:

| Fix kind | Codes | Words | Button |
|---|---|---|---|
| `enable` | 22 | "It is disabled. Enable it — no restart needed." | *Enable <name>* |
| `reconnect` | 45 (not connected), 47 (prepared for safe removal) | "Windows does not see the card now. Check that it is seated and powered (an external GPU: its cable and power); then restart Windows." | none |
| `driver` | 18, 28, 39, 48, 52 (driver missing, corrupt, blocked or unsigned) | "Reinstall the graphics driver, then restart Windows." | none |
| `restart` | 10, 14, 21, 31, 43, 44, 54 and the other listed codes | "Restart Windows. If it is still not working after a restart, reinstall the graphics driver." | none |
| `unknown` | unlisted | "Restart Windows; if it stays, Device Manager shows what code N means for this device." | none |

Each code's row (meaning, fix kind, restart needed) lives once in `gpuProblems.ts`; the tests iterate it (a plan-round finding:
code 45 got its own advice instead of the generic restart-and-reinstall).

The consequence line is the same for every non-working adapter: "Programs that use this GPU — local models such as Ollama
— run on the CPU or do not start", followed by Ollama's line from D2 when there is one. The alert's level is never only a
colour: an icon character and the words "not working" carry it (WCAG 1.4.1).

**D4 — the enable (`src/gpu/gpuEnable.ts`).** The launcher is REUSED: `elevatedRequest` (`windowsTimeFix.ts:85-92`) gains
an optional `elevated` argument naming the PowerShell that runs ELEVATED (today the same 5.1 as the launcher, unchanged for
the Windows Time callers). The enable passes `%ProgramFiles%\PowerShell\7\pwsh.exe` when that file exists (an
administrator-only folder; the Store `pwsh` under `WindowsApps` is NOT used — it is an app-execution alias, research G4),
else the 5.1 one; both must pass `QUOTABLE_PATH`. The inner script, a pure function of ONE validated instance id:

```powershell
$env:PSModulePath = "$PSHOME\Modules"; $ProgressPreference = 'SilentlyContinue'
Import-Module -Name (Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\Modules\PnpDevice') -ErrorAction Stop  # exit 40 on failure
$id = '<instance id>'
$d = Get-PnpDevice -InstanceId $id -ErrorAction Stop                  # absent → exit 41
if ($d.Class -ne 'Display') { exit 42 }                               # not a display adapter → nothing done
if ([int]$d.ConfigManagerErrorCode -ne 22) { exit 43 }                # no longer disabled → nothing done
try { Enable-PnpDevice -InstanceId $id -Confirm:$false -ErrorAction Stop } catch { exit 44 }
Start-Sleep -Seconds 3
exit 0
```

(`PnpDevice` by absolute path because `$PSHOME\Modules` does not hold it under `pwsh` 7, and the module path is cut to
`$PSHOME` so a user's Documents folder supplies nothing — the guard's rule, `windowsTimeFix.ts:15-16`.)

- **The id is safe by construction**: it is the id the extension's OWN query just read (D1), selected on the host by the
  adapter index the page posts — the page never sends an id. It must match `^[A-Za-z0-9\\&_.#{}-]{1,200}$` (no quote, no
  `$`, no backtick, no whitespace) or no request is built; it travels inside a single-quoted literal of the inner script,
  which travels as `-EncodedCommand`, so no quoting layer exists between the two.
- **After any exit** the host runs the D1 query again and words the outcome from WHAT WINDOWS NOW SAYS: code 0 → "<name>
  is enabled — code 0. Start Ollama again so it finds the GPU."; still 22 → "still disabled (code 22)"; another code N →
  "enabled, but still not working (code N: …) — restart Windows"; 1223 → "Nothing was changed: the UAC prompt was
  declined."; 41/42/43 → "nothing was done: <why>"; 40/44/13 → the failure's sentence.
- Ceiling `wslCare.timeouts.gpuEnableSeconds` (default 120, 30–600 — the UAC prompt waits for the person).

**D5 — durable in-flight state.** Before the launcher starts, the host persists `{ op: 'enable', instanceId, sinceUtc }`
in `globalState` (`guardPending.ts`'s shape); while it stands the alert says "Waiting for the elevated PowerShell that
enables <name> (answer the UAC prompt)…" and the button is disabled; it is cleared when the run answers, when the query
shows the device no longer at 22, or after the enable ceiling + 60 s (a reload mid-run never sticks on "waiting").

**D6 — the panel and the bar.** `PanelView` gains `gpu: GpuView` (`{ alerts: [{ line, consequence, ollama, level,
button? }], checkedLine }`). `media/panel.js` renders the alerts as the FIRST element of the panel, ABOVE the heading, each in a box with
`role="alert"` created once per distinct alert text (so it is announced once, not on every render), its button through
`actionButton` posting `{ type: 'enableGpu', index }` — `messages.ts` accepts an integer index below the adapter count
cap (8) and nothing else. When all adapters are fine the panel shows one muted line in *Health*: "GPU: AMD Radeon 890M,
AMD Radeon AI PRO R9700 — working (checked 14:31)". The status bar's text gains `· GPU off` and its error background while
any adapter is critical; its tooltip names the adapter and code.

**A query that did not answer is visible too** (plan round): the `unknown` state is its own panel alert at the top, level
`unknown` — "GPU state could not be checked: <reason>" — and the bar shows `· GPU ?` with the warning background and the
reason in its tooltip, so a closed panel never reads a failed check as "fine". On a host that is not Windows (VS Code on a
Linux desktop) the GPU part is absent rather than unknown — there is nothing of Windows to read.

**D7 — the command and the tripwire.** `wslCare.enableGpu` (*AI OS Care: Enable a disabled GPU…*) duplicates the button
(a QuickPick when several adapters are at 22; "no GPU is disabled" otherwise); `COMMAND_BUTTONS` maps it to the page
action. The tripwire's `MUTATING` gains `(?:Enable|Disable)-PnpDevice` and `MACHINE_TOOLS` gains `pnputil`/`pnputil.exe`,
with their own `noRealWsl.test.ts` cases — a widening; nothing is removed or loosened.

## 6. Build order

1. Tests RED: `gpuState` (parse the measured answer into two adapters; a cut answer / exit / timeout → `unknown`; the
   verdict table — derived from `gpuProblems.ts`, never retyped), `gpuEnable` (the script for the measured id; ids that
   must be refused — a quote, `$`, a backtick, whitespace, 201 characters; the `pwsh` choice; outcome wording per exit and
   per re-read code), `ollamaLog` (the measured `library=cpu` and `library=ROCm` lines; a tail cut mid-line; no file),
   the tripwire's new words, the page (strict DOM harness `panelPage.test.ts`: alert first, `role="alert"`, the button
   posts only `{type, index}`), the message parser, the status bar.
2. Widen `elevatedRequest`; `src/gpu/*` (problems, state, query script, enable, ollama log, host, ui); wire into
   `extension.ts`, `view.ts`, `viewModel.ts`, `messages.ts`, `panel.js`, `panel.css`, `statusBarModel.ts`,
   `commandButtons.ts`, `package.json` (command + three settings).
3. Break-it checks on PRODUCT code only (§7).
4. Docs: README *Extension*, `src_vs_code/README.md` settings, `research/module_vs_code.md`, `architecture.md` (one
   paragraph: the extension's second Windows read and second elevated action), this plan's as-built; promote it.

## 7. Test plan

- Unit (`npm test`, both CI legs): everything in step 1; the parse check of the query, enable and launcher scripts by
  PowerShell's own parser (Windows leg; `windowsTimeFix.test.ts:179` pattern) — never executed.
- Host (`npm run test:host`): the command in Test mode records the modal and the request; a decline records no request;
  the recorded request is refused by the tripwire if run (the elevation and the device word both).
- Break-it (each reverted after): drop the index check in `messages.ts` → the page test goes red; let the id pattern accept
  `'` → the refusal test goes red; skip the post-run re-query → the outcome test goes red; remove the `role="alert"` → the
  page test goes red; render the alert after the sections → red.
- Measured once, read-only, on this machine: the D1 query's wall time and CPU (`Measure-Command` over 10 runs), recorded in
  the research file. **The real enable is NOT run by the implementer** — the owner presses the button himself after the
  release.

## 8. Growth surfaces

None that grows: one `globalState` key (one record, cleared on answer or after its ceiling), no files written, the log read
is bounded by `ollamaLogTailKiB`.

## 9. Residuals (said, not solved)

- The `pwsh` 7 branch of D4 (plan round, finding 1): its READ half — the module path cut, `PnpDevice` imported by path,
  `Get-PnpDevice`, `Class`, `[int]` code — was run unelevated under `pwsh` 7.6.4 and 5.1 and answered identically
  (research G6); `Enable-PnpDevice` is the same `PnpDevice` cmdlet under both. The MSI `pwsh` path itself is not installed
  here, so its selection is unit-tested and its elevated run is not; the elevated run of either edition is first exercised
  by the owner's press.
- Who disables the card is not found (research G3); G1 shows it, it does not prevent it.
- `%ProgramFiles%` and `%SystemRoot%` come from the environment, as the Windows Time launcher's do; the UAC prompt shows
  the program it elevates.
- The daemon's own verdict (G2) and the contract table come later; until then `doctor` / `status --json` say nothing
  about the GPU.

## 10. Definition of Done

- [ ] Every adapter judged; non-zero code → a critical alert at the top of the panel, with meaning, fix, restart need,
      consequence, and Ollama's last line when readable; the status bar marks it.
- [ ] *Enable <name>* only for code 22; modal first; one elevated run; outcome worded from the re-read code.
- [ ] The id is the extension's own reading, validated, inside an encoded script; the page posts only an index.
- [ ] Durable in-flight state; never stuck on "waiting".
- [ ] Tripwire widened with the device verbs; no test reaches the real launcher.
- [ ] Every new number is a setting; the command has its button (`commandButtons.test.ts`).
- [ ] Tests red first, green after; break-its observed; `npm test`, lint, typecheck, `check:vsix`, family checks green.
- [ ] README, `module_vs_code.md`, `architecture.md`, the research file updated; the plan promoted with its deviations.

## 11. As built (2026-10-10) — what shipped differently

- **The alert sits ABOVE the heading**, as the very first element of the panel (the plan said "under the heading"): the
  owner's "impossible to miss" read as above everything, the Refresh / Settings bar included. The alerts live in ONE
  persistent `role="alert"` container whose children are rebuilt only when the alerts' words change, so a screen reader
  announces a new alert once and not on every render (the notice's rule, `media/panel.js`).
- **The code table names 28 documented codes**, not the plan's illustrative eight; each has a fix kind — `enable` (22),
  `reconnect` (24, 41, 45, 47), `driver` (1, 18, 19, 28, 32, 37, 39, 40, 48, 52) or `restart` (the rest) — and an unlisted
  code reads `unknown` with its number.
- **The pending-run rule was extracted, not copied** (`common.reuse-first` 2.2): `src/state/pendingRun.ts` holds the
  deadline rule; the Windows Time guard's `guardPending.ts` and the new `gpuPending.ts` keep their own keys and record
  shapes over it. `elevatedRequest` was WIDENED with an optional `elevated` PowerShell (its default unchanged for the
  Windows Time callers), and an unquotable elevated path refuses the request.
- **The recorder lives in `gpuRecorder.ts`** (no `vscode` import) so the unit suite loads it — the guard's
  `guardRecorder.ts` shape.
- **The command is wired in `gpu/gpuWiring.ts`** (`gpuParts`, `gpuPanelActions`, `gpuRegistrations`), so `extension.ts`
  gained eight lines; its `wire()` kept under the 50-line lint bound by moving the Logs page's three registrations into
  `logsRegistrations`.
- **Measured cost** (research G7): CPU 375–656 ms of one core per read, ≈ 45 s a day at the default 15 minutes.
- **Tests**: `gpu.test.ts` (29, one of them the PowerShell parser check on the Windows leg), four new `panelPage.test.ts`
  cases, one `noRealWsl.test.ts` case (the device verbs), `commandButtons.test.ts` and `manifest.test.ts` widened, and one
  extension-host scenario (the bar mark, the declined and the confirmed enable in Test mode, on 1.85.0 and stable).

### The code round (coai, 2026-10-10: `proceed`, 4 of 12 reviewers — codex's four roles; local had no model, gemini out of quota)

Seven findings, all accepted, each fixed test-first (RED observed against a stub keeping the old behaviour, then GREEN):

| Finding | Fix |
|---|---|
| the Ollama log reader swallowed every read error | `readTail` answers `text` / `absent` (ENOENT, ENOTDIR only) / `unreadable` with its code; the HOST carries `unreadable` to the alert ("Ollama's log could not be read (EACCES)") — a second RED caught the host dropping it |
| the adapter cap silently hid every adapter after the eighth (three findings) | every reported adapter up to `MAX_ADAPTERS` (now 32) is judged; an answer naming more is an explicit `unknown` ("Windows reported N display adapters; this extension reads at most 32"), never a partial read shown as complete |
| the palette said "No GPU is disabled" after a failed check | `noChoiceMessage`: a check that could not run says why |
| `Start-Sleep -Seconds 3` was a literal in the elevated script | `wslCare.gpu.enableSettleSeconds` (default 3, 0–60), clamped to an integer before it reaches the script |
| a test fixture cast to `Snapshot` | a typed `GOLDEN_SNAPSHOT` (the neighbouring fixtures in `commandButtons.test.ts` still cast — named, not rewritten) |

### The own review (Fable, the elevated path, 2026-10-10: no Critical, no Major, six Minor)

| Finding | Done |
|---|---|
| M1: the elevated program's path and the inner script's System32 come from the environment | the modal now names the elevated PROGRAM; the inner script imports `PnpDevice` under `[Environment]::SystemDirectory` (re-verified under both editions, research G6). `%ProgramFiles%` / `%SystemRoot%` for the program path stay the accepted residual of the Windows Time launcher (§9); the same `SystemDirectory` change would harden `windowsTimeFix.ts:36` — proposed, not done here |
| M2: "Waiting…" could stay on screen past the pending deadline | the host schedules one re-render AT the deadline (`after`, cleared on dispose) |
| M3: the palette was silent while an enable stood | it says an enable is already waiting |
| M4: "the panel is read again" without a read | a press on an adapter no longer at 22 reads Windows again |
| M5: the `role="alert"` container is re-inserted on every render | not changed: the same pattern as the notice's live region, which the harness cannot judge either; recorded for the epic's webview work (#87) |
| M6: a name with a newline could shadow its id | the query prints the NAME last, so the parser's first-wins keeps the real id and code |
| (note) a NaN settle value | `wholeSeconds` reads NaN as 0 |
