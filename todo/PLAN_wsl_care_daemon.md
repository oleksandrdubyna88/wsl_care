# PLAN — keep WSL from degrading over the working day (`wsl-care` daemon + VS Code extension)

> Status: **in progress, 2026-10-02 — E1.S1 built (root build files, `src_daemon/` skeleton answering
> `--help`/`--version`, its tests, `ci-daemon.yml`) and E1.S2 built (config layering with observe-only,
> `config get/set/reset`, `ICommandRunner`, `IFileSystem` + `DeletionPolicy` + architecture test,
> `IHostPaths`, `IHostProbe`, run records, Serilog — deviations in the E1.S2 row of §16 and in
> `research/architecture.md`) and E1.S3 built (`WslCare.Scenarios` over the built CLI with fake
> `docker`/`systemctl`/`journalctl` on `PATH`, the derived verb register, a `linux-arm64` CI leg, the AOT
> config smoke, `ci · family checks` — deviations in the E1.S3 row) and E2.S1 built (memory / process /
> container / disk collectors behind `LinuxProbe` and a minimal `WindowsProbe`, `status [--json]` with no
> slow process, a captured procfs fixture tree — deviations in the E2.S1 row) and E2.S2 built (Docker collectors with
> `available: false`, `volume-seen.json`, the Docker hygiene audit, `preview --all [--json]`, the `WslCare.LiveContract`
> executable and captured Docker fixtures — deviations in the E2.S2 row) and E2.S3 built (thresholds, health collectors,
> the daily folder walk, `collect` with run detail → history line, startup reconcile, retention and a read-only mode,
> `doctor`, `events follow` with bounded backfill and in-process backoff — deviations in the E2.S3 row), so epic E2 is
> built; the rest of §16 (E3 onwards) is still open.** Scope: a C# Native AOT daemon/CLI run by a
> systemd timer inside the `Ubuntu` distro (`src_daemon/`), a VS Code extension that shows its state, its
> logs and its help and runs cleanups on demand (`src_vs_code/`), CI/CD modelled on CredsForDevs, and the
> one-time cleanups listed in Phase 0.
>
> Evidence: [2026-10-02_wsl_resource_baseline.md](../research/2026-10-02_wsl_resource_baseline.md),
> [2026-10-02_one_time_cleanup.md](../research/2026-10-02_one_time_cleanup.md),
> [2026-10-02_competitor_survey.md](../research/2026-10-02_competitor_survey.md) — every number quoted here
> comes from them.
>
> Companion plans: [PLAN_windows_care.md](PLAN_windows_care.md) (the same daemon on the Windows host),
> [PLAN_ai_session_archive.md](PLAN_ai_session_archive.md) (moving old AI sessions into a dated archive),
> [PLAN_shared_vscode_kit.md](PLAN_shared_vscode_kit.md) (help/language/size/tone as a shared npm package).

## 1. Symptom and goal

**Symptom.** Every day starts from a fresh WSL VM (the PC is shut down every night) and by the evening
the machine is slow. On 2026-10-01 at 18:36 the VM had 0.27 GB free out of 46 GB: ~22 GB of *inactive*
anonymous memory, ~19 GB of page cache that WSL never gives back to Windows, and the memory so fragmented
that the kernel could not find one free 64 KB block (`page allocation failure`, three such days in five
weeks). Next to that: 135 GB of Docker and cache leftovers (removed by hand on 2026-10-02), 41 GB of
worktrees, AI-agent session folders growing without a limit, a journal whose history is erased by clock
jumps, and no process-level history at all.

**Goal.**
1. **Record** what the VM holds, every 4 hours, so the question "who ate the memory" has an answer.
2. **Fix automatically** what is safe to fix: give the cache back, defragment, stop idle build servers,
   and remove Docker and cache leftovers past configured ages.
3. **Show** it all in VS Code — disk, RAM, swap, containers (now / last 24 h / reclaimable), AI-agent
   folders — with **buttons** to run any cleanup by hand, **settings** for the age limits, **logs** of
   every run for any period, and a **help** page in five languages.
4. **Report** everything else, with numbers, and never touch it unasked.

**Success criterion.** Over one working week after enabling actions: no `page allocation failure`;
`MemAvailable` at 18:00 ≥ 25 % of the VM; Docker reclaimable data stays below 30 GB; anyone can name the
top 5 memory holders of any afternoon in the last 7 days (atop, 10-minute records, `LOGGENERATIONS=7`) and
of any 4-hour sample in the last 90 days (`history.jsonl`) — gate round 1, finding 8; and every number in the extension matches the CLI
output it came from.

## 2. Decisions already taken (2026-10-02)

| Question | Decision |
|---|---|
| Where the work lives | this repository, `github.com/oleksandrdubyna88/wsl_care` — `src_daemon/` + `src_vs_code/` |
| Daemon technology | **C# on .NET 10, Native AOT** — one self-contained binary, no runtime to install in the distro |
| Where the scheduled work runs | a systemd timer inside `Ubuntu`; **and, since the 2026-10-02 extension of scope, Windows Task Scheduler on the host** — see [PLAN_windows_care.md](PLAN_windows_care.md) |
| How far it may fix things on its own | **safe automatic actions only**; everything else is report-only or a button |
| UI | a VS Code extension: status bar, side panel, cleanup buttons, settings, logs page, help page |
| Help and display | like coai: a help page in en/ru/uk/de/es, plus text-size and brightness ("tone") controls — taken from a shared package proposed in [PLAN_shared_vscode_kit.md](PLAN_shared_vscode_kit.md) |
| Publishing | build a **first working prototype**, then publish it to the VS Code Marketplace right away (§11 step 10) |
| CI/CD | **by analogy with CredsForDevs** (§9) |
| History tools | `sysstat` **and** `atop`, installed by the installer |
| Killing old processes | a **setting**, off by default: families from an allowlist, older than N hours, idle |
| AI agents | monitored (folder sizes and session counts), auto-discovered, plus a manual "add CLI path" row; **never deleted — sessions older than N days are moved** into `<base>/<agent>/<yyyy>/<MM>/` ([PLAN_ai_session_archive.md](PLAN_ai_session_archive.md)) |
| What happens now | this plan; implementation is a separate task after review |

Consequences, accepted knowingly:
- The timer cannot run `wsl --shutdown` and cannot compact a `.vhdx` (the VM must be stopped for that).
  Both stay manual (Phase 0) — the extension shows when they are due and how much they would free.
- The daemon sees the **whole VM's** memory (`/proc/meminfo` is VM-wide) but only **Ubuntu's** processes
  (`Ubuntu-26.04` and `docker-desktop` are separate PID namespaces). Containers are covered through the
  Docker CLI; whatever remains is reported as an explicit *unattributed* figure instead of being guessed.
- **Windows-side numbers** — `vmmemWSL`, host RAM, AI-agent folders under `%USERPROFILE%` /
  `%APPDATA%` — are read by the **same C# code compiled for `win-x64`** (`wsl-care.exe`, bundled in the
  `.vsix`). With the Windows plan it is a full daemon there, on its own scheduled task. Walking `C:` from
  inside the VM is avoided on purpose — a 9p directory walk is what failed on 2026-10-01.

## 3. Phase 0 — one-time fixes, by hand, each one confirmed (not the daemon's job)

| # | Fix | Why (baseline finding) | How to verify |
|---|---|---|---|
| 0.1 | `%USERPROFILE%\.wslconfig` — **revised after the competitor survey**: `autoMemoryReclaim=dropCache` is reported as the default since WSL 2.1.3, but it fires only after ~10 min of *idle*, which a working day never has; **not** `gradual` (hangs with systemd + Docker Desktop); **not** `sparseVhd` (disabled since WSL 2.5.6 after corruption reports, and a sparse VHDX cannot be compacted). Instead: a `memory=` cap (32–36 GB — the Windows baseline shows the host starved at 2.3 GB free), `swap=8GB`, `maxCrashDumpCount=3` | F1; Windows baseline | `wsl --shutdown`; by the evening Windows keeps ≥ 15 GB available and WSL shows no `page allocation failure` |
| 0.2 | **Done 2026-10-02, ≈ 135 GB freed** — [record](../research/2026-10-02_one_time_cleanup.md). No exited container carried the Testcontainers label, so the Testcontainers-only action was replaced by A5 (stopped ≥ N days) | F2 | `docker system df` before/after, recorded |
| 0.3 | Compact the vhdx files after 0.2: `wsl --shutdown`, then `Optimize-VHD` / `diskpart compact vdisk` (**not** `--set-sparse`); Docker Desktop's own *Clean up* for `docker_data.vhdx` — **user: later, on request** | F3: 142 + 123 + 71 GB files on `C:` | file sizes before/after |
| 0.4 | `systemctl mask wsl-pro.service` in **both** Ubuntu distros | F5: a reconnect every ~20 s, ~4 000 log lines a day, for an agent that is not installed | no `wsl-pro-service` lines in syslog for a day |
| 0.5 | **Verify** the clock hypothesis before changing anything: count jumps for one day with `systemd-timesyncd` (Ubuntu) and `chronyd` (Ubuntu-26.04) stopped, against the ~1 700/day baseline | F4: jumps erase journald history and flush DNS cache | jumps per day; make the change permanent only if the count drops |
| 0.6 | Decide on `Ubuntu-26.04`: keep it, or `wsl --terminate` it when not in use | F1 caveat, F4 | `free` before/after terminating it |
| 0.7 | Decide on `snapd`: no user snaps are installed | F5: snapd/snapfuse CPU at boot, 15 failed starts | `systemd-analyze` before/after |
| 0.8 | Review `~/git/_wt` (41 GB) and `~/coai-514`, `~/coai-462` **by hand**; remove worktrees **from Windows** with `git worktree remove` | F3 | `du` before/after |

**Never in Phase 0 or anywhere else:** `git worktree prune` from WSL — it unregisters every worktree
created from Windows (`D:/…` paths look missing from inside WSL).

## 4. What is monitored

Every run collects one record and evaluates thresholds. Thresholds are config values; the numbers below are
starting points, re-tuned after one week of recorded data.

### 4.1 Memory (VM-wide)

