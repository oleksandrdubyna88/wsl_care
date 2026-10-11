# PLAN — the disk, the memory behind it, the measured restart advice, statistics kept forever, and the sidebar / full-view split (#87)

> Status: **in progress, 2026-10-10 — E15.S1 built on its branch `feat/wc-e15s1-windows-disk` (*E15.S1 as built* at the
> end); E15.S2 onwards not started.** Scope: four epics, E15–E18 — the daemon's read-only disk
> and memory report with CPU attribution by Claude session (E15), a responsiveness probe with a measured restart forecast
> and the remedy ladder (E16), statistics kept forever on a share (E17), and the shared webview kit, the full view and the
> slim sidebar of GitHub issue #87 (E18). Each story is one pull request. Story G1 (the GPU alert) is NOT here: it is built
> separately (`research/PLAN_gpu_health.md`, built on branch `feat/wc-gpu-health`); its daemon half G2 is E15.S8.
>
> Evidence: [2026-10-10_disk.md](../research/2026-10-10_disk.md) (D1–D9). Related: [module_daemon.md](../research/module_daemon.md),
> [module_vs_code.md](../research/module_vs_code.md), [module_mcp_servers.md](../research/module_mcp_servers.md),
> [architecture.md](../research/architecture.md), [PLAN_twenty_sessions_all_day.md](PLAN_twenty_sessions_all_day.md) (S7b),
> [PLAN_bundle_windows_binary.md](PLAN_bundle_windows_binary.md) (E7.S5a), [PLAN_shared_vscode_kit.md](PLAN_shared_vscode_kit.md).
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
Store build, through the user's alias), falling back to 5.1; the SYSTEM task stays on 5.1. The interop script runs in the
Windows user's context — `pwsh.exe` resolves from WSL to the alias and answered in 0.5–2.8 s (D9); as root it is NOT yet
measured, so E15.S1 measures it first and takes `pwsh.exe` when it answers, `powershell.exe` otherwise (both a closed,
absolute path; the never-list judges the program AND the script).

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
| **E15.S2** the virtual disks and the hosting volume | `ThresholdRules.FromFullRun` (`Thresholds/ThresholdRules.cs:130-148`), `RunMetrics` for growth since the previous run, `DockerTotal` / `docker system df` for Docker's data disk (no second Docker walk), `VolumeUsage` for the distro's own `/` → `Thresholds/DiskRules.cs`: `disk.vhdxGap` (warn > `disk.vhdxGapWarnGb` 20), `disk.vhdxGrowth` (> `disk.vhdxGrowthWarnGbPerDay` 10), `disk.hostFree` (the hosting volume's free < the VHDX files' possible growth, or < `disk.hostFreeWarnGb` 100), the sparse advice as TEXT naming `wsl --manage <distro> --set-sparse true` (no button: the daemon may not run it); `status.disk {vhdx[], volumes[], hostGrowth}` replaces the `host.vhdx` placeholder; `mappedBy: mount|size|none` on the wire. RED: 237.7 − 194.0 = 43.7 GiB warns; growth is a difference of two measured sizes, never of one. | medium |
| **E15.S3** who uses the disk | the two-point ledger of `Mcp/McpCpuLedger.cs` → a per-device `disk-io.json` ledger over `/proc/diskstats` (read class `System`), the root device by mount, others by size; Windows top I/O by `GetProcessIoCounters` in the Windows binary with the fixed caveat "I/O bytes count pipes and network too; the VM's disk traffic is vmmem's" (D6); `disk.busy` (a device above `disk.ioWarnMbPerSec` 200 while `pressure.io` warns). RED: deltas over the ledger interval, a reused device is no baseline. | medium |
| **E15.S4** disk health by disk number | S1's sample → `disk.events` (events on a disk that hosts a working volume; Harddisk1 counted apart), `disk.physical` (critical when not Healthy / OK), `disk.reliability` (`unknown`: "needs elevation; never elevated"). RED: 64 × `disk` 11 on Harddisk1 do not warn for disk 0. | low |
| **E15.S5** Windows memory pressure, one rule on both roads | `WindowsProbe.cs:84-97` (`TotalPageFile` / `AvailablePageFile`), `Health/VmmemAdvice.cs` → `Thresholds/HostRules.cs`: `host.commit` (warn > `thresholds.hostCommitWarnPercent` 80, critical > `…CriticalPercent` 90), `host.compression` (warn > `thresholds.hostCompressionWarnGb` 8), `host.vmmem`; evaluated by the Windows binary's `status` and by the full run over S1's lines. RED: 86 % warns, 91 % is critical, the same function on both bases. | low |
| **E15.S6** CPU saturation attributed to a Claude session | distro: `ProcessCollector` argv + the ancestor walk A18 / MCP share; Windows: `Mcp/Win32ProcessTable.cs:28`, `:46`, `:84-100` and the creation-time-checked walk of S7b.1 → `Collectors/SessionCpu.cs` (PURE: the nearest `claude` ancestor, the session from `--resume=<id>` / `-r`, else "claude <pid> (new session)", "no Claude ancestor", walk bound `cpu.ancestorWalkLimit` 64); `status.cpuSessions {groups[] {session, pid, cores, processes, topPrograms[], commandShown}}`; `cpu.sessions` (warn when one group holds > `thresholds.sessionCpuWarnPercent` 50 of the CPUs AND the machine is busy) — the sidebar recommendation's evidence. The command is `program` + safe prefix (E15.S7). RED: the D7 table as a fixture; a reused parent pid owns nothing; the walk stops at its bound. | medium |
| **E15.S7** the redaction rule widened (#87 item 2, daemon half) | `Collectors/Procfs/ProcessFiles.cs:96-148` → also `NAME=value` with a secret word anywhere, high-entropy positional tokens (≥ `processes.secretMinChars` 20), creds-shaped keys; `program` and `argumentsShown` (≤ `processes.shownSafeArgs` 2 safe arguments) on the wire. RED: a creds config key in a positional argument is redacted. | low |
| **E15.S8** GPU G2 — the daemon's verdict and ONE code table | S1's adapter lines (distro, 4-hourly) and a `cfgmgr32` reader in the Windows binary (`CM_Get_DevNode_Status`; unelevated, AOT-clean) → `gpu.adapters` (critical: any code ≠ 0), `status.gpu`, a doctor check; `Health/DeviceProblemCodes.cs` is the ONE writer of `contracts/device-problem-codes.json`; the extension's G1 table is held equal to it by a test (the only extension change). | low |
| **E15.S9** self-cost and the budget verdict | `Systemd/SystemdParsers.cs:8-26` widened to `CPUUsageNSec`, `MemoryPeak`, `IOReadBytes`, `IOWriteBytes` per unit; `IOAccounting=yes` in the shipped units (D8: today "not set"); `MeasureTree` for state and logs; the unprivileged run logs counted per hour = the extension's calls → `selfCost.cpu` (> `selfCost.cpuSecondsPerDayWarn` 600), `selfCost.memory` (> 90 % of `units.memoryMaxMb`), `selfCost.disk` (> `selfCost.stateWarnMb` 500), `selfCost.extensionCalls` (> `selfCost.extensionCallsPerHourWarn` 120): "AI OS Care itself costs more than its budget: …". RED: the measured 831 MB peak warns at 90 % of 1 GiB. | low |
| **E15.S10** Defender's CPU, recorded every full run (the owner, 2026-10-10) | S1's script gains ONE line in its CIM work: `defender=` from `Win32_PerfRawData_PerfProc_Process` where `Name='MsMpEng'` — `PercentProcessorTime` (cumulative, 100 ns), `Timestamp_Sys100NS`, `IDProcess` — readable unelevated ([2026-10-10_defender.md](../research/2026-10-10_defender.md) F3); the two-point ledger shape of `Mcp/McpCpuLedger.cs:115` → `Health/DefenderCpu.cs` (PURE: core-seconds between two reads of the SAME pid; a new pid is a new baseline, never a difference; the hours covered), `{state}/defender-cpu.jsonl` (`defender.localRetentionDays` 400, until E17.S1 takes it as a statistics record), `status.defender {coresAverage, coreHoursPerWeek, coveredHours, basis: fullRun, age}`, `host.defenderCpu` (warn when the average since the previous run is above `thresholds.defenderCoresWarn`, 1.0 core). Measured FIRST: the line's cost inside the script, cold and warm (F3: a cold CIM read alone took 7.3 s; if the script's cold run cannot stay under its 30 s ceiling, the line moves to its own fixed script). RED: two reads of one pid 10 s apart that added 10.3 core-seconds give 1.03 cores; a pid change gives no difference and says "Defender restarted — a new baseline"; a missing line is unavailable, never 0; the never-list's closed set admits exactly the new script and still refuses a one-word variant. | medium — the cold CIM cost |

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
| **E17.S2** the share, the flush, the folder picker | `Archive/BaseFolderRules.cs` with a stats context, `Files/NetworkPaths.cs`, `MountTable.IsWholeDrive`, the archive's read-back → `stats check-base`, `stats check`, `Stats/StatsShare.cs`; keys `stats.baseFolder`, `stats.flushMinutes` (60), `stats.flushTimeoutSeconds` (30), `stats.pendingWarnDays` (3); doctor `stats.store`, `stats.share`; the extension's *Choose statistics folder…* / *Keep statistics local* asked ONCE per install. Measured FIRST (read-only): a 100 KB temp + rename + read-back on `V:` through `/mnt/v`. RED: a day is confirmed only on a digest match; an offline share queues locally and blocks nothing; a stats folder inside the archive base is refused; nothing on the share is ever deleted. | high — SMB from WSL |
| **E17.S3** export, always redacted | `wsl-care stats export --period … --json` (records with `program` only — never a command line), capped by `stats.exportMaxRecords` (100 000). RED: a planted `--token=…` and a creds key never reach an export. | low |
| **E17.S4** the Windows spool merged | after E7.S5a: the Windows binary writes only its spool; the root daemon merges by watermark and never deletes it. | low (blocked on E7.S5a) |

