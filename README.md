# wsl_care

Keeps the WSL VM on this machine from degrading over the working day: a systemd timer inside `Ubuntu`
that records memory, disk and Docker state every 4 hours and applies safe cleanups, plus a VS Code
extension that shows the state and runs cleanups on demand.

| Folder | Holds |
|---|---|
| `src_daemon/` | the C# Native AOT daemon/CLI `wsl-care` — today the foundation seams, the `config` verbs, `status` (memory, processes, containers, disk), `preview` (what each Docker cleanup would free), the full run `collect`, `doctor`, the container-start follower `events follow`, and the action engine behind `act` with every cleanup — the journal vacuum, the irreversible ones (A4–A9, A11, A12, A14, A17, and the button-only A18 of E7.S2b) and A1–A3, A15, A16 — all built and shipping in `daemon-v0.1.0` |
| `src_vs_code/` | the VS Code extension **AI OS Care** (Marketplace id `remsoftdev.ai-os-care`) — in development: its client of the daemon, the status bar and the panel, since E6.S2 the root boundary, since E6.S3 the cleanup buttons on it, since E6.S4 the Logs page, and their tests — [Extension (preview)](#extension-preview) below |
| [todo/](todo/README.md) | open plans |
| [research/](research/) | measurements of the system as it is — start with [the 2026-10-02 baseline](research/2026-10-02_wsl_resource_baseline.md) and [the architecture](research/architecture.md) |
| `research/diagnostics/` | the read-only scripts that produced the baseline |
| `install.sh`, `src_daemon/systemd/`, `src_daemon/config/machine.json` | the installer, the systemd units and the machine configuration layer it installs |
| `release-please-config.json`, `.github/workflows/release*.yml`, `.github/scripts/`, `.github/rulesets/`, [docs/repo-settings.md](docs/repo-settings.md) | the release pipeline — [Release](#release) below |

## Install

Inside the WSL distro (Ubuntu 24.04 or newer, systemd running), as root:

```bash
curl -fsSL https://raw.githubusercontent.com/oleksandrdubyna88/wsl_care/main/install.sh | sudo sh
curl -fsSL …/install.sh | sudo sh -s -- --dry-run          # print every step, change nothing (root not needed)
curl -fsSL …/install.sh | sudo sh -s -- --version 0.1.0    # a given release instead of the newest daemon-v*
```

**What it checks before installing anything.** It downloads the newest `daemon-v*` release for this machine
(`linux-x64` or `linux-arm64`; never `releases/latest`, which is the VS Code extension), and its `.sha256`:

- the **checksum** proves the archive arrived as it was published — integrity, not authorship: whoever can
  replace the archive can replace its `.sha256` too;
- the **build-provenance attestation** proves that this repository's `release.yml`, run for **the tag of the version
  being installed** on a GitHub-hosted runner, built those bytes — the exact certificate identity
  `https://github.com/oleksandrdubyna88/wsl_care/.github/workflows/release.yml@refs/tags/daemon-v<version>`
  (`--cert-identity`, plus `--deny-self-hosted-runners`), so a build of `release.yml` from any branch is refused. The
  installer, as root, fetches the attestation itself from GitHub's attestation API (unauthenticated; the bundle comes
  snappy-compressed and is decompressed with `od` and `awk`) and runs `gh attestation verify --bundle` with gh's
  configuration, cache and home inside its own temporary folder and no token — **no gh login is needed**, and nobody's
  gh login or settings take part in the verdict. gh still fetches Sigstore's trusted root over the network. It needs
  the [GitHub CLI](https://cli.github.com) **2.56.0 or newer** from GitHub's apt repository
  ([cli.github.com/packages](https://cli.github.com/packages)): Ubuntu 24.04's own `gh` is 2.45.0, which has no
  `gh attestation`, and 2.49.0–2.55.0 cannot read Sigstore's current trusted root (measured 2026-10-03). Without a
  usable `gh` the installer stops **before downloading anything** and says how to install one. To proceed knowingly
  without it — printed loudly, and the checksum still applies:

  ```bash
  curl -fsSL …/install.sh | sudo sh -s -- --skip-attestation
  ```

A mismatch, a missing `.sha256`, a refused attestation, or an archive holding anything but regular files under one
folder (no `..`, no link) stops it with nothing installed.

**What it changes on the machine** — and nothing else:

| Path / thing | What |
|---|---|
| `/opt/wsl-care/bin/wsl-care` (0755), linked from `/usr/local/bin/wsl-care` | the binary; root and the units always use the absolute path |
| `/etc/systemd/system/wsl-care.service` | the timer's full run, `wsl-care collect --timer` (oneshot, `Nice=19`, idle I/O, `MemoryMax=1G` for the run and every tool it starts, `NoNewPrivileges=yes`; a run ends at `timer.runLimitMinutes` (4 h, above its derived worst case), and one that makes no step for 20 min reads wedged, so *Stop* can end it) |
| `/etc/systemd/system/wsl-care.timer` | every 4 hours on the clock (00:00, 04:00, …), catching up ONCE after a night the VM was off; enabled and started |
| `/etc/systemd/system/wsl-care-events.service` | the container-start follower, `wsl-care events follow`, `Restart=always` after 30 s; enabled and started |
| `/etc/systemd/system/<unit>.d/50-wsl-care-config.conf` | each unit's configured values — the timer's period, the services' `Nice`, `MemoryMax`, `TimeoutStopSec`, the follower's `RestartSec` — rendered from the machine layer by the installed binary (`wsl-care units dropin <unit>`); written on every install, so running the installer again after editing the machine layer applies it; `wsl-care doctor` says when one no longer matches |
| `/etc/wsl-care/config.json` | the machine configuration layer — written **only when none exists**, and empty (comments only: every value stays the binary's default); an existing one is never overwritten |
| `/var/lib/wsl-care`, `/var/log/wsl-care` | the state and the run logs, root's, 0755 |
| `sysstat`, `atop` | installed with `apt-get` when missing; sysstat's collection switched on through its own debconf setting; both services enabled |
| `/etc/wsl.conf` | **only** with `--set-default-user <name>`, and only when it names no default user yet: `[user] default=<name>` is added (the user wsl-care's per-user cleanups act for — WSL also logs in as that user from the next distro start). Without the flag nothing is written; the installer says how to name one |

Then it runs one full `collect` (it measures and records — a `collect` outside the timer never cleans) and verifies
every side effect: `sar` and `atop` on `PATH`, `systemctl is-active` for both units, and
`/opt/wsl-care/bin/wsl-care doctor --json` healthy (waiting up to 2 minutes for the follower's first marker). A step
that fails exits non-zero with `FAILED at step "<step>"` and what it found. It never calls `sudo`, never touches
`.wslconfig`, `wsl-pro.service`, the clock services, snapd or any worktree, and never evaluates what it downloads.
`doctor` reports the events follower as a problem while Docker cannot be reached, so install with Docker running.

**What the unit restricts — and the two places it could bite.** `wsl-care.service` is deliberately NOT sandboxed
(every `Protect*` / `Private*` directive would break a named cleanup — the unit file lists which). It sets two
limits, both unobserved until the first live install (`POST_DEPLOY.md` #11 checks them there):

- `MemoryMax=1G` covers the run AND every child it starts (`npm cache clean`, `dotnet nuget locals`, `pip`/`uv`/`pnpm`,
  the Docker CLI, the daily folder walk). If the journal shows a child OOM-killed, raise it in a drop-in
  (`systemctl edit wsl-care.service`).
- `NoNewPrivileges=yes` also blocks the AppArmor profile change a **snap** application makes as it starts. A Docker
  installed as a snap (`/snap/bin/docker`) would then fail under the timer — every Docker figure unavailable, A4–A7
  refused — while working from a terminal. Docker Desktop's CLI and apt's `docker-ce` are not snaps. If yours is,
  either install Docker from apt, or override `NoNewPrivileges=no` in a drop-in.

**Uninstall:**

```bash
curl -fsSL …/install.sh | sudo sh -s -- --uninstall           # units stopped and removed, binary and link removed
curl -fsSL …/install.sh | sudo sh -s -- --uninstall --purge   # … and the state, the logs and the machine configuration
```

`--uninstall` keeps `/var/lib/wsl-care` (history, run details, container starts), `/var/log/wsl-care` (run logs) and
`/etc/wsl-care` (the machine layer). `--purge` removes exactly those three and `/run/wsl-care.lock`, and names each
before removing it — only while it holds the run lock (`flock`, from util-linux): a wsl-care run started by hand that
still holds it (`sudo wsl-care collect` in a terminal) refuses the purge, nothing of the state is removed, and the same
command purges once that run has ended. Never removed: sysstat and atop (other tools may use them), `/etc/wsl.conf`,
every user's `~/.config/wsl-care`.

**The installer's ceilings** — environment variables, whole seconds (a malformed value is refused before anything runs):
`WSL_CARE_INSTALL_SYSTEMCTL_SECONDS` (900) for each `systemctl` call that waits for a systemd job,
`WSL_CARE_INSTALL_DOCTOR_SECONDS` (120) for the final health check to turn healthy, `WSL_CARE_INSTALL_RUN_WAIT_SECONDS`
(600) for an upgrade to wait for a run in flight, `WSL_CARE_INSTALL_PROGRESS_SECONDS` (30) for how often it says so, and
`WSL_CARE_INSTALL_STATUS_SECONDS` (30) for one status question during that wait. Each wait ends on the wall clock; a
command that ignores SIGTERM at its ceiling is killed 10 s later (30 s for the first full run, which may take several
minutes and is announced before it starts).

## Configuration

Settings come from three layers, each overriding the last: the defaults embedded in the binary, the
machine file, and the user file. The daemon runs without VS Code, so the files are the truth; the
extension's settings are an editor for the user file.

| Layer | Linux (WSL distro) | Windows |
|---|---|---|
| machine | `/etc/wsl-care/config.json` | `%ProgramData%\wsl-care\config.json` |
| user | `~/.config/wsl-care/config.json` (`$XDG_CONFIG_HOME` honoured) | `%APPDATA%\wsl-care\config.json` |

```bash
wsl-care config get                      # every setting, its value, and the layer it came from
wsl-care config get volumes.anonymousMaxGb --json
wsl-care config set volumes.anonymousMaxGb 25   # validated; a refused value exits 2 with one "wsl-care:" line
wsl-care config reset volumes.anonymousMaxGb    # back to the machine/default value
```

`config set` and `config reset` write only the user file, atomically. A layer that is unreadable,
not JSON, or holds an unknown key or an out-of-range value never stops the daemon and is never
replaced by defaults: the run becomes **observe-only** (collect and report, no cleanup), every answer
carries `configError {file, line, message}`, and `config set` still works — it keeps the keys it could
still read, drops the rest by name, and moves a file it cannot parse to `config.json.broken-<utc>`
(`-2`, `-3`, … appended when that name is already taken; an earlier broken file is never
overwritten). A repair that loses anything besides the key you are writing also writes `dryRun = true` into the user
file and says so — a lost `auto.A4 = false` would otherwise be `auto.A4 = true` (its default) at the next timer run —
so the timer only previews until you have checked your settings and run `wsl-care config set dryRun false`. A command
that is refused (too large, a broken coupled rule) moves nothing: the broken file stays where it was, and a broken file
the daemon could read is copied aside, so it stays in place until the repaired one replaces it. Every `wsl-care:` message is one line: control characters in what it quotes — a key
you typed, a key read from the file, a path — are shown as `?`.

**What a setting can and cannot do** (plan §15q R1). No setting is free text: `processes.families` takes only the named
families (`dotnet-build-servers`, `testhost`, `docker-desktop-proxy`, `language-servers` — the C# language server, an A11 suspect once its VS Code window closed —, `vscode-server` — which
also matches the VS Code server itself, daemonised and so "orphaned": do not list it for A11 —, `node` — never `other`, the
catch-all, and never `ai-agents`), `distro` a distribution name, `archive.baseFolder` an absolute path — and that one only
in the MACHINE file (`config set` refuses it; a user-file value is ignored). No setting changes what may run or be
deleted — only when a declared cleanup runs and with which bounded number. `contracts/config-keys.json` lists every key
with its range, its default and what it means to a root run.

**Every number is a setting** (the owner's rule, plan §15q *E7.S2c*). Behaviour — thresholds, ages, triggers, the walk's
interval, how many processes the top list keeps — is an ordinary key. A limit on what ROOT reads, does or waits for — a
command's timeout, an output or file cap, a queue, a walk's budget, the timer's period (`timer.periodHours`, a divisor of 24),
the units' `Nice` / `MemoryMax` / `TimeoutStopSec`, the timer run's limit (`timer.runLimitMinutes`), the watchdog that calls a run
that makes no step for 20 min wedged (`running.noProgressMinutes`) — is a **machine-file-only** key with a hard range: lower it freely, raise
it only up to its maximum (a few, like `requests.graceSeconds`, may only be raised). Limits that depend on each other are
checked together (a wedged time of at least three heartbeats, a stop ceiling 30 s above the unit's own stop, …): a
contradiction from the machine file is a `configError` naming the rule (the values below stay in force), one from your file a
notice — the value not taken — and `config set` refuses it. `contracts/config-keys.json` lists each key's range
and default; the defaults are the values this build always had. A unit value changed in the machine file takes effect
when the installer writes the drop-ins again (`doctor`'s `unitConfig` says so). `status --json` carries `limits` — the
values a client mirrors instead of copying (`contracts/status-limits.json`).

**The user file and root.** The root timer reads your user file every 4 h, so it reads it the way root reads a file
another account controls: a regular file you own, nobody else may write it, reached from your home through NO link
(a linked `~/.config/wsl-care` counts too), never waited on, at most 256 KiB. Anything else — a link, a FIFO, a
group-writable file — is a `configError` saying how to fix it, and the run is observe-only; `config set` repairs a linked
layer by replacing the LINK with a regular file holding the values it read (the file it pointed at is untouched). A run
whose configuration was refused prunes no log folder (it does not know your retention). Two more rules, each said as `configNotices {file, line, key, message}`
in `status`, `doctor`, `config get` and the run's detail, never an error: root's own log level and log retention only
tighten from your file (a level no higher, a retention no shorter than the layers below; 0 keeps for ever); and when
WSL interop is DISABLED in the distro — then your account cannot become root on its own — every root-effective value of
your file only tightens (`dryRun` on, an `auto` off, a longer age, a larger trigger) — and your own unprivileged
`status` / `config get` / `doctor` then say which of your values the root timer ignores — the reason once, as the notice
without a key, then one short line per value. Every run's detail lists the
settings it used that did not come from the defaults, with their layer (`config`), and `status` carries the user file's
SHA-256 (`userLayerDigest`).

Run logs go to `/var/log/wsl-care/{yyyy-MM-dd}/wsl-care-{HH-mm-ss}-{pid}.log` (Linux) or
`%LOCALAPPDATA%\wsl-care\logs\…` (Windows), one file per run, UTC — a run that may not write
`/var/log/wsl-care` (yours, without root) logs to `$XDG_STATE_HOME/wsl-care/logs` instead; `logging.minimumLevel` and
`logging.retentionDays` (14) are settings like any other; the console log goes to **stderr** (stdout
carries only answers), so a refusal's `wsl-care:` message may sit beside a log line there. Set
`WSL_CARE_ROOT=<dir>` to lay every
path — configuration, state, logs, the protected folders — out under one directory; tests and
scenario runs use it so nothing real is ever touched.

## Status

```bash
wsl-care status --json    # the fast snapshot the extension reads; schemaVersion 1
wsl-care status           # the same, as a few lines for a terminal
```

`status` answers in well under 2 s — plus the MCP servers' CPU window (1 s by default) when an AI agent's MCP server runs
that its CPU ledger has no baseline of —
and starts no process: it reads `/proc` and the cgroup tree (inside
the distro) or asks Windows for its counters (`wsl-care.exe`), and nothing else. Inside the distro it
reports VM memory (`MemAvailable`, page cache, anonymous and inactive anonymous memory, shared memory,
swap), free high-order blocks in zone Normal (order 4 and 7), pressure (PSI) for memory, I/O and CPU, the
top 30 processes by `RssAnon` + `RssShmem` with their family, owner, age, CPU time, working directory and
command line (secret-looking values redacted, 200 characters), the processes working under `/mnt/`, the
containers' memory from their cgroups, the memory nobody can name (*unattributed*, or *inconsistent
sample* when the parts exceed the whole) and `df /`. On Windows it reports host RAM, the system drive and
the VM's `vmmemWSL` working set. Each binary names the other as the source of the side it does not read.

`docker stats` and the Windows clock take a slow process, so only a full run samples them; `status`
reports them from the last full run in `history.jsonl` with their age (`collect`, below). Until a full run exists
they are `"available": false` with that reason. `status` also counts the container starts of the last 24 h from the
follower's files (`containerStarts`: complete, or partial with each gap named) and shows the daily folder sizes of
the last run that measured them (`folders`, with their growth). Every figure that cannot be
read is `"available": false` with a `reason`, never 0. A broken configuration layer is named in the answer
(`observeOnly`, `configError`) and `status` still answers.

**Verdicts.** `status --json` also answers `verdicts` — every threshold of the plan as the same records a full run writes
into its detail (`id`, `level` = `ok` / `warn` / `critical` / `unknown`, `value`, `limit`, `reason`), each with its
`basis`. The thresholds the fast sample decides (memory, page cache, inactive anonymous memory, swap, fragmentation,
pressure, the VM's ceiling, `/`) are judged **now**, with the configuration in force — `wsl-care config set
thresholds.memAvailableWarnPercent 30` changes the next answer (`basis.source: "sample"`). The ones only a full run can
judge (kernel allocation failures and OOM kills, the journal, clock jumps and drift, the Windows Time service, which clock
is wrong against an independent reference and whether two time-keepers fight over the distro's clock — `clock.timeService`,
`clock.reference`, `clock.fight` since 2026-10-08 — failed units, sysstat / atop,
`discard`, the Docker and npm figures) are carried exactly as the newest full run recorded them, with its run id and
age (`basis.source: "fullRun"`), or are `unknown` with the reason when no full run is recorded; a setting changed since
reaches them at the next full run. The text form prints one line: `verdicts: 1 critical (memory.fragmentation), 2 warn
(…), 12 ok, 8 unknown`. And `productVersion` names the build exactly as `--version` prints it.

**What runs, what this build can do, the last cleanup.** `status --json` also answers `actions` (the action ids this
binary holds for its own side, in the order a run takes them), `capabilities` (what this build can do beyond the first
release's verbs: `act.shownList`, `runs.show`, `running.block`, `logs.instantRange`, … `config.contract`, `agents.list`, `agents.probe`, `config.agentsExtra`, `status.mcpServers` — what a client acts on, never the
version number), `running` — `none`, `queued` (a run accepted and not started yet), `live` (acting: the run, its actions,
the one it is on, its pid and how old its heartbeat is), `wedged` (alive, heartbeat older than 30 s — nothing is killed),
`dead` (its process is gone and no run has swept it yet — status only REPORTS it; the next root run records it
`interrupted`), `unknown` (the pid cannot be inspected) or `unreadable` — and `lastCleanup` (the newest run that removed
or freed something: run id, start, trigger, objects removed, bytes freed; `available: false` with the reason before the
first). `status` stays read-only and needs no root for any of it. The text form adds a `running:` and a `last cleanup:` line.

**MCP servers of the AI agents.** `status --json` answers `mcpServers` (and every full run's detail carries the same
block): every process of a watched MCP server (`mcpServers.watched`, closed over the built-in catalogue — `coai-mcp`, and
`playwright-mcp` as `npx @playwright/mcp` runs it: the `node` process whose script is `…/node_modules/…/playwright-mcp`, never
its `npm exec` launcher; both watched by default — plus the user's own programs, `mcpServers.programs`: program file names such as `["creds-mcp"]`, at most 32,
default empty, each refused when it names an AI agent's program, an interpreter, shell or launcher such as `node`,
`python3` or `npx` (every script they run has that argv[0]), `wsl-care` or a catalogue server — a name only a later
build refuses is left out of the layer with a notice, never an error; such a server has no known
log layout, so its starts are the `liveYounger` lower bound) whose parent chain reaches an AI-agent session, or that was
left behind when its agent died (`orphaned`). Per
instance: pid, owner (the agent session's pid, name and redacted command line), user, state, age, **CPU % of one core
measured over the interval since that process's previous sample** (`cpuBasis: "interval"`, `cpuIntervalSeconds`: from a
small ledger of each instance's CPU ticks, `mcp-cpu.json`, a point between `mcpServers.cpuIntervalMinSeconds`, 120, and
`mcpServers.cpuIntervalMaxMinutes`, 20, old — a server that burns in a burst about once a minute is seen; the 2026-10-07
evening's servers read `idle` at 0 % in a one-second window) **or, with no such point, across a window**
(`cpuBasis: "window"`, `mcpServers.cpuWindowMilliseconds`, default 1 000 ms: two `/proc` reads; the wait happens only for
an instance without a baseline). The ledger is the caller's own: an unprivileged `status` keeps it in
`$XDG_STATE_HOME/wsl-care/` (default `~/.local/state/wsl-care/`, the one file `status` writes, and only while an MCP server
runs), the root timer in `/var/lib/wsl-care/`; `status` as root reads root's and writes nothing; `cpuBaseline` says where
and whether this answer's readings were kept. Memory held (`RssAnon + RssShmem`), the newest write of its own run log, and a `kind`:
`starting` (below `mcpServers.idleCpuPercent`, 2 %, and younger than `mcpServers.idleMinAgeMinutes`, 10), `idle`,
`busy`, `busyWithoutActivity` (busy while its log was last written more than `mcpServers.activityWindowMinutes`, 10, ago
— the state measured on 2026-10-06: seven `coai-mcp` at 27–54 % of a core each with no log line for 10+ minutes) or
`unknown`. Per server: the instances and the **starts in the last `mcpServers.startsWindowMinutes`** (10), counted from
the names of its run logs (`~/.local/share/coai-mcp/logs/<UTC day>/coai-mcp-<HH-mm-ss>-<pid>.log`, names and dates only,
no file opened), each start listed with its time, pid, last log write and whether it still runs (`startTimes`, newest
first, at most `mcpServers.maxStartsListed`, 50) — the restart storm of 2026-10-06 was 34 starts in 10 minutes, and it began
at 16:50Z, before the update it was first blamed on, which only the start times show. Three verdicts judge them now:
`mcp.instances` (warn above `mcpServers.warnInstances`, 12), `mcp.cpu` (warn above `mcpServers.warnCpuPercent`, 100 % of
one core in total), `mcp.starts` (warn when a server started more than `mcpServers.warnStarts`, 10, times in the window).
Read-only towards the servers: nothing is stopped. The Windows binary answers the block unavailable — `coai-mcp.exe` on Windows arrives with
the Windows collectors (E11). Capability `status.mcpServers`; `limits` publishes `mcpCpuWindowMilliseconds` and
`mcpLogListMilliseconds`, the two waits a client's `status` ceiling must allow for. The text form adds one line:
`mcp servers: 7 (0 idle, 7 busy without a log write), 2.6 cores (7 over their last interval, 0 over a 1000 ms window),
0.40 GiB; starts coai-mcp 34 in 10 min`.

**Compatibility.** `schemaVersion` changes only on a breaking change; a field added later (like `verdicts`,
`productVersion`, `actions`, `capabilities`, `running` and `lastCleanup`) never bumps it, so a reader ignores keys it does
not know, treats an absent newer field as "update the daemon to see this", and reads an enum value it does not know as
unknown, never as a crash.

## Preview

```bash
wsl-care preview --all --json   # every cleanup row with its count and reclaimable bytes; schemaVersion 1
wsl-care preview --all          # the same, one line per row
```

`preview` asks Docker — read commands only: `version`, `system df [-v]`, `volume ls --filter dangling=true`,
`container inspect` through a template that names its fields (never a container's environment) — and answers one
row per cleanup of the 2026-10-02 one-time run, each with the age limit in force and the `auto` switch that lets the
timer run it: A4 unattached anonymous volumes, A5 stopped containers (A5Testcontainers apart), A6 dangling and
A6Unused unused images, A7 build cache, A8 (the npm cache) and A9 (the apt cache, disabled snap revisions) from the
daily folder walk of the last full run, with its age. Next to them: the
named volumes no container uses (kept — a person decides), Docker's own totals per type, and a hygiene audit
(containers logging without `max-size`, Docker Desktop's builder GC, forgotten buildx builders). Nothing is removed.

When Docker cannot answer, every Docker figure is `"available": false` with `docker.kind` —
`notInstalled`, `daemonStopped`, `socketRefused`, `timedOut`, `commandFailed`, `refused`, `unparseable` — and the
reason; never 0, and the exit code is still 0. Each Docker command has its own ceiling (10 s for the first probe,
2 min for `system df`) and is killed with its process tree when it passes it.

A4's age is the first time the daemon saw a volume unattached, kept in `/var/lib/wsl-care/volume-seen.json`. Only
a process that may write the state directory records it — root, on an installed machine (the timer); run as your
own user, `preview` reads the record, writes nothing and says `read-only` in `volumeSeen`.

## Collect — the full run

```bash
sudo wsl-care collect --json   # the timer's target: everything, recorded; schemaVersion 1
wsl-care collect               # as yourself: the same measurements, printed, NOTHING recorded ("read-only")
```

`collect` is everything at once: the fast snapshot of `status`, Docker's full numbers and the cleanup rows of
`preview`, `docker stats`, the distro's clock against Windows' (`powershell.exe`, its launch latency subtracted, which also
prints the Windows Time service's state) and against an independent reference (the HTTP `Date` of `clock.referenceUrl`, one
`curl --head`, else timesyncd when it is synchronised), the
health of the distro (failed units, the journal's size and how far back it reaches, clock jumps, the kernel's page
allocation failures and OOM kills since the last run, time sync, `wsl-pro.service`, `discard` / `fstrim.timer`, whether
sysstat and atop still collect), the `.wslconfig` audit, once a day the folder sizes (`~/.npm`, `/var/cache/apt`,
disabled snap revisions, `~/git/_wt`, `~/.nuget/packages`, `~/.cache`, `~/.vscode-server`, `bin/` + `obj/` under
`~/git` — links never followed, a ceiling per folder), and the container starts of the last 24 h. Every threshold of
plan §4 is judged over it: `ok`, `warn`, `critical`, or `unknown` when the figure could not be read. The VM's memory
ceiling is shown against the recommendation `memory=36GB` in `.wslconfig` and is red above 90 % — the recommendation is
shown, `.wslconfig` is never written. Every command is a read; each has its ceiling.

Recorded in this order: the run's detail `/var/lib/wsl-care/runs/{yyyy-MM-dd}/{runId}.json` (written whole or not at
all), then one line in `history.jsonl` that names it, then the run log is closed. Each run first reconciles: a detail
whose line is missing gets a line with outcome `interrupted`; a line whose detail is gone is reported *detail lost*. It
then keeps 90 days of history (a detail goes only after its line did) and 14 days of container-start files. One run at
a time (`run.lock`). Run without root it measures and prints the same, writes nothing, says `read-only: run as root to
record`, and logs to `$XDG_STATE_HOME/wsl-care/logs`.

**The timer's full run also acts.** When the timer starts `collect --timer` (its unit's `ExecStart` says so; `INVOCATION_ID`, which every descendant of a systemd unit inherits, is never read), the run measures, then — under
the same lock, before anything is recorded — passes every action through the action engine as the timer (see *Act*
below: each action's `auto` switch, trigger, idle wait and the first week's dry run decide), and records the measurement
and the actions in the SAME run: one detail (with a `timerPass` part listing every action's preview and result) and one
history line. A `collect` you start yourself, or the panel's *Run full check now*, measures only; a button's `act` is its
own run. An action that fails there is recorded and logged; the exit code stays 0.

`sudo wsl-care collect --detach [--json]` hands the same run to systemd and answers at once — see *Detached runs* below.

Exit codes: 0 recorded (or read-only) · 1 the run could not be recorded (the reason on stderr) · 2 usage · 75 another
run holds the lock · 70 a defect.

## Doctor

```bash
wsl-care doctor --json   # healthy: true|false, one check per part, the versions; schemaVersion 1
wsl-care doctor
```

Read-only: the configuration (observe-only is a problem), the state directory, the last run (older than the timer's
period plus `timer.lateSlackMinutes` — 5 h by default — or failed), lost run details, whether every unit drop-in still says
what the machine layer says (`unitConfig`), `wsl-care.timer` / `wsl-care-events.service` / `sysstat.service` / `atop.service`, whether
sysstat and atop wrote in the last 30 minutes, whether the events follower is current, and the versions of `wsl-care`,
Docker, systemd and the kernel. Root reachability is the extension's check (`notChecked` here). It exits 0 whatever it
finds; `healthy` is the verdict.

## Busy — may an agent start heavy work now?

```bash
wsl-care busy --json   # state: calm | busy | unknown, reasons, the three PSI files, the load
wsl-care busy          # one line: "busy: PSI some avg60 cpu 28.5, io 1.2, memory 0; load 36.1 — cpu avg60 28.5 > 20 (…)"
```

It reads `/proc/pressure/{cpu,io,memory}` and `/proc/loadavg` and nothing else, in milliseconds. It does not walk the
processes, does not open the MCP window, reads no history and writes nothing; any user may run it. The state:
- **busy** when a pressure (PSI `some avg60`) is above its key: cpu `thresholds.cpuPressureWarnPercent` (20), io
  `thresholds.ioPressureWarnPercent` (10), memory `thresholds.memoryPressureWarn` (10). Each crossing is named with its value
  and key.
- **calm** when all three were read and none crosses.
- **unknown** when one could not be read (a kernel without PSI, the Windows binary).

The load is shown, never judged. `status` shows the same rule as the verdicts `pressure.cpu` and `pressure.io`, so the
extension's status bar warns while the machine is busy. `memory.pressure` is a different question (has the VM been
struggling, avg60 or avg300) and keeps its rule.

**The agent-side contract.** Poll it before a heavy step: a build, a test suite, an install.

| exit | meaning | what the agent does |
|---|---|---|
| `0` | calm, or unknown (`state` says which) | go |
| `83` | busy (`machineBusy` in `contracts/exit-codes.json`) | wait with a bounded, JITTERED backoff — about 30 s, 60 s, 120 s, each ± 25 %, at most 10 minutes in all — then go anyway and say so |
| anything else | the signal itself failed (an old binary without the verb answers 2, usage) | say so in the session's log, then go: a broken signal never blocks work |

It is advice, never a lock or a queue: the daemon starts and stops nothing because of it. The jitter is what keeps twenty
waiting agents from all starting at the same second when the pressure falls.

```bash
waited=0; step=30
while true; do
  wsl-care busy >/dev/null 2>&1; rc=$?
  [ "$rc" -eq 83 ] || { [ "$rc" -eq 0 ] || echo "wsl-care busy failed ($rc); going on"; break; }
  [ "$waited" -ge 600 ] && { echo "machine still busy after ${waited}s; going on"; break; }
  pause=$(( step * (75 + RANDOM % 51) / 100 )); sleep "$pause"; waited=$(( waited + pause ))
  [ "$step" -lt 120 ] && step=$(( step * 2 ))
done
```

## Events follow — container starts

```bash
sudo wsl-care events follow          # the wsl-care-events unit's target: runs until SIGTERM / SIGINT
sudo wsl-care events follow --once   # catch up (markers, one backfill) and stop
```

Docker forgets: its event buffer is in memory and short (91 seconds of healthcheck events when measured here), and
`docker ps` forgets removed containers. The follower records every container start (image, name, Testcontainers label)
in `/var/lib/wsl-care/container-starts/{yyyy-MM-dd}.jsonl`, with markers that say how far the record is complete. When
Docker is down it waits in the process — 5 s, doubling to 5 minutes — and writes nothing until Docker answers; then ONE
backfill recovers what Docker still holds, and whatever it cannot prove it holds is ONE `gap` marker with its reason.
A 24-hour count that overlaps a gap is **partial** and names it, until a whole 24 h lies after the gap. It exits 0 on
a signal (writing its stop marker), 1 when it may not write the state directory, 75 when another follower runs, and
otherwise only on a defect — so `Restart=always` does not cycle while Docker is merely down.

## Act — run a cleanup

```bash
sudo wsl-care act A4,A5 --preview --json       # what they would do, from LIVE state; nothing run but reads, nothing written
sudo wsl-care act A5,A4 --confirm --json       # do it (in the fixed order A5 -> A4): one run at a time, recorded
sudo wsl-care act A4 --confirm --manual --only shown.txt --json   # the panel's button: only the volumes its preview SHOWED
wsl-care act A10 --preview                     # as yourself: refused whole ("needs root", exit 77) - nothing read, nothing written
```

Every answer names `productVersion`. A4's preview answer carries `shown` — EVERY volume name it selected (the 20
`items` are for reading; `shown` is what a button sends back, at most 10 000 — past that `shownTruncated: true`, and only
the shown names go). `--manual` (the panel) and `--timer` (the systemd timer) are exclusive: both together are refused. A
confirm cut off by a signal — Ctrl+C, SIGTERM, or SIGHUP when the terminal or the `wsl.exe` that started it goes away —
kills its child and records itself `interrupted` naming the signal: the action it was on is `interrupted` (with what Docker
had already confirmed removed, for A4 / A5), the actions it never reached `interrupted / not run`; it exits 130.

Every `act` runs as **root** (the timer is root; the panel's button reaches root through an argv allowlist). Started by
anyone else it refuses the whole run before the lock or any state is touched. A destructive run from the CLI needs
`--confirm` — the button passes it after you confirmed the preview, together with `--manual` (the run is recorded with
the trigger `manual`; a terminal's as `cli`). Every action previews from LIVE state, removes only what it re-checked,
counts an object already gone as *already gone* (not a failure), and MEASURES what it freed:

| Action | What it runs | Freed is | Notes |
|---|---|---|---|
| `A4` | `docker volume rm <64-hex>…` — never `prune` | the `system df -v` sizes, read just before, of exactly the volumes Docker confirmed | anonymous volumes only, unattached, no `wsl-care.keep=true`, first seen ≥ `volumes.anonymousOlderThanDays` ago; refuses on Docker < 23 or an unreadable version; a button run removes only the volumes passed with `--volume <name>` (repeatable) / `--only <file>` (one name per line) and refuses without them |
| `A5`, `A5Testcontainers` | `docker rm -v <id>…` — never `-f` | the confirmed containers' layers + their anonymous volumes Docker no longer lists | stopped ≥ `containers.stoppedOlderThanDays` / Testcontainers ≥ `containers.testcontainersOlderThanHours`; keep label respected; a container that started since is refused by Docker and kept |
| `A6`, `A6Unused` | `docker image prune -f` / `-a -f --filter until=<h>h`, both with `--filter label!=wsl-care.keep=true` | Docker's own "Total reclaimed space" | never an image any container uses; no prune when the preview selects nothing |
| `A7` | the timer: `docker builder prune -f --max-used-space <buildCache.maxGb>GB` (or `--keep-storage`, whichever THIS Docker's help lists); a button: `-a -f` | Docker's own "Total:" | waits for an idle machine |
| `A8` | `npm cache clean --force` as the target user | `~/.npm` walked before and after | npm not installed = a skip |
| `A9` | `apt-get clean`; `snap remove <name> --revision=<n>` of revisions STILL disabled | `/var/cache/apt` before/after + the snap files gone | a missing tool skips its part |
| `A10` | `journalctl --vacuum-time=<journal.keepDays>d` | the journal files gone after | archived files only |
| `A11` | `SIGTERM`, `SIGKILL` after 10 s — by pid AND start time (a `pidfd`), never by name | memory, not disk (not counted) | off by default; only suspects: orphaned, in `processes.families`, older than `processes.idleOlderThanHours`, no terminal, not root's — and only the TARGET user's processes (no single target user: no suspects) — no CPU in a 5 s window and none since |
| `A18` | the same signals, of the target user's ORPHANED AI-agent processes (claude, codex, gemini, …) — a **button only**: the timer never selects it, whatever any setting says (it has no `auto` switch); a button run ends only the processes its preview SHOWED (`--process <pid:start>`, each still eligible) | memory, not disk (not counted) | only when ALL hold: the `ai-agents` family, the target user's, re-parented to init (never a `systemd --user` service), no terminal, no child process, attributable to one catalogue agent whose session layout is confirmed and whose program resolves into that agent's own install, **no CPU for `processes.aiAgentsIdleHours` (default 4 h) measured** — the timer's full runs record each such process's CPU by pid + boot + start time, on the wall AND the monotonic clock, in `/var/lib/wsl-care/agent-cpu.json` (0600), so the first runs end nothing and a gap or a clock jump restarts the wait — an environment that moves no agent home, and sessions of that agent found, none written in the same window; each re-read just before its signal |
| `A19` | the same signals, of the target user's IDLE MCP servers (`coai-mcp` and `playwright-mcp`, the watched catalogue, and the user's own `mcpServers.programs`; Playwright at work keeps a browser child, which keeps it) — a button AND the timer: `auto.A19` is **on by default** (the owner's decision of 2026-10-08), and the daemon's dry-run rules (`dryRun`, the first week) still decide whether the timer stops anything or only records what it would; a button run stops only the processes its preview SHOWED (`--process <pid:start>`) | memory, not disk (not counted) | only when ALL hold: an instance of a watched MCP server (the PROGRAM is the server — an agent is never one), the target user's, never root's, no terminal, no child process, the process the snapshot saw, and **no CPU for `mcpWatchdog.idleMinutes` (default 60 min) measured** by identity over the same CPU history A18 uses (which now records the MCP servers too) — `mcpWatchdog.orphanIdleMinutes` (default 10) for a server re-parented to init (its agent died) — but never an orphan of a user-listed program, whose name may then be another program's; on the 4-hour timer that is a floor (a server is stopped at the first run that sees it unchanged since the previous one); the agent's session may need `/mcp` to reconnect a stopped server |
| `A12` | deletes the Playwright browsers no project's `browsers.json` references; `dotnet nuget locals http-cache --clear` as the user | each folder before, counted when gone | a button only; refuses the Playwright part when what is referenced cannot be told |
| `A14` | deletes VS Code / Cursor / Windsurf server builds no process uses, keeping the newest 2, and `.obsolete` extensions | each folder before, counted when gone | every delete judged by the deletion policy |
| `A17` | `pnpm store prune`, `uv cache prune`, `pip cache purge` as the target user | each cache before/after | `cargo sweep` is not run (it would delete under `~/git`); Gradle prunes its own caches |
| `A1` | `sync`, then `sysctl -w vm.drop_caches=1` (never another value) | memory, not disk: the page cache and `MemAvailable` before/after | the timer: `MemAvailable` below `thresholds.memAvailableActPercent`, or a page cache above 12 GiB with less than 30 % available; waits for an idle machine on the timer |
| `A2` | `sysctl -w vm.compact_memory=1` | the free 512 KiB (order-7) blocks before/after | the timer: after A1 ran, or AT ONCE — without waiting for idle — when no order-7 block is left or the kernel logged a `page allocation failure` since the last run |
| `A3` | `dotnet build-server shutdown` as the target user | the build servers gone after (memory, not disk) | the timer: a server alive for `buildServers.idleHours` AND none used CPU for `buildServers.idleMinutes` (default 60, measured by identity over the timer's CPU history, which now records the build servers too — the command stops every server at once, so one that works, or one not measured yet, holds the timer; on the 4-hour timer the window is a floor); refused while any `dotnet build`, `test` or `run` is alive, a button too (a button is not held by idleness) |
| `A15` | `fstrim -av` | what fstrim reports trimmed per filesystem (returned to the VHDX) | the timer: weekly, only without `discard` on `/` and with `fstrim.timer` off; waits for an idle machine |
| `A16` | `chronyc makestep` (chronyd running) or `hwclock -s` | the clock offset before/after | skipped when time sync reports synchronised or the clock agrees — and, since 2026-10-08, unless an independent reference shows the step brings the distro CLOSER to true time: a wrong Windows clock, or no reference at all, is never stepped to; the timer: once per drift seen on two observations 5 minutes apart (`clock.maxDriftSeconds`); at most once an hour |

As root, every per-user path — the daily folder walk, the caches above, the user configuration layer — is the
**target user's** home (`/etc/wsl.conf` `[user] default=`, else the single login account), never root's; when the target
is ambiguous the user layer is skipped: the timer runs no action at all (a switch you turned off in your layer cannot be
seen, and a default must not turn it back on), a button's machine-scoped actions still run, every user-scoped action refuses
naming the accounts (set `[user] default=` in `/etc/wsl.conf`, or `install.sh --set-default-user <name>`). `config set` / `config reset` refuse to run as root for the target user (a
root-owned file would lock them out of their own settings) with exit **81**: run them as yourself.

**A tool's own configuration can move its cache.** `npm` (`~/.npmrc` `cache=`), `pip` (`pip.conf` `cache-dir`), `uv`
(`uv.toml`) and `pnpm` (`store-dir`) read their configuration files from the user's home, and A8 / A17 run them as that
user. So before its own cleanup each tool is ASKED where its cache is (`npm config get cache`, `pip cache dir`, `uv cache
dir`, `pnpm store path`); a cache inside an AI agent's folder — or an answer that cannot be read — means that tool is not
run, said in the run. Not seen: a cache moved only by a variable in the user's shell — the tools run with a clean
environment, so such a variable does not apply to them either.

What a run does, in order: takes THE run lock (`/run/wsl-care.lock`, shared with `collect` — the second one refuses
with exit 75 and waits for nothing); sweeps a `running.json` a dead run left (recorded as `interrupted`) or refuses when
a live run is still acting (75) or has stopped beating (**wedged**, 76 — nothing is ever killed automatically); then for
each action: the live preview, its gates, the run, the measured result; a failing action is recorded and the run goes
on. While it acts, `/var/lib/wsl-care/running.json` names the action, the pid and its start, and a heartbeat every 5 s.

Under the systemd timer (`--timer`) each action also needs its `auto.<A#>` switch and its trigger, heavy
actions wait for an idle machine, and the timer **runs dry for its first 7 days** (from its first action pass, recorded
in `/var/lib/wsl-care/first-timer-run.json`) and for as long as `dryRun` is on — a dry run previews and records what
it would have freed. A button never runs dry.

Every command any action or collector starts passes ONE filter first: the never-list (no shell anywhere, no
`docker system prune` / `docker volume prune`, no `vm.drop_caches` but 1, no `git worktree prune`, no `wsl --shutdown`,
no delete by command, no path under `~/git` or an AI agent's folder, …) and then deny-by-default — only an argv a
component declared as a template runs. A tool run as the target user goes through `runuser -u <user> -- <full path>`
with a clean environment.

Exit codes: 0 previewed / recorded · 1 not recorded · 2 usage (unknown or unbuilt action, the other side's action) ·
3 an action failed · 75 busy · 76 wedged · 77 needs root · 78 observe-only (an invalid configuration layer) · 130 interrupted.

### Detached runs — what the panel's buttons use

```bash
sudo wsl-care act A10 --confirm --manual --detach --json          # answers {"result":"accepted","runId":…,"unit":"wsl-care-act@<runId>.service"} at once
printf '%s\n' <64-hex>… | sudo wsl-care act A4 --confirm --manual --detach --only - --json   # A4's shown list on stdin
sudo wsl-care collect --detach --json                              # the full run, the same way
wsl-care runs show <runId> --json                                  # follow it: queued → running → done / refused / interrupted
sudo wsl-care act --stop <runId> --json                            # a WEDGED run only: systemd stops its unit
```

A confirm started from a window that may close (a VS Code reload) must outlive it, so `--detach` never runs the work
itself: it writes the REQUEST `/var/lib/wsl-care/requests/<runId>.json` (0644, created exclusively — the persisted
*queued* state `status` and `runs show` read) and runs `systemctl start --no-block wsl-care-act@<runId>.service`, the
template unit this install ships, whose `ExecStart` is `wsl-care act --request <runId>`. That run re-reads the request
through the same hardened reader `status` uses (root's, no group / other write, at most 1 MiB, validated), records
itself under THAT run id, and removes the request once its `running.json` stands. When another run holds the lock it is
recorded `refused` with the reason — never a silent busy. Every root run (`collect`, `act --request`) first sweeps the
request folder — and so does every `--detach`, under the lock: a request whose run has a history line only loses its file; one
older than 60 s on the monotonic clock (or written in an earlier boot) whose unit has no queued job and is not active is
recorded `interrupted` and goes; one that cannot be used is recorded `refused` and goes. `--only -` reads the shown list from stdin under the
same 1 MiB cap and a 10 s ceiling for the end of input. There is no synchronous fallback: without systemd a detach is
refused (69). At most 32 requests wait at once (73). `act --stop` asks systemd to stop a wedged run's unit — only when
its process lives in `wsl-care.service` or its own `wsl-care-act@<runId>.service`; SIGTERM lets it record itself
`interrupted`, and one that is still there after 90 s is killed and recorded by the next root run's sweep with that reason.

A request an earlier boot left behind is reported `dead` by `status` (`interrupted` by `runs show`) until the next root run
records it. An upgrade waits at most 10 minutes for a run in flight, saying so every 30 s; when the installed binary cannot
answer at all, `WSL_CARE_INSTALL_SKIP_RUN_WAIT=1` skips that wait.

A start that times out asks the unit: accepted when systemd holds the job, `result: unknown` (exit 0, the request kept) when
its state cannot be read. Nothing the daemon writes is group or world writable, whatever the umask.

Exit codes of the detached verbs: 0 accepted / unknown / recorded / stopping · 2 usage, or nothing to stop · 69 no systemd · 71 the
unit would not start (its request removed) · 73 the request budget is full · 75 / 76 / 79 busy / wedged / state unreadable
(at `--request` time RECORDED as `refused`, and a success for the unit) · 77 needs root · 80 no request names the run
(a no-op).

## Logs and runs — what the runs of a period did

```bash
wsl-care logs --json                              # today (UTC): freed per action and in total, runs with/without a cleanup, max/min
wsl-care logs --period yesterday
wsl-care logs --period 2026-10-01 --action A4     # one UTC day, one action, every volume it removed
wsl-care runs --period 2026-09-28..2026-10-02 --json   # every run of a range: trigger, outcome, dry run, actions, freed
wsl-care logs --from 2026-10-02T00:00:00+03:00 --to 2026-10-03T00:00:00+03:00 --json   # a LOCAL day, as two instants
wsl-care runs show 20261002T040000Z-1234 --json     # one run: its state, every object removed and not, commands and exits
```

Read-only: anyone may ask (no lock, nothing written). A run belongs to the UTC day it started. `logs` answers the Logs
page: what was freed in total and per action (with object counts), how many runs did and did not clean anything, the
dry runs apart with what they would have freed, the runs by trigger (timer, button, terminal), the run that freed the
most and the least, each recorded figure's maximum and minimum with its time (`MemAvailable`, page cache, swap, `/`,
Docker reclaimable, container starts) and, per cleanup, every object it removed — and those it did not, with why — from
the run's detail file. Memory actions (A1, A2, A3, A11, A18, A19) free no disk and count no bytes. Periods: `today` (the
default), `yesterday`, `yyyy-MM-dd`, `yyyy-MM-dd..yyyy-MM-dd` (UTC days, at most 366) — or `--from <instant> --to
<instant>`, two RFC 3339 instants with their offset spelt out (`Z` or `+03:00`; a bare date or a time without an offset is
refused, never read in this machine's zone), half-open (from inclusive, to exclusive), at most 366 days: how a client asks
for a LOCAL day, which crosses UTC midnight. `runs` lines carry the `metrics` their history line recorded (a full run's
`MemAvailable`, page cache, swap, `/`, Docker reclaimable, container starts; none for an `act`), and its `kind`: `collect` for
a full check — whatever started or ended it — or `act`; a line's `actions` list only what its actions did, so a full check
that never reached its actions (refused, cut off, swept) has none. A line written before `kind` existed, or whose kind
cannot be known — a request that could not be read, an orphaned run whose detail cannot be read or is of a kind this
version does not know, a dead run whose running state named no kind (and was not an older full check's) or one this
version does not know — has no `kind`. A kind this version does not know (written by a newer version, met after a
downgrade) is answered as written and never taken for `collect` or `act`; the line still counts, and `runs show` answers
such a run's detail as `unreadable` with a `detailProblem` saying why.

`runs show <runId>` answers one run: `done` (with its history line and its detail — every action, every object it removed
and did not remove, every command it ran and its exit), `refused`, `interrupted` (also a run whose process died before
anything recorded it), `running` (its `running` block), `queued`, or `unknown` (nothing names it — never existed here, or
older than the 90-day retention). Read-only like `logs`. Exit codes: 0 answered (an empty period, an unknown run too) ·
2 a period or a run id that is none of these · 4 the history exists but cannot be read.

## AI agents — which are here, and how much they hold

```bash
wsl-care agents list --json             # the catalogue agents found here, sizes from the newest full run (with its age)
wsl-care agents list --measure --json   # the same, the folders walked NOW (at most 60 s), with the five largest sessions
```

`--measure` says on stderr what it is about to walk ("measuring N agent folder(s), up to 60 s…") and each folder as it starts;
stdout carries only the answer.

An agent is found by a binary on `PATH`, an npm global package, or a data folder (`~/.claude`, `~/.codex`, `~/.gemini`,
… — the catalogue, `src_daemon/src/WslCare.Core/Agents/agents.json`, twelve agents). **Nothing is ever started**: a
version is read from disk — a native install's link target (`…/versions/2.1.3`) or the npm package's `package.json` — or
it says "not asked". Every data folder is measured by listing and `stat` alone, never opening a file (a test proves it at
the system-call level with inotify): no link is followed, a folder named `memory` is never entered (of any agent), nor a
folder on another filesystem (a bind mount onto `/mnt/c`, named "different filesystem"). Sessions are counted only where
the layout is confirmed (Claude Code, Codex, Gemini CLI, Antigravity), with the oldest and newest date; elsewhere "—"
with why, never 0.

The root timer's daily full run walks the agents' folders too (one 3-minute budget for all of them; what it does not
reach says "not measured this run"), and records totals, counts and dates — never a session's name. Without `--measure`
the answer reads that run; before the first full run it says so and how to measure. An agent with a folder that was not
measured has no total (the reason is given), never a part shown as the whole. `growthBytes` compares two whole walks, and
`aiAgents.warnGb` / `aiAgents.sessionWarnMb` add a warning. As root only folders count (root neither searches the user's
`PATH` nor reads their packages). Read-only: nothing is written. Every catalogue folder is a protected root (no cleanup
deletes under it) and a never-list name (no command naming it runs). Exit codes: 0 answered · 2 usage.

### Your own AI agents — `aiAgents.extra` and `agents probe`

```bash
wsl-care agents probe /home/me/.local/bin/mycli --json        # as YOU, never as root: what the CLI is, its folders, sizes
wsl-care config set aiAgents.extra - < agents.json            # the list, from stdin only (JSON, at most 1 MiB, 10 s)
```

`agents probe` looks at the file (a regular file this user may start — `access(X_OK)`; it is never started and no byte of it is read),
takes a name from the FILE NAME, and lists the folders such a CLI conventionally keeps (`~/.<name>`, `~/.config/<name>`,
`~/.local/share/<name>`, `~/.cache/<name>`) with their sizes and whether each could be a manual agent's folder. As root it
refuses with exit **81**, naming uid 0 and the fix (set the distribution's default user).

`aiAgents.extra` holds at most 16 entries `{ "cli", "side": "wsl" | "windows", "name", "dataFolders": [1–8 absolute paths],
"sessionGlob" }` (a glob relative to the first folder: letters, digits, `.`, `_`, `-`, `*`, and whole `**` segments). Each
data folder must really lie inside your home, on the home's own filesystem, and must not be, sit inside or contain `~/git`,
a catalogue agent's folder ("already tracked"), any folder a cleanup cleans (`~/.npm`, `~/.cache/ms-playwright`, the NuGet
http-cache, the editor servers, the pnpm / uv / pip caches) or wsl-care's own folders. `config set` judges every entry and
writes nothing when one is refused; the root timer judges them AGAIN on every run. A refused entry is not walked (the
answer says why) — but its folders stay protected all the same: no cleanup deletes under a manual agent's folder, and a
cleanup whose folder overlaps one refuses. Protection is bounded: a folder outside every home, a filesystem root, or one that
is or holds wsl-care's own folders is not protected at all (it would stop root writing its own state) and is reported as a
configuration notice. The `cli` path is never looked at by the daemon. Windows entries are kept for the Windows binary (E7.S5b).

`agents list` finds a binary in your own bin folders (`~/.local/bin`, `~/.cargo/bin`, `~/.npm-global/bin`, nvm's default)
and the system's, whatever the `PATH` a `wsl.exe --exec` call gets; a `PATH` folder under `/mnt/` (Windows' own, on drvfs)
is never searched. A version is read along the binary's own links — a native install's `…/versions/<v>`, or the npm package
the binary runs. A walk or a listing never starts in a folder reached through a link below the home, nor on another
filesystem than the home's; a session's size holds its companion files (Claude Code's session folder and file history,
Antigravity's `brain/` and annotations).

## Extension (preview)

`src_vs_code/` is the VS Code extension **AI OS Care** — Marketplace id `remsoftdev.ai-os-care` (publisher `remsoftdev`,
the extension id `ai-os-care`; both permanent) — in development (E5), not published yet. The daemon it shows keeps its
name, `wsl-care`; the settings and commands keep their `wslCare.*` keys. What exists today
(E5.S1–E6.S4): its client, a **status-bar item**, a **panel** whose cleanups run only after you confirm them, the
**Logs page**, ***Install daemon***, and its packaging and
release pipeline as files and tests (the publisher `remsoftdev` was created by the owner on 2026-10-06 — the E5 live
gate; the Marketplace listing is the owner's, [docs/repo-settings.md](docs/repo-settings.md) steps 9–11).

- ***Install daemon*** (the panel's button when the daemon is not installed, and *AI OS Care: Install daemon…*): the
  distribution is validated first (the setting's pattern, then `wsl.exe --list`); a modal shows the exact command and
  what the distribution needs (systemd, Ubuntu 24.04 / glibc 2.39, `gh` 2.56.0 or newer, `sudo`); on confirmation a
  terminal opens in that distribution, in your home folder, with the command TYPED, never run — `curl -fsSL
  https://raw.githubusercontent.com/oleksandrdubyna88/wsl_care/refs/tags/daemon-v0.1.2/install.sh | sudo sh -s -- --version 0.1.2`,
  pinned to `INSTALL_DAEMON` (0.1.2 — 0.1.0's act unit carries the CollectMode defect), a value of its own never below the
  minimum the extension renders (`MIN_DAEMON_FOR_RENDER`) nor the one it acts with (`MIN_DAEMON_FOR_ACTIONS`, since
  E6.S2; both 0.1.0), never `--skip-attestation`.
- **The package** is one universal `.vsix` (`npm run package`) holding exactly `vsix-files.txt` (`.vscodeignore` is an
  allowlist); `npm run check:vsix` opens it and refuses machine paths, this machine's user name, e-mail addresses,
  source maps and a bundle built for another version — on every pull request, on both CI legs.

- **The status bar** reads `WSL RAM <used>% · swap <x>G · <n> containers`, coloured (theme colours) by the worst memory
  or kernel verdict the daemon reports; "WSL stopped" when the distribution is not running; "daemon not installed",
  "unsupported distro" or "needs a newer extension" when that is the answer. A daemon too old to report verdicts leaves
  it uncoloured and says so in the tooltip. A click opens the panel.
- **The panel** (the *AI OS Care* icon in the activity bar) shows Memory, Top holders, Swap, Disk, Folders, Containers,
  Container starts, Cleanup (what each cleanup would free, and its buttons — below), Health, AI agents and Last
  cleanup. A row the daemon cannot fill yet says when it arrives ("arrives in E6 — …"); a figure the daemon could not
  read says why ("unavailable — <reason>"); nothing is ever shown as a made-up 0. Its buttons: **Refresh**, **Settings**
  and, when the distribution is stopped, **Start WSL and check**. That button and *Install daemon*'s terminal (you
  confirmed opening a shell in that distribution) are the only two things in the extension that start WSL.
- **Every number is a setting** (application scope, each with its range — `src_vs_code/README.md` lists them): the call
  ceilings (`wslCare.timeouts.*`), the durable poll (`wslCare.cleanup.*`), the Logs page's index bound. A ceiling's minimum
  is the daemon's own worst case for that call plus 10 s, derived from the daemon's per-command ceilings in ONE place
  (`src/client/worstCases.ts`); a cleanup's preview waits for one Docker snapshot PER Docker row it asks.
- **When it asks.** Only the focused VS Code window polls, every `wslCare.refreshSeconds` (default 120, 30 to 86 400), and only for
  `status`; the cleanup and health figures are read when the panel opens or Refresh is pressed. A stopped distribution
  is never asked anything (each `status` the daemon answers writes one run-log file — the cost is measured in
  [research/2026-10-04_extension_poll_churn.md](research/2026-10-04_extension_poll_churn.md)).

- **Reading.** It asks the daemon, as your own user: `status --json`, `preview --all --json`, `doctor --json`,
  `--version`, and for the run history `runs show <runId> --json`, `runs --from … --to … --json` and
  `logs --from … --to … --json`. It never changes the daemon's configuration.
- **Start Windows Time (2026-10-08).** When the newest full run found the Windows Time service not running or not starting
  Automatic, or Windows' clock wrong against the reference, the panel offers **Start Windows Time** (also the command
  *AI OS Care: Start Windows Time…*). A modal shows the exact script first; on confirmation ONE Windows PowerShell runs it
  elevated — Windows' UAC asks you — to set the service to start Automatic (`wslCare.windowsTime.setAutomaticStart`,
  default on), start it and run `w32tm /resync /force`; then a full check runs so the panel shows the result. Nothing
  else on Windows is changed, and nothing restarts the service if other software stops it again — that is the guard below.
- **The Windows Time guard (2026-10-08).** *AI OS Care: Install the Windows Time guard…* (also a button on the panel's
  *Health* section) registers ONE scheduled task, `\wsl-care\windows-time-guard`, that runs as SYSTEM at startup, at logon,
  every `wslCare.windowsTime.guard.everyHours` (4) hours, when the Windows Time service logs that it is stopping (its own
  event 258 — the Service Control Manager's 7036 is not logged on Windows 11 at all, measured in
  [research/2026-10-08_windows_time_guard_trigger.md](research/2026-10-08_windows_time_guard_trigger.md)) and when its
  start type is changed (the Service Control Manager's 7040 for `W32Time`, so — while `wslCare.windowsTime.setAutomaticStart`
  is on — a *disabled* is undone about `…guard.delaySeconds` (60) seconds later; with it off the run cannot start a
  disabled service and its last result says so). A guard installed before that fifth trigger reads *install it again*. Each run only sets
  the start type to Automatic (while `wslCare.windowsTime.setAutomaticStart` is on), starts the service — at most once per
  `…guard.minMinutesBetweenStarts` (10) minutes, so it never loops against software that stops it again — and runs
  `w32tm /resync /force`. Before anything runs, the exact elevated script (the task's XML inside it) opens in a read-only
  tab and a modal names what will run; then ONE UAC prompt. The panel's line shows, from Task Scheduler itself, whether the
  guard is installed, whether it is what the current settings would install, and its last result. *Remove the Windows Time
  guard…* deletes the task, its folder and its rate-limit stamp (the service's start type is left as it is). The settings
  `wslCare.windowsTime.guard.*` are baked in at install: change one and the panel says to install it again.
- **The cleanup buttons (E6.S3).** Each cleanup row has **Clean** and **Select**; **Clean selected (n)** runs every ticked
  row as ONE run; **Run full check now** starts a full measurement (it does not clean); a wedged run of the daemon's own
  units gets **Stop** (any other wedged run is named with its pid). A press asks the daemon for a fresh preview, shows it in
  a modal (what each action removes, how much; A4 removes exactly the volumes listed; A5 / A6 / A7 are re-checked when they
  run) and — for A5, A6Unused, A8, A11 and A12 — a second modal naming the setting they use; a preview older than five
  minutes when you confirm is taken again first. Only then is the cleanup confirmed. The run is written to VS Code's
  `globalState` before the call goes out and followed — `status` every 4 s while it is in flight, then ONE `runs show` — until
  its result is shown, so a reload shows "Cleaning… A4" from the daemon and then the result; a run that died reads
  *interrupted*, a refused one its reason, one with no answer in 30 minutes "state unknown" with its run id. *Last cleanup*
  shows the daemon's newest cleanup (`status.lastCleanup`), the results this window showed and "Docker after" with the time
  it was read. Every button's state comes from what the daemon reports, never from the button alone.
- **The Logs page (E6.S4).** *Logs* in the panel's title bar (*AI OS Care: Logs*) and *Logs* beside *Last cleanup* open
  it in the editor area. Periods: **This run** (the last cleanup, `runs show`), **Today**, **Yesterday**, a **day** or a
  **range** from the date picker — LOCAL days, asked as the instants of their local midnights (`logs` and `runs --from
  <instant> --to <instant> --json`), so a summer-time day of 23 or 25 hours is that day; the picker offers only the 90
  days the daemon keeps, and an older day is clamped (and says so). It shows the daemon's answers as they came — nothing is
  added up on the page: **Totals** (freed, objects, per action), **Runs** (with / without a cleanup, dry runs and what
  they would have freed, by trigger, failed, interrupted), **Max / min** (the runs that freed the most and the least, each
  figure's max and min with its time; `vmmemWSL` arrives in E7.S3 / E11), the **trend** (each full run's MemAvailable and
  swap, as a table) and the **run list**; *Show objects* on a line reads that run's `runs show` — every object removed and
  not removed, every command and its exit. The period is kept in VS Code's `globalState`, so it survives a reload. The
  page sends the host only a period's name, the picker's days or a line's index — never a run id or an argument; all
  three reads are unprivileged.
- **The root boundary (E6.S2).** A cleanup needs root, so the
  extension holds exactly five root calls, built in ONE module (`src/root/rootCall.ts`) from a closed set and started only
  through the runner seam: `-d <distro> -u root --cd / --exec /opt/wsl-care/bin/wsl-care` followed by
  `act <ids> --preview --json`, `act <ids> --confirm --manual --detach [--only -] --json` (A4's volumes, exactly the ones
  its preview showed, on stdin), `act --stop <runId> --json`, `collect --detach --json` (*Run full check now*) or the
  root check `--version`. Never `--timer`, `--user` or `config`; a confirm is always detached (it runs in the daemon's
  own unit and survives a reload). The ids are the extension's compiled registry ∩ `status.actions`; whether the daemon
  may act at all is decided by `status.capabilities` (an older one reads "Update daemon", naming the minimum, and nothing
  runs); one root call at a time per distribution; every root call runs without the Windows `WSLENV`; a detach whose
  outcome is unknown (a timeout, an exit that may have come after the request was written) is never reported as a
  failure — a run id the daemon named is handed back for the panel to follow, and with none only the panel's own run of
  exactly those actions is recognised in `status.running`; a preview is confirmed once. Tests over the sources AND the shipped bundle hold that only that module
  spells a root word and only the host-side cleanup controller imports it; the strict fake refuses a synchronous confirm,
  stdin anywhere but `--only -` and an id outside the intersection.
- **What it needs.** Windows with WSL (`extensionKind: ["ui"]`: it runs on the Windows side, also in a Remote – WSL
  window) and the daemon installed in the distribution by `install.sh` (`/opt/wsl-care/bin/wsl-care`; systemd; Ubuntu
  24.04 or newer — an older glibc is reported as an unsupported distribution).
- **How it reaches the daemon.** `%SystemRoot%\System32\wsl.exe -d <distro> --cd / --exec /opt/wsl-care/bin/wsl-care
  <verb>` — the absolute launcher, never a shell, never `--`. It first asks `wsl.exe` whether the distribution exists
  and is running; **a stopped distribution is never started** (no call is made into it at all).
- **Settings** (user settings only — `"scope": "application"`, so a repository's `.vscode/settings.json` cannot change
  them): `wslCare.distro` — empty means WSL's default distribution (the one `wsl.exe -l -v` marks with `*`); a name
  `wsl.exe --list` does not report is refused — and `wslCare.refreshSeconds` (default 120, at least 30).

```bash
cd src_vs_code
npm ci
npm run typecheck && npm run lint
npm test        # compile, bundle (dist/extension.js), then every test — no test can start the real wsl.exe
npm run test:host   # the extension in a real VS Code 1.85.0 and stable (downloads into .vscode-test/; xvfb-run on Linux)
npm run fieldmap:doc   # rewrite the panel's field-map table in research/architecture.md from src/panel/fieldMap.ts
npm run package        # vsce package: ai-os-care-<version>.vsix (runs the stamped bundle first)
npm run check:vsix     # the leak checks on that .vsix (after npm test, which compiles the checker)
npm run icon:make      # redraw media/icon.png from its recipe (src/test/support/iconPng.ts) — no third-party art
```

What it measured about `wsl.exe` before the client was written: [research/2026-10-03_wsl_exe_facts.md](research/2026-10-03_wsl_exe_facts.md).

## Release

A daemon release is the tag `daemon-v<version>` and a GitHub release carrying, for each of `linux-x64`, `linux-arm64`
and `win-x64`, an archive and its `.sha256`, each archive with a build-provenance attestation signed by
`.github/workflows/release.yml` — what `install.sh` downloads and verifies.

| Archive | Holds |
|---|---|
| `wsl-care-<version>-linux-x64.tar.gz`, `…-linux-arm64.tar.gz` | `wsl-care-<version>-<rid>/` with `wsl-care` (0755), `systemd/` (every unit of `src_daemon/systemd/`) and `config/machine.json` (the empty machine layer) — regular files and folders only, owner 0:0 |
| `wsl-care-<version>-win-x64.zip` | `wsl-care-<version>-win-x64/wsl-care.exe` alone — the Windows probe ships no units and no distro machine layer |
| `<archive>.sha256` | one line, `<sha-256>  <archive name>` (`sha256sum -c` reads it as it is) |

**How one happens.**

1. Conventional commits land on `main`: under `src_daemon/`, `feat:` makes a minor release, `fix:` a patch (before 1.0.0
   a breaking change is a minor too); `ci:`, `chore:`, `docs:`, `test:` make none, and a commit touching nothing under
   `src_daemon/` — only `.github/` or root files — is never a daemon release.
2. Someone runs **release-please** (*Actions → release-please → Run workflow*, or `gh workflow run release-please.yml`).
   It opens one pull request bumping `src_daemon/version.txt` and the manifest and writing `src_daemon/CHANGELOG.md`.
   Merging it is the decision to release.
3. The **next** run of release-please (dispatch it again — the merge itself cuts nothing) creates the tag and a DRAFT
   release, with the release App's token so that the tag starts `release.yml`. The first release is `0.1.0` exactly.
4. **`release.yml`**, on that tag alone: a guard (the tag is `daemon-v<version>`, `version.txt` at the tag agrees, the
   commit is on `main`); per RID on its own runner (`ubuntu-24.04`, `ubuntu-24.04-arm`, `windows-latest` — Native AOT
   does not cross-compile) the three test executables, the AOT publish, the same smoke every pull request runs, the
   archive, its attestation; then ONE job uploads every asset onto the draft, checks the draft holds every RID's archive
   and a matching `.sha256` and nothing else, and only then publishes it. One failed leg publishes nothing; a failure
   leaves an invisible draft, fixed forward — a release tag is never moved or deleted.

The scripts the workflows run are in `.github/scripts/` (`smoke-daemon.sh`, `package-daemon.sh`, `release-guard.sh`,
`verify-release-assets.sh`, and the asset contract they share, `lib/daemon-assets.sh`). Every pull request runs the
smoke AND packs the release archive from its leg's published binary on all three runners — the Windows zip included —
checks that pair and opens the printed path outside bash; the scenario suite runs the packaging, guard and
completeness scripts too. Only what needs the release itself (the App token's tag, the attestation, the upload, the
publish) first runs on a release day.

**What the owner creates once** — settings, not code, applied with the commands and the probes in
[docs/repo-settings.md](docs/repo-settings.md):

- the `dew-flow-release-please` GitHub App installed on this repository, and its two Actions secrets
  `RELEASE_PLEASE_APP_ID` and `RELEASE_PLEASE_APP_PRIVATE_KEY` (without them release-please stops in its first step and
  says which is missing);
- the tag ruleset (`.github/rulesets/tags-daemon.json`: only the App creates a `daemon-v*` tag, nobody updates or deletes
  one) and the `main` ruleset (`.github/rulesets/branch-main.json`: pull requests, linear history, the eight required
  checks — the extension's two legs since E5.S1), each verified by a probe that must be refused;
- optionally `SONAR_TOKEN` (Actions AND Dependabot stores) with the SonarCloud project `remsoftdev_wsl_care` — until
  then `sonarcloud.yml` skips with a warning;
- the CodeRabbit App enabled for this repository (`.coderabbit.yaml` is read from then on);
- for the extension (the E5 live gate): the Marketplace publisher (done 2026-10-06: `remsoftdev`), the `marketplace` Environment (a required reviewer,
  `extension-v*` tags only) with `VSCE_PAT` — or OIDC through `--azure-credential`, recommended because global Azure
  DevOps PATs stop working on 2026-12-01 — and the tag ruleset `.github/rulesets/tags-extension.json` (steps 9–11).

### The extension's release

An extension release is the tag `extension-v<version>`: release-please's `extension` package (the `node` strategy over
`src_vs_code/package.json`; the first release is `0.1.0`) cuts it with a DRAFT release, and the tag starts
**`release-extension.yml`** — a file of its own, so the Marketplace secret is never in a workflow a daemon release runs:

1. **guard** — the tag matches `package.json`'s version, the publisher is real, the commit is on `main`, and the
   **daemon versions** of `src_vs_code/min-daemon.json`, as the bundle step emits them — the minimum the extension renders
   (`minDaemonForRender`), since E6.S2 the one it acts with (`minDaemonForActions`), and since #37 the release *Install
   daemon* types (`installDaemon`, at or above both) — are published releases, each of them, and `POST_DEPLOY.md`'s
   last-verified daemon is at or above the install pin, and **the first public extension stays root-free**: a checkout carrying
   the root module is refused unless the release is above `extension-v0.1.0` AND that tag's own tree carries no root
   module AND it is a published, non-draft GitHub release — keyed on the tags, never on the manifest (`release-extension-guard.sh`; the build's check-vsix refuses the bundle the same way);
2. **build** — every test, `vsce package` once, the leak checks with `--release` and the guard's minimum, the `.vsix`'s
   `.sha256`; it can read the repository and nothing more;
3. **attest** — the only job that can sign: it downloads the build's `.vsix`, checks it against its `.sha256` and
   attests it — no npm, no dependency checkout;
4. **github-draft** — the `.vsix` and `.sha256` uploaded to the draft and read back FIRST, so the rollback source exists
   before anything is public;
5. **publish-marketplace** — in the protected `marketplace` Environment: skipped when the Marketplace already serves the
   version, otherwise `vsce publish --packagePath` of the attested file, then a bounded wait until it is served;
6. **github-public** — the draft compared once more with the attested build, then made public, last.

Re-run with **Re-run failed jobs** only — never *Re-run all jobs*, which rebuilds a `.vsix` that is not byte-identical
while the Marketplace may already serve the first. An asset on a release is never replaced, draft or public: the draft
upload adds only what is missing, and both GitHub jobs refuse a release that holds other bytes than this run's build; the
Marketplace job skips a version it already serves; making public is a no-op the second time. A failure leaves a draft;
it is fixed forward with the next patch — an `extension-v*` tag is never moved or deleted.

**Rollback — one command, nothing built:** install a previous release's `.vsix` from GitHub, its attestation verified
first — that `release-extension.yml`, run for THAT tag on a GitHub-hosted runner, built exactly these bytes (the exact
identity: `--signer-workflow` would be a prefix match, which a run from any branch passes):

```bash
gh release download extension-v<previous> -R oleksandrdubyna88/wsl_care --pattern '*.vsix' && gh attestation verify ai-os-care-<previous>.vsix --repo oleksandrdubyna88/wsl_care --cert-identity "https://github.com/oleksandrdubyna88/wsl_care/.github/workflows/release-extension.yml@refs/tags/extension-v<previous>" --deny-self-hosted-runners && code --install-extension ai-os-care-<previous>.vsix
```

— or ship the next patch. Every extension release keeps its `.vsix` and `.sha256` as release assets, which do not
expire (a workflow artifact does).

## Build and test

Needs the .NET 10 SDK (`global.json` pins `10.0.100` with `rollForward: latestFeature`). Every
MSBuild command carries `-m:4`; the root `Directory.Build.rsp` adds `-nr:false`.

```bash
dotnet build wsl_care.slnx -c Release -m:4

# Tests are xUnit v3 executables — run them directly, NEVER `dotnet test` (no VSTest host here)
./src_daemon/tests/WslCare.Core.Tests/bin/Release/net10.0/WslCare.Core.Tests.exe
./src_daemon/tests/WslCare.Cli.Tests/bin/Release/net10.0/WslCare.Cli.Tests.exe
./src_daemon/tests/WslCare.Cli.Tests/bin/Release/net10.0/WslCare.Cli.Tests.exe --filter-method "*Version*"

# The scenario harness: the BUILT wsl-care over a temp WSL_CARE_ROOT, fake docker/systemctl/journalctl/powershell
# alone on its PATH, and the check that every CLI verb has a row in research/module_tests.md
./src_daemon/tests/WslCare.Scenarios/bin/Release/net10.0/WslCare.Scenarios.exe

# The live contract: the REAL docker / systemctl / journalctl of this machine against the product's parsers.
# Not run by CI. A missing tool or daemon is a skip with its reason; WSL_CARE_REQUIRE_LIVE=1 (release) makes it a failure.
# Run it inside WSL Ubuntu for the systemd half; WSL_CARE_LIVE_CAPTURE=<dir> records each answer (fixture capture).
./src_daemon/tests/WslCare.LiveContract/bin/Release/net10.0/WslCare.LiveContract.exe
WSL_CARE_REQUIRE_LIVE=1 ./src_daemon/tests/WslCare.LiveContract/bin/Release/net10.0/WslCare.LiveContract.exe

# Formatting, as CI checks it (reports, never rewrites)
dotnet format wsl_care.slnx --verify-no-changes

# Native AOT binary (on Windows the MSVC build tools are needed, with vswhere.exe on PATH)
dotnet publish src_daemon/src/WslCare.Cli/WslCare.Cli.csproj -c Release -r win-x64 -o artifacts/publish/win-x64 -m:4
./artifacts/publish/win-x64/wsl-care.exe --help
```

On Linux drop the `.exe` and publish with `-r linux-x64` or `-r linux-arm64` on an arm64 host (needs
`clang` and `zlib1g-dev`; Native AOT does not cross-compile). CI runs all of the above on `linux-x64`,
`linux-arm64` and `win-x64`. The daemon's version is `src_daemon/version.txt`. What each test covers —
and every CLI flow with the test that covers it — is in
[research/module_tests.md](research/module_tests.md); a verb added to the CLI without a row there fails
the scenario suite.

The extension builds and tests with Node (CI: Node 22, `ci · extension` on `windows-latest` and `ubuntu-24.04`) —
the commands are in [Extension (preview)](#extension-preview).

Family checks, from the repository root (CI runs them in `ci · family checks`):

```bash
node .agents/conventions/tools/plan-lifecycle.mjs
node .agents/conventions/tools/adapter-check.mjs
node .agents/conventions/tools/pin-check.mjs
node .agents/conventions/tools/build-flags-check.mjs
```
