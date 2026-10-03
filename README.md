# wsl_care

Keeps the WSL VM on this machine from degrading over the working day: a systemd timer inside `Ubuntu`
that records memory, disk and Docker state every 4 hours and applies safe cleanups, plus a VS Code
extension that shows the state and runs cleanups on demand.

| Folder | Holds |
|---|---|
| `src_daemon/` | the C# Native AOT daemon/CLI `wsl-care` — today the foundation seams, the `config` verbs, `status` (memory, processes, containers, disk), `preview` (what each Docker cleanup would free), the full run `collect`, `doctor`, the container-start follower `events follow`, and the action engine behind `act` with the journal vacuum and the irreversible cleanups (A4–A9, A11, A12, A14, A17); A1–A3, A15, A16 arrive in the next release |
| [todo/](todo/README.md) | open plans |
| [research/](research/) | measurements of the system as it is — start with [the 2026-10-02 baseline](research/2026-10-02_wsl_resource_baseline.md) and [the architecture](research/architecture.md) |
| `research/diagnostics/` | the read-only scripts that produced the baseline |
| `install.sh`, `src_daemon/systemd/`, `src_daemon/config/machine.json` | the installer, the three systemd units and the machine configuration layer it installs |

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
- the **build-provenance attestation** (`gh attestation verify --repo oleksandrdubyna88/wsl_care --signer-workflow
  oleksandrdubyna88/wsl_care/.github/workflows/release.yml`) proves this repository's release workflow built those
  bytes. It needs the [GitHub CLI](https://cli.github.com), logged in (`gh auth login`); under `sudo` it runs as the
  user who ran `sudo`, with that user's login. Without `gh` the installer stops **before downloading anything** and
  says how to install it. To proceed knowingly without it — printed loudly, and the checksum still applies:

  ```bash
  curl -fsSL …/install.sh | sudo sh -s -- --skip-attestation
  ```

A mismatch, a missing `.sha256`, a refused attestation, or an archive holding anything but regular files under one
folder (no `..`, no link) stops it with nothing installed.

**What it changes on the machine** — and nothing else:

| Path / thing | What |
|---|---|
| `/opt/wsl-care/bin/wsl-care` (0755), linked from `/usr/local/bin/wsl-care` | the binary; root and the units always use the absolute path |
| `/etc/systemd/system/wsl-care.service` | the timer's full run, `wsl-care collect --timer` (oneshot, `Nice=19`, idle I/O, `MemoryMax=256M`, 10 min) |
| `/etc/systemd/system/wsl-care.timer` | every 4 hours on the clock (00:00, 04:00, …), catching up ONCE after a night the VM was off; enabled and started |
| `/etc/systemd/system/wsl-care-events.service` | the container-start follower, `wsl-care events follow`, `Restart=always` after 30 s; enabled and started |
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

**Uninstall:**

```bash
curl -fsSL …/install.sh | sudo sh -s -- --uninstall           # units stopped and removed, binary and link removed
curl -fsSL …/install.sh | sudo sh -s -- --uninstall --purge   # … and the state, the logs and the machine configuration
```

`--uninstall` keeps `/var/lib/wsl-care` (history, run details, container starts), `/var/log/wsl-care` (run logs) and
`/etc/wsl-care` (the machine layer). `--purge` removes exactly those three and `/run/wsl-care.lock`, and names each
before removing it. Never removed: sysstat and atop (other tools may use them), `/etc/wsl.conf`, every user's
`~/.config/wsl-care`.

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
overwritten). Every `wsl-care:` message is one line: control characters in what it quotes — a key
you typed, a key read from the file, a path — are shown as `?`.

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