### E18 — the KIT and the FULL VIEW (#87)

| Story | What it builds | Risk |
|---|---|---|
| **E18.K1** kit 0.2.0 + Storybook (in `dew_flow_vscode_kit`) | `Tabs` (ARIA tablist, arrow keys, a tone per tab + its word), `DataTable` (sort by click / Enter / Space with `aria-sort`, a search box, consumer-supplied units, a per-row reveal cell), `RecommendationCard` (icon + word, sentence, one button slot, evidence), theme tokens; Storybook (`@storybook/html-vite`, `addon-a11y`) — every component × state × light / dark / high-contrast × three font sizes; axe tests in the kit's CI; classes only. Publishing 0.2.0 to npmjs is owner-visible (Q1). | medium — capped at these components |
| **E18.W0** wsl_care consumes the kit | a second esbuild entry `src/webview/fullView.ts → media/full.js` and `media/full.css` (`scripts/bundle.mjs:55-68`); the exact pinned dependency; `vsix-files.txt`; the harness's tag set widened only per need, each a RED test. | medium — the kit's publish timing |
| **E18.W1** the full view | `logsPanel.ts`' panel shape, the store (no second poll), the renderers shared between both views; tabs *WSL: <distro>* and *Windows* (each per side), the tables with `program` + `argumentsShown` and a reveal that posts only an index, binary units for Held, no wrapping PIDs, "not available yet" (muted); the spawn ledger; every action a button here too (`commandButtons.ts` gains `where: 'full'`, the test widens to the full page). RED: no row shows a command line before reveal; Held 14 MiB reads "14.0 MiB"; a hidden panel receives no post. | medium |
| **E18.W2** the slim sidebar | `fieldMap.ts` gains a required `justifies` per sidebar row (§3.5's 20) and a `sidebar | full` column (one table, two readers; the generated block in `architecture.md` regenerated); the recommendation card under G1's alert; *Open full view*. RED: no `arriving` row in the sidebar; every sidebar row names its button. | low |
| **E18.W3** the Statistics tab with JSON export | E17's verbs through the client's run reads; periods today / yesterday / week / month / year; triggers by metric, why, what happened, predicted vs realised, self-cost; *Export JSON…* writes exactly the daemon's redacted bytes (the extension's first file write, named in `structure.test.ts`). | low |
| **E18.W4** Help in the coai design, five languages | the kit's Help (catalogue, digests, search); an English article for every command, setting, verdict and rung (a coverage test); `wslCare.helpLanguage`; ru / uk / de / es as one content PR each (Q4); the UI stays English. | low |
| **E18.W5** accessibility, both pages, and the close of #87 | keyboard sort / search, ARIA, the kit's contrast job, `pagesShareOneRenderer.test.ts`; docs. The PR body says "Closes #87". | low |

