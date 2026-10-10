# PLAN — the disk, the memory behind it, the measured restart advice, statistics kept forever, and the sidebar / full-view split (#87)

> Status: **plan only, nothing implemented yet, 2026-10-10.** Scope: four epics, E15–E18 — the daemon's read-only disk
> and memory report with CPU attribution by Claude session (E15), a responsiveness probe with a measured restart forecast
> and the remedy ladder (E16), statistics kept forever on a share (E17), and the shared webview kit, the full view and the
> slim sidebar of GitHub issue #87 (E18). Each story is one pull request. Story G1 (the GPU alert) is NOT here: it is built
> separately (`todo/PLAN_gpu_health.md` on branch `feat/wc-gpu-health`); its daemon half G2 is E15.S8.
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
| the GPU alert (G1), the Windows Time guard | **Road 3 — the extension's own unelevated PowerShell** (existing) | NOT widened by this epic |

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
- **K** = the median, over the last `probe.maxBootsUsed` (10) boots with a baseline, of the hours until the factor stayed ≥
  `probe.degradedFactorPercent` (150) for `probe.degradedSamples` (3) calm samples; a boot that never degraded is censored
  and said ("no previous boot slowed past 1.5×; the longest ran H h"). **M** = the uncensored boots used.
- **The minimum (the owner's rule):** while the history spans < `probe.minHistoryDays` (7; 1–90) days OR fewer than
  `probe.minBoots` (2) boots have a baseline, there is NO factor and the text is exactly *"a minimum forecast needs at least 7
  days of history; N days so far"* (7 = the key's value); the verdict `probe.factor` is `unknown` with that reason.
- **The sentence** (daemon-made, shown verbatim): *"now 412 ms vs 180 ms after the last boot → a restart is expected to make
  local steps ~2.3× faster for about the next 9 hours (based on 4 boots)"*, per side (`wsl --shutdown` for the VM, a Windows
  restart for the host).
- **Validation (falsifiable):** at each boot-id change a `boot` record keeps the last forecast (N, K) and, as they become
  known, the realised N′ = last calm score before ÷ the new baseline and K′ = the new boot's degradation hour. The Statistics
  tab shows predicted vs realised per boot; a forecast wrong (N′ below the degraded factor) on 3 of the last 5 boots reads
  "uncertain".
