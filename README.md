# wsl_care

Keeps the WSL VM on this machine from degrading over the working day: a systemd timer inside `Ubuntu`
that records memory, disk and Docker state every 4 hours and applies safe cleanups, plus a VS Code
extension that shows the state and runs cleanups on demand.

| Folder | Holds |
|---|---|
| `src_daemon/` | the C# Native AOT daemon/CLI `wsl-care` — today the foundation seams, the `config` verbs, `status` (memory, processes, containers, disk) and `preview` (what each Docker cleanup would free); the full run and the cleanups arrive in later releases |
| [todo/](todo/README.md) | open plans |
| [research/](research/) | measurements of the system as it is — start with [the 2026-10-02 baseline](research/2026-10-02_wsl_resource_baseline.md) and [the architecture](research/architecture.md) |
| `research/diagnostics/` | the read-only scripts that produced the baseline |

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
`%LOCALAPPDATA%\wsl-care\logs\…` (Windows), one file per run, UTC; `logging.minimumLevel` and
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
reports them from the last full run in `history.jsonl` with their age. Until a full run exists — `collect`
arrives in a later release — they are `"available": false` with that reason. Every figure that cannot be
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
A6Unused unused images, A7 build cache; A8 (npm) and A9 (apt, snap) arrive with the full run. Next to them: the
named volumes no container uses (kept — a person decides), Docker's own totals per type, and a hygiene audit
(containers logging without `max-size`, Docker Desktop's builder GC, forgotten buildx builders). Nothing is removed.

When Docker cannot answer, every Docker figure is `"available": false` with `docker.kind` —
`notInstalled`, `daemonStopped`, `socketRefused`, `timedOut`, `commandFailed`, `refused`, `unparseable` — and the
reason; never 0, and the exit code is still 0. Each Docker command has its own ceiling (10 s for the first probe,
2 min for `system df`) and is killed with its process tree when it passes it.

A4's age is the first time the daemon saw a volume unattached, kept in `/var/lib/wsl-care/volume-seen.json`. Only
a process that may write the state directory records it — root, on an installed machine (the timer); run as your
own user, `preview` reads the record, writes nothing and says `read-only` in `volumeSeen`.

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
