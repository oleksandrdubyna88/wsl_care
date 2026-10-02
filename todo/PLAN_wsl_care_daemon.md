# PLAN — keep WSL from degrading over the working day (`wsl-care` daemon + VS Code extension)

> Status: **plan only, nothing implemented yet (2026-10-02).** Scope: a systemd timer + service inside the
> `Ubuntu` distro (`daemon/`), a VS Code extension that shows its state and runs cleanups on demand
> (`extension/`), their shared config, and the one-time cleanups listed in Phase 0.
>
> Evidence: [2026-10-02_wsl_resource_baseline.md](../research/2026-10-02_wsl_resource_baseline.md) — every
> number quoted here comes from it.

## 1. Symptom and goal

**Symptom.** Every day starts from a fresh WSL VM (the PC is shut down every night) and by the evening
the machine is slow. On 2026-10-01 at 18:36 the VM had 0.27 GB free out of 46 GB: ~22 GB of *inactive*
anonymous memory, ~19 GB of page cache that WSL never gives back to Windows, and the memory so fragmented
that the kernel could not find one free 64 KB block (`page allocation failure`, three such days in five
weeks). Next to that: 108 GB of reclaimable Docker data, 41 GB of worktrees, a journal whose history is
erased by clock jumps, and no process-level history at all.

**Goal.**
1. **Record** what the VM holds, every 4 hours, so the question "who ate the memory" has an answer.
2. **Fix automatically** what is safe to fix: give the cache back, defragment, stop idle build servers, and
   remove Docker leftovers that only tests created.
3. **Show** it all in VS Code — disk, RAM, swap, containers (now / last 24 h / reclaimable) — with
   **buttons** to run any cleanup by hand and **settings** for the age limits ("older than N hours/days").
4. **Report** everything else, with numbers, and never touch it unasked.

**Success criterion.** Over one working week after enabling actions: no `page allocation failure`;
`MemAvailable` at 18:00 ≥ 25 % of the VM; Docker reclaimable data stays below 30 GB; the daily
`history.jsonl` lets anyone name the top 5 memory holders of any afternoon; and every number in the
extension matches the CLI it came from.

## 2. Decisions already taken (2026-10-02)

| Question | Decision |
|---|---|
| Where the work lives | this repository, `D:\rsd\wsl_care` — `daemon/` + `extension/` |
| Where the scheduled work runs | **only** a systemd timer inside `Ubuntu` (no Windows Task Scheduler) |
| How far it may fix things on its own | **safe automatic actions only**; everything else is report-only or a button |
| UI | a VS Code extension shipped in the same package: status bar + side panel + settings + cleanup buttons |
| History tools | `sysstat` **and** `atop` are installed by the installer, for proper system and per-process logs |
| Killing old processes | a **setting**, off by default: families from an allowlist, older than N hours, idle |
| What happens now | this plan; implementation is a separate task after review |

Consequences of "systemd inside Ubuntu only", accepted knowingly:
- The timer cannot run `wsl --shutdown` and cannot compact a `.vhdx` (the VM must be stopped for that).
  Both stay manual (Phase 0) — the extension shows when they are due and how much they would free.
- The daemon sees the **whole VM's** memory (`/proc/meminfo` is VM-wide) but only **Ubuntu's** processes
  (`Ubuntu-26.04` and `docker-desktop` are separate PID namespaces). Containers are covered through the
  Docker CLI; whatever remains is reported as an explicit *unattributed* figure instead of being guessed.
- The extension runs on the **Windows** side (§7), so it adds what the daemon cannot see: the `vmmemWSL`
  working set and host RAM. The `.vhdx` sizes the daemon reads itself through `/mnt/c`.

## 3. Phase 0 — one-time fixes, by hand, each one confirmed (not the daemon's job)

Ordered by expected effect. Each is a separate step so its effect can be measured on its own.

