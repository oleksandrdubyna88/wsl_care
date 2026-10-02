# module_tests — the harness, the flows it drives, and what it does not prove

> Adopted 2026-10-02. Since E1.S1 the daemon has unit tests and one process-level tier (the built
> `wsl-care` run as a child process); E1.S2 added the seam tests, the configuration tests and an
> architecture test. The scenario harness proper — `WslCare.Scenarios`, the built CLI over a fixture
> home with fake `docker`/`systemctl` on `PATH` — arrives with E1.S3.

## Where the tests are and how they run

xUnit v3 on Microsoft Testing Platform. Each test project builds into its own runner **executable**;
`dotnet test` is never used (there is no VSTest host — it aborts with a tooling error).

```bash
dotnet build wsl_care.slnx -c Release -m:4
./src_daemon/tests/WslCare.Core.Tests/bin/Release/net10.0/WslCare.Core.Tests.exe
./src_daemon/tests/WslCare.Cli.Tests/bin/Release/net10.0/WslCare.Cli.Tests.exe
```

On Linux the executables carry no `.exe`. CI (`.github/workflows/ci-daemon.yml`) runs exactly these
commands in Release on `ubuntu-latest` and `windows-latest`, then publishes the Native AOT binary and
smokes it (`--help` lists the commands; `--version` equals `src_daemon/version.txt`).

Every test that touches a disk does so under a temporary root of its own (`TempRoot` /
`SandboxHost` in `WslCare.TestSupport`); the built-binary tests pass `WSL_CARE_ROOT` so the real
profile, `/etc`, `/var` and the real log folder are never read or written. Tests that need a
directory link create a symbolic link, or a junction where the account may not create symlinks
(this machine), and skip with that reason when neither works.

## What each guarantee rests on