`status` answers in well under 2 s and starts no process: it reads `/proc` and the cgroup tree (inside
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
`preview`, `docker stats`, the distro's clock against Windows' (`powershell.exe`, its launch latency subtracted), the
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

Exit codes: 0 recorded (or read-only) · 1 the run could not be recorded (the reason on stderr) · 2 usage · 75 another
run holds the lock · 70 a defect.

## Doctor

```bash
wsl-care doctor --json   # healthy: true|false, one check per part, the versions; schemaVersion 1
wsl-care doctor
```

Read-only: the configuration (observe-only is a problem), the state directory, the last run (older than 5 h or
failed), lost run details, `wsl-care.timer` / `wsl-care-events.service` / `sysstat.service` / `atop.service`, whether
sysstat and atop wrote in the last 30 minutes, whether the events follower is current, and the versions of `wsl-care`,
Docker, systemd and the kernel. Root reachability is the extension's check (`notChecked` here). It exits 0 whatever it
finds; `healthy` is the verdict.

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
| `A11` | `SIGTERM`, `SIGKILL` after 10 s — by pid AND start time (a `pidfd`), never by name | memory, not disk (not counted) | off by default; only suspects: orphaned, in `processes.families`, older than `processes.idleOlderThanHours`, no terminal, not root's, no CPU in a 5 s window and none since |
| `A12` | deletes the Playwright browsers no project's `browsers.json` references; `dotnet nuget locals http-cache --clear` as the user | each folder before, counted when gone | a button only; refuses the Playwright part when what is referenced cannot be told |
| `A14` | deletes VS Code / Cursor / Windsurf server builds no process uses, keeping the newest 2, and `.obsolete` extensions | each folder before, counted when gone | every delete judged by the deletion policy |
| `A17` | `pnpm store prune`, `uv cache prune`, `pip cache purge` as the target user | each cache before/after | `cargo sweep` is not run (it would delete under `~/git`); Gradle prunes its own caches |
| `A1` | `sync`, then `sysctl -w vm.drop_caches=1` (never another value) | memory, not disk: the page cache and `MemAvailable` before/after | the timer: `MemAvailable` below `thresholds.memAvailableActPercent`, or a page cache above 12 GiB with less than 30 % available; waits for an idle machine on the timer |
| `A2` | `sysctl -w vm.compact_memory=1` | the free 512 KiB (order-7) blocks before/after | the timer: after A1 ran, or AT ONCE — without waiting for idle — when no order-7 block is left or the kernel logged a `page allocation failure` since the last run |
| `A3` | `dotnet build-server shutdown` as the target user | the build servers gone after (memory, not disk) | the timer: a server alive for `buildServers.idleHours`; refused while any `dotnet build`, `test` or `run` is alive, a button too |
| `A15` | `fstrim -av` | what fstrim reports trimmed per filesystem (returned to the VHDX) | the timer: weekly, only without `discard` on `/` and with `fstrim.timer` off; waits for an idle machine |
| `A16` | `chronyc makestep` (chronyd running) or `hwclock -s` | the clock offset before/after | skipped when time sync reports synchronised or the clock agrees; the timer: once per drift seen on two observations 5 minutes apart (`clock.maxDriftSeconds`); at most once an hour |

As root, every per-user path — the daily folder walk, the caches above, the user configuration layer — is the
**target user's** home (`/etc/wsl.conf` `[user] default=`, else the single login account), never root's; when the target
is ambiguous the user layer is skipped: machine-scoped actions still run, every user-scoped action refuses
naming the accounts (set `[user] default=` in `/etc/wsl.conf`, or `install.sh --set-default-user <name>`). `config set` / `config reset` refuse to run as root for the target user (a
root-owned file would lock them out of their own settings): run them as yourself.

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

## Logs and runs — what the runs of a period did

```bash
wsl-care logs --json                              # today (UTC): freed per action and in total, runs with/without a cleanup, max/min
wsl-care logs --period yesterday
wsl-care logs --period 2026-10-01 --action A4     # one UTC day, one action, every volume it removed
wsl-care runs --period 2026-09-28..2026-10-02 --json   # every run of a range: trigger, outcome, dry run, actions, freed
```

Read-only: anyone may ask (no lock, nothing written). A run belongs to the UTC day it started. `logs` answers the Logs
page: what was freed in total and per action (with object counts), how many runs did and did not clean anything, the
dry runs apart with what they would have freed, the runs by trigger (timer, button, terminal), the run that freed the
most and the least, each recorded figure's maximum and minimum with its time (`MemAvailable`, page cache, swap, `/`,
Docker reclaimable, container starts) and, per cleanup, every object it removed — and those it did not, with why — from
the run's detail file. Memory actions (A1, A2, A3, A11) free no disk and count no bytes. Periods: `today` (the
default), `yesterday`, `yyyy-MM-dd`, `yyyy-MM-dd..yyyy-MM-dd` (at most 366 days). Exit codes: 0 answered (an empty period
too) · 2 a period that is none of these · 4 the history exists but cannot be read.

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

Family checks, from the repository root (CI runs them in `ci · family checks`):

```bash
node .agents/conventions/tools/plan-lifecycle.mjs
node .agents/conventions/tools/adapter-check.mjs
node .agents/conventions/tools/pin-check.mjs
node .agents/conventions/tools/build-flags-check.mjs
```
