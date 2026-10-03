# module_tests — the harness, the flows it drives, and what it does not prove

> Adopted 2026-10-02 (E1.S1), the scenario harness since E1.S3. The record
> `.agents/conventions/common/scenario-tests.md` asks for: where the harness is, the exact command that
> runs it, every flow `covered` or `not covered` with the test that covers it, what it does NOT prove,
> and when it runs. The flow catalogue below is **checked against the CLI's own verb register** — a verb
> without a row here is a red build (§ *The derived verb register*).

## The tiers

| Tier | Where | What it proves |
|---|---|---|
| Unit, core | `src_daemon/tests/WslCare.Core.Tests` | the seams, the configuration system, the records, the architecture rule — in-process |
| Unit + process, CLI | `src_daemon/tests/WslCare.Cli.Tests` | parsing, the program in-process with captured streams, logging, and the built binary as a child process (`BuiltBinaryTests`) |
| **Scenario** | `src_daemon/tests/WslCare.Scenarios` | the BUILT `wsl-care` driven the way a user and the extension drive it, over a temporary home, with fake `docker` / `systemctl` / `journalctl` / `powershell` / `timedatectl` / `snap` alone on its `PATH`; the derived verb register; since E4.S1 the real `install.sh` under `/bin/sh` over a temporary prefix (§ *The installer harness*) and the shipped units and machine layer read by the product's own parser and loader; since E4.S2 the release scripts under bash and the release workflows' structure (§ *The release pipeline's tests*) |
| **Live contract** | `src_daemon/tests/WslCare.LiveContract` | the REAL `docker` / `systemctl` / `journalctl` (since E2.S3 also `timedatectl`, `snap`, `powershell.exe` through interop, and the event stream) of the owner's machine through the product's own `ProcessCommandRunner` (30 s ceiling, tree kill), parsed by the product's parsers (plan §15a C2, §15b #2/#6) — NOT one of the CI test steps; § *The live contract* below |
| AOT smoke | `.github/workflows/ci-daemon.yml` | the Native AOT binary of each RID answers `--help` / `--version`, performs the configuration round trip, answers `status --json` (on Linux over the captured procfs tree, reporting its `MemTotal`), and records a full run (`collect --json` → one history line naming a run detail, `status --json` naming that run, `doctor --json`), and since E3.S2 previews EVERY action (`act <ids --help names> --preview --json` under a sandbox with root claimed: exit 0 and every id answered on Linux, exit 2 on Windows, no state written — never a destructive run) |

Shared doubles live in `src_daemon/tests/WslCare.TestSupport` (`TempRoot`, `SandboxHost`,
`RecordingCommandRunner`, `FixedTimeProvider`, `DirectoryLinks`, `AccessDenial` — a real access
denial on one directory, `chmod 000` on Linux and an inherited `icacls` deny for the current user's
SID on Windows, lifted on dispose and reported as unavailable when it does not take — `TerminalText`
— stderr with the console sink's colour removed and whatever control characters are left — and
`ChildProcess` — the one launcher the process-level tests share: argv list, 30 s ceiling, the whole
tree killed on timeout, UTF-8 streams — and since E3.S1 `HostileInputs` — the seeded generator of the command-policy
property tests — `FakeProcessTable` and `LinuxSandbox` — the distro's layout over a temporary root on either
operating system). E3.S2's action tests keep their worlds beside them in `WslCare.Core.Tests`: `Actions/DockerWorld` (the
captured Docker under the product policy, edited as text), `Actions/UserWorld` (a target user `me`, tools in their bin
folders, a written process table) and `Processes/Policy/GeneratedWorld` (the action property's generated Docker / snap).
The fake tool is its own project, `src_daemon/tests/WslCare.FakeTool`, so it ships in no product binary.

## How they run

xUnit v3 on Microsoft Testing Platform. Each test project builds into its own runner **executable**;
`dotnet test` is never used (there is no VSTest host — it aborts with a tooling error).

```bash
dotnet build wsl_care.slnx -c Release -m:4
./src_daemon/tests/WslCare.Core.Tests/bin/Release/net10.0/WslCare.Core.Tests.exe
./src_daemon/tests/WslCare.Cli.Tests/bin/Release/net10.0/WslCare.Cli.Tests.exe
./src_daemon/tests/WslCare.Scenarios/bin/Release/net10.0/WslCare.Scenarios.exe
./src_daemon/tests/WslCare.Scenarios/bin/Release/net10.0/WslCare.Scenarios.exe --filter-class "*ConfigFlows"
```

On Linux the executables carry no `.exe`. CI (`ci · daemon`) runs exactly these three commands in
Release on every leg — `ubuntu-latest` (`linux-x64`), `ubuntu-24.04-arm` (`linux-arm64`),
`windows-latest` (`win-x64`) — then publishes the Native AOT binary for that leg's RID and smokes it.

Every test that touches a disk does so under a temporary root of its own; every run of a built binary
gets `WSL_CARE_ROOT`, so the real profile, `/etc`, `/var` and the real log folder are never read or
written. Tests that need a directory link create a symbolic link, or a junction where the account may
not create symlinks (the owner's machine), and skip with that reason when neither works.

## The scenario harness (`WslCare.Scenarios`)

**One scenario, one world** — `ScenarioHome` makes a temporary directory holding:

| Part | What it is |
|---|---|
| `root/` | the CLI's `WSL_CARE_ROOT`; assertions read files through `HostPaths.ForThisMachine(root)` — the product's own layout — never a guessed path |
| `fakebin/` | the CLI's **whole** `PATH`: the fake tool's apphost copied as `docker`, `systemctl`, `journalctl`, `powershell` (`.exe` on Windows; `powershell.exe` on Linux too, the name WSL interop uses) beside its dll. Nothing else is on `PATH`, so a verb can reach only what the scenario fakes; an unfaked tool fails to start rather than reaching the real one (the ubuntu runner has a real `docker`) |
| `fake-calls.jsonl` | the argv log: every fake invocation appends `{"tool", "argv", "location"}` under an exclusive open — `location` is the folder the answering copy was started FROM, so a scenario can tell the fake on `PATH` from a decoy planted elsewhere (`PlantDecoyInWorkingDirectory`) |
| `fake-script.json` | the scripted answers: for a tool and an EXACT argv, the fixture whose bytes go to stdout, a stderr text and an exit code; an unscripted call exits 98 and says so, and a fake started outside a scenario exits 97 |

**Why a C# fake and not scripts.** One fake must work on both families. On Windows a `docker.cmd` is
not found when a program starts a bare `docker` without a shell (.NET's `Process.Start` and
`CreateProcess` resolve `.exe` only), so script fakes would be a second, different fake per OS. A
renamed apphost is a real `docker.exe` / `docker` on each, and it is the product's language (scenario
rule point 2). The protocol — variable names, line format, exit codes — is one file,
`WslCare.FakeTool/FakeToolProtocol.cs`, which the harness references instead of retyping.

**Fixtures** (`WslCare.Scenarios/fixtures/`, copied beside the harness): one **synthetic** file,
`synthetic/harness-self-test.txt`, labelled as such in its first line, used only to prove the fake
answers from fixtures byte for byte. Real tool output lives in `src_daemon/tests/fixtures/docker/` (below).

**The captured Docker answers** (E2.S2): `src_daemon/tests/fixtures/docker/ubuntu-2026-10-02/`, linked into the
Core, CLI and scenario outputs as `fixtures/docker/…` and read through `TestSupport/DockerFixture`. CAPTURED by
the live contract itself (`WSL_CARE_LIVE_CAPTURE`) inside WSL `Ubuntu` on 2026-10-02T15:22:28Z — the stdout of
exactly the argv the product builds — and redacted by `research/diagnostics/docker_fixture_redact.mjs`
(mechanical: a label allowlist, commands, container / named-volume / network / local-image names mapped once
across all files, home paths; it fails on any original name left; rerun on the raw capture it reproduced the
files byte for byte). `DockerFixture.Answers` pairs each product `ToolCommand` with its file — the inspect argv
built from the ids the product's parser reads out of the captured `system df -v` — and the scenario scripts the
fake `docker` from that list, so the fake replays Docker's answers for exactly the product's argv. Its
`SOURCE.txt` says what the state was: the afternoon AFTER the one-time cleanup. **What the fixture reproduces:**
the afternoon's rows (A4 3 / 641.4 MB, A5 13 containers, A6Unused 10 images / 9.806 GB, A7 4 entries /
23.95 MB, 13 kept named volumes / 16.06 GB), each landing on Docker's own `system df` reclaimable. It does NOT
reproduce the 2026-10-02 MORNING rows of the one-time cleanup (387 volumes / 59.6 GB, …): their raw outputs were
never saved, and no fixture is invented from the summary.

**The captured health answers** (E2.S3): `src_daemon/tests/fixtures/health/ubuntu-2026-10-02/`, read through
`TestSupport/HealthFixture`, CAPTURED by the live contract inside WSL `Ubuntu` on 2026-10-02T17:34:47Z — `systemctl
list-units --failed`, `journalctl --list-boots`, the two journal searches (875 clock changes, one order-7 allocation
failure), `timedatectl show`, `systemctl --version`, `systemctl show` of `fstrim.timer` and `wsl-pro.service`, `snap list
--all`, the Windows clock probe (its user name redacted to `owner`). The unfiltered `docker events` window was measured
(248 healthcheck events spanning 91 s) and NOT kept: its exec actions carry command lines with credentials. Event lines
in tests are synthetic in the captured shape (`TestSupport/DockerEventLines`). The fake learned three things for E2.S3:
prefix matching (an argv carrying an instant), a call budget per answer (`upTo`: down, then up) and output followed by a
hang (a live stream); `timedatectl` and `snap` joined the fakes; `RunningChild` sends SIGTERM to a pid the test started.

**The captured procfs tree** (E2.S1): `src_daemon/tests/fixtures/procfs/ubuntu-2026-10-02/`, linked into
the Core, CLI and scenario test outputs as `fixtures/procfs/…` and read through
`TestSupport/ProcfsFixture`. CAPTURED from WSL `Ubuntu` on 2026-10-02T13:41:57Z by
`research/diagnostics/wsl_procfs_fixture.sh` (writes only under `/tmp` in the distro, streams a tar out):
`meminfo`, `buddyinfo`, `stat`, `pressure/*`, `self/auxv`, `status`/`stat`/`cgroup`/`cmdline`/`comm` of 51
processes (the 40 largest by RSS, pid 1, every `/mnt/` walker), the cgroup files of the 10 containers then
running, a reduced `etc/passwd`. Its `SOURCE.txt` names the source, the date and the four redactions (two
token values, a telemetry key, a company site name). The `cwd` links are in `links.txt` because a Windows
checkout cannot hold symlinks: in-process tests answer `ReadLink` from it (`ProcfsFixture.LinkOverlay`),
the scenario copies the tree into its `WSL_CARE_ROOT` and makes real symlinks (Linux). Edges the capture
cannot show — a container member visible in `/proc`, a child of `systemd --user`, a negative remainder —
are built as **synthetic** trees in the kernel's formats (`Core.Tests/Collectors/SyntheticProcTree.cs`,
labelled so), with the captured `auxv`.

