# PLAN — twenty Claude sessions run normally for 24 hours (epic E14)

> Status: **in progress, 2026-10-09: S1 built (§ 13, PR #48); S2a built (the idle MCP watchdog, A19, PR #55); S2c built (the user's own MCP programs, PR #58); S2d built (playwright-mcp, an interpreter-run server, in the catalogue, PR #61); S3 built (A3's timer waits for idle build servers; language servers for A11, PR #60); S5 built (memory and swap before the evening: a report, PR #63); S6 built (the "machine busy" signal: `wsl-care busy`, `pressure.cpu` / `pressure.io`, PR #62); S7a built (the Windows side's MCP servers, read-only, and the vmmem advice); S2b, S4, S7b (a stop on Windows, the owner's), S8 plan only.** Scope: the daemon's MCP metric (S1), an MCP watchdog action
> (S2), a build-server reaper (S3), CPU fairness inside WSL (S4), memory and swap before the evening (S5), a "machine busy"
> signal (S6), the Windows side's MCP servers and advice (S7, inside E11/E12's scope), and a 24-hour soak campaign (S8).
>
> Related docs: [2026-10-07_evening_overload.md](../research/2026-10-07_evening_overload.md) (the evidence),
> [module_mcp_servers.md](../research/module_mcp_servers.md) (the metric S1 fixes), [module_daemon.md](../research/module_daemon.md),
> [architecture.md](../research/architecture.md), [PLAN_wsl_care_daemon.md](PLAN_wsl_care_daemon.md) § *E7.S2d* (the metric's
> plan, its owner question Q-M1 that S2 answers), [PLAN_windows_care.md](PLAN_windows_care.md) (E11/E12, where S7 lands).

## 1. Symptom

**The owner's goal, 2026-10-07 evening:** "I need 20 Claude sessions to run normally for 24 h." Today the machine degrades by
evening and the agents' MCP servers cannot reconnect. Measured that evening
([2026-10-07_evening_overload.md](../research/2026-10-07_evening_overload.md), each row with its method and time):

| What | Evidence | Row |
|---|---|---|
| Windows saturated | CPU 100 % of 24, 22.5 GB free of 91.6 GB, commit 89 of 146 GB, `vmmemWSL` 29.5 GB (32.5 GB at 19:47Z) | W1–W3, W8 |
| WSL saturated | load 36 on 24 CPUs, PSI cpu some avg10 31 %, swap 9.9 of 12 GB used, `Committed_AS` 49 GB > `MemTotal` 47 GB | L1–L3 |
| who burned it | `coai-mcp` 6.9 cores (8 instances of the unfixed 0.44.0), `dotnet` 4.9, `VBCSCompiler` 4.9, `claude` 0.65 — during another agent's busy-loop test (a confound) | L7, W5 |
| why MCP could not reconnect | `coai-mcp` 0.44.0 reads its vault through Windows interop and sweeps its session files BEFORE answering `initialize`; under load that took > 30 s and Claude Code's 30 s connect budget killed it | L8 |
| **our metric was blind** | daemon 0.2.0 `status --json`: all 8 instances `cpuPercent 0`, kind `idle` — while 5 s slices showed up to 1.69 cores (mean 0.61): the servers burst with a period near a minute (not measured exactly) and the 1 s window misses the burst | M1–M3 |
| Windows MCP leak | 85 `creds-mcp.exe`, 1.32 GB, **66 with a dead parent** | W9 |

The upstream defects (`coai-mcp`'s start path — ConnectOtherAIs #690, shipping as 0.44.2; `creds-mcp.exe` not exiting when
its client goes) are not this repository's to fix. What IS ours: a care daemon whose metric reported the exact failure as
"idle", that can see but not stop a leaking server, that cannot tell an agent "the machine is busy now, wait", and that
has never been run against the target load.

## 2. Goal and pass criteria

Twenty Claude Code sessions (plus their MCP servers, builds and tests) run for 24 hours on this machine with:

- **no MCP connect timeout** attributable to load (Claude Code's own "connection timed out after 30000ms" lines, counted);
- **PSI cpu some avg300 below a threshold X** (owner question Q8; the 2026-10-07 evening read 31 % avg10) and no swap
  exhaustion (swap free never below Y GB);
- **MCP servers idle when idle**: the S1 metric's idle-period CPU of all `coai-mcp` instances together below Z cores;
- **every number above read from the daemon itself** (S1, S6) and recorded by the soak campaign (S8) in `research/`.

The pass criteria are S8's; S1–S7 are what is built so that S8 can pass and so that a failing hour is attributable.

## 3. What exists already (reuse first)

| Capability | Where (verified at `56d9c0b`) | Used by |
|---|---|---|
| MCP instances, owner walk, kinds, starts | `src_daemon/src/WslCare.Core/Mcp/McpServerCollector.cs:17-79` (`SampleAsync`, `CpuAsync`), `McpJudge.Kind` `:107-112` | S1 changes the CPU basis; S2 selects from it |
| one pid's start ticks + CPU ticks | `Collectors/Procfs/PidSamples.cs:23-29` (`PidSamples.Read`) | S1, S2, S3 |
| a per-identity CPU history across runs (pid + start ticks + boot id, both clocks, root state file, private, capped) | `Actions/Suspects/AgentCpuHistory.cs:18-148` (`AgentCpuFile`, `SampleTime` `:27-31`, `BootId` `:130-131`) | S1's ledger follows its shape; `SampleTime` and the boot id move to `Collectors/Procfs` so a collector does not depend on the Actions layer (the coai code-round finding 1 of E7.S2d, same reason) |
| signals by pid AND start (pidfd), SIGTERM then SIGKILL after a grace | `Actions/Suspects/SuspectSignals.cs:41` (`EndAllAsync`), keys `:19` | S2 |
| a button-only action id | `Actions/ActionId.cs:41-44` (`ButtonOnlyIds`, A18) | S2's A19 |
| `dotnet build-server shutdown` as the target user, refused during a build | `Actions/BuildServers/BuildServerShutdown.cs:26-166` (A3; trigger = alive ≥ `buildServers.idleHours`, `:79-85`) | S3 widens it |
| PSI memory/io/cpu read | `Collectors/MemoryCollector.cs:8` (`PressureSet`), `Collectors/Procfs/Pressure.cs:14` | S6 (only `memory.pressure` is judged today, `Thresholds/ThresholdRules.cs:182`) |
| swap verdict, `.wslconfig` reader and advice | `Thresholds/ThresholdRules.cs:99` (`memory.swap`), `Health/HealthParsers.cs:92-98`, `Collect/HealthReport.cs:36` (`WslConfigReport`) | S5 |
| the writable-or-user choice of a folder | `src_daemon/src/WslCare.Cli/Logging/WslCareLogging.cs:90` (state log dir if writable, else `$XDG_STATE_HOME/wsl-care/logs`, `Hosting/LinuxHostPaths.cs:64-65`) | S1's ledger place |
| a state file's read cap | `Config/ConfigKeys.Numbers.cs:440` (`records.maxStateFileBytes`, default 1 MiB) | S1 (no new cap key) |
| the root service's own niceness | `src_daemon/systemd/wsl-care.service:67-68` (`Nice=19`, `IOSchedulingClass=idle`; key `units.nice`) | S4 measures whether `nice` matters at all |
| the Windows probe (host RAM, system drive, `vmmemWSL` only) | `Collectors/WindowsProbe.cs:16-26` | S7 (E11) |

## 4. Decisions taken in this plan

1. **One epic, E14, eight stories; S1 first and alone in its pull request** (it fixes a measured defect, so `fix(daemon):`).
   S2–S8 each get their own branch and pull request, in the build order of § 8.
2. **Every behavioural number is a configuration key** (the owner's rule of 2026-10-05): each new number below names its
   key, range, default and trust; `ArchitectureTests.Numbers`, `ContractFilesTests` and `contracts/config-keys.json` hold it.
3. **Nothing is stopped by name.** Every action that ends a process does so by pid AND start ticks (and boot id where
   state crosses runs), never by image name, never the AI agent's own process, never root's processes.
4. **Measure before building on an assumption** (S2's "the agent restarts a killed server", S4's "nice does nothing in
   WSL", S3's "build servers outlive their sessions"): each such story opens with its measurement, recorded in `research/`,
   and the design below is conditional on it.
5. **A cross-repository defect is named, not fixed here.** `coai-mcp`'s start path (ConnectOtherAIs) and `creds-mcp.exe`'s
   orphans (CredsForDevs) are reported to their owners — the report itself is outward-facing and waits for the owner (Q7).

## 5. Stories

### S1 — the MCP metric measures what matters (fixes M1–M3)

**Defect.** `McpServerCollector.CpuAsync` (`McpServerCollector.cs:50-63`) reads each instance's `stat` twice across
`mcpServers.cpuWindowMilliseconds` (1 s by default). A server that burns in bursts with a period near a minute (M1; the
period is not measured exactly) reads 0 % in most calls, and the kind (`McpJudge.Kind`, `:107-112`) follows the reading:
busy-without-activity servers are reported `idle`.

**Design** (own plan review folded in — § 12).

1. **CPU over the interval since the previous sample.** Each sampling records, per listed instance, its identity (boot id,
   pid, start ticks) and its CPU ticks at an instant on BOTH clocks (`SampleTime`: wall and monotonic) in a small ledger,
   `mcp-cpu.json`. The next sampling finds that instance's newest recorded point whose MONOTONIC age is at least
   `mcpServers.cpuIntervalMinSeconds` and at most `mcpServers.cpuIntervalMaxMinutes`, and reports
   `CPU % = 100 × (Δticks ÷ ticks per second) ÷ (Δmonotonic in seconds)` — basis `interval`, with the interval on the
   wire. A point with Δticks < 0 or Δmonotonic ≤ 0 is not a baseline. The monotonic clock is the denominator because a wall
   clock that jumps after the host slept would distort the rate; whether the guest's monotonic clock stops while the
   Windows host sleeps is **not measured** (S8 records it) — if it does not, a reading across a sleep is diluted, never
   inflated.
2. **The interval is bounded so the kind stays true NOW.** `McpJudge.Kind` compares the CPU with a log write inside
   `mcpServers.activityWindowMinutes` (10). An average over hours would call a server that burned three hours ago and is
   quiet now `busyWithoutActivity` (and dilute a burn that began twenty minutes ago). So the maximum interval defaults to
   **20 minutes** — two activity windows — and a point older than that is not used: the window answers instead. The root
   timer (every 4 h) therefore measures over the window until S2 gives root a denser sampler (Q1b); the basis on the wire
   says which.
3. **The short window only as a fallback** for an instance with no usable point (a first sighting, a new boot, a reused
   pid, a point older than the maximum, a ledger that could not be read): today's two reads across the window, basis
   `window`. The window is waited only when at least one listed instance needs it — so a machine whose instances all have a
   baseline answers `status` **faster** than today (no wait at all).
4. **Two points per identity**, so concurrent callers do not starve each other. The update rule: a new identity stores its
   point; when the newest stored point is at least the minimum interval old, it becomes the older point and now becomes the
   newer one; otherwise both stay. A caller polling 1 s after another therefore still finds the older point ≥ the minimum
   old (two VS Code windows each polling `status` every 120 s, the default of
   [2026-10-04_extension_poll_churn.md](../research/2026-10-04_extension_poll_churn.md)); a single caller faster than the
   minimum measures over intervals between the minimum and about twice it. Points older than the maximum interval,
   identities not listed now, and a ledger of another boot are dropped. **Written only when it changed.**
5. **Where the ledger lives.** `CliHost.ForThisMachine` re-homes EVERY verb run as root to the target user
   (`CliHost.cs:122`, `TargetHome.Resolve`), so "the caller's own folder" is not root's when root runs `status` (review
   finding 1). Three places, one per caller:
   - **the root timer's `collect`** (state directory writable): `/var/lib/wsl-care/mcp-cpu.json`, written atomically and
     private (0600 — it names other accounts' pids), read back as root's state (`IFileSystem.ReadStateFile`);
   - **an unprivileged `status`** (the extension's poll): `$XDG_STATE_HOME/wsl-care/mcp-cpu.json` (default
     `~/.local/state/wsl-care/`, `LinuxHostPaths.UserStateDirectory` — the folder an unprivileged run's logs already go
     to), read with `ReadUserFile` (owner = this process's uid, reached from that folder through no link — review finding
     2: `ReadStateFile` trusts only root's files on a real machine, so it would silently find "no baseline" forever) and
     written atomically, private;
   - **`status` as root** reads root's ledger and writes NOTHING (status never writes the root state directory — §15b #3,
     §15j M3 of the parent plan — and never root-owned files into the target user's home); **an unprivileged `collect`**
     reads and writes no ledger (its read-only rule: *read-only: run as root to record*) and measures over the window.
   A missing, unreadable, foreign-owned, foreign-boot or malformed ledger is "no baseline" (the window fallback), never an
   error of the run; a write that fails is reported in the block (`cpuBaseline.recorded: false` + reason), never a failed
   `status`. **This makes an unprivileged `status` write one file it did not write before** — in its own state folder, only
   when an MCP instance runs and the ledger changed. Owner question Q9. The documents that say "status writes no state"
   (`Mcp/McpSample.cs:52`, the parent plan's E7.S2d Decided 8) are amended in the same change.
6. **"Busy without activity" follows the interval reading** — the kind rule itself is unchanged (`McpJudge.Kind`); its
   input is now the interval CPU over at most twenty minutes, so a server that burns a sixth of a core in bursts with no
   log write in the activity window is `busyWithoutActivity`, as measured.
7. **Read-only towards the servers, as before**: no signal, no file opened under the log root. The only write is the
   ledger above.

**Keys (new).**

| Key | Range | Default | Trust | Why this default |
|---|---|---|---|---|
| `mcpServers.cpuIntervalMinSeconds` | 10–600 | 120 | display | two periods of a burst near a minute (M1, not measured exactly); the extension's default poll is 120 s |
| `mcpServers.cpuIntervalMaxMinutes` | 10–1440 | 20 | display | two activity windows (`mcpServers.activityWindowMinutes`, 10): a longer average stops describing now (design 2) |

The ranges cannot contradict each other (the largest minimum, 600 s, is the smallest maximum), so no coupled rule is
needed. Both are display keys while they steer only a metric; S2, which acts on the interval as evidence, re-classifies
them (review finding 10). `mcpServers.cpuWindowMilliseconds` stays (the fallback's window). No cap key is added: the read
cap is `records.maxStateFileBytes`, the entries are bounded by `mcpServers.maxInstances` × 2 points.

**Wire (additive; goldens regenerated).** Per instance: `cpuBasis` (`interval` \| `window` \| `none`) and `cpuIntervalSeconds`
(a figure). The block: `cpuBaseline` (`file`, `recorded`, `reason`). The text line says how many instances were measured
over an interval. Capabilities unchanged (`status.mcpServers` already advertises the block).

**Files.** `Mcp/McpCpuLedger.cs` (new: the file records, read, next, baseline, write, the place), `Mcp/McpServerCollector.cs`
(the CPU path), `Mcp/McpSample.cs` (basis + interval on an instance, the baseline note on the sample, the "no state"
sentence), `Status/McpServersReport.cs`, `Cli/Commands/StatusCommand.cs:27` and `Collect/CollectRun.cs:380` (pass the
place), `Collectors/Procfs/SampleTime.cs` + the boot id (moved from `AgentCpuHistory.cs:27-31`, `:130-131`),
`Hosting/LinuxHostPaths.cs` (`UserStateDirectory`, the parent of `UserLogDirectory`), `Config/ConfigKeys.Numbers.cs`,
`Config/default.json`, `contracts/config-keys.json`, goldens, the read-site table (`ArchitectureTests.ReadSites.cs`).

**RED first (the measured shape).** `A_server_that_bursts_every_minute_is_measured_busy_over_the_interval_not_idle_in_a_quiet_second`:
a fixture whose `stat` ticks grow only during a 10 s burst every 60 s (100 % of a core in the burst = 16.7 % average); a
first sample at t0, a second at t0 + 121 s whose own 1 s window would fall in a quiet second. Today: 0 %, `idle`.
Expected: 16.7 % over 120 s, `busyWithoutActivity` (its log last written 17 min ago). Also:
`A_first_sighting_falls_back_to_the_window_and_says_so`, `Two_callers_a_second_apart_both_measure_over_the_interval`,
`A_baseline_of_another_boot_a_reused_pid_or_older_than_the_maximum_is_not_used`,
`A_four_hour_average_does_not_make_a_now_quiet_server_busy_without_activity` (review finding 3),
`No_wait_when_every_instance_has_a_baseline`, `An_unreadable_or_malformed_ledger_is_no_baseline_never_a_failed_status`,
`A_wall_clock_jump_does_not_change_the_rate`, `An_unchanged_ledger_is_not_rewritten`; at the CLI, over a sandbox WITH a
`coai-mcp` instance (review finding 4: the existing "status writes nothing" test has none, so it cannot see a ledger):
`An_unprivileged_status_keeps_its_ledger_in_its_own_state_folder_and_never_in_the_state_directory`,
`A_root_status_writes_no_ledger_anywhere`, `An_unprivileged_collect_writes_no_ledger`.

**Acceptance.** The RED test seen red with today's 0 % and `idle`, then green; each guard broken and seen red (the
interval path removed, the two-point rule replaced by "always replace", the maximum interval, the boot check, the identity
check, the place rule); Windows suites full and CI green (Linux legs); docs per § 9.

### S2 — an MCP watchdog action, A19 (answers the parent plan's Q-M1)

**Problem.** S1 shows a leaking or wedged server; nothing ends it. On 2026-10-07 eight servers held 6.9 cores (L7).

**Measure first (recorded in `research/` before the design is final):** what Claude Code does when one of its stdio MCP
servers is ended — does it restart it on the next tool call, mark it failed until `/mcp` reconnect, or end the session's
tools? (Q-M1 of the parent plan said "restarts on its next tool call" without a measurement.) If it does not restart,
"restart" here means *end* and the action must say so in its preview.

**Design (conditional on the measurement).** A19 = SIGTERM, then SIGKILL after `processes.termGraceSeconds`, through
`SuspectSignals.EndAllAsync` (pid AND start ticks, pidfd) — of instances of a WATCHED catalogue server (never the agent
process itself, never root's, never a process that is not the snapshot's instance) that are:
`busyWithoutActivity` over an `interval` basis for at least `mcpWatchdog.busyMinutes` (the S1 ledger gains a per-identity
`busyWithoutActivitySince`, kept across samples), **or** `orphaned` (owner gone, parent plan Decided 1) and idle for at least
`mcpWatchdog.orphanIdleMinutes`. A button AND automatic — **owner decision 2026-10-08 (Q1, the parent plan's Q-M1): the automatic switch is a setting,
default ON; "idle" is idle for more than a setting's minutes, default 60.** A watchdog on the 4-hour timer is too slow to
matter, so a second, short timer (`wsl-care-watch.timer`, `mcpWatchdog.periodMinutes`, default 5) samples and runs only
A19, under the daemon's dry-run rules (the first-week dry run and `dryRun`), with an action record per stop.

**Two things the design must settle before its code round (own plan review, findings 5, 6, 10, 11):**

- **`EndAllAsync` as it is would end nothing here.** Its recheck refuses every target whose CPU ticks moved since the
  preview (`SuspectSignals.cs:71`, "used CPU … since the preview: kept") — the right guard for A11 and A18's idle
  processes, and exactly the property a busy server has. A19 needs a recheck mode that keeps the identity, uid and terminal
  checks and drops the CPU one, used by A19 only; RED tests prove A11 and A18 still refuse a moved CPU.
- **Which evidence root acts on.** A19 runs as root (`act`). It reads ONLY root's ledger (`ReadStateFile`), never a user's
  file; and root's ledger holds interval readings only when root samples more often than `mcpServers.cpuIntervalMaxMinutes`
  (the 4-hour timer never does — coai plan round 2026-10-08, finding 0). So a root sampler shorter than that maximum (the
  watch timer of Q1b, sampling only, whether or not A19 may act on its own) is a prerequisite of A19 AT ALL, button
  included — without it A19 would find no interval-based target. Its scope (the target user only, as A18, or every non-root account) is stated in the preview. The S1
  keys that bound the interval become evidence for an action here, so they are re-classified (machine-only or a safe
  direction), and every new key of this story (`mcpWatchdog.*`) gets its range, default and trust in this plan before the
  code round.

**RED:** `A_busy_without_activity_server_for_longer_than_the_key_is_selected_by_pid_and_start`, `A_reused_pid_is_never_signalled`,
`The_agent_process_and_unwatched_servers_are_never_targets`, `A_window_basis_reading_never_selects` (only an interval reading
is evidence of a sustained burn).

#### S2a — what is built first: the IDLE watchdog (2026-10-08, after the owner's decisions)

The owner asked for "a STOP button for idle MCP servers AND a setting to do it automatically" (idle > 60 min, the automatic
switch default ON). **Idle needs no interval and no new sampler**: a server whose CPU ticks did not move between two sightings
of the same identity used no CPU in between, however far apart they are — the evidence A18 already relies on. So the idle
half ships first, on the existing 4-hour timer and as a button; the BUSY half (busy without activity, which needs interval
evidence and therefore the watch timer above) is **S2b**, the next slice, with the two design points above.

1. **A19 `McpServerStop`** (`Actions/Suspects/McpServerStop.cs`, beside A18): SIGTERM, then SIGKILL after
   `processes.termGraceSeconds`, through `SuspectSignals.EndAllAsync` — by pid AND start ticks (pidfd), each target re-read just
   before; its CPU re-check is exactly right for an IDLE target (a server that used CPU since the preview is kept).
2. **Who is a target** — ALL of: an instance of a watched MCP server (`McpInstances.Find` over the one process snapshot,
   `mcpServers.watched`: the PROGRAM is the server, so an agent process is never one); the TARGET user's (A18's rule), never
   root's, never this process, not a zombie, no terminal; and **no CPU for at least `mcpWatchdog.idleMinutes`** (default **60**,
   the owner's), measured by identity over a dense chain of sightings on both clocks (`AgentCpuHistory.IdleFor` — missing
   history is "not idle", so the first runs end nothing). An instance whose agent died (`orphaned`, the owner's Q-M3: a stop
   candidate) needs only **`mcpWatchdog.orphanIdleMinutes`** (default **10**): nobody can talk to it any more.
3. **The evidence:** every timer run already records the AI-agent processes' CPU ticks (`ActionEngine.RecordAgentCpu` →
   `AgentCpuHistory.Record`, `agent-cpu.json`, root's, private, capped by `agentCpu.maxEntries`). It is WIDENED to record the
   instances of every catalogued MCP server as well (one history, one sampler — reuse, not a second file). A preview merges
   "now" in memory and writes nothing, as A18's does.
4. **Button and timer.** `auto.A19` is a new switch, **default ON** (the owner's decision — the first auto switch that starts
   on) — and the daemon's dry-run rules govern it unchanged: while `dryRun` is on or the first-week window runs, the timer
   records what it WOULD stop and stops nothing. Trigger: any target. A button run is bound to what its modal showed, as A18's
   (`IBoundToShownList`, `--process <pid:start>`): the parser's "`--process` needs A18" becomes "needs an action bound to shown
   processes" (A18 or A19), decided in ONE place.
5. **An action record per stop:** the engine's run record — each stopped server an item (pid, server, its owner agent or
   "orphaned", idle time, memory held), each kept one in `notRemoved` with why.
6. **Keys:** `mcpWatchdog.idleMinutes` 10–10080, default 60; `mcpWatchdog.orphanIdleMinutes` 1–10080, default 10 — both decide
   what ends, so a user layer may only LENGTHEN them (`SafeDirection.Higher`), as `processes.aiAgentsIdleHours`.
7. **The extension** knows the id (`rootIds.ts`, held equal to `contracts/actions.json`) and leaves it out of the E6 cleanup
   ops, as A18 (`BUTTON_ONLY_IDS`: its confirm takes `--process`); the panel's button is an extension story.
8. **What it does not settle (said in the preview):** what Claude Code does with an ended stdio server (the measurement of
   S2 above is owed: it needs a real session and was off-limits on the overloaded machine) — the preview says "the session
   may need `/mcp` to reconnect it". The 60 minutes are a FLOOR: on the 4-hour timer a server is stopped at the first run
   that sees it unchanged since the previous one, so up to ~4 h idle in practice until S2b's watch timer.

**RED (S2a):** `An_mcp_server_idle_for_longer_than_the_key_is_a_target_by_pid_and_start`,
`An_mcp_server_that_used_cpu_within_the_window_or_has_no_history_is_kept`,
`An_orphaned_mcp_server_needs_only_the_orphan_idle_minutes`, `Another_users_or_roots_server_and_agent_processes_are_never_targets`,
`A_button_run_ends_only_what_its_modal_showed`, `The_timer_records_mcp_servers_in_the_cpu_history`,
`Auto_A19_is_on_by_default_and_the_dry_run_rules_stop_nothing`, `The_process_flag_is_accepted_for_A19_and_refused_without_A18_or_A19`.

#### S2a as built (2026-10-08, branch `feat/wc-mcp-watchdog`)

**coai plan round (session `f60fbbf6`):** verdict **proceed**, 2 of 2 reviewers (codex, gemini), 6 findings: 1 accepted, 5
rejected with reasons. Accepted: a server with a controlling terminal is never stopped (the shared signal path keeps one —
said, not widened). Rejected: two asking for `auto.A19` OFF until the reconnect is measured (the owner decided ON; the
dry-run default still stops nothing until the owner switches `dryRun` off), interpreted servers by argv[0] (the watched list
is closed over the catalogue, whose one server is native; an agent cannot match), lifetime CPU for the button (every server
spends CPU starting), and "no orphan mechanism" (the E7.S2d owner walk exists and is tested).
**Risk consultation (codex, story 14.2):** verified and taken — a child process keeps a server (it may be waiting on work it
started: `coai-mcp`'s reviewers are child CLIs); the orphan window only for a server re-parented to INIT (a `systemd --user`
child may have a live client); the snapshot's start ticks and the account re-checked against `/proc` before a server is a
target; the history's sample de-duplicated by pid. Its warning stands as a residual: CPU silence does not prove no request is
in flight (a server waiting on a REMOTE call spends nothing either) — the owner's accepted cost, said in every item.
**Deviations from S2a's text:** the selection gained the child, start-ticks and account checks above; the owner's Q-M2 (users
may add their own programs to the watched list) is NOT in this slice — it is a configuration-contract change of its own, next.
Nothing else differs, except item 5's wording: a server the JUDGEMENT keeps (terminal, child, snapshot, CPU) is counted with
its reason in the preview's basis, not listed as an item; `notRemoved` holds the targets the signal-time re-checks refused.
**coai code round (session `f60fbbf6`):** verdict **proceed**, **8 of 8** reviewers, 15 findings: 7 accepted, 8 rejected with
reasons. Accepted and built RED-first with teeth: the child guard repeated on a fresh process table just before the signal
(RED: *signals.Asked … found at least one item*), the held-memory fact of a narrowed button preview (RED: *7000000 … found
12000000*), the CPU history sampling the WATCHED list rather than the whole catalogue (so Q-M2's user programs are recorded),
the judgement as a switch expression with a pid → sample map, the cheap filters first, and a scenario flow over the built CLI
(`McpServerStopFlows`, Linux). Rejected: the grace "hard-coded" (it is `processes.termGraceSeconds` through `Tuning`), the
contract order (generated; the extension compares sorted), the CLI message (`ActionId.ToString()` is its text), a rename of
`BUTTON_ONLY_IDS` (the extension story's), a collection-expression nit.
**Own code review (Opus):** no wrong-process path found. Taken: an orphan window never longer than the idle one (RED: *found
0*). Recorded, not built here: **an existing install whose `dryRun` is already off and whose first-week window has passed gets
NO dry period for A19** — the window is global, stamped at the first timer run ever — so the owner switching to a release with
A19 starts stopping idle servers at the second timer run after the upgrade (owner question below, Q11); `mcpServers.watched`
still a display key although it now steers A19 (the Q-M2 story re-classifies it with the open list); a server answering only
short requests may spend under one 10 ms tick in an hour (a residual beside "a remote call in flight"); an engine-level A19
dry-run test (the dry-run gate is the engine's, held for every auto action by `TimerPassTests` and the A1/A2/A10 tests).

#### S2b — as to be built (2026-10-09, branch `feat/wc-mcp-busy-timer`): the watch timer and A19's BUSY half

**The gap.** S2a stops IDLE servers on the 4-hour timer, so "idle > 60 min" means up to ~4 h in practice. The evening of
2026-10-07 (L7) also had the other kind: eight servers burning 6.9 cores with no log line for 10+ minutes. Nothing stops
those, because the evidence is missing. Root's ledger (`McpCpuLedger`, S1) holds an interval reading only when root samples
more often than `mcpServers.cpuIntervalMaxMinutes` (20). The 4-hour timer never does (S2's design, finding 0 of the
2026-10-08 plan round). S2's design asks for both: a short root timer, and A19's busy selection on top of it.

**Design.**

1. **A watch verb and timer** (`wsl-care watch [--timer] [--json]`, as root, the distro's binary only).
   - `wsl-care-watch.timer` fires every `mcpWatchdog.periodMinutes` (new, default **5**, range 2–15, machine-only,
     `SafeDirection.Lower`). It is a monotonic timer: `OnBootSec` and `OnUnitActiveSec` equal the period. That is not a
     calendar, because the watch has no catch-up to do after the VM was off.
   - It starts `wsl-care-watch.service`, a oneshot running `watch --timer`. Its hardening and limits equal `wsl-care.service`'s,
     except `TimeoutStartSec`, which is `mcpWatchdog.runLimitMinutes` (new, default 10, range 2–60, machine-only).
   - Both units get drop-ins (`UnitDropIns`, `wsl-care units dropin`) and `doctor` checks.
   - A coupled rule (`NumberRules`): `mcpWatchdog.periodMinutes` < `mcpServers.cpuIntervalMaxMinutes`. Otherwise no watch
     sample would ever find a baseline, and A19 could never see a busy server.
2. **What one watch run does** (`Core/Watch/WatchRun.cs`):
   - **(a)** Take THE run lock without waiting. If it is busy, exit 75: nothing is done, and the full run or `act` holding the
     lock samples or acts itself.
   - **(b)** Under the lock, one probe sample. The MCP collector then runs with root's ledger, writes it
     (`McpCpuLedgerPlace.RootState(Writes: true)`, the place `collect` uses), and records the agents' CPU history
     (`AgentCpuHistory.Record`, A19's idle evidence, A18's and A3's too). Then the lock is released.
   - **(c)** Only with `--timer`, and only when every one of these holds:
     - not observe-only;
     - `auto.A19` is on;
     - the daemon's dry-run decision for the TIMER (`DryRunWindow.Decide`, the same two locks as the full run) says real.

     Then A19's live preview runs (`ActionEngine.PreviewAsync`). If it has targets this watch did not already try, they are
     run through `ActionEngine.ExecuteAsync` as an `act` with trigger `timer`, bound to exactly those targets (`--process`
     semantics, `ShownProcesses`). That is a normal recorded run: one history line and one detail, an item per stop.
   - **(d)** The identities tried go into `{state}/mcp-watch.json`: boot id plus `pid:start`, live ones only, capped at
     `mcpServers.maxInstances`. The watch never tries the same process twice; the 4-hour pass still may. So a target whose
     signal is refused every time costs one record, not 288 a day.
   - **What is recorded:** a watch run that stops nothing writes NO history line. Sampling every 5 minutes is not a run anyone
     needs in `logs` or `runs`; the run log keeps it.
   - **During a dry run the watch only samples.** The full run's pass keeps recording what A19 WOULD stop every 4 h. A dry
     watch would record the same "would stop" every 5 minutes.
3. **The busy evidence in the ledger** (`McpCpuEntry.BusySince`, a wall + monotonic instant, omitted when none).
   - At each sample that writes a ledger, an instance whose kind is `busyWithoutActivity` over an `interval` basis (S1's
     interval, already bounded to 120 s–20 min) keeps its entry's `BusySince` if that entry had one, or starts it now.
   - Any other kind, a `window` basis or an unmeasured CPU clears it.
   - So "busy without activity since T" is a chain of interval readings, each no more than `cpuIntervalMaxMinutes` apart. One
     gap clears it.
   - The collector judges the kinds first and records the ledger after (`McpServerCollector.cs:35-38` reorders; `RecordAll`
     moves out of `CpuAsync`, `:69`, `:95`).
   - `BytesPerEntry` (`McpCpuLedger.cs:132`) is raised to fit the new member under the same read cap.
4. **A19's busy half** (`McpServerStop`, `:143` `JudgeOne`).
   - A target is also an instance with ALL of S2a's guards:
     - the target user's, not root's;
     - no terminal, no child;
     - the snapshot's identity re-read;
     - not an orphan of a user-added program.
   - PLUS: busy without a log write for at least `mcpWatchdog.busyMinutes` (new, default **30**, range 10–10080,
     `SafeDirection.Higher` like `idleMinutes`). That is read from ROOT's ledger only (`ReadStateFile`, never a user's file):
     - an entry of this boot and this identity, with `BusySince`;
     - whose newest point is no older than `mcpServers.cpuIntervalMaxMinutes`, so the evidence is current;
     - measured as the shorter of the two clocks.
   - Its item says "busy without a log write for N min" and is marked busy (`ActionItem.Kind` `busy process`).
   - An idle target is unchanged.
5. **The signal path for a busy target** (`SuspectSignals.EndAllAsync`, `:41`; `Changed`, `:71`).
   - `EndAllAsync` gains a per-item "the CPU may have moved" predicate. A busy item keeps the identity, account, terminal and
     not-root re-checks and drops ONLY the CPU one; a busy server moves its CPU by definition.
   - A11, A18 and A19's idle items pass none, so they still refuse a moved CPU (RED tests prove it).
   - One call, so one shared grace for both kinds.
6. **Keys that became evidence** are re-classified (S2's design): `mcpServers.cpuIntervalMinSeconds` and
   `mcpServers.idleCpuPercent` → `SafeDirection.Higher`, `mcpServers.cpuIntervalMaxMinutes` → `Lower`,
   `mcpServers.activityWindowMinutes` → `Higher`. Each now decides what A19 may stop; a user layer may only make A19
   stricter when root reads it. `contracts/config-keys.json` is regenerated.
7. **Install and package.**
   - `install.sh`:
     - units list (`:79`);
     - stop before an upgrade or uninstall (`:476`–`:489`);
     - the release check (`:760`);
     - `enable --now` (`:991`);
     - verify active (`:1024`);
     - drop-ins.
   - `.github/scripts/package-daemon.sh` ships both files.
   - `DoctorRun.Units` (`:60`) adds `wsl-care-watch.timer`.
   - `ShippedFilesTests` hold the new service's hardening equal to `wsl-care.service`'s.
   - `UnitSuccessExitTests` derive its `SuccessExitStatus`.
8. **Measurement still owed.** What Claude Code does with an ended stdio server (S2's "measure first") stays owed. The
   machine is off-limits for it, so every A19 item keeps saying the session may need `/mcp`.
9. **Defaults as assumptions** (the coordinator's, the owner may change): 5 min period, 30 min busy, 10 min run limit.

**Not in this story:** a Windows-side ledger and `collect`'s Windows MCP block (the next story); a stop on Windows (S7b,
the owner's).

**RED (S2b), written before the product code:**
- `A_busy_without_activity_streak_over_interval_readings_sets_busy_since_and_any_other_kind_clears_it`
- `A_gap_longer_than_the_interval_maximum_restarts_the_busy_streak`
- `A_server_busy_without_activity_for_longer_than_the_key_is_an_A19_target_by_pid_and_start`
- `Busy_evidence_older_than_the_interval_maximum_or_from_a_users_ledger_or_another_boot_selects_nothing`
- `A_busy_target_is_signalled_although_its_cpu_moved_and_A11_A18_and_idle_targets_still_refuse_a_moved_cpu`
- `The_watch_samples_into_roots_ledger_and_history_under_the_lock_and_records_no_run_when_nothing_is_stopped`
- `The_watch_acts_only_with_the_timer_flag_auto_on_and_no_dry_run_and_never_tries_one_process_twice`
- `The_watch_exits_75_when_the_lock_is_held_and_77_when_not_root`
- `The_watch_period_must_stay_under_the_interval_maximum`
- `The_watch_units_ship_with_the_full_runs_hardening_and_their_drop_ins_render_the_keys`
- Scenarios over the built CLI (Linux legs): `watch --timer` over the captured tree with a sandbox ledger whose `coai-mcp`
  has a 40-minute busy streak, under `dryRun: false` past the window → one recorded act run stopping it (signals refused in a
  sandbox, recorded as kept); with `dryRun` on → no history line, the ledger written.

#### S2c — users add their own MCP programs (the owner's Q-M2, 2026-10-08)

**The ask:** "users may add their own programs to the watched list (config list, validated)". Today `mcpServers.watched` is
a list CLOSED over the built-in catalogue (one server, `coai-mcp`): no other MCP server can be measured or stopped.

1. **A new key, `mcpServers.programs`** — additive, so `mcpServers.watched` keeps its meaning and its contract: a list of
   program FILE NAMES (what `/proc/<pid>/cmdline`'s argv[0] ends in), each one an MCP server with no log layout (its starts
   counted as the `liveYounger` lower bound, its activity "not derivable"). Default empty. `McpSettings.Watched` = the
   watched catalogue entries + these, so the metric, the CPU history and A19 see them through the ONE list they already use.
2. **Validated, never free text** (plan §15q R1.3): `TextListKey` gains an optional member RULE (`TextRule.McpProgramName`)
   for an open list — widened, not a new shape — and a cap of 32 members. A member is a file name
   `^[A-Za-z0-9][A-Za-z0-9._+-]{0,63}$` and is REFUSED when it names an AI agent's binary (the agent catalogue's), an
   interpreter, shell or launcher (`node`, `python3`, `bash`, `npx`, `uvx`, `dotnet`, `java`, `env`, `sudo`, …: argv[0] of
   an interpreted server is the interpreter, and matching it would make EVERY such process a server), this product
   (`wsl-care`), or a catalogue server (it is already a `watched` choice). An interpreter-run server therefore cannot be added
   this way — said in the refusal; it needs a catalogue entry naming its script.
3. **Trust — a decision, a risk item:** the key is read by root (the timer's A19 and history) and it WIDENS what A19 may stop.
   What it can widen to is bounded: A19 only ever stops the TARGET user's own processes, uid re-read from `/proc`, with every
   guard of S2a (terminal, child, identity, idle by measurement) — so a user adding a name can make root stop only processes
   that same user could stop. The key is therefore an ordinary user key (`KeyTrust.Display` semantics: not machine-only), and
   the machine layer can still set it; the trust rationale is written on the key. A user who does not want a program stopped
   switches `auto.A19` off or removes the name.
4. **Wire:** additive — the `mcpServers` block already lists servers by name; a user program appears as a server with basis
   `liveYounger`. Contracts: `config-keys.json` gains the key with its member rule (`memberPattern`, `maxMembers`, the refused
   names).
5. **An orphan of a USER program is never a stop target** (coai plan round, session `563a1e95`, accepted): a catalogue
   server's name is ours and specific; a user-chosen file name is not, so an init-parented process of that name may be an
   unrelated program that is not an MCP server at all. A19 stops a user program's instance only while its parent is NOT
   init — an agent (or what the agent spawned) still holds it — and with the ordinary idle window, never the orphan window.
6. **A real timer-path flow** (same round, accepted): a scenario over the captured process tree (which holds `creds-mcp`
   under `claude`) with `mcpServers.programs: ["creds-mcp"]`, through the real binary, not only unit tests.

**RED (S2c):** `A_user_program_is_watched_measured_and_stopped_when_idle_like_a_catalogue_server`,
`An_agent_binary_an_interpreter_or_this_product_is_refused_as_a_program_naming_why`,
`A_program_list_past_its_cap_or_with_a_path_is_refused`, `Another_users_process_of_a_user_program_is_never_stopped`,
`An_orphaned_instance_of_a_user_program_is_never_stopped`, `The_config_keys_contract_describes_the_open_list`, and the
scenario flow `A_user_program_from_the_config_is_reported_and_recorded_by_the_timer`.

#### S2c as built (2026-10-08, branch `feat/wc-mcp-user-programs`)

- **The key:** `ConfigKeys.McpServers.Programs` (`mcpServers.programs`, default `[]`, `KeyTrust.Display`, the trust rationale
  on the key). `ConfigKey.TextListKey` is WIDENED, not a new shape: it now carries a member `TextRule` and a cap; a closed
  list keeps its old constructor (member rule = `TextRule.OneOf(Allowed)`, no cap) and its refusal text word for word. The
  open list's refusal names the member and why (`"node" is refused: it is an interpreter, shell or launcher …`), and a list
  past the cap says how many members it got.
- **The rule:** `Mcp/McpUserPrograms` holds it once — the pattern (the WHOLE value must match: .NET's `$` also matches before
  a final newline), then the refusals in order: `.exe` (the daemon strips it before it compares, so such a member could
  never match), an agent catalogue binary, a launcher (also with a version suffix: `python3.12`, `node20`), `wsl-care`, a
  catalogue server's name or program. The launcher list is the plan's plus `tsx`, `ts-node`, `pypy`, `pip`, `pipx`, `poetry`,
  `conda`, `ksh`, `busybox`, `nohup`, `setsid`, `nice`, `ionice`, `timeout`, `stdbuf`, `xargs`, `tmux`, `screen`, `go`,
  `cargo`, `docker`, `podman`, `ssh`, `login`, `cron` (each is the argv[0] of programs it starts). `TextRule.McpProgramName`
  delegates to it, so the reader and the validation cannot disagree; `McpUserPrograms.Entries` re-checks every name and
  de-duplicates (the reader's own guard).
- **The wire:** `McpServerEntry` gained `Origin` (`Catalogue` \| `UserProgram`, default `Catalogue`); `McpSettings.Watched` =
  watched catalogue servers + user programs. The status wire is unchanged — a user program is one more server, basis
  `liveYounger`. `contracts/config-keys.json` gained the key: `memberPattern`, `maxMembers`, `refused`, and no `allowed`.
- **A19:** `OrphanOfUserProgram` is the first guard of the judgement: a user program's instance whose owner is `Orphaned` —
  re-parented to init OR a `systemd --user` child — is kept with `UserProgramOrphan`. Item 5 read "parent not init"; the
  build is stricter (any orphan), because the `systemd --user` child has no agent above it either.
- **The flow:** `McpServerStopFlows.A_user_program_from_the_config_is_reported_and_recorded_by_the_timer`: the timer's full run over the captured tree with `["creds-mcp"]` reports `creds-mcp` (2
  instances, `liveYounger`), records pids 7327 and 8378 in the CPU history and stops nothing on a first sighting. A STOP
  through the built binary is not exercised: it would need a seeded history whose idle span reaches the window, and the
  signal path of the built binary has no fake — the stop itself is held by the unit tests with a recording signal sender.
  Linux legs only (CI); not run locally (the machine-load rule of 2026-10-07: no WSL builds or tests).
- **Deviations / residuals:** `McpUserPrograms.MaxMembers` (32) is a schema limit listed in the numbers scan's `Formats`
  (as `ExtraAgent.MaxEntries`), not a key. An interpreter-run server (`playwright-mcp` = `node …/playwright-mcp`) and a
  Windows server through interop (`/init …/creds-mcp.exe`, argv[0] `init`) cannot be added this way. A name a user adds
  may match an unrelated program of the same name that runs UNDER an agent (an agent's own helper with that name): the
  idle, child, terminal and identity guards still apply, and the user chose the name.
- **Gates:** plan round (coai session `563a1e95`): `proceed`, 1 of 2 providers answered, both findings accepted (items 5 and
  6). Code round (same session, 4 of 8 reviewers — gemini rate-limited): `proceed`, 4 findings — ACCEPTED: (1) a closed
  list given a member rule must still hold every member to its allowed set (`ConfigValidation.MemberProblem`), (2)
  `python3.13t` / `python3.12d` / `node-22` passed the launcher refusal (a version-suffix pattern now allows a separator and
  a build tag of up to two letters; `go2mcp`, `nodemcp` stay accepted); each RED first, green after, red again with the fix
  broken. REJECTED: a typed program-name value (the whole MCP and agent model compares file-name strings; re-typing it is
  its own refactor — proposed to the owner), and "the published pattern accepts a final newline in JavaScript" (it does
  not: JavaScript's `$` without the `m` flag matches only at the end of the input).
- **Own code review** (one reviewer, `feature-dev:code-reviewer` on Opus, read-only, run beside the coai round): it confirmed
  the trust reasoning (root reads only the target user's layer, owner-checked; A19 filters on the same user and re-reads
  the uid) and found, all accepted and each RED first, green after, red again with the fix broken: (2) `bunx`, `pnpx`,
  `pwsh`, `tcsh`, `csh`, `ash`, `mksh`, `lua`, `luajit`, `Rscript`, `julia`, `erl`, `elixir`, `mise`, `asdf`, `corepack`,
  `tini`, `dumb-init` are launchers too; (3) a name a LATER release refuses (a new agent, launcher or catalogue server) made a
  layer written for an older build a configuration error, so every run went observe-only — the loader now leaves such a
  member out with a notice (`TextRule.Outdated`, `ConfigValidation.CheckLayer`); a malformed member is still an error and
  `config set` still refuses the name; (4) the contract could not express two rules — the `.exe` refusal is now in
  `memberPattern` (a JavaScript-compatible negative lookahead) and the launchers are published with
  `launcherVersionSuffix`; its finding 1 was the coai round's two, already fixed in the working tree it read. Its finding 5
  — the contract says `rootEffective: false` for a key root's A19 reads — is kept as decided (item 3): `RootEffective` keys
  the extension's loosening modal on a SAFE DIRECTION, and this key has none that would still let a user add a program
  (`Subset` would let a user layer only narrow the machine layer's list, which defeats Q-M2); the key's register entry and
  this plan say plainly that root reads it. Below its threshold: user-program processes of every non-root account enter the
  CPU history and can push older agent entries past `agentCpu.maxEntries` (fails safe: missing history keeps a process).

#### S2d — interpreter-run MCP servers in the catalogue (Q13, coordinator default 2026-10-08)

**The gap.** `mcpServers.programs` refuses an interpreter as a program (S2c), because argv[0] of every script it runs is the
interpreter. An MCP server started through `npx` runs that way. The captured 2026-10-02 tree shows it twice:
`npm exec @playwright/mcp@latest` (pids 7377, 8415, children of the two `claude` sessions) and, below it,
`node /home/user/.npm/_npx/<hash>/node_modules/.bin/playwright-mcp` (pids 7472, 8477). The tree shows no other
interpreter-run MCP server. The shell between `npm exec` and `node` (ppids 7471, 8476) is not in the capture.

**Design** (as first written; items 1 and 2 were superseded by the plan round — see *S2d as built* below).
1. `McpServerEntry` gains `Interpreters` (default empty). A process is the server when its PROGRAM is one of `Programs`
   (as today) OR its program is one of `Interpreters` AND its SCRIPT — the second word of `ProcessEntry.Programs`, a file name,
   `.exe` stripped — is one of `Programs`. It is never a match on an argument further on (consultation C-2 still holds). The
   specific script name is what makes an interpreter safe to name here. A user program cannot do this, because it names no
   script.
2. Catalogue entry `playwright-mcp`: `Programs ["playwright-mcp"]` (a direct start through its shebang has argv[0]
   `playwright-mcp`; `npx` gives `node …/.bin/playwright-mcp`), `Interpreters ["node", "nodejs"]`, no log layout (starts =
   `liveYounger`). `npm exec` (the wrapper) is never the server; it is one of its ancestors, and the owner walk crosses it to
   the agent.
3. It is watched by default, like every catalogued server (`default.json` `mcpServers.watched`), so A19 sees it with every
   S2a guard. One guard matters here: Playwright keeps a browser CHILD while it works, and A19 keeps a server with a child.
4. It is NOT added: the Windows `creds-mcp.exe` reached through WSL interop (`/init …/creds-mcp.exe`, pids 5252, 7334, 7741,
   8389). `/init` is the interop relay, not an interpreter. Stopping the relay may leave the Windows process running (W9
   already shows 66 orphaned `creds-mcp.exe` on the Windows side), so the Windows side's E11 owns it.
5. Wire: additive — one more server in `mcpServers.servers`. In the captured tree the two `playwright-mcp` processes have
   their parent missing from the capture, so they count as `notUnderAgent` (2), not as instances. The status golden changes
   to match. `processes`/agents are unaffected. `mcpServers.watched`'s allowed list and default gain `playwright-mcp`.
   `mcpServers.programs` now refuses it (a catalogue server; a layer that listed it is left out with a notice, S2c's
   tolerance).

**RED (S2d):** `An_npx_started_playwright_mcp_is_an_instance_under_its_agent_through_npm_exec`,
`Node_running_another_script_is_not_playwright_mcp_and_a_mention_after_the_script_is_not_either`,
`Playwright_mcp_started_directly_by_its_shebang_is_the_server`, `The_wrapper_npm_exec_is_never_the_server`,
`Playwright_mcp_is_watched_by_default_and_refused_as_a_user_program`, and the golden update.

**S2d as built (2026-10-08, branch `feat/wc-mcp-interpreter-servers`).** Plan round (coai session `bf45d6ae`): `proceed`, 2 of
2 reviewers, 5 findings.

- **Accepted:**
  - (1) an option before the script: `ProcessEntry.Script` is the first word after the program that is no option.
  - (2) a shebang script runs as the interpreter, never as itself: the entry names NO program, only the interpreter-run form, and the test uses the real shebang shape.
  - (3) a script of the same name elsewhere: the form requires the script under `/node_modules/`.
  - (4) A19 is shown to target an idle one and keep one with a browser child.
- **Rejected:** (0) bridging a missing parent. On a live system a parent that exits re-parents its child at once. The capture omits processes (the interop relay's parent 559 is missing too), so the golden reports the capture as it is.
- **Built:** `McpServerEntry.RunAs` (`McpScriptMatch`: interpreters, scripts, folders) and `CommandLineText.ScriptOf` / `FileNameOf`. Tests that assumed one watched server now name `coai-mcp`.
- **Code round** (same session, 8 of 8 reviewers): `proceed`, 7 findings.
  - **Accepted:**
    - (0) the default-watched test derives from the catalogue.
    - (4) a scenario flow: the built CLI, with the capture's missing shells added to the sandbox tree, counts both `playwright-mcp` as instances of Claude Code.
    - (5) a same-named file in another package matched. The folders are now the bin link `/node_modules/.bin/` and the package `/node_modules/@playwright/mcp/`. RED first, green, then red again with the bare `/node_modules/`.
  - **Rejected, with reasons:**
    - (1) moving `FileNameOf` out of `Collectors.Procfs`: it is the one place the comparison lives, and it is compiled for every target.
    - (2) a Python `-m` form: no such server is measured; it would be a new case.
    - (3) a positional `RunAs`: it would have to be nullable.
    - (6) parsing `node` options with values: a documented safe miss.
- **Own review** (Opus, read-only): nothing serious. Its notes are recorded:
  - The leaked tree: agent gone, launchers re-parented, `node` alive. It counts as not under an agent and is never an orphan target. This is a residual in `module_mcp_servers.md`, measured in S8.
  - The other missed launch forms (a global install, a relative path, `cli.js`).
  - The stale "read-only metric" comment on `mcpServers.watched`, now corrected.

### S3 — the build-server reaper (widens A3)

**Problem.** L7: `dotnet` 4.9 and `VBCSCompiler` 4.9 cores; 51 `dotnet` processes for 10 sessions (L4). A3 today stops
the target user's build servers only when one is ALIVE for `buildServers.idleHours` (`BuildServerShutdown.cs:79-85`) —
age, not idleness — and never sees a language server whose VS Code window closed.

**Measure first:** over one working day, how many MSBuild node-reuse processes, `VBCSCompiler` and
`Microsoft.CodeAnalysis.LanguageServer` processes exist per open VS Code window and per closed one, and their CPU (the S1
ledger shape, keyed by identity). **Design:** A3's selection widens to (a) build servers whose CPU did not move for
`buildServers.idleMinutes` (measured over the ledger, not age), and (b) language servers of the `vscode-server` family
(`Collectors/ProcessFamilies.cs:69`) whose VS Code server parent is gone (orphaned) — those through `SuspectSignals`
(pid AND start), the build servers still through `dotnet build-server shutdown` as their user (the official command; the
refusal while a build runs stays — reused unchanged: `BuildServerShutdown.Builds` / `IsDotnetBuild`, `:143-151`, re-checked
from a fresh process table just before the command, `:126-137`, with its own tests). A3 keeps its `auto.A3` switch; the new rule (b) is a button until the owner says
otherwise (Q1 covers it).

#### S3 — revised before building (2026-10-08, branch `feat/wc-build-server-reaper`)

Reading the code first changed two things.

1. **`dotnet build-server shutdown` stops ALL of the user's build servers at once** — there is no per-server choice
   (`BuildServerShutdown.RunAsync`). So "the idle build servers" cannot be a selection; idleness can only decide WHEN A3's
   timer runs. **Rule (a), as built:** the timer's trigger fires only when (unchanged) a build server is alive for
   `buildServers.idleHours` (4) AND (new) EVERY build server of the target user used **no CPU for
   `buildServers.idleMinutes`** (new key, 10–10080, default 60, safe direction higher), measured by identity over the same
   CPU history A18 and A19 use (`AgentCpuHistory`, which now also records the `dotnet-build-servers` family). One server
   that worked within the window — or has no history yet — holds the trigger: a compile in flight (an IDE build that is no
   `dotnet build` process) is not cut. This only ever makes the timer fire LESS than today; a button run is unchanged (the
   build refusal is its guard, as before). The facts say how many servers are busy or unmeasured.
2. **Rule (b) already exists: A11.** A11 ends the target user's ORPHANED processes of the families in
   `processes.families` that are older than `processes.idleOlderThanHours` and used no CPU in a measured window, by pid AND
   start, never one with a terminal (`Actions/Suspects/SuspectTermination.cs`). `vscode-server` — which matches
   `Microsoft.CodeAnalysis.LanguageServer` (`Collectors/ProcessFamilies.cs:69`) — is a choosable family. A language server
   whose VS Code server went is re-parented, so it is an A11 suspect once the user lists its family (as built: its OWN family
   `language-servers`, never `vscode-server` — plan-round finding 5 below). Reuse first: no
   second signal path; S3 adds a test that A11 picks such a language server and leaves one whose server lives, and the
   default (A11 off, `vscode-server` not listed) stays the owner's choice (Q14).
3. **Measure first is owed, not done:** the machine-load rule of 2026-10-07 forbids WSL commands in this session, so the
   per-window counts move to S8's soak. The design does not depend on them: (a) only narrows the timer, (b) is opt-in.
4. **Not addressed by S3:** L7's BUSY servers (`VBCSCompiler` at 4.9 cores). Stopping a busy compiler mid-work is never
   safe; that is S4's (CPU fairness) ground.

**RED (S3):** `The_timer_does_not_shut_build_servers_down_while_one_used_cpu_within_the_idle_window`,
`The_timer_shuts_them_down_when_every_server_is_idle_for_the_window_and_one_is_old_enough`,
`A_server_with_no_history_holds_the_timer`, `A_button_run_is_not_held_by_idleness_only_by_a_build`,
`The_timer_records_build_servers_in_the_cpu_history`, `A11_ends_an_orphaned_idle_language_server_when_language_servers_is_listed_and_never_the_vscode_server`,
and the config/contract tests for `buildServers.idleMinutes`.

#### S3 as built (2026-10-08)

- **Plan round** (coai session `b0d57159`): `proceed`, 2 of 2 reviewers, 7 findings.
  - **Accepted:**
    - (1, 6) the growth budget is now stated (below).
    - (2) the conservative semantics on the 4-hour timer: a burst anywhere inside the interval restarts the idle clock at the sighting that sees it, so `idleMinutes` is a floor and the shutdown waits for a later pass. This is tested.
    - (5) listing `vscode-server` for A11 would make the VS Code server itself a suspect: the server is daemonised (parent 1, no terminal), so it is "orphaned" by A11's rule and idle at an idle desk. Rule (b) therefore uses a family of its OWN, `language-servers` (`Microsoft.CodeAnalysis.LanguageServer`, the program or its `.dll`), matched before `vscode-server`. The README and architecture now say not to list `vscode-server` for A11.
  - **Rejected, with reasons:**
    - (0) the window between the build re-check and the command is A3's existing behaviour. No lock in the SDK can make it atomic.
    - (3) "all idle never happens under twenty sessions": holding the whole-user command while any server works is the safe behaviour. Per-process reaping of idle ORPHANED build servers already exists in A11, whose default families include `dotnet-build-servers`. Whether A11 should be on by default is asked (Q15).
    - (4) "a 4-hour cadence never fires": two unchanged sightings 4 h apart give 4 h idle. The true part is finding 2's. A denser sampler is Q1b/S2b.
- **Growth budget** (findings 1 and 6): `AgentCpuHistory` holds LIVE identities only. An identity not seen at a run is dropped at that run's merge, so dead build servers retire at the next timer run. There is no stranded state to sweep: the file is rewritten whole, atomically, by root's timer. It is capped at `agentCpu.maxEntries` (512) and `agentCpu.maxBytes` (256 KiB). Past the cap the OLDEST identities drop, which makes them "no history", so they are kept, never stopped. At the E14 load (20 sessions: ≈ 20 agents + ≈ 40 MCP servers + ≈ 50 build servers alive at once) that is about 110 entries ≈ 34 KiB.
- **Code:**
  - `BuildServerShutdown` gained the `busyServers` fact: the servers not measured idle for `buildServers.idleMinutes` (new key, 10–10080, default 60, `KeyTrust.Higher`). The history is merged with now in memory and nothing is written.
  - The trigger is `old > 0 && busy == 0`.
  - `AgentCpuHistory.Sample` records the `dotnet-build-servers` family.
  - `ProcessFamilies` gained `language-servers`, which is choosable for A11 (`processes.families` closed list, contract regenerated). The captured tree's pid 6612 moves to it: status golden hand-edited (family count and held bytes, the Linux CI legs verify it).
  - A3's button run is unchanged.
- **Not done:** the day-long measurement (S8). L7's busy servers are S4's.
- **Code round** (same coai session, 8 of 8 reviewers): `proceed`, 10 findings.
  - **Accepted:**
    - (0) `module_daemon.md` describes A3's idle gate, with a diagram.
    - (1) the build-server family name lives in `ProcessFamilies.DotnetBuildServers`, so the history no longer depends on the A3 action. This also ends the namespace cycle of finding 4.
    - (3, 6, 7) the language server run as `dotnet exec [--option value]… …LanguageServer.dll` was missed, because only the first two words were matched. The rule now reads the whole argv, anchored at the program, so a process that only NAMES the server is still not one. RED first: *Expected … to be the same string, but they differ at index 0*. Green after; red again with the old head-only match.
    - (5) one query, `AgentCpuHistory.IdleNow`, replaces A3's own sampling and merging.
    - (8) no history read when no server runs. This is performance only, with nothing observable, so it has no test.
  - **Rejected, with reasons:**
    - (2) the cap at 10 times the stated load: it is a setting, and it fails safe (held).
    - (4) moving the history class out of `Actions.Suspects`: the cycle is gone with finding 1, and a move is its own refactor.
    - (9) renaming the `idleServers` fact: it is a wire name in every A3 run record since E3.S3.
- **Own review** (one reviewer on Opus, read-only): nothing serious. It confirmed:
  - the `Busy` logic, the trigger direction and the recording order (before the pass);
  - that A18 and A19 are unaffected;
  - the golden arithmetic (counts 51, held bytes 9 449 013 248);
  - that nothing depended on the language server being `vscode-server`.
  
  Its minor items are done: the RED list's test name, item 2's pointer to finding 5, the history descriptions widened (architecture's A18 diagram and text, `AgentCpuHistory`, the `agentCpu.*` keys), and two pinned cases: the `.dll` launch form, and an unreadable boot id holding the timer.

### S4 — CPU fairness that works inside WSL

**Problem.** Agents' builds and tests (L7) compete with the sessions they serve on equal terms. `nice` is believed to have
no effect in this WSL (no `cpu` controller delegated below `system.slice`) — **not yet measured**.

**Measure first (recorded):** `/sys/fs/cgroup/cgroup.controllers`, the `subtree_control` of `/`, `system.slice`,
`user.slice` and `user@<uid>.service`; then two busy loops pinned to one CPU, `nice 0` vs `nice 19`, in the same cgroup and
in sibling cgroups — the share each gets. **Design (if the measurement says the controller is missing below the user
manager):** a systemd drop-in `user@.service.d/50-wsl-care-delegate.conf` (`Delegate=cpu io memory pids`) written by
`install.sh` only on the owner's yes (Q3), and `wsl-care low -- <cmd…>` — `systemd-run --user --scope -p
CPUWeight=<lowCpu.cpuWeight> -p IOWeight=<lowCpu.ioWeight> -- <cmd…>` through the command policy as a declared template
(the command's own argv passed as data, never a shell string) — or documentation only (Q4). Agent prompts (the family's
rules) then say: heavy builds and test runs go through it.

### S5 — memory and swap before the evening

**Problem.** L3: 2.4 GB of 12 GB swap left, `Committed_AS` above `MemTotal`, at 21:30. `memory.swap` warns on swap USED
(`ThresholdRules.cs:99`, `thresholds.swapWarnGb`); nothing judges swap LEFT or commit.

**Design.** New verdicts `memory.swapFree` (warn below `thresholds.swapFreeWarnGb`), `memory.committed` (warn above
`thresholds.committedWarnPercent` of `MemTotal`), from `/proc/meminfo` fields the collector already reads generically;
A1/A2's triggers may also take memory PSI (`thresholds.memoryPressureWarn` exists) — measured against the 2026-10-02
baseline first. `.wslconfig` advice (memory cap, swap size, `autoMemoryReclaim`) extends the existing `WslConfigReport`
(`HealthReport.cs:36`): **shown, never written** by the product (Q5).

#### S5 — as to be built (2026-10-09, branch `feat/wc-memory-before-evening`): a REPORT, nothing acts differently

1. **`memory.swapFree`** (a fast-sample verdict, after `memory.swap`):
   - warn when swap is configured and `SwapFree` < `thresholds.swapFreeWarnGb` (new key, 0–GbCeiling, default 4);
   - ok with "no swap configured" when `SwapTotal` is 0;
   - the value is "X GiB free of Y GiB".
   
   The evening (L3) had 2.4 GB left, so it warns; the captured calm tree has 12 of 12 free.
2. **`memory.committed`** (after it): warn when `Committed_AS` > `thresholds.committedWarnPercent` (new key, 1–1000, default 80)
   of `MemTotal`. The value is "P % of MemTotal (C of T GiB committed)".
   - Linux over-commits, so this is a warning of what is PROMISED, never of what is used.
   - The evening read 104 %; the calm tree reads 46 %.
   - `MemorySnapshot` gains `Committed` as an init property (default "not read"), so the many positional constructions in
     tests stay valid.
   - The status wire's memory block does not change: the figures are in the verdicts.
3. **A1/A2 and memory pressure, SHADOW only.** The plan says to measure against the baseline first, and this session cannot
   measure (no WSL commands). So A1's and A2's previews gain the fact `memoryPressureAvg60Hundredths`, and their trigger REASON says whether the pressure rule
   (memory PSI some avg60 above `thresholds.memoryPressureWarn`, the S6 rule) WOULD have fired. Whether they fire does not
   change. Every timer run then records the evidence in its run record, so the S8 soak can decide with numbers whether to
   make it a real trigger.
4. **`.wslconfig` advice** (the full run's `WslConfigReport`, additive): `advice`, the lines a person could put under
   `[wsl2]`, each only where the file differs from the recommendation, each with what it says now:
   - `memory=` (`wslConfig.recommendedMemoryGb`, existing, 36);
   - `swap=` (`wslConfig.recommendedSwapGb`, new, 0–1024, default 16 — the evening used 9.9 of 12);
   - `autoMemoryReclaim=dropCache` (the value the existing warning already names as the one that works with systemd and
     Docker).
   
   It is SHOWN, NEVER WRITTEN: no code path writes `.wslconfig`, and an architecture check asserts that no product file
   contains a write to it. Q5 (which settings to advise) stays the owner's; these defaults are the coordinator's
   stated scope.
5. **Wire and goldens:** additive. The seven `status*.json` goldens gain the two verdicts: fixture values, or the
   "meminfo does not exist" reason in the `status-running-*` ones. `config-keys.json` gains three keys.

**RED (S5), written BEFORE the product code:**
- `Swap_left_below_its_key_warns_and_no_swap_is_ok`
- `Committed_above_its_share_of_MemTotal_warns_and_the_calm_tree_is_ok`
- `Status_answers_swapFree_and_committed_after_memory_swap`
- `A1_and_A2_say_whether_memory_pressure_would_have_fired_and_fire_as_before`
- `The_wslconfig_advice_names_only_what_differs_with_what_it_says_now`
- `No_product_code_writes_wslconfig`
- the contract and golden updates.

#### S5 as built (2026-10-09)

- **Plan round** (coai session `42b21ed8`): `proceed`, 2 of 2 reviewers, 6 findings.
  - **Accepted:**
    - (0) `autoMemoryReclaim` is advised under `[experimental]`, lowercase `dropcache`.
    - (1) a swap smaller than the key is judged by `memory.swap` only, not "low".
    - (4) every advised line names its measurement.
    - (5) a companion test proves the no-write scan still finds a write.
  - **Rejected, with reasons:**
    - (2) `thresholds.memoryPressureWarn` already exists.
    - (3) no status golden holds A1's or A2's preview.
- **Built:**
  - `Thresholds/SwapAndCommit`: `memory.swapFree` and `memory.committed`, after `memory.swap`.
  - `MemorySnapshot.Committed`, read from `Committed_AS`.
  - `Actions/Memory/MemoryPressureShadow`: A1's and A2's fact and reason sentence, using `MachineBusy.Crosses`, the one S6 comparison.
  - `Health/WslConfigAdvice` and `WslConfigReport.Advice`.
  - The keys `thresholds.swapFreeWarnGb` (4), `thresholds.committedWarnPercent` (80) and `wslConfig.recommendedSwapGb` (16).
  - The seven status goldens gained the two verdicts.
  - `ArchitectureTests.WslConfig`.
- **RED-first, as the coordinator asked after S6:** every test was written BEFORE the product code and seen red against stubs; teeth were then shown on two rules.
- **Code round** (same coai session, 7 of 8 reviewers; gemini security timed out): `proceed`, 6 findings.
  - **Accepted:**
    - (1) `module_daemon.md` gains the section, with a diagram.
    - (2) scenario flows over the built binary: the captured tree's `memory.swapFree` and `memory.committed`; the fresh-boot variant now carries `Committed_AS`, and its all-ok count moved to 9.
    - (3) the no-write scan asserts the extension's source exists.
    - (4) a swap KNOWN to be 0 is "no swap" even with its free figure unread.
  - **Rejected, with reasons:**
    - (0) threading the config through `HealthReports.From`: the same key goes through the same per-verb `Tuning` scope.
    - (5) unit-aware comparison: a residual, below.
- **Own review** (Opus, read-only): nothing serious, and the goldens and the A1/A2 firing were confirmed. Fixed:
  - An UNREADABLE `.wslconfig` was advised as "not set". `WslConfigAudit.Read` is false then, and no advice is given.
  - The scan now counts `MoveFile` / `DeleteFile` as writes, and a text builder's one-argument `AppendLine` is not one.
  - The status bar would have turned yellow on `memory.committed` (a promise). It is left out of the bar now; the panel still lists it.
  
  Each of these was RED first, then green, then red again with the fix broken.
- **Residuals:**
  - The advice compares setting texts (`36gb` = `36GB`), but not units (`36864MB` reads as different).
  - Whether memory pressure should really trigger A1 or A2 is S8's measurement.
  - Q5 (which settings to advise) stays the owner's.

### S6 — a "machine busy" signal agents can poll

**Problem.** Agents start heavy builds into an already saturated machine (L1–L2, L7). PSI cpu and io are read
(`MemoryCollector.cs:8`) but only memory PSI is judged (`ThresholdRules.cs:182`).

**Design.** Verdicts `pressure.cpu` and `pressure.io` (avg60 against `thresholds.cpuPressureWarnPercent`,
`thresholds.ioPressureWarnPercent`), and a fast verb `wsl-care busy [--json]` that reads only `/proc/pressure/*` and
`/proc/loadavg` (milliseconds, no MCP window, no history) and exits `0` calm / a new documented exit code for busy
(`contracts/exit-codes.json`), naming which pressure crossed which key. Agent tooling polls it before a heavy step and waits
with a bounded backoff.

#### S6 — as to be built (2026-10-09, branch `feat/wc-machine-busy`)

1. **One rule, one place:** `Thresholds/MachineBusy.Judge(PressureSet, limits)`, a pure function. The machine is BUSY when
   any of these crosses its key:
   - cpu `some avg60` > `thresholds.cpuPressureWarnPercent` (new key, 0–100, default 20);
   - io `some avg60` > `thresholds.ioPressureWarnPercent` (new key, 0–100, default 10);
   - memory `some avg60` > `thresholds.memoryPressureWarn` (existing, 10).
   
   It is CALM when every pressure that was read is under its key, and UNKNOWN when none was read: a kernel without PSI, or the
   Windows binary. The defaults follow L2: the evening's cpu `some avg10` was 31 %; the captured calm 2026-10-02 tree reads
   cpu avg60 4.18 and io 1.69. avg60 is a stable window to decide "start now or wait" on (avg10 would flap). The keys are
   display keys (no action reads them).
2. **Status:** two verdicts after `memory.pressure` — `pressure.cpu` and `pressure.io` (warn above the key; value
   `some avg10 X, avg60 Y`). They come from the sample's existing `PressureSet` (read by `MemoryCollector`), so `status`
   and the full run judge them with the same records. The extension's status bar takes every verdict's level, so a busy
   machine shows as a warning there with no extension change.
3. **The verb `wsl-care busy [--json]`:**
   - It reads only `/proc/pressure/{cpu,io,memory}` and `/proc/loadavg` (the load is shown, never judged) and the
     configuration. There is no process walk, no MCP window, no history and no write but its own run log (as every verb); it runs as any user.
   - Exit codes: `0` for calm or unknown, a new code `83 machineBusy` for busy. JSON:
     `{ state: calm|busy|unknown, reasons: [{ resource, window, value, limit, key }], pressure: {cpu, io, memory},
     load: {…}, evaluatedAt }`. The text form is one line.
   - An unknown answer is "go": an agent never waits on a kernel that cannot answer.
4. **The agent-side contract** (README, a new section): poll before a heavy step (a build, a test suite, an install).
   - On `83`, wait with a bounded backoff (30 s, 60 s, 120 s, at most 10 minutes in total), then go anyway and say so.
   - On `0`, or any other code, go.
   - It is never a lock and never a queue. It is advice only; the daemon starts and stops nothing because of it.
   - A shell snippet shows the loop.
5. **Wire:** additive. `contracts/exit-codes.json` gains `machineBusy` (regenerated). The 7 `status*.json` goldens gain
   the two verdicts: values from the fixture's PSI, or the "meminfo does not exist" reason in the six `status-running-*`
   goldens. They are hand-edited (Windows only; the Linux CI legs verify them).

**RED (S6):**
- `A_cpu_io_or_memory_pressure_above_its_key_makes_the_machine_busy_naming_the_key`
- `Every_read_pressure_under_its_key_is_calm_and_none_read_is_unknown`
- `Status_judges_cpu_and_io_pressure_after_memory_pressure`
- `Busy_answers_83_with_its_reasons_and_calm_answers_0`
- `Busy_reads_no_process_and_writes_nothing`
- `An_unreadable_pressure_is_unknown_and_exits_0`
- the contract and golden updates.

#### S6 as built (2026-10-09)

- **Plan round** (coai session `79b51f53`): `proceed`, 2 of 2 reviewers, 7 findings.
  - **Accepted:**
    - (1) when the sample's memory part was not read, the verdict says the PSI was not read with it. It no longer borrows another file's reason.
    - (2) one unread pressure makes an otherwise calm machine UNKNOWN, never calm by absence; a crossed one is still busy.
    - (3) the README loop has jitter (± 25 %), so twenty waiting agents do not wake on the same second.
    - (5) the contract separates a broken signal (any code other than 0 and 83: say so, then go) from calm.
    - (6) the keys' 0–100 range is tested.
  - **Rejected, with reasons:**
    - (0) running the goldens locally: the Linux binary produces them; the Linux legs verify them.
    - (4) avg60 vs `memory.pressure`'s avg60-or-avg300: different questions, documented.
- **Built:**
  - `Thresholds/MachineBusy` (`Judge`, `Verdicts`, `BusyLimits`, `BusyReason`, `BusyJudgement`).
  - `Status/BusyReport` (+ `LoadReport`).
  - `Collectors/Procfs/LoadAverageFile`: the idle gate's parser was moved there and reused.
  - `MemoryCollector.ReadPressures` (static).
  - CLI `busy [--json]` (`BusyCommand`), `ExitCode.MachineBusy = 83`. Neither unit can reach it, and both are classified.
  - The keys `thresholds.cpuPressureWarnPercent` (20) and `thresholds.ioPressureWarnPercent` (10).
  - The seven status goldens gained the two verdicts.
- **Tests:**
  - `MachineBusyTests` (4).
  - `StatusVerdictsTests.Status_judges_cpu_and_io_pressure_after_memory_pressure`.
  - `BusyCommandTests` (3).
  - `BusyFlows` over the built binary (busy on Linux, unknown on Windows).
  - The tests were written first. The first run was against the finished code, so the teeth were shown by breaking product code: unknown-by-absence off, the verdict level fixed at ok, the exit code fixed at 0 — 6 red, restored green.
- **Code round** (same session, 8 of 8 reviewers): `proceed`, 10 findings.
  - **Accepted:**
    - (0, 7) one comparison, `MachineBusy.IsOver`, used by the verb and the verdicts.
    - (1) the PSI reader moved to `PressureFile.ReadSet`.
    - (3) the verdict reason describes the metric ("above the key …").
    - (4, 8, 9) the README loop is bash, says what it waits for, and clamps every pause to the 600 s budget.
    - (6) the report's lists are never null. RED first: *Expected report.Reasons not to be &lt;null&gt;* — the source generator sets an init property it does not find to null. The getters answer `field ?? []`.
  - **Rejected, with reasons:**
    - (2) taking 83 out of the extension's root kinds: its distinct-kind guard needs every code.
    - (5) a live extension check: the extension never runs `busy`; `BusyFlows` checks the built binary.
- **Own review** (Opus, read-only): the rule, the texts, the goldens and the wiring are correct. Four doc and loop items, all fixed:
  - The extension's status bar did NOT take `pressure.*`. Its prefixes now include it, with a test: RED first (*'none' !== 'warn'*), then green.
  - The README loop over-ran its 10-minute bound. It is clamped now, and checked with a stubbed `wsl-care` in Git Bash (not WSL): 600 s exactly, two waits and then go, and a broken signal says so and goes.
  - The loop broke under `set -e`; `|| rc=$?` fixes it.
  - "writes nothing" is now "writes nothing but its own run log".
  - The text form with nothing read now says "PSI not read".

### S7 — the Windows side (inside E11/E12's scope)

**Problem.** W9: 85 `creds-mcp.exe`, 66 orphaned, 1.32 GB; the Windows binary has no process collector, so `coai-mcp.exe`
and `creds-mcp.exe` are invisible (`module_mcp_servers.md` *Residuals*). **Design (lands in E11 / E12 of
[PLAN_windows_care.md](PLAN_windows_care.md)):** the MCP catalogue on Windows (`coai-mcp.exe`, and `creds-mcp.exe` once the
parent plan's Q-M2 opens the catalogue), a Toolhelp snapshot for parents and creation times (an orphan = parent gone, or a
parent created AFTER the child — a reused pid), CPU through `GetProcessTimes` with the S1 ledger shape; the stop action as an
E12 button (W-A, by pid AND creation time). `vmmemWSL` reclaim advice (`autoMemoryReclaim`) in the S5 report. **Defender
exclusions** for build and tool folders are a security trade-off: Q6, never automatic, at most advice.

#### S7a — as to be built (2026-10-09, branch `feat/wc-windows-mcp`): the Windows side's MCP servers, READ-ONLY

**Measured first (Windows, read-only, `Get-CimInstance Win32_Process`, 2026-10-09 ~12:20Z):**
- 41 MCP processes: 3 `coai-mcp.exe` (317 MB working set) and 38 `creds-mcp.exe` (544 MB).
- Parents: 3 `coai-mcp.exe` under `claude.exe`, 3 `creds-mcp.exe` under `claude.exe`.
- 35 `creds-mcp.exe` under ONE `wsl.exe`: VS Code's WSL connection (`wsl.exe -d Ubuntu sh -c "$VSCODE_WSL_EXT_LOCATION/scripts/…"`). They are the Windows halves the distro's `creds-mcp` starts through interop. They stay alive while that connection lives, long after their Linux caller ended — the W9 accumulation.
- No parent was gone at that moment. W9 (2026-10-07) counted 66 orphaned of 85.

**Design (no action — a STOP on Windows is a separate story for the owner):**
1. **A seam, `IWindowsProcessTable`:** one snapshot of `{pid, parent pid, exe name, created, CPU time, working set, private bytes, session id}`.
   - The real `Win32ProcessTable` (win-x64 only) reads it through `CreateToolhelp32Snapshot` (pid, parent, name: one kernel snapshot, nothing started).
   - For a matched process and its parent only, it opens `OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION)` and reads `GetProcessTimes`, `K32GetProcessMemoryInfo` and `ProcessIdToSessionId`. Read rights only, every handle closed.
   - A process that cannot be opened is listed with its unread figures unavailable, never 0.
2. **The servers watched on Windows** (`Mcp/WindowsMcpCatalogue`):
   - `coai-mcp` (the catalogue's), when `mcpServers.watched` holds it;
   - `creds-mcp`, Windows-catalogued because W9 measured its leak there. On the distro it stays a user-program choice (S2c);
   - the user's `mcpServers.programs`.
   
   A name matches the exe name with `.exe` stripped, compared without case (Windows file names).
3. **Owner per instance:**
   - `agent` (the first ancestor that is a catalogue agent binary, `claude.exe` …);
   - `interop` (the parent is `wsl.exe`: a WSL connection's interop child);
   - `orphaned` (the parent is gone, OR the live parent was created AFTER the child: a reused pid);
   - `other` (any other live parent, named).
4. **CPU over an interval:** two snapshots, `mcpServers.cpuWindowMilliseconds` apart (the existing key). Per instance: CPU % of one core over the measured interval. Idle = under `mcpServers.idleCpuPercent` and older than `mcpServers.idleMinAgeMinutes` (existing keys). The S1 ledger (an interval since the previous sample) is a follow-up on Windows: the window is what this first read-only step measures.
5. **Wire:** additive, the Windows binary's `status --json` only.
   - A new `windowsMcpServers` block: `available`/`reason`, `windowMilliseconds`, `count`, `idleCount`, `orphanedCount`, `heldBytes` (Σ private bytes), `workingSetBytes`, `cpuCores`.
   - `servers[]{name, count, idle, orphaned, workingSetBytes}`.
   - `owners[]{kind, parent, count}`: the 35-under-one-`wsl.exe` shape in one line.
   - `instances[]` (at most `mcpServers.maxInstances`): `pid`, `server`, `parentPid`, `parentName`, `owner`, `created`, `cpuPercent`, `workingSetBytes`, `privateBytes`, `sessionId`.
   - The distro's binary answers it unavailable ("the distro's binary reads the distro's servers: mcpServers"). The Windows binary's `mcpServers` reason now points at the new block.
   - No Linux golden changes: the block is omitted when null, and the Linux answer's `windowsMcpServers` is omitted. **To verify:** whether the distro's binary should carry an unavailable block or none. Additive either way.
6. **vmmem advice:** the Windows binary's `host` block gains `vmmemAdvice` when `vmmemWSL` was read:
   - *"vmmemWSL holds X GiB of the host's Y GiB. WSL returns page cache to Windows only with `[experimental] autoMemoryReclaim=dropcache` in `.wslconfig` (the S5 advice; shown, never written); `wsl --shutdown` returns all of it and ends every WSL session."*
   - Text only, never acted on.
7. **Verdicts:** none in this step. The block is a report; a Windows verdict and the stop button come with the owner's decision.

**RED (S7a), written BEFORE the product code:**
- `The_windows_mcp_instances_are_counted_by_exe_name_without_case`
- `An_instance_whose_parent_is_gone_or_was_created_after_it_is_orphaned`
- `Owners_are_agent_interop_orphaned_or_other`
- `Cpu_over_the_window_decides_idle_and_an_unopenable_process_is_unavailable_never_0`
- `The_distro_binary_answers_the_windows_block_unavailable`
- `The_real_process_table_lists_this_test_process_with_its_parent` (Windows only, read-only)
- `Vmmem_advice_names_the_reclaim_setting_and_wsl_shutdown`

#### S7a as built (2026-10-09)

- **Plan round** (coai session `38bcfc7f`): `proceed`, 8 findings.
  - **Accepted:**
    - (0) interop ownership cannot see the caller in the distro: the owner says `interop`, and the residuals say orphan
      detection does not cover the children of a WSL connection that stays alive.
    - (1) a Windows CLI scenario over the built binary (`Scenarios/WindowsMcpFlows`).
    - (2) the two CPU reads are compared only while the pid names the same process (the same creation time).
    - (5) the vmmem advice only above a key (`wslConfig.vmmemAdviceGb`, 24); `dropcache` advised only when `.wslconfig` was
      read without it; `wsl --shutdown` named as the last resort.
    - (6) the owner walk checks the ancestors' creation times too: a reused ancestor pid owns nothing.
    - (7) a deterministic order before the cap: private bytes, largest first, then pid.
  - **Rejected, with reasons:**
    - (3) no wait in `status`: the owner's Q-M5 keeps the CPU window wait; a Windows ledger is a follow-up.
    - (4) reusing the `mcpServers` block: the extension reads neither block yet, and the fields differ (no log activity,
      no starts; owners, sessions and private bytes instead) — a separate block keeps the distro's contract unchanged.
- **Built:**
  - `Mcp/WindowsMcp` (the seam `IWindowsProcessTable`, its records, `UnreadWindowsProcessTable`), `Mcp/Win32ProcessTable`
    (Toolhelp snapshot, `OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION)`, `GetProcessTimes`, `K32GetProcessMemoryInfo`,
    `ProcessIdToSessionId`, `CloseHandle`), `Mcp/WindowsMcpCatalogue`, `Mcp/WindowsMcpOwners`, `Mcp/WindowsMcpCollector`.
  - `Status/WindowsMcpServersReport` (`windowsMcpServers`), `HostReport.VmmemAdvice`, `Health/VmmemAdvice`;
    `HealthCollector.AuditWslConfig(files, file)` (static, so `status` reads `.wslconfig` without a command runner).
  - `CliHost.WindowsProcesses` (reads nothing unless `ForThisMachine` wires the real table on Windows), `StatusCommand`
    `WithWindowsSide`; `McpServerCollector.WindowsNotYet` → `WindowsReadsItsOwn` (the reason now points at `windowsMcpServers`).
  - The key `wslConfig.vmmemAdviceGb` (default 24, `contracts/config-keys.json` regenerated).
- **Deviations from the design:**
  - The distro's binary carries NO `windowsMcpServers` block (item 5 said "unavailable"): absent is additive and leaves the
    Linux goldens unchanged. The RED test `The_distro_binary_answers_the_windows_block_unavailable` became the Linux half of
    `WindowsMcpFlows`.
  - The instance's `parentPid` and `parentName` live inside `owner` (`owner{kind, parentPid, parentName, detail}`); the
    memory totals are `held` / `workingSet` byte figures with `memoryRead` (the code round), not `heldBytes` / `workingSetBytes`.
  - `listed` is in the block (how many of `count` are in `instances[]`), as in `mcpServers`.
  - Measured on this machine through the built binary (sandboxed, read-only): 42 instances (3 `coai-mcp`, 39 `creds-mcp`),
    36 under one `wsl.exe`, 6 under three `claude.exe`, all idle, 0 orphaned, 0.74 GB private; `vmmemWSL` 28.9 of 91.6 GiB, so
    the advice showed.
- **Tests:** red first against stubs (7 core tests and the scenario failed for the right reason), then green; break-it on
  product code — each restored, green after: the reuse check off → 2 red; the same-process check off → 1 red; exe names
  compared with case → 1 red; the file's `dropcache` ignored → 1 red; sorted by pid → 1 red. The Win32 constants and the GiB
  unit are listed as formats in `NumbersArchitectureTests`.
- **Code round** (coai session `38bcfc7f`, epic 14/14): `proceed`, 8 reviewers, 13 findings; plus an own reviewer.
  - **Accepted:**
    - (0) the owner kind is a typed `WindowsMcpOwnerKind` inside; the wire names it in words (`WindowsMcpOwnerReport`,
      `WindowsMcpOwnerGroupReport`).
    - (2, 3, 6, and the own review) unread memory was summed as 0: `held` (was `heldBytes`) and `workingSet` (was
      `workingSetBytes`) are byte figures over the instances read, `memoryRead` says how many, and they are unavailable when
      instances exist and none was read; the per-server working set too.
    - (9) a pid the snapshot names twice crashed the sample (`ToDictionary`): one instance now.
    - (10) the text form paid the CPU window and printed nothing of it: it prints a `windows mcp servers:` line and a
      `vmmem advice:` line.
    - Own review: `AgentAbove` had a complexity of 5 — split into a `Walk` record of three expressions; an unopenable parent
      is tested not to read as a reuse.
  - **Rejected, with reasons:**
    - (1) nullable lists at the JSON edge are the convention of every status block (`available: false` + reason); empty lists
      would read as "measured, none".
    - (4) no sampling indicator: the distro's `status` pays the same window silently, and scripts read the text.
    - (5) moving the collector into the probe pipeline for `collect`: out of scope (status only; the Windows binary runs no
      scheduled `collect`; no verdict or stop yet); the collector is in Core behind a seam for when they come.
    - (7) `AutoMemoryReclaim` is never null — the parser returns `string.Empty` for an absent key.
    - (8) `mcpServers.cpuWindowMilliseconds` is 200..5000; 0 is refused by the loader.
    - (11) sync-over-async in a one-shot console verb, the same as the distro's sampling in the same command.
    - (12) the second pass's extra counters: the whole sample of 42 took 51 ms.
  - **RED → GREEN → RED again** (each fix reverted after the green): unread memory summed as 0 → "found True"; the duplicate
    pid → `ArgumentException … Key: 200`; the text line removed → the line missing; an unreadable parent taken as a reuse →
    the owner differs.- **Residuals:** counted in `status` only (`collect`'s run detail is unchanged); CPU across the window only; an interop
  child's caller is invisible from Windows, so the W9 accumulation shows as one big `owners[]` group, not as orphans;
  `playwright-mcp` is not matched on Windows (no command line read); no Windows verdict and no stop (the owner's decision).

### S8 — the 24 h × 20 sessions soak campaign

**What is sampled, every `soak.periodMinutes` (10):** `wsl-care status --json` (the S1 MCP block, S5/S6 verdicts, memory,
PSI), `wsl-care busy --json`, the count of Claude Code "connection timed out after 30000ms" lines since the previous sample
(read-only, from Claude Code's own MCP logs), Windows counters (CPU, commit, `vmmemWSL`, `creds-mcp.exe` count and
orphans), and one fact S1 assumes: whether the guest's monotonic clock stops while the Windows host sleeps (a sample before
and after a host sleep, wall against monotonic). **By what:** a harness in the product's language (`src_daemon/tests/WslCare.Soak`, a console runner under git,
not a shell script), started by the owner with twenty sessions open, writing one JSON line per sample to
`research/soak/<date>/samples.jsonl` and a summary to `research/<date>_soak.md`. **Pass:** § 2's criteria with the owner's
numbers (Q8) — **S8 does not start until Q8 is answered** (coai plan round finding 6: without the numbers there is no
determinate pass). **Sessions and timeouts are observed continuously, not sampled** (findings 1 and 5): the twenty sessions
are the owner's real ones — a synthetic driver would measure a different load, which is why none is built — and each sample
records how many `claude` sessions and connected MCP servers run, plus EVERY "connection timed out after 30000ms" line
Claude Code wrote to its own MCP logs since the previous sample (the logs are cumulative, so a timeout between two samples is
counted, never missed); one timeout fails the criterion. **Budget:** ~2 KB per sample (the summarised fields, not the whole
status) × 144 samples ≈ 290 KB per day; two runs are planned (a baseline, and one after S2–S4), ≈ 0.6 MB, kept in git for
good as the record (finding 7).

## 6. Boundaries with the neighbouring plans

| Item | Built by | The other plan's part |
|---|---|---|
| the MCP metric's CPU basis | this plan, S1 | [PLAN_wsl_care_daemon.md](PLAN_wsl_care_daemon.md) § *E7.S2d* built the metric; its *as built* gains a pointer here |
| stopping MCP servers (Q-M1 there) | this plan, S2 (A19) | E7.S2d left it as Q-M1; answered here |
| open catalogue names (Q-M2 there) | E7.S2d's Q-M2 | S7's `creds-mcp.exe` waits on it |
| Windows process collector, Windows stop actions | [PLAN_windows_care.md](PLAN_windows_care.md) E11 / E12 | S7 names what they must include for MCP servers |
| `.wslconfig` advice | the parent plan's `wslconfig.memory` (exists) | S5 extends the report, never writes |
| agents waiting on "machine busy" (S6), heavy steps through `wsl-care low` (S4) | the verb and its exit code: this plan | the CONSUMER is the family's shared agent rules and prompts (the conventions repository), outside this one — an outward-facing change proposed there, never edited from here |

Disjoint otherwise. Order: S1 first (S2 and S3 read its ledger); E11 before S7's Windows half.

## 7. Growth and budget

| Surface | Projected size | Who retires it | Interrupted |
|---|---|---|---|
| `mcp-cpu.json` (root, and one per account) | ≤ `maxInstances` (256) × 2 points × ~120 B ≈ 60 KB worst; ~20 instances × 2 × 120 B ≈ 5 KB typical | rewritten whole when it changed: dead identities, other boots and points older than `cpuIntervalMaxMinutes` dropped | written atomically (temp + rename); a torn or malformed file reads as "no baseline". A `status` killed between the temp write and the rename (the extension's 20 s ceiling) leaves one temp file beside the ledger; the next write sweeps the ledger's own temp files older than the minimum interval (coai plan round finding 3, built in S1) |
| the `mcpServers` block in each run detail | +~60 B per instance over today's ~400 B | the run details' 90-day retention | — |
| S2's `busyWithoutActivitySince` | one timestamp per ledger identity | same as the ledger | same |
| S8 samples in `research/soak/` | ≈ 290 KB per 24 h run | kept in git per run (a record), one run per campaign | the harness writes per sample (beside + move), so a stopped campaign keeps every sample taken |

## 8. Build order

1. **S1** (this pull request) — the metric; nothing else can be judged without it.
2. S6 (the busy signal; small, read-only) and S5 (verdicts; read-only).
3. S2 (after its measurement; reads S1's ledger).
4. S3 (after its measurement).
5. S4 (after its measurement and Q3/Q4).
6. S7 inside E11/E12.
7. S8 — first with S1+S5+S6 shipped (a baseline day), again after S2–S4.

## 9. Test plan

Per story: the RED tests named above, each seen red for the real symptom, then green, then the guarding line removed and
seen red again; fixtures of `/proc` in the sandbox (`SyntheticProcTree`) with fake trees, no real account name
(`FixtureIdentity`). A scenario flow per new verb or block (`WslCare.Scenarios`, the built binary) catalogued in
`research/module_tests.md`. Windows suites run in full locally; the Linux legs are CI's while the machine is overloaded (no
WSL builds or test runs by agents until the owner lifts that). Goldens regenerated and read when a JSON shape changes.

## 10. Definition of Done (the epic)

- [ ] Each story's RED tests seen red, green, and with teeth; whole suites green on Windows and on CI's Linux legs.
- [ ] Every new number a key with range, default and trust; contracts regenerated; `ArchitectureTests.Numbers` green.
- [ ] No process ended by name; every end by pid AND start; the agent process and root never targets.
- [ ] Each "measure first" recorded in `research/` before its design was final.
- [ ] Docs: `research/module_mcp_servers.md`, `research/architecture.md` (module map), `research/module_tests.md`,
      `README.md`'s status section; this plan's *as built* per story.
- [ ] S8 run for 24 h with twenty sessions and its result recorded against § 2 — pass or the failing hour attributed.
- [ ] The owner questions answered or recorded as open.

## 11. Owner questions

- **Q1 — A19 automatic: DECIDED (owner, 2026-10-08).** A STOP button for idle MCP servers AND automatic stopping, its
  `auto` switch a setting **default ON**; "idle" is a setting, idle for more than **60 minutes** by default. **Q1b** follows:
  automatic stopping needs a root sampler shorter than `mcpServers.cpuIntervalMaxMinutes` (the coai plan round's finding 0),
  so S2 builds one. Also decided the same day (the parent plan's Q-M2–Q-M5): users may add their own programs to the
  watched list (validated); an MCP server whose agent died stays counted and is a stop candidate; the E7.S2d defaults stay,
  all settings; `status` keeps its extra CPU wait.
- **Q2 — what "restart" means** once S2's measurement says what Claude Code does with an ended server.
- **Q3 — cgroup delegation:** may `install.sh` write `user@.service.d/50-wsl-care-delegate.conf` (a system setting)?
- **Q4 — `wsl-care low`:** a verb, or documentation of the `systemd-run` line only?
- **Q5 — `.wslconfig` advice:** confirm "shown, never written"; which of memory cap, swap size, `autoMemoryReclaim` to advise.
- **Q6 — Defender exclusions:** advise them at all? (A security trade-off; never automatic.)
- **Q7 — upstream reports:** may an agent open issues in ConnectOtherAIs (`coai-mcp` start path under load) and CredsForDevs
  (`creds-mcp.exe` not exiting when its client goes — W9)? Outward-facing, so asked first.
- **Q8 — the soak's pass numbers** X (PSI cpu avg300), Y (swap free), Z (idle MCP cores).
- **Q9 — `status` writes one file now** (`$XDG_STATE_HOME/wsl-care/mcp-cpu.json`, only when an MCP server runs). The
  alternative without any write is a lifetime average, which the parent plan rejected (E7.S2d Decided 5). Accept?
  **Coordinator default (2026-10-08), owner may change: YES** — one ledger file in the user's state folder is fine.
- **Q11 — A19 on an install already past its dry week (own code review, 2026-10-08):** the daemon's dry-run window is
  global, so an install whose `dryRun` is off and whose first week has passed starts stopping idle MCP servers at the second
  timer run after the upgrade, with no dry observation of A19. Keep that (the owner's "default ON"), or give a NEW action its
  own first-week dry window (an engine change, its own story)? **Coordinator default (2026-10-08), owner may change: no
  separate A19 dry period** — it follows the daemon's `dryRun` like every action.
- **Q10 — S1's defaults:** 120 s minimum interval, 20 min maximum (two activity windows — a longer average stops
  describing now, so the 4-hour timer measures over the window until Q1b's sampler exists). **Coordinator default
  (2026-10-08), owner may change: keep 120 s / 20 min.**
- **Q12 — a typed program name (S2c code round, rejected there as out of scope):** re-type the program-name concept across
  the MCP catalogue, the process snapshot and the agent catalogue? **Coordinator default (2026-10-08): not now** — a
  separate refactor, left to the owner.
- **Q13 — interpreter-run MCP servers (S2c):** `mcpServers.programs` cannot name a server whose argv[0] is an interpreter
  (`playwright-mcp` runs as `node …/playwright-mcp`). **Coordinator default (2026-10-08), owner may change: YES** — add
  catalogue entries for the interpreter-run MCP servers measured on this machine (`playwright-mcp` and any other measured
  one), in a small separate PR after S3.
- **Q14 — orphaned VS Code language servers (S3):** A11 already ends orphaned, idle, old processes of the families in
  `processes.families`, by pid and start; `vscode-server` is choosable but not in the default list, and A11 is off by default.
  Revised after the S3 plan round: `vscode-server` must NOT be listed (it matches the daemonised VS Code server itself);
  the C# language server is now its own family, `language-servers`. Add `language-servers` to the default families? (Asked,
  not done: S3 rule (b) stays a choice of the owner, as Q1 said.)
- **Q15 — A11 on by default? (S3 plan round, finding 3):** A11 already reaps idle ORPHANED build servers one by one (its
  default families are `dotnet-build-servers` and `testhost`), which A3's whole-user shutdown cannot do while any server
  works; but A11 is off by default. Switch `auto.A11` on?
- **Q16 — `vscode-server` stays choosable for A11:** narrowing the closed list would make an existing layer that names it
  invalid (observe-only). Keep it choosable with the warning, or remove it (and accept the layer error)?

## 12. Review rounds

- **coai plan round (session `010a086d`, 2026-10-08):** the coai MCP server did not connect in the authoring session
  ("connection timed out after 30000ms" — the very symptom of § 1); the round ran once it did. Verdict **good_enough**, 2 of
  2 reviewers (gemini, codex), 8 findings: 6 accepted, 2 rejected with reasons. 0 (root ledger never interval under the 4 h
  timer → A19 finds nothing): accepted, S2 now makes a root sampler shorter than the maximum a prerequisite of A19 at all.
  1, 5 (S8 cannot see sessions or timeouts between samples): accepted, S8 counts every timeout line since the previous sample
  and the live sessions; a synthetic session driver is declined (it would measure another load). 2 (S3 has no build
  detection): rejected — `BuildServerShutdown.Builds` and its run-time re-check exist and are tested; S3 now says it reuses
  them. 3 (orphaned temp files until S2): accepted, built in S1. 4 (the 4 h timer falls back to the window): rejected —
  deliberate (design 2, own review finding 3); the live metric is the extension's 120 s `status`. 6 (X/Y/Z unresolved):
  accepted, S8 waits for Q8. 7 (soak retention): accepted, two runs ≈ 0.6 MB kept.
- **coai code round (session `010a086d`, 2026-10-08):** verdict **proceed**, **8 of 8** reviewers (gemini, codex), 8
  findings: 2 accepted and fixed RED-first, 6 rejected with reasons. Accepted: a temp file that cannot be swept no longer
  stops the ledger being written (RED: *recorded False … is in use*); no window is waited when the kernel's tick rate is
  unreadable (RED: *waited … found True*) — each with teeth. Rejected: "the sweep throws on a missing folder" (false —
  `ListFiles` answers empty, a test writes into a fresh folder), a linked state FOLDER (same-account only, outside the shared
  writer's stated threat model; the read refuses it), the text-line basis count (inaccurate), re-serialising `before` (a few
  KB), list defaults on positional records (the module's own shape; the deserializer does not run defaults), a ledger
  schema of its own (the same as A18's, a self-healing miss).
- **Cadence consultation (coai, codex, 2026-10-08, closed solved):** two real defects, both fixed: a ledger that is a link
  was written through (the atomic writer replaced the sibling it pointed at) — now refused; a negative monotonic point
  overflowed the age — every point must be non-negative. Its third idea (a separate 4-hour historical CPU figure beside the
  bounded reading) is declined for S1 and left to S2.
- **Own plan review (stand-in, 2026-10-07; one reviewer, `feature-dev:code-reviewer` on Opus, read-only).** Verified as
  fine: every S1 file:line, the CPU formula and units, the two-point rule (no starvation for one poller with jitter, two
  pollers 1 s apart, or a poller faster than the minimum), the growth bound, concurrent writers (the atomic write's temp
  name is unique), the identity, the monotonic denominator. 14 findings (6 Major, 8 Minor), each disposed:

| # | Finding | Disposition |
|---|---|---|
| 1 (Major) | a root `status` is re-homed to the target user (`CliHost.cs:122`) and would write a root-owned ledger into that user's home | **Accepted** — root `status` reads root's ledger and writes nothing (S1 design 5); a CLI test holds it |
| 2 (Major) | `ReadStateFile` trusts only root's files on a real machine: the user's ledger would be "no baseline" forever while sandbox tests pass | **Accepted** — the user ledger is read with `ReadUserFile` (owner = this uid, no link) |
| 3 (Major) | a kind judged over a 4–6 h interval calls a now-quiet server busy without activity | **Accepted** — the maximum interval is 20 min (two activity windows); beyond it the window answers; RED test added |
| 4 (Major) | the "status writes nothing" test has no MCP instance, so it cannot see a ledger | **Accepted** — new CLI tests over a sandbox with a `coai-mcp` instance |
| 5 (Major) | S2: `EndAllAsync`'s recheck refuses a target whose CPU moved — every busy server | **Accepted** — S2 names an A19-only recheck mode, A11/A18 tests kept |
| 6 (Major) | S2: which ledger root trusts, and a 4 h ledger is no evidence | **Accepted** — root's ledger only; Q1b is a prerequisite |
| 7 (Minor) | refuse Δticks < 0, Δmonotonic ≤ 0, a rate above the machine's cores | **Accepted in part** — the first two refused; the cap is **rejected**: the ledger root reads is root's own, and a user's own ledger can only misstate that user's own view; a cap needs the online CPU count, which the collector does not read |
| 8 (Minor) | "the monotonic clock stops while the VM is paused" is unmeasured | **Accepted** — marked unmeasured in S1, measured in S8 |
| 9 (Minor) | orphaned temp files and an fsync per `status` | **Accepted in part** — written only when changed; the temp-file sweep is a recorded residual (§ 7) |
| 10 (Minor) | the interval keys become evidence for S2 | **Accepted** — S2 re-classifies them |
| 11 (Minor) | S2–S8's keys lack range, default, trust | **Accepted** — each story's plan revision gives them before its code round (stated in S2; the same holds for S3–S8) |
| 12 (Minor) | "measured ~60 s burst" overstates the evidence | **Accepted** — "a period near a minute, not measured exactly" |
| 13 (Minor) | `McpSample.cs:52` and the parent plan's Decided 8 say status writes no state | **Accepted** — amended in S1's change |
| 14 (Minor) | S6/S4's consumers are the family's agent rules, outside this repository | **Accepted** — a boundary row in § 6 |

## 13. S1 as built (2026-10-07)

Built on `fix/wc-mcp-cpu-since-last-run`; the guarantees, the red and the teeth are in
[module_tests.md](../research/module_tests.md) § *MCP server instances of the AI agents* (the E14 S1 rows), the design in
[module_mcp_servers.md](../research/module_mcp_servers.md). **Deviations from § 5 S1:**

- **The ledger's code:** `Mcp/McpCpuLedger.cs` holds the file records, the place (`McpCpuLedgerPlace.ForStatus` /
  `ForCollect`), `Baseline`, `Next` and `Record`; the collector's CPU path splits into the ledger reading and the window
  fallback (`WindowAsync`), each instance carrying its `McpCpu` (figure, basis, interval).
- **The boot id and `SampleTime`** moved to `Collectors/Procfs/SampleTime.cs` (`BootIdentity.Read`); A18's history calls them
  there — a refactor, A18's tests unchanged and green.
- **`SyntheticProcTree` gained a boot id** (every synthetic tree starts in one boot), so the collector tests exercise the
  ledger; the CAPTURED tree has none, which keeps `status` over it from writing into the checked-in fixture
  (`McpStatusTests` holds that).
- **Goldens edited by hand:** only `status.json` carries an available block; the agent machine was overloaded and the
  goldens are the Linux binary's answers, so the file was edited to the serializer's shape and CI's Linux leg verifies it.
  A normalisation rule was added (`**.cpuIntervalSeconds.value`: the window is the longer of one second and the real wait).
- **Not built here:** S2's re-classification of the two keys. **Built after the coai plan round:** the sweep of the
  ledger's own orphaned temp files (finding 3) — RED first (*Expected File.Exists(orphan) to be False … but found True*),
  green, teeth (the sweep call removed → red; the age guard removed → the in-flight temp deleted, red).
- **Review rounds:** the coai code round is OWED (the coai MCP server did not connect in this session); one own code
  review stood in — § 14.
- **After the code review:** the ledger is written as one compact JSON line, holds at most `records.maxStateFileBytes` ÷
  320 entries (the newest processes; a test serialises the widest entry and a full ledger under the smallest cap), and is
  read only when well formed (this schema, no null entry or point).

## 14. S1 code review (2026-10-07; one reviewer, `feature-dev:code-reviewer` on Opus, read-only)

Verified as fine: the rate and clocks, the baseline selection, the two-point rule, which reading is recorded, the identity,
write-only-when-changed, concurrency, the root/user trust split, the hand-edited golden (property order, path, reason, the
new rule), the keys and the read-site table, the tests' reasons, the docs. 1 Major, 4 Minor, each RED first where it is a
behaviour, then green, then its line broken and seen red:

| # | Finding | Disposition |
|---|---|---|
| 1 (Major) | JSON that parses with the wrong shape (`"entries":[null]`, an entry with no `points`, a null point) crashed `status` and the timer before the file could be rewritten; the schema was not checked | **Fixed** — `WellFormed`. RED: three cases *NullReferenceException* / *ArgumentNullException*, the schema case *Expected … Window … but found Interval*; green after |
| 2 (Minor) | without the kernel's tick rate the interval path said `interval` for an unmeasured figure | **Fixed** — `McpCpu.Unmeasured`. RED: *Expected … None … but found Interval* |
| 3 (Minor) | the ledger could outgrow its own read cap (indented, up to 1 024 instances) and then read as empty for ever | **Fixed** — compact JSON and at most cap ÷ `BytesPerEntry` entries, the oldest processes dropped; teeth: the cap removed → red |
| 4 (Minor) | nullable returns (`Baseline`, `EntryOf`, `FromLedger`, a reading) and `CpuAsync`'s complexity | **Accepted in part** — `CpuAsync` split (`FirstReads`, `Combine`, `RecordAll`); the nullables are **kept**: each is a legitimate "not found" (no point, no entry, no reading), which the doctrine allows, as `PidSamples.Read` does |
| 5 (Minor) | no test that a root `status` READS the timer's ledger; three "across the window" texts stale | **Fixed** — `A_root_status_measures_over_the_interval_from_the_timers_ledger_and_leaves_it_as_it_was` (teeth: the root place made "none" → red); the texts say "over its interval or across the window" |