| Guarantee | Test file |
|---|---|
| A daemon assembly is stamped from `src_daemon/version.txt`; an unstamped assembly reports `unknown` | `WslCare.Core.Tests/ProductVersionTests.cs` |
| "Strictly under" is a proper descendant, case-sensitive on Linux and case-insensitive on Windows, never fooled by `~/gitx` vs `~/git` or by trailing separators; roots split from segments on both families; a relative path is refused | `WslCare.Core.Tests/Hosting/PathRulesTests.cs` |
| The real path follows a link in the middle of a path, applies `..` to the link TARGET, resolves relative targets against the link's parent, follows chains, stops a cycle with an error, changes drive through a Windows junction, never climbs above the root | `WslCare.Core.Tests/Files/RealPathTests.cs` |
| Linux and Windows layouts match plan §6 / §4.6; a sandboxed layout keeps every path under its root; `WSL_CARE_ROOT` is the variable | `WslCare.Core.Tests/Hosting/HostPathsTests.cs` |
| The never-list, as a table per OS family: inside the root allowed; outside the root, an agent folder, `~/git`, Claude's temp folder, a too-broad root each refused by the NAMED rule; the archive permit allows a move out of an agent folder and nothing else; `projects/*/memory/` is never moved even with the permit; a move's destination is judged; a refusal names the action and the path | `WslCare.Core.Tests/Files/DeletionPolicyTests.cs` |
| Over a real disk: `..` traversal out of the root is refused before anything is deleted; a link inside the root into an agent folder is refused (through it, and the link itself); an atomic write replaces content and leaves no temp file, and is refused outside its root before writing; a move inside the root is performed; missing is not unreadable; 40 parallel appenders produce 40 whole lines; a held lock makes an append time out | `WslCare.Core.Tests/Files/PhysicalFileSystemTests.cs` |
| The real runner: exit code and both streams captured; a missing executable is `FailedToStart`; a timeout kills the WHOLE tree (the grandchild's pid is observed dead) and reports what was captured; output past the cap is cut and marked; the caller's cancellation surfaces as `OperationCanceledException`, not as a timeout; a refusing policy prevents the start entirely; a request needs a positive ceiling | `WslCare.Core.Tests/Processes/ProcessCommandRunnerTests.cs` |
| The register and `default.json` name the same keys; every default validates; the plan's defaults are the shipped ones; a value round-trips through JSON | `WslCare.Core.Tests/Config/ConfigSchemaTests.cs` |
| A layer flattens to dotted keys with the line of every leaf; a syntax error reports its line in one sentence; a non-object is malformed; comments, trailing commas, a BOM and `$schema` are accepted | `WslCare.Core.Tests/Config/ConfigDocumentTests.cs` |
| One validator for file and command line: bool spelling, int range with the range in the message, enumerated text exactly, free text, comma-separated lists; a wrong JSON shape names the offending value | `WslCare.Core.Tests/Config/ConfigValidationTests.cs` |
| Precedence default < machine < user with the layer named per value; an unknown key, an out-of-range or mistyped value, a file that is not JSON, an unreadable file each make the result observe-only with file and line while the same file's valid keys still apply and no default re-enables anything | `WslCare.Core.Tests/Config/ConfigLoaderTests.cs` |
| `set` writes the key nested and the loader reads it back from the user layer; other valid keys are kept; invalid keys are dropped and named; an unparseable file is moved aside with a UTC stamp; `reset` removes a key and says whether it was there; a missing file becomes `{}`; no temp file is left | `WslCare.Core.Tests/Config/UserConfigWriterTests.cs` |
| A run id is the UTC second and the pid; a record is one camel-case line with string enums and `schemaVersion`; every outcome (`interrupted` included) round-trips; the writer appends one line per record under the state directory | `WslCare.Core.Tests/Records/RunRecordTests.cs` |
| No file outside `PhysicalFileSystem.cs` and `ProcessCommandRunner.cs` deletes, moves or starts a process; the scanner matches a planted instance formatted across lines; it still finds the sanctioned calls in each seam; it ignores words that merely contain the names; `WslCare.Core` references no package | `WslCare.Core.Tests/ArchitectureTests.cs` |
| Parsing: every help spelling; `--version`; unknown verbs named; near misses refused; extra words refused; control characters never reach a message; `config get` takes an optional key and `--json` in either order and refuses a second key or an unknown option; `config set` needs exactly key and value; `config reset` exactly one key; `config` alone lists its sub-verbs; every registered command is in the help text and its example parses (derived from the register) | `WslCare.Cli.Tests/CommandLineTests.cs` |
| The whole program in-process: `--version` prints the stamp only; `--help` lists the config verbs; an unknown verb exits 2 with one stderr line even with a newline in it; a cancelled token stops the run as a cancellation | `WslCare.Cli.Tests/ProgramTests.cs` |
| `config get` lists every key with layer `default` on a fresh host, one key alone, refuses an unknown key (exit 2); `config set` validates, writes, and `get` then shows `(user)`; an out-of-range value is refused with ONE stderr line and the file untouched; an unknown key or a mistyped value is refused and nothing is written; a broken user layer is reported observe-only on stderr and in the JSON (`configError` with file and line) while still answering; `set` repairs a broken layer and the next `get` is valid; a non-JSON layer is moved aside and the command says where; `reset` reports the effective value again; the JSON report carries every key with value and layer | `WslCare.Cli.Tests/ConfigCommandTests.cs` |
| Escapes on a redirected writer with a control; levels coloured differently and the message unquoted; a file per run that segments at UTC midnight into the next day's folder with the same pid and never rolls backward; retention selects only expired day folders, deletes through the seam, leaves today and strangers, is a no-op on a missing root, and refuses an expired folder that is a link into `~/git`; starting the logger writes one file under the host's log directory at the configured level | `WslCare.Cli.Tests/LoggingTests.cs` |
| The BUILT `wsl-care` as a child process (30 s ceiling, tree kill): `--help` exits 0 and lists the config verbs; an unknown verb exits 2 with one stderr line; `config set` / `config get --json` / a refused `set` read and write under `WSL_CARE_ROOT` and each run leaves its own log file there | `WslCare.Cli.Tests/BuiltBinaryTests.cs` |

Teeth, observed by breaking the code and watching the named tests go red:

- 2026-10-02 (E1.S1): removing the `Version` stamp, returning `0` from a refusal, dropping the
  control-character replacement.
- 2026-10-02 (E1.S2), the first run with stubbed bodies: the policy allowing everything turned the
  table rows and the real `..`/junction tests red (`Expected Refused, but found AllowedVerdict`); the
  identity path resolver returned `/home/me/./work/../g…`; the loader ignoring the user layer answered
  `Valid` where `ObserveOnly` was due and `= 20` after a `set` to 25; the writer's no-op reset reported
  the key absent; the runner ignoring the policy answered `FailedToStart` where `Refused` was due.
- 2026-10-02 (E1.S2), break-it on the shipped policy: removing the `~/git` rule turned exactly five
  tests red — the Linux and Windows table rows for `GitFolder`, the readable-refusal test, and the real
  junction-into-git test — and the suite went back to 141/141 on restore.

## Flow catalogue

The CLI verbs are listed from `CommandLine.Commands` (plus the refusal path). E1.S3 replaces this
hand-kept table with a register derived from that list and a test that fails when a verb is missing
here.

| Flow | Covered | By |
|---|---|---|
| `wsl-care --help` (and `-h`, `help`, no arguments) | covered | `CommandLineTests`, `ProgramTests`, `BuiltBinaryTests`; AOT binary: CI smoke |
| `wsl-care --version` | covered | `ProgramTests`, `ProductVersionTests`; AOT binary: CI smoke |
| an unknown verb or option is refused (exit 2, one stderr line) | covered | `CommandLineTests`, `ProgramTests`, `BuiltBinaryTests` |
| `wsl-care config get [key] [--json]` | covered | `ConfigCommandTests`, `BuiltBinaryTests` (JIT build, sandboxed root) |
| `wsl-care config set <key> <value>` — accepted, refused, repairing a broken layer | covered | `ConfigCommandTests`, `UserConfigWriterTests`, `BuiltBinaryTests` |
| `wsl-care config reset <key>` | covered | `ConfigCommandTests`, `UserConfigWriterTests` |
| `wsl-care status --json` / `collect` | not covered | not built yet (E2) |
| a cleanup action, previewed then run | not covered | not built yet (E3) |
| the logs page for a period | not covered | the extension is not built yet (E6) |
| the AI-session archive and restore | not covered | not built yet (E9) |

## What it does not prove

- The process tests run the **JIT** build; only the CI smoke runs the Native AOT binary, and only
  `--help` / `--version` there. The owner's local AOT smoke of `config get` / `config set` is recorded
  in the commit message, not run by CI yet (E1.S3's harness takes that over).
- `linux-arm64` is published by no pull request — the release workflow (E4) is the first place it is
  built and run.
- On this machine the directory-link tests run over **junctions**, not symbolic links; the symlink
  branch runs on the Linux CI leg.
- `ShutdownSignals` is thin wiring over `PosixSignalRegistration` and is not tested in isolation; the
  token's effect on a run is (`ProgramTests`, `ProcessCommandRunnerTests`).
- The `AllowAllCommandPolicy` is a placeholder; the never-list and its property test are E3.S1.
- Nothing here runs on a real WSL VM under memory pressure; the live smoke on the owner's machine is
  the only place that happens, at release time.

## When it runs

On every push to `main` and every pull request (`ci · daemon`, unconditional); the live smoke at
every release.
