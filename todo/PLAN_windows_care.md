# PLAN — `wsl-care` on Windows: monitor, clean and fix the host itself

> Status: **plan only, nothing implemented yet (2026-10-02).** Scope: the `win-x64` build of the
> `wsl-care` AOT binary becomes a full daemon on Windows (scheduler, collectors, actions), plus the
> Windows sections of the VS Code extension.
>
> Parent plan: [PLAN_wsl_care_daemon.md](PLAN_wsl_care_daemon.md). Evidence:
> [2026-10-02_windows_baseline.md](../research/2026-10-02_windows_baseline.md),
> [2026-10-02_competitor_survey.md](../research/2026-10-02_competitor_survey.md).

## 1. Symptom and goal

**Symptom.** "After ~3 months of active work after a reinstall Windows starts to lag hard." Measured
2026-10-02, 55 minutes after boot: 2.3 GB of 91.6 GB RAM available, commit 113 of 144 GB, CPU at 100 %;
`vmmemWSL` 40.5 GB and Ollama's `llama-server` 30–40 GB; non-paged pool 5.5 GB; Fast Startup carrying
kernel state across nightly shutdowns; `%TEMP%` 35 GB with 155 k entries and 23 k leaked folders; a
service crash-looping 441 times; reliability index 3.5.

**Goal.** The same three verbs as on the WSL side — **record** (so the next "it lags" has data),
**fix automatically** what is safe, **show and offer** the rest with a preview — for the Windows host.

**Success criterion.** Over one working week: available RAM at 18:00 ≥ 15 GB; non-paged pool growth
known per day and attributed; `%TEMP%` below 5 GB and 10 k entries; no crash loop older than a day
unreported; every lag complaint can be matched to a record in the history.

## 2. Decisions (2026-10-02)

