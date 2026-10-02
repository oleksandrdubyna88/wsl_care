# module_tests — the harness, the flows it drives, and what it does not prove

> Adopted 2026-10-02. Since E1.S1 the daemon skeleton has unit tests and one process-level tier (the
> built `wsl-care` run as a child process). The scenario harness proper — `WslCare.Scenarios`, the
> built CLI over a fixture home with fake `docker`/`systemctl` on `PATH` — arrives with E1.S3.

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
smokes it (`--help` lists both commands; `--version` equals `src_daemon/version.txt`).

## What each test file covers

| Test file | Covers |
|---|---|
| `src_daemon/tests/WslCare.Core.Tests/ProductVersionTests.cs` | a daemon assembly is stamped from `src_daemon/version.txt` (path read back from the build, not guessed); an assembly with no stamp reports `unknown`, not an empty or zero version |
| `src_daemon/tests/WslCare.Cli.Tests/CommandLineTests.cs` | parsing: no arguments → help; every documented help spelling; `--version`; an unknown verb names itself and points at `--help`; near misses (`--Version`, `-version`) refused; extra words after a flag refused; control characters never reach a message; every command in the register is in the help text and parses from its usage spelling (derived from `CommandLine.Commands`, not retyped) |
| `src_daemon/tests/WslCare.Cli.Tests/ProgramTests.cs` | the whole program in-process with captured streams: `--version` prints the assembly informational version and nothing else; `--help` writes the help text to stdout only; an unknown verb exits `2` with nothing on stdout and exactly one line on stderr, even when the argument carries a newline |
| `src_daemon/tests/WslCare.Cli.Tests/BuiltBinaryTests.cs` | the BUILT `wsl-care` apphost as a child process (30 s ceiling, tree kill on timeout): `--help` exits `0` on stdout; an unknown verb exits `2` with one stderr line |

Teeth, observed 2026-10-02 by breaking the code and watching the named tests go red: removing the
`Version` stamp (→ `1.0.0+<sha>` reported), returning `0` from a refusal (in-process and process
tests), and dropping the control-character replacement (both newline tests). The last one first
stayed green in-process because the helper split on `\r\n` only — fixed to split on any line break.

## Flow catalogue

The CLI verbs are listed from `CommandLine.Commands` (plus the refusal path). E1.S3 replaces this
hand-kept table with a register derived from that list and a test that fails when a verb is missing
here.

| Flow | Covered | By |
|---|---|---|
| `wsl-care --help` (and `-h`, `help`, no arguments) | covered | `CommandLineTests`, `ProgramTests`, `BuiltBinaryTests`; AOT binary: CI smoke |
| `wsl-care --version` | covered | `ProgramTests`, `ProductVersionTests`; AOT binary: CI smoke |
| an unknown verb or option is refused (exit 2, one stderr line) | covered | `CommandLineTests`, `ProgramTests`, `BuiltBinaryTests` |
| `wsl-care status --json` / `collect` | not covered | not built yet (E2) |
| a cleanup action, previewed then run | not covered | not built yet (E3) |
| the logs page for a period | not covered | the extension is not built yet (E6) |
| the AI-session archive and restore | not covered | not built yet (E9) |

## What it does not prove

- The process tests run the **JIT** build; only the CI smoke runs the Native AOT binary, and only
  `--help` / `--version` there.
- `linux-arm64` is published by no pull request — the release workflow (E4) is the first place it is
  built and run.
- Nothing here runs on a real WSL VM under memory pressure; the live smoke on the owner's machine is
  the only place that happens, at release time.

## When it runs

On every push to `main` and every pull request (`ci · daemon`, unconditional); the live smoke at
every release.