- **Only a boot that FOLLOWED the advice validates it** (plan round). Each `boot` record carries its cause: `advised` — the
  extension's *Shut down WSL…* ran after showing the forecast: before it starts `wsl.exe --shutdown` it calls the new
  unprivileged verb `wsl-care probe mark-restart --forecast <id of the shown forecast>`, which writes
  `$XDG_STATE_HOME/wsl-care/restart-marker.json` (time, the forecast's id), read and consumed by the root daemon on the next
  boot — or `other` (no fresh marker: a crash, an update, `wsl --shutdown` typed by hand, a Windows restart, which this
  product only advises). The predicted-vs-realised table and the "uncertain" rule use `advised` boots only; `other` boots
  are listed apart and still feed the baselines and K (a boot is a boot for how fast the machine degrades). A marker older
  than `probe.restartMarkerMaxMinutes` (10) is ignored.

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

### E16 — the PROBE, the FORECAST and the remedy LADDER

| Story | What it builds | Risk |
|---|---|---|
| **E16.S1** the probe | §3.3's components in `Probe/ResponsivenessProbe.cs` (one implementation, both OS), `Probe/ProbeStore.cs` (`{state}/probe.jsonl`, `probe.localRetentionDays` 400 until E17 takes the forever copy), the watch step and the full-run step under the lock, `wsl-care probe [--json]` (+ the verb register), `status.probe`, `probe.budget`; `RunBudget.WatchRunWorstCase` grows by `probe.maxMilliseconds`. Measured FIRST: ten probe runs on the owner's distro. RED: fixed work; over-budget kept and flagged; a busy machine skips and says why; the files live only under the state folder. | medium |
| **E16.S2** the forecast | `Probe/Forecast.cs` (PURE: baseline, N, K, M, censoring, the minimum, the sentence), `Probe/BootChanges.cs` (a `boot` record with the last forecast; realised values fill in later); the keys of §3.3; `probe.factor`. RED, with the owner's words: six days of history give NO factor and say "a minimum forecast needs at least 7 days of history; 6 days so far"; the minimum in the sentence is the key's value; a 30-day synthetic fixture with four boots gives N, K, M; a forecast wrong on 3 of 5 boots reads "uncertain". A `research/` note states the method with the fixture's numbers. | high — statistics the gate will argue |
| **E16.S3** the ladder and the ONE recommendation | the previews and measured results of A19, A11, A18, A1, A2, the container stats, `VmmemAdvice`, the forecast, the run history → `Thresholds/Ladder.cs` (PURE: rungs in cost order, each `{id, kind: action|extensionButton|advice, addresses[], state, wouldFree, lastEffect, text}`; the recommendation = the first rung with something to do among those addressing the WORST non-ok verdict; "all within limits"); S7b's A21 shown as `arrivesWithS7b` on the wire, never in the sidebar. RED: the recommendation names the group, the limit, the measured value and the button; severity first, then cost; a rung's last effect comes from the history, never a guess. | medium |
| **E16.S4** *Shut down WSL…* (ask first) and the Disk & memory section | the restart marker first (`wsl-care probe mark-restart`, §3.3), then `WslCareClient` (the only argv speller, `src/client/WslCareClient.ts:17-28`) gains the one request `wsl.exe --shutdown`; a modal names what ends ("every WSL session and every program in the distro — N agent processes, M containers now"), the forecast line and that the effect is measured; greyed while a run is live / queued or the archive runs; `wslCare.timeouts.wslShutdownSeconds` (120); command + button; the sidebar's section renders `status.disk`, `host.*`, the forecast and the ladder (rungs as buttons where the cleanup controller offers the action, advice lines otherwise). RED: exactly `wsl.exe --shutdown`; a decline starts nothing; the strict fake accepts only `['--shutdown']`; the tripwire refuses the real one. Never automatic. | medium |

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

E15.S1 → E15.S2 → E15.S5 → E15.S7 → E15.S6 → E15.S4 → E15.S3 → E15.S9 → E15.S8 ‖ E16.S1 → E16.S2 → E16.S3 → E16.S4 ‖
E17.S1 → E17.S2 → E17.S3 ‖ E18.K1 → W0 → W1 → W2 → W3 → W4 → W5. E16 starts after E15.S1; E17.S1 after E16.S1 (the probe
records). E17.S4 and the Windows halves' panel rows wait for E7.S5a and S7b's runner. At most three open pull requests in
this repository, one per lane; E18.K1 lives in the kit's repository.

## 6. Boundaries with the other plans (the same table is written into each of them when its story starts)

| Item | This epic | The other plan |
|---|---|---|
| GPU | E15.S8: the daemon's `gpu.adapters`, the contract table with one writer, a doctor check; a test holding G1's table equal | G1 (`todo/PLAN_gpu_health.md`, branch `feat/wc-gpu-health`): the extension's read, the top alert, the bar mark, *Enable <GPU>*; builds no daemon code |
| E7.S5a ([PLAN_bundle_windows_binary.md](PLAN_bundle_windows_binary.md)) | the Windows tab reads `wsl-care.exe status --json` through `WindowsCareClient` once it exists (a follow-up story) | the bundle, the attestation, `WindowsCareClient` and its first `--version` |
| S7b ([PLAN_twenty_sessions_all_day.md](PLAN_twenty_sessions_all_day.md)) | the ladder's A21 rung as `arrivesWithS7b`; the Windows probe as a `watch` step S7b's runner will call; E15.S6 reuses S7b.1's Windows ledger | A21, the Windows `watch`, its runner |
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

Stated assumptions (decided, not asked): `git status` is not a probe component; Windows top I/O stays a labelled slice of
E15.S3; the Statistics tab shows this machine only; the self-cost defaults come from D8; a restart's effect is attributed to
ANY boot-id change, not only to the button; `host.*` verdicts colour the status bar, `selfCost.*` and `stats.*` do not; the
Windows probe's runner is S7b's, not a new scheduled task.

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

## 12. Definition of Done (per story, and for the epic)

- [ ] Every new figure has its basis and an honest unavailable reason; every threshold is a key with its range and default.
- [ ] Every verdict and the recommendation are computed in `WslCare.Core`; the extension holds no threshold.
- [ ] RED first, GREEN after, a break-it of the product line; all suites green; goldens and contracts regenerated.
- [ ] Every action is a panel button; *Shut down WSL…* asks first and never runs on its own; nothing elevates, compacts or
      sets a VHDX sparse.
- [ ] Command lines are hidden by default in the UI and never in an export (tested with a creds-shaped fixture).
- [ ] The forecast says the 7-day minimum sentence and gives no factor until the history allows one.
- [ ] Statistics carry the machine id, build and install date; the share is a verified copy; no record is lost offline.
- [ ] Docs, module maps, the research index and this plan's as-built are updated; each finished story is recorded here;
      the epic's last PR closes #87; the plan is promoted when its last story ships.