| Question | Decision |
|---|---|
| Is Windows in scope | **yes** — the user extended the daemon to Windows |
| Binary | the same C# Native AOT code, `win-x64` (already planned as the probe) |
| Scheduler | **Windows Task Scheduler**, the analogue of the systemd timer: task `\wsl-care\collect` — at logon + 20 min, then every 4 h, run as the user, `StartWhenAvailable`, battery-agnostic (desktop) |
| Admin work | a second task `\wsl-care\collect-elevated` registered once by the installer with *Run with highest privileges* for the few admin-only actions (§5); everything else runs unelevated. Buttons that need admin trigger that task — no UAC prompt per click |
| History | `%LOCALAPPDATA%\wsl-care\` with the same file shapes as Linux (§6 of the parent plan); plus a built-in **performance counter log** (below) |
| Safety | as on Linux: safe actions automatic, the rest button-with-preview or report-only; `dryRun` for the first week |

**Performance history without writing a sampler** (the Windows analogue of sysstat): the installer
creates a Data Collector Set with `logman create counter wsl-care-perf -si 00:05:00 -f bincirc -max 512`
over Memory (Available MBytes, Committed Bytes, Pool Nonpaged/Paged Bytes, Standby, Modified), Process
(Private Bytes, Handle Count of every process), Processor, PhysicalDisk — a circular binary log readable by
`relog`/Performance Monitor and by the daemon. (May need the *Performance Log Users* group; verify.)

## 3. What is monitored (Windows)

| Area | Metrics | Warn / alert (starting points) |
|---|---|---|
| Memory | available, commit / limit, standby, modified, non-paged and paged pool **and their growth since boot**, memory compression size | available < 10 % warn, < 5 % alert; NP pool > 2 GB or > 300 MB/h growth |
| Big holders | top 25 by private bytes with start time; **`vmmemWSL`**, **`llama-server`** (+ loaded model, context, idle time from `ollama ps` / `/api/ps`), process **counts by name** (duplicates: `creds-mcp`, `node`, `conhost`, `wsl`, `Code`, `msedgewebview2`) | `llama-server` holding > 10 GB and idle > N min; > N instances of one MCP server |
| Orphans | processes whose parent has exited, from known families (MCP servers, `node`, `dotnet` build servers, `conhost` chains), older than N h, idle | report |
| Kernel state | uptime **since last full boot**, Fast Startup flag, `hiberfil.sys` size, handles total | uptime > 7 d with Fast Startup on → hint |
| Disk growth | **what grew since yesterday** (WizTree model): known folders daily; MFT scan when elevated | folder +5 GB/day |
| `%TEMP%` | size, entries, stale `swap.vhdx`, empty random folders, files > N days, `claude\` work folders | > 10 GB or > 20 k entries |
| Auto-start | **baseline diff** of Run keys, Startup folders, scheduled tasks, services, drivers (non-Microsoft only), with "new since <date>" | new entry → report |
| Event signals | crash loops (`.NET Runtime 1026`, `Application Error 1000` per app per day), `disk 11/51/153` per device, `WHEA`, display TDR `4101`, `Kernel-Power 41`, bugchecks, `Volsnap 25`, BitLocker PCR mismatch, `HttpEvent 15005` storms | crash loop ≥ 10/day; any WHEA/TDR/bugcheck |
| Health | reliability index trend, pending reboot, Windows Update state, Defender status (+ exclusions and Dev Drive trust when elevated), Search service and scope, power mode, event-log sizes | index < 5 |
| WSL from outside | VHDX sizes vs. used inside, `.wslconfig` audit (cap, `autoMemoryReclaim`, no `sparseVhd`), Docker Desktop `daemon.json` (log rotation, builder GC) | |
| AI agents | Windows side of the parent plan §4.6 and the archive plan — the Windows agents walk and its one-file cache are built by the parent's E7.S5 (parent plan §15q, *Boundaries*); the Windows collectors, task and history stay here (E11); `%TEMP%\claude\` is never walked or cleaned | |
| MCP servers of the AI agents | `coai-mcp.exe`, and `creds-mcp.exe` once the catalogue opens (parent plan E7.S2d Q-M2): count, owner, orphans (parent gone, or a parent created after the child — a reused pid), CPU over the interval through `GetProcessTimes` — named by [PLAN_twenty_sessions_all_day.md](PLAN_twenty_sessions_all_day.md) S7 (measured 2026-10-07: 85 `creds-mcp.exe`, 66 orphaned, 1.32 GB); the collector is E11's, a stop button by pid AND creation time is E12's | orphans > 0 |
| Dev caches | NuGet (`global-packages`, `http-cache`, `v3-cache`), npm, pnpm, pip, VS Code (`Cache`, `CachedData`, `GPUCache`, logs, `CachedExtensionVSIXs`, `workspaceStorage` of missing folders), Playwright, .NET SDK/workload inventory | |

## 4. Actions (Windows)

| # | Action | Trigger | Default | Admin | Safety |
|---|---|---|---|---|---|
| W-A1 | **Unload an idle Ollama model** (`ollama stop <model>` / `keep_alive: 0`) | `llama-server` > 10 GB and no request for `ollama.idleMinutes` (20) | **off** (button; opt-in auto) | no | reload is automatic on the next request |
| W-A2 | `%TEMP%`: stale `<GUID>\swap.vhdx` (not locked, not the running VM's), empty random-name folders older than 1 day, files and folders older than `temp.olderThanDays` (7), locked items skipped — **on every 4-hour run** (user decision 2026-10-02) | > 1 GB or > 20 k entries eligible | on | no | temp by definition; locked = in use = skipped |
| ~~W-A3~~ | ~~Claude Code work folders in `%TEMP%\claude\`~~ — **dropped 2026-10-02 (user): nothing under `%TEMP%\claude\` is ever cleaned**, because it carries Claude session material. W-A2 excludes that whole subtree by an explicit, tested guard; its size is still reported | — | — | — | — |
| W-A4 | NuGet `http-cache` + `temp` (`dotnet nuget locals http-cache --clear`, `temp --clear`) | > 2 GB | on | no | caches; `global-packages` only by button |
| W-A5 | VS Code caches (`Cache`, `CachedData` older than the running build, `GPUCache`, logs > 14 d, `CachedExtensionVSIXs`) | Code **not running** | on | no | rebuilt on start |
| W-A6 | `workspaceStorage` entries whose folder no longer exists | any | button | no | state of deleted projects |
| W-A7 | `cleanmgr /sagerun` profile: WU cleanup, Delivery Optimization, WER, thumbnails; `Delete-DeliveryOptimizationCache` | weekly | on | **yes** | Microsoft's own handlers |
| W-A8 | `DISM /AnalyzeComponentStore` → `StartComponentCleanup` (never `/ResetBase`) | "cleanup recommended" | button | **yes** | supported path |
| W-A9 | Crash dumps / WER queues / LiveKernelReports older than N days (after their signal is recorded) | eligible | button | partly | diagnostics already captured |
| W-A10 | **Auto-start entry: disable** (StartupApproved / task disable / service → *Manual*), with an undo journal | user picks | button | some | reversible, one entry at a time; never bulk |
| W-A11 | Orphaned process family: terminate | user picks | button | no | never a process with a window or recent CPU |
| W-A12 | Docker/WSL VHDX compaction: `wsl --shutdown` → `Optimize-VHD` / diskpart | free inside > 10 GB | button | **yes** | stops WSL — confirmation names what is running |
| W-A13 | ProBalance-style temporary de-prioritisation of background CPU hogs (never the foreground app, never protected processes), auto-revert | CPU > 85 % for 30 s | **off** (opt-in) | no | temporary |
| W-A14 | .NET: `dotnet workload clean`; set `DOTNET_CLI_WORKLOAD_UPDATE_NOTIFY_DISABLE=1` (user env) | inventory shows leakage | button | no | stops the temp-folder leak |
| W-A15 | AI-session archive (Windows side) — the verbs, the lock and the reconcile are E9.S5's ([PLAN_wsl_care_daemon.md](PLAN_wsl_care_daemon.md) §15r); this plan's E11 task runs `wsl-care.exe archive run` as the user on its schedule | per the archive plan | per plan | no | move, never delete |

**Configuration advisors** (report + one-click apply with undo, never silent):
`OLLAMA_KEEP_ALIVE` (W1), `.wslconfig` `memory=` cap (W2), Fast Startup off (W3), Search scope
exclusions (W12), Dev Drive cache env vars (W11), event-log sizes (W16), Docker Desktop `daemon.json`
log rotation + builder GC, power mode.

**The WSL cap, as the user decided it (2026-10-02):** the extension only **shows** the recommendation —
*"set `memory=36GB` in `.wslconfig`"* — with the current value next to it; it does not write the file on
its own. The VM's usage against its ceiling (`vmmemWSL` working set vs. the configured or default
ceiling) is drawn green / amber / **red above 90 %**.

**Accepted from the competitor survey (2026-10-02, user: "accept all"):** every item ranked in
[2026-10-02_competitor_survey.md](../research/2026-10-02_competitor_survey.md) §1 and §2 is in scope, at
the safety level the survey gave it; its §3 *never* list is binding.

**Dropped (user, 2026-10-02):** W10 — the ClawsKey keyboard's USB-storage controller errors. Nothing can
be done from here; the event is still COUNTED so a change in its rate is visible, but no hint or action
is offered for it.

**Never** (in addition to the parent plan's list): registry cleaning, RAM boosters / standby flushing,
Prefetch deletion, bulk service changes, Defender exclusions for whole drives or disabling AV,
third-party driver updates, `C:\Windows\Installer`, WinSxS by hand, `/ResetBase`, auto-deleting Downloads.

## 5. First step: the elevated diagnosis (one-off, by hand, with the user)

Before automating anything that needs admin, run once elevated and record in `research/`:
`fsutil devdrv query D:`/`F:`; Defender exclusions and performance-mode state; `Get-MMAgent`;
`DISM /AnalyzeComponentStore`; the Search index size and scope; Autoruns snapshot as the auto-start
baseline; and the **per-adapter split of the NetAdapterCx pool** (disable each *disconnected* adapter
for ten seconds, read `cxbm`/`NxRx`, re-enable — the only way to attribute it without a kernel
debugger).

**The pool tags themselves turned out NOT to need admin** (2026-10-02): `NtQuerySystemInformation`
class 22 answers unelevated, so the daemon reads tag-level pool usage on every run and the extension
shows the top tags with their owning driver — see the Windows baseline's *Non-paged pool, attributed*.

## 6. Extension (Windows sections)

The side panel gets a **Windows** group mirroring the WSL one: Memory (with `vmmemWSL` and `llama-server`
called out, and a **Unload model** button), Kernel state (uptime, Fast Startup, pools with a growth
sparkline), `%TEMP%`, Auto-start (diff with **Disable** buttons and an *Undo* list), Event signals, Dev
caches, Health. The Cleanup table, Logs page and AI-agents section show both sides with a side column.
Admin actions are marked with a shield and run through the elevated task.

## 7. Build order

1. Elevated one-off diagnosis (§5) → `research/`.
2. `WindowsProbe` collectors: memory/pools/processes/duplicates, Ollama, `%TEMP%`, events, auto-start
   baseline, health; `status`/`collect` on Windows.
3. Installer for Windows: the two scheduled tasks, the `logman` collector set, `%LOCALAPPDATA%\wsl-care\`.
4. Actions W-A2…W-A5, W-A14 (unelevated, safe) behind `dryRun`; then W-A1, W-A6, W-A9…W-A13; then the
   elevated W-A7, W-A8 through the elevated task.
5. Configuration advisors with undo journal.
6. Extension Windows group.
7. One week of `dryRun`; review and tune in `research/`.

## 8. Test plan

- Collectors against recorded fixtures from this machine (`Get-Counter` samples, process lists with
  parents, `ollama ps` output, event exports, the `%TEMP%` tree shape incl. GUID swap files and empty
  random folders).
- W-A2: a locked `swap.vhdx` and the running VM's swap are never selected; an empty random folder of today
  is kept; a non-empty one older than N days goes; reparse points are not followed.
- W-A5 refuses while any `Code.exe` runs; W-A10 writes an undo entry and undo restores the exact value;
  W-A12's preview lists running distros and containers.
- Advisors: applying and undoing each setting round-trips exactly (registry/env/ini/json).
- Elevation: an admin action from a button runs through the elevated task and reports its result back;
  without the task registered it reports "needs the elevated task" instead of failing silently.
- Never-list property test, as on Linux.

## 8a. Gate round 1 — amendments (2026-10-02)

- **`logman`** (finding 11): output `%LOCALAPPDATA%\wsl-care\perf\`, circular 512 MB, created by the
  installer as the user; `doctor` checks the set runs and restarts a stopped one; if creation is refused
  (the *Performance Log Users* group), the daemon falls back to its own per-run counters and says so.
- **The elevated channel** (finding 13): a request is one JSON file `{ id, action, preview }` in
  `%ProgramData%\wsl-care\requests\` (ACL: the user may create, SYSTEM and administrators read), with
  `action` from a fixed allowlist; `schtasks /run` triggers the elevated task, which validates the id
  and the action, ignores everything else, writes `results\<id>.json`, and times out after 10 minutes.
  **Amended by the retro review of PR #3 (coai, 2026-10-06):** any process of this user can write a request, so
  a request is untrusted input, and a confirm must not be cheaper than a preview. A confirm request
  (`preview: false`) must quote a one-time token that a PREVIEW run of the elevated task itself wrote into its
  `results\<id>.json`, together with the targets it showed. The task refuses a confirm with a missing, reused or
  expired token (one use, 10 minutes), and re-checks the previewed targets at confirm time. Any difference is a
  refusal naming it. For W-A12, which stops WSL, the re-check includes that nothing named in the preview is
  running. Stated plainly: the token proves that a preview happened, not that a person clicked. A process of
  this user can also run the preview, so the protection is against a confirm with no preview behind it and
  against changed targets. E12's plan round decides whether that is enough for each elevated action, or
  whether an action stays a button that runs elevated only from an interactive UAC prompt.
- **Pool tags without admin** (finding 3, rejected): measured to work unelevated; a failing call falls
  back to totals with the reason.
- **The first Windows action, and where its boundary is** (2026-10-08): *Start Windows Time* — a button that runs ONE
  elevated PowerShell from an interactive UAC prompt, the shape this section left open — and the single-purpose SYSTEM
  task that restarts `w32time` by itself (its story 2) are built in
  [PLAN_windows_time_guard.md](../research/PLAN_windows_time_guard.md) (boundary table §4 there) and
  [PLAN_windows_time_task.md](../research/PLAN_windows_time_task.md) — the guard is ONE fixed task, `\wsl-care\windows-time-guard`,
  with no request files and no token; the `\wsl-care\` folder it creates is shared, and its *Remove* deletes the folder only
  when nothing else is in it. The generic elevated channel above
  (requests, the one-time token, the elevated task `collect-elevated`) stays THIS plan's; the time guard must not grow into it.

## 9. Definition of Done

- [ ] §5 elevated diagnosis recorded; the non-paged-pool consumer named.
- [ ] Windows tasks and the perf-counter log run; history and run details appear under `%LOCALAPPDATA%`.
- [ ] W-A1…W-A15 implemented with preview, undo where applicable, `dryRun` week reviewed.
- [ ] Extension shows the Windows group; Logs and Cleanup cover both sides.
- [ ] §1 success criterion checked over a working week and recorded.
- [ ] Plan promoted to `research/` when shipped.