**The harness reads the product's own types**: the verb register `CommandLine.Commands`, `ExitCode`,
`ConfigKeys`, `ConfigValidation` (every accepted or refused fixture value is checked against the real
validator first, and the refused value is the key's maximum + 1 read from the register), and the
`config get --json` report through the product's source-generated `WslCareJsonContext`.

**stderr is classified, not counted** (`CliStderr`). Every run that reaches the machine writes at
least its Information request line to stderr — the family logging rule sends the console sink there
because stdout carries answers. So "a refusal is ONE stderr line" holds for the **message** (`wsl-care: …`),
not for the stream: a scenario asserts exactly one message line, and that every other line is a
well-formed log line (`[HH:mm:ssZ LVL] …`, colour stripped) — nothing unexplained. Measured
2026-10-02 when the first scenario run found two lines; the in-process tests run with a silent logger
and could not see it.

## The derived verb register

`VerbRegisterTests` enumerates `CommandLine.Commands` — the one register the parser and the help text
are built from — and for every verb:

1. runs its `Example` argv against the built CLI in a fresh home and expects exit 0, the usage code 2, or —
   for `act`, whose example the unprivileged harness runs — the documented refusal 77 (*needs root*); never 70
   (internal) or a crash;
2. requires a row in **§ Flow catalogue** below whose FIRST cell starts with `` `wsl-care <usage>` ``,
   the usage exactly as the register spells it. Prose and other tables do not count.

Two companions keep (2) from passing vacuously: a planted verb `planted-verb-without-a-row <thing>` must
be reported missing from this real file, and a synthetic file mentioning verbs only in prose, in another
table, and after the catalogue section must report them missing. **Observed red first** (2026-10-02,
against a stub check that never found anything missing): the planted companion failed with *Expected
collection to be equal to {"planted-verb-without-a-row <thing>"}, but found empty collection*, the
synthetic one likewise — and the main check passed **green** against that stub, which is exactly why the
companions exist.

## The live contract (`WslCare.LiveContract`)

The one check that the real tools still speak what the parsers read (plan §15a C2: fakes prove invocation, not
the output contract). It is an xUnit v3 MTP executable in `wsl_care.slnx`, built with everything else and run by
hand — none of CI's test steps names it:

```bash
./src_daemon/tests/WslCare.LiveContract/bin/Release/net10.0/WslCare.LiveContract          # local: a skip names what is absent
WSL_CARE_REQUIRE_LIVE=1 ./src_daemon/tests/WslCare.LiveContract/bin/Release/net10.0/WslCare.LiveContract   # release: a skip FAILS
```

Run it **inside WSL `Ubuntu`** (a Linux build, as E2.S1's procfs scenario) for the whole contract; on Windows the
Docker half runs against Docker Desktop and the systemd half skips (no `systemctl`). It checks: `docker version`
reads as reachable; `system df` has the four types, every figure readable; the product's sums over `system df -v`
rows equal Docker's reclaimable totals for images, volumes and build cache, and the counts match; `volume ls
--filter dangling=true` names exactly the volumes `df -v` shows without a link; `ps -a` rows, read by the same
row parser, name the `df -v` containers; `container inspect` through the template answers for containers `df -v`
lists, never prints `Env`, and every mounted volume is one `df -v` lists; `stats --no-stream` has one sample per
running container; `events` of the last 24 h are container starts inside the window; since the E2 code round
`network inspect bridge` names a 64-hex engine mark whose creation is no later than the start of any running
container (the continuity rule's engine signal, `DockerContractTests`); `systemctl show` of journald
is an active unit with an activation time, of a missing unit `not-found`; `journalctl --disk-usage` is a positive
byte count; since E2.S3 the failed units and the boots as JSON, journal searches (one matching nothing answers a count of 0), `timedatectl`, `systemctl --version`, `UnitFileState`, `snap list --all`, the Windows clock probe through WSL interop, an unfiltered past events window, and a FUTURE `--until` streaming and closing by itself (`HealthContractTests`). Pairs of answers compared with each other are taken while Docker holds still (probe, subject, probe
again, three attempts) and an error Docker printed is retried twice 5 s apart — without both, a parallel session
building images made the run red for reasons that were not the parsers' (measured 2026-10-02). `CI=true` skips it
unless required.

**Runs:** before every daemon release, on the owner's machine, with `WSL_CARE_REQUIRE_LIVE=1` — the release
checklist in `POST_DEPLOY.md`; and whenever the Docker or systemd version on the machine changes.

## What each guarantee rests on

| Guarantee | Test file |
|---|---|
| A daemon assembly is stamped from `src_daemon/version.txt`; an unstamped assembly reports `unknown` | `WslCare.Core.Tests/ProductVersionTests.cs` |
| "Strictly under" is a proper descendant, case-sensitive on Linux and case-insensitive on Windows, never fooled by `~/gitx` vs `~/git` or by trailing separators; roots split from segments on both families; a relative path is refused | `WslCare.Core.Tests/Hosting/PathRulesTests.cs` |
| The real path follows a link in the middle of a path, applies `..` to the link TARGET, resolves relative targets against the link's parent, follows chains, ends a cycle as `Unresolvable` (not a hang), stops at a component that cannot be inspected and names it (fail closed), changes drive through a Windows junction, never climbs above the root | `WslCare.Core.Tests/Files/RealPathTests.cs` |
| Linux and Windows layouts match plan §6 / §4.6; a sandboxed layout keeps every path under its root; `WSL_CARE_ROOT` is the variable | `WslCare.Core.Tests/Hosting/HostPathsTests.cs` |
| The never-list, as a table per OS family: inside the root allowed; outside the root, an agent folder, `~/git`, Claude's temp folder, a too-broad root each refused by the NAMED rule; the archive permit allows a move out of an agent folder and nothing else; `projects/*/memory/` is never moved even with the permit; a move's destination is judged; a refusal names the action and the path | `WslCare.Core.Tests/Files/DeletionPolicyTests.cs` |
| Over a real disk: `..` traversal out of the root is refused before anything is deleted; a link inside the root into an agent folder is refused (through it, and the link itself); an atomic write replaces content and leaves no temp file, and is refused outside its root before writing; a move inside the root is performed; missing is not unreadable; 40 parallel appenders produce 40 whole lines; a held lock makes an append time out; a reader holding the history open never blocks an append, and an atomic replace waits out a reader that holds the file for a moment (Windows refuses a rename onto an open file even when the reader shares delete). **The flaky Core run of 2026-10-03, found under load**: `RunRetentionTests.An_append_that_arrives_while_the_history_is_being_rewritten_lands_in_the_new_file` failed 2 of 4 runs with 24 CPU burners and 2 of 10 with 8 (`IOException: The process cannot access the file '…history.jsonl' because it is being used by another process`, from `PhysicalFileSystem.AppendLine`) — retention reads the history again right after its rewrite releases the lock, and `File.ReadAllBytes` shares read only, so on Windows a racing run's append lost its line; the engine's heartbeat test failed the same way once (`beaten` stayed `0001-01-01`: its raw read of `running.json` threw mid-replace). **Red** (deterministic, before the fix): `Did not expect any exception because a reader of the history must never make a run lose its line, but found System.IO.IOException: The process cannot access the file …`; **break-it** of the rename wait (`ReplaceRetryFor` = 0): `System.UnauthorizedAccessException : Access to the path is denied`; green after `ReadFile` opened with `FileShare.ReadWrite \| FileShare.Delete` and the atomic rename waits up to 2 s on Windows. Proof: the retention test 0 failures in 24 loaded runs (8 burners) after the fix, the whole Core suite green in 12 of 12 loaded runs once the runner's wall-clock tests ran alone (below) | `WslCare.Core.Tests/Files/PhysicalFileSystemTests.cs` |
| Fail closed: with the real link reader for every component but one, which throws what an access denial throws, a delete, a directory delete, a move (source or destination), an atomic write and a declared root through that component are each refused by `Unresolvable`, naming the component, and nothing is deleted, moved or written; the same seam still allows what does not pass through it; a REAL directory this account was denied (`AccessDenial`) refuses a delete under it as `Unresolvable` rather than letting the policy allow it and `File.Delete` throw | `WslCare.Core.Tests/Files/UnresolvablePathTests.cs` |
| The atomic write writes where it was ALLOWED to: a directory moved out of the declared root and replaced by a link to it — after the target was approved, or after the temporary file was written — is refused (`OutsideDeclaredRoot`) and the file outside keeps its old content; a target replaced by a link to a place INSIDE the root after the temporary file was written is refused as `PathChanged` and the temporary file removed; the temporary file is created in the target's RESOLVED parent, not beside the spelled path. The swaps run inside the file system's own step callback, at the moment a racing process would act | `WslCare.Core.Tests/Files/AtomicWriteRevalidationTests.cs` |
| The real runner: exit code and both streams captured; a missing executable is `FailedToStart`; a timeout kills the WHOLE tree (the grandchild's pid is observed dead) and reports what was captured; output past the cap is cut and marked; the caller's cancellation surfaces as `OperationCanceledException`, not as a timeout; a refusing policy prevents the start entirely; a request needs a positive ceiling. `StreamAsync` (gate finding #1/#5): a callback that throws, and the caller's cancellation, each leave the streaming child DEAD when the exception arrives (killed and reaped in a `finally`) and its grandchild gone (observed by pid); a child writing 1.6 MB to stderr while streaming 2 000 stdout lines runs to its end, its stderr capture cut at the cap. **Teeth** (the kill was already there, so a break-it): with the finally's kill removed, `Expected IsAlive(pids!.Value.Parent) to be False because the streaming child 29756 is killed AND awaited before the exception propagates, but found True` (and the same for cancellation); with stderr not drained, `Expected type to be WslCare.Core.Processes.CommandOutcome+Exited, but found WslCare.Core.Processes.CommandOutcome+TimedOut` at the 45 s ceiling; restored, green. **Wall-clock tests, run alone since 2026-10-03** (the non-parallel `WallClock` collection of `WslCare.Core.Tests`): under load the shell loops outran their budgets (the 1.6 MB stderr child its 45 s, the 3 000-line cap child its 60 s, the PowerShell parent printed no pid inside the 6 s ceiling) — 4 failures in 9 loaded runs; the budgets are now 3 min / 3 min / 20 s (tree kill under 60 s), and 12 of 12 loaded runs passed | `WslCare.Core.Tests/Processes/ProcessCommandRunnerTests.cs` |
| The product decides which file a tool name means, on `PATH` alone: the first absolute entry holding it wins and is returned as a FULL path; a name on no entry is not found with a reason naming it and the count searched; `.`, a relative entry and an empty entry never stand for the current directory (a decoy reachable only through them is never chosen); an absolute path passes through, a relative one is refused; on Windows only `.exe` / `.com` are candidates, never a `.cmd` / `.bat` that would need a shell; on Linux a file without an execute bit is skipped for the next entry; the runner turns "not found" into `FailedToStart` saying PATH was searched | `WslCare.Core.Tests/Processes/ExecutableResolverTests.cs` |
| The register and `default.json` name the same keys; every default validates; the plan's defaults are the shipped ones; a value round-trips through JSON | `WslCare.Core.Tests/Config/ConfigSchemaTests.cs` |
| A layer flattens to dotted keys with the line of every leaf; a syntax error reports its line in one sentence; a non-object is malformed; comments, trailing commas, a BOM and `$schema` are accepted | `WslCare.Core.Tests/Config/ConfigDocumentTests.cs` |
| One validator for file and command line: bool spelling, int range with the range in the message, enumerated text exactly, free text, comma-separated lists; a wrong JSON shape names the offending value | `WslCare.Core.Tests/Config/ConfigValidationTests.cs` |
| Precedence default < machine < user with the layer named per value; an unknown key, an out-of-range or mistyped value, a file that is not JSON, an unreadable file each make the result observe-only with file and line while the same file's valid keys still apply and no default re-enables anything | `WslCare.Core.Tests/Config/ConfigLoaderTests.cs` |
| `set` writes the key nested and the loader reads it back from the user layer; other valid keys are kept; invalid keys are dropped and named; an unparseable file is moved aside with a UTC stamp; two repairs in the same second (frozen clock) keep BOTH broken files — `…Z` and `…Z-2`, neither overwritten — and both succeed; `reset` removes a key and says whether it was there; a missing file becomes `{}`; no temp file is left | `WslCare.Core.Tests/Config/UserConfigWriterTests.cs` |
| A run id is the UTC second and the pid; a record is one camel-case line with string enums and `schemaVersion`; every outcome (`interrupted` included) round-trips; the writer appends one line per record under the state directory | `WslCare.Core.Tests/Records/RunRecordTests.cs` |
| No file outside `PhysicalFileSystem.cs` and `ProcessCommandRunner.cs` deletes, moves or starts a process; the scanner matches a planted instance formatted across lines; it still finds the sanctioned calls in each seam; it ignores words that merely contain the names; `WslCare.Core` references no package | `WslCare.Core.Tests/ArchitectureTests.cs` |
| Parsing: every help spelling; `--version`; unknown verbs named; near misses refused; extra words refused; control characters never reach a message; `config get` takes an optional key and `--json` in either order and refuses a second key or an unknown option; `config set` needs exactly key and value; `config reset` exactly one key; `status` takes nothing or `--json` and refuses anything else; `config` alone lists its sub-verbs; every registered command is in the help text and its example parses (derived from the register); the longest spelling wins when two commands share a prefix, whatever their register order (a synthetic register with the short spelling first) | `WslCare.Cli.Tests/CommandLineTests.cs` |
| The whole program in-process: `--version` prints the stamp only; `--help` lists the config verbs; an unknown verb exits 2 with one stderr line even with a newline in it; a cancelled token stops the run as a cancellation | `WslCare.Cli.Tests/ProgramTests.cs` |
| `config get` lists every key with layer `default` on a fresh host, one key alone, refuses an unknown key (exit 2); `config set` validates, writes, and `get` then shows `(user)`; an out-of-range value is refused with ONE stderr line and the file untouched; an unknown key or a mistyped value is refused and nothing is written; a broken user layer is reported observe-only on stderr and in the JSON (`configError` with file and line) while still answering; `set` repairs a broken layer and the next `get` is valid; a non-JSON layer is moved aside and the command says where; `reset` reports the effective value again; the JSON report carries every key with value and layer | `WslCare.Cli.Tests/ConfigCommandTests.cs` |
| No control character reaches stderr raw: an unknown key holding a newline, a carriage return or a clear-screen escape (named fixtures, so a failing test name cannot clear the terminal) is refused by `config get` / `set` / `reset` in ONE message line with none of them left; an internal error whose reason holds them is one clean line; the console sink replaces a control character carried by a logged value or an exception message and keeps the line whole | `WslCare.Cli.Tests/ControlCharacterTests.cs` |
| `Output` is the one road to a stream: every `.Write(`/`.WriteLine(` in the CLI's source is in `Output.cs` or the console sink (scanned across lines); the scan matches a planted instance, still finds the writes in both allowed files, and ignores `AppendLine`, `WriteTo`, `WriteStartObject`, `Format` | `WslCare.Cli.Tests/OutputRoadTests.cs` |
| Escapes on a redirected writer with a control; levels coloured differently and the message unquoted; a file per run that segments at UTC midnight into the next day's folder with the same pid and never rolls backward; retention selects only expired day folders, deletes through the seam, leaves today and strangers, is a no-op on a missing root, and refuses an expired folder that is a link into `~/git`; starting the logger writes one file under the host's log directory at the configured level | `WslCare.Cli.Tests/LoggingTests.cs` |
| The BUILT `wsl-care` as a child process (through `TestSupport/ChildProcess`: 30 s ceiling, tree kill): `--help` exits 0 and lists the config verbs; an unknown verb exits 2 with one stderr line; `config set` / `config get --json` / a refused `set` read and write under `WSL_CARE_ROOT` and each run leaves its own log file there; `--help`, `--version` and a refusal write nothing under the root | `WslCare.Cli.Tests/BuiltBinaryTests.cs` |
| The procfs parsers over the captured tree: `meminfo` sizes are KiB → bytes and counts (`HugePages_*`) are never sizes, a missing line is unavailable naming the key; `buddyinfo` per zone, free blocks of order ≥ 4 / ≥ 7 in zone Normal and their bytes, a zone with none (the 2026-10-01 dump, synthetic line) is a measured 0, an unlisted zone unavailable; PSI `some`/`full` avg10/60/300 + total, a missing `full` unavailable, a malformed `some` unavailable; clock tick and page size from `self/auxv` (100 / 4096), none when absent; `btime`; `status` → name, state, parent, uid, `RssAnon` + `RssShmem` (not `VmRSS`), kernel threads by `Kthread` or no `RssAnon`; `stat` counted from the last `)`; container membership by cgroup path in both Docker layouts; `cmdline` split on NUL, secret-looking values redacted in `--x=v` and `--x v` forms, cut to 200; container cgroups with `memory.current` and `memory.stat` anon + shmem, a missing mount unavailable, an empty one an empty set; `passwd` | `WslCare.Core.Tests/Collectors/ProcfsParserTests.cs` |
| Who holds it: the remainder is AnonPages + Shmem − processes − containers (anon + shmem, never `memory.current`); a negative one is an inconsistent sample with its overshoot, never a negative number; zero is a remainder; an unread input leaves it not computed with the reason; process memory is `RssAnon` + `RssShmem` (a 5 000 kB `RssFile` is not held); a container member visible in `/proc` is counted ONCE, through its cgroup; a member whose container cgroup cannot be read is counted as a process, so still once; on the captured tree the remainder is positive and `memory.current` would have made it negative | `WslCare.Core.Tests/Collectors/AttributionTests.cs` |
| The process table over the captured tree: 51 counted, the top 30 ranked by held bytes (pylance first); owner, parent, state, CPU seconds and age from `btime` + `starttime` / tick, cwd through the link, the shown command line, tty; an unreadable cwd is unavailable with a reason; families in catalogue order (agent before vscode-server) and every process in exactly one; `/mnt/` walkers by cwd or argument; orphaned = parent pid 1 or `systemd --user`; a pid that vanished is counted apart; no procfs → unavailable; no tick → age/CPU unavailable, memory still read; cancellation stops the walk | `WslCare.Core.Tests/Collectors/ProcessCollectorTests.cs` |
| The probes and the report shape: the Linux probe over the captured tree reads memory, 10 containers, 51 processes, a remainder, `df` of its root, and names the Windows binary for the host figures; over an empty root every section is unavailable with its path and the JSON carries no value key (never 0); an unavailable figure is written with `available: false` + reason and no `bytes`; `schemaVersion`, side and sample time; the `.vhdx` figure is named and unavailable; an inconsistent sample is a state with the overshoot; the Windows probe reports host RAM, the system drive and `vmmemWSL` and names the Linux binary for the VM; the real `GlobalMemoryStatusEx` answers (Windows only); `df`'s Use% leaves the reserve out | `WslCare.Core.Tests/Collectors/ProbeTests.cs` |
| Slow parts from the last full run: none before any run (with how to record one); `docker stats` back with run id and age; a newer run with no slow parts does not hide an older sample and a torn line is skipped; a newer run that FAILED to sample is the answer, with its reason and run id; a pre-E2 history line writes no `slow` key and reads as not sampled; the parts round-trip through the compact context | `WslCare.Core.Tests/Records/LastFullRunTests.cs` |
| The read-only queries the collectors added to the file-system seam: a directory link answers its target, a directory or an absent path is not a link, a link that cannot be inspected is unreadable (fail closed, as the deletion policy); the volume holding a directory is measured in one call; a volume that does not exist is unreadable | `WslCare.Core.Tests/Files/ReadOnlyQueriesTests.cs` |
| `status [--json]` in-process over the captured tree: the JSON answer with `schemaVersion`, the fixture's figures, the host named as the other binary, no slow part yet — and the recording command runner received NOTHING; the slow parts of a recorded full run come back with their age, still with nothing started; the text form's lines (ASCII only); a broken configuration layer is named (`observeOnly`, `configError`) and status still answers | `WslCare.Cli.Tests/StatusCommandTests.cs` |
| The harness's own fakes: a bare `docker` looked up by a real shell on the scenario `PATH` reaches the fake, which records its argv and prints the scripted fixture byte for byte with the scripted exit code and stderr; each of `docker`, `systemctl`, `journalctl`, `powershell` (installed as `powershell.exe` on Linux, the name WSL interop uses) answers as itself, records argv exactly (spaces included) and refuses an unscripted call (98); `git` is NOT reachable on the scenario `PATH`; a fake started outside a scenario refuses (97) | `WslCare.Scenarios/FakeToolFlows.cs` |
| The BUILT CLI starts the `docker` on its `PATH`, never one in its current directory: with a decoy fake planted in the scenario's working directory (the CLI's current directory, not on `PATH`) every docker call records the `fakebin/` location; with no docker on `PATH` and the decoy present, `preview` answers `notInstalled` and nothing is started. The scenario child runs WITHOUT `NoDefaultCurrentDirectoryInExePath` (an agent's shell sets it; with it set, `CreateProcess` skips the current directory and the first run of this test passed against the unfixed launcher). **Observed red first** (2026-10-02, Windows, unfixed `ProcessCommandRunner`): `Expected docker to contain only items matching SameFolder(c.Location, …FakeBin) … but {FakeCall { Argv = {"version", "--format", "{{json .}}"}, Location = "…\wsl-care-test-scn-resolve-path-wins-…" }} do(es) not match` and `Expected property report.Docker.Kind … (actual) "commandFailed" "notInstalled" (expected)` — the decoy answered (unscripted, 98) — the shape of CI run 37045304356's nine failures, where System32's real `docker.exe` answered. Green after the fix | `WslCare.Scenarios/ToolResolutionFlows.cs` |
| Docker's human spellings: sizes in the base their suffix names (`MB` = 10⁶, `MiB` = 2²⁰; the summary `9.806GB (48%)`, the `20.5kB (virtual 306MB)` of `ps`), N/A and garbage unavailable, never 0; both timestamp shapes (`2026-10-02 14:23:39 +0200 CEST`, nine-digit RFC 3339) to the same UTC instant; Go's zero time is "never"; percentages; a label value holding a comma; a listing with a non-JSON line unavailable naming the line | `WslCare.Core.Tests/Docker/DockerTextTests.cs` |
| Every failure kind with Docker 29.6.1's REAL stderr: missing socket, missing named pipe, the pre-29 wording → `daemonStopped`; permission denied, connection refused → `socketRefused`; Docker's i/o timeout and our ceiling → `timedOut` ("its process tree was killed"); no executable → `notInstalled`; a policy refusal; a cut answer → `unparseable`; an unknown message → `commandFailed` quoted; `"Server": null` → `daemonStopped` whatever the exit code; an inspect naming only vanished containers is read for the others, the same stderr on another command (or mixed with a refusal) still fails | `WslCare.Core.Tests/Docker/DockerCliTests.cs` |
| The parsers over the CAPTURED answers: version; `system df` totals (23 / 29 / 48 / 72; 9.806 GB, 16.7 GB, 23.98 MB reclaimable); `df -v` rows whose sums equal those totals; 16 dangling volumes (3 anonymous); 29 inspected containers, 13 stopped, 28 unbounded logs, bind mounts with no name; `ps -a` ids = `df -v` ids; 16 stats in binary units; an empty events window and a real event line; `systemctl show` of an active and a missing unit; `journalctl --disk-usage` 407.2M = 426 980 147 bytes | `WslCare.Core.Tests/Docker/DockerParserTests.cs` |
| Every command `DockerCommands` can build (enumerated by reflection) is a read verb with a ceiling; seven write verbs are not read verbs; the template names no environment, command or source; a batch of 101 ids is refused; over the captured answers the collector runs exactly the five product argvs; when no daemon answers (four ways) only the version probe runs and every part carries its reason; a failed `df -v` leaves the totals and the dangling list readable and starts no inspect; 150 containers are inspected in two batches | `WslCare.Core.Tests/Docker/DockerCollectorTests.cs` |
| `volume-seen.json`: a still-unattached name keeps its first sighting, a new one is stamped now, a vanished one is dropped; the record round-trips through the state directory with no temporary file left; no record is empty, a broken one is reported; an UNWRITABLE state directory (`AccessDenial`) makes the write `read-only` and leaves no file | `WslCare.Core.Tests/Docker/VolumeSeenTests.cs` |
| The rows over the captured Docker: at limit 0 A4 3 / 641.4 MB, A5 13 + their 8 anonymous volumes (6 named kept), no Testcontainers, A6Unused 10 = Docker's images reclaimable, A7 4, 13 kept named volumes, A4 + kept = Docker's volumes reclaimable; at the shipped limits nothing young is taken and the notes say what was left; an old-enough volume is selected and a `wsl-care.keep` one never; an image a stopped container uses is never listed even when Docker's count says 0; Testcontainers counted apart by hours; the build-cache age filter selects 0 while a 0 GB cap shows the whole cache; Docker 22 → A4 counted with its refusal; no Docker → every row unavailable with the reason | `WslCare.Core.Tests/Docker/CleanupPreviewTests.cs` |
| The hygiene audit: 28 unbounded logs, a log planted where an Engine in the distro would write it is measured, the others "inside its own VM"; on Windows the builder GC of `daemon.json` (absent → not present; this machine's file → present, `true`, `20GB`) and log sizes named the Linux binary's; buildx leftovers (synthetic rows); `docker stats` for a full run → 16 samples, or the reason and no containers; the JSON answer writes an unavailable row with `reason` and NO `count` / `reclaimableBytes` key, `schemaVersion`, `docker.kind`, `volumeSeen.recorded: false` on an outage | `WslCare.Core.Tests/Docker/DockerHygieneTests.cs` |
| `preview` parsing: `--all` required, `--json` in either order, anything else refused naming it | `WslCare.Cli.Tests/CommandLineTests.cs` |
| `preview --all [--json]` in-process over the captured answers: every row, kept, totals, the first sightings observed in memory and NOT written (plan §15b #3 — since the E2 code round), read verbs only; the text form one line per row, ASCII only; Docker missing is an answer with exit 0 and only the version probe run | `WslCare.Cli.Tests/PreviewCommandTests.cs` |
| The flows of § Flow catalogue, against the built CLI | `WslCare.Scenarios/HelpAndVersionFlows.cs`, `WslCare.Scenarios/ConfigFlows.cs`, `WslCare.Scenarios/ControlCharacterFlows.cs`, `WslCare.Scenarios/StatusFlows.cs`, `WslCare.Scenarios/PreviewFlows.cs`, `WslCare.Scenarios/CollectFlows.cs`, `WslCare.Scenarios/EventsFlows.cs` |
| The derived register: every verb of `CommandLine.Commands` runs its example (exit 0 or 2) and has a flow-catalogue row; a planted verb is reported missing; prose, other tables and rows after the section do not count | `WslCare.Scenarios/VerbRegisterTests.cs` |
| `collect`'s record order (plan §15b #1): the detail is written (atomically) before the history line, and the line names it; a detail that cannot be written makes the run `failed` with the reason on its line and no detail named; a line that cannot be written fails the run and the NEXT run's reconcile gives the orphan detail an `interrupted` line; a line whose detail is gone is reported *detail lost* and left as it is; an unprivileged run measures and writes nothing (not even a directory); a second run while one holds `run.lock` is busy and measures nothing; the line carries the non-ok warnings and the slow parts `status` reads | `WslCare.Core.Tests/Collect/CollectRunTests.cs` |
| Retention of the run records (90 days, plan §6): an old line goes and its detail with it, a young one stays; a line that does not parse is kept (it cannot be aged); a detail is never removed while a line names it, even in an old day folder; a dead atomic write's temporary file is removed. Since the E2 code round (gate findings #0/#4, #11): an append interleaved INSIDE the history rewrite (a hook between the read and the write, on another thread) lands in the new file; a run 90 days and one hour old keeps its line and detail together, so the next reconcile resurrects nothing; a detail older than the window without a line is never made `interrupted` (a young one still is) and retention removes it with its day folder; an aged day folder no line names goes WHOLE, strays included, and a day inside the window is untouched. **Observed red first** (2026-10-02, unfixed code): `Expected collection to be empty because a run retention pruned is not a run that died, but found at least one item {"20260704T110000Z-100"}`; `Expected records.Select(r => r.DetailPath) to be equal to {"runs/2026-09-30/…"} … but {"runs/2026-06-04/…", "runs/2026-09-30/…"} contains 1 item(s) too many`; `Expected boolean to be False because an aged day no line names is removed whole, but found True`. The interleaved append was GREEN first — `RewriteLines` already read under the lock (E2.S3) — so its teeth were proved by a break-it: with the read moved before the lock it failed `Expected … to be equal to {"20261001T120000Z-100", "20261002T120000Z-4242"} … but {"20261001T120000Z-100"} contains 1 item(s) less`; restored, green | `WslCare.Core.Tests/Records/RunRetentionTests.cs` |
| The thresholds at their edges: MemAvailable at 25 / 24.9 / 15 / 14.9 % against the two settings (and a setting moving the edge); order-7 blocks 32 / 31 / 0; swap 4 / 5 GiB; `/` 80.0 / 80.1 %; the 2026-10-01 18:36 state critical on memory, fragmentation, the VM ceiling and the allocation failure; a fresh boot ok on every memory row; the VM ceiling red only above 90 % and naming `memory=36GB` (never written); one clock observation above the limit is not a drift, two are only when at least 5 min apart (300 s warns, 299 s does not); an unread figure is `unknown` with its reason; A4's count trigger; one verdict per id | `WslCare.Core.Tests/Thresholds/ThresholdRulesTests.cs` |
| The health collectors over the answers CAPTURED 2026-10-02 (`fixtures/health`): the failed unit, the journal's oldest entry, 875 clock changes, one order-7 allocation failure, NTP synchronised, systemd 255, `wsl-pro` enabled, no disabled snap; journalctl's exit 1 with nothing printed is 0 matches, an error is not; disabled snap revisions (synthetic Notes); discard, the automount root, a Windows profile seen as `/mnt/c/Users/owner`, `.wslconfig` in either section; the Windows clock offset with the launch latency subtracted; on the Windows layout the distro parts name the Linux binary; a missing tool leaves only its part unavailable; read verbs only | `WslCare.Core.Tests/Health/HealthTests.cs` |
| The daily folder walk: a tree is the sum of its files; a link inside is neither counted nor entered, a folder that is a link is not walked; bin/ + obj/ only, node_modules never entered; a walk at its entry limit says its figure is a lower bound; a missing folder is missing, not 0; once a day (19 h no, 20 h yes); the npm / apt / disabled-snap figures | `WslCare.Core.Tests/Folders/FolderSizesTests.cs` |
| A8 and A9 from the newest folder sample with its run and age in the basis; unavailable with the reason before one exists, or when the run could not measure the folder | `WslCare.Core.Tests/Docker/FolderRowsTests.cs` |
| The follower's rules (plan §15b #0, #8): the wait for Docker 5 → 10 → … → 300 s; a buffer that reaches back past the last marker fills the gap and records only newer starts; a daemon restart, an empty buffer, a marker older than 24 h and the first start ever are each ONE gap with its reason; a full day of coverage counts complete with the top images; a count overlapping a gap is partial and names it until a whole 24 h lies after its end; a follower that stopped recording, and nothing recorded before the window, make it partial too; the last coverage. **The continuity rule** (gate finding #2/#7/#9; the 3-argument cases above are now the engine-unknown fallback): an idle engine with an empty buffer and no restart is covered with zero starts; a host that slept with the same engine after is covered; a FULL buffer (≥ `FullAt`) whose oldest event is newer than the marker is ONE gap up to that event, and one that still reaches past it is covered; an engine restarted after the marker is a gap from the marker to its start (also into an empty buffer) and the starts after are recorded; a different engine claiming an older start proves nothing beyond its oldest event; an unreadable engine falls back with its reason; a first start on an engine older than the window proves the whole window, one started inside it is a gap up to its start; the capacity is 256 and `FullAt` reads every measured full answer (248) as full; a `covered` marker round-trips its engine and `LastEngine` takes the newest that recorded one; a fresh 24-hour summary answers its window and a stale one is partial with the open gap. **Teeth** (the rule was written before these tests in this session, so a break-it): with the engine evidence ignored (`EngineEvidence.Unknown` passed through) nine failed for the real symptom — `Expected object to be <null> because an idle engine that did not restart dropped nothing, but found WslCare.Core.Events.CoverageLine+Gap`, the same for the slept host and the first start on an old engine, `… to contain "restarted"` / `"full"` / `"different Docker engine"`; restored, green | `WslCare.Core.Tests/Events/CoverageTests.cs` |
| The follower over a scripted docker and a moved clock: down three times then up → waits of 5, 10, 20 s, ONE gap for the outage, the backfilled and the live start, the stop marker carrying the live coverage; a buffer that still covers the outage writes no gap; a segment ending at its `--until` is a covered marker and the next resumes from it; a broken stream goes back to waiting and still writes one gap per outage; `--once` with Docker down returns at once, writes only its markers, read verbs only; day files past 14 days pruned through the seam; an idle engine whose bridge matches the last marker's answers an empty buffer and the catch-up writes no gap, reads the engine ONCE after the events, records it on the new marker and writes the 24-hour summary; an engine restarted since the marker is ONE gap up to its start even over an empty buffer | `WslCare.Core.Tests/Events/EventsFollowerTests.cs` |
| `doctor`: an installation doing its job is healthy and names the wsl-care / docker / systemd / kernel versions; a stale last run, a stopped unit and a silent follower are each a named problem; a fresh machine says nothing was recorded and creates nothing; an invalid layer is a problem and doctor still answers; read-only questions only | `WslCare.Core.Tests/Doctor/DoctorTests.cs` |
| `collect` / `doctor` / `events follow` in-process: `collect --json` records and `status` then reads that run's slow parts with their age and counts the never-run follower as partial; the text form; read-only exits 0 with the stderr note and no state directory; an unwritable history line exits 1; a held `run.lock` exits 75; `doctor --json` exits 0 with its verdict; `events follow --once` with Docker down exits 0 naming why; by an unprivileged process exits 1; a second follower exits 75; stopped by a signal exits 0 with its stop marker; an unprivileged run logs to the user's log directory; `status` answers the follower's 24-hour count (200 backfilled starts) from `starts-summary.json` and neither lists the day folder nor opens a day file (a read-recording file system; gate finding #8). **Observed red first** (2026-10-02, unfixed `StatusCommand`): `Expected reads.Paths … to not have any items matching p.StartsWith("…\wsl-care\container-starts", OrdinalIgnoreCase) … but found {"…\container-starts", "…\container-starts\2026-10-02.jsonl"}`; green after the fix | `WslCare.Cli.Tests/FullRunCommandTests.cs` |
| Docker's event stream and the health tools for real: failed units and boots as JSON, journal searches (one matching nothing answers 0), `timedatectl`, `systemctl --version`, `UnitFileState`, `snap list --all`, the Windows clock probe through interop, an unfiltered past window, and a FUTURE `--until` that streams and closes by itself | `WslCare.LiveContract/HealthContractTests.cs` (run by hand; § *The live contract*) |
| The never-list rule by rule, DERIVED from `NeverList.Rules` (a rule without a known instance is red): each refuses its instance and names itself; harmless argv (`vm.drop_caches=1`, `--vacuum-time=30d`, a volume id, `du` of `/var/cache/apt`) break no rule; 22 spellings of never-commands (`RM.EXE`, `/usr/bin/rm`, `find -delete`, `git clean`, `docker system prune` without `-a`, `sh script.sh`, `pwsh -File`, `wsl --unregister`, a `--grep=` path under `.claude`, a Windows `Temp\claude` path, `runuser -l`, `runuser` without `--`, a full-path `runuser`, `taskkill /IM`) refused; deny by default; a planted template cannot admit `docker system prune -a` (the never-list is asked first); a never-command behind `runuser` is refused even with a planted user template for it; the clock probe is the one PowerShell argv; EVERY read command the collectors build — each factory, a 1- and a 100-id inspect, every unit `systemctl show` reads, the three journal searches — is allowed by the product policy; a hostile or short container id is not a declared instance; a runner cannot be built without a policy; no product file but the runner names its unguarded test seam (with the companion that the scan finds the definition). **Found by this table, not by the fakes:** the journal search's `--unit=systemd-resolved` was refused (a unit name without `.service`), which on Windows had stayed invisible because the clock-jump count is a Linux-only assertion — the scope slot now takes a bare service name | `WslCare.Core.Tests/Processes/Policy/CommandPolicyTests.cs` |
| **The property test** (seeded, 20 000 requests; 500 instances per template; 2 000 action cases): no argv the product policy allows is a never-command by the independent `NeverOracle`, and every one it allows matches a declared template (with vacuity guards: > 10 % of the inputs never-commands, > 10 % allowed template instances); every declared template instantiated with values its slots accept is never a never-command; every registered action, previewed and run over generated configurations (`journal.keepDays`, `dryRun`) and generated journals with hostile file names, asks only for argv the policy allows, of its OWN templates, none a never-command. Companions, each red by design and kept: a permissive policy (`ALLOWED a never-command: git worktree prune`), a never-list-on-the-outer-argv-only policy (undeclared argv AND `ALLOWED a never-command: runuser -u me -- …/dash -c x`), a planted `vm.drop_caches=<0..3>` template (named by the template property; still refused at run time), a planted action that builds `sh -c "rm -rf <preview name>"` | `WslCare.Core.Tests/Processes/Policy/CommandPolicyPropertyTests.cs` (+ `NeverOracle.cs`, `TestSupport/HostileInputs.cs`) |
| The engine over the distro's layout in a temp root with scripted actions: the fixed execution order whatever the request order, `running.json` naming each action while it runs and gone after, detail then history line with per-action status and freed bytes; a throwing action and a failing one recorded `failed` while the next ran and the run `completed`; the timer honours each `auto` switch and each trigger, a button neither; the timer dry for 7 days from its first action pass with `dryRun` off (the last minute dry, the minute after ran; `wouldFreeBytes` on the line), dry while `dryRun` is on, never for a button, a recorded start never moved, an unreadable stamp restarting the week; a heavy action deferred at 87.5 % CPU (5-minute load 3.5 over 4 CPUs) and while `dotnet test` runs, run when idle, the deferral recorded; a timer-only rule not holding back a button; an unread CPU figure deferring; a dead pid's `running.json` swept with an `interrupted` line naming the action it was on and the last heartbeat; a reused pid (different start) swept as mismatched; a live pid with a 31 s heartbeat WEDGED, nothing run, the file byte-identical, no history; a live fresh one busy though the lock was free; an unparseable file or one missing fields refused; a dead run that had recorded itself removed without a second line; a held lock → busy, nothing written; a `collect` started inside an act's run → `busy` (one lock for both); a wedged holder of the lock reported wedged; a signal mid-run → `interrupted` recorded, the next action not run, `running.json` gone, the cancellation rethrown; the heartbeat rewriting `running.json` on its own while an action is busy; a preview taking no lock and writing nothing; a user-scoped action refused for an ambiguous target while A10 ran; the other side's action and a preview's refusal named; an invalid configuration layer → every action skipped, outcome `observeOnly` | `WslCare.Core.Tests/Actions/ActionEngineTests.cs` (+ `ScriptedAction.cs`) |
| A10, the reference action, under the product policy: the preview counts only ARCHIVED files older than the limit (not the active `system.journal`, not a 5-day-old archive, not a stray `.txt`) and carries journald's own size; the trigger fires above 1 GiB and not at exactly 1.0 G or at 900 M; an unreadable size is no preview with the reason; the freed bytes are the before-sizes of exactly the files that are gone (3 000 — not the preview's 5 000), before / after totals beside them, the two commands in order; a failing vacuum is a failure in journalctl's words, still measured; through the engine a button press runs it and records `ran` | `WslCare.Core.Tests/Actions/JournalVacuumTests.cs` |
| The target user, as a table: `wsl.conf`'s default (quoted, spaced, after another section, `root`), else the single login account (root, a nologin daemon and `nobody` ignored); two login accounts, a default user `passwd` lacks, an invalid name, a relative home → ambiguous with the reason; none → none; an unreadable `passwd` refuses. A tool run as the user: `runuser -u me -- <the FIRST bin folder's file> args`, a clean environment of exactly `HOME`, `USER`, `LOGNAME`, `PATH` (the fixed folders), allowed by the policy; nvm's default resolved to the highest installed match (`22` → `v22.11.0`) and an unresolvable alias (`lts/*`) left out; a tool in no bin folder refused before any start; the policy refusing a wrapped file outside the bin folders (`/tmp/evil`, `…/.local/bin/../../git`, `~/git/bin`, relative), an inherited environment, a machine template behind `runuser` and a user template without it; every login account's `git` and agent folders protected by the deletion policy, not only `$HOME`'s | `WslCare.Core.Tests/Actions/TargetUserTests.cs` |
| The parts: number / user-name / text slots at their edges and against hostile values; binding names the slot and refuses extra values; a catalogue refuses a pathed executable and a repeat that is not last; the action ids are exactly the `auto.*` keys and the execution order holds each once (A5 → A4 → A6 → A7 → A8 → A9, A1 → A2); id lists known and unique, an unknown id refused naming the legal values; root is the OS's answer and only a sandbox may claim it; the build detector; which idle rule waits for which trigger | `WslCare.Core.Tests/Actions/EnginePartsTests.cs` |
| `act` in-process: the parse (no action, a flag first, neither or both of `--preview` / `--confirm`, a repeated or unknown flag, an unknown id, a duplicate id → usage); an unprivileged `--preview` or `--confirm` exits 77 with NOTHING touched — no state directory, no lock file, not one read command; a preview prints the live preview as JSON and writes nothing; a confirmed act records, answers `recorded` with the measured result and ran exactly disk-usage then vacuum; the text form; a failed action exits 3 naming it with the run recorded; a held lock exits 75 having run nothing; a wedged run exits 76; an action this build does not hold, and A10 asked of the Windows binary, exit 2 by name; an invalid layer exits 78 with nothing run; the help names the verb and the actions this build holds | `WslCare.Cli.Tests/ActCommandTests.cs` |
| `act` against the BUILT CLI: unprivileged → 77 and no state file, no lock, no fake called; with root CLAIMED in the sandbox, `--preview` reads journald's size through the fake and writes nothing (on Windows: exit 2 naming the distro side); `--confirm` runs disk-usage then `--vacuum-time=30d` through the fake (nothing real is vacuumed), records detail then history, removes `running.json` (Linux legs); with the run lock held by the test, `collect` exits 75 (both families) and `act` exits 75 (Linux) and neither records anything | `WslCare.Scenarios/ActFlows.cs` |

| A4–A7 over the CAPTURED Docker (`Actions/DockerWorld`: the fixture files, edited as TEXT for a server version, a keep label, a Testcontainers label, under the PRODUCT policy): A4 removes exactly the volumes `docker volume rm` printed back, an "no such volume" counted as already gone (not a failure), freed = the `df -v` sizes of the CONFIRMED ones, Docker's Local Volumes total beside it, first sightings written; a keep-labelled volume never in the argv; Docker 22 and an unreadable version refuse through the engine with no removal; a `manual` run without a shown list refuses, with one removes only shown names still candidates (a shown non-candidate counted in a fact), recorded `manual`; "volume is in use" kept by Docker, an unreadable answer not counted and a failure; A5 confirms by printed id, a container that started since (Docker quotes its NAME) kept, only anonymous volumes Docker no longer lists counted, never `-f`; A5Testcontainers takes only labelled containers and nothing else is removed when nothing is selected; A6Unused's argv (`-a -f --filter until=168h --filter label!=wsl-care.keep=true`) and Docker's own total; no prune when nothing is selected; A7 on the timer takes `--max-used-space 20GB` from the CAPTURED help, `--keep-storage` from an older help, refuses with neither, a button prunes `-a -f`; and — derived over every registered action that has a row — each action's live preview equals its row of `preview --all` (what, availability, basis, count, bytes, refusal) | `WslCare.Core.Tests/Actions/DockerCleanupTests.cs` |
| The user-scoped cleanups over a sandbox whose target user is `me` (`Actions/UserWorld`): A8 without npm is a SKIP and runs nothing; with npm it runs `runuser -u me -- <full path> cache clean --force` with a clean environment (HOME = the user's), allowed by the product policy, freed = `~/.npm` before minus after; A17 with no tool is a skip naming cargo sweep and Gradle, with pnpm + pip3 runs exactly `pnpm store prune`, `pip3 cache purge` and sums what left the caches; A12 removes only the browser no live project's `browsers.json` references (a link into a vanished `/tmp` package references nothing), the trigger never fires, the Playwright part refuses whole without `.links/`, without a `browsers.json`, during an install (`__dirlock`) or without a process table, keeps a browser a process names, clears NuGet's http-cache with `dotnet nuget locals http-cache --clear` as the user; A14 keeps the newest two builds and the one a process names, removes the rest and the `.obsolete` extension, ignores a traversal key; and a build folder that is a LINK into `~/git` is refused by the deletion policy with the repository untouched | `WslCare.Core.Tests/Actions/UserCacheTests.cs` |
| A9: `apt-get clean` and `snap remove` of exactly the revisions still disabled, each measured by its file gone (an x-revision kept and noted), allowed by the product policy; apt-get and snap missing skip their parts without failing; the trigger at 200 MiB + 1 byte and at one revision | `WslCare.Core.Tests/Actions/PackageCacheTests.cs` |
| A11: the candidates as a table (orphaned, family, age, no terminal, not root, not a zombie, not this process); a process whose CPU ticks moved during the window is not a suspect, an idle one is signalled with its pid AND start; CPU, a terminal, a reused pid or a vanished one between preview and signal → nothing signalled, not a failure; an unwired sender refuses with the reason; on Linux the pidfd sender ends ITS OWN `sleep` child on SIGTERM, kills a child that ignores SIGTERM after the grace, and refuses a pid whose start does not match | `WslCare.Core.Tests/Actions/SuspectTerminationTests.cs` |
| The target user's home (plan §15c #2): as root with `wsl.conf`'s default, `Home`, the user layer and the daily walk's `~/.npm` are that user's and the replaced home stays protected; with two login accounts and no default the user layer is unreadable and the run observe-only; unprivileged nothing moves; a missing `/etc/passwd` is NO account, an unreadable one is ambiguous | `WslCare.Core.Tests/Actions/TargetHomeTests.cs`, `TargetUserTests.cs` |
| A tool not installed is a SKIP through the engine (never run, never failed) | `WslCare.Core.Tests/Actions/ActionEngineTests.cs` |
| The ACTION property now runs all thirteen registered actions against a GENERATED world per case (`Processes/Policy/GeneratedWorld`: Docker answers with hostile volume, container, mount and repository names, Docker 22 / 29 / unreadable, keep and Testcontainers labels, a snap listing with hostile names, three builder-prune helps, a shown list carrying hostile names, pip present or not), runuser-wrapped argv judged against the action's USER templates and the wrapped argv by the oracle — and asserts, derived from the registry, that EVERY declared template ran in some case | `WslCare.Core.Tests/Processes/Policy/CommandPolicyPropertyTests.cs` |
| Only the signal seam (`Processes/ProcessSignals.cs`) names a signalling call (`"pidfd_send_signal"`, `"kill"`, `"tgkill"`, …); the scan still finds the seam's own call and a planted one | `WslCare.Core.Tests/ArchitectureTests.cs` |
| `act` in-process (E3.S2): `--manual` records the trigger `manual`; a shown list needs A4 and 64-hex names, `--volume` / `--only` need a value, `--only` once; a parse carries `--manual`, the volumes and the file; an `--only` file with a bad line is refused naming the LINE (never its content) before any command; `config set` as root for the target user refuses and writes nothing; the help lists every registered action (derived) | `WslCare.Cli.Tests/ActCommandTests.cs` |
| A1 and A2 over a sandboxed `/proc` (`LinuxSandbox.Memory`, with the captured `auxv`) (E3.S3): A1 previews the page cache and the available share and fires at the plan §4.1 edges (10 % < 15 %; 13 GiB cache with 27.5 % available; not at 32.5 %); runs `sync` then `sysctl -w vm.drop_caches=1` and measures the cache 6 → 1 GiB and `MemAvailable` 10.0 → 22.5 %, `freedBytes` unknown; a failing `sync` never asks for the drop; an unreadable meminfo is unavailable. A2 is URGENT on no order-7 block or a kernel `page allocation failure`, fires after A1, not otherwise; measures the free order-7 blocks 0 → 64. Through the engine: on a BUSY machine the timer defers A1 and runs A2 at once on the event; on an idle one it runs A1 then A2 because A1 ran; in the dry-run week the event is `dryRun`. **Break-its**: the engine's idle gate asked of an urgent preview → `Expected … {"A1:deferred", "A2:ran"}, but {"A1:deferred", "A2:deferred"}` (and the dry-week test: `"deferred"` where `"dryRun"` was due); `RanEarlier` answering false → `{"A1:ran", "A2:ran"}, but {"A1:ran", "A2:skipped"}`; restored, green | `WslCare.Core.Tests/Actions/MemoryActionTests.cs` |
| The engine (E3.S3): an urgent preview skips the idle gate and NOTHING else (a refusal still refuses) while its non-urgent neighbour is deferred, and says so in its reason; an action sees which earlier actions of the same run RAN (`RanEarlier`: a skipped one does not count). **Break-it** (`RanEarlier` false): `Expected seen to be equal to {"A1"} … but found empty collection` | `WslCare.Core.Tests/Actions/ActionEngineTests.cs` |
| A3 over a sandbox whose target user is `me` (E3.S3): the preview lists the target user's `dotnet-build-servers` only (another user's server and a node process left out), the timer fires on a server ≥ `buildServers.idleHours` old; a living `dotnet build` / `test` / `run` REFUSES, for a button too; a build that starts between the preview and the run stops it before any command; the run asks `runuser … dotnet build-server shutdown` and counts exactly the servers gone after, the rest `notRemoved`; no dotnet or no server is a skip; an unreadable table is unavailable; an MSBuild node or VBCSCompiler is a server, never a build. **Break-its**: no refusal → `Expected preview.Refusal "" to contain "a dotnet build is alive"` (three cases); no re-check → the shutdown was asked (`Expected run.Succeeded to be True because nothing failed: nothing was asked, but found False`) | `WslCare.Core.Tests/Actions/BuildServerTests.cs` |
| A15 (E3.S3): weekly from the history's newest A15 that ran (never → fires; 6.9 days ago → not; 7 days → fires); `discard` on `/`, an enabled `fstrim.timer` or an unread timer state keep the timer away; an unread mount table is unavailable; the run records each filesystem fstrim reports (a mount path with a space included) with its bytes and device, exit 64 a success with a note, any other failing exit a failure. **Break-it** (the week ignored): `Expected (TriggerAsync()) to match Not(d.Fired) AndAlso d.Reason.Contains("6 day(s) ago")` | `WslCare.Core.Tests/Actions/FilesystemTrimTests.cs` |
| A16 (E3.S3): one observation above the limit is not a drift, two 4 minutes apart neither, two 5 minutes apart are; a synchronised clock or a live offset within the limit is a skip; the run steps with `hwclock -s`, records `clock-fix.json` and the same drift is NOT corrected again while no full run has seen the clock agree — once one has, a new drift fires again; a correction less than an hour ago refuses a button; with `chronyd` running the step is `chronyc makestep`; an unanswered probe is unavailable and a failed step records nothing. **Break-it** (the event never "already corrected"): `Expected (PreviewAsync()).Decision to match Not(d.Fired) AndAlso d.Reason.Contains("already corrected once")` | `WslCare.Core.Tests/Actions/ClockFixTests.cs` |
| The timer pass of a full run (E3.S3): the timer's `collect` measures, then previews the actions UNDER ITS OWN LOCK (a second take is busy from inside the preview) and records the dry-run week's `dryRun` with what it would free in the ONE history line beside the metrics, the detail's `timerPass`, `running.json` gone after, the week's stamp written; after the week the action RUNS and the line carries its measured result; a terminal's or a button's `collect` never acts; a live run's `running.json` stops the pass (reason recorded, the file left) and the measurement is still recorded. **Break-it** (no pass): `Expected lockHeldDuringPreview to be True …`, `Expected _journal to be equal to {"preview A10", "run A10"}, but found empty collection` | `WslCare.Core.Tests/Collect/TimerPassTests.cs` |
| `logs` / `runs` over a seeded history (E3.S3): today starts at 00:00:00 UTC (yesterday's 23:59:59 is yesterday's), 4 runs: 2 with a cleanup, 2 without, 1 dry (would free 500), timer 2 / button 1 / terminal 1; freed 9 200 and 3 objects; per action in execution order; the most (9 000) and least (200) freeing runs; each metric's max and min with its time (a metric never recorded is absent); each cleanup's objects from an `act` detail and from a full run's `timerPass`, a lost detail still counted; a range and a date cover their UTC days; `--action` narrows; an unparseable line counted; a missing history empty; the period shapes and their refusals. **Break-its**: a run counted a cleanup without removing anything → `… "08:00:cli:none:True:0" …` where `False` was due; the period widened by a day → `Expected logs.Runs.Total to be 1, but found 5`. **Red** (C# doctrine §4a): an E3.S1-era act detail without `notRemoved` / `notes` read them as null — `Expected cleanup.NotRemoved not to be <null>` — normalised where `RunLogs` reads them | `WslCare.Core.Tests/History/RunLogsTests.cs` |
| `logs` / `runs` in-process (E3.S3): the parse (today by default, `--period` / `--action` / `--json` in any order; a missing value, a repeat, an unknown action, a second `--json`, `--action` on `runs`, `runs show` → usage); `logs --json` answers today and writes nothing (no lock file); the text forms; a bad period exit 2 naming the shapes; an unreadable history exit 4 with the reason. **Red** before the rows existed: `VerbRegisterTests` named both usages as missing from the flow catalogue | `WslCare.Cli.Tests/LogsCommandTests.cs` |
| The ACTION property (E3.S3): the generated world also answers the Windows clock probe (ahead / behind / garbage), `timedatectl`, `systemctl show fstrim.timer`, `fstrim -av` (exit 0 / 64 / 1 with a hostile line), a sandboxed `/proc` (meminfo, buddyinfo with 0 / 5 / 100 order-7 blocks, mounts) and a GENERATED process table (build servers, `dotnet build|test|run`, `chronyd`, hostile command lines, sometimes unreadable), with `RanEarlier` random — so every template of A1, A2, A3, A15 and A16 runs in some case (the derived coverage assertion) and none produces a never-command | `WslCare.Core.Tests/Processes/Policy/CommandPolicyPropertyTests.cs` |

| A4 takes an ANONYMOUS volume only — Docker's `com.docker.volume.anonymous` label AND a 64-hex name: a hex name without the label (what `docker volume create` without a name leaves; Docker 23+ keeps it as named) and a dangling volume missing from `system df -v` are never selected, never removed, never recorded as first sightings; the hex-unlabelled one is listed with the kept named volumes; A5 counts such a volume as named and kept; a volume two removed containers share (`--volumes-from`) is one preview item and is counted once in the freed bytes | `WslCare.Core.Tests/Actions/DockerCleanupTests.cs`, `WslCare.Core.Tests/Docker/CleanupPreviewTests.cs` |
| The action property's generated Docker world holds hex-named volumes without the anonymous label and dangling names `system df -v` does not list, mounts them in containers too, and asserts none ever appears in a `docker volume rm` argv (an action asking for one is a violation) | `WslCare.Core.Tests/Processes/Policy/CommandPolicyPropertyTests.cs` (+ `GeneratedWorld.cs`) |
| A failed action's measured deletions count in `logs` (freed, objects, per action with `failed`, runs with a cleanup) and `runs`, its failure printed beside its figures — read from the history line (`ActionRecord.failure`); the totals, counts and extremes of a period open NO run detail; the objects are read only with `--detail` or one `--action`, from at most the newest 50 details (the oldest beyond are listed `notRead`); `--detail` parses, twice or on `runs` it is refused | `WslCare.Core.Tests/History/RunLogsTests.cs`, `WslCare.Cli.Tests/LogsCommandTests.cs` |
| A3 refuses — nothing asked — when the process table cannot be read again just before the command | `WslCare.Core.Tests/Actions/BuildServerTests.cs` |
| A file a caller names (`--only`) is read as a REGULAR file only and never past the cap of bytes actually read: a directory, a FIFO (refused within 10 s, never waited on) and a character device (`/dev/zero`, never streamed) are refused naming the kind; a stream whose length lies is still cut at the cap; `act --only <directory>` / `<fifo>` exit 2 naming "not a regular file" | `WslCare.Core.Tests/Files/RegularFilesTests.cs`, `WslCare.Cli.Tests/ActCommandTests.cs` |
| The pidfd sender's logic over a fake of its native calls and a clock the test moves: a `poll` that fails is `Failed` and no SIGKILL is sent on that guess; three suspects that ignore SIGTERM take ONE grace + one kill wait (≤ 16 s of fake time), each killed; a cancellation during the grace throws and closes every pin, escalating nothing; on Linux, three real children that trap SIGTERM end on SIGKILL after one 2 s grace in under 5 s | `WslCare.Core.Tests/Actions/PidfdSignalsTests.cs`, `WslCare.Core.Tests/Actions/SuspectTerminationTests.cs` |
| A11's preview counts no bytes (the memory is the `heldMemoryBytes` fact), so a dry run of A11 adds nothing to the logs' would-free | `WslCare.Core.Tests/Actions/SuspectTerminationTests.cs` |
| An ambiguous target user leaves the user layer out (not observe-only): a machine-scoped action runs, a user-scoped one is refused naming both accounts | `WslCare.Core.Tests/Actions/TargetHomeTests.cs` |
| An action's runner runs a collector's shared read template it did not declare, and still refuses an undeclared write | `WslCare.Core.Tests/Actions/EnginePartsTests.cs` |
| The line files: a half-written last `history.jsonl` line is ignored, not counted unparseable (a complete bad line still is); the append after a writer that died mid-line starts on a line of its own, so the next record is not swallowed; a half-written container-starts line is ignored | `WslCare.Core.Tests/Records/LineFileTests.cs` |
| `running.json` that never parses is read three more times, 100 ms apart, then `stateUnreadable` (not wedged), naming the file; one torn read (a reader racing the writer's replace) is read again and the dead run it names is swept | `WslCare.Core.Tests/Actions/ActionEngineTests.cs` |
| Every full run sweeps a dead run's `running.json` whatever started it (terminal and timer), recording it `interrupted`; a full run holds its own `running.json` (action `collect`, its pid and run id) while it measures and removes it at the end — and when cancelled mid-measure | `WslCare.Core.Tests/Collect/TimerPassTests.cs` |
| The Windows-layout hygiene test leaves nothing in the temp folder on a Linux run (its layout root is a folder INSIDE the temp root, where a backslash path is one file name) | `WslCare.Core.Tests/Docker/DockerHygieneTests.cs` |

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
- 2026-10-02 (E1.S3): the register check stubbed to find nothing missing turned both companions red
  (*Expected collection to be equal to {"planted-verb-without-a-row <thing>"}, but found empty
  collection*) while the main register check stayed green — the vacuous pass the companions exist for.
  `config get` made to start `docker version` through the product's own `ProcessCommandRunner` turned
  exactly `ConfigFlows.No_config_verb_starts_any_tool` red (*Expected home.Calls to be empty … but found
  at least one item*) — which also proves the CLI's own launcher reaches the fake on the scenario `PATH`.
  Deleting the `config reset` row from this file turned `Every_registered_verb_has_a_row_in_the_flow_catalogue_of_module_tests`
  red naming `config reset <key>`. The CI config smoke, extracted from the workflow and run with the
  first `set` changed to 24, failed with `config get did not answer 25 (after set)`.
- 2026-10-02 (review fixes on PR #4), each red observed against the unfixed code before the fix:
  - **Same-second repair (#3):** the second repair failed with `System.IO.IOException : Cannot create a
    file when that file already exists.` from `UserConfigWriter.MoveAside` — the CLI's internal error.
  - **Fail closed (#4)**, with only the reader seam added: the delete, the directory delete and the move
    went through (`Expected boolean to be True because a path whose real location is unknown is never
    deleted, but found False`), the atomic write landed (`Expected string to be "old", but "new"
    differs`), the root was judged by its spelling (`… to be DeletionRule.Unresolvable {value: 7}, but
    found DeletionRule.OutsideDeclaredRoot`), and over the REAL denied directory the policy allowed the
    delete and `File.Delete` threw `System.UnauthorizedAccessException : Access to the path
    '…\work\locked\a.txt' is denied.`; the positive companion stayed green. After the fix, the real
    reader made to skip its `Attributes` read turned the denied-directory test red with that same
    exception — the line the guarantee rests on is the attribute read, because `LinkTarget` alone
    answers `null` for a component it cannot inspect (measured on Windows 11 and WSL Ubuntu).
  - **Atomic-write revalidation (#0, #2)**, with only the step seam added: both swaps let the write
    follow the link out of the root (`Expected string to be "old" because the write must not follow
    the swapped link out of the declared root, but "new" differs`), the target replaced by a junction
    made the rename throw `System.UnauthorizedAccessException : Access to the path is denied.`, and the
    temporary file was `…\cfg-link\config.json.<guid>.tmp`, not under `cfg-real`.
  - **Control characters (#7):** the scan's first run listed the direct stderr writes it then fixed —
    `Program.cs:51` and `:59`, `Logging/WslCareLogging.cs:69`, `Commands/ConfigCommand.cs:105`; the
    internal-error line split into two with a raw ESC; the console sink wrote a raw ESC from a logged
    value; the scenario's key read from the user file split the log line (`Unexplained … {"forged" —
    "wsl-care config get" lists the keys this build knows"}`). The nine typed-key refusals were GREEN
    first — `Output.Refuse` already applied `CommandLine.Printable` at `141faa4` — and went red (two
    lines, or a raw ESC) together with the internal-error and scenario cases when `Printable` was taken
    out of `Output`; green on restore.
  - **Parser (#6)**, no behaviour change: the new longest-spelling test went red when the length
    ordering was removed from `CommandLine.LongestFirst` (`Expected object to refer to … Command`), and
    the 16 existing parser tests stayed green throughout.
- 2026-10-02 (E2.S1). The collectors' first draft was written before their tests, so every group's
  teeth were proved the other way round: the production line the guarantee rests on was broken on
  purpose, the named tests watched going red for the behavioural reason, the line restored, green again.
  One observation per group:
  - **Parsers:** `MemInfo.Bytes` answering unavailable for every key turned
    `Meminfo_sizes_are_kibibytes_turned_into_bytes…` and the probe-over-the-fixture test red (*Expected
    object to be …Reading`1+Available…*).
  - **Inconsistent sample:** `Attribution.Compute` mapping every difference to `Remainder` turned
    `A_negative_remainder_is_an_inconsistent_sample_and_never_a_negative_number` red (*Expected result to
    be …InconsistentSample*).
  - **Counted once:** dropping the cgroup exclusion from the process sum turned
    `A_container_process_visible_in_the_distro_is_counted_once_through_its_cgroup` red (*Expected
    processes.ProcessCount to be 1 because the member of the counted container is not a distro process as
    well, but found 2*).
  - **Ranking / walkers / families:** the top list unsorted turned `The_top_30_are_ranked…` red (*…to be in
    descending order, but found {3600384L, 126976L, …}*); the argv half of the `/mnt/` check removed missed
    pid 5252 (*…to contain 5252 because an argument is /mnt/c/.../creds-mcp.exe*); `vscode-server` placed
    before `ai-agents` classified the Claude Code binary wrongly.
  - **No slow process:** `status` made to run `docker stats --no-stream` through the host's runner turned
    both in-process tests red (*Expected runner.Requests to be empty … but found at least one item*) and
    three `StatusFlows` red on the fakes' argv log (*Expected home.Calls to be empty*).
  - **Budget:** a 2.5 s sleep in `status` turned the side flow red (*Expected time to be less than 2s,
    but found 2s, 803ms*).
  - **Slow parts with age:** the history reader stubbed to "no run yet" turned four `LastFullRunTests` red.
  - **Never 0:** the context's `WhenWritingNull` removed turned both JSON-shape tests red (*Expected
    vmmem.TryGetProperty("bytes", out _) to be False, but found True*).
  - **Verb register:** `status [--json]` registered before its catalogue row turned both register checks
    red (*missing: status [--json]*).
  - **Break-it on the attribution — total RSS instead of `RssAnon` + `RssShmem`** (`ProcStatus` reading
    `VmRSS` into the anon slot): exactly five Core tests and two CLI tests went red —
    `A_status_file_yields_name_parent_owner_and_rss_anon_plus_rss_shmem` (*1345765376L, but found
    1456066560L*), `Process_memory_is_rss_anon_plus_rss_shmem_not_vm_rss` (*665600L … but found 5836800L*),
    `The_top_30_are_ranked_by_rss_anon_plus_rss_shmem_largest_first`, the probe-over-the-fixture test and
    `The_captured_tree_attributes_a_positive_remainder…` — the captured 2026-10-02 sample itself turning
    into an *inconsistent sample* (overshoot 1 728 458 752 bytes), which is exactly the double count §15b #4
    was written against — plus `StatusCommandTests`' JSON and text tests. Green on restore.

- 2026-10-02 (E2.S2). The collectors were written before their tests, so — as in E2.S1 — the teeth were proved
  by breaking the line each guarantee rests on, watching the named tests go red for the behavioural reason, and
  restoring it. One observation per group:
  - **Units:** `MiB` read as 10⁶ turned `Megabytes_and_mebibytes_are_not_the_same_figure` red (*Expected … to be
    104857600L, but found 100000000L*) and the `53.84MiB` case.
  - **Classification:** the `permission denied` phrase removed turned the real permission-denied stderr into
    `CommandFailed` (*Expected the enum to be DockerFailure.SocketRefused {value: 2}, but found
    DockerFailure.CommandFailed {value: 4}*). Swapping the phrase groups' ORDER turned nothing red — no real
    message carries phrases of two kinds — so the comment claiming the order mattered was corrected.
  - **No daemon, nothing else:** the early return removed turned all four `When_no_daemon_answers…` cases red
    (*Expected runner.Requests to contain a single item because only the version probe runs*).
  - **First sightings:** keeping vanished names turned the observe test red; the `read-only` catch removed made
    the real denied state directory throw `System.UnauthorizedAccessException : Access to the path
    '…\volume-seen.json.<guid>.tmp' is denied.` — the OS refusing the temporary file is what "unprivileged" is.
  - **Protections:** dropping the inspected-image-id check listed the image of a stopped container (*Expected
    value to be 10 because the image of a stopped container is not unused, but found 11*); dropping the keep
    label selected the labelled volume (*Expected value to be 2, but found 3*).
  - **Read verbs only:** `volume ls` changed to `volume prune` in `DockerCommands` turned the scenario red on the
    fakes' argv log (*Expected home.Calls to contain only items matching ((c.Tool == "docker") AndAlso
    IsReadVerb(c.Argv))*) and both Core read-verb tests — the fake only replays, it never prunes.
  - **Never 0:** an unavailable row written with `count: 0` turned the JSON-contract test red (*Expected
    a4.TryGetProperty("count", out _) to be False because an unread figure is absent, never 0*).
  - **Verb register:** `preview --all [--json]` registered before its catalogue row turned both register checks
    red (*missing: preview --all [--json]*).
  - **Vanished container (a defect the live contract found, test FIRST):** with the fix reverted the new
    `An_inspect_that_names_only_containers_removed…` failed with *Expected answer to be …Answered … Reason = "docker
    container inspect … failed: Error response from daemon: No such container: bd82df97…"*; green with the fix.
  - **A budget measured beside the new flows:** on Linux `StatusFlows`' 2 s budget read 2.64 s once `PreviewFlows`
    (a 10 s hang flow among them) ran in parallel — a wall-clock measurement of the suite, not of the verb. The
    timed flows now run in the `WallClock` collection, alone; green on both families afterwards.
  - **Found by the live contract, not by any fake:** `$m.Name` in the inspect template failed the whole command on
    this machine's bind mounts (*template parsing error: … at <$m.Name>: map has no entry for key "Name"*), and an
    empty events window failed an `OnlyContain` assertion — both fixed before any fixture was recorded.
- 2026-10-02 (E2.S3). **Test first, red against stubbed bodies** (`Coverage.Plan` returning no gap and no start,
  `Last24h` returning complete with 0, `NextBackoff` returning 5 s): 13 of the 17 follower tests red for the behaviour
  — *Expected waits … to be equal to {5.0, 10.0, 20.0, …}, but {5.0, 5.0, 5.0, …} differs at index 1*; *Expected value
  to be 3, but found 0*; *Expected plan.Starts … to be equal to {"new"}, but found empty collection*; *Expected boolean
  to be False, but found True* (partial counts); *Expected Store.ReadAll()…OfType<CoverageLine.Start>() to contain a
  single item, but the collection is empty*. Implementing the rules left ONE red that was a real defect of the follower,
  not of the rules: *Expected lines[^1] to represent … 12:01:00 because the stop marker carries how far coverage reached,
  but 12:00:35 does not* — a SIGTERM inside a stream segment dropped the coverage that segment had reached; coverage now
  lives in a field every recorded start moves. **Break-it, for the groups written code first** (each production line
  changed alone, the named test observed red, the line restored, the suite green again):
  - **Write order:** the detail written after the history line → *Expected value to be greater than 5 because the history
    line is written only after its detail is on disk, but found 3*.
  - **Read-only:** the write probe's verdict ignored → *Expected the enum to be Recording.ReadOnly, but found
    Recording.Busy* (the run went on to lock a directory it could not create; `TryLockExclusive` now creates the
    parent first, so a missing directory is never mistaken for a busy lock).
  - **Failed write:** the outcome kept `completed` → *Expected … RunOutcome.Failed because never a silent success, but
    found RunOutcome.Completed*.
  - **Reconcile:** the `interrupted` line not appended → *Expected lines to contain 2 item(s), but found 1*.
  - **Retention:** a detail removed by its folder's age alone → *Expected DetailExists(relative) to be True, but found
    False*.
  - **Thresholds:** `< act` made `<= act` → the 15.0 % edge *Expected … Level.Warn, but found Level.Critical*; the 5-minute
    rule removed → the 299 s case *Expected … Level.Ok, but found Level.Warn*; the ceiling moved to 95 % → *Expected the
    enum to be Level.Critical, but found Level.Ok*.
  - **Doctor:** `healthy` hard-wired → *Expected boolean to be False, but found True*.
  - **Journal search:** the exit-1-is-zero arm removed → the no-match case unavailable (*… exited 1, nothing on
    stderr*).
  - **Links:** `AttributesToSkip = ReparsePoint` removed → *Expected … Bytes to be 10L because the 10 000 bytes behind the
    link are another tree's, but found 10010L* (over a junction on this machine).
  - **Clock:** the offset taken from the printed instant instead of the process start → *Expected … OffsetSeconds to
    approximate 0.1867 +/- 0.0001 …, but 0.9375776 differed by 0.7508776* — the launch latency counted as skew.
  - **Found while writing the tests, not by any fake:** the docker read-verb enumeration crashed on the new
    `EventStream(since, until, slack)` signature (*Object of type List`1 cannot be converted to type TimeSpan*) — its
    sample arguments now cover a `TimeSpan`; FluentAssertions 7's `OnlyContain` FAILS on an empty collection, so the live
    checks of a window with no start (the ordinary case here) assert `NotContain` of a bad item instead.

- 2026-10-02 (E3.S1). The engine and the policy were written before their tests, so — as in E2.S1/E2.S2 — the teeth were
  proved by BREAKING each production line a group rests on, alone, watching the named test go red for the behaviour,
  restoring it (written afresh, so its timestamp is new) and seeing the suite green again:
  - **The property test, against a deliberately permissive policy** (`CommandPolicy.Review` returning `Allowed` first):
    *Expected run.Violations to be empty, but found at least one item {"case 0: ALLOWED a never-command: git worktree
    prune"}*. The kept companions are red by design on every run: the permissive policy, the outer-argv-only policy (its
    first run found what the test had not predicted — *ALLOWED a never-command: runuser -u me -- …/v22.11.0/bin/dash -c x*,
    a never-command behind runuser's one legal shape — and the test was rewritten to assert exactly that, the refuted
    approach kept as a test), the planted `vm.drop_caches=<0..3>` template, the planted shell-string action.
  - **The wrapped command judged twice:** the second never-list check in `ReviewWrapped` disabled → *Expected type to be
    WslCare.Core.Processes.CommandVerdict+Refused, but found WslCare.Core.Processes.CommandVerdict+AllowedVerdict*.
  - **Order:** `InExecutionOrder` returning the request's order → *Expected Statuses(result) to be equal to {"A5:ran",
    "A4:ran", "A10:ran"}, but {"A10:ran", "A5:ran", "A4:ran"} differs at index 0*.
  - **A failing action continues:** the unit's catch narrowed to `TimeoutException` → the test failed with
    *System.InvalidOperationException : docker answered nonsense* escaping the run.
  - **Dry-run window:** the 7 days made 0 → *Expected Statuses(first) to be equal to {"A10:dryRun"}, but {"A10:ran"}*.
  - **Idle gate:** `Applies` ignoring `IdleRule.Always` → *Expected Statuses(busyCpu) to be equal to {"A7:deferred"},
    but {"A7:ran"}*.
  - **Wedged:** the 30 s staleness never reached → *Expected type to be …ActResult+Wedged, but found …ActResult+Busy*.
  - **Dead / mismatched:** a gone pid read as live → *Expected type to be …ActResult+Done, but found …ActResult+Busy*; the
    2 s start tolerance made infinite → the same for a reused pid.
  - **Freed bytes measured:** the freed figure taken from the preview → *Expected run.FreedBytes to be 3000L because the
    size, read before, of the one file that is gone - not the preview's 5000, but found 5000L*.
  - **One lock:** `collect` back on E2.S3's `{state}/run.lock` → *Expected collect!.Recording to be Recording.Busy
    because collect and act take the SAME lock; the second one refuses, but found Recording.Recorded*.
  - **Root first:** the root check bypassed → *Expected exit to be 77, but found 0* for both `--preview` and `--confirm`.
  - **Heartbeat:** the loop's period made an hour → after its 10 s wait *Expected beaten to represent the same point in
    time as <2026-10-02 12:00:10 +0h> … but <2026-10-02 12:00:00 +0h> does not*.
  - **Target user:** an ambiguous set taken as its first account → the two ambiguous rows *Expected type to be
    …TargetUserResult+Ambiguous, but found …TargetUserResult+Found*.
  - **Verb register:** `act` registered before its catalogue row turned both register checks red (*missing: act
    <A#>[,<A#>...] (--preview or --confirm) [--json]*).

- 2026-10-03 (E3.S2). The actions were written before their tests (the session was cut by a spend limit between the two),
  so — as in E3.S1 — every group's teeth were proved by BREAKING the production line it rests on, alone, rebuilding,
  watching the named test go red for the behaviour, and restoring the file by writing it (a fresh timestamp):
  - **A4 freed from the confirmed only:** freed summed over every target → *Expected run.FreedBytes to be 427600000L because
    a volume docker volume rm did not confirm counts nothing (plan 15c #1), but found 641400000L*.
  - **Keep label:** the label exclusion removed from `CleanupTargets.AnonymousVolumes` → *Expected run.Count to be 2, but found 3*.
  - **Unreadable Docker version:** the `ServerMajor: 0` arm removed → *Expected a4.Reason "Docker nightly is below 23: …" to
    contain "could not be read as a number"* (and before E3.S2 the row said nothing at all for such a version).
  - **A button needs the shown list:** the check moved to the timer → the bare `manual` run's reason differed (red).
  - **A container that started since:** the alias (the daemon quotes the container's NAME) removed → *Expected run.Succeeded
    to be True because container-02: Docker neither printed its name nor said why, but found False*.
  - **No prune without a selection:** the guard removed → *Expected world.Calls("image", "prune") to be empty …, but found
    at least one item*.
  - **A7's detected flag:** `--max-used-space` forced → *… to be equal to {…, "--keep-storage", "20GB"}, but {…,
    "--max-used-space", "20GB"} differs at index 4*.
  - **A tool missing = skip:** A8's skip removed → *Expected preview.Skip "" to contain "npm is not installed for me"*; the
    engine's skip gate removed → *Expected Statuses(result) to be equal to {"A8:skipped"}, but {"A8:ran"}*.
  - **A11's CPU window:** the tick comparison removed → *… {"10 p10"}, but {"10 p10", "20 p20"} contains 1 item(s) too
    many*; the re-check before the signal reduced to the owner → the `cpu` and `tty` cases red (*Expected signals.Asked to be
    empty, but found at least one item*).
  - **A9's names:** the name / numeric-revision check removed → *Expected run.Succeeded to be True because snap remove evil
    --revision=x1 was refused: snap-remove-revision: revision must be --revision=<1..999999999>* — the policy is the second
    filter, the action's own check the first.
  - **A12 without links:** the refusal returned empty → *Expected preview.Targets to be empty, but found at least one item*.
  - **A14 keeps the newest two:** `Skip(Keep)` made `Skip(0)` → the two newest builds listed (red).
  - **The target user's home:** `Resolve` keeping root's paths → *Expected paths.Home to be the same string, but they differ*.
  - **CLI:** `--manual` ignored → *Expected … Trigger to be RunTrigger.Manual {value: 1}, but found RunTrigger.Cli {value: 2}*;
    the root refusal of `config set` removed → *Expected exit to be 2, but found 0*.
  - **The action property, against a planted bad action:** A4's slot widened to accept plain text AND the shown names put
    straight into the argv → *Expected violations to be empty, but found at least one item {"case 1: A4 asked for a command its
    executor refused: docker volume rm … (?? -EncodedCommand) …"}* — the generated shown list is what reaches it. A planted
    WIDE SLOT alone (no action producing a hostile value) stayed green: the action property sees only what an action asks
    for; the template property is the one for slots, and plain text cannot spell a never-command.
  - **The signal seam scan:** a `"kill"` literal planted in `CacheFolders.cs` → *… found at least one item
    {"…CacheFolders.cs:24: \"kill\""}*.
  - **The verb register:** the new `act` usage registered before its catalogue rows changed → both register checks red
    (*missing: act <A#>[,<A#>...] (--preview or --confirm) [--manual or --timer] [--volume <name>]... [--only <file>] [--json]*).
  - **Found, not planted — a test that expired:** `LoggingTests.Starting_the_logger_writes_one_file_per_run…` went red on
    2026-10-03 with no change of ours (*Expected File.ReadAllText(files[0]) "" to contain "hello from the test"*): the host's
    clock was FIXED at 2026-10-02 12:00 while Serilog stamps events with the real clock, so the run-file sink rolled to the
    next day's segment. The test now starts its run at the real now (red before the change, green after).

- 2026-10-03 (E3 review fixes: the gate's code round, session `a90e342d`, and an independent review). Every fix began with
  its test, run against the UNFIXED code and seen failing for the real symptom:
  - **A4 and the anonymous label (review 1):** the action → *Expected preview.Targets.Select(t => t.Key) to be equal to
    {"6443…", "9548…"} because only volumes carrying Docker's anonymous label AND a 64-hex name are A4's, but {"6300…",
    "6443…", "9548…", "aaaa…"} contains 2 item(s) too many*; the row → *Expected a4.Count to be 2 because a 64-hex name alone
    is not anonymous: Docker's label decides, as volume prune does, but found 4*; A5 → *Expected after.Targets…(anonymous
    volume names) {"d81b…", …} to not contain "d81b…" because docker rm -v keeps a volume Docker does not treat as
    anonymous*; the property → *Expected violations to be empty, but found at least one item {"case 6: A4 removed 44a2…, a
    volume Docker treats as NAMED (no anonymous label, or labels unknown)"}*.
  - **A failed action's deletions (review 2):** *Expected logs.FreedBytes to be 59000000000L because what a failed action
    measurably removed was removed, but found 0L*.
  - **A shared volume (review 3):** *Expected preview.Targets.Count(t => t.Name == volume) to be 1 because a volume shared by
    two selected containers is one object, but found 2*.
  - **A3's re-check (review 4):** *Expected _world.Runner.Requests to be empty because a build that cannot be ruled out is a
    build that may be alive: nothing is asked, but found at least one item*.
  - **`--only` (review 5):** Windows, a directory → *Expected stderr "wsl-care: act: the --only file …/tmp/shown-folder is
    missing, unreadable, or larger than 1048576 bytes; nothing was done" to contain "not a regular file"*; WSL, a FIFO →
    *Expected finished to be True because a FIFO is refused, never waited on, but found False* (the root read hung 10 s).
  - **A poll error (review 6):** *Expected type to be …SignalOutcome+Failed, but found …SignalOutcome+Ended* — the pidfd
    sender got a native-call seam (`IPidfdCalls`) and a clock first, unchanged in behaviour, to make it observable.
  - **Ambiguous target (gate #2):** *Expected loaded.IsObserveOnly to be False because without a single target user the user
    layer is left out, not made an error that stops every action, but found True*.
  - **Shared reads (gate #3):** *Expected type not to be …CommandOutcome+Refused because docker version is a shared
    collector read, but it is*.
  - **A11's bytes (gate #4):** *Expected logs.Runs.WouldFreeBytes to be 0L because a dry run of A11 would free memory, and
    the logs' would-free total is disk, but found 1000000L*.
  - **Line files (gate #6):** *Expected read.Unparseable to be 0 because a line still being written is not a corrupt line, but
    found 1*; the append after torn remains → *Expected read.Records to contain a single item, but the collection is empty*
    (the new record had been glued onto the torn line and lost — found while writing the test, fixed with it); the
    container-starts reader → *Expected store.ReadAll() to contain a single item …, but found* two.
  - **`running.json` unreadable (gate #7):** *Expected type to be …ActResult+StateUnreadable, but found …ActResult+Wedged*
    (both contents); one torn read → *Expected type to be …ActResult+Done, but found …ActResult+Wedged*.
  - **`collect` sweeps (gate #8):** from a terminal → *Expected File.Exists(RunningState.File(_sandbox.Paths)) to be False
    because a dead run's running.json is swept by any full run, but found True*.
  - **One grace (gate #9):** *Expected (_clock.GetUtcNow() - started) to be less than or equal to 16s because SIGTERM goes to
    all, ONE grace is waited across all of them, then the survivors get SIGKILL - never one grace each, but found 45s*.
  - **Totals without details (gate #10):** *Expected logs.Cleanups to contain only items matching (c.Removed.Count == 0)
    because the objects come from the details, which were not asked for, but* every cleanup carried its objects.
  - **`collect`'s own running state (gate #11):** *Expected seen not to be <null> because running.json exists while the full
    run measures*.
  - **The hygiene test's leak:** on WSL → *Expected Directory.GetFileSystemEntries(…) to be empty because the test leaves
    nothing behind in the temp folder, but found at least one item {"/tmp/wsl-care-test-hygiene-windows-a994…\Users\me\.docker\daemon.json"}*.
  - **The complexity refactor (review 7) changed no test:** every method written in E3 measured at cyclomatic complexity
    ≤ 4 (decision points: `if`, loops, `catch`, `case` labels, `?:`, `&&`, `||`, `??`, `?.`, `when`; a switch
    EXPRESSION's arms and a pattern's `or` alternatives count as one test, as doctrine §6 names the switch expression as
    the remedy) — 87 methods over 4 before, 0 after — and the whole suite stayed green on Windows and in WSL, unchanged.
    The one visible difference is the run log: an `act`'s failed / deferred / refused line now carries its count and
    freed bytes too, as the timer pass's always did (one `OutcomeLog` for both).
  - **Tests that enshrined the old behaviour were changed with the fix, not weakened:** the engine's "cannot be told"
    theory now expects `StateUnreadable` (and the three retries), the ambiguous-target test the run that still acts, and the
    three detail-reading `logs` tests ask for `detail: true`. The A5 fixture's keep-labelled volume carries the anonymous
    label too, as Docker writes it — a fixture with the keep label alone is now a NAMED volume.

## The installer harness (`InstallWorld`, E4.S1)

`install.sh` is a shell script, but its harness is C# in `WslCare.Scenarios` like every other flow (scenario rule point 2;
no new dependency — bats was not taken). `InstallWorld` runs the REAL script with the real `/bin/sh` (dash on Ubuntu) and
`InstallFlows` asserts what it did:

| Part | What it is |
|---|---|
| `root/` | `WSL_CARE_INSTALL_ROOT`, the stand-in for `/`: every path the script reads or writes as a file sits under it. Seeded with `/run/systemd/system` (systemd booted), an `/etc/passwd` with `alice` and `zed`, and `/etc/default/sysstat` with `ENABLED="true"` |
| `fakebin/` | the fake tool (`WslCare.FakeTool`) as `curl`, `gh`, `systemctl`, `apt-get`, `debconf-set-selections`, `dpkg-reconfigure`, `runuser`, `sudo`, `id`, `uname`, `sar`, `atop` — everything that changes the machine, reaches the network, or answers who and where the script runs (so a test can be root, or arm64, without being either). A test leaves one out to stand for a tool that is not installed |
| `realbin/` | links to an ALLOWLIST of real text and file tools (`awk`, `cat`, `chmod`, `cut`, `grep`, `gzip`, `head`, `install`, `ln`, `ls`, `mkdir`, `mktemp`, `mv`, `od`, `readlink`, `rm`, `rmdir`, `sed`, `sha256sum`, `sleep`, `sort`, `tar`, `timeout`, `tr`, `wc`). `PATH` is exactly `fakebin:realbin`, so a script change that reaches for another tool fails here first. A world may link `awk` to a named one (`/usr/bin/mawk`, `/usr/bin/gawk`) — the snappy decoder is run under both |
| the attestations | since the E4 review: every published release carries one attestation per signer — a bundle in Sigstore's shape (`AttestationBundles`: a self-signed certificate with the SAN and the Fulcio extensions, a DSSE statement naming the archive's digest), snappy-compressed (literals only), served at a bundle URL that the fake attestation API names (`bundle: null`, `&` written `\u0026`). The fake `gh` VERIFIES (`VerifiesAttestation`, `FakeAttestation`): `--cert-identity` exact, `--signer-workflow` a literal prefix, `--repo`, `--source-ref`, `--deny-self-hosted-runners`, the artifact's digest — gh's semantics as measured on gh 2.97.0. Every fake call records `HOME`, `GH_CONFIG_DIR`, `XDG_*_HOME`, `GH_TOKEN`, `GITHUB_TOKEN` (`WSL_CARE_FAKE_RECORD_ENV`), so whose configuration gh ran under is asserted. A captured cli/cli bundle (`src_daemon/tests/fixtures/attestation/`, 64 literal + 69 copy elements, bytes above 127) is served where the decoder's copies must be right |
| `tmp/` | the script's `TMPDIR`; every flow asserts it is EMPTY afterwards (the trap removed the temporary folder on success, on failure and on refusal) |
| the release | built per test with `System.Formats.Tar` from THIS repository's units and machine layer and served by the fake `curl` (a new answer option, `OutputFlag`: the fixture goes to the file after `--output`); its `.sha256` computed, or replaced by a test |
| the binary | a two-line shell stub that appends the path it was started as to `stub-invocations.log` and hands its argv to a fake `wsl-care` — so the installer's use of the ABSOLUTE path is observed, and `collect` / `doctor --json` are scripted; `doctor --json` is serialised by the product's own `WslCareJsonContext` from a `DoctorReport`, never typed by hand |

It runs as the test user, never root: a path that escaped the prefix would be refused by the operating system before it
changed `/etc` or `/opt`. Linux only (POSIX sh, GNU coreutils and tar): the two Linux CI legs run it; the Windows leg
skips each flow with that reason. On the owner's machine it ran in WSL `Ubuntu` from a copy of the worktree under
`/tmp`, built there (`dotnet build wsl_care.slnx -c Release -m:4`, then the Scenarios executable with
`--filter-class "*InstallFlows"`).

**Red, per guarantee** (2026-10-03, in WSL `Ubuntu`). The flows passed at their first run, which proves nothing by
itself (testing rule), so each guarantee was proved to have teeth by deleting the line its behaviour rests on in a copy
of `install.sh` (a scratch script applied one `sed` per mutation, checked the file really changed, ran the one test,
restored the file and compared it byte for byte) — 28 mutations, every one red for its own symptom:

| Guarantee | The line removed | The red |
|---|---|---|
| a checksum mismatch aborts before anything is installed | the `expected = actual` comparison | `Expected result.Exit to be 1 because the install should fail … but found 0` |
| a release without its `.sha256` is refused | `fail download` on the `.sha256` fetch | the run went on to `sed: can't read …/wsl-care-0.1.0-linux-x64.tar.gz.sha256`, not step `download` |
| a refused attestation aborts | `fail attestation` | `… to be 1 … but found 0` |
| no `gh` stops before the download, with guidance | the `have gh` preflight | stderr was `timeout: failed to run command 'gh': No such file or directory` instead of the guidance |
| `--skip-attestation` says so loudly | the banner line | stderr held only the frame of `=` lines |
| an existing machine layer is kept | the exists-check | `… to be the same string because plan §15e #1: an existing machine layer is never overwritten, but they differ at index 0` |
| an inactive unit fails naming the step | `systemctl is-active … \|\| fail` | `… to be 1 … but found 0` |
| an unhealthy doctor fails naming the step | the `healthy` test | `… to be 1 … but found 0` |
| uninstall keeps history, logs, machine layer | `--purge`'s condition (always purge) | `DirectoryNotFoundException: … root/var/lib/wsl-care/history.jsonl` |
| `--purge` removes exactly those | the `rm -rf` of the three folders | `Expected Directory.Exists(world.At(gone)) to be False because --purge removes /var/lib/wsl-care, but found True` |
| `--dry-run` changes nothing | `return 0` in `run` | the prefix tree gained `opt/wsl-care/bin/wsl-care`, the units, the link, … (`Expected world.Tree() to be a collection with 7 item(s)`) |
| an existing `[user] default=` is never rewritten | the keep decision | `… to be 0 because the install should succeed … but found 1` (the preflight refused over the `[user]` section) |
| wsl.conf is written only with the flag | the `advise` branch (made to write) | `… to be the same string because never written without --set-default-user, but they differ on line 3` |
| an unknown user refuses before anything | the `user_exists` check | `… to be 1 … but found 0` |
| installer and daemon read the same default user | the `tolower` of the key in the awk reader | `Expected string to be "zed" … because both read the same default user from [User]\n  Default = "zed"  \n, but "" has a length of 0` |
| a non-root run is refused, no sudo | the root check | `… to be 1 … but found 0` |
| no systemd → refused with the setting named | the `/run/systemd/system` check | `… to be 1 … but found 0` |
| an escaping archive member is refused | the member-name `case` | stderr was GNU tar's `Removing leading 'wsl-care-0.1.0-linux-x64/../'`, not the member check's reason |
| a link member is refused | the `tar -tvzf` type check | `… to be 1 … but found 0` (installed WITH the link member) |
| a malformed `--version` is refused (exit 2) | the version pattern check | `Expected value to be 2 because "0.1.0/../../x" is not a version, but found 1` |
| the newest DAEMON release, never the extension's | `daemon-v` in the tag pattern | `… to be 0 … but found 1` (it downloaded `extension-v0.2.0`'s name) |
| under sudo, gh runs as `SUDO_USER` | the `SUDO_USER` branch of the verifier | `Expected world.CallsOf("runuser") to contain a single item … but the collection is empty` |
| an upgrade restarts the follower | the `UPGRADE` branch | `… to contain items {"daemon-reload", "try-restart wsl-care-events.service", …} in order, but "try-restart …" (index 1) did not appear` |
| a foreign `/usr/local/bin/wsl-care` is refused | the link-ownership check | stderr was `ln: failed to create symbolic link …: File exists`, not the preflight's reason |
| missing sysstat / atop are installed with apt | `have sar \|\| need=sysstat` | `… apt-get … to be equal to {"update -q", "install … sysstat atop"}, but {…, "install … atop"} differs at index 1` |
| sysstat left switched off fails | the re-check after `dpkg-reconfigure` | `… to be 1 … but found 0` |
| the link names the ABSOLUTE path | the link target (made prefixed) | `Expected string to be the same string because the link names the ABSOLUTE install path (plan §15e #3), but they differ at index 1` |
| the installed binary records one full run | the `first_run` call | `… to be equal to {"collect", "doctor --json"} … but {"doctor --json"} contains 1 item(s) less` |

**The E4 review, part A — the install trust boundary** (2026-10-03, three independent reviews standing in for the coai
gate; in WSL `Ubuntu`, the worktree copied under `/tmp` and built there). Every behaviour was first written as a test and
run against the E4.S1/E4.S2 `install.sh`, where each went red for the real symptom:

| Guarantee | Red against the old installer |
|---|---|
| A1 an attestation of `release.yml` built from a branch is refused | `Expected result.Exit to be 1 because the install should fail … attestation ok: built by oleksandrdubyna88/wsl_care/.github/workflows/release.yml (authenticity)` — `release.yml@refs/heads/x` installed |
| A1 an attestation of another release's tag is refused | the same: `daemon-v0.0.9`'s identity accepted for 0.1.0's archive |
| A1 a self-hosted runner's attestation is refused | the same: `attestation ok` for a `self-hosted` runner environment |
| A1 the pinned identity, statically | `Expected string "#!/bin/sh …" to contain "SIGNER_IDENTITY=\"https://github.com/$SIGNER_WORKFLOW@refs/tags/daemon-v$VERSION\""` |
| A2 gh 2.45.0 (no `gh attestation`) refused before any download | `Expected result.Exit to be 1 … release daemon-v0.1.0 … attestation ok …` — downloaded and installed |
| A2 gh 2.49.0 (below the floor) refused before any download | the same, installed |
| A2 the guidance names GitHub's apt repository | `Expected result.Stderr "… on Ubuntu: sudo apt-get install gh), log in (gh auth login) …" to contain "https://cli.github.com/packages"` |
| A3 under sudo root verifies itself, no runuser | `fake runuser: no scripted answer for: runuser -u alice -- gh attestation verify --repo … --signer-workflow …` (exit 1) |
| A3 a real bundle is decompressed exactly (×3 awks) | `… the bundle …/attestations/none.json is not a Sigstore bundle …" to contain "https://github.com/cli/cli/.github/workflows/deployment.yml@refs/heads/trunk"` — the old installer never fetched a bundle |
| A3 each of several bundles is verified alone | `Expected Verifications(world) to contain 2 item(s) … but found 1` |

Then, with the fix in, each new line was deleted or bent in a `/tmp` copy of `install.sh` (one `sed`, the change checked,
the one test run, the file restored and compared by SHA-256 — byte-identical every time); 10 mutations, every one red:

| The line bent | The red |
|---|---|
| `--deny-self-hosted-runners` removed | the self-hosted flow: `Expected result.Exit to be 1 because the install should fail` |
| `--cert-identity "$SIGNER_IDENTITY"` → `--signer-workflow "$SIGNER_WORKFLOW"` | the branch flow: the same (the fake gh's measured prefix match admits `refs/heads/x`) |
| the identity's `@refs/tags/daemon-v$VERSION` removed | `Expected … to be "https://github.com/oleksandrdubyna88/wsl_care/.github/workflows/release.yml@refs/tags/daemon-v0.1.0" with a length of 99` |
| the version floor made `true` | the gh 2.49.0 flow: `Expected result.Exit to be 1` |
| the `--help` question replaced by a constant naming every flag | the help-fails / missing-flag flow: `Expected result.Exit to be 1` (a first attempt pointed this mutation at the 2.45.0 flow, which the version check refuses first — it stayed green, so the flow `A_gh_whose_attestation_verify_fails_or_lacks_a_flag_…` was added) |
| the decoder's one-byte-offset copy reading only the low byte | the captured bundle: `… is not a Sigstore bundle: ',' is invalid after a property name` |
| gh's token variables no longer unset | `Expected verify.Environment … not to contain key "GH_TOKEN"` |
| gh's `HOME` no longer isolated | `… because gh's HOME is inside the installer's temporary folder, nobody's home, but "/home/user" is too short` |
| only the first bundle tried | `Expected result.Exit to be 0 because the install should succeed` |

`MemoryMax=1G` (A4) is a configuration decision, not a behaviour: `ShippedFilesTests.The_service_memory_ceiling_leaves_room_for_the_tools_the_cleanups_start`
holds the value; no red was needed for it.

**The shipped files** (`ShippedFilesTests`, every OS): each unit's `ExecStart` starts `/opt/wsl-care/bin/wsl-care` and
its argv is parsed by the CLI's own `CommandLine.Parse` — the timer's service to `Request.Collect { Timer: true }`
(§15d CI), the follower's to `Request.EventsFollow(Once: false)`; `SuccessExitStatus` equals `ExitCode.Busy`; the timer
is `OnCalendar` every 4 h with `Persistent=true`; no unit sets a sandbox directive that breaks a named action (its
companion: the same scan finds `NoNewPrivileges`); `install.sh`'s `UNITS` line equals the folder's files; the machine
layer loads VALID through the real `ConfigLoader` and sets nothing, and the example in its own comment loads valid as
the machine layer (the one positive beside the negatives). `systemd-analyze verify` (CI, Linux legs) reads the units with
systemd's parser and fails on any output — it exits 0 on an unknown key, observed in `ubuntu:24.04` (systemd 255) with
a planted `Persistant=`.

## The release pipeline's tests (E4.S2)

A release runs rarely and expensively, so every part of it that can run on a pull request does (testing rule, "a check
that only runs during a release has never run"). All of it is C# in `WslCare.Scenarios`, no new package:

| Class | What it holds | Where |
|---|---|---|
| `PackageFlows` | `.github/scripts/package-daemon.sh` run under bash against a stub binary; the archive read back with `System.Formats.Tar` (an oracle independent of GNU tar): exactly the members `install.sh`'s unpack loop names — read from `install.sh` itself, `ReleaseFiles.InstallerRequiredMembers` — plus their folders, entry types, owner, modes, bytes, and the `.sha256` line; then the archive served to the real `install.sh` by `InstallWorld.PublishFiles` (new), which must install it. The Windows zip is read with `ZipFile` | Linux legs; the zip case where `7z` is on `PATH` (the GitHub Ubuntu image has it) |
| `ReleaseScriptFlows` | `release-guard.sh` and `verify-release-assets.sh` by EXIT CODE (a check that prints and exits 0 stops nothing in a workflow); the guard's off-`main` case in a throwaway git repository whose commit identity comes from `GIT_AUTHOR_*` / `GIT_COMMITTER_*` in the child's environment (no git configuration is written); archive names from the asset contract itself (`daemon_archive_name`, run under bash), never retyped | Linux legs |
| `ReleaseWorkflowTests` | the workflows' structure, read with `WorkflowYaml` — a reader for the YAML subset the workflows are written in that THROWS on anything else (a tab, an anchor, a folded or continued scalar, a duplicate key, a flow map with content), so a construct it does not know fails where it was written rather than being guessed. Its own test plants six such constructs and reads one known document | every OS |
| `ReleaseConfigTests` | `release-please-config.json`, the manifest and `version.txt`, the two ruleset bodies — against the workflows (the tag, the check names, expanded from the matrix) | every OS |

On the owner's machine (2026-10-03): the structure tests on Windows; the whole Scenarios suite in WSL `Ubuntu` from a copy
of the worktree under `/tmp`, built there, with a downloaded 7-Zip 23.01 (`7zz`, linked as `7z`) on a scratch `PATH`
folder for the zip case.

**Red, per guarantee.** Every check passed at its first run, which proves nothing by itself, so each was shown to have
teeth by a scratch script: one `sed` per mutation of the real file, the file checked changed, the one test run, the file
restored from a copy and compared by SHA-256 (every restore byte-identical). The scripts' 16 mutations ran in WSL (two first
tries deleted a line and left an empty `then` block — red for a shell syntax error, the wrong reason — and were redone as
substitutions), the workflow / configuration 36 on Windows; each red for its own symptom:

| Guarantee | The mutation | The red |
|---|---|---|
| the archive holds nothing extra | `package-daemon.sh` also installs `README.md` | `Expected entries.Select(e => e.Name.TrimEnd('/')) to be a collection with 8 item(s) because exactly the members install.sh requires … but {…, "wsl-care-0.1.0-linux-arm64/README.md", …}` |
| the archive misses nothing | the `machine.json` install deleted | the same assertion, `config/machine.json` absent; and the end-to-end flow: `Expected install.Exit to be 0 because install.sh must accept what release.yml packs` |
| regular files only | the binary `ln -s` instead of `install` | `Expected entries to contain only items matching (… == 53 OrElse … == 48) because install.sh refuses a link or a special file … but {wsl-care-0.1.0-linux-arm64/wsl-care} do(es) not match`; end to end: install exit 1 |
| the `.sha256` is sha256sum's two-space format | one space | `Expected File.ReadAllText(archive + ".sha256") to be the same string because sha256sum's own format … differ at index 65` |
| a bad version is refused | the version check replaced by `:` | `Expected result.Exit to be 2 … 0.1 linux-x64 … but found 0` |
| the Windows zip holds the exe alone | `7z` also given a copy folder | `Expected files.Select(e => e.FullName) to be equal to {"wsl-care-0.1.0-win-x64/wsl-care.exe"} … but {…, "x/wsl-care.exe"} contains 1 item(s) too many` |
| the guard compares `version.txt` | that comparison deleted | `Expected result.Exit to be 1 because daemon-v0.1.1 against version.txt 0.1.0 must be refused` |
| the guard checks the version shape | the pattern check deleted | three cases red, e.g. `… "the tag says 0.1.0/../x but src_daemon/version.txt …"` instead of the shape refusal |
| the guard checks `main` | `git merge-base --is-ancestor` replaced by `true` | `Expected off.Exit to be 1 because version=0.1.0` |
| the guard hands the version on | the `GITHUB_OUTPUT` append sent to `/dev/null` | `Expected File.ReadAllText(output) to be "version=0.1.0\n"` |
| an extra asset is refused | that problem line deleted | `Expected result.Exit to be 1 because an asset no release ships must be refused` |
| a tampered archive is refused | the hash comparison deleted | `Expected result.Exit to be 1 because an archive that does not match must be refused` |
| a missing archive is refused | that problem line deleted | `… because a RID without its archive must be refused` |
| problems fail the check | the final `exit 1` made `exit 0` | the same |
| tag push only | `workflow_dispatch:` added to `release.yml` | `Expected on.Keys to be equal to {"push"} … but {"workflow_dispatch", "push"} contains 1 item(s) too many` |
| workflow level reads | `release.yml`'s top `contents: write` | `Expected Permissions(Load(name)) to be equal to {["contents"] = "read"} because release.yml: …` |
| only the build job signs | `id-token: write` added to `publish` | `Expected signers to be equal to {"release.yml/build"} … but {"release.yml/build", "release.yml/publish"}` |
| only `publish` writes | the build job's `contents: write`; the proposer's `contents: write` | `Expected writers to be equal to {"release.yml/publish"} … but {"release.yml/build", …}` / `{"release-please.yml/release-please", …}` |
| `publish` waits for every leg | `needs: [guard]`; `if: always()` added | `… to be a collection with 2 item(s) because one red leg publishes nothing … but {"guard"}`; `Expected publish.Has("if") to be False` |
| every RID ships, on its PR runner | the `win-x64` entry deleted; `release.yml` on `ubuntu-latest`; `ci-daemon.yml` on `ubuntu-22.04` | `… to be equal to {"linux-x64", "linux-arm64", "win-x64"} … contains 1 item(s) less`; `Expected dictionary to be equal to … but {["linux-x64"] = "ubuntu-24.04", …}` (both directions) |
| one smoke script for both | `release.yml`'s smoke inlined; `ci-daemon.yml` calling another script | `Expected StepIndex(Job(Release, "build"), SmokeScript) to be greater than or equal to 0 … but found -1`; the same for `ci-daemon.yml` |
| the leg's stages, the attested subject | `subject-path` changed; the Scenarios run deleted | `Expected steps[order[4]]["with"].Map["subject-path"].Text to be the same string because the attested subject is the archive …`; `Expected order {-1, 5, 6, 7, 8, 9} to not contain -1` |
| verify from the draft, publish last | the from-draft verification deleted; a step appended after publishing | `Expected collection {2, 3, 4, -1, 6} to not contain -1`; `Expected goPublic to be 7 because nothing runs after the release is public, but found 6` |
| the guard sees `main` | `fetch-depth: 1` | `Expected string to be "0" because is-the-commit-on-main needs main in the clone` |
| the proposer refuses without secrets and hands on the App token | `exit 1` made `exit 0`; `token: ${{ secrets.GITHUB_TOKEN }}` | `Expected Run(steps[check]) … to contain "exit 1"`; `… because with GITHUB_TOKEN the tag would start nothing` |
| SHA pins | `attest-build-provenance@v4` | `… to be empty because every uses: names a 40-hex commit … but found … {"release.yml:155: - uses: actions/attest-build-provenance@v4"}` |
| ceilings, credentials, injection | `persist-credentials: true` in Sonar; the CodeRabbit job's timeout deleted; `${{ github.event.pull_request.number }}` in its `run:` | `… "persist-credentials" … to be "false"`; `… job.Has("timeout-minutes") to be True because coderabbit-review.yml/ask: every wait has a ceiling`; `Did not expect Run(step) … to contain "${{"` |
| the guard and the installer agree on a version | the contract's pattern narrowed | `Expected ReleaseFiles.DaemonVersionPattern to be the same string because the guard never admits a tag the installer would refuse` |
| draft, forced tag, the tag shape | `draft` false; `force-tag-creation` false; `component` renamed | `… because nothing is public until every RID's asset is on it`; `… because without it a draft cuts no tag and release.yml never runs`; `… because the tag release-please cuts is the tag release.yml starts on` |
| manifest = `version.txt`, first version 0.1.0 | manifest `0.1.0`; `initial-version` `1.0.0` | `Expected string to be "0.0.0" because release-please bumps both in one pull request …`; `… to be "0.1.0" because the first daemon release is 0.1.0 exactly` |
| the tag ruleset | the `update` rule deleted; the pattern `refs/tags/v*` | `… to be a collection with 3 item(s) because creation alone would leave a protected tag that can simply be moved`; `… to be equal to {"refs/tags/daemon-v*"}` |
| required checks = gating jobs, pinned to Actions | a context renamed; one `integration_id` changed; a bypass actor added to `main` | `… because a required check that never reports blocks every pull request forever …`; `… because only GitHub Actions can satisfy them`; `Expected ruleset.GetProperty("bypass_actors").GetArrayLength() to be 0` |
| the reader refuses the unknown | an anchor in `pr-title.yml` | `System.NotSupportedException : pr-title.yml:20: an anchor, alias or tag - outside the workflow YAML subset` |
| the installer's signer is the attesting workflow | `attest-build-provenance` replaced by `actions/attest` | the signer flow red: `release.yml` must contain `attest-build-provenance` |

## Flow catalogue

One row per flow. A row for a registered verb starts with `` `wsl-care <usage>` `` exactly as
`CommandLine.Commands` spells it — `VerbRegisterTests` fails, naming the verb, when one is missing. A
`covered` row names the test that covers it.

| Flow | Covered | By |
|---|---|---|
| `wsl-care --help` (and `-h`, `help`, no arguments): exit 0, every registered usage listed, nothing written under the root, no tool started | covered | `HelpAndVersionFlows.Help_exits_zero_and_lists_every_registered_command_on_stdout`; also `CommandLineTests`, `ProgramTests`, `BuiltBinaryTests`; AOT binary: CI smoke |
| `wsl-care --version`: the number in `src_daemon/version.txt` (a `+<commit>` suffix allowed) | covered | `HelpAndVersionFlows.Version_prints_the_version_in_src_daemon_version_txt`; also `ProgramTests`, `ProductVersionTests`; AOT binary: CI smoke |
| an unknown verb or option is refused: exit 2, one stderr line naming it | covered | `HelpAndVersionFlows.An_unknown_verb_is_refused_with_the_usage_code_and_one_stderr_line`; also `CommandLineTests`, `ProgramTests`, `BuiltBinaryTests` |
| `wsl-care config get [key] [--json]` on a fresh home: one line per key of the register, each from `(default)` | covered | `ConfigFlows.Config_get_on_a_fresh_home_lists_every_key_from_the_default_layer`; also `ConfigCommandTests` |
| `wsl-care config set <key> <value>` accepted: written to the user layer where the product's layout says; `config get` then shows `(user)` in text and `"layer": "user"` in JSON | covered | `ConfigFlows.An_accepted_config_set_writes_the_user_layer_and_config_get_then_names_user`; AOT binary: CI config smoke |
| `wsl-care config set <key> <value>` refused: exit 2, empty stdout, ONE `wsl-care:` message (other stderr lines are log lines), the user file byte-identical | covered | `ConfigFlows.A_refused_config_set_prints_one_message_line_exits_with_the_usage_code_and_leaves_the_user_file_unchanged`; AOT binary: CI config smoke |
| `wsl-care config set <key> <value>` over a broken user layer: `config get` still answers, observe-only on stderr and in the JSON (`configError` naming the file); `set` repairs it, moves the bad file to `config.json.broken-*`, and the next `get` is valid | covered | `ConfigFlows.A_broken_user_layer_is_reported_observe_only_and_config_set_repairs_it` |
| `wsl-care config reset <key>`: the key leaves the user file and `get` names `(default)` again | covered | `ConfigFlows.Config_reset_removes_the_key_from_the_user_layer_and_get_names_default_again` |
| control characters typed into a key (`config get` / `set` / `reset`) or read from the user layer never reach stderr raw: one `wsl-care:` message per refusal, no unexplained line, no control character but the console sink's colour | covered | `ControlCharacterFlows.Control_characters_typed_into_a_key_or_read_from_the_user_layer_never_reach_stderr_raw`; also `ControlCharacterTests`, `OutputRoadTests`; AOT binary (`win-x64`): smoke by hand 2026-10-02 |
| no `config` verb starts any tool: every config verb's example, a refused set, a broken layer and its repair leave the fakes' argv log empty | covered | `ConfigFlows.No_config_verb_starts_any_tool`; the log is proved alive by `FakeToolFlows` |
| every registered verb's `Example` runs against the built CLI: exit 0 or 2, never 70 | covered | `VerbRegisterTests.Every_registered_verb_runs_its_example_against_the_built_cli_without_crashing` (one case per verb, derived) |
| `wsl-care status [--json]` over the captured procfs tree (Linux): exit 0 in under 2 s wall clock (measured around the process, after one unmeasured warm-up start), `schemaVersion`, the fixture's `MemTotal`, 10 containers, 51 processes, a cwd read through a real symlink, `df` available — and the fakes' argv log EMPTY with `docker` and `powershell` on the `PATH` (plan §15b #5) | covered (Linux legs; skipped on Windows with the reason) | `StatusFlows.Status_json_over_the_captured_procfs_answers_within_the_budget_and_starts_no_slow_process`; in-process on every OS: `StatusCommandTests`; AOT binary: CI status smoke (Linux over the same tree) |
| `wsl-care status [--json]` on this binary's own side: under 2 s, the Windows binary answers host RAM / drive / `vmmemWSL` and names the VM as the other binary; the Linux binary over an empty root reports memory unavailable with the path and no value key; no slow part recorded yet; no tool started | covered | `StatusFlows.Status_json_on_this_binarys_side_answers_within_the_budget_names_what_it_cannot_read_and_starts_nothing`; AOT binary (`win-x64`): CI status smoke |
| `wsl-care status [--json]` after a full run recorded slow parts: `docker stats` come back from `history.jsonl` with the run id and their age, the Windows clock unavailable; nothing started | covered | `StatusFlows.Status_reads_the_slow_parts_back_from_the_last_full_run_with_their_age`; also `LastFullRunTests`, `StatusCommandTests` |
| `wsl-care status [--json]` as text, and a stray argument refused with exit 2 and one `wsl-care:` message | covered | `StatusFlows.Status_without_json_prints_text_and_a_stray_argument_is_refused_with_the_usage_code`; also `CommandLineTests` |
| `wsl-care preview --all [--json]` over the docker answers CAPTURED on 2026-10-02 at limit 0: A4 3 volumes / 641.4 MB, A5 13 containers, A6Unused 10 images, A7 4 entries, 13 kept named volumes — and A6Unused, A7 and A4 + kept each land on Docker's OWN `system df` reclaimable; 28 unbounded logs; `volume-seen.json` NOT written although the sandbox is writable (preview only reads, plan §15b #3); the fakes saw exactly the five product argvs, every one a read verb | covered | `PreviewFlows.Preview_over_the_captured_docker_at_limit_zero_reproduces_its_rows_and_starts_docker_read_verbs_only`; in-process: `PreviewCommandTests`, `CleanupPreviewTests` |
| `wsl-care preview --all [--json]` at the shipped limits: a volume first seen now is left (note: 3 younger), one first seen two days ago (a pre-written `volume-seen.json`) is counted, and its first sighting survives the look | covered | `PreviewFlows.At_the_shipped_limits_a_volume_first_seen_now_is_left_and_one_first_seen_two_days_ago_is_counted` |
| `wsl-care preview --all [--json]` when Docker cannot answer: not on PATH → `notInstalled`, a stopped daemon (Docker's real stderr) → `daemonStopped` with only the version probe run, a hang → `timedOut` at the 10 s probe ceiling with the tree killed; every row `available: false` with the reason and NO `count` / `reclaimableBytes` key; exit 0 | covered | `PreviewFlows.Without_docker_on_the_path_…`, `PreviewFlows.A_stopped_daemon_…`, `PreviewFlows.A_docker_that_hangs_…`; classification: `DockerCliTests`, `DockerCollectorTests` |
| `wsl-care preview --all [--json]` with a `docker` decoy in the CLI's current directory: the fake on `PATH` answers every call; with no docker on `PATH` the answer is `notInstalled` and the decoy is never started | covered | `ToolResolutionFlows.A_docker_in_the_current_directory_is_never_started_the_one_on_the_path_is`, `ToolResolutionFlows.Without_docker_on_the_path_a_docker_in_the_current_directory_still_leaves_docker_not_installed`; rules: `ExecutableResolverTests` |
| `wsl-care preview --all [--json]` on a WRITABLE state directory (the privileged case): `volume-seen.json` is not created where absent and stays byte-identical where present (its name Docker no longer lists survives), `volumeSeen.recorded: false` with a `read-only:` reason naming `collect`, the rows still count from the in-memory observation. **Observed red first** (2026-10-02, unfixed `PreviewRun`): `Expected boolean to be False because a preview creates no volume-seen.json, even where it could, but found True` (gate finding #3/#6/#10); green after the fix | covered | `PreviewFlows.Preview_on_a_writable_state_directory_still_never_writes_volume_seen_json`; in-process: `PreviewCommandTests` |
| `wsl-care preview --all [--json]` with the state directory unwritable (`AccessDenial`): `volumeSeen.recorded: false`, `read-only:` reason, the rows still answer, nothing written | covered | `PreviewFlows.An_unwritable_state_directory_makes_preview_read_only_and_it_still_answers`; also `VolumeSeenTests` |
| `wsl-care preview --all [--json]` as text, and `preview` without `--all` refused with exit 2 and one `wsl-care:` message | covered | `PreviewFlows.Preview_without_json_prints_the_rows_and_preview_without_all_is_refused`; also `CommandLineTests` |
| `wsl-care collect [--timer] [--json]` over the captured Docker AND health answers (and, on Linux, the procfs tree): exit 0, `recording: recorded`, ONE history line naming the detail file that exists, the A4 row of the captured Docker, the clock-jump count of the capture (875, Linux), the `wslconfig.memory` verdict naming `memory=36GB`; every fake call a read command — then `status --json` returns `docker stats` (and on Linux the Windows clock, launch latency subtracted) from THAT run with its age | covered | `CollectFlows.Collect_records_detail_then_history_and_status_shows_its_slow_parts_with_their_age`; write order in-process: `CollectRunTests.The_detail_is_written_first_then_the_history_line_that_names_it`; `FullRunCommandTests.Collect_json_records_the_run_and_status_then_reads_its_slow_parts_back_with_their_age` |
| `wsl-care collect [--timer] [--json]` with the state directory unwritable (`AccessDenial`; on Linux the system log directory too): exit 0, `recording: readOnly`, the `wsl-care: read-only: run as root to record` message, no history line, no `runs/`, no first sighting — and on Linux the run log under `$XDG_STATE_HOME/wsl-care/logs` | covered | `CollectFlows.An_unwritable_state_directory_makes_collect_measure_print_and_record_nothing`; in-process: `CollectRunTests.An_unprivileged_run_measures_and_reports_but_writes_nothing_and_says_read_only`, `FullRunCommandTests.An_unprivileged_collect_…`, `FullRunCommandTests.A_run_that_may_not_write_the_system_log_directory_logs_to_the_users_own` |
| Under an INHERITED `INVOCATION_ID` (every scenario sets one, as every descendant of a systemd unit carries it — a CI runner job, a VS Code Server user service): `collect` without `--timer` is a `cli` run with no timer pass, and an `act` is `cli` / `manual`, never `timer` — only `--timer`, which the timer unit passes in its `ExecStart`, makes the run the timer's | covered | `LogsFlows.A_collect_under_an_inherited_INVOCATION_ID_without_timer_is_a_cli_run_and_acts_on_nothing` — red first: `…TimerPass to be <null> … but found …TimerPass`; the four `ActCommandTests` that failed on the GitHub runners (CI run 37129452377: `Trigger … Manual … but found Timer`, `DryRun … False … but found True`) pass with `INVOCATION_ID` set; the timer's own run: `LogsFlows.The_timers_full_run_…` with `collect --timer` |
| `wsl-care collect [--timer] [--json]` when a record cannot be written (exit 1, `recording: failed`, the reason) and while another run holds `run.lock` (exit 75, nothing measured) | covered (in-process) | `FullRunCommandTests.A_collect_whose_history_line_cannot_be_written_…`, `FullRunCommandTests.A_collect_while_another_holds_the_run_lock_…`, `CollectRunTests`; not staged against the built binary: a failing disk cannot be made to fail on cue there |
| `wsl-care doctor [--json]` after a `collect`: exit 0, `lastRun` ok, the follower that never ran a named `problem`, `healthy: false`; read commands only | covered | `CollectFlows.Doctor_after_a_collect_finds_the_last_run_recent_and_still_names_what_is_not_installed`; in-process: `DoctorTests`, `FullRunCommandTests.Doctor_json_answers_exit_zero_with_its_verdict_and_checks` |
| `wsl-care events follow [--once]` after a daemon restart that lost Docker's buffer: exit 0, ONE `gap` marker whose reason says how far the buffer reached, the start recorded as backfilled — then `status --json` counts the last 24 h `partial` with the gap named | covered | `EventsFlows.Once_after_a_daemon_restart_writes_ONE_unrecoverable_gap_and_status_counts_partial_naming_it`; rules: `CoverageTests` |
| `wsl-care events follow [--once]` over an idle engine that did not restart (the last marker's bridge, an EMPTY buffer): exit 0, `0 start(s), 0 gap marker(s)`, no gap marker, the new `covered` marker carries the engine, `docker network inspect bridge` among the read verbs, `starts-summary.json` written | covered | `EventsFlows.Once_over_an_idle_engine_that_did_not_restart_writes_no_gap_and_records_the_engine_on_its_marker`; rules: `CoverageTests` |
| `wsl-care events follow [--once]` with the daemon down: exit 0, the reason printed, no gap marker (a gap is known only once Docker answers), only the version probe run | covered | `EventsFlows.Once_with_the_daemon_down_exits_zero_names_why_and_writes_no_gap` |
| `wsl-care events follow [--once]` followed live (Linux): the socket down then up waited for IN-PROCESS (one 5 s wait), ONE gap marker, a live start in the day file while the stream is open, SIGTERM → exit 0 with the stop marker carrying how far coverage reached | covered (Linux legs; skipped on Windows with the reason) | `EventsFlows.Followed_live_it_waits_for_the_socket_in_process_records_a_start_and_stops_clean_on_SIGTERM`; on every OS in-process: `EventsFollowerTests` (the 5 → 10 → 20 s backoff on a moved clock), `FullRunCommandTests.Events_follow_stopped_by_a_signal_exits_zero_with_its_stop_marker` |
| `wsl-care act <A#>[,<A#>...] (--preview or --confirm) [--manual or --timer] [--volume <name>]... [--only <file>] [--json]` unprivileged: exit 77, ONE `needs root` message, empty stdout, no running.json / history line / dry-run stamp / run detail / lock file, no tool started | covered (skipped for a root or elevated account, with the reason) | `ActFlows.An_unprivileged_act_is_refused_whole_and_leaves_no_state_no_lock_and_no_command_behind`; in-process: `ActCommandTests.An_unprivileged_act_is_refused_whole_before_the_lock_or_any_state_is_touched` |
| `wsl-care act <A#>[,<A#>...] (--preview or --confirm) [--manual or --timer] [--volume <name>]... [--only <file>] [--json]` `--preview` with root claimed in the sandbox: previewed from the fake journalctl's `--disk-usage`, nothing written; on the Windows binary exit 2 naming the distro side | covered | `ActFlows.With_root_claimed_a_preview_reads_the_journal_s_size_and_writes_nothing`; in-process: `ActCommandTests`; AOT binary (`win-x64`): smoke by hand 2026-10-02 |
| `wsl-care act <A#>[,<A#>...] (--preview or --confirm) [--manual or --timer] [--volume <name>]... [--only <file>] [--json]` `--confirm` with root claimed: A10 runs through the fake (`--disk-usage`, then `--vacuum-time=30d`), detail then history line, `running.json` gone | covered (Linux legs; skipped on Windows with the reason) | `ActFlows.With_root_claimed_a_confirmed_act_runs_a10_through_the_fake_and_records_detail_then_history`; on every OS in-process: `ActCommandTests.A_confirmed_act_runs_records_and_answers_with_the_measured_result`, `JournalVacuumTests`, `ActionEngineTests` |
| `wsl-care act <A#>[,<A#>...] (--preview or --confirm) [--manual or --timer] [--volume <name>]... [--only <file>] [--json]` while the run lock is held: `act` 75, `collect` 75, nothing recorded — ONE lock for both | covered (`collect` on both families, `act` on the Linux legs) | `ActFlows.One_lock_for_collect_and_act_the_second_one_refuses_with_75_and_waits_for_nothing`; in-process: `ActionEngineTests.A_full_run_started_while_an_act_holds_the_lock_is_busy_one_lock_for_both`, `ActCommandTests` |
| `wsl-care act <A#>[,<A#>...] (--preview or --confirm) [--manual or --timer] [--volume <name>]... [--only <file>] [--json]` meeting a wedged run (76), an action that fails (3), an invalid layer (78), an unbuilt or other-side action (2), the timer's gates and dry run | covered (in-process) | `ActCommandTests`, `ActionEngineTests`; not staged against the built binary: a live wedged process and the systemd timer cannot be made on cue there |
| `wsl-care act <A#>[,<A#>...] (--preview or --confirm) [--manual or --timer] [--volume <name>]... [--only <file>] [--json]` `--preview` of EVERY action this build holds (derived from the registry) over the CAPTURED Docker, root claimed, a target user with no tool installed: exit 0, every id answered, A4 previews the three captured volumes, A8 is a skip naming npm, every docker call a read verb, no state written; the Windows binary exits 2 | covered (Linux legs; Windows: the exit-2 half) | `ActFlows.A_preview_of_every_action_this_build_holds_answers_each_from_live_state_and_starts_only_read_commands`; AOT binary: CI act smoke (every RID) |
| `wsl-care act <A#>[,<A#>...] (--preview or --confirm) [--manual or --timer] [--volume <name>]... [--only <file>] [--json]` `A4 --confirm --manual --volume <one>` over the captured Docker: exit 0, the fake saw exactly `docker volume rm <that one>`, freed = its captured `df -v` size, the key never serialised, the history line's trigger `manual` | covered (Linux legs; skipped on Windows with the reason) | `ActFlows.A_button_run_of_a4_removes_only_the_volume_the_panel_showed_records_the_manual_trigger_and_freed_from_the_confirmed_one`; in-process: `DockerCleanupTests`, `ActCommandTests` |
| the other E3.S2 actions end to end against the built binary (A5–A7, A9, A11, A12, A14, A17 confirmed) | not covered | in-process only (`DockerCleanupTests`, `UserCacheTests`, `PackageCacheTests`, `SuspectTerminationTests`): a confirmed user-scoped run needs `runuser` as root, and A11's real sender is never wired under a sandbox — by design |
| `wsl-care collect [--timer] [--json]` as the TIMER (`--timer`, as its unit starts it) over the captured answers and a 1.5 G journal: exit 0, the action pass ran after measuring in its dry-run week, ONE history line (trigger `timer`, `dryRun`), on Linux A10 `dryRun` (its trigger fired), on Windows every action skipped naming the distro side; every fake call a read command — then `runs --json` and `logs --json` read that run back | covered | `LogsFlows.The_timers_full_run_acts_after_measuring_in_its_dry_run_week_and_runs_and_logs_read_it_back`; in-process: `TimerPassTests`, `MemoryActionTests` |
| `wsl-care logs [--period <today, yesterday, yyyy-MM-dd or from..to>] [--action <A#>] [--detail] [--json]` over a seeded history: `--period 2026-10-01` holds the 23:59:59 run only (3 objects, 3 GB, 1 with a cleanup, 1 button); a reversed range exit 2 with ONE message; nothing written but its run log | covered | `LogsFlows.Logs_and_runs_over_a_seeded_history_answer_a_utc_date_and_a_range_and_write_nothing`; in-process: `LogsCommandTests`, `RunLogsTests` |
| `wsl-care runs [--period <today, yesterday, yyyy-MM-dd or from..to>] [--json]` over the same history: the range lists both runs, the dry one with what it would free | covered | `LogsFlows.Logs_and_runs_over_a_seeded_history_answer_a_utc_date_and_a_range_and_write_nothing`; in-process: `LogsCommandTests`, `RunLogsTests` |
| the E3.S3 actions confirmed against the built binary (A1, A2, A3, A15, A16) | not covered | in-process only (`MemoryActionTests`, `BuildServerTests`, `FilesystemTrimTests`, `ClockFixTests`, the property test): `sysctl`, `fstrim`, `hwclock`, `chronyc` are never faked as root writes on PATH here, and A3 needs `runuser` as root; their previews ARE run by the built binary in `ActFlows.A_preview_of_every_action_this_build_holds_…` |
| `wsl-care runs show <runId>` / `runs log <runId>` | not covered | not built (plan §6 names them; E6's Logs page decides whether `logs`' cleanups suffice) |
| `wsl-care agents list` / `agents probe <path>` | not covered | not built yet (E7) |
| `wsl-care archive preview / run / restore / list` | not covered | not built yet (E9) |
| `install.sh`: a fresh install — binary 0755 at `/opt/wsl-care/bin/wsl-care`, the link to that ABSOLUTE path, the three units byte for byte 0644, the machine layer when absent, the state folders; `systemctl` daemon-reload → enable --now timer + follower → enable --now sysstat + atop → is-active ×2; the binary started by its absolute path for `collect` then `doctor --json`; no sudo; the temporary folder gone | covered (Linux legs; the Windows leg skips with the reason) | `InstallFlows.A_fresh_install_places_the_binary_link_units_and_machine_layer_enables_both_units_and_verifies_through_the_absolute_path` |
| `install.sh`: the newest `daemon-v*` release (the list's first entry is the extension's), archive then `.sha256`, gh verifying THAT archive before any `systemctl`; every curl call asks for https-only, redirects included, under `--max-time` | covered (Linux legs) | `InstallFlows.The_newest_daemon_release_is_downloaded_never_the_extensions_and_verified_before_any_write` |
| `install.sh --version <x.y.z>`: no releases-list call; a malformed version (`../`, `v`-prefix, `;`, a newline) exit 2 before anything runs | covered (Linux legs) | `InstallFlows.An_explicit_version_skips_the_releases_list_and_a_malformed_one_is_refused_before_anything_runs` |
| `install.sh`: checksum mismatch → step `checksum`, no gh call, nothing installed; no `.sha256` → step `download`, nothing installed | covered (Linux legs) | `InstallFlows.A_checksum_mismatch_aborts_before_the_attestation_and_before_anything_is_installed`, `InstallFlows.A_release_without_its_sha256_is_refused_never_installed_unchecked` |
| `install.sh`: a refused attestation → step `attestation`, nothing installed; under `SUDO_USER` (and a `GH_TOKEN` in the environment) root verifies a bundle it fetched itself — the attestation API read unauthenticated, no `runuser`, gh's `HOME` / `GH_CONFIG_DIR` / `XDG_*_HOME` inside the temporary folder, no token | covered (Linux legs) | `InstallFlows.A_refused_attestation_aborts_before_anything_is_installed`, `InstallFlows.Under_sudo_root_verifies_a_bundle_it_fetched_itself_with_no_login_no_token_and_no_runuser` |
| `install.sh`: the attestation identity is EXACT — `release.yml` built from a branch, `release.yml` at ANOTHER release's tag, or on a self-hosted runner is refused with nothing installed; the verification asks `--cert-identity release.yml@refs/tags/daemon-v<version>` + `--deny-self-hosted-runners` + `--repo`, never `--signer-workflow` | covered (Linux legs) | `InstallFlows.An_attestation_of_release_yml_built_from_a_branch_is_refused_and_nothing_is_installed`, `…An_attestation_of_another_release_tag_is_refused_the_identity_is_the_tag_of_the_version_installed`, `…An_attestation_made_on_a_self_hosted_runner_is_refused` |
| `install.sh`: the bundle decoder — a real GitHub bundle (captured) decompressed exactly, under the system awk, mawk and gawk; several attestations, one genuine, suffice and each is verified alone; no attestation, or a stream that is not snappy, refused before anything | covered (Linux legs) | `InstallFlows.A_real_github_bundle_is_decompressed_byte_for_byte_so_gh_reads_its_signer` (×3), `…Among_several_attestations_one_genuine_bundle_is_enough…`, `…No_attestation_or_an_unreadable_bundle_is_refused_before_anything_is_installed` |
| `install.sh` without a usable `gh`: none, gh 2.45.0 (no `gh attestation`, Ubuntu 24.04's), gh 2.49.0 (older than the measured floor 2.56.0), a `--help` that fails or lacks a flag the check uses → step `preflight` BEFORE any download, pointing at GitHub's apt repository (`cli.github.com/packages`) and `--skip-attestation`, never at `apt-get install gh` or a login; nothing installed | covered (Linux legs) | `InstallFlows.Without_gh_the_installer_stops_before_downloading_anything_and_says_how_to_proceed`, `…A_gh_without_attestation_verify_is_refused_before_any_download_pointing_at_githubs_apt_repository`, `…A_gh_that_has_attestation_verify_but_is_older_than_the_measured_floor_is_refused_before_any_download`, `…A_gh_whose_attestation_verify_fails_or_lacks_a_flag_the_check_uses_is_refused_before_any_download` |
| `install.sh --skip-attestation`: installs without gh, the banner on stderr, a bad checksum still refused | covered (Linux legs) | `InstallFlows.Skip_attestation_installs_without_gh_says_so_loudly_and_still_refuses_a_bad_checksum` |
| `install.sh`: an existing `/etc/wsl-care/config.json` kept byte for byte | covered (Linux legs) | `InstallFlows.An_existing_machine_configuration_is_kept_byte_for_byte` |
| `install.sh`: a unit not active → step `verify: wsl-care-events.service active`; an unhealthy `doctor --json` → step `verify: doctor healthy` with doctor's checks on stderr; sar missing after apt → step `verify: sysstat (sar on PATH)`; sysstat still off after `dpkg-reconfigure` → step `packages` | covered (Linux legs) | `InstallFlows.A_unit_that_is_not_active_after_enabling_fails_the_install_naming_that_step`, `…An_unhealthy_doctor_fails…`, `…Missing_sysstat_and_atop_are_installed_with_apt…`, `…Sysstat_switched_off_is_switched_on_through_debconf…` |
| `install.sh --uninstall`: units, binary, link and `/opt/wsl-care` gone, history / logs / machine layer kept and named; `--purge` removes exactly the three folders and the lock, names each, touches nothing else | covered (Linux legs) | `InstallFlows.Uninstall_removes_the_units_binary_and_link_and_keeps_history_logs_and_machine_config`, `InstallFlows.Uninstall_with_purge_removes_exactly_the_state_logs_machine_config_and_lock_and_names_them` |
| `install.sh --dry-run` (install and uninstall): the prefix tree identical, no unit / package / binary call, every step printed, no root needed | covered (Linux legs) | `InstallFlows.Dry_run_changes_nothing_needs_no_root_and_prints_every_step` |
| `install.sh --set-default-user <name>`: without the flag nothing written (advice printed); with it and no default, `[user] default=` appended and read back by the daemon's `TargetUserDiscovery.DefaultUser`; an existing default never rewritten; an unknown user or a `[user]` section without `default=` refused before anything; the installer's reader and the daemon's agree on ten wsl.conf shapes | covered (Linux legs) | `InstallFlows.Without_the_flag_wsl_conf_is_never_written…`, `…With_the_flag_and_no_default_user…`, `…With_the_flag_an_existing_default_user_is_never_rewritten`, `…The_flag_for_an_unknown_user…`, `…The_installer_and_the_daemon_read_the_same_default_user_from_every_wsl_conf_shape` |
| `install.sh` preflight refusals: not root (the `sudo sh -s --` line, sudo never called), no systemd, an unknown architecture, a foreign `/usr/local/bin/wsl-care`, a hostile archive member (`..`, outside the folder, a link — each riding a complete release); arm64 installs the `linux-arm64` asset; an upgrade restarts the follower | covered (Linux legs) | `InstallFlows.A_non_root_run_is_refused…`, `…Without_systemd_running…`, `…On_arm64…`, `…A_wsl_care_on_the_link_path…`, `…An_archive_member_that_leaves_its_folder_or_is_a_link…`, `…An_upgrade_restarts_the_running_follower…` |
| the shipped units and machine layer: `ExecStart` argv parsed by the CLI (`collect --timer`, `events follow`), `SuccessExitStatus` = `ExitCode.Busy`, the timer's calendar, no breaking sandbox directive, `install.sh`'s unit list = the folder, the machine layer valid and empty | covered (every OS) | `ShippedFilesTests`; systemd's own parser: CI `systemd-analyze verify` (Linux legs) |
| `install.sh`'s pinned identity is this repository's attesting `release.yml` AT the release tag, and no command line of it uses `--signer-workflow` | covered (every OS) | `InstallFlows.The_identity_the_installer_pins_is_this_repositorys_attesting_release_workflow_at_the_release_tag`; `ReleaseWorkflowTests.The_release_scripts_agree_with_the_installer_on_what_a_version_is` (the identity's tag = the trigger) |
| the release archive (`package-daemon.sh`, as `release.yml` runs it): per Linux RID exactly the members `install.sh`'s unpack loop requires plus their folders, regular files and folders only, owner 0:0, 0755 binary / 0644 units and machine layer byte for byte, the `.sha256` line `<hash>  <name>`; the Windows zip holds `wsl-care.exe` alone; a bad version / unknown RID / missing binary refused, nothing written | covered (Linux legs; the zip where 7-Zip is on `PATH`) | `PackageFlows.A_linux_archive_holds_exactly_what_install_sh_unpacks_as_regular_files_under_one_folder` (linux-x64, linux-arm64), `…The_windows_archive_holds_the_exe_alone_under_its_folder`, `…A_bad_version_an_unknown_rid_or_a_missing_binary_is_refused_and_nothing_is_written` |
| `install.sh` installs the archive the release script packed (the packer and the installer agree, end to end) | covered (Linux legs) | `PackageFlows.The_installer_installs_the_archive_the_release_script_packed` |
| the release guard (`release-guard.sh`): `daemon-v<version>` with `version.txt` agreeing is admitted and the version reaches `GITHUB_OUTPUT`; another component's tag, a malformed version, a disagreeing `version.txt`, a commit off `main` are refused (exit 1, `::error::`) | covered (Linux legs) | `ReleaseScriptFlows.The_guard_admits_…`, `…The_guard_refuses_a_tag_it_cannot_release` (5 cases), `…The_guard_refuses_a_tagged_commit_that_is_not_on_main` |
| the completeness check (`verify-release-assets.sh`): every RID's archive and a matching `.sha256` passes; a missing archive, a missing `.sha256`, a tampered archive, a `.sha256` naming another file, an extra asset, a whole RID missing are each refused, every problem named | covered (Linux legs) | `ReleaseScriptFlows.A_complete_set_passes_the_completeness_check`, `…An_incomplete_or_wrong_set_is_refused_naming_every_problem` |
| the release pipeline's structure: tag-push-only trigger, per-job permissions across every workflow (signing = the build job, `contents: write` = the publish job), `publish` needs guard + every leg, RIDs = the asset contract = the pull-request legs on the same runners, the shared smoke, stage and publish order, SHA pins, ceilings, no credential left in a checkout, no expression in a `run:`; release-please's tag = the trigger = the tag ruleset, manifest = `version.txt`, first version 0.1.0; required checks = the gating jobs | covered (every OS) | `ReleaseWorkflowTests` (16), `ReleaseConfigTests` (5) |
| the published binary's smoke (`smoke-daemon.sh`: help/version, config round trip, `status`, a recorded full run read back with `doctor`, `act --preview` of every action) | covered by CI, not by the suite | `ci · daemon` runs it on every pull request on all three RIDs; `release.yml` runs the same file on every leg; run by hand on 2026-10-03 against the JIT builds (Linux in WSL, Windows in Git Bash), and red with a planted `version.txt` |
| the release run itself — the App token's tag starting `release.yml`, the attestation, the upload to the draft, the publish | not covered | needs the owner's settings (`docs/repo-settings.md`) and the cut of `daemon-v0.1.0`; verified after it by `POST_DEPLOY.md` items 8–10 |
| `install.sh` against a real release, a real `gh attestation verify`, real systemd and apt | not covered | the first live install on the owner's machine is E4's done-line, after the owner cuts `daemon-v0.1.0` (`docs/repo-settings.md`) |
| the extension: status bar, panel, buttons, logs page, settings sync, help | not covered | the extension is not built yet (E5–E8) |

## What it does not prove

- **The installer is proved over a prefix and fakes, never against this machine.** systemd, apt, gh, curl and the
  release are fakes; "root" is a fake `id`. What the real tools DO with the argv the script sends — `systemctl enable
  --now`, apt's install, sysstat's postinst honouring the debconf switch, curl's `--proto =https` — is theirs, and is
  first observed at the live install (E4's done-line). The flows assert the argv sent and the files written, and say
  so. The real `/bin/sh`, `tar`, `sha256sum`, `install`, `ln`, `od`, `awk` (mawk and gawk) and `sed` ARE exercised.
  **gh is the one fake that judges**: it enforces the identity flags with gh's semantics as MEASURED on 2026-10-03 —
  with the real gh 2.97.0 and cli/cli's own attestation, in WSL `Ubuntu`, no login: `--cert-identity` exact,
  `--signer-workflow` a literal prefix (`…/deploy` accepted `…/deployment.yml@refs/heads/trunk`), a wrong identity
  refused, `--source-ref` and `--deny-self-hosted-runners` enforced. What it does NOT do is Sigstore: no signature, no
  certificate chain, no transparency log, no TUF — a test bundle is self-signed. The facts the design rests on were
  observed with the real tools, not the fake: an unauthenticated `GET /repos/cli/cli/attestations/sha256:…` answers
  (60 requests an hour) with `bundle: null` and a `bundle_url` serving snappy JSON; `gh attestation verify --bundle`
  verifies with an EMPTY gh configuration and no token on gh 2.56.0 through 2.97.0, while online verification without
  a login stops with exit 4 ("gh auth login"); with the network cut it does not finish (TUF), so it hangs to the 180 s
  ceiling and refuses; gh 2.49.0–2.55.0 cannot build the public-good verifier at all (`unsupported tlog public key
  type: PKIX_ED25519`), bisected over nine releases; gh 2.56.0's `attestation verify --help` lists every flag the
  installer uses. A bundle file must end in `.json` or `.jsonl` (`.bundle` refused).
- **The units' hardening is unobserved.** `NoNewPrivileges=yes` and the resource limits are requests to systemd;
  `systemd-analyze verify` proves the files parse, not that a live run of every action succeeds under them. Two risks
  are named rather than tested (E4 review): a snap-packaged Docker under `NoNewPrivileges`, and a child killed at
  `MemoryMax=1G` — `POST_DEPLOY.md` #11 reads the journal for both on the live install.
- **The doctor wait is proved at 0 seconds** (`WSL_CARE_INSTALL_DOCTOR_SECONDS=0`): one attempt. The 2-minute wait for
  the follower's first marker on a live machine is not timed.

- **A5's "goes with the container" is Docker's decision, not ours.** The preview counts a mounted volume as going with
  `docker rm -v` when `system df -v` labels it anonymous; Docker itself removes a volume whose MOUNT named no source
  (moby's `removeMountPoints`), which is the same set for every volume Docker created anonymously — a volume made by
  `docker volume create` and mounted by its id is kept by Docker and counted as named here. The run counts only what
  `docker volume ls` no longer lists. Not observed on a live engine (the captured Docker labels all 48 volumes consistently).
- **The fake pidfd calls prove the grace logic, not the kernel.** One shared grace, a poll error and a cancellation are
  tested over `IPidfdCalls` with a moved clock on any platform; the real `poll` over several pidfds is exercised by the
  Linux-only test of three real children (run in WSL), never against processes the product did not start.
- **The fakes replay one afternoon of one Docker.** The captured answers are Docker Desktop 4.81.0 / Engine
  29.6.1 after a cleanup: no dangling image, no Testcontainers container, no `docker-container` builder, no
  container start in Docker's event buffer, no Engine inside the distro (so no log size is ever measured live).
  Those edges are proved on synthetic rows in the captured shapes, labelled so. Whether the real tools still
  answer in these shapes is the live contract's job, and it runs by hand — at release and when a tool changes.
- **The decoy stands in the current directory only.** `ToolResolutionFlows` plants its decoy in the CLI's current
  directory because a scenario may write there; the other places the operating system's own search would try first —
  the application's directory, System32, the Windows directory — are covered by the same rule (`PATH` alone, full
  path started) and by `ExecutableResolverTests`, never by planting a file there (the test output, the machine).
- **The live contract needs a quiet Docker.** It retries and re-reads, then skips (fails at release) when the
  figures it compares keep changing. On 2026-10-02, with a parallel session building images and replacing
  containers, the first required runs failed for exactly those races; with stillness judged on the compared
  figures (counts and reclaimable bytes — a running database grows its volume between any two calls) the
  required run inside WSL `Ubuntu` passed 11/11, and on Windows 8 passed with the 3 systemd checks skipped. It runs as the unprivileged user, so the privileged
  write of `volume-seen.json` under `/var/lib/wsl-care` is proved only in a sandbox, never on the real path.
- **`status` shells out to nothing; `preview` only to Docker read verbs** — proved on the fakes' argv log
  and by enumerating `DockerCommands`, not by an OS-level sandbox: a code path that started another tool
  would fail to find it on the scenario PATH rather than reach a real one.
- **The procfs tree is one afternoon of one machine**: 51 of ~196 processes, no kernel thread, no
  container member in `/proc` (Docker Desktop keeps them in another PID namespace). The container-member,
  `systemd --user` and negative-remainder edges are proved on synthetic trees only. No capture of
  `Ubuntu-26.04` or of a Docker Engine installed in the distro exists.
- **The Linux scenario over the procfs tree runs on the Linux legs only**; on the owner's machine it was
  run by hand on 2026-10-02 from a copy of the worktree built under `/tmp` in WSL `Ubuntu` (Release, JIT).
  The budget is held by the SECOND start (one unmeasured warm-up), and by the JIT build; the AOT binary is
  smoked in CI for `status --json` but not timed there.
- **The Windows probe's figures are the real host's even under `WSL_CARE_ROOT`** (only the system drive
  follows the sandbox): host RAM and `vmmemWSL` come from the operating system, read-only. The tests
  assert their shape, not their values; the fake counters prove the assembly.
- **The slow parts' writer is `collect`** (E2.S3), proved end to end by `CollectFlows` (collect, then `status`); the older
  `LastFullRunTests` still write their lines with the product's own `RunRecordWriter`.
- **`collect` runs as the test user, never as root.** "Privileged" is a sandbox the user owns (writable); "unprivileged"
  is a state directory denied with `AccessDenial` (and a double that answers the write probe no). The real
  `/var/lib/wsl-care` as `root:root 0755` is the installer's (E4.S1) and is first met on the live install.
- **The folder walk is proved on small trees.** The 2 000 000-entry / 2-minute ceiling is proved by a 3-entry limit; a
  walk of the real `~/git` (41 GB of worktrees) has not been timed, and under the root timer `$HOME` is root's — whose
  home it walks is E4.S1's decision.
- **The follower's real stream is proved twice and the fake's once.** The live contract observes that a FUTURE
  `--until` streams and closes by itself (a 4-second window); the 10-minute segment of the product is not run live. The
  scenario stream is the fake printing a line and hanging; the SIGTERM flow runs on the Linux legs only. Docker's
  buffer reached back 91 seconds when measured (`fixtures/health/…/SOURCE.txt`), so on this machine the backfill fills
  only a short restart of the follower; every longer outage is, correctly, a gap.
- **Health parts are proved over one afternoon's answers** (`fixtures/health/ubuntu-2026-10-02`): one failed unit, no
  disabled snap, no OOM kill; the other shapes are synthetic in the captured format, labelled so. `journalctl --dmesg`
  sees THIS boot only.
- **Clock drift is proved on numbers**, not on a skewed clock: no test moves this machine's clock, and A16 (the fix) is
  E3.S3's.
- **The secret redaction of command lines is a pattern** (`--name=value` / `--name value` where the name
  holds token, secret, password, apikey or credential); a secret in any other shape is shown as it is,
  cut at 200 characters.
- The scenario and process tests run the **JIT** build. The Native AOT binary is exercised by the CI
  smoke only — `--help`, `--version` and the configuration round trip (set → get → refused set →
  get) — on all three RIDs. Locally the owner's machine can publish only `win-x64` (with `vswhere.exe`
  on `PATH`); both smoke steps were run against that binary by hand on 2026-10-02.
- **`linux-arm64` has never run on this machine** (there is no arm64 host here). Its first green is the
  `ubuntu-24.04-arm` leg of the first pull request that carries E1.S3.
- The CLI's `HOME` / `USERPROFILE` are **not** redirected in a scenario: isolation rests on
  `WSL_CARE_ROOT`, which is itself the product guarantee under test (`HostPathsTests` prove every path
  lands under it). A code path that ignored it would read the real profile rather than fail.
- The scenario `PATH` holds **only** the fakes. A future verb that legitimately needs another system
  tool fails in the harness until that tool is faked or deliberately admitted — on purpose.
- On Windows, the shell lookups of `FakeToolFlows` go through `cmd.exe` with plain words only; argv
  with spaces is proved by starting the fake directly.
- On the owner's machine the directory-link tests run over **junctions**, not symbolic links; the
  symlink branch runs on the Linux CI legs.
- **The atomic write's residual window is not staged.** The tests swap a link in after the approval
  and after the temporary file is written, through the file system's step callback; the window between
  the last re-check and the rename itself cannot be reached without a handle-relative rename, which the
  code does not have ([architecture.md](architecture.md) § *Fail-closed resolution and the atomic
  write*). A FILE symlink swap is not staged either — this machine's account cannot create one; the
  swaps use directory links (junctions here, symlinks on the Linux legs).
- **The real access denial runs where it can be staged:** `icacls` deny on Windows, `chmod 000` on the
  Linux CI legs; it skips, with that reason, for an account the denial does not bind (root). The other
  fail-closed tests use a seam double, so they prove the decision, not that the disk reports the
  failure — that is the denied-directory test's job.
- `OutputRoadTests` sees `.Write(` / `.WriteLine(` calls in the CLI's source; a `TextWriter` handed to a
  library that writes to it would escape the scan (none exists today). The FILE log sink renders values
  as they are: control characters in a log file are not a terminal concern and are not replaced there.
- `ShutdownSignals` is thin wiring over `PosixSignalRegistration` and is not tested in isolation; the
  token's effect on a run is (`ProgramTests`, `ProcessCommandRunnerTests`).
- **The never-list is proved on argv, not on effects.** The property test shows no generated input gets a never-command
  PAST THE POLICY; what a tool does with an argv the policy allows is the tool's (journald's vacuum, later Docker's
  removals) and is proved only against fakes and, for real, by the live smoke at release. The generator reaches what
  its corpus and its slot kinds reach — a seed is one walk, and a new slot kind fails the generator until it is taught.
  The oracle is the plan's list written a second time by the same author: an independent SHAPE, not an independent mind.
- **No act ever ran as root here.** "Root" in every test and scenario is a claim (`ProcessPrivilege` set by the test,
  or `WSL_CARE_SANDBOX_PRIVILEGED=1` under `WSL_CARE_ROOT`); `/run/wsl-care.lock` and `/var/lib/wsl-care` as root's, the
  real `journalctl --vacuum-time`, and `runuser` (its environment handling is read from util-linux's source, not
  observed) are first met on the live install (E4). No destructive command was run against this machine.
- **The process table is the real one even under `WSL_CARE_ROOT`** (`SystemProcessTable`, a pid is a fact about the
  machine); the dead / mismatched / wedged decisions are proved on a scripted table, and live only for this process's
  own pid.
- **The idle figures are fixtures**: `/proc/loadavg` and `/proc/stat` written by the test; whether the 5-minute load
  average is a good stand-in for "CPU below 20 % for 5 minutes" on this machine is a measurement the dryRun week owes.
- **The heartbeat is proved at 20 ms** with a moved clock, not at 5 s over a minutes-long action.
- **The timer's action pass on a live systemd** is not observed: the timer gates and the dry-run window are proved through
  the engine with `RunTrigger.Timer` and over the built CLI with `collect --timer`; the unit that passes `--timer` is
  checked by `ShippedFilesTests` and `systemd-analyze verify`, not by a running timer (E4's live install).
- **The Docker WRITE answers are not captured** (E3.S2): capturing `docker volume rm`, `docker rm -v`, `image prune` and
  `builder prune` would remove things on this machine, which nothing here ever does. Their shapes — names echoed on stdout,
  one `Error response from daemon: …` line per refused name, `Total reclaimed space:` / `Total:` — are the docker CLI's and
  buildx's source, written into the tests by hand; the parsers read only what those lines carry. The one captured E3.S2
  answer is the read-only `docker builder prune --help` (`fixtures/docker/ubuntu-2026-10-02/builder-prune-help.out`).
- **No confirmed user-scoped run is observed for real**: `runuser` needs root; the tests prove the argv, the clean
  environment and the policy's verdict, and what the TOOL does is the test's scripted effect on the sandbox.
- **The pidfd sender is proved on Linux only**, against children the test started (a `sleep`, a `sleep` that ignores
  SIGTERM); never against a process of the machine, and never under a sandbox, where the CLI wires the refusing sender.
- **A12's "referenced" rule is Playwright's own** (its install-cache validation: `.links/` → `browsers.json`), read from its
  behaviour and this machine's cache on 2026-10-02, not from a Playwright release's tests.
- Nothing here runs on a real WSL VM under memory pressure; the live smoke on the owner's machine is
  the only place that happens, at release time.
- **The release itself has not run** (E4.S2): no App token has cut a tag here, no attestation has been made or verified,
  no draft has been filled or published — that needs the owner's settings (`docs/repo-settings.md`) and is checked after
  the first cut by `POST_DEPLOY.md` items 8–10. The tests hold the workflows' STRUCTURE and run the scripts the steps call;
  what GitHub does with the YAML (that `push: tags` fires for an App-created ref, that `gh release download` sees a draft,
  that the attestation names `release.yml`) is read from the family's runs and from the tools' sources, not observed here.
- **The rulesets' effect is not observed by a test**: GitHub reads none of the files. Their PROBE (a creation or push that
  must be refused) is a step of `docs/repo-settings.md`, run by the owner when applying them.
- **The smoke script is not run by the suite**: it needs a published binary, so CI runs it (every pull request, every
  release leg); on 2026-10-03 it ran by hand against the JIT builds — Linux in WSL, Windows in Git Bash — and refused a
  planted `version.txt`. The AOT binaries it was written for are CI's.

## When it runs

On every push to `main` and every pull request: `ci · daemon` (unconditional, no path filter) runs the
three test executables and the shared smoke of the published AOT binary (`.github/scripts/smoke-daemon.sh`) on
`ubuntu-24.04`, `ubuntu-24.04-arm` and `windows-latest` (the installer, packaging and release-script flows on the two
Linux legs, and `systemd-analyze verify` of the units there); `ci · workflows` runs actionlint and shellcheck of
`install.sh` and `.github/scripts/`; `release.yml` runs the same three executables and the same smoke on every leg of a
`daemon-v*` tag before anything is packed; `ci · family checks` runs the shared plan, pin, adapter
and build-flags checks. The live smoke on the owner's machine runs at every release (E4 onwards) — `install.sh` for real
and `POST_DEPLOY.md` against the installation — and so does the live contract with `WSL_CARE_REQUIRE_LIVE=1`
(§ *The live contract*).
