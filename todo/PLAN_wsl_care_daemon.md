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
> built; and E3.S1 built (the action engine, the `CommandPolicy` every argv passes — the never-list, then deny by default
> against declared templates, with its property test — the root-only `act`, the target user and its `runuser` wrapper,
> ONE lock for `collect` and `act`, `running.json` with heartbeat / sweep / wedged, the timer's 7-day dry run, the idle
> gate, and the reference action A10 — deviations in the E3.S1 row) and E3.S2 built (the irreversible deletions A4–A9, A11, A12,
> A14, A17 with one preview computation shared with `preview --all`, measured freed bytes, a pid-and-start signal seam, the
> target user's home for every per-user path, `--manual` and A4's shown list — deviations in the E3.S2 row, 2026-10-03) and E3.S3
> built (A1, A2 with its event, A3, A15, A16, the timer's action pass inside `collect`, `logs` / `runs` — deviations in the E3.S3
> row, 2026-10-03), so epic E3 is built; and E4.S1 built (the three units, `install.sh` with checksum + attestation +
> uninstall, the empty machine layer — deviations in the E4.S1 row, 2026-10-03) and E4.S2 built as files and tests (release-please,
> `release.yml` per RID with attestations, the shared smoke / package / guard / verify scripts, the two rulesets, Sonar and
> CodeRabbit — deviations in the E4.S2 row, 2026-10-03) and E4's independent review applied (§15e, 2026-10-03), with the
> cadence critique for E4–E6 recorded (§15f), and the E5 plan review folded in (§15g, 2026-10-03: E5 split into E5.S0–E5.S3
> with an E5 live gate) and E5.S0 built (`verdicts` + `productVersion` in `status --json`, the compatibility rule, the
> golden contracts — deviations in the E5.S0 row, 2026-10-03) and E5.S1 built (the `wsl.exe` measurement, the
> `src_vs_code` skeleton, the runner seam, `WslCareClient` over four read-only verbs, the strict fake, the structural and
> bundle tests, `ci-extension.yml` — deviations in the E5.S1 row, 2026-10-03) and E5.S2 built (the status bar, the read-only
> panel from one field map held equal to `research/architecture.md`, the webview rules, the focused-window polling, the
> strict page harness, `@vscode/test-electron` on 1.85.0 + stable, the M1 churn measured for the owner — deviations in the
> E5.S2 row, 2026-10-04) and E5.S3 built as files and tests (*Install daemon* typing the pinned command, the universal `.vsix` with its allowlist and leak checks, Marketplace metadata, `release-extension.yml` + `tags-extension.json` + the release-please package, with the coai gate's E5 plan round folded in as §15h — deviations in the E5.S3 row, 2026-10-04); and the E6 plan review folded in (§15j, 2026-10-04: an own Plan agent standing in for coai, every finding accepted; E6 re-split into E6.S0–E6.S4 with an E6 daemon live gate and an E6 live gate) and E6.S0 built (the daemon read contract — `status`'s `actions` / `capabilities` / `running` / `lastCleanup`, A4's `shown`, `runs show`, the instant range, `RunLine.metrics`, `contracts/*.json`, SIGHUP, exclusive marks — deviations in the E6.S0 row, 2026-10-04); and its review round fixed (§15j, 2026-10-04) and the coai gate's E6 plan round folded in (§15k, 2026-10-04: 20 findings accepted, E6 built as one unit) and E6.S1 built, its review round fixed (§15l) and the coai E6 code round fixed (§15m, verdict proceed) (the detached runs — `act` / `collect --detach`, the request files written exclusively, `act --request`, the request sweep, `act --stop` with its stop marker, `--only -`, the template unit `wsl-care-act@.service`, the installer's rename and bounded wait — deviations in the E6.S1 row, 2026-10-04; the distro-survival measurement moved to the E6 daemon live gate's first step); and E7.S0 built (the configuration trust and contract — §15q *E7.S0 as built*, 2026-10-05); the E4 live gate — the owner's settings (`docs/repo-settings.md`), the cut of `daemon-v0.1.0`,

> E5.S2 row, 2026-10-04) and E5.S3 built as files and tests (*Install daemon* typing the pinned command, the universal `.vsix` with its allowlist and leak checks, Marketplace metadata, `release-extension.yml` + `tags-extension.json` + the release-please package, with the coai gate's E5 plan round folded in as §15h — deviations in the E5.S3 row, 2026-10-04); and the E6 plan review folded in (§15j, 2026-10-04: an own Plan agent standing in for coai, every finding accepted; E6 re-split into E6.S0–E6.S4 with an E6 daemon live gate and an E6 live gate) and E6.S0 built (the daemon read contract — `status`'s `actions` / `capabilities` / `running` / `lastCleanup`, A4's `shown`, `runs show`, the instant range, `RunLine.metrics`, `contracts/*.json`, SIGHUP, exclusive marks — deviations in the E6.S0 row, 2026-10-04); and its review round fixed (§15j, 2026-10-04) and the coai gate's E6 plan round folded in (§15k, 2026-10-04: 20 findings accepted, E6 built as one unit) and E6.S1 built, its review round fixed (§15l) and the coai E6 code round fixed (§15m, verdict proceed) (the detached runs — `act` / `collect --detach`, the request files written exclusively, `act --request`, the request sweep, `act --stop` with its stop marker, `--only -`, the template unit `wsl-care-act@.service`, the installer's rename and bounded wait — deviations in the E6.S1 row, 2026-10-04; the distro-survival measurement moved to the E6 daemon live gate's first step); and E6.S2–E6.S4 built as files and tests on `feat/wc-e6-cleanup-logs` (PR #12 — the root boundary, the cleanup buttons and *Last cleanup*, the Logs page; NOT merged until `extension-v0.1.0` is tagged, §15j B3 — deviations in their rows, 2026-10-05); the E4 live gate — the owner's settings (`docs/repo-settings.md`), the cut of `daemon-v0.1.0`,
> the live install and its stamp — the owner's M1 decision, the E5 live gate, and the rest of §16 (E6 onwards) are still open.** Scope: a C# Native AOT daemon/CLI run by a
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
  ~16 GB, `named-volume-a` 10.3 GB alone) — a human decides per volume; no action exists for them.
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
| A4 | **`docker volume rm` of a re-checked list, never `prune`** (gate round 1, finding 4): at run time it removes only volumes that were in the shown preview AND are still unattached AND carry no `wsl-care.keep` label AND were first seen ≥ `volumes.anonymousOlderThanDays` ago (timer default 1 day; a button may choose 0, as the one-time cleanup did) — **unnamed (anonymous) volumes only**, anonymous as Docker decides it: the `com.docker.volume.anonymous` label AND a 64-hex name; a 64-hex name without the label (`docker volume create` without a name) or a volume whose labels are unknown is never taken (§15d) | > `volumes.anonymousMaxCount` (100) or > `volumes.anonymousMaxGb` (20) | — | on | no container refers to them; named volumes never touched. **Refuses on Docker < 23** | 387 volumes, 59.6 GB |
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
| `/var/lib/wsl-care/history.jsonl` | one summary line per run: trigger, dryRun, metrics (§4), warnings, per-action `{id, count, freedBytes}` (results only), and — amended by §15o — the run's `kind` (`collect` \| `act`) | 90 days |
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
| `act <A#>[,<A#>…] (--preview or --confirm) [--manual or --timer] --json` (amended by §15f #7, §15j: exactly one of `--preview` / `--confirm`; `--manual` and `--timer` exclusive; `--detach`, `--only -`, `--request`, `--stop` arrive with E6.S1) | per action: *what*, *count*, *freed GB*, A4's `shown` list in a preview; the answer's `productVersion` | buttons |
| `logs [--period today\|yesterday\|yyyy-MM-dd\|yyyy-MM-dd..yyyy-MM-dd \| --from <RFC3339> --to <RFC3339>] [--action <A#>] [--detail] --json` (amended by E3.S3 and §15j M7: the period shapes as built, UTC days, plus the instant range) | runs in the period + aggregates (§7.4) from the history lines alone; the objects removed with `--detail` or one `--action` (§15d #10) | *Logs* page |
| `runs show <runId> --json` (§15j M3: built in E6.S0; `runs log` CUT) | one run: queued / running / done with its full detail (every removed and not-removed object, the commands and their exits) / refused / interrupted / unknown | *Logs* page |
| `agents list --json` / `agents probe <path> --json` | §4.6 discovery result / what a manual CLI path resolves to | *AI agents* section, *Add CLI path* |
| `config get` / `config set <key> <value>` | validated read/write of the user override file | settings sync |
| `doctor --json` | sysstat/atop/timer/events-unit health, versions | *Health* section |
| `events follow` | the §4.3 follower | `wsl-care-events.service` |

Every JSON answer carries `schemaVersion`; the extension refuses a major version it does not know and says
so. **The compatibility rule** (§15g M2, written before the first public consumer):

- `schemaVersion` changes **only on a breaking change** — a field removed, renamed, retyped, or its meaning changed.
  An **additive** field never bumps it.
- The client **ignores unknown keys** and treats every field added after `0.1.0` as optional: an absent one reads as
  "update the daemon to see this", never as 0 and never as an error.
- **An unknown enum value reads as unknown, never a crash** (§15j m1) — a `running.state`, an `outcome`, a `result` or a
  `status` the client does not know is shown as "unknown (<value>)"; a TS test holds it (E6.S2).
- The extension compiles `SUPPORTED_SCHEMA = [1]` and `MIN_DAEMON_FOR_RENDER = 0.1.0`. The daemon's version is
  `status.productVersion` (E5.S0); from a daemon that predates it, `--version` (`x.y.z(+sha)?`; `unknown` is an
  unstamped build — render, do not refuse).
- Refusal is **per verb**: an unknown `preview` major blanks only *Cleanup*, an unknown `doctor` major only *Health*.
- The client tests replay **two golden sets**: the one frozen at `daemon-v0.1.0` (`contracts/golden/daemon-0.1.0/`,
  frozen at the E5 live gate) and HEAD (`contracts/golden/head/`, written by the scenario harness, E5.S0).

**Durable running state.** A running action writes `/var/lib/wsl-care/running.json` (action, pid, start,
heartbeat every 5 s) before it starts and removes it when it ends; a reader treats a heartbeat older than
30 s as dead (a pid alone can be reused). The extension derives "Cleaning…" from that file, so a VS Code
reload mid-cleanup still shows the truth and never sticks on a dead run. **`wedged`** (§15k #0) = a live process whose
heartbeat — written by its timer thread — is older than 30 s; every command a run starts has its own ceiling with a tree
kill, so no hang keeps a run `live` forever. — **Amended by §15f #7 and §15j M3:** the
extension never reads `running.json` raw; it reads `status --json`'s `running` block (`none` / `queued` / `live` /
`wedged` / `dead` / `unknown` / `unreadable`), which `status` derives read-only, never sweeping.

## 7. The VS Code extension (`src_vs_code/`)

### 7.1 Where it runs

`extensionKind: ["ui"]` — it runs on the **Windows** side whether the window is local or *Remote – WSL*;
reaches the daemon with `wsl.exe -d <distro> [-u root] --cd / --exec /opt/wsl-care/bin/wsl-care …` (§15f #1: `--exec`,
never `wsl.exe … -- …`, which hands argv to a shell; every `act` and `collect --detach` with `-u root`, which `wsl.exe`
grants without a password — §15c #0 supersedes the old A1/A2/A9/A10/A11 list) and runs the bundled `wsl-care.exe` for
Windows-side numbers (E7.S3).

### 7.2 What you see

**Status bar:** `WSL RAM 62 % · swap 1.2 G · / 13 %`, coloured ok / warn / alert; click opens the panel.

**Side panel "AI OS Care"** (the extension was named *WSL Care* until the owner's rename of 2026-10-06), sections:

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

**What the read-only panel of E5 can show (§15g B2).** E5 calls four verbs only — `status --json`, `preview --all
--json`, `doctor --json`, `--version` — so the sections above are filled from what those answer, and every other row says
when it arrives. The detailed table (section · row · JSON path · verb · refresh trigger) lands in E5.S2.

- **Available in E5:** from `status` — `vm.memory` (incl. fragmentation, pressure, swap), `vm.processes` (top holders,
  families), `vm.containers`, `vm.unattributed`, `vm.disk`, `containerStarts`, `folders`, `slow.*` with their age, and
  (E5.S0) `verdicts` and `productVersion`; from `preview` — the cleanup rows, kept named volumes, totals, the hygiene
  audit; from `doctor` — the checks, versions, `lastRun`, `configError`.
- **Not available in E5**, each row shown as "arrives in E#", never blank or 0: the `MemAvailable` sparkline and the swap
  trend today (need `logs` / `runs` — E6); Health's "warnings since", clock jumps and journald span (collect's run
  detail only — a later verb; since E5.S0 the `clock.jumps` and `journal.history` VERDICTS, with their figure as text, are
  carried in `status`'s `verdicts` from the newest full run, with its age); `vmmemWSL`, host RAM, `C:` free (`wsl-care.exe` — E7.S3); `.vhdx` sizes (E11); AI agents
  (E7); *Last cleanup* (E6).
- A figure the daemon answered `available: false` shows "unavailable — <reason>".

### 7.3 Buttons

Click → `act <A#> --preview` → a modal with exactly what will be removed (counts, names for ≤ 20 items, GB)
→ **Confirm** → `act <A#>` → the result as a notification and under *Last cleanup*. **Clean selected**
previews every ticked row in one modal and runs them in the order A5 → A4 → A6 → A7 → A8 → A9 (removing
containers first frees their volumes and images — the order of the 2026-10-02 run). Actions that touch
user data or force re-downloads (A5 for non-Testcontainers, A6 unused, A8, A11, A12) need a second
confirmation that names the setting they used. While running, the buttons read *Cleaning…* (from
`status.running`, §6 — never `running.json` raw; §15j M3). **Amended by §15j:** the confirm is a DETACHED run
(`act … --confirm --manual --detach`, B2) the panel follows through `status.running` and then `runs show`; A4's names come
from the preview's `shown` list through `--only -` (B1); the modal lives in the host (M8).

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
| **Run list** | time, trigger, dryRun, actions, freed; expand a run → **what exactly was removed**: every object with type, name/id, image, age and size (from `runs/{day}/{runId}.json`, through `runs show`); its **commands and exits** from `runs show` (§15j M3 — the raw run-log link and `runs log` are cut) |

Everything comes from `wsl-care logs` / `runs` / `runs show` — the page computes nothing itself. Local days reach the daemon
as the instant range `--from` / `--to` (§15j M7).

### 7.5 Settings

VS Code settings under `wslCare.*`, each mirrored to the user config file via `config set` on change
(validated by the CLI; a rejected value is reverted with its message):

- `wslCare.distro` (`Ubuntu`), `wslCare.refreshSeconds` (60), `wslCare.dryRun` (true for the first week),
  — **amended by §15f #12 and §15g M1, M3:** `wslCare.distro` empty = WSL's default distro (the `*` marker of
  `wsl.exe -l -v`), `wslCare.refreshSeconds` default 120 with a schema `minimum` of 30, both `"scope": "application"`;
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

**Reuse note.** ~~These modules are copied from coai (MIT, same author) with a header naming the source
commit.~~ **Superseded (§15g m4):** nothing is copied from coai. Help, zoom and tone come from the shared kit
([PLAN_shared_vscode_kit.md](PLAN_shared_vscode_kit.md)) in E8 — "the kit imported, no copied coai modules" (§16 E8); the
table above names the coai pieces the kit is extracted from, not files to copy here. E5 needs none of them (§15g M7).

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
| `release.yml` | same | tags `daemon-v*`, `extension-v*`. **daemon:** native runners per RID (AOT does not cross-compile) — `linux-x64`, `linux-arm64` (`ubuntu-24.04-arm`), `win-x64` (the probe); tests → publish → smoke → `wsl-care-$VERSION-$RID.tar.gz` / `.zip` + `.sha256` → uploaded to the release-please **draft** → "every RID has an asset" check → publish. **extension:** tag must equal `package.json` version; ~~the `win-x64` probe is downloaded from the matching daemon release and bundled;~~ (struck for E5 by §15f #5 / §15g M5 — bundling `wsl-care.exe` is E7.S3) `vsce package` once → `vsce publish --packagePath` (`VSCE_PAT`) → `.vsix` on the release. **Amended by §15g M5:** the extension leg is a separate `release-extension.yml` (tags `extension-v*`), not a leg of `release.yml` |
| `pr-title.yml` | same | conventional-commit PR titles |
| `coderabbit-review.yml` + `.coderabbit.yaml` | same | `language: ru-RU`, `profile: chill` |
| `sonarcloud.yml` | same | `dotnet-coverage` over the test executables + `c8` for the extension |
| `ci-workflows` job | `ci-server.yml` actionlint job | actionlint pinned by version + SHA-256, shellcheck present |
| `.github/dependabot.yml` | same | nuget, npm (`/src_vs_code`), github-actions; weekly; FluentAssertions major, `@types/vscode` major+minor ignored |
| `.github/branch-protection.json` + `scripts/branch-protection.mjs`, `.github/tag-ruleset.json` | same | required job names, linear history, conversation resolution; tag ruleset on `daemon-v*`/`extension-v*` with only the GitHub App as bypass; CodeQL via default setup |

`release-please-config.json`: `separate-pull-requests`, `include-component-in-tag`, `tag-separator: "-"`,
`draft: true`, `force-tag-creation: true` (without it a draft cuts no tag and `release.yml` never runs),
no `exclude-paths` (the package path decides — an exclude list outside it is inert; E4 review B3), `bump-minor-pre-major:
true`; packages `src_daemon` → component `daemon` (`simple`), `src_vs_code` →
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
  src/   extension.ts client/WslCareClient.ts views/      (E5)
         logs/ (E6)  settings/ (E7)  — help, zoom and tone come from the kit in E8 (§15g m4), no help/ zoom/ tone/ copies
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
- View-model functions (bytes → "12.3 GB", colours from `status`'s `verdicts` (E5.S0), button state from `status`'s
  `running` block (E6.S0, §15f #7 — the extension never reads `running.json` raw; §15g m4), period labels).
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
| 2 | §5 (A3, A8, A12, A14, A17; the A8/A9 walks; protected roots), E3.S1 | **Accepted.** The **target user** is discovered once per run: the `[user] default=` of `/etc/wsl.conf`; else the single account with uid ≥ 1000 and a login shell in `/etc/passwd`; anything ambiguous makes every user-scoped action refuse with the reason (machine-scoped actions still run). A user-scoped action runs its tool as that user through `runuser -u <user> -- <exe> <args…>` (argv, a clean environment with that user's `HOME`, `USER` and a PATH built from a fixed list of the user's bin folders — `~/.nvm/versions/node/<default>/bin`, `~/.local/bin`, `~/.cargo/bin`, `/usr/local/bin`, `/usr/bin`), and the executable is resolved against that list before the start. This also settles E2.S3's open question for E4.S1: folder walks and the DeletionPolicy's protected roots (`~/git`, AI-agent folders) use the TARGET user's home, never root's. **Built in E3.S2 (2026-10-03), so E4.S1 no longer carries it:** as root the daily walk, the roots of A8 / A12 / A14 / A17 and the user configuration layer are the target user's home (`TargetHome.Resolve`); ~~an ambiguous target makes the run observe-only~~ (amended by §15d #2: an ambiguous target leaves the user layer out — machine-scoped actions run, every user-scoped one refuses); the protected roots cover root's and every login account's home (E3.S1). |
| 3 | §5 (A1, A2, A8), E3.S1–S3 | **Accepted.** No shell anywhere: A1 is `/bin/sync` then `sysctl -w vm.drop_caches=1`, A2 is `sysctl -w vm.compact_memory=1`, A8 is `npm cache clean --force` resolved per #2. The never-list forbids any `vm.drop_caches` value other than 1 and any `sh -c` / `bash -c` / `bash -ic`; the CommandPolicy property test covers both. |

### 15d. Gate — epic 3 code round (2026-10-03, session `a90e342d`) and an independent review

Both over the whole epic's diff (PR #7). Every accepted finding landed with a test seen failing first for the real symptom
(`research/module_tests.md`).

| # | Lands in | Decision |
|---|---|---|
| 0, 1 | `research/architecture.md` | **Accepted.** The overview names `logs` / `runs`; the seams diagram's verbs node gains `LogsCommand`, its actions node lists the whole registry. |
| 2 | §15c #2, E3.S2 | **Accepted.** An ambiguous target user no longer makes the whole run observe-only: the run reads the defaults and the machine layer WITHOUT the user layer (`HomeOwner.UserLayerSkipped`, `CliHost.LoadConfig`), machine-scoped actions run, the engine's target-user gate refuses every user-scoped one. Residual, stated where the loader says it: a machine-scoped `auto` switch a user turned off in their own layer is not seen until `/etc/wsl.conf` names the user. |
| 3 | E3.S2 | **Accepted.** `ActionCommands.AsRunner()` also runs the collectors' shared read-only `ReadCommandTemplates`; anything else undeclared is refused; the policy judges every argv either way. |
| 4 | E3.S2 (A11) | **Accepted.** A11's preview bytes are none; the memory its suspects hold is the `heldMemoryBytes` fact (and each item's size), never would-free or freed bytes. |
| 5 | — | **Rejected with evidence:** glibc 2.39 (Ubuntu 24.04) exports `pidfd_send_signal@@GLIBC_2.36`. |
| 6 | E2.S3, E3.S3 | **Accepted.** Readers of the line files (`history.jsonl`, the container-start days) ignore a trailing line without its newline; beyond the finding, the next append first ends torn remains with a newline, so a writer that died mid-line no longer swallows the next record (found by the test). |
| 7 | §15 #6 | **Accepted.** An unreadable or unparsable `running.json` is read again three times, 100 ms apart, before any verdict; one that never reads is its own state, `stateUnreadable` (exit 79), naming the file and the reason — never "wedged" of a live process. |
| 8 | E3.S1, E3.S3 | **Accepted.** Every full run sweeps a dead or mismatched `running.json` in its housekeeping, whatever `INVOCATION_ID` says (`RunningSweep`, the one sweep the engine uses too). |
| 9 | §5 A11 | **Accepted.** SIGTERM to every suspect, ONE shared 10 s deadline across their pidfds, then SIGKILL to the survivors; a cancellation is honoured within a 200 ms slice and every pin closed. |
| 10 | §6, §7.4 | **Accepted.** `logs` / `runs` compute totals, counts, with / without, max / min and metrics from the history lines alone; the objects removed are read from the run details only with `--detail` or one `--action`, from at most the newest 50 (`detailsRead` / `detailsNotRead`). The history line gained what the totals need beside the figures: a failed action's failure. |
| 11 | §6, §15 #6 | **Accepted.** `collect` writes `running.json` (action `collect`, pid, start, a 5 s heartbeat) as soon as it holds the lock and removes it in a `finally`; another run's file is never overwritten or removed. |

Independent review (all accepted):

| # | Decision |
|---|---|
| R1 | A4 / its row / `volume-seen.json` / A5's accounting classify a volume as anonymous only with Docker's `com.docker.volume.anonymous` label AND a 64-hex name; a hex name without the label is named (kept, listed with the named volumes), a volume missing from `system df -v` is never selected (named in a row note). |
| R2 | A failed action's measured deletions count in `logs` / `runs`, with its failure beside the figures. |
| R3 | A5 counts an anonymous volume shared by two removed containers once (preview and freed). |
| R4 | A3 refuses when the process table cannot be read again just before the command. |
| R5 | `act --only` reads a REGULAR file only (a directory, FIFO, socket or device refused at once) and never past its 1 MiB cap of bytes actually read. |
| R6 | A `poll` error during A11's wait is a failure, never an end; nothing more is sent on that guess. |
| R7 | Every E3 method's cyclomatic complexity ≤ 4 (C# doctrine §6), refactored without behaviour change. |
| CI | The trigger is EXPLICIT: `collect --timer` / `act --timer` is the timer, never `INVOCATION_ID` — every descendant of a systemd unit inherits it (CI run 37129452377: four act tests on the GitHub runners took a runner job for the timer and dry-ran a button press; a VS Code Server started as a user service would have done the same). E4.S1's timer unit MUST pass `--timer` in its `ExecStart`. |

### 15e. Gate — epic 4 plan round (2026-10-03, session `05f9799b`)

| # | Lands in | Decision |
|---|---|---|
| 0 | §9, E4.S2 | **Accepted.** Workflow-level permissions stay `contents: read`; each job gets only what it needs: the per-RID build job `id-token: write` + `attestations: write` (`actions/attest-build-provenance`), the upload/publish job `contents: write`. Nothing else holds a write scope. |
| 1 | §9, E4.S1, E4.S2 | **Accepted.** Each release archive `wsl-care-$VERSION-$RID.tar.gz` carries the binary AND `systemd/` (`wsl-care.service`, `wsl-care.timer`, `wsl-care-events.service`) and `config/default.json`; `install.sh` installs the units to `/etc/systemd/system/` and the machine config to `/etc/wsl-care/config.json` only when none exists (never overwrites a person's machine layer). A packaging test lists the archive and fails on a missing member. |
| 2 | §9, E4.S2 | **Accepted.** `release.yml` is a per-RID matrix that builds, tests, smokes, attests and uploads to the draft release, then ONE downstream job (`needs: [build]`) checks that every expected RID asset and its `.sha256` exist and only then publishes the release — never a subset of platforms. |
| 3 | §9, §15 #1, E4.S1 | **Accepted.** `install.sh` installs to `/opt/wsl-care/bin/wsl-care` and links `/usr/local/bin/wsl-care` to it; its own smoke and every root argv the extension sends use the ABSOLUTE path `/opt/wsl-care/bin/wsl-care` (no PATH lookup as root). |
| 4 | §15 #12, E4.S1 | **Accepted.** Attestation stays required by default: when `gh` is missing `install.sh` stops BEFORE installing anything and prints how to install `gh`, or how to proceed knowingly with `curl … | sh -s -- --skip-attestation` (printed loudly, the `.sha256` integrity check still applies). |
| 5 | §9, E4.S2 | **Accepted in this form:** `ci-daemon.yml` already smokes the published AOT binary with `status --json`, `preview --all --json`, the full `collect` run and `act --preview` of every action (E2–E3), not only `--help`; `release.yml` reuses exactly those smoke steps (one shared script), so the released binary is exercised the same way. |

**Independent review of epic 4's diff (2026-10-03)** — three reviewers standing in for the coai gate, whose vendors were
out of quota (security, correctness, cadence). Every finding accepted; each behaviour landed with a test seen red first
for the real symptom (`research/module_tests.md`, *The E4 review*). Each row OVERRIDES the section it names.

| # | Lands in | Decision |
|---|---|---|
| A1 | §15 #12, E4.S1 | **Accepted (HIGH).** The attestation identity is EXACT: `--cert-identity https://github.com/oleksandrdubyna88/wsl_care/.github/workflows/release.yml@refs/tags/daemon-v<version>` with `--repo` and `--deny-self-hosted-runners`, never `--signer-workflow` — gh matches that as a literal PREFIX of the identity, so `release.yml` built from any branch passed (measured on gh 2.97.0 with cli/cli's own attestation). The fake gh now ENFORCES the flags with those measured semantics, so the tests are about what passes. |
| A2 | §15e #4, E4.S1 | **Accepted, corrected by measurement (MEDIUM).** The review's floor (2.49.0, where `gh attestation` appeared) is not enough: bisected over gh's releases, **2.56.0** is the oldest that verifies a public-good attestation today (2.49.0–2.55.0: `unsupported tlog public key type: PKIX_ED25519`); Ubuntu 24.04 ships 2.45.0. The preflight asks the version AND `attestation verify --help` for the four flags, before any download, and points at GitHub's apt repository (`cli.github.com/packages`), never `apt-get install gh`, never a login. |
| A3 | §15e #4, E4.S1 | **Accepted, the better design taken (LOW).** Root fetches the attestation itself, unauthenticated (`GET /repos/…/attestations/sha256:<digest>` → `bundle_url` → snappy JSON, decompressed by awk over od), and runs `gh attestation verify --bundle` as root with gh's home, configuration and cache inside the run's temporary folder and no token. Measured: a `--bundle` verification needs no login on gh 2.56.0–2.97.0 (online verification without one exits 4); gh still reaches Sigstore's TUF CDN, unauthenticated. No `runuser`, so the person who ran sudo no longer decides the verdict. Each bundle is verified alone; any one passing suffices. |
| A4 | §8, E4.S1 | **Accepted, decided.** `MemoryMax` 256M → **1G** (the cgroup covers every child: npm, dotnet, pip, the Docker CLI, the 2M-entry walk); `NoNewPrivileges=yes` KEPT, its snap risk (no AppArmor profile change for a snap Docker) written in the unit, the README and `POST_DEPLOY.md` #11, which reads the journal for an OOM kill or a snap refusal on the live install. `doctor` was not changed: a check that guesses at snap confinement before it has been observed would be a claim, not a measurement. |
| B1 | E4.S2 | **Accepted (CRITICAL).** `package-daemon.sh` printed `$(cd "$out" && pwd)` — an MSYS path (`/d/a/…`) under Git Bash, which `attest-build-provenance` and `upload-artifact` (Windows programs) read as `D:\d\a\…`: the win-x64 release leg would have failed at the attestation. It now prints `<out-dir>` as given, through `cygpath -m` where present; `PackagePathFlows` opens the printed path with .NET on every OS (red on Windows before the fix). |
| B2 | §9, E4.S2 | **Accepted.** Windows packaging ran on no pull request although three documents said so. Every `ci-daemon.yml` leg now packs the archive from its published AOT binary with the release's script, checks that RID's pair (`verify-release-assets.sh` gained optional RIDs) and opens the path in a `shell: pwsh` step — not bash, because Git Bash rewrites a path-looking variable on its way to any child (observed), which would hide the very spelling; `release.yml` runs the same check before attesting. The Windows zip test runs on Windows wherever 7-Zip resolves. The overclaims (`package-daemon.sh`, README, `research/architecture.md`) were corrected. |
| B3 | §9, E4.S2 | **Accepted.** `exclude-paths: [".github"]` was inert (release-please hands `src_daemon` only commits under its path), so it was removed; `ReleaseConfigTests` now tests the package-path rule itself and the key's absence. Added `bump-minor-pre-major: true` (a `feat!:` at 0.x is a minor, not 1.0.0) and, decided, `bump-patch-for-minor-pre-major: false` (a `feat:` stays a minor — every epic ends in a minor release). |
| B4 | §15e #5 | **Accepted, added.** `smoke-daemon.sh` gained part 6: `preview --all --json` with no docker reachable — exit 0, the JSON parsed, `schemaVersion` 1, every row `available: false` with its reason, nothing written. |
| B5 | E4.S2 | **Accepted.** Every workflow must declare `permissions:` at the top or on every job; a workflow without one passed the per-job checks vacuously (red with a planted workflow). |
| B6 | E4.S2 | **Accepted.** The gating workflows are DERIVED — every workflow `pull_request` / `pull_request_target` triggers, minus `sonarcloud.yml` and `coderabbit-review.yml` (named, with reasons) — not hand-typed (red with a planted gating workflow). |
| B7 | `POST_DEPLOY.md` | **Accepted.** Item 2 compares: `--version \| cut -d+ -f1 \| grep -qx "$TARGET"`; the Target line covers it and says the checklist runs inside WSL. |
| B8 | docs, tests | **Accepted.** `module_tests.md` (ubuntu-24.04; the smoke's home), README row (A1–A3, A15, A16 ship in 0.1.0); the stale skip deleted (part A); `WorkflowYaml.Quoted` / `PlainScalar` / `SingleQuotedEnd` / `ParseLiteral` and `ShippedFilesTests.Unit` at complexity ≤ 4; `ReleaseScripts.OnPath` replaced by `ExecutableResolver`, `EnsureParent` by the TempRoot. |
| B9 | E4.S1 | **Accepted.** `install.sh` picked the newest tag with a line-based `sed`; a compact (one-line) answer found none. It now takes every `"tag_name": "daemon-v<x.y.z>"` from the joined answer and the numerically highest version (`sort -t. -k1,1n -k2,2n -k3,3n`), pre-releases excluded. |

**Retro coai round over PR #8 (2026-10-06, session `5c23779d`)** — a review of the merged E4 diff after the fact, plus the
consultant (`ad2e68fb`) and an own review. Shipped in one fix pull request (`fix/wc-retro-pr8-installer-ceilings`); every
behaviour landed with a test seen red first (`research/module_tests.md`, *The PR #8 retro round*). Three items of the PR #11
retro round (session `f4bdf605`, consultation `9064487b`) touch the same wait code and ship in the same pull request.

| # | Source | Decision |
|---|---|---|
| plan round | gate | **3 findings rejected:** a truncated download (`install.sh` is truncation-safe by structure — the work starts only in the `if` on its last lines; before it, a cut stream parses and checks arguments and defines functions, then ends having changed nothing); the E6 stop path (shipped since: `act --stop`, `TimeoutStopSec`); apt unbounded (bounded and attended: `apt-get update` under a ceiling, `install` by `DPkg::Lock::Timeout=300`, on the person's terminal). |
| G0 | gate, code round | **Rejected:** plan §9's ru-RU text — an owner policy question, not a code defect. |
| G1 | gate, code round | **Accepted.** `--uninstall --purge` removed the state while a run started by hand could still hold the run lock. The purge now runs under `flock -n` on `/run/wsl-care.lock` (the daemon's `RunLock` is `flock(2)` there) and removes the lock file LAST inside the locked section; a held lock refuses at step `purge`, nothing of the state removed; no `flock` refuses at `preflight` before anything changes. |
| G2 | gate, code round (narrowed) | **Accepted.** Every job-waiting `systemctl` call (`daemon-reload`, `try-restart`, `enable --now`, `disable --now`, `stop`) runs under `WSL_CARE_INSTALL_SYSTEMCTL_SECONDS` (900) with a kill grace — on WSL Ubuntu `sysstat.service` is `Type=oneshot` with `TimeoutStartUSec=infinity` and `JobTimeoutUSec=infinity`; at the ceiling the step fails saying the job may still be running. The dry run still prints the systemctl command. |
| G3 | gate + consultant | **Accepted.** The first full run is announced, with its 900 s ceiling, before `collect` starts. |
| C1 | consultant (missed by the gate) | **Accepted.** The health wait counted only its 5 s sleeps while each `doctor --json` could take 120 s (2 minutes → ~50 minutes). Now a wall-clock deadline; each call gets what is left (at least 10 s), no sleep past it. |
| C2 | consultant (missed by the gate) | **Accepted.** No `timeout` had `-k`: a child ignoring SIGTERM outlived every ceiling. Every `timeout` now has `-k` (10 s; 30 s for the first `collect`). |
| O1 | own review (origin E7.S2c) | **Accepted.** `timer.periodHours = 24` passed validation and rendered `OnCalendar=*-*-* 00/24:00:00`, which systemd 255 refuses — the timer never fired. 24 renders `*-*-* 00:00:00`; a Core test runs `systemd-analyze calendar` over every accepted period (it EXITS 0 on a refused calendar — the output is read). `systemd-analyze verify` of the timer drop-in at every accepted period in `verify-systemd-units.sh` (merged with `fix/wc-act-unit-collectmode` while this round ran) is a follow-up. |
| O3 | own review | **Accepted.** `POST_DEPLOY.md` item 10 piped `curl … \| sh`, which exits 0 when curl fails (measured with a 404); it downloads to a file first. |
| O4 | own review | **Accepted.** The extension rollback check (docs/repo-settings.md, README) used `--signer-workflow` (a prefix match); it pins `--cert-identity …/release-extension.yml@refs/tags/extension-v<previous>` + `--repo` + `--deny-self-hosted-runners`, and a test holds every documented attestation command to that. |
| O5 | own review | **Accepted.** Only `WSL_CARE_INSTALL_DOCTOR_SECONDS` was validated; a non-numeric RUN_WAIT made the upgrade wait endless. Every ceiling variable is a whole number of seconds in its range or a usage refusal (exit 2) before anything runs. |
| R11-G1 | from the PR #11 retro round | **Accepted.** The status call's bare `timeout 30` is `WSL_CARE_INSTALL_STATUS_SECONDS` (30), validated, with the kill grace. |
| R11-G3 | from the PR #11 retro round | **Accepted.** The upgrade wait could overrun RUN_WAIT by a status call plus a sleep; each status call is cut to what is left (at least 5 s) and no sleep reaches past the deadline. |
| R11-C | from the PR #11 retro round | **Accepted.** `run_in_flight` failed OPEN on an installed binary that exists but is not executable (`[ -x ]`); it now takes the no-answer path — in flight, then the refusal with the manual escape. |
| PR34-G | coai gate round on PR #34 (session `7c89fe30`) | **Accepted (1 of 5; 4 rejected in that round).** The health wait printed its readable `doctor` report AFTER the deadline, past the bound its comment and refusal stated. The report is kept; the comment states the true bound (DOCTOR_SECONDS + 2 × (floor + grace), 160 s by default) and the refusal says the measured time of the wait ("still not healthy after <n>s of a <s>s wait") rather than "at most". No behaviour change. |
| PR34-R | rebase onto `fdc9de6` (#28) | **Fixed.** #28 added a draft check to docs/repo-settings.md without `--deny-self-hosted-runners`, wrapped over two lines; the O4 scan read it cut at the line break (CI red). The scan follows wrapped spans now, and the command has the flag. |
| S1 | own review | **Open for the owner:** move the daemon release's attestation into its own job, as the extension release did. |
| O2 | own review | **Open for the owner, needs a measurement:** `Nice=19` / `IOSchedulingClass=idle` give no background priority against the user's processes on WSL — the block scheduler is `none` and no cpu controller is delegated to `system.slice`. |

### 15f. Cadence critique, epics 4–6 (own-agent stand-in for coai, 2026-10-03)

The coai gate's vendors were out of quota, so the cadence round for {E4–E6} was done by the agent itself, after the E4
code review above. Every recommendation is **Accepted**; the column says where it lands. Each row OVERRIDES the section it
names (§7.1–§7.7, §15 #1/#5, §15c #0 where they say otherwise). E5/E6 are not built yet: these are the stories' scope.

| # | Recommendation | Lands in |
|---|---|---|
| 1 | **How the extension starts the daemon.** `wsl.exe -d <distro> [-u root] --cd / --exec /opt/wsl-care/bin/wsl-care …` — never `wsl.exe … -- …`, which hands argv to the distro's default shell (as root: a shell interpreting argv; and rc-file noise breaks the JSON on stdout). Observe `--exec echo '$HOME'` against `-- echo '$HOME'` on this machine before trusting it, and record the observation. | E5.S1 (the client), E6.S1 |
| 2 | **The root boundary is a confused-deputy boundary, not a malware boundary** (any process of this Windows user can already run `wsl -u root`). ONE module builds root argv — a structural test over the bundled JS plus a planted-instance companion proves no other module does. Argv only from closed sets: ids = the extension's registry ∩ the ids the daemon reports; constant flags `--preview` / `--confirm` / `--manual` / `--json`; NEVER `--timer`; *Run full check now* = `collect` without `--timer`. The webview never supplies argv: it sends `{rowId, previewToken}`, the host keeps the preview and takes A4's volume names from it. Confirmation lives in the host (a modal `showWarningMessage`). No `enableCommandUris`, no URI handler, no contributed command that takes arguments. The distro name is validated against `wsl.exe --list --quiet` (UTF-16LE) plus a strict pattern. Every setting that reaches the daemon has `"scope": "application"` or `"machine"` — a cloned repository's `.vscode/settings.json` must not steer it. | E6.S1 (Fable), settings scopes from E5.S1 |
| 3 | **Version handshake.** The daemon adds `productVersion`, `actions: [ids]` and `capabilities` to `status --json` (or a `version --json` verb), and `productVersion` to every `act` answer. The extension refuses to RENDER on an unknown `schemaVersion` major, refuses to ACT below a compiled `minDaemonForActions` or for an id not in both registries, and re-checks at preview time. The panel then says "daemon x.y is older than this extension needs — Update", never an empty table. | E6.S0 (daemon), E5.S1 (render check), E6.S1 (act check) |
| 4 | **E4 merges before its live install.** E4's done-line is files + tests + CI green, merged; the release and the live install are the **E4 live gate** (§16), which the owner runs and stamps. | §16 (done) |
| 5 | **What the first public version (E5) must NOT contain:** any root call path — no `-u root`, `act`, `collect` or `config set` string; the verbs are only `status --json`, `preview --all --json`, `doctor --json`, `--version`, held by the structural test of #2; `wsl-care.exe` (bundling moves to E7.S3); telemetry or any network call; this machine's data (a `vsce ls` allowlist in CI); an auto-executed install (*Install daemon* TYPES the command with `sendText(cmd, false)`, pinned to the `daemon-v<X>` tag, never `--skip-attestation`). **It must contain:** `engines.vscode ^1.85.0` with `@types/vscode` held at it, `extensionKind ["ui"]`, untrusted-workspace support with the setting scopes of #2, `preview: true`, a "Windows + WSL only" notice off win32, stated prerequisites (systemd; Ubuntu ≥ 24.04 / glibc 2.39 — "GLIBC_2.38 not found" mapped to "unsupported distro"; gh ≥ 2.56.0 per §15e A2). The *Install daemon* review moves into E5.S3 (Fable-level). | E5.S1–S3, E7.S3 |
| 6 | **Marketplace mechanics.** LICENSE at the root (this round) and in `src_vs_code`; the publisher id created early (permanent; check the display name "AI OS Care" is free — renamed from "WSL Care" by the owner on 2026-10-06); a PNG icon ≥ 128 px; a README with https-only, non-SVG images and `repository`; `bugs` / `homepage` / `pricing: Free` / `categories` / `keywords`; CHANGELOG through a release-please `node` package `src_vs_code` (initial 0.1.0, manifest 0.0.0); the tag ruleset gains `extension-v*` (re-applied with a probe) and the branch ruleset the extension's checks; `ci-extension.yml` with no path filter (or an always-reporting gate job); `VSCE_PAT` in a protected Environment — or `vsce publish --azure-credential` through OIDC, its current state verified first — with the PAT's expiry recorded in `POST_DEPLOY.md`; the `.vsix` attested, published from `--packagePath` of the attested file, idempotent (skipped when `vsce show` already serves the version), the `.vsix` uploaded to the GitHub DRAFT first, then the Marketplace, and only then the draft made public (order amended by §15h #0); `POST_DEPLOY.md` gains "served version == tag" and "installed vsix contents match the build". | E5.S3 |
| 7 | **A new story E6.S0 "daemon contract"**, released as `daemon-v0.2.0` BEFORE `extension-v0.2.0`: `act` takes exactly one of `--preview` / `--confirm`, buttons pass `--manual`; a preview needs root (77); ids are the `auto.*` keys; A4's shown list arrives through `--only -` (stdin, the same 1 MiB cap) because 387 × `--volume` ≈ 28.6 K characters nears the 32 767 Windows command-line limit (until then refuse above ~30 K); *Clean selected* = ONE `act A5,A4,A6,…` call; `status --json` gains a `running {state, action, runId, trigger, heartbeatAt}` block (the extension never reads `running.json` raw); a "stop a wedged run" verb, or the button is dropped; `runs show` / `runs log`, or §7.4's *This run* and raw-log link are cut; periods are UTC days (labelled or converted); the extension handles exit codes 2, 3, 4, 70, 75, 76, 77, 78, 79 and 130 distinctly; §7.1's root-only list is superseded by §15c #0; §15 #5's `doctor` root check moves to the extension. | E6.S0 (new) |
| 8 | **Polling never starts or keeps the VM running:** `wsl.exe --list --running --quiet` first; when the distro is not running the panel says "WSL stopped" and nothing polls; `preview --all` and `doctor` only on panel open / refresh. | E5.S2 |
| 9 | **A cleanup survives a VS Code reload.** Measure whether a root `act --confirm` survives the death of `wsl.exe` / the extension host; if not, a daemon-side detach (`act --detach` through a `systemd-run` transient unit, returning the `runId`) and the extension follows `running`. A confirm is never time-killed — the stated exception to the tree-kill-on-timeout rule. | E6.S0 (measure + detach), E6.S1 |
| 10 | *(should)* **One shared contract.** The extension's client tests replay JSON recorded from the BUILT CLI at the same commit (golden files written by the scenario harness; a test fails on drift); action ids and exit codes come from a checked-in `contracts/*.json` that a daemon test holds equal to `ActionId.All` / `ExitCode`. | E5.S1 (goldens), E6.S0 (contracts) |
| 11 | *(should)* **Preview → confirm wording.** Only A4 is bound to the list it showed; A5 / A6 / A7 re-select live, so the modal says "re-checked at run time"; a preview expires (e.g. after 5 minutes). | E6.S1 |
| 12 | *(should)* `wslCare.distro` defaults to WSL's default distro, or the one holding `/opt/wsl-care` — never a hard-coded `Ubuntu`. | E5.S1 |
| 13 | *(should)* E5 ships a universal `.vsix` with the non-Windows notice; `--target win32-x64` once E7 bundles the probe. | E5.S3, E7.S3 |

### 15g. Gate — epic 5 plan round (own-agent stand-in for coai, 2026-10-03)

The coai gate's vendors were out of quota, so an own Plan agent reviewed the E5 plan in its place: 2 blocking, 8 major and
10 minor findings. **Every finding is Accepted**; the column says where it lands. Each row OVERRIDES the section it names
(§6, §7.1–§7.7, §9, §10, §12, §15f, §16) where they say otherwise. E5 is split into E5.S0–E5.S3 (§16) with an **E5 live
gate** (owner) after it.

| # | Finding | Lands in | Decision |
|---|---|---|---|
| B1 | Status-bar colours have no source: `StatusReport` is figures only; `Verdict` / `Level` reach only `RunDetail.Thresholds` through `collect` | E5.S0 (daemon), E5.S2 (bar) | **Accepted.** `status --json` gains `verdicts` — the SAME `Verdict` records and ids `collect` writes, computed from the fast sample and the effective config (additive, `schemaVersion` stays 1) — and `productVersion`. Rides `daemon-v0.1.0` (its release pull request is not merged), else the next daemon minor stamped before `extension-v0.1.0`. The extension shows an uncoloured bar and "update the daemon to see warnings" when `verdicts` is missing. |
| B2 | No map from panel field to source; §7.2 asks for data the four allowed verbs do not provide | §7.2 (summary), E5.S2 (table) | **Accepted.** E5.S2 carries a table section · row · JSON path · verb · refresh trigger; a row the verbs cannot fill shows "arrives in E#" (never blank or 0), a figure answered `available: false` shows "unavailable — <reason>". What is and is not available: §7.2. |
| M1 | 60 s polling writes ~1 440 run-log files a day per window (every verb but `--help` / `--version` opens a run log; unprivileged → `$XDG_STATE_HOME/wsl-care/logs/…`; a retention walk at every start) | E5.S2 (policy), `research/` (measurement), the owner | **Accepted; the churn decision is the OWNER's and is OPEN.** Only the focused window polls (`window.state.focused`) and refreshes once on focus; `wslCare.refreshSeconds` schema `minimum` 30, default 120; only `status` is polled; `preview` / `doctor` on panel open and Refresh. The file count is measured and recorded in `research/`; whether to accept it or to record a family-rule exception for a high-frequency read-only verb is put to the owner — not decided here. |
| M2 | No schemaVersion compatibility rule before the first public consumer | §6 (the rule), `research/architecture.md` (E5.S0), E5.S1 (client) | **Accepted.** The rule is in §6: `schemaVersion` changes only on a breaking change, additive fields never bump it, the client ignores unknown keys and treats every post-0.1.0 field as optional; `SUPPORTED_SCHEMA = [1]`, `MIN_DAEMON_FOR_RENDER = 0.1.0`; the daemon version from `status.productVersion`, else `--version` (`unknown` → render); refusal per verb; client tests replay the golden sets frozen at `daemon-v0.1.0` and HEAD. |
| M3 | Distro validation and setting scopes sat in E6.S1 | E5.S1 | **Accepted.** Distro validation and `"scope": "application"` land in E5.S1; `application` beats `machine` (Remote "machine" settings live inside the distro) — supersedes §15f #2's "or machine". Observe and record which settings file a UI-kind extension reads in a Remote-WSL window. The default distro is the `*` marker of `wsl.exe -l -v` (header and "Running" are localised, the marker is not); running = `--list --running --quiet`; both outputs UTF-16LE. Never probe every distro for `/opt/wsl-care`. |
| M4 | "Listed on the Marketplace" made E5's done-line depend on owner-only steps | §16 E5 row, E5 live gate | **Accepted.** E5 done = merged (files, tests, CI green); the **E5 live gate (owner)** follows (§16). Mechanical: the extension release guard refuses unless `daemon-v<MIN_DAEMON>` is a published non-draft release and `POST_DEPLOY.md` names it as last verified. |
| M5 | Release wiring underspecified | E5.S3, §9 | **Accepted.** (1) a separate `release-extension.yml` — `ReleaseWorkflowTests` pins `release.yml`'s triggers, `install.sh` trusts `release.yml@refs/tags/daemon-v…`, and the publish job's Environment secret must never be visible to daemon jobs; (2) jobs guard (tag == `package.json` version, commit on `main`, minimum daemon published) → build (`npm ci`, test, `vsce package` once, attest the `.vsix`; `id-token` / `attestations: write` only) → publish-marketplace (`environment: marketplace`, `contents: read`, skipped when `vsce show` already serves the version, `vsce publish --packagePath <attested file>`) → publish-github (`contents: write`, upload to release-please's draft, read back, check the set, make public) — **amended by §15h #0:** the GitHub part is split around the Marketplace, github-draft (upload, read back, check) BEFORE publish-marketplace and github-public (make public) after it, every job re-runnable; (3) its own `tags-extension.json` ruleset (`refs/tags/extension-v*`, the App the only bypass, its own probe) — `tags-daemon.json` untouched; (4) release-please package `src_vs_code` = `{component: extension, release-type: node, initial-version: 0.1.0}`, manifest `"src_vs_code": "0.0.0"`, `package.json` 0.0.0, `ReleaseConfigTests` updated; (5) `branch-main.json` lists the `ci-extension` job contexts (else `ReleaseConfigTests`' required-check test is red), the owner re-applies; (6) §9's "the win-x64 probe is downloaded … and bundled" struck for E5 (E7.S3). |
| M6 | The client's `wsl.exe` behaviour was assumed, not measured | E5.S1 | **Accepted.** Measure FIRST on this machine and record in `research/` (dates, WSL version), then a fake STRICTER than the real thing, with its own tests: the absolute `%SystemRoot%\System32\wsl.exe` (never `PATH`); encodings (`wsl.exe`'s messages and `--list` UTF-16LE, Linux stdout UTF-8); exit code + stderr of `--exec` on a missing `/opt/wsl-care/bin/wsl-care` (likely colliding with `RunFailed` = 1 — "not installed" only from the measured signature, else "unknown failure"); "GLIBC_2.38 not found" → unsupported distro; stderr carries Serilog lines + ANSI (strip; show only the `wsl-care:` line); per-verb timeouts above the daemon's ceilings (`preview`'s `system df` may take 2 min); whether killing `wsl.exe` ends the Linux process; at most one call per verb in flight; the runner seam takes `{file, args}` and the fake is a Node script (a `.cmd` cannot be spawned without a shell on current Node); the fake is wired only when `context.extensionMode === ExtensionMode.Test` and fails closed (Test mode without the fake spawns nothing); §15f #1's `--exec echo '$HOME'` vs `-- echo '$HOME'` comparison in the same measurement. |
| M7 | E5 depended on the unpublished kit | E5.S1–S2, E8 | **Accepted.** E5 does not depend on the kit; help / zoom / tone stay in E8. No escaper is needed: a static HTML shell with a per-render nonce (`crypto.randomBytes`), data by `postMessage`, DOM by `textContent` / `createElement`, no `innerHTML` and no `${…}` of daemon data (command lines, cwd and container names are attacker-settable by any distro process); the TS doctrine's `scriptInterpolation` scan test is copied before the first webview. If the owner insists on the kit before its npm 0.1.0: the kit's own `npm pack` tarball as a `file:` dependency at a recorded commit, switched to the registry in E8 with identical integrity asserted. Cross-epic for E8: kit `engines.node >= 20` vs `engines.vscode ^1.85.0` (Node 18.15) — E8 raises the floor (1.90+ runs Node 20) or proves the bundled kit runs on Node 18. |
| M8 | What leaks into the public `.vsix` was unchecked | E5.S3 (packaging), E5.S1 (CI steps) | **Accepted.** `.vscodeignore` as an allowlist (`dist/extension.js`, `media/**`, the icon, README, CHANGELOG, LICENSE, `package.json`); CI's `vsce ls --no-dependencies` equals a checked-in list exactly; CI unzips the `.vsix` and fails on drive paths `[A-Za-z]:\\`, `/home/`, `/mnt/`, `\\wsl`, the owner's Windows user name, e-mail addresses, and a build stamp not matching the package version; no source maps (or `sourcesContent` off); README screenshots from a SYNTHETIC fixture (never the live panel or the captured procfs / Docker fixtures); goldens, fixtures, `research/` and `.agents/` never packaged; no telemetry, no network; the "Windows + WSL only" notice is local. |
| m1 | The verb allowlist needs a structural test | E5.S1 | **Accepted.** One module exports a closed verb union; an import-graph test (only the runner imports `child_process`, only the client builds argv); a bundle scan for `-u` / `root`, `--timer`, `--confirm`, `--manual`, `config`, each with a planted-instance companion (not a bare-word scan for "act" / "collect"). |
| m2 | *Install daemon* under-specified | E5.S3 | **Accepted.** Built whole in E5.S3 (Fable-level): `curl -fsSL https://raw.githubusercontent.com/oleksandrdubyna88/wsl_care/refs/tags/daemon-v<MIN>/install.sh \| sudo sh -s -- --version <MIN>` (**amended by §15i C4:** the ref spelt in full, `refs/tags/…`, so no branch of that name can be served instead — the form observed answering 200 / 404 on 2026-10-04); a host modal with the command and the prerequisites (systemd, Ubuntu ≥ 24.04, gh ≥ 2.56.0) as text (`gh --version` is outside the verb allowlist); `createTerminal({shellPath: wsl.exe, shellArgs: ['-d', distro, '--cd', '~']})` (**§15i C4:** the user's home, observed) + `sendText(cmd, false)`; observe where a UI extension's terminal opens in a Remote-WSL window. |
| m3 | Polling could start the VM | E5.S2 | **Accepted.** When the distro is not running, no `-d` call at all (panel open included); an explicit "Start WSL and check" button; the race between the running check and the call is stated. |
| m4 | Plan drift | §7.6, §10, §12, §16 (this round) | **Accepted, fixed here.** §7.6's "copied from coai" superseded by E8's "no copied coai modules"; §10 no longer lists help / zoom / tone / settings for E5; §12's button state comes from `status`'s `running` block, not `running.json`; E6.S0's `daemon-v0.2.0` → "the next daemon minor"; daemon-side test-only commits use `test(daemon):` so they cut no release. |
| m5 | CI for the extension under-specified | E5.S1 | **Accepted.** `ci-extension.yml` with no path filter, on `windows-latest` and `ubuntu-24.04` (the non-Windows notice; xvfb for test-electron): setup-node pinned by SHA, `npm ci`, a clean `tsc` with `noEmitOnError`, lint, unit tests, page tests, `@vscode/test-electron` against 1.85.0 AND stable, `vsce package`, the allowlist + content checks, a 14-day artifact; `@vscode/vsce` pinned in devDependencies; Dependabot npm `/src_vs_code` ignoring `@types/vscode` major + minor; CodeRabbit path instructions; Sonar `c8` added or explicitly deferred. |
| m6 | The extension needs its own scenario harness | E5.S1 (harness), E5.S2 (rows), E5 live gate | **Accepted.** A TS scenario harness in the repository (`src/test/scenarios`: the extension host + the fake runner), flows derived from `contributes.commands` / views; `research/module_tests.md` gains an extension section; a test fails when a contributed command has no row; the real extension → `wsl.exe` → daemon path is a named live check in the E5 live gate. |
| m7 | Golden JSON for the client tests (§15f #10) | E5.S0 (writer + drift test + `head`), E5 live gate (freeze), E5.S1 (TS reads) | **Accepted.** A C# writer in `WslCare.Scenarios` (Linux legs only); volatile paths (`sampledAt`, `sampleMilliseconds`, `ageSeconds`, pids, …) normalised by a NAMED, reviewed list; a drift test fails when the checked-in `contracts/golden/head/*.json` is not current; `contracts/golden/daemon-0.1.0/` is frozen at the tag (an E5 live-gate step); the TS tests only read the files. |
| m8 | No rollback written down | E5.S3 | **Accepted.** Rollback = the next patch, or installing the `.vsix` from the GitHub release; that one command is written in the README's release section. |
| m9 | PAT vs OIDC chosen blind | E5 live gate | **Accepted.** The current state of Azure DevOps global PATs is checked before choosing `VSCE_PAT` over `vsce publish --azure-credential`. |
| m10 | Webview hardening | E5.S2 | **Accepted.** No `enableCommandUris`; `localResourceRoots` = `media/`; a closed message set (`refresh`, `installDaemon`, `openSettings`) validated in the host; nothing from the webview becomes argv. |

### 15h. Gate — epic 5 plan round (coai, qwen GLM-5.3, 2026-10-04)

The coai gate was back for E5's plan round (session `6687c653`): 5 findings, **every one Accepted**, each built in E5.S3
with a test seen red first (`research/module_tests.md`, *What each E5.S3 guarantee rests on*). Each row OVERRIDES the
section it names where they say otherwise.

| # | Finding | Lands in | Decision |
|---|---|---|---|
| 0 | Publish order and rollback: a Marketplace success before the GitHub upload could leave the rollback source missing | `release-extension.yml`, §15f #6, §15g M5 (2), `docs/repo-settings.md`, README, `POST_DEPLOY.md` item 6 | **Accepted.** The order is guard → build → **github-draft** (the `.vsix` + `.sha256` uploaded to release-please's DRAFT, read back, compared byte for byte) → **publish-marketplace** (idempotent skip as before) → **github-public** (the draft made public). Every job is re-runnable: the upload compares instead of replacing once the release is public, the Marketplace job skips a served version, making public is a no-op the second time — written in the runbook (`docs/repo-settings.md`, *Cutting `extension-v0.1.0`*) and the README's release section. `POST_DEPLOY.md` item 6 also fails on a Marketplace version with no matching GitHub `.vsix`. `ReleaseExtensionWorkflowTests` holds the order. |
| 1 | `preview --all --json` can outrun its 330 s ceiling on a machine with many containers, and then reads as a bare timeout | `WslCareClient`, `client/verbs.ts`, `failureText.ts`, `POST_DEPLOY.md` item 3 | **Accepted.** When `preview` times out and the newest `status` answered more running containers than the ceiling assumes (`PREVIEW_CONTAINER_ASSUMPTION` = 100, one `container inspect` batch), the outcome is `previewTooManyContainers` — "too many containers for a quick preview (<n>)". The count is RUNNING containers, a lower bound of what `preview` inspects. The real duration at the real count is measured at the E5 live gate, before the listing (item 3). |
| 2 | E5.S0's acceptance overclaims: a user-layer threshold does not change a CARRIED verdict at once | §16 E5.S0 row | **Accepted.** The acceptance line now reads: a changed user-layer threshold changes the verdict for the eight sample-evaluated thresholds; carried verdicts update at the next full run. |
| 3 | Whether killing `wsl.exe` ends the daemon AND its `docker` child was assumed | `research/2026-10-03_wsl_exe_facts.md` row 18 | **Accepted, observed 2026-10-04.** A linux-x64 build in `/tmp` (JIT, self-contained — the distro has no `clang` for AOT), started as the runner starts it, killed by its own `wsl.exe` pid only (twice: during `docker system df` and `docker system df -v`): **neither `wsl-care` nor the `docker` child survived** (0.5 s, 3 s, 10 s later). The relay's hang-up reaches the whole foreground group, so NO daemon-side cleanup is needed; the AOT binary itself is the E5 live gate's to confirm. `/tmp` removed. |
| 4 | Distribution names: the strict pattern refused names WSL itself reports | `WslCareClient`, the strict fake, their tests | **Accepted.** A name `wsl.exe --list --quiet` reports is accepted AS IT IS (argv reaches `wsl.exe` without a shell, so the listing is the authority); only a leading `-` is refused (it would be read as an option after `-d`). The strict pattern applies to the SETTING's value only, before any spawn. An unlisted setting is refused naming the listed distributions AND the pattern; the pattern refusal names the pattern (nothing was started, so no list is asked — the zero-spawn guarantee kept). The fake refuses a `-d` value starting with `-` and answers any other listed name. |

### 15i. E5 code round (coai qwen + two own reviews, 2026-10-04)

The code round over E5's whole diff: the coai gate (session `6687c653`, qwen) and two independent own reviews (security,
correctness), plus the owner's privacy finding. **Every item is Accepted**; each behaviour item was shown red first
(`research/module_tests.md`, *What each E5 code-round guarantee rests on*). Each row OVERRIDES the section it names.

| # | Finding | Decision | Landed in |
|---|---|---|---|
| A | **Privacy:** the committed goldens and the captured procfs / health fixtures carried the owner's Linux and Windows user names, home and profile paths, project and repository names, installed extension ids / versions and a scratchpad path — in a PUBLIC repository | **Accepted.** ONE identity list by shape, no original value in it (`WslCare.TestSupport/FixtureIdentity`: temp folder → `/tmp/x`, every user name → `user`, projects → `project-a`…, extension ids → `vendor.extension-a`…, e-mail), applied to the fixtures (`FixtureAnonymisationTests`, `WSL_CARE_ANONYMISE_FIXTURES=1`) and as part 4 of the golden writer's list; goldens regenerated in WSL; `FixturePrivacyTests` scans every `fixtures` / `golden` tree for foreign home / profile names, e-mail addresses and the running machine's user name; the SOURCE.txt files say so. The data before this remains in git history (main and the pull-request branches) — removing it needs a history rewrite and a force-push, the owner's decision | fixtures, `contracts/golden/head/`, `GoldenContracts`, `architecture.md` *Fixture privacy* |
| B1 | (#1) `journal.history` / `clock.drift` goldens carried placeholder prose in `value` | **Accepted.** Fixed to the concrete values at the capture (0.8 days; +0.19 s, ok, the product's sentence) by object rules | `GoldenContracts`, goldens |
| B2 | (#2, #5) the guard parsed `MIN_DAEMON_FOR_RENDER` from TypeScript with a sed pattern | **Accepted.** `scripts/bundle.mjs` emits `dist/min-daemon.json` (the module RUN, not matched); the checked-in `src_vs_code/min-daemon.json` is what the guard reads at the tag (python3 JSON), held equal to the constant by `minDaemon.test.ts` and by `check-vsix` (with the emitted one) | bundle, `check-vsix.mjs`, `vsixCheck.ts`, guard |
| B3 | (#3 + security #1) the build job held `id-token` / `attestations: write` beside `npm ci` scripts, the tests and a downloaded VS Code | **Accepted.** build = `contents: read`; a new `attest` job (sparse checkout of `.github/scripts`, download, verify the pair, attest; no npm); github-draft needs it | `release-extension.yml`, `ReleaseWorkflowTests`, `ReleaseExtensionWorkflowTests` |
| B4 | (#6) POST_DEPLOY item 6 read `versions[0]` and needed `unzip` | **Accepted.** Containment AND rank through ONE comparison, `.github/scripts/lib/versions.sh` (the guard's too); the `.vsix` read with python3's zipfile; post-deploy-check passes | `POST_DEPLOY.md`, `lib/versions.sh`, guard |
| B5 | (#7) the guard emitted `min_daemon` without a declared output | **Accepted — wired.** `outputs.min_daemon` declared and USED: the build runs `check-vsix --min-daemon` | `release-extension.yml` |
| C1 | (security #2) github-public made the draft public without comparing it with the attested build; "Re-run all jobs" could put rebuilt bytes beside a Marketplace serving the first | **Accepted.** github-public downloads the attested artifact and `cmp`s before `--draft=false`; github-draft never replaces an asset (uploads only what is missing) and refuses a difference naming "Re-run FAILED jobs only"; the rollback recipe runs `gh attestation verify` first | `release-extension.yml`, README, `docs/repo-settings.md` |
| C2 | (security #3) `install.sh`'s gh advice pointed a pinned install at main's installer with `--skip-attestation` | **Accepted.** ONE `rerun_command`: the pinned `refs/tags/daemon-v<version>` with the arguments given, printed as the LAST RESORT; the root re-run line uses it too (the same class, swept) | `install.sh`, `InstallFlows` |
| C3 | (security #4) the call log grew forever outside Test mode | **Accepted.** `clientRunner(testMode, …)` logs in Test mode only | `testApi.ts`, `extension.ts` |
| C4 | (hardening) the install URL could be ambiguous between a branch and a tag; the terminal opened wherever VS Code started | **Accepted.** `refs/tags/daemon-v<MIN>` (verified read-only with `curl -sI` on a cli/cli tag: 200, a missing one 404); `shellArgs ['-d', distro, '--cd', '~']` (observed: the user's home) | `installCommand.ts`, `WslCareClient.terminalTarget`, §15g m2 |
| D1 | a huge `refreshSeconds` overflowed `setInterval` (1 ms loop) | **Accepted.** Clamped to 86 400 (`MAX_REFRESH_SECONDS`), schema `maximum` | `poller.ts`, `package.json` |
| D2 | an unavailable PARENT (`vm`, `vm.memory`) read "update the daemon to see this" | **Accepted.** `jsonPath.unavailableAncestor`: "unavailable — <its reason>" in the panel, `?` + that reason in the bar | `viewModel.ts`, `statusBarModel.ts` |
| D3 | two `--version` calls could run at once (preview + doctor) | **Accepted.** The in-flight version is shared per distribution | `WslCareClient.ts` |
| D4 | `clientFlows` "one daemon call" asserted `>= 1` | **Accepted.** The exact call list (the verb, plus one `--version` for preview / doctor) | `clientFlows.test.ts` |
| D5 | `aria-live` on `<main>`, whose tree is rebuilt on every render | **Accepted.** The notice is the one live region, one element for the page's life | `panelHtml.ts`, `media/panel.js`, `panel.css` |
| D6 | docs drift (architecture activation / packaging / golden sets / `\u002B` / failed running check / "What exists"; README's starters of WSL; two stale comments) | **Accepted.** Fixed (docs only, no red needed) | `architecture.md`, README, this plan's E5.S0 row, `installDaemon.ts`, `catalogue.test.ts` |

### 15j. Gate — epic 6 plan round (own-agent stand-in for coai, 2026-10-04)

The coai gate's vendors were out of quota, so an own Plan agent reviewed the E6 plan in its place. Its verdict was "not
ready to build": 3 blocking, 10 major and 11 minor findings. **Every finding is Accepted**, with the coordinator's
decisions below; the column says where it lands. Each row OVERRIDES the section it names (§5, §6, §7.1–§7.4, §15f, §15g,
§16) where they say otherwise. E6 is re-split (§16): **E6.S0** (daemon read contract), **E6.S1** (daemon detach, stdin,
stop, units), the **E6 daemon live gate**, **E6.S2** (extension root boundary), **E6.S3** (buttons, durable cleanup state,
Last cleanup, Run full check now), **E6.S4** (Logs page) and the **E6 live gate**.

| # | Finding | Lands in | Decision |
|---|---|---|---|
| B1 | `act --preview` does not carry A4's full shown list: `ActionPreview.Items` keeps the first 20 (`MaxItems`), `Targets` is `[JsonIgnore]`, so a manual A4 confirm would pass 20 of 387 names | E6.S0 (daemon), E6.S2 (host) | **Accepted — the shown-list route, not a daemon-side preview token.** A4's outcome in `act --preview --json` gains an additive `shown: [64-hex…]` holding EVERY selected name (capped at `MaxShownVolumes` = 10 000, ≈ 650 KB); A4 only (A5 / A6 / A7 re-select live, §15f #11). A golden and a derived test hold `shown.length == preview.count`. The host validates each name `^[0-9a-f]{64}$` before sending it back through `--only -` (stdin). A preview still writes no state (§15b #3). |
| B2 | Detach is REQUIRED (§15f #9): measured, `wsl-care` dies with `wsl.exe` (JIT; AOT is a live-gate check and the design must not depend on it); `ShutdownSignals` has no SIGHUP; a reload mid-A4 kills `docker volume rm` with no detail or history written; the runner's mandatory kill-on-timeout cannot coexist in-process with "a confirm is never time-killed" | E6.S0 (SIGHUP), E6.S1 (detach) | **Accepted.** `act <ids> --confirm --manual --detach [--only -] --json` (root) does every pre-lock check (root, built, side, observe-only, the shown list read and validated, running → 75 / 76 / 79), allocates the runId, writes `{state}/requests/<runId>.json` (0644: ids, trigger, shown list, created) = the persisted QUEUED state, runs `systemctl start --no-block wsl-care-act@<runId>.service` (a declared `CommandTemplate` with a closed unit slot) and returns `{result: "accepted", runId, productVersion}` at once. A shipped TEMPLATE UNIT `wsl-care-act@.service` — not `systemd-run`, a generic wrapper the never-list forbids — with the hardening of `wsl-care.service` (`MemoryMax=1G`, `NoNewPrivileges`, `Nice` / IO class), `TimeoutStartSec=infinity`, `KillMode=control-group`, `ExecStart=/opt/wsl-care/bin/wsl-care act --request %i`. `act --request <runId>` runs the engine under the pre-allocated runId; meeting the lock or a wedged run writes a TERMINAL history line with a new outcome `refused` and its reason, and removes the request (never a silent Busy). The request sweep is OWNERSHIP-checked: the unit inactive per `systemctl is-active` AND the request older than N minutes → reconciled `interrupted`. **No synchronous fallback:** without systemd, `--detach` refuses with its own exit code (69, `EX_UNAVAILABLE`) and the panel says "needs systemd". SIGHUP is registered as a cancellation (defence in depth: a terminal `act --confirm` cut off records `interrupted`). The extension's runner timeout then applies only to the short detach call. |
| B3 | Release order: if E6's extension code merged before `extension-v0.1.0` were tagged, release-please would make the FIRST public extension root-capable (§15f #5) | §16 (E6 rows), E6.S2 (the check) | **Accepted, as the coordinator clarified it:** the rule protects the FIRST PUBLIC EXTENSION from carrying a root path. **The DAEMON parts of E6 (E6.S0, E6.S1) may merge to `main` at any time:** if they merge before the owner cuts `daemon-v0.1.0` they ride 0.1.0 (as E5.S0 did) and `minDaemonForActions` is 0.1.0; otherwise they ride the next daemon minor and `minDaemonForActions` is that minor. **Only the EXTENSION parts (E6.S2–E6.S4) wait:** they merge to `main` only after `extension-v0.1.0` is tagged from E5's merge commit (E5 live gate step 5). Mechanical check (E6.S2): `ReleaseConfigTests` (or the extension release guard) refuses an extension release whose bundle contains the root module while `.release-please-manifest.json` shows `src_vs_code` at 0.0.0. |
| M1 | Evolving the E5 bundle scan without weakening it | E6.S2 | **Accepted.** (1) Source level: ONE module `src/root/rootCall.ts` is the only one spelling root argv words (`-u`, the `root` argv element, `--preview`, `--confirm`, `--manual`, `--detach`, `--only`, `-`, `act`, `collect`, `--stop`); it builds argv from a closed `ROOT_OPS` union and spawns only through the runner seam; an import-graph test: only the host-side cleanup controller imports it, no webview / page module. (2) Bundle level: esbuild unminified keeps `// src/root/rootCall.ts` headers — the bundle is partitioned by them; the scan fails if the root marker is missing or minification is on; every region but root keeps today's full FORBIDDEN set; the root region's argv-shaped literals equal an exact expected set; `--timer`, `--user`, `config` stay forbidden EVERYWHERE, root included; prose "root" only as exact literals from the failure-text table (like the sudo allowlist); planted companions (`-u` in another region; `--timer` in root; an extra literal in root; a stripped marker). (3) The "read-only" wording of `package.json`'s description, the README and the `vsce ls` allowlist is updated. |
| M2 | `--only -` (stdin) must be buildable | E6.S1 (daemon), E6.S2 (runner seam, fake) | **Accepted.** `CommandLine.NeedsValue` treats `-` as missing → `-` accepted as the stdin marker; stdin read with a byte cap (1 MiB + 1 = refused) AND a time ceiling (10 s without EOF = refused), the same line validation that never echoes, all before the lock; with `--detach` the list is persisted into the request file. The runner seam gains an optional `stdin: Buffer` (written, then ended); the strict fake accepts stdin only for the root `act … A4 … --only -` shape. Facts-note rows: the byte-exact relay through `wsl.exe`, EOF, 650 KB, stdin with `WSL_UTF8=1` inherited. |
| M3 | The `running` block and `runs show` were undecided | E6.S0 | **Accepted: `runs show` BUILT, `runs log` CUT.** `status.running {state: none \| queued \| live \| wedged \| dead \| unknown \| unreadable, action, current, runId, trigger, startedAt, heartbeatAt, heartbeatAgeSeconds, reason}`; `status` is unprivileged and NEVER sweeps (dead is reported, not cleaned); queued is read from the request files. `runs show <runId> --json` answers queued / running / done (full detail: every removed and not-removed object, the commands, their exits) / refused / interrupted / unknown. §7.4's raw-log link becomes "commands and exits" from `runs show`. |
| M4 | No way to stop a wedged run | E6.S1 (daemon), E6.S3 (button) | **Accepted.** `act --stop <runId>` (root): only when `running` is `wedged` for that runId AND `/proc/<pid>/cgroup` names `wsl-care.service` or `wsl-care-act@<runId>.service`; it runs `systemctl stop <that unit>` (a declared template, closed unit slot) → SIGTERM → cancellation → `interrupted` recorded. Never a kill by pid from the extension or the daemon. A wedged run outside our units gets text with its pid and no button. |
| M5 | One minimum daemon is not enough | E6.S2, the E6 live gate | **Accepted.** `min-daemon.json` gains `minDaemonForActions` (the daemon minor that carries E6.S0 + E6.S1, per B3), emitted from the bundle; the extension release guard requires that release published and stamped; *Install daemon* / *Update daemon* type the actions minimum. The AUTHORITY for acting is `status.capabilities` (`act.detach`, `act.onlyStdin`, `act.shownList`, `runs.show`, `running.block`, …) — the version only supplies the message; an `unknown` / `0.0.0+sha` daemon may act only if it advertises the capabilities. `install.sh` refuses to upgrade while `running` is live or queued (or waits, bounded). |
| M6 | Polling during a cleanup | E6.S3; the OWNER | **Accepted; the churn decision is the OWNER's and stays OPEN** (with §15g M1). Poll only while `running.state ∈ {queued, live}` or a runId this window started has no terminal answer; every 3–5 s; `status` only; it stops itself; one `runs show` at the terminal state. The extra run-log churn goes in front of the owner with the M1 numbers ([2026-10-04_extension_poll_churn.md](../research/2026-10-04_extension_poll_churn.md)) — or `status` (or a new `status --running`) stops opening a run log, as a recorded family-rule exception. Not decided here. |
| M7 | The Logs page and *Last cleanup* asked for what the CLI does not answer | E6.S0 (daemon), E6.S3 / E6.S4 (pages) | **Accepted.** UTC days: the additive INSTANT range `logs` / `runs --from <RFC3339> --to <RFC3339>` (preferred, decided in E6.S0) — the host sends the local-midnight instants. *Last cleanup*: `status.lastCleanup {runId, startedAt, trigger, freedBytes, count}`. "Docker after": `preview --all` totals re-read after the run, labelled with their time (or cut from §7.2). The `MemAvailable` sparkline / swap trend: additive `metrics` on `RunLine`. `vmmemWSL` max / min → "arrives in E7.S3 / E11". Object lists lazily through `runs show` on expand; `detailsNotRead` shown. The date picker clamps to the 90-day retention. |
| M8 | The webview protocol carried a token the host did not need | E6.S3 | **Accepted.** `previewToken` dropped; the webview sends `{type: 'clean', rowIds: RowId[]}` with a compiled closed `RowId` enum; preview → host modal (`showWarningMessage({modal: true})` + a second modal for A5, A6Unused, A8, A11, A12) → confirm is ONE host transaction. A runId from the webview is an index into data the host read itself, still checked against the runId pattern; a period is built host-side from validated real calendar `yyyy-MM-dd` parts. Control and bidi characters are stripped and attacker-settable names truncated before they reach the native modal. m10's closed message set is extended, with tests and planted companions. |
| M9 | *Run full check now* | E6.S1 (daemon), E6.S3 (button) | **Accepted.** = `collect --detach` (request kind `collect`, never `--timer`), followed through `running`. |
| M10 | Models | §16 | **Accepted, decided before coding.** E6.S0, E6.S1 and E6.S2 run on **Opus** (Fable intended; its monthly limit is spent), each followed by **two independent own reviews** (security / confused deputy; durable state / crash paths) besides the epic's gate round; the substitution is recorded in each story row. |
| m1 | schemaVersion | §6, every E6 story | **Accepted.** `schemaVersion` stays 1 for every addition (`actions`, `capabilities`, `running`, `lastCleanup`, `act.productVersion`, A4's `shown`, the result `accepted`, the outcome `refused`, `RunLine.metrics`, `runs show` with its own `schemaVersion` 1); §6 adds "an unknown enum value reads as unknown, never a crash" with a TS test (E6.S2); `--detach` stays explicit — making it the default for `--manual` would be breaking. |
| m2 | `--manual` and `--timer` together | E6.S0 | **Accepted.** Mutually exclusive in E6.S0 (refused with a usage message); the fake refuses both. |
| m3 | Exit codes | E6.S0 (contracts), E6.S1 (69), E6.S3 (texts) | **Accepted.** 69 = "detach unavailable (no systemd)"; `contracts/exit-codes.json` and `contracts/actions.json` (ids incl. `A5Testcontainers` / `A6Unused` and the `ExecutionOrder`) held equal to `ExitCode` / `ActionId.All` by a daemon test, read by TS; distinct UI text for 1 (non-relay), 2, 3, 4, 70, 75, 76, 77, 78, 79, 130 and `wsl.exe` −1. |
| m4 | The root check | E6.S2 | **Accepted.** `-u root --cd / --exec /opt/wsl-care/bin/wsl-care --version` (not `--exec true`), cached per session; a refusal greys the buttons "needs root". |
| m5 | Untrusted workspaces | E6.S2 | **Accepted — decided in writing there:** keep `untrustedWorkspaces.supported: true` with the written reason (no workspace input reaches root; every setting has application scope). |
| m6 | The template unit's plumbing | E6.S1, the E6 daemon live gate | **Accepted.** `wsl-care-act@.service` joins `install.sh`'s UNITS (install / uninstall), the archive member list (the packaging test) and `ShippedFilesTests`; the `requests/` folder and its retention; `SuccessExitStatus=3 75` decided there; the unit's properties observed on the live install (`POST_DEPLOY`: `systemctl show -p KillMode,TimeoutStartUSec,MemoryMax wsl-care-act@x`). |
| m7 | Plan drift | §6, §7.1, §7.3, §7.4 (this round) | **Accepted, fixed here:** §6's CLI table (`act` with exactly one of `--preview` / `--confirm`, the period shapes as built, `runs show` built and `runs log` cut), §7.1 (`--exec`, not `wsl.exe … -- …`; the old root-only list superseded by §15c #0), §7.3 "from `running.json`" → "from `status.running`", §7.4's raw-log link → `runs show`. |
| m8 | Preview expiry and wording | E6.S3 | **Accepted.** Host-side: the preview expires after 5 minutes, A5 / A6 / A7 say "re-checked at run time", A4 alone is bound to its list; both tested. |
| m9 | One root operation at a time | E6.S2 / E6.S3 | **Accepted.** One root operation in flight per distribution (host-side serialisation); the daemon lock stays the authority; a 75 shows the `running` block. |
| m10 | Starting before the E4 stamp | §16 | **Accepted.** E6.S0 / E6.S1 code may start against the scenarios before the E4 live gate's stamp; only releases and live measurements need it. |
| m11 | The fake and a synchronous confirm | E6.S2 | **Accepted.** The strict fake REFUSES a root `act … --confirm` without `--detach`. |

**E6.S0 review round (two own reviews standing in for coai — security; durable state — 2026-10-04).** Every finding
Accepted unless marked; each fix landed with a test seen red first for the real reason (`research/module_tests.md`, *The
E6.S0 review round*).

| # | Finding | Disposition |
|---|---|---|
| S1 | `RunRequests` read with `ReadFile`: no cap, followed links, blocked on a FIFO, no owner / mode check, content unvalidated | **Fixed.** `IFileSystem.ReadStateFile` → `RegularFiles.ReadOwned`: `O_NONBLOCK` + `O_NOFOLLOW` (0x20000 x86-64, 0x8000 arm64), type, owner and mode from ONE `statx` of the open descriptor — regular, uid = the state's owner (0 on a machine; a sandbox's own euid, `PhysicalFileSystem.TrustedStateOwner`), no g+w / o+w — at most 1 MiB; content validated (schema 1, kind `act` / `collect`, known ids, `["collect"]` for a collect, every shown name 64-hex, ≤ `ShownList.MaxNames`); at most 64 request files read, the rest named. |
| S2 | `Unknown` dropped the run id, so `runs show` of a live run with an uninspectable pid answered "never existed" | **Fixed.** `RunningStatus.Unknown` keeps the file; the block names the run; `runs show` answers `running` with the `unknown` block. |
| S3 | the dead state published a pid that is gone or another process's | **Fixed.** `pid` (and `heartbeatAgeSeconds`) only for `live` / `wedged`. The dead golden changed accordingly. |
| S4 | `RunId.TryParse` accepted a pid with a leading zero (two ids for one run) | **Fixed.** Canonical spelling only. |
| D1 | identity by `Process.StartTime` ± 2 s: a wall-clock step (hundreds per 4 h here; A16 steps it) made a live run read dead; the heartbeat age used the wall clock | **Fixed.** `running.json` gains `startTicks` (`/proc/[pid]/stat` field 22), `bootId` and `heartbeatMonotonicMs` (additive); identity = same boot id + exact ticks, another boot = dead; the age is monotonic within one boot; the wall clock only where a side cannot tell (older files, Windows). |
| D2 | an interrupted record held neither the action in flight nor its confirmed deletions; `DockerRemovals` threw the confirmed batches away | **Fixed.** `RemoveAsync` returns a partial result on cancellation (the batch in flight "unknown: cut off mid-command", the rest "not attempted"); A4 / A5 return it as an `Interrupted` run (A4 leaves `volume-seen.json` as it was — the next look drops the removed names); the engine records the in-flight action `interrupted` (with its removals when it returned them) and every requested action that never ran `interrupted / not run`; `logs` counts an interrupted action's real deletions. The SIGHUP flow asserts the in-flight A10 `interrupted` (it was green with the bug). |
| D3 | `status` said "dead, nothing recorded it yet" for a run whose line exists | **Fixed — `none`** with the reason "recorded itself as <outcome>; only its running.json is left" (chosen over `dead`: nothing is in flight). |
| D4 | read-order races (`runs show` history → running → request, `status` request read finding nothing) | **Fixed.** `runs show` reads request → `running.json` → history (the order states move) and answers the most advanced; `status` reads `running.json` again when the requests show nothing; a request gone as it is read is skipped, not bad. **For E6.S1:** write `running.json` BEFORE removing the request. |
| — | *(record only)* a `collect` cancelled during measurement leaves no record (`CollectRun` throws, the `finally` removes `running.json`, `runs show` then says unknown); SIGHUP now joins that path and M9's `collect --detach` stopped by `systemctl stop` will hit it | **On E6.S1's list.** |
| coai #11 | (from §15k, cheap here) a preview over the cap | **Fixed in this round:** A4's outcome carries `shownTruncated: true` when `count` > 10 000 (absent otherwise); `count` stays the total. |

### 15k. Gate — coai epic 6 plan round (2026-10-04)

The coai gate was back for E6's plan round (two reviewers, verdict `good_enough`): 20 findings, **every one Accepted** by
the coordinator with the decisions below, which are binding. Each row OVERRIDES what it names (§6, §15j, §16) where they
say otherwise. **The gate operator's commands were applied:** E6 is built as ONE unit (no further re-split); the autonomous
six orders hold (red-green, docs, every test, PR, deploy, re-read); consultants are asked before the person; the risky items
are named — **E6.S1 (detach and request trust)** and **E6.S2 (the root boundary)** — and each gets a risk consultation before
its code round.

| # | Finding | Lands in | Decision |
|---|---|---|---|
| 0 | (Major) a hung child against "a confirm is never time-killed" | §6, E6.S1 | **Accepted.** Per-COMMAND ceilings with tree kill stay inside the engine (every `CommandTemplate` already has one); only the whole-run timeout is dropped (`TimeoutStartSec=infinity`). A command that hits its ceiling records that step failed / interrupted, the run goes on or ends with a record, the lock is released. §6: `wedged` = "heartbeat older than 30 s", the heartbeat being the timer thread — per-command ceilings bound any hang, so `live` cannot last forever. E6.S1 scenario: a fake docker that never exits → a recorded result, the lock released. |
| 1 | (Major) an orphaned request when the unit fails to start | E6.S1 | **Accepted.** `--detach` writes the request atomically (temp + rename, `O_EXCL` on the final name), then `systemctl start --no-block`; if the start fails it removes the request and exits with a DISTINCT code, named in `contracts/exit-codes.json`. Scenario: a fake systemctl failing the start → no request file, a non-zero exit. |
| 2 | (Major) the sweep's double terminal line / a still-pending job | E6.S1 | **Accepted.** The sweep FIRST checks the history for that run id — a terminal line means it only deletes the request file; a request is still pending while `systemctl show -p Job` (or `list-jobs`) shows a queued job, not only by `is-active`. `act --request` with a missing request is a no-op exit with a named code (no history line). Scenarios for both. |
| 3 | (Major) a detach timeout is an unknown outcome | E6.S2 / E6.S3 | **Accepted.** The host treats a detach timeout or kill as "outcome unknown" and follows `status.running` (queued / live) for a bounded period before it reports; the detach timeout comes from a MEASURED cold-boot time. E6.S3 scenario: the fake accepts, the runner times out, the panel shows the run. |
| 4 | (Major) run ids only in memory, an unbounded poll | E6.S3 | **Accepted.** The host persists started run ids in `globalState` until a terminal answer was shown; `unknown` from `runs show` is terminal for polling; a hard poll ceiling (30 min) then "state unknown" with the run id. Scenarios: refused → reload → the reason shown; unknown stops the poll. |
| 5 + 13 | (Major + Blocking) the distro may stop after the last `wsl.exe` exits | E6.S1 (first item), the E6 daemon live gate, E6 live gate | **Accepted.** A MEASUREMENT is the first item of E6.S1 / the E6 daemon live gate and is recorded in `research/` BEFORE E6.S2 / E6.S3 build on the reload promise: start a long unit through the exact `wsl.exe` call, close every `wsl.exe` client, wait past the idle timeout, record whether the unit (and the distro) survive. If not: a keep-alive (bounded, only while `running` is live / queued), or the promise narrowed to "survives a reload, not a close" with an explicit `interrupted` reconciliation at the next boot; the E6 live gate gains a close-VS-Code case. E6.S1 still builds (detach is needed for the reload case either way). |
| 6 | (Major) the stdin relay was unmeasured | facts note, the E6 daemon live gate | **Accepted — measured** by the coordinator on 2026-10-04: Node 24 spawning `%SystemRoot%\System32\wsl.exe -d Ubuntu --cd / --exec /usr/bin/wc -c` and writing 650 000 bytes (10 000 × 64-hex + LF) then `end()` → 650 000 received, exit 0, ~250 ms; the same with `WSL_UTF8=1` inherited. Recorded as row 20 of [2026-10-03_wsl_exe_facts.md](../research/2026-10-03_wsl_exe_facts.md) (the M2 relay row); re-checked at the live gate with `-u root`. |
| 7 | (Major) B3's check keyed on the manifest | E6.S2 | **Accepted.** Keyed on TAGS: the release check refuses when the release tag is `extension-v0.1.0` or earlier, or no `extension-v0.1.0` tag exists yet, while the bundle contains the root marker; tests with a 0.1.0 and a 0.2.0 fixture. Supersedes §15j B3's manifest wording. |
| 8 + 17 | (Major) `SuccessExitStatus`, and growth | E6.S1 | **Accepted, decided now.** Recorded refusals (75 / 76 / 79 / `refused` / the missing-request no-op) are success exits in `SuccessExitStatus`; `act --request` runs `systemctl reset-failed` for its OWN instance on a non-success end (a declared template, closed slot), or the sweep does. **Growth:** a request ≤ 1 MiB; ≤ 32 request files (newer ones refused at detach with a code) — ≤ 32 MiB in the folder; deleted on every terminal path, the sweep included; the sweep's N = 15 minutes; failed unit instances reset; the units' journal follows journald's own limits. |
| 9 | (Minor) hardening drift between the units | E6.S1 | **Accepted.** `ShippedFilesTests` asserts the hardening key / value sets of `wsl-care.service` and `wsl-care-act@.service` are equal, with a companion proving it reads the keys. |
| 10 | (Minor) bare words in the bundle scan's FORBIDDEN set | E6.S2 | **Accepted.** The scan matches multi-character flags and argv-shaped arrays / spawn arguments only; `-`, `act`, `collect` are guarded by the import-graph test and the root region's exact-literal equality — stated as deliberate (amends §15j M1). |
| 11 | (Minor) the cap against the count | E6.S0 (review round), E6.S2 / E6.S3 | **Accepted.** A4's preview reports `count` = the total selected and `shown` capped at 10 000 with `shownTruncated: true` (absent otherwise); invariant shown.length == min(count, 10 000); the modal says only the shown names will be removed. **Built in the E6.S0 review round** (`ShownList.Truncates`); the golden asserting the rule past 10 000 is E6.S2's. |
| 12 | (Minor) the preview's expiry against the modals | E6.S3 | **Accepted.** The host re-checks the preview's age after the LAST modal resolves, immediately before the detach call; expired → re-preview and show the new count. A test with a frozen clock. |
| 14 | (Major) the request files' ownership | E6.S1 | **Accepted.** `requests/` is `root:root 0755`, created by root (mode stated); files are created `O_EXCL` 0644; `act --request` reads through the SAME hardened reader as `status` (the E6.S0 review round's S1: uid 0, no g / o write, the size cap, validated content) and re-validates the shown list. Acceptance tests with planted non-root-owned and group-writable requests. |
| 15 | (Major) N undefined, the sweep's invoker | E6.S1 | **Accepted.** N = 15 minutes; the sweep runs at the start of every root `collect` (timer or detached) and every `act --request`; `status` stays read-only. Scenario: a unit that failed at once → `interrupted` within N, the poll stops. |
| 16 | (Major) `install.sh` upgrading under a running unit | E6.S1 | **Accepted.** `install.sh` installs the binary by atomic rename (never an in-place overwrite); the bounded wait is 10 minutes, then it REFUSES with a non-zero exit and a message naming the run; the request schema stays backward compatible (schemaVersion 1, additive), so a queued request survives an upgrade — a scenario. |
| 18 | (Major) the stop path | E6.S1 | **Accepted.** `wsl-care-act@.service` and `wsl-care.service` get `TimeoutStopSec=90`; a stop that escalates to SIGKILL leaves `running.json`, and the next root run's sweep records `interrupted` with the reason "stopped: did not exit within 90 s of SIGTERM". A scenario where the fake ignores SIGTERM. |
| 19 | (Minor) the host's timeouts | E6.S2 / E6.S3 | **Accepted.** Each host call's timeout is stated (status, preview, runs show, detach incl. stdin), and the user-visible result when the daemon's 10 s stdin ceiling trips: a refusal with its reason, no request created, a retry offered. |

### 15l. E6.S1 review round (two own reviews standing in for coai — security; durable state — 2026-10-04)

Every finding was ACCEPTED and fixed red → green → red (the fix's load-bearing line reverted: red with the real symptom;
restored: green) — the record is `research/module_tests.md`, *The E6.S1 review round*. The coai code round is still to come.

| # | Finding | Disposition |
|---|---|---|
| S1 (Medium) | the request's temporary file was created 0666 − umask, filled, then chmodded 0644 — under a loose root umask another account could open it for writing in between; `stops/` was made with a plain `CreateDirectory`; the `WriteFileAtomically` temporaries the same | **Fixed.** Every temporary is created 0600 (`FileStreamOptions.UnixCreateMode`; the umask can only tighten it), written, flushed, THEN made 0644, then linked / renamed. Every folder `PhysicalFileSystem` makes is created 0755 level by level (`Directory.CreateDirectory(path, mode)` gives the mode to the LEAF only — the state directory itself came out 0777 under umask 000, found by the new flow); the history, the lock files and the run logs are created 0644 at most. Flow: `DetachFlows.Under_umask_000_nothing_the_daemon_writes_is_group_or_world_writable` (a detach, a confirm, an `act --stop` under `umask 000`; nothing under the state directory group / world writable) and the temporary's mode observed at the write step (0600). |
| S2 (Low) | the content check pinned neither the trigger nor the creation — a `{kind: collect, trigger: timer}` request would run the ACTING timer pass; a future `createdAt` was never swept and held a budget slot forever | **Fixed.** The reader refuses a trigger root never writes (`collect` → `manual` only; `act` → `manual` / `cli`); a request stamped ahead of the monotonic clock, or (unstamped) more than 5 minutes ahead of the wall clock, is stale — asked of systemd, never held. |
| S3 (Low) | `act --stop` compared only the last cgroup component — a user's own `systemd --user` unit named `wsl-care.service` matched | **Fixed** (`RunStops.UnitOf`): the whole `0::` path must be `/system.slice/wsl-care.service` or `/system.slice/system-wsl\x2dcare\x2dact.slice/wsl-care-act@<runId>.service`. `act --stop` moved to its own file, `RunStops.cs`. |
| S4 (Low) | `install.sh`'s wait failed OPEN when `status` timed out or crashed, and did not wait on `wedged` | **Fixed.** No answer (a failure, a timeout, an empty answer) counts as in flight and is retried inside the same 10-minute bound, then refused naming it; only an answer WITHOUT a running block (a binary older than E6.S0) proceeds; `wedged` is waited on. |
| minor | a failed `mv` left `wsl-care.new`; uninstall did not remove one | **Fixed** — removed on the failed rename and by uninstall. |
| D1 (HIGH) | an orphaned request (a distro stop dropping the job, a detach killed between the request and the start, a job failure, `act --request` SIGKILLed before `running.json`) held `queued` and refused every `--detach` — the panel's only remedy — and every upgrade for up to ~4 h 15 min, because only a root `collect` or `act --request` swept | **Fixed.** `--detach` takes THE run lock (busy → 75, or 76 when a wedged run holds it), sweeps the request folder under it, then asks the budget and the running state and writes the request; the lock is released before `systemctl start` (the run takes it itself). The grace is 60 s on the MONOTONIC clock (deviation from §15k #15's 15 minutes): a request whose unit is inactive with no job is stale after it, or at once when written in an earlier boot. `install.sh`'s refusal names the remedy. |
| D2 (Medium) | a signal during the request sweep lost the run's only record — `act --request` removed its request bare on the cancellation (no line, no `running.json`, no request: `runs show` = unknown for an accepted run); in `collect` the sweep ran outside the cut-off guard (zero lines) | **Fixed.** The sweep's `systemctl show` runs uncancelled (it has its own ceiling); a cancellation of `act --request` / a detached collect keeps the record — the request goes only after the run has a line, else ONE `interrupted` "cut off before it started" line first; `collect`'s sweep sits inside a guard that records "interrupted by … while it swept the request folder". |
| D3 (Medium) | the 15-minute age was wall clock — a forward step swept a request in its pre-enqueue window (a false `interrupted`, the confirmed click lost), a backward step held a stale one | **Fixed** as E6.S0's D1: the request carries `bootId` and `createdMonotonicMs` (additive, schema 1); within one boot it is aged by the monotonic clock; another boot is stale at once (still asked of systemd); only an unstamped request falls back to the wall clock. |
| D4 (Medium) | the sweep's history check was a snapshot — a run could record `refused` between it and the unit's look, and get a second terminal line `interrupted` | **Fixed** — the history is read again for that run after `systemctl show` says the unit is done (check after observe, as `RunningSweep.SweepDead` does). |
| D5 (Minor) | a request the reader refuses was never removed — status `unreadable` and every detach 79 forever (e.g. after `install.sh --version <older>`) | **Fixed** — the root sweep and `act --request` record it `refused` with the reader's reason, then remove it. |
| D6 (Minor) | a `systemctl start --no-block` that TIMED OUT removed the request and answered 71, though the job may have been queued | **Fixed** — the unit is asked first: busy → `accepted`; done → removed, 71; unreadable → the request stays for the sweep and the answer is `result: unknown` (exit 0; §15k #3 — the panel follows `status.running`). A refusal or a `systemctl` that never ran is certain and still answers 71. |

### 15m. Gate — coai epic 6 code round (2026-10-05)

Verdict **proceed** (qwen + qwen-2). **What the gate did and did not inspect:** 4 of 8 reviewers answered — BOTH
SecurityReliability reviewers timed out — and the API reviewers saw a SHAPED diff with `DetachedRuns.cs`, `RunRequests.cs`,
`RequestSweep.cs`, `RunningState.cs`, `PhysicalFileSystem.cs` and `RegularFiles.cs` ELIDED: the gate did not read the detach, the
request reader, the sweep, the running state or the file system. Those files are covered by the two own reviews of §15l only.
All 9 findings ACCEPTED and fixed red → green (`research/module_tests.md`, *The coai E6 code round*):

| # | Finding | Disposition |
|---|---|---|
| 0 (Major, conventions) | the parsed `RunId` was discarded and the raw string travelled in `ActFromRequest` / `ActStop` / `RunsShow` | **Fixed** — the three requests carry the typed `Core.Records.RunId`; `.Text` only at the unit-name and answer seams. A type change: the compiler is its test (the old shape no longer compiles against the parse tests). |
| 1 (Major, architecture) | `run_in_flight` grepped exact indentation and only `live|queued|wedged` — a new state silently disabled the wait | **Fixed** — the answer is flattened and the running block's `state` read without layout; in flight UNLESS `none` / `dead` (no running block = an older binary → proceed; an unreadable block → in flight). `InstallFlows` puts EVERY running-state golden (`status*.json`), indented and compact, through the real guard, and a test fails when a `RunningStateName` has no decision. |
| 2 (Minor) | `act --request` / `act --stop` answered a generic message for a bad id | **Fixed** — one helper, `CommandLine.RunIdVerb`, for the three verbs: `"<value>" is not a run id (yyyyMMddTHHmmssZ-<pid>, as runs and logs print it)`. |
| 3 (Minor) | `Request.Logs` / `Runs` carried `Period = "today"` in the instant-range mode | **Fixed** — empty in that mode (`CommandLine.PeriodOf`); the invariant is documented on the records. |
| 4 (Major, UX) | the upgrade wait was silent for up to 10 minutes | **Fixed** — `still waiting: <state> <runId>, <elapsed>s of <ceiling>s` every 30 s (`WSL_CARE_INSTALL_PROGRESS_SECONDS`). |
| 5 (Major, UX) | when the installed binary gives no status answer, the advice ran the same broken binary | **Fixed** — that refusal names the manual escape: remove `/var/lib/wsl-care/running.json` and `requests/*.json` by hand, or `WSL_CARE_INSTALL_SKIP_RUN_WAIT=1`. **Deviation:** the finding suggested `WSL_CARE_INSTALL_RUN_WAIT_SECONDS=0` as the skip; 0 already means "refuse at once" (and the tests use it), so the skip is its own variable. A real live / queued / wedged run keeps the old advice. |
| 6 (Major, UX) | a request written in an EARLIER boot reported `queued` until a root run swept it | **Fixed** — `RunningReports` compares the request's `bootId` with `ProcessTable.Boot()` and reports it `dead` ("written in an earlier boot; systemd never started it - the next root run records it interrupted"); `runs show` answers it `interrupted`; status stays read-only. Golden `status-running-earlier-boot.json`. |
| 7 (Major, perf) | every status poll read and validated every request file (up to 32 × 1 MiB) | **Fixed** — `RunRequests.Peek`: ordered and counted by FILE NAME, only the oldest read through the hardened reader (the next one only when the oldest cannot be used). The full read stays with the sweep and `act --request`. With 32 queued, status reads ONE file. Consequence: when the oldest two are unusable the state is `unreadable` even if a later one parses. |
| 8 (Minor) | the 600 s budget counted the 5 s sleeps, not wall time | **Fixed** — measured with `date +%s`; the refusal comes at the advertised ceiling. |

### 15n. E6.S3 review round (coai code round + two own reviews, 2026-10-05)

The coai E6.S3 code round answered **proceed** with 7 of 8 reviewers: 26 findings, **25 accepted, #11 rejected**. Two own reviews
(security; durable state) added A1–A5 and B1–B5. Every accepted finding was fixed test-first — the test seen red for the real
symptom, the fix, green, the fix's load-bearing line broken and seen red again (`research/module_tests.md`, *The E6.S3 review
round*).

| # | Finding | Disposition |
|---|---|---|
| A1 (Medium, security) | daemon text reaches `showInformation/Warning/ErrorMessage`, whose markdown turns `[label](command:id)` into a clickable command | **Fixed.** `text/safeText.ts` `noticeText` breaks `](` after the sanitiser; `cleanupHost.ts` wraps every cleanup surface in it (one road), *Install daemon*'s report takes it too; `safeText` also replaces U+2028 / U+2029, LRM / RLM / ALM and the zero-width characters |
| A2 | `clean` took several row ids while its modal named one | **Fixed** — `clean` carries exactly one; several are `cleanSelected`'s |
| A3 | the first modal was built from the daemon's `actions` while the confirm acts on `ids` | **Fixed** — one block per confirmed id; a preview not describing every id is NOT confirmed (told) |
| A4 | *Run full check now* was not checked host-side | **Fixed** — refused when the controls grey it |
| A5 (+ coai #20) | a journal `since` was shape-checked only (2026-13-01 → NaN → a RangeError ended the whole loop; a future instant never aged) | **Fixed** — the instant must exist and lie ≤ 5 min ahead; a NaN age is past the ceiling; each entry is settled in its own `try` |
| B1 (Important) | an entry past the ceiling ended "state unknown" on the first poll whose status did not answer (VS Code before WSL) | **Fixed** — the ceiling ends an entry only after a status answered for ITS distribution and its record was read once (one `runs show`, or one runs window clamped to the 90-day retention). Found while mutating: an unresolved entry whose running block stays unreadable was waited on forever — it now gets that last look, then "state unknown" |
| B2 (Important) | a refused / interrupted / swept full check read "never ran" (its line carries `["collect"]`); `[]` lines of an unusable request or a reconciled orphan could be adopted | **Fixed** — `[]` and `["collect"]` both match, the two shapes are excluded by their reasons. **Follow-up for the daemon (not done here):** write `["collect"]` on the completed line too — **superseded by §15o** (branch `fix/wc-full-check-line-names-collect`): the line gains `kind`, the pseudo-row goes; the follower reads `kind` first and keeps this rule for a line without it (E6.S3 row) |
| B3 | `since` is the Windows clock, `startedAt` and the window WSL's | **Fixed** — both ends widened by 5 minutes (the daemon's `RequestSweep.FutureSkew`); several matches stay candidates |
| B4 | an unresolved confirm whose run was wedged / unknown / unreadable at 90 s ended "never ran"; Stop added a second entry | **Fixed** — a matching wedged run is adopted, an unreadable block waited on; Stop adopts the unresolved entry of its run |
| B5 | `dispose` cancelled only the timer | **Fixed** — a disposed follower shows, removes and re-reads nothing |
| coai #0 / #5 (Blocking) | the run-id regex duplicated (client, fake, root ids) | **Fixed** — `src/shared/shapes.ts` (run id, UTC instant); a scan fails on any other speller; the fake holds the same objects |
| coai #1 / #6 | `number()` read an absent count as 0 | **Fixed** — "? objects" (no button), "? GB", "nothing to clean" only for a real 0; Docker after "at least …, n types not read" |
| coai #2 / #13 | `as unknown as World` in a test | **Fixed** — a class builds the world |
| coai #3 / #9 / #16 | the follower could never start from idle | **Fixed** — `running()` from the store's newest status; a run no entry follows is watched (focused, bounded by the ceiling) and its result shown once |
| coai #4 | an unfocused window settled another window's entry | **Fixed** — the same predicate as the poll decides what a tick settles |
| coai #7 / #15 / #21 | the journal's read-modify-write races across windows | **Fixed as far as the API allows** — `Memento.update(key, fn)` does NOT exist (`@types/vscode` 1.85: `update(key, value)` only); no cached copy, serialised writes, read-back one turn later with re-apply, tombstones merged, the result shown by the window whose claim stands; the residual race is stated in `journal.ts`'s header |
| coai #8 / #12 / #14 | `gb` ×4, `minuteOf` ×3 | **Fixed** — `src/text/format.ts` |
| coai #10 | a failing record read retried every 4 s for 30 minutes | **Fixed** — 3 tries with backoff, then "state unknown — the record could not be read" |
| **coai #11** | end an entry at the ceiling without a record read | **Rejected** — the ceiling alone is no evidence (B1): ONE bounded record read comes first, then the ceiling applies |
| coai #17 | the action cap reused the entry cap | **Fixed** — `MAX_ACTIONS_PER_ENTRY` = the registry's size |
| coai #18 | `resultText.ts` imported the follower's constant | **Fixed** — the ceiling is handed in |
| coai #19 | `void host.clean/…` swallowed a fault | **Fixed** — told (sanitised) and logged to the *WSL Care* log output channel at the detached edge |
| coai #20 | the 32-entry cap evicted the oldest unshown entry | **Fixed — refusal chosen**: a new entry is refused and the person told to wait; nothing is evicted |
| coai #22 | entries settled one after another | **Fixed** — at most 4 at once |
| coai #23 | a panel round per ended entry | **Fixed** — one per tick |
| coai #24 | the Select tick posted `{type: 'none'}` | **Fixed** — it toggles and posts nothing |
| coai #25 | `runs show` every tick while status and history disagree | **Fixed** — one per leaving flight |

### 15o. A full check's history line names itself — `kind` (daemon follow-up of the E6.S3 review round, B2; 2026-10-05)

> Status: **built 2026-10-05; coai code round proceed and its review round fixed (table at the end of this section)** — the
> extension's half is built on `feat/wc-e6-cleanup-logs` (PR #12: kind first, and an act line without a kind or with `act`
> — the E6.S3 row and §15p C7), not merged until `extension-v0.1.0` is tagged, so the section stays here. **Deviations:** the three "a requested run that never did its work" writers
> (`DetachedRuns.Refused`, `DetachedRuns.CutOff`, `RequestSweep`'s swept request) take ONE line from a new
> `RunRequestFile.TerminalLine` instead of three copies; the prefixes are `Records/HistoryReasons` (the unusable prefix a
> named `RequestSweep.UnusablePrefix`); the RED test and the reason enumeration live in a new `Cli.Tests/FullCheckLineTests`
> (one `Ending` per writer), the Core cases in `Core.Tests/Records/RunKindTests`; **found by the enumeration:** a READABLE
> reconciled full-check orphan carries `kind: "collect"` AND the reconcile's prefix — correct under kind first (the prefixes
> are the fallback for a kind-less line, which the contract's description says), so the reconcile is not in the "no
> prefix" list; `DetachedRuns.CutOff` is not reachable from outside for a full check (`CollectRun` records its own cut-offs
> first), so its line is tested through the expression it appends; `RefusingDetailWrites` moved to `TestSupport`.
> Record: `research/module_tests.md`, *A full check's history line names itself*.
>
> Original status: **plan only, nothing implemented yet, 2026-10-05.** Scope: the daemon's history line and `running.json` writers,
> `runs` / `runs show`'s `RunLine`, their goldens and docs. Branch `fix/wc-full-check-line-names-collect`. The extension is
> not changed here (boundary below). **coai plan round (2026-10-05): verdict proceed, 3 findings, all accepted** — #1
> (Major) prove the follower's exclusions cannot match a new full-check line, and both sides change (decision 2, the
> boundary, the test plan); #2 (Major) the name `collect` reserved and the older-file inference tightened (decision 3);
> #3 (Minor) what `contracts/` pins and what the goldens pin (*Not changed*).

**Symptom.** A reader cannot tell a full check's history line by one rule, and `actions: []` is ambiguous. The writers today
(verified on `origin/main` 8cac31d):

| Writer | When | `actions` written today |
|---|---|---|
| `CollectRun.Line` (`src_daemon/src/WslCare.Core/Collect/CollectRun.cs:300`) | a full check recorded (completed / observeOnly / failed) | manual / cli: `[]`; timer: the timer pass's per-action results |
| `CollectRun.RecordCutOff` (`CollectRun.cs:183`) | a full check cut off during the measurement or the request sweep | `[]` |
| `DetachedRuns.Refused` (`src_daemon/src/WslCare.Cli/Commands/DetachedRuns.cs:310`) | a detached run refused (lock, wedged, unreadable, observe-only) | the request's ids → `[{id: "collect", status: "refused"}]` for a full check |
| `DetachedRuns.CutOff` (`DetachedRuns.cs:259`) | a detached run cut off before it started | `[{id: "collect", status: "interrupted"}]` |
| `RequestSweep.Interrupted` (`src_daemon/src/WslCare.Core/Actions/Engine/RequestSweep.cs:144`) | a stale request whose unit is gone | `[{id: "collect", status: "interrupted"}]` |
| `RunningSweep.SweepDead` (`src_daemon/src/WslCare.Core/Actions/Engine/RunningSweep.cs:79`) | a dead holder of `running.json` | measuring: `[{id: "collect", …}]`; in the timer pass every registry id `interrupted` (the pass rewrites `running.json`, `ActionEngine.cs:176`) |
| `RequestSweep.Unusable` (`RequestSweep.cs:67`) | a request the reader refuses | `[]` — act or full check unknowable |
| `RunReconcile.InterruptedLine` (`src_daemon/src/WslCare.Core/Records/RunReconcile.cs:49`) | an orphaned detail | `[]` — act or full check |
| `ActionEngine.Line` (`src_daemon/src/WslCare.Core/Actions/Engine/ActionEngine.cs:469`) | an `act` recorded | each action's result |

So `[]` is a completed manual full check, a cut-off full check, an unusable request and a reconciled orphan of either kind;
the extension's follower compensates with `[]` OR `["collect"]` plus three reason-PREFIX exclusions (E6.S3 branch,
`src_vs_code/src/cleanup/runMatching.ts`, `actionsMatch` / `NOT_A_FULL_CHECK`) — matching on English sentences.

**What `actions` means — checked before deciding.** Per-action RESULTS, not the requested ids: plan §6 defines the line's
"per-action `{id, count, freedBytes}`"; the row type is `ActionRecord(Id, Count, FreedBytes) { Status, WouldFreeBytes,
Failure }` (`Records/RunRecord.cs:95`); a timer line's rows are `ActionRecords.Of` of each outcome; and every reader reads a
row as an action's result — `RunLogs.Totals` groups rows by id into `logs`' `perAction` (`History/RunLogs.cs:126`, so the
pseudo-row ALREADY puts a zero `collect` entry into `perAction` for every refused or swept detached full check),
`IsCleanup` / `Freed` sum the acted rows (`RunLogs.cs:150`, `:153`; `lastCleanup` picks by `IsCleanup`), `Cleanups` matches
rows to the detail's outcomes by id (`RunLogs.cs:184`), A15 looks for the newest `A15` `ran` row
(`Actions/Disk/FilesystemTrim.cs:148`). Writing `["collect"]` on EVERY full-check line would put a non-action row beside
real results on a timer line and a `collect` entry into `perAction` for every full check. **Rejected.**

**Decision (additive, `schemaVersion` stays 1).**

1. **`kind` on the history line** (`RunRecord`): `"collect"` | `"act"` — what the run WAS. Written by every writer that
   knows it; absent on lines written before this change and on the two lines whose kind cannot be known (an unusable
   request; an orphan whose detail cannot be read). A reader's one rule: `kind == "collect"` is a full check.
2. **`actions` keeps ONE meaning — per-action results.** The `collect` pseudo-row is no longer written: a refused, cut off
   before it started, swept-request or swept-while-measuring full check writes `actions: []` with `kind: "collect"`. `[]`
   then means only "no action produced a result"; whether the run was a full check is `kind`'s answer. *Why the removal is
   safe:* no daemon release exists (`src_daemon/version.txt` 0.0.0, no tag, no release); the only reader keyed on the row
   is the unmerged extension follower, which already accepts `[]` for a full check, and none of the new lines' reasons hit
   its exclusions — PROVED, not asserted (plan round #1): the follower's three "not a full check" prefixes become a
   contract the daemon emits, `contracts/history-reasons.json`, generated from the daemon's own constants (`RunReconcile`'s
   two reasons; the unusable request's prefix, made a named `RequestSweep` constant) and held equal by `ContractFilesTests`,
   so both sides read ONE list instead of two copies; and `FullCheckLineTests` drives every writer of a `kind: "collect"`
   line — `DetachedRuns.Refused` (the lock), `DetachedRuns.CutOff`, `RequestSweep.Interrupted`, `RunningSweep.SweepDead`
   (with and without a stop marker), `CollectRun.RecordCutOff` (both moments), `CollectRun.Line` (completed, observe-only,
   failed on its detail) — and asserts that no such line's reason starts with any of them. Lines written by pre-release
   builds keep their row and are reported AS STORED (no read-side rewriting); they age out with the 90-day retention. *Alternative weighed:* keep the row (strictly additive) — rejected: two shapes
   for one fact forever, and the zero `collect` entry in `perAction` stays.
3. **`kind` in `running.json` too** (`RunningFile`, additive): the sweep of a dead holder cannot otherwise tell the timer's
   pass (registry ids, trigger `timer`) from an `act --timer` of the same ids. `CollectRun` writes `collect`; the engine
   writes `collect` for the timer pass (`ActRequest` gains `Kind`, default `act`, set to `collect` only by
   `ActionEngine.TimerPassAsync`, `ActionEngine.cs:146`) and `act` otherwise. A file from an older writer: `collect` only
   for the EXACT shape `CollectRun` writes (`CollectRun.cs:121`) — `actions` exactly `["collect"]` AND `current`
   `"collect"` (the engine writes registry ids and starts `current` empty) — else absent (plan round #2). **The name
   `collect` is reserved** as the meta name of a full check: no action may be named it — said in `ActionId`'s doc comment
   and held by a test over `ActionId.All` and `contracts/actions.json` (compared ignoring case).
4. **The reconcile** takes the kind from the orphaned detail: `act` when the detail's `kind` is `act`, `collect` when it
   has NO `kind` member (a full run's detail), and absent for any other value — a future detail kind is never filed as a
   full check (review G1) — the rule `RunLogs.IsAct` (`RunLogs.cs:231`) already applies, reused rather than restated;
   unreadable → absent. The request's kind likewise (review G2): `collect` / `act` exactly, anything else no kind AND no
   action rows (its "actions" would be guesses).
5. **The compiler names every writer:** `Kind` is a POSITIONAL parameter (`RunKind?`, no default) of `RunRecord`
   (`RunRecord.cs:116`) and `RunningFile` (`Actions/Engine/RunningState.cs:19`), so a construction that does not decide does
   not compile — the "decision applied at some of its sites" defect closed by construction, not by a scan. A line or file
   without the member binds `null` on read (the source generator does not require constructor parameters).
6. **The wire:** `RunLine.kind` (`runs`, `runs show`; `History/LogsReports.cs:29`, filled in `RunLogs.Line`, `:102`), absent
   when the line has none. `RunKind` is an enum with `JsonStringEnumMemberName`, strict like `RunTrigger` / `RunOutcome`.
   **Residual, stated in full (review O5) — reachable only after a downgrade (`install.sh --version <older>`) to a build that
   does not know a kind a newer one wrote; the same class `RunOutcome` carries, no code change:** (a) an unknown `kind` in
   `running.json` makes it UNREADABLE (the strict enum fails the parse), and an unreadable running state refuses every `act`
   and every timer pass until the file is removed by hand; (b) a history line with an unknown kind is unparseable (counted in
   `unparseableLines`), and every history-first check treats an unparseable line as NO line — `RunningSweep.SweepDead`
   (`RunningSweep.cs:70`), the request sweep (`RequestSweep.cs:144` / `:154`), `act --request`'s own check
   (`DetachedRuns.cs:195`-`196`) and the reconcile (`RunReconcile.cs:28`-`30`) — so after such a downgrade a run could get a
   SECOND terminal line, or `act --request` could run a request whose run had already recorded itself.

**Not changed:** the request file (`kind`, `actions: ["collect"]`, its reader), `status.running` (still `actions:
["collect"]` while a full check measures; exposing `running.kind` is a possible follow-up this fix does not need), `logs`'
aggregates (only new lines lose the pseudo-row), `lastCleanup`, `runs show`'s states, exit codes,
`contracts/actions.json` / `exit-codes.json`. **What `contracts/` pins and what it does not** (plan round #3): it holds
`actions.json`, `exit-codes.json` and, new here, `history-reasons.json` — no `RunLine` field list; the `RunLine` wire
(`kind` included) is pinned by the head goldens regenerated in build step 5 (`runs-local-day.json`, `runs-show-*.json`).

**The boundary with the extension** — BOTH sides change (plan round #1); named here, and on the extension's side (E6.S4,
PR #12) when it takes its half:

| Item | Built by | The other side's part |
|---|---|---|
| `kind` on every history line and in `running.json`; no `collect` pseudo-row; `RunLine.kind`; `contracts/history-reasons.json` | this change (daemon) | nothing needed in between — the current follower keeps working unchanged |
| the follower matches a full check by `kind === "collect"` FIRST, falling back to today's rule only for a line without `kind` (a daemon older than this), its exclusion prefixes read from `contracts/history-reasons.json` instead of its own copy | the extension — E6.S4 on PR #12, NOT this PR | the daemon's additive field and contract |

Order: the daemon first (additive; nothing breaks in between). Disjoint otherwise.

**Files.** `Actions/ActionId.cs` (the reserved name, doc only), `contracts/history-reasons.json` (new, generated),
`Records/RunRecord.cs` (`RunKind`, positional `Kind`), `Actions/Engine/RunningState.cs` (`RunningFile.Kind`),
`Actions/Engine/ActRecords.cs` (`ActRequest.Kind`), `Actions/Engine/ActionEngine.cs`, `Collect/CollectRun.cs`,
`Actions/Engine/RequestSweep.cs`, `Actions/Engine/RunningSweep.cs`, `Records/RunReconcile.cs` + `Records/RunDetailStore.cs`
(the head's kind), `Cli/Commands/DetachedRuns.cs`, `History/LogsReports.cs`, `History/RunLogs.cs`; the tests constructing
`RunRecord` / `RunningFile` (mechanical); `contracts/golden/head/*` only where the wire changed; `README.md` (*Logs and
runs*), `research/architecture.md` (the run-records seam row), `research/architecture-daemon-e6.md` (*The daemon read contract*, moved there on 2026-10-06), `research/module_tests.md`, this
section and the §6 table's history row.

**Build order.** 1 the RED test (below) · 2 `RunKind` and the positional members, the build fixed site by site · 3 the
writers (kind, no pseudo-row; the reconcile's head kind; the timer pass's request kind) · 4 `RunLine.kind` · 5 goldens
regenerated (`WSL_CARE_WRITE_GOLDENS=1`), the diff read · 6 docs.

**Test plan.**

- **RED first, at the wire** (compiles against today's code — it reads `history.jsonl` lines as raw JSON), in a new
  `Cli.Tests/FullCheckLineTests` (`DetachedRunsTests` is near the 800-line cap):
  `Every_terminal_line_of_a_detached_full_check_names_it_by_kind_and_carries_no_collect_row`, over a completed detached
  full check, one refused at the lock and one cut off before it started. Expected today: the completed line has no `kind`,
  the refused one carries the `collect` row — red naming that.
- **The follower's exclusions cannot match a full check** (plan round #1): `FullCheckLineTests` drives every
  `kind: "collect"` writer listed under decision 2 and asserts no reason starts with a prefix of
  `contracts/history-reasons.json`; its companion asserts the prefixes DO match the two lines they are for (an unusable
  request's, a reconciled orphan's) — a list that matched nothing would pass the first test forever.
  `ContractFilesTests` holds the file equal to the constants.
- **The reserved name** (plan round #2): no `ActionId.All` id and no `contracts/actions.json` id equals `collect` ignoring
  case; an older `running.json` is read as `collect` only for `["collect"]` + `current: "collect"` — `["collect"]` with
  another `current` is absent.
- **Per writer** (Core / CLI tests beside the existing ones): `collect` completed manual and cli (`collect`, `[]`) and timer
  (`collect`, the pass's results, no `collect` row); cut off during the measurement and during the request sweep
  (`collect`, `[]`); a swept collect request (`collect`, `[]`) and a swept act request (`act`, its ids `interrupted`);
  `Unusable` (`[]`, no `kind`); `RunningSweep` over a measuring full check (`collect`, `[]`), the timer pass (`collect`, the
  ids), an act (`act`), an older file without `kind` holding `["collect"]` (`collect`) and holding ids (absent);
  `RunReconcile` over an act detail (`act`), a full-run detail (`collect`), an unreadable one (absent); an `act` line
  (`act`); `running.json` written by `collect` and by the timer pass (`collect`) and by an act (`act`).
- **Reading:** a line without `kind` still parses and `RunLine.kind` is absent; a line with an unknown kind is counted
  unparseable (the residual, pinned); a refused detached full check adds no `collect` entry to `logs`' `perAction`.
- **Goldens:** regenerated where the wire changed (expected: `runs-show-done.json` gains `kind: "act"`; whatever else moves is
  read from the diff, not predicted); `GoldenContractTests` green after; the drift test still red with a renamed field.
- **Teeth:** set `CollectRun.Line`'s kind back to `null`, and separately re-add the pseudo-row in `DetachedRuns.Refused` —
  each turns the RED test red again; restored, green.
- **Whole suites:** every test executable on Windows and on WSL (a normal user, a `/tmp` copy removed after), `dotnet
  format --verify-no-changes`, the family checks (`plan-lifecycle.mjs` and the rest `family-checks.yml` runs).

**Growth.** None new: one field (~16 bytes) per history line under the existing 90-day retention; `running.json` stays one
file.

**Definition of Done.**

- [ ] Every history line written after this change for a full check carries `kind: "collect"`, for an act `kind: "act"`;
      only an unusable request's and an unreadable orphan's line carry none.
- [ ] No new line carries a `collect` row in `actions`; `actions` holds per-action results only.
- [ ] `running.json` carries `kind`; a dead holder's swept line takes it (or the `["collect"]` marker of an older file).
- [ ] `runs` / `runs show` answer `kind`; `schemaVersion` 1 everywhere.
- [ ] `contracts/history-reasons.json` generated from the daemon's constants, and no reason of a `kind: "collect"` line
      starts with one of its prefixes (tested over every writer, with the companion).
- [ ] `collect` is a reserved name: no action id carries it (tested), `ActionId` says so.
- [ ] The RED test seen failing for the real symptom, then green; the teeth check done; both observations recorded in
      `research/module_tests.md`.
- [ ] Goldens regenerated only where the wire changed; docs updated (README, `architecture.md`, `module_tests.md`, §6).
- [ ] Whole suites green on Windows and WSL, `dotnet format` clean, family checks green.

**§15o review round (2026-10-05): the coai code round (verdict proceed, 4 of 4 reviewers answered) and an own review.**
Every finding ACCEPTED and fixed on this branch; each behaviour seen red for the real symptom (before the fix, or by
reverting it), green, and its load-bearing line broken and seen red again — the record is `research/module_tests.md`,
*A full check's history line names itself*.

| # | Finding | Disposition |
|---|---|---|
| G1 (Major, coai) | `RunKinds.OfDetailKind` filed any non-`act` detail kind as a full check | **Fixed** — `RunKind?`: `act` → act, no member → collect, any other value → absent. `RunKindTests` (an `archive` and an `Act` detail → a kind-less line) |
| G2 (Major, coai) | `RunKinds.OfRequest` filed any non-`collect` request kind as an act — a mislabelled line with the old row shape | **Fixed** — `RunKind?`: `collect` / `act` exactly, else absent; `TerminalLine` then writes NO kind and NO action rows (decided: an unknown kind's "actions" are guesses). `RunKindTests` (`archive`, `Collect`, `""`) |
| G3 (Minor, coai) | `FullCheckLineTests.End` at cyclomatic complexity 5 | **Fixed** — a lookup of one handler per `Ending`; every method ≤ 4 |
| G4 (Nit, coai) | `KindOrMarker` null-safety unexplained; `SweepDead`'s `file.Actions` could be null? | **Fixed** — both commented: the one reader (`RunningState.Read`) admits no file without `actions` / `current`, so a broken file is unreadable and never swept; a list pattern is false on null. Pinned by `RunKindTests.A_running_json_without_actions_is_unreadable_…` |
| O1 (Important, own) | `FullCheckLineTests` copied ~30 lines of the `DetachedRunsTests` fixture | **Fixed** — one `Cli.Tests/DetachedRunHarness` (sandbox, runner, root host, request planting, records) used by both; `DetachedRunsTests` is 680 lines |
| O2 (own) | no writer-side test that `act --timer` stays `act` in `running.json` | **Fixed** — `ActionEngineTests.An_act_names_itself_act_…` over Cli / Manual / Timer, running.json read while the act runs and the line |
| O3 (own) | the "cut off before it started" ending never called `DetachedRuns.CutOff` | **Fixed** — the reachable path is driven: an ACT request cut off inside its request sweep → ONE `act` line, the asked ids interrupted (`FullCheckLineTests.An_act_request_cut_off_inside_its_request_sweep_…`); the full check's shape of that line stays an `Ending` built through the same expression (no path reaches it from outside) |
| O4 (own) | the follower check ran in the weak direction and nothing froze the reason text on disk | **Fixed** — `ContractFilesTests.The_reasons_already_on_disk_are_frozen` pins the three reasons (and the contract's list) to literals ("these strings are on disk; a change is a contract break"); the companion asserts the reasons AS WRITTEN start with the follower's prefixes |
| O5 (own) | the downgrade residual understated | **Documented** — decision 6 and `research/architecture-daemon-e6.md` (moved from `architecture.md` on 2026-10-06) state both halves (an unreadable `running.json` blocks every act / timer pass until removed by hand; an unparseable line is "no line" to every history-first check) |

### 15q. E7 split and design — AI-agent discovery, settings ↔ config, Add CLI path

> Status: **in progress, 2026-10-05 — E7.S0 built and its review round fixed** (the configuration trust and contract; deviations in *E7.S0 as built*, the review in *E7.S0 review round*
> below); **E7.S1, E7.S2 and E7.S2b built 2026-10-05** (the agent catalogue, discovery, the walk, `agents list`; `aiAgents.extra`, `agents probe`; A18; deviations in *E7.S1 as built*, *E7.S2 as built*, *E7.S2b as built*; the E7.S1/S2 review round fixed, *E7.S1/S2 review round*); **E7.S2c built 2026-10-05** (every number a key, `Tuning`, the unit drop-ins, `status` `limits`; deviations in *E7.S2c as built*); **the E7.S2b/S2c review round and the coai E7 code round (passed) fixed 2026-10-06** (*E7.S2b/S2c review round*: A18 bound to its modal, the two-clock dense idle, the progress watchdog and the timer run limit — N-4 reversed —, the rules held per layer); E7.S3–E7.S5 (with E7.S2b and E7.S2c added by the owner 2026-10-05) and the E7 live gate still open; **E7.S2d (the MCP server instances, owner request 2026-10-06) built 2026-10-06** (*E7.S2d as built*). Originally: plan only, nothing implemented yet, 2026-10-05. Scope: epic E7 — the daemon's `agents list` / `agents
> probe`, the AI-agent sizes on the daily walk, the trust model of the user configuration layer that `config set` writes
> and the root timer reads, `aiAgents.extra`; the extension's AI-agents section, *Add CLI path…*, the settings editor
> mirrored to the daemon's config, the bundled `wsl-care.exe`. Branch `feat/wc-e7-agents-settings` — this plan AND the daemon
> code (E7.S0–E7.S2). **Deviation:** the split planned first (`feat/wc-e7-daemon`) is not used, because the coai session is
> keyed on this branch; the extension parts (E7.S3–E7.S5) get `feat/wc-e7-extension`. **The review round is folded in**
> (*§15q review round* at the end of this section: the coai plan round and an own plan review, every finding accepted);
> where a row of that table and the text disagree, the row wins. This section OVERRIDES §4.6, §6 (the `agents` and `config` rows: `--measure`, `config set aiAgents.extra -`), §7.2 (AI
> agents row), §7.5, §12 (AI agents, Config)
> and the three E7 story rows of §16 where they say otherwise; §16's E7 rows now point here.

**Goal (the owner's asks, 2026-10-02).** (1) The AI-agent folders are monitored, with CLI auto-detection and an *Add CLI
path…* button for an agent discovery missed. (2) The cleanups get "older than N" settings. (3) The settings live in the
extension and are mirrored to the daemon's config, which stays the truth because the daemon runs without VS Code (§6).

**The owner's hard constraints — never violated, each held by a test named below.**

| # | Constraint | Held by |
|---|---|---|
| H1 | Nothing inside an AI agent's folder is ever deleted (or moved — moving is E9's alone, with hash verification) | E7.S1, E7.S2: catalogue AND manual folders join `ProtectedRoots.AgentRoots`; a manual folder may not overlap any cleanup root |
| H2 | `projects/*/memory/` of any agent is never touched | E7.S1: EVERY agent walk — catalogue and manual entries alike — never ENTERS a folder named `memory` (and each entry's own `neverEnter`); not even a stat inside it |
| H3 | Sizes may be measured; nothing is read, moved or deleted | E7.S1: the walk lists names and stats entries (`TreeWalk`, `TreeWalk.cs:36`: links never followed); a SYSCALL-level scenario (an inotify `IN_OPEN` watch over the fixture tree, or `strace -f -e trace=open,openat`) proves no file under an agent root is ever opened — a recording double cannot see `TreeWalk`'s own syscalls |
| H4 | `%TEMP%\claude\` is never cleaned (Windows side — E11 / E12, W-A2's guard) | E7 walks and cleans nothing there; a manual data folder under `ClaudeTempRoots` (`/tmp/claude`, `%TEMP%\claude`) is refused (E7.S2) |

**What exists today — verified on `origin/main` `a629b37`.**

- **No agent discovery at all.** No `Agents/` folder, no `agents.json`, no `agents` verb. The protected agent roots are a
  hard-coded list in two places: `LinuxHostPaths.AgentRootsUnder` (`src_daemon/src/WslCare.Core/Hosting/LinuxHostPaths.cs:137`,
  used at `:127`) and `WindowsHostPaths.AgentRoots` (`WindowsHostPaths.cs:77`), plus a third list of folder NAMES in the
  never-list (`Processes/Policy/NeverList.cs:31`). `research/architecture.md:241` already says "the catalogue of E7 must
  feed the same list".
- **The daily walk skips agent folders on purpose** (`Folders/FolderSizes.cs:24-25`); its targets are `FolderSizes.cs:48-62`,
  its ceiling 2 M entries / 2 min per folder (`:35`), once per 20 h (`:32`). It is recorded as the history line's
  `slow.folders` with the previous sample kept for growth (`Records/SlowParts.cs:45`, `:67`, `:81`), invoked at
  `Collect/CollectRun.cs:344`. On the owner's machine the first full `collect` took 3.5 min (§17 #2).
- **`config get` / `config set` / `config reset` exist** (E1.S2: `Cli/Commands/ConfigCommand.cs`), with validation against
  ONE register (`Core/Config/ConfigKeys.cs:137-157`) of four value shapes (`Config/ConfigKey.cs:21-30`), an atomic
  repairing writer (`Config/UserConfigWriter.cs`), and root refusing to write the target user's layer
  (`ConfigCommand.cs:69-72`). `aiAgents.warnGb` / `sessionWarnMb` exist (`ConfigKeys.cs:103-107`); `aiAgents.extra` was
  deferred to E7 for its object shape (§16 E1.S2 row). The daemon keys `distro` and `refreshSeconds` (`ConfigKeys.cs:24-25`)
  are read by no daemon code.
- **Root reads the user layer like any file.** `ConfigLoader.Load` reads both the machine and the user layer with the plain
  `IFileSystem.ReadFile` (`Config/ConfigLoader.cs:38`, `:43-44`; `Files/PhysicalFileSystem.cs:48`): no size cap, links
  followed, owner not checked, a FIFO opened blocking. As root (the timer, every `act`) the user layer is the TARGET user's
  `~/.config/wsl-care/config.json` (`LinuxHostPaths.cs:89`, `WithHome` `:78`; `Cli/CliHost.cs:43`). The hardened reader
  exists — `RegularFiles.ReadOwned` (`Files/RegularFiles.cs:68`: `O_NOFOLLOW` + `O_NONBLOCK`, one `statx` of the open
  descriptor, owner, no g/o write, a cap) — and is used for the request files only (`PhysicalFileSystem.cs:73`). **The same
  class — root reading a file the user controls with the plain reader — sits at six more sites:** in the target home
  `Actions/TargetUserCommands.cs:78` (`~/.nvm/alias/default`), `Actions/UserCaches/BrowserAndHttpCaches.cs:170` and `:189`,
  `Actions/UserCaches/EditorServerCleanup.cs:109`; in the Windows profile through `/mnt/c`, `Health/HealthCollector.cs:72`
  (`.wslconfig`) and `Docker/DockerHygiene.cs:76` (Docker Desktop's `daemon.json`). Found by listing all 26 `ReadFile(`
  callers outside the file system itself (a grep for the call) and classifying each by whose file its path names.
- **The extension** (main = E5) knows four verbs (`src_vs_code/src/client/verbs.ts:11`) and two settings (`wslCare.distro`,
  `wslCare.refreshSeconds`, both `"scope": "application"`); its bundle scan forbids the argv word `config` everywhere
  (`src/test/bundleScan.test.ts:39`). E6.S2 (PR #12, unmerged) partitions the bundle by module and keeps `config`
  forbidden in EVERY region, the root one included (§15j M1, §15k #10). E7 amends that rule (E7.S3).

#### Decisions taken in this plan

**D1 — Who measures what.** The ROOT daily walk (inside `collect`, once per 20 h, the same ceiling) measures the agents'
data folders under the TARGET user's home and records them as a new slow part `slow.agents` beside `slow.folders` (the
previous sample kept for "growth since yesterday", as `PreviousFolders` is) — **totals, counts and dates only**: the five
largest session NAMES are never persisted in `history.jsonl`; `agents list --measure` answers them live. The whole agent walk
has ONE total ceiling of 3 min inside a root `collect` — catalogue entries first, then extras; what is left reads "not
measured this run" — and `agents list --measure` has its own ceiling below the host call's timeout (review M7). `agents list --json` is UNPRIVILEGED: live
discovery (cheap — PATH lookup, folder existence, npm global folders) joined with the newest recorded `slow.agents` and its
age; `agents list --measure --json` walks now, as the invoking user, and records nothing (an unprivileged run writes no
state, §15b #3). The Windows side: `wsl-care.exe agents list` (E7.S5) keeps its newest walk in ONE file.

**D2 — What a "session" is.** One definition, the catalogue's, and for the four agents whose layout was confirmed on
2026-10-02 it is the archive plan's §3 table ([PLAN_ai_session_archive.md](PLAN_ai_session_archive.md)) — which
supersedes §4.6's rougher column (§4.6 says Gemini `tmp/*/`; the confirmed layout is `tmp/<project>/chats/session-*.jsonl`).
Every other agent is **monitor only: sessions not counted (layout unconfirmed)** — shown as "—", never 0. A session is
found by listing names and stat-ing entries; no file is opened.

**D3 — No agent binary is ever executed** (changed by review M10; §4.6's "`--version` within 2 s" is superseded). The
version comes from what is on disk without running anything: the binary's link target when a native install encodes it
(e.g. `~/.local/share/claude/versions/<v>`), or the `version` of the npm package's `package.json` under a global
`node_modules` (outside every agent folder). Otherwise it reads "not asked". No version template exists in the policy.

**D4 — *Add CLI path…*: a picker AND a typed path, both resolved on the host, the path never reaching root.**
A host QuickPick offers **Browse…** (`showOpenDialog`, one file) and **Type a path…** (`showInputBox` with a validator).
*Why both:* the Windows dialog shows what exists (no typos) and reaches the distro through `\\wsl.localhost\<distro>\…` —
but opening that share STARTS the distro (acceptable for an explicit click, unlike polling, §15f #8) and a slow 9p browse
is exactly the case the typed path covers. **The dialog answers a URI, and the mapping is by SCHEME (review M12):**
`file:` → the UNC / drive rules below; `vscode-remote://wsl+<d>/…` (what a Remote-WSL window returns) → that Linux path
when `<d>` is the validated distribution; distribution names compared case-insensitively; any other scheme refused. A pure,
tested function maps the result: `\\wsl.localhost\<d>\…` or
`\\wsl$\<d>\…` → a Linux path, only when `<d>` is the validated configured distribution (else refused naming both);
`X:\…` → the Windows side (E7.S5; until then refused "arrives with the bundled Windows binary"); anything else (another
UNC, `\\?\`, relative, a control character, a NUL, a leading `-`, > 1 024 characters) refused before any spawn. The
probe is `wsl.exe -d <distro> --cd / --exec /opt/wsl-care/bin/wsl-care agents probe <path> --json` — **no `-u`**; the
daemon refuses `agents probe` as root, naming uid 0 and the fix (review C3: "agents probe runs as the user who owns the CLI,
not as uid 0 — set the distro's default user: `wsl.exe --manage <distro> --set-default-user <user>`, or `[user] default=`
in `/etc/wsl.conf`"); it lstat/stat-s the file
(regular, an execute bit for this user via `access(X_OK)` — never `exec`, never a byte read), derives a name from the
FILE NAME, and lists the conventional data folders under the user's home (§4.6) with sizes. The user confirms in a HOST
modal (never the webview: the webview sends `{type: 'addCliPath'}` and `{type: 'removeAgent', index}` only — an index into
data the host read). Saved to `wslCare.aiAgents.extra` and mirrored (D5). Root later reads only the entry's
`dataFolders` and `sessionGlob` as data for the walk; the `cli` field is never a file-system argument in a root process
(a recording double asserts it).

**D5 — The settings mirror.** Every daemon key the extension edits is one VS Code setting `wslCare.<key>` with
`"scope": "application"` (§15g M3) and `"ignoreSync": true` — the values are this machine's, and a value synced from
another machine must not steer this machine's root timer (the flag's existence on `engines ^1.85.0` is verified before
relying on it; whether Settings Sync honours it is a live-gate observation). Type, `minimum` / `maximum`, `enum` and
`default` are generated from ONE contract, `contracts/config-keys.json`, which the daemon emits from `ConfigKeys` +
`default.json` (held equal by `ContractFilesTests`, as `actions.json` / `exit-codes.json` are) and a TS test holds
`package.json` equal to it. **Not mirrored:** `wslCare.distro`, `wslCare.refreshSeconds` (the client's own; the daemon keys
of the same names stay, unused, because removing a key turns every user layer that holds it into an unknown-key
observe-only error), and the display settings of E8. The "older than N" settings are the ones the actions read today,
with the daemon's ranges (unit and the meaning of 0 in each description):

| Setting `wslCare.…` | Action | Range | Default |
|---|---|---|---|
| `volumes.anonymousOlderThanDays` | A4 | 0–3650 d (0 = every unattached anonymous volume) | 1 |
| `containers.stoppedOlderThanDays` / `containers.testcontainersOlderThanHours` | A5 | 0–3650 d / 0–8760 h | 7 / 2 |
| `images.unusedOlderThanDays` | A6 unused | 0–3650 d | 7 |
| `buildCache.olderThanDays` | A7 | 0–3650 d | 7 |
| `journal.keepDays` | A10 | 1–3650 d | 30 |
| `buildServers.idleHours` / `processes.idleOlderThanHours` | A3 / A11 | 0–8760 h | 4 / 8 |
| `archive.olderThanDays` | A13 (E9) | 1–3650 d | 14 |

plus every `auto.*`, `dryRun`, the size triggers (`volumes.anonymousMaxCount` / `MaxGb`, `images.unusedMaxGb`,
`buildCache.maxGb`, `npm.maxCacheGb`), `thresholds.*`, `processes.families`, `idle.*`, `clock.maxDriftSeconds`,
`aiAgents.*`. The defaults quoted are `default.json`'s (`config get` on the build is the authority, not this table). No NEW
age knob is added (A12, A14's "keep newest 2", A17's `cargo sweep --time 30` stay as built; Q7) — so the owner's ask (2)
is met for the EXISTING age keys only, and the DoD says so.

**The one-time conflict notice (§7.5), made precise.** At activation and on a distribution change the host runs
`config get --json` (unprivileged — the user's own layer) and compares ONLY explicitly set values: a VS Code setting's
`inspect().globalValue` against the user layer's entry. A value set by the machine layer or the default is never a
conflict (the native Settings UI cannot show that — the panel does, and a VS Code change to such a key is ignored with a
notice). Differences → ONE notice per (distribution, a digest of the
differing keys and both values), persisted in `globalState`: **Keep VS Code's** (`config set` each), **Keep the daemon's**
(`update(…, Global)` each), **Decide later** (asked again only when the digest changes). Neither side is overwritten
silently. A VS Code change → `config set` (a reset to default → `config reset`); the daemon's refusal (exit 2 with its
message) reverts the VS Code value and shows the message; the host's own reverts are marked so the change event they
cause is not mirrored again. A change that LOOSENS a root-effective key (R1) is confirmed in a host modal first; declined →
reverted.

**Amended by the review round (C2, C4, M4, M5, M6; the working assumptions of Q5, Q6):**

- **Ours or not ours (C2).** Root-effective keys and `aiAgents.extra` are mirrored ONLY for a change made through the
  extension's own UI or commands in this window — the host holds a "this change is ours" token for it. A change that
  arrives by `onDidChangeConfiguration` WITHOUT the token (Settings Sync, another tool, a hand edit of `settings.json`) goes
  through the conflict notice — a click — and is never applied automatically. So the design does not depend on whether
  Sync honours `ignoreSync`.
- **One window mirrors (M4).** An application setting's change event fires in EVERY window; only the window holding a short
  `globalState` lease (renewed while focused) mirrors and shows modals; revert markers are keyed by (key, value). A
  two-window host test.
- **ONE write function (M5).** Every write to the user layer — the mirror, the reconcile's *Keep VS Code's*, *Add CLI path*,
  *Remove* — goes through one host function that applies the loosening rule, so a reload cannot bypass the modal.
- **Partial failure (C4).** *Keep VS Code's* with N keys applies them in order and CONTINUES past a refusal, then reports
  "k of N applied; refused: <key — reason>, …"; a refused key keeps the daemon's value on both sides, never a silent
  partial state.
- **The VM is not started (M6).** The activation reconcile first asks `wsl.exe --list --running --quiet` (§15f #8); a
  stopped distribution is reconciled when it is next seen running.
- **Which changes need the modal (Q5, working assumption).** `dryRun` true → false; an `auto` switched on for an
  off-by-default action (A5, A6Unused, A8, A11, A12, A17); any `processes.families` change. Ages and thresholds get a
  notice with **Undo** instead.
- **Scope.** A change mirrors to the CURRENT distribution only; the other distributions keep theirs. The contract marks the
  unused `distro` / `refreshSeconds` keys `daemonUnused` (Q6) and the extension never edits them.

#### The two risky items — where being wrong is expensive

**R1 — `config set` is a write path into what the ROOT timer does (a confused-deputy and trust question).** The user layer
is written by the unprivileged user and read by root every 4 h (`CliHost.cs:43`): `dryRun: false` + `auto.A5: true` +
`containers.stoppedOlderThanDays: 0` makes the timer remove every stopped container; `journal.keepDays: 1` makes root
vacuum the journal, which the user alone could not. What makes this acceptable is a FACT to be observed, not assumed:
on WSL with interop enabled, any process of this user — Windows or Linux — can already run `wsl.exe -u root` without a
password (§15 #1, §15f #2: "a confused-deputy boundary, not a malware boundary"), and every action is already a button.
So the user layer is the user's intent; the defence is that nothing ELSE can write it, that root reads it safely, that it
can only steer what the closed registry already does, and that its effect is visible. Decided:

1. **Root reads both layers through the hardened reader** — the machine layer owned by uid 0, the user layer by the TARGET
   user's uid, regular, no link, nonblocking, no g/o write, ≤ 256 KiB (`RegularFiles.ReadOwned`, widened by an owner
   argument rather than copied). A refusal is a `ConfigError` naming the reason → observe-only (§15a #1), never a hang and
   never a followed link. **The class is swept in the same story:** the six other root reads of a user-controlled file
   listed above go through a reader policy PER CLASS (review M2): a target-home file — owner the target uid, no g/o write;
   a Windows-profile file through drvfs (`.wslconfig`, `daemon.json`) — `O_NOFOLLOW` + `O_NONBLOCK` + regular + the cap,
   and NO uid / mode check, because drvfs shows 0777 by default and Windows' ACLs are the boundary there (the real mount
   mode recorded in `research/`). `RegularFiles.ReadOwned` already takes an owner; the widening is `IFileSystem`, whose
   `ReadStateFile` hard-wires `TrustedStateOwner` (`PhysicalFileSystem.cs:73`). `UserConfigWriter.ReadCurrent`
   (`UserConfigWriter.cs:73`) reads through a bounded reader too, so a FIFO cannot hang `config set`. A structural test
   classifies every `ReadFile(` call site — and every `File.ReadAll*` (`Actions/Engine/ProcessTable.cs:96`,
   `WindowsSystemDrive.cs:170`), `ReadRegularFile`, and root's `MeasureTree` / directory listings of target-home paths —
   (procfs, `/etc`, root's state, target home, Windows profile) and fails on an unclassified new one, with a planted
   companion. A group-writable user layer (a umask of 002) would turn the timer observe-only: the umask WSL Ubuntu gives
   is measured first; then either a private group with no other members is accepted, or `doctor` prints the `chmod`.
2. **Interop off ⇒ the user layer only TIGHTENS root** (amended by review M3). When the distro's interop handler is absent
   or disabled (`WindowsSystemDrive.InteropRefusal`, `Processes/WindowsSystemDrive.cs:105`, reused), user→root IS a real
   boundary. `HomeOwner.Unknown` is NOT reused (it would refuse every user-scoped action, `Actions/TargetUser.cs:192`):
   `ConfigLoader.Load` gets a separate reason, keeps the target user, and applies the user layer's values to
   root-effective keys only in the contract's SAFE direction (`dryRun` true, an `auto` off, a longer age, a larger size
   trigger); a value in the other direction is ignored with "interop is disabled, so the user layer cannot loosen root;
   set machine-wide values in /etc/wsl-care/config.json". `status` / `doctor` / `config get` say so.
3. **No key can widen what is deletable — false on `main` today, fixed in E7.S0 (review B1).** `processes.families` is a
   free `TextListKey` (`Config/ConfigValidation.cs:29`, `:39`) and accepts `["other"]`, the catch-all
   (`Collectors/ProcessFamilies.cs:29`, `:43`), so root's A11 would end idle orphaned processes of OTHER uids
   (`Actions/Suspects/SuspectTermination.cs:115-125` refuses only root's own). `archive.baseFolder` is a free-text path
   key (`ConfigKeys.cs:114`) that E9's root A13 will write into. Decided: every `TextKey` / `TextListKey` is CLOSED by an
   `Allowed` set or is a declared PATH key with its own validator, checked at `config set` AND at load —
   `processes.families` ⊆ `ProcessFamilies.Catalogue` minus `other` and minus `ai-agents` (working assumption: the AI
   agents' processes are the owner's work; whether `ai-agents` may be chosen is an owner question); `archive.baseFolder`
   a declared path key, MACHINE-layer only until E9 adds R2-style validation. Numeric keys fill template slots by design
   (`Actions/JournalVacuum.cs:49`, `:67`): every number slot's bounds lie inside its key's range. The never-list, the
   `CommandPolicy`, the `DeletionPolicy` and `ProtectedRoots` reference no `EffectiveConfig` / `ConfigKeys`, except one
   typed, validated extras input (R2). Each held by a test, the families one RED first (a live bug).
4. **Visible effect.** Every run detail records the effective entries whose layer is not `default`
   (`config: [{key, value, layer}]`, additive) — so `runs show` / the Logs page can say "the timer ran A5 with
   `containers.stoppedOlderThanDays` = 0 (user layer)". The extension's loosening modal (D5) names the effect in words.
5. **Writers.** Only the user, unprivileged: root refuses to write the layer (exists); the extension spawns `config set`
   WITHOUT `-u` from one module (E7.S3's scan); keys from a closed list (the extension's mirrored keys ∩ the daemon's
   contract); values validated on both sides; the webview never supplies a key or a value; settings `application` +
   `ignoreSync` (D5).
6. **Root's own audit log is not the user's to steer.** `logging.minimumLevel` / `logging.retentionDays` from the user layer
   set root's log level and retention (`Cli/Logging/WslCareLogging.cs:34`, `:46`): both become root-effective with a safe
   direction (a level no higher than `Information`, a retention no shorter than the machine's) — a user value in the other
   direction is ignored for a root run with a notice.
7. **An outside change is noticed.** `status --json` carries a digest of the user layer (additive), so the extension sees a
   change made outside it mid-session and offers the conflict notice.

*What would be expensive if wrong:* the interop premise. If the live gate shows `wsl.exe -u root` is NOT reachable from an
unprivileged process of this user, item 2's rule must apply whenever that is so, and the premise sentence above is
corrected — the gate records the observation in `research/` (a root call, so the owner runs it).

**R2 — the agent-folder rules (never delete, never read, never enter memory) with user-named paths in a root walk.**
`aiAgents.extra` puts folders a user (or a stray process of theirs) named into the ROOT daily walk and into the protected
roots. Decided, enforced at `config set` AND again at every root read — never trusted because it validated once. **A value that
fails leaves the WALK of that run only, with a notice; its spelled path, and its real path when resolvable, STAY in the
protected roots** (review B2: over-protection is safe; dropping it would unprotect the folder the moment, say, a later
daemon adds an overlapping cleanup root). An overlap makes the cleanup action refuse under that root, with a record.

1. A data folder is absolute; its REAL path lies strictly under the target user's real home, on the home's own filesystem
   (same `st_dev` — so never `/mnt/c` over 9p, never a bind mount; the 2026-10-01 failure, §2), is not the home itself, not
   under `~/git` (`GitRoots`), not under `ClaudeTempRoots` (H4), not equal to, inside or containing a catalogue root
   ("already tracked as <agent>"), and **not equal to, inside or containing any root a §5 action cleans** (`~/.npm`,
   `~/.cache/ms-playwright`, the NuGet `http-cache`, `~/.vscode-server`, the A17 tool caches — read from the action
   registry, not listed by hand), so "nothing in an agent folder is deleted" holds for command-based cleanups too, not only
   for `IFileSystem` deletes. The cleanup roots are DECLARED by each user-home action through a new `ICleanupAction` member
   — none exists today (`Actions/ICleanupAction.cs:181-205`; `Actions/UserCaches/CacheFolders.cs` has part of it) — with a
   test that every user-home action declares its roots (review M8). *Residual, stated:* a tool that relocates its cache
   (`npm config cache`, `PIP_CACHE_DIR`, `UV_CACHE_DIR`, `PNPM_STORE_DIR`, `XDG_CACHE_HOME`) is covered only at its
   DEFAULT root. Never the product's own config, state or install folders (`~/.config/wsl-care`, the state and install
   directories — an extra there would lock `config set`, `UserConfigWriter.cs:65`, `:116-120`; review M9). ≤ 16 entries,
   ≤ 8 folders each, ≤ 1 024 characters per path; a session glob is relative, `[A-Za-z0-9._*-]` segments and `**`, no
   `..`, ≤ 128 characters (E9 will consume it — named in the boundary below). **Windows extras** (E7.S5b): under
   `%USERPROFILE%`, no reparse point on any component, a local fixed volume, not under or containing `%TEMP%\claude`.
   **The same-device rule holds per DIRECTORY, not only for the data folder** (review C1): a nested bind mount
   (`~/myagent/model-cache` → `/mnt/c`) would drag the walk onto 9p, so the walk checks `st_dev` at every directory, stops
   at a change and reports "excluding <subdir> (different filesystem)".
2. Catalogue folders and accepted manual folders join `ProtectedRoots.AgentRoots` (`Files/Deletion/ProtectedRoots.cs:27`)
   BEFORE the first action of a run; the catalogue feeds `LinuxHostPaths` / `WindowsHostPaths` (replacing the two
   hard-coded lists) and the never-list's names (derived; a test holds them equal). **The host becomes two-phase (review
   M1):** the `DeletionPolicy` is built in the `PhysicalFileSystem` constructor (`Files/PhysicalFileSystem.cs:45`), which
   `CliHost.ForThisMachine` creates (`Cli/CliHost.cs:70`) BEFORE `Program.Main` loads the config (`Cli/Program.cs:42-43`);
   so: load the config, rebuild the paths with the accepted extras, rebuild the file system and what holds it
   (`Signals`), then run — tested by asserting that the run's actions' policy holds the extras. *Residual, stated:*
   `CommandPolicy.Product` is static, so the extras never reach the never-list's protected-path ARGV rule; they are
   protected by the `DeletionPolicy` and by R2.1's no-overlap rule, not by argv inspection.
3. The walk never enters a `neverEnter` folder (H2), never follows a link, never opens a file (H3), never crosses a device
   (C1); per folder the existing ceiling, and the total ceiling of D1. Over-exclusion is the safe side: `memory` is skipped
   anywhere in EVERY agent's tree, manual entries included ("of any agent"), and the answer says
   `excluded: ["memory (never entered)"]` — the size is "excluding memory", never presented as the whole. `TreeWalk`'s
   `neverEnter` matches exact names only (`Files/TreeWalk.cs:62`); it is widened for a prefix exclusion (Gemini CLI's
   tree excludes `antigravity*`, which is its own agent) and the per-directory device check, and grouped so one pass
   measures one agent's folders.
4. Names that reach the screen — project folders, session names, agent names derived from a file name — are
   attacker-settable (any process can create `~/.claude/projects/<anything>`): control and bidi characters stripped and
   lengths capped before a modal or the DOM (E6's `text/safeText.ts`, reused), `textContent` only (§15g M7). They never go
   into goldens or fixtures (§15i A: `FixtureIdentity`, `FixturePrivacyTests`).

#### E7.S0 as built (2026-10-05)

Built on `feat/wc-e7-agents-settings`; the record of every guarantee, its red and its teeth is `research/module_tests.md`
§ *The configuration trust (E7.S0)*, the design `research/architecture.md` § *The configuration trust (E7.S0)*.
**Deviations from the text above:**

- **A third text shape.** Besides a closed set and a path, `distro` is a `TextRule.Matching` key (the extension's distribution
  pattern) — free-shaped text is allowed ONLY for a `daemonUnused` key, and a test holds it so (`ConfigKeyShapeTests`).
- **The group-writable layer:** refused with the fix in the message ("run chmod go-w <file>; config set writes it 0644"),
  which `doctor`'s config check and `status`'s `configError` carry — chosen over accepting a private group (no `/etc/group`
  parsing). Measured: a WSL Ubuntu login shell's umask for a normal user is 0022, so a hand-made layer is 0644.
- **The machine layer** is read as root's own state file (`ReadStateFile`, uid 0, no group / other write): a machine layer
  an administrator made group-writable now makes the run observe-only, naming the fix — the same class, swept.
- **`UserConfigWriter`** reads the layer it rewrites with the bounded `ReadRegularFile` (regular, nonblocking, capped) and
  still follows a link there — it is the user's own process; the atomic write replaces a linked layer as it did before.
- **The slot-bounds rule** is EQUALITY: every number slot a key fills accepts exactly that key's range times its unit
  (journal keep days, A6's hours, A7's cap), and every other number slot is declared "not configuration" by name.
- **Surfaces:** `configNotices` in `status`, `doctor`, `config get` and both run details; `userLayerDigest` in `status` only;
  the run details' `config` lists every non-default setting with its layer (not only the keys an action read). All additive,
  absent when empty. Capability `config.contract`.
- **The read-site classification** also covers listings and walks of target-home folders; its residual — a listing of a
  folder that is itself a link lists through it (names only) — is stated in the table.
- **Not changed:** the never-list's protected-path argv rule stays static (R2.2's residual is E7.S2's).

#### E7.S0 review round (2026-10-05) — two own reviews (security; correctness)

Every finding ACCEPTED and fixed in its own commit (`fix(daemon): E7.S0 review round …`), each behaviour seen red for the
real symptom, green, and its load-bearing line broken and seen red again — the record is `research/module_tests.md`,
*The E7.S0 review round*.

| # | Finding | Disposition |
|---|---|---|
| S1 (Medium) | the drvfs reader's `O_NOFOLLOW` guarded the LAST component only — `…/Users/me/.docker` → `/root/.docker` took root's read to a root file with no owner check; a profile `C:\..\..\root` became `/root` | **Fixed** — `BeneathFiles`: every folder below the one holding the drive letter's folder is opened from the previous descriptor with `O_PATH \| O_DIRECTORY \| O_NOFOLLOW`, the file with `O_NOFOLLOW \| O_NONBLOCK` from the last; a profile path with a `..` segment or a control character is not a drive path (`HealthCollector.InDistro`). **Deviation:** the folder chain was chosen over the review's device comparison — it refuses a link on the same filesystem too |
| S2 (Medium) | the read-site table missed reads through a wrapper | **Fixed** — the scan also matches `ProcText.Read` / `Bytes`, `RegularFiles.Read` / `ReadHead` / `ReadOwned` / `ReadNoFollow`, `BeneathFiles.Read` and a bare `ReadText`; 24 more sites classified (all the kernel's or root's); a planted companion for each wrapper. The typed-system-path alternative was not taken (22 call sites for no further guarantee) |
| S3 (Low) | = C5 | see C5 |
| S4 (Low) | `doctor`'s text printed a user layer's unknown key raw — an OSC 52 sequence reached the admin's terminal | **Fixed** — every `doctor` text line goes through `CommandLine.Printable` |
| S5 (Low) | .NET's `$` matches before a final newline: `distro` took "Ubuntu\n" | **Fixed** — a `TextRule.Matching` value is accepted only when the match covers the WHOLE value (the expression stays JavaScript-compatible for the contract) |
| S6 (Low) | a linked `~/.config/wsl-care` let the user learn a root-only file's owner and mode from the run's reason | **Fixed** — the user layer and every target-home file are read through the same folder chain from the home (`ReadUserFile(…, beneath)`): a link anywhere below the home is refused naming the component, nothing beyond it described |
| S7 (design → decided) | A11 ended idle orphans of ANY non-root account | **Fixed** — A11's candidates are the TARGET user's processes only; with no single target user its preview is unavailable, naming why |
| C1 (Important) | a refused (observe-only) configuration pruned the run logs with the DEFAULT 14 days, deleting an admin's longer retention | **Fixed** — an observe-only run prunes nothing and logs one warning saying so |
| C2 (Important) | without interop an UNPRIVILEGED answer showed a user value the root timer would ignore, with no notice | **Fixed** — `UserLayerTrust.RootTimerReads`: an unprivileged run inside the distro also reads the layer as the root timer would and adds those notices ("the root timer ignores this value: …") without changing its own configuration; the head goldens of `status` / `doctor` carry them (the golden sandbox has no interop entry) |
| C3 (Minor) | only a group-writable layer got a fix hint; `config set` over a linked layer wrote through the link and then printed the default | **Fixed** — every refusal carries its fix (link, foreign owner, oversize, not regular, group-writable); `config set` replaces a LINK by a regular file holding the values read through it (`IFileSystem.ReplaceLinkWithFile`, judged where the link itself lives — the file it pointed at is untouched) and prints the value it wrote |
| C4 (Minor) | an invalid machine-only value in the user layer made the whole run observe-only | **Fixed** — a machine-only key in the user layer is a notice BEFORE validation |
| C5 (Minor, both) | `idle.minutes` declared "higher is safer" but the gate judged ONE load average, so a longer window could be looser | **Fixed** — busy is the HIGHEST average up to the window; the contract's direction is now true (no contract change) |

#### E7.S1 as built (2026-10-05)

Built on `feat/wc-e7-agents-settings`; the record of every guarantee, its red and its teeth is `research/module_tests.md`
§ *The AI agents: catalogue, discovery, the walk (E7.S1)*, the design `research/architecture.md` § *The AI agents*.
**Deviations from the text above:**

- **As root, NO PATH lookup at all** — the story row's "the target user's fixed bin list as root" is replaced by D3 as the
  review round amended it: root counts folders only; no binary, no npm package, no version is looked at as root.
- **The walk runs on the Linux side only.** `collect` walks the agents found by FOLDER when its folder walk is due and
  records `slow.agents`; the Windows binary answers `agents list --measure` (now, its own folders) and reads no history —
  the hourly Windows walk and `agents-last.json` stay E7.S5's.
- **Session names are relative to the layout's folder** (`projects/<project>/<id>.jsonl`) and appear only in a live
  answer (`--measure`), at most five; the persisted sample keeps the largest session's SIZE only.
- **The never-list's agent names** are each catalogue folder's first segment that is not a generic container (`.cache`,
  `.config`, `.local`, `share`), lowercased — except Roaming's bare `Claude`, which the never-list already recognises by
  its parent (a bare `claude` would refuse every binary of that name).
- **The budget's time left is read once per folder** — the first version read the clock twice and could start a folder
  with a negative ceiling (seen red, fixed).
- **`agents list` text form** prints the tracked agents and "N more catalogue agent(s) not found here"; JSON carries all
  twelve, `tracked: false` included, so a client can show what is looked for.
- **Measured** on this machine (WSL, Release, `--measure`, a normal user): six agents, ~2.5 GiB, 685 sessions and
  conversations, 1.3–2.3 s — the 3-minute budget is far from binding today.
- **Capability `agents.list`** ships with E7.S1 (the story row put it in E7.S2 with the others); `agents.probe` and
  `config.agentsExtra` stay E7.S2's.

#### E7.S2 as built (2026-10-05)

Built on `feat/wc-e7-agents-settings`; the record of every guarantee, its red and its teeth is `research/module_tests.md`
§ *Manual agents and agents probe (E7.S2)*, the design `research/architecture.md` § *Manual agents and `agents probe`*.
**Deviations from the text above:**

- **`aiAgents.extra` has no safe direction** (`KeyTrust` `none`, not root-effective in the contract): root takes it as DATA,
  judges every entry again on every read, and the key can only ADD protection; a refused entry leaves the walk only.
- **`config set aiAgents.extra` takes `-` only** (stdin, the `StdinList` cap and ceiling, widened with a shared
  `StdinList.MaxBytes`); a JSON value on the command line is refused naming the stdin form.
- **The probe's own exit code** — `81 NotAsRoot` (added to `ExitCode` and `contracts/exit-codes.json`) rather than the
  usage code: the extension can tell "you asked as root" from "a malformed path". On the Windows binary the probe answers
  "arrives with E7.S5b" (usage).
- **The probe's candidate folders** are `~/.<name>`, `~/.config/<name>`, `~/.local/share/<name>`, `~/.cache/<name>` that
  exist; the suggested entry holds the ones the rules accept; a catalogue binary name answers "already tracked".
- **Windows entries** pass the shape check; the Linux binary neither lists nor walks them (they are not its side); the
  Windows binary protects their folders as spelt (`WindowsHostPaths.WithExtraAgentRoots`) — their rules and walk are E7.S5b's.
- **The `cli` field is never looked at, by any run** — not only root's: `agents list` run by the user does not stat it either.
- **The overlap refusal is the ENGINE's** (`AgentFolderOverlap`, asked for every preview of an action with `HomeRoots`), not
  each action's; `HomeRoots` is a default interface member (empty), declared by A8, A12, A14, A17, with A3 the one named
  user-scoped exception.
- **`ProtectedRoots` keeps the real path AND the spelling of every agent root** (catalogue ones too), not only of extras.
- **`SessionGlob` gained `**`** (a whole segment: the folder it is in and every folder below, breadth first, `memory` and
  links never entered, the entry cap applies).
- **An agent's total became unavailable when any folder of it was not measured** (and growth needs two whole walks) — an
  E7.S1 behaviour found wrong by this story's refused-entry scenario (an "available" 0 for a refused agent), fixed here.
- **Capabilities** `agents.probe` and `config.agentsExtra` (`agents.list` shipped with E7.S1).

#### E7.S1/S2 review round (2026-10-05) — two own reviews (correctness; safety/security)

Every finding ACCEPTED and fixed in its own commit (`fix(daemon): E7.S1/S2 review round …`), each behaviour seen red for its
real symptom, green, and its load-bearing line broken and seen red again — the record is `research/module_tests.md`,
*The E7.S1/S2 review round*.

| # | Finding | Disposition |
|---|---|---|
| R1 (High) | discovery searched only `PATH` — under `wsl.exe --exec` it has no `~/.local/bin` / nvm bin and 30+ Windows folders on drvfs (measured, `research/2026-10-03_wsl_exe_facts.md` row 21): a 0777 `%APPDATA%\npm` shim was a "binary", every name was stat-ed over 9p unbounded | **Fixed** — a FIXED list first (nvm's default, `~/.local/bin`, `~/.cargo/bin`, `~/.npm-global/bin`, `/usr/local/bin`, `/usr/bin` — `TargetUserCommands`' list, now shared, `~/.npm-global/bin` added for root too), then only the PATH entries neither under the automount root nor on another filesystem than `/`; the whole lookup bounded (`Processes/Bounded.Run`, shared with the system-drive lookup) |
| R2 (High) | a walk cut by a limit was shown as the whole total and growth was taken against it | **Fixed** — a cut total keeps its reason (`available` with the reason); growth only when BOTH walks are `Whole`; the test that pinned the old behaviour corrected |
| R3 (High) | the version came from the first npm root holding the package (often the OLDEST nvm), and a link was followed one hop | **Fixed** — the found binary's FULL link chain (≤ 40 hops): a version in a link target, or the package the chain leads into (`…/node_modules/<pkg>/`); the existence-only npm fallback only when no binary was found; the source is named |
| R4 (Medium) + S3b/S3c | the session listing had no device check, no deadline inside a level, no cancellation, followed a linked start folder, listed folders twice, `**/**` double-counted, and an intermediate stop became `counted: true, count: 0` | **Fixed** — `SessionGlob` rewritten: each folder listed once (a per-call cache), the deadline and the token before every listing, the listing's device kept (a folder on another one left out and said), an intermediate stop = not counted with its reason; the start folder must pass `AgentWalk.PlaceProblem`; `GlobProblem` refuses a second `**` |
| R5 (Medium) + S7 | `agents probe` gave each candidate its own 60 s | **Fixed** — one walk over all candidates, one budget |
| R6 (Medium) | `files: 0` for a folder not measured | **Fixed** — `null` when the bytes are unavailable |
| R7 (Medium) | a session's size was its transcript only (D2's one session is more) | **Fixed** — the catalogue's layouts gain `companions` (Claude Code `{dir}/{id}` + `file-history/{id}`; Antigravity `brain/{id}` + `annotations/{id}.pbtxt`), sized by stat / the same walk rules under the budget; `sessionWarnMb` now sees them |
| R8 (Medium) + S5 | removing `Program.Main`'s phase two or the production `Rewire` turned no test red | **Fixed** — `AgentsExtraFlows.A_manual_agent_folder_inside_a_cleanup_folder_makes_the_root_cleanup_refuse_through_the_built_cli` (built binary, root claimed) — red at first for a REAL E7.S2 defect: the action's SKIP was read before the overlap refusal, so A12 over an agent folder read "nothing to remove"; the overlap refusal now replaces the action's own refusal AND its skip |
| R9 (Minor) + S6 | `slow.agents` could carry a project folder's name in `excluded` | **Fixed** — a persisted walk keeps "N folder(s) on another filesystem"; names in live answers only |
| R10 (Minor) | `config set` could write a layer larger than its own reader takes (JSON escapes non-ASCII as six bytes) | **Fixed** — `UserConfigWriteResult.TooLarge`, refused naming the size and the cap, nothing written |
| R11 (Minor) | the probe's "may start" was any execute bit | **Fixed** — `access(X_OK)` for the invoking user (`RegularFiles.MayExecute`) |
| R12 (Minor) | `agents probe a b` said "the path ;" | **Fixed** — "takes one path …; got an extra argument …" |
| R13 (Minor, doctrine) | outcome tuples, a two-nullable `Side`, cyclomatic complexity > 4 | **Fixed** — `ExtraList` / `ExtraEntryRead` / `VersionFound` / `StartCheck` records, `DiscoverySide` a closed hierarchy (`Distro` / `WindowsHost`), the shape rules a rule list, `SessionGlob.Matches` split |
| — (extension need) | E7.S3 reverts a setting on exit 2 and must tell "the default user is root" from a refused value | **Done here** — `config set` / `config reset` as root for the target user exit **81** (`NotAsRoot`), like `agents probe`. For E7.S3 / E7.S4: `DAEMON_EXIT` gains 81 with its failure text; the host's call ceilings for `agents list --measure` and `agents probe` derive from `AgentWalk.MeasureNowBudget` (60 s) plus the lookup ceiling (10 s) |
| S1 (Important) | an extra's folder could be `/`, `/var/lib/wsl-care` or a drive root and join the protected roots unbounded — root's OWN state writes refused (a user→root denial with interop off) | **Fixed** — `ExtraRoots`: a folder (spelt AND real) is protected only inside a protected home, never a filesystem root, never equal to or holding wsl-care's own folders; the rest dropped with a configuration notice. B2 holds: every cleanup folder is inside the home |
| S2 (Important) | a manual data folder at or under a `memory` folder was walked | **Fixed** — refused by the rules (spelt or real, any case); `TreeWalk.Measure` refuses a ROOT whose own name is never-enter |
| S3a (Important) | catalogue folders were compared with the walk root's device, not the home's; a catalogue folder that IS a mount, or is reached through a linked middle component, was walked over 9p; S3d the TOCTOU between the checked real path and the walked spelled path | **Fixed** — `AgentWalk.PlaceProblem` before every walk and listing (catalogue and manual alike): the real path must be the spelled place under the home's real path, and its device the home's |
| S4 (Important) | npm / pip / uv / pnpm can move their cache into an agent folder through their own configuration FILES (HOME is set) — the static `HomeRoots` could not see it; `ToolCacheTrims`' comment claimed the opposite | **Fixed** — each tool is ASKED first through a declared read template (`npm config get cache`, `pnpm store path`, `uv cache dir`, `pip[3] cache dir`), as the target user, bounded; the answer is checked against every agent folder; an unreadable answer is a refusal; A8 refuses in its preview, A17 skips that tool with a note; the comment corrected. Residual (README, architecture): a tool configured by an environment variable in the user's shell is not seen — the commands run with a clean environment, so such a variable does not apply to them either |
| S8 (Low) | `memory` was matched by exact case | **Fixed** — never-enter names and prefixes compared case-insensitively |
| S9 (Low) | the inotify proof dropped folder events (H3 proved, H2 not) | **Fixed** — `FolderEvents()`: the memory folder is never even opened. The strengthened test first went red for a TEST defect (the watch's own set-up listing every folder); the set-up's events are now drained before the walk |

#### E7.S2b — orphaned AI-agent processes (owner decision 2026-10-05)

**The ask.** AI-agent CLI processes (`claude`, and the other members of the `ai-agents` process family) left behind on WSL
— their terminal gone, re-parented to init, doing nothing — hold memory for days. The owner wants them cleanable. A11
excludes the family today and Q13 assumed it stays excluded; this amendment answers Q13: **not through the general
`processes.families` list (its exclusion of `ai-agents` stays, E7.S0's B1 test holds it), but through a dedicated path of
its own.** Built AFTER E7.S2, as its own story.

**Decided:**

1. **A new action id, `A18` — "orphaned AI-agent processes", a BUTTON only.** Not a section of A11: A11 has a timer
   switch (`auto.A11`), and a part of it that must never run on the timer would be an exception inside one action. A18 has
   no `auto` key; its `Trigger` never fires (as A12's, `BrowserAndHttpCaches.cs:77`), and the timer pass and
   `collect --timer` never select it, whatever any setting says — a test holds both. Its own preview row; `contracts/actions.json`
   and the extension's registry gain `A18` (generated / held equal as today); the reserved name `collect` stays reserved.
   Execution order: beside A11 (after the Docker actions, before A1 / A2), user-scoped (`CommandScope.User` — the target user).
2. **Eligibility — ALL of:** the process is in the `ai-agents` family (`Collectors/ProcessFamilies.cs`, the catalogue's
   binaries); it belongs to the TARGET user (E7.S0 review S7 — never another account's, never root's); its parent is gone
   (re-parented to init / the session's subreaper, as A11 judges "orphaned"); no controlling TTY; **no CPU for N hours,
   measured** (item 3); and **no live session of that agent**: the agent's session layout (E7.S1, D2) is CONFIRMED and no
   session file of that agent under the target home was modified within the last N hours (listing + stat only, as E7.S1's
   `SessionGlob`; a listing cut by its ceiling or its time = "cannot tell" = not eligible). An agent whose layout is
   unconfirmed (monitor only), or a process the catalogue cannot attribute to ONE agent, is **never eligible**.
3. **"No CPU for N hours" is measured, not approximated.** A11 judges "older than N h and < 1 CPU-second over the last
   interval"; A18 does not. Every run (timer or button, preview included) records, for each `ai-agents` candidate of the
   target user, its cumulative CPU ticks (`/proc/<pid>/stat` utime + stime) keyed by its IDENTITY — `(pid, boot_id, start
   ticks = /proc/<pid>/stat field 22)`. A process is idle for N h only when the state holds a sample of the SAME identity
   taken ≥ N h ago whose CPU ticks equal now's. Missing history, a different boot, a different start time (a reused pid) →
   not eligible; so the first runs after install never end anything.
   **State file** `/var/lib/wsl-care/agent-cpu.json` — root-owned 0600, written atomically (temp + rename, the state-file
   reader on the way back: `ReadStateFile`, uid 0, no link, no wait, a cap); per identity the OLDEST sample whose ticks
   still equal the newest (so "unchanged since" is one comparison) and the newest; entries for live processes only, pruned
   on every run; capped (≤ 512 identities, ≤ 64 KiB) — over the cap the oldest identities drop and are simply "no history".
   An unprivileged run neither reads nor writes it (its preview says "measured by the root runs only").
4. **A new setting `processes.aiAgentsIdleHours`**, default **4**, range **1–168** h, `KeyTrust.Higher` (a longer idle
   requirement is stricter — a user layer may only raise it when root does not trust it, R1.2), in `default.json`,
   `ConfigKeys`, `contracts/config-keys.json`; mirrored by E7.S3 like every other key. The SAME N is the CPU window and the
   session window.
5. **The confirmation** (the button's modal, E6's host modal) names every process: agent, pid, idle hours (the measured
   "unchanged since"), its project folder when the session listing attributes one — every string sanitised (R2.4, E6's
   `safeText`). The run re-checks each target's identity and ticks (as A11, `SuspectTermination.cs:176-186`): a process
   that used CPU, gained a terminal, changed owner or is a different identity is KEPT, with why.
6. **Signals as A11:** SIGTERM through the pidfd sender by pid AND start time, SIGKILL after 10 s if still the same
   identity; the run detail lists every process with its outcome (ended by SIGTERM, by SIGKILL, kept, already gone).

**Files:** `Actions/ActionId.cs` (A18 in `All` and the execution order), `Actions/Suspects/AgentOrphans.cs` (new, the
action — reusing `SuspectTermination`'s sampling and signal path by extraction, not copy), `Actions/Suspects/AgentCpuHistory.cs`
(new, the state file), `Agents/AgentCatalogue.cs` + `Agents/SessionGlob.cs` (the "live session" check),
`Collectors/ProcessFamilies.cs` (agent attribution), `Config/ConfigKeys.cs` + `default.json`, `Actions/Engine/ActionRegistry`
(registration; the timer's selection), `contracts/actions.json`, `contracts/config-keys.json`, the extension's action
registry (generated), `research/module_tests.md`, `research/architecture.md`, `README.md`.

**RED tests** (each red for its real symptom first, then the teeth): `Ai_agent_orphan_is_eligible_only_after_N_hours_without_cpu_by_identity`;
`Missing_history_never_makes_a_process_eligible`; `A_reused_pid_is_never_treated_as_idle` (same pid, another start time /
boot); `A_live_session_keeps_the_agent_process` (a session file modified within N h); `An_unconfirmed_layout_keeps_the_agent_process`;
`Another_accounts_agent_process_is_never_a_candidate`; `A_process_with_a_terminal_or_a_live_parent_is_kept`;
`The_timer_never_ends_an_agent_process` (`collect --timer` with every `auto` on); `The_run_rechecks_identity_and_cpu_before_each_signal`;
`The_cpu_history_is_root_only_bounded_and_pruned_to_live_processes`; `processes_aiAgentsIdleHours_is_1_to_168_default_4_safe_higher`;
the families exclusion of E7.S0 still green (`ai-agents` NOT choosable in `processes.families`).

**DoD:** the RED tests above seen red then green, each guard broken and seen red again; A18 a button only by structure and
by test; the history file bounded, root-only, pruned; the setting in the contract; the modal's strings sanitised; docs,
goldens (the preview row, `capabilities` unchanged unless the extension needs one — decided at build), module_tests flows.
**Windows** (W-A11, E12) stays separate; the same rule should apply there — a setting defaulting to 4 h, a button only
(recorded in *Boundaries*). The coai code round after E7.S2 covers E7.S2b if it is built by then; otherwise it gets its own.

#### E7.S2b as built (2026-10-05)

Built on `feat/wc-e7-agents-settings`; the record is `research/module_tests.md` § *A18 — orphaned AI-agent processes (E7.S2b)*.
**Deviations from the text above:**

- **A preview writes no state.** The decided text had "every run, preview included" record the CPU history; the product's
  standing rule is that `act --preview` touches no state (`ActFlows`), so ONLY the timer's full run records it
  (`ActionEngine.RecordAgentCpu`, before the pass; a failure is a note of the pass). A18's preview reads the history and
  merges the processes it sees NOW in memory — so a process first seen by the preview is "no history".
- **`ActionId` has a closed `TimerSwitch`** (`Auto(key)` | `ButtonOnly(why)`); A18 is the one button-only id, not derived
  from an `auto.*` key. The timer pass does not even select it, and the engine's timer gate skips it if asked directly.
- **The history cap is 128 KiB, not 64 KiB:** a test sized a full 512-entry file at ~90 KiB — the planned cap would have
  read a full history as "no history" (seen red, fixed; the growth row corrected).
- **A18 is user-scoped** (it needs the target user) and declares no home folder (`ActionHomeRootsTests` names it).
- **The signal path was EXTRACTED from A11** into `SuspectSignals` (re-read, one SIGTERM each, one shared grace, SIGKILL,
  the verdicts) — A11's own tests unchanged and green.
- **Agent attribution** is by the process's program or the script node runs (its first two arguments' file names, `.exe`
  stripped) against the catalogue's binaries; none or more than one = kept.
- **An unreadable boot id** makes the preview unavailable (no idle time can be told).
- **The extension side** (A18 in its action registry, the modal naming each process sanitised) lands with E7.S3 / E7.S4 and
  PR #12's registry; `contracts/actions.json` carries A18 now.

#### E7.S2c — every number is configurable (owner rule 2026-10-05): the inventory

> **The owner's rule (2026-10-05, verbatim):** "все цифры, которые у нас есть - должны быть настраиваемые" — *every number we
> have must be configurable.* **Standing convention from now on:** a new behavioural number is a configuration key (daemon)
> or a VS Code setting (extension), never a literal; recorded in `research/architecture.md` § *Numbers are configuration*.
> Built as **E7.S2c**, after E7.S2 and E7.S2b. This subsection is the INVENTORY (taken 2026-10-05 from `feat/wc-e7-agents-settings`
> at `217ec0f` + the E7.S2 work in progress, and from the open extension branch `feat/wc-e6-cleanup-logs` at `423b526`, PR #12, whose
> numbers this branch does not hold yet); `file:line` as read then — E7.S2c re-verifies each before it moves it.

**The three groups (the coordinator's decision, told to the owner):**

- **(A) behaviour** → an ordinary key, the user layer allowed, mirrored by E7.S3, `min`/`max` and a safe direction in
  `contracts/config-keys.json`.
- **(B) root-safety limit** (a cap, a queue budget, a ceiling that bounds what ROOT reads, does or waits for, a walk budget)
  → a key that is **machine-layer only** (`KeyTrust.MachineOnly`, like `archive.baseFolder`) with a HARD safe range: the
  limit may be lowered freely and raised only up to a fixed maximum (today's value unless the row says otherwise). The
  extension shows it read-only: "set in /etc/wsl-care/config.json".
- **(C) not configurable** — a format, a contract or a unit, not behaviour; each with its one-line reason.

Extension-only numbers become VS Code settings (`application` scope) with ranges; **a timeout must stay above the daemon
ceiling it waits on — a test computes the daemon's worst case from the C# constants (keys after S2c) and fails below it.**

**Counts.** Daemon: **A 35, B 77, C 71** (183 rows). Extension on this branch: **A 2** (1 is the existing
`refreshSeconds`), **B 11**, **C 31**; extension on PR #12 (new or changed): **A 9, B 20, C 22**. Already configurable: the
49 daemon keys of `ConfigKeys.cs` and the 2 VS Code settings (`wslCare.refreshSeconds`, `wslCare.distro`).

##### Found by the inventory — defects to fix in S2c (each RED first)

| # | Finding | Fix proposed |
|---|---|---|
| N-1 | `doctor`'s extension ceiling (`src_vs_code/src/client/verbs.ts:41`, 100 s) is BELOW the daemon's worst case: 4 × `systemctl show` + `systemctl --version` at `SystemdCommands.Ceiling` 15 s (`Core/Systemd/SystemdCommands.cs:38`, `:55`) + `docker version` 10 s (`DockerCommands.cs:52`) = 85 s, + up to 4 s drain per killed command (`ProcessCommandRunner.DrainGrace` 2 s, waited twice, `ProcessCommandRunner.cs:229-242`) = 109 s | the setting's minimum = the computed worst case; the test above |
| N-2 | `preview --all`'s 330 s (`verbs.ts:42`) equals its worst case exactly (310 s + 20 s drain, ≤ 100 containers); on PR #12 the SAME 330 s is the ceiling of `act <ids> --preview` for "Clean selected", where EACH Docker row takes its own Docker snapshot (`DockerLook.TakeAsync`, `DockerLook.cs:17-23`) — k × 310 s, 1 860 s for six rows | daemon: one Docker snapshot per preview call, shared by the Docker actions; extension: the ceiling computed from the selected ids until then |
| N-3 | PR #12's detach ceiling (`root/rootCall.ts:59`, 90 s) holds for at most ONE stale request: the sweep runs one 15 s `systemctl show` per stale request (`RequestSweep.cs:42-59`, `:119-131`); two give 101 s. A timeout there is "outcome unknown" and followed — lower cost, still wrong | bound the sweep's shows per call, or compute the ceiling; the test above |
| N-4 | the TIMER unit kills a whole full run at `TimeoutStartSec=10min` (`src_daemon/systemd/wsl-care.service:58`) while one prune command may run 15 min (`DockerCleanupCommands.PruneCeiling`, `:29`), a full collect already takes ~3.5 min (§17 #2) and E7.S1 adds an agent walk of up to 3 min; `CommandRequest.cs:19`'s comment "systemd stops the unit at 10 minutes" says the ceilings were meant to fit — they do not | the unit limit derived from the run's budget (B key `timer.runLimitMinutes`, installer writes it), or the prune ceiling below it; owner decision |
| N-5 | `PhysicalFileSystem.ReadFile` has no byte cap (`Files/PhysicalFileSystem.cs:48`) and root reads `running.json`, `history.jsonl`, run details, `volume-seen.json`, `/etc/passwd`, `wsl.conf`, the clock and dry-run state files and the container-starts files through it | a B cap per class (`records.maxStateFileBytes`), the read-site table extended to say which reader is capped |
| N-6 | the same number written twice and drifting apart: 1 MiB in 12 places, 4 MiB ×2, 64 KiB ×2, 4096 ×2 (one a bare literal), 300 chars ×3, 200 chars ×2, 5 s lock timeout ×2, the GiB constant 10 times, the 4 h timer period ×4, and ~20 sentences that spell a constant's value as text ("newest 2", "200 MiB", "10 s", "5 minutes", "90-day" …) | one source per number; every sentence formats the effective value |

##### Group A — behaviour → ordinary keys (user layer allowed)

| Today (file:line, value) | Meaning | Key | Range | Default | Safe |
|---|---|---|---|---|---|
| `Folders/FolderSizes.cs:32` 20 h | folders walked again after | `walk.intervalHours` | 4–168 | 20 | higher |
| `Files/TreeWalk.cs:41` 20 | exclusions named per walk | `walk.maxExclusionsNamed` | 0–100 | 20 | none |
| `Agents/AgentWalk.cs:25` 60 s | `agents list --measure` budget (must stay below the client's call ceiling — test) | `agents.measureBudgetSeconds` | 5–60 | 60 | lower |
| `Actions/Engine/StopMarkers.cs:21` 1 day | stop marker kept | `runs.stopMarkerRetentionHours` | 1–168 | 24 | higher |
| `Actions/ICleanupAction.cs:114` 20 | items a preview names (the modal) | `preview.maxItems` | 1–100 | 20 | none |
| `Actions/Clock/ClockFix.cs:52` 1 h | at most one clock fix per | `clock.minimumGapMinutes` | 60–1440 | 60 | higher |
| `Thresholds/ThresholdRules.cs:71` 5 min | two drift observations apart (gates A16) | `clock.driftObservationsApartMinutes` | 1–1440 | 5 | higher |
| `Actions/Disk/FilesystemTrim.cs:38` 7 d | A15 runs weekly | `trim.periodDays` | 1–90 | 7 | higher |
| `Actions/PackageCaches/PackageCacheClean.cs:32` 200 MiB | A9 trigger | `aptCache.triggerMb` | 0–100 000 | 200 | higher |
| `Actions/UserCaches/ToolCacheTrims.cs:34` 5 GiB | A17 trigger | `toolCaches.triggerGb` | 0–100 000 | 5 | higher |
| `Actions/UserCaches/EditorServerCleanup.cs:29` 2 | A14 keeps the newest N builds | `editorServers.keepNewest` | 1–20 | 2 | higher |
| `Actions/Suspects/SuspectTermination.cs:36` 5 s | A11's no-CPU window | `processes.cpuWindowSeconds` | 5–60 | 5 | higher |
| `Actions/Suspects/SuspectTermination.cs:39` 10 s | A11 SIGTERM → SIGKILL grace (plan §5 fixes 10 s: raise-only) | `processes.termGraceSeconds` | 10–120 | 10 | higher |
| `Records/RunRetention.cs:23` 90 d | history and run-detail retention (tighten-only for root, as `logging.retentionDays`) | `runs.historyRetentionDays` | 7–3650 | 90 | higher |
| `Events/ContainerStartsStore.cs:21` 14 d | container-start lines kept | `events.startsRetentionDays` | 1–3650 | 14 | higher |
| `Events/Coverage.cs:41`, `:44` 5 s / 5 min | Docker socket retry, first / longest | `events.retryFirstSeconds`, `events.retryMaxSeconds` | 1–60, 5–3600 | 5, 300 | higher |
| `Events/Coverage.cs:48` +5 min | follower stale after segment + | `events.stalenessSlackMinutes` | 1–60 | 5 | none |
| `Events/Coverage.cs:50` 5 | top images in the starts summary | `events.topImages` | 0–50 | 5 | none |
| `Events/EventsFollower.cs:40` 10 min | one `docker events` segment | `events.segmentMinutes` | 1–60 | 10 | none |
| `Thresholds/ThresholdRules.cs:38`–`:77` (14 rows) | page cache warn 15 GiB / act 12 GiB / act-below-available 30 %, inactive anon 15 GiB, order-7 blocks 32, PSI 10, `/` used 80 %, journal 1 GiB (also A10's trigger, `JournalVacuum.cs:97`), journal history 7 d, clock jumps 100 / 4 h, collector fresh 30 min, recommended `.wslconfig` memory 36 GB, WSL memory critical 90 % | `thresholds.pageCacheWarnGib`, `.pageCacheActGib`, `.pageCacheActAvailablePercent`, `.inactiveAnonWarnGib`, `.order7WarnBlocks`, `.memoryPressureWarn`, `.rootUsedWarnPercent`, `journal.maxGb`, `thresholds.journalHistoryWarnDays`, `.clockJumpsWarnPer4h`, `.collectorFreshMinutes`, `wslConfig.recommendedMemoryGb`, `thresholds.wslMemoryCriticalPercent` | each as its unit allows (0–100 for a percent, 0–100 000 GiB) | today's | the act thresholds as their trigger (higher / lower), the warn ones none |
| `Collectors/ProcessCollector.cs:75` 30 | top processes in a full run | `processes.topCount` | 0–200 | 30 | none |
| E7.S2b (planned) | A18's idle hours | `processes.aiAgentsIdleHours` | 1–168 | 4 | higher |
| *extension* `poll/poller.ts:24` 120 s | status poll | `wslCare.refreshSeconds` (exists) | 30–86 400 | 120 | — |
| *PR #12* `cleanup/runFollower.ts:35` + `root/cleanupController.ts:71` 4 s (one setting, two copies today) | follower poll while a cleanup runs (plan M6: 3–5 s) | `wslCare.cleanup.followPollSeconds` | 3–5 | 4 | — |
| *PR #12* `cleanup/cleanFlow.ts:53` 5 min | a preview older than this is taken again before the modal | `wslCare.cleanup.previewExpiryMinutes` | 1–15 | 5 | — |
| *PR #12* `cleanup/cleanupHost.ts:42` 5 | results kept for *Last cleanup* | `wslCare.cleanup.resultsShown` | 1–20 | 5 | — |

##### Group B — root-safety limits → machine-layer-only keys (lower freely, raise up to the fixed maximum)

| Today (file:line, value) | Meaning | Key | Range (max = hard) |
|---|---|---|---|
| `Folders/FolderSizes.cs:35` 2 000 000 / 2 min (also `CacheFolders.cs:65`, `AgentWalk.cs:66`) | entries / time per folder walk | `walk.maxEntries`, `walk.maxSeconds` | 1 000–2 000 000, 5–120 |
| `Agents/AgentWalk.cs:22` 3 min | the agent walk inside a root collect | `agents.walkBudgetSeconds` | 10–180 |
| `Agents/SessionGlob.cs:17` 500 000 | entries one session listing sees | `agents.sessionMaxEntries` | 1 000–500 000 |
| `Agents/AgentDiscovery.cs:40` 1 MiB | `package.json` cap | `agents.maxPackageJsonBytes` | 64 KiB–1 MiB |
| `Actions/Engine/RunRequests.cs:82`, `:86`, `:90` 1 MiB / 64 / 32 | request file cap / files read / queue budget (derived: cap ≥ `act.maxShownNames` × 67 B; queue ≤ read) | `requests.maxBytes` (derived), `requests.maxRead`, `requests.maxQueued` | —, 32–64, 1–32 |
| `Actions/Engine/RequestSweep.cs:37` 60 s | a request's grace before swept (the extension's 90 s waits on it — test) | `requests.graceSeconds` | 30–600 (raise-only above 60) |
| `Actions/ICleanupAction.cs:68` 10 000 | names in one shown list (the extension's `MAX_SHOWN_VOLUMES` mirrors it — test reads the C# value) | `act.maxShownNames` | 1–10 000 |
| `Cli/Commands/ActCommand.cs:42`, `Cli/StdinList.cs` 1 MiB / 10 s | `--only` file / stdin cap and ceiling (also `config set aiAgents.extra -`) | `act.maxOnlyFileBytes` (derived), `act.stdinTimeoutSeconds` | —, 1–10 |
| `Actions/Engine/StopMarkers.cs:39` 4 096 B | stop marker read cap (a bare literal today) | `stops.maxMarkerBytes` | 256–4 096 |
| `Actions/Engine/DryRunWindow.cs:37` 7 d | first-week timer runs stay dry — **inverted**: lowering is the unsafe side | `dryRun.firstWindowDays` (raise-only) | 7–3 650 |
| every command template's ceiling (24 rows): Docker probe 10 s / listing 30 s / disk usage 2 min / removal 5 min / prune 15 min (`Docker/DockerCommands.cs:52-58`, `Actions/DockerCleanups/DockerCleanupCommands.cs:26-29`); systemd read 15 s / search 30 s / unit start 30 s (`Systemd/SystemdCommands.cs:38-42`, `UnitCommands.cs:23`); journal vacuum 5 min, sync 2 min, drop caches 30 s, compact 2 min, apt clean 5 min, snap remove 5 min, build-server shutdown 2 min, fstrim 10 min, npm clean 10 min, NuGet clear 10 min, tool trims 10 min, hwclock / chronyc 30 s, Windows clock probe 20 s, snap list 30 s, system-drive lookup 5 s | per-command time limits (the extension's call ceilings are computed from them — N-1/N-2) | `commands.<template>TimeoutSeconds` (one per template id; grouped where one constant serves several) | 1 s up to today's value |
| `Systemd/UnitCommands.cs:25` 120 s | `systemctl stop` ceiling — **inverted**: must stay above the unit's `TimeoutStopSec=90` | `systemd.unitStopTimeoutSeconds` (raise-only) | 91–300 |
| output caps: `CommandRequest.DefaultOutputCapChars` 1 MiB, Docker small 1 MiB / large 64 MiB / action 4 MiB, systemd 1 MiB / search 4 MiB / unit 64 KiB, health 1 MiB | bytes a command's output may hold | `commands.outputCapBytes`, `docker.smallOutputCapBytes`, `.largeOutputCapBytes`, `.actionOutputCapBytes`, `systemd.outputCapBytes`, `.searchOutputCapBytes`, `.unitOutputCapBytes` | 4 KiB up to today's |
| `Docker/DockerCommands.cs:28` 100 | ids per `inspect` / `rm` (command-line length) | `docker.batchSize` | 1–100 |
| user-file caps: Playwright link 64 KiB, `browsers.json` 1 MiB, `.obsolete` 1 MiB, nvm alias 4 096 B, `.wslconfig` 1 MiB, `daemon.json` 1 MiB | bytes root reads of a file someone else controls | `userFiles.maxSmallFileBytes`, `userFiles.maxJsonBytes`, `health.maxWslConfigBytes`, `docker.maxDaemonJsonBytes` | 256 B up to today's |
| quotes kept: `RunRecord.cs:155`, `DockerCli.cs:53`, `ToolAnswers.cs:15` 300 chars; `HealthCollector.cs:28-29` 5 lines × 200 chars; `ProcessFiles.cs:95` 200 chars | how much tool text a record keeps | `records.maxReasonChars`, `health.kernelLinesKept`, `health.kernelLineChars`, `processes.shownCommandChars` | 0 up to today's |
| `Records/RunRecordWriter.cs:19`, `Events/ContainerStartsStore.cs:26` 5 s | lock waits | `records.lockTimeoutSeconds` | 1–30 |
| `History/RunLogs.cs:50` 50, `History/LogPeriod.cs:12` 366 d | run details one `logs` opens; longest range (must stay ≥ the history retention) | `logs.maxDetailsRead`, `logs.maxRangeDays` | 1–50, 90–366 |
| `Processes/ProcessSignals.cs:112` 5 s | wait after SIGKILL | `processes.killWaitSeconds` | 1–30 |
| `Events/EventsFollower.cs:43` 1 min | a segment's ceiling beyond its length | `events.segmentSlackSeconds` | 10–60 |
| `systemd/wsl-care.service:58` 10 min (N-4) | the timer run's whole-run limit (the installer writes the unit) | `timer.runLimitMinutes` | owner decision |
| *extension* `client/verbs.ts:39-42`, `client/WslCareClient.ts:66`; *PR #12* `root/rootCall.ts:59`, `:62`, `client/verbs.ts:99-103` | call ceilings: status / version 20 s, doctor 100 s, preview 330 s, `wsl --list` 15 s, detach 90 s, stop 150 s, run reads 20 s | `wslCare.timeouts.statusSeconds`, `.doctorSeconds`, `.previewSeconds`, `.actPreviewSeconds`, `.wslListSeconds`, `.detachSeconds`, `.stopSeconds`, `.runReadSeconds` | minimum = the daemon's computed worst case (test); max 900 / 3 600 |
| *PR #12* `cleanup/runFollower.ts:37` 30 min | a journal entry's ceiling, then "state unknown" (the act unit has no whole-run limit) | `wslCare.cleanup.followCeilingMinutes` | 30–240 |
| *extension* `panel/format.ts:10` 500 and the PR #12 clips (120, 80, 200, 40, 1 000) | text clips on attacker-settable strings | ONE `wslCare.panel.maxTextLength` with a hard maximum, the clips derived | 100–1 000 |

##### Group C — not configurable (format, contract, unit)

`ExitCode` values and `exit-codes.json` (a process contract) · `SchemaVersion.Current` (wire contract) · the 64-hex volume /
container id and the 12-char short id (Docker's shapes) · the run id `yyyyMMddTHHmmssZ-<pid>` (the id format) ·
`/proc/<pid>/stat` field indexes 3/7/14/15/22, `mountinfo` fields, `auxv` keys, `O_*`/`AT_*`/`statx`/errno and signal
numbers (kernel ABI) · byte and time unit conversions (1024, 2^30, 1e9, 100 ns ticks, ×24, ×10, ×1000) · Docker / journalctl
size-unit tables · `DockerEngine.MinimumMajorForA4` 23 (a fact about Docker's behaviour) · `fstrim`'s exit 64 (the tool's
contract) · the heartbeat 5 s and wedged-after 30 s (`RunningState.cs:103`, `:106`: a protocol between the root writer and
every reader — two configurations would make a live run look wedged) · `StartTolerance` 2 s (the kernel tick and boot-time
rounding) · `RealPath.MaxLinkHops` 40 (the kernel's ELOOP) · `TreeWalk.CancellationStride` (granularity) · the
`Coverage` 24 h count window (a wire meaning, spelt "last 24 h" in the contract) and moby's events buffer 256 (measured
Docker constant) · every key's own range (`ConfigKeys` ceilings — the range IS the contract) · the shape limits of
`aiAgents.extra` (16 / 8 / 1 024 / 128 / 64 — the schema of a value, in `config-keys.json`) · `ConfigLoader.MaxLayerBytes`
256 KiB (it bounds the parse of the very file a key would be read from) · `CommandRequest.MaxTimeout` 24 h (the hard maximum
every timeout key is checked against) · the regular-expression match timeouts 250 ms (a ReDoS guard on fixed product
patterns) · display truncations of text lines (`Take(5)`, `Take(3)`, 40 / 120 chars in refusals — presentation) · the
extension's message-shape bounds (`MAX_RUN_INDEX`, `MAX_STOP_INDEX`, `ROW_IDS`), the CSP nonce, `SUPPORTED_SCHEMA`,
`MIN_DAEMON_FOR_*`, UTF-16 detection.

**Behavioural, but proposed to stay FIXED (the owner decides; listed so nothing is hidden):** internal plumbing a person cannot
choose a right value for — `RunningReadRetry` 3 × 100 ms, `ProcessCommandRunner.DrainGrace` 2 s, the signal poll slice
200 ms, the Windows rename retry 2 s / 10 ms, the lock jitter 5–25 ms, `RequestSweep.FutureSkew` 5 min, `EarlyEnd` 2 s, the
backoff factor 2; the extension's `KILL_GRACE_MS` 2 s, `OUTPUT_LIMITS` 16 MiB / 1 MiB (a hit is a defect, not a tuning case),
the status-bar priority 50, and PR #12's retry / concurrency / coordination numbers (`PREVIEW_ROUNDS` 3, `READ_TRIES` 3,
`READ_BACKOFF_MS`, `SETTLE_AT_ONCE` 4, `TOMBSTONE_TTL_MS` 10 min, `FOLLOW.boundMs` 60 s, `graceMs` 90 s — which must stay above
the daemon's request grace) and its journal budget 32 (a mirror of `requests.maxQueued`, derived from it). Copies of the
systemd unit (`CollectRun.DefaultWindow` 4 h, `DoctorRun.LastRunMaxAge` 5 h, the "90 s" stop text) are not numbers of their
own: they are DERIVED from one timer-period constant. `A17 cargo sweep --time 30` is not in the code (cargo sweep is never
run, `ToolCacheTrims.cs:36`) — nothing to make configurable.

##### E7.S2c — the story

- **Keys:** every A row a key in `ConfigKeys` + `default.json` (today's value as the default — behaviour unchanged), every B
  row a `MachineOnly` key with `KeyTrust` carrying a HARD maximum (and a `RaiseOnly` flag for the inverted three); the contract
  generated (`ContractFilesTests`); coupled limits DERIVED, not keyed twice (request cap from `act.maxShownNames`; `logs.maxRangeDays`
  ≥ `runs.historyRetentionDays`; `requests.maxQueued` ≤ `requests.maxRead`) and checked at load (a violation = a `ConfigError`
  naming both keys).
- **Call sites:** every literal replaced by a read of the effective configuration; the command templates take their ceilings
  from the config the policy is built with (the policy remains static in its SHAPE — the slot-bounds rule of §15q R1.3 applies
  to every number slot a key fills).
- **The structural test** `No_behavioural_number_is_a_literal`: scans `src_daemon/src` (and `src_vs_code/src`) for
  `TimeSpan.From*` and numeric literals in the patterns the inventory used, outside the defaults and an explicit
  allowlist that IS group C (each entry with its reason), with a planted companion that must be found.
- **Every sentence** that spells a number formats the effective value (N-6).
- **Extension:** the settings above (`application` scope, ranges in `package.json`, the code clamps — VS Code does not
  enforce `minimum`/`maximum` on read); the ceiling test (N-1–N-3) computes the daemon's worst case from the C# constants.
- **RED first:** N-1 / N-2 / N-3 (a ceiling below the computed worst case), a planted literal found by the structural test,
  a B key's raise above its hard maximum refused, a user-layer value of a B key ignored with a notice.
- **DoD:** the inventory table re-verified at build time (a row whose `file:line` moved is updated, not dropped); every A and
  B row a key or a setting; every C row in the allowlist with its reason; docs and the contract; RED-GREEN-RED per behaviour.

#### E7.S2c as built (2026-10-05)

Built on `feat/wc-e7-agents-settings`; the record is `research/module_tests.md` § *Every number is configuration (E7.S2c)*.
**126 number keys** (`Config/ConfigKeys.Numbers.cs`, generated with their defaults into `default.json`): the 35 A rows,
the B rows as 91 machine-layer-only keys (the inventory's 77, split where one row held two numbers, plus `agentCpu.*` from
E7.S2b and `requests.maxBytes`). **Deviations from the text above:**

- **How a number reaches its call site: `Tuning`, an ambient configuration** (`Config/Tuning.cs`). The numbers bound static
  command templates, the policy's catalogue and the collectors' constants — places no configuration object reaches. One
  process, one configuration: `Program.Main` sets it once after the load (`Tuning.ForThisProcess`), `Program.Run` scopes the
  verb to the loaded configuration (an `AsyncLocal`, so a test's host is honoured and parallel tests never meet), and every
  read before the load sees the embedded defaults (today's values). A named constant became a property that reads its key
  (`RunningState.HeartbeatPeriod => Tuning.Current.Seconds(ConfigKeys.Running.HeartbeatSeconds)`), so its callers did not change.
- **Command templates keep their identity, their limits are read late.** `ActionCommands` matches a template by
  `ReferenceEquals`, so templates stay static singletons; their limits are a closed `CommandLimits` — `Keyed(timeout, cap)`
  read when a request is built, `Of(Func<ToolCommand>)` for a fixed read template that follows its command, `Fixed` for a
  test's own. A test reads the templates under the defaults FIRST, so a template that froze its limits at declaration fails.
- **The configuration's own patterns are bounded by the key's MAXIMUM** (`KeyRules`, 1 000 ms): they run while the
  configuration is being loaded, before any configured value exists — the bootstrap rule the machine layer's own read
  already follows (`config.maxLayerBytes`' maximum). Every other match (the extra agents' names and globs, a catalogue
  version pattern) uses `patterns.matchTimeoutMilliseconds`; the extra-agent `GeneratedRegex` pair became runtime matches for that.
- **Coupled rules, checked at load** (`Config/NumberRules.cs`, a violation refuses the layer naming the rule): wedged ≥ 3 ×
  heartbeat; `requests.maxRead` ≥ `maxQueued`; `act.maxListBytes` ≥ 67 × `act.maxShownNames`; `logs.maxRangeDays` ≥
  `runs.historyRetentionDays`; `systemd.unitStopTimeoutSeconds` ≥ `units.stopTimeoutSeconds` + 30; jitter max > min;
  `events.retryMaxSeconds` ≥ `retryFirstSeconds`; and, added here, **`timer.periodHours` divides 24** — the timer's
  calendar (`00/<h>`) restarts at midnight, so 5 or 7 would leave one short interval a day.
- **`CommandRequest.MaxTimeout` is `commands.maxTimeoutHours`**, and the test holds every timeout key's range maximum under
  the key's MINIMUM (1 h): no machine value can push a command's timeout past what a request accepts.
- **The request skew is seconds** — `requests.futureSkewSeconds` (60–3600, default 300), not the inventory's minutes, so it
  is published as the extension reads it (coordinator 2026-10-05).
- **The timer period is ONE key, and the units follow it through drop-ins.** `Systemd/UnitDropIns.cs` renders one drop-in per
  unit (`<unit>.d/50-wsl-care-config.conf`): the timer's `OnCalendar` (cleared, then `*-*-* 00/<timer.periodHours>:00:00`),
  `RandomizedDelaySec`, `AccuracySec`; the services' `Nice`, `MemoryMax`, `TimeoutStopSec`; the follower's `RestartSec`.
  `install.sh` writes them from the INSTALLED binary (`wsl-care units dropin <unit>`, a new read-only verb — the script never
  parses the configuration), on every install and upgrade, and removes them on uninstall. The shipped unit files keep
  today's values, which is what the drop-in of the defaults says (a test). `doctor` gained `unitConfig`: an installed drop-in
  that no longer says what the machine layer says is a `problem` naming what is wanted and "run install.sh again"; none at
  all is fine while the configuration keeps the defaults. Every derived copy of the period reads the key
  (`CollectRun.DefaultWindow`, `DoctorRun.LastRunMaxAge` = period + `timer.lateSlackMinutes`, the doctor sentence).
- **N-4 (REVERSED by the E7.S2b/S2c review, C-H2 — see that round): `wsl-care.service` `TimeoutStartSec=infinity`**, like `wsl-care-act@.service`; the test: every `oneshot` unit's start
  limit is `infinity` (its start IS the whole run, bounded by each command's own ceiling), a `simple` unit sets none.
- **N-5: root never reads a whole file it did not bound.** `IFileSystem.ReadFile(path, maxBytes)` (the physical one reads at
  most one byte past the cap; a test double checks after); `Files/RootFileCaps.cs`: a state file (`running.json`, the
  dry-run stamp, the clock state, `volume-seen.json`, the events summary, a drop-in) at `records.maxStateFileBytes` (1 MiB),
  a growing file (the history, a run's detail, the event lines) at `records.maxHistoryBytes` (256 MiB) — which also bounds
  every other one-argument read. Past the cap = unreadable, the caller's existing conservative edge (the dry-run week
  restarts, the history read names the problem).
- **N-6: one definition per number.** The sentences that spelt a default now format the value in force: the threshold
  limits (`warn > 80 %`, `warn < 32 free order-7 blocks`, the journal's days, the clock jumps, the drift's "5 minutes", the
  collectors' "30 minutes"), the dry-run week, A11's / A18's "SIGKILL after 10 s", A14's "newest 2", A9's "200 MiB", the
  stop marker's "90 s", the retention's "90-day", the doctor's "every 4 h", the clock fix's "at most one per hour" (now "per
  60 minutes"). A test changes the keys and reads the sentences.
- **N-1, N-2, N-3 are the extension's, and live on PR #12.** That branch already holds every extension number as a setting
  (`settings/numbers.ts`) with each ceiling's minimum above the daemon's derived worst case (`client/worstCases.ts`,
  `ceilings.test.ts`) — `timeouts.doctorSeconds` defaults to 120 s over a 109 s worst case (N-1). This branch does not edit
  the extension's `verbs.ts` a second time (it would conflict with PR #12's rewrite of the same lines); its part is the
  daemon side of the mirror — `status --json` `limits`, below.
- **`status --json` publishes `limits`** (coordinator 2026-10-05, additive, schema version 1): `historyRetentionDays`,
  `requestFutureSkewSeconds` — the two PR #12's `shared/daemonLimits.ts` reads — and every other daemon value a host decision
  rests on whose machine range reaches ABOVE its default (a copy of the default would then be too small): `requestGraceSeconds`
  (the host waits past it), `maxShownNames` (the shown-list cap the host validates against), `unitStopSeconds` (`act --stop`'s
  worst case), `drainGraceMilliseconds` (what a killed command adds, twice, to every worst case). A ceiling whose range maximum
  IS its default (every Docker and `systemctl show` ceiling) can only be lowered, so the extension's copy stays a safe upper
  bound and is not published. `Status/StatusLimits.cs` names the fields once; `contracts/status-limits.json` is generated from
  it (name, key, unit, range, default) and a test holds the writer's JSON names equal to the contract's — the file the
  extension's reader test reads. A daemon older than E7.S2c sends no `limits`: the reader takes the contract's defaults.
- **The structural test** (`ArchitectureTests.Numbers.cs`) scans `src_daemon/src` for five shapes — a numeric `const` /
  `static readonly`, an inline `TimeSpan.From*(<digit>)`, a `Take(n ≥ 2)`, a byte product (`n * 1024 …`, `1L << n`), and a
  literal handed to a wait, a jitter or a capped read (`Sleep(10)`, `Next(5, 25)`, `ReadStateFile(path, 4096)`) — and fails on
  every hit outside the group-C allowlist of 114 entries, each with its reason (units, kernel ABI, display truncations, argv
  heuristics, the extra-agent schema, Docker's and moby's own constants, the report's 24 h definition). Its companion plants
  one literal of each shape; a third test fails on a listed entry that no longer exists. The extension's numbers are PR #12's
  (`numbers.test.ts` holds `package.json` equal to its table).
- **Not built here, recorded:** the extension's reader test against `contracts/status-limits.json`, and the extension's use
  of `drainGraceMilliseconds` / `unitStopSeconds` in its worst cases — PR #12's side of the boundary row below.

#### E7.S2b/S2c review round (2026-10-06) — two own reviews (security, A18; correctness, S2c)

Every finding accepted (the coordinator, 2026-10-06), fixed in ONE commit `fix(daemon): E7.S2b/S2c review round …`; RED-GREEN-RED
per finding — the record is `research/module_tests.md` § *The E7.S2b/S2c review round*.

| # | Finding | Fixed by |
|---|---|---|
| A-H1 | A18's button run was not bound to what its modal showed: a process that became eligible after the modal was ended too, and a multi-id request could carry A18 with no A18 modal | A18 is `IBoundToShownList` like A4: the preview answers `pid:start` keys (`shown`), `act … --process <pid:start>` passes them back (A18 only, each shape-checked, at most `act.maxShownNames`), the request file carries `shownProcesses` (validated), a MANUAL run without them is refused, a run with them ends only the processes BOTH still eligible now and among the keys — the live re-judge kept |
| A-M1 | CPU measured per process, not per tree: an idle wrapper whose child agent works became eligible (and SIGTERM reached the child) | a candidate with any child process is kept: "it has a child process" |
| A-M2 | "orphaned" counted processes the user's `systemd --user` started | for A18, orphaned = parent pid 1 only |
| A-M3 | agents recognised by basename alone | the program (`/proc/<pid>/exe`), or the script node runs, must lead along its links into the agent's own install — a native `…/versions/…` target or the agent's npm package (`AgentDiscovery.IsInstallOf`); else "not that agent" |
| A-M4 | both idle windows used the wall clock (a forward jump after a host sleep passed both at once) | every sample carries the monotonic clock too; the idle time is the SHORTER of the two clocks, and counts only over a dense chain — no gap between sightings, nor between the newest and now, longer than two timer periods |
| A-M5 | zero session matches read as "no live session"; a process with its own agent home was judged by the default one | none found = kept ("cannot tell"); `/proc/<pid>/environ` read as root, and a moved `HOME`, XDG folder or agent home variable (`CLAUDE_CONFIG_DIR`, `CODEX_HOME`, `GEMINI_…`: any variable named after a catalogue binary holding a path outside the agent's own folders) keeps the process |
| A-L1 | `agent-cpu.json` 0644, and past the cap the HIGHEST pids were dropped | written 0600 (`IFileSystem.WritePrivateFileAtomically`); past the cap the OLDEST processes are dropped (= no history = kept); the cap 256 KiB, a full 512-entry history with both clocks is 157 KiB (a coupled rule holds `agentCpu.maxBytes` ≥ 320 × `agentCpu.maxEntries`) |
| A-L2 | the kill-time re-check compared "not root" only | the item key carries the account (`pid:start:cpu:uid`); another account now = not signalled (A11 too, it shares the path) |
| A-L3 | discovery bounded per agent; the PATH entries' device stats outside any bound | ONE `Bounded.Run` around the folder choice and every lookup |
| gap | a tool's cache answer was checked against the agent folders only | also every `~/git` and Claude's temp folder |
| C-H1 | a history past `records.maxHistoryBytes` read as "no runs": the reconcile wrote a false `interrupted` line per detail, more every run; the request sweep likewise | an unreadable history (`HistoryRead.Problem`) skips the reconcile and the whole request sweep, said in the run's notes; the key's minimum is 64 MiB, and a coupled rule holds it ≥ 128 KiB × `runs.historyRetentionDays` |
| C-H2 | **reverses N-4.** `TimeoutStartSec=infinity` removed the last backstop: the heartbeat beats on a timer, so a hung read kept a run "live" forever, and `act --stop` refuses a live run | (a) wsl-care.service `TimeoutStartSec` = `timer.runLimitMinutes` (new machine key, default 240; the drop-in renders it; a coupled rule holds it ≥ the derived worst case of a timer run — every command template once at its ceiling with its drains, the two walks, 10 min: 220 min with the defaults); (b) a progress watchdog — `running.json` carries the time of the last step (a command started or ended, a stretch of a walk, an action begun), and a run with none for `running.noProgressMinutes` (new machine key, default 20; a coupled rule holds it ≥ the longest single command ceiling with its drains and 60 s: 17 min) reads WEDGED with a fresh heartbeat, so `act --stop` ends it. The act unit keeps `infinity` and gains (b) |
| C-M1 | a user value in range could put ROOT observe-only through a coupled rule, the error blamed on the wrong layer | the rules are held per LAYER: the keys a layer set for a broken rule go back to the layer below — from the machine layer an error, from the user layer a NOTICE (root stays able); `runs.historyRetentionDays` clamped to `logs.maxRangeDays`' maximum (366); `config set` evaluates the rules on the configuration it would produce and refuses a breaking value |
| C-M2 | a broken rule left the bad value in force (`units dropin` rendered `00/5:00:00`) | the take-back above; `units dropin` refuses (78) while a layer is in error |
| C-M3 | the event stream's ceiling (segment + slack) could pass `commands.maxTimeoutHours`, so the follower restarted every 30 s | `events.segmentMinutes` at most 50; a coupled rule (segment + slack + 60 s ≤ the maximum); the timeout test covers COMPOSED ceilings |
| C-M4 | the writer kept the key's range maximum as the user-layer cap, the reader the value in force | ONE cap, `ConfigLoader.UserLayerCap`; the refusal's fix text prints the cap in force |
| C-M5 | `events.startsRetentionDays` user-layer up to 3650, while root re-reads every kept day file every ~10 min; no `MemoryMax` on the follower | machine-only, at most 90; the summary reads only the day files of the last 24 h; the follower gets `MemoryMax` (`units.memoryMaxMb`, rendered too) |
| C-M6 | missing coupled rules | `requests.maxBytes` ≥ 67 × `act.maxShownNames` + 4096; `timer.lateSlackMinutes` > `randomizedDelayMinutes` + `accuracyMinutes`; `agentCpu.maxBytes` ≥ 320 × `agentCpu.maxEntries` |
| C-M7 | `RewriteLines` read `history.jsonl` whole | read under `RootFileCaps.History`; past it the rewrite is refused and the file kept |
| C-M8 | `install.sh --version <older>` failed at the drop-ins after replacing the binary and units | a `units dropin` that answers 2 (an older release: an unknown verb) or 78 (a configuration in error) gets no drop-in, said, the stale one removed; anything else still fails the step |
| C-M9 | the number scan missed comparisons, clips, `new TimeSpan(…)`, one-digit waits and properties that answer a literal | five more shapes, each planted in the companion; the 21 numbers they found are formats (group C, each with its reason) |

**Deviations from the review's text:**

- `timer.runLimitMinutes` defaults to **240, not 60**: the derived worst case of a timer run with the defaults is 220 min, so 60 would
  contradict the coupled rule the same finding asked for. The watchdog (20 min without a step) is what ends a hang early.
- The watchdog's key is `running.noProgressMinutes` (the `running.*` group), not `run.noProgressMinutes`.
- The derived worst case counts each command TEMPLATE once — a removal of many batches or one trim per tool takes more; the
  limit is a backstop against a hang, not a plan of a busy run, and says so in its rule.
- A-M5's "agent home variable" is a NAME rule (prefixed by a catalogue binary, holding a path), not a list per agent: the
  catalogue names no variables, and the rule keeps a process — the safe direction — for any it does not know.
- A-L1 keeps "drop the oldest" (the planned rule) rather than recording the old order as a deviation: a dropped identity is "no
  history", which only keeps a process.

#### coai E7 code round (2026-10-06) — the daemon half

The coai code round on the E7 daemon half PASSED (verdict proceed; all four qwen reviewers answered; security: nothing blocking or
major). Eight findings, all accepted, fixed in `fix(daemon): coai E7 code round …`:

| # | Finding | Fixed by | Behaviour |
|---|---|---|---|
| 1 | Major: `CommandLine.SplitActOptions` cyclomatic complexity 5 | the per-option dispatch extracted (`TakeOption`, `WithValue`) | none — a refactor; the act parsing tests unchanged and green |
| 2 | Major: `AgentsCommand` read `AgentSizesSource.Reason` with `??` | `AgentSizesSource.View()` → a closed `SizesView` (`MeasuredNow` / `FullRun(RunId, Age)` / `Unavailable(Reason)`); the nullable members stay only at the JSON edge | none — a refactor; the text-form tests green |
| 3 | Minor: `Sessions.Count!.Value` | `AgentSessionsReport.View()` → `SessionCount` (`Counted(Count)` / `NotCounted(Reason)`) | none — a refactor |
| 4 | Minor: `MaxShownVolumes` a property the parser cannot honour | a `const` again: the parser's compile-time ceiling, `act.maxShownNames`' range maximum (a test holds them equal); the VERB refuses a list past the value in force | yes: a machine layer that lowers `act.maxShownNames` now refuses a longer list (RED with it dropped) |
| 5 | Minor: `CliHost.AgentExtrasDropped` | `CliHost.WithAgentExtras(ConfigLoadResult)` returns the host AND the load with the dropped folders as structured notices (key `aiAgents.extra`, the user layer) — no host state | the same notices; held by the phase-two tests (3 red with them dropped) |
| 6 | Minor: an empty `units dropin` answer installed silently | `install.sh` treats an empty answer as no drop-in, warns, removes a stale one | yes (RED with the check dropped, WSL) |
| 7 | Major (UX): `agents list --measure` silent for up to its budget | one stderr line before the walk ("measuring N agent folder(s), up to <budget> s…") and one per folder as it starts (`AgentWalk.OnFolder`); stdout untouched | yes (RED for each line dropped) |
| 8 | Minor (UX): the ~250-character interop explanation repeated per key | ONE notice without a key carries it; each key's notice says the short fact; goldens regenerated (`status.json`, `doctor.json`) | yes (RED with the per-key text back) |

#### E7.S2d — MCP server instances of the AI agents (owner request 2026-10-06)

> Status: **built 2026-10-06** (deviations in *E7.S2d as built* below; the coai code round and the PR still open). Originally: plan only, 2026-10-06. Scope: a read-only daemon collector and a `status --json`
> metric (also in every full run's detail), three threshold verdicts, thirteen configuration keys (the thirteenth, `mcpServers.maxStartsListed`, added 2026-10-07). Branch `feat/wc-e7-mcp-instances`.
> The extension shows it later (not in this story). Nothing is ever stopped or killed by it.

**The ask (owner, 2026-10-06, verbatim in translation):** "a separate metric for coai: how many MCP instances run, which hang
idle, how much they eat".

**The symptom, measured 2026-10-06 in WSL Ubuntu (24 cores).** Seven `coai-mcp` processes ran — one stdio MCP server per
Claude Code session. Each one's program is `~/.vscode-server/data/User/globalStorage/remsoftdev.connect-other-ais/coai-mcp`,
its parent the `claude` native binary of the Claude Code extension
(`~/.vscode-server/extensions/anthropic.claude-code-<v>-linux-x64/resources/native-binary/claude`), the grandparent the VS Code
server's `node`. Each burned 27–54 % of a core CONTINUOUSLY while its log had no line for 10+ minutes (≈ 2.6 cores together).
After the extension updated `coai-mcp` to 0.43.0, a start took 20–32 s at 100 % of a core; Claude Code's 30 s MCP connect
timeout SIGTERMed it and started it again — **34 starts in 10 minutes**. The logs follow the family's logging contract:
`~/.local/share/coai-mcp/logs/<UTC day>/coai-mcp-<HH-mm-ss UTC>-<pid>.log` (lines "starting", "consultants: wrote …",
"SIGTERM asked this server to stop"; 110 files in that day's folder by 19:18 local time). Nothing in wsl-care shows any of it
today: the process table (`Collectors/ProcessCollector.cs:86`) files these processes under the family `vscode-server`
(`Collectors/ProcessFamilies.cs:40`, the program path holds `/.vscode-server/`), with no CPU rate, no owner agent and no
restart count.

**Goal.** A metric any reader of `status --json` can act on: how many MCP servers of AI agents run, which ones are idle, which
burn CPU with no activity, how much CPU and memory they hold together, and how often each server was STARTED lately — the
restart storm is the signal the owner actually hit.

##### Decided

1. **What an instance is (generic, not coai-only).** A process of the snapshot (`ProcessSnapshot.All`, the ONE `/proc`
   walk — no second walker) whose PROGRAM matches an entry of the MCP server catalogue: the file names of its first two argv
   words, `.exe` stripped — the attribution `AgentOrphans.AgentOf` already uses (`Actions/Suspects/AgentOrphans.cs:243`),
   EXTRACTED into the agents module (`Agents/AgentProcesses.cs`, new: `ProgramNames`, `AgentOf`) so A18 and this collector
   call one function. Its OWNER is found by walking `ParentPid` through the same snapshot (a visited set ends any loop) to the
   first process whose family is `ai-agents` AND that `AgentOf` attributes to exactly one catalogue agent: that is the
   owning agent session (its pid, the catalogue agent's name, its shown — redacted, cut — command line). **Deviation from the
   ask's wording, a decision:** a chain that reaches pid 1 with no agent on it is KEPT as an instance, `orphaned: true`,
   agent none — an MCP server whose agent died is exactly the leak this metric exists to show. A chain that reaches a live
   process that is not an agent (some other host running the same server) is NOT an instance; such processes are only
   counted (`notUnderAgent`).
2. **The catalogue** (`McpServers/McpServerCatalogue.cs`, new, embedded in code — a closed list of records): per server its
   name, its program names and an optional LOG LAYOUT, a closed hierarchy: `None`, or `FamilyRunLogs(root under the home,
   file prefix)` — the family logging contract `{root}/{yyyy-MM-dd}/{prefix}-{HH-mm-ss}-{pid}.log`, UTC, with a run that
   outlives the day continuing in a `00-00-00` segment of the same pid. First and only entry: `coai-mcp` (programs `coai-mcp`;
   logs `.local/share/coai-mcp/logs`, prefix `coai-mcp`). A server with another layout later adds its own case — one module
   per layout behind the one record, never an `if` on a server's name.
3. **Which servers are watched is a configuration key**, `mcpServers.watched`, a list CLOSED over the catalogue's names
   (`ConfigKey.TextListKey`, as every list key since E7.S0 B1 — free text is allowed for no key a run reads, `ConfigKeyShapeTests`),
   default every catalogue name. A server outside the catalogue needs a catalogue entry (code); an open list of names would be a
   new key shape — an owner question (Q-M2), not built.
4. **Per instance:** pid, server, user, state (`/proc/<pid>/status` `State`), age, CPU % over the window, held memory
   (`RssAnon + RssShmem`, the product's one memory measure — never `VmRSS`, §15b #4), owner agent (pid, name, command line) or
   `orphaned`, last activity, and a KIND.
5. **CPU % is MEASURED over a window, never derived from the lifetime average.** Each instance's `/proc/<pid>/stat` is read
   (`SuspectTermination.Sample`, `Actions/Suspects/SuspectTermination.cs:143` — reused as is: pid, start ticks, CPU ticks), the
   collector waits `mcpServers.cpuWindowMilliseconds`, and reads it again. CPU % = 100 × (Δticks ÷ clock ticks per second —
   the kernel's, from `auxv`, as the process table reads it) ÷ the window in SECONDS (milliseconds ÷ 1000), the window being
   the LONGER of the configured window and the measured elapsed time, so a late wake-up never inflates the rate (50 ticks at
   100 ticks/s over 1 000 ms = 100 × 0.5 ÷ 1.0 = 50 % — plan round finding 0). A different start (a reused pid) or a process gone at the second
   read = CPU unavailable with the reason ("it exited during the window"), never 0. The wait happens only when at least one
   instance exists, so a machine without MCP servers pays nothing; the window is bounded (machine-only key, hard maximum
   5 s — inside the extension's 20 s `status` ceiling with room).
6. **The kind of an instance** — a closed set, one rule each, evaluated in this order:
   `unknown` (CPU unavailable) · `starting` (CPU below `mcpServers.idleCpuPercent` and younger than
   `mcpServers.idleMinAgeMinutes`) · `idle` (CPU below the threshold AND age at or above the minimum) · `busyWithoutActivity`
   (CPU at or above the threshold AND its last activity is KNOWN and older than `mcpServers.activityWindowMinutes`) · `busy`
   (CPU at or above the threshold otherwise — recent activity, or activity not derivable). "Busy without activity" is the
   measured 2026-10-06 state and is reported separately from idle, as the owner's brief asks.
7. **Activity, where it is derivable: the newest log file of THAT pid.** For a server with `FamilyRunLogs`, the collector lists
   the log root's day folders for today and yesterday (UTC) — names and stat only, through `SessionGlob.Find`
   (`Agents/SessionGlob.cs:39`, the listing A18 uses: each folder listed once, no link followed, no file opened, the device
   held, an entry cap and a deadline), after `AgentWalk.PlaceProblem` (`Agents/AgentWalk.cs:69`) accepts the root (its real
   path under the real home, the home's device). A file whose name time is earlier than the process's start (beyond the
   start tolerance of `RunningState.StartTolerance`) belongs to an earlier process with the same pid and is ignored. The
   instance's last activity is the newest last-write of its files; none found = activity unknown ("no log file of this pid in
   <root>"), so the instance can be `busy`, never `busyWithoutActivity`. **Not derivable generically, said:** a server with no
   log layout has activity "not derivable: <server> has no log layout in the catalogue". Another account's instance is never
   matched to this home's logs (its pid has no file there).
8. **Restart churn per server — starts in the last `mcpServers.startsWindowMinutes`.** With `FamilyRunLogs`: the log files
   whose NAME instant (day folder + `HH-mm-ss`, UTC) lies in the window, a `00-00-00` continuation excepted. Plan round
   finding 1 (a reused pid must not hide a start): when the pid runs NOW, its `00-00-00` file is a continuation only if
   that process started before the midnight the name marks (its start from the snapshot's age, within
   `RunningState.StartTolerance`) — otherwise it is a start; when the pid no longer runs, it is a continuation if the previous
   day's folder holds a file of the same pid. *Residual, stated:* an exited server that started at exactly 00:00:00 on a pid
   an earlier run of the previous day also used is read as a continuation — one start missed, never one invented. A listing cut short makes the count unavailable with the
   reason, never partial-as-whole. **Generic fallback, and why it is weaker:** the live instances younger than the window — a
   LOWER bound, marked `basis: "liveYounger"`; a process killed within the window is not seen. The ask's "distinct pids seen
   across runs" is NOT built: `status` is unprivileged and writes no state (§15b #3), and the root timer runs every 4 h — far
   too rarely to see a 10-minute storm. Said in the field.
9. **Totals:** instances, idle, busy without activity, CPU in cores (Σ CPU % ÷ 100, over the instances whose CPU was read —
   a total over fewer instances says how many), held bytes Σ, and per server: count and starts.
10. **Where it runs.** `McpServers/McpServerCollector.cs` (new) takes the snapshot the probe already read, the layout, the
    file system, the clock and a wait seam. **Not inside `LinuxProbe.Sample`**: `ActionEngine` takes the probe's sample
    several times per run (`Actions/Engine/ActionEngine.cs:162`, `:219`, `:555`), and each would pay the window. Called by
    `status` (`Cli/Commands/StatusCommand.cs:23`, after the probe) and by `collect` (`Collect/CollectRun.cs:346`, after the
    probe — root: the paths follow the TARGET user's home, as every per-user read of a full run does). The Windows binary
    answers the block unavailable: "the Windows binary has no process collector yet (E11)".
11. **The wire shape** — `status --json` gains an additive `mcpServers` block (absent from every older daemon; `available`
    + values or `available: false` + `reason`, figures unread never 0, §15b #7): `windowMilliseconds`, `count`, `idleCount`,
    `busyWithoutActivityCount`, `notUnderAgent`, `cpuCores` (number figure), `heldBytes`, `servers[]` (`name`, `count`,
    `starts`: figure + `windowMinutes` + `basis`), `instances[]` (largest CPU first, at most `mcpServers.maxInstances`, then
    `listed`/`count` say so). The run detail embeds the same block through its `sample` (`RunDetail.Sample`). Capability
    `status.mcpServers`. The text form of `status` prints one line ("mcp servers: 7 (0 idle, 7 busy without activity),
    2.60 cores, 0.4 GiB; starts coai-mcp 34 in 10 min").
12. **Three verdicts** in the threshold evaluator (`Thresholds/ThresholdRules.cs`, a new `FromMcp`, the existing `Above`
    shape and wording), evaluated over the collector's sample: in `status` NOW (source `sample`) and in every full run's
    `thresholds`. `mcp.instances` — warn > `mcpServers.warnInstances` ("MCP server processes of AI agents, one per agent
    session"); `mcp.cpu` — warn > `mcpServers.warnCpuPercent` % of one core in total ("CPU the agents' MCP servers burn
    together; N of them busy without activity"); `mcp.starts` — warn when any server's starts in the window exceed
    `mcpServers.warnStarts` ("<server> started N times in M min — a restart storm: the agent's MCP connect timeout kills a
    slow start and starts it again"); unknown with the reason when not derivable.
13. **Read-only, by construction.** No signal, no file opened under the log root, nothing written; the collector holds no
    command runner and no signal sender. A "stop the idle / busy-without-activity MCP servers" action is NOT in scope —
    owner question Q-M1.

##### Keys (every number and list configurable — the owner's rule of 2026-10-05)

| Key | Group | Range | Default | Trust | Why this default |
|---|---|---|---|---|---|
| `mcpServers.watched` | list, closed over the catalogue | catalogue names | `["coai-mcp"]` | display | every catalogued server |
| `mcpServers.cpuWindowMilliseconds` | B (root waits on it) | 200–5000 | 1000 | lower, machine-only | at 100 ticks/s one tick is 1 % of a core over 1 s — the resolution the 2 % idle line needs; costs `status` 1 s only when an instance exists |
| `mcpServers.idleCpuPercent` | A | 0–100 | 2 | display | a server waiting on stdin uses no tick at all; 2 % (2 ticks a second) is above a timer or GC blip and far below the measured burn of 27–54 % |
| `mcpServers.idleMinAgeMinutes` | A | 0–1440 | 10 | display | a server that just started and had no request yet is not hanging; 10 min is the gap the owner saw before calling it a hang |
| `mcpServers.activityWindowMinutes` | A | 1–1440 | 10 | display | the measured "no log line for 10+ minutes" |
| `mcpServers.startsWindowMinutes` | A | 1–1440 | 10 | display | the measured storm was counted over 10 min; ≤ 1 day so today's and yesterday's folders always cover it |
| `mcpServers.warnInstances` | A | 0–10000 | 12 | display | 7 measured in a normal working day (one per session); 12 is ~1.7× that |
| `mcpServers.warnCpuPercent` | A | 1–100000 (% of one core) | 100 | display | healthy servers idle at ~0; one whole core for servers serving nothing is a defect; the storm measured 260 % |
| `mcpServers.warnStarts` | A | 0–100000 | 10 | display | one start per session start; more than 10 in 10 min is not people opening sessions — 34 measured in the storm |
| `mcpServers.maxInstances` | B (bounds root's reads) | 1–1024 | 256 | lower, machine-only | the instances sampled and listed; ~36× the measured 7 |
| `mcpServers.maxLogEntries` | B | 100–100000 | 20000 | lower, machine-only | entries one log listing sees; a storm of 34 / 10 min is ~5 000 a day |
| `mcpServers.logListMilliseconds` | B | 100–5000 | 1000 | lower, machine-only | the listing's deadline; two folders of a few thousand names take milliseconds |

All in `ConfigKeys` (`McpServers` group) + `default.json` + `contracts/config-keys.json` (generated, `ContractFilesTests`); no
coupled rule is needed (`startsWindowMinutes` ≤ 1 day is the range itself). `SessionListing` gains an optional entry cap
(default `SessionGlob.MaxEntries`, today's behaviour) — widened, not copied. `ArchitectureTests.Numbers` stays green: no
new literal outside the group-C allowlist (the day-folder count 2 is the definition of "today and yesterday", group C with
its reason, or derived from the starts window's maximum).

##### Growth

| Surface | Projected size | Who retires it |
|---|---|---|
| the `mcpServers` block in each run detail | typical 7 instances × ~400 B ≈ 3 KB per full run; worst `maxInstances` 256 × ~400 B ≈ 100 KB | the run details' 90-day retention (6 runs/day ≈ 1.6 MB typical over 90 days) |
| `status --json` | the same block, never stored | — |

No state file, no history-line field: nothing new grows on disk except inside the run detail.

##### Windows

The Windows binary has no process collector (`Collectors/WindowsProbe.cs`: host RAM, the system drive and `vmmemWSL` through
`Process.GetProcessesByName` only — no parent pids, no CPU times). `coai-mcp.exe` under VS Code's `globalStorage`
(`remsoftdev.connect-other-ais`) is therefore **the next step, recorded for E11** (the Windows collectors,
[PLAN_windows_care.md](PLAN_windows_care.md)): the same catalogue, a Toolhelp snapshot for parents, `GetProcessTimes` twice
across the window; the block answers unavailable on Windows until then.

##### Test plan — RED first, fixtures of `/proc` with fake trees (no real account name: `FixtureIdentity`)

- `An_mcp_server_under_a_claude_session_is_an_instance_with_its_owner_agent` (sandbox `/proc`: `node` → `claude` → `coai-mcp`).
- `An_mcp_server_whose_agent_died_is_an_orphaned_instance` (parent pid 1) and `An_mcp_server_under_another_host_is_not_an_instance`.
- `Cpu_percent_is_measured_across_the_window_from_two_stat_reads` (the wait seam rewrites `stat` ticks: 50 ticks over 1 s = 50 %).
- `A_pid_reused_or_gone_during_the_window_has_cpu_unavailable_never_zero`.
- `Each_kind_at_its_edge` (idle / starting / busy / busyWithoutActivity / unknown, the thresholds read from the keys).
- `Busy_without_activity_needs_a_known_log_older_than_the_window` (a log of this pid written 15 min ago; one written now = busy;
  none = busy with activity unknown).
- `A_log_of_an_earlier_process_with_the_same_pid_is_not_its_activity`.
- `The_restart_storm_counts_34_starts_in_10_minutes` (34 fixture log files in today's folder, 3 older; a `00-00-00`
  continuation not counted; a start just before midnight counted from yesterday's folder), and
  `A_midnight_file_of_a_live_process_started_after_midnight_is_a_start` (a reused pid with a file yesterday).
- `Starts_without_a_log_layout_are_a_lower_bound_marked_liveYounger`.
- `A_linked_or_foreign_device_log_root_is_not_listed` (PlaceProblem) and `A_cut_listing_makes_starts_unavailable_not_partial`.
- `No_wait_when_no_instance_runs` (the seam is never called).
- `The_three_verdicts_warn_above_their_keys` and `An_unwatched_server_is_not_counted` (`mcpServers.watched`).
- Status: the golden `status.json` regenerated (the block and the three verdicts), the text line, capability; the Windows
  binary's block unavailable with its reason; collect: the run detail carries the block and the verdicts.
- `AgentOf` extraction: A18's tests unchanged and green (a refactor), plus a direct test of `AgentProcesses.AgentOf`.
- Scenario (`WslCare.Scenarios`, built binary, sandbox): `status --json` over a fixture tree with a fake `coai-mcp` under a fake
  `claude` and a storm of log files → the block and `mcp.starts` warn; `research/module_tests.md` names the flow.
- Teeth, each: remove the window's second read, the parent walk, the pid-start guard, the continuation exception, the
  `PlaceProblem` check — and watch the named test go red.

##### Definition of Done

- [ ] The RED tests above seen red for the real symptom, then green, each guard broken and seen red again.
- [ ] One `/proc` walk: the collector reads only the snapshot plus two `stat` reads per instance; `AgentOf` has ONE home.
- [ ] Every number and the server list are keys with ranges and defaults; `ArchitectureTests.Numbers`, `ContractFilesTests`
      and the config-shape tests green.
- [ ] `status --json` `mcpServers` + capability + three verdicts; goldens regenerated and read; the run detail carries it.
- [ ] Read-only: no signal, no file opened, nothing written (a recording file system asserts no open under the log root).
- [ ] Windows recorded as the E11 next step; docs: `research/architecture.md`, `research/module_tests.md`, README's status
      section; this subsection's deviations recorded when built.
- [ ] Daemon suites green on Windows and WSL (normal user, `nice -n 19`, one at a time); `dotnet format --verify-no-changes`.

##### Open questions for the owner

- **Q-M1 — a stop action.** Should wsl-care offer a button (never the timer) that SIGTERMs MCP servers that are
  `busyWithoutActivity` or `idle` and orphaned, by pid AND start as A11/A18 do? Not built; the agent restarts a killed server
  on its next tool call, so the value is unclear.
- **Q-M2 — open server names.** `mcpServers.watched` is closed over the catalogue (the E7.S0 B1 rule). Should a user be able
  to name ANY program as an MCP server (a new list-with-a-shape key kind, a contract change)?
- **Q-M3 — the orphan rule.** An MCP server whose agent died is counted (`orphaned: true`) — keep, or leave it out as the
  literal "parent chain reaches an agent" reads?
- **Q-M4 — the defaults** above (2 %, 10 min, 12 instances, 1 core, 10 starts / 10 min).
- **Q-M5 — the status budget** (added at build): with an MCP server running, `status` takes 2 s + the CPU window (1 s by default,
  machine-only, 200–5 000 ms). Keep, lower the default window (500 ms halves the resolution to 2 % per tick — at the idle
  line), or measure the CPU only in the full run (a 4-hour-old figure, which misses a 10-minute storm)?

##### Plan round (coai session `7f843e99`, 2026-10-06)

Verdict **proceed**, 1 of 1 reviewer answered (codex), 2 findings, both ACCEPTED and folded into the text above: **0** (Major)
the CPU formula gave a fraction, not a percent — the ×100 and the millisecond → second conversion are now explicit (Decided 5);
**1** (Minor) a reused pid could make a real start at midnight read as a continuation — the continuation rule now checks the
live process's start, with the remaining residual stated (Decided 8) and a test added.

##### Cadence consultation, epics 7–9 (coai `26b4a958`, codex, 2026-10-06) — verified, and these OVERRIDE the text above

- **C-1, verified true:** `SessionGlob`'s entry cap and deadline are checked only BETWEEN listings, and
  `PhysicalFileSystem.ListEntries` (`Files/PhysicalFileSystem.cs:167`) reads and sorts a whole folder first and answers `[]`
  for a folder it cannot read — so an unreadable day folder would read as "complete, zero starts". **Fixed by widening, not
  copying:** `IFileSystem` gains a bounded listing, `ListEntries(path, maxEntries)` → `EntryListing` (`Listed(entries,
  Complete)` | `Unreadable(reason)`; a missing folder is `Listed([], true)`), the physical one enumerating lazily and stopping
  one entry past the cap, the default member (every fake) answering from today's listing. `SessionGlob` lists through it, so
  a cut or unreadable folder makes its scan NOT complete — which also corrects A18 and the agents' session counts. Follow-up
  turn 2 (verified): the bounded listing takes the caller's deadline and cancellation too, checked at every entry, and the
  REMAINING allowance of the whole walk, not a fresh one per folder; it does not set `IgnoreInaccessible` (the physical
  listing's `true`, `PhysicalFileSystem.cs:186`, skips unreadable entries silently), so an error is `Unreadable`. A cooperative
  check cannot interrupt one blocked file-system call — it is a bound on work between calls, not a wall-time guarantee, and is
  described so. **A18, corrected:** "none found is kept" did not cover readable OLD sessions beside an unreadable folder —
  today that scan is complete and the process can become eligible (`AgentOrphans.cs:235`); with the fix any failed or cut
  folder makes the whole scan incomplete and A18 keeps the process ("cannot tell"). **The agents' counts:** `AgentWalk`
  chooses "not counted" by `Reached`, not `Complete` (`Agents/AgentWalk.cs:162`); a scan that reached its level but lost a
  folder shows its count as a LOWER bound (`complete: false` with the note), never a complete zero. RED tests:
  `An_unreadable_sibling_folder_keeps_the_agent_process_cannot_tell` (A18: one project folder with an old session, one
  unreadable), `An_unreadable_folder_makes_the_scan_incomplete_never_zero`, `The_listing_stops_at_its_cap_and_its_deadline`,
  `An_agents_count_with_an_unreadable_folder_is_a_lower_bound`.
- **C-2, verified true:** `ProcessEntry.CommandLine` is display text — redacted and cut to `processes.shownCommandChars`
  (`Collectors/ProcessCollector.cs:128`), so splitting it on spaces loses a program whose path holds a space or passes the
  cut. **Fixed:** the snapshot keeps the program names from the RAW argv (`ProcessEntry.Programs`, the file names of the first
  two words, `.exe` stripped — `Agents/AgentProcesses.ProgramNames`, one function), and `AgentOf` reads them. **The MCP match
  is narrower than `AgentOf`'s:** the PROGRAM (argv[0]) only, so `printf coai-mcp` is not a server; a catalogue entry may
  name scripts for a server an interpreter runs (none today). RED tests: `A_program_path_with_spaces_or_past_the_display_cut_is_still_recognised`,
  `A_server_name_as_an_argument_of_another_program_is_not_an_instance`.
- **C-3 (E9, `PLAN_ai_session_archive.md` §8a vs §8b — per-entry resume vs keep-every-file-of-a-changed-session):** not this
  story's; passed to the coordinator for E9, unverified here.
- **On the two doubts:** keep ONE live window in `status` (a 4-hour-old full-run figure misses a 10-minute storm) — and, my
  inference from `Status/StatusLimits.cs`' own rule (publish every value a host decision rests on whose machine range reaches
  above its default), `limits` publishes `mcpCpuWindowMilliseconds` and `mcpLogListMilliseconds`, so the extension's status
  ceiling can count them (`contracts/status-limits.json` regenerated). Reading another product's log NAMES and stats is within
  the E7 walk rules; the kind's sentence says "no log write in N min", never "no activity" as a fact.
- **Closed `solved`** (2026-10-06): C-1 and C-2 verified and fixed RED-GREEN-RED before the collector was built on them.

##### E7.S2d as built (2026-10-06)

Built on `feat/wc-e7-mcp-instances`; the record of every guarantee, its red and its teeth is `research/module_tests.md`
§ *MCP server instances of the AI agents (E7.S2d)*, the design `research/architecture.md` § *MCP server instances of the AI
agents*. **Deviations from the text above:**

- **Where it lives:** `Core/Mcp/` (`McpServerCatalogue`, `McpInstances`, `McpRunLogs`, `McpServerCollector` with `McpJudge` and
  the one road in `McpSampling`, `McpSample`), the verdicts in `Thresholds/McpVerdicts.cs` — not a `FromMcp` inside
  `ThresholdRules.cs`, which is already near 400 lines; the wire shape in `Status/McpServersReport.cs`.
- **The kind's sentence** reads "busy with no log write in the activity window" (consultation turn 1: an absent log write does
  not prove absent work); the JSON keeps `busyWithoutActivity` as the owner's brief named it.
- **The status budget (a decision, Q-M5 below):** the scenario's 2 s budget became 2 s PLUS the CPU window when an instance
  runs — the captured 2026-10-02 tree already holds two `claude` → `coai-mcp` sessions, so `status` over it now waits 1 s by
  design. The probe's own `sampleMilliseconds` is still held under 2 s. The other WSL timing failures of the first run (a
  status with no instance at 4.1 s, a Docker ceiling at 26 s against 25 s, four install flows) were load (~55 on 24 cores):
  re-run alone at load ~21, every one green.
- **Found in the fixture:** the captured tree of 2026-10-02 holds the measured shape (`claude` 7203 → `coai-mcp` 7329, `claude`
  8290 → `coai-mcp` 8380) — the CLI tests and the goldens run over real shapes, not only synthetic ones. It also holds
  `creds-mcp` (native, and the Windows `.exe` through `/init`) and `playwright-mcp` run by `npx` — catalogue candidates for
  Q-M2, not added (no log layout is known for them).
- **The orphan rule** (Decided 1) uses the product's own `ProcessEntry.Orphaned` (parent pid 1 or a `systemd --user`), not
  "pid 1" alone; a WSL session relay (`/init` with another pid) is a live non-agent parent, so such a server counts as
  `notUnderAgent` — residual, stated.
- **Residual of the continuation rule:** with `startsWindowMinutes` at its maximum (one day) just after midnight, a dead run's
  `00-00-00` file of YESTERDAY whose earlier file is two days old (not listed) reads as a start — the only way it can invent one.
- **Windows:** the block answers unavailable with "the Windows binary has no process collector yet (E11)"; a CLI test holds it.

##### E7.S2d code round (coai session `7f843e99`, 2026-10-06) and an own review

**coai:** verdict **proceed**, 4 of 4 reviewers answered (codex), 6 findings: 5 ACCEPTED, 1 rejected. **Own review** (one
Opus reviewer, read-only, run at the same time): no Blocking, 1 Major, 3 Minor. Each fix RED first, then green, then its line
broken and seen red again (`research/module_tests.md` § *MCP server instances*).

| # | Finding | Disposition |
|---|---|---|
| coai 0 (Major) | the starts basis an untyped string in the model | **Fixed** — `McpStartsBasis` an enum; the wire name given at the edge (`McpServerReport.BasisName`); JSON unchanged |
| coai 1 (Major) | the collector depends on the Actions layer (`SuspectTermination.Sample`, `RunningState.StartTolerance`) | **Fixed by extraction** — `Collectors/Procfs/PidSamples` (the record `PidSample`, `Read`, `StartTolerance`); A11, A18's history, the shared signal path and the MCP collector all use it; A11/A18 tests unchanged and green |
| coai 2 (Major) | `Directory.Exists` answers false for a folder it may not traverse — an unreadable log root read as "no logs, 0 starts" | **Fixed** — `McpRunLogs.Present` walks down from the home with the bounded listing: unreadable or cut = unavailable, only a whole listing without the folder = absent. RED: *Expected starts.Count.IsAvailable to be False … but found True* |
| coai 3 (Major) | past `mcpServers.maxInstances` the idle / busy counts covered the listed ones only, unsaid | **Fixed** — the verdict says "CPU, idle and busy figures over the N listed: mcpServers.maxInstances", the text line "of the N listed". RED: the value lacked it |
| coai 4 (Minor) | each instance scanned every log entry | **Fixed** — `McpLogs.ByPid`, grouped once |
| coai 5 (Minor) | status blocks silently for the window | **Rejected** — 1 s by default, at most 5 s, only when a server runs; a stderr line would land in the extension's log on every poll; the wait is in the contract (`limits`), the README and Q-M5 |
| own M1 (Major) | the start was computed as now − age with a now taken AFTER the window: a 3 s window moved it past the log's name and the instance lost its own log | **Fixed** — `McpJudge` takes `agesAt`, read before the window. RED: *Expected instance.LastLogWrite.IsAvailable to be True … but found False* |
| own m1 (Minor) | the live-start rule applied to ANY process at the pid, so a reused pid could invent a start | **Fixed** — only a live process that is this server; else the dead-pid rule. RED with the fixture's file inside the window (its first version passed for the wrong reason: the file was outside the window) |
| own m2 (Minor) | a missing or unseen log root = a measured zero | **Covered by coai 2** for the unseen case; a root a WHOLE listing shows absent stays 0 starts — that is a measurement, not a guess |
| own m3 (Minor) | a link swapped in between the lstat checks and the listing (inherited from A18's listing) | **Residual, recorded** — only `coai-mcp-HH-mm-ss-pid.log` names are kept, nothing is opened, the listing is capped; the full fix is a descriptor-based listing (`openat` + `O_NOFOLLOW` + `O_DIRECTORY`) for every `SessionGlob` user, a story of its own |

**Final coai round** (`again`, same session, 2026-10-06): verdict **proceed**, 4 of 4 reviewers answered, 7 findings: 3 ACCEPTED,
4 rejected with reasons.

| # | Finding | Disposition |
|---|---|---|
| 0 (Blocking) | no `research/module_*.md` for the MCP module | **Rejected** — checked: `research/` holds no `module_*.md` but `module_tests.md`; every daemon module is a section of `architecture.md`, and this one has its section, diagram and module-map row |
| 1 (Major) | nullable lists on the wire report | **Rejected** — the status wire's stated convention (`StatusReport.cs` header): an unavailable block carries no value keys; `[]` would read as "available, zero instances" |
| 2, 3 (Major, two reviewers) | `mcp.cpu` did not say it covers the listed instances only | **Fixed** — the same note as `mcp.instances`. RED: the value lacked it |
| 4 (Major) | `Directory.Exists` is false for a folder behind an untraversable parent; the bounded listing answered "empty, complete" | **Fixed** in `PhysicalFileSystem.ListEntries(path, bounds)`: the attributes tell not-found (empty, whole) from access denied (`Unreadable`). RED in WSL: *Expected … Unreadable, but found … Listed* |
| 5 (Minor) | a progress line for the window | **Rejected** again — round 1's reason stands; `status` writes to stderr only to refuse |
| 6 (Minor) | `status` read beside `stat` in the CPU window | **Rejected** — a few hundred bytes per pid, bounded by `mcpServers.maxInstances`; one sampler for three callers is round 1's extraction |

##### After the rounds (2026-10-07)

- **Start times (the owner's correction, 2026-10-07):** the storm of 2026-10-06 began at 16:50Z, BEFORE the 0.43.0 binary's
  mtime of 16:54Z — so the churn is attributable only per start time, and "busy without activity" stays the key signal.
  Each server now lists the starts inside the window with their time, pid, run-log last write and whether they still run
  (`McpStart`, `startTimes` on the wire), newest first, capped by a thirteenth key `mcpServers.maxStartsListed` (0–1000,
  default 50; the count is never capped). RED first (empty lists), GREEN, teeth (the running rule and the cap).
- **Final round finding 0 reversed:** after the rebase onto `main`, `research/` HAS the module-document convention
  (`module_vs_code.md`, added 2026-10-06 after the same kind of finding), so the rejection — made on the older tree — no
  longer held: [module_mcp_servers.md](../research/module_mcp_servers.md) now carries the module, and `architecture.md`
  keeps a pointer and the module-map row (it is near the conventions resolver's 256 KiB cap).
- **Round 3** (`again`, same session, after the rebase onto `main` `1654e56`, 2026-10-07): verdict **proceed**, **8 of 8**
  reviewers answered (codex AND gemini — the gate now runs two vendors), 8 findings: 2 ACCEPTED, 6 rejected with reasons.

| # | Finding | Disposition |
|---|---|---|
| 0 (gemini, Major) | the server match looks at argv[0] only, so an interpreter-run server cannot be matched | **Rejected** — deliberate (C-2: `printf coai-mcp` is not a server, a test holds it); no catalogued server is interpreter-run; a catalogue entry may declare scripts when one is added (Q-M2) |
| 1 (gemini, Major) | the verdicts bypass `ThresholdRules.Evaluate` | **Rejected** — `McpVerdicts` is the one evaluator of the three ids, called by the two entry points that hold an MCP sample; the engine must not pay the CPU window where it samples the probe |
| 2 (gemini, Major) | the bounded listing's DEFAULT member reads an unreadable folder as empty | **Rejected** — only test doubles use it; the one production file system overrides it; doubles that need "unreadable" re-implement it (three test files do) |
| 3 (gemini, Major) | "a hard-coded pid 4242" in the activity reason | **Rejected, false** — the golden normaliser's rule `pidInText` (`GoldenContracts.cs:141`); the product names the instance's own pid, a test holds it |
| 4 (codex, Major) | nullable lists on the wire | **Rejected** again — the stated wire convention |
| 5 (codex, Minor) | a new log layout would fall back to the live count silently | **Fixed** — an exhaustive match on `McpLogLayout` in the summary and the log reader (refactor, no behaviour) |
| 6 (codex, Major) | a pid reused between the snapshot and the FIRST CPU read reports another process's CPU | **Fixed** — the snapshot keeps each process's start ticks (`ProcessEntry.StartTicks`) and the first read must match them. RED: *Expected instance.CpuPercent.IsAvailable to be False … but found True*; teeth: the arm removed, red again |
| 7 (codex, Minor) | a stderr progress line | **Rejected** a third time — the earlier reason stands |

#### Stories

| # | Story | Files (verified above) | Acceptance | Model, reviews |
|---|---|---|---|---|
| **E7.S0** | **The config trust and contract (daemon) — R1.** FIRST the live bug of R1.3 (B1): closed text / list keys, `processes.families` without `other` / `ai-agents`, `archive.baseFolder` machine-only, the slot-bounds and no-config-in-policy tests; then the hardened read of both layers (owner per layer, 256 KiB) and the bounded `ReadCurrent`; the sweep of the six sibling root reads under the per-class reader policy (M2) + the call-site classification test; interop-off ⇒ the safe-direction rule (M3); the logging keys' safe direction; the user-layer digest in `status`; run detail `config` provenance (additive); `contracts/config-keys.json` (name, shape, min, max, allowed, default, `rootEffect` and its safe direction — what the extension's loosening modal keys on) + `ContractFilesTests`; the test that no key reaches a policy, template or path slot; capability `config.contract` | `ConfigLoader.cs`, `RegularFiles.cs`, `PhysicalFileSystem.cs`, `IFileSystem.cs`, `CliHost.cs`, `TargetUser.cs`, `WindowsSystemDrive.cs` (reuse only), `TargetUserCommands.cs`, `BrowserAndHttpCaches.cs`, `EditorServerCleanup.cs`, `HealthCollector.cs`, `DockerHygiene.cs`, `Collect/RunDetail.cs`, `ConfigKeys.cs` (metadata), `ConfigValidation.cs`, `ProcessFamilies.cs`, `SuspectTermination.cs` (tests only), `Cli/Logging/WslCareLogging.cs`, `UserConfigWriter.cs`, `Status/StatusReport.cs`, `Status/Capabilities.cs:36`, `contracts/config-keys.json` (new) | `config set processes.families other` refused (RED today) and a user layer holding it is a `ConfigError`; a FIFO, a link, a foreign-owned or group-writable user layer → observe-only naming why, within 1 s, root never blocks; the drvfs reads still read a 0777 `.wslconfig`; interop disabled → a loosening user value ignored with the sentence, a tightening one applied, user-scoped actions still run; a timer run under a user value names it in `runs show`; contract drift red with one renamed key | **Fable** if its monthly limit has reset, else **Opus** (recorded, §15j M10); two own reviews: security / confused deputy, crash / durable state |
| **E7.S1** | **The agent catalogue, discovery, the daily walk (daemon) — R2.** `Agents/agents.json` (embedded data: binaries, npm packages, data folders per OS, session layout per D2, `neverEnter`); discovery (PATH as the invoking user — the target user's fixed bin list as root, `TargetUserCommands`; npm global folders by existence, no `npm` process; folders); `slow.agents` (totals, counts, dates) on the 20 h walk under the 3 min total ceiling; `agents list [--measure] --json` (both RIDs); D3's version from disk, nothing executed; `memory` never entered for any agent, the prefix exclusion, the per-directory device check; catalogue → protected roots + never-list names | `Agents/` (new), `FolderSizes.cs` (the walk joins `collect`, not `FolderSizes`' own list), `SlowParts.cs`, `CollectRun.cs:344`, `LinuxHostPaths.cs:127/137`, `WindowsHostPaths.cs:77`, `NeverList.cs:31`, `Files/TreeWalk.cs`, `Files/IFileSystem.cs`, `PhysicalFileSystem.cs` and the file-system fakes, `Json/WslCareJsonContext.cs`, `CommandLine.cs` (verb), contracts goldens | `agents list` on the fixture home finds each entry by binary, by npm package, by folder alone; the §4.6 per-agent fields; sessions per layout, "—" when unconfirmed; growth vs the previous sample; no process started by discovery (a recording runner); the syscall-level no-open scenario (H3); a nested mount → "excluding <subdir> (different filesystem)"; the walk stops at the total ceiling with the rest "not measured this run" | **Opus**; two own reviews: agent-folder safety (H1–H3), privacy |
| **E7.S2** | **`aiAgents.extra` and `agents probe` (daemon) — R2.** A fifth value shape (`AgentListKey`: `{cli, side, name, dataFolders[], sessionGlob}`) with R2.1's validation; `config set aiAgents.extra -` reading compact JSON from stdin (the bounded stdin reader of `--only -`, `Cli/StdinList.cs:20`, widened, 1 MiB / 10 s); `agents probe <path> --json` unprivileged only (C3's refusal text); the declared cleanup roots (M8); the two-phase host (M1); extras in the walk and — failing or not — in the protected roots (B2); capabilities `agents.list`, `agents.probe`, `config.agentsExtra` | `ConfigKey.cs`, `ConfigValidation.cs`, `ConfigDocument.cs`, `UserConfigWriter.cs`, `StdinList.cs`, `Agents/`, `ProtectedRoots.cs`, `CliHost.cs`, `Program.cs`, `Capabilities.cs`, `ICleanupAction.cs`, `CacheFolders.cs`, `NpmCacheClean.cs`, `ToolCacheTrims.cs`, `BrowserAndHttpCaches.cs` | each R2.1 refusal names the rule (the product's own folders included); a manual folder makes A12 / A17 refuse under it; a valid extra, then a new overlapping cleanup root → the action refuses (B2); the run's policy holds the extras (M1); every user-home action declares its roots; the probe refuses as root naming uid 0 and the fix; the probe opens nothing; `cli` never a path argument in a root run | **Opus**; two own reviews: path validation / confused deputy, data safety |
| **E7.S2b** | **Orphaned AI-agent processes (daemon) — owner decision 2026-10-05.** A18, a button only: the target user's `ai-agents` processes that are orphaned, have no TTY, used NO CPU for `processes.aiAgentsIdleHours` (default 4) MEASURED by identity `(pid, boot_id, start ticks)` against the root-only `agent-cpu.json`, and whose agent (confirmed layout only) has no session file modified within that window; SIGTERM then SIGKILL after 10 s; every process in the record | see *E7.S2b* above | the RED tests listed there | **Opus**; the coai code round after E7.S2 (or its own) |
| **E7.S2c** | **Every number configurable (daemon + extension) — owner rule 2026-10-05.** The inventory's A rows as ordinary keys, its B rows as machine-layer-only keys with hard maxima (three raise-only), coupled limits derived; every call site reads the effective configuration; the extension's ceilings as settings whose minimum is the daemon's computed worst case; the structural no-literal test with group C as its allowlist; defects N-1–N-6 fixed RED first | see *E7.S2c* above | see *E7.S2c — the story* | **Opus**; two own reviews (root safety of the B keys; the extension ceilings) |
| **E7.S2d** | **MCP server instances of the AI agents (daemon) — owner request 2026-10-06.** A read-only collector over the one process snapshot: catalogued MCP servers (`coai-mcp` first, `mcpServers.watched`) under an agent session (or orphaned), CPU % measured across a window, idle / busy-without-activity by the server's own log, restart churn from its log names; `status --json` `mcpServers` with each start's time, three verdicts, thirteen keys | see *E7.S2d* above | the RED tests listed there | **Opus**; the coai gate (plan + code round) and an own review |
| — | *(gate)* the daemon parts merge; `extension-v0.1.0` tagged (E5 live gate) and E6.S2 merged (PR #12) before E7.S3 | | | |
| **E7.S3** | **Settings ↔ config (extension) — R1's other half.** `package.json` settings generated from / held equal to `contracts/config-keys.json` (`application`, `ignoreSync`); ONE module `src/config/configCall.ts` (only `config get --json`, `config set <key> <value>`, `config reset <key>`, `config set aiAgents.extra -`, no `-u`); the bundle scan amended: `config` allowed ONLY in that region, forbidden in the root region and everywhere else; `-u`, `root`, `--timer` forbidden in the config region; the reconcile + one-time notice; mirror-on-change, revert-on-refusal, the loosening modal; a daemon without `config.contract` → the settings shown read-only "update the daemon" | `src/config/` (new), `client/verbs.ts`, `client/WslCareClient.ts`, `extension.ts`, `package.json`, `test/bundleScan.test.ts`, `test/structure.test.ts`, the fake `wsl.exe` | each mirrored setting's exact argv; the scan red with `config` planted outside its region, `-u` planted inside it; a refused value reverted with its message; one notice per digest across a reload; no `-u` anywhere on the path | **Opus**; two own reviews: confused deputy (argv, scopes, sync), durable state of the notice / revert loop |
| **E7.S4** | **The AI-agents section and *Add CLI path…* (extension, WSL side).** The panel section of §7.2 from `agents list --json` (on panel open / refresh, never polled — §15g M1); D4's flow; the manual badge and Remove; warnings (`aiAgents.warnGb`, `sessionWarnMb`); "—" for unconfirmed sessions; R2.4 sanitising | `src/agents/` (new), `panel/fieldMap.ts`, `panel/viewModel.ts`, `panel/messages.ts`, `panel/panelHtml.ts`, `media/panel.js`, `research/architecture.md` field map (:1830) | the path-mapping table (UNC, `\\wsl$`, another distro, `X:\`, a control character, a leading `-`); the probe's exact argv with no `-u`; the webview's two messages only; a crafted project name renders inert | **Opus**; two own reviews: confused deputy (path → argv), webview / rendering |
| **E7.S5** | *(split by review M11 into S5a / S5b / S5c — the three rows below this one; this row's text is S5a's origin and is kept as the record)* **The bundled `wsl-care.exe` (extension + release).** `release-extension.yml` fetches the `win-x64` asset of `daemon-v<MIN>`, verifies `.sha256` and the attestation with the exact identity (§15e A1), bundles `bin/wsl-care.exe`; a `--target win32-x64` `.vsix` (§15f #13) beside the universal one or instead — decided there with the Marketplace's per-target rules read first; the allowlist and leak scan cover the exe; a closed Windows verb set (`status --json`, `agents list [--measure] --json`, `agents probe <path> --json`, `config get/set` for the Windows layer, `--version`) spawned by ABSOLUTE path from `extensionUri`; Windows numbers in Memory / Disk (`vmmemWSL`, host RAM, `C:` free — the `WindowsProbe` of E2.S1); the Windows agents rows and *Add CLI path* for `X:\…`; the walk at most hourly, newest result in `%LOCALAPPDATA%\wsl-care\agents-last.json` | `release-extension.yml`, `check-vsix.mjs`, `.vscodeignore`, the `vsce ls` list, `src/windows/` (new), `Collectors/WindowsProbe.cs` (reuse), `WindowsHostPaths.cs` | the `.vsix` holds exactly the allowlist + the exe whose hash equals the attested asset; the Windows rows filled, never 0 when unavailable; `vmmemWSL` max / min on the Logs page stays "arrives in E11" (no Windows history before E11 — §15j M7) | **Opus**; one own review: release / supply chain |
| **E7.S5a** | **The bundle:** fetch, `.sha256` and attestation of the `win-x64` asset, `bin/wsl-care.exe`, the per-target `.vsix`, the allowlist and leak scan, spawning by absolute path; only `--version` and `agents list` used | as S5 | the exe's hash equals the attested asset; the `.vsix` holds exactly the allowlist | **Opus**; one own review: supply chain |
| **E7.S5b** | **The Windows agents:** the Windows rows and *Add CLI path* for `X:\…` (R2.1's Windows extras rules); the Windows config layer defined — `%APPDATA%\wsl-care\config.json` (`WindowsHostPaths.cs:65`), keys `aiAgents.*` only, written only by the extension, inherited by E11; `agents-last.json` (one file) until E11 retires it | `src/windows/`, `WindowsHostPaths.cs`, `Agents/` | a Windows extra under `%TEMP%\claude`, through a reparse point or on a removable / network volume refused; the walk at most hourly, in the background, under the total ceiling, the cached result shown at once (Q9) | **Opus**; two own reviews: path validation, data safety |
| **E7.S5c** | **The Windows numbers in Memory / Disk** (`vmmemWSL`, host RAM, `C:` free — `WindowsProbe` of E2.S1, rendered) — explicit, last; moves to E11 if E7 runs long, recorded then | panel field map, `research/architecture.md:1798-1808` | the rows filled, "unavailable — <reason>" never 0 | **Opus** |

**Every story** follows the red-green-red order of `research/module_tests.md`: the RED test written first and seen failing
for the real symptom, the fix, green, then the load-bearing line reverted and seen red again; whole suites on Windows and
WSL (normal user, a `/tmp` copy removed after); `dotnet format --verify-no-changes`; `npm test`; the family checks; the
docs (`research/architecture.md` — the configuration seam row and *The target user's home*, a new *AI agents* seam, the
field map rows at `:1798-1830`; `research/module_tests.md` flows; README's *Configuration* and *AI agents*; this section's
deviations).

**RED tests, one per story (named for the guarantee).**
- S0 `A_families_list_cannot_widen_A11_beyond_the_named_families` (today `config set processes.families other` is
  accepted and A11's candidates take every family — B1, a live bug);
  `A_user_layer_root_cannot_trust_is_refused_at_once_and_never_followed` (a FIFO — today the read blocks; a link to a
  root-only JSON — today followed and its keys echoed into `configError`);
  `With_interop_disabled_a_user_value_can_only_tighten_root`.
- S1 `Agents_list_finds_each_catalogue_agent_by_binary_npm_or_folder` (today: unknown verb, exit 2);
  `The_agent_walk_never_enters_memory_and_never_opens_a_file` (a 10 MB `projects/p/memory/x` excluded; a recording double).
- S2 `A_manual_data_folder_cannot_overlap_a_cleanup_root_or_leave_the_home`; `Agents_probe_refuses_root_and_never_executes`;
  `A_failing_extra_stays_protected` (B2); `The_runs_deletion_policy_holds_the_accepted_extras` (M1).
- S3 `A_mirrored_setting_reaches_the_daemon_only_through_config_set_without_root` (scan + argv);
  `A_value_the_daemon_refuses_is_reverted_with_its_message`.
- S4 `A_picked_path_of_another_distribution_is_refused_before_any_spawn`.
- S5 `The_bundled_exe_is_the_attested_asset_byte_for_byte`.
- Teeth for each: remove the exclusion / the owner check / the region rule / the `-u` guard and watch the test go red again.

#### The release interplay

- **The daemon parts (E7.S0–E7.S2) merge to `main` at any time**, CI green — riding `daemon-v0.1.0` if they merge before the
  owner cuts it (no daemon tag exists yet; `.release-please-manifest.json` is 0.0.0), else the next daemon minor. The
  extension acts on their CAPABILITIES (`config.contract`, `agents.list`, `agents.probe`, `config.agentsExtra`), never on a
  version (§15j M5); an older daemon gives "update the daemon to see AI agents / to edit settings".
- **The extension parts (E7.S3–E7.S5) merge only after `extension-v0.1.0` is tagged.** §15j B3, as written, protects the
  first public extension from a ROOT path, and E7's extension code has none — so B3's letter does not cover it. **§15f #5
  does:** the first public version "must NOT contain … `config set`" (it writes what root reads — R1), its verbs are only
  `status` / `preview` / `doctor` / `--version`, and it carries no `wsl-care.exe`. All three are E7's. The mechanical check
  (§15k #7, keyed on tags) is widened in E7.S3: an extension release at or below `extension-v0.1.0` refuses when the bundle
  carries the config region's marker, an `agents` verb, or `bin/wsl-care.exe` — defence in depth behind the merge order,
  not the protection itself.
- **They also follow E6's extension half** in practice: E7.S3 amends E6.S2's partitioned scan (PR #12), so it starts from
  `main` after PR #12 merges. The release is then `extension-v0.3.0` (§16); if the owner lets E7 go first, it is the next
  minor after 0.1.0 and the §16 number moves — the order is the owner's (open question 10).

#### Boundaries with the neighbouring plans

| Item | Built by | The other side's part |
|---|---|---|
| the agent catalogue, session layouts (D2), `aiAgents.extra` with `sessionGlob`, the protected roots | E7 (this section) | E9 ([PLAN_ai_session_archive.md](PLAN_ai_session_archive.md) §3) adds each entry's `archive` block and the move; it reads E7's `sessionGlob` (validated by E7, R2.1) and must not redefine "one session" |
| the Windows agents walk and its one-file cache | E7.S5 | the Windows collectors, task and history are E11 ([PLAN_windows_care.md](PLAN_windows_care.md)); `%TEMP%\claude\` and every TEMP cleanup are E12 (W-A2's guard) |
| orphaned AI-agent processes | E7.S2b (A18, the distro's) | Windows' W-A11 (E12, [PLAN_windows_care.md](PLAN_windows_care.md)) is separate; the same rule should apply there — a setting defaulting to 4 h, a button only, idle measured by identity |
| the daemon values the extension MIRRORS (`status --json` → `limits`) | E7.S2c (the writer, `Status/StatusLimits.cs`, and `contracts/status-limits.json` generated from it) | PR #12 / E6 (`shared/daemonLimits.ts`, the reader: `historyRetentionDays`, `requestFutureSkewSeconds`; whole numbers in range, else the contract's default); its reader test reads the contract file; the four further fields (`requestGraceSeconds`, `maxShownNames`, `unitStopSeconds`, `drainGraceMilliseconds`) are for its worst cases and selection checks to adopt |
| the extension's numbers (settings, ceilings above the daemon's worst case, N-1–N-3) | PR #12 (`settings/numbers.ts`, `client/worstCases.ts`, `ceilings.test.ts`) | E7.S2c makes the daemon's ceilings keys; those whose range only LOWERS keep the extension's copy a safe upper bound, the rest are in `limits` |
| the bundle scan's regions | E6.S2 (root region), E7.S3 (config region) | E7.S3 widens E6.S2's rule "config forbidden everywhere" to "everywhere but the config region"; the root region stays as E6 left it |

Order: E7's daemon parts first (additive), then the extension parts after both release gates above. Disjoint otherwise.

#### Growth and budget

| Surface | Projected size | Who retires it | Interrupted |
|---|---|---|---|
| the user layer | ≤ 256 KiB by the reader cap; `aiAgents.extra` ≤ 16 × 8 × 1 KiB ≈ 130 KiB worst, ~1 KiB typical | rewritten in place (atomic) | atomic temp + rename (exists) |
| `config.json.broken-*` (an unparseable layer moved aside, `UserConfigWriter.cs:110-136`) | one file per corruption event, ≤ 256 KiB each; rare | **kept forever, a decision** — the user's text is never discarded; listed by `doctor` when any exist | — |
| `slow.agents` on the history line (totals, counts, dates — no session names) | once per 20 h: ~6 tracked agents × ~300 B ≈ 2 KB/day → ~0.2 MB over the 90-day history retention; worst case (every catalogue entry + 16 extras) ≈ 11 KB/day → ~1 MB | the history's 90-day retention | the line is written whole or not at all (write order §15b #1) |
| run detail `config` provenance | ≤ ~50 entries × ~80 B ≈ 4 KB per run | the run details' 90-day retention | as the detail |
| `%LOCALAPPDATA%\wsl-care\agents-last.json` | ONE file, two samples (newest + the one ≥ 20 h older, for growth), ≤ 64 KiB | replaced on every walk; E11 retires the file (its history takes over) | atomic temp + rename |
| `/var/lib/wsl-care/agent-cpu.json` (E7.S2b: per process identity, the oldest unchanged and the newest CPU sample) | live `ai-agents` processes of non-root accounts, ≤ 512 identities; a full file is ~90 KiB (measured by a test: ~180 B an entry), read under a 128 KiB cap; ~10 entries ≈ 2 KiB typical | pruned on every run to live identities; the cap drops the oldest | atomic temp + rename; a torn or unreadable file = "no history" (nothing eligible), rewritten whole |
| extension `globalState` | one digest per distribution | replaced when the digest changes | — |
| unprivileged run logs of `agents list` / `config get` / `agents probe` | one file per call (§15g M1); on panel open / refresh / a click only — never polled | the existing log retention | — |

The walk's TIME also grows: the WSL agent folders measured on 2026-10-02 hold ~2.7 GB in a few thousand sessions; E7.S1
measures the added time on the fixture and the live gate on this machine, against a full `collect` that already takes
3.5 min (§17 #2).

#### Build order

1. E7.S0 (everything later writes through it) → 2. E7.S1 → 3. E7.S2 → 3b. E7.S2b → 3c. E7.S2c → the daemon release carrying them →
4. *(wait for `extension-v0.1.0` and PR #12)* → 5. E7.S3 → 6. E7.S4 → 7. E7.S5 → 8. the E7 live gate.

#### The E7 live gate (owner; each step observed, stamped with date, build and outcome)

1. **The R1 premise:** from an unprivileged shell in the distro, `/mnt/c/Windows/System32/wsl.exe -d <distro> -u root
   --exec id -u` — record whether it answers `0`; with interop disabled, record that a loosening user value is ignored.
   Also recorded: the mount mode drvfs gives `.wslconfig` (M2) and the umask a login shell in WSL Ubuntu has (R1.1).
2. `agents list --json` on both sides against §4.6's numbers; the walk's added time inside the real `collect`.
3. *Add CLI path…* end to end for one Linux and one Windows CLI; the probe ran unprivileged (its run log is in
   `$XDG_STATE_HOME`, not `/var/log`).
4. A setting changed in VS Code appears in `~/.config/wsl-care/config.json`, owned by the user; a timer run under it names
   the layer in `runs show`; the conflict notice appears once.
5. Settings Sync: a mirrored setting with `ignoreSync` does not arrive on a second machine — or it does, and C2's rule is
   seen holding (the synced value reaches the conflict notice, never the daemon); §15g M3's "which settings file a UI-kind extension reads in a Remote-WSL window" if
   still open.
6. The extension release after `extension-v0.1.0` (and E6's), its guard green, `POST_DEPLOY.md` run.

#### Test plan (beyond the RED tests)

- **Daemon:** discovery over a fake home per catalogue entry and per detection route; session counting per confirmed
  layout over captured, anonymised tree SHAPES (names from `FixtureIdentity`, sizes synthetic); `neverEnter` and
  link-never-followed property tests over random trees; every R2.1 rule at its edge; protected roots ⊇ catalogue ∪ accepted
  extras, never-list names = catalogue names; `ContractFilesTests` for `config-keys.json`; the `ReadFile(` call-site
  classification with its planted companion; goldens `agents-list-*.json`, `agents-probe-*.json`, `config-get-*.json`
  regenerated and read; scenario flows for every new verb in `research/module_tests.md` (a missing row is red, E1).
- **Extension:** `package.json` ↔ contract equality; the reconcile as a pure function (explicit-only, machine-layer not a
  conflict, digest stable across key order); the revert loop terminates; the loosening modal shown exactly for the
  contract's root-effect direction; the path mapper table; the strict fake refuses `config` with `-u`, `agents probe`
  with `-u`, any `config set` key outside the list; page scripts RUN over the goldens; the bundle scan with every planted
  companion; `@vscode/test-electron` on 1.85.0 and stable.
- **Release (S5):** the `.vsix` allowlist with the exe, its hash against the attested asset, the leak scan over the exe's
  strings, the per-target packaging read from the built artefact (not from the workflow's opinion).

#### Definition of Done

- [ ] H1–H4 each held by a named test, seen red with the guard removed.
- [ ] Root reads both config layers and the six sibling user-controlled files through the hardened reader; every `ReadFile(`
      call site classified by a test with a planted companion.
- [ ] Interop disabled ⇒ the user layer only tightens root, user-scoped actions still run; said by `status` / `doctor` /
      `config get`.
- [ ] Every text / list key closed or a declared path key (B1, the families bug seen RED first); every number slot inside
      its key's range; no policy or protected-roots type references the configuration except the typed extras input;
      `aiAgents.extra` only adds protection, and a failing extra stays protected (B2).
- [ ] The run's deletion policy holds the accepted extras (two-phase host, M1); every user-home action declares its
      cleanup roots (M8); no extra on the product's own folders (M9).
- [ ] `agents list` / `agents probe` on both sides; sessions per the one definition, "—" when unconfirmed; no agent binary
      executed at all (D3 as changed by M10); the walk stops at a device change (C1) and at its total ceiling (M7).
- [ ] Every mirrored setting generated from `contracts/config-keys.json`, `application` + `ignoreSync`; `config` spelt only
      in the config region; the one-time notice and the revert path tested.
- [ ] *Add CLI path…* by picker (by URI scheme, M12) and by typed path; the path never in a root argv, never opened by root.
- [ ] Mirrored only for "our" changes (C2), by one window (M4), through one write function (M5), without starting the VM
      (M6); a partial *Keep VS Code's* reported "k of N" (C4).
- [ ] The owner's ask (2), "older than N", met for the EXISTING age keys only (no new knob, Q7) — plus the one knob the
      owner decided on 2026-10-05, `processes.aiAgentsIdleHours` (E7.S2b).
- [x] E7.S2c (daemon, 2026-10-05; the extension's settings and N-1–N-3 on PR #12): every behavioural number a key or a setting (the owner rule 2026-10-05), the structural no-literal test green with
      group C as its reasoned allowlist; no extension ceiling below the daemon's computed worst case (N-1–N-3).
- [x] E7.S2d (2026-10-06): the MCP server instances of the AI agents in `status --json` and the run detail, three verdicts, thirteen
      keys, read-only; Windows recorded as the E11 next step.
- [ ] E7.S2b: an orphaned `ai-agents` process ends only by the button, only after N h without CPU measured by identity,
      never with a live session of its agent; the timer never ends one (test).
- [ ] `wsl-care.exe` bundled byte-for-byte from the attested asset (S5a); the Windows agents rows (S5b) and the Windows
      Memory / Disk rows (S5c, or recorded as moved to E11) filled.
- [ ] The extension parts merged only after `extension-v0.1.0` (and PR #12); the widened release check proves it.
- [ ] Docs, goldens, `research/module_tests.md` flows and this section's deviations updated; the E7 live gate stamped.

#### Open questions for the owner

The review round's proposed defaults are this plan's WORKING ASSUMPTIONS (the code is built to them); each is still the
owner's to overturn: Q1 never enter `memory`; Q2 session listing yes, counts persisted only; Q3 never run an agent binary
(M10); Q4 skip loosening + apply safe-direction values (M3); Q5 the modal only for `dryRun` true → false, an `auto` on for
A5 / A6Unused / A8 / A11 / A12 / A17 and any families change, a notice with Undo for ages and thresholds; Q6 keep the
unused keys, `daemonUnused` in the contract; Q7 no new knobs; Q8 names shown live in the panel only, never persisted in
history; Q9 the Windows walk at panel open, ≤ hourly, in the background, under a total ceiling, the cached result shown at
once; Q10 E6 first → `extension-v0.3.0`; Q11 DECIDED (C5): the sweep is in E7.S0; Q12 stays with the open churn decision.
Added by the review: **Q13** may `ai-agents` be a choosable A11 family (assumed: no)? **ANSWERED by the owner 2026-10-05:** not via the
general families list; via E7.S2b's dedicated button-only action A18 (see *E7.S2b*).

1. **Memory folders:** never entered at all (proposed — their size is not counted, the total says "excluding memory"), or
   may their SIZE be measured by stat (still never opened)?
2. **Session listing:** counting sessions lists names and stats entries inside the agents' folders (no file opened) — is
   that within "sizes may be measured"?
3. **Agent versions:** never run any agent binary (working assumption, M10) — is a version from disk enough?
4. **Interop off:** skip the user layer for root (proposed), or always read it?
5. **The loosening modal** for root-effective keys (an `auto` switched on, an age lowered, `dryRun` off): wanted, or too
   much friction?
6. **Unused daemon keys** `distro` / `refreshSeconds`: keep them unused (proposed), or retire them with a migration note?
7. **New "older than N" knobs** for A14 (keep newest 2 builds), A17 (`cargo sweep --time 30`) or A12 — wanted in E7, or
   left as built?
8. **Showing session and project names** (the 5 largest sessions) in the panel — acceptable? They never enter goldens,
   fixtures or the `.vsix`.
9. **The Windows walk** at panel open, at most hourly, newest result kept in one file — acceptable, or only on Refresh?
10. **Order:** E7's extension parts after E6's (PR #12) → `extension-v0.3.0` (proposed), or E7 settings first?
11. **The root-read sweep** (six sibling sites of the hardened-reader class) inside E7.S0 (proposed, per the
    security rule's "sweep the class in the same task"), or as a separate fix first?
12. **The run-log churn** of the new unprivileged verbs joins the open §15g M1 / §15j M6 decision.

#### §15q review round (2026-10-05) — the coai plan round and an own plan review

**The coai plan round:** verdict **proceed**, 5 findings, all ACCEPTED. **The own independent plan review:** verdict "not
ready as written, but close" — 2 Blocking, 12 Major and the minors below, all ACCEPTED. Each row OVERRIDES the text it
names; the text above was updated to match.

| # | Finding | Disposition | Lands in |
|---|---|---|---|
| C1 (Major, coai) | the `st_dev` check on the data folder only — a nested bind mount drags the walk onto 9p | **Accepted.** Per-directory device check; stop at a change; "excluding <subdir> (different filesystem)" | R2.1, R2.3, E7.S1 |
| C2 (Major, coai) | if Sync ignores `ignoreSync`, a synced root-effective value is mirrored silently | **Accepted.** Only "our" changes (a host-held token) are mirrored; any other change goes through the conflict notice | D5, E7.S3 |
| C3 (Minor, coai) | the probe's root refusal must name uid 0 and the fix | **Accepted.** The text names `--set-default-user` and `/etc/wsl.conf` | D4, E7.S2 |
| C4 (Minor, coai) | *Keep VS Code's* with N keys — partial failure undefined | **Accepted.** Continue past a refusal, report "k of N applied; refused: …" | D5, E7.S3 |
| C5 (Minor, coai) | DoD vs open question 11 | **Accepted, decided:** the six-site sweep is in E7.S0 | R1.1, Q11 |
| B1 (Blocking, own — a live bug on `main`) | R1.3 is false: `processes.families` accepts `other` → root's A11 ends other uids' idle orphans; `archive.baseFolder` free text; number slots | **Accepted.** Closed text / list keys (families ⊆ catalogue − `other` − `ai-agents`), `archive.baseFolder` machine-only path key, slot bounds, no config in the policies — RED first | R1.3, E7.S0 (first) |
| B2 (Blocking, own) | a failing extra dropped at root read UNPROTECTS the folder | **Accepted.** It leaves the walk only; its paths stay protected; an overlap makes the action refuse | R2 (intro), E7.S2 |
| M1 | the deletion policy is built before the config is loaded | **Accepted.** Two-phase host; a test on the run's policy; the never-list residual stated | R2.2, E7.S2 |
| M2 | `ReadOwned`'s g/o-write refusal fails on drvfs (0777) | **Accepted.** Per-class reader policy; the real mount mode recorded | R1.1, E7.S0, live gate 1 |
| M3 | interop off must not reuse `HomeOwner.Unknown` | **Accepted.** A separate reason; the target user kept; safe-direction values applied | R1.2, E7.S0 |
| M4 | the change event fires in every window | **Accepted.** One lease-holding window mirrors; markers by (key, value); a two-window test | D5, E7.S3 |
| M5 | the loosening modal bypassable through the reconcile | **Accepted.** ONE write function for every user-layer write | D5, E7.S3 |
| M6 | the activation reconcile starts the VM | **Accepted.** `--list --running --quiet` first | D5, E7.S3 |
| M7 | no total budget for the agent walk | **Accepted.** 3 min total, catalogue first; `--measure` below the host timeout; the added time measured | D1, R2.3, E7.S1 |
| M8 | "cleanup roots from the action registry" does not exist | **Accepted.** A new `ICleanupAction` member, a test, the relocation residual stated; files added to S2 | R2.1, E7.S2 |
| M9 | an extra on the product's own folders would lock `config set` | **Accepted.** Refused | R2.1, E7.S2 |
| M10 | executing agent binaries for `--version` | **Accepted — D3 changed:** nothing is executed; version from the link target or the npm `package.json`, else "not asked" | D3, E7.S1, Q3 |
| M11 | E7.S5 too large; Windows numbers; the Windows layer undefined | **Accepted.** S5a / S5b / S5c; the Windows layer and extras rules defined; E11 retires `agents-last.json` | stories, R2.1, growth |
| M12 | a Remote-WSL dialog returns `vscode-remote://wsl+<d>/…` | **Accepted.** Map by URI scheme; case-insensitive distro names; in the mapping table test | D4, E7.S4 |
| m-a | the widening is `IFileSystem.ReadStateFile`'s hard-wired owner, not `ReadOwned` | **Accepted** | R1.1 |
| m-b | the classifier must also cover `File.ReadAll*`, `ReadRegularFile`, root's `MeasureTree` / listings of target-home paths | **Accepted** | R1.1, E7.S0 |
| m-c | `UserConfigWriter.ReadCurrent` reads plainly — a FIFO hangs `config set` | **Accepted.** Bounded reader | R1.1, E7.S0 |
| m-d | a group-writable layer (umask 002) turns the timer observe-only | **Accepted.** The umask measured first; a private group accepted or `doctor` prints the `chmod` | R1.1, E7.S0 |
| m-e | `logging.*` from the user layer steers root's audit log | **Accepted.** Root-effective with a safe direction | R1.6, E7.S0 |
| m-f | `TreeWalk`'s `neverEnter` is exact names only; S1's files incomplete | **Accepted.** Prefix exclusion, per-directory device check, one grouped pass; `TreeWalk.cs`, `IFileSystem.cs`, the fakes added | R2.3, E7.S1 |
| m-g | `memory` must be never-enter for EVERY agent walk, extras included | **Accepted** | H2, R2.3 |
| m-h | H3 needs a syscall-level proof | **Accepted.** inotify `IN_OPEN` or `strace` over the fixture tree | H3, E7.S1 |
| m-i | session names in `history.jsonl` | **Accepted.** Totals / counts / dates only; names live only | D1, growth |
| m-j | the native Settings UI cannot show the layer or read-only | **Accepted.** Said in the panel; such a change ignored with a notice | D5 |
| m-k | a change mirrors to the current distribution only | **Accepted**, stated | D5 |
| m-l | ref drift: `Capabilities.cs:36` → `:37` | **Checked, not taken:** `grep -n` on `origin/main` puts `All` at line 36. The other drift accepted: `research/architecture.md`'s field map and §15f #5 / #13 now point at E7.S5a–c | stories |
| m-m | the widened release check is defence in depth | **Accepted**, said | release interplay |
| m-n | the owner's ask (2) is met only for existing keys | **Accepted**, in the DoD | D5, DoD |
| m-o | a user-layer digest in `status` so an outside change is noticed | **Accepted** — added to E7.S0 | R1.7, E7.S0 |
| Q1–Q12 | the proposed defaults | **Accepted as working assumptions**, still listed for the owner | open questions |

### 15p. E6.S4 review round (coai code round + two own reviews, 2026-10-05)

The coai code round over E6.S4 (verdict **proceed**, all 4 reviewers; 6 findings — 3 accepted, 3 rejected), the own
security review (no findings; one note below the threshold, folded into K3) and the own correctness review (C1–C7), with
the coordinator's dispositions. Every accepted finding landed RED → GREEN → RED (its load-bearing line broken, restored by
SHA-256) — the record is `research/module_tests.md` § *The E6.S4 review round*. The owner's answers to E6.S4's open
questions: the trend table instead of a drawn sparkline — **accepted**; ONE *Logs* button for the last cleanup — fine for
now; the act side of §15o — **yes** (C7); the host tier's one `EPERM` while removing its temp folder — kept recorded,
investigated if it recurs (it did not recur in this round's run).

| # | Finding | Disposition |
|---|---|---|
| K1 (coai) | `logsController.test.ts` held its own async copy of `support/zone.ts`'s `withZone` | **Fixed** — `withZone` restores when a returned promise settles; the copy is gone. Found while giving the test teeth: `delete process.env.TZ` does NOT reset Node's zone cache, so the helper leaked its zone into every later test of a file (measured); it now SETS the machine's zone back before deleting the variable, and its test runs in a file of its own (`zone.test.ts`) |
| K2 (coai) | `runReadTail` sent the union tag as the CLI verb | **Fixed** — `RUN_READ_VERBS`, an explicit `{ [K in RunReadName]: readonly string[] }` table; a new read does not compile until its verb is written |
| K3 (coai) + the security review's note | a fast double press of *Logs* could create two `WebviewPanel`s (an await before `createWebviewPanel`); a restored panel replaced an open one | **Fixed** — `logsPage/panelSlot.ts`: the panel is created or revealed synchronously, before any await; a panel VS Code restores while one is open is disposed and the open one revealed; a late dispose of an older panel never empties the slot of a newer one |
| R1–R3 (coai) | three findings whose premise was a 15-minute timer — the run list, the history read and the payload of a 90-day window treated as tens of thousands of lines | **Rejected** — the daemon's timer is `OnCalendar=*-*-* 00/4:00:00` (`src_daemon/systemd/wsl-care.timer`): six runs a day, 540 in the 90 days the daemon keeps, under ~1 000 with every button press — the bounds already in place (`MAX_RUN_INDEX` 9 999, the 20 s read ceiling, `logs` reading history lines only) hold with room |
| C1 (Important) | an unreadable history (exit 4, the daemon's `problem` with all-zero figures, `RunLogs.Logs` / `RunShow.Read`) was shown as fact; `runs show` then said "unknown — it never existed" | **Fixed** — an answer carrying a string `problem` is its own state (`unreadable`): every block, the run list, *This run* and an expanded detail say "the run history could not be read: <problem> — no figures" (through `safeText`), and show no figure. The client already answered exit 4 with a body; a test now holds it for `logs` and `runs`. The fixtures are the goldens with `problem` added in the test (no daemon golden carries one) |
| C2 | the window label was recomputed at every render — past local midnight day D's answers were relabelled D+1 | **Fixed** — the window is built when a read starts (`readsFor(period, window)`) and kept beside its answers; a render never recomputes it |
| C3 | "no cleanup is recorded yet" when `status` had not answered or had failed | **Fixed** — three reasons: "status has not been read yet", "status did not answer: <label>", "no cleanup is recorded yet"; the panel's store change re-posts the Logs view (`statusChanged`) |
| C4 | every render wiped the date inputs | **Fixed** — the header and the picker are built once and updated in place: `min` / `max` always, a value only while the input still holds what the page last set |
| C5 | `ready` re-read and collapsed the expanded runs on every tab return | **Fixed** — `ready` posts the answers held for the current period and reads only when it holds none; `refresh` still reads |
| C6 | a flash of the old period's answer under the new period's label (the selection set before the persist await, the generation bumped only when the read started) | **Fixed** — a selection begins synchronously: its generation, its cleared slots and its window before the await; a selection superseded while it was written is not read |
| C7 (§15o, the open question) | an act entry could be resolved by a manual line of kind `collect` carrying its ids | **Fixed** — an act's line must be `kind === undefined || kind === "act"`, symmetric with the full check's. Precondition met: the daemon stamps `act` on every act-origin line (§15o on `fix/wc-full-check-line-names-collect` — the terminal line, the request sweep, the running-state sweep and the refusal carry the request's kind) |
| main moved | `main` gained the daemon's §15o (#16, a629b37): the goldens carry `kind`, and `contracts/history-reasons.json` holds the reason prefixes a reader uses for a line WITHOUT a kind | **Rebased onto `main`**: the follower's fallback prefixes are now EXACTLY the contract file's (two of the three had been shorter copies) — a compiled copy held EQUAL to `contracts/history-reasons.json` by a test, the way `ACTION_IDS` is held to `contracts/actions.json` (the bundle reads no repository file at run time); a reconciled full-check orphan with a readable detail now carries `kind: collect` AND the reconcile's prefix — kind wins (tested); the strict-kind test reads the goldens' kinds |
| N-2 / N-3 (+ N-1), from the E7 numbers inventory (§15q E7.S2c on `feat/wc-e7-agents-settings`) | the preview's host ceiling (330 s) EQUALLED its worst case, and "Clean selected" previews k Docker rows each with its OWN snapshot (k × 330 s) under the same 330 s; the detach's 90 s failed with two stale requests in the sweep (101 s); `doctor`'s 100 s was below its 109 s | **Fixed** — `client/worstCases.ts` derives every call's worst case from the daemon's per-command ceilings (each killed command + its 4 s drain) and what the call can do: a snapshot 330 s, `doctor` 109 s, a detach 671 s (the shown list, one `systemctl show` per queued request up to 32, the start, one more show), a stop 124 s, a preview the SUM of its rows' shares (a Docker row 330 s, A9 34 s); `client/ceilings.ts` sizes each call from the settings, in ONE place; `ceilings.test.ts` holds each ceiling strictly above its worst case with the defaults AND every setting at its minimum, for all 255 selections of rows. A detach past its ceiling stays "outcome unknown", followed. New defaults: `doctor` 120 s, `preview --all` 350 s, a preview 350 s per Docker row + A9 + the base, a detach 690 s |
| "every number is configurable" (the owner's standing rule) | the E6 numbers were constants | **Done** — `settings/numbers.ts`, one table, `package.json` held equal to it: `wslCare.timeouts.{status,version,doctor,preview,previewPerDockerRow,runRead,detach,stop}Seconds`, `wslCare.cleanup.{followPollSeconds, unknownDetachFollowSeconds, followCeilingMinutes, requestGraceSeconds, previewExpiryMinutes, journalEntries}`, `wslCare.logs.maxRunIndex` — application scope, ranges and defaults; a value out of range clamped; read at each use. A ceiling's minimum is its worst case + 10 s; the request grace's minimum is above the daemon's 60 s. Not made settings: values that MIRROR the daemon (the 90-day retention, the 5-minute clock skew, the run-id and instant shapes), the follower's internal read retries and batch size, `wsl.exe --list`'s ceiling (E5) — recorded, not decided |
| tests green with a bug | absent ≠ 0, the stale-detail guard, the midnight rollover, exit 4 on run reads, the act negative case | **Added** — each with its teeth shown by mutation; the stale-detail guard's generation check first looked redundant (its mutant survived) until the case it guards was written: expand, Refresh, expand the same line again — the old list's late answer must not fill it |

## 16. Epics and stories (split 2026-10-02, on Fable, as the gate's operator commands require)

Every epic is its own branch from the previous epic's final commit, one review-gate code round over its
whole diff, CI green at its head. Every story ships its scenario flows and updates
`research/module_tests.md` and `research/architecture.md`. Outside the epics, by hand with the owner: the
**E4 live gate** (below); Phase 0 (0.1, 0.4–0.8) — no longer before E4's live install (§15f): it gates the dryRun
week's REVIEW instead, which needs Phase 0.1's `.wslconfig` cap in place for its numbers to mean anything; the Windows
§5 elevated diagnosis before E12; the dryRun week and this plan's promotion after E13.

| # | Name | Branch | True when done |
|---|---|---|---|
| E1 | Skeleton, foundation seams, daemon CI, scenario harness | `feat/wc-e1-skeleton` | `dotnet build wsl_care.slnx` green on ubuntu+windows; test executables run; AOT `linux-x64`/`win-x64` with zero trim warnings + `--help` smoke; `WslCare.Scenarios` drives the built CLI; a test fails when a CLI verb is missing from `module_tests.md` |
| E2 | Linux collectors, `status`/`collect`/`preview`/`doctor`, events follower | `feat/wc-e2-collectors` | `status --json` < 2 s with `schemaVersion`; `collect` writes history, run detail, run log; `preview --all --json` reproduces the 2026-10-02 rows from fixtures; follower markers + backfill; `doctor --json` |
| E3 | Action engine, A1–A17, never-list guard, `logs`/`runs` | `feat/wc-e3-actions` | every `act A#` behind its `auto` switch; property test: no input yields a *never* command; lock, 7-day dryRun, wedged/dead `running.json`; `logs --period` / `runs` answer §7.4 |
| E4 | Units, `install.sh`, release pipeline, `daemon-v0.1.0` | `feat/wc-e4-daemon-release` | files + tests + CI green, merged (§15f #4). The release and the install are the **E4 live gate** below — `daemon-v0.1.0` with 3 RID assets + `.sha256` + attestations, installed here, timer + events unit active, `doctor` green, stamped |
| E5 | Extension prototype (read-only) + **Marketplace** `extension-v0.1.0` | `feat/wc-e5-extension` | files + tests + CI green (`ci-extension.yml` included), merged (§15g M4): `status --json` carries `verdicts` + `productVersion` (E5.S0, rides `daemon-v0.1.0`); a universal `.vsix` WITHOUT `wsl-care.exe` (bundling is E7.S3) and with no root call path, held by the allowlist and content checks; status bar + read-only panel per the §7.2 / B2 field map; *Install daemon* that types the pinned command; `release-extension.yml` + `tags-extension.json` as files and tests — scope and exclusions per §15f #1, #3, #5, #6, #8, #10, #12, #13 and §15g. The Marketplace listing is the **E5 live gate** below, after the E4 live gate's stamp |
| E6 | Daemon contract, detach, cleanup buttons, root boundary, Last cleanup, Logs page (re-split by §15j) | daemon parts `feat/wc-e6-daemon`; extension parts `feat/wc-e6-cleanup-logs` | **daemon (E6.S0, E6.S1):** merged with CI green at any time — riding `daemon-v0.1.0` if merged before the owner cuts it, else the next daemon minor (§15j B3); then the **E6 daemon live gate**. **extension (E6.S2–E6.S4):** merged only after `extension-v0.1.0` is tagged (§15j B3): preview → host confirm → detached run → result for every A#, durable across a reload; root only through the ONE argv module (§15f #2, §15j M1); Logs page = §7.4 as §15f #7 and §15j M3 / M7 amend it; then the **E6 live gate** — per §15f #1–#3, #7, #9–#11 and §15j. E6.S0 / E6.S1 may start before the E4 live gate's stamp (§15j m10); releases and live measurements wait for it |
| E7 | AI-agent discovery, settings ↔ config, Add CLI path (re-split by §15q into E7.S0–E7.S5) | `feat/wc-e7-agents-settings` | `agents list` matches §4.6 on both sides; *Add CLI path…* end to end; settings mirrored with the one-time conflict notice; the daemon parts merged at any time, the extension parts only after `extension-v0.1.0` and E6's extension half (§15q *The release interplay*); then the **E7 live gate** (§15q); `extension-v0.3.0` |
| E8 | Help in 5 languages, zoom, tone — via the kit | `feat/wc-e8-help-kit` | the kit imported, no copied coai modules; articles in en/ru/uk/de/es with fallback + stale notes; zoom/tone on every page; `extension-v0.4.0` |
| E9 | AI-session archive — daemon, both sides | `feat/wc-e9-archive-daemon` | `archive preview\|run\|restore\|list` on both sides; never-move property tests; index-before-delete + reconcile; live round trip byte-identical; A13 in the timer |
| E10 | Archive in the extension | `feat/wc-e10-archive-ui` | settings with folder picker; Archive page with restore; Logs show A13; `extension-v0.5.0` |
| E11 | Windows collectors, `install` (task + logman) | `feat/wc-e11-windows-collectors` | `status`/`collect`/`doctor`/`install` on Windows; the task and perf log running here; pool tags with fallback |
| E12 | Windows actions, elevated channel, advisors with undo | `feat/wc-e12-windows-actions` | W-A1…W-A14 with preview; elevated request → result round trip; advisors/undo round-trip exactly; Windows never-list property test |
| E13 | Extension Windows group | `feat/wc-e13-windows-ui` | Windows sections; side column in Cleanup/Logs/AI agents; shield actions through the channel; `.wslconfig` cap shown, never written; `extension-v0.6.0` |

**The E4 live gate (owner).** E4 is done when merged; what only the owner can do follows, in this order, each step
observed rather than assumed (`docs/repo-settings.md` has the commands):

1. The repository settings applied — the release App and its two secrets, the tag ruleset and the `main` ruleset —
   each ruleset first created WITH a probe that GitHub refuses (`docs/repo-settings.md` steps 1–4).
2. The live contract (`POST_DEPLOY.md` #4) inside WSL `Ubuntu`: 0 failed, 0 skipped.
3. release-please dispatched, its pull request read (exactly 0.0.0 → 0.1.0) and merged, dispatched again: the tag
   `daemon-v0.1.0` and the draft cut, `release.yml` observed from the guard to the publish.
4. The live install here: `curl … install.sh | sudo sh` (gh ≥ 2.56.0, no login).
5. `POST_DEPLOY.md` 1–11 run against it and STAMPED with the date and the version.

E5.S0–E5.S3 may start at once (S1–S3 against fakes). The E5 live gate waits for that stamp; E6.S0 / E6.S1 code may start
against the scenarios before it (§15j m10) — only releases and live measurements need it.

**The E5 live gate (owner).** E5 is done when merged (§15g M4); what only the owner can do follows, AFTER the E4 live
gate's stamp, in this order, each step observed rather than assumed:

1. The Marketplace publisher created (the id is permanent; the display name "AI OS Care" checked free — the owner's rename of 2026-10-06; done 2026-10-06: publisher `remsoftdev`, the extension `remsoftdev.ai-os-care`); the current state of
   Azure DevOps global PATs checked before choosing `VSCE_PAT` over `vsce publish --azure-credential` (§15g m9).
2. The `marketplace` Environment with a required reviewer, and `VSCE_PAT` in it with its expiry recorded in
   `POST_DEPLOY.md` — or the OIDC credential.
3. The tag ruleset `tags-extension.json` and the updated `branch-main.json` (the `ci-extension` contexts) applied, each
   first created WITH a probe that GitHub refuses (`docs/repo-settings.md`).
4. The minimum daemon release (`daemon-v0.1.0`, carrying E5.S0) published and stamped by the E4 live gate; then
   `contracts/golden/daemon-0.1.0/` frozen from it (§15g m7) and committed.
5. release-please's extension pull request read (exactly 0.0.0 → 0.1.0) and merged; the tag `extension-v0.1.0` cut;
   `release-extension.yml` observed from the guard through github-draft and publish-marketplace to github-public (order of §15h #0); before it, `POST_DEPLOY.md` item 3's preview timing at the real container count (§15h #1).
6. `POST_DEPLOY.md` run and STAMPED with the date and the version: the Marketplace serves 0.1.0 == the tag; the installed
   `.vsix`'s contents == the attested build; the live panel against the installed daemon (the real extension → `wsl.exe`
   → daemon path, §15g m6); no spawn while WSL is stopped.

**The E6 daemon live gate (owner, §15j).** E6.S0 and E6.S1 are done when merged; what only the owner can do follows, AFTER
the E4 live gate's stamp, each step observed rather than assumed:

1. FIRST — the distro-survival measurement (§15k #5 + #13, moved here from E6.S1 because it needs root and every `wsl.exe`
   closed): start a long unit through the exact `wsl.exe -u root` call the panel will make, close EVERY `wsl.exe` client,
   wait past the distro's idle timeout, and record in `research/` whether the unit — and the distro — survived. E6.S2 /
   E6.S3 do not rely on the reload promise until it is recorded; if they do not survive, §15k #5's keep-alive or the
   narrowed promise applies.
2. The daemon release carrying E6.S0 + E6.S1 cut: `daemon-v0.1.0` itself when they merged before its cut (B3), otherwise the
   next daemon minor (`daemon-v0.2.0`), cut after `daemon-v0.1.0`. That version is `minDaemonForActions`.
3. Installed here (`install.sh`, which installs the template unit `wsl-care-act@.service`).
4. `POST_DEPLOY.md` run against it (item 7 reads the template's properties): the template unit loaded; its properties observed (`systemctl show -p
   KillMode,TimeoutStartUSec,MemoryMax wsl-care-act@x`, §15j m6); a detached A10 / A1 confirm SURVIVES killing the calling
   `wsl.exe` and `runs show` of it answers `done`; an `act --stop` of a deliberately wedged run observed end to end (the run records
   itself `interrupted` on SIGTERM, or the 90 s SIGKILL is swept with the stop marker's reason); the live measurements E6.S1 could not make without root (the AOT binary
   dying with `wsl.exe` without `--detach`, the stdin relay rows, `systemctl start` from `wsl.exe -u root`) recorded in
   the facts note — STAMPED with the date and the version.
   **Observed 2026-10-06 against `daemon-v0.1.0`: item 7 FAILED** — the template carried `CollectMode=inactive-or-failed`
   under `[Service]`; systemd 255 logged `Unknown key name 'CollectMode' in section 'Service', ignoring.` and `systemctl
   show` gave `CollectMode=inactive`, so a failed detached run stays in `systemctl --failed` and counts in
   `systemd.failedUnits`. The checker had printed PASS: it runs a cell's FIRST code span, then `systemctl cat` alone. Fixed
   at `daemon-v0.1.1` (tagged, never published: `release.yml` lacked the conventions fetch its Scenarios suite needs — first published in `daemon-v0.1.2`) — the key moved to `[Unit]` (`ShippedFilesTests` holds every key of every unit and drop-in to the
   section systemd reads it from); CI's verify reads EVERY unit file, templates as instances, with the build's drop-ins
   (`verify-systemd-units.sh` — the step had named the three E4.S1 units by hand and never read the template); items 1, 5,
   7 and 11 now assert their values (`PostDeployCommandFlows`; `research/module_tests.md`, *The act template's
   CollectMode*). This step re-runs items 1, 5, 7 and 11 against 0.1.2.
5. `contracts/golden/daemon-<minDaemonForActions>/` frozen from that release and committed (a `daemon-0.1.0` set that
   already carries E6.S0 serves when B3's first case applied).

**The E6 live gate (owner, §15j).** E6.S2–E6.S4 are done when merged — and they merge only after `extension-v0.1.0` is
tagged (B3). Then, after the E6 daemon live gate's stamp:

1. `extension-v0.2.0` released only after `extension-v0.1.0` (B3); its guard requires `daemon-v<minDaemonForActions>`
   published and stamped (M5).
2. `POST_DEPLOY.md` run and STAMPED: a real reload during a real confirm survives and the panel shows the run through to
   its result; *Last cleanup* and the Logs page match the CLI (`status.lastCleanup`, `logs`, `runs show`); no spawn while
   WSL is stopped.

**Stories and their models** (Fable where a wrong answer is paid for later; Opus otherwise):

| Story | Content | Model |
|---|---|---|
| E1.S1 | root build files, `wsl_care.slnx`, Core/Cli + test projects, `ci-daemon.yml` (matrix, format, build, test exes, AOT publish, smoke), actionlint/shellcheck, pr-title, dependabot | Opus — mirrors CredsForDevs |
| E1.S2 | config layering + schema with observe-only on an invalid layer (§15a #1), `config get/set/reset`, `ICommandRunner` (argv, ceilings, tree kill) + recording double, `IFileSystem` + `DeletionPolicy` + architecture test (§15a C1), `IHostPaths` (C3), `IHostProbe`, run records incl. `interrupted`, Serilog. **Built 2026-10-02; deviations:** `processes.killEnabled` dropped (duplicate of `auto.A11`; A5/A6 carry two switches each per §5); `aiAgents.extra` deferred to E7 (object shape); the schema lives in code (`ConfigKeys`) with defaults in the embedded `default.json`, no separate schema file; the run-file sink writes with its own `StreamWriter`, not `Serilog.Sinks.File` (one package); `WslCare.Core` has zero packages and no `ILogger<T>` yet; console logs go to **stderr** (stdout carries answers); `--help`/`--version`/usage refusals open no log file; a `RootTooBroad` rule (root = `/`, drive root or home) was added; `/tmp/claude` is the Linux analogue of `%TEMP%\claude\`; only `history.jsonl` is written (the per-run detail file is E2.S3); a `WslCare.TestSupport` project holds the shared doubles; `WSL_CARE_ROOT` sandboxes every path for tests | **Fable** — the seams every later story hangs on |
| E1.S3 | `WslCare.Scenarios` (built CLI, temp home, fake docker/systemctl/journalctl on PATH), fixtures, derived verb register. **Built 2026-10-02; deviations:** the fakes are ONE C# console project (`WslCare.FakeTool`) whose apphost is copied under each tool's name, not scripts — a `docker.cmd` is not found when a program starts a bare `docker` on Windows; the scenario `PATH` holds the fakes and nothing else, so no real tool is reachable; `fixtures/` holds one SYNTHETIC self-test file only — real tool captures are E2's (§15a C2), none invented here; the built-binary launcher moved to `WslCare.TestSupport/ChildProcess` (shared with `BuiltBinaryTests`), with UTF-8 streams; the first scenario run found that a refusal is one `wsl-care:` MESSAGE but not one stderr LINE — the console log writes its request line to stderr too — so the scenarios classify stderr and the README now says so; the flow catalogue row format is fixed: first cell starts with `` `wsl-care <usage>` `` as `CommandLine.Commands` spells it; beyond the story: `ci-daemon.yml` gained the `linux-arm64` leg on `ubuntu-24.04-arm` (the family platform rule; E1's done-line names only linux-x64/win-x64) and the AOT config round-trip smoke; the family checks run in a new `family-checks.yml` (the four tools; ROLLOUT.md's `rules.mjs check` and `gate-snippet-check` are not wired); the CLI's `HOME`/`USERPROFILE` are not redirected in a scenario — isolation rests on `WSL_CARE_ROOT` | Opus |
| E2.S1 | memory/process/disk collectors (RssAnon + RssShmem, cgroup dedup — §15b #4), Linux + minimal Windows probe, `status --json` with no slow process (§15b #5). **Built 2026-10-02; deviations:** the container figure SUBTRACTED in the unattributed arithmetic is the cgroup's `memory.stat` anon + shmem, not `memory.current` (both are reported) — measured: mixing `memory.current` (page cache + kernel memory included) with AnonPages + Shmem made the live remainder −1.05 GiB, like for like +0.46 GiB, and the captured fixture reproduces it; the clock tick and page size come from `/proc/self/auxv`, not a native `sysconf`; command lines are shown with secret-looking values redacted (measured: `--connection-token=`, `--csrf_token=` in plain argv) — not in the plan; the §4.2 families are a built-in catalogue, regex settings wait for E7's object-shaped keys; every counted process is kept (`ProcessSnapshot.All`) for A11, the report shows the top 30; the minimal Windows probe is host RAM (`GlobalMemoryStatusEx`, hence `AllowUnsafeBlocks` for the generated stub), the system drive and `vmmemWSL`'s working set — host `C:` is the Windows binary's, never read through `/mnt/c`; the `.vhdx` sizes are named in the answer and unavailable until E11; the daily folder sizes and `.vhdx` growth of §4.4 are full-run figures (E2.S3); `RunRecord` gained an optional `slow` part (`containerStats`, `windowsClock`) that `status` reads back with its age — its writers are E2.S2/E2.S3; `status` without `--json` prints a short ASCII summary; `IFileSystem` gained `ReadLink` and `MeasureVolume`, `LinuxHostPaths` the proc / cgroup / filesystem roots and `/etc/passwd` (all under `WSL_CARE_ROOT` when sandboxed), `WindowsHostPaths` the system drive; the harness fakes `powershell` too (`powershell.exe` on Linux); the Linux scenario over the procfs tree runs on the Linux legs and is skipped on Windows (run by hand in WSL from a `/tmp` copy on 2026-10-02); the 2 s budget is held by the second start after a warm-up; `ci-daemon.yml` gained a `status --json` smoke of the published binary | Opus |
| E2.S2 | Docker collectors with `available: false` (§15b #7), `volume-seen.json`, hygiene audit, `preview --all --json`, `WslCare.LiveContract` (§15b #2, #6). **Built 2026-10-02; deviations:** every docker argv is built in ONE place (`DockerCommands`, read verbs only, a ceiling each: 10 s for the `version` probe, 30 s listings, 2 min `system df`), run through `ICommandRunner` as a named `ToolCommand`; the tool's FILE is the product's decision too — a bare name is resolved on `PATH` alone and started by its full path (`ExecutableResolver`, added after CI run 37045304356 found GitHub's `System32\docker.exe` answering every Windows scenario ahead of the fake on `PATH`, because the operating system searches System32 and the current directory first); the failure kinds are `notInstalled`, `daemonStopped`, `socketRefused`, `timedOut`, `commandFailed`, `refused`, `unparseable`, classified from Docker 29.6.1's own stderr (measured on Linux and Windows); containers are read from `system df -v` + a `container inspect` TEMPLATE that names its fields (no environment, command or bind source ever leaves the docker CLI) — `docker ps -a` is parsed only by the live contract; the rows are named by their `auto` switch (`A5Testcontainers`, `A6Unused` are rows), and A6 also excludes every image id an inspected container uses whatever Docker's count says; `volume-seen.json` records the first sighting UNATTACHED and drops a name attached again (age = "unused for"); privilege is the OS's answer — the write is attempted, root succeeds on the installed `0755` state directory, an unprivileged run reports `read-only` and writes nothing; ~~a root `preview` also records~~ (withdrawn after the E2 code round, gate finding #3/#6/#10: `preview` never writes `volume-seen.json`, whatever its privilege — only `collect` records first sightings); A8/A9 are rows marked `available: false` until the full run measures the folders (E2.S3); the builder-GC figure is the Windows binary's (`%USERPROFILE%\.docker\daemon.json`) and unavailable inside the distro until E2.S3 resolves the Windows profile; under Docker Desktop container log sizes are unavailable (the logs are in its VM); `IFileSystem` gained `FileSize`, `IHostPaths` `DockerDesktopConfigFile` and `DistroPath`; the `systemctl show` / `journalctl --disk-usage` / `docker events` parsers ship here for the live contract, their collectors are E2.S3's. Found live: `$m.Name` in an inspect template fails the whole command on a bind mount (fixed with `index`); `container inspect` exits 1 for a container removed since the listing (now read for the rest); Docker's `system df` / `ps` fail transiently ("snapshotter.Usage failed … lstat") while another session creates containers; Docker's event buffer held only 255 healthcheck `exec_*` events, no start of the last 24 h — the follower's backfill must expect that. The fixtures are the AFTERNOON Docker (after the one-time cleanup): the morning's 387-volume / 59.6 GB rows had no saved raw output and are not reproduced | Opus |
| E2.S3 | thresholds, health collectors, run detail → history write order + startup reconcile (§15b #1), retention sweeps, `collect` (read-only when unprivileged, §15b #3), `doctor`, `events follow` with bounded backfill and in-process backoff (§15b #0, #8). **Built 2026-10-02; deviations:** thresholds without a `ConfigKeys` setting are named constants in `ThresholdRules` (plan §4's starting points; keys with E7 if the dryRun week asks for tuning); an unread figure is a fourth level, `unknown`; the `.wslconfig` row SHOWS `memory=36GB` and is red above 90 % of the VM's ceiling (inside the VM `MemTotal` is the ceiling) — the file is only read, through the Windows profile the clock probe prints (`/etc/wsl.conf` automount root), which also gives the distro Docker Desktop's `daemon.json` (E2.S2's builder-GC gap); the clock offset is *Windows' process start − our launch instant* (latency = printed − started, one clock), and the two observations of §15 #10 are this run's and the previous full run's; clock jumps are systemd-resolved's "Clock change detected" lines, the kernel's signals `journalctl --dmesg --grep` of this boot (no `dmesg`); `collect` holds `{state}/run.lock` for its run (E3.S1's lock supersedes it) so the reconcile cannot race it, a second run exits 75; write failure exit 1; the trigger is `timer` when systemd's `INVOCATION_ID` is set; privilege is a delete-on-close write probe of the state directory — unprivileged writes NOTHING (not the first sightings either) and every verb that cannot write `/var/log/wsl-care` logs to `$XDG_STATE_HOME/wsl-care/logs`; the backfill reads the whole 24 h window UNFILTERED (the oldest buffered event of any kind is the only proof the buffer reaches the marker; measured: 248 healthcheck events spanning 91 s), parses it in memory only and stores only starts; the live stream runs in bounded 10-minute segments (`ICommandRunner.StreamAsync`, Docker closes a future `--until` itself — observed live), each end a `covered` marker; `events follow --once` added; the 24 h count is also partial before the follower's first 24 h and while its newest coverage is > 15 min old; folder walks are daily (> 20 h), bounded (2 M entries / 2 min), never follow a link, and "what grew" is per walked folder (not `$HOME`/`/var`); `.vhdx` sizes, inotify, VS Code Server builds and the "failed to start within" boot error are not collected; the run log keeps `logging.retentionDays` (14), not 30; under the root timer `$HOME` is root's (E4.S1 decides whose home is walked and protected — **decided in E3.S2**: the target user's); `ICommandRunner.StreamAsync`, five `IFileSystem` members and `IHostPaths.UserLogDirectory` were added; the fake tool gained prefix / call-budget / hang-after answers and `timedatectl` + `snap`. **After the code round (gate session `714367be`, all findings accepted):** the backfill reads the engine mark (`docker network inspect bridge`) and the continuity rule of §15b #0 as amended; `events follow` writes `{state}/starts-summary.json` after every marker and `status` reads only that; retention ages a history line and its `runs/{day}/` folder by ONE UTC-day boundary, removes an aged day folder whole, rewrites the history under one lock acquisition, and the reconcile never makes a detail older than the window `interrupted`; `StreamAsync` drains stderr concurrently and kills + reaps the tree in a `finally` on every exit but a normal end | Opus |
| E3.S1 | action engine, `CommandPolicy` (the one filter every runner call passes), `act` root-only (§15c #0), target-user discovery + `runuser` argv (§15c #2), lock, dryRun, idle gating, measured freed bytes, `running.json`. **Built 2026-10-02; deviations:** the reference action is A10 (`journalctl --vacuum-time=<journal.keepDays>d`, machine-scoped, never waits for idle; preview = journald's `--disk-usage` + the ARCHIVED files whose last write is older than the limit, named an estimate; freed = the before-sizes of exactly the files gone after); the never-list is BROADER than §5 — every shell in any form (not only `-c`/`-ic`), every delete or move by command (`rm`, `mv`, `find -delete`, `git clean`, … — files go only through `IFileSystem`), `docker system prune` in ANY form, any argument path under any home's `git` / an agent folder / Claude's temp folder, every command wrapper (`sudo`, `env`, `xargs`, `nohup`, `wsl`, …) but `runuser` in its one shape, kill-by-name, `sysctl -p`, inline-code interpreters — and PowerShell is allowed only as E2.S3's literal clock-probe argv; deny by default: an argv runs only as an instance of a DECLARED `CommandTemplate` (bare executable, literals, a closed set of typed slots — no slot takes a leading `-` or a control character), the catalogue = the collectors' read commands (`ReadCommandTemplates`) + the registered actions' templates, an action binds only its OWN templates (`ActionCommands`) and the runner's policy judges again; `ProcessCommandRunner`'s only public constructor takes the sealed `CommandPolicy` (`ICommandPolicy` and `AllowAllCommandPolicy` are gone; the runner's own shell-spawning tests use an internal seam a test keeps out of product code); the property test is hand-rolled and seeded (`TestSupport/HostileInputs`, no package) over 20 000 requests + every template + 2 000 action cases, judged by an INDEPENDENT oracle (`NeverOracle`), each property with a red companion (a permissive policy, an outer-argv-only policy, a planted wide template, a planted shell-string action); the action ids are the `auto.*` keys (so `A5Testcontainers` and `A6Unused` are ids) with a fixed execution order; root = `Environment.IsPrivilegedProcess`, asked FIRST (exit 77, before any read, lock or state — `--preview` included), and under `WSL_CARE_ROOT` alone `WSL_CARE_SANDBOX_PRIVILEGED=1` makes a test process answer root; the 7-day dry run starts at the timer's FIRST action pass (`{state}/first-timer-run.json`), the timer is dry while `dryRun` is on OR the week runs, a button never; ONE lock `/run/wsl-care.lock` for `collect` and `act`, the second REFUSES (75), never waits; a preview takes no lock and writes nothing; `running.json` records the process START (`Process.GetProcessById`), a reused pid within 2 s is the same process, a dead run that already recorded itself loses only its file, an unparseable file or an uninspectable pid refuses like wedged (76); idle = the kernel's load average over the window covering `idle.minutes` ÷ CPUs (I/O wait counts as busy), builds include `dotnet publish`, `pack`, `msbuild` and `npm install`, `IdleRule` TimerOnly for A1/A2 and Always for A7/A15; the protected roots cover root's and every login account's home (the walk and the user config layer still follow `$HOME` — E4.S1; **closed by E3.S2**: the target user's home); `act` under systemd is the timer, but `collect` does not run the engine yet (E3.S3 wires the timer pass); exit codes 3 (an action failed), 76 (wedged), 77 (needs root), 78 (observe-only) added; `RunRecorder` extracted from `CollectRun`, `CommandRequest.Environment`, `ExecutableResolver.ResolveIn`, `IHostPaths.RunLockFile`, `ActionRecord.status`/`wouldFreeBytes` added; `runuser`'s environment handling is read from util-linux's source, not observed (no root here); found by the new read-command table: the journal search's `--unit=systemd-resolved` (no `.service`) needed its own slot shape | **Fable** — deletion safety and the never-list |
| E3.S2 | A4–A9, A11, A12, A14, A17. **Built 2026-10-03; deviations:** the Docker rows' objects are SELECTED in one place (`Preview/CleanupTargets`) and each row has its own builder (`CleanupPreviews.A4Row` … `A9Row`), which the actions call over their own LIVE look (`DockerLook`, the collector run through `ActionCommands.AsRunner()` — declared reads only) — so an action's preview IS its row of `preview --all` (a derived test holds what / count / bytes / basis / refusal equal); the full target list goes from preview to run in memory (`ActionPreview.Targets`, `ActionItem.Key`, never serialised); "re-checked" = that live preview, taken seconds before in the same engine call, plus Docker's own refusals (no `-f` anywhere); A4 also refuses when Docker's version is not a number (the row's refusal too — before, an unreadable version passed); A4's shown list is `--volume <name>` (repeatable) and/or `--only <file>` (≤ 1 MiB, ≤ 10 000 names, a bad line refused by NUMBER, its content never echoed), A4 only, and a `manual` run of A4 WITHOUT one refuses; A4's freed figure is exactly §15c #1, its first sightings are written after the removal; A5 counts an anonymous volume only when `docker volume ls` (a new read, `DockerCommands.VolumeList`) no longer lists it, and matches Docker's "container is running" refusal by the container's NAME (the daemon quotes the name, not the id); A6 adds `--filter label!=wsl-care.keep=true` to both prunes (the preview cannot show image labels — the run can only take fewer), starts no prune when the live selection is empty, and A6Unused's `-a` also takes dangling images of that age; A7's cap is detected from `docker builder prune --help` (a read-only probe; captured 2026-10-02: buildx lists `--max-used-space`, not `--keep-storage`) — neither flag → the timer refuses; timer = capped, button AND terminal = `-a -f`; A7's dry run records the row's figure (the capped figure is the row's note); A8 / A9 preview = their rows (the daily walk's sample) — unavailable, so refused, before the first full run; the run measures live; A9 re-lists the disabled revisions at run time and removes only numeric revisions of valid snap names (`SlotKind.SnapName`, new); a missing tool skips its part, detected by `FailedToStart`, not by `PATH`; A11 measures "no recent CPU" as ZERO ticks in a 5 s window (plus a third read just before the signal), never root's processes, and signals through a new seam, `Processes/ProcessSignals.cs` (`pidfd_open` → start compared → `pidfd_send_signal`, `poll` for the end; an architecture test keeps the signalling calls there), wired by the CLI only in the distro and never under a sandbox; A11 reports no freed bytes (memory, not disk); A12's "referenced" is Playwright's own `.links/` → `browsers.json` rule, the Playwright part refuses whole on any doubt (no link, an unreadable one, a package without `browsers.json`, `__dirlock`, no process table), NuGet's http-cache is cleared by `dotnet nuget locals http-cache --clear` as the user; A14 keeps the newest 2 builds per editor (`.vscode-server`, `-insiders`, `.cursor-server`, `.windsurf-server`; `bin/*` and `cli/servers/*`) by folder time (`IFileSystem.DirectoryLastWrite`, new), keeps any build a process names (commit or path), deletes `.obsolete` extension folders but leaves the `.obsolete` file to the editor; its trigger is any target; A17 runs pnpm / uv / pip (pip3 only without pip); **`cargo sweep` is NOT run** (it deletes projects' `target/` folders, and projects live under `~/git` — the never-list wins) and **Gradle is not run** (no cache command; it prunes itself); its trigger is one cache above 5 GiB (npm's default — unmeasured, auto off); the engine gained `ActionPreview.Skip` (status `skipped`), `ActionRun.NotRemoved` / `Notes`, `ActionCommands.Locate` / `AsRunner`, and `ActionContext.Processes` / `Signals` / `ShownVolumes` / `Wait`, every one defaulting to the safe answer; **the target user's home** (§15c #2, closed here, not by E4.S1): as root, `TargetHome.Resolve` moves `Home`, the user layer and the user's state folder to the target user's home (the replaced home stays protected); ~~an AMBIGUOUS target makes the user layer unreadable → observe-only~~ (amended by §15d #2: the user layer is left out, machine-scoped actions run, user-scoped ones refuse); a MISSING `/etc/passwd` is now `None` (was `Ambiguous`); `config set` / `reset` refuse to run as root for the target user (a root-owned file would lock them out of their settings); the button's mark is `--manual` (trigger `manual`; the timer wins when `INVOCATION_ID` is also set); the property test runs all 13 actions against a generated Docker / snap world with hostile names and a hostile shown list, judges runuser argv against USER templates, and asserts — derived — that every declared template ran; the CI act smoke previews every action the binary's `--help` names, root claimed in a sandbox; a pre-existing test that expired on 2026-10-03 (`LoggingTests`, a fixed clock against Serilog's real one) was fixed | **Fable** — irreversible deletion |
| E3.S3 | A1, A2 (event-driven), A3, A10, A15, A16, `logs`/`runs`. **Built 2026-10-03; deviations:** A10 was checked against its §5 row and is unchanged (E3.S1); `sync` is a bare name resolved on `PATH` (the catalogue holds bare names only; `/bin/sync` is `/usr/bin/sync` on Ubuntu), and a `sync` that fails stops A1 before the drop; A1, A2, A3 free MEMORY: `freedBytes` stays unknown, their before / after and notes say what moved (A1 the page cache and `MemAvailable`, A2 the free order-7 blocks, A3 the servers gone with what they held), so `logs` sums disk only; A2's event is an `Urgent` preview (new `ActionPreview.Urgent`: the engine skips ONLY the idle gate — the `auto` switch and the dry-run week still apply) on no free order-7 block in zone Normal OR a `page allocation failure` in the kernel log since the last run, and "after A1" is a new `ActionContext.RanEarlier`; **"event-driven" = acted on by the first run that previews A2** (the timer pass every 4 h, a button, any `act A2`) — no watcher process was built; E4.S1's units may schedule `act A2` more often; A3's "no build alive" covers `dotnet build|test|run|publish|pack|msbuild|watch` of ANY user, refuses a button too, is re-checked just before the command, and "idle for `buildServers.idleHours`" is the server's AGE (no CPU window); A15 also stays away while `fstrim.timer` is enabled (or its state is unread) — not in the row — and "weekly" is the history's newest A15 that ran, fstrim's own report is the measured result (trimmed blocks, not freed bytes; exit 64 a success with a note); A16's two observations are the last recorded full run's and a LIVE probe (one more `powershell.exe` launch in the preview), its once-per-event record is `{state}/clock-fix.json` and the event lasts until a full run records an observation within the limit after it, a button corrects on its one live observation, the per-hour cap refuses a button too, and the tool is `chronyc makestep` only while `chronyd` runs (else `hwclock -s`); the **timer pass**: `collect` started by the timer runs the engine AFTER measuring, under the same lock, over every registered action, and records the outcomes in the SAME detail (`timerPass`) and history line; the pass reuses the engine's `running.json` sweep but not its reconcile (the full run's housekeeping did it), its `running.json` goes after the record (`EndTimerPass`); a live or wedged run stops the pass, never the measurement; a failed action there does not change `collect`'s exit code; a `collect` from a terminal or the panel never acts; `logs` / `runs` spell the period `today` (default) / `yesterday` / `yyyy-MM-dd` / `yyyy-MM-dd..yyyy-MM-dd` (≤ 366 days), not §6's `date:` / `range:` prefixes, and `runs` lists a period — `runs show <runId>` / `runs log <runId>` are not built (`logs` carries each cleanup's objects); the code lives in `WslCare.Core/History` (a `Logs/` folder is ignored by the repository's `logs/` rule on a case-insensitive file system); a new exit code 4 (`logs` / `runs`: the history cannot be read); `ReadFile` now shares write and delete, and the atomic rename waits out a reader on Windows (the flake fix, its own commit); `ReadCommandTemplates.SystemctlShow` / `JournalSearch`, `HealthCollector.MeasureWindowsClockAsync` and `ThresholdRules.IsDrift` became shared members so A15, A2 and A16 reuse the collectors' templates, the clock observation and the drift rule | Opus |
| E4.S1 | units (the timer's `ExecStart` passes `collect --timer` — §15d CI), default config, `install.sh` with checksum + attestation, uninstall. **Built 2026-10-03; deviations:** **the timer** is `OnCalendar=*-*-* 00/4:00:00` + `Persistent=true` + `RandomizedDelaySec=5min` + `AccuracySec=1min`, not §8's monotonic `OnBootSec=20min` / `OnUnitActiveSec=4h` — `Persistent=` acts on calendar timers only (systemd.timer(5)), and its stored last trigger answers what §8 chose a monotonic timer for: the first boot of the day runs ONCE for the night's missed slots; **`wsl-care.service`** adds `SuccessExitStatus=75` (`ExitCode.Busy`: a second run meeting the lock is designed, and the health collector counts failed units) and `NoNewPrivileges=yes`, and deliberately sets NO `ProtectHome` / `ProtectSystem` / `ProtectKernelTunables` / `ProtectClock` / `PrivateDevices` / `ProtectProc` / `PrivateUsers` / `PrivateTmp` / `CapabilityBoundingSet` — each breaks a named action (A8/A12/A14/A17, A9/A10 and the state, A1/A2, A16, A15, A11 and the collectors), the unit says which, `ShippedFilesTests` keeps them out; the events unit carries no `MemoryMax`/`Nice` (unmeasured); every hardening line is a request to systemd until the live install observes it; **the machine layer** ships as `config/machine.json` (not §15e #1's `config/default.json`) and is deliberately EMPTY (comments + `{}`), not the embedded defaults — a copy would freeze every default at the installed version, show every key as `(machine)`, and turn invalid (every run observe-only) the moment a release renames a key; E4.S2 packs `config/machine.json`; **attestation** is `gh attestation verify --repo oleksandrdubyna88/wsl_care --signer-workflow oleksandrdubyna88/wsl_care/.github/workflows/release.yml` (stricter than §15 #12's `--repo` alone) — E4.S2 must attest from `release.yml` itself, not a reusable workflow (a skipped test, `InstallFlows.The_signer_workflow_…`, starts checking once `release.yml` exists); gh needs a login for online verification, so under `sudo` it runs as `SUDO_USER` through `runuser`, its temporary folder made readable and still root's; **the `.sha256` is REQUIRED** (the CredsForDevs model warns and proceeds without one); beyond the plan the archive must hold only regular files and folders under `wsl-care-$VERSION-$RID/` (no `..`, no link, checked on `tar -tv` before extraction) and preflight also refuses a distro without systemd running (`/run/systemd/system`, advice to set `[boot] systemd=true` by hand), a foreign `/usr/local/bin/wsl-care`, and — for `--set-default-user` — an unknown user, a symlinked `/etc/wsl.conf` or a `[user]` section without `default=` (never rewritten; the reader mirrors `TargetUserDiscovery.DefaultUser` and a test runs both over ten shapes); **packages:** sysstat's collection is switched on through its own debconf setting (`sysstat/enable`, then `dpkg-reconfigure` when still off — Ubuntu ships it off and `doctor`'s collector check needs samples), `systemctl enable --now sysstat.service atop.service`; atop keeps Ubuntu's own defaults (600 s interval = §1's 10-minute records; LOGGENERATIONS stays 28, not §1's 7 — `/etc/default/atop` is not rewritten); `apt-get install` has no kill ceiling (a dpkg killed mid-configure breaks the package database), bounded by `DPkg::Lock::Timeout=300`; **verification** adds ONE `collect` (records, never acts) by the absolute path before `doctor`, so `lastRun` can be healthy at all, and polls `doctor --json` for up to 120 s (`WSL_CARE_INSTALL_DOCTOR_SECONDS`) — while Docker is unreachable the follower check fails it, so install with Docker running; an upgrade `try-restart`s the follower onto the new binary; `--dry-run` needs no root and still downloads and verifies into its temporary folder; usage refusals exit 2, a failed step exits 1 with `FAILED at step "<step>"`; `--purge` also removes `/run/wsl-care.lock`; E3.S3's "the units may schedule `act A2` more often" was NOT taken (A2 runs from the timer pass; a second timer waits for the dryRun week's data). **Tests:** C# in `WslCare.Scenarios` (no bats): `InstallWorld` runs the real script under `/bin/sh` over a temporary prefix (`WSL_CARE_INSTALL_ROOT`), `PATH` = fakes (curl, gh, systemctl, apt-get, debconf, runuser, sudo, id, uname, sar, atop) + links to an allowlist of real text/file tools; the fake tool gained `OutputFlag`; 29 flows in `InstallFlows` (Linux legs, one skipped until E4.S2 adds `release.yml`; each guarantee shown red by deleting the line it rests on — `research/module_tests.md`), `ShippedFilesTests` on every OS; CI gained `systemd-analyze verify` of the units (Linux legs, any output fails — it exits 0 on an unknown key) and a digest-pinned shellcheck of `install.sh`; `POST_DEPLOY.md` gained the follower and the installed timer unit, and #5 now asks for `"healthy": true`; five `;` inside sequence-diagram messages of `research/architecture.md` (E3 sections) that Mermaid read as statement ends were fixed while re-rendering every diagram | **Fable** — the install trust boundary |
| E4.S2 | release-please, `release.yml` per-RID with attestations, tag ruleset, branch protection, Sonar, CodeRabbit; cut `daemon-v0.1.0`. **Built 2026-10-03 as files and tests; the settings and the cut are the owner's (`docs/repo-settings.md`); deviations:** **the trigger** is `push: tags: daemon-v*` alone (no `workflow_dispatch`, no `pull_request`): with `draft` + `force-tag-creation` release-please, holding the App's token, creates the tag ref through `git.createRef` BEFORE the draft (read in release-please-action v5.0.0's bundled source), and that ref is an ordinary tag push — `release: published` never fires for a draft, and a dispatch carries no tag; the `extension-v*` line waits for E5; **§15e #2's "the matrix uploads to the draft"** — the build job holds no `contents: write` (§15e #0 wins), so each leg uploads a one-day RUN artifact and the ONE publish job refuses a release that is not a draft, checks the built set BEFORE uploading, uploads, downloads the draft back and checks THAT set (every RID's archive + a matching `.sha256`, nothing else — `verify-release-assets.sh`), and publishes as its last step; **runners**: the release builds on `ubuntu-24.04` / `ubuntu-24.04-arm` / `windows-latest` by name, and `ci-daemon.yml`'s `linux-x64` leg moved from `ubuntu-latest` to `ubuntu-24.04` so every pull-request leg runs on its release runner (the glibc 2.39 floor of §15 #15; job names unchanged); **the archive**: §15e #1's `config/default.json` is E4.S1's `config/machine.json`; the Windows zip holds `wsl-care.exe` ALONE (no units, no distro machine layer — the extension bundles it, E5), zipped with 7-Zip; owner 0:0, sorted names, `gzip -n`; the `.sha256` line is written by the script (`<hash>  <name>`), never by `sha256sum >` (Git Bash may mark binary mode with `*`); **the first version**: manifest `{"src_daemon": "0.0.0"}` (equal to `version.txt`; release-please backfills a previous release from the manifest only when it is NOT 0.0.0) + `initial-version: 0.1.0` (what `buildNewVersion` returns with no previous release; `simple` does not override it) — the kit used an empty manifest, 0.0.0 keeps manifest = `version.txt` at every moment; **release-please** runs on `workflow_dispatch` only until the first release pull request has been read (the family's order), its job holds `contents: read` (every write is the App token's — narrower than §9); no `extension` package yet (no `package.json` to bump; the config's `$note` says what E5 adds); **beyond the story**: the guard also refuses a tagged commit that is not on `main`; **the smoke** — `ci-daemon.yml`'s five smoke steps are now ONE script, `.github/scripts/smoke-daemon.sh` (bash on all three runners, Git Bash on Windows), which `release.yml` calls too; its scratch files moved from the repository folder into the temp folder; the scripts share one asset contract (`lib/daemon-assets.sh`: RIDs, the version pattern = `install.sh`'s, archive names); **Sonar**: NO `sonar-project.properties` — the SonarScanner for .NET 11.3.0 fails its `end` step when one sits in the folder `begin` ran in (`SonarProjectPropertiesValidator`, read in its source at that tag) and never reads one; the settings are `.github/sonar.properties`, passed key by key to `begin` by `sonarcloud.yml`; scanner 11.3.0 and `dotnet-coverage` 18.11.2 pinned; without `SONAR_TOKEN` the job warns and passes, so `SonarCloud Scan` is NOT a required check; key `remsoftdev_wsl_care`; **branch protection is a RULESET** (`.github/rulesets/branch-main.json`), not §9/§10's classic `branch-protection.json` + `scripts/branch-protection.mjs`, and the tag ruleset is `.github/rulesets/tags-daemon.json`, not `.github/tag-ruleset.json` — both plain PUT bodies, applied by the owner with a probe that must be refused; the six required contexts are the check names PR #7 reported, pinned to the GitHub Actions app (`integration_id` 15368), strict, squash or rebase only, no bypass; CodeQL default setup is an optional owner step, not required; **CodeRabbit**: `.coderabbit.yaml` (ru-RU, chill, per-path instructions for the daemon, the installer, the workflows and the scripts) + `coderabbit-review.yml` (the PR number through `env:`, `pull-requests: write` on the job only); **pins** added, each tag resolved to its commit through the GitHub API: `attest-build-provenance` v4.2.2, `upload-artifact` v7.0.1, `download-artifact` v8.0.1, `create-github-app-token` v3.2.0, `release-please-action` v5.0.0, `setup-java` v6.0.1; `ci · workflows` now shellchecks `.github/scripts/` (`-x`); **tests**: `ReleaseWorkflowTests` (16) and `ReleaseConfigTests` (5) on every OS over a YAML-subset reader that throws on anything it does not know (`WorkflowYaml`, no package), `PackageFlows` (5 — E4.S1's open item: the archive LISTED against `install.sh`'s own unpack list, then installed by the real `install.sh`) and `ReleaseScriptFlows` (9) on the Linux legs; `InstallFlows.The_signer_workflow_…` now runs; 52 mutations, each red for its own symptom (`research/module_tests.md`) | **Fable** — credentials and supply chain |
| E5.S0 | **daemon additions the read-only panel needs:** `status --json` gains `verdicts` (the same `Verdict` records and ids `collect` writes, through the same `ThresholdRules`) and `productVersion` (actions / capabilities stay E6.S0); §15g M2's compatibility rule in §6 and `research/architecture.md`, `schemaVersion` stays 1; §15g m7's golden writer + drift test with `contracts/golden/head/`. **Acceptance:** scenario flows show the verdict for a 2026-10-01-like fixture (alert) and a fresh-boot fixture (ok); a changed threshold in the user layer changes the verdict for the eight sample-evaluated thresholds, carried verdicts updating at the next full run (wording corrected by §15h #2); the drift test shown red with one field renamed; `smoke-daemon.sh` checks `verdicts` present; rides `daemon-v0.1.0` (commit type `feat(daemon):`) — §15g B1, M2, m7. **Built 2026-10-03; deviations:** ONE evaluator — `ThresholdRules.Evaluate` split without a behaviour change into `FromSample` (the eight thresholds a fast sample decides) and the full-run rest, whose ids, order and limits a reader takes from `UnreadFullRun(config)` (the rules evaluated over unread inputs; no second id list); `status` evaluates the eight NOW with the effective configuration and CARRIES every other verdict exactly as the newest full run recorded it in its detail (`FullRunVerdicts`, one file read through a narrow DTO) — not re-evaluated, because their inputs (health, Docker rows, build cache, folder sample, two clock observations) are not all on the history line; a setting changed since reaches a carried verdict at the next full run, and its `limit` says what was applied; with no readable full run each is `unknown` with the reason under the limit in force; `Verdict` gained an optional `basis` (`sample` / `fullRun`, run id, `evaluatedAt`, `ageSeconds`) — absent from what `collect` writes, so the detail is unchanged; the VM-ceiling verdict's level is the memory's, its value says the `.wslconfig` audit is the full run's (status starts no slow process); `productVersion` is `Program.VersionText`, the one expression `--version` prints (the encoder writes its `+` as `\u002B`, so the smoke parses with Python rather than grepping); the text form gains one `verdicts:` line; the 2026-10-01 scenario is SYNTHETIC over the captured tree (`ProcfsVariants`: the baseline's dump, `MemAvailable` left out, so `memory.available` is `unknown` — the "alert" is `memory.fragmentation` critical with page cache and inactive anon warn), the fresh boot invented and labelled; the goldens normalise `productVersion` and doctor's own version to `unknown` (the release number moves at every release-please bump — a pinned golden would turn the release pull request red), and `disk.root` / `clock.drift` / `journal.history` by object rules (the runner's disk, the captured clock minus now, now minus the captured oldest journal entry); doctor's unit checks are `unknown` in the goldens (no `systemctl show` of those units is captured); `PreviewFlows.AllAges` became `internal` for the writer | Opus |
| E5.S1 | **measurement, skeleton, client, CI:** the research note (§15g M6, M3 observations with dates + the WSL version); `src_vs_code` `package.json` (`extensionKind ["ui"]`, `engines.vscode ^1.85.0` + `@types/vscode` pinned to it, untrusted / virtual workspaces, `preview: true`, version 0.0.0), settings `wslCare.distro` (empty = default) + `wslCare.refreshSeconds` (minimum 30), both `application` scope; an esbuild bundle (`--external:vscode`, node18); the runner seam + `WslCareClient` (absolute `wsl.exe`, `-d <validated> --cd / --exec /opt/wsl-care/bin/wsl-care <closed verb>`, UTF-16LE, per-verb timeouts, one call per verb in flight, stderr classification, exit-code mapping, the per-verb schema handshake); the strict fake with its own tests; Test-mode injection failing closed; the m1 structural test + the `scriptInterpolation` scan; `ci-extension.yml` (m5); the `branch-main.json` contexts; Dependabot npm. **Acceptance:** client tests replay both golden sets; an unknown `schemaVersion` → "needs a newer extension"; an unlisted or out-of-pattern distro refused before any spawn; no test can reach the real `wsl.exe` (asserted); every structural assertion red with a planted instance; `ReleaseConfigTests` green with the new contexts — §15f #1, #3, #5, #10, #12; §15g M2, M3, M6, M7, M8 (CI steps), m1, m5, m6. **Built 2026-10-03; deviations:** **measured first** (`research/2026-10-03_wsl_exe_facts.md`, WSL 2.7.10.0, scripts in `research/diagnostics/wsl_exe_*.mjs`): `PATH` holds TWO `wsl.exe` (System32 and the Store alias) — the client uses `%SystemRoot%\System32\wsl.exe` only, no `windir` / `C:\Windows` fallback; `wsl.exe`'s OWN refusals (unknown distro, bad option) exit **-1** (Node reports 4294967295 on Windows; the runner reads exit codes as signed 32-bit) with their UTF-16LE sentence on **stdout**, not stderr; an inherited `WSL_UTF8=1` makes `wsl.exe`'s own output UTF-8, so the decoder decides from the BYTES (a BOM, or a NUL at an odd offset), not from the call; the missing binary is exit 1 with ONE UTF-8 relay line `<3>WSL (<pid> - Relay) ERROR: CreateProcessCommon:818: execvpe(/opt/wsl-care/bin/wsl-care) failed: No such file or directory` — "not installed" only from that line for OUR path at exit 1 (a missing other path or `Permission denied` is an unknown failure); `--exec echo '$HOME'` printed `$HOME`, `-- echo '$HOME'` printed `/home/<user>` (§15f #1 confirmed); killing `wsl.exe` ends an ordinary Linux process (the relay's hang-up) but a process ignoring SIGHUP survives, reparented to PID 1 — so the runner kills `wsl.exe` alone (no tree kill), and whether the AOT `wsl-care` ends with it is an **E5 live-gate check**; NOT measured, with reasons: a stopped distribution and the "none running" answer (stopping one is forbidden — the client treats ANY failure of the running check as stopped and the fake answers both shapes), the old-glibc line (no old distro here — matched by its documented shape at any exit code), and which settings file a UI-kind extension reads in a Remote-WSL window (needs an interactive VS Code — **an E5 live-gate observation**); **the ceilings** are the SUM of the daemon's sequential ceilings plus a margin, so not the brief's ~60 / ~180 s: `doctor` 100 s (4 × `systemctl show` 15 s + `systemctl --version` 15 s + `docker version` 10 s = 85 s), `preview` 330 s (10 s + 2 × 120 s + 30 s + one 100-container `inspect` batch 30 s = 310 s; more than 100 containers can exceed it and reads as timed out), `status` / `--version` 20 s, the three WSL questions 15 s; **the handshake**: `0.0.0(+sha)` (every build before the first release) renders like `unknown`, and an unrecognised version text renders too (the schema check still guards the shape) — §6 names only `unknown`; preview / doctor are judged against the version a status already read, else ONE cached `--version` per distribution; the act-only exit codes (3, 75–79) read as unknown failures in E5; the client's exit-code names are held equal to `ExitCode.cs` by a TS test that reads the enum (the checked-in `contracts/*.json` stays E6.S0's); **beyond the story**: every test process is started with a **tripwire** (`src/test/support/noRealWsl.ts`, `--require`d by `scripts/run-tests.mjs`) that throws on any start of `wsl` / `wsl.exe` through every `child_process` launcher, and a test asserts it is armed — that is the "no test can reach the real `wsl.exe` (asserted)"; the fake also refuses a `-d` to a STOPPED distribution (95) and a start as anything but an absolute `…\System32\wsl.exe` (96), and holds its OWN copy of the four verbs (the oracle of §15f #5); the structural tests read the sources with the TypeScript parser (`sourceScan.ts`: every import form, every string literal), not as text; the bundle scan's first break-it was GREEN for a wrong reason — an unused export is tree-shaken by esbuild — and the mutation was moved into a live argv; `wslCare.distro`'s schema `pattern` is the client's own pattern (a test holds them equal); `activationEvents: ["onStartupFinished"]` and `activate` starts no process; the bundle is not minified; Dependabot also holds `typescript` on 6.x (typescript-eslint < 6.1.0) and `@types/node` on 18.x (the Node of VS Code 1.85); a CodeRabbit path instruction for `src_vs_code/src/**/*.ts`; `docs/repo-settings.md` step 4 names the two new contexts and says to re-apply only after a pull request has reported them; **the m6 harness** is the CLIENT tier (the real `WslCareClient` over the real runner against the fake, `src/test/scenarios/clientFlows.test.ts`, flows derived from `VERB_NAMES` and every golden set present) plus the derived catalogue check over `research/module_tests.md` § *The extension* (`catalogue.test.ts`: client verbs, contributed commands and views — none contributed yet, a planted companion shows it bites); **left for later, by design**: `@vscode/test-electron` (activation, status bar, panel, xvfb, 1.85.0 + stable) — **TODO E5.S2** (not cheap without a page to render: a VS Code download per leg); the kit's `pageHarness.ts` (`node:vm` DOM shim) — E5.S2, with its first page; `vsce package`, `@vscode/vsce`, `.vscodeignore`, the `vsce ls` allowlist and the content checks — E5.S3 (M8); Sonar `c8` — deferred to E5.S3 explicitly. 157 extension tests (15 files) green on Windows (the `ubuntu-24.04` leg is CI's); 110 of the first 154 seen red against compiling stubs first, then 15 single-line mutations each red and restored byte for byte, and 3 self-review tests red first (`research/module_tests.md`) | Opus |
| E5.S2 | **status bar + read-only panel:** the status bar from `status` (+ `verdicts`; uncoloured with a hint when missing); a `WebviewView` panel from the B2 field map ("unavailable — reason", "arrives in E#"); *Cleanup* read-only; the polling policy (§15g M1, m3); the non-win32 notice; webview hardening (m10). **Acceptance:** page scripts RUN in a `node:vm` harness (allowlisted globals, a timeout, a strict fake DOM with its own tests) over the goldens, each assertion with teeth; hostile process names render as text; test-electron on 1.85.0 + stable: activates, the status bar item appears, the panel renders against the fake, no spawn while the fake says stopped or the window is unfocused; `module_tests.md` rows derived from `contributes` — §15f #5, #8; §15g B1, B2, M1, M7, m3, m6, m10. **Built 2026-10-04; deviations:** **the field map** is ONE TS table (`src/panel/fieldMap.ts`, 45 rows in 11 sections — 36 read from `status` / `preview` / `doctor`, 9 "arrives in E#" with why) that the view model renders from and `npm run fieldmap:doc` writes into `research/architecture.md` between two markers; `fieldMap.test.ts` holds the two equal and `viewModel.test.ts` fails when a read row's path is absent from the head goldens; *Top holders*, *Folders* and *Container starts* are sections of their own (the brief's list), *Swap* keeps its own; the rows are, in order, arriving / checking… / the verb's failure (per verb) / "update the daemon to see this" (absent path) / "unavailable — <reason>" / the figure; **the status bar** reads the brief's `WSL RAM <used>% · swap <x>G · <n> containers` (not §7.2's `/ 13 %`), coloured by the worst of the `memory.*` and `kernel.*` verdicts only (what the bar shows plus the allocation-failure / OOM alerts) — `ok` and `unknown` colour nothing, so the head golden's clock / systemd / collector warnings leave it uncoloured; a figure answered `available: false` is `?`; **the page→host message set** is `ready`, `rendered {rows}`, `refresh`, `openSettings`, `startWsl` — `ready` because a message posted before the page script runs is lost, `rendered` because no VS Code API lets a test see a webview's DOM (the extension-host scenarios read it); validated exactly (no extra key); **"Start WSL and check"** is the client's new `run(verb, { startIfStopped })` — the running check is still asked, its "not running" no longer stops THAT call; never folded into a poll in flight (keyed apart) — plus the command `wslCare.startWsl`; the strict fake gained `startable` (a `-d` to a stopped distribution starts it and the scenario file records it running), off by default; **beyond the plan:** every daemon string reaches the page through `safeText` (C0/C1 controls, DEL and the bidi override / isolate characters made a visible U+FFFD — a process name cannot re-order the panel —, clipped at 500 characters); the bundle scan caught the shell's first draft (`<main id="root">` spells the forbidden word) and the element is `id="panel"`; activation now asks `status` once when the window is focused (E5.S1's "activate starts no process" is superseded — the bar needs a first answer); `activate` returns a test API in Test mode ONLY (the runner seam's call log, the bar's view, the webview's row count, a focus override — a test runner cannot set window focus); the panel is a `WebviewView` in its own activity-bar container *WSL Care* with a monochrome SVG icon (`media/wsl-care.svg`); **the page harness** is ported from the kit and made STRICTER — a proxy per element that throws on every member it does not model (every HTML sink, `style`, `on*`, `src`, `href`), `createElement` of a text page's tags only, `setAttribute` of `role` / `scope` / `title` / `type` / `colspan` / `aria-*` / `data-*` only, `textContent` of a string only — and its own tests found its own defect (a refused selector shape answered `[]` on an empty element); **test-electron** (`@vscode/test-electron` 3.1.0, dev only; `scripts/run-host.mjs`, `src/test/host/suite.ts`, no mocha) runs LOCALLY on Windows against 1.85.0 and stable (1.140.0 on 2026-10-04) — two launches per version: with the strict fake, and in Test mode without it (must start nothing) — and on the Linux leg in CI only (xvfb), where it asserts the "Windows + WSL only" notice and that nothing reaches the runner; wired as two steps + a monthly download cache inside the EXISTING `ci · extension` job (job names unchanged, so `branch-main.json` and `ReleaseConfigTests` are untouched; the job's ceiling 20 → 35 min); **M1** measured, not decided: the real poller over a simulated day makes 721 `status` runs (= daemon run-log files) per fully focused day at 120 s, 241 for an 8-hour focused day, 2 881 at the 30 s floor — `research/2026-10-04_extension_poll_churn.md`; the owner's decision stays open | Opus |
| E5.S3 | **Install daemon, packaging, the release pipeline as files and tests:** *Install daemon* per §15g m2; Marketplace metadata per §15f #6 (LICENSE, a PNG icon ≥ 128 px, a README with https-only synthetic screenshots, `repository` / `bugs` / `homepage` / `pricing` / `categories` / `keywords`, CHANGELOG through release-please); `release-extension.yml` + `tags-extension.json` + the release-please package (§15g M5); the leak checks (M8); `POST_DEPLOY.md` rows; `docs/repo-settings.md` steps; the rollback line (m8). **Acceptance:** the universal `.vsix` holds exactly the allowlist and its build stamp matches; `release-extension.yml`'s structure held by C# tests (triggers, per-job permissions, the Environment only on publish, Marketplace before GitHub, the idempotent skip, the guard incl. the minimum daemon); the typed command pinned to `daemon-v<MIN>`, never `--skip-attestation` (asserted); merged with CI green — §15f #5, #6, #13; §15g M4, M5, M8, m2, m8 **Built 2026-10-04; deviations:** **§15h folded in** (the coai gate's E5 plan round, 5 findings): the publish order is draft upload → Marketplace → public, every job re-runnable; a crowded preview's timeout says so; listed distribution names are taken as WSL reports them; the kill behaviour was OBSERVED (nothing survives — no daemon-side cleanup); E5.S0's acceptance corrected. ***Install daemon*** — the command in ONE module (`install/installCommand.ts`), the flow with injected collaborators (`install/installDaemon.ts`), the modal and the terminal real outside Test mode and RECORDERS in it (`install/installUi.ts` — a real terminal would start the real `wsl.exe`, outside the tripwire's reach); the distribution resolved by a new `WslCareClient.terminalTarget()` (pattern, then `--list`; no running check — the person asked for a shell there), kept in the client so it remains the one module spelling `wsl.exe` argv; the panel's first button when `status` answers *daemon not installed* (not for an unsupported distro); the bundle scan needed NO exception (`sudo` is not in the forbidden set) — instead a new scan holds that `sudo` appears in the bundle only in the command's template part and its prerequisite line, and a structural scan that only `installCommand.ts` spells the installer, its URL or `sudo`; the Marketplace README carries the very command (a test). **The package** — `@vscode/vsce` **4.0.0** (engines node ≥ 22: CI already runs 22.x); the bundle moved from the esbuild CLI to `scripts/bundle.mjs` (the same options, plus the build stamp `WSL_CARE_BUILD_STAMP` := `"wsl-care-build <version>"`, read back by the host suite's test API); `.vscodeignore` an allowlist; `vsix-files.txt` (what `vsce ls` prints; vsce renames README/CHANGELOG/LICENSE inside the `.vsix`); the checks in `src/test/support/vsixCheck.ts` + a dependency-free ZIP reader (`zipFile.ts`), run by `scripts/check-vsix.mjs` on both CI legs (job names unchanged, so `branch-main.json` and `ReleaseConfigTests`' required checks untouched); the drive-path rule also catches `X:/`; JavaScript escapes are collapsed before the path rules (`"System32\\wsl.exe"` is not a `\\wsl` share — the first run flagged it); names are looked for in the bundle's string LITERALS only (the CI account `runner` is also an identifier in the bundle — measured: whole-text scanning would fail the Linux leg); the build machine's user name is derived at check time (`os.userInfo()`, `USERNAME`, `USER`, the home folder), `vsix-denylist.txt` holds repository-internal fragments the owner can extend; a denied word is never printed. **Metadata** — `media/icon.png` 256 px DRAWN by `src/test/support/iconPng.ts` (`npm run icon:make`; held to its recipe by pixels with a one-sample tolerance, because `Math.atan2` and zlib may differ between Node releases), `pricing` / `bugs` / `homepage` / `galleryBanner` / `categories` (`Other`, `Visualization`) / `keywords`; **no screenshots** — no synthetic-fixture render pipeline exists here, and the README ships without images (an E5 live-gate item); the publisher stays `publisher-tbd`, and the manifest test now accepts any Marketplace id (the live gate's change is one line) while `check-vsix --release` and the guard refuse the placeholder; the host suite reads `<publisher>.<name>` from the manifest. **The pipeline** — `release-extension.yml` with FIVE jobs (guard, build, github-draft, publish-marketplace, github-public — §15h #0 split the GitHub job in two, so two jobs hold `contents: write`); the guard is a script (`.github/scripts/release-extension-guard.sh`) reading `MIN_DAEMON_FOR_RENDER` from `handshake.ts` (a C# test holds that line's shape), asking `gh api …/releases/tags/daemon-v<MIN>` with the job's read-only token, and requiring POST_DEPLOY's stamp in the shape `Last verified: <date> · <target> · daemon <x.y.z>` with x.y.z ≥ MIN (not exactly MIN, or the next daemon release would block every extension release); the asset set has its own script (`verify-extension-assets.sh`); the build job also runs the extension-host tier under xvfb; the Marketplace job installs vsce with `npm ci --ignore-scripts` (no dependency script beside the secret) and decides the skip from `vsce show --json`, which prints `undefined` and exits 0 for an unknown extension (observed — so an unparseable answer reads "not served"); a bounded 15-minute wait for the Marketplace to serve the version; the credential is `VSCE_PAT` in the `marketplace` Environment, and **OIDC (`--azure-credential`) is recommended** — checked 2026-10-04: global Azure DevOps PATs, the only kind the Marketplace accepts, stop working on 2026-12-01 (the creation block was withdrawn; org-scoped Marketplace PATs are an open request, microsoft/vsmarketplace#2121) — documented as a one-pull-request switch; `tags-extension.json` is `tags-daemon.json` with the pattern and name changed (a test holds them equal otherwise); release-please gains `src_vs_code` with `changelog-path` and a `CHANGELOG.md` header for it to write under. **`POST_DEPLOY.md`** stays at twelve: items 1 and 6 merged (both `systemctl is-active`, one call) to make room; item 3 (manual) rewritten for the Marketplace-installed extension — the real path, no start of a stopped WSL, the Remote-WSL settings file, the preview timing; item 6 (auto) the served version == the newest tag, its GitHub `.vsix` present and attested, the installed bundle byte-equal; item 12 (auto) the credential's recorded expiry (`VSCE_PAT expires:` header line). **Not built, stated:** Sonar `c8` for the extension (E5.S1 deferred it to E5.S3; the brief for E5.S3 did not carry it) — still deferred, `sonarcloud.yml` covers the daemon only. **Not touched:** `release.yml` — its header still says the extension's leg "joins this file in E5", now stale (left for the next daemon change, the brief froze the file). **Residual risk, recorded:** the build job holds `id-token: write` while `npm ci` runs dependency install scripts (as the daemon's build runs `dotnet restore`); moving the attestation into a job of its own that only downloads and attests would narrow it — not done (the brief places the attestation in build). **Amended by §15i** (the E5 code round): that attest job now exists and the build signs nothing; the guard reads `src_vs_code/min-daemon.json`, not `handshake.ts`; no asset on a release is replaced (re-run failed jobs only); github-public compares the draft with the attested build | **Fable-level** — what ships publicly (built on Opus 5.5 while Fable is limited) |
| E6.S0 | **the daemon read contract (§15j):** `status --json` gains `actions` (this side's registry ids, in `ExecutionOrder`), `capabilities` (the ones this story delivers: `act.shownList`, `runs.show`, `running.block`, `logs.instantRange`; `act.detach` / `act.onlyStdin` / `act.stop` arrive with E6.S1), the `running` block with the M3 states (`none` / `queued` / `live` / `wedged` / `dead` / `unknown` / `unreadable`; status is unprivileged and NEVER sweeps — dead is reported, not cleaned; `queued` reads the request files E6.S1 will write, empty until then) and `lastCleanup {runId, startedAt, trigger, freedBytes, count}`; every `act` answer gains `productVersion`; A4's outcome in `act --preview --json` gains `shown` (every selected name, capped at `MaxShownVolumes`, A4 only — B1); `runs show <runId> --json` (read-only: queued / running / done with the full detail / refused / interrupted / unknown, its own `schemaVersion` 1 — M3); `RunLine.metrics` (M7); `logs` / `runs --from <RFC3339> --to <RFC3339>` beside the UTC-day periods (M7); `contracts/actions.json` + `contracts/exit-codes.json` held equal to `ActionId.All` / `ExitCode` (m3); SIGHUP registered as a cancellation (B2); `--manual` / `--timer` mutually exclusive (m2); `schemaVersion` stays 1 everywhere (m1). **Acceptance:** goldens for `status` in each running state that can be staged (none, live, wedged, dead, unreadable), `act A4 --preview --json` with 387 shown names (`shown.length == count`), `runs show` (done / interrupted / unknown) and `logs` over a UTC-midnight-crossing local day; the drift test red with one field renamed; `schemaVersion` asserted 1; the contracts held equal; SIGHUP on `act --confirm` records `interrupted` with a detail; an unprivileged `status` never writes or sweeps (the state directory made unwritable). Merges at any time (B3). **Built 2026-10-04; deviations:** `running` names the run's ids as `actions` (a list — a run holds several, a full run is `["collect"]`) beside `current`, and adds `pid` (so a wedged run outside the units can be stopped by hand, M4), `queuedAt` and `queued` (how many requests wait); the request-file format is DESIGNED here for E6.S1 to write — `{state}/requests/<runId>.json` = `{schemaVersion, runId, kind: act|collect, actions, trigger, createdAt, shown}`, only a file named `<runId>.json` and filed under its own run id counts — and `queued` is the oldest readable request while NO run holds `running.json`; a request that does not parse with nothing else in flight is `unreadable` naming it; `runs show` answers a DEAD holder of `running.json` as `interrupted` (never `running` — it will never finish) without sweeping it, answers exit 0 for every state (`unknown` included) and 4 only when the history exists and cannot be read; `RunOutcome.Refused` is added now so a history carrying E6.S1's `refused` line never becomes unparseable; `RunLine.metrics` carries the metrics the history line already records (every full run since E2.S3) — an `act` line gets none (the sparkline's points are the full runs; adding a probe to every act was not needed); the instant range answers `period.label: instants`, `from` / `to` = the UTC days the instants touch, plus `fromInstant` / `toInstant` in UTC, is refused beside `--period` or with one end missing, and allows at most 366 days; A4's `shown` comes through a new `IBoundToShownList` (A4 alone), is absent when the preview could not be read, and is capped by `ShownList.MaxNames` (the 10 000 moved into `WslCare.Core`, `CommandLine.MaxShownVolumes` refers to it); SIGHUP's record names the signal through `ShutdownSignals.Cause` → `CliHost.InterruptCause` → `EngineContext.InterruptCause` ("interrupted by SIGHUP (the terminal or the wsl.exe that started it went away) before every action had run"); the Windows binary's `actions` is empty (no Windows-side action yet); the contracts are `{schemaVersion, description, ids, executionOrder}` and `{schemaVersion, description, codes: [{name (camelCase), code}]}`, written by `ContractFilesTests` under `WSL_CARE_WRITE_GOLDENS=1`; the running-state goldens are `status` over an otherwise EMPTY sandbox (the running block is what differs; the main `status.json` is `none` over the captured tree), `queued` was added to the staged states, `unknown` is not staged (in-process only), and `runs-local-day.json` was written beside `logs-local-day.json` (the `metrics` live on `runs`' lines); beyond the story: `smoke-daemon.sh` part 3 also checks the running block, the capabilities and `runs show` of a stranger on the published binary. The two own reviews (§15j M10) are the coordinator's next step. | **Opus** (Fable intended, its limit spent — §15j M10), then two independent own reviews (security / confused deputy; durable state / crash paths) |
| E6.S1 | **daemon detach, stdin, stop, units (§15j B2, M2, M4, M9, m3, m6):** `act` / `collect --detach`, `--only -` (a 1 MiB cap and a 10 s ceiling), the request files `{state}/requests/<runId>.json`, `act --request <runId>`, the outcome `refused`, the ownership-checked request sweep, `wsl-care-act@.service`, `act --stop <runId>`, the new `CommandTemplate`s (`systemctl start --no-block` / `stop` with a closed unit slot) in the property test, exit 69, the capabilities `act.detach` / `act.onlyStdin` / `act.stop`, `install.sh` / the archive / `ShippedFilesTests`. **Acceptance:** scenario flows with a fake `systemctl` (accepted → `act --request` runs and records under the pre-allocated runId; the timer holding the lock → the request records `refused` and removes itself; a stale request + an inactive unit → swept `interrupted`; no systemd → refusal 69, never a synchronous fallback); stdin (10 000 names pass; 10 001 / 1 MiB + 1 / a bad line — refused by its number, never echoed — / no EOF in 10 s all refuse); the property test with hostile unit names (only `wsl-care-act@<runId>` / `wsl-care.service` allowed); the live measurements recorded in the facts note (AOT death with `wsl.exe` without `--detach`, the stdin relay rows, `systemctl start` from `wsl.exe -u root`, the unit surviving the kill of the calling `wsl.exe`) — as E6 daemon live-gate items where they need root. Merges at any time (B3). **Amended by §15k:** FIRST the reload-survival measurement (#5 + #13, recorded in `research/` before E6.S2 / E6.S3 rely on it); per-command ceilings, no whole-run timeout, a never-exiting fake docker → a record and the lock released (#0); the request written atomically (`O_EXCL`), a failed start removes it with a distinct exit code (#1); the sweep checks history first and treats a queued job as pending, a missing request is a no-op with a named code (#2); `SuccessExitStatus` for the recorded refusals, `reset-failed` of its own instance, ≤ 32 requests of ≤ 1 MiB, N = 15 min (#8 + #17); equal hardening sets in `ShippedFilesTests` (#9); `requests/` root:root 0755, files 0644 `O_EXCL`, `act --request` through the hardened reader with planted non-root / group-writable requests refused (#14); the sweep at every root `collect` and `act --request` (#15); `install.sh` atomic rename, a 10-minute bounded wait then refusal, a queued request surviving an upgrade (#16); `TimeoutStopSec=90` and a stop that ignores SIGTERM swept `interrupted` (#18); and from the E6.S0 review round: write `running.json` BEFORE removing the request; a `collect` cancelled mid-measurement must leave a record. **Built 2026-10-04; deviations:** the distro-survival measurement (§15k #5 + #13) needs root and every `wsl.exe` closed, so it is NOT done here — it is the FIRST step of the E6 daemon live gate, recorded in `research/` before E6.S2 / E6.S3 rely on the reload promise; the stdin relay was measured by the coordinator (§15k #6, facts note row 20); `reset-failed` is not a command of ours: the template carries `CollectMode=inactive-or-failed`, systemd's own way to unload an ended instance, failed or not (the property is observed at the live gate); `SuccessExitStatus=3 75 76 78 79 80` — 78 (observe-only, recorded `refused` at `--request` time) joins the list the plan named; the exit codes are 69 no systemd, 71 the unit would not start (its request removed), 73 the request budget full, 80 no request names the run; the budget (32) is checked BEFORE the running state, so a full folder answers 73 and not the queued state's 75 — and a detach refuses while a run is QUEUED as well as live (one root operation at a time); the answer of a detach / stop is its own `HandOffReport` (`schemaVersion` 1: `result` accepted / stopping, `kind`, `runId`, `unit`, `productVersion`; golden `act-detach-accepted.json`); the run id is the detaching process's and the unit's run keeps it (`ActRequest.RunId`, `CollectContext.RunId`); the request is written through a new `IFileSystem.CreateFileExclusively` (temporary sibling, 0644, then `link(2)` — a non-replacing move on Windows) into a folder whose 0755 is SET after `mkdir` — the first implementation let the umask make it 0700, found by `DetachFlows` under `umask 077`; the sweep runs under the lock in `collect` and, through `ActRequest.UnderLock`, in `act --request` — and `act --request` itself checks the history first for its OWN run (a run that recorded itself and died before removing its request is removed, exit 80, never run twice); `act --stop` writes a stop marker `{state}/stops/<runId>` that `RunningSweep` turns into the SIGKILL reason and the request sweep expires; uninstall stops every loaded `wsl-care-act@*.service` before removing the template; the never-exiting child of §15k #0 is a `journalctl --disk-usage` that sleeps (a 15 s ceiling) rather than a docker listing (30 s) — the runner's ceiling is the same for every tool; `CommandPolicyPropertyTests` caps each kind of violation separately (the new templates shifted the seeded sequence), and the hostile unit names are their own list (`HostileInputs.HostileUnitNames`); `POST_DEPLOY.md` item 7 now also checks the template's properties as loaded (the file's cap of 12 items: folded into the installed-units item, not a 13th). The collect-cancelled-during-measurement record of the durable review is built (`CollectRun.RecordCutOff`). **coai E6 code round (§15m, 2026-10-05):** verdict proceed, 9 findings fixed (typed run ids in the requests, the install guard decides by state and fails closed, progress and the manual escape, a request of an earlier boot reported dead, status reads only the oldest request). **E6.S1 review round (§15l, 2026-10-04):** all 11 findings fixed — the grace is now 60 s on the monotonic clock (not §15k #15's 15 minutes), `--detach` sweeps under the lock, requests carry `bootId` / `createdMonotonicMs`, nothing is created group / world writable, `act --stop` matches whole cgroup paths, the installer's wait fails closed, a timed-out start answers `unknown`. | **Opus** (Fable intended — §15j M10), then two independent own reviews |
| E6.S2 | **the extension's root boundary (§15j M1, M2, M5, m1, m4, m5, m9, m11):** `src/root/rootCall.ts` with the closed `ROOT_OPS` (preview, confirm + detach, stop, collect + detach, the root check); runner stdin; ids = the compiled registry ∩ `status.actions` ∩ `capabilities`; A4's names from the held preview, each validated 64-hex; the M1 scans; the fake's root shapes; `minDaemonForActions`; the B3 release check. **Acceptance:** the exact argv per op asserted; `--timer` / `--user` / `config` never; every scan red with a planted instance; the fake refuses a synchronous confirm, stdin outside `--only -`, an id outside the intersection; a daemon below the actions minimum or without the capabilities shows "Update daemon" and starts no root call; the tripwire still blocks the real `wsl.exe`. **Merges only after `extension-v0.1.0` is tagged (B3).** **Amended by §15k:** the B3 check keyed on TAGS with 0.1.0 / 0.2.0 fixtures (#7); the bundle scan on flags and argv shapes, bare words guarded by the import graph and the root region's exact literals (#10); a detach timeout = outcome unknown, followed through `status.running` (#3); `shownTruncated` honoured and a golden past 10 000 (#11); every host call's timeout stated, the 10 s stdin ceiling shown as a refusal with a retry (#19). **Built 2026-10-04 (branch `feat/wc-e6-cleanup-logs`, stacked on E6.S0 + E6.S1); deviations:** the op that runs `collect --detach` is named `fullCheck` (*Run full check now*), so `collect` stays a word only `rootCall.ts` spells; the controller (`root/cleanupController.ts`) is wired in `extension.ts` and reachable through the Test-mode API, but no button or command calls it (E6.S3); at source level the exact literals `act` / `collect` ARE scanned (stricter than #10) while the bare `-` is not (the client spells it in `startsWith('-')`), held by the import graph and the bundle's exact root set as #10 says; the detach ceiling (90 s) is DERIVED from the daemon's own ceilings (stdin 10 s + one sweep `systemctl show` 15 s + `systemctl start --no-block` 30 s + a second look 15 s + the relay), not a measured cold-boot time — a root call only goes to a RUNNING distribution, and the real time needs `wsl.exe -u root`, an E6 live-gate item; #19's ceilings are stated for status (20 s, unchanged), preview (330 s), the root preview (330 s), the detaches (90 s), the stop (150 s) and the root check (20 s) — `runs show` is a read verb E6.S3 adds and states there; B3 lives in two places, the guard (the root MODULE in the checkout + the tags) and check-vsix `--root-allowed` (the root MARKER in the bundle), and never reads `.release-please-manifest.json`; `minDaemonForActions` = 0.1.0, B3's first case (it assumes E6.S0 + E6.S1 merge before the owner cuts `daemon-v0.1.0`; if 0.1.0 is cut first, it and `min-daemon.json` become 0.2.0); the capabilities each op needs: `running.block` + `runs.show` always, `act.shownList` (preview), `act.detach` (confirm, full check), `act.onlyStdin` (a confirm of A4), `act.stop` (stop) — `logs.instantRange` is E6.S4's; the root check caches a SUCCESS per session per distribution and asks again after a refusal; a second root op in flight in the same distribution is REFUSED (`rootBusy`), not queued; an unknown detach is followed every 4 s for 60 s and ends `acceptedObserved` or `outcomeUnknown` (with the run id when there is one), an `accepted` without a run id counts as unknown, and a stop's unknown is reported without following (its run is live by definition); exit 2 on a confirm that piped a list is `shownListRefused` (nothing started, a retry offered) by the code alone, no message matching; the panel's sanitiser became `src/text/safeText.ts`, shared with the enum reader and the root words; the hand-off's `kind` is not read (the op says it); `root/rootFailureText.ts` is not yet in the bundle (nothing shipped imports it before E6.S3), so the bundle scan reads its allowed prose from the source; the 5-minute preview expiry (m8) stays E6.S3's — `HeldPreview.takenAtMs` is recorded for it; the strict fake's registry is `contracts/actions.json` itself; the case past 10 000 is the built CLI over 10 001 synthetic volumes asserted IN MEMORY by `GoldenContractTests` (`ReadContractScenes.CappedVolumes`; a 765 KB checked-in golden of it was dropped by the review round) — the daemon code is untouched; beyond the story: the root flows join the derived extension catalogue (`root <op>` rows), and a mutation found one guarantee without a test (an `accepted` answer without a run id), which got its test. The risk consultation §15k orders for E6.S2 and its coai code round are the coordinator's (the coai server did not connect in this session). **E6.S2 review round (two own reviews standing in for coai — security; durable state / correctness — 2026-10-04): every finding ACCEPTED and fixed red → green → red** (`research/module_tests.md`, *The E6.S2 review round*): **S1** (Important) B3 allowed root once the `extension-v0.1.0` TAG existed — a refused 0.1.0 carrying the root module stays tagged — → root only when that tag exists, its OWN tree carries no root module (`git cat-file -e`), it is a PUBLISHED non-draft release (asked last) and the release is above it; **S2** (hardening) `WSLENV` reached root calls → `ProcessRequest.withoutEnv`, every root request takes it out, the fake refuses one that carries it; **M1** every non-zero detach exit was certain → only 1, 2, 69, 71, 73, 75–80 and −1 (`CERTAIN_DETACH_EXITS`), the rest (70, 130, 137, 143, 3, 4) unknown and followed; **M2** a detach with no run id adopted ANY queued / live run → only this distribution's run with `trigger: manual` and exactly the asked actions (`["collect"]` for a full check), `runningOf` reads `trigger` / `actions`, another run is reported as `outcomeUnknown.otherRun` (a field, chosen over an `ours` flag on `acceptedObserved`: an adopted run is always the provable one); **M3** a known run id was held back for the whole follow → `outcomeUnknown{runId}` at once, E6.S3's durable poll follows it (one follower), the controller follows only without a run id; **M4** a held preview could be confirmed twice → consumed once the confirm's call went out, kept when nothing started (a refusal before the call, a launcher that never started, `shownListRefused`); **L1** two launcher readings → one `launchFailure` in `client/failures.ts` (the timeout's reading a parameter; the client now names a signal); **L2** a timed-out / unstartable root check read "needs root" → only an exit (not 0, not −1) is `rootRefused`, none cached; **L3** the follow's status reads ignored the distribution → compared, `outcomeUnknown.followed {polls, answered}` and the reason say when status never answered, the real bound (~60 s + one interval + one poll) stated; **tests** the in-flight slot freed in `finally` on a rejecting runner and status, 76 with its running block (both green at once, their mutations red); **golden** the 765 KB `act-a4-preview-capped.json` DROPPED — the capped scene asserted in memory, the host case generated. Found while fixing: the structure scan caught the controller spelling `collect` → `rootCall.ts` exports `FULL_CHECK_ACTIONS`. **coai E6.S2 rounds (2026-10-05):** PLAN round **#0 accepted** — the 10 000-name cap / 650 000-byte stdin relay cites its measurement, already recorded as row 20 of [2026-10-03_wsl_exe_facts.md](../research/2026-10-03_wsl_exe_facts.md) (Node 24 spawning `%SystemRoot%\System32\wsl.exe -d Ubuntu --cd / --exec /usr/bin/wc -c`, 10 000 × 64-hex + LF written then `end()`: 650000 received, exit 0, ~250 ms, identical with `WSL_UTF8=1`), now linked from `process/runner.ts`, `root/rootIds.ts` and `root/rootCall.ts`; the re-measure with `-u root` stays an E6 live-gate item; **#3 accepted** — the terminal state after a no-run-id follow expires is `outcomeUnknown` with no run id, resolved by E6.S3 on its next load from the daemon's records (`runs` / `lastCleanup` over the window since the confirm, `trigger: manual` + the confirmed ids) — written into `cleanupController.ts`'s header and the E6.S3 row; **#1 rejected** (the build order and DoD exist in §11 / §12 / §16), **#2 rejected** (distribution validation is specified — §15g M3, §15h #4 — and built in E5.S1). CODE round **#0 accepted (Major)** — nothing enforced `MIN_DAEMON_FOR_ACTIONS` ≥ `MIN_DAEMON_FOR_RENDER`: the release guard now refuses an artefact whose actions minimum is below the render minimum (`version_at_least`), and `minDaemon.test.ts` asserts it with the extension's own comparison (`handshake.versionAtLeast`, built on the render check's `older`). **Coverage, stated:** the coai code round had 2 of 8 reviewers answering; both SecurityReliability reviewers timed out — the root boundary's security coverage is the two own reviews above. | **Opus** (Fable intended — §15j M10), then two independent own reviews |
| E6.S3 | **buttons, durable cleanup state, Last cleanup, Run full check now (§15j M4, M6, M7, M8, M9, m3, m8, m9):** **From E6.S2 (coai plan round #3):** a detach the controller could not follow to a run (`outcomeUnknown` with NO run id, after its bound) is RESOLVED here on the next load from the daemon's own records — `runs --from <the confirm's instant> --to <now>` and `status.lastCleanup` over the window since the confirm, matching `trigger: manual` and exactly the confirmed ids (`["collect"]` for a full check): one match is the run (then followed as any run id), none after the request grace means it never ran, several stay "unknown" with the candidates shown; the confirm's instant and ids are persisted in `globalState` for exactly this; and an `outcomeUnknown` WITH a run id is followed by this story's poll (the controller hands it back at once, E6.S2 review M3). the webview's `{clean, rowIds}` / `cleanSelected` / `runFullCheck` / `stop` (an index into host data); host modals (sanitised, the second confirmation naming the setting, a 5-minute expiry, "re-checked at run time"); following `running` and `runs show`; the in-flight poll (M6); *Last cleanup* from `lastCleanup` with "Docker after" from re-read preview totals and their time; distinct exit-code texts. **Acceptance:** the reload scenario (start A4, reload, the panel shows "Cleaning… A4" from `running`, then the result from `runs show`; a dead run shows `interrupted`, never sticks; a refused request shows its reason); a 387-volume A4 pipes all 387 names; *Clean selected* = ONE `act` call; page scripts RUN over the goldens. **Merges only after `extension-v0.1.0` is tagged (B3).** **Amended by §15k:** run ids persisted in `globalState` until a terminal answer, `unknown` terminal, a 30-minute poll ceiling (#4); a detach timeout shown as the run it started (#3); the preview's age re-checked after the last modal, with a frozen clock (#12); the close-VS-Code case if #5 + #13's measurement narrows the promise. **Built 2026-10-05 (branch `feat/wc-e6-cleanup-logs`, on E6.S2; the daemon untouched); deviations:** the page's closed set gains `clean` AND `cleanSelected`, both carrying `rowIds` of the compiled `ROW_IDS` (a message each so the host can word the modal; either is ONE preview and ONE `act` call), `runFullCheck` bare and `stop` an index ≤ 3; `ROW_IDS` is the eight rows `preview --all` reports (A4, A5, A5Testcontainers, A6, A6Unused, A7, A8, A9) — §7.2's "further rows" A1–A3 / A10–A12 have no preview row and so no button (open); the run reads `runs show` / `runs --from --to` are client verbs of their own (`verbs.ts` `RUN_READ_NAMES`, unprivileged, 20 s each, the run id and `yyyy-MM-ddTHH:mm:ssZ` instants checked before a spawn, exit 4 with an answer read as an answer); the coai #3 resolution reads `status.running` (a queued / live run of `trigger: manual` with exactly the confirmed actions is adopted) and, after a 90 s grace (the daemon's 60 s + a margin), `runs` over the window — NOT `status.lastCleanup`, which carries no action ids and so cannot match "exactly the confirmed ids" (every cleanup it names is also a `runs` line); a full check's HISTORY line records no action (`CollectRun.TimerPassAsync` returns before the engine for any trigger but the timer) while its running block names `["collect"]`, so a full check is matched as a manual line with no actions; the 30-minute ceiling is on the wall clock from the confirm and applies only to an entry that did not end when evaluated (a window closed for hours still gets its answer first); an entry another window started is followed only while this window is focused; the journal is `globalState` key `wslCare.cleanup.journal.v1`, read as untrusted, at most 32 entries; "Docker after" is the preview's reclaimable total with the time it was read, the panel round asked ONCE after each terminal answer (3 runs), labelled "Docker now" when the preview predates the newest cleanup; *Run full check now* has no modal (a full run that is not the timer's does not act); *Stop* has a modal, is offered only for a wedged run of `trigger` `manual` / `timer` on a daemon advertising `act.stop` (else text with its pid), and a run the journal already follows is not followed twice; *Last cleanup* is a field-map row read from `status.lastCleanup` (the arriving row is gone). **Found and fixed on the way:** an unreadable preview showed "not read — " with no reason (the action's empty reason hid the preview's — `rootAnswers.ts`); a poll armed by the confirm fired after the run had ended and asked one status with nothing in flight (found by the extension-host tier; `fired()` re-checks); the Test-mode recorder lived in the `vscode` module (moved to `cleanRecorder.ts`). **Not done:** the close-VS-Code case (§15k #5 + #13's measurement is the E6 daemon live gate's first step and is not taken); the webview click and the native modal are not exercised inside VS Code (no API — the host tier drives the host through the test API, the page is RUN in the harness). **M6:** the poll is implemented as specified and its churn MEASURED (`runFollower.test.ts`: 15 status runs a minute of a run + 1 `runs show`; the ceiling bounds a wedged run at 451) and recorded in [2026-10-04_extension_poll_churn.md](../research/2026-10-04_extension_poll_churn.md) § E6.S3 — the decision stays the OWNER's, no family-rule exception added. One full `npm test` run had the untouched runner timing test red once (passes alone and in five later full runs) — recorded in `research/module_tests.md` as open. **E6.S3 review round (2026-10-05):** the coai code round (proceed, 7 of 8 reviewers, 26 findings — 25 accepted, #11 rejected) and two own reviews (security A1–A5, durable state B1–B5): every finding and its disposition in [§15n](#15n-e6s3-review-round-coai-code-round--two-own-reviews-2026-10-05); the M6 churn of a wedged run is now 451 status runs + ONE record read before the ceiling ends it (B1). **§15o, the extension half (2026-10-05, on this branch; the daemon half is plan §15o on `fix/wc-full-check-line-names-collect`):** the follower matches a full check KIND FIRST (`runMatching.ts` `isFullCheckLine`) — a line with `kind` is decided by it alone (`collect` is the full check whatever its actions and reason, `act` never is); a line without one, or with a kind this build does not know (`runAnswers.ts` reads `kind` strictly: exactly `collect` or `act`, anything else absent — §6's unknown-enum rule, never a crash), keeps the B2 rule exactly (`[]` or `["collect"]`, the three reason prefixes excluded). Additive: nothing breaks before or after the daemon ships `kind`. | Opus |
| E6.S4 | **the Logs page (§15j M3, M7, M8):** a `WebviewPanel` with §7.2's CSP rules; periods This run / Today / Yesterday / a date / a range (webview state or memento); host-built argv for `logs` / `runs` / `runs show`; local days through the instant range; totals / runs / max-min / the run list from the answers only; lazy object lists through `runs show`; "arrives in E#" for `vmmemWSL`; the retention clamp. **Acceptance:** each period → its exact argv; the selection survives a reload; page tests assert every block equals the JSON (no arithmetic in the page); `detailsNotRead` shown; a malicious period / runId from the webview starts no process. **Merges only after `extension-v0.1.0` is tagged (B3).** | Opus |
| E7.S0 | the config trust and contract (daemon) — §15q R1 | **Fable** if its limit has reset, else Opus + two own reviews (§15q) |
| E7.S1 | agent catalogue, discovery, the daily walk, `agents list` (daemon) — §15q R2 | Opus + two own reviews |
| E7.S2 | `aiAgents.extra`, `agents probe` (daemon) — §15q R2 | Opus + two own reviews |
| E7.S3 | settings ↔ config (extension) | Opus + two own reviews |
| E7.S4 | AI-agents section, Add CLI path (extension, WSL side) | Opus + two own reviews |
| E7.S5 | bundling `wsl-care.exe`, a `--target win32-x64` `.vsix` (moved here from E5 by §15f #5, #13), Windows numbers in Memory/Disk, the Windows agents | Opus + one own review |

| E6.S4 | **the Logs page (§15j M3, M7, M8):** a `WebviewPanel` with §7.2's CSP rules; periods This run / Today / Yesterday / a date / a range (webview state or memento); host-built argv for `logs` / `runs` / `runs show`; local days through the instant range; totals / runs / max-min / the run list from the answers only; lazy object lists through `runs show`; "arrives in E#" for `vmmemWSL`; the retention clamp. **Acceptance:** each period → its exact argv; the selection survives a reload; page tests assert every block equals the JSON (no arithmetic in the page); `detailsNotRead` shown; a malicious period / runId from the webview starts no process. **Merges only after `extension-v0.1.0` is tagged (B3).** **Built 2026-10-05 as files and tests** (branch `feat/wc-e6-cleanup-logs`, PR #12, still DO NOT MERGE): `src/logsPage/` (`period.ts`, `logsMessages.ts`, `logsController.ts`, `logsViewModel.ts` + `logsBlocks.ts` + `runDetail.ts`, `logsPanel.ts`), `media/logs.js`, the client's third run read `logs --from <instant> --to <instant> --json` (unprivileged, 20 s, §15k #19), `wslCare.openLogs` in the panel title and *Logs* beside *Last cleanup* (`openRunLogs`), the shared format module `src/text/format.ts`; acceptance held by `period.test.ts`, `logsController.test.ts` and `logsPage.test.ts` (each period → its exact argv; the selection survives a reload; every block equals the JSON, no arithmetic; `detailsNotRead` shown; a malicious period / run id / extra field starts no process) and the extension-host tier on 1.85.0 + stable — the red record in `research/module_tests.md` § *What each E6.S4 guarantee rests on*. **Deviations:** the module folder is `src/logsPage/` (the repository's `.gitignore` ignores every `logs/`); the selection lives in `globalState` (`wslCare.logs.period.v1`, the host's — written BEFORE it is read), not in the webview's state, and a page open at a reload is restored by a serializer (the manifest gains `onWebviewPanel:wslCare.logs`); *This run* is `runs show` of the run `status.lastCleanup` names (kept with its id across a reload) — the *Last cleanup* section has ONE *Logs* button, for that run (its results list carries none); **the MemAvailable sparkline is a TREND TABLE** of the runs' `RunLine.metrics` (MemAvailable %, swap used) — a drawn sparkline would bucket the values (arithmetic over the answer) and needs SVG or canvas the strict CSP and harness do not model, so none is drawn (the choice the brief allowed); an expanded object shows type, name, size and note — the daemon's `ActionItem` is `{kind, name, bytes, note}`, so image and age are in the note, not columns; `logs`' own `cleanups` list is not rendered (the run list and `runs show` cover it); the clamp: the picker's inputs carry `min` / `max`, and the HOST clamps any real calendar day to today and the 89 days before it and says so (a non-calendar value is no message); a capability gate (`logs.instantRange` for a day, `runs.show` for a run) when `status` answered; times are shown in the machine's zone with their offset (converted at the view edge only); the Logs page shares the panel's shell (widened with `page`) and its ONE stylesheet; the page harness models one more element, `<input type="date">`. **Found while mutating:** a selection read the CURRENT period after its persist await, so a later selection made both reads ask the later window — fixed (`readPeriod(period)`). **§15o, alongside (its own commit):** the follower's full-check matching is KIND FIRST — see the E6.S3 row. | Opus |

| E6.S4 | **the Logs page (§15j M3, M7, M8):** a `WebviewPanel` with §7.2's CSP rules; periods This run / Today / Yesterday / a date / a range (webview state or memento); host-built argv for `logs` / `runs` / `runs show`; local days through the instant range; totals / runs / max-min / the run list from the answers only; lazy object lists through `runs show`; "arrives in E#" for `vmmemWSL`; the retention clamp. **Acceptance:** each period → its exact argv; the selection survives a reload; page tests assert every block equals the JSON (no arithmetic in the page); `detailsNotRead` shown; a malicious period / runId from the webview starts no process. **Merges only after `extension-v0.1.0` is tagged (B3).** **Built 2026-10-05 as files and tests** (branch `feat/wc-e6-cleanup-logs`, PR #12, still DO NOT MERGE): `src/logsPage/` (`period.ts`, `logsMessages.ts`, `logsController.ts`, `logsViewModel.ts` + `logsBlocks.ts` + `runDetail.ts`, `logsPanel.ts`), `media/logs.js`, the client's third run read `logs --from <instant> --to <instant> --json` (unprivileged, 20 s, §15k #19), `wslCare.openLogs` in the panel title and *Logs* beside *Last cleanup* (`openRunLogs`), the shared format module `src/text/format.ts`; acceptance held by `period.test.ts`, `logsController.test.ts` and `logsPage.test.ts` (each period → its exact argv; the selection survives a reload; every block equals the JSON, no arithmetic; `detailsNotRead` shown; a malicious period / run id / extra field starts no process) and the extension-host tier on 1.85.0 + stable — the red record in `research/module_tests.md` § *What each E6.S4 guarantee rests on*. **Deviations:** the module folder is `src/logsPage/` (the repository's `.gitignore` ignores every `logs/`); the selection lives in `globalState` (`wslCare.logs.period.v1`, the host's — written BEFORE it is read), not in the webview's state, and a page open at a reload is restored by a serializer (the manifest gains `onWebviewPanel:wslCare.logs`); *This run* is `runs show` of the run `status.lastCleanup` names (kept with its id across a reload) — the *Last cleanup* section has ONE *Logs* button, for that run (its results list carries none); **the MemAvailable sparkline is a TREND TABLE** of the runs' `RunLine.metrics` (MemAvailable %, swap used) — a drawn sparkline would bucket the values (arithmetic over the answer) and needs SVG or canvas the strict CSP and harness do not model, so none is drawn (the choice the brief allowed); an expanded object shows type, name, size and note — the daemon's `ActionItem` is `{kind, name, bytes, note}`, so image and age are in the note, not columns; `logs`' own `cleanups` list is not rendered (the run list and `runs show` cover it); the clamp: the picker's inputs carry `min` / `max`, and the HOST clamps any real calendar day to today and the 89 days before it and says so (a non-calendar value is no message); a capability gate (`logs.instantRange` for a day, `runs.show` for a run) when `status` answered; times are shown in the machine's zone with their offset (converted at the view edge only); the Logs page shares the panel's shell (widened with `page`) and its ONE stylesheet; the page harness models one more element, `<input type="date">`. **Found while mutating:** a selection read the CURRENT period after its persist await, so a later selection made both reads ask the later window — fixed (`readPeriod(period)`). **§15o, alongside (its own commit):** the follower's full-check matching is KIND FIRST — see the E6.S3 row. **E6.S4 review round (2026-10-05):** the coai code round (proceed, all 4 reviewers; K1–K3 accepted, R1–R3 rejected — their premise was a 15-minute timer, the daemon's is 4-hourly, under ~1 000 runs in 90 days), the own security review (no findings; its note folded into K3) and the own correctness review (C1–C7, all fixed: the history-unreadable `problem` shown as such, the window kept with its answers, three reasons for a greyed *This run* and a re-post on `status`, the picker kept across renders, `ready` without a re-read, a selection begun before its await, an act line never of kind `collect`) — every finding and its disposition in [§15p](#15p-e6s4-review-round-coai-code-round--two-own-reviews-2026-10-05). | Opus |
| E7.S1 | agent catalogue and discovery, `agents list/probe` | Opus |
| E7.S2 | settings ↔ config | Opus |
| E7.S3 | AI-agents section, Add CLI path, Windows numbers in Memory/Disk; bundling `wsl-care.exe` and a `--target win32-x64` `.vsix` (moved here from E5 by §15f #5, #13) | Opus |
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

**The prototype and the Marketplace** (user decision 2026-10-02): at the E5 live gate — `daemon-v0.1.0` and
`extension-v0.1.0` published, the daemon release stamped by the E4 live gate first. Read-only on purpose, and with no
root call path in the package at all (§15f #5): the root boundary gets its own Fable story and gate round (E6), behind
the daemon contract of E6.S0, before any button reaches the public. Each later extension epic ends with its release.

**Risk list for the gate** (cadence groups {E1–E3}, {E4–E6}, {E7–E9}, {E10–E12}, {E13}): E3.S1+S2 (the
only thing between the timer and irreversible Docker deletion), E9.S1+S2 (moving the owner's AI
sessions), E6.S1 (a detached root run started from a request file, §15j B2), E6.S2 (root through an argv allowlist), E12.S2 (a user-writable request folder read by a
highest-privilege task), E12.S1 (bulk TEMP deletion), E4.S1+S2 (`curl | sh` as root, attestations,
credentials).

**Order and the kit.** E1→E4 make the daemon releasable first, because the extension is a view over the
CLI contract. Only E8 imports `@oleksandrdubyna88/vscode-webview-kit`; nothing depends on E8, so it can
slide after E13 if the kit is late. The archive (E9–E10) precedes Windows (E11–E13) because Claude
already deletes sessions at day 30.

## 17. Live findings after the first install (2026-10-04)

The daemon was installed into WSL `Ubuntu` and its timer ran `wsl-care collect --timer` from `wsl-care.service`. Three
findings; #1 is fixed by the branch `fix/wc-powershell-under-systemd`, #2 and #3 are open follow-ups. Each row OVERRIDES
the section it names.

| # | Finding | Decision | Landed in |
|---|---|---|---|
| 1 | **Under the timer the Windows clock probe never ran** (§4.5, §15b #5, A16): `clock.drift` unknown and A16 refused, both "powershell.exe could not be started: not installed, or not on PATH (powershell.exe was not found on PATH (5 directories searched, executable files only))". systemd gives a service the PATH `/usr/local/sbin:/usr/local/bin:/usr/sbin:/usr/bin:/snap/bin`; WSL appends the Windows folders only for interactive and login sessions, so `ExecutableResolver` could never find `powershell.exe` — on every machine. Started by its ABSOLUTE path from a transient `systemd-run --wait --pipe -p NoNewPrivileges=yes` unit the probe ran (exit 0): interop works under the service, only the lookup did not | **Fixed** (branch `fix/wc-powershell-under-systemd`, shape after the coai plan round and an own security review — §17a). A closed fallback in the resolver, inside the distro only: a name in `WindowsSystemDrive.Programs` (just `powershell.exe` → `Windows/System32/WindowsPowerShell/v1.0`) that PATH does not hold is looked for in that one folder of the system drive, under a 5 s ceiling, after four questions — where `C:` is mounted (`/proc/self/mountinfo`, rule in §17a A), whether WSL interop is enabled, whether only root can change the mount point's ancestors, and whether the file passes the checks of `SystemDriveFiles`. The system drive is ASSUMED to be `C:`, documented: nothing a service can read names it, and searching every drive would let a folder a Windows user may create on a data drive be started by the root daemon. PATH still wins; the never-list and the policy are unchanged and still judge the bare name; the launcher starts the resolved path as found and reports it (`CommandOutcome.StartedFrom`). Every guarantee was shown red before green (`research/module_tests.md`) | `ExecutableResolver`, `WindowsSystemDrive`, `SystemDriveFiles`, `SystemDriveLookup`, `RegularFiles`, `ProcessCommandRunner`, `ActionCommands`, their tests, `ServicePathContractTests`, `architecture.md` *Which file a tool name means* |
| 2 | **FOLLOW-UP, not fixed:** the daily folder walk measures `~/git/_wt` twice — `git-worktrees` walks it, and `git-build-output` (root `~/git`, `bin` / `obj`, skipping `node_modules` / `.git`) walks into it again (`FolderSizes.cs`). On the owner's machine `_wt` hit the 2-minute per-folder ceiling at about 1.9 M entries, and the first full `collect` took 3.5 min | **Open — decide** whether `git-build-output` skips `_wt` (its build output would then be counted under `git-worktrees` only, or not at all), and whether a folder cut by the ceiling should show as a partial figure on the Logs page | — |
| 3 | **FOLLOW-UP, not fixed:** the run detail (`runs/<day>/<runId>.json`) carries per action only id / count / freedBytes / status; the sentence saying WHY an action was skipped (e.g. "trigger not reached: the build cache holds 0.98 GiB; the trigger is above 20 GiB") is only in the journal, so the Logs page (§7, §14) cannot show it | **Open — decide** whether the action entry gains a `reason` field (a schema change: `schemaVersion`, the golden contracts and the extension's field map move together) | — |

### 17a. Review of #1 — coai plan round (2 reviewers, verdict `good_enough`) and an own security review (2026-10-04)

Each row OVERRIDES the description in #1 above where they differ. Rejected: the plan-format nit (the scope text was the
pull request's statement; §17 is the plan record).

| # | Finding | Decision | Landed in |
|---|---|---|---|
| A | (coai, Major, two reviewers) mount-table shapes: decode the octal escapes; take WSL 2 `9p`, WSL 1 `drvfs` and virtiofs; several entries only when they agree | **Accepted, with own #1/#2.** The rule reads `/proc/self/mountinfo` (it carries each mount's root and device): root `/`, an absolute mount point, and `9p` + `aname=drvfs` + a `path=` option naming `C:\` or `C:` (the SOURCE label is not read), or `drvfs` with the source `C:\` / `C:`. `\040` `\011` `\012` `\134` decoded. One mount point (stacked lines agree; the top one gives the device); different ones refused, naming them. Fixtures: the unedited observed C: line, WSL 1, a manual `C:` mount, a source/`path=` disagreement, an automount root with a space, stacked and two-place mounts, a bound folder, none, virtiofs | `WindowsSystemDrive`, `WindowsSystemDriveTests` |
| own 1 | (Important) with `[wsl2] virtiofs=true` WSL mounts the share by TAG and binds a child onto `/mnt/c`: no `C:\` source, no `aname=drvfs` | **Accepted — fail closed.** Not identified (not observable on this machine, which mounts over 9p); the refusal names what IS at the automount folder (`/mnt/c is a virtiofs mount of <tag> (root /C)`). Identifying it is open: it needs a virtiofs machine to observe | `WindowsSystemDrive.NoDrive` |
| own 2 | (Important) a manual `mount -t drvfs C: /mnt/c` writes `path=C:`; a line whose source says `C:\` while `path=` names another drive must not match | **Accepted** — fixtures for both | `WindowsSystemDriveTests` |
| B | (coai, Major) require WSL interop at resolve time | **Accepted.** `WSLInterop` or `WSLInterop-late` must exist and read `enabled` (observed 2026-10-04: `WSLInterop`, `enabled`, interpreter `/init`, magic `4d5a`); otherwise a refusal naming interop. The guarantee is worded as what is CHECKED (the `MZ` header, interop enabled), not what is achieved | `WindowsSystemDrive.InteropRefusal` |
| C | (coai, Minor + Major) the execute bit is no protection (drvfs reports its own mode); symlinks on every component; one bounded read from one descriptor; exec exactly the resolved path | **Accepted.** Observed: `0555` on every component from `/mnt/c/Windows` to `powershell.exe`, owner the default account. The bit stays only as exec(2)'s requirement. The protection rests on: `C:\Windows\System32` is admin-only on Windows; no symbolic link on any component below the mount point; the type, the device, the inode and the `MZ` bytes from ONE `O_NONBLOCK` descriptor, at most 2 bytes read; the launcher starts the resolved path as found, never re-resolved. Residuals recorded in `SystemDriveFiles`: a Windows administrator can replace the file (outside the confused-deputy boundary); another `MZ` binfmt handler (wine, mono); check by descriptor, start by path (milliseconds) | `SystemDriveFiles`, `RegularFiles.ReadHead` / `StatNoFollow` |
| own 3 | (Low) an ancestor that is not root's, or group/other-writable, would let its owner swap the tree; the file must be on the matched mount's device | **Accepted.** Every ancestor from `/` must be a root-owned directory nobody else may write (observed: `/` and `/mnt` root, 755); every component and the opened file on the mount's device (observed 0:159 for `/mnt/c`) | `SystemDriveFiles.MountPointRefusal`, `Problem` |
| own 4 | (Low) `File.Exists` is true for a FIFO, whose plain open blocks — before the runner's ceiling | **Accepted.** `O_NONBLOCK` open + statx on the descriptor (the existing `RegularFiles` reader, widened, not copied): a FIFO is refused at once | `RegularFiles`, `SystemDriveFilesTests` |
| D | (coai, Major) state and test through the launcher that the policy judges the bare name and the launched file is the resolved path, and logs name both | **Accepted.** The product policy allows the bare argv and refuses the absolute one; the launcher, asked for `powershell.exe`, starts exactly what the lookup returned and reports it as `StartedFrom`; an action's command record reads `powershell.exe -NoProfile … (started from <path>)`. `collect`'s own clock reading records no command line — the record exists for an action (A16) only | `ProcessCommandRunner`, `CommandOutcome`, `ActionCommands`, `SystemDriveResolverTests` |
| E | (coai, Minor) a bounded resolve | **Accepted.** The whole lookup runs under `WindowsSystemDrive.Ceiling` (5 s) and the caller's token; no answer is a named refusal (a blocked kernel read cannot be cancelled — its thread is abandoned, once per resolve) | `ExecutableResolver.Bounded` |
| F | (coai, Minor, + own) test isolation; detect WSL independently; assert the production wiring | **Accepted.** `ServicePathContractTests` runs in a non-parallel collection, restores PATH in `finally`, decides it is on WSL by the interop entry (not `/proc/version`: a Docker Desktop container shares WSL's kernel string) and then ASSERTS the drive is found; it resolves with PATH as an argument first, then through the launcher with PATH narrowed. A unit test holds `Resolve(name)` to this machine's lookup, never the PATH-only one, on every Linux leg | `ServicePathContractTests`, `SystemDriveResolverTests` |
| G | (coai, Minor) an orphaned Windows `powershell.exe` | **Accepted as a live-gate item, no code.** The probe is `-NoProfile -NonInteractive -Command` with a script that does no I/O, waits on nothing and reads no input; what can hang is PowerShell's START, which no line inside the script can bound. Live-gate item G below | — |
| H | (coai, Major) A16 goes live everywhere — its default and what it changes | **Recorded, nothing flipped.** `auto.A16` is `true` in `default.json`. A16 steps the WSL VM's clock — `chronyc makestep` while chronyd runs, else `hwclock -s` (system clock from the RTC, which inside WSL is the Hyper-V host's clock); it never touches the Windows clock and needs no Windows elevation (root in the distro, `CAP_SYS_TIME`; the unit sets no `ProtectClock=`). Gates: skipped while timesyncd/chrony report synchronised; only on a drift over `clock.maxDriftSeconds` (5 s) on two observations ≥ 5 min apart; at most once an hour and once per drift event. And the timer acts at all only when `dryRun` (default `true`) is switched off AND 7 days have passed since its first run (`DryRunWindow`). Whether `auto.A16` should default to `false` until live-gate item H is the owner's question | — |
| I | (coai, Major ×2) verify on the real unit | **Recorded:** post-merge verification step I below, stamped by whoever runs it | — |
| code round | (coai code round over PR #10: verdict `proceed`, qwen, 3 of 4 reviewers answered; the security and UX reviewers found nothing) two convention findings, cyclomatic complexity over 4 (C# doctrine §6): `WindowsSystemDrive.IsTheDrive` and `SystemDriveFiles.HeadProblem` | **Accepted, fixed as a pure refactor**: one predicate per mount type (`IsWsl2Drvfs`, `IsWsl1Drvfs`, `IsVirtiofs`) under `IsAWholeDriveAtAnAbsolutePoint` && `IsADrvfsMountOfTheDrive`; the head checks split into `HeadIdentityProblem` (same file, on the mount) and `HeadContentProblem` (execute bit, `MZ`), called in sequence. No behaviour change, so no new red: the whole suite, the 13 break-it tests among it, stays green | `WindowsSystemDrive`, `SystemDriveFiles` |
| own, found while verifying the refactor | the full core suite on Windows failed an UNRELATED test in 3 of 5 runs — `An_atomic_replace_waits_out_a_reader_that_holds_the_file_for_a_moment`, its reader released by a pool task after 200 ms, timed out at 2 s — while it passed alone (3 of 3) and on `origin/main` (3 of 3). Excluding the two tests that block a lookup on purpose made the suite clean (3 of 3): the bounded lookup ran on a THREAD-POOL thread, so a lookup blocked in the kernel held a pool thread, and the pool's slow growth delayed unrelated work | **Fixed:** the lookup runs on a thread of its own (`TaskCreationOptions.LongRunning`), so an abandoned lookup never holds the pool — in the daemon as in the suite. Full core suite 4 of 4 clean after it | `ExecutableResolver.Bounded` |

**Live-gate items for #1** (run on the owner's machine; each stamped with the date, the build and the outcome):

- **G** — launch the clock probe from the service context with a forced short ceiling (a transient unit, or a build whose
  ceiling is lowered for the test), let it time out, and confirm on Windows that no `powershell.exe` FROM THAT LAUNCH
  remains — identified by its own PID and command line, never by image name. *Not run yet.*
- **H** — from the real service context: `act A16 --preview` (records the live offset and the gates), then, on the owner's
  word, `act A16 --confirm` once; record the outcome and the offset after. *Not run yet.*
- **I** — after the merge: rebuild the local archive, reinstall, `systemctl start wsl-care.service`, and read the run
  record: `clock.drift` measured, A16 not refused for a missing `powershell.exe`. *Not run yet — stamped by the coordinator.*

**Retro coai round over PR #10 (2026-10-06, session ad685697)** — the owner's rule of §17c: a merged PR goes through the
gate and the consultant again. Fixed in `fix/wc-retro-pr10-system-drive`, each one red first (`research/module_tests.md`
*The PR #10 retro round*):

| # | Finding | Decision |
|---|---|---|
| plan round 2 | the Windows ACL of `C:\Windows\System32` should be checked; the abandoned lookup threads accumulate | **Rejected.** The ACL is outside the confused-deputy boundary (an administrator is already above the daemon) and drvfs shows no Windows ACL to check; the fallback is resolved a few times per short-lived run, so at most a few threads are abandoned, each with its process |
| C0 | a root bind mount or a virtiofs share could pose as the drive | **Rejected.** A mount needs root; virtiofs is taken only when its source or `path=` names the drive |
| C1 | `InteropRefusal` judged only the first registered entry: `WSLInterop` disabled beside an enabled `WSLInterop-late` was refused | **Accepted.** Empty when ANY registered entry is `enabled`; the refusal names every entry and its state |
| C2 | (the code round's third finding, in session ad685697) | **Rejected as stated**; its concrete consequence is the clock bias below, accepted from consultation b41d9220 |
| b41d9220 | the clock offset counted the system-drive lookup (up to its ceiling) as drift — A16 acts on drift | **Accepted.** The launcher stamps `CommandOutcome.StartedAt` immediately before `Process.Start`; the offset (the full run's and A16's, one function) is measured from it |
| own O1 | deleting `SameFile` or the descriptor's `OnTheMount` in `HeadIdentityProblem` left the suite green | **Accepted.** `HeadProblem` tested over the two readings; each check shown red by its deletion |
| own O2 | deleting the link arm or the uid arm of `AncestorProblem` left the suite green | **Accepted.** `/proc/self` (a root-owned link) and this account's home; each arm shown red by its deletion (WSL) |
| own O3 | the super options were unescaped: a drvfs mount of a folder named `C:\134` read as the whole drive | **Accepted.** The super options are read raw, as the kernel prints them |
| own O4 | an EIO from the head read on a failing 9p share threw out of the whole collect, wrapped in `AggregateException` | **Accepted.** The read failure is a reason; `Bounded.Run` surfaces the lookup's own exception |
| own O5 | `SystemDriveLookup.ThisMachine` froze `commands.systemDriveLookupSeconds` in a static | **Accepted.** Built per use; `NumbersAreConfigurationTests` holds it |
| own O6 | `MountInfoLine.Parse` cyclomatic complexity 5 | **Accepted.** Split (`Separator`), no behaviour change |

**Open for the owner:** (1) a mount-identity check — `statx` `STATX_MNT_ID` against the mountinfo id — would refuse a root
bind of a `C:\` subfolder over `/mnt/c`; it is root-only and hardening, not a hole. (2) Two mount points of the same device
(Docker Desktop binding `/mnt/c` again) are refused fail-closed, so on such a machine the fallback does not run.

### 17b. Retro review of PR #9 (E5) — coai codex + the consultant (2026-10-06)

E5 was re-reviewed after its merge (owner, 2026-10-06: every merged PR through the gate and the consultant). Plan round
`proceed` (1 of 1 reviewers): 2 findings, both rejected — the kill of `wsl.exe` was observed to end the daemon and its
`docker` child (§15h #3; the daemon keeps SIGHUP's default action), and the run logs are bounded by
`logging.retentionDays`. Code round `proceed` (4 of 4): the missing module document ACCEPTED (now
[module_vs_code.md](../research/module_vs_code.md), a map into `architecture.md`'s extension sections, which stay where
they are while other branches edit them); the poll churn rejected for the same retention reason (M1 stays the owner's).
The consultant found what the reviewers missed: **a `wslCare.distro` switch could show one distribution's `preview`
under the other's heading** — fixed RED first (`distroSwitch.test.ts`; the client shares a call in flight per setting
and verb, the poller stamps each round with its target and re-reads it at every boundary — the fix PR's own code round
found the first fix noticed a switch only when the next round began). **Open follow-up:** with `wslCare.distro` empty,
WSL's default changed in the middle of a panel round can still mix two distributions in that round (pre-existing; bind
the RESOLVED distribution to the round). Two hypotheses were not changed in code: *Install daemon*'s
terminal in a Remote – WSL window (now a named observation in `POST_DEPLOY.md` item 3) and a Marketplace version
published by hand with other bytes (by design skipped; `POST_DEPLOY.md` item 6 compares the installed bundle with the
attested build).

### 17c. Retro gate over the merged epics (coai codex + its consultant, 2026-10-06)

The owner (2026-10-06): every PR merged on own-agent review only, or with most gate reviewers timed out, goes through the gate
and the consultant again. One provider (codex) answered every round.

**PR #4 (E1)** — plan round session `b5f6018b` (1/1 reviewer, `proceed`, 3 findings: 1 accepted, 2 rejected — history
retention is E2.S3's `RunRetention`; the policy is tested at the seam already); code round (4/4 reviewers, `proceed`, 5
findings: 3 accepted, 2 rejected — `DeletionScope` is the typed value; the synchronous prune is one day folder per day of
small files, no measurement says otherwise); consultation `c4ca369d` (codex) added three defects the reviewers missed, each
confirmed by a RED test. Fixed in `fix/wc-retro-pr4-e1-safety`:

| # | Defect in what shipped (still on main) | Fix |
|---|---|---|
| P2 | A `config set` / `reset` that repairs a broken user layer dropped what it could not read, so a lost `auto.A# = false` was the action back ON by default — exactly what §15a #1 forbids | a lossy repair writes `dryRun = true` and says so (not when the command WRITES `dryRun`; a `reset dryRun` is pinned too) — an owner question below |
| C | The broken layer was moved aside BEFORE the too-large / coupled-rule refusals were asked: a refused `set` took the file away | the move happens only once the write goes ahead; a layer whose bytes were read (an empty one too) is COPIED aside, created exclusively so a concurrent repair's kept file is never overwritten (consultation `f4a0e9b4`), so it is in place until its replacement is renamed over it (a crash between leaves it); an unreadable one is moved and, when the write is refused or fails, moved BACK — a refused put-back is an error, never swallowed (the fix PR's own plan and code rounds, session `3991eba5`) |
| C | A folder that HOLDS a protected root was deletable — the never-list judged only the path itself | ancestors refused under the root's own rule |
| C | `{"auto":{"A4":{}}}` flattened to nothing: the layer read as valid and A4 kept the value below it | an object at a setting's key is that setting's (invalid) value |
| C | A log root that could not be listed threw out of `LogRetention.Prune`, outside `Main`'s catch | one counted failure in the report |
| F0 | `ConfigDocument.LeafLines` (and `Flatten`) above cyclomatic complexity 4 | split into a small reader walk |
| F4 | `config reset` of an absent key said nothing, though the writer knew | a note on stderr — only when the layer was readable, so its absence is known |

F2 (log retention under observe-only) was real at the merge and is already fixed on main (E7.S0 review C1).

**Owner question (PR #4):** the lossy-repair fix chose to keep §15a #1's promise that `config set` repairs a broken layer and
to pin `dryRun = true`; the consultant preferred refusing a lossy repair outright (the person edits the file, or a future
explicit `config repair`). Pinning keeps the panel able to repair; refusing never changes a setting the person did not name.

### 17d. Retro gate over PR #5 (E2) — coai codex, 2026-10-06

Plan round session `b69d827b` (1/1 reviewer, `proceed`, 2 findings, both rejected: the engine-restart mark was measured and
its residuals are documented in `DockerEngineStart.cs`; `volume-seen.json` holds only anonymous volumes, whose names are
random 64-hex ids). Code round (4/4 reviewers, `proceed`, 4 findings: 1 accepted, 3 rejected — the module docs live as
sections of `research/architecture.md` since E1, an owner question; a `CliHost` test seam is no shipped defect; the
inspect template emits only a mount's type and name, so no bind source is ever captured). The consultation for this round
is OWED — the shared consult cap was reached. Fixed in `fix/wc-retro-pr5-collect-progress`: `collect` logs that it is
measuring before the first tool is asked anything (a full run said nothing for minutes). The fix's own plan round narrowed
the promise: the line says the run started; a slow stage may stay silent until it ends or reaches its ceiling.

### 17e. Retro gate over PR #7 (E3) — coai codex, 2026-10-06

Plan round session `69109113` (1/1 reviewer, `proceed`, 2 findings: 1 accepted, 1 rejected — a button's A7 preview lists
every reclaimable entry, exactly what `builder prune -a -f` removes). Code round (4/4 reviewers, `proceed`, 3 findings: 2
accepted, 1 rejected — `WSL_CARE_SANDBOX_PRIVILEGED` keeps the caller's own uid, so no boundary is crossed). The
consultation for this round is OWED — the shared consult cap was reached; the question consultant had no row switched on.
Fixed in `fix/wc-retro-pr7-engine`:

| # | Defect in what shipped (still on main) | Fix |
|---|---|---|
| P0 | §15d #2's residual: with no single target user the user layer is not read, and the TIMER still ran machine-scoped cleanups on the defaults — a switch the person turned off there was overridden (§15a #1) | while the user layer is skipped the timer runs no action, naming why; a button still runs. This reverses §15d #2 for the timer — an owner question |
| C0 | No module document for the daemon (`common.knowledge-base`) | [module_daemon.md](../research/module_daemon.md), a map into `architecture.md`'s epic sections, as the retro of PR #9 did for the extension; it also answers PR #5's rejected F0 |
| C2 | A confirmed `act` said nothing while a slow action ran | one log line before the first tool is asked; a preview says nothing extra |

### 17f. Retro gate over PR #17 (E7, the daemon half) — coai codex, 2026-10-06

Plan round session `2c1df544` (1/1 reviewer, `proceed`, 2 findings, both rejected: `wsl.exe -u root` asks no password by
WSL's design and interop-off already makes the user layer tighten-only; a component swapped under the home during the walk
can only be swapped by the home's owner, and the walk opens no file — kept as a residual). The plan text given was §15q's
design part verbatim; the E7.S2b and E7.S2c sections were named for the reviewer to read in the checkout (§15q is 129 KB).
Code round (4/4 reviewers, `proceed`, 4 findings, all accepted). The consultation for this round is OWED — the shared consult
cap was reached. Fixed in `fix/wc-retro-pr17-cli`:

| # | Defect in what shipped (still on main) | Fix |
|---|---|---|
| F2 | `doctor`'s versions line skipped `CommandLine.Printable`: a version a tool answered reached the terminal raw (OSC 52) | the whole line passes it — red first |
| F1, F3 | The act parser's `ActSplit` appended to mutable lists AND copied the whole list per `--process` value (402 MB for 10 000 keys) | `ImmutableList` — no mutation, a shared tree per add — red first on the allocation |
| F0 | `AgentsCommand` forced `null!` through `Reading.ValueOr` | the report's own "no sample" spelt as such (`Sample(…)`) |

Observed (2026-10-06, Windows, Debug): RED — the doctor test failed with the ESC printed (`Expected stdout … not to contain
ESC because no control character of a version answer reaches the terminal`) and the parse test with
`Expected allocated to be less than 67108864L … but found 402604896L`; GREEN after the fix — `RetroPr17Tests` 2/2; the whole
suite Core 1335/1335, Cli 257/257, Scenarios 370/370 (1 skipped by design).

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