| # | Fix | Why (baseline finding) | How to verify |
|---|---|---|---|
| 0.1 | `%USERPROFILE%\.wslconfig`: add `[experimental]` `autoMemoryReclaim=dropCache` (try `gradual` later) and `sparseVhd=true`; keep `memory` at the default for now | F1: 19 GB of cache is never returned; F3: vhdx never shrinks | `wsl --shutdown`; by the evening `vmmemWSL` is clearly below the 46 GB ceiling. **Check first** whether `gradual` conflicts with Docker Desktop on this WSL version; if it does, stay on `dropCache` |
| 0.2 | **Done 2026-10-02, ≈ 135 GB freed** — [record](../research/2026-10-02_one_time_cleanup.md); note: no exited container carried the Testcontainers label, so the Testcontainers-only action was replaced by A5 (stopped ≥ N days). Originally planned as: one-time Docker cleanup: `docker builder prune --filter until=168h`, `docker image prune` (dangling only), remove stopped containers labelled `org.testcontainers=true`, then `docker volume prune` (anonymous only — Docker ≥ 23 default) | F2: 71 GB volumes + 21 GB build cache + 16 GB images reclaimable | `docker system df` before/after, recorded in `research/` |
| 0.3 | Compact the vhdx files after 0.2: `wsl --shutdown`, then `wsl --manage <distro> --set-sparse true` or `Optimize-VHD` / `diskpart compact vdisk`; Docker Desktop's own *Clean up* for `docker_data.vhdx` | F3: 142 + 123 + 71 GB files on `C:` | file sizes before/after |
| 0.4 | `systemctl mask wsl-pro.service` in **both** Ubuntu distros | F5: a reconnect every ~20 s, ~4 000 log lines a day, for an agent that is not installed | no `wsl-pro-service` lines in syslog for a day |
| 0.5 | **Verify** the clock hypothesis before changing anything: count jumps for one day with `systemd-timesyncd` (Ubuntu) and `chronyd` (Ubuntu-26.04) stopped, against the ~1 700/day baseline | F4: jumps erase journald history and flush DNS cache | jumps per day; make the change permanent only if the count drops |
| 0.6 | Decide on `Ubuntu-26.04`: keep it, or `wsl --terminate` it when not in use (it shares the VM's memory and kernel clock) | F1 caveat, F4 | `free` before/after terminating it |
| 0.7 | Decide on `snapd`: no user snaps are installed (`core22` + `snapd` only) | F5: snapd/snapfuse CPU at boot, 15 failed starts | boot time (`systemd-analyze`) before/after |
| 0.8 | Review `~/git/_wt` (41 GB) and `~/coai-514`, `~/coai-462` **by hand**; remove worktrees **from Windows** with `git worktree remove` | F3 | `du` before/after |

**Never in Phase 0 or anywhere else:** `git worktree prune` from WSL — it unregisters every worktree
created from Windows (`D:/…` paths look missing from inside WSL).

## 4. The daemon — what it monitors

Every run collects one record (one JSON line) and evaluates thresholds. Thresholds are config values; the
numbers below are starting points, re-tuned after one week of recorded data.

### 4.1 Memory (VM-wide)

| Metric | Source | Warn | Act |
|---|---|---|---|
| `MemAvailable` % of `MemTotal` | `/proc/meminfo` | < 25 % | < 15 % → §5 A1 |
| page cache (`Cached` + `Buffers`) | `/proc/meminfo` | > 15 GB | > 12 GB **and** available < 30 % → §5 A1 |
| anonymous, of which inactive | `/proc/meminfo` `Inactive(anon)` | > 15 GB | report (it names the processes, §4.2) |
| swap used / total | `/proc/meminfo` | > 4 GB | — |
| fragmentation: free blocks ≥ 64 KB (order ≥ 4) in zone Normal | `/proc/buddyinfo` | < 64 blocks | = 0 → §5 A2 |
| memory pressure | `/proc/pressure/memory` `some avg300` | > 10 | — |
| `page allocation failure` since last run | `kern.log` / `journalctl -k` | ≥ 1 → **alert** | — |
| OOM kills since last run | same | ≥ 1 → **alert** | — |

### 4.2 Who holds it

- The top 30 Ubuntu processes by RSS: pid, user, rss, age (`etimes`), state, cpu time, cwd, command line
  truncated to 200 characters.
- **Families**, aggregated by config-defined regex: `vscode-server` (extension hosts, ServiceHub,
  `Microsoft.CodeAnalysis.LanguageServer`), `dotnet-build-servers` (MSBuild `/nodemode:`, VBCSCompiler,
  Razor server), `testhost`, `node`, `claude`/`codex`/`gemini` CLIs, `docker-desktop-proxy`, everything
  else.
- Per-container memory: `docker stats --no-stream` (name, image, memory, age, `org.testcontainers` label).
- **Unattributed** = `AnonPages` + `Shmem` − Σ Ubuntu RSS − Σ container memory. The residue is
  `Ubuntu-26.04` + docker-desktop internals; reported, never guessed.
- **Suspects**: processes with PPID 1 or reparented to `systemd --user`, in a family above, older than the
  configured age, with < 1 CPU-second in the last interval (orphaned and idle). Input for action A11.
- Processes whose cwd or arguments are under `/mnt/` (a walk over 9p caused the 2026-10-01 failure).

### 4.3 Docker

- **Running now** (`docker ps`) and the `docker system df` totals per type (count, size, reclaimable) — the
  "Docker after: 13 images, 28 containers, 44 volumes, build cache empty" line of the 2026-10-02 cleanup.
- **One row per cleanup**, the same rows as the one-time cleanup record
  ([2026-10-02_one_time_cleanup.md](../research/2026-10-02_one_time_cleanup.md)), each with *count* and
  *reclaimable GB* — these are exactly the `--preview` numbers of the matching action in §5:

  | Row | Count | Reclaimable GB | Action |
  |---|---|---|---|
  | anonymous volumes not attached to any container | `docker volume ls -q --filter dangling=true`, 64-hex names only | `docker system df -v` volume sizes | A4 |
  | containers stopped ≥ N days, with the anonymous volumes they hold (Testcontainers-labelled ones counted separately) | `docker inspect` `State.FinishedAt` (or `Created` for never-started) | container size + their anonymous volumes | A5 |
  | images not referenced by any container (dangling ones counted separately) | `docker image ls` vs every container's image id | `docker system df` images reclaimable | A6 |
  | build cache | `docker buildx du` | total and reclaimable | A7 |
  | npm cache | — | `du ~/.npm` | A8 |
  | apt cache + disabled snap revisions | `snap list --all` disabled | `du /var/cache/apt` + snap file sizes | A9 |

- **Kept, report-only:** named volumes not attached to any container, with their sizes (2026-10-02: ~16 GB,
  `named-volume-a` 10.3 GB alone) — shown so a human decides per volume; no action exists for them.
- `docker_data.vhdx` size and how much of it is now free inside (= what compaction would return).
- `docker system df -v` takes seconds to minutes; the full numbers come from the 4-hour run and are cached,
  `status --json` reports their age.
- **Started in the last 24 h.** `docker ps` cannot answer it (removed containers vanish) and the daemon's
  own event buffer is too short for 2 000–3 000 starts a day. So a second, tiny unit
  `wsl-care-events.service` follows `docker events --filter type=container --filter event=start --format
  '{{json .}}'` and appends one line per start to `/var/lib/wsl-care/container-starts/{yyyy-MM-dd}.jsonl`
  (image, name, testcontainers label). Counts for "last 24 h" and "per image" come from those files; kept
  14 days. Restarts itself when Docker Desktop restarts (`Restart=always`, `RestartSec=30`).

### 4.4 Disk

`df /` (used / free / %), host `C:` free space through `/mnt/c`, sizes of the three `.vhdx` files and their
growth since the previous record, and — once a day, not every 4 h, because it is expensive — `du` of
`~/git/_wt`, `~/.npm`, `~/.nuget/packages`, `~/.cache`, `~/.vscode-server`, `~/.claude/projects`, and of
`bin/` + `obj/` under `~/git`. Warn when `/` > 80 % or a vhdx grew > 10 GB in a day.

### 4.5 System health

Clock-jump count since the last run (warn > 100 per 4 h), journald disk usage and its oldest entry (warn
when history is shorter than 7 days), failed units (user + system), uptime, the WSL `failed to start
within` boot error, and whether `sysstat` and `atop` are collecting (their last sample is < 30 min old).

## 5. Actions — automatic (timer) and manual (buttons)

Every action is one module with `should_run(record, config)` and `run(executor)`, records before/after
numbers in the history line, and is reachable two ways: the **timer** runs it when its trigger fires and
its `auto` switch is on; the **extension** runs it on a button press regardless of the trigger (always with
a preview first, §7.3).

A4–A9 are the cleanups of the 2026-10-02 one-time run, one action per row of that record, with the age
limits it used as defaults ("≥ 7 days" etc.). Each produces a result row in the same shape — *what*,
*count*, *freed GB* — and every run ends with the "Docker after" totals (§4.3, §7.2).

| # | Action | Auto trigger | Setting (default) | Auto by default | Why it is safe | 2026-10-02 |
|---|---|---|---|---|---|---|
| A1 | `sync; echo 1 > /proc/sys/vm/drop_caches` | §4.1 | — | on | the cache is rebuilt on demand; freed pages go back to Windows. Never `3` | — |
| A2 | `echo 1 > /proc/sys/vm/compact_memory` | after A1, or fragmentation = 0 | — | on | defragmentation only | — |
| A3 | `dotnet build-server shutdown`, as the owning user | build servers alive, **no** `dotnet build/test/run` alive | `buildServers.idleHours` (4 h) | on | the official command; the next build restarts them | — |
| A4 | `docker volume prune -f` — **every unnamed (anonymous) volume not attached to any container** | > `volumes.anonymousMaxCount` (100) or > `volumes.anonymousMaxGb` (20) | — | on | no container refers to them; named volumes are never touched. **Refuses on Docker < 23** (there `prune` took named volumes too) | 387 volumes, 59.6 GB |
| A5 | `docker rm -v` of containers stopped ≥ N days (anonymous volumes go with them, named stay) | any exist | `containers.stoppedOlderThanDays` (7 d); Testcontainers-labelled: `containers.testcontainersOlderThanHours` (2 h) | **Testcontainers: on; all others: off** (button, or opt-in) | user containers are removed only when the user turned that on or pressed the button; named volumes survive | 58 containers, ~3.5 GB |
| A6 | `docker image prune -f` (dangling) **and** `docker image prune -af --filter until=…` (not referenced by any container) | dangling: any; unused: images reclaimable > `images.unusedMaxGb` (10) | `images.unusedOlderThanDays` (7 d) | dangling: on; unused: **off** (button, or opt-in) | an image used by any container, even a stopped one, is never removed; the rest is re-pullable | 99 → 13 images, 31.5 GB |
| A7 | build cache: auto — `docker builder prune -f` with a **size cap** (`--max-used-space` / `--keep-storage`, whichever this Docker supports — check at implementation); button — `docker builder prune -af` (all) | cache > `buildCache.maxGb` (20) | `buildCache.maxGb` (20); age filter `buildCache.olderThanDays` (7 d) for the timer only | on (cap); all: button | rebuildable. 2026-10-02 showed an **age filter alone frees nothing** (all 34.6 GB were < 7 days old) — the size cap is the trigger that works | 34.6 GB |
| A8 | `npm cache clean --force`, as the user (through `bash -ic` so `nvm` is on `PATH`) | `~/.npm` > `npm.maxCacheGb` (5) | `npm.maxCacheGb` (5) | **off** (button, or opt-in) | a cache; the next `npm ci` downloads again | 7.0 → 1.7 GB, 5.3 GB |
| A9 | `apt-get clean` + `snap remove --revision` of **disabled** snap revisions | apt cache > 200 MB or any disabled revision | — | on | package caches and superseded snap revisions only | ~0.4 GB |
| A10 | `journalctl --vacuum-time=…` | journald > 1 GB | `journal.keepDays` (30 d) | on | old logs | — |
| A11 | Terminate **suspect** processes (§4.2): `SIGTERM`, `SIGKILL` after 10 s | suspects exist | `processes.idleOlderThanHours` (8 h) + `processes.families` allowlist | **off** | opt-in only; never touches a process with a TTY or one that used CPU in the interval | — |
| A12 | Playwright browsers / NuGet HTTP cache | — | — | **off** (button only) | caches, but large re-downloads | — |

**Freed bytes are measured, not estimated:** Docker's own "Total reclaimed space" for A4, A6, A7; the
`docker system df -v` sizes of the removed containers and volumes for A5; `du` before/after for A8, A9.
Every action run — timer or button — appends `{time, action, trigger: timer|button, count, freedBytes,
items[≤ 50 names]}` to `/var/lib/wsl-care/cleanups.jsonl` (kept 90 days).

**Never, neither automatic nor as a button:** `git worktree prune`, `docker system prune -a`,
`docker volume prune --all`, `echo 3 > drop_caches`, deleting anything under `~/git`, `wsl --shutdown`
from inside the VM. Tests enforce that no code path can produce these commands.

Global guards: one run at a time (`flock` on `/run/wsl-care.lock`, shared by timer and buttons);
`dry_run = true` for the first 7 days for the **timer** (buttons always preview, then execute on
confirmation); each action has its own `auto` switch; a failing action is logged and the run continues.

## 6. Shared config and the CLI contract

**Config, one source of truth.** `daemon/config/default.toml` (shipped) < `/etc/wsl-care/config.toml`
(machine) < `~/.config/wsl-care/config.toml` (user overrides, written by the extension). The daemon runs
without VS Code, so the file — not the editor — is the truth; the extension's settings are an editor for
it (§7.4).

**CLI** (`/opt/wsl-care/bin/wsl-care`), the only interface the extension uses — no second implementation
of any collector in TypeScript:

| Command | Output | Used by |
|---|---|---|
| `wsl-care status --json` | fresh fast snapshot (< 2 s: meminfo, buddyinfo, `docker ps`, `df`, starts-24h count, last full-run record for the slow parts) | panel, status bar |
| `wsl-care collect` | full run (timer target) | timer, "Run full check now" button |
| `wsl-care act <A#> --preview --json` | what would be removed / freed, with counts and GB | every button, before confirmation |
| `wsl-care act <A#>[,<A#>…] --json` | one result row per action (*what*, *count*, *freed GB*) + "Docker after" totals | button after confirmation, **Clean selected** |
| `wsl-care preview --all --json` | every §4.3 cleanup row with count and reclaimable GB, plus the kept named volumes | panel *Cleanup* table |
| `wsl-care cleanups --since 30d --json` | the `cleanups.jsonl` records and per-action totals | panel *Cleanup history* |
| `wsl-care history --since 24h --json` | records for charts | panel charts |
| `wsl-care config get/set <key> <value>` | validated read/write of the user override file | settings sync |
| `wsl-care doctor --json` | sysstat/atop/timer/events-unit health | panel "Health" section |

The JSON carries `schemaVersion`; the extension refuses a major version it does not know and says so.

**Durable running state.** A running action writes `/var/lib/wsl-care/running.json` (action, pid, start)
before it starts and removes it when it ends; the timer's startup sweep removes a stale one whose pid is
gone. The extension derives "Cleaning…" from that file, not from its own memory, so a VS Code reload
mid-cleanup still shows the truth and never sticks on a dead run.

## 7. The VS Code extension (`extension/`)

### 7.1 Where it runs

`extensionKind: ["ui"]` — it runs on the **Windows** side whether the window is local or *Remote – WSL*,
and reaches the daemon with `wsl.exe -d <distro> -- /opt/wsl-care/bin/wsl-care …`. A3–A8 and A12 run as
the user; A1, A2, A9, A10 and A11 (other users' processes) need `-u root`, which `wsl.exe` grants without a
password. Running on Windows also gives it two numbers the daemon cannot see: the `vmmemWSL`
working set and host RAM.

### 7.2 What you see

**Status bar** (always): `WSL RAM 62 % · swap 1.2 G · / 13 %` with a colour (ok / warn / alert from the
daemon's rules); click opens the panel. Refresh every 60 s, and immediately after any button.

**Side panel "WSL Care"** (activity-bar view, webview), sections:

| Section | Shows |
|---|---|
| **Memory** | VM: used / available / cache / inactive-anon / free, of the 46 GB ceiling; `vmmemWSL` on Windows; host RAM; fragmentation indicator; a sparkline of `MemAvailable` today (from history + `sar -r`) |
| **Swap** | used / total, trend today |
| **Disk** | `/` used / free; host `C:` free; the three `.vhdx` sizes and "could shrink by ~X GB" hint; big folders (daily `du`) |
| **Containers** | running now; started in the last 24 h (total + top images); stopped (of which Testcontainers, of which ≥ N days); the "Docker after" line — images / containers / volumes / build cache with count, size and reclaimable |
| **Top holders** | top Ubuntu processes and families by RSS, top containers by memory, the *unattributed* residue |
| **Health** | last full run (time, result), warnings since it, clock jumps, journald span, sysstat/atop/timer/events-unit state |
| **Cleanup** | the table of the one-time cleanup, live: one row per A4–A9 — *what* (with the current age limit in the text, e.g. "containers stopped ≥ 7 days, with their anonymous volumes"), *count*, *reclaimable GB*, a checkbox and a **Clean** button; A1–A3 and A10–A12 as further rows; a **Clean selected** button with the total GB; a *Kept* sub-table of unattached named volumes (size, last used container) with no button; **Run full check now** |
| **Last cleanup** | the result of the most recent run in the same table shape — *what*, *freed* — plus "Docker after: …"; who ran it (timer / button) and when |
| **Cleanup history** | freed GB per day and per action over 30 days (from `cleanups.jsonl`); totals for 7 and 30 days |

### 7.3 Buttons

Click → `act <A#> --preview` → a modal with exactly what will be removed (counts, names for ≤ 20 items, GB)
→ **Confirm** → `act <A#>` → the result (freed GB / MB) as a notification and in the panel. **Clean
selected** previews every ticked row in one modal (the table with a total) and runs them in the order
A5 → A4 → A6 → A7 → A8 → A9 (removing containers first frees their volumes and images for the later
steps — the order of the 2026-10-02 run); its result is one table, shown under *Last cleanup*. Actions
that touch user data or force re-downloads (A5 for non-Testcontainers containers, A6 unused images, A8,
A11, A12) need a second confirmation that names the setting they used. While running, the buttons read
*Cleaning…* and are disabled (from `running.json`, §6).

### 7.4 Settings

VS Code settings under `wslCare.*`, each mirrored to `~/.config/wsl-care/config.toml` via
`wsl-care config set` on change (validated by the CLI; an invalid value is rejected with its message and
the setting is reverted):

- `wslCare.distro` (default `Ubuntu`)
- `wslCare.volumes.anonymousMaxCount` (100), `wslCare.volumes.anonymousMaxGb` (20)
- `wslCare.containers.stoppedOlderThanDays` (7), `wslCare.containers.testcontainersOlderThanHours` (2)
- `wslCare.images.unusedOlderThanDays` (7), `wslCare.images.unusedMaxGb` (10)
- `wslCare.buildCache.maxGb` (20), `wslCare.buildCache.olderThanDays` (7)
- `wslCare.npm.maxCacheGb` (5), `wslCare.journal.keepDays` (30)
- `wslCare.buildServers.idleHours` (4)
- `wslCare.processes.killEnabled` (false), `wslCare.processes.idleOlderThanHours` (8),
  `wslCare.processes.families` (`["dotnet-build-servers","testhost"]`)
- `wslCare.thresholds.memAvailableWarnPercent` (25), `…ActPercent` (15), `wslCare.thresholds.swapWarnGb` (4)
- `wslCare.auto.<A#>` — the per-action `auto` switch, and `wslCare.dryRun` (true for the first week)
- `wslCare.refreshSeconds` (60)

On startup the extension reads the file (`config get`) and shows a one-time notice when it differs from
the VS Code settings, offering which side to keep — never silently overwriting either.

### 7.5 First run and install

If `wsl-care` is missing in the distro, the panel shows **Install daemon** → runs `install.sh` from the
bundled `daemon/` (copied to the distro through `\\wsl$`), which installs `sysstat` and `atop` (10-minute
intervals), the CLI, both units, and the default config — with an apt password prompt in a VS Code
terminal, never stored. **Uninstall** removes units and `/opt/wsl-care`, keeps history unless asked.

### 7.6 Stack

TypeScript, the VS Code API, a plain webview (no framework) with `postMessage`; process calls through one
`WslCareClient` class (spawn `wsl.exe`, timeout, JSON parse, schema check). Packaged with `@vscode/vsce` to
a `.vsix`. Before choosing lint/test tool versions, check them against the current TypeScript major (a
TypeScript major can outrun `typescript-eslint`).

## 8. Repository layout

```
daemon/
  src/wsl_care/   collect/ rules.py actions/ report.py cli.py config.py
  config/default.toml
  systemd/        wsl-care.service wsl-care.timer wsl-care-events.service
  install.sh uninstall.sh
  tests/          fixtures/ (captured from this machine)
extension/
  src/            extension.ts client/WslCareClient.ts views/ settings/
  media/          panel.html panel.css panel.js
  test/
research/         baseline + weekly results
todo/             this plan
.gitattributes    *.sh *.py *.toml *.service *.timer text eol=lf
```

Daemon: Python 3.12, **standard library only** (`tomllib`, `json`, `subprocess`, `unittest`). Units:
`wsl-care.service` (`Type=oneshot`, root, `Nice=19`, `IOSchedulingClass=idle`, `MemoryMax=256M`,
`TimeoutStartSec=10min`); `wsl-care.timer` (`OnBootSec=20min`, `OnUnitActiveSec=4h`, `AccuracySec=5min` —
monotonic, because the VM is off every night). Logs: `/var/lib/wsl-care/history.jsonl` (90 days) and
`/var/log/wsl-care/{yyyy-MM-dd}/wsl-care-{HH-mm-ss}-{pid}.log` (UTC, one file per run); every warning also
goes to `logger -t wsl-care`.

## 9. Build order

1. Repo skeleton, `.gitattributes`, default config, `config.py` (three-layer merge + validation).
2. Collectors + `wsl-care status --json` / `collect`; `wsl-care-events.service` for 24-h starts.
3. `rules.py`; history, run log, `logger`.
4. Actions A1–A12 with `--preview`; the never-list guard; `running.json` + sweep.
5. `install.sh` (sysstat, atop, units, CLI) — **observe only** first, actions off.
6. Extension: `WslCareClient`, status bar, panel sections read-only.
7. Extension: Cleanup buttons (preview → confirm → act), running state.
8. Extension: settings ↔ config file sync, first-run install.
9. Package `.vsix`, install, one week of timer `dry_run`; review in `research/`, tune, switch `dry_run` off
   action by action.

## 10. Test plan

**Daemon** (`unittest`, fixtures captured from this machine):
- Parsers: `/proc/meminfo`, `/proc/buddyinfo`, `/proc/pressure/memory`, `ps`, `docker … --format json`.
- The 2026-10-01 18:36 state (free 0.27 GB, no order ≥ 4 blocks) → `alert` + A1 + A2; a fresh-boot
  fixture → `ok`, no action.
- Each threshold just below and just above its value; each age setting at its edge (e.g. a Testcontainers
  container at 1 h 59 min stays, at 2 h 01 min goes).
- Actions against a fake executor that records commands: assert **exactly** which commands run; a property
  test that no input can make any action emit a command from the *never* list; A4 refuses on Docker 22;
  A11 never targets a process with a TTY or recent CPU.
- Guards: `dry_run` emits nothing; a second run exits on the lock; a failing action does not stop the run;
  a stale `running.json` with a dead pid is swept.
- Config: three-layer merge order; `config set` rejects an out-of-range value with a message.
- Events follower: a recorded `docker events` stream → per-day files and a correct 24-h count across
  midnight.
- Cleanup rows, from fixtures recorded on 2026-10-02 (`docker system df -v`, `docker inspect`,
  `docker volume ls`): A4 counts the 387 anonymous dangling volumes / 59.6 GB and **excludes** the 7 named
  dangling ones; A5 at 7 days selects the 58 containers and leaves the 18 younger ones; A6 never lists an
  image that a stopped container uses; A7 with `olderThanDays=7` alone selects ~0 GB while the size cap
  selects the cache (the observed trap); A9 lists only `disabled` snap revisions.
- Freed-bytes parsing of Docker's "Total reclaimed space: 59.59GB" / "kB" / "B" lines; `cleanups.jsonl`
  append + 30-day totals; **Clean selected** runs in the A5 → A4 → A6 → A7 → A8 → A9 order.

**Extension**:
- `WslCareClient` against a fake `wsl.exe` (a script printing recorded JSON): parse, timeout, non-zero
  exit, unknown `schemaVersion`.
- Pure view-model functions (bytes → "12.3 GB", status colour, button enabled/disabled from
  `running.json`) unit-tested without VS Code.
- Settings sync: changed setting → exactly one `config set`; rejected value → setting reverted + message.
- Integration (`@vscode/test-electron`): the extension activates, the status bar item appears, the panel
  renders against the fake client.

**Live smoke on this machine:** `wsl-care status --json`; one timer-driven run and its run log; each
button's preview against the real Docker; A1 for real with the before/after `MemAvailable` recorded.

## 11. Definition of Done

- [ ] Phase 0 steps done or explicitly declined, each with its before/after numbers in `research/`.
- [ ] `sysstat` and `atop` collect; the timer runs every 4 h; `history.jsonl`, run logs and the 24-h
      container-start files appear.
- [ ] A1–A12 implemented with `--preview`, each switchable; the timer's `dry_run` week reviewed.
- [ ] Nothing from the *never* list can be executed — enforced by tests.
- [ ] The extension shows disk, RAM, swap, containers (now / 24 h / reclaimable), top holders and health;
      every button previews, confirms and reports what it freed; settings round-trip to the config file.
- [ ] The *Cleanup* table shows the same rows as the 2026-10-02 one-time cleanup (anonymous volumes,
      old stopped containers, unused images, build cache, npm cache, apt/snap) with count and reclaimable
      GB, and *Last cleanup* shows the freed table + "Docker after" in the same shape.
- [ ] Daemon and extension tests green, including the 2026-10-01 fixture.
- [ ] The §1 success criterion checked over one working week and recorded in `research/`.
- [ ] This plan promoted to `research/` with status `IMPLEMENTED <date>` and its deviations.

## 12. Open questions

1. Windows toast notifications on `alert` from the extension (easy, it runs on Windows) — on by default, or
   only the status-bar colour?
2. Should A1 also run on a short timer (every 30 min) while 4 h stays the full run? `autoMemoryReclaim`
   (0.1) may make it unnecessary — decide after a week with 0.1 in place.
3. Which process families, if any, belong in the A11 allowlist by default — decide from one week of §4.2
   data, not before.
4. Should the daemon also be installed into `Ubuntu-26.04`, or should that distro simply be stopped when
   idle (0.6)?