| Metric | Source | Warn | Act |
|---|---|---|---|
| `MemAvailable` % of `MemTotal` | `/proc/meminfo` | < 25 % | < 15 % → A1 |
| page cache (`Cached` + `Buffers`) | `/proc/meminfo` | > 15 GB | > 12 GB **and** available < 30 % → A1 |
| anonymous, of which inactive | `/proc/meminfo` `Inactive(anon)` | > 15 GB | report (§4.2 names the processes) |
| swap used / total | `/proc/meminfo` | > 4 GB | — |
| fragmentation: free blocks ≥ 64 KB (order ≥ 4) **and ≥ 512 KB (order ≥ 7, what VMBus needs)** in zone Normal | `/proc/buddyinfo` | order ≥ 7 < 32 blocks | order ≥ 7 = 0 or any `page allocation failure` → A2 **immediately** (event-driven, not only on the timer) — our order-7 `kworker` failures of 2026-09-09/16 match the known WSL VMBus signature ([#41634](https://github.com/microsoft/WSL/issues/41634)) |
| pressure trend (primary "VM struggles" signal) | `/proc/pressure/{memory,io,cpu}` `some`/`full` avg10/60 | memory `some avg60` > 10 | triggers an early run |
| memory pressure | `/proc/pressure/memory` `some avg300` | > 10 | — |
| `page allocation failure` since last run | `kern.log` / `journalctl -k` | ≥ 1 → **alert** | — |
| OOM kills since last run | same | ≥ 1 → **alert** | — |
| `vmmemWSL` working set, host RAM | Windows probe (§2) | `vmmemWSL` > 80 % of the VM ceiling | — |

### 4.2 Who holds it

- The top 30 Ubuntu processes by RSS: pid, user, rss, age, state, cpu time, cwd, command line (200 chars).
- **Families**, aggregated by config-defined regex: `vscode-server` (extension hosts, ServiceHub,
  `Microsoft.CodeAnalysis.LanguageServer`), `dotnet-build-servers` (MSBuild `/nodemode:`, VBCSCompiler,
  Razor server), `testhost`, `node`, AI-agent CLIs (from the §4.6 catalogue), `docker-desktop-proxy`,
  everything else.
- Per-container memory: `docker stats --no-stream`.
- **Unattributed** = `AnonPages` + `Shmem` − Σ (`RssAnon` + `RssShmem`) over Ubuntu processes OUTSIDE container cgroups − Σ container memory (cgroup `memory.current`, each container counted once) — reported, never guessed. A result below zero is shown as *inconsistent sample*, never as a negative number (§15b #4).
- **Suspects**: processes with PPID 1 or reparented to `systemd --user`, in a family above, older than the
  configured age, with < 1 CPU-second in the last interval. Input for action A11.
- Processes whose cwd or arguments are under `/mnt/` (a walk over 9p caused the 2026-10-01 failure).

### 4.3 Docker

- **Running now** and the `docker system df` totals per type (count, size, reclaimable) — the "Docker
  after: 13 images, 28 containers, 44 volumes, build cache empty" line of the 2026-10-02 cleanup.
- **One row per cleanup**, the same rows as the one-time cleanup record, each with *count* and
  *reclaimable GB* — exactly the `--preview` numbers of the matching action in §5:

  | Row | Count | Reclaimable GB | Action |
  |---|---|---|---|
  | anonymous volumes not attached to any container | `docker volume ls -q --filter dangling=true`, 64-hex names only | `docker system df -v` volume sizes | A4 |
  | containers stopped ≥ N days, with the anonymous volumes they hold (Testcontainers counted separately) | `docker inspect` `State.FinishedAt` (or `Created` for never-started) | container size + their anonymous volumes | A5 |
  | images not referenced by any container (dangling counted separately) | `docker image ls` vs every container's image id | `docker system df` images reclaimable | A6 |
  | build cache | `docker buildx du` | total and reclaimable | A7 |
  | npm cache | — | size of `~/.npm` | A8 |
  | apt cache + disabled snap revisions | `snap list --all` disabled | `/var/cache/apt` + snap file sizes | A9 |

- **Kept, report-only:** named volumes not attached to any container, with their sizes (2026-10-02:
  ~16 GB, `mindex_qdrant_data` 10.3 GB alone) — a human decides per volume; no action exists for them.
- `docker_data.vhdx` size and how much of it is free inside (= what compaction would return).
- `docker system df -v` takes seconds to minutes; the full numbers come from the 4-hour run and are cached;
  `status --json` reports their age.
- **Started in the last 24 h.** `docker ps` cannot answer it (removed containers vanish) and Docker's event
  buffer is too short for 2 000–3 000 starts a day, so a second unit, `wsl-care-events.service`
  (`wsl-care events follow`), appends one line per container start to
  `/var/lib/wsl-care/container-starts/{yyyy-MM-dd}.jsonl` (image, name, Testcontainers label); kept 14
  days; `Restart=always`, `RestartSec=30`.

### 4.4 Disk

`df /` (used / free / %), host `C:` free space, the three `.vhdx` sizes and their growth since the previous
record, and — once a day, because it is expensive — the size of `~/git/_wt`, `~/.npm`, `~/.nuget/packages`,
`~/.cache`, `~/.vscode-server`, and of `bin/` + `obj/` under `~/git`. Warn when `/` > 80 % or a vhdx grew
> 10 GB in a day.

### 4.5 System health

Clock-jump count since the last run (warn > 100 per 4 h), journald disk usage and its oldest entry (warn
when history is shorter than 7 days), failed units, uptime, the WSL `failed to start within` boot error,
and whether `sysstat` and `atop` are collecting (last sample < 30 min old).

Added from the competitor survey:
- **Clock skew**: `CLOCK_BOOTTIME` vs `CLOCK_REALTIME`, and the distro's time vs Windows' (`powershell.exe
  Get-Date` / the Windows daemon) — drift > `clock.maxDriftSeconds` (5) triggers A16.
- **`discard` in `/proc/mounts`** for `/`; if absent, `fstrim.timer` state.
- **inotify** instances and watches per user vs. the limits.
- **meminfo explainer**: growth of `SUnreclaim` (kernel leak), `Shmem` (`/dev/shm`, tmpfs),
  `Inactive(anon)`, swap — each with a plain-language line in the report.
- **OOM forensics**: who was killed and when; whether earlyoom/systemd-oomd is present.
- **`.wslconfig` audit** (read through `/mnt/c`): ceiling, `autoMemoryReclaim` value, `sparseVhd` present →
  warning, `gradual` with systemd/Docker → warning.
- **Docker hygiene audit**: containers on `json-file` without `max-size` and their log sizes; Docker
  Desktop `daemon.json` builder GC present or not (the Windows-side file, not `/etc/docker`); forgotten
  `docker-container` buildx builders and their `buildx_buildkit_*_state` volumes.
- **VS Code Server builds**: `~/.vscode-server/bin/<commit>`, `~/.vscode-server/cli/servers/*`,
  `~/.cursor-server`, `~/.windsurf-server` — how many, which are in use (`/proc/*/cmdline`), size.
- **"What grew since yesterday"** for `$HOME`, `/var`, `~/.cache`.

### 4.6 AI agents — folder sizes and session counts

**Why.** Measured 2026-10-02: on Windows `~/.claude` 2.8 GB with **477 project folders / 2 631 sessions**,
`AnthropicClaude` 1.2 GB, `.codex` 0.45 GB with 385 sessions, `.gemini` 0.5 GB; in WSL `~/.claude` 1.6 GB
(32 projects, 732 sessions, **one project alone 1.4 GB**), `~/.gemini` 0.9 GB (`antigravity-cli` 666 MB),
`~/.codex` 174 MB. Every coai gate run creates its own Claude project folder (`…coai-wt-…-r1`), so the
count only grows.

**Discovery — automatic, on both sides.** A catalogue (`agents.json`, embedded, data not code) lists known
agents. For each entry: display name, binary names, npm package names, data folders per OS, and a
*session layout* (which subfolder holds sessions and what one session is — a folder or a file glob):

| Agent | Binaries | Data folders (Linux / Windows) | One session is |
|---|---|---|---|
| Claude Code | `claude` | `~/.claude` / `%USERPROFILE%\.claude`, `%LOCALAPPDATA%\AnthropicClaude`, `%APPDATA%\Claude` | `projects/*/` folder; `projects/*/*.jsonl` file |
| Codex | `codex` | `~/.codex` | `sessions/**/*.jsonl` |
| Gemini CLI | `gemini` | `~/.gemini` (minus `antigravity*`) | `tmp/*/` |
| Antigravity | `agy`, `antigravity` | `~/.gemini/antigravity*`, `~/.cache/antigravity` / `%APPDATA%\Antigravity`, `%LOCALAPPDATA%\agy` | catalogue entry to confirm on implementation |
| GitHub Copilot CLI | `copilot` | `~/.copilot` | `session-state/*` |
| Rovo Dev | `acli`, `rovodev` | `~/.rovodev` | `sessions/*` |
| Cursor agent, OpenCode, Amp, Qwen Code, Kiro, Aider, Goose, Crush, Windsurf | their binaries | their documented folders | per entry |
| Ollama | `ollama` | `~/.ollama/models`, `OLLAMA_MODELS` | model count instead of sessions |

An agent is **tracked** when any of its binaries is on `PATH`, an npm global package of it is installed, or
any of its data folders exists. Linux side: the daemon, once a day and on demand. Windows side: the
`win-x64` probe, when the panel opens (at most hourly, cached).

**Manual addition.** A row at the end of the agents table: **"Add CLI path…"** — for when the user knows an
agent exists that discovery missed. The user enters the path of the CLI (Linux or Windows); the tool checks
the file exists and is executable, derives a name from it, then looks for data folders by convention —
`~/.<name>`, `~/.config/<name>`, `~/.local/share/<name>`, `~/.cache/<name>`, `%APPDATA%\<name>`,
`%LOCALAPPDATA%\<name>` — and shows what it found, with sizes. The user confirms, removes or adds data
folders and an optional session glob. Saved to `aiAgents.extra` in the config (§6); shown with a "manual"
badge and a remove button.

**Per agent, shown:** side (WSL / Windows), version if the CLI answers `--version` within 2 s, total size
of its data folders, number of session folders/files, the 5 largest sessions, oldest and newest session
date, growth since yesterday. **Warnings:** an agent over `aiAgents.warnGb` (5 GB) or a single session over
`aiAgents.sessionWarnMb` (500 MB).

## 5. Actions — automatic (timer) and manual (buttons)

Every action implements one interface, `ICleanupAction { Id; Preview(record, config); Run(executor) }`,
records before/after numbers, and is reachable two ways: the **timer** runs it when its trigger fires and
its `auto` switch is on; the **extension** runs it on a button press regardless of the trigger (always
with a preview first, §7.3). A4–A9 are the cleanups of the 2026-10-02 one-time run, one action per row of
that record, with its age limits as defaults.

| # | Action | Auto trigger | Setting (default) | Auto by default | Why it is safe | 2026-10-02 |
|---|---|---|---|---|---|---|
| A1 | `/bin/sync`, then `sysctl -w vm.drop_caches=1` — two argv invocations, no shell (§15c #3) | §4.1 | — | on | the cache is rebuilt on demand; freed pages go back to Windows. Never `3` | — |
| A2 | `sysctl -w vm.compact_memory=1` (argv, §15c #3) | after A1, or fragmentation = 0 | — | on | defragmentation only | — |
| A3 | `dotnet build-server shutdown`, as the owning user | build servers alive, **no** `dotnet build/test/run` alive | `buildServers.idleHours` (4 h) | on | the official command; the next build restarts them | — |
| A4 | **`docker volume rm` of a re-checked list, never `prune`** (gate round 1, finding 4): at run time it removes only volumes that were in the shown preview AND are still unattached AND carry no `wsl-care.keep` label AND were first seen ≥ `volumes.anonymousOlderThanDays` ago (timer default 1 day; a button may choose 0, as the one-time cleanup did) — **unnamed (anonymous) volumes only** | > `volumes.anonymousMaxCount` (100) or > `volumes.anonymousMaxGb` (20) | — | on | no container refers to them; named volumes never touched. **Refuses on Docker < 23** | 387 volumes, 59.6 GB |
| A5 | `docker rm -v` of containers stopped ≥ N days (anonymous volumes go with them, named stay) | any exist | `containers.stoppedOlderThanDays` (7 d); Testcontainers: `containers.testcontainersOlderThanHours` (2 h) | **Testcontainers: on; others: off** (button, or opt-in) | user containers go only when the user turned it on or pressed the button | 58 containers, ~3.5 GB |
| A6 | `docker image prune -f` (dangling) and `docker image prune -af --filter until=…` (not referenced by any container) | dangling: any; unused: reclaimable > `images.unusedMaxGb` (10) | `images.unusedOlderThanDays` (7 d) | dangling: on; unused: **off** (button, or opt-in) | an image used by any container, even a stopped one, is never removed | 99 → 13 images, 31.5 GB |
| A7 | build cache: auto — `docker builder prune -f` with a **size cap** (`--max-used-space` / `--keep-storage`, whichever this Docker supports); button — `docker builder prune -af` | cache > `buildCache.maxGb` (20) | `buildCache.maxGb` (20), `buildCache.olderThanDays` (7 d) | on (cap); all: button | rebuildable. An **age filter alone freed nothing** on 2026-10-02 (all 34.6 GB < 7 days old) | 34.6 GB |
| A8 | `npm cache clean --force`, as the target user (§15c #2) — `npm` resolved from the user's bin folders, never through `bash -ic` (§15c #3) | `~/.npm` > `npm.maxCacheGb` (5) | `npm.maxCacheGb` (5) | **off** (button, or opt-in) | a cache | 5.3 GB |
| A9 | `apt-get clean` + `snap remove --revision` of **disabled** snap revisions | apt cache > 200 MB or any disabled revision | — | on | package caches and superseded revisions only | ~0.4 GB |
| A10 | `journalctl --vacuum-time=…` | journald > 1 GB | `journal.keepDays` (30 d) | on | old logs | — |
| A11 | Terminate **suspect** processes (§4.2): `SIGTERM`, `SIGKILL` after 10 s | suspects exist | `processes.idleOlderThanHours` (8 h) + `processes.families` | **off** | opt-in; never a process with a TTY or recent CPU | — |
| A12 | Playwright browsers **not referenced by any project** (never `uninstall --all`) / NuGet `http-cache` | — | — | **off** (button only) | caches, but large re-downloads | — |
| A13 | AI-session archive — move, never delete ([PLAN_ai_session_archive.md](PLAN_ai_session_archive.md)) | daily, sessions older than N days | `archive.olderThanDays` (14) | on once a base folder is set | copy → verify hash → delete source; restorable | — |
| A14 | Old VS Code Server builds (and Cursor/Windsurf servers) not used by any running process; keep the newest 2; `.obsolete` extensions | > 2 builds | — | on | re-downloaded on demand by the editor | — |
| A15 | `fstrim -av` | weekly, when `discard` is not mounted | — | on | returns freed blocks so compaction can shrink the VHDX | — |
| A16 | Clock fix: `hwclock -s` / `chronyc makestep` **once, on detected drift** (never from cron) | §4.5 drift | `clock.maxDriftSeconds` (5) | on | one correction per event | — |
| A17 | Package-manager-native cache trims: `pnpm store prune`, `uv cache prune`, `pip cache purge`, `cargo sweep --time 30`, Gradle retention | per tool threshold | per tool | **off** (button, or opt-in) | each tool's own safe command | — |

**Volume age.** `docker volume prune` has no `until` filter, so the daemon records the first time it sees
each anonymous volume (`/var/lib/wsl-care/volume-seen.json`); A4 can then be limited to volumes unused
for ≥ `volumes.anonymousOlderThanDays` (default 0 = all unattached, the 2026-10-02 behaviour). A label
`wsl-care.keep=true` protects any volume or container from every action.

**Heavy actions wait for idle** (as WSL itself does): A1, A2 on the timer, A7, A15 run only after CPU has
been below `idle.cpuPercent` (20 %) for `idle.minutes` (5) and no `docker build` / `dotnet build|test` /
`npm ci` is running; otherwise they are deferred to the next run and the deferral is logged. The
event-driven A2 for an order-7 shortage (§4.1) is the exception — it runs at once.

**Freed bytes are measured, not estimated:** Docker's own "Total reclaimed space" for A6, A7; the
`docker system df -v` sizes of the removed objects for A4 (only the volumes `docker volume rm` confirmed, §15c #1) and A5; the folder size before/after for A8, A9.

**Never, neither automatic nor as a button:** `git worktree prune`, `docker system prune -a`,
`docker volume prune --all`, any `vm.drop_caches` value other than `1`, deleting anything under `~/git`, `wsl --shutdown`
from inside the VM, deleting anything in an AI agent's folder (A13 *moves* with hash verification — the
only write it does there), enabling `sparseVhd`, recommending `autoMemoryReclaim=gradual` with systemd +
Docker Desktop. Tests enforce that no code path can produce these commands.

Global guards: every `act` runs as root (§15c #0) — one run at a time (`flock` on `/run/wsl-care.lock`, shared by timer and buttons);
`dryRun = true` for the first 7 days for the **timer** (buttons always preview, then execute on
confirmation); each action has its own `auto` switch; a failing action is logged and the run continues.

## 6. Data, config and the CLI contract

**Runs and their records.** Every run — timer, button, or *Run full check now* — gets a `runId` (UTC
timestamp + pid) and writes:

| File | Content | Kept |
|---|---|---|
| `/var/lib/wsl-care/history.jsonl` | one summary line per run: trigger, dryRun, metrics (§4), warnings, per-action `{id, count, freedBytes}` | 90 days |
| `/var/lib/wsl-care/runs/{yyyy-MM-dd}/{runId}.json` | the full detail: **every** removed object (type, id, name, image, age, size), the preview that triggered it, before/after numbers | 90 days |
| `/var/log/wsl-care/{yyyy-MM-dd}/wsl-care-{HH-mm-ss}-{pid}.log` | the human-readable run log (UTC, one file per run) | 30 days |
| `/var/lib/wsl-care/container-starts/{yyyy-MM-dd}.jsonl` | §4.3 | 14 days |
| `%LOCALAPPDATA%\wsl-care\history.jsonl` | Windows-probe readings (§2) | 90 days |

**Config, one source of truth:** JSON (System.Text.Json source generation — AOT-safe), validated against a
schema. `default.json` (embedded) < `/etc/wsl-care/config.json` (machine) < `~/.config/wsl-care/config.json`
(user overrides, written by the extension). The daemon runs without VS Code, so the file is the truth; the
extension's settings are an editor for it (§7.5).

**CLI** — `wsl-care` (Linux, `/opt/wsl-care/bin`) and `wsl-care.exe` (Windows probe, inside the `.vsix`);
the only interface the extension uses, so no collector is implemented twice:

| Command | Output | Used by |
|---|---|---|
| `status --json` | fresh fast snapshot (< 2 s), slow parts from the last full run with their age | panel, status bar |
| `collect` | full run (timer target) | timer, *Run full check now* |
| `preview --all --json` | every §4.3 cleanup row with count and reclaimable GB, plus kept named volumes | *Cleanup* table |
| `act <A#>[,<A#>…] [--preview] --json` | per action: *what*, *count*, *freed GB*; then "Docker after" | buttons |
| `logs --period today\|yesterday\|date:YYYY-MM-DD\|range:FROM..TO --json` | runs in the period + aggregates (§7.4) | *Logs* page |
| `runs show <runId> --json` / `runs log <runId>` | one run's full detail / its raw log text | *Logs* page |
| `agents list --json` / `agents probe <path> --json` | §4.6 discovery result / what a manual CLI path resolves to | *AI agents* section, *Add CLI path* |
| `config get` / `config set <key> <value>` | validated read/write of the user override file | settings sync |
| `doctor --json` | sysstat/atop/timer/events-unit health, versions | *Health* section |
| `events follow` | the §4.3 follower | `wsl-care-events.service` |

Every JSON answer carries `schemaVersion`; the extension refuses a major version it does not know and says
so.

**Durable running state.** A running action writes `/var/lib/wsl-care/running.json` (action, pid, start,
heartbeat every 5 s) before it starts and removes it when it ends; a reader treats a heartbeat older than
30 s as dead (a pid alone can be reused). The extension derives "Cleaning…" from that file, so a VS Code
reload mid-cleanup still shows the truth and never sticks on a dead run.

## 7. The VS Code extension (`src_vs_code/`)

### 7.1 Where it runs

`extensionKind: ["ui"]` — it runs on the **Windows** side whether the window is local or *Remote – WSL*;
reaches the daemon with `wsl.exe -d <distro> -- /opt/wsl-care/bin/wsl-care …` (A1, A2, A9, A10, A11 with
`-u root`, which `wsl.exe` grants without a password) and runs the bundled `wsl-care.exe` for Windows-side
numbers.

### 7.2 What you see

**Status bar:** `WSL RAM 62 % · swap 1.2 G · / 13 %`, coloured ok / warn / alert; click opens the panel.

**Side panel "WSL Care"**, sections:

| Section | Shows |
|---|---|
| **Memory** | VM used / available / cache / inactive-anon / free of the ceiling; `vmmemWSL`; host RAM; fragmentation; sparkline of `MemAvailable` today |
| **Swap** | used / total, trend today |
| **Disk** | `/` used / free; `C:` free; `.vhdx` sizes with a "could shrink by ~X GB" hint; big folders |
| **Containers** | running now; started in the last 24 h (total + top images); stopped (Testcontainers, ≥ N days); "Docker after" totals |
| **AI agents** | one row per tracked agent (§4.6): side, size, sessions, largest session, growth; warnings; the **Add CLI path…** row |
| **Top holders** | top processes and families by RSS, top containers, the *unattributed* residue |
| **Health** | last full run, warnings since, clock jumps, journald span, sysstat/atop/timer/events state |
| **Cleanup** | the live table of the one-time cleanup: one row per A4–A9 — *what* (with the current age limit in the text), *count*, *reclaimable GB*, a checkbox and **Clean**; A1–A3, A10–A12 as further rows; **Clean selected** with the total; a *Kept* sub-table of unattached named volumes, no button; **Run full check now** |
| **Last cleanup** | the most recent run's freed table + "Docker after", who ran it and when |

The panel's title bar carries three buttons: **Logs** (§7.4), **Help** (§7.6), **Settings**.

### 7.3 Buttons

Click → `act <A#> --preview` → a modal with exactly what will be removed (counts, names for ≤ 20 items, GB)
→ **Confirm** → `act <A#>` → the result as a notification and under *Last cleanup*. **Clean selected**
previews every ticked row in one modal and runs them in the order A5 → A4 → A6 → A7 → A8 → A9 (removing
containers first frees their volumes and images — the order of the 2026-10-02 run). Actions that touch
user data or force re-downloads (A5 for non-Testcontainers, A6 unused, A8, A11, A12) need a second
confirmation that names the setting they used. While running, the buttons read *Cleaning…* (from
`running.json`, §6).

### 7.4 Logs page

Opened by **Logs** in the panel title, and by **Logs** next to every row of *Last cleanup* (opens that run).

**Period selector:** *This run* (per cleanup) · *Today* · *Yesterday* · **date picker** (a single day or a
from–to range). The period is part of the page state and survives a reload.

For the selected period:

| Block | Content |
|---|---|
| **Totals** | freed in total and **per action** (A4 volumes, A5 containers, A6 images, A7 build cache, A8 npm, A9 apt/snap, …), in GB and object counts |
| **Runs** | number of runs; **with a cleanup** (at least one action freed something) vs **without**; dry-run runs counted separately ("would have freed X GB"); timer vs button |
| **Max / min** | the run that freed the most and the least (non-zero); per metric over the period — `MemAvailable`, swap used, `/` used, `vmmemWSL`, Docker reclaimable — max and min with the time they occurred |
| **Run list** | time, trigger, dryRun, actions, freed; expand a run → **what exactly was removed**: every object with type, name/id, image, age and size (from `runs/{day}/{runId}.json`); a link to its raw run log |

Everything comes from `wsl-care logs` / `runs show` — the page computes nothing itself.

### 7.5 Settings

VS Code settings under `wslCare.*`, each mirrored to the user config file via `config set` on change
(validated by the CLI; a rejected value is reverted with its message):

- `wslCare.distro` (`Ubuntu`), `wslCare.refreshSeconds` (60), `wslCare.dryRun` (true for the first week),
  `wslCare.auto.<A#>` per action
- `wslCare.volumes.anonymousMaxCount` (100), `wslCare.volumes.anonymousMaxGb` (20)
- `wslCare.containers.stoppedOlderThanDays` (7), `wslCare.containers.testcontainersOlderThanHours` (2)
- `wslCare.images.unusedOlderThanDays` (7), `wslCare.images.unusedMaxGb` (10)
- `wslCare.buildCache.maxGb` (20), `wslCare.buildCache.olderThanDays` (7)
- `wslCare.npm.maxCacheGb` (5), `wslCare.journal.keepDays` (30), `wslCare.buildServers.idleHours` (4)
- `wslCare.processes.killEnabled` (false), `wslCare.processes.idleOlderThanHours` (8),
  `wslCare.processes.families` (`["dotnet-build-servers","testhost"]`)
- `wslCare.thresholds.memAvailableWarnPercent` (25), `…ActPercent` (15), `wslCare.thresholds.swapWarnGb` (4)
- `wslCare.aiAgents.warnGb` (5), `wslCare.aiAgents.sessionWarnMb` (500), `wslCare.aiAgents.extra` (array,
  written by *Add CLI path*)
- Display, as in coai (§7.6): `wslCare.helpLanguage` (`en`), `wslCare.uiScale` (0, −5…5),
  `wslCare.textTone` (0, −5…5) — `ConfigurationTarget.Global`, not mirrored to the daemon's config.

On startup the extension reads the file (`config get`) and, when it differs from the VS Code settings,
shows a one-time notice offering which side to keep — never silently overwriting either.

### 7.6 Help page, language, text size and brightness — as in coai

Reuse coai's design (`dew_flow_connect_other_ais/src_vs_code/src/`), module for module:

| Piece | coai source | Here |
|---|---|---|
| Help command + button in the panel title | `package.json` `coai.help` (`view/title`, `navigation@0`), `extension.ts` `registerCommand('coai.help', showHelp)` | `wslCare.help` |
| Single-instance help webview, re-rendered on language change, `enableFindWidget`, CSP with nonce | `helpPanel.ts`, `helpPage.ts` (`renderHelpHtml`, mini-markup, client-side routing, search index) | same shape |
| Articles as typed TS literals: `HelpBody { title, whatItIs, why, setup, usage, whatCanGoWrong }`; English in `helpContent.ts`, one file per language (`helpRu.ts`, `helpUk.ts`, `helpDe.ts`, `helpEs.ts`) | `helpContent.ts`, `help<Lang>.ts` | one article per section, action and setting |
| Missing translation → English with a "not translated yet" note | `bodyFor` → `{ body, fallback }` | same |
| **Stale translation** detection: an 8-hex digest of the English body stamped next to each translation; a changed English text marks the translation *stale* and shows a note; `npm run help:stamp` | coai *plan* `todo/PLAN_a_stale_translation_is_invisible.md` (not built there yet) | **built in from day one** |
| Text size: `uiScale` −5…5, 13 px base × 1.1 per step, applied as `font-size` on `body` | `zoomControl.ts`, `uiScaleHost.ts` | `wslCare.uiScale` |
| Brightness ("tone"): `textTone` −5…5, 8 % `color-mix` per step towards a warm or away colour, CSS variables, light-theme switch | `textTone.ts`, `textToneHost.ts` | `wslCare.textTone` |
| Setting writes report their failure to the user | `settingWrite.ts` | same |
| Escaping / JSON-in-script helpers | `webviewHtml.ts` (`escapeHtml`, `jsonForScript`) | same |

Scope of translation, as in coai: help articles in five languages; the rest of the UI in English. Zoom and
tone apply to **every** page — panel, logs, help — from one value.

**Reuse note.** These modules are copied from coai (MIT, same author) with a header naming the source
commit. Two copies will drift; extracting them into a shared npm package is §13 Q1, not this plan.

### 7.7 First run and install

If `wsl-care` is missing in the distro, the panel shows **Install daemon** → runs the released `install.sh`
in a VS Code terminal (an apt password prompt, never stored): the binary, `sysstat` + `atop` (10-minute
intervals), both units, the default config. **Uninstall** removes units and `/opt/wsl-care`, keeps history
unless asked.

## 8. Daemon implementation (`src_daemon/`)

| Piece | Choice |
|---|---|
| Runtime | .NET 10, `PublishAot=true`, `StripSymbols=true`, `InvariantGlobalization=true`, `JsonSerializerIsReflectionEnabledByDefault=false` — as `CredsCli.csproj`; **zero** AOT/trim warnings (`TreatWarningsAsErrors`) |
| Projects | `WslCare.Core` (collectors, rules, actions, records — no I/O behind interfaces it does not own), `WslCare.Cli` (the AOT executable, command routing), `WslCare.Core.Tests`, `WslCare.Cli.Tests` |
| Process calls | one `ICommandRunner` (argument arrays, timeouts, captured output); tests swap it for a recorder — the only place a process starts |
| Platform split | `IHostProbe` with `LinuxProbe` (`/proc`, systemd, Docker) and `WindowsProbe` (`vmmemWSL`, host RAM, Windows AI-agent folders); one binary per RID |
| JSON | System.Text.Json source-generated contexts for config, records and CLI output |
| Logging | Serilog per the family logging rule: coloured console (journald under systemd) + one file per run at `/var/log/wsl-care/{day}/…`, UTC; configured in code (no reflection-based settings reader under AOT), levels from the JSON config |
| Units | `wsl-care.service` (`Type=oneshot`, root, `Nice=19`, `IOSchedulingClass=idle`, `MemoryMax=256M`, `TimeoutStartSec=10min`); `wsl-care.timer` (`OnBootSec=20min`, `OnUnitActiveSec=4h`, `AccuracySec=5min` — monotonic: the VM is off every night); `wsl-care-events.service` |

## 9. CI/CD — by analogy with CredsForDevs

Mirror `dew_flow_creds_for_devs` (`.github/`, root build files, `install.sh`); differences only where this
repo is smaller.

**Repository basics.** `Directory.Build.props` (`net10.0`, `Nullable`, `TreatWarningsAsErrors`,
`ManagePackageVersionsCentrally`, `InvariantGlobalization`), `Directory.Packages.props` (central versions,
a reason comment on every pin; `xunit.v3`, FluentAssertions held at 7.x for its licence),
`global.json` (`10.0.100`, `rollForward: latestFeature`, `test.runner: Microsoft.Testing.Platform`),
`Directory.Build.rsp` (`-nr:false`), `nuget.config` (`<clear/>` + nuget.org). Tests are xUnit v3 MTP
**executables** — never `dotnet test`.

| Workflow | Copies | Does |
|---|---|---|
| `ci-daemon.yml` | `ci-clients.yml` + `ci-server.yml` | push/PR on `main`, path filter `src_daemon/**`; matrix `ubuntu-latest` + `windows-latest`: `dotnet format --verify-no-changes` → build → test executables → `dotnet publish -r <rid>` (AOT) → smoke `wsl-care --help` |
| `ci-extension.yml` | `ci-extension.yml` | `npm ci` → typecheck → lint → test → `vsce package` → artifact (14 days) |
| `release-please.yml` | same | `workflow_dispatch`; token minted by a GitHub App (tags made with `GITHUB_TOKEN` trigger nothing) |
| `release.yml` | same | tags `daemon-v*`, `extension-v*`. **daemon:** native runners per RID (AOT does not cross-compile) — `linux-x64`, `linux-arm64` (`ubuntu-24.04-arm`), `win-x64` (the probe); tests → publish → smoke → `wsl-care-$VERSION-$RID.tar.gz` / `.zip` + `.sha256` → uploaded to the release-please **draft** → "every RID has an asset" check → publish. **extension:** tag must equal `package.json` version; the `win-x64` probe is downloaded from the matching daemon release and bundled; `vsce package` once → `vsce publish --packagePath` (`VSCE_PAT`) → `.vsix` on the release |
| `pr-title.yml` | same | conventional-commit PR titles |
| `coderabbit-review.yml` + `.coderabbit.yaml` | same | `language: ru-RU`, `profile: chill` |
| `sonarcloud.yml` | same | `dotnet-coverage` over the test executables + `c8` for the extension |
| `ci-workflows` job | `ci-server.yml` actionlint job | actionlint pinned by version + SHA-256, shellcheck present |
| `.github/dependabot.yml` | same | nuget, npm (`/src_vs_code`), github-actions; weekly; FluentAssertions major, `@types/vscode` major+minor ignored |
| `.github/branch-protection.json` + `scripts/branch-protection.mjs`, `.github/tag-ruleset.json` | same | required job names, linear history, conversation resolution; tag ruleset on `daemon-v*`/`extension-v*` with only the GitHub App as bypass; CodeQL via default setup |

`release-please-config.json`: `separate-pull-requests`, `include-component-in-tag`, `tag-separator: "-"`,
`draft: true`, `force-tag-creation: true` (without it a draft cuts no tag and `release.yml` never runs),
`exclude-paths: [".github"]`; packages `src_daemon` → component `daemon` (`simple`), `src_vs_code` →
`extension` (`node`). Every `uses:` pinned by SHA, `permissions: contents: read`, `persist-credentials:
false`, `concurrency` with cancel-in-progress, `timeout-minutes` everywhere.

**`install.sh`** (as CredsForDevs': POSIX `sh`, `curl … | sh`): detect `linux-x64`/`linux-arm64`, find the
newest `daemon-v*` through the releases API (not `releases/latest`, which is the `.vsix`), download the
tarball **and its `.sha256` and verify** (abort on mismatch), install to `/opt/wsl-care/bin` with
`install -m 0755`, then — the part CredsForDevs does not need — install `sysstat` + `atop`, the units and
the default config, `systemctl enable --now wsl-care.timer wsl-care-events.service`, and smoke
`wsl-care doctor`.

Secrets to create: `RELEASE_PLEASE_APP_ID`, `RELEASE_PLEASE_APP_PRIVATE_KEY`, `VSCE_PAT`, `SONAR_TOKEN`.

## 10. Repository layout

```
src_daemon/
  src/WslCare.Core/  Collectors/ Rules/ Actions/ Records/ Agents/agents.json
  src/WslCare.Cli/   Program.cs Commands/  (AOT executable: wsl-care / wsl-care.exe)
  tests/             WslCare.Core.Tests/ WslCare.Cli.Tests/ fixtures/ (captured from this machine)
  config/default.json  systemd/ (wsl-care.service, .timer, wsl-care-events.service)
src_vs_code/
  src/   extension.ts client/WslCareClient.ts views/ logs/ help/ (helpContent, help<Lang>) zoom/ tone/ settings/
  media/ test/
.github/  workflows/ dependabot.yml branch-protection.json tag-ruleset.json scripts/
install.sh  release-please-config.json  .release-please-manifest.json
Directory.Build.props  Directory.Packages.props  global.json  nuget.config  wsl_care.slnx
research/  todo/
```

## 11. Build order

1. Root build files, `wsl_care.slnx`, `ci-daemon.yml` + `ci-workflows` job on an empty Core/Cli/tests
   skeleton — CI green before any feature.
2. Config (JSON + schema, three layers), `ICommandRunner`, records; collectors §4.1–4.5;
   `status --json` / `collect`; `events follow`.
3. Rules; history, run detail files, run log, Serilog.
4. Actions A1–A17 with `--preview`; the never-list guard; `running.json` with heartbeat; `logs` / `runs`.
5. AI-agent catalogue, discovery, `agents list` / `agents probe`; the Windows probe build.
6. `install.sh`, units; **observe only** first.
7. Extension: `WslCareClient`, status bar, panel sections read-only, `ci-extension.yml`.
8. Extension: cleanup buttons, *Last cleanup*, *Logs* page.
9. Extension: settings ↔ config sync, *Add CLI path*, help page + zoom + tone + stale-translation stamps.
10. Release pipeline: release-please, `release.yml`, tag ruleset, branch protection, dependabot, Sonar,
    CodeRabbit; first `daemon-v0.1.0` + `extension-v0.1.0` — the **first working prototype is published to the VS Code Marketplace right away** (user decision 2026-10-02): publisher id, `VSCE_PAT`, icon, README, `CHANGELOG.md`, `engines.vscode` aligned with `@types/vscode`.
11. One week of timer `dryRun`; review in `research/`, tune, switch `dryRun` off action by action.

## 12. Test plan

**Daemon** (xUnit v3 MTP executables, FluentAssertions 7.x, fixtures captured from this machine):
- Parsers: `/proc/meminfo`, `/proc/buddyinfo`, `/proc/pressure/memory`, `ps`, `docker … --format json`.
- The 2026-10-01 18:36 state (free 0.27 GB, no order ≥ 4 blocks) → `alert` + A1 + A2; a fresh-boot
  fixture → `ok`, no action.
- Each threshold and each age setting at its edge.
- Cleanup rows from the 2026-10-02 fixtures: A4 counts 387 volumes / 59.6 GB and excludes the 7 named
  dangling ones; A5 at 7 days selects the 58 containers and leaves the 18 younger ones; A6 never lists an
  image a stopped container uses; A7 with only the age filter selects ~0 GB while the size cap selects the
  cache; A9 lists only disabled snap revisions; A4 refuses on Docker 22.
- Actions against the recording `ICommandRunner`: exactly which commands run; a property test that no input
  makes any action emit a *never* command; A11 never targets a process with a TTY or recent CPU.
- Guards: `dryRun` emits nothing; a second run exits on the lock; a failing action does not stop the run; a
  `running.json` with a stale heartbeat is treated as dead.
- Logs: `logs --period` for today / yesterday / a date / a range across midnight UTC vs local time; runs
  with vs without cleanup; dry-run runs counted apart; max/min freed and max/min per metric with times; the
  run detail lists every removed object.
- AI agents: discovery over a fake home tree (each catalogue entry found by binary, by npm package, by
  folder alone); session counting per layout; `agents probe` on a manual path finds conventional folders;
  a non-executable or missing path is rejected with a message.
- Config: merge order; `config set` rejects out-of-range values.
- AOT: the CI publish step plus a smoke run of the published binary — a reflection path that works under
  JIT but breaks under AOT fails here, not on the user's machine.

**Extension** (as coai/CredsForDevs):
- `WslCareClient` against a fake `wsl.exe` / `wsl-care.exe` printing recorded JSON: parse, timeout, non-zero
  exit, unknown `schemaVersion`.
- View-model functions (bytes → "12.3 GB", colours, button state from `running.json`, period labels).
- Logs page: the period selector produces the right `logs --period` argument and survives a reload.
- Help, ported from coai's tests: every command and setting has an article; every article in every
  language; translations differ from English; a stale digest is reported; fallback note shown.
- Zoom/tone: every page that has zoom also has tone; values clamp to −5…5; the host pushes the value.
- Page scripts are **run** against a synthetic document, not matched as text (coai's
  `panelStorageScript.test.ts` pattern).
- Integration (`@vscode/test-electron`): the extension activates, the status bar item appears, the panel
  renders against the fake client.

**Live smoke on this machine:** `wsl-care status --json`; one timer run and its log; each button's preview
against the real Docker; A1 for real with before/after `MemAvailable`; `agents list` on both sides matches
the §4.6 numbers.

## 13. Open questions

1. ~~Copy coai's modules or extract a shared package?~~ → proposed in
   [PLAN_shared_vscode_kit.md](PLAN_shared_vscode_kit.md) (new repo + public npm package); awaits the
   user's go-ahead and its own open questions (scope name, live regions).
2. ~~AI-agent data cleanup?~~ → **decided 2026-10-02: move, never delete** —
   [PLAN_ai_session_archive.md](PLAN_ai_session_archive.md).
3. Windows toast notifications on `alert` — on by default, or only the status-bar colour?
4. Should A1 also run on a short timer (every 30 min)? `autoMemoryReclaim` (0.1) may make it unnecessary.
5. Which process families belong in the A11 allowlist by default — decide from one week of §4.2 data.
6. Install the daemon also into `Ubuntu-26.04`, or stop that distro when idle (0.6)?
7. ~~Marketplace from the start?~~ → **decided 2026-10-02: first working prototype, then publish at once**
   (§11 step 10).
8. Verify on this machine that `autoMemoryReclaim` really defaults to `dropCache` on WSL 2.7.10 (the survey
   cites 2.1.3 release notes) — it decides whether A1 duplicates WSL's own idle reclaim.

## 15. Gate round 1 — amendments (2026-10-02)

`review_plan` over the three wsl_care plans together (session `572b5534`, both reviewers answered, 17
findings, verdict `good_enough`). Sixteen accepted, one rejected with evidence. Each amendment below
OVERRIDES the section it names.

| # | Section | Amendment |
|---|---|---|
| 0 | §5 A4/A5 | Order fixed A5 → A4; the first-seen record (`volume-seen.json`) is keyed by volume name and drops names Docker no longer lists. |
| 1 | §7.1 | Root is reached only through an **argv allowlist** built in code — `wsl-care act` with any action id the registry knows (every `act` runs as root, §15c #0) and an optional `--preview`, and `wsl-care collect` for *Run full check now* (§15b #3) — never a shell string, never user text. Any process of this Windows user can already run `wsl -u root`; the boundary we own is what we pass. |
| 4 | §5 A4 | Inline above: `docker volume rm` of the preview ∩ still-unattached ∩ unlabelled ∩ old-enough set. |
| 5 | §7.1 | `wsl -u root` without a password is verified, not assumed: `doctor` runs `wsl.exe -u root -- true`; if refused, root actions show as *needs root* and are never attempted. |
| 6 | §6 | `running.json` records pid + process start time + heartbeat. A stale heartbeat on a LIVE matching process is reported as **wedged**: no new run starts, nothing is killed automatically, a button offers to stop it. Only a dead or mismatched pid is swept. |
| 8 | §1 | Inline above: the success criterion's history window. |
| 9 | §4.3 | The events follower writes start/stop/gap markers and backfills from `docker events --since` its last marker (bounded) when it starts; a 24-hour count that overlaps a gap is shown as **partial**, with the gap. |
| 10 | §5 A16 | Clock fix needs two consecutive observations ≥ 5 minutes apart above the threshold, at most one correction per hour, and is skipped when timesyncd/chrony reports synchronised. |
| 12 | §9 | `release.yml` adds build-provenance attestations (`actions/attest-build-provenance`); `install.sh` verifies the tarball with `gh attestation verify --repo oleksandrdubyna88/wsl_care` and refuses without it unless `--skip-attestation` is passed (printed loudly). The `.sha256` is integrity, not authentication. |
| 14 | §12 | A **`WslCare.Scenarios`** project drives the BUILT CLI end to end over a fixture home with fake `docker`/`systemctl` executables on `PATH`; `research/module_tests.md` lists every CLI verb as a flow. |
| 15 | §8, §9 | **Support matrix:** .NET SDK `10.0.100` `latestFeature` (`global.json`); `linux-x64`/`linux-arm64` AOT built on ubuntu-24.04 → glibc 2.39, so Ubuntu 24.04 and 26.04 are supported and older ones are not; `win-x64` on `windows-latest` for Windows 11; the extension declares `engines.vscode ^1.85.0` with `@types/vscode` held at that floor. |
| 3 | (Windows plan) | **Rejected:** "pool tags need admin". Measured 2026-10-02 unelevated: status 0, 3 350 tags. A failing call falls back to pool totals with the reason. |

Findings 2, 7, 16 amend the archive plan and 11, 13 the Windows plan — see their own amendment sections.

### 15a. Gate — epic 1 plan round (2026-10-02)

| # | Lands in | Decision |
|---|---|---|
| 0 | E1.S2 (record), E3.S1 (sweep) | **Accepted.** No persisted list is trusted across a crash: every action computes its targets from live state at run time (A4 re-lists, §15 #4; the archive reconciles, E9), and is idempotent per target — a volume already gone counts as *already gone*, not a failure. The startup sweep that removes a dead or mismatched `running.json` also writes a run record with outcome `interrupted` (action, start, last heartbeat), so history never shows a run that silently vanished. E1.S2's run-record type carries `interrupted` from the start. |
| 1 | E1.S2 | **Accepted, changed.** An unreadable or schema-invalid layer never stops the daemon, and is never silently replaced by defaults either — a default can re-enable an action the user switched off. The run degrades to **observe-only** (collect and report, no `act`), `status`/`doctor` carry `configError {file, line, message}`, the panel shows it with the file path, and `config set` / `config reset <key>` still rewrite the broken user layer. |
| 2 | — | **Rejected:** already covered by §15 #6 — `running.json` records pid **and process start time**; a reused pid has a different start time, so it is *mismatched* and swept, never *wedged*. |
| 3 | E4.S1 | **Accepted.** `install.sh` verifies each side effect before declaring success — `sar` and `atop` on PATH, `systemctl is-active` for the timer and the events unit, `wsl-care doctor --json` healthy — and exits non-zero naming the step that failed. |

**Cadence consultation for epics 1–3** (local Gemma 4, verified before acting):

| # | Lands in | Decision |
|---|---|---|
| C1 | E1.S2 | **Accepted.** A never-list guarded only at `ICommandRunner`/`CommandPolicy` misses deletions that are not commands (the archive's moves, E9; `%TEMP%` cleanup W-A2, E12). E1.S2 adds an `IFileSystem` seam whose every delete and move passes ONE `DeletionPolicy` — never under an AI agent's folder except the archive's verified move, never under `~/git`, never `%TEMP%\claude\`, never outside the action's declared root — and an architecture test that fails on `File.Delete`, `File.Move`, `Directory.Delete`, `Directory.Move` or `Process.Start` anywhere outside the two seams, with a companion proving the scan matches a planted instance. |
| C2 | E2 | **Accepted for E2.** Fake executables prove invocation, not the real tools' output contract: E2 adds a live contract check against the real Docker/systemd here (skipped in CI with an explicit reason, required at release); parser fixtures stay recorded from real output (the 2026-10-02 captures). |
| C3 | E1.S2 | **Rejected in part:** platform paths stay in E1.S2 because E2's Windows probe already runs the `win-x64` build — but behind an `IHostPaths` interface with a Linux and a Windows implementation, never as literals in shared code. |

### 15b. Gate — epic 2 plan round (2026-10-02, session `714367be`)

| # | Lands in | Decision |
|---|---|---|
| 0 | E2.S3 | **Accepted.** The backfill window is bounded at 24 h before the last marker (`docker events --since <marker> --until <now>`). Docker keeps its event buffer in memory only, so a gap it cannot fill — the daemon restarted, or the outage is older than the window — is recorded as ONE `unrecoverable` gap marker with its start and end; every 24-hour count overlapping it is `partial` with the gap named, and counts become complete again only once a whole 24 h lies after the gap's end. **Amended after the E2 code round (gate finding #2/#7/#9):** an empty or short buffer is NOT by itself proof of loss — E2.S3's rule (filled only when the oldest buffered event reaches the marker) wrote a false gap for every idle engine and every host sleep. The continuity rule: Docker's buffer is a ring of 256 events (moby `eventsLimit`; measured full answers 248–255), complete from its oldest event and, while not full, from the engine's start; a gap is unrecoverable only when the buffer is FULL (≥ 240) and its oldest event is newer than the marker, or the ENGINE RESTARTED since the marker — read-only, from the default `bridge` network the engine re-creates at every start (`docker network inspect bridge`: id + creation), recorded on each `covered` marker. An unreadable engine mark falls back to the oldest-event rule. |
| 1 | E2.S3 | **Accepted.** Write order per run: the run detail `runs/{day}/{runId}.json` first (temp + rename), then the `history.jsonl` line that names it, then the run log is closed. A failed write makes the run `failed` with the reason, never a silent success. Startup reconciles: a history line whose detail is missing is shown as *detail lost*; a detail with no history line gets a history line with outcome `interrupted` (§15a #0). Retention sweeps remove a detail only after its history line has aged out, both through `IFileSystem`. |
| 2 | E2.S2 | **Accepted.** The live contract check runs every real command through `ICommandRunner` with a 30 s ceiling and tree kill. Locally, a missing daemon (no `docker`, no systemd) is an explicit skip with the reason; in CI it is skipped with that reason; at release (`daemon-v*`) a skip is a FAILURE — the release checklist requires a run on this machine. |
| 3 | §6, E2.S3, E6.S1 | **Accepted.** State under `/var/lib/wsl-care` is written by root only. An unprivileged process never writes it: `status` and `preview` only read it (files 0644; since the E2 code round also for a privileged `preview` — gate finding #3/#6/#10 — and `status` reads the follower's 24-hour summary, never the raw day files — finding #8); `collect` run unprivileged measures and prints but writes nothing and says so (*read-only: run as root to record*); the panel's *Run full check now* reaches root through the argv allowlist, which gains `wsl-care collect` (§15 #1). Run logs of an unprivileged run go to `$XDG_STATE_HOME/wsl-care/logs`. Tested with the state directory made unwritable. |
| 4 | §4.2, E2.S1 | **Accepted.** Process memory is `RssAnon` + `RssShmem` from `/proc/[pid]/status`, not total RSS; container processes are identified by cgroup and counted once, through the container's cgroup, never again as Ubuntu processes. A negative remainder is an *inconsistent sample*, not a number. |
| 5 | §6, E2.S1 | **Accepted.** `status --json` launches no slow process: `docker stats` and the Windows clock (`powershell.exe Get-Date`) are sampled only in `collect`, and `status` returns them from the last full run WITH their age. The clock-skew check measures the launch latency and subtracts it, and needs two observations (§15) before it reports drift. |
| 6 | §12, E2.S2 | **Accepted.** `src_daemon/tests/WslCare.LiveContract` (a separate executable, not in the default test run): the real `docker system df -v`, `docker volume ls`, `docker ps -a`, `docker events --since`, `systemctl show`, `journalctl --disk-usage` parsed by the product's own parsers; their outputs become the fixtures the scenario fakes replay (C2). |
| 7 | §4.3, §6, E2.S2 | **Accepted.** Every Docker figure carries `available: false` with the reason (daemon stopped, socket refused, timeout) when Docker cannot answer; the panel shows *Docker unavailable*, never 0 GB. Same rule for systemd/journal figures. |
| 8 | E2.S3 | **Accepted.** `events follow` waits for the Docker socket inside the process with backoff (5 s doubling to 5 min) and writes ONE gap marker per outage, not one per restart; it exits only on a signal or an unexpected error, so `Restart=always` stops cycling while Docker is merely down. |

### 15c. Gate — epic 3 plan round (2026-10-02, session `a90e342d`)

| # | Lands in | Decision |
|---|---|---|
| 0 | §5, §15 #1, E3.S1, E6.S1 | **Accepted.** One execution context: **every `act` runs as root** — the timer is root, and a button reaches root through the argv allowlist, which now holds `wsl-care act <id> [--preview]` for EVERY id the action registry knows (A15 and A16 included), built in code from that closed registry, never from user text. `act` started unprivileged refuses the whole run with *needs root* before taking the lock or touching state — never half a run. The lock (`/run/wsl-care.lock`), `running.json` and history stay root-owned (§15b #3). |
| 1 | §5 A4, E3.S2 | **Accepted.** `docker volume rm` prints only names, so A4's freed bytes are the `docker system df -v` sizes (read just before removal) of exactly the volumes `docker volume rm` confirmed removed; a volume it did not confirm counts nothing. Docker's totals before/after are recorded beside it as a cross-check, never as the figure. |
| 2 | §5 (A3, A8, A12, A14, A17; the A8/A9 walks; protected roots), E3.S1 | **Accepted.** The **target user** is discovered once per run: the `[user] default=` of `/etc/wsl.conf`; else the single account with uid ≥ 1000 and a login shell in `/etc/passwd`; anything ambiguous makes every user-scoped action refuse with the reason (machine-scoped actions still run). A user-scoped action runs its tool as that user through `runuser -u <user> -- <exe> <args…>` (argv, a clean environment with that user's `HOME`, `USER` and a PATH built from a fixed list of the user's bin folders — `~/.nvm/versions/node/<default>/bin`, `~/.local/bin`, `~/.cargo/bin`, `/usr/local/bin`, `/usr/bin`), and the executable is resolved against that list before the start. This also settles E2.S3's open question for E4.S1: folder walks and the DeletionPolicy's protected roots (`~/git`, AI-agent folders) use the TARGET user's home, never root's. |
| 3 | §5 (A1, A2, A8), E3.S1–S3 | **Accepted.** No shell anywhere: A1 is `/bin/sync` then `sysctl -w vm.drop_caches=1`, A2 is `sysctl -w vm.compact_memory=1`, A8 is `npm cache clean --force` resolved per #2. The never-list forbids any `vm.drop_caches` value other than 1 and any `sh -c` / `bash -c` / `bash -ic`; the CommandPolicy property test covers both. |

## 16. Epics and stories (split 2026-10-02, on Fable, as the gate's operator commands require)

Every epic is its own branch from the previous epic's final commit, one review-gate code round over its
whole diff, CI green at its head. Every story ships its scenario flows and updates
`research/module_tests.md` and `research/architecture.md`. Outside the epics, by hand with the owner:
Phase 0 (0.1, 0.4–0.8) before E4's live install; the Windows §5 elevated diagnosis before E12; the dryRun
week and this plan's promotion after E13.

| # | Name | Branch | True when done |
|---|---|---|---|
| E1 | Skeleton, foundation seams, daemon CI, scenario harness | `feat/wc-e1-skeleton` | `dotnet build wsl_care.slnx` green on ubuntu+windows; test executables run; AOT `linux-x64`/`win-x64` with zero trim warnings + `--help` smoke; `WslCare.Scenarios` drives the built CLI; a test fails when a CLI verb is missing from `module_tests.md` |
| E2 | Linux collectors, `status`/`collect`/`preview`/`doctor`, events follower | `feat/wc-e2-collectors` | `status --json` < 2 s with `schemaVersion`; `collect` writes history, run detail, run log; `preview --all --json` reproduces the 2026-10-02 rows from fixtures; follower markers + backfill; `doctor --json` |
| E3 | Action engine, A1–A17, never-list guard, `logs`/`runs` | `feat/wc-e3-actions` | every `act A#` behind its `auto` switch; property test: no input yields a *never* command; lock, 7-day dryRun, wedged/dead `running.json`; `logs --period` / `runs` answer §7.4 |
| E4 | Units, `install.sh`, release pipeline, `daemon-v0.1.0` | `feat/wc-e4-daemon-release` | `daemon-v0.1.0` with 3 RID assets + `.sha256` + attestations; installed here, timer + events unit active, `doctor` green |
| E5 | Extension prototype (read-only) + **Marketplace** `extension-v0.1.0` | `feat/wc-e5-extension-prototype` | `ci-extension.yml` green; `.vsix` bundles `wsl-care.exe`; status bar + read-only panel; *Install daemon*; listed on the Marketplace |
| E6 | Cleanup buttons, root boundary, Last cleanup, Logs page | `feat/wc-e6-cleanup-logs` | preview → confirm → result for every A#; root only through the argv allowlist; Logs page = §7.4; `extension-v0.2.0` |
| E7 | AI-agent discovery, settings ↔ config, Add CLI path | `feat/wc-e7-agents-settings` | `agents list` matches §4.6 on both sides; *Add CLI path…* end to end; settings mirrored with the one-time conflict notice; `extension-v0.3.0` |
| E8 | Help in 5 languages, zoom, tone — via the kit | `feat/wc-e8-help-kit` | the kit imported, no copied coai modules; articles in en/ru/uk/de/es with fallback + stale notes; zoom/tone on every page; `extension-v0.4.0` |
| E9 | AI-session archive — daemon, both sides | `feat/wc-e9-archive-daemon` | `archive preview\|run\|restore\|list` on both sides; never-move property tests; index-before-delete + reconcile; live round trip byte-identical; A13 in the timer |
| E10 | Archive in the extension | `feat/wc-e10-archive-ui` | settings with folder picker; Archive page with restore; Logs show A13; `extension-v0.5.0` |
| E11 | Windows collectors, `install` (task + logman) | `feat/wc-e11-windows-collectors` | `status`/`collect`/`doctor`/`install` on Windows; the task and perf log running here; pool tags with fallback |
| E12 | Windows actions, elevated channel, advisors with undo | `feat/wc-e12-windows-actions` | W-A1…W-A14 with preview; elevated request → result round trip; advisors/undo round-trip exactly; Windows never-list property test |
| E13 | Extension Windows group | `feat/wc-e13-windows-ui` | Windows sections; side column in Cleanup/Logs/AI agents; shield actions through the channel; `.wslconfig` cap shown, never written; `extension-v0.6.0` |

**Stories and their models** (Fable where a wrong answer is paid for later; Opus otherwise):

| Story | Content | Model |
|---|---|---|
| E1.S1 | root build files, `wsl_care.slnx`, Core/Cli + test projects, `ci-daemon.yml` (matrix, format, build, test exes, AOT publish, smoke), actionlint/shellcheck, pr-title, dependabot | Opus — mirrors CredsForDevs |
| E1.S2 | config layering + schema with observe-only on an invalid layer (§15a #1), `config get/set/reset`, `ICommandRunner` (argv, ceilings, tree kill) + recording double, `IFileSystem` + `DeletionPolicy` + architecture test (§15a C1), `IHostPaths` (C3), `IHostProbe`, run records incl. `interrupted`, Serilog. **Built 2026-10-02; deviations:** `processes.killEnabled` dropped (duplicate of `auto.A11`; A5/A6 carry two switches each per §5); `aiAgents.extra` deferred to E7 (object shape); the schema lives in code (`ConfigKeys`) with defaults in the embedded `default.json`, no separate schema file; the run-file sink writes with its own `StreamWriter`, not `Serilog.Sinks.File` (one package); `WslCare.Core` has zero packages and no `ILogger<T>` yet; console logs go to **stderr** (stdout carries answers); `--help`/`--version`/usage refusals open no log file; a `RootTooBroad` rule (root = `/`, drive root or home) was added; `/tmp/claude` is the Linux analogue of `%TEMP%\claude\`; only `history.jsonl` is written (the per-run detail file is E2.S3); a `WslCare.TestSupport` project holds the shared doubles; `WSL_CARE_ROOT` sandboxes every path for tests | **Fable** — the seams every later story hangs on |
| E1.S3 | `WslCare.Scenarios` (built CLI, temp home, fake docker/systemctl/journalctl on PATH), fixtures, derived verb register. **Built 2026-10-02; deviations:** the fakes are ONE C# console project (`WslCare.FakeTool`) whose apphost is copied under each tool's name, not scripts — a `docker.cmd` is not found when a program starts a bare `docker` on Windows; the scenario `PATH` holds the fakes and nothing else, so no real tool is reachable; `fixtures/` holds one SYNTHETIC self-test file only — real tool captures are E2's (§15a C2), none invented here; the built-binary launcher moved to `WslCare.TestSupport/ChildProcess` (shared with `BuiltBinaryTests`), with UTF-8 streams; the first scenario run found that a refusal is one `wsl-care:` MESSAGE but not one stderr LINE — the console log writes its request line to stderr too — so the scenarios classify stderr and the README now says so; the flow catalogue row format is fixed: first cell starts with `` `wsl-care <usage>` `` as `CommandLine.Commands` spells it; beyond the story: `ci-daemon.yml` gained the `linux-arm64` leg on `ubuntu-24.04-arm` (the family platform rule; E1's done-line names only linux-x64/win-x64) and the AOT config round-trip smoke; the family checks run in a new `family-checks.yml` (the four tools; ROLLOUT.md's `rules.mjs check` and `gate-snippet-check` are not wired); the CLI's `HOME`/`USERPROFILE` are not redirected in a scenario — isolation rests on `WSL_CARE_ROOT` | Opus |
| E2.S1 | memory/process/disk collectors (RssAnon + RssShmem, cgroup dedup — §15b #4), Linux + minimal Windows probe, `status --json` with no slow process (§15b #5). **Built 2026-10-02; deviations:** the container figure SUBTRACTED in the unattributed arithmetic is the cgroup's `memory.stat` anon + shmem, not `memory.current` (both are reported) — measured: mixing `memory.current` (page cache + kernel memory included) with AnonPages + Shmem made the live remainder −1.05 GiB, like for like +0.46 GiB, and the captured fixture reproduces it; the clock tick and page size come from `/proc/self/auxv`, not a native `sysconf`; command lines are shown with secret-looking values redacted (measured: `--connection-token=`, `--csrf_token=` in plain argv) — not in the plan; the §4.2 families are a built-in catalogue, regex settings wait for E7's object-shaped keys; every counted process is kept (`ProcessSnapshot.All`) for A11, the report shows the top 30; the minimal Windows probe is host RAM (`GlobalMemoryStatusEx`, hence `AllowUnsafeBlocks` for the generated stub), the system drive and `vmmemWSL`'s working set — host `C:` is the Windows binary's, never read through `/mnt/c`; the `.vhdx` sizes are named in the answer and unavailable until E11; the daily folder sizes and `.vhdx` growth of §4.4 are full-run figures (E2.S3); `RunRecord` gained an optional `slow` part (`containerStats`, `windowsClock`) that `status` reads back with its age — its writers are E2.S2/E2.S3; `status` without `--json` prints a short ASCII summary; `IFileSystem` gained `ReadLink` and `MeasureVolume`, `LinuxHostPaths` the proc / cgroup / filesystem roots and `/etc/passwd` (all under `WSL_CARE_ROOT` when sandboxed), `WindowsHostPaths` the system drive; the harness fakes `powershell` too (`powershell.exe` on Linux); the Linux scenario over the procfs tree runs on the Linux legs and is skipped on Windows (run by hand in WSL from a `/tmp` copy on 2026-10-02); the 2 s budget is held by the second start after a warm-up; `ci-daemon.yml` gained a `status --json` smoke of the published binary | Opus |
| E2.S2 | Docker collectors with `available: false` (§15b #7), `volume-seen.json`, hygiene audit, `preview --all --json`, `WslCare.LiveContract` (§15b #2, #6). **Built 2026-10-02; deviations:** every docker argv is built in ONE place (`DockerCommands`, read verbs only, a ceiling each: 10 s for the `version` probe, 30 s listings, 2 min `system df`), run through `ICommandRunner` as a named `ToolCommand`; the tool's FILE is the product's decision too — a bare name is resolved on `PATH` alone and started by its full path (`ExecutableResolver`, added after CI run 37045304356 found GitHub's `System32\docker.exe` answering every Windows scenario ahead of the fake on `PATH`, because the operating system searches System32 and the current directory first); the failure kinds are `notInstalled`, `daemonStopped`, `socketRefused`, `timedOut`, `commandFailed`, `refused`, `unparseable`, classified from Docker 29.6.1's own stderr (measured on Linux and Windows); containers are read from `system df -v` + a `container inspect` TEMPLATE that names its fields (no environment, command or bind source ever leaves the docker CLI) — `docker ps -a` is parsed only by the live contract; the rows are named by their `auto` switch (`A5Testcontainers`, `A6Unused` are rows), and A6 also excludes every image id an inspected container uses whatever Docker's count says; `volume-seen.json` records the first sighting UNATTACHED and drops a name attached again (age = "unused for"); privilege is the OS's answer — the write is attempted, root succeeds on the installed `0755` state directory, an unprivileged run reports `read-only` and writes nothing; ~~a root `preview` also records~~ (withdrawn after the E2 code round, gate finding #3/#6/#10: `preview` never writes `volume-seen.json`, whatever its privilege — only `collect` records first sightings); A8/A9 are rows marked `available: false` until the full run measures the folders (E2.S3); the builder-GC figure is the Windows binary's (`%USERPROFILE%\.docker\daemon.json`) and unavailable inside the distro until E2.S3 resolves the Windows profile; under Docker Desktop container log sizes are unavailable (the logs are in its VM); `IFileSystem` gained `FileSize`, `IHostPaths` `DockerDesktopConfigFile` and `DistroPath`; the `systemctl show` / `journalctl --disk-usage` / `docker events` parsers ship here for the live contract, their collectors are E2.S3's. Found live: `$m.Name` in an inspect template fails the whole command on a bind mount (fixed with `index`); `container inspect` exits 1 for a container removed since the listing (now read for the rest); Docker's `system df` / `ps` fail transiently ("snapshotter.Usage failed … lstat") while another session creates containers; Docker's event buffer held only 255 healthcheck `exec_*` events, no start of the last 24 h — the follower's backfill must expect that. The fixtures are the AFTERNOON Docker (after the one-time cleanup): the morning's 387-volume / 59.6 GB rows had no saved raw output and are not reproduced | Opus |
| E2.S3 | thresholds, health collectors, run detail → history write order + startup reconcile (§15b #1), retention sweeps, `collect` (read-only when unprivileged, §15b #3), `doctor`, `events follow` with bounded backfill and in-process backoff (§15b #0, #8). **Built 2026-10-02; deviations:** thresholds without a `ConfigKeys` setting are named constants in `ThresholdRules` (plan §4's starting points; keys with E7 if the dryRun week asks for tuning); an unread figure is a fourth level, `unknown`; the `.wslconfig` row SHOWS `memory=36GB` and is red above 90 % of the VM's ceiling (inside the VM `MemTotal` is the ceiling) — the file is only read, through the Windows profile the clock probe prints (`/etc/wsl.conf` automount root), which also gives the distro Docker Desktop's `daemon.json` (E2.S2's builder-GC gap); the clock offset is *Windows' process start − our launch instant* (latency = printed − started, one clock), and the two observations of §15 #10 are this run's and the previous full run's; clock jumps are systemd-resolved's "Clock change detected" lines, the kernel's signals `journalctl --dmesg --grep` of this boot (no `dmesg`); `collect` holds `{state}/run.lock` for its run (E3.S1's lock supersedes it) so the reconcile cannot race it, a second run exits 75; write failure exit 1; the trigger is `timer` when systemd's `INVOCATION_ID` is set; privilege is a delete-on-close write probe of the state directory — unprivileged writes NOTHING (not the first sightings either) and every verb that cannot write `/var/log/wsl-care` logs to `$XDG_STATE_HOME/wsl-care/logs`; the backfill reads the whole 24 h window UNFILTERED (the oldest buffered event of any kind is the only proof the buffer reaches the marker; measured: 248 healthcheck events spanning 91 s), parses it in memory only and stores only starts; the live stream runs in bounded 10-minute segments (`ICommandRunner.StreamAsync`, Docker closes a future `--until` itself — observed live), each end a `covered` marker; `events follow --once` added; the 24 h count is also partial before the follower's first 24 h and while its newest coverage is > 15 min old; folder walks are daily (> 20 h), bounded (2 M entries / 2 min), never follow a link, and "what grew" is per walked folder (not `$HOME`/`/var`); `.vhdx` sizes, inotify, VS Code Server builds and the "failed to start within" boot error are not collected; the run log keeps `logging.retentionDays` (14), not 30; under the root timer `$HOME` is root's (E4.S1 decides whose home is walked and protected); `ICommandRunner.StreamAsync`, five `IFileSystem` members and `IHostPaths.UserLogDirectory` were added; the fake tool gained prefix / call-budget / hang-after answers and `timedatectl` + `snap`. **After the code round (gate session `714367be`, all findings accepted):** the backfill reads the engine mark (`docker network inspect bridge`) and the continuity rule of §15b #0 as amended; `events follow` writes `{state}/starts-summary.json` after every marker and `status` reads only that; retention ages a history line and its `runs/{day}/` folder by ONE UTC-day boundary, removes an aged day folder whole, rewrites the history under one lock acquisition, and the reconcile never makes a detail older than the window `interrupted`; `StreamAsync` drains stderr concurrently and kills + reaps the tree in a `finally` on every exit but a normal end | Opus |
| E3.S1 | action engine, `CommandPolicy` (the one filter every runner call passes), `act` root-only (§15c #0), target-user discovery + `runuser` argv (§15c #2), lock, dryRun, idle gating, measured freed bytes, `running.json` | **Fable** — deletion safety and the never-list |
| E3.S2 | A4–A9, A11, A12, A14, A17 | **Fable** — irreversible deletion |
| E3.S3 | A1, A2 (event-driven), A3, A10, A15, A16, `logs`/`runs` | Opus |
| E4.S1 | units, default config, `install.sh` with checksum + attestation, uninstall | **Fable** — the install trust boundary |
| E4.S2 | release-please, `release.yml` per-RID with attestations, tag ruleset, branch protection, Sonar, CodeRabbit; cut `daemon-v0.1.0` | **Fable** — credentials and supply chain |
| E5.S1 | extension skeleton, `WslCareClient`, `ci-extension.yml` | Opus |
| E5.S2 | status bar, read-only panel, *Install daemon* | Opus |
| E5.S3 | Marketplace publish leg, publisher, icon, README, CHANGELOG | **Fable** — `VSCE_PAT` and what ships publicly |
| E6.S1 | root argv allowlist, `doctor` root check, buttons with preview/confirm, running state | **Fable** — the root boundary |
| E6.S2 | Last cleanup, Run full check now | Opus |
| E6.S3 | Logs page | Opus |
| E7.S1 | agent catalogue and discovery, `agents list/probe` | Opus |
| E7.S2 | settings ↔ config | Opus |
| E7.S3 | AI-agents section, Add CLI path, Windows numbers in Memory/Disk | Opus |
| E8.S1–S3 | help via the kit, zoom + tone everywhere, ru/uk/de/es + stale stamps | Opus |
| E9.S1 | archive engine (one session one month, never-move list, in-use skip, copy→fsync→hash→index→delete) | **Fable** — irreplaceable data |
| E9.S2 | restore, reconcile, list | **Fable** — reverse move and crash recovery |
| E9.S3 | both sides, `wslpath`, base-path validation, retention warning | Opus |
| E10.S1–S2 | archive settings and Archive now; Archive page and Logs | Opus |
| E11.S1–S3 | Windows collectors I and II; Windows install/doctor/perf log | Opus |
| E12.S1 | unelevated Windows cleanups incl. W-A2 with the `%TEMP%\claude\` guard | **Fable** — bulk deletion in a 155 k-entry tree |
| E12.S2 | the elevated channel and admin actions | **Fable** — the elevated boundary |
| E12.S3 | opt-in/reversible actions and advisors with undo | Opus |
| E13.S1 | Windows group read-only + side column | Opus |
| E13.S2 | Windows buttons through the channel | **Fable** — crosses the elevated boundary |

**The prototype and the Marketplace** (user decision 2026-10-02): at the end of E5 — `daemon-v0.1.0` and
`extension-v0.1.0` published. Read-only on purpose: the root boundary gets its own Fable story and gate
round (E6) before any button reaches the public. Each later extension epic ends with its release.

**Risk list for the gate** (cadence groups {E1–E3}, {E4–E6}, {E7–E9}, {E10–E12}, {E13}): E3.S1+S2 (the
only thing between the timer and irreversible Docker deletion), E9.S1+S2 (moving the owner's AI
sessions), E6.S1 (root through an argv allowlist), E12.S2 (a user-writable request folder read by a
highest-privilege task), E12.S1 (bulk TEMP deletion), E4.S1+S2 (`curl | sh` as root, attestations,
credentials).

**Order and the kit.** E1→E4 make the daemon releasable first, because the extension is a view over the
CLI contract. Only E8 imports `@oleksandrdubyna88/vscode-webview-kit`; nothing depends on E8, so it can
slide after E13 if the kit is late. The archive (E9–E10) precedes Windows (E11–E13) because Claude
already deletes sessions at day 30.

## 14. Definition of Done

- [ ] Phase 0 steps done or explicitly declined, each with before/after numbers in `research/`.
- [ ] The AOT binary builds with zero AOT/trim warnings for `linux-x64`, `linux-arm64`, `win-x64`.
- [ ] `install.sh` verifies the checksum; `sysstat` and `atop` collect; the timer runs every 4 h; history,
      run details, run logs and container-start files appear.
- [ ] A1–A17 implemented with `--preview`, each switchable; the timer's `dryRun` week reviewed.
- [ ] Nothing from the *never* list can be executed — enforced by tests.
- [ ] The extension shows memory, swap, disk, containers (now / 24 h / reclaimable), AI agents (both sides,
      with *Add CLI path*), top holders, health; the *Cleanup* table has the 2026-10-02 rows; *Last
      cleanup* shows the freed table + "Docker after".
- [ ] The *Logs* page answers, for this run / today / yesterday / any date or range: what was removed in
      detail, totals per action, runs with and without cleanup, max and min.
- [ ] Help in en/ru/uk/de/es with fallback and stale-translation notes; text size and tone on every page.
- [ ] CI green on every workflow; a tagged release publishes daemon binaries with `.sha256` and the `.vsix`.
- [ ] The §1 success criterion checked over one working week and recorded in `research/`.
- [ ] This plan promoted to `research/` with status `IMPLEMENTED <date>` and its deviations.
