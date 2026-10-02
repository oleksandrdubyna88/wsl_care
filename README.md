# wsl_care

Keeps the WSL VM on this machine from degrading over the working day: a systemd timer inside `Ubuntu`
that records memory, disk and Docker state every 4 hours and applies safe cleanups, plus a VS Code
extension that shows the state and runs cleanups on demand.

| Folder | Holds |
|---|---|
| `src_daemon/` | the C# Native AOT daemon/CLI `wsl-care` — today the foundation seams and the `config` verbs; the collectors and cleanups arrive in later releases |
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
wsl-care config set volumes.anonymousMaxGb 25   # validated; refused values exit 2 with one stderr line
wsl-care config reset volumes.anonymousMaxGb    # back to the machine/default value
```

`config set` and `config reset` write only the user file, atomically. A layer that is unreadable,
not JSON, or holds an unknown key or an out-of-range value never stops the daemon and is never
replaced by defaults: the run becomes **observe-only** (collect and report, no cleanup), every answer
carries `configError {file, line, message}`, and `config set` still works — it keeps the keys it could
still read, drops the rest by name, and moves a file it cannot parse to `config.json.broken-<utc>`.

Run logs go to `/var/log/wsl-care/{yyyy-MM-dd}/wsl-care-{HH-mm-ss}-{pid}.log` (Linux) or
`%LOCALAPPDATA%\wsl-care\logs\…` (Windows), one file per run, UTC; `logging.minimumLevel` and
`logging.retentionDays` (14) are settings like any other. Set `WSL_CARE_ROOT=<dir>` to lay every
path — configuration, state, logs, the protected folders — out under one directory; tests and
scenario runs use it so nothing real is ever touched.

## Build and test

Needs the .NET 10 SDK (`global.json` pins `10.0.100` with `rollForward: latestFeature`). Every
MSBuild command carries `-m:4`; the root `Directory.Build.rsp` adds `-nr:false`.

```bash
dotnet build wsl_care.slnx -c Release -m:4

# Tests are xUnit v3 executables — run them directly, NEVER `dotnet test` (no VSTest host here)
./src_daemon/tests/WslCare.Core.Tests/bin/Release/net10.0/WslCare.Core.Tests.exe
./src_daemon/tests/WslCare.Cli.Tests/bin/Release/net10.0/WslCare.Cli.Tests.exe
./src_daemon/tests/WslCare.Cli.Tests/bin/Release/net10.0/WslCare.Cli.Tests.exe --filter-method "*Version*"

# Formatting, as CI checks it (reports, never rewrites)
dotnet format wsl_care.slnx --verify-no-changes

# Native AOT binary (on Windows the MSVC build tools are needed, with vswhere.exe on PATH)
dotnet publish src_daemon/src/WslCare.Cli/WslCare.Cli.csproj -c Release -r win-x64 -o artifacts/publish/win-x64 -m:4
./artifacts/publish/win-x64/wsl-care.exe --help
```

On Linux drop the `.exe` and publish with `-r linux-x64` (needs `clang` and `zlib1g-dev`). The daemon's
version is `src_daemon/version.txt`. What each test covers is in
[research/module_tests.md](research/module_tests.md).

Family checks, from the repository root:

```bash
node .agents/conventions/tools/plan-lifecycle.mjs
node .agents/conventions/tools/adapter-check.mjs
node .agents/conventions/tools/pin-check.mjs
node .agents/conventions/tools/build-flags-check.mjs
```