## 5. Build order and pull requests

E15.S1 → E15.S2 → E15.S5 → E15.S10 → E15.S7 → E15.S6 → E15.S4 → E15.S3 → E15.S9 → E15.S8 ‖ E16.S1 → E16.S2 → E16.S3 → E16.S4 ‖ E16.S5 (after E15.S10; its checkbox half does not wait for the ladder) ‖
E17.S1 → E17.S2 → E17.S3 ‖ E18.K1 → W0 → W1 → W2 → W3 → W4 → W5. E16 starts after E15.S1; E17.S1 after E16.S1 (the probe
records). E17.S4 and the Windows halves' panel rows wait for E7.S5a and S7b's runner. At most three open pull requests in
this repository, one per lane; E18.K1 lives in the kit's repository.

## 6. Boundaries with the other plans (the same table is written into each of them when its story starts)

| Item | This epic | The other plan |
|---|---|---|
| GPU | E15.S8: the daemon's `gpu.adapters`, the contract table with one writer, a doctor check; a test holding G1's table equal | G1 (`research/PLAN_gpu_health.md`, IMPLEMENTED 2026-10-10 on branch `feat/wc-gpu-health`): the extension's read, the top alert, the bar mark, *Enable <GPU>*; builds no daemon code |
| E7.S5a ([PLAN_bundle_windows_binary.md](PLAN_bundle_windows_binary.md)) | the Windows tab reads `wsl-care.exe status --json` through `WindowsCareClient` once it exists (a follow-up story) | the bundle, the attestation, `WindowsCareClient` and its first `--version` |
| S7b ([PLAN_twenty_sessions_all_day.md](PLAN_twenty_sessions_all_day.md)) | the ladder's A21 rung as `arrivesWithS7b`; the Windows probe as a `watch` step S7b's runner will call; E15.S6 reuses S7b.1's Windows ledger | A21, the Windows `watch`, its runner |
| The Archive page (E10.S2a, [PLAN_wsl_care_daemon.md](PLAN_wsl_care_daemon.md) §15s) | E18.W1 may move it into the full view on the kit's `DataTable` (sort, search, a per-row reveal of the session path) | §15s builds it on `logsPanel.ts`' shape: decisions in `archivePage/archiveController.ts` / `archiveViewModel.ts`, the rendering in ONE file (`media/archive.js`), a closed message set naming rows by index — no sort or search of its own |
| E10 archive (#88, #92) | `configCall.ts` gains a second key; the archive's folder flow reused; a stats folder may not lie inside the archive base | unchanged |
| The kit | E18.K1 = kit 0.2.0 | the kit's 0.1.0 release; coai's own switch |

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
| Q1 | The kit on npmjs (public) as the kit plan decided on 2026-10-02? | (a) publish 0.2.0 to npmjs with provenance, wsl_care pins it — recommended; (b) a committed tarball per release (no Dependabot, a blob in git); (c) a submodule (rejected earlier with a reason) | E18.W0 |
| Q2 | Pre-fill the statistics folder dialog with the archive base's drive? | (a) yes — recommended; (b) a blank dialog | E17.S2's UI |
| Q3 | The root daemon runs a second fixed PowerShell script every full run (System log, registry, storage cmdlets) | (a) yes, `pwsh.exe` if it answers as root, else 5.1 — recommended; (b) only through the Windows binary (waits for E7.S5a, needs new P/Invoke families) | E15.S1 |
| Q4 | Who writes the ru / uk / de / es Help? | (a) AI-drafted, reviewed by the owner, one PR per language — recommended; (b) English with "not translated yet" | nothing in W4 itself |
| Q5 | *Shut down WSL…* while a cleanup or the archive runs | (a) greyed — recommended; (b) available with a stronger modal | E16.S4's modal |
| Q6 | This machine's Defender exclusions came BEFORE the first per-run record, so the weekly saving has no "before" days here | (a) say so, and offer *Measure now* as the only figure on this machine — recommended; (b) the owner unchecks the box for 7 days to build a baseline (costs Defender CPU for a week); (c) take F1's probe as the baseline (a synthetic workload, not core-hours per week — rejected) | E16.S5's saving line only |

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
   `pwsh` is an app-execution alias under the USER's profile (`%LOCALAPPDATA%\Microsoft\WindowsApps`), a file the Windows user can
   replace; the resolver's system-drive fallback starts only programs from a closed list in administrator-only folders, after
   checking root-owned ancestors and the `MZ` header (an alias is a reparse point, not checked). Admitting it would let the root
   daemon start a user-writable program — a policy decision for the owner and the gate. Also, as of this story the distro's own
   PATH answered `command -v pwsh.exe` with nothing (only `powershell.exe`), unlike D9. The clock probe's program is unchanged.
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

