# PLAN — the disk, the memory behind it, the measured restart advice, statistics kept forever, and the sidebar / full-view split (#87)

> Status: **in progress, 2026-10-12 — E15.S1 merged (#99, *E15.S1 as built*); E15.S2 merged (#111, *E15.S2 as
> built*); E15.S2b merged (#126, *E15.S2b as built*); E15.S5 merged (#150, *E15.S5 as built*); E15.S1b built on its branch `feat/wc-e15s1b-msi-pwsh` (*E15.S1b as built* at the end); E15.S7 built on its branch `feat/wc-e15s7-redaction` (draft PR #161, 2026-10-17; *E15.S7 as built* at the end); the owner's answers to Q1–Q6 recorded (§9); E15.S3, E15.S4
> and E15.S6 onwards (E15.S7 aside) not started; E18 waits for the owner's go.** Scope: four epics, E15–E18 — the daemon's read-only disk

> built*); E15.S2b merged (#126, *E15.S2b as built*); E15.S1b built on its branch `feat/wc-e15s1b-msi-pwsh` (*E15.S1b as built* at the end); the owner's answers to Q1–Q6 recorded (§9); E15.S3 onwards
> not started; E18 waits for the owner's go.** Scope: four epics, E15–E18 — the daemon's read-only disk
> and memory report with CPU attribution by Claude session (E15), a responsiveness probe with a measured restart forecast
> and the remedy ladder (E16), statistics kept forever on a share (E17), and the shared webview kit, the full view and the
> slim sidebar of GitHub issue #87 (E18). Each story is one pull request. Story G1 (the GPU alert) is NOT here: it is built
> separately (`research/PLAN_gpu_health.md`, built on branch `feat/wc-gpu-health`); its daemon half G2 is E15.S8.
>
> Evidence: [2026-10-10_disk.md](../research/2026-10-10_disk.md) (D1–D9). Related: [module_daemon.md](../research/module_daemon.md),
> [module_vs_code.md](../research/module_vs_code.md), [module_mcp_servers.md](../research/module_mcp_servers.md),
> [architecture.md](../research/architecture.md), [PLAN_twenty_sessions_all_day.md](PLAN_twenty_sessions_all_day.md) (S7b),
> [PLAN_bundle_windows_binary.md](../research/PLAN_bundle_windows_binary.md) (E7.S5a), [PLAN_shared_vscode_kit.md](PLAN_shared_vscode_kit.md).
> Closes #87 only from the PR that ships its last item (E18.W5).

## 1. Symptom

The owner, 2026-10-10: *"separately check what is going on with the disk"*, then *"all of this must be COLLECTED and SHOWN
— and FIND A WAY TO FIGHT IT. Even simply proposing a restart … but it must be MEASURED: if you restart now, the next 4
hours will run 2 or 5 times faster."* Measured the same day (D1–D9):

| What | Measured | What the product says today |
|---|---|---|
| Ubuntu's VHDX holds 43.7 GiB its guest does not use | host file 237.7 GiB, guest `/` 194.0 GiB used, not sparse, WSL 2.7.10 offers `--set-sparse` (D2, D3) | `host.vhdx`: "not collected" (`Status/StatusReports.cs:16`, `:127`) |
| D: (the repositories) is 84 % full; every volume is a partition of ONE NVMe | D1 | nothing — no verdict judges a Windows volume (the Windows binary reads only C:, `Collectors/WindowsProbe.cs:34-41`) |
| Windows commit 126.6 / 145.6 GiB (86 %), Memory Compression 15.6 GiB | D5; the 0x154 bugcheck of 2026-10-07 was a compressed page lost on this NVMe | nothing |
| CPU at 100 %: one Claude session's `npm test` ran 23 `node` children | D7 (`claude.exe` 49140, `--resume=537542bd…`) | nothing names the session |
| 12 GiB read from the virtual disks in 5 minutes at boot | D3 (Docker's database start-up) | invisible |
| NVMe health readable; its reliability counters need elevation | D4; 64 × `disk` 11 are the USB keyboard (Harddisk1), not the NVMe | nothing |
| AI OS Care's own cost: a collect took 1 min 55 s of CPU; a run peaked at 831 MB of its 1 GiB | D8 | nothing |
| The sidebar shows "0.0 GiB" held for every process, wraps PIDs, shows full command lines (a creds config key was seen) and "arrives in E6/E7" rows | issue #87's screenshot; `src_vs_code/src/panel/rowRenderers.ts:37` (`gib()`), `fieldMap.ts:107-108`, `:129-131`, `:140-141` | — |

## 2. Goal — what must be true when the epic is done

1. **Collected.** Every figure above is read by the product, read-only, each with its basis (`sample` / `fullRun` + age)
   and an honest `unavailable — <reason>` where it cannot be read; every threshold is a configuration key.
2. **Judged in ONE place.** Every verdict, the ladder's recommendation and its sentence are C# functions in `WslCare.Core`,
   compiled into both binaries; the extension renders `level` / `value` / `limit` / `reason` / `text` and holds no threshold
   (#87 item 3).
3. **Fought, cheapest first, with measured effects.** A remedy ladder (A19 → A11 / A18 → A1 → A2 → heavy-container advice →
   *Shut down WSL…* (an extension button that asks first) → restart Windows (advice)); each rung's last MEASURED effect from
   the run history; ONE top recommendation ("memory.available reached its limit (18 % < 25 %) → I recommend *Stop idle MCP
   servers* (last time it freed 0.6 GiB)") or "all within limits".
4. **The restart advice is measured.** A responsiveness probe, a post-boot baseline, a factor N, a horizon K from previous
   boots, the count M — and, until 7 days and 2 boots of history exist, exactly *"a minimum forecast needs at least 7 days of
   history; N days so far"* and NO factor (the owner, 2026-10-10; 7 is a key). Predicted vs realised is recorded at every
   boot.
5. **Kept forever.** Statistics (runs, verdicts, actions, recommendations, probe samples, forecasts, boots, self-cost) in an
   append-only store, local first, copied to the owner's share (`V:\AiOsCare`), every record carrying the machine id, the
   Windows edition and build, and the Windows install date; daily and monthly rollups; JSON export always redacted.
6. **Shown where it belongs.** The sidebar keeps the parameters that justify a button (§3.5) and the one recommendation;
   everything else is a full webview view built on a shared kit with a Storybook; tabs per side; sortable, searchable tables;
   arguments hidden by default and redacted in exports; Help in the coai design and five languages.
7. **AI OS Care costs less than it saves.** Per-unit CPU, memory peak, disk written, and the extension's own `wsl.exe` calls,
   judged against a budget.

## 3. The architectural spine (decided here, because every story depends on it)

### 3.1 Who reads which fact, who judges it — "two binaries, ONE rule set, three roads"

| Windows-side fact | Road | Freshness |
|---|---|---|
| VHDX files (path, size, sparse, last write) and the distro ↔ file map (`HKCU\…\Lxss`), Windows volumes with their disk number, physical disk status, the System log's disk events BY DISK NUMBER, the reliability counters' refusal, the machine identity (MachineGuid, edition, build, install date), commit / compression lines, the display adapters' codes (G2's distro half) | **Road 1 — the WSL daemon's full run, through a SECOND fixed interop PowerShell script** (`HealthCommands.WindowsDisk`, tagged lines, run only by `collect`, like the clock probe `Health/HealthCommands.cs:34-50`, `Health/HealthCollector.cs:173-203`) | every full run (the 4-h timer), carried into `status` with basis `fullRun` + age (`Status/StatusVerdicts.cs:47-59`) |
| commit / limit (the Windows binary's `MEMORYSTATUSEX` already has `TotalPageFile` / `AvailablePageFile`, unused — `Collectors/WindowsProbe.cs:84-97`), Memory Compression, vmmem, CPU by Claude session, top I/O processes, the Windows probe | **Road 2 — the Windows binary** (`wsl-care.exe status --json`, `probe`) | the poll's cadence — once E7.S5a lets the extension start it; the periodic Windows probe once S7b's runner exists |
| the GPU alert (G1), the Windows Time guard, the Defender checkbox's state (E16.S5) | **Road 3 — the extension's own unelevated PowerShell** (existing) | widened ONLY by E16.S5: an unelevated replay of event 5007, and its one elevated change behind a UAC prompt (the GPU enable's launcher) |

The same rule judges a fact on both roads (`HostRules.Commit(...)`, one function); the wire's `Verdict.Basis` says which.
**The cost — a never-list change:** `NeverList.IsForeignPowerShell` (`Processes/Policy/NeverList.cs:79-81`) admits today
exactly the clock probe's argv; it becomes "any PowerShell argv not equal to one of the CLOSED set
`HealthCommands.FixedScripts` (`WindowsClock`, `WindowsDisk`)", with a RED test that a third script, the clock script with
one word changed, and `-File` are still refused. `wsl --shutdown` and `--set-sparse` stay refused for the daemon
(`NeverList.cs:46`, `:49`, `:138-139`). **PowerShell (the owner, 2026-10-10):** user-context launches use `pwsh` 7 (the
Store build, through the user's alias), falling back to 5.1; the SYSTEM task stays on 5.1. **The daemon's root interop scripts (decided 2026-10-11, Q3):** the MSI `pwsh` at the known folder
`%ProgramFiles%\PowerShell\7\pwsh.exe` when it is present, otherwise Windows PowerShell 5.1. It is checked by the rules of
`src_vs_code/src/powershell/elevatedEngine.ts`:
- Program Files is the KNOWN-FOLDER path, never the environment's `ProgramFiles`, and the program is refused when the
  environment names a different folder.
- The file is real, with no link type.
- `Get-AuthenticodeSignature` reads Valid from `CN=Microsoft Corporation`.
- Every interop script pins `$env:PSModulePath` first.

(The coordinator, 2026-10-11, after the #104 Fable review. The module-path pin already shipped for both existing scripts, in
the `fix/wc-interop-module-path` follow-up.) The daemon never resolves the Store package: its cost and its path churn buy nothing for fixed, read-only
ASCII scripts. (The Store package's own folder under `%ProgramFiles%\WindowsApps` is TrustedInstaller-only; only the
per-user alias, `%LOCALAPPDATA%\Microsoft\WindowsApps\pwsh.exe`, is user-writable.) Story E15.S1b builds it; until then both
scripts run under 5.1 (E15.S1's deviation 1). The never-list judges the program AND the script.

Rejected: *everything through the Windows binary* (blocks the report on E7.S5a, an unbuilt plan of another agent, and the
binary has no WMI under Native AOT for the disk status and the event log); *the extension reads and judges in TypeScript*
(#87 item 3; and nothing would reach the run history); *the interop script in the 5-minute watch* (288 PowerShell starts a
day; `status` must start no process, `Cli/Commands/StatusCommand.cs:21-22`).

### 3.2 The statistics store — append-only JSON Lines, local first, one writer per machine

**No SQLite**: SQLite over SMB with several writers is unsafe, so the share's format must be files anyway; `WslCare.Core`
takes no package (`tests/WslCare.Core.Tests/ArchitectureTests.cs:99`, `Directory.Packages.props:7-11`) and a native
`e_sqlite3` per RID would enter the AOT publish; the atomic-write, append-lock, retention and SMB seams already exist
(`Records/RunRecordWriter.cs:27-31`, `Files/NetworkPaths.cs:26-41`, `:63-79`, `Archive/SideLease.cs:30-35`).

- **The single writer per machine is the distro's ROOT daemon** (`collect` and `watch`, serialised by `/run/wsl-care.lock`).
  The Windows binary writes only a local SPOOL (`%LOCALAPPDATA%\wsl-care\stats-spool\<day>.jsonl`), merged by the root
  daemon through drvfs; the Windows side never touches the share. The extension writes nothing to the store; its own cost is
  derived from the run-log files every unprivileged `status` already writes.
- **Records** (one JSON line each: `v` schema, `t` UTC, `m` MachineGuid, `wb` Windows build, `wi` install date, `side`,
  `kind`, payload): `run` (~3 KB), `act` (~1 KB), `probe` (~220 B), `forecast` (~400 B), `boot` (predicted vs realised),
  `remedy` (a rung's measured effect).
- **Layout.** Local, authoritative: `/var/lib/wsl-care/stats/raw/<side>/<yyyy-MM>/<dd>.jsonl`, `rollups/daily/<day>.json`,
  `rollups/monthly/<yyyy-MM>.json`, `index.json`. Share: `<stats.baseFolder>\<MachineGuid>\machine.json`, the same tree
  plus `<dd>.sha256` (digest + line count). A year reads 12 monthly rollups, a week 7 daily ones — never thousands of files.
- **The flush is a whole-file copy, never an append over SMB:** each day file changed since the last confirmed flush is
  written to the share as temp + rename, read back and compared with its digest (the archive's pattern), skipped when the
  share's drive is not mounted or the Offline Files guard refuses, bounded by `stats.flushTimeoutSeconds`, run LAST in a run.
  `index.json` lists the pending days; a doctor check and a verdict warn past `stats.pendingWarnDays`.
- **The archive's guarantees do NOT transfer to drvfs as they are** (the cadence consultation, verified 2026-10-10): the
  Offline Files guard exists only on the Windows path (`Files/PhysicalFileSystem.Archive.Windows.cs:244-248`, `TrustedHash`);
  the Linux read-back opens and hashes synchronously with no such guard and no cancellation
  (`Files/PhysicalFileSystem.Archive.cs:464-470`). So E17.S2 starts with a **transport proof** before any pruning is enabled:
  the share I/O runs in a bounded, isolated worker (its own process, so a blocked drvfs call cannot hold the run lock past
  `stats.flushTimeoutSeconds` — cancelling a wait is not stopping a write), tracked as a survivor the next run waits out
  (the archive's `Archive/ArchiveChildren.cs` `Survivor` shape) and FENCED: each flush writes temp files named with its own
  generation id and renames only while that generation is still the newest, so an old worker that resumes cannot replace a
  newer day file. A read-back — even from a later process — can be answered by the client's cache, so it is NOT proof of
  server-backed storage: **local pruning stays OFF** (`stats.pruneConfirmedDays`, default false) — the local store is
  authoritative and bounded by the backlog cap below; the share is a verified-as-far-as-possible copy. The interruption tests
  (connectivity cut during open, write, rename and read-back on a disposable share; a worker that outlives its timeout) are
  E17.S2's acceptance, and turning pruning on is a later decision with its own measurement.
- **Never lose a record:** local append first; a local raw day is pruned only after `stats.localRetentionDays` AND a
  confirmed share copy (or with no share set, never below the key). Rollups are kept forever everywhere; the share keeps
  everything forever (the owner).
- **The local backlog is bounded, and reaching the bound is loud, never silent** (plan round): when the share stays offline,
  local raw days cannot be pruned, so `stats.localBacklogMaxMb` (64–65 536, default 1 024 — about 30 years of this machine's
  records at the budget below) caps them. At `stats.localBacklogWarnPercent` (80) of it the verdict `stats.backlog` warns
  and the panel says the share has not confirmed a copy since <day>; AT the cap the writer keeps every `run`, `act`,
  `forecast`, `boot` and `remedy` record and stops keeping individual `probe` samples (the only high-volume kind,
  ~288 a day) — their daily rollup still counts them, each such day carries `probesNotKept: N` and the verdict turns
  critical. Nothing is dropped without a count, and the run is never blocked.
- **Schema and migrations:** every record and day file carries `v`; raw files are never rewritten; a reader counts newer
  records as "not read"; `wsl-care stats rebuild` regenerates rollups. **Integrity:** `wsl-care stats check` and a doctor
  check parse every day line by line (a bad line is counted, never fatal) and compare digests.
- **Growth budget** (from the cadences: 6 full runs a day `src_daemon/systemd/wsl-care.timer:21`, 288 watch runs
  `wsl-care-watch.timer:16-17`; the extension's ≤ 721 `status` calls a day are NOT records): ≈ 95 KB a day per side ≈ 35 MB a
  year; with the Windows side ≈ 70 MB a year; ten years ≈ 0.7 GB on the share. Local raw at the 400-day default ≈ 40 MB a side.
  A test holds the per-day size under a ceiling over the cadences.
- **Fresh install asks where:** *Choose statistics folder…* (the archive's dialog → `stats check-base <path>` → `config set
  stats.baseFolder`; `src_vs_code/src/config/configCall.ts:40-55` widened from one key to a closed set of two); declined =
  local only, said in the panel.

### 3.3 The responsiveness probe and the forecast

- **Components** (fixed work, so ≤ 1 s of one core by construction, measured per sample): (1) spawn latency — the product's
  own binary with `--version`; (2) small-file latency — open + read + close of `probe.smallFiles` (64) files of
  `probe.smallFileBytes` (4096) created once under `{state}/probe/files/`; (3) a CPU + memory micro-benchmark — SHA-256 over
  `probe.hashMiB` (32) and touching every page of a fresh `probe.touchMiB` (64) allocation (page-fault latency is what memory
  compression and the VM's ballooning degrade). `git status` is not a component: it measures (1) + (2) again and would add a
  `git` dependency (stated assumption). A sample over `probe.maxMilliseconds` (1000) is kept, flagged, and warns.
- **Never disturbs sessions:** a sample is taken only when `MachineBusy.Judge` says calm (`Thresholds/MachineBusy.cs:51`, the
  existing PSI keys); otherwise it is recorded as `skipped: busy (…)`. Windows: calm = CPU busy share under
  `probe.windowsCalmCpuPercent` (50).
- **Where it runs:** the distro — a step of `watch --timer` (every 5 min, under the run lock, `Watch/WatchRun.cs:68-76`) and
  of every full run, plus `wsl-care probe [--json]`; Windows — the same implementation in `wsl-care.exe probe`, run periodically
  only once S7b's runner exists (until then the Windows rung says so).
- **Recorded per sample:** side, boot id, uptime, both clocks, each component's ms, total, calm / skipped and why, PSI
  cpu / io / memory, MemAvailable %, swap, `Committed_AS` (distro); commit %, compression GiB, vmmem GiB, CPU busy % (Windows;
  and, at 4-h resolution, from Road 1's lines in every full run).
- **Baseline** (per side, per boot id): the median total of the first `probe.baselineSamples` (6) CALM samples after
  `timer.bootDelayMinutes` (15) and within `probe.baselineWindowMinutes` (60); fewer than 3 → "no baseline for this boot".
- **N** = median of the last `probe.nowSamples` (3) calm samples ÷ the baseline; N < 1.1 → "no slowdown measured since boot".
- **K** = the Kaplan–Meier median, over the last `probe.maxBootsUsed` (10) boots with a baseline, of the hours until the factor
  stayed ≥ `probe.degradedFactorPercent` (150) for `probe.degradedSamples` (3) calm samples. A boot is censored at its LAST
  adequately observed calm sample — a gap over `probe.maxGapMinutes` (30) without a calm sample ends its observation, so a
  busy afternoon is not counted as healthy hours. When the median is not reached the text says so with its coverage: *"median
  degradation time not reached; 1 of 4 boots observed degrading, observed for 1–10 h"*. **M** = the boots used, with how many
  were observed degrading. (Calm-only sampling and restarts prompted by poor performance can make censoring depend on
  degradation; the estimate is a product gate, not a statistical guarantee — said in the research note.)
- **The minimum (the owner's rule):** while the history spans < `probe.minHistoryDays` (7; 1–90) days OR fewer than
  `probe.minBoots` (2) boots have a baseline, there is NO factor and the text is exactly *"a minimum forecast needs at least 7
  days of history; N days so far"* (7 = the key's value); the verdict `probe.factor` is `unknown` with that reason.
- **The sentence** (daemon-made, shown verbatim): descriptive by default — *"local steps now take 2.3× as long as after the
  last boot (412 ms vs 180 ms)"*; the predictive form (*"a restart is expected to make local steps ~2.3× faster for about the
  next 9 hours (based on 4 boots)"*) only under the calibration gate below. Per side: `wsl --shutdown` for the VM; for the host
  (a Windows restart, advice only, never `advised`) the sentence stays descriptive.
- **Validation (falsifiable) — shadow predictions:** every *Shut down WSL…* press records, BEFORE the restart and immutably, a
  shadow prediction (N, K) computed by the same function whether or not it is shown; at the next boot the realised N′ = last
  calm score before ÷ the new baseline and K′ = the new boot's degradation hour (or `unresolved` while censored) are scored
  against it. The Statistics tab shows predicted vs realised per advised boot. A censored K′ is unresolved evidence, never a
  success.
- **Only a boot that FOLLOWED the advice validates it** (plan round). Each `boot` record carries its cause: `advised` — the
  extension's *Shut down WSL…* ran after showing the forecast: before it starts `wsl.exe --shutdown` it calls the new
  unprivileged verb `wsl-care probe mark-restart --forecast <id of the shown forecast>`, which writes
  `$XDG_STATE_HOME/wsl-care/restart-marker.json` (time, the forecast's id), read and consumed by the root daemon on the next
  boot — or `other` (no fresh marker: a crash, an update, `wsl --shutdown` typed by hand, a Windows restart, which this
  product only advises). The predicted-vs-realised table and the "uncertain" rule use `advised` boots only; `other` boots
  are listed apart and still feed the baselines and K (a boot is a boot for how fast the machine degrades). A marker older
  than `probe.restartMarkerMaxMinutes` (10) is ignored.
- **Descriptive first, predictive only when calibrated** (the cadence consultation of epics 1–3, 2026-10-10, verified against
  the formulas above). Two synthetic cases break the predictive sentence as written: a forecast of 5× for 9 h realised five
  times as 1.6× for 1 h is never declared wrong (the "uncertain" rule only tests N′ < 1.5); and four boots where one degrades
  at hour 1 and three stay healthy until a shutdown at hour 10 give K = 1 h from the uncensored median. So:
  1. **The default sentence is descriptive:** *"local steps now take 2.3× as long as after the last boot (412 ms vs 180 ms)"*
     — a claim about the probe, true as measured.
  2. **K is a survival estimate** (Kaplan–Meier over all boots with a baseline, censored at their shutdown), reported as "about
     K hours" only when the estimate's median is reached; otherwise "no previous boot slowed past 1.5× within H h".
  3. **The predictive sentence** appears only when, over the last `probe.calibrationWindowBoots` (10) scored `advised` boots, at
     least `probe.calibrationBoots` (3) were within `probe.calibrationTolerancePercent` (30) of the shadow prediction AND they
     are at least `probe.calibrationSuccessPercent` (70) of the scored ones — for N and for K separately (unresolved K′ count
     in neither numerator nor denominator; each of N and K can read "insufficient evidence" on its own). Three successes are a
     provisional product gate, not proof of reliability. The 7-day / 2-boot minimum stays — an eligibility rule, not evidence.
- **Freshness gates** (the same consultation): "now" is the median of the last `probe.nowSamples` calm samples taken within
  `probe.nowMaxAgeMinutes` (30); older or skipped samples give "no current measurement (the machine was busy since HH:MM)". The
  ladder (E16.S3) acts only on verdicts whose basis is younger than `ladder.maxEvidenceAgeMinutes` (60); an older carried
  verdict is shown with its age but never drives the recommendation (the `fullRun` basis keeps its severity today,
  `Status/StatusVerdicts.cs:54-59`).

### 3.4 The shared webview kit and its Storybook (the owner's Q1: a shared kit)

**No new repository.** The kit already exists: `dew_flow_vscode_kit` (public), package `@oleksandrdubyna88/vscode-webview-kit`,
no runtime dependency, release-please + `npm publish --provenance` prepared, 0.1.0 not yet published (checked: npmjs answers
404), with coai's display controls (± size, ± tone), the Help viewer in coai's five languages (`HELP_LANGUAGES = en, ru,
uk, de, es`, coai `src_vs_code/src/helpContent.ts:31-36`), escapers and a page-script harness. This epic adds the components
as **kit 0.2.0 with a Storybook** (E18.K1, in that repository): `Tabs`, `DataTable`, `RecommendationCard`, theme tokens, and
the existing display controls and Help viewer. wsl_care pins an exact version from npmjs (Dependabot bumps it; esbuild
compiles it into the bundle, so the `.vsix` ships nothing extra). The conventions submodule shape is NOT reused: a submodule
for product code was rejected with a measured reason (`todo/PLAN_shared_vscode_kit.md:19-20`). coai and creds are not touched.
The kit uses CLASSES only — no `style`, no `innerHTML` — so the panel's nonce-only CSP (`src_vs_code/src/panel/panelHtml.ts:59`)
and the strict DOM harness (`src/test/support/pageHarness.ts:18-24`, `:30`, `:33`) hold.

**The paired plan in the kit's repository** (plan round): E18.K1 opens with `dew_flow_vscode_kit` ·
`todo/PLAN_kit_components_storybook.md`, written in that repository's own pull request BEFORE any K1 code and carrying this
same table; the kit's existing `todo/PLAN_extract_the_kit.md` (its 0.1.0 release) is named there as the order's first step.

| Item | `dew_flow_vscode_kit` builds | wsl_care builds |
|---|---|---|
| `Tabs`, `DataTable`, `RecommendationCard`, theme tokens; the display controls and Help viewer (exist) | the components, their CSS (classes only), their tests, axe checks, the Storybook (every component × state × light / dark / high contrast × three font sizes), release 0.2.0 | nothing of the components — it consumes them |
| the data shown, the units, the redaction, the reveal, the recommendation's words | nothing (consumer-supplied callbacks and strings) | everything (E18.W1–W3; the daemon makes the words) |
| the order | 0.1.0 (its E3) → K1 → 0.2.0 on npmjs | W0 starts only after 0.2.0 is published (or the kit's `pack-and-consume` tarball as a bridge, never committed) |
| disjoint | no wsl_care-specific component, no `vscode` API in the webview components | no copy of a kit component, ever (`common.reuse-first`) |

### 3.5 One data service; the sidebar keeps 20 parameters

`OutcomeStore` + `Poller` already are the one data service (`src_vs_code/src/poll/poller.ts:86-124`); the full view is a
`WebviewPanel` (the Logs page's shape, `src/logsPage/logsPanel.ts:23-46`) that READS the store, never polls; it asks
`preview` / `doctor` only on open, *Refresh* and becoming visible; a hidden view gets no posts. A spawn ledger in the host
(calls and wall ms per verb per hour) is shown as "AI OS Care's own cost".

| # | Sidebar parameter (verdict / path) | The button or recommendation it justifies |
|---|---|---|
| 1 | WSL RAM available % (`memory.available`) | *Drop caches* (A1), *Compact* (A2) |
| 2 | Swap used of total (`memory.swap`, `memory.swapFree`) | A1 / A2, the restart rungs |
| 3 | Page cache (`memory.pageCache`) | A1 |
| 4 | Free order-7 blocks (`memory.fragmentation`) | A2 |
| 5 | PSI cpu / io / memory (`pressure.*`, `memory.pressure`) | "the machine is busy — wait"; the recommendation's evidence |
| 6 | Distro `/` used % and free (`disk.root`) | *Clean*, *Clean selected* |
| 7 | Docker reclaimable (`preview` totals) | *Clean selected* (A4–A7) |
| 8 | Containers running and their memory | the heavy-container advice rung |
| 9 | MCP servers: count, idle, busy-without-activity, cores (`mcp.*`) | *Stop idle MCP servers* (A19) |
| 10 | Orphaned AI-agent processes (A18's preview) | *End orphaned agent processes* (A18) |
| 11 | Top CPU group (`cpu.sessions`: "claude 49140 (--resume=5375…) runs npm test — 3.4 cores") | the recommendation's evidence |
| 12 | vmmemWSL of host RAM (`host.vmmem`) | *Shut down WSL…*, the `.wslconfig` advice |
| 13 | Windows commit % (`host.commit`) | the restart rung |
| 14 | Memory Compression (`host.compression`) | the restart rung |
| 15 | Host volume free vs the VHDX files' possible growth (`disk.hostFree`) | the sparse advice, the cleanups |
| 16 | Responsiveness now vs post-boot, the forecast sentence (`probe.factor`) | *Shut down WSL…*, "restart Windows" |
| 17 | GPU state (G1's alert, at the top) | *Enable <GPU>* (G1) |
| 18 | Daemon health: last full run age, `doctor` | *Run full check now*, *Install daemon* |
| 19 | A running / queued / wedged cleanup (`status.running`) | *Stop* |
| 20 | AI OS Care's own cost vs budget (`selfCost.*`) | *Settings* (lower the poll rate) |

Everything else moves to the full view. The sidebar never shows a command line and never an "arrives in E#" row; the full
view shows `program` + the daemon's safe prefix, reveals the redacted command line per row on click, and says "not available
yet — needs the bundled Windows daemon" (muted) where E7.S5a has not landed. Held memory uses binary adaptive units
(KiB / MiB / GiB).

### 3.6 The owner's review of the mockup (2026-10-15): no sideways scroll, one height per row, a distro picker

A static mockup of the whole redesign (side bar, full view WSL / Windows, Statistics, Help, the kit's components; light /
dark / high contrast; checked in headless Chrome at 300 / 1280 / 1920 px) was approved by the owner on 2026-10-15 with
three changes. They bind E18.K1, W1 and W2:

1. **No table ever scrolls sideways**, the small ones above all (*Actions and what they freed*, *Recommendations by rung*,
   *Disk*, *Own cost*): columns shrink and wrap, headers are short, and where that is not enough a row breaks onto several
   lines — never a scroll bar. The kit owns it (`dew_flow_vscode_kit · todo/PLAN_kit_components_storybook.md` §4.6a: the
   `DataTable` is a CSS size container, never a scroll container; below `30em` / `40em` / `60em` of its OWN width, by its
   `stack` option, each row shows as "label: value" lines and the header becomes a row of sort buttons; proved in a real
   Chrome at 300 / 1280 / 1920 px × four themes × three sizes). wsl_care passes short headers and the `stack` width per
   table; it writes no table CSS of its own.
2. **Cards side by side in one row have one height**, their buttons on one line at the card's bottom (the grid stretches;
   a card is a flex column whose button row takes the remaining space). wsl_care's own page CSS.
3. **A distro picker on top of the WSL tab** — there may be many distros, and every figure on the tab follows the chosen
   one. In WSL 2 every distro runs in ONE VM, so the figures split in two, and the page says which is which:

   | The WSL VM — the same in every distro, labelled "the WSL VM — shared by every distro" | The chosen distro's own |
   |---|---|
   | `vm.memory`, swap, PSI (`memory.*`, `pressure.*`), `kernel.*`, `clock.*`, the probe and the forecast, `host.*`, `disk.vhdx` / volumes (Windows-side files) | `vm.disk` / `disk.root`, processes, `mcpServers`, `interopRelays`, `folders`, `actions` / `limits` / `capabilities` / `running` / `lastCleanup`, the archive, the ladder and its recommendation; Docker where the engine runs inside this distro (Docker Desktop's own distro is shown read-only) |

   **The data path today** (checked 2026-10-15 on `main`): one setting `wslCare.distro` (`src_vs_code/package.json:165-171`,
   empty = the default from `wsl -l -v`), read on every call; the daemon's argv always carries `-d <distro>`
   (`src_vs_code/src/client/WslCareClient.ts:101-103`); a stopped distro is never woken (`:295-299`). Distros are listed only
   by private helpers (`WslCareClient.ts:82-84`, `:273`; `src_vs_code/src/wsl/distros.ts:31`). `OutcomeStore` (`src_vs_code/src/state/outcomeStore.ts:21`) holds ONE
   snapshot with no distro key, and the poller clears it when its target changes (`src_vs_code/src/poll/poller.ts:196-216`);
   `WslCareClient`'s `knownVersion` / `knownContainers` are single slots that remember one distro (`WslCareClient.ts:116`, `:118`), while the in-flight keys are already
   `${setting}|…`. The outcomes that concern a distro already name it (`src_vs_code/src/client/outcome.ts:57-67`); `status --json`
   does not name its own distro (only the best-effort `disk.distroName`) — the extension knows which distro it asked, so the
   daemon is not changed.

   **What the picker needs** (E18.W1a, before any UI): a public `listDistros()` on `WslCareClient` (name, running / stopped,
   WSL version, default) from the existing helpers; every verb taking an explicit distro, the setting staying the default;
   `OutcomeStore` as a map distro → snapshot, today's snapshot being the default distro's (the side bar's); the poller keeps
   polling the default distro on its schedule and reads a PICKED distro only while the full view shows it — on pick,
   *Refresh* and becoming visible (§3.5's rule) — and never a stopped one; the in-flight and `known*` slots keyed by distro;
   every action, its modal and the run journal name the picked distro. A distro with no daemon shows *Install daemon in
   <distro>…* and the VM's figures labelled "measured by <distro>'s daemon"; a stopped distro shows its last known figures,
   muted, with their time, and *Start <distro> and check*. The side bar keeps showing the default distro, its name in the
   section title — no picker there. A WSL 1 distro is not in the VM: it is listed with "WSL 1 — AI OS Care needs WSL 2" and shows no VM figures (gate, plan round 2026-10-15).

## 4. Stories — one pull request each, grouped in four epics (build order in §5)

Every story: RED tests first, then GREEN, then a break-it of the product line the behaviour depends on; every number a key
(`Config/ConfigKeys.Numbers.cs` + `Config/default.json` + `contracts/config-keys.json` via `ContractFilesTests`; extension
numbers in `src/settings/numbers.ts` = `package.json`); goldens under `contracts/golden/head/` regenerated; the verdict-id
lists (`Status/StatusVerdictsTests.cs`, `statusBarModel.ts`) extended; docs (README, `research/module_*.md`,
`architecture.md`, this plan's as-built); every new action a panel button in the same change (`commandButtons.ts` + test).
The template for a threshold key is commit `95448096` (E14 S5).

### E15 — the disk and memory REPORT (daemon, read-only)

| Story | What it builds (reuse → new) | Risk |
|---|---|---|
| **E15.S1** the second fixed interop script | `HealthCommands`' tagged-line shape → `Health/WindowsDiskCommands.cs` (Lxss map, VHDX size / sparse / last write, `Get-Volume` + `Get-Partition` with the disk number, `Get-PhysicalDisk`, `Get-StorageReliabilityCounter` in a try (its refusal kept), `Get-WinEvent` System / disk, stornvme, Ntfs, volmgr, storahci / 7, 11, 51, 129, 153, 157 bounded by `disk.eventsSinceDays` (60) and `-MaxEvents disk.eventsMaxRead` (5000), commit and compression lines, MachineGuid + edition + build + install date, `Win32_VideoController` codes for G2, a trailing `end=1`); `Health/WindowsDiskSample.cs` (every figure a `Reading<T>`); the never-list's closed set (§3.1); key `health.windowsDiskTimeoutSeconds` (5–120, 30); `RunBudget` grows by it. Measured FIRST on the owner's machine: the script's wall time, as the target user and as root, with `pwsh.exe` and `powershell.exe`. RED: a third script / a one-word variant / `-File` refused; a cut answer is unavailable, never 0; the D1–D5 answer as an anonymised fixture. No verdict yet. | high — a policy change; the event query's duration |
| **E15.S1b** the MSI `pwsh` for the root interop scripts (Q3, 2026-10-11) | the resolver's system-drive fallback (a closed list of administrator-only folders, root-owned ancestors, the `MZ` header) gains ONE absolute candidate, the MSI `pwsh` at the KNOWN-FOLDER Program Files path (`[Environment]::GetFolderPath('ProgramFiles')`, never the environment's `ProgramFiles`; refused when the environment names a different folder), taken only when the file is real with no link type and `Get-AuthenticodeSignature` reads Valid from `CN=Microsoft Corporation` — the same rules as `src_vs_code/src/powershell/elevatedEngine.ts` (`SIGNED_HELPER`, `anchoredProgramFiles`; the coordinator, 2026-10-11, after the #104 Fable review); how the root daemon runs that check without trusting a user-writable path is settled in the story's own plan round; every interop script keeps `$env:PSModulePath` pinned first (`HealthCommands.ModulePathPin`, shipped 2026-10-11); the closed set names the two programs (`pwsh` MSI, `powershell.exe`) for each fixed script; the run detail names the engine that ran. Never the Store package, never the alias. Measured FIRST: both scripts under the MSI `pwsh`, cold and warm, where it is installed (the owner's machine has only the Store build today; the story then ships the fallback and its tests). RED: a spoofed environment `ProgramFiles` naming another folder is refused; the alias and the Store package path are refused; at the MSI path, an unsigned file, a file whose signature is not Valid, a file signed by another publisher than CN=Microsoft Corporation, and a link (or a file reached through one) are each refused and 5.1 runs (plan round, 2026-10-11); `pwsh` with a third script is refused. | medium — a policy change |
| **E15.S2** the virtual disks and the hosting volume | `ThresholdRules.FromFullRun` (`Thresholds/ThresholdRules.cs:130-148`), `RunMetrics` for growth since the previous run, `DockerTotal` / `docker system df` for Docker's data disk (no second Docker walk), `VolumeUsage` for the distro's own `/` → `Thresholds/DiskRules.cs`: `disk.vhdxGap` (warn > `disk.vhdxGapWarnGb` 20), `disk.vhdxGrowth` (> `disk.vhdxGrowthWarnGbPerDay` 10), `disk.hostFree` (the hosting volume's free < the VHDX files' possible growth, or < `disk.hostFreeWarnGb` 100), the advice as TEXT for a file that is not sparse: compaction with WSL stopped (`Optimize-VHD` / `diskpart compact vdisk`, PLAN_wsl_care_daemon.md 0.3), NEVER `--set-sparse` (corrected 2026-10-11, see *E15.S2 correction*; no button: the daemon may not compact); `status.disk {vhdx[], volumes[], hostGrowth}` replaces the `host.vhdx` placeholder; `mappedBy: mount|size|none` on the wire. RED: 237.7 − 194.0 = 43.7 GiB warns; growth is a difference of two measured sizes, never of one. | medium |
| **E15.S3** who uses the disk | the two-point ledger of `Mcp/McpCpuLedger.cs` → a per-device `disk-io.json` ledger over `/proc/diskstats` (read class `System`), the root device by mount, others by size; Windows top I/O by `GetProcessIoCounters` in the Windows binary with the fixed caveat "I/O bytes count pipes and network too; the VM's disk traffic is vmmem's" (D6); `disk.busy` (a device above `disk.ioWarnMbPerSec` 200 while `pressure.io` warns). RED: deltas over the ledger interval, a reused device is no baseline. | medium |
| **E15.S4** disk health by disk number | S1's sample → `disk.events` (events on a disk that hosts a working volume; Harddisk1 counted apart), `disk.physical` (critical when not Healthy / OK), `disk.reliability` (`unknown`: "needs elevation; never elevated"). RED: 64 × `disk` 11 on Harddisk1 do not warn for disk 0. | low |
| **E15.S5** Windows memory pressure, one rule on both roads | `WindowsProbe.cs:84-97` (`TotalPageFile` / `AvailablePageFile`), `Health/VmmemAdvice.cs` → `Thresholds/HostRules.cs`: `host.commit` (warn > `thresholds.hostCommitWarnPercent` 80, critical > `…CriticalPercent` 90), `host.compression` (warn > `thresholds.hostCompressionWarnGb` 8), `host.vmmem`; evaluated by the Windows binary's `status` and by the full run over S1's lines. RED: 86 % warns, 91 % is critical, the same function on both bases. | low |
| **E15.S6** CPU saturation attributed to a Claude session | distro: `ProcessCollector` argv + the ancestor walk A18 / MCP share; Windows: `Mcp/Win32ProcessTable.cs:28`, `:46`, `:84-100` and the creation-time-checked walk of S7b.1 → `Collectors/SessionCpu.cs` (PURE: the nearest `claude` ancestor, the session from `--resume=<id>` / `-r`, else "claude <pid> (new session)", "no Claude ancestor", walk bound `cpu.ancestorWalkLimit` 64); `status.cpuSessions {groups[] {session, pid, cores, processes, topPrograms[], commandShown}}`; `cpu.sessions` (warn when one group holds > `thresholds.sessionCpuWarnPercent` 50 of the CPUs AND the machine is busy) — the sidebar recommendation's evidence. The command is `program` + safe prefix (E15.S7). RED: the D7 table as a fixture; a reused parent pid owns nothing; the walk stops at its bound. | medium |
| **E15.S7** the redaction rule widened (#87 item 2, daemon half) | `Collectors/Procfs/ProcessFiles.cs:96-148` → also `NAME=value` with a secret word anywhere, high-entropy positional tokens (≥ `processes.secretMinChars` 20), creds-shaped keys; `program` and `argumentsShown` (≤ `processes.shownSafeArgs` 2 safe arguments) on the wire. RED: a creds config key in a positional argument is redacted. | low |
| **E15.S8** GPU G2 — the daemon's verdict and ONE code table | S1's adapter lines (distro, 4-hourly) and a `cfgmgr32` reader in the Windows binary (`CM_Get_DevNode_Status`; unelevated, AOT-clean) → `gpu.adapters` (critical: any code ≠ 0), `status.gpu`, a doctor check; `Health/DeviceProblemCodes.cs` is the ONE writer of `contracts/device-problem-codes.json`; the extension's G1 table is held equal to it by a test (the only extension change). | low |
| **E15.S9** self-cost and the budget verdict | `Systemd/SystemdParsers.cs:8-26` widened to `CPUUsageNSec`, `MemoryPeak`, `IOReadBytes`, `IOWriteBytes` per unit; `IOAccounting=yes` in the shipped units (D8: today "not set"); `MeasureTree` for state and logs; the unprivileged run logs counted per hour = the extension's calls → `selfCost.cpu` (> `selfCost.cpuSecondsPerDayWarn` 600), `selfCost.memory` (> 90 % of `units.memoryMaxMb`), `selfCost.disk` (> `selfCost.stateWarnMb` 500), `selfCost.extensionCalls` (> `selfCost.extensionCallsPerHourWarn` 120): "AI OS Care itself costs more than its budget: …". RED: the measured 831 MB peak warns at 90 % of 1 GiB. | low |
| **E15.S10** Defender's CPU, recorded every full run (the owner, 2026-10-10) | A THIRD fixed interop script (`Health/DefenderCpuCommands.cs`, a member of the never-list's closed set) with ITS OWN ceiling `health.defenderCpuTimeoutSeconds` (15), run after the disk script — so a slow Defender read never costs the disk facts (the whole disk script is one sample, `Health/HealthCollector.cs:38`; risk consultation for epic 1, 2026-10-11). It prints `defender=` from `Win32_PerfRawData_PerfProc_Process` where `Name='MsMpEng'` — `PercentProcessorTime` (cumulative, 100 ns), `Timestamp_Sys100NS`, `IDProcess` and the raw `ElapsedTime` (a START marker, not elapsed seconds) — readable unelevated ([2026-10-10_defender.md](../research/2026-10-10_defender.md) F3); the two-point ledger shape of `Mcp/McpCpuLedger.cs:115` → `Health/DefenderCpu.cs` (PURE: core-seconds between two reads of the SAME process — the same boot id AND pid AND start marker, as `Mcp/McpCpuLedger.cs:228` keys its entries, because Windows reuses pids and an engine update restarts MsMpEng without a reboot; anything else is a new baseline, never a difference; a decreasing counter, a non-positive timestamp interval or missing data invent no usage; restart gaps add no covered hours; a weekly figure says it is extrapolated from the covered hours), `{state}/defender-cpu.jsonl` (`defender.localRetentionDays` 400, until E17.S1 takes it as a statistics record), `status.defender {coresAverage, coreHoursPerWeek, coveredHours, basis: fullRun, age}`, `host.defenderCpu` (warn when the average since the previous run is above `thresholds.defenderCoresWarn`, 1.0 core). Measured FIRST, under Windows PowerShell 5.1 (F3 used pwsh 7): the script cold and warm, at a low and a high process count, filtered and unfiltered (does `-Filter "Name='MsMpEng'"` still enumerate every process?), so the ceiling and `RunBudget` growth are set from numbers. RED: two reads of one process 10 s apart that added 10.3 core-seconds give 1.03 cores; a pid change, the same pid with a different start marker (and a LARGER counter), a changed boot, a decreasing counter and a non-positive interval each give no difference ("Defender restarted — a new baseline"); a stalled Defender script past its ceiling leaves the disk facts available, reports its own timeout, and its child is killed; a missing line is unavailable, never 0; the never-list's closed set admits exactly the new script and still refuses a one-word variant. | medium — the cold CIM cost (own ceiling) |

### E16 — the PROBE, the FORECAST and the remedy LADDER

| Story | What it builds | Risk |
|---|---|---|
| **E16.S1** the probe | §3.3's components in `Probe/ResponsivenessProbe.cs` (one implementation, both OS), `Probe/ProbeStore.cs` (`{state}/probe.jsonl`, `probe.localRetentionDays` 400 until E17 takes the forever copy), the watch step and the full-run step under the lock, `wsl-care probe [--json]` (+ the verb register), `status.probe`, `probe.budget`; `RunBudget.WatchRunWorstCase` grows by `probe.maxMilliseconds`. Measured FIRST: ten probe runs on the owner's distro. RED: fixed work; over-budget kept and flagged; a busy machine skips and says why; the files live only under the state folder. | medium |
| **E16.S2** the forecast | `Probe/Forecast.cs` (PURE: baseline, N, K, M, censoring, the minimum, the sentence), `Probe/BootChanges.cs` (a `boot` record with the shadow prediction; realised values fill in later); the keys of §3.3; `probe.factor`. RED, with the owner's words: six days of history give NO factor and say "a minimum forecast needs at least 7 days of history; 6 days so far"; the minimum in the sentence is the key's value; a 30-day synthetic fixture with four boots gives N, K, M; the consultation's two cases are fixtures (5× / 9 h predicted, 1.6× / 1 h realised five times → the predictive sentence never unlocks; one boot degrading at 1 h and three censored at 10 h → "median degradation time not reached; 1 of 4 boots observed degrading"); a gap of busy samples ends observation; a shadow prediction is recorded before every advised restart and scored at the next boot. A `research/` note states the method with the fixture's numbers. | high — statistics the gate will argue |
| **E16.S3** the ladder and the ONE recommendation | the previews and measured results of A19, A11, A18, A1, A2, the container stats, `VmmemAdvice`, the forecast, the run history → `Thresholds/Ladder.cs` (PURE: rungs in cost order, each `{id, kind: action|extensionButton|advice, addresses[], state, wouldFree, lastEffect, text}`; the recommendation = the first rung with something to do among those addressing the WORST non-ok verdict; "all within limits"); S7b's A21 shown as `arrivesWithS7b` on the wire, never in the sidebar. RED: the recommendation names the group, the limit, the measured value and the button; severity first, then cost; a rung's last effect comes from the history, never a guess. | medium |
| **E16.S4** *Shut down WSL…* (ask first) and the Disk & memory section | the restart marker first (`wsl-care probe mark-restart`, §3.3), then `WslCareClient` (the only argv speller, `src/client/WslCareClient.ts:17-28`) gains the one request `wsl.exe --shutdown`; a modal names what ends ("every WSL session and every program in the distro — N agent processes, M containers now"), the forecast line and that the effect is measured; greyed while a run is live / queued or the archive runs; `wslCare.timeouts.wslShutdownSeconds` (120); command + button; the sidebar's section renders `status.disk`, `host.*`, the forecast and the ladder (rungs as buttons where the cleanup controller offers the action, advice lines otherwise). RED: exactly `wsl.exe --shutdown`; a decline starts nothing; the strict fake accepts only `['--shutdown']`; the tripwire refuses the real one. Never automatic. | medium |
| **E16.S5** *Exclude development folders from Defender* — a checkbox (the owner, 2026-10-10) | **The state, unelevated (Road 3).** The extension's PowerShell replays event 5007 of `Microsoft-Windows-Windows Defender/Operational` for each configured folder ([2026-10-10_defender.md](../research/2026-10-10_defender.md) F3, F4): a newest event with `New value: …\Exclusions\Paths\<folder> = 0x0` reads `excluded`; no event, or a shape not seen yet, reads `not known` — never "not excluded". A removal's shape is captured from the first real uncheck (with the owner's consent) and becomes a fixture. Process exclusions found in 5007 are shown as a warning and are never added. The replay is history, not proof, so its words are "last recorded as excluded, at <time>". The box is CHECKED when every configured folder is last recorded as excluded; UNCHECKED when every folder's newest event is a recorded REMOVAL of its entry (a captured removal shape) — worded "entry removed at <time>; coverage by a parent folder or a policy not verified" unless a fresh elevated check confirmed it; NOT KNOWN — an indeterminate box naming the folders — when any folder reads `not known`; MIXED when the known states differ (risk consultation, 2026-10-11). The folders: `wslCare.defender.exclusionFolders`, default `${devRoot}`, `%USERPROFILE%\.nuget\packages`, `%LOCALAPPDATA%\npm-cache`. `${devRoot}` is the nearest common parent of the open workspace folders that are on a local FIXED drive. At most 8 folders. **EVERY folder** (default or typed) passes one rule before any launch: only the named placeholders are expanded; the result is drive-absolute, and each component uses only ASCII letters, digits, space, `.`, `_` and `-` (no quote of any kind, no wildcard, no `%` left, no UNC or `\\?\` prefix, no alternate stream, no `~` short name, no empty, `.` or `..` component, no trailing dot or space, no DOS device name); the folder exists on a FIXED volume, and neither it nor any ancestor is a reparse point (junction, symlink); two kinds of refusal, kept apart: a folder may not BE or CONTAIN a drive root, the profile root or `C:\Users` (their narrow descendants — `D:\rsd`, the NuGet and npm caches — are allowed); and a folder may not be, contain or lie INSIDE `%SystemRoot%`, the Program Files folders or `%ProgramData%`. Refused folders are named and nothing runs. The elevated script re-checks the same rule on the exact consented paths before any change (this narrows the time between check and use; it does not close it against a hostile local replacement, and the story says so). **The change, elevated (ONE UAC prompt).** Reuse the GPU enable's path: `gpu/gpuScripts.ts:257` `engineOf` (pwsh 7 by its signature-checked MSI path, then the signature-checked Store package executable under `WindowsApps` — never the alias — then Windows PowerShell 5.1: the owner's order), re-verified after the modal as `gpu/gpuEnable.ts:216` does, the module path cut to `$PSHOME\Modules` and `Defender` imported by its absolute System32 path as `gpuScripts.ts:108` imports `PnpDevice`, `windowsTime/windowsTimeFix.ts:97` `elevatedRequest` and `:47` `encoded` (the inner script as `-EncodedCommand`; each folder a single-quoted literal checked by a closed character rule before any launch), `windowsTimeFix.ts:156` `CONFIRM_LABEL`. NOT the GPU's advisory pending record (`gpu/gpuPending.ts:28`) unchanged: add and remove are OPPOSITE changes, so one change at a time is claimed atomically (a directory created without `recursive` under the shared storage) and the claim records the elevated process (pid + start time); it is released only when that process is seen to be gone — never by elapsed time or the launcher's own timeout, because a started elevated change may outlive both. The inner script also carries its issue time and refuses to START after `wslCare.timeouts.defenderChangeSeconds` (120), so a UAC prompt answered late changes nothing. The inner script runs `Add-MpPreference -ExclusionPath` or `Remove-MpPreference -ExclusionPath` with exactly the listed folders — NEVER `-ExclusionProcess` or `-ExclusionExtension` — then reads back `(Get-MpPreference).ExclusionPath` AND checks each folder with the elevated `MpCmdRun -CheckExclusion -path` (effective coverage; its log side effect accepted). A policy that stops local lists from merging (`HKLM\SOFTWARE\Policies\Microsoft\Windows Defender`, `DisableLocalAdminMerge`) is its own exit code ("a policy ignores local exclusions"). The result channel is the exit code: 0 = every folder confirmed as asked; otherwise `64 + mismatch mask + (unknown mask << 8)` (bit i = folder i, at most 8 folders) — a CONFIRMED mismatch is apart from a check that could not be made (access denied, timeout, unexpected output), so a partial result names its folders even when no new 5007 event arrives, and a code outside that shape is rejected as malformed. Only a confirmed check may say "still excluded (a parent folder or a policy covers it)"; a failed check says "not verified". A fresh elevated result takes precedence over the replay until a newer 5007 event arrives. Then the 5007 replay runs again, and the panel says what Windows now reports and which engine ran. The modal names every folder, says plainly "this LOWERS protection: Defender stops scanning files in these folders", and shows the exact elevated script. **The saving line.** From E15.S10's records, around the newest 5007 change that names a CONFIGURED folder (an event for any other path is not an anchor): "a measured saving needs at least 7 days before and after the change (before: N days, after: M days)" (7 is `defender.minSavingDays`, the forecast's rule), then "Defender used X core-hours/week less (−Y %)". When another exclusion or real-time change (any `Exclusions\` or `Real-Time Protection` 5007) falls inside either window, the figure is withheld and the line names that change (plan round, 2026-10-11). **Measure now** (an optional button): F1's workload as an A/B — the same 4 000 files in a NEW folder under an excluded folder and in a NEW folder under `%TEMP%` — labelled "a comparison of two locations; whether the second one is covered is not verified", never "not excluded" — MsMpEng sampled, each run deleting only the folder it created, in a `finally`; every probe folder is RECORDED (path, pid, start time) in the extension's storage when it is made, and the next *Measure now* removes only RECORDED folders whose process is gone, after a containment and reparse check, naming what it removed — a look-alike folder it did not record is never touched; the result reads "excluded: A s, B core-seconds; not excluded: C s, D core-seconds". Commands and panel buttons in the same change (`commandButtons.ts`). A Fable own reviewer reads the elevated path beside the coai gate. RED: a replay of this machine's three 5007 events reads CHECKED; a folder with no event reads `not known`, never unchecked; the elevated script never contains `ExclusionProcess` or `ExclusionExtension`; a decline starts nothing; a folder with a quote character (straight or smart) is refused before any launch; `C:\Users`, `C:\Windows\System32`, `C:\PROGRA~1`, `D:\dev\..\`, a trailing dot or space and a junction to Windows are refused on BOTH input routes; all-`not known` never reads UNCHECKED; an add delayed past its expiry exits without a change; the inner script never loads a user-folder `Defender` module; exit `64 + 0b010` names the second folder; the saving line with 0 days before gives no figure; the tripwire refuses a real `Add-MpPreference` / `Remove-MpPreference` in tests; an unrelated 5007 change inside a window withholds the saving; a recorded probe folder of a dead pid is swept, one of a live pid is kept, an unrecorded look-alike with a sentinel file survives; `D:\rsd` and both caches are ACCEPTED; an add suspended after its last check, with the launcher timed out, still blocks a remove until its process is gone; a failed `CheckExclusion` reads "not verified", never "still excluded"; a removal event with a parent exclusion left never reads as plainly not excluded; an empty history with the control location covered never prints "not excluded". **A scenario** (the extension's host harness, the strict fake PowerShell) drives the panel flow end to end — check → modal → decline (nothing runs); check → confirm → the fake elevated script answers "partial" (one folder not in the read-back) → the box reads MIXED naming that folder; uncheck → confirm → done → UNCHECKED — and `research/module_tests.md` catalogues it with its limits (no real UAC, no real Defender). | high — an elevated change that lowers protection |

### E17 — STATISTICS kept forever

| Story | What it builds | Risk |
|---|---|---|
| **E17.S1** the local store, records, rollups and `stats` | §3.2's records, `Stats/StatsWriter.cs` (append under the lock), `Stats/Rollups.cs` (PURE daily / monthly counts: not-ok verdicts by id, actions with freed bytes, recommendations by rung, probe min / median / max, boots predicted / realised, self-cost), `Stats/StatsReader.cs`, `wsl-care stats [--period today|yesterday|week|month|year|<from>..<to>] [--json]`, `stats rebuild`; machine identity cached from S1's lines. RED: every record carries machine id, build and install date; a year view reads twelve monthly rollups and no raw file; a bad line is counted, never fatal; rebuilt rollups equal the incremental ones. A new `research/module_stats.md`. | medium |
| **E17.S2** the share, the flush, the folder picker | `Archive/BaseFolderRules.cs` with a stats context, `Files/NetworkPaths.cs`, `MountTable.IsWholeDrive`, the archive's read-back → `stats check-base`, `stats check`, `Stats/StatsShare.cs`; keys `stats.baseFolder`, `stats.flushMinutes` (60), `stats.flushTimeoutSeconds` (30), `stats.pendingWarnDays` (3); doctor `stats.store`, `stats.share`; the extension's *Choose statistics folder…* / *Keep statistics local* asked ONCE per install (Q2: a fresh install ASKS; the dialog may pre-fill a local default folder and never pre-fills a network path on its own; on the owner's machine he picks `V:\AiOsCare`). Measured FIRST (read-only): a 100 KB temp + rename + read-back on `V:` through `/mnt/v`. RED: a day is confirmed only on a digest match; an offline share queues locally and blocks nothing; a stats folder inside the archive base is refused; nothing on the share is ever deleted. | high — SMB from WSL |
| **E17.S3** export, always redacted | `wsl-care stats export --period … --json` (records with `program` only — never a command line), capped by `stats.exportMaxRecords` (100 000). RED: a planted `--token=…` and a creds key never reach an export. | low |
| **E17.S4** the Windows spool merged | after E7.S5a: the Windows binary writes only its spool; the root daemon merges by watermark and never deletes it. | low (blocked on E7.S5a) |

### E18 — the KIT and the FULL VIEW (#87)

> **The owner's go: 2026-10-12.** Publishing the kit to npmjs is approved (Q1). The mockup was approved on 2026-10-15 with
> three changes (§3.6); W1 is split into W1a (the per-distro data path) and W1b (the full view). E17 is not started on
> `main` (2026-10-15: no `Stats/`, no `stats` verb, no contract), so W3 is built after E17.S1 and E17.S3, not against a
> contract that does not exist yet; W4 goes before it.

| Story | What it builds | Risk |
|---|---|---|
| **E18.K1** kit 0.2.0 + Storybook (in `dew_flow_vscode_kit`) | `Tabs` (ARIA tablist, arrow keys, a tone per tab + its word), `DataTable` (sort by click / Enter / Space with `aria-sort`, a search box, consumer-supplied units, a per-row reveal cell, and §3.6's fit rule — never a sideways scroll: a size container whose rows stack below a width), `RecommendationCard` (icon + word, sentence, one button slot, evidence), theme tokens; Storybook (`@storybook/html-vite`, `addon-a11y`) — every component × state × light / dark / high-contrast × three font sizes; axe tests in the kit's CI; classes only. Publishing 0.2.0 to npmjs is owner-visible (Q1). | medium — capped at these components |
| **E18.W0** wsl_care consumes the kit | a second esbuild entry `src/webview/fullView.ts → media/full.js` and `media/full.css` (`scripts/bundle.mjs:55-68`); the exact pinned dependency; `vsix-files.txt`; the harness's tag set widened only per need, each a RED test. | medium — the kit's publish timing |
| **E18.W1a** the per-distro data path (§3.6, no UI) | `WslCareClient.listDistros()` (public, from `WslCareClient.ts:82-84`, `:273` and `src/wsl/distros.ts:31`); an explicit distro on every verb, `wslCare.distro` the default; `OutcomeStore` as distro → snapshot; the poller's rule of §3.6 (the default on schedule, a picked one only while shown, never a stopped one); the in-flight and `known*` slots keyed by distro; actions, modals and the journal naming their distro. ONE distro-aware entry point: `WslCareClient` stays the only argv speller and every verb and action takes the distro as an argument, never from the setting at the call site. The maps hold only the distros the last `listDistros()` reported — an entry for a distro no longer listed is dropped at that list (a machine's distro count bounds them; no LRU needed). A picked distro that stops between reads: the client's existing running check (`WslCareClient.ts:295-299`) answers `stopped` without starting it, the in-flight read for it is dropped, and the view turns to the stopped card. A WSL 1 distro (the daemon needs WSL 2) shows "WSL 1 — AI OS Care needs WSL 2" and NO VM figures, never the shared-VM label. RED: picking a distro never clears the default's snapshot; a stopped distro is never started by a read; two distros' outcomes never mix; a test that enumerates EVERY verb and action of the client (the verb register and `commandButtons.ts`) and checks each argv carries the picked `-d`; a distro removed from the list drops its entries; a picked distro that stops mid-session turns to the stopped card with no daemon call; a mixed WSL 1 / WSL 2 machine shows no VM figures for the WSL 1 distro. | medium |
| **E18.W1b** the full view | the distro picker on the WSL tab (a radio group: name, state, its status mark; arrows / Home / End), §3.6's split shown on the page; cards of one height per row; `logsPanel.ts`' panel shape, the store (no second poll), the renderers shared between both views; tabs *WSL: <distro>* and *Windows* (each per side), the tables with `program` + `argumentsShown` and a reveal that posts only an index, binary units for Held, no wrapping PIDs, "not available yet" (muted); the spawn ledger; every action a button here too (`commandButtons.ts` gains `where: 'full'`, the test widens to the full page). RED: no row shows a command line before reveal; Held 14 MiB reads "14.0 MiB"; a hidden panel receives no post; every figure follows the picked distro and the VM's are labelled as the VM's; the page passes a headless-Chrome layout check at 300 / 1280 / 1920 px × four themes (no page, box, table or cell wider than its room; cards in a row of one height) — the kit's §4.6a probe reused, never copied (shipped from the kit as a bin if K1's code round admits it, otherwise a small kit story before W1b). | medium |
| **E18.W2** the slim sidebar | `fieldMap.ts` gains a required `justifies` per sidebar row (§3.5's 20) and a `sidebar | full` column (one table, two readers; the generated block in `architecture.md` regenerated); the recommendation card under G1's alert; *Open full view*; the default distro's name in the section title (no picker in the side bar, §3.6). RED: no `arriving` row in the sidebar; every sidebar row names its button. | low |
| **E18.W3** the Statistics tab with JSON export | E17's verbs through the client's run reads; periods today / yesterday / week / month / year; triggers by metric, why, what happened, predicted vs realised, self-cost; *Export JSON…* writes exactly the daemon's redacted bytes (the extension's first file write, named in `structure.test.ts`) — never parsed and re-serialised. Starts only when E17.S1 and E17.S3 are MERGED; W4 does not wait for it. RED: a known daemon export (with a planted `--token=…` the daemon already redacted) is written and the file's bytes equal the daemon's stdout byte for byte. | low |
| **E18.W4** Help in the coai design, five languages | the kit's Help (catalogue, digests, search); an English article for every command, setting, verdict and rung (a coverage test); `wslCare.helpLanguage`; ru / uk / de / es as one content PR each (Q4); the UI stays English. | low |
| **E18.W5** accessibility, both pages, and the close of #87 | keyboard sort / search, ARIA, the kit's contrast job, `pagesShareOneRenderer.test.ts`; docs. The PR body says "Closes #87". | low |

## 5. Build order and pull requests

E15.S1 → E15.S2 → E15.S1b → E15.S5 → E15.S10 → E15.S7 → E15.S6 → E15.S4 → E15.S3 → E15.S9 → E15.S8 ‖ E16.S1 → E16.S2 → E16.S3 → E16.S4 ‖ E16.S5 (after E15.S10; its checkbox half does not wait for the ladder) ‖
E17.S1 → E17.S2 → E17.S3 ‖ E18.K1 → W0 → W1a → W1b → W2 → W4 → W5's prerequisites; W3 starts only when E17.S1 and E17.S3 are merged (gated on them, not on W4), and W5 closes #87 after W3. E16 starts after E15.S1; E17.S1 after E16.S1 (the probe
records). E17.S4 and the Windows halves' panel rows wait for E7.S5a and S7b's runner. At most three open pull requests in
this repository, one per lane; E18.K1 lives in the kit's repository.

## 6. Boundaries with the other plans (the same table is written into each of them when its story starts)

| Item | This epic | The other plan |
|---|---|---|
| GPU | E15.S8: the daemon's `gpu.adapters`, the contract table with one writer, a doctor check; a test holding G1's table equal | G1 (`research/PLAN_gpu_health.md`, IMPLEMENTED 2026-10-10 on branch `feat/wc-gpu-health`): the extension's read, the top alert, the bar mark, *Enable <GPU>*; builds no daemon code |
| E7.S5a ([PLAN_bundle_windows_binary.md](../research/PLAN_bundle_windows_binary.md)) | the Windows tab reads `wsl-care.exe status --json` through `WindowsCareClient` once it exists (a follow-up story) | the bundle, the attestation, `WindowsCareClient` and its first `--version` |
| S7b ([PLAN_twenty_sessions_all_day.md](PLAN_twenty_sessions_all_day.md)) | the ladder's A21 rung as `arrivesWithS7b`; the Windows probe as a `watch` step S7b's runner will call; E15.S6 reuses S7b.1's Windows ledger | A21, the Windows `watch`, its runner |
| The Archive page (E10.S2a, [PLAN_wsl_care_daemon.md](PLAN_wsl_care_daemon.md) §15s) | E18.W1 may move it into the full view on the kit's `DataTable` (sort, search, a per-row reveal of the session path) | §15s builds it on `logsPanel.ts`' shape: decisions in `archivePage/archiveController.ts` / `archiveViewModel.ts`, the rendering in ONE file (`media/archive.js`), a closed message set naming rows by index — no sort or search of its own |
| E10 archive (#88, #92) | `configCall.ts` gains a second key; the archive's folder flow reused; a stats folder may not lie inside the archive base | unchanged |
| The kit | E18.K1 = kit 0.2.0 | the kit's 0.1.0 release; coai's own switch |
| Every lettered volume's free space; the AI-agent worktrees ([PLAN_disk_hygiene_windows.md](PLAN_disk_hygiene_windows.md), 2026-10-14) | `disk.hostFree` (E15.S2) stays the VHDX-growth question; E15.S1's script is reused unchanged; E18.W2's sidebar list (§3.5) STAYS 20 rows (the owner, 2026-10-14): row 15 becomes the worst Windows volume with a short reclaimable-build-output suffix, row 7 (Docker reclaimable) gains the test-leftover count; E18.W1's Windows tab carries an "AI-agent worktrees" card (that plan's §8) | `disk.volumeFree` (DH-1), the worktree report (DH-3), A24/A24Orphans (DH-4), the `testsToDelete-` sweep (DH-6, DH-7) |

## 7. Growth surfaces

| Surface | Projected size | Retired by | Interrupted |
|---|---|---|---|
| stats raw (local) | ≈ 95 KB/day/side ≈ 40 MB/side at 400 days; at most `stats.localBacklogMaxMb` (1 024) when the share stays offline | `stats.localRetentionDays`, only after a confirmed share copy; at the backlog cap individual probe samples stop being kept, counted (§3.2) | append under the lock; a torn last line is counted as bad, never fatal |
| stats raw (share) | ≈ 70 MB/year, ≈ 0.7 GB in ten years | kept forever (the owner) | whole-file temp + rename; pending days retried each run |
| rollups | ≈ 1.5 MB/year | kept forever | rebuilt from raw by `stats rebuild` |
| `probe.jsonl` (until E17) | ≈ 70 KB/day | `probe.localRetentionDays` | appended under the lock |
| `disk-io.json` ledger | ≤ `records.maxStateFileBytes` (1 MiB) | two points per device, other boots dropped | written atomically |
| probe files | 64 × 4 KiB = 256 KiB | created once; never grow | recreated when missing |
| restart marker | one file, < 1 KB | consumed at the next boot; ignored after `probe.restartMarkerMaxMinutes` | written atomically by the unprivileged verb |
| `defender-cpu.jsonl` (until E17) | one line per full run, ≈ 200 B × 6 a day ≈ 0.4 MB/year | `defender.localRetentionDays` (400) | appended under the lock; a torn last line is counted as bad |

## 8. Test plan (across the epic)

- Unit and scenario suites on every PR (`WslCare.Core.Tests`, `WslCare.Cli.Tests`, `WslCare.Scenarios` executables;
  `npm test`, `test:host`, lint, typecheck, `check:vsix`); the live contract for the new interop script on the owner's
  machine (read-only); goldens regenerated in WSL.
- Each story's RED list is in §4; each has a break-it of its product line.
- Measured before building on an assumption: the interop script's wall time (E15.S1), ten probe runs (E16.S1), an SMB round
  trip on `V:` (E17.S2) — each recorded in `research/` with its prediction first.
- The forecast's method is a fixture-driven unit test plus, after release, the predicted-vs-realised table — the only true test.

## 9. Questions (consultants first; the owner gets only what neither can settle)

| # | Question | Options and the recommendation | Blocks |
|---|---|---|---|
| Q1 | The kit on npmjs (public) as the kit plan decided on 2026-10-02? | **Decided 2026-10-11 (the owner): (a), publish to npmjs — but E18 does not start before his go.** (a) publish 0.2.0 to npmjs with provenance, wsl_care pins it — recommended; (b) a committed tarball per release (no Dependabot, a blob in git); (c) a submodule (rejected earlier with a reason) | E18.W0 |
| Q2 | Pre-fill the statistics folder dialog with the archive base's drive? | **Decided 2026-10-11 (the owner): a fresh install ASKS where to store the statistics; the dialog may pre-fill a local default folder, never a network path on its own; on this machine the owner picks `V:\AiOsCare`.** (a) yes — recommended; (b) a blank dialog | E17.S2's UI |
| Q3 | The root daemon runs a second fixed PowerShell script every full run (System log, registry, storage cmdlets) | **Decided 2026-10-11 (the coordinator, with the recommendation): yes; the MSI `pwsh` at `%ProgramFiles%\PowerShell\7\pwsh.exe` when present and signature-checked, otherwise 5.1; never the Store package (E15.S1b).** (a) yes, `pwsh.exe` if it answers as root, else 5.1 — recommended; (b) only through the Windows binary (waits for E7.S5a, needs new P/Invoke families) | E15.S1 |
| Q4 | Who writes the ru / uk / de / es Help? | **Decided 2026-10-11: an agent writes them, as coai's were, each language its own content PR through the gate; the UI stays English.** (a) AI-drafted, reviewed by the owner, one PR per language — recommended; (b) English with "not translated yet" | nothing in W4 itself |
| Q5 | *Shut down WSL…* while a cleanup or the archive runs | **Decided 2026-10-11: (a), greyed with the reason shown while a cleanup or an archive run is in flight.** (a) greyed — recommended; (b) available with a stronger modal | E16.S4's modal |
| Q6 | This machine's Defender exclusions came BEFORE the first per-run record, so the weekly saving has no "before" days here | **Decided 2026-10-11: (a) — the panel says the exclusions predate the first record, so there are no "before" days on this machine, and offers *Measure now*.** (a) say so, and offer *Measure now* as the only figure on this machine — recommended; (b) the owner unchecks the box for 7 days to build a baseline (costs Defender CPU for a week); (c) take F1's probe as the baseline (a synthetic workload, not core-hours per week — rejected) | E16.S5's saving line only |

Stated assumptions (decided, not asked): `git status` is not a probe component; Windows top I/O stays a labelled slice of
E15.S3; the Statistics tab shows this machine only; the self-cost defaults come from D8; a restart's effect is attributed to
ANY boot-id change, not only to the button; `host.*` verdicts colour the status bar, `selfCost.*` and `stats.*` do not; the
Windows probe's runner is S7b's, not a new scheduled task; the Defender checkbox reads its state from event 5007 and never elevates only to read; `${devRoot}` is derived from the open workspace and shown in the modal before any change.

## 10. Risks and their mitigation

1. **The never-list widening and the script's duration (E15.S1)** — a closed enumeration with RED tests for a third script, a
   variant and `-File`; `-MaxEvents` and a since-days bound; measured before the plan round of the story; full run only.
2. **Half of the "now" Windows facts depend on E7.S5a and S7b** — the design degrades honestly: the distro probe, the VM
   forecast, commit / compression at 4-h resolution and every disk verdict work without the Windows binary; the full view
   says what is missing.
3. **SMB from WSL (E17.S2)** — local first, whole-file copies with read-back digests, per-machine folders, the flush last with
   its own ceiling, pending visible, nothing deleted on the share.
4. **The forecast's validity (E16.S2)** — calm samples only, fixed work, the 7-day + 2-boot minimum with no factor before it,
   censoring said, predicted vs realised recorded and shown, "uncertain" after repeated misses.
5. **The kit on #87's critical path** — five components only, the kit's own CI, a `pack-and-consume` tarball as a bridge if
   the publish slips, no interim copy in wsl_care.

## 11. The plan round (coai, 2026-10-10: `proceed`, 1 of 1 reviewer — codex; gemini out of quota, the local engine had no model)

Three findings, all accepted and folded in: the local backlog is bounded and loud at its bound (§3.2, §7); only a boot that
followed the advice validates the forecast — the restart marker (§3.3); the kit's repository gets its paired plan and the
two-sided table (§3.4). Commands applied: the plan is not split again; work proceeds autonomously; questions go to the
consultants first.

**The Defender stories' plan round** (coai, 2026-10-11, branch `docs/wc-defender-plan`, `proceed`, 1 of 1 reviewer — codex):
three findings, all accepted. They added the checkbox flow's scenario and its `module_tests.md` entry; the saving window
anchored to the change of a CONFIGURED folder, withheld when another exclusion or real-time change falls inside it; and
*Measure now*'s cleanup in a `finally`, with a sweep of what it recorded. **The risk consultation for epic 2** (gpt-6-astra,
two turns, closed `solved`) reshaped E16.S5's elevated path:
- one validation rule for EVERY folder, re-checked inside the elevated script, with "be or contain" refusals kept apart
  from "inside" refusals;
- the Store engine named correctly: the signature-checked package executable, not the alias;
- the `Defender` module imported by absolute path;
- a claim that is released only when the elevated process is gone, plus an issue-time expiry;
- an exit-code channel that keeps a confirmed mismatch apart from a check that could not be made;
- the replay worded as history ("last recorded …");
- a control location labelled "not verified".

Still to verify on Windows when the story is built: what `DisableLocalAdminMerge` does, and what the elevated
`MpCmdRun -CheckExclusion` prints.

## 12. Definition of Done (per story, and for the epic)

- [ ] Every new figure has its basis and an honest unavailable reason; every threshold is a key with its range and default.
- [ ] Every verdict and the recommendation are computed in `WslCare.Core`; the extension holds no threshold.
- [ ] RED first, GREEN after, a break-it of the product line; all suites green; goldens and contracts regenerated.
- [ ] Every action is a panel button; *Shut down WSL…* asks first and never runs on its own; nothing elevates except the
      Defender checkbox's one UAC-prompted change (E16.S5, folder exclusions only, read back), and nothing compacts or sets a
      VHDX sparse.
- [ ] Command lines are hidden by default in the UI and never in an export (tested with a creds-shaped fixture).
- [ ] The forecast says the 7-day minimum sentence and gives no factor until the history allows one.
- [ ] Statistics carry the machine id, build and install date; the share is a verified copy; no record is lost offline.
- [ ] Docs, module maps, the research index and this plan's as-built are updated; each finished story is recorded here;
      the epic's last PR closes #87; the plan is promoted when its last story ships.

## E15.S1 as built (2026-10-10, branch `feat/wc-e15s1-windows-disk`)

**What shipped.** `Health/WindowsDiskCommands.cs` — the second fixed interop script, one line of ASCII with no slot (only
`disk.eventsSinceDays` and `disk.eventsMaxRead`, clamped integers), TAGGED lines and a last `end=1`; it reads the Lxss map and
each distro's VHDX (size, sparse bit, last write), Docker's data disk at its default path, the lettered volumes with their disk
numbers, the physical disks, the reliability counters (refused unelevated, the refusal kept), the System log's storage events
aggregated per UTC day, provider, id and `HarddiskN` inside PowerShell, commit / limit, Memory Compression, the edition /
version / build / install date, the MachineGuid and the display adapters' problem codes. `Health/WindowsDiskSample.cs` (every
figure a `Reading<T>`), `Health/WindowsDiskParsers.cs` (by tag; a cut answer unavailable as a whole; a malformed line counted
and skipped), `HealthCollector.MeasureWindowsDiskAsync` run by the full run once, after the clock probe and its reference,
`HealthSample.WindowsDisk`, and the run detail's `health.windowsDisk` (`Collect/WindowsDiskReport.cs`). The never-list's
`powershell` rule is a CLOSED SET (`HealthCommands.FixedScripts`: the clock probe and the disk script, program AND arguments,
under the configuration in force); the read template `ReadCommandTemplates.WindowsDisk` has one slot,
`SlotKind.WindowsDiskScript`, that accepts exactly the current script. Keys `health.windowsDiskTimeoutSeconds` (5–120, 30,
machine-only), `disk.eventsSinceDays` (1–365, 60), `disk.eventsMaxRead` (100–100 000, 5000, machine-only);
`contracts/config-keys.json` regenerated; the derived timer worst case grew from 259 to 260 min (still under 276). The anonymised
capture is `src_daemon/tests/fixtures/health/windows-disk-2026-10-10`; the scenarios are `WindowsDiskFlows`; the live contract
gained the script through interop. Measured first (18:29Z, the product's own text, Windows PowerShell 5.1 unelevated): 2.4–3.3 s
warm, 10.7 s cold, 26 lines.

**Deviations.**

1. **Windows PowerShell 5.1, not `pwsh` 7 — a follow-up, not a setting.** The owner's preference is `pwsh` first. The Store
   `pwsh` is reached through an app-execution alias under the USER's profile (`%LOCALAPPDATA%\Microsoft\WindowsApps`), a file the
   Windows user can replace (the package's own folder under `%ProgramFiles%\WindowsApps` is TrustedInstaller-only); the resolver's system-drive fallback starts only programs from a closed list in administrator-only folders, after
   checking root-owned ancestors and the `MZ` header (an alias is a reparse point, not checked). Admitting it would let the root
   daemon start a user-writable program — a policy decision for the owner and the gate. Also, as of this story the distro's own
   PATH answered `command -v pwsh.exe` with nothing (only `powershell.exe`), unlike D9. The clock probe's program is unchanged.
   **Decided 2026-10-11 (Q3):** the MSI `pwsh` when present and signature-checked, otherwise 5.1, never the Store package —
   story E15.S1b.
2. **Not measured as root.** The implementer may not run anything as root on the owner's machine: the script was timed as the
   Windows user from Windows, and the live contract runs it as the distro's user. The first timer run is the first root run.
3. **The sample is in the run detail only** — not in `SlowParts` / the history line, and `status` is unchanged. E15.S2 decides how
   `status` carries it (`basis: fullRun` + age).
4. **The never-list is stricter than before on the PROGRAM**: `pwsh` (or an absolute `powershell.exe`) with the clock probe's
   exact arguments used to pass; it is refused now. Nothing in the product spells either.
5. **A parameterised template with a new slot kind**, not `CommandTemplate.Fixed`: `Fixed` freezes an argv at first use, and two
   of the script's integers are configuration (the clock reference's reason).
6. `disk.eventsSinceDays` is a display key (a user layer may set it; the work stays bounded by the machine-only `eventsMaxRead`).

**RED first** (against stubs): *Expected Broken(Disk) to be &lt;null&gt; because the Windows disk script is the second member of
the closed set, but found "powershell"*; *Expected Broken([program, .. HealthCommands.WindowsClock.Arguments]) to be
"powershell" because the closed set names the program too, but found &lt;null&gt;*; *Expected rule.Description "PowerShell with
anything but the fixed Windows clock probe" to contain "closed set"*; the parser and collector tests *"not built yet" to contain
"cut"* / *"did not answer within 30 s; its process tree was killed"*. **Break-its**, each restored: the never-list line back to
the clock's arguments (5 red), the sample not stored (2 red), the end-line check removed (4 red), the template removed (3 red) —
messages in [module_tests.md](../research/module_tests.md) *E15.S1*.

**The story's plan round** (coai, 2026-10-10, `proceed`, 1 of 1 reviewer — codex): three findings, all accepted. (1) *Whose
HKCU?* — interop starts Windows programs as the Windows user who started the VM (Microsoft documents it; the cadence
consultation agreed), so the script now also prints `windows.account=` (the SID), the run detail carries it, and the live
contract asserts a user SID (`S-1-5-21-…`) and that the distro it runs in is in the Lxss map. RED first: the fixture's new line
read as malformed (*Expected facts.MalformedLines to be 0, but found 1*) before the parser knew the tag; break-it: the account's
parse replaced by a missing reading → 2 red, restored. (2) *Size and retention of the new payload* — the captured answer is
1 473 bytes; the run detail's `health.windowsDisk` block is a few KB per full run, 6 a day × the run details' existing
retention `runs.historyRetentionDays` (90, `Records/RunRetention.cs`) ≈ 2–3 MB, retired by that same sweep; no new growth
surface. (3) *A window cut by `disk.eventsMaxRead`* — already carried: `StorageEvents.Capped` (`Read >= MaxRead`) is in the
sample and the report (`capped`), tested by the captured answer (`!e.Capped`). The epics' cadence consultation (epics 1–3,
gpt-6-astra, two turns, closed `solved`) changed §3.2 (the share's fenced isolated worker; local pruning off by default) and
§3.3 (a descriptive sentence by default, a Kaplan–Meier K, shadow predictions behind a calibration gate, freshness gates).

**The story's code round** (coai, 2026-10-11, `proceed`, 4 of 4 reviewers — codex): four findings, three accepted, one
rejected. Rejected: (0) "the output list is mutated" — the `List[string]` is the script's own local buffer, written once at
the end; nothing else holds it. Accepted: (1) a failed `Get-Partition` was swallowed into "on no partition" — it now prints
`!<exception type>` and reads "Get-Partition failed for X: (type)"; (2) a registry-named path could be UNC or mapped, and
`Get-Item` on it would send the user's NTLM credentials to that host — the script now opens a file only on a local FIXED
drive and otherwise reads "is not on a local fixed drive, so it was not opened"; (3) the event groups were unbounded — they
are cut at the new key `disk.eventGroupsMax` (10–800, 500, machine-only; 800 × a 64-byte line + 8 KB of other sections stays
under `health.outputCapBytes`' 64 KiB floor) and the script prints the total, so `StorageEvents.GroupsCut` says when the list
is short. **Own review** (Opus) found three more, all fixed: `Join-Path` resolves the drive and threw on an unmounted letter,
ending the whole distro section (now `[IO.Path]::Combine`); the event day was formatted in the user's calendar (a Thai or
Persian year parsed as a valid date; now invariant); and Windows PowerShell 5.1 wrote the answer in the OEM code page, so a
Cyrillic label or user name reached the UTF-8 reader as U+FFFD (now UTF-8 bytes on the raw stdout stream). RED first, each:
*the test named the reason / the `DriveType` check / `GroupsCut` / `[IO.Path]::Combine` / the invariant format / the UTF-8
write, and the script lacked it*. **Break-its** (product code, each restored): the partition reason arm removed → 1 red; the
drive check removed from the script → 1 red; `GroupsCut` forced false → 1 red. Re-measured: 6.1–6.2 s warm, 21.9 s cold under
load (research D10). Left open for the owner and later stories (own review, below 80 % confidence): a timed-out script's
Windows process may survive the WSL tree kill; the `Message` fallback per event is not measured against the ceiling at the
100 000 maximum; a machine with no `stornvme` provider may answer `events.error`; access denied reads as "not there".

**The final code round** (coai, 2026-10-11, `again`, `proceed`, 4 of 4 reviewers — codex): five findings, all accepted and
fixed. The run detail dropped the cut flag (two reviewers) — `StorageEventsReport` now carries `groupsTotal` and `groupsCut`;
`IsVolume` was above complexity 4 — split into `IsLetter` and `IsDiskField`; the parser's tag set could drift from the
script's tags — a test holds every printed tag to `WindowsDiskParsers.KnownTags`; physical disks went on the wire as the
domain record — now `PhysicalDiskReport` (same JSON shape). RED first for the report fields and the wire record; break-its in
[module_tests.md](../research/module_tests.md) *E15.S1*.


## E15.S2 design (2026-10-11, branch `feat/wc-e15s2-virtual-disks`)

**Goal.** From each full run's Windows disk sample (E15.S1), judge three things:
- each virtual disk's GAP: the file size minus what the guest uses;
- its GROWTH per day since the previous full run;
- the hosting volume's FREE space against what its files can still grow.

Show the figures and the verdicts in the run detail and in `status`, with their basis and age. Read-only: no new command
runs, and the never-list is untouched.

**What the code gives today** (mapped 2026-10-11):
- `status` carries every `ThresholdRules.FromFullRun` verdict automatically (`Status/StatusVerdicts.cs:31-58`), with
  `basis: fullRun`, the run id and the age.
- The Windows disk sample is only in the run detail (`HealthReport.WindowsDisk`), not on the history line.
- The distro's `status` has no `host` section: on Linux it is unavailable by design (`Collectors/LinuxProbe.cs:16-17`).
- Docker's `docker system df` totals are in the full run's `PreviewResult.Snapshot.Totals`
  (`Docker/DockerCollector.cs:14-20`), but not in `ThresholdInputs`.
- The distro's own `/` is `ThresholdInputs.Root` (`Collectors/Samples.cs:9-21`, statvfs).

**1. Which Lxss distro is this one? (measured 2026-10-11).**
- `systemctl show-environment` has no `WSL_*` variable. The three `wsl-care` units have an empty `Environment=`.
  `wslinfo` has no distro-name option. A shell started through `wsl.exe` has `WSL_DISTRO_NAME=Ubuntu`.
- So `Health/DistroIdentity.cs` works like this:
  - Any ROOT run whose environment carries `WSL_DISTRO_NAME` (install / update, a collect started by the panel through
    `wsl.exe`) writes `{state}/distro-name.json` (`{name, recordedAt}`). It writes atomically, and only when the name
    changed.
  - The full run takes the environment first, then the file.
  - If neither exists, the own distro's mapping is `none`, with the reason "this distro's name is not known yet — any
    wsl-care command started through wsl.exe records it (the panel's *Run full check now* does)".
  - A recorded name that is not in the Lxss map is also `none`, and the reason names it.

**2. `Health/VirtualDisks.cs` (PURE).** It turns the sample, the root volume, Docker's totals, the distro name and the
previous sizes into `DiskFacts`.
- **Per virtual disk:**
  - name, path, size, sparse;
  - the hosting volume's letter (from the path);
  - `guestUsedBytes`, `mappedBy`, `gapBytes`, `growthBytesPerDay`, and `possibleGrowthBytes`, each a `Reading`.
- **`mappedBy`** is one of three values:
  - `mount`: this distro's own `/`. Guest used = `Root.UsedBytes`. Possible growth = `Root.TotalBytes` − file size,
    because the guest filesystem's size is the disk's virtual size.
  - `dockerDf`: Docker's data disk. Guest used = the sum of `docker system df`'s sizes (images, containers, local volumes,
    build cache). This is labelled "Docker's own totals — an estimate, not the filesystem". Possible growth is unknown.
  - `none`: other distros, which are not mounted here. Gap and possible growth are unknown, and the reason says why.
- **Per volume that hosts a virtual disk:** letter, size, free, the disks on it, their summed growth per day, and their
  known possible growth together with the count of unknown ones.

**3. Growth needs two measured sizes.**
- `Records/SlowParts.cs` gains `VirtualDisks` (`{sampledAt, disks[{name, path, sizeBytes}]}`, about 600 B per line), and
  `LastFullRun` gains it too (the `PreviousClock` pattern, `Collect/CollectRun.cs:402`, `:465`).
- Growth per day = (size now − size then) ÷ elapsed days. It is computed only for the same path, and only when both runs
  measured it.
- Below `disk.growthMinHours` (1 h) the growth is unavailable, with the reason "too close to the previous measurement". A
  10-minute pair would otherwise turn 1 GiB into 144 GiB a day.
- A negative growth is a shrink, shown as it is.

**4. Verdicts (`Thresholds/DiskRules.cs`, spread into `FromFullRun`; all carried by `status`):**
- `disk.vhdxGap`: warn when a mapped gap is above `disk.vhdxGapWarnGib` (20). The value names the file and reads
  "file − guest = gap".
  - When the file is not sparse, the reason adds the advice as TEXT: compaction with WSL stopped, never the sparse switch
    (corrected 2026-10-11 — the first text named `--set-sparse true`; see *E15.S2 correction*).
  - For `dockerDf`, the reason says it is Docker's estimate.
  - When nothing is mapped, the verdict is unknown.
- `disk.vhdxGrowth`: warn when a file grows faster than `disk.vhdxGrowthWarnGibPerDay` (10). It is unknown until two
  measured sizes exist.
- `disk.hostFree`: warn when a hosting volume's free space is below `disk.hostFreeWarnGib` (100), or below the KNOWN
  possible growth of its files. Otherwise it is `ok` ONLY when every file on that volume has a known possible growth. When
  any file's possible growth is unknown, the level is `unknown`, never `ok`. The value still shows the free space, and the
  reason says "the possible growth of N files is not known (…); free X GiB is above the floor and the known growth Y GiB"
  (plan round, 2026-10-11).

**5. Wire.**
- The run detail gains a `disk` section (`Collect/DiskReport.cs`).
- `status` gains a top-level additive `disk`: the newest full run's `DiskReport`, with
  `basis {source: fullRun, runId, evaluatedAt, ageSeconds}`. It is read from the detail with a partial record, as
  `Status/FullRunVerdicts.cs` reads the verdicts.
- The Windows binary's `status` says `disk` is unavailable, because the distro's full run reads the virtual disks.
- `host.vhdx` stays (no wire removal). Its reason is re-worded to point at the distro's `status.disk`.

**6. Keys:**
- `disk.vhdxGapWarnGib` (1–1024, default 20)
- `disk.vhdxGrowthWarnGibPerDay` (1–1024, default 10)
- `disk.hostFreeWarnGib` (1–10240, default 100)
- `disk.growthMinHours` (1–48, default 1)

All four are display keys. They are added to `contracts/config-keys.json`.

**Where the defaults come from** (plan round, 2026-10-11). They are STARTING VALUES, and only one is backed by a
measurement. The README and the key descriptions say so:
- 20 GiB gap: under half of the 43.7 GiB gap measured on Ubuntu's VHDX (D2/D3), so that measured case warns.
- 100 GiB free: about 4 % of the 2.4 TiB system volume (D1). Not measured as a need; it is a floor.
- 10 GiB/day growth: not measured. D3 has only write volume (15.5 GB written in 6.8 h), and writes are not growth.
  The first weeks of `growthBytesPerDay` records will calibrate it.
- 1 h minimum: it keeps the extrapolation factor at 24 or below. Not measured.

**7. Extension.**
- The panel's `disk.vhdx` row (`src_vs_code/src/panel/fieldMap.ts:141`, "arriving E11") becomes a read row on
  `verdicts[id=disk.vhdxGap]`.
- The status bar is unchanged: `disk.*` does not colour it.
- The field-map document block in `architecture.md` is regenerated.

**8. Goldens.**
- The captured collect flow answers the Windows disk script with `WindowsDiskFixture` (as `WindowsDiskFlows.cs:36-37`
  does), so the seven status goldens show real figures.
- Volatile values get normalisation rules.

**9. RED first:**
- **Gap.** D2/D3's numbers (237.7 GiB file, 194.0 GiB used by `/`) give a gap of 43.7 GiB, which warns, with the sparse
  advice for Ubuntu.
- **Growth:**
  - One measurement gives no growth.
  - Two measurements give their difference per day.
  - 10 minutes apart gives "too close".
  - A different path gives no growth.
- **Distro name:**
  - It is read from the environment.
  - Without the environment, it is read from the file.
  - With neither, the mapping is `none` with the reason.
  - A name that is not in the Lxss map is also `none`.
- **Docker:** Docker's disk is mapped by df and labelled an estimate.
- **Host free:**
  - `disk.hostFree` warns below 100 GiB.
  - It also warns below the known possible growth.
  - It counts the files whose growth is unknown.
  - 150 GiB free with 20 GiB of known growth and one unknown file reads `unknown`, never `ok`.
- **`status`:**
  - It carries `disk` with its age.
  - With no full run, it says so.
  - The Windows binary says the distro reads it.

There will be break-its on the product lines.

**10. Docs:** README (the keys), `module_daemon.md`, `module_tests.md`, `architecture.md` (the field map), this plan's
as-built section.

**Deviations from the story row, decided here:**
- `mappedBy` is `mount | dockerDf | none`, not `mount | size | none`. Mapping a block device by size identifies it, but it
  gives no guest usage, because those filesystems are not mounted here (D3).
- The keys are in GiB (`…Gib`), not "Gb", because Windows reports the file sizes in binary units.
- `host.vhdx` is re-worded, not removed.

## E15.S2 as built (2026-10-11, branch `feat/wc-e15s2-virtual-disks`)

**What shipped.** `Health/DistroIdentity.cs` (`WSL_DISTRO_NAME` first, then `{state}/distro-name.json`, written by a ROOT `collect`
that carries the variable — atomically, only when the name changed; a name is believed only when it passes WSL's rule, the
extension's `DISTRO_NAME` pattern); `Health/VirtualDisks.cs` (PURE) → `Health/VirtualDiskFacts.cs` (`DiskFacts`: per disk the
volume letter, `mappedBy` `mount | dockerDf | none`, guest used, gap, growth per day, possible growth, each a `Reading`; per hosting
volume size, free, the disks, summed growth, known possible growth and the disks whose growth is unknown); `Thresholds/DiskRules.cs`
(`disk.vhdxGap`, `disk.vhdxGrowth`, `disk.hostFree`, spread into `ThresholdRules.FromFullRun` after `disk.discard`, so `status`
carries them with the run and their age); `SlowParts.VirtualDisks` + `LastFullRun.VirtualDisks` (the sizes on the history line,
the previous run's for growth); `Collect/DiskReport.cs` (the run detail's `disk`) and `Status/StatusDisk.cs` (`status.disk` with
`basis {source: fullRun, runId, evaluatedAt, ageSeconds}`, read from the same detail file as the verdicts through
`RunDetailVerdicts.Disk`); the Windows binary answers `disk` unavailable and `host.vhdx` points at it. Keys `disk.vhdxGapWarnGib`
(1–1024, 20), `disk.vhdxGrowthWarnGibPerDay` (1–1024, 10), `disk.hostFreeWarnGib` (1–10240, 100), `disk.growthMinHours` (1–48, 1),
display keys, their descriptions and the README naming them STARTING VALUES with their provenance; `contracts/config-keys.json`
regenerated. The panel's `disk.vhdx` row reads `verdicts[id=disk.vhdxGap]`; the status bar is unchanged (a test holds that the
three `disk.*` verdicts do not colour it); the field-map block in `architecture.md` regenerated. The scenarios' captured collect
answers the disk script with the 2026-10-10 capture, so the seven status goldens carry the three verdicts and `status.json` the
`disk` section (regenerated on Linux, WSL `Ubuntu`, from a copy under `/tmp`).

**Deviations.**

1. **Who records the name.** `collect` run as root (synchronously, or as `collect --detach` — the panel's *Run full check now* is
   `wsl.exe -u root … collect --detach`, so the detaching process carries the variable and the systemd unit that does the run
   reads the file). Not `install.sh` / update: it runs under `sudo`, whose default `env_reset` drops the variable (sudo's
   documented default, not measured here); not `act`. The design said "any root run"; one verb is the smallest sound place.
2. **The environment is an argument**: `CollectContext.DistroVariable`, set by the CLI from the process — so no test reads the
   test process's own environment; the scenarios' captured collect REMOVES `WSL_DISTRO_NAME` (a test run from a WSL shell inherits
   it), so the goldens are the systemd unit's case. (A first version removed it for EVERY scenario; on Linux in WSL that split the
   archive's side folder — `ArchiveNames` reads the same variable in the test process and in the CLI — and two `ArchiveRunFlows`
   went red, so the scrub is now only in `CollectFlows.Captured`.) In the goldens no distro is mapped, Docker's data disk is (by
   `docker system df`), and `disk.hostFree` is `unknown`. The `mount` mapping is proved by the unit tests and by `VirtualDiskFlows` on Linux.
3. **The gap test reads 43.6 GiB, not 43.7**: the exact bytes (255 195 086 848 − 208 337 252 352 = 46 857 834 496) are 43.6 GiB;
   D3's 43.7 rounded both sides first. It still warns at 20.
4. **No `hostGrowth` field** (the story row's `status.disk {vhdx[], volumes[], hostGrowth}`): each volume carries
   `growthBytesPerDay`, `knownPossibleGrowthBytes` and `unknownGrowth` (design §2's shape).
5. **An unreadable Lxss map leaves the whole disk section unavailable** with its reason (Docker's disk alone is not judged): this
   distro's mapping needs the map. Docker's data disk that is absent or unread is simply not listed.
6. **`host.vhdx`'s reason is `StatusDisk.ReadByTheDistro`** — one sentence for both, and `VhdxNotCollected` is gone.

**RED first** (against stubs answering "not built yet" and a `DiskRules` returning no verdict): 45 of 227 red, each for the
missing behaviour — *Expected type to be …Reading`1+Available[[…DiskFacts…]] because not built yet, but found …Unavailable…*
(the facts); *Expected DistroIdentity.NotKnownYet "not built yet" to contain "wsl.exe"*; *Expected Record(name, Now) "not built
yet" to contain "is not a distribution name"*; *Expected Record(string.Empty, Now) to be empty, but found "not built yet"*;
*Expected verdicts.Select(v => v.Id) to be equal to {"disk.vhdxGap", "disk.vhdxGrowth", "disk.hostFree"}, but found empty
collection*; *Expected ids {… "disk.discard", "docker.A4", …} to contain items {"disk.discard", "disk.vhdxGap", …} in order*; the
verdict tests *Sequence contains no matching element*; *Expected disk.Available to be True because not built yet, but found
False*; *Expected StatusDisk.OnWindows.Reason "not built yet" to contain "the distro's full run"*; *Expected type to be
…Available[[…DiskReport…]] because not read, but found …Unavailable…*. The `host.vhdx` re-wording: `ProbeTests` red first
(*… to be the same string, but they differ at index 4*). Then green: 227 of 227.

**Break-its** (product code, each restored, each named test red alone): gap = file (not file − guest) → *Expected
Value(ubuntu.GapBytes) to be 46857834496L … but found 255195086848L*; every distro mapped by mount → *Expected other.MappedBy to be
MappedBy.None … but found MappedBy.Mount*; the `disk.growthMinHours` arm removed → the 10-minute pair reads a growth; the
same-path check removed → *Expected …GrowthBytesPerDay.IsAvailable to be False … but found True*; the unknown-growth arm of
`disk.hostFree` removed → *Expected free.Level to be Level.Unknown … but found Level.Ok* (the 150 / 20 / 1 case); the known-growth
arm removed → *Expected free.Level to be Level.Warn … but found Level.Ok*; the sparse arm removed → the reason lacks
`wsl --manage Ubuntu --set-sparse true`; the file before the environment → *The_environment_names_the_distro_first* red; the
"unchanged" arm removed → the file is rewritten (*differ on line 4*); `status.disk` without its basis → *Expected disk.Basis to
be …VerdictBasis*; the detail's disk not read → *…Available[[…DiskReport…]] … but found …Unavailable*; `DiskRules` not spread into
the full-run list → the order test red; the panel's row back to `arriving` → 2 red (`fieldMap.test`, `viewModel.test`); and on
Linux the root `collect`'s `RecordDistro` call commented out → `VirtualDiskFlows` red (no `distro-name.json`).

**The story's code round** (coai, 2026-10-11, `proceed`, 4 of 4 reviewers — codex): four findings. Three were accepted:
- `disk.vhdxGap` and `disk.vhdxGrowth` judged only the disks with a figure, so they could read `ok` while a disk was
  unknown. This finding came from two reviewers. A known breach still warns; otherwise the verdict is now `unknown`. For the
  gap that means a disk mapped to its guest whose gap could not be read; for growth, any file with no growth yet.
- The daemon's distro-name rule could drift from the extension's `DISTRO_NAME`. An architecture test now holds them equal.

One was rejected: "nullable lists in `DiskReport` should default to `[]`". Doctrine §7 is about immutability, and that
holds. The null is this repo's JSON-edge contract ("available": false, a reason, no value), so an unavailable section never
reads as "measured, none".

**Own review** (Opus), all fixed:
- Without this distro's name, Docker's estimate alone made the gap `ok`. Now it is `unknown`, or `warn` when Docker's
  gap breaches the limit (as it does in the capture, about 34 GiB).
- `HostWithFree` was at complexity 5. It is now a tuple switch.
- No test proved that only root records the name. A scenario on Linux now proves it.
- The growth baseline is the newest sizes at least `disk.growthMinHours` older, so a manual run soon after a timer run
  still has a growth.
- A clock that moved back says so.
- A partial history line is not a measurement.
- A disk section that does not parse costs only `disk`, not the carried verdicts.
- The `disk.*` keys moved to `Config/ConfigKeys.Disk.cs`. `ConfigKeys.Numbers.cs` was 945 lines on main and is now
  933; it is over 800 lines from before this story, and that is left for the owner.

Not taken:
- A cloned distro (`wsl --export` / `--import`) carries the old `distro-name.json`. That is a known limit, written in
  module_daemon.
- The `--set-sparse` caveat: WSL 2.7.10's `wsl --help` lists `--set-sparse <true|false>` with no unsafe flag, so the
  text stays as it is.

RED messages and break-its are in [module_tests.md](../research/module_tests.md) *E15.S2*.

The goldens were re-checked on Linux. They are unchanged, because the capture's gap already warned.

The Linux `FixturePrivacyTests` failure on the owner's machine (POST_DEPLOY.md names the local user) comes from before
this story, from main.

**The final code round** (coai, 2026-10-11, `again`, `proceed`, 4 of 4 reviewers — codex): six findings, two accepted, four
rejected with reasons. Accepted:
- The growth baseline is now chosen PER FILE: the newest earlier VALID measurement of that path, at least
  `disk.growthMinHours` old. A newer run that missed the file, or a partial entry, no longer hides an older measurement.
- The history is walked lazily instead of being copied into a list.

Rejected:
- **The scenario harness's `Extra` dictionary:** it is the harness's own API, used by every flow.
- **"JavaScript `$` matches before a final newline":** verified false; `/…$/.test('Ubuntu\n')` is `false`, the same as the
  daemon's `\z`.
- **The cloned distro:** no unelevated, non-raw fact ties a filesystem to one VHDX. The case heals on the clone's first
  `wsl.exe` root collect. It is a known limit in module_daemon.
- **Indexing the previous disks by path:** tens of disks at most.

The goldens were regenerated on Linux: the growth reason now reads "no earlier full run measured this file".

## E15.S2 correction — no sparse-VHD advice (2026-10-11, branch `fix/wc-no-sparse-advice`)

E15.S2 shipped a gap advice that named `wsl --manage <distro> --set-sparse true`. That was a defect:
- The project's own research says WSL disabled sparse VHD from 2.5.6/2.5.8 after data-corruption reports, that a sparse VHDX
  cannot be compacted, and that the "enable sparseVhd" advice must go
  ([2026-10-02_competitor_survey.md](../research/2026-10-02_competitor_survey.md), microsoft/WSL#13075).
- The never-list refuses `--set-sparse` for the same reason.
- PLAN_wsl_care_daemon.md 0.3 gives the remedy: compact with WSL stopped (`Optimize-VHD` or `diskpart compact vdisk`),
  **not** `--set-sparse`.

The story row and design §4 asked for the sparse advice, and no gate round caught it. It was found while reading the never-list
for E15.S1b.

Fixed: the advice is now compaction with WSL stopped (text only; wsl-care never compacts), plus the warning that sparse VHD is
not advised. RED first: *Did not expect gap.Reason "… wsl --manage Ubuntu --set-sparse true …" to contain "--set-sparse true"*.
A sparse file gets neither the switch nor the compaction advice. WSL 2.7.10's `wsl --help` still lists `--set-sparse`; listing
it is not advice to use it.

## E15.S2b design — install records the distro name (2026-10-11, branch `feat/wc-install-distro-name`)

**Why.** E15.S2 finds this distro's own virtual disk by NAME. A systemd unit has no `WSL_DISTRO_NAME`, and `sudo`'s
`env_reset` drops it from `install.sh`. So until someone presses *Run full check now*, no disk is mapped by mount, and the gap is
`unknown`. **Decided (the coordinator, 2026-10-11):** install records the name through an explicit, validated argument. It does
not preserve the environment through sudo.

**What.**
1. **The argument.** `install.sh` gains `--distro-name <name>`. The documented one-liner passes
   `--distro-name "$WSL_DISTRO_NAME"`; the installing user's shell expands it BEFORE `sudo` runs.
   - A NON-EMPTY name that fails the distro-name rule is refused by `usage_fail` before anything changes. The rule is a letter or
     digit, then up to 63 letters, digits, `.`, `_` or `-` — the rule of `DistroIdentity.NamePatternText` and the extension's
     `DISTRO_NAME`.
   - An EMPTY value (the one-liner run outside WSL) is no name. Install says so and goes on.
   - `--uninstall` refuses the argument.
2. **One writer.** `install.sh` does not write the file itself. Its first full run already exists (`first_run`, `install.sh:1046`).
   It runs root `collect`, and a root `collect` records `WSL_DISTRO_NAME` through `DistroIdentity.Record` (E15.S2). So
   `first_run` passes the validated name as that variable: `env WSL_DISTRO_NAME=<name> … collect`.
   - The file keeps ONE writer and ONE format.
   - The daemon validates the name a second time.
   - A later root collect through `wsl.exe` still refreshes it.
   - `--dry-run` prints `would record the distro name <name> (through the first full run)`.
3. **The extension's one-liner waits.** `INSTALL_COMMAND` (`src_vs_code/src/install/installCommand.ts:44`) pins the
   `install.sh` of a released tag, and the released `install.sh` refuses an unknown argument. The extension adds
   `--distro-name "$WSL_DISTRO_NAME"` only after a daemon release ships this change; that is a recorded follow-up. The README's
   one-liner gains it now, for the installer at `main`.

**Tests (Linux scenarios, in the `Install*Flows` style), RED first:**
- `--distro-name Ubuntu` → after install, `DistroIdentity.Read` gives `Ubuntu`.
- `--distro-name ''` → no file, and the output names the reason.
- No argument → no file.
- Each of `-x`, `a b`, `a/b`, `a;b`, a 65-character name and `$(x)` is refused with nothing changed. The same table is asserted
  against `DistroIdentity`'s rule, so the shell and C# accept the same names.
- `--uninstall --distro-name x` is refused.
- `--dry-run` prints the line and writes nothing.

Break-its on `install.sh`'s validation and on the `env` pass. Docs: README (the argument, the one-liner), module_daemon (who
records the name), module_tests.

## E15.S2b as built (2026-10-11, branch `feat/wc-install-distro-name`)

**What shipped.**
- `install.sh --distro-name <name>`. The value is checked against `DISTRO_NAME_PATTERN`
  (`^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$`) through the installer's existing `matches` (which first refuses every byte outside
  `[0-9A-Za-z._-]`, a newline included). A name outside the rule is a `usage_fail` (exit 2) before anything changes. The
  argument with NO operand (the last argument) is a usage error; an explicitly EMPTY operand (`--distro-name ''`) is allowed and
  means "no name". `--uninstall` refuses the argument, also when it is empty.
- `first_run` starts the installed binary as `env WSL_DISTRO_NAME=<name> timeout … collect` (`with_distro_name`). Without a
  name, the environment is left as it is. The root `collect` records the name through `DistroIdentity.Record` — the file keeps
  ONE writer and ONE format, and the daemon checks the name a second time. The installer never writes `distro-name.json`.
- What the installer says: `recording the distro name <name> (through the first full run, as WSL_DISTRO_NAME)`; under
  `--dry-run`, `would record the distro name <name> (through the first full run)`; for an empty value, that no name is
  recorded now and that a root collect through `wsl.exe` (the panel's *Run full check now*) records it later. Without the
  argument it says nothing.
- `usage()`, the script's header and the README: the argument, and the one-liner
  `… | sudo sh -s -- --distro-name "$WSL_DISTRO_NAME"`. The README's table names `/var/lib/wsl-care/distro-name.json` (written
  by the first full run, never by the installer).
- The rule is held equal three ways: `ArchitectureTests.The_installer_and_the_daemon_share_one_distro_name_rule` compares the
  pattern bodies (every OS), and `InstallDistroNameFlows.The_installer_and_the_daemon_accept_exactly_the_same_names` runs
  fifteen names through `install.sh` and `DistroIdentity.IsName` (Linux).

**Tests.** `Scenarios/InstallDistroNameFlows` (10 flows, Linux legs), one architecture test, one `DistroIdentityTests` guard
(64 characters recorded, 65 refused). RED messages and break-its are in [module_tests.md](../research/module_tests.md)
*E15.S2b*. Totals at the end: Windows Core 2554 (0 failed), Cli 383 (0 failed), Scenarios 550 (0 failed, the Linux-only
flows skipped); Linux (WSL `Ubuntu`, a copy under `/tmp`) Scenarios 550 (0 failed, 3 skipped), Core 2554 (0 failed), Cli 383
(0 failed).

**Deviations.**
1. **The first full run in the scenarios is the BUILT CLI, not the fake.** The install world's binary is a stub that hands its
   argv to a fake, so the design's "after install, `DistroIdentity.Read` gives `Ubuntu`" could not be observed there. The new
   flows give the stub a `StubPrelude` that hands `collect` to the built CLI in a captured scenario's sandbox
   (`CollectFlows.Captured`, `ClaimsRoot`), keeping `WSL_DISTRO_NAME` as `install.sh` passed it. The flow reads the file with
   the product's serializer and then runs a second `collect` without the variable (the timer's case): it maps Ubuntu's disk by
   `mount`. That is the product's own `Read`, so the design's assertion holds through the product, not through a test copy.
2. **The install world removes an inherited `WSL_DISTRO_NAME`** (sudo's `env_reset`): a test run from a WSL shell inherits it,
   and without the removal the no-argument flow would record the shell's name. `env` joined the world's real-tool allowlist,
   because the first run now uses it.
3. **The re-run line keeps an empty argument** (own review, a defect the empty operand made reachable). The line a refusal
   prints ("re-run: curl … | sudo sh -s -- …") was built from `"$*"`, which drops an empty argument. So
   `--distro-name ''` came back as a bare `--distro-name`, which is now a usage error for the person who pastes it. The
   arguments are now joined one by one, with an empty one shown as `''`. Red first; one break-it.
4. **`--uninstall --distro-name ''` is refused too** (the design said "refuses the argument"; `--set-default-user ''` is still
   let through by the older check, which tests the value, not the argument).

**The story's code round** (coai, 2026-10-12, `proceed`, 4 of 4 reviewers — codex): three findings.
- **Accepted (two reviewers):** an explicitly EMPTY `--distro-name ''` now starts the first run WITHOUT an inherited
  `WSL_DISTRO_NAME` (`(unset WSL_DISTRO_NAME; …)`, POSIX), because the person asked for "no name".
- **Rejected, for the case where the argument is OMITTED:** the run keeps the environment as it is. Root entered through
  `wsl.exe -u root` carries the value WSL set for this distro — the same trust as any root collect through `wsl.exe`, which E15.S2
  already records. The documented one-liner goes through sudo, which removes it. A flow now holds this.
- **Accepted:** the empty-value message no longer claims why the value is empty.

The fix landed before its tests ran, so it was proven by revert on Linux (`/tmp/wc-e15s2b-r1-20261012T080620Z`): the fix
reverted in the copy → 2 red (*Expected File.Exists(…) to be False … but found True*; the message still contained "was not
set"); restored → 12 of 12 green. In the same full Linux run, 2 timing-sensitive scenarios outside this story failed (a 3 s
status budget at 4.6 s under load; a pid reused in the umask flow). Both passed alone on a rerun.

**Open — the extension's follow-up (design point 3).** `INSTALL_COMMAND` (`src_vs_code/src/install/installCommand.ts:44`)
pins the `install.sh` of a RELEASED tag, and a released `install.sh` refuses an unknown argument. So the extension adds
`--distro-name "$WSL_DISTRO_NAME"` only after a daemon release ships this change. Not changed here.

## E15.S5 design — Windows memory pressure, one rule on both roads (2026-10-12, branch `feat/wc-e15s5-host-memory`)

**Goal.** Three host verdicts, judged by ONE pure function, on whichever road has the facts:
- `host.commit` — Windows' commit charge against its commit limit.
- `host.compression` — the Memory Compression process's working set.
- `host.vmmem` — the WSL VM's working set as Windows sees it.

Read-only: nothing is acted on.

**The two roads (mapped 2026-10-12):**
- **The Windows binary's `status`.** `Collectors/WindowsProbe.cs` already calls `GlobalMemoryStatusEx`. `ullTotalPageFile` is the
  commit limit, and `ullAvailPageFile` is what can still be committed, so charge = total − available. That is the same quantity as
  the full run's `CommittedBytes` / `CommitLimit`. Neither field is read today (`Win32Counters.Memory`, `WindowsProbe.cs:64-70`).
  `VmmemWorkingSet()` is read; Memory Compression is not.
- **The distro's full run.** E15.S1's disk script gives `WindowsDiskFacts.CommitBytes`, `CommitLimitBytes` and
  `CompressionBytes`. It gives no vmmem figure.

**1. Facts.** `Health/HostMemoryFacts.cs` holds `HostMemoryFacts(Reading<long> CommitBytes, Reading<long> CommitLimitBytes,
Reading<long> CompressionBytes, Reading<long> VmmemBytes, Reading<long> HostTotalBytes)`. It has two builders:
- `FromProbe(HostSample)` (Windows);
- `FromWindowsDisk(WindowsDiskSample)` (the full run). Here `VmmemBytes` is unknown, with the reason "vmmemWSL is read by the
  Windows binary's status".

The Windows sample gains `HostSample.Commit` (from `TotalPageFile` / `AvailablePageFile`) and `HostSample.CompressionBytes`
(`Process.GetProcessesByName("Memory Compression")` from the kernel's snapshot, as `vmmemWSL` is read: nothing is started, and
no handle is opened). `IWindowsCounters` gains the two readers, and its test fakes follow. Each reader fails ALONE (plan
round): a `GlobalMemoryStatusEx` failure makes only `Commit` unknown. For Memory Compression, no process, a process that exits
between the listing and the read, or a refused read makes only `CompressionBytes` unknown, with the exception named. The status
goes on, and a test plants each failure.

**2. The one rule** (`Thresholds/HostRules.cs`, PURE): `Verdicts(HostMemoryFacts, EffectiveConfig)`.
- `host.commit`: warn above `thresholds.hostCommitWarnPercent` (80); critical above `thresholds.hostCommitCriticalPercent` (90);
  the value is "charge of limit = %".
- `host.compression`: warn above `thresholds.hostCompressionWarnGb` (8 GiB).
- `host.vmmem`: warn above the EXISTING `wslConfig.vmmemAdviceGb` (one number for one idea). The reason is
  `VmmemAdvice.For(...)`'s text, so the advice and the verdict cannot disagree.
- An unread figure is `unknown` with its reason, never `ok`.
- Load-time rule: critical > warn. A layer that inverts them is refused, by the existing coupled-limits check (`NumberRules`).

**3. Where each road judges:**
- **Full run:** `HostRules` is spread into `ThresholdRules.FromFullRun` over `FromWindowsDisk(inputs.Health.WindowsDisk)`, so the
  distro's `status` carries these verdicts with `basis: fullRun` and their age.
- **Windows binary:** `StatusVerdicts.From` judges `HostRules` over `FromProbe(sample.Host)` with `basis: sample`. A sampled
  verdict REPLACES the carried full-run verdict of the same id, because the newer, direct reading wins. So a `status` never lists
  an id twice. The distro's binary has no host sample and keeps the carried ones.

**4. Keys:** `thresholds.hostCommitWarnPercent` (50–99, 80), `thresholds.hostCommitCriticalPercent` (51–100, 90),
`thresholds.hostCompressionWarnGb` (1–256, 8). These are display keys, with a coupled rule warn < critical. The defaults are
PROVISIONAL starting values (plan round). Measured on the owner's machine, 4.4 h after boot under heavy agent load
([2026-10-12_host_memory.md](../research/2026-10-12_host_memory.md)):
- commit was 68.8 %, the SAME on both roads (perf counter and `MEMORYSTATUSEX`, to 0.01 %);
- compression was 6.0 GiB;
- vmmem was 18.9 GiB.

So the defaults stay quiet on a normal heavy day. The records of every full run calibrate them later.

**5. Extension:** `src_vs_code/src/statusBar/statusBarModel.ts` `RELEVANT_VERDICT_PREFIXES` gains `host.`, per the plan's stated
assumption ("`host.*` verdicts colour the status bar"). Tests: `statusBar.test.ts`.

**6. RED first:**
- 86 % warns.
- 91 % is critical.
- 79.9 % is ok.
- A limit of 0 or an unread charge is `unknown`.
- 8.1 GiB of compression warns.
- vmmem above `wslConfig.vmmemAdviceGb` warns, and its reason is the advice text.
- **The same function on both bases:** equal figures built through `FromProbe` and through `FromWindowsDisk` give the same verdicts
  (vmmem aside).
- The Windows status lists `host.commit` once, with `basis: sample`, even when its history carries a full-run one.
- The distro status carries the full-run `host.*` verdicts.
- `GlobalMemoryStatusEx`'s page-file fields map to charge and limit (a fake counter).
- The status bar colours on `host.commit` warn.
- **Across the boundary** (plan round): the extension's status-bar model reads the HEAD GOLDEN `status.json` that the real
  daemon writes (the captured full run answers the disk script, so it carries `host.commit` / `host.compression` with
  `basis: fullRun`), and colours from those verdicts. The id or the shape of a verdict cannot drift between the two halves
  unseen. `module_tests` records the flow.

Break-its on the edges and on the replacement. Docs: README (the keys), `module_daemon`, `module_vs_code`, `module_tests`, the
as-built section.

**Out of scope, recorded:** a vmmem line in the distro's disk script. That would let the full run judge `host.vmmem` too. It
waits until E15.S1b (which changes that script) has merged.

## E15.S5 as built (2026-10-12, branch `feat/wc-e15s5-host-memory`)

**What shipped.** The design above, in full. `Health/HostMemoryFacts.cs` (the facts, two builders), `Thresholds/HostRules.cs`
(the ONE pure rule: `host.commit`, `host.compression`, `host.vmmem`), `Collectors/ProcessWorkingSets.cs` (the working-set reader
that fails alone), `HostCommit` and the two new `HostSample` members, `IWindowsCounters.Commit()` / `CompressionWorkingSet()`, the
three `thresholds.*` keys with their coupled rule, `HostRules` spread into `ThresholdRules.FromFullRun` after `disk.hostFree`, the
replacement rule in `StatusVerdicts.From`, and `host.` in the extension's `RELEVANT_VERDICT_PREFIXES`. Nothing is acted on.

**Deviations (the smallest sound change each time):**
- `HostMemoryFacts` carries a sixth member, the `.wslconfig` audit, and both builders take it. `host.vmmem`'s reason is
  `VmmemAdvice.For`'s text, and that text needs the audit; with it, the verdict and the Windows status's `host.vmmemAdvice` have
  the SAME input. `status` on Windows reads `.wslconfig` ONCE and hands that audit to both (`StatusVerdicts.From` gained an
  overload with it; the old signature passes the "status does not read it" reason, as before).
- `VmmemAdvice.For` was widened with an overload over the host's RAM in bytes (the facts hold a `Reading<long>`, not a
  `HostMemory`); the old overload delegates to it, so there is one text.
- `host.vmmem`'s level follows the advice (warn exactly when the advice speaks). They cannot disagree by construction.
- `ProcessWorkingSets.Sum` reads `vmmemWSL` too (reuse: one reader for both process names). So `vmmemWSL` now also fails alone;
  before, a `vmmemWSL` that exited between the listing and the read threw out of `status`.
- `Memory()` and `Commit()` each make their own `GlobalMemoryStatusEx` call, so one failure costs one figure.
- The replacement is unconditional: on the Windows binary a sampled `host.*` verdict replaces the carried one even when the sample
  is `unknown` (the Windows binary's own state holds no full run that judges these; the newer, direct reading wins). A sampled id
  the full run does not judge would be added after the carried ones (none today).
- The `status --json` `host` block is unchanged; the new figures travel as the verdicts' values.
- The extension's head golden now colours the bar warn: its captured full run (2026-10-10) read 9.86 GiB of Memory Compression,
  above the provisional 8. The status-bar tests that colour one verdict at a time start from that golden with `host.compression`
  set to ok (`calm()`), so each keeps its teeth.
- ~~No architecture.md change~~ — wrong, corrected in the code round: #129 split most of architecture.md into the module docs,
  but its section *The verdicts in `status`, `productVersion` and the golden contracts (E5.S0)* stays there, and it now carries
  the E15.S5 rule (the Windows side's sampled `host.*`, the replacement, the appended `host.vmmem`, the bar).
- **The full run judges no `host.vmmem`** (code round and own review, below): only `host.commit` and `host.compression` come from
  `FromWindowsDisk` (`HostRules.FullRunVerdicts`); all three come from `FromProbe` (`HostRules.Verdicts`). "The same function on
  both bases" now reads: the same `Commit` and `Compression` functions judge both roads; vmmem is the Windows binary's alone.

**Tests, RED first** (every message and break-it is in `module_tests.md`, *E15.S5*): 25 of 242 Core checks and the CLI check red
against stubs, each for the named reason (no verdicts; `host.commit` at index -1; an empty `host.commit` list; the coupled layer
not refused; the charge read as the available figure; the planted exceptions escaping; the processes not released); the
extension 3 of 18 red (*actual: 'none', expected: 'warn'*). Seven break-its on product code, each restored byte for byte and
green again: the commit and compression edges, the replacement, the reader's catch, the coupled rule, the Windows status without
its audit, and `host.commit` excluded from the bar.

**Suites** (2026-10-12, a heavily loaded machine): Windows — Core 2654 (2573 passed, 81 skipped, 0 failed), CLI 392 (390, 2
skipped), Scenarios 609 (325 passed, 283 skipped, 1 failed: `StatusFlows.Status_json_on_this_binarys_side_answers_within_the_budget…`
at 3.55 s against its 3 s budget; alone it passed 2 of 3 runs, 4.15 s the third; the two new process snapshots cost about 17 ms and
43 ms on this machine, so the budget is lost to the load, not to them — CI is the authority); Linux (WSL Ubuntu, one
folder `/tmp/wc-e15s5-linux-20261012T184227Z`) — the seven status goldens regenerated there and `GoldenContractTests` green (7);
Core 2654 (2626 passed, 27 skipped, 1 failed: `ClockGuardTests.A_wall_clock_that_jumps_during_the_measurement_voids_it` read
+7199.9 s for +7200.0 s under load, green on a rerun, 44 of 44), CLI 392 (379 passed, 13 skipped), Scenarios 609 (602 passed, 5
skipped, 2 failed: the two `FixturePrivacyTests`, because the run's `HOME` folder was named `home` and the test takes `$HOME`'s
name as a user name; with another folder name both are green, 57 of 57); the extension 1250 (1247 passed,
1 skipped, 2 failed: two `fakeWsl.test` cases that timed out at 15 s under load and pass alone, 49 of 49); lint, format and
plan-lifecycle clean.

**After the rebase onto `origin/main` (90b36369)**, everything again: Windows — Core 2749 (2668 passed, 81 skipped), CLI 410
(408, 2 skipped), Scenarios 616 (332 passed, 284 skipped), 0 failed; Linux (the same folder) — the goldens unchanged
(`GoldenContractTests` green without a write), Core 2749 (1 failed: the same `ClockGuardTests` case, green alone, 45 of 45), CLI
410 (391 passed, 19 skipped), Scenarios 616 (609 passed, 7 skipped), 0 failed; the extension 1250 (1249 passed, 1 skipped); lint,
format and plan-lifecycle clean.

**Code round and own review (2026-10-12).** Three fixes and two checks, each fix red first.
- **The distro's `host.vmmem` was permanently `unknown`** (own review, confidence 80). The full run reads no vmmem, so every distro
  `status` carried `host.vmmem: unknown`, the status bar's tooltip listed it forever, and `CollectRun` wrote it into every run's
  history as a warning. The full-run road now judges commit and compression only (`HostRules.FullRunVerdicts`, spread into
  `ThresholdRules.FromFullRun`). On the Windows binary the sampled `host.vmmem` replaces nothing and is appended after the carried
  verdicts (after `npm.cache`), once. The seven Linux status goldens were regenerated.
- **Only the Windows side samples the host** (coai finding 2). `StatusVerdicts` judges `HostRules` only when `ProbeSample.Side` is
  `Windows`. A distro sample that ever carried a host part keeps the carried `host.*` verdicts, unchanged.
- **architecture.md** (coai finding 0): the E5.S0 verdicts section gained the E15.S5 rule (see the corrected deviation above).
- **Checked, no change**: (a) `MEMORYSTATUSEX`'s page-file fields are the smaller of the system's and a job's commit limits; the
  daemon sets no job commit limit (WR-D3's job is `JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE` alone), so they are the system's — now said
  in `HostCommit.FromPageFile`'s remarks. (b) The coupled rule makes a machine-layer contradiction an error (observe-only) and a
  user-layer one a notice. That is how EVERY coupled rule and every invalid value behaves (`ConfigLoader.HoldRules` /
  `ApplyEntry`); there is no softer path for display keys (a display key out of range in the machine layer is an error too), and
  no other display warn/critical pair has a coupled rule. So the rule stays as it is.

Red (8 of 40 Core checks, 1 of 19 extension checks): *Expected HostRules.FullRunVerdicts(Facts(), Config()).Select(v => v.Id) to be
equal to {"host.commit", "host.compression"}, but {"host.commit", "host.compression", "host.vmmem"} contains 1 item(s) too many*; *Expected
ids {…} to not contain "host.vmmem" because the full run does not read vmmem, so it judges none*; *Expected ids.IndexOf("host.vmmem") to
be 36, but found 31*; *Expected Find(verdicts, "host.commit") to be* the carried verdict (the distro sample's host part had replaced
it); *Expected warnings … not to contain* a `host.vmmem` record (the collect's history line); the extension: *The input was expected
to not match the regular expression /host.vmmem: unknown/. Input: '… host.vmmem: unknown — …'*. Break-its (product code, each
restored byte for byte): `ThresholdRules` back to `HostRules.Verdicts` → 6 red (the full-run list, the history line, the distro
status, the Windows order and position); the `Side: HostSide.Windows` arm removed → *A_distro_sample_with_a_host_part_keeps_the_carried_host_verdicts*
red.

The Windows suite then found one more test to follow the contract: `Scenarios/CollectFlows` compared the Windows status's ids
with the run's exactly; it now expects the sampled `host.vmmem` after the carried verdicts, before the MCP servers' own.

**Suites of the code round** (rebased onto `origin/main` 47dbc1fa, 2026-10-14; the machine still heavily loaded): Windows — Core
2753 (2672 passed, 81 skipped), CLI 410 (408, 2 skipped), Scenarios 616 (330 passed, 284 skipped, 2 failed: two
`FetchWindowsDaemonFlows` cases whose Git-bash script did not exit within 60 s; the class alone is green, 43 of 43; an earlier
run under heavier load lost 27 cases the same way, all time limits); Linux, a NEW folder
`/tmp/testsToDelete-wc-e15s5-20261014T193812Z` — the goldens unchanged (`GoldenContractTests` green without a write), Core 2753
(2714 passed, 39 skipped), CLI 410 (391, 19 skipped), Scenarios 616 (609 passed, 7 skipped), 0 failed; the goldens themselves
were written in `/tmp/wc-e15s5-round2-linux-20261012T202048Z` (its suites: one `StatusFlows` time budget, 3.10 s for 3 s at a
load average of 35); the extension 1251 (1250 passed, 1 skipped); lint, format, plan-lifecycle and the rules check clean.
Rebased again onto `origin/main` 0d9be6a3 (WR-D2, the disk-hygiene plan; no status, threshold or contract file changed) and
re-run in full, 0 failed everywhere: Windows — Core 2783 (2702 passed, 81 skipped), CLI 424 (422, 2 skipped), Scenarios 621 (335
passed, 286 skipped); Linux, a NEW folder `/tmp/testsToDelete-wc-e15s5-20261014T205832Z` — goldens unchanged, Core 2783 (2714, 69
skipped), CLI 424 (394, 30 skipped), Scenarios 621 (613, 8 skipped); the extension 1251 (1250, 1 skipped); lint, format,
plan-lifecycle and the rules check clean.
Then onto `b741a934` (WR-D4) and `28ebdeed` (a plan-only change), on a machine at 100 % CPU (Linux load average 40–60):
Windows Core 2791 and CLI 428, 0 failed; Linux (`/tmp/testsToDelete-wc-e15s5-20261014T212844Z`) Core 2791 and CLI 428, 0 failed,
the golden check green. The last runs (`/tmp/testsToDelete-wc-e15s5-20261015T075131Z` on Linux): Windows Scenarios 624 — 64 failed,
all but a few `TimeoutException`s (a built CLI or Git-bash child that did not exit or made no progress in time); its 25 classes
re-run together: 212, 2 failed; Linux Scenarios 624 — 7 failed, the four classes re-run one at a time: `DetachFlows`,
`GoldenContractTests` (the golden that read `dead` for `live` under load is green, 7 of 7) and `ReadContractFlows` green, the
two `StatusFlows` time budgets still over (4.1 s for 2 s, 5.6 s for 3 s); the extension 1251 — 84 failed, every one in a
process-spawning file (`fakeWsl`, the flow scenarios, `runner`, `engineHost`, …), each of the twelve files green alone; lint,
format, plan-lifecycle and the rules check clean. Left red under this load, all time limits: the Windows
`StatusFlows.Status_json_on_this_binarys_side_answers_within_the_budget…` (4–10 s for 3 s; it was green on `0d9be6a3`), the
Linux `StatusFlows` budgets, and `FetchWindowsDaemonFlows.A_download_that_hangs_is_ended_at_its_attempt_limit…` (a 2 s attempt
limit, untouched here). CI is the authority for these.

**Open tail:** a vmmem line in the distro's disk script (so the full run judges `host.vmmem` too) waits for E15.S1b, which changes
that script; with it the full run can judge `host.vmmem` again (`FullRunVerdicts` gains it). The three defaults are provisional until the full runs' records calibrate them.

## E15.S1b design — the MSI pwsh for the root interop scripts (2026-10-11, branch `feat/wc-e15s1b-msi-pwsh`)

**Decided (Q3, the coordinator, 2026-10-11).** The daemon's interop scripts use the MSI `pwsh` 7 when it is present and
verified, otherwise Windows PowerShell 5.1. They never use the Store package or the alias. The rules are those of
`src_vs_code/src/powershell/elevatedEngine.ts`:
- Program Files is the known-folder path, refused when the environment's `ProgramFiles` names another folder.
- The file is real, with no link type.
- `Get-AuthenticodeSignature` reads Valid from `CN=Microsoft Corporation`.
- The module path is pinned first (shipped in #112).

**Measured first (2026-10-11, read-only):**
- **No MSI `pwsh` on the owner's machine.** `[Environment]::GetFolderPath('ProgramFiles')` = `$env:ProgramFiles` =
  `C:\Program Files`, and `C:\Program Files\PowerShell\7\pwsh.exe` is absent. The owner of `C:\Program Files` is
  `NT SERVICE\TrustedInstaller`. So on this machine the story ships the verification, the fallback and the refusal tests;
  pwsh's own timing is measured on the first machine that has the MSI build.
- **The 9p system drive shows every folder below `/mnt/c` with the distro user's uid and mode 555.** That includes
  `Windows/System32/WindowsPowerShell/v1.0` and `Program Files`. The resolver checks root ownership only ABOVE the mount point
  (`SystemDriveFiles.MountPointRefusal`). Below it, it checks: no link, the same device, a regular file, an execute bit, the `MZ`
  header. So `Program Files/PowerShell/7` rests on the same basis as System32 does today: Windows lets only administrators
  (TrustedInstaller) change it.

**1. The verifier is the clock probe.** It stays on Windows PowerShell 5.1, the trusted System32 path. It already runs first in
every full run, and the latency it measures is calibrated for 5.1.
- AFTER its two instants and the Windows Time lines (so the latency is unchanged), it prints tagged lines:
  - `pwsh.knownFolder=` — `[Environment]::GetFolderPath('ProgramFiles')`
  - `pwsh.envProgramFiles=` — `$env:ProgramFiles`
  - `pwsh.msi=` — `absent`, `link`, `empty`, or `file`
  - `pwsh.signature=` — `signed`, or `unsigned:<status>|<subject>`
- `Get-AuthenticodeSignature` comes from the pinned module path.
- No new process: there is no third PowerShell start per full run.

**2. The judgement is PURE** (`Health/PwshEngine.cs`): `Engine Choose(clockLines)` returns `msi` with the absolute WSL path, or
`windowsPowerShell` with a reason.
- `msi` only when all of these hold:
  - the known folder is EXACTLY `C:\Program Files` (the daemon maps only that folder, on the assumed system drive, to
    `<mount>/Program Files`);
  - the environment's `ProgramFiles` equals it (case-insensitive);
  - the file is real (`file`);
  - the signature is `signed`.
- A clock probe that failed or was cut gives `windowsPowerShell`, with the reason "the clock probe did not verify pwsh".
- The run detail's `health.windowsDisk.engine` says `{program, reason}`.

**3. The resolver and the closed set.**
- `WindowsSystemDrive.Programs` gains ONE entry: `pwsh.exe` → `Program Files/PowerShell/7`. It is checked by the same
  `SystemDriveFiles.Problem` checks.
- The never-list's closed set admits `pwsh.exe` ONLY with the Windows disk script's exact arguments — the same list as
  `WindowsDiskCommands.WindowsDisk.Arguments` (`-NoProfile`, `-NonInteractive`, `-Command`, the script under the configuration
  in force), built from that one source. It never admits pwsh with the clock probe, and never with a third script. E15.S10 adds
  its own script when it is built; it is out of this story (plan round).
- The disk script runs under the chosen engine. The clock probe always runs under 5.1.

**Tests, RED first:**
- Each of these gives 5.1, with a reason:
  - a spoofed environment `ProgramFiles` (`D:\Evil`) while the known folder is `C:\Program Files`;
  - a known folder that is not `C:\Program Files`;
  - `absent`, `link` or `empty`;
  - `unsigned:NotSigned`, `unsigned:HashMismatch`, and a Valid signature from another publisher;
  - a failed clock probe.
- Everything verified gives pwsh at `<mount>/Program Files/PowerShell/7/pwsh.exe`.
- Never the Store path or the alias.
- The never-list refuses pwsh with the clock script and pwsh with a one-word variant of the disk script, and admits pwsh with
  the exact disk script.
- The clock probe's latency lines come before the new tags.
- The scenario's fake PowerShell answers both engines.

- **The real script under the real MSI pwsh** (plan round): a Windows test runs the ACTUAL disk script with
  `%ProgramFiles%PowerShellpwsh.exe` and parses it with `WindowsDiskParsers`. The answer must end with `end=1`, have no
  malformed line, and read every section (a section that refers to WSL, such as the Lxss map, may say why on a machine without
  WSL). It SKIPS when the MSI build is absent — as on the owner's machine — and it RUNS on the CI's `windows-latest`, which ships
  PowerShell 7 at that path. This is the evidence that the selection is safe to ship.

Break-its on the judgement and on the closed set. Docs: module_daemon (the engine), README, module_tests, the as-built section.

### E15.S1b stories (the split, 2026-10-12 — decided by a Fable model, as the gate operator asked)

Five stories, built in this order on the one branch, one commit each, no gate between them. Each is complete on its own:
after S1 the probe prints facts nobody reads yet; after S2 the judgement exists and is tested but nothing runs under it;
after S3 the policy admits the run that S4 then makes; S5 records it. Measured before the split (2026-10-12, the Store
`pwsh` 7.6.6 on the owner's machine, read-only): with the module path pinned to `$PSHOME\Modules`, `pwsh` 7 finds NO
`Get-Volume`, `Get-Partition`, `Get-PhysicalDisk` or `Get-StorageReliabilityCounter` — the Storage module is not in pwsh's
own `Modules` folder — while `Get-WinEvent`, `Get-CimInstance` and `Get-AuthenticodeSignature` are there. After
`Import-Module` of `[Environment]::SystemDirectory\WindowsPowerShell\v1.0\Modules\Storage` (the extension's own G6 road for
`PnpDevice`) all four answer. So the disk script must import Storage by that absolute path, or the real-pwsh test of S3
fails on CI. That is S3's work, not a new story.

1. **S1 — The clock probe verifies the MSI pwsh; the parser reads its tags.**
   *What:* `HealthCommands.ClockScript` prints, AFTER the two instants and the w32time lines, `pwsh.knownFolder=`,
   `pwsh.envProgramFiles=`, `pwsh.msi=` (`absent` | `link` | `empty` | `file`) and `pwsh.signature=` (`signed` |
   `unsigned:<status>|<subject>`), by `elevatedEngine.ts`'s rules, inside its own `try { } catch { }` so a fault there
   leaves the tags empty and never ends the probe. `HealthParsers.WindowsClock` treats the new tags as TAGGED (never a
   position); `ClockParsers.PwshProbe` reads them into `PwshProbe`; `WindowsClockAnswer.Pwsh` and `WindowsClockSample.Pwsh`
   carry it (not written to the history line).
   *Files:* `Health/HealthCommands.cs`, `Health/HealthParsers.cs`, `Health/ClockParsers.cs`, `Health/PwshProbe.cs` (new),
   `Records/SlowParts.cs`; a live capture under 5.1, anonymised: `tests/fixtures/health/pwsh-engine-2026-10-12/`,
   `TestSupport/PwshEngineFixture.cs`.
   *Tests:* `Health/PwshProbeTests` (new): the capture reads `absent` with both folders `C:\Program Files`; the 2026-10-08
   answer (before the tags) says no pwsh line; an empty value is an empty value; the tags never shift the three positional
   lines; in the script both instants and the service query come BEFORE the pwsh look, and the look sits in `try { } catch`;
   `ClockGuardTests`, `HealthTests`, `InteropModulePathTests` stay green; the probe run live under 5.1 (read-only).
2. **S2 — The judgement is pure; `pwsh.exe` joins the resolver's closed list.**
   *What:* `Health/PwshEngine.cs`: `InteropEngine(Kind, Program, Reason)`; `PwshEngine.Choose(sample, automountRoot)` answers
   `msi` with `<automountRoot>c/Program Files/PowerShell/7/pwsh.exe` ONLY when the known folder is exactly `C:\Program Files`,
   the environment's `ProgramFiles` equals it (case-insensitive), `pwsh.msi` is `file` and `pwsh.signature` is `signed`;
   else `windowsPowerShell` (`powershell.exe`) with the reason; an unmeasured probe or one without the tags: "the clock probe
   did not verify pwsh: …". `HealthCommands.Pwsh = "pwsh.exe"`; `WindowsSystemDrive.Programs` gains `pwsh.exe` →
   `Program Files/PowerShell/7`.
   *Files:* `Health/PwshEngine.cs` (new), `Health/HealthCommands.cs`, `Processes/WindowsSystemDrive.cs`.
   *Tests:* `Health/PwshEngineTests` (new): the spoofed environment, another known folder, `absent` / `link` / `empty`,
   `unsigned:NotSigned`, `unsigned:HashMismatch`, a Valid signature of another publisher, a failed probe, a probe without
   the tags — each 5.1 with its reason; everything verified → the path, under `/mnt/` and under another automount root; never
   a `WindowsApps` path. `SystemDriveResolverTests`: `pwsh.exe` under the service's PATH resolves in its folder on the drive and
   is refused by the same checks.
3. **S3 — The closed set admits pwsh with the disk script alone; the script runs under pwsh 7.**
   *What:* `WindowsDiskCommands.WindowsDiskUnderPwsh` — `WindowsDisk` with `Executable = HealthCommands.Pwsh`, its own name
   and summary, the SAME `Arguments` instance (one source); `HealthCommands.FixedScripts` = the clock probe, the disk script,
   the disk script under pwsh; the read template `ReadCommandTemplates.WindowsDiskUnderPwsh`; the never-list's words; the
   disk script's prelude imports Storage by its absolute System32 path right after the pin (measured above; the same module
   under 5.1).
   *Files:* `Health/WindowsDiskCommands.cs`, `Health/HealthCommands.cs`, `Processes/Policy/ReadCommandTemplates.cs`,
   `Processes/Policy/NeverList.cs`.
   *Tests:* `PowerShellClosedSetTests`: the set is three and names its programs; `pwsh.exe` passes with the disk script's exact
   arguments and the policy allows it; `pwsh.exe` with the clock arguments, with a one-word variant, with another
   configuration's script, `pwsh` without `.exe` and an absolute `pwsh.exe` are refused; `InteropModulePathTests` covers the
   third member; `WindowsDiskTests`: the Storage import follows the pin and names `SystemDirectory`;
   `Health/RealPwshDiskScriptTests` (new, Windows only): the ACTUAL script under `C:\Program Files\PowerShell\7\pwsh.exe` —
   skipped without it (the owner's machine), run on CI's `windows-latest` — ends with `end=1`, 0 malformed lines, the
   volumes with `C:`, the physical disks, the events, the identity; Lxss may say why. The script run live under the Store
   `pwsh` 7 and under 5.1 on the owner's machine (read-only) as the local evidence.
4. **S4 — The full run starts the disk script under the chosen engine; the detail names it.**
   *What:* `HealthCollector.CollectAsync` chooses the engine from the clock sample and the automount root, after the clock
   probe and its reference as before; `MeasureWindowsDiskAsync(runner, clock, engine, token)` runs
   `WindowsDiskCommands.Under(engine)`; `WindowsDiskSample.Engine` (a `Reading`); the run detail's
   `health.windowsDisk.engine {program, reason}` (absent on the Windows binary and on older details); the scenario harness
   installs a fake `pwsh` too.
   *Files:* `Health/HealthCollector.cs`, `Health/WindowsDiskSample.cs`, `Health/WindowsDiskCommands.cs`,
   `Collect/WindowsDiskReport.cs`, `tests/WslCare.FakeTool/FakeToolProtocol.cs`, `Scenarios/CollectFlows.cs`,
   `Scenarios/WindowsDiskFlows.cs`.
   *Tests:* `HealthTests`: a verified probe answer starts `pwsh.exe` with the disk arguments and the sample's engine names the
   path; the capture (`absent`) starts `powershell.exe` with the reason; a failed probe → 5.1 "did not verify"; the disk
   script still runs after the clock probe. `WindowsDiskTests`: the report carries `engine`; no Health type on the wire.
   `WindowsDiskFlows` (the built CLI): a probe answer derived from the capture with the four tags verified makes `collect`
   start the fake `pwsh` once with the disk arguments, never the fake `powershell` for it, and the detail says
   `/mnt/c/Program Files/PowerShell/7/pwsh.exe`; the captured flow keeps `powershell`; `status` starts neither. The goldens
   re-checked on Linux.
5. **S5 — The record.**
   *What:* module_daemon (the engine choice, the closed set of three, the Storage import), README (which PowerShell the
   daemon uses), module_tests (E15.S1b: the checks, the RED messages, the break-its), *E15.S1b as built* and the status line.
   *Files:* `research/module_daemon.md`, `README.md`, `research/module_tests.md`, `todo/PLAN_disk_health.md`.
   *Tests:* `plan-lifecycle.mjs`; every suite green; `dotnet format --verify-no-changes`.

## E15.S1b as built (2026-10-12, branch `feat/wc-e15s1b-msi-pwsh`)

**Models.** S1 and S2: Fable. The split into five stories: Fable. S3, S4, S5 and the fixed-folder rule: Opus.

**What shipped.**
- **S1 — the probe.** The clock probe (Windows PowerShell 5.1) looks at `<known folder>\PowerShell\7\pwsh.exe` AFTER its two
  instants and the Windows Time lines, inside its own `try`, and prints `pwsh.knownFolder=`, `pwsh.envProgramFiles=`,
  `pwsh.msi=` (`absent` | `link` | `directory` | `empty` | `file`) and `pwsh.signature=` (`signed` | `unsigned:<status>|<subject>`).
  The parser reads them by tag into `PwshProbe`; `WindowsClockSample.Pwsh` carries it (not written to the history line).
  Fixture: `tests/fixtures/health/pwsh-engine-2026-10-12` (5.1, the owner's machine, `absent`).
- **S2 — the judgement.** `PwshEngine.Choose(sample, automountRoot)` is pure: the MSI pwsh only when the known folder is
  exactly `C:\Program Files`, the environment agrees (case-insensitive), the item is a `file` and it is `signed`; else
  `powershell.exe` with the reason. `WindowsSystemDrive.Programs` gains `pwsh.exe` → `Program Files/PowerShell/7`.
- **S3 — the closed set and the script.** `WindowsDiskCommands.WindowsDiskUnderPwsh` is `WindowsDisk` with only the program
  (the bare `pwsh.exe`), the name (`pwsh-windows-disk`) and the summary changed — one `Arguments` list.
  `HealthCommands.FixedScripts` is three: the clock probe (5.1 only), the disk script under 5.1, the disk script under pwsh.
  `ReadCommandTemplates.WindowsDiskUnderPwsh` is the same words and slot under `pwsh.exe`. The never-list's words name the
  pwsh member. The script imports Storage by its absolute System32 path right after the pin.
  `RealPwshDiskScriptTests` runs the actual script under `%ProgramFiles%\PowerShell\7\pwsh.exe` (Windows; it skips without
  the MSI build).
- **S4 — the run.** `HealthCollector.CollectAsync` chooses the engine after the clock probe and its reference (the MSI file
  under the automount root of `/etc/wsl.conf`) and runs `WindowsDiskCommands.Under(engine)`. `WindowsDiskSample.Engine` (a
  `Reading`, kept when the script fails) becomes the run detail's `health.windowsDisk.engine {program, reason}`
  (`InteropEngineReport`) — absent on the Windows binary and on an older detail. The scenario harness installs a fake
  `pwsh` (`pwsh.exe` on Linux).
- **S5 — the record.** module_daemon (the engine choice, the closed set of three, the Storage import), README (which
  PowerShell the daemon uses), module_tests (the E15.S1b row and the flow row), this section, the status line and the
  `todo/README.md` row.

**Measured (2026-10-12, the owner's machine, read-only, unelevated).**
- The disk script WITHOUT the Storage import under the Store pwsh 7.6.6 (`pwsh.exe -NoProfile -NonInteractive -Command`):
  `volumes.error=CommandNotFoundException`, `disks.error=CommandNotFoundException`,
  `reliability.refused=CommandNotFoundException`, `end=1`.
- WITH the import: 4 volumes, 2 disks, `reliability.refused=CimException` (unelevated, as under 5.1), `end=1`, 27 lines,
  exit 0, under the Store pwsh in 9.2–15.7 s and under 5.1 in 6.2–7.4 s (two runs each; the machine was loaded). Under
  pwsh, `Import-Module` writes two non-terminating errors to stderr — the nested modules `StorageHealth.cdxml` and
  `StorageSubSystem.cdxml` are not found with the module path pinned. The cmdlets the script uses all answer, and the
  daemon reads stderr only for a non-zero exit.
- The script without the import under 5.1 answers in full (the pinned path already names Storage there).

**Tests.** RED messages and break-its are in [module_tests.md](../research/module_tests.md) *E15.S1b*. Totals after S4:
Windows Core 2670 (0 failed, 82 skipped), Cli 391 (0 failed, 2 skipped), Scenarios 598 (0 failed, 284 skipped — the Linux
legs). Linux (WSL `Ubuntu`, the copy `/tmp/wc-e15s1b-linux-20261012T104403Z`): Core 2670 (0 failed, 27 skipped), Cli 391
(0 failed, 12 skipped), Scenarios 598 (2 failed, 5 skipped). The two failures were `FixturePrivacyTests`: the script set
`HOME` to a folder named `home`, and the test takes HOME's last segment as this machine's user name, which many tracked
files contain. Rerun with a neutral `HOME`: 57 of 57 green, and `WindowsDiskFlows` 3 of 3. No golden changed: no golden
holds the Windows disk section's engine.
Totals after the fixed-folder rule and the rebase onto main (2026-10-12): Windows Core 2685 (0 failed, 87 skipped), Cli 391
(0 failed, 2 skipped), Scenarios 610 (0 failed, 284 skipped). Linux (a new copy, `/tmp/wc-e15s1b-linux-20261012T184232Z`; the
first folder was gone after WSL restarted with the session): Core 2685 (0 failed, 27 skipped), Cli 391 (0 failed, 12 skipped),
Scenarios 610 (2 failed, 5 skipped). The two failures are `StatusFlows` time budgets on the loaded machine (3.40 s against
3 s; 2.004 s against 2 s) — `status` starts no PowerShell and this story does not touch it; CI is the authority for them.
`WindowsDiskFlows` 3 of 3 green there.

**Deviations.**
1. **The Storage import joins with `[IO.Path]::Combine`, not `Join-Path`.** The split named the extension's G6 road, which
   uses `Join-Path`. An existing test of the disk script forbids `Join-Path` (it resolves the drive and throws on an
   unmounted letter — the E15.S1 code round), so the import uses the script's own rule. The path is the same:
   `[Environment]::SystemDirectory` + `WindowsPowerShell\v1.0\Modules\Storage`.
2. **A failed Storage import has no line of its own.** The import sits in `try { … } catch { }`. Each Storage section then
   fails inside its own `try` and prints its own error line (`volumes.error=CommandNotFoundException`), so the other
   sections still answer. A new `storage.error=` tag would have changed the parser and the wire; the sections' own lines
   already say what failed.
3. **The real-pwsh test's ceiling is 120 s** (a cold pwsh on a CI runner), not the product's 30 s. The first build started the
   file through `Process`; since the code round it runs through the product's runner and policy (item 4 below).
4. **`WindowsDiskCommands.Under(engine)`** is the one place that maps an engine to a command (the split named it in S4's
   *What*; it is a pure static function).
5. **The fixed-folder rule (the coordinator, after S4).** The first build left "PATH still wins for `pwsh.exe`" open. It is
   closed in this story, in the smallest sound shape: the RESOLVER never takes `pwsh.exe` from PATH, with or without a PATH
   entry (`WindowsSystemDrive.FixedFolderOnly`, `ExecutableResolver.OnPath`); it finds it ONLY in `Program Files/PowerShell/7`
   on the mounted system drive, through the same mount, interop, ancestor and file checks as `powershell.exe`'s fallback. On
   the Windows binary a `pwsh.exe` lookup answers "never from PATH" too. The argv still names the BARE `pwsh.exe` (the
   coordinator's point 2 asked for the absolute engine path in the argv): an absolute path passes the resolver unchanged,
   WITHOUT the system drive's checks (no link, the drive's device, a regular file, `MZ`), so naming it would have traded those
   checks away. The file started is `<the drive's mount point>/Program Files/PowerShell/7/pwsh.exe`, and since the code round the
   detail names exactly that file (the runner's `StartedFrom`). The scenario seam:
   `SystemDriveLookup.ForThisProcess` gives `SystemDriveLookup.Sandboxed` (`<sandbox>/mnt/c`, a regular file, no link) only to
   a process inside `WSL_CARE_ROOT` that is NOT really privileged; a real root process always asks this machine.
   RED first: on Linux 5 of 7 `PwshFixedFolderTests` (*Expected type to be …NotFound because the resolver answered Found {
   Path = /tmp/…/windowsapps/pwsh.exe, OnTheSystemDrive = False }*; *Expected resolved to be …Found { Path = /mnt/c/Program
   Files/PowerShell/7/pwsh.exe, OnTheSystemDrive = True }*; the sandbox theory *… to be True … but found False*) and the
   scenario (*Expected Path.GetFullPath(home.Calls.Single(IsDiskScriptUnderPwsh).Location) … to be the same string because the
   fixed-folder file answered, never the pwsh.exe on PATH, but they differ at index 82* — the PATH decoy answered); on Windows
   2 of 3 that run there. Break-its (product code, each restored): `pwsh.exe` taken from PATH again → 5 red on Linux (and the
   scenario red), 1 on Windows; the sandbox granted to a privileged process too → the theory's privileged case red; the
   sandbox's link check removed → *In_the_sandbox_a_link_in_the_fixed_folder_is_not_started* red (a guard written after the
   fix, proved by this break-it).

**Closed.**
- **"PATH still wins for `pwsh.exe`"** — the first build's open point — is closed by deviation 5: `pwsh.exe` comes from its
  fixed folder only, never from PATH.

**The code round (coai `proceed`, and the coordinator's own Opus review, 2026-10-12).** Each fix red first, each with a
break-it on product code:
1. **Forged pwsh tags (security).** The probe printed environment values raw: `$env:USERPROFILE` (the third positional line,
   before every tag) and `$env:ProgramFiles`, and also the folder, the item and the signer. A value with a line feed and
   `pwsh.msi=file` / `pwsh.signature=signed` put forged tags BEFORE the real ones, and the parser took the first: msi with no
   verification. Fixed in both halves: the script prints every value with `-replace '[\x00-\x1f]','?'` (the profile, the
   two Windows Time words, the four pwsh values; the probe stays ASCII), and the parser reads NO pwsh line when any pwsh tag
   appears more than once (`ClockParsers.RepeatedPwshLine`), so 5.1 runs. RED: *Expected probe.Pwsh.ReasonOrEmpty "" to contain
   "more than once"* (through the ProgramFiles line and through the profile line); the script test red on the missing
   `-replace`. Live under 5.1 (read-only, a child process): a forged `USERPROFILE` printed as one line,
   `C:\Users\x?pwsh.knownFolder=…`; a forged `ProgramFiles` never reached the child (Windows sets it per process bitness).
   Break-its: the repeated-tag check removed → 2 red; the cleaning disabled → the script test red.
2. **A verified MSI pwsh that cannot be started.** With the fixed-folder rule a refused or slow system-drive lookup lost the
   whole disk section where 5.1 used to work. Now a `FailedToStart` under the MSI pwsh runs the 5.1 script ONCE and the
   engine says why (`PwshEngine.NotStartable`, quoting the runner's reason). A timeout or an exit code under pwsh is not
   retried (it would double the ceiling), and 5.1 has nothing to fall back to. RED: *Expected argv.Count(…WindowsDisk.Argv)
   to be 1 because 5.1 runs once instead, but found 0*. Break-it: the fallback keyed to the wrong engine → 2 red.
3. **One source for the program.** `InteropEngine(Kind, Reason)` derives its program from the kind, so the two cannot
   disagree; `Choose` no longer takes the automount root. The detail's program is the file the RUNNER started
   (`CommandOutcome.StartedFrom` → `WindowsDiskSample.StartedFrom`), as the drive's mount names it — not a path built from
   `/etc/wsl.conf`; the bare program when nothing was started from the drive. RED: *Expected health.WindowsDisk.StartedFrom to
   be "/mnt/x/Program Files/PowerShell/7/pwsh.exe" … but "" has a length of 0*; *Expected WindowsDiskReports.From(read).Engine
   to be …InteropEngineReport*. Break-it: the report ignoring the started file → 1 red. The scenario now expects the sandbox's
   fixed-folder file.
4. **One shared process launcher.** `RealPwshDiskScriptTests` now runs through the product's `ProcessCommandRunner` and
   `CommandPolicy.Product`; only the lookup is the test's (`WithResolverForItsOwnTests`, the existing internal seam), and
   only the ceiling (120 s). The product is not weakened. Dev check (not committed): pointed at the Store alias it passed
   (1 of 1) through the policy and the runner.
5. **Cheaper fixes.** A quoted probe value is cut to `records.maxReasonChars` (`ClockParsers.Cut`, now internal; RED *found
   5113*; break-it → red). The clock lines are written and flushed BEFORE the look, and a probe that times out during the
   look still gives the clock from its whole flushed lines with the pwsh look unread (`HealthCollector.PwshLookTimedOut`;
   RED *Expected sample.Measured to be True … but found False* and the flush-order test; break-its: the timed-out answer not
   read → red; the first flush removed → red). `FixedFolderOnly` compares without case (the 9p share is case-insensitive;
   the test was written after the fix and proved by revert on Linux). Stale doc comments fixed (`HealthCommands.Pwsh`;
   `ModulePathPin` and `ClockScript` each have their own summary). The tautological "never the Store package" test is
   replaced by one the code could fail: the msi program is in `FixedFolderOnly` and the 5.1 one is not.

Totals after the code round: Windows Core 2766 (0 failed, 89 skipped), Cli 409 (0 failed, 2 skipped), Scenarios 614 (0
failed, 285 skipped); Linux (`/tmp/wc-e15s1b-linux-20261012T184232Z`, refreshed) Core 2766 (0 failed, 39 skipped), Cli 409
(0 failed, 18 skipped), Scenarios 614 (0 failed, 7 skipped). `dotnet format --verify-no-changes` and plan-lifecycle clean.
After the rebase onto main `fce407f8` (2026-10-14): Windows Core 2812 (0 failed, 89 skipped), Cli 423 (0 failed, 2 skipped),
Scenarios 622 (0 failed, 287 skipped); Linux (`/tmp/testsToDelete-wc-e15s1b-20261014T192847Z`) Core 2812 (0 failed, 69
skipped), Cli 423 (0 failed, 29 skipped), Scenarios 622 (0 failed, 8 skipped).

**The final code round (coai `proceed`, 2026-10-14).** Four findings accepted, each red first (or a guard where no red was
possible), each with a break-it on product code:
- **Finding 4, the fallback command.** `RunDiskAsync` ran `WindowsDiskCommands.WindowsDisk` on fallback while the answer was
  read with `Under(used)`. They were equal only because `NotStartable` returns the 5.1 kind. Now the fallback command is
  `Under(the fallback engine)`, one expression for what runs and what is read. No red was possible; a guard asserts that the
  command run on fallback equals `Under(the engine kept)`. Break-it: the fallback run under the msi engine → 2 red.
- **Finding 7, a fallback that fails too.** The engine said "Windows PowerShell 5.1 ran instead" even when 5.1 could not be
  started. It now says "was tried instead", and the sample's own reason names the 5.1 failure. RED: *Did not expect
  engine.Reason "… Windows PowerShell 5.1 ran instead" to contain "ran instead" because 5.1 did not run*. Break-it: the
  wording restored → red.
- **Finding 8, a live clock-probe test.** `RealClockProbeTests` (Windows only) runs the ACTUAL clock probe under the real
  `powershell.exe` through the product's runner and policy, read-only, with a 60 s ceiling: exit 0, the clock measured,
  each pwsh tag exactly once. It ran on the owner's machine. Its red is a break-it: a paren removed from the probe →
  *Expected exited.ExitCode to be 0 because At line:1 char:468 Missing closing ')' in expression*.
- **Finding 11, the known folder's case.** `FolderProblem` compares without case, as Windows compares folder names. RED:
  *Expected PwshEngine.Choose(Sample(@"c:\program files", …)).Kind to be InteropEngineKind.Msi … but found
  InteropEngineKind.WindowsPowerShell*. Break-it: ordinal again → red. (The S2 break-it "the known folder compared without
  case" is reversed by this finding: a trailing separator is still another spelling and is refused.)

Rejected in the round, with the reasons recorded in coai (no work): the C: assumption, the silent catch on the Storage
import, progress output, the `WSL_CARE_ROOT` seam, `IsPrivilegedProcess`, caching Authenticode.

**The security review of the tag-injection fix (Fable, 2026-10-15).** Two real defects and three extras, each red first, each
with a break-it on product code:
- **S1, the parser refused duplicates but not shape.** The real probe prints the pwsh block LAST, in a second write after
  both Windows Time lines. A Windows user can end the probe (their own process) with exit 0 after the first flush; with a
  line break smuggled into the profile, a forged block BEFORE the Windows Time lines had each tag once and gave msi. Now the
  pwsh block is read ONLY as the trailing block — the last four non-empty lines, in the script's order, with both Windows
  Time lines before it (`ClockParsers.NotTheTrailingPwshBlock`); the duplicate rule stays. RED, the exact forged input:
  *Expected answer.Pwsh.IsAvailable to be False because the real probe prints the pwsh block last, after both Windows Time
  lines, but found True*. Break-it: the "after both Windows Time lines" condition removed → at first NO test went red (the
  order rule alone already refused every case written); two cases were added (a trailing block with no Windows Time line,
  and with only one), and the break-it then went red.
- **S2, best-fit console encoding defeated the sanitiser.** Measured on the owner's machine (read-only, `powershell.exe` 5.1,
  stdout redirected to a file):
  - inheriting this session's console: `utf-8`, code page 65001, `System.Text.EncoderReplacementFallback`; U+25D9, U+266A,
    U+263A, U+0444 → their UTF-8 bytes (`E2 97 99`, `E2 99 AA`, `E2 98 BA`, `D1 84`);
  - in a FRESH hidden console (what a launch without an inherited UTF-8 console gets; the machine's OEMCP is 437, ACP 1252):
    `IBM437`, code page 437, **`System.Text.InternalEncoderBestFitFallback`**; U+25D9 → **`0A`** (a real line feed),
    U+266A → **`0D`**, U+263A → **`01`**, U+0444 → `3F` (`?`).
  So `-replace '[\x00-\x1f]','?'` ran on the UTF-16 string and the encoder re-created a line break after it. Fixed: the probe
  prints by the disk script's ONE road — `HealthCommands.Utf8WriteStart` + text + `Utf8WriteEnd`
  (`[Text.Encoding]::UTF8.GetBytes(…)` written to `[Console]::OpenStandardOutput()` and flushed), twice (the clock lines, then
  after the look the pwsh lines), with `[char]10` line breaks and no double quote; the disk script's own print now uses the
  same two constants. UTF-8 has no best fit, and the daemon decodes UTF-8, so a non-ASCII profile also arrives intact (it was
  `?` before). RED: the script-shape test (*Did not expect clock … to contain "[Console]::Out"*) and, Windows only, the live
  `RealClockProbeTests.A_hostile_profile_comes_back_as_one_line_and_forges_no_tag` — a profile `C:\Users\Фёдор◙pwsh.msi=file`
  in a fresh hidden console came back as `C:\Users\?????` and a SECOND line `pwsh.msi=file`. That test starts the probe's own
  arguments through `Process`, because the hostile value must reach the child through its environment and the shared runner
  has no environment seam (adding one for a test would widen the product's launcher). Break-it: the encoding set to the ANSI
  code page (`[Text.Encoding]::Default`, best fit) → the live test red (*C:\Users\??????pwsh.msi=file*).
- **Extra a.** A kill in the middle of the second write left a partial line, and a timed-out answer that did not end in a line
  feed lost the whole clock reading. The partial tail is now dropped (`HealthCollector.WholeLines`) and the clock is read; the
  pwsh look stays unread on a timeout. RED: *Expected sample.Measured to be True … but found False*. Break-it: the old
  "ends in a line feed" rule back → red.
- **Extra b.** `WindowsSystemDrive.Programs` compares without case, as `FixedFolderOnly` does: `Pwsh.exe` resolves in the same
  fixed folder on the drive and never on PATH. RED: *Expected resolved to be …Found* (2 cases). Break-it: ordinal again → 2 red.
- **Extra c.** The Open list and module_daemon say what the code guarantees, no more (below).

Totals after the security review, rebased onto main `28ebdeed` (2026-10-15): Windows Core 2828 (0 failed, 89 skipped), Cli
427 (0 failed, 2 skipped), Scenarios 625 (1 failed, 287 skipped: a `StatusFlows` time budget, 3.5 s against 3 s, with the
Linux leg running beside it; alone 3.1 s). Linux (`/tmp/testsToDelete-wc-e15s1b-20261015T081613Z`) Core 2828 (0 failed, 77
skipped), Cli 427 (0 failed, 33 skipped), Scenarios 625 (6 failed, 11 skipped) — rerun alone, `ReadContractFlows`,
`DetachFlows`, `GoldenContractTests` and `WindowsDiskFlows` were green (40 run, 2 failed): only the two `StatusFlows` time
budgets stay red on the loaded machine (4.9 s against 2 s, 6.3 s against 3 s). They time nothing this story touches; CI is
the authority for them. `dotnet format --verify-no-changes` and plan-lifecycle clean.

**The coai security round (`proceed`, 2026-10-15)**, two findings accepted: the live test reads the pwsh tags from the ONE list
(`HealthCommands.PwshTags`, which the parser's trailing-block rule uses too), and the MSI path relative to Program Files has ONE
definition (`HealthCommands.PwshMsiFolder`) that the probe's look, `PwshEngine.MsiFile` and the resolver's folder
(`PwshEngine.MsiFolderOnTheDrive` in `WindowsSystemDrive.Programs`) are built from; the probe's text is unchanged, byte for
byte. A guard test holds the three equal (no red was possible: they were equal literals). Break-its: one site given its own
literal again → the guard red; the definition itself changed → the three still agree (they cannot diverge) and only the pins
of the real folder went red. After the change: Windows Core 2829 (0 failed, 89 skipped), Cli 427 (0 failed, 2 skipped).

**Open.**
- **The real-pwsh evidence is CI's.** The owner's machine has no MSI build, so `RealPwshDiskScriptTests` skipped locally;
  the first CI run on `windows-latest` is the evidence that the selection is safe to ship.
- **pwsh is slower than 5.1 for this script on the owner's machine** (9–16 s against 6–7 s, the Store build, loaded
  machine). Both are under the 30 s ceiling; the MSI build's own timing is measured on the first machine that has it.
- **Windows on a drive other than C:** always gets Windows PowerShell 5.1: the daemon maps only `C:\Program Files` (a
  documented assumption, the same as `WindowsSystemDrive.Drive`).
- **The probe itself is the Windows user's (security review, extra c).** `powershell.exe` (the probe) is still PATH-first, and
  interop runs it as the Windows user. Under a root collect launched from Windows (`wsl.exe -u root`, the Windows PATH
  appended), that user can put their own `powershell.exe` first and forge the WHOLE verification — a well-shaped trailing
  block, `pwsh.msi=file`, `pwsh.signature=signed`. Nothing in the parser can tell that apart. What bounds it is the
  fixed-folder rule: the daemon still starts only `Program Files/PowerShell/7/pwsh.exe` from the mounted system drive
  (administrator-only), or 5.1 — never a file the user chose. Under the timer (systemd's PATH) the probe is System32's.
- **Residuals of the trust boundary (code round, recorded, not changed):** the signature rule trusts the user's own root
  store (the folder is administrator-only, so a forged signature still needs an administrator to place the file); nothing ties
  the started file to the verified one but its path (administrator-only, and the window is seconds); `powershell.exe` (the
  probe) is still PATH-first; pwsh 7 reads the CurrentUser `powershell.config.json` and honours `DOTNET_STARTUP_HOOKS` /
  `CORECLR_PROFILER` — the same trust boundary as the user's own session, since interop runs every Windows program as that user.

## E15.S7 design — the redaction rule widened (2026-10-15, branch `feat/wc-e15s7-redaction`)

**Goal.** One pure function in `WslCare.Core` judges every command line the product shows or stores, on both binaries
(§3.1's spine: two binaries, ONE rule set). Today's rule (`Collectors/Procfs/ProcessFiles.cs:133-151`) redacts only
`--name=value` and `--name value` where the name holds one of eight words (`:100`). Everything else is shown as it is, cut to
200 characters (`:126-130`) — `research/module_tests.md:4489-4491` says so in the "what it does not prove" list. The owner saw a
creds config key in the sidebar (#87, this plan §1, the last table row). This story closes that gap on the daemon's side, and
puts `program` + `argumentsShown` on the wire so the extension's half (E18.W1b, `:375`) can hide command lines by default.
Read-only; nothing is acted on; no wire field is removed.

### 1. The threat model, and the inventory of every road (mapped 2026-10-15 on `a432e970`)

**What leaks today.** Any secret that is not the value of a flag whose NAME holds a secret word. Measured shapes:

| Shape | Example (invented values) | Today |
|---|---|---|
| a secret in a POSITIONAL argument | `creds config cfgk_7Q…` (the creds CLI takes its config key positionally — `dew_flow_creds_for_devs/src_vs_code/src/configSnippetBodies.ts:72-74`; the key is `cfgk_` + 43 base64url characters, `configKey.ts:46-54`) | shown whole |
| `NAME=value` with a secret word, no leading `-` | `env GITHUB_TOKEN=ghp_… gh pr list`, `docker run -e AWS_SECRET_ACCESS_KEY=…` | shown: `IsSecretAssignment` needs `-` first (`ProcessFiles.cs:144`) |
| a secret inside a structured positional | VS Code's `/TelemetrySession:{"CollectorApiKey":"…"}` — the fixture had to be redacted BY HAND (`src_daemon/tests/fixtures/procfs/ubuntu-2026-10-02/SOURCE.txt:24-27`) | shown whole |
| a credential inside a URL | `DATABASE_URL=postgres://app:Pa55@db/x`, `git clone https://user:ghp_…@github.com/…` | shown whole |
| a creds-shaped token after an innocent flag | `curl -H "Authorization: Bearer eyJ…"`, `gh api -H "Authorization: token ghp_…"` | shown whole |
| an innocent name with a secret value | `-u user:pass` (`curl`), `sshpass -p …`, `mysql -pSecret` | shown whole |

**The inventory — every road by which a process command line reaches stored or displayed output.** Every road starts at ONE
place: `ProcessCollector.Entry` builds `ProcessEntry.CommandLine` from the raw argv through `CommandLineText.Shown`
(`Collectors/ProcessCollector.cs:152`). There is no second redaction and no second display text.

| # | Road | Where it is written / shown | Rule applied |
|---|---|---|---|
| R1 | `status --json` → `vm.processes.top[].commandLine` and `vm.processes.mntWalkers[].commandLine` | `Status/StatusReports.cs:84-96`, `Status/StatusReport.cs:155-171`; the extension's two process tables (`src_vs_code/src/panel/rowRenderers.ts:36-43`, rows `holders.processes` / `holders.mnt`, `fieldMap.ts:132`, `:135`); the human `status` text prints name, pid and GiB only (`Cli/Commands/StatusCommand.cs:242-245`) | `Shown` |
| R2 | `status --json` → `mcpServers.instances[].owner.commandLine` (the agent session that owns an MCP server) | `Mcp/McpInstances.cs:65-68` copies `process.CommandLine`; `Mcp/McpSample.cs:33-34`; `Status/McpServersReport.cs:74-81`; every full run's embedded sample (`McpServersReport.cs:6-8`) | `Shown` (the same string) |
| R3 | action previews and run details: A11's items carry `"<family>, <user>, N h old: <commandLine>"` as the item's note (`Actions/Suspects/SuspectTermination.cs:142-147`, `ActionItem` `Actions/ICleanupAction.cs:98`); A3's build-server list and the idle gate's "busy builds" reasons copy `p.CommandLine` (`Actions/BuildServers/BuildServerShutdown.cs:167`, `:182`; `Actions/Engine/IdleGate.cs:80`) | `act --preview --json`, the run detail `{state}/runs/<day>/<runId>.json` (`Records/RunDetailStore.cs:30-34` — a file other accounts can read, `ProcessFiles.cs:90-91`), `runs show`, `logs --detail`, the panel's modal | `Shown` (copied) |
| R4 | the golden `contracts/golden/head/status.json` — 52 `commandLine` strings in a PUBLIC repository, regenerated from the captured fixture (`research/module_tests.md:888-925`) | git | `Shown` + the by-hand fixture redactions + `FixtureIdentity` |
| R5 | the Windows binary: `Win32ProcessTable.CommandLine` reads ONE process's line through a query-only handle (`Mcp/Win32ProcessTable.cs:82-100`) | read ONLY by the archive's in-use check (`Archive/InUseWindows.cs:79-87`, `:121-129`), which matches `Contains(id)` and emits the FIXED sentence `Liveness.OnItsCommandLine(pid)` (`Archive/Liveness.cs:55-57`); the Windows MCP owners carry exe names only (`Mcp/WindowsMcpOwners.cs:80-82`, `Status/WindowsMcpServersReport.cs:106-108`); a table that reads nothing answers "reads no command line" (`Mcp/WindowsMcp.cs:24-26`) | none needed TODAY: no Windows command line is on any wire. **E15.S6's `status.cpuSessions[].commandShown` (`:338`) is the first one** — the Windows binary must then hold the same rule, which is why S7 comes before S6 in §5 (`:383`) |
| R6 | the distro archive's in-use scan joins a Claude Code line RAW in memory (`Archive/InUse.cs:148-151`) | matched (`Naming`, `:119-120`), never written | not a leak; stays |
| R7 | raw-argv consumers that are NOT display: families (`ProcessCollector.cs:153`, `Collectors/ProcessFamilies.cs:11-17`), `Programs` / `Script` (`:158-159`), `IsUnderMnt` (`:174-176`), interop relays (`Collectors/Procfs/InteropRelays.cs:53-54`, `:66`), the never-list over the daemon's OWN argv | in memory | must NOT change — the rule is for display text only |
| R8 | consumers of the SHOWN line as matching input: `AgentOrphans.Script` takes the word after the program when it starts with `/` (`Actions/Suspects/AgentOrphans.cs:176-177`); `IdleGate.IsBuild` and `BuildServerShutdown.IsDotnetBuild` split it on spaces (`IdleGate.cs:83-85`, `BuildServerShutdown.cs:169-173`); `BrowserAndHttpCaches.cs:248` and `EditorServerCleanup.cs:161` ask `Contains(<distro or folder text>)` | in memory | the widened rule must keep paths and program words intact, or these regress — a RED test holds each |

Not roads: `/proc/[pid]/environ` is never read; `docker inspect`'s template leaves a container's environment and command inside
the docker CLI (`Docker/DockerCommands.cs:31-35`); the daemon's logs carry its OWN argv through `CommandLine.Printable`
(`Cli/Program.cs:173`), never another process's.

**The one false positive already in the golden**: `--max-thinking-tokens <redacted>` (`contracts/golden/head/status.json:278`,
`:305`) — "token" inside a count flag. Kept: the rule is deny by default, and a number lost is cheaper than a token shown.

### 2. The rule — ONE pure function, both binaries

New unit `WslCare.Core/Processes/Redaction/` (three files, each under 400 lines; no package, no I/O, no configuration read
inside — the limits arrive as arguments, as `HostRules` takes `EffectiveConfig`):

- `CommandLineRedaction.cs` — `Redact(IReadOnlyList<string> argv, RedactionLimits limits) → IReadOnlyList<string>` and
  `Shown(argv, limits) → string` (joined, cut on a rune boundary, see §3). `CommandLineText.Redact` / `.Shown`
  (`ProcessFiles.cs:125-134`) become one-line delegations with the limits from `Tuning.Current`, so the existing tests and every
  caller of R1–R3 change nothing. `RedactionLimits(int SecretMinChars, int MaxJudgedArgChars, int ShownChars)` is a record.
- `SecretShapes.cs` — the closed lists and the entropy test (§2.3, §2.4), `[GeneratedRegex]` as `ProcessFamilies.cs:65-75` does,
  every pattern anchored and without nested quantifiers; a match runs under the `patterns.matchTimeoutMilliseconds` ceiling the
  way `TextRule.Matching` does (`Config/KeyRules.cs:39-42`).
- `ShownArguments.cs` — `program` and `argumentsShown` (§2.5).
- **The Windows entry point**: `Processes/WindowsCommandLine.Split(string line) → IReadOnlyList<string>` — the inverse of
  `WindowsCommandLine.Of` (`Processes/WindowsCommandLine.cs:11-43`) with `CommandLineToArgvW`'s rules (a bare argv[0] up to the
  first space or its closing quote; `2n` backslashes before a quote → `n` backslashes; `2n+1` → `n` and a literal quote; a quote
  toggles the quoted state). A property test holds `Split(Of(exe, args)) == [exe, ..args]` — parsing semantics over argument sequences that `Of` produced,
  never a byte identity over arbitrary raw lines — over a generated set (spaces, tabs, quotes, trailing backslashes, empty
  arguments), plus explicit vectors for `CommandLineToArgvW`'s edge cases (a backslash run before a closing quote, `""` inside a
  quoted run, a trailing backslash, an empty argument, a quoted argv[0] with a space) (plan round, finding 0).
  **The Windows guarantee is stated, not assumed** (plan round, finding 4): the rule judges arguments as `CommandLineToArgvW`
  splits them. A program that parses its raw line differently (`--auth:'secret value'`) can carry a value the split does not
  isolate, so a raw-line BACKSTOP runs after the rule (judge's review, 2026-10-16 — the exact definition):
  1. Find every match, in the RAW line, of a secret word (§2.2) followed directly by a separator: `:`, `=`, or one run of
     whitespace.
  2. Its VALUE SPAN starts after the separator. When the span's first character is a quote that `CommandLineToArgvW` does NOT
     honour (`'` or a backtick), the span runs to the matching closing quote (or the end of the line when there is none);
     otherwise it runs to the next whitespace that is outside a double-quoted run, exactly as `Split` would end the argument.
  3. Split the span on whitespace into words, each with any surrounding `'`, `"` or backtick removed; empty words are dropped.
  4. The backstop FIRES when any of those words EQUALS a whole argument of the redacted result (`Redact(Split(line))`), compared
     as whole strings, ordinal — never a substring test. Then the whole shown line becomes `<redacted>`.
  So `prog.exe --auth:'my secret value'` fires (the span is `'my secret value'`; `secret` and `value'`→`value` survive as whole
  arguments — `value'` compared after the same quote strip on the argument side), while `claude.exe --resume=<guid> …
  --max-thinking-tokens 0` does NOT (the span is `0`, which is no whole argument of the result: `<redacted>` took its place, and
  the `0` inside the GUID is a substring, not an argument). RED tests: the non-`CommandLineToArgvW` quoting line; the GUID line
  (a redacted value whose text also occurs inside a shown argument does not fire); `tool.exe --token abc --port 5` does not fire.
  The Windows side then calls the SAME `CommandLineRedaction.Shown(Split(line))` and applies the backstop to its result.
  E15.S6 is its first wire consumer; E9.S5's matcher (R5) keeps matching the raw line, it shows nothing.

**2.1 Order of judgement, per argument** (`Judge(argument, previous)` — a `switch`, the first arm that fires decides; nothing is
judged twice and a value is never partly redacted):

1. `previous` is a secret FLAG (today's `IsSecretFlag`, `ProcessFiles.cs:147-148`, over the widened word list) → `<redacted>`.
2. `previous` is a program-specific value flag (§2.3, the closed table) → `<redacted>`.
3. The argument is `name=value` (with or without leading dashes; the name is the text before the first `=`, non-empty, of
   `[A-Za-z0-9_.-]`, so `a==b` and `=x` are not assignments) → the name holds a secret word → `name=<redacted>`; else the VALUE is
   judged by arms 4–8 as if it were a positional, and a value that fails gives `name=<redacted>` — never `name=abc<redacted>`.
   *Amended 2026-10-18 (security review B):* a value that fails by arm 7 (a blob) gives the WHOLE argument `<redacted>`; the NAME is
   judged too — a name that is creds-shaped or high-entropy (`<48 base64url>=1`) makes the whole argument `<redacted>`; an assignment
   needs a NON-empty value (`<44 base64>=` and `<hex40>=` are tokens with padding, judged by arm 8, not assignments).
4. A URL (`^[a-z][a-z0-9+.-]{1,15}://`) — judged FIRST among the shape arms (judge's review, 2026-10-16: the tests' and the
   design's order disagreed; a URL is the one token whose credential can be cut out cleanly). Its userinfo, when present,
   becomes `<redacted>` whole (`scheme://<redacted>@host/…` — the user name goes too); each query parameter whose name holds a
   secret word, or is `sig`, `signature`, `key`, `code`, `auth`, `access_token` → `name=<redacted>`. Then the creds-shape test
   (arm 5) runs over what REMAINS of the URL (scheme, host, port, path, the query's names and kept values): a hit there makes the
   whole token `<redacted>` (a `ghp_…` in a path segment). Otherwise the rest of the URL is shown. (A URL is judged before the
   path arm: it holds `/`.) So `https://alice:ghp_…@host/o/r` → `https://<redacted>@host/o/r`.
   *Amended 2026-10-18 (security review D and coai code round 0):* after the userinfo and the secret parameters are cut (`pass` joins
   the secret names; the fragment's `&` list is read like a query, so `#access_token=…` goes), EACH path segment and each KEPT query or
   fragment value is judged by the creds shapes and the entropy test and a segment that fails is replaced ALONE —
   `https://host/ghp_…/a.tgz` → `https://host/<redacted>/a.tgz`, a Slack or Discord webhook's last segment, a Telegram bot URL's
   `bot<id>:<secret>` segment (a new creds family), `?t=<random>`. A creds shape still left in the host makes the whole token
   `<redacted>`. A detector that runs out of time answers "unknown" and the argument is `<redacted>` (fail closed).
5. A creds shape anywhere in the text (§2.3) → `<redacted>`.
6. *(Amended 2026-10-18, security review C: this arm now comes AFTER arm 7 — a Windows `/password:value` and a script holding a
   secret word look like paths; and, from 2026-10-19, a path of exactly ONE segment — `/x` or `./x` — whose segment is a creds shape
   or high-entropy is a token with a slash in front and is `<redacted>`; `pass` joined the secret words as a BOUNDED word.)* A PATH →
   shown whole. A path is: starts with `/`, `./`, `../`, `~/`, `\\`, or `[A-Za-z]:[\\/]`, AND every character is in
   letters and digits of ANY script (`\p{L}`, `\p{N}`) plus space and `._~+@%:/\-()[],'&` (so `C:\Program Files (x86)\…`,
   `/mnt/c/Users/Фёдор Иванов/…` and `D:\dev\a&b` are paths; `/TelemetrySession:{"CollectorApiKey":…}` is NOT — it holds `{`
   and `"`) (plan round, finding 2; a RED case for each of the three paths). Paths carry
   programs, scripts and folders the view needs (R8); a secret used as a file NAME is a residual (§9).
7. A secret WORD followed by a separator (`[:="']`) inside a positional (`CollectorApiKey":"…`, `Authorization: Bearer …`,
   `Server=…;Password=…` reached through arm 3) → `<redacted>` whole; the extent of a value inside a blob cannot be delimited,
   so the blob goes. *Amended 2026-10-18 (security review E, C):* the separator also covers WHITESPACE followed by a non-space
   (`sh -c "tool --password Hunter2 …"`), `sshpass` is a secret word (`bash -lc "sshpass -p x ssh host"`), and the arm runs BEFORE
   arm 6. The cost: a free-text prompt holding a secret word (`claude -p "explain the token flow"`) is redacted whole (§9).
   Arm 1's "secret flag" also accepts a Windows switch `/name` (so `/password x` redacts `x`), for that arm only; arm 2 gains the
   attached short options (`curl -uuser:pass`, `sshpass -pVALUE` → the option stays, the rest goes).
8. The high-entropy test (§2.4) → `<redacted>`. *Amended 2026-10-18 (security review A):* the test judges the token WITHOUT its
   leading dashes and trailing padding (`-<48 base64url>` and `--<48 base64url>` are tokens, not flags); there is no flag
   exemption (`--sourceGeneratorExecutionPreference` stays shown because its two classes are two without the dashes).
9. Otherwise shown — after control and format characters are replaced (§3).

**2.2 Secret words** (substring match, case-insensitive, as today — `token`, `secret`, `password`, `passwd`, `apikey`, `api-key`,
`api_key`, `credential`): widened with `passphrase`, `pwd`, `creds`, `auth` (covers `authorization`, `oauth`), `bearer`, `cookie`,
`csrf`, `signature`, `private-key` / `private_key`, `access-key` / `access_key`, `client-secret` / `client_secret`; and `key`
as a BOUNDED word only (`(^|[-_.])key($|[-_.=])`: `--key=`, `API_KEY=`, `-key`; not `--keyboard`, `--keep`). `public-key` is
not exempted: a public key path redacted is harmless. `session`, `id`, `resume` are NOT secret words — session ids are what the
archive's G1 and E15.S6 read (`InUseWindows.cs:76-78`, this plan `:261`). The list is CLOSED in code (§4 says why there is no
key for it).

**2.3 Creds-shaped tokens and program-specific flags** (closed lists in `SecretShapes`, each entry with its source in a
comment):

| Family | Pattern (anchored to a token boundary `(^|[^A-Za-z0-9])`) |
|---|---|
| GitHub | `gh[pousr]_[A-Za-z0-9]{20,}`, `github_pat_[A-Za-z0-9_]{20,}` |
| OpenAI / Anthropic | `sk-[A-Za-z0-9_-]{16,}` (covers `sk-ant-…`, `sk-proj-…`) |
| Stripe | `[sp]k_(live\|test)_[A-Za-z0-9]{16,}` |
| Slack | `xox[abprs]-[A-Za-z0-9-]{10,}` |
| AWS | `(AKIA\|ASIA)[0-9A-Z]{16}` |
| Google | `AIza[0-9A-Za-z_-]{35}`, `ya29\.[0-9A-Za-z_-]+` |
| GitLab / npm / PyPI / DigitalOcean / Hugging Face | `glpat-[A-Za-z0-9_-]{20,}`, `npm_[A-Za-z0-9]{36}`, `pypi-[A-Za-z0-9_-]{20,}`, `dop_v1_[a-f0-9]{64}`, `hf_[A-Za-z0-9]{20,}` |
| SendGrid | `SG\.[A-Za-z0-9_-]{16,}\.[A-Za-z0-9_-]{16,}` |
| **CredsForDevs config key** | `cfgk_[A-Za-z0-9_-]{20,}` (`configKey.ts:46-54`) — the plan's RED |
| JWT | `eyJ[A-Za-z0-9_-]{8,}\.[A-Za-z0-9_-]{8,}\.[A-Za-z0-9_-]*` |
| PEM / age | `-----BEGIN [A-Z ]*PRIVATE KEY`, `-----BEGIN `, `AGE-SECRET-KEY-1[A-Z0-9]{20,}` |
| Basic-auth userinfo as a VALUE | `^[^:/@\s]{1,64}:[^\s@]{1,256}$` — only as the value of `-u`, `--user`, `--proxy-user`, `--credentials`, `--basic-auth` (so `systemd --user`, whose next word holds no `:`, is untouched) |

Program-specific value flags (arm 2), keyed by `program` (§2.5): `sshpass` → `-p`; `mysql`, `mysqldump`, `mariadb`,
`mariadb-dump` → `-p` and a token starting with `-p` longer than 2 characters becomes `-p<redacted>`; `curl`, `wget`, `git`,
`gh` → `-u` / `--user` only when the next token has the userinfo shape above. Docker's `-p 8080:80` is a port map and is NOT in
the table.

**2.4 The high-entropy test** (arm 8, on a token that reached it — so no path, no URL, no assignment, no flag):

- length ≥ `processes.secretMinChars` (20);
- credential alphabet: every character in `[A-Za-z0-9+/=_-]`, a trailing run of `=` stripped first (base64 padding);
- character classes present among {lowercase, UPPERCASE, digits, symbols `+/=_-`}: **at least three**, OR exactly one letter case
  plus digits with the digits being **≥ 30 %** of the characters (a 32-hex Twilio / Mailchimp-shaped token has ~40 % digits; a
  CamelCase identifier such as `sourceGeneratorExecutionPreference` has two classes and no digits);
- Shannon entropy over the token's characters ≥ **3.5 bits per character** (random base62 of 20 characters measures 4.1–4.3;
  `ubuntu-24.04-20260101` — three classes — measures about 3.3 and stays).

The false positives, each exempted by shape and each a RED test that it is SHOWN:
- **paths** — never reach the test (arm 6);
- **GUIDs** `^[0-9a-fA-F]{8}-([0-9a-fA-F]{4}-){3}[0-9a-fA-F]{12}$` — Claude session ids (`--resume=<guid>`, `-r <guid>`); the
  sidebar's row 11 shows one (`:261`);
- **digests are NOT exempt** (decided 2026-10-15, deny by default): a bare 32-, 40- or 64-hex positional is redacted — a
  pre-`ghp_` GitHub token is 40 hex, and a hex-encoded key of any length looks exactly like a digest. A digest inside a PATH
  (`.vscode-server/bin/<40 hex>/node`, `workspaceStorage/<32 hex>`) is kept by arm 6; a bare container id or commit in a
  shown line becomes `<redacted>`, a cost accepted;
- **npm specs** `^@?[a-z0-9][a-z0-9._-]*(/[a-z0-9][a-z0-9._-]*)?@[a-z0-9.+-]+$` — `@playwright/mcp@latest`
  (`status.json:548`);
- **versions and dates** — digits with `.`, `-`, `:`, `+` only;
- **base64 blobs** are NOT exempt: `-EncodedCommand <base64>` and a pasted blob are redacted. The daemon's own interop
  scripts are compared by the never-list over the RAW argv (R7), so nothing of the daemon changes behaviour;
- **URLs with credentials** are arm 4's: the userinfo goes and every path segment and query / fragment value is judged on its own
  (§2.1 as amended 2026-10-18): the entropy test runs on each SEGMENT, never on the URL as a whole, and a segment that fails is
  replaced alone.

**2.5 `program` and `argumentsShown`** (`ShownArguments.Of(argv, redacted, limits)`):

- `program` = `CommandLineText.FileNameOf(argv[0])` (`ProcessFiles.cs:119-120`: the file name, `.exe` stripped) — what
  `Programs[0]` already is (`ProcessCollector.cs:158`); empty argv → the `/proc/[pid]/status` name, as `Programs` falls back
  to it. `program` is never redacted: it is a file name (a program named after a secret is a residual, §9).
- `argumentsShown` = the longest SAFE PREFIX of the arguments after the program, at most `processes.shownSafeArgs` (2) of them,
  each after the rule. An argument is SAFE when ALL hold: the rule left it unchanged (no `<redacted>` in it); it is one of — a
  flag without a value (`-x`, `--name`), a `--name=value` whose value passed, a path (arm 6), a GUID, an npm spec, or a plain
  word of `[A-Za-z0-9._@:+-]` shorter than `secretMinChars`; and it is at most `processes.shownCommandChars` long. The FIRST
  unsafe argument ends the prefix (so a value that follows a flag we do not know is never shown past the point where doubt
  starts: `claude -p <prompt>` shows `["-p"]` and stops, because a prompt is free text). Empty and whitespace-only arguments (node's
  title padding — `status.json:548` carries the trailing spaces) never enter the shown prefix.
- `argumentsTotal` = the number of non-EMPTY arguments after the program — a whitespace-only one counts (coai code round 3, 2026-10-18:
  the total is what the process was given; only the prefix skips it) — so the view can say "+ N more" without the line.
- Examples from the golden: `npm exec @playwright/mcp@latest` → `npm`, `["exec", "@playwright/mcp@latest"]`, 2;
  `…/claude --output-format stream-json --verbose …` → `claude`, `["--output-format", "stream-json"]`, 9;
  `…/agy --hub --hub-port=41790 --app_data_dir=antigravity --csrf_token=…` → `agy`, `["--hub", "--hub-port=41790"]`, N;
  `creds relay` → `creds`, `["relay"]`, 1; `creds config cfgk_…` → `creds`, `["config"]`, 2.

### 3. Allow and deny decisions — deny by default

| Decision | Rule |
|---|---|
| what is shown | the program; flags; paths; GUIDs; npm specs; versions; short plain words; a URL's scheme, host, port and path |
| what becomes `<redacted>` | the value of a secret flag or assignment; a creds-shaped token; a blob holding a secret word with a separator; a high-entropy token; a URL's userinfo and secret query values; a program-specific password value |
| the replacement | always the single constant `<redacted>` (`ProcessFiles.cs:98`) — **never the value's length, never a prefix, never a suffix** (`abc…`, `***`, `gh…p_`); a RED test asserts the redacted text holds no 4-character substring of the value |
| partial values | none: a value is replaced whole; `name=` keeps its name only when the name is `[A-Za-z0-9_.-]` |
| no truncation leak | the join is cut AFTER redaction (as today, `ProcessFiles.cs:128-129`) — a cut can only shorten `<redacted>`; `argumentsShown` elements are never cut (an element over the cap is unsafe and ends the prefix); the cut lands on a rune boundary, never inside a surrogate pair, so `status --json` never carries an unpaired surrogate |
| an argument over `processes.maxJudgedArgChars` (4096) | `<redacted>` whole, unjudged — a 4 KiB positional is a script or a blob, and a bounded input is what keeps every regex linear (`/proc/[pid]/cmdline` may be 2 MiB) |
| Unicode | argv is UTF-8 with invalid bytes already replaced by U+FFFD (`Encoding.UTF8.GetString`, `ProcessFiles.cs:123`); letters of any script count as letters for the class test; the entropy test counts code points, not UTF-16 units |
| control and format characters | every character of category `Cc` (NUL, tab, LF, ESC — terminal injection into `logs`, `runs show`), `Cf` (zero-width, U+202E RTL override — which could make `<redacted>` read otherwise) and `Cs` (a lone surrogate) is replaced by `?` in every shown argument. `CommandLine.Printable` (`Cli/CommandLine.cs:792-799`, `internal`, `Cc` only) MOVES to `WslCare.Core/Processes/Redaction/ShownText.cs` widened to `Cf` / `Cs`, and the CLI calls it there — reuse-first step 2.4, so there is one definition |
| the extension | renders the string as text with its own cap (`src_vs_code/src/panel/format.ts:29-31`); the hostile-string tests (`src/test/panelPage.test.ts:98`, `src/test/panel/viewModel.test.ts:143`) keep passing unchanged |

### 4. The configuration keys

| Key | Shape | Default | Trust | Why |
|---|---|---|---|---|
| `processes.secretMinChars` | int 12–64 | 20 | `new(SafeDirection.Lower, MachineOnly: true)` — a SMALLER number redacts more; machine layer only, as `processes.shownCommandChars` (`Config/ConfigKeys.Processes.cs:32-33`), because a user layer must not loosen what root writes into a history other accounts read | the plan's row (`:339`); the detectors' common floor |
| `processes.shownSafeArgs` | int 0–8 | 2 | `Lower`, `MachineOnly` — fewer is safer | the plan's row |
| `processes.maxJudgedArgChars` | int 1024–65536 | 4096 | `Lower`, `MachineOnly` — a smaller bound redacts more and bounds every match | §3's regex bound; the inventory's rule "every number a key" (`:321-323`) |

All three join `ConfigKeys.Processes`, the `NumberKeys` list (`Config/ConfigKeys.Numbers.cs:150-153`), `Config/default.json:71`'s
block, group B of `NumbersAreConfigurationTests` (`Config/NumbersAreConfigurationTests.cs:62`), and `contracts/config-keys.json`
(regenerated by `ContractFilesTests`, `config-keys.json:2722-2734` is the neighbour). `UserLayerTrustTests` gains the three.

**No open list in this story.** The secret words and the creds shapes are closed in code. A user-widenable word list would need an
OPEN `TextListKey` with a reviewed `TextRule` record (`Config/ConfigKey.cs:37-56`, `Config/KeyRules.cs:7-25`: no free text), AND
a safe direction "a SUPERSET is safer" that `SafeDirection` (`KeyRules.cs:118-137`) does not have — `Subset` is the opposite.
Adding an enum member for one key is a change to the trust model, and a list that can be SHORTENED weakens redaction. Recorded as
the open tail: `processes.extraSecretWords` with `TextRule.SecretWord` (`^[a-z][a-z0-9_-]{2,31}$`, at most 32 members) and a
`SafeDirection.Superset`, if the owner asks for it.

### 5. The wire contract

- **Additive.** `ProcessReport` (`Status/StatusReport.cs:155-171`) gains `program` (string), `argumentsShown` (string[]) and
  `argumentsTotal` (int) after `commandLine`; `McpOwnerReport` (`Status/McpServersReport.cs:74`) gains `program` and
  `argumentsShown`. `commandLine` STAYS — the plan's own rule, "no wire removal" (`StatusReport.cs:191-192`); its VALUE changes
  only where the widened rule bites. `schemaVersion` stays 1 (`SchemaVersion.cs:9`): a field added, none moved or renamed. The
  extension reads fields by key (`src_vs_code/src/panel/read.ts:31-33`) and ignores what it does not know.
- **The goldens.** `contracts/golden/head/status.json` is regenerated on Linux (`research/module_tests.md:921-924`) and the diff
  reviewed as a contract change: 52 entries gain the three fields; `commandLine` changes for the fixture's structured
  `/TelemetrySession:` arguments (they become `<redacted>` whole — the by-hand `REDACTED` inside them no longer reaches the
  golden) and nowhere else, which the review proves line by line. `GoldenContractTests` holds it afterwards. `status-limits.json`
  is unchanged: nothing here is a limit the extension mirrors.
- **The extension's consumers, and what must not break.** The extension half of #87 item 2 is E18.W1b (`:375`): the full view
  shows `program` + `argumentsShown`, reveals the line per row. NOT this story. This story must leave the extension green without a
  change: the process tables keep rendering `commandLine` (`rowRenderers.ts:36-38`); the status-bar model reads verdicts only; no
  schema check refuses an unknown field (the head golden the extension's tests read is updated in the same PR, and its tests stay
  green — the E15.S5 cross-boundary pattern, `:1024-1027`). E18.W3's export (`:377`) writes the daemon's redacted bytes, so it
  inherits this rule for free.

### 6. RED first — the list

A new `Core.Tests/Processes/Redaction/CommandLineRedactionTests.cs`, each case named for the guarantee, and a fixture
`src_daemon/tests/fixtures/redaction/realistic-leaks.txt` — INVENTED values in every real shape (a value is never a real secret;
`FixturePrivacyTests` scans it like every fixture; a comment on each line says which arm must fire):

1. **The plan's RED:** `creds config cfgk_<43 base64url>` → `creds config <redacted>`; `argumentsShown` is `["config"]`.
2. `GITHUB_TOKEN=ghp_…` (no dash) → `GITHUB_TOKEN=<redacted>`; `env ANTHROPIC_API_KEY=sk-ant-… claude` → the key redacted,
   `claude` shown.
3. `docker run -e AWS_SECRET_ACCESS_KEY=… -e PGPASSWORD image` → the first value redacted; `PGPASSWORD` (no value) shown.
4. `/TelemetrySession:{"CollectorApiKey":"…"}` → `<redacted>` whole (arm 7, not a path).
5. `curl -u alice:Pa55w0rd https://x/` → `curl -u <redacted> https://x/`; `systemd --user --deserialize 48` unchanged.
6. `curl -H "Authorization: Bearer eyJ….…"` → `curl -H <redacted>`; `gh api -H "Authorization: token ghp_…"` likewise.
7. `git clone https://user:ghp_…@github.com/o/r` → `https://<redacted>@github.com/o/r`;
   `DATABASE_URL=postgres://app:p@db/x` → `DATABASE_URL=<redacted>` (arm 3's value fails arm 5).
8. `--password=x`, `--password x`, `-pSecret` under `mysql`, `sshpass -p x ssh host` → each value redacted; `docker run -p 8080:80`
   unchanged.
9. `node -e "const token='abc'; …"` → the script redacted (arm 7); `node -e "const net = require('net'); …"` (`status.json:656`)
   unchanged.
10. `dotnet exec x.dll --connectionString "Server=a;Password=b"` → `--connectionString <redacted>`.
11. `claude --resume=<guid>` unchanged; `claude -r <guid>` unchanged; `claude -p "any text"` → `argumentsShown` is `["-p"]`.
12. A bare 48-character base64url positional → `<redacted>`; the same token one character under `secretMinChars` with the key
    set to its length → shown (the key is read, not a constant).
13. Shown, each: `/home/user/.vscode-server/bin/<40 hex>/node`, `--add-dir=/home/user/git/project-tokenizer` (a path value with
    "token" inside it stays — arm 3 judges the NAME `add-dir`, then the value by arm 6), `@playwright/mcp@latest`, `ubuntu-24.04-20260101`, `sourceGeneratorExecutionPreference`, `rovodev-ide-vscode`.
14. A 32-hex, a 40-hex and a 64-hex bare positional → each `<redacted>` (no digest exemption).
15. **Never a prefix:** for every redacted case the output holds no 4-character substring of the value and no digit of its length.
16. **No truncation leak:** a 300-character secret at the end of a line, cut at 200 → the line ends in `<redacted>` or a prefix of
    that constant, never in a character of the secret; `shownCommandChars` set to 5 → `<reda`.
17. **Rune boundary:** a cut that would split a surrogate pair ends before the pair; the JSON stays valid.
18. **Control characters:** an argument holding ESC `[31m`, U+202E and a lone surrogate → `?` for each; the same test red when the
    CLI's `Printable` and the Core one are two definitions (the CLI test asserts `CommandLine.Printable` IS `ShownText.Printable`).
19. **R8 regressions:** `AgentOrphans.Script`, `IdleGate.IsBuild`, `IsDotnetBuild` over the captured fixture give what they gave
    before the widening (a snapshot assertion on the fixture's 51 processes).
20. **Both binaries, one rule:** `Split(Of(exe, args)) == [exe, ..args]` over a generated set; `Shown(Split(line))` of a Windows
    `claude.exe --resume=<guid> -p "…"` equals the distro `Shown` of the same argv.
21. **The wire:** the golden's 52 entries carry `program` / `argumentsShown` / `argumentsTotal`; the owner report carries the two;
    `argumentsShown` never holds `<redacted>` and never more than `shownSafeArgs` elements; `shownSafeArgs` 0 → `[]`.
22. **Keys:** a user layer that raises `secretMinChars` to 64 or `shownSafeArgs` to 8 is ignored with a notice (machine only); the
    three are in `contracts/config-keys.json`.
23. An argument of 5 000 characters → `<redacted>` without judging it; the test asserts the call returns within the pattern
    ceiling against a pathological input (`a` × 4 096 followed by `!`).

### 7. Break-its (product code, each restored byte for byte)

| Line broken | Red test |
|---|---|
| arm 8 removed (no entropy test) | 1 (the key is caught by the prefix list) stays green, 12 and 14 red — so the prefix list and the entropy test are each proved alone |
| the `cfgk_` shape removed with arm 8 kept | 1 still green by entropy, and a test with `secretMinChars` raised to 64 then red — the plan's RED does not rest on one arm |
| arm 6's alphabet widened to accept `{` and `"` | 4 red |
| `Redacted` replaced by `value[..4] + "…"` | 15 red |
| the cut moved before `Redact` | 16 red |
| `Split`'s backslash rule simplified | 20 red |
| `Printable` not applied in `Shown` | 18 red |
| `shownSafeArgs` ignored (3 shown) | 21 red |
| `MachineOnly` dropped from a key | 22 red |

### 8. Build order (small commits), test plan, docs, DoD

Commits, each green on its own:
1. `test(daemon): E15.S7 RED — the redaction fixture and the tests over stubs` (the 23 cases, the fixture, `Split` and
   `ShownArguments` as stubs that throw `NotImplementedException` — each failure message names the symptom, never the stub).
2. `feat(daemon): SecretShapes — the creds shapes, the secret words, the entropy test`.
3. `feat(daemon): CommandLineRedaction — one rule, CommandLineText delegating; ShownText.Printable shared with the CLI`.
4. `feat(daemon): the three processes.* keys` (+ `default.json`, `NumberKeys`, group B, `config-keys.json`, `UserLayerTrustTests`).
5. `feat(daemon): program, argumentsShown, argumentsTotal on the wire` (`ProcessReport`, `McpOwnerReport`; goldens regenerated
   on Linux, the diff reviewed).
6. `feat(daemon): WindowsCommandLine.Split — the Windows binary's entry to the same rule` (round-trip property test).
7. `docs: E15.S7 as built`.

Test plan: Windows — Core, CLI, Scenarios; Linux (WSL, a NEW `/tmp/testsToDelete-wc-e15s7-<stamp>` folder, as E15.S5 did) —
the goldens written, then `GoldenContractTests` green without a write, Core, CLI, Scenarios; the extension suite UNCHANGED and
green (the proof the change is additive); lint, format, plan-lifecycle, the rules check. The RED messages of commit 1 and the
break-its' messages go to `research/module_tests.md` *E15.S7*, with the suite totals.

Docs to update in the same PR: `README.md:388` (the sentence "secret-looking values redacted (`--x=v`, `--x v`…)" → the widened
rule and the two display fields); `research/module_daemon.md` (the process table's text and the `mcpServers` owner);
`research/module_mcp_servers.md` (the owner's new fields); `research/architecture.md` (the E5.S0 contracts section: the additive
fields, as E15.S5's correction did, `:1061-1063`); `research/module_tests.md:4489-4491` REWRITTEN — the bullet that says a secret
in any other shape is shown as it is must say what the rule now catches and what §9 leaves; `contracts/config-keys.json`; this
plan's *E15.S7 as built* and status line; `todo/README.md`.

Definition of Done:
- [ ] One function (`CommandLineRedaction.Shown`) behind every road R1–R4; `CommandLineText.Shown` / `.Redact` delegate to it.
- [ ] The Windows entry (`WindowsCommandLine.Split` + the same `Shown`) exists with its round-trip test; E15.S6 names it as its
      consumer.
- [ ] The 23 RED cases were red for the named symptom, then green; the nine break-its were red, then green after a byte-equal restore.
- [ ] `<redacted>` is the only replacement; no prefix, no length, no partial value; the cut is after the rule and on a rune boundary.
- [ ] `argumentsShown` holds only safe arguments, at most `shownSafeArgs`, and never `<redacted>`.
- [ ] The three keys are machine-only with `Lower` as the safe direction; `config-keys.json` regenerated.
- [ ] The goldens regenerated on Linux, the `commandLine` diff reviewed line by line (only the `/TelemetrySession:` entries change).
- [ ] The extension suite green WITHOUT a change; `schemaVersion` unchanged; `commandLine` still on the wire.
- [ ] R8's matchers unchanged over the fixture (test 19).
- [ ] `ShownText.Printable` is the ONE definition (the CLI's moved, not copied).
- [ ] Docs listed above updated; `module_tests.md:4489-4491` rewritten.
- [ ] The status line and `todo/README.md` moved from "being designed" to "built on its branch", then "merged (#N)".

### 9. Residuals — what a regex cannot catch, stated plainly

- **Free text.** A secret typed into a prompt (`claude -p "use key …"`, `-e <script>` without a secret word) is shown unless the
  value alone is creds-shaped or high-entropy. `argumentsShown` stops at `-p`, so the full view hides it by default; the revealed
  `commandLine` still has it.
- **Short secrets.** A PIN, a short password (`-pHunter2` outside the mysql table), a 12-character token with no marker: below
  `secretMinChars` and with no shape, they are shown. Lowering the key raises false positives; 20 is the detectors' floor.
- **Passphrases of words** (`correct-horse-battery-staple`): two classes, low entropy — shown.
- **Bare hex digests are lost** (a container id, a commit) — the price of redacting every bare hex token; inside a path they stay.
- **A secret as a file name or a path component** (`/tmp/ghp_…`): arm 6 shows paths — unless the component is creds-shaped
  (arm 5 runs before arm 6, so `/tmp/ghp_abc…` IS redacted whole; a plain-looking name is not).
- **A secret split across two positionals**, or one whose flag is in another language's spelling (`--contraseña`), is shown.
- **The process name and the cwd** (`ProcessReport.Name`, `.Cwd`) are not judged: a cwd names folders, not credentials; a process
  named after a secret is a residual.
- **Kernel ring lines** (`health.kernelLinesKept` × `health.kernelLineChars`, `Health/HealthCollector.cs:70-71`, `:180`) can echo a command line in
  an OOM or segfault message; they do not pass through this rule. Named here; a follow-up may route them through `Shown` over a
  split on spaces.
- **History already written** keeps what the old rule showed: detail files under `{state}/runs/` are never rewritten (§3.2's
  "raw files are never rewritten"); `runs.historyRetentionDays` retires them. The PR body says so.
- **A free-text prompt that holds a secret WORD is redacted whole** (2026-10-18, security review E): `claude -p "explain the token flow"`
  becomes `<redacted>` — the price of reading whitespace after a secret word as a separator, which is what catches
  `sh -c "tool --password x"`. A prompt WITHOUT a secret word is still shown (and `argumentsShown` still stops at `-p`).
- **F — base32 / TOTP seeds and letters-only mixed-case tokens** (2026-10-18): `JBSWY3DPEHPK3PXPMZXW6YTBOI4TQ3LX` (uppercase and digits,
  digits below 30 %) and a random 24-letter mixed-case token (two classes, no digits) pass the entropy test's class rule; shown.
- **H — Windows `--token= value`** (2026-10-18): the split of `tool.exe --token= abc` gives `--token=` and `abc`; an assignment needs a
  non-empty value, so `--token=` is a blob (redacted) but the `abc` after a name that ends in `=` is judged on its own and shown.
- **I — an unencoded `/` inside a userinfo password** (2026-10-18): `postgres://app:p/ss@db/x` has its authority end at the first
  `/`, so the "userinfo" is `app:p`, the rest is read as a path and `ss@db` is shown. RFC 3986 requires `/` in a password to be
  percent-encoded; a tool that does not encode it leaks the tail.
- **Not read, so not leaked:** `/proc/[pid]/environ`, Docker's container environment (`DockerCommands.cs:31-35`), the Windows
  binary's command lines (R5, until E15.S6).

### 10. The designer's open questions, decided (2026-10-15, the orchestrator)

1. 40/64-hex exemption → **removed** (deny by default; §2.4, RED 14).
2. Secret words as substrings → **kept** (`--max-thinking-tokens` keeps losing its number; a lost number is cheaper than a shown
   token).
3. History already written → **no sweep**; `runs.historyRetentionDays` retires it; the PR body says so.
4. A user-widenable word list → **deferred** to the open tail (`processes.extraSecretWords` + `SafeDirection.Superset`); not
   needed for #87.
5. Kernel ring lines → **a follow-up**, recorded in the open tail; not in this story.

## E15.S7 as built (2026-10-17, branch `feat/wc-e15s7-redaction`, draft PR #161)

**What shipped.** One redaction rule, `WslCare.Core/Processes/Redaction/CommandLineRedaction`, behind every road R1–R4 of the design's
inventory, on both binaries; `program` / `argumentsShown` / `argumentsTotal` on the wire; three machine-only keys; the Windows entry
(`WindowsCommandLine.Split` / `.Shown`) with its raw-line backstop. The judge's review of the WIP (2026-10-16) is applied: the URL arm
before the creds shape, the exact backstop span, every provider-shaped token built from halves (`{GHP}` / `{CFGK}` / `{SKANT}` /
`{JWT}` placeholders in the fixture), one invented case per creds family, the pattern ceiling at the key's DEFAULT,
`AgentOrphans.Script` internal and called directly. Read: [module_daemon.md](../research/module_daemon.md) § *One redaction rule for
every command line*, [module_tests.md](../research/module_tests.md) § *One redaction rule … (E15.S7)* (the RED messages, the guards,
the twelve break-its), [architecture.md](../research/architecture.md) (E5.S0 contracts).

**Commits** (conventional, each builds and is green for its own tests): `docs(plan)` ×4 (design, decisions, plan round, judge's
review); `test(daemon): E15.S7 RED`; `feat(daemon): SecretShapes …`; `feat(daemon): the three processes.* keys …`;
`feat(daemon): CommandLineRedaction …`; `feat(daemon): program, argumentsShown, argumentsTotal on the wire`;
`feat(daemon): WindowsCommandLine.Split …`; `docs: E15.S7 as built`.

### The golden diff, every `commandLine` that changed (`contracts/golden/head/status.json`, regenerated in WSL)

The design (§5, §8) and the brief expected ONLY the `/TelemetrySession:` entries to change. **That row is not in the golden** (the
captured Visual Studio server processes are not in the top 30 or the `/mnt` walkers), and **two other entries changed**. The security
judge decides on them. Verbatim, before → after (both cut at the 200 characters `processes.shownCommandChars` allows):

1. A bare **40-hex commit id** in the `wslServer.sh` launch — redacted by the DECIDED rule "no digest exemption" (§2.4, RED 14;
   a pre-`ghp_` GitHub token is 40 hex):

   ```
   - "commandLine": "sh /mnt/c/Users/user/.vscode/extensions/vendor.extension-h-1.0.0/scripts/wslServer.sh 07f806f999227108933c2e30515b26eecc1fda74 stable code-server .vscode-server --host=127.0.0.1 --port=0 --connection-",
   + "commandLine": "sh /mnt/c/Users/user/.vscode/extensions/vendor.extension-h-1.0.0/scripts/wslServer.sh <redacted> stable code-server .vscode-server --host=127.0.0.1 --port=0 --connection-token=<redacted> --use-host-pr",
   ```

   (The old line was cut inside `--connection-` by the 200-character limit; the shorter `<redacted>` lets the same cut reach
   `--connection-token=<redacted>`, which the old rule would also have redacted.)

2. A **`sh -c` script** holding `--connection-token=REDACTED` (a by-hand redaction in the fixture) — the whole argument is a blob
   with a secret word and a separator (arm 7), so it goes whole:

   ```
   - "commandLine": "sh -c \"$VSCODE_WSL_EXT_LOCATION/scripts/wslServer.sh\" 07f806f999227108933c2e30515b26eecc1fda74 stable code-server .vscode-server --host=127.0.0.1 --port=0 --connection-token=REDACTED --use-host-proxy ",
   + "commandLine": "sh -c <redacted>",
   ```

Everything else in the diff is the three added fields (50 process rows and the 2 live MCP owners; 280 lines added) and the trailing
comma that the new field after `commandLine` forces on two lines (the two `claude … --max-thinking-tokens <redacted>` rows, whose
text is unchanged). One further change was found in the first regeneration and FIXED rather than accepted:
`--sourceGeneratorExecutionPreference` and `--devKitDependencyPath` were redacted by the entropy test (their dashes count as a
symbol class and their CamelCase as two letter cases); a flag name is now exempt, with a test that went red first.

### Deviations from the design

- **Commits 3 and 4 swapped**: the keys came before the rule's wiring, because `CommandLineText` delegates with limits READ from
  them; each commit builds and is green.
- **Whitespace-only arguments are skipped and not counted** (design §2.5 says empty only): the safer reading — node's title
  padding is a run of spaces, and such an argument shows nothing a view could use. Recorded in `ShownArguments.Of`'s remarks.
- **The pattern ceiling is a compile-time constant** (`SecretShapes.PatternCeilingMilliseconds` = 1000) — a `[GeneratedRegex]`
  attribute takes a constant, never a configured value, so it is the range maximum of `patterns.matchTimeoutMilliseconds` (the
  bootstrap ceiling `TextRule.Matching` uses), held equal by a test and listed in `NumbersArchitectureTests` with its reason, like
  `CommandLine.MaxShownVolumes`. A match that times out counts as a HIT (deny by default). The linearity comes from the 4096-character
  bound and the anchored, bounded patterns.
- **Arm 3 and the blob**: a value that fails by arm 7 (a secret word and a separator inside it) gives the WHOLE argument, not
  `name=<redacted>` — the design's own "reached through arm 3" example (`Server=…;Password=…`) and test 10 demand it.
- **A "secret flag" (arm 1) is a name with no value in it** (`^--?[A-Za-z0-9][A-Za-z0-9._-]*$`), not "starts with `-` and holds no
  `=`": `--auth:"my secret value"` is one argument judged on its own, and it must not make the `--port` after it secret (test
  `A_windows_line_whose_secret_the_split_isolates…`).
- **The backstop scans resume after a value span** (a name such as `secret` inside `--auth:"my secret value"` is part of that
  value) and treats a timeout as a hit.
- **A flag name is exempt from the entropy test** (above).
- **Entropy calibration constants** (`MinEntropyBits` 3.5, `MinClasses` 3, `MinDigitPercent` 30) are code constants listed in
  `NumbersArchitectureTests` as the credential alphabet's properties; the length floor IS a key.
- **Fixture arm labels** `arm4` / `arm5` of two lines were corrected to the amended order; the arm labels are informational.
- **Test defects found and fixed** in commit 4's tests: the surrogate test called reflection-based JSON (disabled in this
  build; it now asks `JsonEncodedText`), and the no-prefix test used a value that shares four letters (`assw`) with the flag name it
  follows. `McpStatusTests` compares the owner by value including its two new fields.
- **Not done in this story**: the extension's own half (E18.W1b, the full view that shows `program` + `argumentsShown`); the
  open tail below.

### The security judge's two golden verdicts (Fable, 2026-10-18) — decisions

1. **The bare 40-hex commit id STAYS redacted, with no exemption.** It cannot be told apart from a 40-, 32- or 64-hex credential
   (a pre-`ghp_` GitHub token is 40 hex), and the commit stays readable in the kept `.vscode-server/bin/<40hex>/` paths, which arm 6
   shows whole. The golden line stays as regenerated.
2. **`sh -c <redacted>` is RIGHT.** The live value is a real connection token inside one shell script argument; a narrower redaction
   (the token alone) needs a POSIX-shell tokenizer, which this rule does not have. Open tail: a quoting-aware tokenizer for the
   argument of `sh -c` / `bash -lc`, so only the token goes.

### Security review (Fable) and code round (2026-10-18) — A–E, G and coai 0–3

The security judge said "do not ship as is" and the coai code round accepted four findings. Every fix has a RED test
(`SecurityReviewTests` and the changed tests, commit `test(daemon): E15.S7 security review RED`, 28 of 248 red) and a break-it on
product code (restored byte for byte, `git diff` empty, green again):

| Finding | Red message (against the pushed build) | Break-it and its red |
|---|---|---|
| **A** a token with one or two leading dashes passed the flag exemption into `commandLine` AND `argumentsShown` | *Expected Shown(argv) to be the same string, but they differ at index 5* (`tool -<48>` shown) | N1 the dashes kept again: 3 red (the two flag guards and the entropy theory) |
| **B** a trailing `=` hid a token (`<44 base64>=`, `<hex40>=`, `<48>=1`) | the same message, 3 cases | N2 the assignment name not judged: 1 red (`<48>=1`) |
| **C** Windows `/password:value` passed as a path; `/password x` kept `x`; the raw Windows line too | *Expected Shown("cmdkey","/add:srv","/user:alice","/password:Hunter2Invented") to be the same string, but they differ at index 28*; *Expected WindowsCommandLine.Shown(…) … differ at index 32* | N4 path arm before the blob arm: 3 red; N5 a Windows switch is no secret flag: 1 red (`/password` then `x`, differ at index 15) |
| **D** a URL's path segments, kept query and fragment values were not judged (Slack / Discord / Telegram webhooks, `?pass=`, `?t=<random>`, `#access_token=`) | *Expected Shown("curl","https://hooks.slack.example/services/T0000000/B0000000/q7Hn…") to be the same string, but they differ at index 60* and six more | N6 segments and values not judged: 5 red; N7 `pass` not a secret name: 1 red; N15 the Telegram shape removed: 3 red |
| **E** a secret word followed by a space inside `sh -c` / `bash -lc` was shown | *Expected Shown("sh","-c","tool --password Hunter2Invented --port 5") to be the same string, but they differ at index 6* | N8 whitespace is no separator: 4 red; N9 `sshpass` is no secret word: 1 red |
| **G** U+2028 / U+2029 passed | *Expected ShownText.Printable("a b c") to be "a?b?c"* | N10 the two categories removed: 1 red |
| **coai 0** the URL detector's timeout handler answered false (open) | *Expected offenders to be empty because a match that ran out of time must redact … {"UrlRedaction.cs"}* (a source scan of every timeout handler of the rule; `SafeShapes` alone answers false — a shape not proved is a shape not shown) | N11 the detector fails open again: 1 red |
| **coai 1** a Windows caller could skip the backstop (`WindowsCommandLine.Split` was public) | *Expected typeof(WindowsCommandLine).GetMethod(Split, Public \| Static) to be <null>, but found … Split*; plus a scan that no other product code splits a Windows line | N12 `Split` public again: 1 red |
| **coai 2** curl `-uuser:pass` and sshpass `-pVALUE` attached to their option | *Expected Shown("curl","-uuser:Pa55w0rdInvented","https://x.example/") to be the same string, but they differ at index 7* | N13 no attached `-u`: 2 red |
| **coai 3** `argumentsTotal` skipped whitespace-only arguments | *Expected Of(["node","","script.js","   ",""]) to be ShownCommandLine {…, Total = 2}* | N14 the total skips whitespace-only again: 1 red |

The code (commit `fix(daemon): E15.S7 security review - A to E and G, coai code round 0 to 3`): `HighEntropy` judges the token without
its leading dashes and trailing padding and has no flag exemption; `ArgumentJudge` judges an assignment's name, requires a non-empty
value, runs arm 7 before arm 6, takes a Windows switch as a secret flag for arm 1, and gives an attached short option its two-character
prefix back; `UrlRedaction` classifies Yes / No / Unknown and judges each segment and value; `SecretShapes` adds the Telegram family, the
whitespace separator, `sshpass` and the attached `-p` / `-u`; `ShownText` replaces U+2028 / U+2029; `ShownArguments` counts every
non-empty argument; `WindowsCommandLine.Split` is internal. Costs and residuals recorded in §9: a free-text prompt holding a secret
word is redacted whole, and F, H and I stay residuals. The judge's probe (`Program.cs`, outside the repository) was not re-run
against the final build; the RED tests are its cases.

### Totals and checks

Rebased on `origin/main` a672ae74 (2026-10-18). **Windows:** Core 3202 (3113 passed, 89 skipped, 0 failed); CLI 443 (440 passed, 3
skipped, 0 failed); Scenarios 635 (344 passed, 288 skipped, 3 failed — `FixtureAnonymisationTests` was REAL: the identity list
rewrote an address-shaped `Inv3nted@db.example.invalid` in `realistic-leaks.txt` to `user@example.invalid`, now committed as the list
writes it; `EventsFlows…` and `VirtualDiskFlows…` were timeouts under load and pass alone; the fixture, privacy and redaction
contract classes were rerun: 63 of 63). **Linux** (WSL, a NEW `/tmp/testsToDelete-wc-e15s7-20261018T091038Z`, script files only): the
goldens regenerated, then `GoldenContractTests` green without the write variable (7 of 7, twice); Core 3202 (3101 passed, 101
skipped, 0 failed); CLI 443 (399 passed, 44 skipped, 0 failed); Scenarios 635 (618 passed, 15 skipped, 2 failed — the two
`StatusFlows` wall-clock budgets on a machine at 100 % CPU; two quiet reruns 9 of 9 and 9 of 9, a third had one red at 3.003 s
against the 3 s budget: the budget is 2 s of the CPU window plus the process; the rule's own cost was not measured separately — the same test passed
twice in three quiet reruns).
**Break-its:** twelve, each red, restored byte for byte and green (the nine of §7 plus the URL-first order, the backstop's
whole-argument compare and the unbounded word `key`; B3 first did not compile and was redone) — the messages are in
[module_tests.md](../research/module_tests.md). **Format** (`dotnet format wsl_care.slnx --verify-no-changes`): one real finding
(a missing final newline in `ProcessFiles.cs`), fixed in the commit it belongs to; **plan-lifecycle**, `adapter-check`,
`pin-check` and `build-flags-check`: clean. **The extension** (`npm test`, `npm run lint`): NOT run — `src_vs_code` has no
`node_modules` in this worktree and installing is refused on the machine; nothing in `src_vs_code` changed, no extension source or test
names a value the golden diff changed, and the new fields are additive (read by key), so the claim "green without a change" rests on
that reading until the extension suite is run by someone who may install.

**The re-check (Fable, 2026-10-19): SHIP.** A–E are closed by mechanism, not by case, and the judge found no new hole; it strongly
recommended two small fixes before the merge and the second coai round (proceed) added three findings. All are in the final round,
each with a RED test and a break-it (restored byte for byte):
1. **`pass` as a BOUNDED secret word** — `cmdkey /add:srv /user:alice /pass:Hunter2Invented` was shown whole on both binaries (`/pass:`
   is cmdkey's real syntax). RED: *Expected Shown("cmdkey","/add:srv","/user:alice","/pass:Hunter2Invented") to be the same string, but
   they differ*; the guards `--bypass`, `--passthrough`, `--passes`. Break-its: P1 (pass out of the blob arm) 2 red, P2 (out of
   `NamesASecret`) 4 red, P3 (unbounded substring) 6 red.
2. **A single-segment "path" that is a token** — `/<std base64>` and `./<blob>` passed into `commandLine` and `argumentsShown`. RED:
   *Expected Shown(argv) to be the same string, but they differ at index 5* (×2). Break-it P4 (stays a path) 2 red; multi-segment paths
   stay as decided (`.vscode-server/bin/<hex40>/node`).
3. **coai 9** — the CLI's own invocation was logged with `Printable` only. RED: *Expected line "Help: wsl-care "status --password=Hunter2Invented
   --json"" to contain "status --password=<redacted> --json"*. Break-it: the call put back — the same red, restored.
4. **coai 8** — the creds-family catalogue is ONE list (`RedactionCases.CredsFamilyList`) and every top-level alternative of
   `SecretShapes.Families` must have an invented token in it. Break-it P5 (a family added without a case) 1 red: *Expected uncovered
   to be empty because each family of SecretShapes.Families needs an invented token …*.
5. **coai 4** — the timeout direction is written on `SecretShapes.Hits` (true: a hit) and `SafeShapes.Match` (false: not safe).

**Final totals after the security review and the code round (2026-10-20, on `origin/main` 5d7d193d, the fixes in place).** Windows:
Core 3280 (3191 passed, 89 skipped, 0 failed), CLI 443 (440 passed, 3 skipped, 0 failed), Scenarios 635 (347 passed, 288 skipped,
0 failed). Linux (WSL, a NEW `/tmp/testsToDelete-wc-e15s7-20261020T182754Z`, script files only): the goldens regenerated with
`WSL_CARE_WRITE_GOLDENS=1` — NO file changed, so the `commandLine` diff is still the two known entries — and `GoldenContractTests`
green without the write variable (7 of 7, twice); Core 3280 (3179 passed, 101 skipped, 0 failed), CLI 443 (399 passed, 44 skipped,
0 failed), Scenarios 635 (618 passed, 15 skipped, 2 failed: the same two `StatusFlows` wall-clock budgets as before, 7.2 s against
3 s and 4.1 s against 2 s, with the machine at 80–100 % CPU from other runs). The rule is not the cause: a timing probe over the
captured tree's 51 processes ran 20 full `Redact` passes in well under 300 ms in total (under 15 ms a pass); five reruns of the
`StatusFlows` class alone on the loaded machine failed 2, 2, 2, 2 and 1 of 9 and, on a quiet machine on 2026-10-18, passed 9 of 9
twice — a budget that moves with the load. `dotnet format --verify-no-changes`, `plan-lifecycle` and the rules check: clean. The
extension's `npm test` and `npm run lint` did NOT run (no `node_modules` in the worktree, installing is refused): CI's extension legs
cover them, and nothing under `src_vs_code` changed.

### Open tail

- `processes.extraSecretWords` (an OPEN, reviewed `TextRule.SecretWord` list with a `SafeDirection.Superset`) if the owner asks.
- Kernel ring lines (`health.kernelLinesKept`) can echo a command line; routing them through `Shown` is a follow-up.
- History already written keeps what the old rule showed; `runs.historyRetentionDays` retires it (the PR body says so).
- E15.S6 is the first wire consumer of `WindowsCommandLine.Shown`.
- A quoting-aware tokenizer for the argument of `sh -c` / `bash -lc`, so only the token goes and not the whole script (the judge's
  verdict 2 above).
- **Program-specific flags inside a blob** — `sh -c "curl -u a:b …"`, `"mysql -pX"`, `"tool -p x"`: arm 2 judges ARGUMENTS, and a flag
  inside one argument is not seen; only a secret WORD in the blob (or `sshpass`) redacts it. The tail: split the blob, detect the
  program's flag, redact the blob whole.
- **A URL segment with an extension** (`…/<token>.json`) is not judged by the entropy test (the dot is outside the credential alphabet),
  and **`;` matrix parameters** (`/a;jsessionid=<token>`) are read as part of a path segment.
- **`/rp` and `/P:`** — Windows tools' short switches that carry a password (`runas /savecred`, `net use /user: … /P:`) are not
  secret names; their value is judged only by shape.
- **Over-redaction costs, accepted (deny by default):** a long CamelCase flag name with a digit (three classes, entropy above 3.5); an
  UPPERCASE-and-digit environment name of 20 characters or more; a path whose segment holds a space after a secret word
  (`/opt/Auth Tool/run` — arm 7 runs before the path arm) — which also reaches the R8 matchers (`AgentOrphans.Script`,
  `IdleGate.IsBuild`, `IsDotnetBuild` read the SHOWN line, so a redacted path is no script and no build); the captured tree has none,
  and `MatchersOverTheShownLineTests` holds that.
- **The final short re-check (Fable, 2026-10-21, 9615cad5): SHIP.** It found two roads the §1 inventory did not list, both over the
  daemon's OWN child commands from the closed template catalogue (no template takes free text), so neither blocks this story:
  - **R9 — `ActionCommandRecord.Display` / `.Detail`** (`Actions/ActionCommands.cs:157-168`) carry a child's RAW argv and its RAW
    first stderr line into the run detail (`runs show` prints it through `ShownText.Printable` only); `git` can echo a remote URL
    with a userinfo token in an error. The tail: `display` through `CommandLineText.Shown(argv)` and the stderr line through the rule.
  - **`CommandPolicy.Shown`** (`Processes/Policy/CommandPolicy.cs:105-106`) builds refusal reasons with its own printable (Cc only)
    and a 120-character PREFIX cut per argument — a prefix of a raw argument, which §3 forbids. The tail: `CommandLineText.Shown`.
  - Nits: four private `Printable` copies (`PackageCacheClean.cs:219`, `ExtraAgent.cs:164`, `DistroIdentity.cs:95`,
    `Worktrees/FolderKinds.cs:113` — the last reaches `status --json` with U+202E/U+2028 kept) should call `ShownText.Printable`;
    `SafeShapes.SingleSegment` covers `/x` and `./x` only (`~/`, `../`, a drive root and a trailing `/` are not tokens' shapes).