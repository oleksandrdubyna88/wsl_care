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
| AOT smoke | `.github/scripts/smoke-daemon.sh` (run by `ci-daemon.yml` on every pull request leg and by `release.yml` on every release leg) | the Native AOT binary of each RID answers `--help` / `--version`, performs the configuration round trip, answers `status --json` (on Linux over the captured procfs tree, reporting its `MemTotal`), and records a full run (`collect --json` → one history line naming a run detail, `status --json` naming that run, `doctor --json`), and since E3.S2 previews EVERY action (`act <ids --help names> --preview --json` under a sandbox with root claimed: exit 0 and every id answered on Linux, exit 2 on Windows, no state written — never a destructive run), and since the E4 review answers `preview --all --json` with no docker reachable (the JSON parsed by Python, `schemaVersion` 1, every row `available: false` with its reason), and since E5.S0 `status --json` carries its `verdicts` (the sample and the full-run ids, every level one of the four) and the `productVersion` `--version` prints — both parsed by Python, because the encoder writes the `+` of `0.0.0+<sha>` as `\u002B`. After the smoke, every pull-request leg packs the release archive from that binary (`package-daemon.sh`), checks the leg's pair (`verify-release-assets.sh <version> <dir> <rid>`) and opens the printed path in a `shell: pwsh` step |
| Extension: unit, structure, bundle (E5.S1) | `src_vs_code/src/test` (`npm test`) | the runner seam, the client's argv / refusals / handshake over the golden contracts, the manifest, one-runner / one-argv-builder / one-verb-module over the parsed sources, the shipped bundle free of root, timer, confirm, manual and config words; every test process carries a tripwire against the real `wsl.exe` — § *The extension* |
| **Scenario, extension client** (E5.S1) | `src_vs_code/src/test/scenarios` | the real `WslCareClient` over the real runner seam against the strict fake `wsl.exe` (a Node script), flows derived from the client's verbs; the extension-host tier (`@vscode/test-electron`) is E5.S2's |

Shared doubles live in `src_daemon/tests/WslCare.TestSupport` (`TempRoot`, `SandboxHost`,
`RecordingCommandRunner`, `FixedTimeProvider`, `DirectoryLinks`, `AccessDenial` — a real access
denial on one directory, `chmod 000` on Linux and an inherited `icacls` deny for the current user's
SID on Windows, lifted on dispose and reported as unavailable when it does not take — `TerminalText`
— stderr with the console sink's colour removed and whatever control characters are left — and
`ChildProcess` — the one launcher the process-level tests share: argv list, 30 s ceiling — or, since 2026-10-05, a
`ProgressWait`: killed after a silence, or at a cap, which every scenario CLI run uses — the whole tree killed on timeout,
UTF-8 streams — and since E3.S1 `HostileInputs` — the seeded generator of the command-policy
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
Release on every leg — `ubuntu-24.04` (`linux-x64`), `ubuntu-24.04-arm` (`linux-arm64`),
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
| The never-list, as a table per OS family: inside the root allowed; outside the root, an agent folder, `~/git`, Claude's temp folder, a too-broad root each refused by the NAMED rule; the archive permit allows a move out of an agent folder and nothing else; `projects/*/memory/` is never moved even with the permit; a move's destination is judged; a refusal names the action and the path; a folder that HOLDS a protected root (agent, repositories, Claude temp) is refused under that root's rule for a delete and a move, while a sibling whose name merely starts like it is not | `WslCare.Core.Tests/Files/DeletionPolicyTests.cs` |
| Over a real disk: `..` traversal out of the root is refused before anything is deleted; a link inside the root into an agent folder is refused (through it, and the link itself); an atomic write replaces content and leaves no temp file, and is refused outside its root before writing; a move inside the root is performed; missing is not unreadable; 40 parallel appenders produce 40 whole lines; a held lock makes an append time out; a reader holding the history open never blocks an append, and an atomic replace waits out a reader that holds the file for a moment (Windows refuses a rename onto an open file even when the reader shares delete). **The flaky Core run of 2026-10-03, found under load**: `RunRetentionTests.An_append_that_arrives_while_the_history_is_being_rewritten_lands_in_the_new_file` failed 2 of 4 runs with 24 CPU burners and 2 of 10 with 8 (`IOException: The process cannot access the file '…history.jsonl' because it is being used by another process`, from `PhysicalFileSystem.AppendLine`) — retention reads the history again right after its rewrite releases the lock, and `File.ReadAllBytes` shares read only, so on Windows a racing run's append lost its line; the engine's heartbeat test failed the same way once (`beaten` stayed `0001-01-01`: its raw read of `running.json` threw mid-replace). **Red** (deterministic, before the fix): `Did not expect any exception because a reader of the history must never make a run lose its line, but found System.IO.IOException: The process cannot access the file …`; **break-it** of the rename wait (`ReplaceRetryFor` = 0): `System.UnauthorizedAccessException : Access to the path is denied`; green after `ReadFile` opened with `FileShare.ReadWrite \| FileShare.Delete` and the atomic rename waits up to 2 s on Windows. Proof: the retention test 0 failures in 24 loaded runs (8 burners) after the fix, the whole Core suite green in 12 of 12 loaded runs once the runner's wall-clock tests ran alone (below). **The win-x64 CI flake of 2026-10-06** (main, `ci · daemon`, two attempts, a different test each): the atomic-replace test released its reader from a pool task 200 ms later, and on the loaded runner that task got no thread inside the 2 s wait (`System.UnauthorizedAccessException : Access to the path is denied` from `MoveReplacing`). The product was right — it already waits out the access refusal; the TEST's timing was the fault. Now the product reports each retried refusal as `AtomicWriteStep.ReplaceRefused`, and the test lets its reader go at the first one, on the writing thread: no second thread, no timing. On Windows it also asserts the refusal happened (the retry path is what it exercised), on Linux that none did. **Red** (deterministic, the old test under an exhausted thread pool — 400 blocked work items for 4 s): 3 of 3 failed with the CI message; **green** 5 of 5 under the same exhaustion; **break-it** (`IsHeldOpenOnWindows` false): `System.UnauthorizedAccessException : Access to the path is denied` | `WslCare.Core.Tests/Files/PhysicalFileSystemTests.cs` |
| Fail closed: with the real link reader for every component but one, which throws what an access denial throws, a delete, a directory delete, a move (source or destination), an atomic write and a declared root through that component are each refused by `Unresolvable`, naming the component, and nothing is deleted, moved or written; the same seam still allows what does not pass through it; a REAL directory this account was denied (`AccessDenial`) refuses a delete under it as `Unresolvable` rather than letting the policy allow it and `File.Delete` throw | `WslCare.Core.Tests/Files/UnresolvablePathTests.cs` |
| The atomic write writes where it was ALLOWED to: a directory moved out of the declared root and replaced by a link to it — after the target was approved, or after the temporary file was written — is refused (`OutsideDeclaredRoot`) and the file outside keeps its old content; a target replaced by a link to a place INSIDE the root after the temporary file was written is refused as `PathChanged` and the temporary file removed; the temporary file is created in the target's RESOLVED parent, not beside the spelled path. The swaps run inside the file system's own step callback, at the moment a racing process would act | `WslCare.Core.Tests/Files/AtomicWriteRevalidationTests.cs` |
| The real runner: exit code and both streams captured; a missing executable is `FailedToStart`; a timeout kills the WHOLE tree (the grandchild's pid is observed dead) and reports what was captured; output past the cap is cut and marked; the caller's cancellation surfaces as `OperationCanceledException`, not as a timeout; a refusing policy prevents the start entirely; a request needs a positive ceiling. `StreamAsync` (gate finding #1/#5): a callback that throws, and the caller's cancellation, each leave the streaming child DEAD when the exception arrives (killed and reaped in a `finally`) and its grandchild gone (observed by pid); a child writing 1.6 MB to stderr while streaming 2 000 stdout lines runs to its end, its stderr capture cut at the cap. **Teeth** (the kill was already there, so a break-it): with the finally's kill removed, `Expected IsAlive(pids!.Value.Parent) to be False because the streaming child 29756 is killed AND awaited before the exception propagates, but found True` (and the same for cancellation); with stderr not drained, `Expected type to be WslCare.Core.Processes.CommandOutcome+Exited, but found WslCare.Core.Processes.CommandOutcome+TimedOut` at the 45 s ceiling; restored, green. **Wall-clock tests, run alone since 2026-10-03** (the non-parallel `WallClock` collection of `WslCare.Core.Tests`): under load the shell loops outran their budgets (the 1.6 MB stderr child its 45 s, the 3 000-line cap child its 60 s, the PowerShell parent printed no pid inside the 6 s ceiling) — 4 failures in 9 loaded runs; the budgets are now 3 min / 3 min / 20 s (tree kill under 60 s), and 12 of 12 loaded runs passed | `WslCare.Core.Tests/Processes/ProcessCommandRunnerTests.cs` |
| The product decides which file a tool name means, on `PATH` alone: the first absolute entry holding it wins and is returned as a FULL path; a name on no entry is not found with a reason naming it and the count searched; `.`, a relative entry and an empty entry never stand for the current directory (a decoy reachable only through them is never chosen); an absolute path passes through, a relative one is refused; on Windows only `.exe` / `.com` are candidates, never a `.cmd` / `.bat` that would need a shell; on Linux a file without an execute bit is skipped for the next entry; the runner turns "not found" into `FailedToStart` saying PATH was searched | `WslCare.Core.Tests/Processes/ExecutableResolverTests.cs` |
| **The Windows system drive fallback, resolver and launcher** (live finding 2026-10-04, plan §17 #1, §17a): under the PATH systemd gives a service `powershell.exe` resolves to its folder on the mounted drive, flagged as found there; a copy on PATH still wins and nothing of the drive is asked; a name outside `WindowsSystemDrive.Programs` is PATH-only and nothing of the drive is read; Windows never consults it; no interop → refused naming interop (the file not inspected); a mount point somebody but root could change, a file the checks refuse, no mounted drive → each refused with its reason; a share that does not answer → a named refusal within the ceiling (200 ms in the test), never a hang; the caller's cancellation ends the wait as a cancellation; the product's own `Resolve(name)` consults this machine's drive, never the PATH-only stand-in (every Linux leg); the product policy allows the bare argv and refuses the resolved absolute one; the launcher, asked for `powershell.exe`, starts exactly the file the lookup returned, reports it as `StartedFrom`, and the action's command record ends `(started from <path>)`; a PATH find reports none. **Observed red first** (Linux, the resolver ignoring the drive): `Expected type to be …ResolvedExecutable+Found because the resolver answered NotFound { Reason = powershell.exe was not found on PATH (5 directories searched, executable files only) }` — the live symptom word for word. **Break-its** (Linux container, one production line reverted each, then restored): interop unchecked → `…answered Found { Path = /mnt/c/Windows/System32/WindowsPowerShell/v1.0/powershell.exe, OnTheSystemDrive = True }`; the launcher not reporting → `Expected outcome to be "/bin/true" … but ""`; the ceiling removed, `Resolve(name)` wired to PATH-only, the fallback removed — each red, see the PR | `WslCare.Core.Tests/Processes/SystemDriveResolverTests.cs` |
| **Where the system drive is mounted, and whether interop runs** (`/proc/self/mountinfo`): the unedited observed C: line among the table's other lines (root, driver share, a network drive, D: stacked twice, Docker's folder mount of C:) → `/mnt/c`, device 0:159; WSL 1 `drvfs` with source `C:\` or `C:`; a manual `mount -t drvfs C:` (`path=C:`); a 9p line whose source says `C:\` but whose `path=` names `D:\`, and a non-drvfs `aname` → refused; an automount root holding a space (`\040`); stacked lines for one mount point agree and the top one gives the device; two DIFFERENT mount points → refused naming both; a bound folder of the drive is not the drive; no mount → refused naming `/proc/self/mountinfo` and what is at `/mnt/c`; WSL's virtiofs mode (constructed, not observed) → refused naming the virtiofs mount found there; a relative mount point never used; interop `enabled` under `WSLInterop` or `WSLInterop-late` runs, disabled or unregistered is refused naming interop. **Break-its**: the 9p line judged by its source label → `Expected … Unavailable …, but found … Available`; the first of two mount points taken → the same; the escapes not decoded → the space root not found | `WslCare.Core.Tests/Processes/WindowsSystemDriveTests.cs` |
| **What is checked before a file on the drive is started** (Linux, real files; Windows answers that the drive is the distro's): a regular executable file with `MZ` on the mount passes; no `MZ` header, a symbolic link on a component BELOW the mount point (`System32`) or as the file itself, a missing file, no execute bit, a FIFO under that name (refused in milliseconds, never waited on), a file on another device than the mount → each refused with its reason; `/`, root-owned 755, passes and a world-writable ancestor (`/tmp`) is refused (a deeper positive path is not portable: GitHub's ubuntu image ships `/usr/share` as 777, observed 2026-10-04). **Break-its**: the component link check removed → `Expected string "" to contain "System32 is a symbolic link"`; the MZ check → `"" to contain "not a Windows program (no MZ header)"`; the device comparison → `"" to contain "not on the drive's mount"`; the ancestors' mode check → `"/tmp/anything does not exist" to contain "writable by its group or by others"`; the descriptor's type check → `NotSupportedException : Stream does not support seeking` (the FIFO reached the read) | `WslCare.Core.Tests/Processes/SystemDriveFilesTests.cs` |
| The register and `default.json` name the same keys; every default validates; the plan's defaults are the shipped ones; a value round-trips through JSON | `WslCare.Core.Tests/Config/ConfigSchemaTests.cs` |
| A layer flattens to dotted keys with the line of every leaf; a syntax error reports its line in one sentence; a non-object is malformed; comments, trailing commas, a BOM and `$schema` are accepted | `WslCare.Core.Tests/Config/ConfigDocumentTests.cs` |
| One validator for file and command line: bool spelling, int range with the range in the message, enumerated text exactly, free text, comma-separated lists; a wrong JSON shape names the offending value | `WslCare.Core.Tests/Config/ConfigValidationTests.cs` |
| Precedence default < machine < user with the layer named per value; an unknown key, an out-of-range or mistyped value, a file that is not JSON, an unreadable file, an object where a setting's value belongs (`{"auto":{"A4":{}}}`) each make the result observe-only with file and line while the same file's valid keys still apply and no default re-enables anything | `WslCare.Core.Tests/Config/ConfigLoaderTests.cs` |
| `set` writes the key nested and the loader reads it back from the user layer; other valid keys are kept; invalid keys are dropped and named; an unparseable file is moved aside with a UTC stamp; two repairs in the same second (frozen clock) keep BOTH broken files — `…Z` and `…Z-2`, neither overwritten — and both succeed; `reset` removes a key and says whether it was there; a missing file becomes `{}`; no temp file is left; a repair that discards an unparseable layer or drops another invalid entry pins `dryRun = true` in the user layer (over a machine `dryRun = false`), correcting only the invalid key itself pins nothing; a `set` the loader would refuse leaves a broken layer where it is, nothing moved aside; a write that fails after the move aside — with ANY exception, not only I/O — puts the broken layer back; a lossy repair by `reset dryRun` pins too; while the repaired layer is written the broken one is still in place (copied aside, not moved) — an empty one too; a kept broken file that appears under the chosen name meanwhile is never overwritten (created exclusively); a refused put-back is an `InvalidOperationException` naming the file, never swallowed; after a lossy repair the timer decides dry even 30 days past its first week | `WslCare.Core.Tests/Config/UserConfigWriterTests.cs` |
| A run id is the UTC second and the pid; a record is one camel-case line with string enums and `schemaVersion`; every outcome (`interrupted` included) round-trips; the writer appends one line per record under the state directory | `WslCare.Core.Tests/Records/RunRecordTests.cs` |
| No file outside `PhysicalFileSystem.cs` and `ProcessCommandRunner.cs` deletes, moves or starts a process; the scanner matches a planted instance formatted across lines; it still finds the sanctioned calls in each seam; it ignores words that merely contain the names; `WslCare.Core` references no package | `WslCare.Core.Tests/ArchitectureTests.cs` |
| Parsing: every help spelling; `--version`; unknown verbs named; near misses refused; extra words refused; control characters never reach a message; `config get` takes an optional key and `--json` in either order and refuses a second key or an unknown option; `config set` needs exactly key and value; `config reset` exactly one key; `status` takes nothing or `--json` and refuses anything else; `config` alone lists its sub-verbs; every registered command is in the help text and its example parses (derived from the register); the longest spelling wins when two commands share a prefix, whatever their register order (a synthetic register with the short spelling first) | `WslCare.Cli.Tests/CommandLineTests.cs` |
| The whole program in-process: `--version` prints the stamp only; `--help` lists the config verbs; an unknown verb exits 2 with one stderr line even with a newline in it; a cancelled token stops the run as a cancellation | `WslCare.Cli.Tests/ProgramTests.cs` |
| `config get` lists every key with layer `default` on a fresh host, one key alone, refuses an unknown key (exit 2); `config set` validates, writes, and `get` then shows `(user)`; an out-of-range value is refused with ONE stderr line and the file untouched; an unknown key or a mistyped value is refused and nothing is written; a broken user layer is reported observe-only on stderr and in the JSON (`configError` with file and line) while still answering; `set` repairs a broken layer and the next `get` is valid; a non-JSON layer is moved aside and the command says where; `reset` reports the effective value again; a `reset` of a key the user layer does not hold says nothing was removed (and a `set` of an absent key does not, nor a `reset` over an unparseable layer); a lossy repair says it switched the timer to dry run; the JSON report carries every key with value and layer | `WslCare.Cli.Tests/ConfigCommandTests.cs` |
| No control character reaches stderr raw: an unknown key holding a newline, a carriage return or a clear-screen escape (named fixtures, so a failing test name cannot clear the terminal) is refused by `config get` / `set` / `reset` in ONE message line with none of them left; an internal error whose reason holds them is one clean line; the console sink replaces a control character carried by a logged value or an exception message and keeps the line whole | `WslCare.Cli.Tests/ControlCharacterTests.cs` |
| The retro gate over PR #17: `doctor`'s versions line passes `CommandLine.Printable` like its checks — a docker version answer carrying an OSC 52 sequence reaches stdout without its ESC (red first: the ESC was printed); ten thousand `--process` keys parse in under 64 MB (red first: 402 MB, one list copy per key) | `WslCare.Cli.Tests/RetroPr17Tests.cs` |
| `Output` is the one road to a stream: every `.Write(`/`.WriteLine(` in the CLI's source is in `Output.cs` or the console sink (scanned across lines); the scan matches a planted instance, still finds the writes in both allowed files, and ignores `AppendLine`, `WriteTo`, `WriteStartObject`, `Format` | `WslCare.Cli.Tests/OutputRoadTests.cs` |
| Escapes on a redirected writer with a control; levels coloured differently and the message unquoted; a file per run that segments at UTC midnight into the next day's folder with the same pid and never rolls backward; retention selects only expired day folders, deletes through the seam, leaves today and strangers, is a no-op on a missing root, and refuses an expired folder that is a link into `~/git`; a log root that cannot be listed is one `Failed` entry, never an exception; starting the logger writes one file under the host's log directory at the configured level | `WslCare.Cli.Tests/LoggingTests.cs` |
| The BUILT `wsl-care` as a child process (through `TestSupport/ChildProcess`: 30 s ceiling, tree kill): `--help` exits 0 and lists the config verbs; an unknown verb exits 2 with one stderr line; `config set` / `config get --json` / a refused `set` read and write under `WSL_CARE_ROOT` and each run leaves its own log file there; `--help`, `--version` and a refusal write nothing under the root | `WslCare.Cli.Tests/BuiltBinaryTests.cs` |
| The procfs parsers over the captured tree: `meminfo` sizes are KiB → bytes and counts (`HugePages_*`) are never sizes, a missing line is unavailable naming the key; `buddyinfo` per zone, free blocks of order ≥ 4 / ≥ 7 in zone Normal and their bytes, a zone with none (the 2026-10-01 dump, synthetic line) is a measured 0, an unlisted zone unavailable; PSI `some`/`full` avg10/60/300 + total, a missing `full` unavailable, a malformed `some` unavailable; clock tick and page size from `self/auxv` (100 / 4096), none when absent; `btime`; `status` → name, state, parent, uid, `RssAnon` + `RssShmem` (not `VmRSS`), kernel threads by `Kthread` or no `RssAnon`; `stat` counted from the last `)`; container membership by cgroup path in both Docker layouts; `cmdline` split on NUL, secret-looking values redacted in `--x=v` and `--x v` forms, cut to 200; container cgroups with `memory.current` and `memory.stat` anon + shmem, a missing mount unavailable, an empty one an empty set; `passwd` | `WslCare.Core.Tests/Collectors/ProcfsParserTests.cs` |
| Who holds it: the remainder is AnonPages + Shmem − processes − containers (anon + shmem, never `memory.current`); a negative one is an inconsistent sample with its overshoot, never a negative number; zero is a remainder; an unread input leaves it not computed with the reason; process memory is `RssAnon` + `RssShmem` (a 5 000 kB `RssFile` is not held); a container member visible in `/proc` is counted ONCE, through its cgroup; a member whose container cgroup cannot be read is counted as a process, so still once; on the captured tree the remainder is positive and `memory.current` would have made it negative | `WslCare.Core.Tests/Collectors/AttributionTests.cs` |
| The process table over the captured tree: 51 counted, the top 30 ranked by held bytes (pylance first); owner, parent, state, CPU seconds and age from `btime` + `starttime` / tick, cwd through the link, the shown command line, tty; an unreadable cwd is unavailable with a reason; families in catalogue order (agent before vscode-server) and every process in exactly one; `/mnt/` walkers by cwd or argument; orphaned = parent pid 1 or `systemd --user`; a pid that vanished is counted apart; no procfs → unavailable; no tick → age/CPU unavailable, memory still read; cancellation stops the walk | `WslCare.Core.Tests/Collectors/ProcessCollectorTests.cs` |
| The probes and the report shape: the Linux probe over the captured tree reads memory, 10 containers, 51 processes, a remainder, `df` of its root, and names the Windows binary for the host figures; over an empty root every section is unavailable with its path and the JSON carries no value key (never 0); an unavailable figure is written with `available: false` + reason and no `bytes`; `schemaVersion`, side and sample time; the `.vhdx` figure is named and unavailable; an inconsistent sample is a state with the overshoot; the Windows probe reports host RAM, the system drive and `vmmemWSL` and names the Linux binary for the VM; the real `GlobalMemoryStatusEx` answers (Windows only); `df`'s Use% leaves the reserve out | `WslCare.Core.Tests/Collectors/ProbeTests.cs` |
| Slow parts from the last full run: none before any run (with how to record one); `docker stats` back with run id and age; a newer run with no slow parts does not hide an older sample and a torn line is skipped; a newer run that FAILED to sample is the answer, with its reason and run id; a pre-E2 history line writes no `slow` key and reads as not sampled; the parts round-trip through the compact context | `WslCare.Core.Tests/Records/LastFullRunTests.cs` |
| The read-only queries the collectors added to the file-system seam: a directory link answers its target, a directory or an absent path is not a link, a link that cannot be inspected is unreadable (fail closed, as the deletion policy); the volume holding a directory is measured in one call; a volume that does not exist is unreadable | `WslCare.Core.Tests/Files/ReadOnlyQueriesTests.cs` |
| `status [--json]` in-process over the captured tree: the JSON answer with `schemaVersion`, the fixture's figures, the host named as the other binary, no slow part yet — and the recording command runner received NOTHING; the slow parts of a recorded full run come back with their age, still with nothing started; the text form's lines (ASCII only); a broken configuration layer is named (`observeOnly`, `configError`) and status still answers | `WslCare.Cli.Tests/StatusCommandTests.cs` |
| The harness's own fakes: a bare `docker` looked up by a real shell on the scenario `PATH` reaches the fake, which records its argv and prints the scripted fixture byte for byte with the scripted exit code and stderr; each of `docker`, `systemctl`, `journalctl`, `powershell` (installed as `powershell.exe` on Linux, the name WSL interop uses) answers as itself, records argv exactly (spaces included) and refuses an unscripted call (98); `git` is NOT reachable on the scenario `PATH`; a fake started outside a scenario refuses (97) | `WslCare.Scenarios/FakeToolFlows.cs` |
| The harness's wait on progress (`TestSupport/ProgressWait` through `ChildProcess`, every OS, the child a fake scripted to sleep): a child whose mark keeps changing runs past a 1 s silence to its own exit; a silent one is killed at the silence (*… made no progress for 1 s*) long before its cap; one that progresses for ever ends at its cap (*… did not exit within 3 s*); a ceiling and a progress wait together are refused; a scenario CLI run that calls no fake for its silence is killed for it, though the product would have cut the hung fake at its own ceiling and finished; a fake call grows the scenario's mark, and the scenario's silence is 30 s and its cap 5 min | `WslCare.Scenarios/ProgressWaitTests.cs` |
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
| `status`'s verdicts (E5.S0, plan §15g B1): the eight thresholds a fast sample decides are `ThresholdRules`' own, evaluated over the sample with the effective configuration (`basis.source: sample`, the sample's instant, age 0) — the 2026-10-01 evening critical on fragmentation, warn on page cache and inactive anon, `memory.available` unknown because `MemAvailable` is not in the file; a fresh boot ok on all eight; a warn threshold of 70 % set in the user layer turns 60 % available from ok to warn; the ids and their order equal `Evaluate`'s (what `collect` writes); every other id is the newest full run's record unchanged with its run id, its end and its age — a recorded memory verdict is NOT carried, the sample re-judges it; with no full run every full-run id is `unknown` with `LastFullRun.NoFullRunYet` under the limit the configuration puts in force (`npm.maxCacheGb` 9 → `warn > 9 GB`); a run that recorded no verdict for an id leaves it unknown naming the run | `WslCare.Core.Tests/Status/StatusVerdictsTests.cs` |
| The newest FULL run's verdicts read back: a `collect` line naming its detail is a full run, a later `act` line is not; the verdicts, run id and end come from the detail; no full run → `NoFullRunYet`; a full run whose line names no detail is passed over for the newest that does; a named detail that is gone, or one that does not parse, is a reason naming the file; an unreadable history is its own reason | `WslCare.Core.Tests/Status/FullRunVerdictsTests.cs` |
| `status --json` in-process (E5.S0): `verdicts` with `memory.available` ok at 66.8 % over the captured tree (basis `sample`) and `clock.jumps` unknown with no full run, nothing started; `productVersion` equals what `--version` prints; `config set thresholds.memAvailableWarnPercent 70` turns it warn with `warn < 70 %`; the text form's ONE `verdicts:` line; after an in-process `collect`, every carried verdict equals the detail's own record with the run id, its end and 1 800 s of age, and the ids equal the detail's in order | `WslCare.Cli.Tests/StatusCommandTests.cs`, `WslCare.Cli.Tests/FullRunCommandTests.cs` |
| The golden contracts (E5.S0, plan §15g m7): the checked-in `contracts/golden/head/*.json` are what the BUILT CLI answers at this commit, normalised; every rule of the normalisation list still matches a value; the normaliser replaces exactly what its rules name with a value of the same type, keeps a fixture pid, rewrites the sandbox root and run ids inside sentences; the first difference names the line and both texts | `WslCare.Scenarios/GoldenContractTests.cs` (the drift test on the Linux legs) |
| The health collectors over the answers CAPTURED 2026-10-02 (`fixtures/health`): the failed unit, the journal's oldest entry, 875 clock changes, one order-7 allocation failure, NTP synchronised, systemd 255, `wsl-pro` enabled, no disabled snap; journalctl's exit 1 with nothing printed is 0 matches, an error is not; disabled snap revisions (synthetic Notes); discard, the automount root, a Windows profile seen as `/mnt/c/Users/user`, `.wslconfig` in either section; the Windows clock offset with the launch latency subtracted; on the Windows layout the distro parts name the Linux binary; a missing tool leaves only its part unavailable; read verbs only | `WslCare.Core.Tests/Health/HealthTests.cs` |
| The daily folder walk: a tree is the sum of its files; a link inside is neither counted nor entered, a folder that is a link is not walked; bin/ + obj/ only, node_modules never entered; a walk at its entry limit says its figure is a lower bound; a missing folder is missing, not 0; once a day (19 h no, 20 h yes); the npm / apt / disabled-snap figures | `WslCare.Core.Tests/Folders/FolderSizesTests.cs` |
| A8 and A9 from the newest folder sample with its run and age in the basis; unavailable with the reason before one exists, or when the run could not measure the folder | `WslCare.Core.Tests/Docker/FolderRowsTests.cs` |
| The follower's rules (plan §15b #0, #8): the wait for Docker 5 → 10 → … → 300 s; a buffer that reaches back past the last marker fills the gap and records only newer starts; a daemon restart, an empty buffer, a marker older than 24 h and the first start ever are each ONE gap with its reason; a full day of coverage counts complete with the top images; a count overlapping a gap is partial and names it until a whole 24 h lies after its end; a follower that stopped recording, and nothing recorded before the window, make it partial too; the last coverage. **The continuity rule** (gate finding #2/#7/#9; the 3-argument cases above are now the engine-unknown fallback): an idle engine with an empty buffer and no restart is covered with zero starts; a host that slept with the same engine after is covered; a FULL buffer (≥ `FullAt`) whose oldest event is newer than the marker is ONE gap up to that event, and one that still reaches past it is covered; an engine restarted after the marker is a gap from the marker to its start (also into an empty buffer) and the starts after are recorded; a different engine claiming an older start proves nothing beyond its oldest event; an unreadable engine falls back with its reason; a first start on an engine older than the window proves the whole window, one started inside it is a gap up to its start; the capacity is 256 and `FullAt` reads every measured full answer (248) as full; a `covered` marker round-trips its engine and `LastEngine` takes the newest that recorded one; a fresh 24-hour summary answers its window and a stale one is partial with the open gap. **Teeth** (the rule was written before these tests in this session, so a break-it): with the engine evidence ignored (`EngineEvidence.Unknown` passed through) nine failed for the real symptom — `Expected object to be <null> because an idle engine that did not restart dropped nothing, but found WslCare.Core.Events.CoverageLine+Gap`, the same for the slept host and the first start on an old engine, `… to contain "restarted"` / `"full"` / `"different Docker engine"`; restored, green | `WslCare.Core.Tests/Events/CoverageTests.cs` |
| The follower over a scripted docker and a moved clock: down three times then up → waits of 5, 10, 20 s, ONE gap for the outage, the backfilled and the live start, the stop marker carrying the live coverage; a buffer that still covers the outage writes no gap; a segment ending at its `--until` is a covered marker and the next resumes from it; a broken stream goes back to waiting and still writes one gap per outage; `--once` with Docker down returns at once, writes only its markers, read verbs only; day files past 14 days pruned through the seam; an idle engine whose bridge matches the last marker's answers an empty buffer and the catch-up writes no gap, reads the engine ONCE after the events, records it on the new marker and writes the 24-hour summary; an engine restarted since the marker is ONE gap up to its start even over an empty buffer | `WslCare.Core.Tests/Events/EventsFollowerTests.cs` |
| `doctor`: an installation doing its job is healthy and names the wsl-care / docker / systemd / kernel versions; a stale last run, a stopped unit and a silent follower are each a named problem; a fresh machine says nothing was recorded and creates nothing; an invalid layer is a problem and doctor still answers; read-only questions only | `WslCare.Core.Tests/Doctor/DoctorTests.cs` |
| `collect` / `doctor` / `events follow` in-process: `collect --json` records and `status` then reads that run's slow parts with their age and counts the never-run follower as partial; the text form; read-only exits 0 with the stderr note and no state directory; an unwritable history line exits 1; a held `run.lock` exits 75; `doctor --json` exits 0 with its verdict; `events follow --once` with Docker down exits 0 naming why; by an unprivileged process exits 1; a second follower exits 75; stopped by a signal exits 0 with its stop marker; an unprivileged run logs to the user's log directory; `collect` logs that it is measuring BEFORE the first tool is asked anything, and through the real logger the line reaches the console (stderr) and the run file (retro gate over PR #5); `status` answers the follower's 24-hour count (200 backfilled starts) from `starts-summary.json` and neither lists the day folder nor opens a day file (a read-recording file system; gate finding #8). **Observed red first** (2026-10-02, unfixed `StatusCommand`): `Expected reads.Paths … to not have any items matching p.StartsWith("…\wsl-care\container-starts", OrdinalIgnoreCase) … but found {"…\container-starts", "…\container-starts\2026-10-02.jsonl"}`; green after the fix | `WslCare.Cli.Tests/FullRunCommandTests.cs` |
| Docker's event stream and the health tools for real: failed units and boots as JSON, journal searches (one matching nothing answers 0), `timedatectl`, `systemctl --version`, `UnitFileState`, `snap list --all`, the Windows clock probe through interop, an unfiltered past window, and a FUTURE `--until` that streams and closes by itself | `WslCare.LiveContract/HealthContractTests.cs` (run by hand; § *The live contract*) |
| The clock probe as the TIMER starts it, live: on a WSL distro (decided by the interop entry, not by the code under test) the drive MUST be found; resolved with the service's PATH as an argument it is found on the drive; with this process's PATH narrowed to the service's, the probe through the product launcher answers two instants and a profile and reports the file it started — a non-parallel collection, PATH restored in `finally`, skipped with its reason off WSL. **Teeth, live in WSL 2026-10-04:** with `Resolve(name)` wired to the PATH-only lookup it failed with `powershell.exe could not be started: not installed, or not on PATH (powershell.exe was not found on PATH (5 directories searched, executable files only); the Windows system drive was not searched: this lookup consults PATH alone)`; restored, green | `WslCare.LiveContract/ServicePathContractTests.cs` (run by hand; § *The live contract*) |
| The never-list rule by rule, DERIVED from `NeverList.Rules` (a rule without a known instance is red): each refuses its instance and names itself; harmless argv (`vm.drop_caches=1`, `--vacuum-time=30d`, a volume id, `du` of `/var/cache/apt`) break no rule; 22 spellings of never-commands (`RM.EXE`, `/usr/bin/rm`, `find -delete`, `git clean`, `docker system prune` without `-a`, `sh script.sh`, `pwsh -File`, `wsl --unregister`, a `--grep=` path under `.claude`, a Windows `Temp\claude` path, `runuser -l`, `runuser` without `--`, a full-path `runuser`, `taskkill /IM`) refused; deny by default; a planted template cannot admit `docker system prune -a` (the never-list is asked first); a never-command behind `runuser` is refused even with a planted user template for it; the clock probe is the one PowerShell argv; EVERY read command the collectors build — each factory, a 1- and a 100-id inspect, every unit `systemctl show` reads, the three journal searches — is allowed by the product policy; a hostile or short container id is not a declared instance; a runner cannot be built without a policy; no product file but the runner names its unguarded test seam (with the companion that the scan finds the definition). **Found by this table, not by the fakes:** the journal search's `--unit=systemd-resolved` was refused (a unit name without `.service`), which on Windows had stayed invisible because the clock-jump count is a Linux-only assertion — the scope slot now takes a bare service name | `WslCare.Core.Tests/Processes/Policy/CommandPolicyTests.cs` |
| **The property test** (seeded, 20 000 requests; 500 instances per template; 2 000 action cases): no argv the product policy allows is a never-command by the independent `NeverOracle`, and every one it allows matches a declared template (with vacuity guards: > 10 % of the inputs never-commands, > 10 % allowed template instances); every declared template instantiated with values its slots accept is never a never-command; every registered action, previewed and run over generated configurations (`journal.keepDays`, `dryRun`) and generated journals with hostile file names, asks only for argv the policy allows, of its OWN templates, none a never-command. Companions, each red by design and kept: a permissive policy (`ALLOWED a never-command: git worktree prune`), a never-list-on-the-outer-argv-only policy (undeclared argv AND `ALLOWED a never-command: runuser -u me -- …/dash -c x`), a planted `vm.drop_caches=<0..3>` template (named by the template property; still refused at run time), a planted action that builds `sh -c "rm -rf <preview name>"` | `WslCare.Core.Tests/Processes/Policy/CommandPolicyPropertyTests.cs` (+ `NeverOracle.cs`, `TestSupport/HostileInputs.cs`) |
| The engine over the distro's layout in a temp root with scripted actions: the fixed execution order whatever the request order, `running.json` naming each action while it runs and gone after, detail then history line with per-action status and freed bytes; a throwing action and a failing one recorded `failed` while the next ran and the run `completed`; the timer honours each `auto` switch and each trigger, a button neither; the timer dry for 7 days from its first action pass with `dryRun` off (the last minute dry, the minute after ran; `wouldFreeBytes` on the line), dry while `dryRun` is on, never for a button, a recorded start never moved, an unreadable stamp restarting the week; a heavy action deferred at 87.5 % CPU (5-minute load 3.5 over 4 CPUs) and while `dotnet test` runs, run when idle, the deferral recorded; a timer-only rule not holding back a button; an unread CPU figure deferring; a dead pid's `running.json` swept with an `interrupted` line naming the action it was on and the last heartbeat; a reused pid (different start) swept as mismatched; a live pid with a 31 s heartbeat WEDGED, nothing run, the file byte-identical, no history; a live fresh one busy though the lock was free; an unparseable file or one missing fields refused; a dead run that had recorded itself removed without a second line; a held lock → busy, nothing written; a `collect` started inside an act's run → `busy` (one lock for both); a wedged holder of the lock reported wedged; a signal mid-run → `interrupted` recorded, the next action not run, `running.json` gone, the cancellation rethrown; the heartbeat rewriting `running.json` on its own while an action is busy; a preview taking no lock and writing nothing; a user-scoped action refused for an ambiguous target while A10 ran; with the user layer NOT read (no single target user) the timer skips every action, naming why, while a button still runs (retro gate over PR #7); the other side's action and a preview's refusal named; an invalid configuration layer → every action skipped, outcome `observeOnly` | `WslCare.Core.Tests/Actions/ActionEngineTests.cs` (+ `ScriptedAction.cs`) |
| A10, the reference action, under the product policy: the preview counts only ARCHIVED files older than the limit (not the active `system.journal`, not a 5-day-old archive, not a stray `.txt`) and carries journald's own size; the trigger fires above 1 GiB and not at exactly 1.0 G or at 900 M; an unreadable size is no preview with the reason; the freed bytes are the before-sizes of exactly the files that are gone (3 000 — not the preview's 5 000), before / after totals beside them, the two commands in order; a failing vacuum is a failure in journalctl's words, still measured; through the engine a button press runs it and records `ran` | `WslCare.Core.Tests/Actions/JournalVacuumTests.cs` |
| The target user, as a table: `wsl.conf`'s default (quoted, spaced, after another section, `root`), else the single login account (root, a nologin daemon and `nobody` ignored); two login accounts, a default user `passwd` lacks, an invalid name, a relative home → ambiguous with the reason; none → none; an unreadable `passwd` refuses. A tool run as the user: `runuser -u me -- <the FIRST bin folder's file> args`, a clean environment of exactly `HOME`, `USER`, `LOGNAME`, `PATH` (the fixed folders), allowed by the policy; nvm's default resolved to the highest installed match (`22` → `v22.11.0`) and an unresolvable alias (`lts/*`) left out; a tool in no bin folder refused before any start; the policy refusing a wrapped file outside the bin folders (`/tmp/evil`, `…/.local/bin/../../git`, `~/git/bin`, relative), an inherited environment, a machine template behind `runuser` and a user template without it; every login account's `git` and agent folders protected by the deletion policy, not only `$HOME`'s | `WslCare.Core.Tests/Actions/TargetUserTests.cs` |
| The parts: number / user-name / text slots at their edges and against hostile values; binding names the slot and refuses extra values; a catalogue refuses a pathed executable and a repeat that is not last; the action ids are exactly the `auto.*` keys and the execution order holds each once (A5 → A4 → A6 → A7 → A8 → A9, A1 → A2); id lists known and unique, an unknown id refused naming the legal values; root is the OS's answer and only a sandbox may claim it; the build detector; which idle rule waits for which trigger | `WslCare.Core.Tests/Actions/EnginePartsTests.cs` |
| `act` in-process: the parse (no action, a flag first, neither or both of `--preview` / `--confirm`, a repeated or unknown flag, an unknown id, a duplicate id → usage); an unprivileged `--preview` or `--confirm` exits 77 with NOTHING touched — no state directory, no lock file, not one read command; a preview prints the live preview as JSON and writes nothing; a confirmed act records, answers `recorded` with the measured result and ran exactly disk-usage then vacuum; the text form; a failed action exits 3 naming it with the run recorded; a held lock exits 75 having run nothing; a wedged run exits 76; an action this build does not hold, and A10 asked of the Windows binary, exit 2 by name; an invalid layer exits 78 with nothing run; the help names the verb and the actions this build holds; a confirmed act logs what it runs before the first tool is asked, a preview does not (retro gate over PR #7) | `WslCare.Cli.Tests/ActCommandTests.cs` |
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
the `Install*Flows` classes assert what it did — `InstallFlows` (a fresh install: the release, the checksum, the machine
layer, the verify steps, a dry run, the preflight refusals), `InstallAttestationFlows`, `InstallUpgradeFlows`,
`InstallUninstallFlows` and `InstallDefaultUserFlows`, one class per feature since 2026-10-05 (they were one file of 1,140
lines, over the 800-line limit), sharing `InstallChecks`:

| Part | What it is |
|---|---|
| `root/` | `WSL_CARE_INSTALL_ROOT`, the stand-in for `/`: every path the script reads or writes as a file sits under it. Seeded with `/run/systemd/system` (systemd booted), an `/etc/passwd` with `alice` and `zed`, and `/etc/default/sysstat` with `ENABLED="true"` |
| `fakebin/` | the fake tool (`WslCare.FakeTool`) as `curl`, `gh`, `systemctl`, `apt-get`, `debconf-set-selections`, `dpkg-reconfigure`, `runuser`, `sudo`, `id`, `uname`, `sar`, `atop` — everything that changes the machine, reaches the network, or answers who and where the script runs (so a test can be root, or arm64, without being either). A test leaves one out to stand for a tool that is not installed |
| `realbin/` | links to an ALLOWLIST of real text and file tools (`awk`, `cat`, `chmod`, `cut`, `grep`, `gzip`, `head`, `install`, `ln`, `ls`, `mkdir`, `mktemp`, `mv`, `od`, `readlink`, `rm`, `rmdir`, `sed`, `sha256sum`, `sleep`, `date`, `sort`, `tar`, `timeout`, `tr`, `wc`). `PATH` is exactly `fakebin:realbin`, so a script change that reaches for another tool fails here first. A world may link `awk` to a named one (`/usr/bin/mawk`, `/usr/bin/gawk`) — the snappy decoder is run under both |
| the scripted clock | since 2026-10-05, `UseScriptedClock()`: `realbin/date` and `realbin/sleep` become two POSIX sh scripts over one file — `date +%s` answers the second in it (start `ScriptedClockStart`, 2026-10-04T12:00:00Z), `sleep N` adds N and returns at once — so a wait's output and its refusal depend on the seconds the script SLEPT, not on how long its status calls took. Stricter than the real tools: any other `date` form and any `sleep` that is not one whole number exits 2 naming what it was asked (`ScriptedClockTests`). Used by the progress flow; the wall-clock ceiling flow keeps the real clock on purpose |
| the attestations | since the E4 review: every published release carries one attestation per signer — a bundle in Sigstore's shape (`AttestationBundles`: a self-signed certificate with the SAN and the Fulcio extensions, a DSSE statement naming the archive's digest), snappy-compressed (literals only), served at a bundle URL that the fake attestation API names (`bundle: null`, `&` written `\u0026`). The fake `gh` VERIFIES (`VerifiesAttestation`, `FakeAttestation`): `--cert-identity` exact, `--signer-workflow` a literal prefix, `--repo`, `--source-ref`, `--deny-self-hosted-runners`, the artifact's digest — gh's semantics as measured on gh 2.97.0. Every fake call records `HOME`, `GH_CONFIG_DIR`, `XDG_*_HOME`, `GH_TOKEN`, `GITHUB_TOKEN` (`WSL_CARE_FAKE_RECORD_ENV`), so whose configuration gh ran under is asserted. A captured cli/cli bundle (`src_daemon/tests/fixtures/attestation/`, 64 literal + 69 copy elements, bytes above 127) is served where the decoder's copies must be right |
| `tmp/` | the script's `TMPDIR`; every flow asserts it is EMPTY afterwards (the trap removed the temporary folder on success, on failure and on refusal) |
| the release | built per test with `System.Formats.Tar` from THIS repository's units and machine layer and served by the fake `curl` (a new answer option, `OutputFlag`: the fixture goes to the file after `--output`); its `.sha256` computed, or replaced by a test |
| the binary | a two-line shell stub that appends the path it was started as to `stub-invocations.log` and hands its argv to a fake `wsl-care` — so the installer's use of the ABSOLUTE path is observed, and `collect` / `doctor --json` are scripted; `doctor --json` is serialised by the product's own `WslCareJsonContext` from a `DoctorReport`, never typed by hand |

It runs as the test user, never root: a path that escaped the prefix would be refused by the operating system before it
changed `/etc` or `/opt`. Linux only (POSIX sh, GNU coreutils and tar): the two Linux CI legs run it; the Windows leg
skips each flow with that reason. On the owner's machine it ran in WSL `Ubuntu` from a copy of the worktree under
`/tmp`, built there (`dotnet build wsl_care.slnx -c Release -m:4`, then the Scenarios executable with
`--filter-class "*InstallFlows"`; since the split, `--filter-class "*.Install*Flows"` runs all five classes).

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

**The E4 review, part B — the release pipeline's correctness.** Each behaviour first written as a test and seen red
against the E4.S2 files:

| Guarantee | Red before the fix |
|---|---|
| B1 the archive path `package-daemon.sh` prints opens outside bash (Windows, Git for Windows' bash) | `Expected File.Exists(archive) to be True because the path the script prints (/tmp/wsl-care-test-package-path-…/out/wsl-care-0.1.0-linux-x64.tar.gz) is what release.yml hands to attest-build-provenance and upload-artifact, which are not bash, but found False` |
| B2 every pull-request leg packs, checks its pair and opens the path outside bash | `Expected collection {10, -1, -1, -1} to not contain -1` (publish found; package, check, path step absent) |
| B2 `verify-release-assets.sh` with named RIDs | `Expected value to be 0 because linux-arm64's own pair is complete: usage: verify-release-assets.sh <version> <dir>` |
| B3 no inert `exclude-paths` | `Expected config.TryGetProperty("exclude-paths", out _) to be False because inert: no exclude path can drop a commit the package path never received, but found True` |
| B3 a breaking change before 1.0 bumps the minor | `KeyNotFoundException` on `bump-minor-pre-major` (the setting was absent; the test now names it) |
| B4 the smoke previews with no docker | `Expected SmokeMarkers() to contain only items matching … smoke.Contains(m) … but {"preview --all --json"}` |
| B5 every workflow declares permissions | with a planted `zz-planted.yml` (a pull-request workflow, no `permissions:`): `… to be empty because a workflow without a permissions block gets the repository's default token … but found at least one item {"zz-planted.yml"}` — the per-job checks passed it before, vacuously |
| B6 the gating workflows are derived | with the same plant: `Expected checks… to be a collection with 7 item(s) because … a gating job left out protects nothing … contains 1 item(s) less` — the hand-typed list could not see the new file by construction |
| B9 the newest tag from compact JSON | `FAILED at step "resolve-release": no daemon-v* release found at …/releases?per_page=100` (the one-line answer had no newline, so the old line reader saw no line at all) |

B7 (POST_DEPLOY #2) is a checklist command, checked by `post-deploy-check.mjs`'s structure and by running its comparison
by hand (`0.1.0+eda332f` against `$TARGET` 0.1.0 → 0, 0.1.1 → 1). B8's refactors (complexity ≤ 4 in `WorkflowYaml` and
`ShippedFilesTests`, `ExecutableResolver` for the 7-Zip lookup, the TempRoot for a link's folder, the stale skip deleted)
change no behaviour: the reader's quoted forms gained a check that passes on the old reader and the new one alike.

`MemoryMax=1G` (A4) is a configuration decision, not a behaviour: `ShippedFilesTests.The_service_memory_ceiling_leaves_room_for_the_tools_the_cleanups_start`
holds the value; no red was needed for it.

**The shipped files** (`ShippedFilesTests`, every OS): each unit's `ExecStart` starts `/opt/wsl-care/bin/wsl-care` and
its argv is parsed by the CLI's own `CommandLine.Parse` — the timer's service to `Request.Collect { Timer: true }`
(§15d CI), the follower's to `Request.EventsFollow(Once: false)`; `SuccessExitStatus` equals `ExitCode.Busy`; the timer
is `OnCalendar` every 4 h with `Persistent=true`; no unit sets a sandbox directive that breaks a named action (its
companion: the same scan finds `NoNewPrivileges`); `install.sh`'s `UNITS` line equals the folder's files; the machine
layer loads VALID through the real `ConfigLoader` and sets nothing, and the example in its own comment loads valid as
the machine layer (the one positive beside the negatives). Since daemon 0.1.1 every key of every unit, and of every drop-in
`wsl-care units dropin` renders, must sit in the section systemd 255 reads it from (a key → man page table:
systemd.unit(5), .service(5), .exec(5), .kill(5), .resource-control(5), .timer(5); a key not in it is refused until it is
added with its page). `.github/scripts/verify-systemd-units.sh` (CI, Linux legs) reads EVERY file of the units folder
with systemd's parser — a template through an instance name (`wsl-care-act@20000101T000000Z-1.service`), each unit with
the drop-in the job's own Release build renders — and fails on any output: `systemd-analyze verify` exits 0 on an
unknown key, observed in `ubuntu:24.04` (systemd 255) with a planted `Persistant=`, and again on 2026-10-06 with the
0.1.0 template's `CollectMode=` under `[Service]` (below, *The act template's CollectMode*).

## The release pipeline's tests (E4.S2)

A release runs rarely and expensively, so every part of it that can run on a pull request does (testing rule, "a check
that only runs during a release has never run"). All of it is C# in `WslCare.Scenarios`, no new package:

| Class | What it holds | Where |
|---|---|---|
| `PackagePathFlows` | since the E4 review: `package-daemon.sh` under the bash the workflows use on THIS OS — `/bin/bash`, or Git for Windows' `bin\bash.exe` found from the `git` on `PATH` (never a bare `bash`, which on Windows may be WSL's launcher) — and the printed path opened by .NET, a program that is not bash; the Windows zip moved here, run wherever 7-Zip resolves | every OS (the zip where `7z` is on `PATH`) |
| `PackageFlows` | `.github/scripts/package-daemon.sh` run under bash against a stub binary; the archive read back with `System.Formats.Tar` (an oracle independent of GNU tar): exactly the members `install.sh`'s unpack loop names — read from `install.sh` itself, `ReleaseFiles.InstallerRequiredMembers` — plus their folders, entry types, owner, modes, bytes, and the `.sha256` line; then the archive served to the real `install.sh` by `InstallWorld.PublishFiles` (new), which must install it. The Windows zip is read with `ZipFile` | Linux legs; the zip case where `7z` is on `PATH` (the GitHub Ubuntu image has it) |
| `ReleaseScriptFlows` | `release-guard.sh` and `verify-release-assets.sh` (since the review also with named RIDs: one leg's pair passes, anything else is refused, an unknown RID exits 2) by EXIT CODE (a check that prints and exits 0 stops nothing in a workflow); the guard's off-`main` case in a throwaway git repository whose commit identity comes from `GIT_AUTHOR_*` / `GIT_COMMITTER_*` in the child's environment (no git configuration is written); archive names from the asset contract itself (`daemon_archive_name`, run under bash), never retyped | Linux legs |
| `ReleaseWorkflowTests` | the workflows' structure, read with `WorkflowYaml` — a reader for the YAML subset the workflows are written in that THROWS on anything else (a tab, an anchor, a folded or continued scalar, a duplicate key, a flow map with content), so a construct it does not know fails where it was written rather than being guessed. Its own test plants six such constructs and reads one known document | every OS |
| `ReleaseConfigTests` | `release-please-config.json`, the manifest and `version.txt`, the two ruleset bodies — against the workflows (the tag, the check names, expanded from the matrix); since E5.S3 the `src_vs_code` package too (`node`, its tag = `release-extension.yml`'s, manifest = `package.json`, both packages bootstrapped at 0.1.0) | every OS |
| `ReleaseExtensionWorkflowTests` (E5.S3) | `release-extension.yml`'s structure: the tag-only trigger, each job's exact permissions, the `marketplace` Environment on one job and `VSCE_PAT` named by that job alone (across every workflow), the order github-draft → publish-marketplace → github-public (§15h #0), the idempotent skip and the attested file published, one package checked with `--release` then attested, the guard's inputs, the `MIN_DAEMON_FOR_RENDER` line the guard parses, `tags-extension.json` equal to `tags-daemon.json` but for name and pattern | every OS |
| `ReleaseExtensionScriptFlows` (E5.S3) | `release-extension-guard.sh` by exit code with a fake `gh` on `PATH`: tag shape, `package.json` version, the placeholder publisher, off-`main`, the minimum daemon not published / a draft / another tag, POST_DEPLOY's stamp without a date / without a daemon / older than the minimum, a newer one (and `0.10.0` compared as a number); `verify-extension-assets.sh` whole and broken six ways (since 2026-10-06 also a `.sha256` naming another package; every name is read from `package.json` through `ReleaseFiles.ExtensionVsix`) | Linux legs |

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

## The status verdicts and the golden contracts (E5.S0)

**What is tested.** `StatusVerdicts` and `FullRunVerdicts` in-process (`Core.Tests/Status`), `status` in-process over the
captured tree (`Cli.Tests`), and the BUILT CLI in `StatusFlows` / `CollectFlows` — the 2026-10-01 evening and a fresh boot
as two SYNTHETIC memory states laid over a copy of the captured tree by `TestSupport/ProcfsVariants` (only `meminfo`,
`buddyinfo` and `pressure/` rewritten; the 2026-10-01 state from the baseline's dump, `MemAvailable` LEFT OUT because the
dump did not record it — its verdict is `unknown`, never an invented figure; the fresh boot invented and labelled so).

**The golden writer** (`WslCare.Scenarios/GoldenContracts`, plan §15f #10, §15g m7). On the Linux legs (the Windows binary
answers for another side, so the test skips there with that reason): `CollectFlows.Captured` (the captured Docker and
health answers on the fakes, plus `docker version`), the user layer at every age limit 0 (`PreviewFlows.AllAges` — the
only setting whose rows do not move as the fixtures age), the captured procfs tree, ONE `collect`, then `status --json`,
`preview --all --json`, `doctor --json`, each re-indented with LF line ends. The NAMED, reviewed normalisation list —
each entry a value that moves between two runs of one build over one fixture set, replaced by a fixed value of the same
type:

| Rule | Why it moves | Replaced with |
|---|---|---|
| `**.sampledAt` | the instant the sample or a slow part was taken | `2000-01-01T00:00:00+00:00` |
| `**.sampleMilliseconds` | how long the fast sample took | `0` |
| `**.ageSeconds` (a number) and `**.ageSeconds.value` (a process's figure) | ages at the time of the answer; a process's age is now − its start | `0` |
| `**.evaluatedAt` | when a verdict was evaluated | the fixed instant |
| `**.runId`, and a run id quoted inside any sentence (`runIdInText`) | a run id is its start instant and the CLI's pid | `20000101T000000Z-1` |
| `checkedAt` | the instant `doctor` answered | the fixed instant |
| `productVersion`; `component: wsl-care` → `version` (doctor) | the commit after `+`, and the release number every release-please bump moves (a golden pinned to it would turn the release pull request red) | `unknown` — the contract's own value for an unstamped build, which a client renders (plan §6) |
| `vm.disk.path`, `.totalBytes`, `.usedBytes`, `.availableBytes`, `.usedPercent`; `id: disk.root` → `level`, `value` | `df /` is the sandbox's filesystem on the runner's own disk | `/golden-root`, 100 / 13 / 87 GB, 13 %; `ok`, `13.0 %` (as that disk is judged) |
| `slow.windowsClock.offsetSeconds`; `id: clock.drift` → `level`, the offset at the head of `value`, `reason` | the fake clock probe answers a captured instant: the offset is that instant − now | `0`; the CONCRETE value at the capture — `ok`, `+0.19 s` (the probe's process start − the capture's instant), the product's own sentence for an offset within the limit (E5 code round #1; it read `<captured clock minus now> s` before) |
| `containerStarts.from` / `.to`, `containerStarts.gaps[*].from` / `.to` | the 24-hour window ends now | the fixed instant |
| `id: journal.history` → `level`, the days at the head of `value` | now − the captured oldest journal entry: it grows daily and turns ok at 7 days | `warn` and `0.8 days` — the CONCRETE value at the capture (the health capture's instant − the oldest entry; E5 code round #1, it read `<days since the oldest entry>` before) |
| every string holding the sandbox root | the temporary sandbox | `/golden-root` |
| part 4, IDENTITY — every string, after the sandbox root: `FixtureIdentity.Rules` (`tempFolder`, `linuxHome`, `windowsProfile`, `passwdAccount`, `accountToken`, `projectDirectory`, `extensionId`, `email`), mappings learnt from the checked-in captured fixtures | not a mover: a person in the data (E5 code round, privacy) — the sandbox's own home `/golden-root/home/me` included | `user`, `/home/user`, `/tmp/x`, `project-a`…, `vendor.extension-a`…, `user@example.invalid`; these rules need not match (they guard a future capture — `FixturePrivacyTests` proves the files) |

Observed while building the list (2026-10-03, WSL `Ubuntu`, from a copy under `/tmp`): two generations seconds apart
differed in exactly `checkedAt`, doctor's and preview's quoted run ids, the clock offset (its figure and its verdict text)
and the 24-hour window — all then added; after that two generations were byte-identical. A day-scale mover cannot show
in two runs seconds apart, so the answers were also read for anything derived from now: `journal.history` was the one
found (the cleanup rows do not move at limit 0; process ages are normalised). Two runs on ONE disk cannot show a mover
that follows the disk: the container list followed the directory order of the filesystem under the sandbox and went
stale on both Linux CI legs (2026-10-04, fixed in `PhysicalFileSystem` — see the E5 code-round table). A regeneration is
now also compared under a second filesystem: `TMPDIR` on a tmpfs mounted under `/tmp` in WSL, byte-identical to the ext4 one.

**The drift test** (`GoldenContractTests`): each checked-in file must EQUAL the normalised answer, and the failure names
the file and the first differing line; its companion fails when a rule of the list matches nothing any more. Regenerate
on Linux (WSL) with `WSL_CARE_WRITE_GOLDENS=1 ./src_daemon/tests/WslCare.Scenarios/bin/Release/net10.0/WslCare.Scenarios
--filter-class "*GoldenContractTests"` and review the diff as a contract change. The set frozen at `daemon-v0.1.0`
(`contracts/golden/daemon-0.1.0/`, keeping its real version) is an E5 live-gate step (plan §16), not written here.

**Red first, per guarantee** (2026-10-03). The tests were written against stubs — `StatusVerdicts.From` answering `[]`,
`FullRunVerdicts.Read` answering "not built yet", `status` setting no `productVersion` — and run on Windows:

- `StatusVerdictsTests`, 8 red, each for the missing verdicts: *Expected verdicts.Where(v => v.Basis!.Source ==
  VerdictSource.Sample) to contain 8 item(s), but found 0: {empty}*; *Expected carried.Select(v => v.Id) to be equal to
  {"kernel.allocationFailures", …, "npm.cache"}, but found empty collection*; *Expected status.Select(v => v.Id) to be
  equal to {"memory.available", "memory.pageCache", …}* (the others: no element matched the id they ask for).
- `FullRunVerdictsTests`, 6 red: *Expected type to be …Reading`1+Available[[…RecordedVerdicts…]], but found
  …Unavailable…*; *Expected Read().ReasonOrEmpty "not built yet" to contain "runs/2026-10-03/20261003T080000Z-2.json"*.
- `StatusCommandTests` / `FullRunCommandTests`: *Expected report.ProductVersion not to be <null> or empty, but found
  <null>*; *Expected collection to contain a single item matching l.StartsWith("verdicts: ", Ordinal), but no such item
  was found*; *Expected carried not to be empty*.
- The BUILT CLI (`Scenarios`, Windows): *Expected string to be "0.0.0+7aeb02dd39a9841915112456532d3803e4cb6026", but
  found <null>* (`StatusFlows`, the product version); *Expected verdicts.Select(v => v.Id) to be equal to
  {"memory.available", …}* (`CollectFlows`).
- The Linux-only flows, by reverting the line each rests on, in WSL: the `Verdicts =` line of `StatusCommand` deleted
  turned the 2026-10-01, fresh-boot and user-layer flows red (*System.InvalidOperationException : status --json carried
  no verdicts*; the fresh-boot flow first failed with a bare `ArgumentNullException` — its accessor was changed to name
  the symptom); `MemoryAvailable`'s warn threshold hard-coded to 25 instead of read from the configuration turned the
  user-layer flow red alone: *Expected the enum to be Level.Warn {value: 1} because 66.8 % is below the user's warn
  threshold of 70 %, but found Level.Ok {value: 0}*. Restored (SHA-256 equal to the worktree's) → green.
- The smoke, against the JIT build in WSL: the `Verdicts =` line deleted → `::error::status --json carries no verdicts
  (plan §15g B1)`; the `ProductVersion =` line deleted → `::error::status --json: its verdicts or its productVersion are
  not what --version and the thresholds say`; restored → *passed all six parts*.
- The drift test, in WSL: `StatusReport.SampleMilliseconds` renamed on the wire to `sampleMs` →
  *contracts/golden/head/status.json must be what the CLI answers at this commit — line 5: checked in '
  "sampleMilliseconds": 0,', the CLI answers '  "sampleMs": 63,'*; restored → green.

## The daemon read contract (E6.S0)

**What is tested.** In-process on every OS: `RunningReportsTests` (every running state over a sandbox, read through a file
system that FAILS the test on any write, move, delete, lock or probe — status never sweeps), `LastCleanupTests`,
`RunShowTests` (the resolution order history → `running.json` → requests, the full detail, a dead holder = interrupted
and not swept), `RunLogsTests` (the instant range, half-open ends, the refused shapes, `RunLine.metrics`,
`IsCleanup` / `Freed`), `DockerCleanupTests` (A4's `shown` = every selected key, the cap, no other action's outcome
carries one), `ActCommandTests` (`productVersion`, `--manual` with `--timer` refused) and `ReadContractCommandTests`
(status's four members, `runs show`, the instant range through `Program.Run`). The BUILT CLI in `ReadContractFlows`
(staged running states against the REAL process table, the read-only state directory, `runs show` of a confirmed act and
the run it swept, the local day, the 387-volume preview, SIGHUP, the exclusive marks), the goldens
(`GoldenContracts.ReadContractAsync`, staged by `ReadContractScenes`) and `ContractFilesTests` (the two contract files,
enumerated from the types). `SyntheticDocker` is a SYNTHETIC Docker — 387 anonymous volumes of 154 MB, the shape of the
2026-10-02 morning — invented and labelled so, never a capture.

**Red first, per behaviour group** (2026-10-04). The groups whose code was written first were stubbed back (the
implementation kept aside, the stub returning the "not built" answer) and the tests run against the stub; the rest were
written test-first:

- `running` block (`RunningReportsTests`, Windows, `RunningReports.Read` stubbed to `none`): 11 red, each for the missing
  state — *Expected report.State to be "queued" with a length of 6, but "none" has a length of 4*; *Expected string to be
  "wedged" …, but "none"*; *Expected string to be "unknown" …, but "none"*. And the teeth of "never sweeps": `Read`
  patched to call `RunningSweep.Apply` first → *System.InvalidOperationException : a reader wrote, moved, removed or locked
  …\wsl-care* (the no-writes file system caught it); restored → green.
- `lastCleanup` / `IsCleanup` (stubbed to "none" / `false`): *Expected last to be …LastCleanupReport* (the unavailable one
  found); *Expected records.Where(RunLogs.IsCleanup)… to be equal to {<2026-10-01 23:59:59 +0h>, …}, but found empty
  collection*.
- `runs show` (`RunShowTests`, `RunShow.Read` stubbed to `unknown`, "not built yet"): 9 red — *Expected show.State to be
  "done" …, but "unknown"*; *Expected string to be "queued" …, but "unknown"*; *Expected string "not built yet" to contain
  "90-day"*.
- the instant range and `RunLine.metrics` (`ParseInstants` refusing "not built yet", `Metrics` not yet copied): *Expected
  type to be …PeriodParse+Parsed, but found …PeriodParse+Refused*; *Expected LogPeriod.ParseInstants(from, to) "the instant
  range is not built yet" to contain "offset"* (each refused shape); *Expected runs.Single(r => r.StartedAt ==
  Today).Metrics to be …RunMetrics* (null found).
- A4's `shown` (`VolumeRemoval.Shown` answering `[]`, the engine not yet asking): *Expected collection to contain 10000
  item(s), but found 0: {empty}*; *Expected root not to be <null>* (no `shown` on A4's outcome).
- the CLI (`ReadContractCommandTests`, `ActCommandTests`, Windows): *Expected report.Actions to be equal to
  {"A5Testcontainers", "A5", "A4", …}, but found <null>*; *Expected exit to be 0 because wsl-care: "wsl-care runs" does not
  take "--from"*; *Expected CommandLine.Parse(["runs", "show", …]) to be …Request+RunsShow*; *Expected
  Report(preview).ProductVersion to be "0.0.0+398f73a…", but found <null>*; *Expected type to be …Request+Failed, but found
  …Request+Act* (`--manual --timer`).
- the BUILT CLI (`ReadContractFlows`, Windows): the five staged states and *none* red (no `running` block); *Expected
  runs.Exit to be 0 because wsl-care: "wsl-care runs" does not take "--from"*; *Expected collection to contain a single item
  matching m.Contains("not both", Ordinal)*. `ContractFilesTests`: *Expected File.Exists(path) to be True because
  …\contracts\actions.json is checked in*.
- SIGHUP (`ReadContractFlows`, in WSL, the SIGHUP registration removed from `ShutdownSignals`): *Expected result.Exit to be
  130 …, but found 129* — the default action killed the confirm mid-vacuum, no history line; restored → green.
- The drift test, in WSL: `RunningReport.HeartbeatAgeSeconds` renamed on the wire to `heartbeatAge` →
  *contracts/golden/head/status-running-live.json must be what the CLI answers at this commit — line 357: checked in '
  "heartbeatAgeSeconds": 0', the CLI answers '    "heartbeatAge": 0'*; restored → green.

**Observed while building the goldens** (WSL `Ubuntu`, a copy under `/tmp`): the first generation normalised a staged
pid to `1` — fixed to 4242 so no golden reads "pid 1 is gone" (init); the unreadable state was first staged as a torn
object, whose reason quoted .NET's own JSON exception text (a wording a runtime update may change) — staged as `{}` now,
whose reason is the product's own sentence.

### The E6.S0 review round (two own reviews, 2026-10-04)

Every finding was written as a test first and run against the unfixed code (Windows unless named); the message is the
real symptom:

- **S1, the request reader** (`RunRequestsTests`): content — *Expected type to be …RunRequestRead+Bad, but found
  …RunRequestRead+Parsed* for schema 2, an unknown kind, an unknown action, a non-hex shown name, a collect naming an action
  and 10 001 shown names; *Expected Bad(Only()) "does not parse (The input does not contain any JSON tokens …)" to contain
  "larger than"* (no cap: 1 MiB + 1 read whole); *Expected collection to contain 64 item(s), but found 67* (no flood bound);
  *Expected RunRequests.List(…, vanishing) to be empty, but found at least one item* (a vanished file reported bad — D4).
  In WSL, with the new reader swapped back for `ReadFile`: *Expected finished to be True because a request is never opened
  in a way that could block, but found False* (the FIFO held the read 10 s) and *… not owned by the state's owner … found
  …Parsed*. The owner / mode rule itself: `RegularFilesTests.A_state_file_is_trusted_only_when_its_owner_alone_may_write_it`.
- **S2** (`RunningReportsTests`, `RunShowTests`): *Expected string to be "20261002T115800Z-4242", but found <null>*;
  *Expected string to be "running", but "unknown"*.
- **S3**: *Did not expect a value, but found 4242* (the dead state's pid).
- **S4** (`RunRecordTests`): *Expected RunId.TryParse(text) to be <null>* for `…-0123` and `…-00`.
- **D1** (`RunningReportsTests`): *Expected report.State to be "live" because run … died: pid 4242 is a different process now
  (started 2026-10-02T12:57:00, the run's started 2026-10-02T11:57:00)* — the wall clock stepped an hour; *… other start
  ticks … to be "dead", but "live"*.
- **D2** (`ActionEngineTests`, `DockerCleanupTests`): *Expected detail.Actions… to be equal to {"A5:interrupted",
  "A10:interrupted"}, but found empty collection*; *Expected _journal … to not contain "run A10"* (a partial run did not
  stop the run); *System.OperationCanceledException* thrown out of `DockerRemovals.RemoveAsync` (the confirmed batch lost).
  The SIGHUP flow, strengthened, in WSL with the engine's in-flight / not-run records removed: *Expected collection to
  contain a single item matching (a.Id == "A10"), but the collection is empty* — the flow was green with the bug before.
- **D3**: *Expected string to be "none", but "dead"*.
- **D4**: *Expected string to be "live", but "none"* (`status`: a request that became a run between the reads); *Expected
  string to be "done" …, but "unknown"* (`runs show`).

All green after the fixes, on Windows and in WSL. Goldens regenerated in WSL: only `status-running-dead.json` moved (its
`pid` and `heartbeatAgeSeconds` are gone); the `running.json` fields are additive and appear in no answer.

## Detached runs, the request, the stop (E6.S1)

**What is tested.** Core, on every OS: `DetachedRunTests` (the request written EXCLUSIVELY — a second writer gets
`AlreadyExists` and the first request stands byte for byte; 0755 / 0644 on Linux — the request sweep: history first, a
request younger than its grace (60 s monotonic since the review round) left alone, a queued job or any active state pending, a done unit ONE `interrupted` line
and the request gone, an unreadable unit kept, the run's own request never swept; the stop marker turning a SIGKILLed run's
sweep reason into "stopped: … did not exit within 90 s of SIGTERM"; old markers removed; the engine and `collect` under a
pre-allocated run id, hearing `running.json` before the request goes; a `collect` cancelled during the measurement leaving
ONE `interrupted` line naming the cause) and `UnitCommandsTests` (the three systemctl templates, every name of
`HostileInputs.HostileUnitNames` refused by template AND policy, 100 000 seeded mutations of a valid unit name of which
every accepted one is exactly `wsl-care-act@<yyyyMMddTHHmmssZ>-<pid>.service`, `Busy`). CLI in-process:
`DetachedRunsTests` (the parse, every `--detach` refusal and its code, the accepted answer, stdin, `act --request`, `act
--stop`). The BUILT CLI: `DetachFlows` (accepted → `act --request` records under the answered id; the timer's lock →
`refused` recorded; a stale request swept by `collect`; no systemd → 69; a never-exiting child ended by its own ceiling;
the request folder 0755 and the request 0644 under `umask 077`), `ShippedFilesTests` (the act template's `ExecStart`
parsed by the CLI with `%i` = a run id, `TimeoutStartSec=infinity`, `CollectMode` in `[Unit]` (since 0.1.1), `SuccessExitStatus` = exactly the
recorded answers, the hardening of the two root units EQUAL with a companion that reads every key), `Install*Flows` (the act
unit installed and uninstalled with its instances stopped, the bounded wait refusing under `live` / `queued`, the rename,
a request surviving an upgrade) and the golden `act-detach-accepted.json`.

**Red first, per behaviour group** (2026-10-04). The code was written before its tests, so every group was proved by
reverting its load-bearing line, running the tests (red, the message below), and restoring it (green) — scripted, the file
restored byte for byte each time; Windows unless named:

| Group | Reverted | Red message |
|---|---|---|
| the unit slot | `ActUnit.Accepts` without the run-id parse | 9 red: *found at least one item {"systemctl-start-act: start --no-block wsl-care-act@*.service"}*; *"wsl-care-act@20261004T12000Z-4321.service" was accepted, so it must be wsl-care-act@<yyyyMMddTHHmmssZ>-<pid>.service* |
| a queued job | `Busy` reads the active state only | *Expected UnitCommands.Busy(show) to be True, but found False* (`Job=12`) |
| the sweep: history first | the recorded check never taken | *Expected collection to contain a single item because history first: the run's own line is the only one* |
| the sweep: its grace | the age check never taken | *Expected (Sweep()) to be empty, but found … "swept the request of run 20261002T114501Z-4 …"* |
| the sweep: its own request | the `own` filter removed | *… "swept the request of run 20261002T100000Z-8: its unit is not running (recorded as interrupted)"* |
| the sweep: a done unit | nothing recorded for `Done` | 3 red: *Expected Queued(request) to be False, but found True* |
| the stop marker | the stopped reason dropped | *Expected … to start with "stopped: act --stop asked systemd to stop it", but "swept: pid 999 is gone; …"* |
| the engine's run id | `request.RunId ??` dropped | *Expected done.Detail.RunId to be …RunId* (a fresh one) |
| `running.json` before the request goes | `OnRunningWritten` never called | *Expected runningAtCallback to be True because the request goes only once running.json stands* |
| a collect cut off | `RecordCutOff` removed | *Expected var line = History() to contain a single item, but the collection is empty* |
| collect's run id | `c.RunId ??` dropped | *Expected result.Detail!.RunId to be …RunId* |
| exclusive create (Windows) | `File.Move(…, overwrite: true)` | *Expected type to be …ExclusiveCreate+AlreadyExists …, but found …ExclusiveCreate+Created* |
| exclusive create (WSL) | `EEXIST` read as created | the same message |
| no systemd | the marker check removed | *Expected value to be 69, but found 0* |
| a failed start | the request kept | *Expected Requests() to be empty because no orphaned request (plan 15k #1)* |
| a queued run | `Queued` not busy | *Expected value to be 75, but found 0* |
| the budget | `>=` → `>` | *Expected value to be 73, but found 75* |
| stdin's byte cap | one byte more allowed | *Expected string "wsl-care: act: line 1 of the --only file is not an anonymous volume's name …" to contain "larger than 1048576 bytes"* |
| stdin's ceiling | the ceiling × 100 | *Expected time to be less than 10s, but found 30s, 43ms* |
| a refused request | no history line | *Expected var line = History() to contain a single item, but the collection is empty* (also the built binary, WSL) |
| a refused request's file | not removed | *Expected Requests() to be empty because every terminal path removes its request* |
| a missing request | exit 2 instead of 80 | *Expected value to be 80, but found 2* |
| `act --stop`: the cgroup check | any readable unit allowed | *Expected value to be 2, but found 1* — the command policy refused `systemctl stop session-1.scope` (the second barrier held) |
| `act --stop`: the marker | never written | 2 red: *Expected StopMarkers.List(…) to be equal to {…}* |
| the request gone while acting | `OnRunningWritten` a no-op | *Expected requestThere to be False, but found True* |
| its OWN request, already recorded | written test-first while re-reading the diff | *Expected value to be 80, but found 0* — a run that recorded itself and died before removing its request ran a SECOND time under its id; fixed (history first in `act --request` too), green |
| `act --request` sweeps | no `UnderLock` | *Expected History()… to be a collection with 2 item(s)* |
| the hardened reader (WSL) | `ReadRegularFile` for requests | *Expected value to be 2, but found 0* (a group-writable request ran) |
| `collect` sweeps (WSL) | no `RequestSweep` | the stale request's line missing |
| the per-command ceiling (WSL) | the runner's ceiling × 40 | *System.TimeoutException : … wsl-care act --request 20261004T174352Z-1206027 did not exit within 30 s* |
| the request file mode (WSL) | `MakeReadable` removed | *Expected File.GetUnixFileMode(…json) to be OtherRead\|GroupRead\|UserWrite\|UserRead {value: 420}, but found UserWrite\|UserRead {value: 384}* — green under umask 022, which is why the flow runs under 077 |
| the install wait (WSL) | `wait_for_runs` never called | 2 red: the upgrade went on to `systemctl try-restart` |
| the atomic install (WSL) | `install` straight over the binary | the dry run printed no `.new` / `mv -f` |
| uninstall (WSL) | no stop of `wsl-care-act@*.service` | the systemctl calls lacked it |
| hardening equality | the act unit's `MemoryMax=2G` | *Expected Hardening("wsl-care-act@.service") to be equal to {…"MemoryMax"] = "1G"…}* |
| its companion | `KillMode=process` in wsl-care.service | *… differs at key "KillMode"* (the first try replaced the COMMENT line and stayed green — the anchored retry is the real check) |
| `SuccessExitStatus` | 80 dropped | *Expected codes to be equal to {"3", "75", "76", "78", "79", "80"} …, contains 1 item(s) less* |

**A defect the new flow found** (WSL): `Under_a_restrictive_umask_the_request_folder_is_0755_and_the_request_0644` went red
against the first implementation — *found UnixFileMode.UserExecute\|UserWrite\|UserRead {value: 448}*: `mkdir(2)` masks the
mode with the umask, so a root shell with `umask 077` would have made `requests/` 0700 and the queue invisible to the
unprivileged `status`. Fixed (`EnsureParent` sets the mode after creating the folder); green.

**CI found one more** (the `win-x64` leg, run 37223302838): `VerbRegisterTests.Every_registered_verb_runs_its_example_…(act --request <runId>)` — *Expected result.Exit to be one of {0, 2, 77}*: the GitHub Windows runner is ELEVATED, so the example ran as root and answered `act --request`'s designed no-op, 80 (reproduced here with root claimed in a sandbox: exit 80). The allowed set gained 80 — a named no-op, not a crash.

**Three existing tests changed, and why.** `VerbRegisterTests` as above. `CommandPolicyPropertyTests.JudgePolicy` now caps each KIND of violation at 50
instead of all of them together: the three new templates shifted the seeded sequence so far that the naive-policy test's
"undeclared argv" violations filled the shared 50 before a wrapped never-command appeared (the test's own property was
unchanged). And the hostile unit names live in their own list (`HostileInputs.HostileUnitNames`), not in `HostileValues`,
so the seeded sequences of the policy properties stay as they were.

### The E6.S1 review round (two own reviews, 2026-10-04, plan §15l)

Every finding was written as a test, the fix's load-bearing line then reverted (red, the message below) and restored (green);
Windows unless named. The WSL checks ran in a `/tmp` copy, which was rebuilt fresh before the final suite (a restored source
older than its build is not recompiled — the lesson of the E6.S1 L11 check).

| Finding | Test | Reverted | Red message |
|---|---|---|---|
| S1 temporary mode (WSL) | `DetachedRunTests.A_temporary_file_is_created_owner_only_…` | `UnixCreateMode` dropped from `WriteNew` | *Expected seen to be equal to {UserWrite\|UserRead {value: 384}, …}, but {OtherRead\|GroupRead\|UserWrite\|UserRead {value: 420}, …} differs at index 0* |
| S1 folders (WSL) | `DetachFlows.Under_umask_000_nothing_the_daemon_writes_is_group_or_world_writable` | `CreateDirectory` back to the plain call | *… found at least one item {"var/lib/wsl-care/stops OtherExecute, OtherWrite, …"}* — and BEFORE the per-level fix the same flow found the state directory itself 0777 (`Directory.CreateDirectory(path, mode)` gives the mode to the leaf only) |
| S1 history (WSL) | the same flow | the append's `UnixCreateMode` skipped | *… {"var/lib/wsl-care/history.jsonl OtherWrite, OtherRead, GroupWrite, GroupRead, UserWrite, UserRead"}* |
| S2 trigger | `DetachedRunTests.A_request_with_a_trigger_root_never_writes_…` (3 cases) | the trigger check removed | *Expected type to be …RunRequestRead+Bad, but found …RunRequestRead+Parsed* ×3 |
| S2 future creation | `DetachedRunTests.An_unstamped_request_claiming_a_future_creation_…` | the future check removed | *Expected Queued(request) to be False, but found True* |
| S3 whole cgroup path | `DetachedRunsTests.A_wedged_run_in_a_unit_that_only_ends_in_one_of_the_names_…` (4 cases) | last-component compare | 3 red: *Expected value to be 2, but found 0* (a user unit named wsl-care.service, a user unit named for the run, the act unit outside its slice — stopped); the fourth (another run's id) is the run-id guard, green both ways |
| S4 no answer (WSL) | `InstallUpgradeFlows.An_upgrade_whose_installed_binary_gives_no_status_answer_…` (exit 70, empty) | no answer = not in flight | 2 red: the upgrade went on to `systemctl try-restart` |
| S4 wedged (WSL) | `InstallUpgradeFlows.An_upgrade_under_a_run_in_flight_…(wedged)` | `wedged` out of the pattern | the upgrade went on |
| minor `.new` (WSL) | `InstallUninstallFlows.Uninstall_removes_a_new_binary_…` | uninstall's removal dropped | *Expected File.Exists(…wsl-care.new) to be False, but found True* |
| D1 orphan | `DetachedRunsTests.A_detach_sweeps_an_orphaned_request_…`; built binary: `DetachFlows.A_detach_sweeps_a_request_an_earlier_boot_left_behind_…` | the detach's sweep removed | *Expected exit to be 0 because wsl-care: busy: run 20261002T115800Z-60 (A9) was accepted and has not started yet* |
| D2 act | `DetachedRunsTests.A_request_cut_off_before_it_started_keeps_one_interrupted_line` | the cut-off removes the request bare | *Expected var line = History() to contain a single item matching (r.RunId == 20261002T115955Z-62), but the collection is empty* |
| D2 collect | `DetachedRunTests.A_collect_cut_off_while_it_sweeps_…` | the sweep's guard records nothing | *Expected History() to contain a single item, but the collection is empty* |
| D3 monotonic | `DetachedRunTests.A_stamped_request_…` (forward and back) and `…_of_an_earlier_boot_…` | the boot stamp ignored | 3 red: *Expected (Sweep()) to be empty, but found … "swept the request of run 20261002T100000Z-20 …"* (a forward step swept a 20-s-old request); *Expected Queued(request) to be False, but found True* ×2 |
| D4 re-read | `DetachedRunTests.A_run_that_records_itself_while_the_sweep_looks_…` | the second history read removed | *Expected History() to contain a single item, but found* two (`refused` and `interrupted`) |
| D5 sweep | `DetachedRunTests.An_unusable_request_is_recorded_refused_…` | the sweep's Bad branch removed | *Expected File.Exists(path) to be False, but found True* |
| D5 `--request` | `DetachedRunsTests.A_request_whose_content_is_not_what_root_writes_…` | the old bare refusal | *Expected History() to contain a single item, but the collection is empty* |
| D6 timed-out start | `DetachedRunsTests.A_timed_out_start_asks_the_unit_…` (busy / done / unreadable) | a timeout treated as a failure | 2 red: *Expected exit to be 0 because wsl-care: systemctl start --no-block … did not succeed (timed out after 30 s); the request was removed* |

Tests changed by the round: `A_detach_while_a_run_is_queued_…` and the budget test plant requests INSIDE the grace (an older one
is swept now — the old test pinned the bug); the 79 test stages an unreadable `running.json` (an unusable request is swept now,
`A_detach_records_an_unusable_request_refused_…`); the content and group-writable `--request` tests expect the `refused` line.

### The coai E6 code round (2026-10-05, plan §15m)

Tests written first and run against the unfixed code (Windows unless named), or — for the installer — the new tests run
against the OLD `install.sh` (the file at `bef4ba4`) in WSL:

| # | Test | Red message |
|---|---|---|
| 0 | `DetachedRunsTests.The_detach_shapes_parse_into_their_requests`, `ReadContractCommandTests.Runs_show_parses_…`, `ShippedFilesTests` | a type change — the tests now construct the requests with a `RunId`, which the old string-typed records do not compile against; no runtime red is possible |
| 2 | `DetachedRunsTests.A_bad_run_id_is_refused_naming_the_value_and_the_shape_…` (3 verbs) | 2 red: *Expected failed.Message to be the same string, but they differ at index 24* (`act --request`) / *index 21* (`act --stop`); `runs show` already answered so |
| 3 | `ReadContractCommandTests.Logs_and_runs_parse_an_instant_range_…` (it pinned `"today"`) | *Expected CommandLine.Parse(["logs", "--from", …]) to be …Request+Logs* (Period "today") |
| 6 | `DetachedRunTests.A_request_of_an_earlier_boot_reports_dead_…` | *Expected string to be "dead" with a length of 4, but "queued" has a length of 6* |
| 7 | `DetachedRunTests.The_running_block_reads_only_the_oldest_request_…`, `When_the_oldest_request_cannot_be_used_…` | *Expected counting.Reads to be 1, but found 32*; *Expected value to be 3, but found 2* (the count was of parsed requests, not files) |
| 1 (WSL) | `InstallUpgradeFlows.The_upgrade_wait_decides_every_running_state_golden_…` (each golden, indented and compact) | the old guard let the COMPACT `live` golden and the `unreadable` golden through (*Expected result.Exit to be 1 because the install should fail*), and its message named no state |
| 4 (WSL) | `InstallUpgradeFlows.A_long_wait_says_every_progress_period_…` | no `still waiting:` line |
| 5 (WSL) | `InstallUpgradeFlows.An_upgrade_whose_installed_binary_gives_no_status_answer_…`, `The_escape_skips_the_wait_…` | the refusal advised `sudo wsl-care collect`, naming no manual escape; the skip did not exist |
| 8 (WSL) | `InstallUpgradeFlows.The_wait_ceiling_is_measured_on_the_wall_clock_…` (a status that takes 6 s, ceiling 10 s) | *Expected (DateTime.UtcNow - started) to be less than 24s …, but found 28s, 832ms* |

All green after the fixes (the new `install.sh` restored and compared byte for byte), on Windows and in WSL.

### The progress flow made deterministic (2026-10-05)

`InstallUpgradeFlows.A_long_wait_says_every_progress_period_…` (finding 4 above) failed once in a full WSL Scenarios run under
load and passed alone. Not a flake — a test that read the WALL clock in whole seconds. It ran with a 6 s ceiling and a 1 s
period against the script's fixed 5 s poll: `started=$(date +%s)`, status call 1, the first note, `sleep 5`, status call 2,
`now=$(date +%s)`. Whenever the part of a second already gone at `started` plus the two status calls (each a `timeout`, the
old binary and a `tr`/`grep`/`sed` pipeline) passed 1 s, `now - started` was already 6 on the second poll, so the wait
refused there and never printed a `still waiting:` line. **Reproduced** on `origin/main` (`c49b571`) in WSL `Ubuntu`
(Release, a `/tmp` copy, the one test in a loop): **1 failure in 20 runs alone, 4 in 20 under 24 CPU burners** — each
*Expected result.Stdout "… a wsl-care run is live (20261004T120000Z-4242); waiting (at most 6s)" to contain "still waiting:
live 20261004T120000Z-4242, "*, the refusal arriving at the second poll (the failed runs took 6.2 s alone, 11–15 s loaded).

**The fix is in the TEST WORLD, not in `install.sh`:** the flow runs on the scripted clock (§ *The installer harness*), with a
30 s ceiling and a 10 s period. Time moves only by the script's own sleeps, so the outcome is exact and can be asserted
whole: the note, then `still waiting: live 20261004T120000Z-4242, 10s of 30s` and `…, 20s of 30s` and NOTHING on the polls at
5, 15 and 25 s (the period, not the poll), the refusal `a wsl-care run is live (20261004T120000Z-4242), still after 30s`,
the clock at exactly start + 30 (the refusal on the poll that reaches the ceiling, before another sleep), and the old binary
untouched. The ceiling measured on the WALL clock keeps its own flow (`The_wait_ceiling_is_measured_on_the_wall_clock_…`),
which is why that one stays on the real clock.

**Teeth** (in the WSL copy, one `sed` per mutation of `install.sh`, the file checked changed, the one test run, the file
restored and compared by SHA-256 — every restore byte-identical):

| Mutation | Red |
|---|---|
| the `say "still waiting: …"` line deleted | *… but {"…; waiting (at most 30s)"} contains 2 item(s) less* |
| the period check `-ge "$PROGRESS_SECONDS"` → `-ge 0` (a line every poll) | *… contains 3 item(s) too many* (lines at 5, 15 and 25 s) |
| `, ${elapsed}s of ${RUN_WAIT_SECONDS}s` dropped from the line | *… differs at index 1* (`still waiting: live 20261004T120000Z-4242`) |
| `${STATE…}${RUN_ID…}, ` dropped from the line | *… differs at index 1* (`still waiting: 10s of 30s`) |
| the ceiling check `-ge` → `-gt` (refuses one poll late) | *… contains 1 item(s) too many* (`…, 30s of 30s`) |

**After the fix**, in WSL: the flow 30 of 30 alone, 30 of 30 under 24 CPU burners and 20 of 20 under 48; the scripted clock's own tests
(`ScriptedClockTests`: both forms answered, 30 scripted seconds slept at once, eight other forms refused with exit 2 and
the clock unmoved) green. The whole Scenarios suite in WSL, three times: 301 passed and 1 skipped of 302 in runs 2 and 3; run
1 failed ONE other flow, `LogsFlows.The_timers_full_run_acts_after_measuring_…`, which is not touched here. Looped under 24
CPU burners it failed 2 runs in 10 with *System.TimeoutException : …/wsl-care collect --timer --json did not exit within
30 s* — the child ceiling of `ChildProcess` — against 6.6 s for the whole test alone. Recorded as an open finding (a
wall-clock budget, or a slow timer pass under load — not yet traced), not fixed in this change. Traced and closed in the
next section: the budget, not the pass.

### The timer's full run under load: the harness's wall budget, not the pass (2026-10-05)

The open finding above, traced and closed. **Reproduced** on `origin/main` (`0d5aef9`) in WSL `Ubuntu` (Release, a `/tmp`
copy, the one test in a loop): 0 failures in 5 runs alone and 0 in 10 under 24 CPU burners on that day (the machine was
less loaded than on the day of the finding — the whole test took 23–30 s there), and **4 in 10 under 48 burners**, each
*System.TimeoutException : …/wsl-care collect --timer --json did not exit within 30 s*.

**Where the time goes** — measured in an instrumented copy only (the fake stamped its process start, its `Main` and its
exit into a file beside the call log; the scenario's folder was kept): the pass makes **45 fake calls, one after the
other**, and the time is the fakes', not the pass's:

| | the fakes' calls | inside the fakes | between two calls (longest) | a single call (longest) | the pass (`collect` log, first line → record written) |
|---|---|---|---|---|---|
| alone | 45 in 3.8–5.3 s | 3.6–4.9 s | 0.07–0.12 s | 0.11–0.16 s | 4.2–5.7 s |
| 24 burners | 45 in 12.5–16.4 s | 11.1–14.5 s | 0.26–0.55 s | 0.40–0.51 s | 14.0–18.3 s |
| 48 burners | 42–45 in 19.0–29.1 s | 16.3–24.4 s | 0.63–1.30 s | 0.65–1.75 s | 21.7–28.9 s, or never (killed) |

Each fake is a framework-dependent .NET process: 30–40 ms from its start to `Main` alone, 100–190 ms under 24 burners.
No sleep, no lock wait, no ceiling: every call answered (none waited out a product ceiling), the longest silence between
two calls was 1.3 s, and the CLI's own work between the last call and its record is milliseconds. The two killed runs had
made 45 and 42 calls — still calling a fake every second when the 30 s total cut them off. **So it is (a): a legitimately
slow pass, and a wall-clock budget that is a guess about how loaded the machine is.** Not a product defect.

What the 45 calls are, for the record (not changed here): the measurement's own reads, then the action pass's — each
docker action (A4, A5, A6, A7) takes its own LIVE look (`DockerLook.TakeAsync`: `version`, `system df`, `system df -v`,
`volume ls`, `container inspect`), as plan §15a #0 requires, so 20 of the 45 repeat five of the measurement's six docker
reads, four times; A7 asks `builder prune --help`; and six more repeat a read the measurement made: `snap list`,
`journalctl --disk-usage`, `systemctl show fstrim.timer`, the host figures through `powershell`, `timedatectl` and
`journalctl --since`.

**The fix is in the harness, not the product:** `ChildProcess.RunAsync` takes a `ProgressWait` (`TestSupport`) —
a mark that changes whenever the child does something, a silence after which it is killed with its tree, and a cap.
`ScenarioHome.RunAsync` waits every CLI run on the length of its fakes' call log: **30 s without a new fake call** (the
same 30 s a total gave a silent child) or **5 minutes in all** (ten times the slowest progressing pass measured; a child
that calls fakes that long is a loop). A child still killed says which: *… made no progress for 30 s* or *… did not exit
within 300 s*. Nothing the flow asserts changed.

The wait's own tests, `ProgressWaitTests` (every OS; the child is a fake scripted to sleep): a child whose mark keeps
changing runs past a 1 s silence to its own exit (4 s); a silent one is killed at the 1 s silence long before its 60 s
cap; one that progresses for ever still ends at a 3 s cap; a ceiling and a progress wait together are refused; a
scenario's CLI that calls no fake for its silence (`docker version` scripted to hang 20 s, which the product would cut
at its own 10 s probe ceiling and carry on) is killed for it; and a fake call grows the scenario's mark.

**Teeth** (in a WSL `/tmp` copy, one mutation at a time: the file checked changed, rebuilt, `ProgressWaitTests` run, the
file restored and compared by SHA-256 — every restore byte-identical):

| Mutation | Red |
|---|---|
| the wait ignores progress (a 30 s total again) | 3 of 6: the CLI scenario *Expected a* `System.TimeoutException` *to be thrown, but no exception was thrown*; the silent child *… "…/fakebin/docker sleep did not exit within 30 s" does not* match *made no progress for 1 s*; the cap *… did not exit within 30 s* where 3 s was asked |
| a new mark never restarts the silence | 2 of 6: the busy child *System.TimeoutException : …/fakebin/docker sleep made no progress for 1 s*; the cap test killed for silence instead |
| no silence kill (the check never true) | 2 of 6: the CLI scenario not killed; the silent child *… did not exit within 60 s* (waited out to its cap) |
| no cap (a progressing child waited for ever) | 1 of 6: *Expected a* `System.TimeoutException` *to be thrown, but no exception was thrown* (the 60 s sleep ran out) |
| `ScenarioHome.RunAsync` not waited on progress | 1 of 6: the CLI scenario *Expected a* `System.TimeoutException` *to be thrown, but no exception was thrown*; and **the timer flow under 48 burners failed 6 runs in 10** with the original *… collect --timer --json did not exit within 30 s* |

**After the fix**, in WSL (Release, a fresh `/tmp` copy): the timer flow **10 of 10 alone, 20 of 20 under 24 CPU burners
and 30 of 30 under 48** (against 4 failures in 10 under 48 before; 20 of the 48-burner runs and all of the others on the build before `ScenarioHome.Silence` existed, the
last 10 on this one — the wait itself is the same). `ProgressWaitTests` 6 of 6 on Windows and in WSL. The whole Scenarios
suite in WSL three times: 307 passed and 1 skipped of 308, each time. Windows (Release): Core 934 (914 passed, 20
skipped), Cli 199 (197, 2), Scenarios 308 (183 passed, 125 skipped — the Linux-only flows).

### A full check's history line names itself — `kind` (2026-10-05, plan §15o)

What a reader of `history.jsonl` sees, at the wire. `Cli.Tests/FullCheckLineTests` drives EVERY writer of a full check's
terminal line — its `Ending` enum is that list (completed, observe-only, failed on its detail, refused at the lock, cut off
during the measurement, cut off while sweeping, cut off before it started, a swept request, a swept dead holder, one after
`act --stop`) — reads the line as raw JSON and asserts: the outcome and reason the ending names (proof the staging reached
that writer), `kind: "collect"`, no `collect` row in `actions`, and (coai plan round #1) a reason that starts with none of
`HistoryReasons.NotAFullCheckWithoutKind` — the prefixes `contracts/history-reasons.json` carries. Its companion asserts
the prefixes DO mark the unusable request's and the reconciled orphans' lines, and that a readable full-check orphan
carries `kind: collect` beside the reconcile's prefix (kind first). "Cut off before it started" is not reachable from
outside for a full check (`CollectRun` records its own cut-offs first): its line is built by the very expression
`DetachedRuns.CutOff` appends. `Core.Tests/Records/RunKindTests`: an older `running.json` is a full check only in the exact
shape `CollectRun` writes (five shapes); a dead holder's line takes its kind and lists only actions (a measuring full check,
the timer's pass, an `act --timer` of the same ids, older files of both); a swept act request names `act` with its ids
interrupted; the reconcile reads an orphan's kind from its detail; a line without `kind` parses and answers none; a line with
an unknown kind is counted unparseable (the stated residual); `runs` answers each line's kind; a refused full check adds no
`collect` entry to `perAction`; no action id is the reserved name. `ContractFilesTests` (Scenarios) holds
`contracts/history-reasons.json`, checks the registry AND the checked-in `contracts/actions.json` for the reserved name, and
(review round) freezes the three on-disk reasons to literals; the companion asserts the reasons as written start with the
prefixes the follower on the E6.S3 branch matches. `TimerPassTests` and
`ActionEngineTests` gained the kind of the timer's line and of its `running.json` during the pass, and of an act's line and
`running.json`. `RefusingDetailWrites` moved to `TestSupport` (it was private to `CollectRunTests`).

**Red first** (Windows, before the fix; the test reads raw JSON, so it compiled against the old code): 3 of 3 —
*Expected Kind(line) to be "collect" … but "" has a length of 0* for the completed line (`"actions":[]`, no `kind`), the
refused one (`"actions":[{"id":"collect",…,"status":"refused"}]`) and the one cut off while sweeping (`"actions":[]`).
Green after the fix: 21 of 21.

**Teeth** (Windows, one mutation at a time: the file checked changed, rebuilt, the class run, restored by writing it back and
compared by SHA-256 — every restore byte-identical):

| Mutation | Red |
|---|---|
| `CollectRun.Line`'s kind → `null` | 3 of 21: completed, observe-only, failed — *Expected Kind(line) to be "collect" …* |
| the `collect` row back in `DetachedRuns.Refused` | 1: refused at the lock — *Expected ActionIds(line) {"collect"} to not contain "collect"* |
| the older-file inference without its `current` check | 2 of 20 (`RunKindTests`): `["collect"]` with current `""` / `A10` — *Expected … KindOrMarker() to be <null>, but found RunKind.Collect* |
| `DetachedRuns.CutOffReason` starting `refused: its request could not be used` | 1: *Expected HistoryReasons.MarksNotAFullCheck(reason) to be False …* (cut off before it started) |
| the swept holder keeps the `collect` row | 2 (`RunKindTests`): *Expected line.Actions.Select(a => a.Id) to be equal to {empty} …, but found {"collect"}* |
| the timer pass's `ActRequest.Kind` not set | 1 (`TimerPassTests`): *Expected duringThePass.Kind to be RunKind.Collect …, but found RunKind.Act* |
| `RunRequestFile.TerminalLine` writing a full check as `act` with its row | 3: refused, cut off before it started, swept request — *Expected Kind(line) to be "collect" …* |

**Goldens** regenerated in WSL (`WSL_CARE_WRITE_GOLDENS=1`, a `/tmp` copy, copied back and compared by SHA-256): only
`runs-local-day.json` (`kind` on its three lines), `runs-show-done.json` and `runs-show-interrupted.json` (`kind: "act"`)
changed — additive members, nothing else moved.

**The §15o review round** (coai code round + an own review, 2026-10-05; plan §15o's table). Each behaviour red → green →
red (Windows, Debug; the mutation checked applied, the file restored by writing it back, SHA-256 byte-identical):

| # | Test | Red before the fix | Teeth (the fix's line broken) |
|---|---|---|---|
| G1 | `RunKindTests.A_reconciled_orphan_takes_its_kind_from_its_detail` (`archive`, `Act`) | 2: *Expected History() to be <null>, but found RunKind.Collect* | `_ => RunKind.Collect` back in `OfDetailKind`: the same 2 |
| G2 | `RunKindTests.A_request_of_an_unknown_kind_gets_a_line_with_no_kind_and_no_action_rows` (`archive`, `Collect`, `""`) | 3: *Did not expect line.Kind to have a value … but found RunKind.Act* | `_ => RunKind.Act` back: the same 3; the rows written for an unknown kind (`!= Collect`): 3, *Expected line.Actions to be empty …* |
| G4 | `RunKindTests.A_running_json_without_actions_is_unreadable_and_never_swept_into_a_line` | none — the guard already held (the test pins the stated reason) | `Actions: not null` dropped from `RunningState.Read`: *System.ArgumentNullException : Value cannot be null. (Parameter 'source')* in the sweep |
| O2 | `ActionEngineTests.An_act_names_itself_act_in_running_json_and_on_its_line_whatever_its_trigger` (Cli, Manual, Timer) | none — already true; the theory is the missing writer-side test | `ActRequest.Kind` defaulting to `Collect`: 3, *Expected kinds to be equal to {RunKind.Act} … but {RunKind.Collect} differs at index 0* |
| O3 | `FullCheckLineTests.An_act_request_cut_off_inside_its_request_sweep_gets_ONE_act_line_from_DetachedRuns_CutOff` | none — the path was right, it was untested | `DetachedRuns.CutOff`'s line `with { Kind = null }`: *Expected Kind(line) to be "act" … but "" has a length of 0* |
| O4 | `ContractFilesTests.The_reasons_already_on_disk_are_frozen`; the companion's "as written" assertions | none — a new pin | `RunReconcile.InterruptedReason` reworded: *… these strings are on disk; a change is a contract break, but they differ at index 29* |

G3 (the handler lookup in `FullCheckLineTests`) and O1 (`Cli.Tests/DetachedRunHarness`, shared by `DetachedRunsTests` and
`FullCheckLineTests`) are refactors of tests with no behaviour of their own: both classes green before and after (54 and
22). O5 is documentation (plan §15o decision 6, `research/architecture.md`).

### The configuration trust (E7.S0, 2026-10-05, plan §15q R1)

What a root run may take from another account's configuration layer, and what no setting may do at all
(`research/architecture.md` § *The configuration trust (E7.S0)*). The tests:

| Guarantee | Tests |
|---|---|
| no list key widens A11: `processes.families` ⊆ the catalogue without `other` / `ai-agents`, on the command line and in a file (review B1, a live bug) | `Core.Tests/Config/ConfigKeyClosureTests`; `ConfigTrustFlows` |
| every list key closed; a path key machine-only; a pattern key read by no daemon code; every number slot a key fills accepts exactly that key's range (derived from the product catalogue's templates, an unmapped slot fails naming it) | `Config/ConfigKeyShapeTests` |
| the never-list, the command policy and catalogue, the deletion policy and the protected roots reference no configuration type — with the companion that the pattern still finds one in `JournalVacuum.cs` | `ArchitectureTests.No_policy_or_protected_roots_type_reads_the_configuration` (+ companion) |
| the user layer is refused at once when it is a FIFO, never followed when it is a link, refused naming `chmod go-w` when group-writable; an ordinary layer still read | `Config/ConfigLayerTrustTests` (Linux legs for the first three) |
| root takes the target user's layer only when that user owns it | `UserLayerTrustTests.Root_reading_the_target_users_layer_requires_that_user_to_own_it` (Linux) |
| without interop a root run takes a user value only in its safe direction — judged against the machine layer, not only the default — and says so as a notice, never observe-only; a tightening or a display value is taken | `UserLayerTrustTests` (three facts) |
| with interop root takes the user's layer, but root's own log level and retention only tighten (0 = kept for ever); a user's own run takes its whole layer | `UserLayerTrustTests` (three facts) |
| a machine-only key is taken from the machine layer and ignored, with a notice, from the user layer; `config set` refuses it | `UserLayerTrustTests`, `ConfigTrustFlows` |
| the trust follows whose home the paths follow (`UserLayerTrusts.For`); the user layer's digest is the SHA-256 of what was read | `UserLayerTrustTests` |
| a Windows-profile file through drvfs is read with no owner / mode check, but never through a link, never waited on (FIFO), never past its cap | `Files/NoFollowReaderTests` (Linux) |
| `config set` over a FIFO moves it aside, never waits on it | `Config/UserConfigWriterBoundTests` (Linux) |
| every read in the product classified by whose file it names, a file someone else controls read by its hardened reader — a new, a stale or a wrongly read site fails, naming it; the scan finds a planted read across lines and the known hardened ones | `ArchitectureTests.Every_read_is_classified_by_whose_file_it_names_and_uses_that_class_s_reader` (+ companion) |
| an `act` run's detail names every setting not from the defaults with its layer, and the notices; a run under the defaults records exactly what it did before | `Actions/ConfigProvenanceTests` |
| `contracts/config-keys.json` is what `ConfigKeys` + `default.json` enumerate; it carries every key, the closed families and the trust of the keys R1 is about | `ContractFilesTests` (+ `The_config_keys_contract_holds_every_key_with_its_trust`) |
| a root-run scenario finds interop as a WSL distro has it: `ScenarioHome` writes the `WSLInterop` entry (enabled) when a scenario claims root, and a scenario deletes it for the other world | `ConfigTrustFlows` |

**Red first** (before the fix):

- `ConfigKeyClosureTests.A_families_list_cannot_widen_A11_beyond_the_named_families` (Windows): 4 of 4 — *Expected type
  to be …ValueCheck+Invalid, but found …ValueCheck+Ok* for `other`, `ai-agents`, `testhost,other`, `anything-at-all`.
- `ConfigLayerTrustTests` (WSL, a normal user, a `/tmp` copy): 3 of 4 — the FIFO: *Expected finished to be True because
  the user layer is read without waiting on a FIFO, but found False* (the load blocked 5 s); the link and the
  group-writable layer: *Expected type to be …ConfigLoadResult+ObserveOnly, but found …ConfigLoadResult+Valid*; the
  ordinary layer green.
- The other guarantees are new behaviour written with the code; each was proved by the teeth below instead.

**Teeth** (each load-bearing line broken alone, rebuilt, the guarding class run, the file restored by writing it back and
compared by SHA-256 — every restore byte-identical; `scratchpad` runner, Windows Debug unless marked WSL):

| Mutation | Red |
|---|---|
| `CheckMembers` accepting any member | 4 (`ConfigKeyClosureTests`): *Expected type to be …Invalid, but found …Ok* |
| the user layer read with the plain `ReadFile` (WSL) | 3 (`ConfigLayerTrustTests`): the FIFO waited on, the link and the 0664 layer *Valid* |
| `TightenOnly` ignoring `LoosenRefused` | 2 (`UserLayerTrustTests`): *Expected …DryRun to be True, but found False*; *…to be 30 because 14 is tighter than the default but looser than the machine's 30, but found 14* |
| `RootsOwnKeyRule` never applying | 1: *Expected …MinimumLevel to be the same string, but they differ at index 0* |
| the machine-only branch of the loader dropped | 1: *Expected fromUser.Config.Text(…BaseFolder) to be empty, but found "/srv/archive"* |
| `config set`'s machine-only refusal dropped (built binary) | 1 (`ConfigTrustFlows`): *Expected refused.Exit to be 2, but found 0* |
| `TargetUserCommands` back on the plain `ReadFile` | 1 (`ArchitectureTests`): *… found at least one item {"WslCare.Core/Actions/TargetUserCommands.cs: 1 x ReadFile"}* |
| the act detail's `Config` set to null | 1 (`ConfigProvenanceTests`): *Expected detail.Config to contain a single item, but found &lt;null&gt;* |
| `status`'s `ConfigNotices` set to null (WSL) | 1 (`ConfigTrustFlows`): *Expected statusJson["configNotices"] not to be &lt;null&gt; because status says which user values the run did not take* |
| `CliHost`'s interop check wired to "available" (WSL) | 1 (`ConfigTrustFlows`): *Expected Value(report, "dryRun").GetBoolean() to be True, but found False* |
| `ReadNoFollow` following links (WSL) | 1 (`NoFollowReaderTests`): *Expected type to be …Unreadable, but found …Content* |
| `UserConfigWriter` back on the plain `ReadFile` (WSL) | 1 (`UserConfigWriterBoundTests`): *Expected finished to be True because config set never waits on a FIFO, but found False* |
| the user-layer digest left empty | 1 (`UserLayerTrustTests`): *Expected …UserLayerDigest to be "44136fa3…" … but "" has a length of 0* |

**Goldens** regenerated in WSL (`WSL_CARE_WRITE_GOLDENS=1`, a `/tmp` copy, copied back and compared byte for byte): the
seven `status*.json` gained `config.contract` in `capabilities`, `status.json` also `userLayerDigest` (its scenario has a
user layer) — additive members, nothing else moved; the extension's `npm test` (351, 1 skipped) replays them green.
`contracts/config-keys.json` is new. The umask a WSL Ubuntu login shell gives a normal user was measured 0022 (2026-10-05,
`bash -lc umask`), so a hand-made layer is 0644 there; `config set` writes 0644 whatever the umask.

### The E7.S0 review round (2026-10-05, plan §15q *E7.S0 review round*)

Two own reviews (security; correctness), every finding accepted. The tests — `Core.Tests/Config/ConfigReviewRoundTests`,
`Core.Tests/Files/DriveReaderTests`, `Core.Tests/Actions/ActionsReviewRoundTests`, `ArchitectureTests.The_read_scan_finds_a_read_through_a_wrapper`,
`Scenarios/ConfigReviewRoundFlows` — and their red before the fix (Windows, and WSL for the Linux-only ones: a normal user,
a `/tmp` copy, removed):

| # | Test | Red before the fix |
|---|---|---|
| S1 | `DriveReaderTests.A_link_in_a_parent_folder_of_a_drive_file_is_never_followed` (WSL) | *Expected type to be …Unreadable, but found …Content* — `.docker` a link to another folder, read through |
| S1 | `DriveReaderTests.A_windows_profile_with_a_parent_segment_is_not_a_path_on_the_drive` (3 cases) | *Expected HealthCollector.InDistro(profile, "/mnt/").IsAvailable to be False, but found True* |
| S2 | `ArchitectureTests.The_read_scan_finds_a_read_through_a_wrapper` | the scan found none of `ProcText.Read` / `Bytes`, `RegularFiles.Read` / `ReadHead`, `ReadText` |
| S4 | `ConfigReviewRoundFlows.Doctor_text_never_carries_a_terminal_control_sequence_from_the_user_layer` | *Did not expect doctor.Stdout … to contain "\u001b"* — the unknown key `ESC]52;c;…` printed raw |
| S5 | `ConfigReviewRoundTests.A_pattern_key_refuses_a_value_with_a_trailing_newline` (2 cases) | *Expected type to be …Invalid, but found …Ok* for "Ubuntu\n", "x\n" |
| S6 | `ConfigReviewRoundTests.A_link_on_the_way_to_the_user_layer_is_refused_and_nothing_beyond_it_is_described` (WSL) | *Expected type to be …ObserveOnly, but found …Valid* — a linked `~/.config/wsl-care` followed |
| S7 | `ActionsReviewRoundTests.An_idle_orphan_of_another_non_root_account_is_never_a_suspect` | *Expected preview.Targets … {"10 p10"}, but {"10 p10", "20 p20"} contains 1 item(s) too many* |
| C1 | `ConfigReviewRoundFlows.A_refused_configuration_never_prunes_the_logs_with_the_default_retention` (Windows) | *Expected Directory.Exists(old) to be True …, but found False* |
| C2 | `ConfigReviewRoundFlows.Without_interop_an_unprivileged_answer_says_which_user_values_the_root_timer_ignores` (WSL) | *Expected config["configNotices"] not to be &lt;null&gt; …* |
| C3 | `ConfigReviewRoundTests.A_linked_user_layer_is_refused_with_how_to_fix_it` (WSL) | *Expected result.Errors "cannot be read: a symbolic link, never followed" to contain "replace the link with a regular file"* |
| C3 | `ConfigReviewRoundFlows.Config_set_over_a_linked_user_layer_writes_a_regular_file_and_prints_the_value_it_wrote` (WSL) | *Expected set.Exit to be 0* — the layer refused, the set printed the default; the first fix attempt (moving the link aside) failed 70: the deletion policy judged the link's TARGET, outside the scope — hence `ReplaceLinkWithFile`, judged where the link lives |
| C4 | `ConfigReviewRoundTests.An_invalid_machine_only_value_in_the_user_layer_is_a_notice_not_an_error` | *Expected result.IsObserveOnly to be False, but found True* |
| C5 | `ActionsReviewRoundTests.A_machine_busy_a_minute_ago_is_not_idle_over_a_longer_window` | *Expected IdleGate.Judge(…).Idle to be False … but found True* |

The C1 test first passed VACUOUSLY on Linux — the old folder was planted in the user log root while the sandbox's `/var/log`
(writable by the test) was the one logged to; it now plants the folder in BOTH roots and asserts the run logged. Three older
tests changed with a decided behaviour: `SuspectTerminationTests` / `PidfdSignalsTests` name the target user (S7), and
`ActionEngineTests`' deferral reads 97.5 % (load1 3.9 on 4 CPUs — the highest average, C5); `ConfigLayerTrustTests`' link
refusal reads "a link … never followed".

**Teeth** (each load-bearing line broken alone, rebuilt, the class run, the file restored byte-identical by SHA-256):

| Mutation | Red |
|---|---|
| the drive reader back to a last-component `O_NOFOLLOW` (WSL) | 1: *Expected type to be …Unreadable, but found …Content* |
| `IsPlainDrivePath` without the `..` check | 3: *…IsAvailable to be False, but found True* |
| the scan without the `ProcText` alternative | 2: the companion, and the table *… ContainerCgroups.cs: ProcText.Read (in the table, not in the source)* |
| `doctor`'s line without `Printable` | 1 (`ConfigReviewRoundFlows`) |
| the whole-value match back to `IsMatch` | 2: *Expected type to be …Invalid, but found …Ok* |
| `ReadUserFile` back to the last-component reader (WSL) | 1: *Expected type to be …ObserveOnly, but found …Valid* |
| A11's target-user check dropped | 1: *… contains 1 item(s) too many* |
| the observe-only prune guard off | 1: *Expected olds to contain only items matching Exists(old) …* |
| `CliHost` not asking how root reads the layer (WSL) | 1: *Expected config["configNotices"] not to be &lt;null&gt;* |
| the link fix text not chosen (WSL) | 1: *… "…; fix or remove the file; config set rewrites it" to contain "replace the link with a regular file"* |
| `config set` not replacing the link (WSL) | 1: *Expected set.Exit to be 0* |
| the machine-only notice after validation | 1: *Expected result.IsObserveOnly to be False, but found True* |
| the idle gate back to one average | 1: *Expected IdleGate.Judge(…).Idle to be False … but found True* |

**Goldens** regenerated in WSL: `status.json` and `doctor.json` gained `configNotices` ("the root timer ignores this value:
…" for the golden layer's five 0-day ages — the golden sandbox has no interop entry); additive, nothing else moved.

### The AI agents: catalogue, discovery, the walk (E7.S1, 2026-10-05, plan §15q D1–D3, R2)

What `agents list` and the daily walk may do inside an agent's folder — size and count, nothing else
(`research/architecture.md` § *The AI agents*). The tests:

| Guarantee | Tests |
|---|---|
| the catalogue is complete (ids unique, every folder spelt from a known root), a session layout starts in one of its agent's own folders and only a confirmed agent counts sessions | `Core.Tests/Agents/AgentCatalogueTests` |
| every catalogue folder is a protected agent root on its side, and the protected roots are exactly the catalogue's (held equal) | `AgentCatalogueTests.Every_catalogue_folder_is_a_protected_agent_root_on_its_side` |
| the never-list's agent names are DERIVED from the catalogue: every catalogue folder in an argument is refused, the names it had before are a subset, a bare `claude` is not one | `AgentCatalogueTests.The_never_list_protects_every_catalogue_folder_in_an_argument` |
| `memory` is never entered for ANY agent — a manual entry without it in its own list too — and the size says it excludes it | `AgentWalkTests.Memory_is_never_entered_for_any_agent_and_the_size_says_it_excludes_it` |
| an entry's prefix is never entered (Gemini CLI's `antigravity*`), so one agent is not counted inside another | `AgentWalkTests.An_entrys_prefix_is_never_entered_…` |
| a link inside an agent folder is neither counted nor entered | `AgentWalkTests.A_link_inside_an_agent_folder_is_neither_counted_nor_entered` |
| a folder on another filesystem is not entered and is named "(different filesystem)" (review C1; the device seam makes it a unit test on every OS) | `AgentWalkTests.A_folder_on_another_filesystem_is_not_entered_and_is_named` |
| ONE total budget: the time left is read once per folder; a folder the budget did not reach says "not measured this run", never 0, and its sessions are not counted (review M7) | `AgentWalkTests.The_walk_stops_at_its_total_budget_and_names_what_it_did_not_reach` |
| sessions of a confirmed layout counted from listings, with oldest / newest dates and the five largest by name; a `memory` folder where the layout's `*` would match it is not entered | `AgentWalkTests.Sessions_of_a_confirmed_layout_…` |
| an unconfirmed layout is "—" with "monitor only"; a missing folder "does not exist", not 0 | `AgentWalkTests.An_unconfirmed_layout_…` |
| what is persisted carries no session name (the size of the largest is kept) | `AgentWalkTests.A_persisted_sample_carries_no_session_name`; `AgentsFlows` (after a `collect`) |
| discovery: by binary on PATH, by npm package (its version from `package.json`), by folder alone; a native install's version from its link target; otherwise "not asked … nothing is executed" | `Agents/AgentDiscoveryTests` (Linux for the PATH rule) |
| as root only folders count, no version asked | `AgentDiscoveryTests.As_root_only_folders_count_and_no_version_is_asked` |
| discovery and the walk have no way to start a process (no `ICommandRunner` in any signature) | `AgentDiscoveryTests.Discovery_and_the_walk_have_no_way_to_start_a_process` |
| **H3 at the syscall level**: an inotify `IN_OPEN \| IN_ACCESS` watch on every folder of a Claude Code tree (memory included) sees no FILE opened during the walk and the session listing; the companion sees a file that is opened | `Agents/AgentNoOpenTests` (Linux) |
| the built CLI: found by binary and folder, 100 bytes without `memory/`, one session named, version "not asked", the fake `claude` on PATH never started; text; usage; `none` before a full run, the run's totals after | `Scenarios/AgentsFlows` |
| the new read sites are classified: `AgentDiscovery` (`ReadRegularFile` of the invoking user's own `package.json` — class `OwnUnprivileged` —, `ListEntries`), `AgentWalk` (`WalkTree`), `SessionGlob` (`ListEntries`) | `ArchitectureTests.Every_read_is_classified_…` |

**Red first.** The behaviours were written with their tests; red was observed where it could be, and the rest proved by
the teeth below:

- `AgentWalkTests.The_walk_stops_at_its_total_budget_…` was red on the first run for a REAL defect: *Expected … Reason to
  be "not measured this run: …", but "stopped after -60 s …"* — the walk read the clock twice (once for "out of time?",
  once for "how much is left"), so a folder could start with a negative ceiling. Fixed: the time left is read ONCE.
- `VerbRegisterTests` (2) red while the verb had no flow-catalogue row: *missing: agents list [--measure] [--json]*.
- Two test-side corrections, not product defects: the session names are relative to the layout's folder
  (`projects/b/two.jsonl`), and on Windows the PATH rule is the Windows one, so the PATH-detection facts run on the Linux
  legs; Claude Code has three Windows folders, so the Windows flow asserts the one that exists.

**Teeth** (each load-bearing line broken alone, rebuilt, the guarding class run, the file restored byte-identical by
SHA-256; Windows Debug unless marked WSL):

| Mutation | Red |
|---|---|
| `memory` dropped from every entry's never-enter set | 1: *Expected sample.Find("manual")!.TotalBytes to be 7L because memory/ is never entered for ANY agent …, but found 50007L* |
| the prefix rule matching nothing | 1: *Expected size.TotalBytes to be 40L, but found 9040L* |
| `StayOnDevice = false` | 1: *Expected measured.Bytes to be 10L, but found 30010L* |
| the budget's time left ignored (the per-folder ceiling only) | 1: *Expected late.Folders.Single().Reason to be "not measured this run: …", but "" …* |
| the session listing entering `memory` | 1: *Expected size.Sessions to be …SessionFigures …* |
| the persisted sample keeping the names | 1: *Expected persisted.Agents to contain only items matching (a.Largest == null) …* |
| the same, through the built CLI after a `collect` (WSL) | 1 (`AgentsFlows`): *Expected Claude(after).Sessions.LargestSessions to be &lt;null&gt; …* |
| root searching the PATH (WSL) | 1: *Expected Of(found, "claude-code").Tracked to be False because root never searches the user's PATH, but found True* |
| the never-list missing one derived name | 1: *Expected unprotected to be empty, but found … {"/home/me/.aider"}* |
| the Linux protected roots missing one catalogue folder | 1: *Expected linux.AgentRoots to be a collection with 14 item(s) …* |
| the session listing opening each session file for one byte (WSL) | 1 (`AgentNoOpenTests`): *Expected watch.FileEvents() to be empty …, but found … {"…/home/me/.claude/projects/a/one.jsonl"}* |
| `--measure` not parsed | 2 (`AgentsFlows`): *Expected report.Sizes.Source to be "now" …, but "none"*; the text without "measured now" |

**Goldens** regenerated in WSL: `agents-list.json` is new (the golden sandbox plants a Claude Code folder before its
`collect`: two sessions at fixed instants and a `memory` file, so the answer is the full run's — 400 bytes, 2 sessions,
`excluded: ["memory (never entered)"]`, no session name); the seven `status*.json` gained `agents.list` in
`capabilities`; nothing else moved. A new rule normalises `answeredAt`.

**Measured** (2026-10-05, WSL, a normal user, the Release build, `agents list --measure` over the real home — totals only
recorded): six agents tracked, ~2.5 GiB, 185 sessions (Claude Code, Codex) and 500 Antigravity conversations counted; three runs 2.26 s,
1.29 s, 1.32 s — far inside the 60 s `--measure` budget and the 3-minute walk budget of a full run.

### Manual agents and agents probe (E7.S2, 2026-10-05, plan §15q D4, R2)

`aiAgents.extra`, the rules a manual agent's folder must pass at `config set` and at every root read, the two-phase host,
the declared cleanup folders and the overlap refusal, and `agents probe` (`research/architecture.md` § *Manual agents and
`agents probe`*). The tests:

| Guarantee | Tests |
|---|---|
| the SHAPE of `aiAgents.extra` (the fifth key shape): a valid list taken with every member and written back unchanged; each rule refused naming the entry and the rule — a relative folder, a `.`/`..` segment, a leading `-`, a control character, 0 or 9 folders, a relative `cli`, an unknown side, a bad name (a trailing newline too: `\z`, not `$`), a `..` / absolute / bracketed glob, an unknown member, wrong member types, a Windows entry without a drive path; at most 16 entries, no name twice; not JSON, not a list | `Core.Tests/Agents/ExtraAgentShapeTests` (21) |
| the FILESYSTEM rules (R2.1): accepted inside the home; refused outside it, the home itself, under `~/git`, a catalogue folder or inside one ("already tracked"), A8's `~/.npm`, inside A12's Playwright folder, A17's pnpm store, A14's editor server, wsl-care's own `~/.config/wsl-care` and a folder containing it (M9), a folder that does not exist, a folder on another device (review C1), a link inside the home pointing out of it (the REAL path decides; Linux); a Windows entry left to the Windows binary | `Agents/ExtraAgentRulesTests` (17) |
| every user-scoped action declares the home folders it cleans (A3 the named exception), every `CacheFolders.UnderHome(context, "…")` literal in an action's source is declared (+ the companion that the scan finds A12's), no catalogue folder overlaps a declared cleanup folder (review M8) | `Actions/ActionHomeRootsTests` |
| an action whose cleanup folder overlaps an agent's folder refuses and never runs; the run's deletion policy refuses a delete under a manual agent's folder, and the same delete without the extra is allowed (review B2) | `ActionHomeRootsTests` |
| an accepted manual agent is walked without `memory`, its sessions counted by its own glob (with `**`); a refused one stays in the answer, not walked, every figure "not walked: <rule>"; the `cli` is never a file-system argument (a recording double); a Windows entry is neither listed nor walked by the distro's binary | `Agents/ExtraAgentsTests` |
| an agent's total is unavailable with the folder's reason when a folder was not measured (not reached, refused), a missing folder counts as nothing, a cut walk as its lower bound; growth only between two whole walks | `Agents/AgentsReportTests` |
| `agents probe`: usable, named by its file, the conventional folders that exist measured and judged, the suggested entry; a catalogue binary "already tracked"; a path that is no file not usable and nothing looked at; no execute bit (Linux); the name derivation; **the probe opens no file of the CLI or its folders** (inotify, Linux) | `Agents/AgentProbeTests` |
| `config set aiAgents.extra -` reads stdin, judges, writes; a refused folder names its rule and writes nothing; stdin only (a JSON argument refused), not JSON refused; `agents probe` as root: exit 81 naming uid 0 and the fix; a path of the wrong shape refused; the JSON contract; **the second phase of the host protects the manual agents' folders**, and no extras = the same host (review M1) | `Cli.Tests/AgentsCommandTests` (11) |
| the built CLI: the probe as root (81) on every OS; a probe of a CLI (the fake tool) never starts it; `agents list --measure` with an accepted and a refused manual agent; a root `collect` walks an accepted one and `agents list` reads its total | `Scenarios/AgentsExtraFlows` (the last three on the Linux legs) |
| the contracts: `config-keys.json` gains `aiAgents.extra` (shape `agentList` and its limits), `exit-codes.json` gains `notAsRoot` 81 | `ContractFilesTests` |

**Red first.** The behaviours were written with their tests and proved by the teeth below; red observed for a real
symptom where it could be:

- `AgentsExtraFlows.Agents_list_shows_the_manual_agents_…` (WSL) was red for a REAL defect, in E7.S1's report: *Expected
  refused.TotalBytes.Available to be False, but found True* — an agent whose folders were not walked reported an
  "available" total of 0 (the same held for a folder the budget did not reach). Fixed: `AgentsReports.Total`; growth only
  between whole walks; `AgentsReportTests` pins it.
- `ExtraAgentRulesTests.A_link_inside_the_home_pointing_out_of_it_…` was SKIPPED on Linux at first — the test created the
  link before its parent folder existed, so `DirectoryLinks.TryCreate` failed and the test skipped instead of running.
  Found by listing the skipped tests of the WSL run; fixed in the test, now it runs (green) on Linux.
- `VerbRegisterTests` (2) red while `agents probe <path> [--json]` had no flow-catalogue row; `ContractFilesTests` red while
  the contracts lacked `aiAgents.extra` and exit 81.

**Teeth** (each load-bearing line broken alone, rebuilt, the guarding class run, the file restored byte-identical by
SHA-256; Windows Debug unless marked WSL):

| Mutation | Red |
|---|---|
| the name pattern anchored with `$` instead of `\z` | 1: *Expected type to be …Invalid, but found …Ok* (the "x\n" name) |
| the `.`/`..` segment check dropped | 1: *… Invalid, but found … Ok* |
| the inside-the-home rule dropped | 2: *Refusal "/home/me overlaps ~/git …" to contain "is not inside the home"*; *Refusal "" to contain "/data/mycli"* |
| the device rule dropped | 1: *Refusal "" to contain "another filesystem"* |
| the declared cleanup folders left out of the rules (M8) | 4: `~/.npm`, the Playwright folder, `~/.vscode-server`, the pnpm store accepted |
| the product's folders left out (M9) | 2: `~/.config/wsl-care` and `~/.config` accepted |
| A12 not declaring the NuGet http-cache | 1: *undeclared … {"A12: ~/.local/share/NuGet/http-cache"}* |
| the engine's overlap guard off (B2) | 1: *Expected outcome.Status to be "refused" …, but "ran"* |
| the manual agents' folders left out of `AgentRoots` (B2) | 2: the overlap test ran the action; the delete under the extra *…+AllowedVerdict* |
| the second phase of the host never applied (M1) | 1: *Expected second.Paths.AgentRoots … to contain …/.mycli* |
| the probe's root refusal off | 1: *Expected probe.Exit to be 81, but found 0* |
| `config set` taking a JSON argument | 1: the refusal named "not JSON" instead of "is read from stdin" |
| a refused manual agent walked | 1: *Expected size.TotalBytes to be 0L, but found 5000L* |
| discovery stat-ing the manual agent's `cli` | 1: *Expected recording.Asked … not to contain … .local/bin/mycli* |
| the total available whatever a folder says | 2: *…TotalBytes.Available to be False because 100 bytes of one folder are not the agent's size, but found True*; growth from a partial walk |
| the probe reading the CLI (WSL) | 1 (`AgentProbeTests`): *Expected watch.FileEvents() to be empty …, but found … {"…/home/me/.local/bin/mycli"}* |

Two first attempts did not bite and were redone: the two-phase mutation did not compile (a nullable warning is an
error here), and the first "probe reads the CLI" mutation changed the probe's answer instead of only reading — so four
tests failed for the WRONG reason (a usable CLI reported unusable); the second version reads and answers as before, and
only the inotify proof went red.

**Goldens** regenerated in WSL: the seven `status*.json` gained `agents.probe` and `config.agentsExtra` in `capabilities`;
nothing else moved (the golden sandbox has no manual agent, so `agents-list.json` is unchanged).

### The E7.S1/S2 review round (2026-10-05, plan §15q *E7.S1/S2 review round*)

Two own reviews (correctness: 3 High, 5 Medium, 5 Minor; safety/security: 5 Important, 4 Low), every finding accepted. The
tests — `Core.Tests/Agents/AgentsReviewRoundTests` (23), `UserCacheTests.S4_*` (2), `AgentNoOpenTests` (the folder-event
assertion), `Cli.Tests/AgentsCommandTests` (R12, S1 ×3), `ActCommandTests` (81), `Scenarios/AgentsExtraFlows` (R8) — and their
red before the fix (Windows, and WSL for the Linux-only ones: a normal user, a `/tmp` copy, removed):

| # | Red before the fix |
|---|---|
| R1 | WSL: `~/.local/bin/claude` not found under the measured exec PATH shape; a `/mnt/c/…/npm/codex` shim *Expected … DetectedBy {"binary"} to not contain "binary"* |
| R2 | *Expected report.TotalBytes to be ByteFigure(true, 100, "stopped after …")* — the cut walk read as whole, growth taken |
| R3 | WSL: *Expected codex.Version to be "0.44.0" …, but "0.30.0"* (the oldest nvm); *… "2.1.3" …, but "1.0.0"* (a stale package beat a two-hop native link) |
| R4 | `**/**` accepted; a folder listed twice; *Expected sessions.Counted to be False …, but found True* for a listing stopped before the sessions' level; WSL: a linked `~/.claude` listed through the link |
| R5 | *Expected … Count(NotReached) to be greater than 0 …, but found 0* — four candidates, four budgets |
| R6 | a folder not measured reported `files: 0` |
| R7 | *Expected size.Sessions.LargestBytes to be 5400L …, but found 2000L* — the companions not counted |
| R9 | *… excluded {"projects/secret-client-project (different filesystem)"}* persisted |
| R10 | *Expected type not to be …Written …, but it is* — a 786 KB layer written past its reader's 256 KiB cap |
| R11 | WSL: a file with an execute bit for OTHERS only was "usable" for its owner |
| R12 | *the path ; got "/home/me/.local/bin/a"* |
| 81 | *Expected exit to be 81, but found 2* (`config set` as root for the target user) |
| S1 | *Expected second.Paths.AgentRoots … not to contain …* for `/var/lib/wsl-care`, `/` and the home itself |
| S2 | `~/.mycli/memory`, `~/.mycli/Memory`, `~/memory/mycli` accepted (3); *Expected type to be …Unreadable, but found …Measured* for a walk rooted at `memory` |
| S3 | *Reason "" to contain "another filesystem"* — a catalogue folder on another device than the home walked |
| S4 | *Expected preview.Refusal "" to contain "/home/me/.claude/npm-cache"*; *… Wrapped {"pip3 cache purge"} to not contain "pip3 cache purge"* |
| S8 | *Expected …TotalBytes to be 10L, but found 9010L* — a `Memory` folder entered |

Two reds were the TEST's, found and fixed before relying on them: the R1 PATH shape first mixed this machine's real `/usr/bin`
with the sandbox (the WSL run found the owner's real `/usr/bin/codex`) — every entry is now inside the sandbox; and the
strengthened S9 assertion first saw the memory folder "opened" by the watch's OWN set-up (it lists every folder to add its
watches) — the set-up's events are drained before the walk.

R8's scenario was red for a REAL defect of E7.S2: *Expected a12.Reason "nothing to remove; the Playwright part refuses: …" to
contain "overlaps the AI agent folder"* — the action's SKIP was read before the overlap refusal, so A12 over an agent folder
read as "nothing to do", not "refused". The overlap refusal now replaces the action's own refusal and its skip.

**Teeth** (each load-bearing line broken alone, rebuilt, the guarding class run, the file restored byte-identical by
SHA-256; Windows Debug unless marked WSL) — 27 mutations, every one red:

| Mutation | Red |
|---|---|
| the automount filter dropped (WSL) | 1 (R1 shim) |
| the fixed bin list dropped (WSL) | 3 (R1 local bin, both R3) |
| the cut total without its reason | 1 |
| growth without the whole-walk rule | 1 |
| the second `**` accepted | 1 |
| the listing cache dropped | 1 (listed twice) |
| the intermediate stop ignored | 1 |
| the home-device rule dropped | 1 |
| a budget per probe candidate | 1 |
| an unmeasured folder's file count kept | 1 |
| companions not counted | 1 |
| persisted walks keeping folder names | 1 |
| the layer-size check dropped | 1 |
| the probe's "one path" rule dropped | 1 |
| `config set` as root back to 2 | 1 |
| protection unbounded | 3 |
| a memory segment in a manual folder accepted | 3 |
| a walk rooted at `memory` allowed | 1 |
| the tool not asked (A8 / A17) | 2 |
| `memory` compared by case | 1 |
| the link-on-the-way rule dropped (WSL) | 1 |
| only one link followed (WSL) | 1 |
| the version from the npm roots first (WSL) | 2 |
| any execute bit (WSL) | 1 |
| `memory` not never-enter (WSL, the folder-event proof) | 1 |
| `Program.Main` without phase two (WSL, the built CLI) | 1: the A12 reason without the overlap |
| the overlap refusal leaving the action's skip in place (WSL, the built CLI) | 1: *… "nothing to remove; …" to contain "overlaps the AI agent folder"* |

One first attempt (the listing cache mutated so it did not compile — a nullable warning is an error here) was redone.

**Goldens:** `agents-list.json` — the "not asked" sentence names the new source rule; nothing else moved.

### A18 — orphaned AI-agent processes (E7.S2b, 2026-10-05, owner decision)

| Guarantee | Tests |
|---|---|
| eligible only after N hours with NO CPU measured by identity across the timer's records; the first sight and 3 h are not enough, 4.5 h is, the item names the agent, the measured hours and the folder | `Core.Tests/Actions/AgentOrphansTests.Ai_agent_orphan_is_eligible_only_after_N_hours_without_cpu_by_identity` |
| missing history is never idle — 30 days of uptime with no record ends nothing | `…Missing_history_never_makes_a_process_eligible` |
| a CPU tick, a reused pid (another start) or another boot restarts the clock; idle again only N hours later | `…A_reused_pid_or_a_cpu_tick_or_another_boot_restarts_the_idle_clock` (3) |
| a live session of its agent keeps it; an unconfirmed layout keeps it; a terminal or a live parent keeps it; another account's process is never judged | `…A_live_session_keeps_the_agent_process`, `…An_unconfirmed_layout_…`, `…A_process_with_a_terminal_or_a_live_parent_is_kept` (2), `…Another_accounts_agent_process_is_never_a_candidate` |
| the run re-reads each target: one that used CPU since the preview is kept, the other is signalled by pid and start | `…The_run_rechecks_identity_and_cpu_before_each_signal` |
| the timer never ends an agent process: the timer pass does not select A18, asking the timer engine for it directly is skipped, the trigger never fires | `…The_timer_never_ends_an_agent_process`; over the built CLI: `Scenarios/AgentOrphansFlows` (every `auto` on, dry run off, 1 h window: the pass holds no A18, the history was recorded, a preview after it ends nothing and writes no state; Linux legs) |
| the history is root state, pruned to live identities, capped at 512, and a full file fits its read cap | `…The_cpu_history_is_root_state_bounded_and_pruned_to_live_processes` |
| `processes.aiAgentsIdleHours` 1–168, default 4, safe higher; `ai-agents` still not choosable for A11 | `…Processes_aiAgentsIdleHours_is_1_to_168_default_4_safe_higher`; `ConfigKeyClosureTests` |
| A18 is the one button-only id; asking its auto switch is a defect | `EnginePartsTests.The_action_ids_are_exactly_the_auto_switches_and_the_button_only_ones_…` |
| A11's signal path, now shared (`SuspectSignals`), behaves as before | `SuspectTerminationTests`, `ActionsReviewRoundTests` (unchanged, green) |

**Red first:** the cap test was red for a REAL defect — *a full 512-entry history serialises to 90 618 bytes, over the planned
64 KiB cap*, which would have read a full history as "no history"; the cap is 128 KiB. The scenario was red once for its own
set-up (the sandbox had no `/proc/sys/kernel/random` folder). The rest was written with its code and proved by the teeth.

**Teeth** (Windows Debug, `AgentOrphansTests`, each file restored byte-identical):

| Mutation | Red |
|---|---|
| the timer pass selecting button-only ids | 1: the pass held A18 |
| the timer gate letting a button-only id run | 1: the scripted A18 ran |
| the history keyed by pid alone | 1 (the start case) |
| the history surviving another boot | 1: *a changed boot … found 1* |
| a new identity starting "idle for a year" (missing history = idle) | 5: the first sight, 30 days, every restart case |
| the live-session check never matching | 1: *Expected preview.Count to be 0, but found 1* |
| the confirmed-layout rule dropped | 1 |
| the target-user filter dropped | 1: *… found 1* |
| the live-parent rule dropped | 1: *… found 1* |
| the run's CPU re-check dropped (shared path) | 1: the process that used CPU was signalled |
| dead identities kept | 1 |
| the trigger firing | 1 |

Two first attempts (the identity and missing-history rules mutated in `IdleFor`) did not bite: the in-memory merge of
`Next` already enforces both, so the teeth moved to `Next`, where the rule lives.

**Goldens:** the seven `status*.json` gained `A18` in `actions`; `contracts/actions.json` and `config-keys.json` regenerated.

### Every number is configuration (E7.S2c, 2026-10-05, owner rule)

| Guarantee | Tests |
|---|---|
| no behavioural number is a literal in `src_daemon/src`: five shapes (a numeric `const` / `static readonly`, an inline `TimeSpan.From*(<digit>)`, `Take(n ≥ 2)`, a byte product, a literal handed to `Sleep` / `Next` / a capped read) outside the 114 reasoned group-C entries | `Core.Tests/ArchitectureTests.Numbers.No_behavioural_number_is_a_literal`; the companion plants one of each shape (`…The_number_scan_finds_a_planted_literal_of_every_shape`); a listed entry that is gone is stale (`…Every_listed_format_still_exists_in_the_source`) |
| every A row an ordinary key, every B row a machine-layer-only key; a value above the hard maximum refused naming the key and the bound; a user-layer B value a notice, the default kept | `Core.Tests/Config/NumbersAreConfigurationTests.A_behaviour_number_is_an_ordinary_key` (35), `…A_root_safety_limit_is_a_machine_only_key` (91), `…A_limit_above_its_hard_maximum_is_refused_…` |
| coupled limits that contradict each other refuse the layer naming the rule — wedged < 3 × heartbeat, a read cap below the queue, a list cap that cannot hold the shown names, a log range shorter than the retention, a stop ceiling not 30 s above the unit's stop, jitter max ≤ min, a timer period that does not divide 24 | `…Coupled_limits_that_contradict_each_other_refuse_the_layer_naming_the_rule` (7) |
| every timeout key's range stays under `commands.maxTimeoutHours`' minimum | `…Every_timeout_key_stays_under_the_commands_maximum` |
| a machine value reaches the code it bounds: a read command's ceiling, a keyed template's (read when a request is built), a fixed read template that follows its command, the Docker batch, the unit stop template, the heartbeat and wedged times, the walk's limits, `CommandRequest.MaxTimeout`; outside the scope the defaults are back | `…A_machine_value_reaches_the_command_the_wait_and_the_walk_it_bounds` (the templates read under the defaults FIRST) |
| the defaults are today's values (behaviour unchanged) | `…A_default_is_todays_value_so_behaviour_is_unchanged` |
| N-6: a sentence says the number in force — A11 / A18's grace, A14's "newest N", the retention's days, the drift's minutes, the root-disk and fragmentation limits | `…A_sentence_says_the_number_in_force_not_a_copy_of_the_default`; `ClockFixTests.A_correction_less_than_an_hour_ago_…` reads "less than 60 minutes ago" |
| N-5: a capped read refuses one byte past its cap and reads a file at it; an uncapped read is bounded by `records.maxHistoryBytes`; the dry-run stamp past `records.maxStateFileBytes` is unreadable and the week restarts | `Files/PhysicalFileSystemTests.A_capped_read_refuses_one_byte_past_the_cap_…`, `…An_uncapped_read_is_bounded_by_records_maxHistoryBytes`, `…A_dry_run_stamp_past_the_state_cap_…`; the read-site table (`ArchitectureTests.ReadSites`) holds doctor's drop-in read as a system read |
| N-4: every `oneshot` unit's `TimeoutStartSec` is `infinity`, a `simple` unit sets none | `Scenarios/ShippedFilesTests.Every_unit_s_start_limit_is_infinity_or_above_the_worst_case_of_its_run` |
| the drop-in of the defaults says what each shipped unit file says; under a changed machine layer it carries the configured period, Nice, MemoryMax, TimeoutStopSec, RestartSec, and the two root services' drop-ins are equal | `ShippedFilesTests.The_drop_in_of_the_defaults_says_what_each_shipped_unit_says`, `…A_drop_in_carries_the_configured_values` |
| `units dropin <unit>` renders the machine layer's values; another unit is refused naming the four | `Cli.Tests/UnitsCommandTests` (2) |
| `install.sh` writes each unit's drop-in from the INSTALLED binary (0644, before any unit is enabled), a drop-in the binary cannot render fails the install at `install-units`, the dry run names each, the uninstall removes each and its emptied folder | `InstallFlows.A_fresh_install_…` (four `units dropin` calls before `collect`), `…A_drop_in_the_binary_cannot_render_fails_the_install_before_any_unit_is_enabled`, `…Dry_run_…`, `InstallUninstallFlows.Uninstall_removes_the_units_binary_and_link_…` |
| `doctor`'s `unitConfig`: a timer drop-in that no longer matches the machine layer is a `problem` naming `OnCalendar=*-*-* 00/6:00:00` and "run install.sh again"; matching drop-ins, and none under the defaults, are `ok` | `Doctor/DoctorTests.A_timer_drop_in_that_no_longer_matches_…`, `…Drop_ins_that_match_and_none_at_all_under_the_defaults_are_both_fine` |
| `status --json` publishes `limits` — the values in force, the machine layer's when set | `Cli.Tests/UnitsCommandTests.Status_json_publishes_the_limits_in_force` |
| `contracts/status-limits.json` carries the two names PR #12's `daemonLimits.ts` reads, and the daemon's JSON writer spells every field as the contract does, in its order | `Scenarios/ContractFilesTests.The_status_limits_contract_carries_the_names_the_extension_reads_and_the_writer_spells_them`; the equality test regenerates the file |

**Red first:** the structural test was red with **230 hits** before the migration (the scan's list, classified hit by hit
into a key or a reasoned format). The migration itself was red for a REAL defect: **607 Core tests failed** with *ValueFactory
attempted to access the Value property of this instance* — the configuration's own key patterns read `Tuning` while `Tuning`'s
defaults were being loaded; the fix is the bootstrap rule (the patterns are bounded by the key's maximum). The rest was written
with its code and proved by the teeth below. Test-side defects met on the way: the doctor test's machine layer was refused on
Linux (a sandbox file is not root's — the test now trusts the sandbox owner, as `SandboxHost` does); `ClockFixTests` expected
the old sentence "less than an hour ago".

**Teeth** (each file restored byte-identical; Windows Debug unless named):

| Mutation | Red |
|---|---|
| `CommandLimits.Keyed` reads the defaults instead of the configuration | 1: *Expected 1m and 1s … but found 15m* |
| a fixed read template freezes its command's limits | 0 at first — the template was first touched INSIDE the scope; the test now reads the templates under the defaults first: 1, *Expected 6s … found 20s* |
| the heartbeat back to `static readonly TimeSpan … = TimeSpan.FromSeconds(5)` | 1: the scan names `RunningState.cs: HeartbeatPeriod` |
| the physical capped read never refuses | 2 (the cap, the history bound) |
| the dry-run window reads its stamp uncapped | 1: the week did not restart |
| A11's summary back to "10 s" | 1: *… to contain "SIGKILL after 20 s"* |
| `wsl-care.service` back to `TimeoutStartSec=10min` | 1 (`ShippedFilesTests`) |
| the period-divides-24 rule dropped | 1: the period 5 loaded |
| a `limits` field renamed (`retentionDays`) | 2: the extension's names, the checked-in contract |
| `unitConfig` never a problem | 1 |
| `Program.Run` scoped to the defaults instead of the loaded configuration | 1: the drop-in kept `00/4` |
| `status` without `limits` | 1 |
| `install.sh` without `write_dropins` (WSL) | 3: the fresh install, the dry run, the failing render |
| the uninstall leaving the drop-ins (WSL) | 0 at first — no test looked; the uninstall test now asserts each `<unit>.d` is gone: 1 |

**Goldens:** the seven `status*.json` gained `limits`, `doctor.json` gained `unitConfig`; `contracts/config-keys.json` (126 new
keys) and the new `contracts/status-limits.json` regenerated.

### The E7.S2b/S2c review round (2026-10-06, plan §15q *E7.S2b/S2c review round*)

| Finding | Tests |
|---|---|
| A-H1: a manual A18 run without the processes its modal showed is refused; with them it ends only those still eligible; the preview answers `pid:start`; `--process` belongs to A18 and holds only `pid:start`; a detached run carries `shownProcesses` and a request with a bad one is refused | `Core.Tests/Actions/AgentOrphansTests.A_button_run_without_the_processes_its_modal_showed_is_refused`, `…A_button_run_ends_only_the_still_eligible_processes_its_modal_showed`, `…The_preview_answers_its_processes_as_pid_and_start_keys`; `Cli.Tests/ActCommandTests.A_shown_process_list_belongs_to_a18_…` (5), `…A_shown_process_list_parses_beside_a4_s_volumes`; `DetachedRunsTests.A_detached_a18_run_carries_its_shown_processes_into_the_request`; `RunRequestsTests` (the `shown process` case) |
| A-M1, A-M2: a wrapper with a child, a process systemd --user started — kept | `AgentOrphansTests.A_wrapper_with_a_child_process_is_kept`, `…A_process_the_users_systemd_manager_started_is_not_an_orphan` |
| A-M3: only a program that resolves into the agent's own install (native versions folder; node + the npm package's link chain) | `…Only_a_program_that_resolves_into_the_agents_own_install_is_that_agent` (4) |
| A-M4: a wall-clock jump does not pass the window (the shorter clock counts); a gap breaks the chain, and stays in it | `…A_wall_clock_jump_after_a_host_sleep_does_not_pass_the_idle_window`, `…A_gap_in_the_cpu_history_breaks_the_chain` (the clock: `ManualTimeProvider { SteppedTimestamps = true }`, `JumpWallClock`) |
| A-M5: no session found = cannot tell; a moved agent home keeps the process; the agent's own folder named explicitly does not | `…No_session_found_is_cannot_tell_and_a_moved_agent_home_keeps_the_process` |
| A-L1: the history is 0600, past the cap the oldest go, a full one fits its cap | `…The_cpu_history_is_private_to_root` (Linux), `…The_cpu_history_is_root_state_bounded_and_pruned_to_live_processes` |
| A-L2: an account changed since the preview is not signalled | `…A_process_whose_account_changed_since_the_preview_is_not_signalled` |
| A-L3: a hanging device stat while choosing the folders is inside the lookup's one ceiling | `Agents/AgentsReviewRoundTests.A_hanging_device_question_while_choosing_the_folders_is_inside_the_lookup_s_one_ceiling` |
| the gap: a tool's cache in a `~/git` or Claude's temp folder is refused | `UserCacheTests.A_tools_cache_answer_inside_a_git_or_claude_temp_folder_is_refused` (2) |
| C-H1, C-M7: a history past its cap is a problem — no reconcile line, every request kept, the rewrite refused and the file kept | `Records/HistoryCapTests` (3, a real 64 MiB + 1 history) |
| C-H2: a beating run with no step for the window is wedged (and `act --stop` is named); a step moves the progress time, a beat alone does not; the timer's unit has a finite limit above the derived worst case, the act unit `infinity` | `Status/RunningReportsTests.A_beating_run_that_made_no_step_for_the_watchdog_window_is_wedged`, `Actions/RunProgressTests` (2 — the beat test drives the heartbeat on a `ManualTimeProvider { DrivesTimers = true }`: each beat is an `Advance` the test takes and each read waits, under a 30 s ceiling, for exactly that beat's time; the old form slept 300 ms on the real clock and on the loaded win-x64 runner of 2026-10-06 read the file as first written, `Expected … after <begun> because the heartbeat beats on its timer, but it was <begun>` — reproduced 2 of 3 under an exhausted thread pool, green 5 of 5 there after; break-it, progress stamped on every beat: `Expected Read().ProgressAt to represent the same point in time as <begun> because no step was made`); `Scenarios/ShippedFilesTests.Every_unit_s_start_limit_is_infinity_or_above_the_worst_case_of_its_run`, `…A_drop_in_carries_the_configured_values`; the rules: `NumbersAreConfigurationTests.Coupled_limits_…` (`timer.runLimitMinutes`, `running.noProgressMinutes`), `…The_defaults_hold_every_coupled_rule` |
| C-M1: a user value that breaks a rule is a notice, the value below in force; `config set` refuses it | `NumbersAreConfigurationTests.A_user_value_that_breaks_a_rule_is_a_notice_and_the_value_below_stays_in_force`; `Cli.Tests/UnitsCommandTests.Config_set_refuses_a_value_that_would_break_a_rule_with_the_machine_layer` |
| C-M2: a machine value that breaks a rule is an error and not in force; `units dropin` refuses (78) | `…A_machine_value_that_breaks_a_rule_is_an_error_and_not_in_force`; `UnitsCommandTests.A_drop_in_is_refused_while_the_configuration_is_in_error` |
| C-M3: the composed event-stream ceiling stays under the maximum at every value the ranges allow | `…Every_composed_command_ceiling_stays_under_the_commands_maximum` |
| C-M4: `config set` keeps the cap its reader keeps | `UnitsCommandTests.Config_set_keeps_the_user_layer_under_the_cap_in_force` |
| C-M5: the summary reads only the last 24 h of day files; the retention is machine-only, ≤ 90 | `Events/StartsSummaryReadTests` (2) |
| C-M6: the three new coupled rules | `Coupled_limits_…` (`timer.lateSlackMinutes`, `agentCpu.maxBytes`, `requests.maxBytes`) |
| C-M8: a binary without the drop-in verb installs with no drop-ins, said, the stale one removed | `InstallFlows.A_binary_without_the_drop_in_verb_installs_without_drop_ins_and_says_so` (Linux) |
| C-M9: five more literal shapes, each planted | `ArchitectureTests.Numbers.The_number_scan_finds_a_planted_literal_of_every_shape` |

**Red first:** these were written beside their fixes; each was proved by reverting its load-bearing line (the teeth below). Met
red on the way: a full A18 history with both clocks is **157 072 bytes**, over the 128 KiB cap (the cap is 256 KiB now, held by a
rule); two Linux-only test defects (the no-session test met a missing sessions folder, which reads "cannot tell"; the cap test's
folders were made outside the distro view).

**Teeth** (each file restored byte-identical; Windows Debug unless named):

| Mutation | Red |
|---|---|
| the manual-run refusal dropped / the narrowing to the shown keys dropped | 1 / 1 |
| the child rule, the pid-1 rule, the install rule dropped | 1, 1, 3 |
| the wall clock alone; the dense-chain check dropped | 1; 1 |
| the no-session branch; the environment check | 1; 1 |
| the account compared as "not root" only | 1 |
| the history keeping the oldest (highest-start dropped) | 1 |
| the lookup's ceiling one minute | 1: *found 4 s* |
| the cache answer checked against agent folders only | 2 |
| `--process` without A18 accepted; a bad request key accepted; the request written without `shownProcesses` | 1; 1; 1 |
| the history file written 0644 (WSL) | 1 |
| the reconcile / the sweep ignoring the history problem; the rewrite taking an unreadable file as empty | 1; 1; 1 |
| the rules not held per layer / a user break as an error / the take-back dropped | 14 / 1 / 2 |
| the late-slack rule, the run-limit rule | 1, 1 |
| a beat stamping progress without a step; the reader ignoring progress | 1; 1 |
| the service drop-in without `TimeoutStartSec` | 1 |
| the writer's cap the range maximum; `config set` not evaluating the rules; `units dropin` rendering an invalid configuration | 1; 1; 1 |
| the summary reading every day file; the starts retention user-layer | 1; 1 |
| `install.sh` failing on 2 / 78 (WSL) | 1 |
| the comparison shape dropped from the scan | 2 (the planted one, and an allowlisted entry now stale) |

### The coai E7 code round (2026-10-06, plan §15q *coai E7 code round*)

| Finding | Tests |
|---|---|
| #1–#3 (refactors: the act option dispatch, the closed sizes / session views) | unchanged: `ActCommandTests` (parsing), `AgentsCommandTests` and `AgentsFlows` (the text form) — green |
| #4: the parser holds the range maximum; the verb holds the value in force | `Cli.Tests/UnitsCommandTests.The_shown_list_cap_in_force_is_held_by_the_verb_and_the_parser_holds_the_range_maximum` |
| #5: the dropped manual-agent folders are notices of the load | `AgentsCommandTests.S1_…` (the notice), `…Without_manual_agents_the_second_phase_is_the_same_host` |
| #6: an empty drop-in answer is never installed | `InstallFlows.An_empty_drop_in_answer_is_never_installed` (Linux) |
| #7: `agents list --measure` says on stderr what it walks; stdout is the JSON | `UnitsCommandTests.Agents_list_measure_says_what_it_walks_on_stderr_and_leaves_the_json_alone` |
| #8: the interop explanation once, each key the short fact | `Config/UserLayerTrustTests.With_interop_disabled_a_user_value_can_only_tighten_root`; `Scenarios/ConfigTrustFlows`, `ConfigReviewRoundFlows` |

**Teeth:** the verb's cap check dropped — 1 red; the notices not added — 3; the per-folder line dropped — 1; the pre-walk line
without its budget — 1; the explanation per key again — 1; the empty-answer check dropped (WSL) — 1. Each file restored
byte-identical.

### The act template's CollectMode, and the three gaps that let it ship (daemon 0.1.1, 2026-10-06)

The live 0.1.0 install failed POST_DEPLOY item 7: `wsl-care-act@.service` carried `CollectMode=inactive-or-failed` under
`[Service]`, systemd.unit(5) reads it in `[Unit]` only, and systemd 255 said so in the journal —
`/etc/systemd/system/wsl-care-act@.service:46: Unknown key name 'CollectMode' in section 'Service', ignoring.` —
while `systemctl show` gave `CollectMode=inactive`: a failed detached run stays in `systemctl --failed` and counts in
`systemd.failedUnits`. Three things had let it through, each now a test:

- **`ShippedFilesTests` enforced the defect** — it read the key in `[Service]`. Now
  `The_detached_run_s_template_runs_its_instance_s_request_as_the_cli_parses_it` wants it in `[Unit]` and NOT in
  `[Service]`; `Every_key_of_every_shipped_unit_sits_in_the_section_systemd_reads_it_from` and
  `Every_key_of_every_drop_in_sits_in_the_section_systemd_reads_it_from` hold every key to its man page's section (every
  OS), with `The_section_scan_reads_the_template_s_unit_section` as the companion.
- **CI never read the template.** The `systemd-analyze verify` step named `wsl-care.service`, `.timer` and
  `wsl-care-events.service` by hand (E4.S1); the template arrived in E6.S1 and joined `install.sh`'s `UNITS` and
  `ShippedFiles.UnitNames`, never the step. `verify-systemd-units.sh` now reads the folder, a template through an
  instance, with the built binary's drop-ins — `SystemdUnitVerifyFlows` (Linux legs, real `systemd-analyze`): the 0.1.0
  template planted into the shipped text fails naming `wsl-care-act@.service:` and the key; a drop-in with an unknown key
  fails naming `<unit>.d/50-wsl-care-config.conf`; a binary that cannot render a drop-in and an empty folder fail; valid
  units with drop-ins pass and are named (skipped, with systemd's words, on a host whose own units a normal user cannot
  read — WSL Ubuntu's root-only `netplan-ovs-cleanup.service` — where the CI step is the positive); the CI step runs the
  script over `src_daemon/systemd` with the Release binary, after the build, and names no unit.
- **POST_DEPLOY item 7 asserted nothing** — the checker runs a cell's FIRST code span, which was `systemctl cat` alone, so
  the live run printed PASS. The audit of every automated item found three more that exit 0 regardless: item 1
  (`systemctl is-active` of two units exits 0 when EITHER is active — observed, `active` / `inactive` / exit 0), item 5
  (`doctor --json` exits 0 healthy or not), item 11 (an empty journal passed; the `memory peak` it printed does not exist
  on systemd 255, whose line is `Consumed …s CPU time.`). Items 2, 6, 8, 9 and 12 end in an assertion already. Each
  rewritten item is RUN by `PostDeployCommandFlows` (Linux legs) as the checker runs it — the command the checker's own
  `inspect` extracts (first span, `\|` unescaped; coai code round: never a second copy of those rules), under `/bin/sh -c`
  — against a stand-in `wsl.exe` relaying to stand-in `systemctl` / `journalctl` / `wsl-care`: healthy passes, every
  broken state named fails.
- **Item 11 could not have passed on the real relay at all** (own review of this change, both reviewers): after `--`,
  wsl.exe hands its arguments to the distro's shell as ONE command line (observed from inside WSL on 2026-10-06:
  `wsl.exe -d Ubuntu -- sh -c 'x=1; echo "[$x]"'` prints `[]`, `--exec` prints `[1]`; the facts note's row 7 already
  measured it), so `-- sh -c '…$(journalctl …)…$j…'` had root's shell expand the journal into the script. Item 11 now runs
  `journalctl` alone through wsl.exe and reads the journal on this side, as data; the stand-in `wsl.exe` re-parses a `--`
  line exactly that way (`The_stand_in_wsl_exe_re_parses_a_dash_dash_command_line_as_the_real_one_does` pins both forms
  to the observation), and `Item_11_reads_a_journal_line_with_quotes_and_a_command_substitution_as_text` proves a `"` or
  `$(…)` in the journal is never executed. Its AppArmor alternative was dropped: a kernel `DENIED` line has no unit field
  and never reaches `journalctl -u wsl-care.service`.

**Red, observed before the fix:** `ShippedFilesTests` (Windows, Debug) — 3 failed: `Expected unit["Service"] … not to
contain key "CollectMode" … but found it anyhow`; `Expected Unit("wsl-care-act@.service")["Unit"] to contain 3 item(s) …
but found 2`; `Expected keys.Keys to be a subset of {…} because wsl-care-act@.service: every key of [Service] is one
systemd reads in [Service] … but items {"CollectMode"} are not part of the superset`. In WSL as the normal user the old CI
command over the three named units printed nothing about the template, and `verify-systemd-units.sh src_daemon/systemd`
printed `src_daemon/systemd/wsl-care-act@.service:46: Unknown key name 'CollectMode' in section 'Service', ignoring.` and
exited 1. `PostDeployCommandFlows` against the 0.1.0 `POST_DEPLOY.md` (WSL, Release) — 7 failed: item 1 with the timer or
the follower inactive (`Did not expect result.Exit to be 0`), item 5 on `"healthy": false`, item 7 on `CollectMode=inactive`
and on a `collect` without `--timer`, item 11 on an empty journal and on printing nothing for the run.
**Green:** the unit fixed — `ShippedFilesTests` 18/18; the items rewritten — `PostDeployCommandFlows` and
`SystemdUnitVerifyFlows` 19 passed, 1 skipped (the positive, for the netplan reason above); the script over the fixed
folder with the WSL-built Release binary's drop-ins reported nothing but that host's netplan line. The rewritten item 7 run
against the owner's live 0.1.0 install FAILS as it should: `wsl-care-act@: wants CollectMode=inactive-or-failed, systemd
loaded: … CollectMode=inactive`; item 1 passes there (`wsl-care.timer and wsl-care-events.service active`).
**Teeth:** the script made to skip `*@.*` files (the old list's blind spot) — `SystemdUnitVerifyFlows` 2 red (the template
flow saw only the netplan line; the drop-in flow saw `no unit files`); the script restored byte-identical — green.
`RestartSec` taken out of the key table — both key scans red, naming `wsl-care-events.service` and its drop-in; restored.
**The review round's red:** with the stand-in `wsl.exe` made faithful, the first rewrite of item 11 (still `-- sh -c`)
failed its healthy flow — `grep: Consumed": No such file or directory`, the outer shell's re-quoting — and the
journal-as-data flow likewise; the item rewritten, green. Final: `ShippedFilesTests`, `PostDeployCommandFlows` and
`SystemdUnitVerifyFlows` — WSL 39 passed, 1 skipped (the netplan positive); Windows 19 passed, 21 skipped (the Linux
flows).

**daemon-v0.1.1 did not publish.** The checker fetch was added to `ci-daemon.yml` only; `release.yml` runs the same
Scenarios executable before it packs, without the submodule, and `PostDeployCommandFlows` — failing in CI rather than
skipping, by design — failed both Linux legs at Test (`Expected missing to be empty because CI runs the items through the
checker's own extraction, but found "the conventions checker is not checked out at …"`). The publish job never ran; the
`daemon-v0.1.1` draft stayed empty and was never made public; the tag was not moved. `testing.md`'s *a check that only
runs during a release has never run*, met in this repository: now
`ReleaseWorkflowTests.Every_job_that_runs_the_scenario_suite_fetches_the_conventions_checker_before_it` scans EVERY job of
every workflow that runs the Scenarios executable — red first on `release.yml/build` (`found -1`), then, with that fixed,
on a third job nobody had named, `sonarcloud.yml/sonar` (skipped today for want of `SONAR_TOKEN`, so it would have failed
the day the token arrives); both fetch it now, green. The fix ships as daemon 0.1.2.

## The extension (`src_vs_code/`)

> E5.S1 (2026-10-03): the client tier of the extension's harness — the real `WslCareClient` over the real runner seam
> against a strict fake `wsl.exe` — and the structural, manifest and bundle checks. E5.S2 (2026-10-04): the views'
> unit tests over the goldens (status bar, view model, field map held equal to `architecture.md`, poller on a manual
> clock, webview shell / CSP / messages), the panel's page script RUN in a strict `node:vm` harness, and the
> EXTENSION-HOST tier — `@vscode/test-electron` against VS Code 1.85.0 and stable. E5.S3 (2026-10-04): *Install daemon*
> (the pinned command, the modal and the terminal through recorders), the `.vsix` content checks over the PACKAGED
> artefact, the icon held to its recipe, and in C# the extension release workflow's structure and its guard / asset
> scripts. Every fact the fake reproduces was measured first: [2026-10-03_wsl_exe_facts.md](2026-10-03_wsl_exe_facts.md).

### Where it is and how it runs

TypeScript, `node:test`, compiled in place into `src_vs_code/out/` (strict, `noEmitOnError`). One command, identical to
CI's (`ci · extension`, `windows-latest` and `ubuntu-24.04`):

```bash
cd src_vs_code && npm ci && npm run typecheck && npm run lint && npm test
npm run test:host        # the extension-host tier: VS Code 1.85.0 + stable (xvfb-run -a on Linux)
```

`npm run test:host` = compile → bundle → `scripts/run-host.mjs`, which downloads each VS Code into `.vscode-test/`
(git-ignored; CI caches it per month) and launches it TWICE per version with `src/test/host/suite.js` as the extension
tests: once in Test mode with the strict fake named (`WSL_CARE_TEST_FAKE_WSL`, over a scenario file the suite rewrites
between scenarios), once in Test mode WITHOUT it (the runner must be the closed one). No mocha: test-electron only
downloads and launches; the suite reports by throwing, and an empty scenario list throws.

`npm test` = compile → bundle (`esbuild`, `dist/extension.js`, node18, `vscode` external, no source map) →
`scripts/run-tests.mjs`, which walks `out/test` for every `*.test.js` (no glob: it cannot forget a file, and prints the
count) and starts ONE `node --test` with `--require out/test/support/noRealWsl.js`.

| Part | What it is |
|---|---|
| `src/test/support/noRealWsl.ts` | **the tripwire**: loaded into every test process, it replaces `spawn` / `spawnSync` / `execFile` / `execFileSync` / `exec` / `execSync` on the raw `child_process` module with versions that THROW for any program named `wsl` / `wsl.exe` — so no test can reach the real WSL of whoever runs the suite. `noRealWsl.test.ts` asserts it is armed in the process it runs in, and that the product runner handed the real `SystemRoot` gets `failedToStart` naming the tripwire |
| `src/test/fake/fakeWsl.ts` | **the strict fake `wsl.exe`**, a Node script started through the product's own `nodeScriptRunner` (`node fakeWsl.js <the argv the client built>`; the requested program arrives as `WSL_CARE_FAKE_REQUESTED_FILE`). It answers the three WSL questions in UTF-16LE exactly as measured, a daemon call from a golden set (or edited copies), `--version` as text; it reproduces the measured missing-binary signature (exit 1), wsl.exe's own refusal of an unknown distribution (-1, UTF-16LE on stdout), the documented old-glibc line, a daemon exit with stderr, a hang. It REFUSES — stricter than the real one — a start as anything but an absolute `…\System32\wsl.exe` (96), no scenario (97), every argv the client must never send (98: `-u`, `--user`, `--`, `-e`, anything after the binary but the four verbs, so `--timer` / `--confirm` / `--manual` / `config` / `act` / `collect` / `logs`), and a `-d` to a STOPPED distribution (95 — the real one would start the VM). Its four verbs are its OWN copy, the oracle of plan §15f #5 |
| `src/test/support/fakeWorld.ts` | one temporary folder per test: the scenario file, the fake's call log (`{argv, file}` per call), removed after |
| `src/test/support/recordingRunner.ts` | a runner that starts nothing: scripted answers by exact argv, every request recorded |
| `src/test/support/sourceScan.ts` | the TypeScript parser naming every import (all forms: `import`, `import =`, `require`, `import()`, `export from`) and every string literal of a source — the structural tests read programs, not text |
| `src/test/scenarios/clientFlows.test.ts` | **the client flows**: the real client, the real runner, the fake; the flow list DERIVED from `VERB_NAMES`, the golden sets from `contracts/golden/*/` |
| `src/test/support/pageHarness.ts` | **the page harness** (E5.S2), ported from dew_flow_vscode_kit's `pageHarness.ts` and made stricter: `node:vm` with three globals (`document`, `window`, `acquireVsCodeApi` — no `require`, `process`, `fetch`, `setTimeout`), a 5 s deadline on the script AND on every dispatched message / click, and a PROXY per element that throws on any member it does not model — every HTML sink (`innerHTML`, `outerHTML`, `insertAdjacentHTML`, `document.write`), `style`, `on*`, `src`, `href`; `createElement` of a text page's tags only; `setAttribute` of `role` / `scope` / `title` / `type` / `colspan` / `aria-*` / `data-*` only; `textContent` of a string only (as in a browser, it replaces the children). Its own tests: `harness.test.ts` |
| `src/test/support/outcomes.ts` | golden bodies (or edited copies) read through the client's own `parseAnswer` — the view tests see exactly the typed answers the product receives |
| `src/test/support/fieldMapDoc.ts` | the field map as the Markdown block `architecture.md` carries — generated, never typed (`npm run fieldmap:doc` writes it) |
| `src/test/support/vsixCheck.ts` + `zipFile.ts` | **the `.vsix` checks** (E5.S3) — a dependency-free ZIP reader that refuses what it cannot read safely (ZIP64, encryption, another method, a climbing or duplicate name), and the checks `scripts/check-vsix.mjs` runs over the packaged file: the allowlist exactly, no machine path / user name / denylist word / e-mail / source map, the build stamp, a real publisher on a release. Tested by planting each offence into an archive built from the files the allowlist names (`vsixCheck.test.ts`) |
| `src/test/support/iconPng.ts` | the Marketplace icon's RECIPE — a rasteriser and PNG encoder (Node zlib only) and a decoder the test uses to hold `media/icon.png` to it |
| `src/install/installUi.ts` (product code) | in Test mode the modal and the terminal of *Install daemon* are RECORDERS the test API exposes — no real terminal (it would start the real `wsl.exe`) |
| `src/test/host/suite.ts` + `scripts/run-host.mjs` | **the extension-host tier**: the extension as it ships (`dist/extension.js`) in a real VS Code; observed through the runner seam's call log and the test API `activate` returns in Test mode only (`testApi.ts`) |

### Extension flow catalogue

A row for every client verb (`client <verb>`), every contributed command (`command <id>`) and view (`view <id>`) —
derived from `VERB_NAMES` and `package.json`'s `contributes`; `catalogue.test.ts` fails, naming the flow, when one is
missing (E5.S2 contributes the view `wslCare.panel` and three commands; it was red with exactly those four missing
before the rows below were written — its planted companion still shows a command or view without a row is reported;
E5.S3 adds the command `wslCare.installDaemon`, and the check was red with exactly it missing until its row existed).

| Flow | Covered | By |
|---|---|---|
| `client status` — `-d <distro> --cd / --exec /opt/wsl-care/bin/wsl-care status --json` after `--list --quiet` (+ `-l -v` when no distro is set) and `--list --running --quiet`; answered over every golden set with its schema accepted, verdicts and `productVersion` read, the version judged from `productVersion` (no `--version` call); stopped → no `-d` call; not installed; old glibc; a refusal with Serilog noise reduced to its `wsl-care:` line; a hang ended by the runner at the ceiling | covered | `clientFlows.test.ts` (flows), `client.test.ts`, `handshake.test.ts`, `failures.test.ts` |
| `client preview` — `preview --all --json`, 330 s ceiling; over every golden set; an unknown `schemaVersion` blanks preview ONLY (status still answered); stopped / not installed / old glibc | covered | `clientFlows.test.ts`, `client.test.ts`, `handshake.test.ts` |
| `client doctor` — `doctor --json`, 100 s ceiling; over every golden set; the daemon version from an earlier status or ONE `--version` call per distribution; stopped / not installed / old glibc | covered | `clientFlows.test.ts`, `client.test.ts`, `handshake.test.ts` |
| `client version` — `--version`, 20 s ceiling; read as `x.y.z(+sha)?`, `unknown` (unstamped), `0.0.0+sha` (a build before the first release) | covered | `clientFlows.test.ts`, `client.test.ts`, `handshake.test.ts` |
| `view wslCare.panel` — the read-only panel (`WebviewView`, activity-bar container *AI OS Care*): opening it (or making it visible) asks `status`, then `preview` + `doctor` unless `status` stopped at a target failure; renders every field-map row ("arrives in E#", "unavailable — reason", "update the daemon to see this", the verb's own failure, "checking…"); a static shell with a per-render nonce and a strict CSP; data only by `postMessage`; the page's DOM by `createElement` / `textContent`; the closed page→host message set | covered | `viewModel.test.ts`, `fieldMap.test.ts`, `panelPage.test.ts` (the page RUN in the strict harness over every golden set and hostile strings), `webviewHost.test.ts`, `poller.test.ts`; in a real VS Code 1.85.0 + stable: `host/suite.ts` (*opening the panel asks preview and doctor once and the webview renders every field-map row*) |
| `command wslCare.openPanel` — the status-bar item's click; focuses `wslCare.panel` | covered | `host/suite.ts` (executes it, the webview reports its rows) |
| `command wslCare.refresh` — the panel title's Refresh: the same round as a panel open (`status`, then `preview` + `doctor`); never starts a stopped distribution | covered | `poller.test.ts` (*opening the panel asks status, then preview and doctor*; *a stopped distribution: the panel asks status only*), `host/suite.ts` (*a stopped distribution: NO -d call … on a panel refresh*) |
| `command wslCare.startWsl` — *Start WSL and check*: `status` with `startIfStopped`, the ONE `-d` that may start a stopped distribution, because the user asked; every distribution check still applies; never folded into a poll in flight | covered | `client.test.ts` (*startIfStopped …*), `poller.test.ts` (*"Start WSL and check" passes startIfStopped to status ONLY*), `fakeWsl.test.ts` (*startable …*), `host/suite.ts` (*"Start WSL and check" makes the one -d the user asked for*) |
| `command wslCare.installDaemon` — *Install daemon* (E5.S3; also the panel button when the status answer is *daemon not installed*): the distribution validated (the setting's pattern before any spawn, then `--list`) BEFORE anything is opened; a modal with the exact pinned command and the prerequisites as text; on confirm ONE terminal `wsl.exe -d <distro>` (absolute launcher) and the command TYPED with `sendText(command, false)`, never run; declined → nothing; the page can only send the bare message | covered | `installDaemon.test.ts` (order, typed-not-run, refusal before anything, the real client's `terminalTarget`, a smuggled message dropped), `client.test.ts` (*terminalTarget …*), `viewModel.test.ts` (*E5.S3: a daemon that is not installed offers "Install daemon"*), `structure.test.ts` / `bundleScan.test.ts` (one command module, no runner, sudo only there), `host/suite.ts` (*Install daemon …* — the recorded modal and terminal in a real VS Code) |

### What each guarantee rests on

Every guarantee was first seen RED against stubs that compiled and returned neutral answers (2026-10-03: 154 tests,
110 red for their own symptom), then each was red again with the ONE production line it rests on broken — 15 such
mutations, each rebuilt, run and restored byte for byte (`SHA-256` equal). Three tests added in the self-review were red
first against the finished client (157 tests today):

| Guarantee | Test | Red observed |
|---|---|---|
| only `src/process/runner.ts` imports `child_process` | `structure.test.ts` | `import { spawn } from 'node:child_process'` added to `extension.ts` → *only the runner imports child_process — in any form* red; stub run: `+ [] - ['src/process/runner.ts']` |
| only `WslCareClient.ts` spells `wsl.exe` argv words / the daemon path; only `verbs.ts` the verb option words | `structure.test.ts` | `export const STRAY = ['--exec']` added to `runnerSelection.ts` → *only the client spells wsl.exe argv words or the daemon path* red |
| no `-u` / `--user` / root / `--timer` / `--confirm` / `--manual` / `config` word in the SHIPPED bundle | `bundleScan.test.ts` | `['-u', 'someone']` spread into a live argv of the client (an unused export is tree-shaken by esbuild and proved nothing — the first attempt) → *the shipped bundle spells no root, timer, confirm, manual or config word* red; each word also planted in a copy of the real bundle, as an argv element and in a template |
| a STOPPED distribution gets no `-d` call; a failed running check counts as stopped | `client.test.ts`, `clientFlows.test.ts` | the running check replaced by `running.ok \|\| …` → *a STOPPED distribution gets no -d call at all* and *when the running check itself fails …* red; stub run: `+ kind: 'interrupted' - kind: 'stopped'` |
| an out-of-pattern distribution is refused before ANY spawn; an unlisted one before any `-d` | `client.test.ts`, `clientFlows.test.ts` | the pattern check disabled → *an out-of-pattern distribution is refused before ANY spawn* red |
| "not installed" ONLY from the measured signature for our path at exit 1 | `failures.test.ts` | the signature test dropped (exit 1 alone) → *a missing OTHER path, a permission refusal …* and *the daemon's own exit 1 (RunFailed) is an unknown failure …* red; stub run: `The input did not match the regular expression /$^/` over the measured line |
| an unknown `schemaVersion` → "needs a newer extension", per verb | `handshake.test.ts`, `client.test.ts`, `clientFlows.test.ts` | the supported-schema check removed → the three *with an unknown schemaVersion major …* tests red; stub: `+ kind: 'unparseable' - kind: 'needsNewerExtension'` |
| an unknown verdict level reads `unknown`; unknown keys ignored; a missing `verdicts` / `productVersion` tolerated | `handshake.test.ts` | the level passed through unchecked → *a verdict with an unknown level reads as unknown …* red |
| Test mode without a fake spawns nothing (fail closed); outside Test mode an environment variable cannot redirect | `runnerSelection.test.ts` | the closed branch replaced by the real runner → *Test mode without a usable fake is closed* and *the closed choice yields a runner that answers failedToStart …* red; stub: `'real' !== 'closed'` |
| a timed-out child is killed, the answer bounded by ceiling + grace | `runner.test.ts` | `this.child.kill()` removed → *a child past its ceiling is reported timed out … and is dead afterwards* red (the child alive after 2.7 s) |
| `wsl.exe` output decoded as UTF-16LE from its bytes (measured `55 00 62 00`); UTF-8 under `WSL_UTF8=1` and for Linux output | `wsl.test.ts` | detection disabled → *wsl.exe output is decoded as UTF-16LE* red; stub: `+ 'U\x00b\x00u\x00…' - 'Ubuntu\r\ndocker-desktop\r\n…'` |
| the default distribution is the ONE `*` row of `-l -v` (localised header ignored) | `wsl.test.ts` | the pattern widened to the first row → the `*`-row test red |
| one call per verb in flight | `client.test.ts` | the in-flight check bypassed → *one call per verb in flight …* red |
| `wsl.exe` refusing or not answering one of its OWN questions is a WSL failure with its sentence or its ceiling; an unread `--version` is not remembered | `client.test.ts` | red before the self-review fix: `+ kind: 'unknownFailure', code: 1 - kind: 'wslFailed', message: 'The Windows Subsystem for Linux is not installed.'`; `+ code: undefined, kind: 'unknownFailure' - message: 'wsl.exe did not answer within 15000 ms'`; `+ kind: 'notRead' - kind: 'release'` (the second call reused the failed read) |
| `--exec`, never `--`; exactly the four verbs | `clientFlows.test.ts` (the fake refuses), `structure.test.ts` | `--` inserted into the daemon argv → every answered flow red (the fake: exit 98); a fifth word added to the `version` tail → *flow · version over the head goldens* red |
| the client's exit-code names are the daemon's `ExitCode` values | `exitCodes.test.ts` | reads `src_daemon/src/WslCare.Cli/ExitCode.cs`; a planted renumbered enum is read back |
| the manifest: `extensionKind ["ui"]`, `engines.vscode ^1.85.0` + `@types/vscode` exactly 1.85.0, untrusted + virtual workspaces, `preview`, 0.0.0, both settings `application`, distro pattern = the client's, refresh ≥ 30 (default 120), no runtime dependency, every dev dependency pinned, the licence byte-equal | `manifest.test.ts` | stub run: the distro pattern (`/.*/`) did not equal the schema's |
| the flow catalogue is derived | `catalogue.test.ts` | red until this section existed: *research/module_tests.md has no "## The extension (`src_vs_code/`)" heading* |
| the TS doctrine's `scriptInterpolation` scan, ready before the first webview (E5.S2) | `scriptInterpolation.test.ts` | ported from the kit; empty allowlist; its fixture companion finds both spellings |
| the fake is stricter than `wsl.exe`, never more permissive | `fakeWsl.test.ts` | stub run: every fake answer red through the stub runner (`{"kind":"failedToStart","reason":"stub"}`) |

### What each E5.S2 guarantee rests on

The E5.S2 tests were written FIRST against compiling stubs (2026-10-04: 275 tests, **60 red** for their own symptom — the
bar, the view model, the field map, the poller, the shell and the messages, the page — while the 15 harness tests, the
harness being test code written whole, were green but one: *a selector … that matches nothing … other shapes are
refused* was red against the finished harness, because an empty element answered `[]` for `.hidden` without parsing the
selector — a defect of the harness, fixed). The catalogue test was red with exactly the four new flows missing before
their rows were written. Then each guarantee was broken by ONE production line and seen red — 21 mutations, each rebuilt
(`npm test`: compile, bundle, run), run and restored byte for byte (SHA-256 equal); four first attempts were red only
because `noUnusedLocals` refused the compile, and were redone as behavioural mutations (the table records those). One
more mutation was run through the EXTENSION HOST.

| Guarantee | Test | Red observed (the one line broken) |
|---|---|---|
| only the focused window polls; a timer firing after focus was lost asks nothing | `poller.test.ts`; `host/suite.ts` | the focus check in `tick` removed → *a timer that fires after focus was lost (the race) asks nothing*; in VS Code 1.85.0 → `FAIL an unfocused window starts nothing when the interval fires` (the host run exit 1) |
| a panel refresh after a stop asks neither `preview` nor `doctor` | `poller.test.ts` | `stopsTheOthers(status) && false` → *a stopped distribution: the panel asks status only …* |
| the interval never goes below 30 s | `poller.test.ts` | the floor removed → *the interval setting: default 120, never below 30 …* |
| `startIfStopped` lets exactly the user's `-d` through; a start is never folded into a poll in flight | `client.test.ts` | `running \|\| (startIfStopped && false)` → *startIfStopped: a stopped distribution gets the ONE -d …* and *a poll in flight is not shared …*; the in-flight key without `+start` → the latter |
| only memory / kernel verdicts colour the bar; `unknown` colours nothing | `statusBar.test.ts` | the relevant prefixes widened to every verdict → 3 red, among them *only the verdicts about what the bar shows colour it …* |
| no verdicts → uncoloured with "update the daemon to see warnings" | `statusBar.test.ts` | the hint removed → *a daemon without verdicts …* |
| an absent field reads "update the daemon to see this", never 0 | `viewModel.test.ts` | the absent-path branch disabled → *a field an older daemon does not send reads "update the daemon to see this" …* |
| `available: false` reads "unavailable — <reason>" | `viewModel.test.ts` | the branch disabled → *a whole figure answered available:false reads "unavailable — <reason>"* |
| refusal is per verb: each row reads its OWN verb | `viewModel.test.ts` | every row read from `status` → 3 red, among them *an unknown preview schema blanks ONLY the preview rows …* |
| control and bidi characters are made visible; long strings clipped | `format.test.ts`, `viewModel.test.ts`, `panelPage.test.ts` | the replacement made the identity → *safeText makes control and bidi-override characters visible …*, *hostile … reach the view as clipped, visible TEXT*, *hostile … render as inert TEXT …* |
| the page writes text only | `panelPage.test.ts` | `node.textContent` → `node.innerHTML` in `media/panel.js` → 8 red, each throwing `strict DOM: writing TD.innerHTML (an HTML sink) is not modelled`, and the source scan red |
| the page renders only a view message | `panelPage.test.ts` | `isView` loosened → *a message that is not a view is ignored …* |
| CSP scripts by nonce only; command URIs off | `webviewHost.test.ts` | `'unsafe-inline'` added → *the CSP: default-src none; scripts and styles ONLY by this render's nonce …*; `enableCommandUris: true` → *the webview options …* |
| the page→host message set is exact | `webviewHost.test.ts` | the key-count check loosened → *anything else from the page is dropped …* |
| the document's field map is the code's | `fieldMap.test.ts` | a label changed in `fieldMap.ts` only → *research/architecture.md carries exactly the field map …* |
| every read row names a real field | `viewModel.test.ts` | a path re-pointed to `vm.memory.swapTotl` → *every row the field map READS exists at its path …* (and the document test) |
| the harness refuses HTML sinks and loading tags | `harness.test.ts` | `innerHTML` modelled as a member → both *an HTML sink throws: el.innerHTML…* red; `createElement` of any tag → all 7 *createElement('<tag>') throws* red |
| the fake never starts a stopped distribution unless the scenario is `startable` | `fakeWsl.test.ts` | the guard disabled → *a -d to a STOPPED distribution is refused …* |
| the shipped bundle carries no forbidden word | `bundleScan.test.ts` | caught for real, not planted: the shell's first draft `<main id="root">` → *the shipped bundle spells no root, timer, confirm, manual or config word* (`+ ['root']`); the element is `id="panel"` |

**The extension-host tier, green locally** (Windows, 2026-10-04): VS Code 1.85.0 and stable 1.140.0, each launched
with the fake (6 scenarios) and without it (1 scenario) — 14 scenario runs, `npm run test:host` exit 0.

### What each E5.S3 guarantee rests on

E5.S3 (2026-10-04): *Install daemon*, the package and its leak checks, the extension's release pipeline, and the five
findings of the coai gate's E5 plan round (plan §15h). Every guarantee below was broken by ONE production line, the
suite run, and the file restored byte for byte (SHA-256 equal) — by a harness that applies the mutation, runs `npm test`
(compile, bundle, every test) or rebuilds `WslCare.Scenarios` and runs its release classes, and restores. Six first
attempts were red only because the compiler refused them (`noUnusedLocals`, a narrowed type) and were redone as
behavioural mutations; the table records the behavioural ones. 338 extension tests after E5.S3 (337 pass, 1 skipped:
the packaged-`.vsix` read when no `.vsix` newer than the bundle exists).

| Guarantee | Test | Red observed (the one line broken) |
|---|---|---|
| the command is pinned to the minimum daemon's TAG, `--version <MIN>`, never `--skip-attestation`, never `main` | `installDaemon.test.ts` | `--skip-attestation` added → 3 red (*the command is pinned …*, *never skips the attestation …*, *the Marketplace README shows the very command …*); `daemon-v<MIN>` → `main` → the same 3 |
| the command is TYPED, never executed | `installDaemon.test.ts` | `sendText(INSTALL_COMMAND, true)` → *confirmed: … typed, NOT executed* (`sendText(command, false): typed, never run`) |
| a refused distribution gets no modal and no terminal | `installDaemon.test.ts` | a terminal opened on the refusal path → *a refused distribution: reported, no modal, no terminal* and *with the real client: an out-of-pattern setting is refused BEFORE anything starts …* |
| nothing is opened without the modal's confirmation | `installDaemon.test.ts` | the confirm bypassed → *confirmed: …* and *declined …: no terminal, nothing typed* |
| `terminalTarget` validates against `--list` | `client.test.ts`, `installDaemon.test.ts` | the setting used without the list → 4 red, among them *terminalTarget: the absolute launcher and -d <the listed distribution> …* |
| a page message cannot carry text into the command | `installDaemon.test.ts`, `webviewHost.test.ts` | `keys.length === 1` → `>= 1` → *a webview message cannot inject text …* (`{"type":"installDaemon","command":"rm -rf ~"}`) and *anything else from the page is dropped …* |
| a missing daemon offers *Install daemon* | `viewModel.test.ts` | the `notInstalled` button removed → *E5.S3: a daemon that is not installed offers "Install daemon" first* |
| only `installCommand.ts` spells the installer / `sudo`; `sudo` in the bundle only there | `structure.test.ts`, `bundleScan.test.ts` | `Run sudo install.sh: …` put into `failureText.ts` → 3 red (*only the install-command module spells …*, *sudo appears in the bundle only inside the install command …*, *the sudo scan is alive …*) |
| the README carries the command the extension types | `installDaemon.test.ts` | the README's line changed → *the Marketplace README shows the very command …* |
| §15h #4: a LISTED name is used as it is | `client.test.ts` | the setting pattern applied to listed names again → *a default distribution WSL lists under a name outside the setting pattern is used AS IT IS* (`reason: "a name starting with \"-\"…"` on `Ubuntu+Dev~2`) |
| §15h #4: a listed leading `-` is refused | `client.test.ts` | the check disabled → *a default whose name fails the pattern …* and *a listed default starting with "-" is refused …* |
| §15h #4: an unlisted refusal names the listed distributions | `client.test.ts` | the names dropped from the reason → *a distribution wsl.exe --list does not report …* and *an unlisted setting is refused naming both …* |
| §15h #4: the fake refuses a leading-`-` `-d` | `fakeWsl.test.ts` | the fake's check removed → *§15h #4: a -d value starting with "-" is refused …* |
| §15h #1: a crowded preview timeout says so | `client.test.ts` | the explanation fed 0 containers → *a preview timeout with more running containers …*; `>` → `>=` → *at or below the assumption … stays a plain timeout* |
| the bundle is stamped with its version | `bundleScan.test.ts`, `vsixCheck.test.ts`, `manifest.test.ts` | the `define` removed from `scripts/bundle.mjs` → 21 red (every clean-archive check, the stamp scans, the bundle-options test) |
| the `.vsix` checks: drive path, e-mail, extra entry, stale stamp, placeholder publisher on a release | `vsixCheck.test.ts` | each rule disabled in turn → its planted test red: both drive-path plants; the e-mail plant (`did not match … holds an e-mail address`); *an extra entry is a finding*; *a stale bundle …*; *a RELEASE refuses the placeholder publisher* |
| names are read in the bundle's LITERALS only | `vsixCheck.test.ts` | the whole bundle text read → *in the bundle only string LITERALS are read …* (`the real bundle carries the identifier, not the word in a string` — the CI account `runner`) |
| the icon is its recipe | `icon.test.ts` | one channel of the fill colour changed → *its pixels are the ones the recipe draws* (`5453 bytes differ`) |
| `release-extension.yml`: tag-only, per-job scopes, the Environment on one job, the secret named once, draft → Marketplace → public, the skip, one package with `--release`, the attested file published, a guard reading `releases/tags/daemon-v<MIN>`, the `MIN` line's shape, the extension ruleset, the manifest bootstrap, SHA pins, no `${{ }}` in `run:` | `ReleaseExtensionWorkflowTests`, `ReleaseWorkflowTests`, `ReleaseConfigTests` | 16 mutations, each red: a `workflow_dispatch` trigger; `id-token: write` on the Marketplace job (2 red: its scope and the signer list); `environment:` on build; the Marketplace not waiting for the draft (*… exists BEFORE the Marketplace serves anything (15h #0)*); public not waiting for the Marketplace; the `if:` of the skip removed; a second `npm run package` (`found 2`); the leak check without `--release`; `${{ github.ref_name }}` in a `run:` (2 red); an action by tag; the guard asking `releases/latest`; the manifest at 0.1.0; the extension ruleset on `daemon-v*`; `MIN_DAEMON_FOR_RENDER: string =` (the guard's line shape); `VSCE_PAT` named in the draft job; `vsce publish` without `--packagePath` |
| the guard refuses a draft / wrong daemon release, an older verified daemon, the placeholder publisher; the asset set checks its checksum | `ReleaseExtensionScriptFlows` (Linux, under bash) | in the WSL copy: the stamp comparison disabled → *… daemon 0.0.9, older than the minimum*; the release answer check disabled → both *… not a published, non-draft release* rows; the publisher check disabled → *the placeholder publisher*; `sha256sum --check` replaced → *the extension asset set … refused broken* |

**The extension-host tier, green locally** (Windows, 2026-10-04): VS Code 1.85.0 and stable, each with the fake (9
scenarios — the build stamp, *Install daemon* declined and confirmed through the recorders) and without it (3 — the stamp,
*Install daemon* ending at the closed runner, nothing started), `npm run test:host` exit 0.

### What each E5 code-round guarantee rests on

The E5 code round (2026-10-04; plan §15i — the coai gate's code round and two own reviews, plus the privacy finding).
Every behaviour item was written as a test FIRST and run against the unfixed code; the red message below is that run's,
for the real symptom. The Linux-only flows (the guard, the installer) were run red in a `/tmp` copy in WSL with HEAD's
`install.sh` and `release-extension-guard.sh` put back, then green with the fixed ones. Afterwards: 351 extension tests
(350 pass, 1 skipped), and the C# suites green on Windows and in WSL.

| Item | Test | Red observed against the unfixed code |
|---|---|---|
| A — no committed fixture / golden carries a person | `FixturePrivacyTests` (Scenarios, every OS) | **248 findings**: `contracts/golden/head/status.json` — 79 foreign `/home/<name>`, 31 foreign Windows profile paths, 31 × the running machine's user name; `preview.json` 1 (`/golden-root/home/me`); procfs `links.txt` 26 + 16 + 16, 31 `cmdline` files, `etc/passwd` 1; the health `powershell-clock.out` and its `SOURCE.txt` 1 each (a hand-chosen profile name). The values are not repeated here; the scan never prints them. Each rule has its planted-instance test (and its clean counter-examples: `user`, a systemd template unit, a placeholder), and *the walk reaches the captured trees* is the companion that keeps the scan from matching nothing |
| A — the fixtures pass through the ONE identity list unchanged | `FixtureAnonymisationTests` | before the rewrite: *every captured fixture passes through the identity list unchanged … but found* `health/ubuntu-2026-10-02/SOURCE.txt` (and 41 more); the companion *still carries every neutral shape* red with `user:x:1000:1000::/home/user:/bin/bash` missing. A first rewrite was rolled back from git and redone after its review found a project folder literally called `project-<word>` taken as already neutral and an extension id surviving in an extension host's log folder — both now planted in `FixtureIdentityTests` |
| A — the golden writer anonymises with the same list | `GoldenContractTests.Every_string_of_an_answer_passes_through_the_identity_list…` | the identity call removed from `Value()` → `to contain "commandLine": "/home/user/.vscode-server/bin/x/node /tmp/x/run.sh mail user@example.invalid"`; the existing normaliser test likewise (`/golden-root/home/user/.npm`) |
| B1 — carried clock verdicts are concrete | `GoldenContractTests.The_journal_and_clock_verdicts_are_fixed…` | the old placeholder rules → `to contain "value": "0.8 days (oldest entry 2026-10-01 22:10Z)" because the health capture (2026-10-02T17:34:47Z) minus the oldest entry` |
| B2 — the guard reads the minimum from the JSON artefact | `ReleaseExtensionScriptFlows.A_reformatted_handshake…`, `…malformed_minimum_daemon_artefact…` (WSL) | HEAD's guard: `Expected result.Exit to be 0 because ::error::extension release guard: src_vs_code/src/client/handshake.ts defines no single MIN_DAEMON_FOR_RENDER = 'x.y.z'`; the five malformed / missing `min-daemon.json` rows `Expected result.Exit to be 1 because version=0.1.0` (it never read the file) |
| B2 — the artefact exists and equals the constant | `minDaemon.test.ts`, `ReleaseExtensionWorkflowTests.The_minimum_daemon_artefact…` | `ENOENT … src_vs_code\min-daemon.json`, `ENOENT … dist\min-daemon.json`; the C# shape test failed reading the missing file. `minDaemonFindings` (check-vsix) is new code: each disagreement planted in `vsixCheck.test.ts` |
| B3 — only the attest job signs, and it runs no npm | `ReleaseWorkflowTests.Only_the_daemon_build_and_the_extension_attest_job_can_sign…`, `ReleaseExtensionWorkflowTests` (four tests) | `Expected signers[1] to be the same string … release-extension.yml/attest` (it was `/build`); `StepIndex(build, "actions/attest-build-provenance@") to be -1 … but found 10`; *the six jobs, in their order … contains 1 item(s) less*; *the attest job signs …* red (no such job) |
| B4 — item 6 ranks by containment and rank, without `unzip` | `ReleaseExtensionWorkflowTests.Post_deploy_item_6…`, `ReleaseExtensionScriptFlows.The_shared_version_ranking…` | the row carried `versions[0]` and `unzip -p`; the lib flow is new code (green against both guards) |
| B5 — every guard line is a declared output | `ReleaseExtensionWorkflowTests.The_guard_runs_first…` | `Expected guard["outputs"].Map.Keys to be equal to {"version", "publisher", "min_daemon"} … contains 1 item(s) less` |
| C1 — nothing replaces a release asset; public only after a cmp with the attested build | `ReleaseExtensionWorkflowTests.Every_github_step_is_rerunnable_and_bytes_on_a_release_are_never_replaced` | the upload step held `--clobber` and github-public had no download / `cmp` |
| C2 — a pinned install's last resort names the same tag and version | `InstallAttestationFlows.Without_gh_a_pinned_install…`, `…started_without_root…` (WSL) | HEAD's `install.sh`: stderr held `…/main/install.sh \| sudo sh -s -- --skip-attestation` (and `…/main/install.sh \| sudo sh -s -- --version 0.1.0` for the root line), not `refs/tags/daemon-v0.1.0` |
| C3 — no call log outside Test mode | `testApi.test.ts` | with `clientRunner` extracted first WITHOUT a behaviour change: `nothing reads the log outside Test mode, so nothing is kept` — `actual` held 100 entries |
| C4 — the URL spells `refs/tags/`, the terminal starts in `~` | `installDaemon.test.ts`, `client.test.ts`, host `suite.ts` | `actual: 'curl -fsSL …/wsl_care/daemon-v0.1.0/install.sh …'`; `actual: [ '-d', 'Debian' ], expected: [ '-d', 'Debian', '--cd', '~' ]` |
| D1 — a huge interval is clamped to a day | `poller.test.ts`, `manifest.test.ts` | `actual: 2147484, expected: 86400`; the schema's `maximum`: `actual: undefined, expected: 86400` |
| D2 — an unavailable parent reads "unavailable — <its reason>" | `viewModel.test.ts`, `statusBar.test.ts` | `memory.total — actual: 'update the daemon to see this', expected: 'unavailable — /proc/meminfo could not be read'`; the bar: `'… Not read: not reported\nNot read: not reported …'` |
| D3 — preview + doctor share ONE `--version` | `client.test.ts` | `one --version for both verbs — actual: 2, expected: 1` |
| D4 — the flow asserts the exact daemon calls | `clientFlows.test.ts` | a test-only correction (green on the code); its teeth: `ownVersion` made to ignore `productVersion` → `+ '-d Ubuntu --cd / --exec /opt/wsl-care/bin/wsl-care --version'` on *flow · status over the head goldens*, which the old `>= 1` passed; restored green |
| D5 — the notice is the one live region | `panelPage.test.ts`, `webviewHost.test.ts` | the notice: `actual: undefined, expected: 'polite'`; the shell: `<main id="panel" aria-live="polite"></main>` did not match `/<main id="panel"><\/main>/` |
| D6 — docs drift | — | documentation and comments only: no behaviour, no red |
| A, widened — no TRACKED text file carries a person (every file `git ls-files` lists, or a walk without `.git`; images and binaries skipped), with ONE commented allowlist of invented test accounts (`FixturePrivacy.SyntheticNames`, moved there from the test class by the CI fix below) and service / reserved-example e-mail addresses admitted | `FixturePrivacyTests.No_tracked_text_file…`, `…admits_only_the_listed_synthetic_names…`, `Service_and_reserved_example_addresses…` | counted by file and rule, never the value — on Windows 10: `research/2026-10-03_wsl_exe_facts.md` 1 home path, `research/module_tests.md` 1, `WslCare.TestSupport/HostileInputs.cs` 1 home path + 2 × the machine user name, `Core.Tests/Actions/TargetHomeTests.cs` 5 home paths; in WSL 25 (the Linux account is the machine name there): `research/2026-10-02_one_time_cleanup.md` 1, `research/2026-10-02_wsl_resource_baseline.md` 2, `research/2026-10-03_wsl_exe_facts.md` 1 + 1, `research/diagnostics/clean_more.sh` 1, `clean_safe.sh` 2, `research/module_tests.md` 1 + 1, `HostileInputs.cs` 1, `TargetHomeTests.cs` 5 + 9. Fixed with the identity list's placeholders (`user`, `project-a`…, `named-volume-a`…; `alice` in TargetHomeTests, which asserts a refusal names two accounts), numbers unchanged; the project and volume names in the cleanup note and the plan, which no rule can recognise, anonymised by reading. Then 0 on both |
| CI fix (run 37202261532) — the machine-name rule ignores service accounts, synthetic names and names under 3 characters, and still finds a distinctive name as a whole word or path segment | `FixturePrivacyTests.A_service_or_synthetic_account_as_the_machine_name_finds_nothing…` (10 rows), `…distinctive_machine_name_planted_in_a_tree…`, `…service_accounts_and_the_name_floor_are_the_vsix_checks_own`, `…leave_out_blanks_duplicates_short_names…`; the machine's names now come in through a seam (`FixturePrivacy.RepositoryFindings(root, candidates)`; `MachineNameCandidates()` is the one place the environment is read) | CI: 541 findings on each Linux leg (the account is `runner`, an ordinary word here — the test runner, the command runner), 1 on Windows (`runneradmin` in `vsixCheck.test.ts`). Reproduced on Windows against the unfixed filter (only `user` and `root` left out, no length floor), counts only: `runner` → *548 finding(s)* (the new tests' own lines included), `RUNNER` 548, `vscode` 366, `me` 230, `alice` 52, `runneradmin` 3, `ab` 3, `codespace` 1 (`root` and `user` already green); the planted tree → *notes.txt:1 holds the user name #2 …* plus `runner.txt` found (`runner` kept as #1); the mirror → *{"runner", "runneradmin"} do(es) not match* `ServiceAccounts`. The whole-word match itself was already right (`ProcessCommandRunner` never matched); what was missing is the list. Green after `ServiceAccounts` (`runner`, `runneradmin`, `root`, `vscode`, `codespace`, `user`), `MinimumNameLength` = 3 and the synthetic names in `Personal` |
| CI fix (run 37202261532) — a directory listing is in ordinal order, so `status --json`'s container list is the same on every machine | `Core.Tests/Files/PhysicalFileSystemTests.Directories_and_files_are_listed_in_ordinal_order…`; `GoldenContractTests` under a second filesystem | CI: `contracts/golden/head/status.json` line 1510 — checked in `"id": "247515d6…"`, linux-x64 answered `8e4a5ad9…`, linux-arm64 `96a3e74b…`: the 64-hex ids are the container cgroup directories of the captured tree (`sys/fs/cgroup/docker/<id>`), listed by `ContainerCgroups.Read` in the disk's order — ext4 reads a directory in the order of a hash seeded per filesystem, so each runner image (and the WSL disk the golden was made on) has its own. Not an environment-derived VALUE: the same ten ids, reordered. Reproduced in WSL `Ubuntu` from a `/tmp` copy with HEAD's `PhysicalFileSystem`: green with `TMPDIR` on the ext4 `/tmp`, red with `TMPDIR` on a tmpfs mounted under `/tmp` — *line 1510: checked in '…"id": "247515d6…"', the CLI answers '…"id": "0e456d1d…"'*; the two generations differed in exactly 30 lines, 1510–1616 of `status.json` (the ten container items), `preview.json` and `doctor.json` identical — no other listing-dependent field. The unit test, Windows, unfixed: *… differs at index 2* (NTFS answers case-insensitively: `beta` before `Delta`, `_under` last). Fixed in the product (`PhysicalFileSystem.ListDirectories` / `ListFiles` sort ordinally; `IFileSystem` documents it) rather than by a golden rule — the order was the CLI's own non-determinism, and every reader of a listing inherits the fix; the regenerated golden only reorders that block (same lines as a multiset). After it: two ext4 generations byte-identical, ext4 and tmpfs byte-identical, and the checked-in set equal to both |
| found while verifying — the host scenario *the focused window asks status* raced an earlier `status` still in flight | `src/test/host/suite.ts` | twice in three local runs on stable 1.140.0: `+ actual - expected  + []  - [ '-d Ubuntu --cd / --exec /opt/wsl-care/bin/wsl-care status --json' ]` — activation's own focused tick (or a real window-state event while focus was not overridden) was still running, and the client SHARES a run in flight, so the scenario's focus logged no request of its own. The same product code passed in a clean copy of the tree, so the race is the test's: it now overrides focus off and waits until nothing is in flight before resetting the log; then green on 1.85.0 and stable (one later run's scenarios all passed but `run-host.mjs` failed removing its temporary folder, `EPERM` — a VS Code child still holding it; the next run was green end to end) |

### What the rename to AI OS Care rests on (owner decision, 2026-10-06)

The extension is **AI OS Care**, id `ai-os-care`, publisher `remsoftdev` — `remsoftdev.ai-os-care`. The daemon keeps
`wsl-care`; the setting and command keys stay `wslCare.*`.

| Guarantee | Test | Observed red |
|---|---|---|
| the manifest's `name` is `ai-os-care`, `displayName` `AI OS Care`, `publisher` `remsoftdev` (never the placeholder) | `manifest.test.ts` | on the tree before the rename: `'wsl-care' !== 'ai-os-care'` and `'publisher-tbd' !== 'remsoftdev'` |
| no shipped file of the `.vsix` (every text file `vsix-files.txt` names — the bundle and the SVG icon included; a line is read with its `\uXXXX` escapes and `&nbsp;` decoded, since esbuild escapes non-ASCII) says "WSL Care" | `productName.test.ts` | on the tree before the rename: 25 lines listed — `CHANGELOG.md:3`, `README.md:1`, `README.md:11`, `README.md:28`, eleven lines of `dist/extension.js`, `media/panel.css:1`, `media/panel.js:1`, eight lines of `package.json`; its companion (the new name in the manifest and the bundle, four planted spellings found, `wslCare.*` keys and the daemon path not mistaken for the name) red with *the manifest carries the new display name* |
| the Marketplace text (README, CHANGELOG, the manifest's display strings) names `wsl-care` only as the daemon — `/opt/wsl-care/bin/wsl-care`, "the `wsl-care` daemon" | `productName.test.ts` | green before and after (the old text already used `wsl-care` only for the daemon); its companion shows the allowlist removes the README's daemon reference and keeps a planted *wsl-care extension* |
| the `.vsix` vsce writes is `<name>-<version>.vsix` and `check-vsix.mjs` reads both from `package.json`; the release workflow, the asset check and `POST_DEPLOY.md` item 6 use `ai-os-care-<version>.vsix` and `<publisher>.ai-os-care` | `npm run check:vsix` in CI (exit 2 when the derived file is missing — `vsixCheck.test.ts`'s real-artefact test skips under `npm test`, which re-bundles after any package), `test -f "ai-os-care-$VERSION.vsix"` in `release-extension.yml`, `ReleaseExtensionWorkflowTests`, `ReleaseExtensionScriptFlows` | `npm run package` wrote `ai-os-care-0.0.0.vsix` (vsixmanifest `Id="ai-os-care" Publisher="remsoftdev"`); `check-vsix` clean, `--release` clean |

### What the distribution-switch guarantee rests on (retro review of PR #9, 2026-10-06)

Found by the consultant of the retro coai review of PR #9 and confirmed by reading the code: the client shared a call
in flight by VERB alone and the poller stored whatever answered last, so after a `wslCare.distro` switch the panel
could show the previous distribution's `preview` (or `doctor`) under the new one's heading. All ten tests are in
`src/test/distroSwitch.test.ts`; the first four were run against the unfixed `main` (c61ec98) first.

| Guarantee | Test | Red observed against the unfixed code |
|---|---|---|
| a `preview` asked after the switch reaches the new distribution, not the call still in flight for the old one | *a preview asked for another distribution is not joined …* (client, recording runner) | *the call for Debian must reach Debian, not share the Ubuntu call in flight* — `'Ubuntu' !== 'Debian'` |
| the same, end to end: the real client, poller and store after a switch hold only the new distribution | *the panel never shows the previous distribution's preview …* | *the panel holds a preview of a distribution the setting no longer names* (the Ubuntu preview was released only after Debian's round had asked its own, which is what the user path does) |
| a late answer of the previous distribution never overwrites the new one's | *a late answer for the previous distribution …* (poller) | *Ubuntu's late preview replaced Debian's* |
| a switch seen by a status-only poll (panel hidden) leaves no `preview` / `doctor` of the previous distribution | *a distribution switch seen by a status-only poll …* | *still showing Ubuntu's preview under Debian* |
| a status poll of the SAME distribution never drops a `preview` in flight (the guard against an over-eager generation) | *a status poll of the SAME distribution …* | green before and after (no defect to show); teeth: the generation bumped on EVERY round → red |
| a round made obsolete while its `status` is pending asks no `preview` / `doctor` once that status answers (added by the fix PR's own coai plan round) | *a round made obsolete while its status is pending …* | written after the fix; teeth: the generation check in `afterStatus` removed → *the obsolete round went on to ask preview / doctor after its status answered* |
| a switch while a round waits for its `status`, with NO new round started (the window unfocused, so the config change's tick asks nothing), ends that round without asking or storing anything (the fix PR's own coai code round, 3 findings, one gap) | *a switch while a round waits for its status, with NO new round started …* | against the first fix: *the round asked the new distribution for preview / doctor beside the old status*; teeth: `isCurrent` no longer re-reading the setting → red |
| a switch between the `status` being stored and the round going on asks no `preview` / `doctor` (the boundary `afterStatus` guards; the consultant's addition) | *a switch between the status being stored and the round going on …* | teeth: `afterStatus` comparing the generation without re-reading the setting → *the round went on to ask the new distribution for preview / doctor* |
| a switch in an unfocused window clears what was shown and asks nothing (the consultant's addition to the same round) | *a switch in an unfocused window clears what was shown …* | against the first fix: *the previous distribution's answers are still shown*; teeth: the `observeTarget()` before `tick()`'s focus guard removed → red |
| a non-string `wslCare.distro` (a hand-edited `42`, `null`, an object, `{"toString": null}`) is refused as `distroRefused` before any spawn and never throws (the final code round; the branch had made the setting's read synchronous, so a `.trim()` on a number would have thrown out of a timer callback) | *a non-string wslCare.distro … is refused as a value, never thrown* | against the branch before it: *run() threw for 42*; then, with a first normaliser that printed the value, *run() threw for {"toString":null}* — `Cannot convert object to primitive value` (the consultant's case); green with `distroSettingText` naming only the type |

Break-it, each restored byte for byte: the client key without the setting → the first two red; the poller's obsolete-
round check removed → the second and third red; the store not cleared on a new target → the fourth red.

### What the manual Marketplace route and the clean changelog rest on (owner decision, 2026-10-06)

The owner publishes the extension by uploading the attested `.vsix` by hand — no `VSCE_PAT`, no Entra identity
(docs/repo-settings.md, step 9). And the first release pull request (#19) showed release-please keeping the extension
CHANGELOG's preamble BELOW the release notes under a stray `## Changelog`.

| Guarantee | Test | Observed red |
|---|---|---|
| `POST_DEPLOY.md` item 12 — its own command, read from the row and run under `/bin/sh` as post-deploy-check runs it — passes for `VSCE_PAT expires: none — manual upload`, for `none — OIDC` and for a PAT more than 30 days from expiry (60 and 31 days ahead); fails for a PAT exactly 30 days ahead (the boundary — the day is read as its UTC midnight and must lie MORE than 30 days after now) or inside 30 days, the `none yet` placeholder, a bare `none` and a missing line (dates derived from now at noon UTC — the command asks `date`) | `ReleaseExtensionScriptFlows.Post_deploy_item_12_passes_a_manual_upload…` (Linux; run in WSL as the user) | before the row changed: *Expected (result.Exit == 0) to be True because item 12 over 'VSCE_PAT expires: none — manual upload (…)' (exit 1)* |
| the manual route RUN (coai plan round 1, accepted): publish-marketplace's three scripts, taken from `release-extension.yml` and run under `bash -e -o pipefail` with a fake vsce at the path the job calls (it checks `show <id> --json`, logs each call) and a failing fake `sleep` — a hand-uploaded version listed after another one gives `served=true` (so the publish step's `if:` skips it) and ends the wait on its first query with no sleep; another version only, vsce 4.0.0's `undefined` and an empty list give `served=false`; the publish step with an empty `VSCE_PAT` exits 1 naming `VSCE_PAT is not set` before any vsce call. NOT proved: GitHub's Environment approval, the real Marketplace, github-public's gh calls (structure only, ReleaseExtensionWorkflowTests) | `ReleaseExtensionScriptFlows.The_manual_upload_route_finds_the_version_served…` (Linux; WSL as the user) | written against the unchanged workflow, so seen red by breaking the copy's workflow: the served answer forced to false → *Expected served.Output "served=false" to contain "served=true"*; a `sleep 30` before the first query → *Expected wait.Result.Exit to be 0 … but found 99*; and item 12 put back to OIDC only → its test red again over the manual-upload line. The copy restored byte for byte after each |
| the installed extension equals the attested `.vsix` FILE BY FILE (coai code round 2 on the manual route, accepted: a bundle-only check passes a package.json pointing `main` at an added file): `compare-installed-extension.sh` — every `extension/` file present with the same bytes, nothing extra, apart from what VS Code itself does as measured on an installed Marketplace extension (adds `.vsixmanifest`, writes `__metadata` into package.json, compared as JSON without it); an unreadable `.vsix` exits 2, never a pass — also one that opens but whose member fails to read — a CRC mismatch (coai code round 3) or a damaged LZMA member written by python3 (round 4); each seen red as a Python traceback with exit 1 before the read was guarded. Only the archive open and each member read are guarded, so a defect of the script itself still surfaces (round 5). ONE script for the manual upload's pre-approval step (docs/repo-settings.md step 9) and `POST_DEPLOY.md` item 6, which `ReleaseExtensionWorkflowTests.Post_deploy_item_6…` now holds to calling it (and to naming no `dist/extension.js`) | `ReleaseExtensionScriptFlows.The_installed_extension_must_equal_the_attested_vsix_file_by_file…` (Linux; WSL as the user) | the script landed with the test, so its teeth were shown on the WSL copy, each restored byte for byte: extras no longer refused → *Expected result.Exit to be 1 because an added file alone: … is the attested build (4 files)*; only `dist/extension.js` compared (round 1's design) → the `main`-pointed-at-an-added-file case red; `__metadata` not removed → *Expected same.Exit to be 0 … package.json: differs* |
| every release-please package's changelog is empty or already holds a version heading — release-please's updater (`src/updaters/changelog.ts`, read in its source) inserts before the first `\n###? v?[0-9[]` and otherwise keeps non-empty text BELOW the new entry with its H1 demoted; derived from the configured packages | `ReleaseConfigTests.Every_package_changelog_is_empty_or_starts_its_entries…` | with the preamble in `src_vs_code/CHANGELOG.md`: *src_vs_code/CHANGELOG.md must be empty or already hold a release heading*; `src_daemon/CHANGELOG.md` (already released) passes. Meanwhile #19 was merged and `extension-v0.1.0` cut (2026-10-06) with the stray tail already in the file — so the fix keeps `# Changelog` + the 0.1.0 entry and removes the stray `## Changelog` preamble below it (a tag is never moved: the 0.1.0 package keeps the tail; 0.1.1 onwards does not) |

### What the extension's tests do not prove

- **No real `wsl.exe` is ever started by a test** — by design (the tripwire). The fake's answers are the measured ones of
  WSL 2.7.10.0 on one machine; a different WSL version that changes an encoding or a message is caught only by the E5
  live gate's named check (the real extension → `wsl.exe` → daemon path, plan §15g m6).
- **Unmeasured shapes the fake still answers**: what `--list --running --quiet` prints when NO distribution runs (both
  candidate shapes are answered, and the client makes no `-d` call for either), and the old-glibc loader line (its
  documented shape). The daemon's coloured stderr through `wsl.exe` is E1.S3's direct observation, not observed through
  `wsl.exe`.
- **Whether the AOT `wsl-care` ends when `wsl.exe` is killed** — measured for `sleep` (it does), for a process ignoring
  SIGHUP (it does not) and, since 2026-10-04 (§15h #3), for a JIT build of `wsl-care preview --all --json` with its
  `docker` child (both end, [2026-10-03_wsl_exe_facts.md](2026-10-03_wsl_exe_facts.md) row 18); the AOT binary itself is
  the installed daemon of the E5 live gate.
- **What VS Code's real terminal does with `sendText(command, false)`**, and where a UI-kind extension's terminal opens
  in a Remote – WSL window — the tests see the RECORDER's calls; the real terminal is an E5 live-gate observation
  (`POST_DEPLOY.md` item 3).
- **The release run itself** — `vsce publish`, the Marketplace's validation and serving, the Environment's approval, the
  attestation and the uploads to a real draft never run outside a release; the tests hold the workflow's structure and
  run its two scripts. `POST_DEPLOY.md` items 6 and 12 check the result after the E5 live gate.
- **No screenshots** — the Marketplace README ships without images (no synthetic-fixture render pipeline here).
- **Which settings file a UI-kind extension reads in a Remote-WSL window** — an E5 live-gate observation.
- **The page harness is not a browser.** It runs `media/panel.js` against a modelled DOM that is stricter than a
  browser; it does not lay out, paint or apply the CSP. The CSP is asserted over the shell's TEXT (parsed into
  directives), and the real webview — the page under its nonce-only CSP inside VS Code — is exercised by the
  extension-host tier, which observes only the row count the page reports (no VS Code API exposes a webview's DOM).
- **The extension-host tier ran locally on Windows only** (2026-10-04: 1.85.0 and stable 1.140.0, both launches green);
  the Linux leg runs it in CI under xvfb, where the platform gate means it proves the "Windows + WSL only" notice and
  that nothing reaches the runner — not the panel's figures.
- **Window focus in the extension host** is set through the test API's override (a test runner cannot focus a window):
  the scenarios prove what the poller does for a given focus state, not that VS Code reports focus correctly — the real
  `onDidChangeWindowState` wiring is thin and is the E5 live gate's to observe.
- **The run-log churn** (M1) is measured over the poller on a simulated day; what one `status` run costs the daemon in
  bytes and time is read in its source, not observed (the daemon is not installed here) — an E5 live-gate check.

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
| `wsl-care config set processes.families testhost,other` refused (plan §15q R1.3, review B1): exit 2, one message naming the key, the stranger and the allowed families; nothing written | covered | `ConfigTrustFlows.Config_set_refuses_a_family_list_that_would_widen_A11_and_writes_nothing`; also `ConfigKeyClosureTests` |
| `wsl-care config set archive.baseFolder <path>` refused: a machine-only key, the machine layer named, nothing written | covered | `ConfigTrustFlows.Config_set_refuses_a_machine_only_key_naming_the_machine_layer` |
| root (claimed) reading the target user's layer WITH interop: their values taken, no notice | covered | `ConfigTrustFlows.With_interop_a_root_run_takes_the_target_users_layer_as_their_intent` (Linux legs) |
| root (claimed) reading the target user's layer WITHOUT interop: only tightening values taken, `config get --json` and `status --json` carry `configNotices` naming interop and the machine layer; `status` carries `userLayerDigest` | covered | `ConfigTrustFlows.Without_interop_a_root_run_takes_only_the_tightening_values_and_every_answer_says_why` (Linux legs) |
| no `config` verb starts any tool: every config verb's example, a refused set, a broken layer and its repair leave the fakes' argv log empty | covered | `ConfigFlows.No_config_verb_starts_any_tool`; the log is proved alive by `FakeToolFlows` |
| every registered verb's `Example` runs against the built CLI: exit 0 or 2, never 70 | covered | `VerbRegisterTests.Every_registered_verb_runs_its_example_against_the_built_cli_without_crashing` (one case per verb, derived) |
| `wsl-care status [--json]` over the captured procfs tree (Linux): exit 0 in under 2 s wall clock (measured around the process, after one unmeasured warm-up start), `schemaVersion`, the fixture's `MemTotal`, 10 containers, 51 processes, a cwd read through a real symlink, `df` available — and the fakes' argv log EMPTY with `docker` and `powershell` on the `PATH` (plan §15b #5) | covered (Linux legs; skipped on Windows with the reason) | `StatusFlows.Status_json_over_the_captured_procfs_answers_within_the_budget_and_starts_no_slow_process`; in-process on every OS: `StatusCommandTests`; AOT binary: CI status smoke (Linux over the same tree) |
| `wsl-care status [--json]` on this binary's own side: under 2 s, the Windows binary answers host RAM / drive / `vmmemWSL` and names the VM as the other binary; the Linux binary over an empty root reports memory unavailable with the path and no value key; no slow part recorded yet; no tool started | covered | `StatusFlows.Status_json_on_this_binarys_side_answers_within_the_budget_names_what_it_cannot_read_and_starts_nothing`; AOT binary (`win-x64`): CI status smoke |
| `wsl-care status [--json]` after a full run recorded slow parts: `docker stats` come back from `history.jsonl` with the run id and their age, the Windows clock unavailable; nothing started | covered | `StatusFlows.Status_reads_the_slow_parts_back_from_the_last_full_run_with_their_age`; also `LastFullRunTests`, `StatusCommandTests` |
| `wsl-care status [--json]` as text, and a stray argument refused with exit 2 and one `wsl-care:` message | covered | `StatusFlows.Status_without_json_prints_text_and_a_stray_argument_is_refused_with_the_usage_code`; also `CommandLineTests` |
| `wsl-care status [--json]` over the 2026-10-01 18:36 memory state (SYNTHETIC, `ProcfsVariants.October1Evening`: the baseline's dump, `MemAvailable` left out) on the captured tree (Linux): `memory.fragmentation` critical, `memory.pageCache` and `memory.inactiveAnon` warn, `memory.available` unknown naming `MemAvailable`, basis `sample` at the sample's instant; no tool started | covered (Linux legs; skipped on Windows) | `StatusFlows.Status_json_over_the_2026_10_01_evening_is_critical_on_fragmentation_and_warns_on_cache_and_inactive_anon`; also `StatusVerdictsTests` |
| `wsl-care status [--json]` over a fresh boot (SYNTHETIC, `ProcfsVariants.FreshBoot`) on the captured tree (Linux): the seven memory verdicts ok | covered (Linux legs; skipped on Windows) | `StatusFlows.Status_json_over_a_fresh_boot_is_ok_on_every_memory_verdict`; also `StatusVerdictsTests` |
| `wsl-care status [--json]` after `config set thresholds.memAvailableWarnPercent 70` over the captured tree (Linux): `memory.available` ok before, warn after, limit `warn < 70 %` | covered (Linux legs; skipped on Windows) | `StatusFlows.A_threshold_set_in_the_user_layer_moves_the_verdict_status_answers`; in-process on every OS: `StatusCommandTests.A_threshold_set_in_the_user_layer_changes_the_verdict_status_answers` |
| `wsl-care status [--json]` names `productVersion` exactly as `--version` prints it | covered | `StatusFlows.Status_json_names_the_product_version_exactly_as_version_prints_it`; AOT binary: CI smoke |
| `wsl-care status [--json]` in each running state a scenario stages against the REAL process table — `live` (a `running.json` held by the test process, its real start, a fresh heartbeat), `wedged` (heartbeat ten minutes old), `dead` (a pid no process has), `unreadable` (`{}`), `queued` (a request file): each answers its state, `schemaVersion` 1, `capabilities` = `Capabilities.All`; no tool started | covered | `ReadContractFlows.Status_reads_each_staged_running_state_through_the_real_process_table` (5 cases); in-process: `RunningReportsTests` (+ unknown, a pid that is another process now, live over a waiting request, a request filed under another run's name, strays in the folder), `ReadContractCommandTests` |
| `wsl-care status [--json]` with nothing in flight: `running.state` `none`, `actions` this side's registry ids in execution order (the Windows binary: none), `lastCleanup` unavailable naming why | covered | `ReadContractFlows.Status_with_nothing_in_flight_names_none_this_sides_actions_and_no_cleanup_yet`; in-process: `ReadContractCommandTests.Status_names_this_sides_actions_…`, `LastCleanupTests` (the newest cleanup with a failed action's real deletions; a dry run and a run that removed nothing are none) |
| `wsl-care status [--json]` reports a DEAD run and never sweeps it: `running.json` byte-identical, no history line — and with the state directory made read-only (0555, Linux) it still answers `dead` and the tree is unchanged | covered (the writable half on every OS; the read-only half on the Linux legs, skipped for root) | `ReadContractFlows.Status_reports_a_dead_run_and_never_sweeps_it_even_with_the_state_directory_unwritable`; in-process: `RunningReportsTests` through a file system that FAILS on any write, move, delete, lock or probe; `ReadContractCommandTests.Status_reports_a_dead_run_and_the_last_cleanup_without_sweeping_or_writing_anything` |
| `wsl-care status [--json]` and `runs show` over a LEFT-OVER `running.json` whose run has a history line (it recorded itself and died before removing the file): status `none` naming the left-over file, runs show `done` — the two agree (E6.S0 review D3) | covered | `ReadContractFlows.A_left_over_running_json_of_a_recorded_run_is_none_in_status_and_done_in_runs_show`; in-process: `RunningReportsTests` (D1 clock steps, D3, D4, S2, S3), `RunShowTests` (S2, D4), `RunRequestsTests` (S1) |
| `wsl-care status [--json]` after `collect`: `verdicts` carry every id the run recorded, in its order; each full-run verdict is the detail's own record with the run id | covered | `CollectFlows.Collect_records_detail_then_history_and_status_shows_its_slow_parts_with_their_age`; in-process: `FullRunCommandTests.Status_after_a_collect_carries_the_full_runs_own_verdict_records_with_its_run_and_their_age` |
| the golden contracts: `collect`, then `status --json`, `preview --all --json`, `doctor --json` over the captured fixtures (every age limit 0) — and, since E6.S0, `status --json` in each staged running state, `act A4 --preview --json` over 387 synthetic volumes, `runs show` (done / interrupted / unknown), `runs` / `logs` over a UTC-midnight-crossing local day — normalised, equal to `contracts/golden/head/*.json` | covered (Linux legs; skipped on Windows) | `GoldenContractTests.The_checked_in_goldens_are_what_the_built_cli_answers_at_this_commit_and_every_normalisation_rule_still_matches` |
| `contracts/actions.json` and `contracts/exit-codes.json`: equal to what `ActionId.All`, `ActionId.ExecutionOrder` and every value of `ExitCode` enumerate (never retyped); the companion asserts the second switches, the edges and the counts | covered (every OS) | `ContractFilesTests.The_checked_in_contracts_equal_what_the_types_enumerate`, `ContractFilesTests.The_contracts_hold_every_id_and_every_code_including_the_second_switches_and_the_edges` |
| `wsl-care preview --all [--json]` over the docker answers CAPTURED on 2026-10-02 at limit 0: A4 3 volumes / 641.4 MB, A5 13 containers, A6Unused 10 images, A7 4 entries, 13 kept named volumes — and A6Unused, A7 and A4 + kept each land on Docker's OWN `system df` reclaimable; 28 unbounded logs; `volume-seen.json` NOT written although the sandbox is writable (preview only reads, plan §15b #3); the fakes saw exactly the five product argvs, every one a read verb | covered | `PreviewFlows.Preview_over_the_captured_docker_at_limit_zero_reproduces_its_rows_and_starts_docker_read_verbs_only`; in-process: `PreviewCommandTests`, `CleanupPreviewTests` |
| `wsl-care preview --all [--json]` at the shipped limits: a volume first seen now is left (note: 3 younger), one first seen two days ago (a pre-written `volume-seen.json`) is counted, and its first sighting survives the look | covered | `PreviewFlows.At_the_shipped_limits_a_volume_first_seen_now_is_left_and_one_first_seen_two_days_ago_is_counted` |
| `wsl-care preview --all [--json]` when Docker cannot answer: not on PATH → `notInstalled`, a stopped daemon (Docker's real stderr) → `daemonStopped` with only the version probe run, a hang → `timedOut` at the 10 s probe ceiling with the tree killed; every row `available: false` with the reason and NO `count` / `reclaimableBytes` key; exit 0 | covered | `PreviewFlows.Without_docker_on_the_path_…`, `PreviewFlows.A_stopped_daemon_…`, `PreviewFlows.A_docker_that_hangs_…`; classification: `DockerCliTests`, `DockerCollectorTests` |
| `wsl-care preview --all [--json]` with a `docker` decoy in the CLI's current directory: the fake on `PATH` answers every call; with no docker on `PATH` the answer is `notInstalled` and the decoy is never started | covered | `ToolResolutionFlows.A_docker_in_the_current_directory_is_never_started_the_one_on_the_path_is`, `ToolResolutionFlows.Without_docker_on_the_path_a_docker_in_the_current_directory_still_leaves_docker_not_installed`; rules: `ExecutableResolverTests` |
| `wsl-care preview --all [--json]` on a WRITABLE state directory (the privileged case): `volume-seen.json` is not created where absent and stays byte-identical where present (its name Docker no longer lists survives), `volumeSeen.recorded: false` with a `read-only:` reason naming `collect`, the rows still count from the in-memory observation. **Observed red first** (2026-10-02, unfixed `PreviewRun`): `Expected boolean to be False because a preview creates no volume-seen.json, even where it could, but found True` (gate finding #3/#6/#10); green after the fix | covered | `PreviewFlows.Preview_on_a_writable_state_directory_still_never_writes_volume_seen_json`; in-process: `PreviewCommandTests` |
| `wsl-care preview --all [--json]` with the state directory unwritable (`AccessDenial`): `volumeSeen.recorded: false`, `read-only:` reason, the rows still answer, nothing written | covered | `PreviewFlows.An_unwritable_state_directory_makes_preview_read_only_and_it_still_answers`; also `VolumeSeenTests` |
| `wsl-care preview --all [--json]` as text, and `preview` without `--all` refused with exit 2 and one `wsl-care:` message | covered | `PreviewFlows.Preview_without_json_prints_the_rows_and_preview_without_all_is_refused`; also `CommandLineTests` |
| `wsl-care collect [--timer or --detach] [--json]` over the captured Docker AND health answers (and, on Linux, the procfs tree): exit 0, `recording: recorded`, ONE history line naming the detail file that exists, the A4 row of the captured Docker, the clock-jump count of the capture (875, Linux), the `wslconfig.memory` verdict naming `memory=36GB`; every fake call a read command — then `status --json` returns `docker stats` (and on Linux the Windows clock, launch latency subtracted) from THAT run with its age | covered | `CollectFlows.Collect_records_detail_then_history_and_status_shows_its_slow_parts_with_their_age`; write order in-process: `CollectRunTests.The_detail_is_written_first_then_the_history_line_that_names_it`; `FullRunCommandTests.Collect_json_records_the_run_and_status_then_reads_its_slow_parts_back_with_their_age` |
| `wsl-care collect [--timer or --detach] [--json]` with the state directory unwritable (`AccessDenial`; on Linux the system log directory too): exit 0, `recording: readOnly`, the `wsl-care: read-only: run as root to record` message, no history line, no `runs/`, no first sighting — and on Linux the run log under `$XDG_STATE_HOME/wsl-care/logs` | covered | `CollectFlows.An_unwritable_state_directory_makes_collect_measure_print_and_record_nothing`; in-process: `CollectRunTests.An_unprivileged_run_measures_and_reports_but_writes_nothing_and_says_read_only`, `FullRunCommandTests.An_unprivileged_collect_…`, `FullRunCommandTests.A_run_that_may_not_write_the_system_log_directory_logs_to_the_users_own` |
| Under an INHERITED `INVOCATION_ID` (every scenario sets one, as every descendant of a systemd unit carries it — a CI runner job, a VS Code Server user service): `collect` without `--timer` is a `cli` run with no timer pass, and an `act` is `cli` / `manual`, never `timer` — only `--timer`, which the timer unit passes in its `ExecStart`, makes the run the timer's | covered | `LogsFlows.A_collect_under_an_inherited_INVOCATION_ID_without_timer_is_a_cli_run_and_acts_on_nothing` — red first: `…TimerPass to be <null> … but found …TimerPass`; the four `ActCommandTests` that failed on the GitHub runners (CI run 37129452377: `Trigger … Manual … but found Timer`, `DryRun … False … but found True`) pass with `INVOCATION_ID` set; the timer's own run: `LogsFlows.The_timers_full_run_…` with `collect --timer` |
| `wsl-care collect [--timer or --detach] [--json]` when a record cannot be written (exit 1, `recording: failed`, the reason) and while another run holds `run.lock` (exit 75, nothing measured) | covered (in-process) | `FullRunCommandTests.A_collect_whose_history_line_cannot_be_written_…`, `FullRunCommandTests.A_collect_while_another_holds_the_run_lock_…`, `CollectRunTests`; not staged against the built binary: a failing disk cannot be made to fail on cue there |
| `wsl-care doctor [--json]` after a `collect`: exit 0, `lastRun` ok, the follower that never ran a named `problem`, `healthy: false`; read commands only | covered | `CollectFlows.Doctor_after_a_collect_finds_the_last_run_recent_and_still_names_what_is_not_installed`; in-process: `DoctorTests`, `FullRunCommandTests.Doctor_json_answers_exit_zero_with_its_verdict_and_checks` |
| `wsl-care events follow [--once]` after a daemon restart that lost Docker's buffer: exit 0, ONE `gap` marker whose reason says how far the buffer reached, the start recorded as backfilled — then `status --json` counts the last 24 h `partial` with the gap named | covered | `EventsFlows.Once_after_a_daemon_restart_writes_ONE_unrecoverable_gap_and_status_counts_partial_naming_it`; rules: `CoverageTests` |
| `wsl-care events follow [--once]` over an idle engine that did not restart (the last marker's bridge, an EMPTY buffer): exit 0, `0 start(s), 0 gap marker(s)`, no gap marker, the new `covered` marker carries the engine, `docker network inspect bridge` among the read verbs, `starts-summary.json` written | covered | `EventsFlows.Once_over_an_idle_engine_that_did_not_restart_writes_no_gap_and_records_the_engine_on_its_marker`; rules: `CoverageTests` |
| `wsl-care events follow [--once]` with the daemon down: exit 0, the reason printed, no gap marker (a gap is known only once Docker answers), only the version probe run | covered | `EventsFlows.Once_with_the_daemon_down_exits_zero_names_why_and_writes_no_gap` |
| `wsl-care events follow [--once]` followed live (Linux): the socket down then up waited for IN-PROCESS (one 5 s wait), ONE gap marker, a live start in the day file while the stream is open, SIGTERM → exit 0 with the stop marker carrying how far coverage reached | covered (Linux legs; skipped on Windows with the reason) | `EventsFlows.Followed_live_it_waits_for_the_socket_in_process_records_a_start_and_stops_clean_on_SIGTERM`; on every OS in-process: `EventsFollowerTests` (the 5 → 10 → 20 s backoff on a moved clock), `FullRunCommandTests.Events_follow_stopped_by_a_signal_exits_zero_with_its_stop_marker` |
| `wsl-care act <A#>[,<A#>...] (--preview or --confirm) [--manual or --timer] [--detach] [--volume <name>]... [--only <file or ->] [--process <pid:start>]... [--json]` unprivileged: exit 77, ONE `needs root` message, empty stdout, no running.json / history line / dry-run stamp / run detail / lock file, no tool started | covered (skipped for a root or elevated account, with the reason) | `ActFlows.An_unprivileged_act_is_refused_whole_and_leaves_no_state_no_lock_and_no_command_behind`; in-process: `ActCommandTests.An_unprivileged_act_is_refused_whole_before_the_lock_or_any_state_is_touched` |
| `wsl-care act <A#>[,<A#>...] (--preview or --confirm) [--manual or --timer] [--detach] [--volume <name>]... [--only <file or ->] [--process <pid:start>]... [--json]` `--preview` with root claimed in the sandbox: previewed from the fake journalctl's `--disk-usage`, nothing written; on the Windows binary exit 2 naming the distro side | covered | `ActFlows.With_root_claimed_a_preview_reads_the_journal_s_size_and_writes_nothing`; in-process: `ActCommandTests`; AOT binary (`win-x64`): smoke by hand 2026-10-02 |
| `wsl-care act <A#>[,<A#>...] (--preview or --confirm) [--manual or --timer] [--detach] [--volume <name>]... [--only <file or ->] [--process <pid:start>]... [--json]` `--confirm` with root claimed: A10 runs through the fake (`--disk-usage`, then `--vacuum-time=30d`), detail then history line, `running.json` gone | covered (Linux legs; skipped on Windows with the reason) | `ActFlows.With_root_claimed_a_confirmed_act_runs_a10_through_the_fake_and_records_detail_then_history`; on every OS in-process: `ActCommandTests.A_confirmed_act_runs_records_and_answers_with_the_measured_result`, `JournalVacuumTests`, `ActionEngineTests` |
| `wsl-care act <A#>[,<A#>...] (--preview or --confirm) [--manual or --timer] [--detach] [--volume <name>]... [--only <file or ->] [--process <pid:start>]... [--json]` while the run lock is held: `act` 75, `collect` 75, nothing recorded — ONE lock for both | covered (`collect` on both families, `act` on the Linux legs) | `ActFlows.One_lock_for_collect_and_act_the_second_one_refuses_with_75_and_waits_for_nothing`; in-process: `ActionEngineTests.A_full_run_started_while_an_act_holds_the_lock_is_busy_one_lock_for_both`, `ActCommandTests` |
| `wsl-care act <A#>[,<A#>...] (--preview or --confirm) [--manual or --timer] [--detach] [--volume <name>]... [--only <file or ->] [--process <pid:start>]... [--json]` meeting a wedged run (76), an action that fails (3), an invalid layer (78), an unbuilt or other-side action (2), the timer's gates and dry run | covered (in-process) | `ActCommandTests`, `ActionEngineTests`; not staged against the built binary: a live wedged process and the systemd timer cannot be made on cue there |
| `wsl-care act <A#>[,<A#>...] (--preview or --confirm) [--manual or --timer] [--detach] [--volume <name>]... [--only <file or ->] [--process <pid:start>]... [--json]` `--preview` of EVERY action this build holds (derived from the registry) over the CAPTURED Docker, root claimed, a target user with no tool installed: exit 0, every id answered, A4 previews the three captured volumes, A8 is a skip naming npm, every docker call a read verb, no state written; the Windows binary exits 2 | covered (Linux legs; Windows: the exit-2 half) | `ActFlows.A_preview_of_every_action_this_build_holds_answers_each_from_live_state_and_starts_only_read_commands`; AOT binary: CI act smoke (every RID) |
| `wsl-care act <A#>[,<A#>...] (--preview or --confirm) [--manual or --timer] [--detach] [--volume <name>]... [--only <file or ->] [--process <pid:start>]... [--json]` `A4 --confirm --manual --volume <one>` over the captured Docker: exit 0, the fake saw exactly `docker volume rm <that one>`, freed = its captured `df -v` size, the key never serialised, the history line's trigger `manual` | covered (Linux legs; skipped on Windows with the reason) | `ActFlows.A_button_run_of_a4_removes_only_the_volume_the_panel_showed_records_the_manual_trigger_and_freed_from_the_confirmed_one`; in-process: `DockerCleanupTests`, `ActCommandTests` |
| `wsl-care act <A#>[,<A#>...] (--preview or --confirm) [--manual or --timer] [--detach] [--volume <name>]... [--only <file or ->] [--process <pid:start>]... [--json]` `A4 --preview` over 387 SYNTHETIC anonymous volumes (`SyntheticDocker`), root claimed: exit 0, count 387, 20 `items`, `shown` = all 387 names (each 64-hex), no state written | covered (Linux legs; skipped on Windows with the reason) | `ReadContractFlows.A4s_preview_over_387_volumes_carries_all_387_names_it_selected_and_writes_nothing`; in-process: `DockerCleanupTests.A4s_preview_outcome_carries_every_selected_name_as_shown_and_no_other_actions_outcome_carries_one`, `…A4s_shown_list_is_every_target_key_in_order_capped_at_the_most_a_shown_list_carries` |
| `wsl-care act <A#>[,<A#>...] (--preview or --confirm) [--manual or --timer] [--detach] [--volume <name>]... [--only <file or ->] [--process <pid:start>]... [--json]` `--confirm` cut off by SIGHUP while its vacuum runs (a fake `journalctl` that waits 60 s): exit 130, ONE history line `interrupted` whose reason names SIGHUP, its detail written, `running.json` gone | covered (Linux legs; skipped on Windows with the reason) | `ReadContractFlows.A_confirm_cut_off_by_SIGHUP_records_itself_interrupted_with_a_detail_naming_the_signal` |
| `wsl-care act <A#>[,<A#>...] (--preview or --confirm) [--manual or --timer] [--detach] [--volume <name>]... [--only <file or ->] [--process <pid:start>]... [--json]` with `--manual` AND `--timer`: exit 2, ONE message saying "not both", no tool started, no state | covered | `ReadContractFlows.Manual_and_timer_together_are_refused_before_anything_is_touched`; in-process: `ActCommandTests.Manual_and_timer_together_are_refused_naming_both`; `productVersion` on every act answer: `ActCommandTests.Every_act_answer_names_the_product_version_exactly_as_version_prints_it` |
| `wsl-care act <A#>[,<A#>...] (--preview or --confirm) [--manual or --timer] [--detach] [--volume <name>]... [--only <file or ->] [--process <pid:start>]... [--json]` `A10 --confirm --manual --detach --json` with root claimed and a fake `systemctl`: exit 0, `accepted` with the run id and `wsl-care-act@<runId>.service`, the fake saw exactly `systemctl start --no-block <that unit>`, ONE request file named for the run — then `act --request <runId>` (as systemd would start it) records ONE `completed` line under THAT run id, trigger `manual`, the request gone, no running.json | covered (Linux legs; skipped on Windows with the reason) | `DetachFlows.An_accepted_detach_starts_its_unit_and_the_unit_s_request_run_records_under_the_answered_run_id`; in-process: `DetachedRunsTests.A_confirmed_detach_writes_the_request_starts_its_unit_and_answers_accepted_at_once` |
| `wsl-care act <A#>[,<A#>...] (--preview or --confirm) [--manual or --timer] [--detach] [--volume <name>]... [--only <file or ->] [--process <pid:start>]... [--json]` `--detach` refusals: no systemd (`/run/systemd/system` absent) exit 69 and nothing written, never a synchronous run; a run queued within its grace (75), wedged (76), an unreadable `running.json` (79), another run holding the lock (75 / 76); an orphaned request past its grace or an unusable one swept first and the detach accepted; a timed-out start asking the unit (accepted / 71 / `unknown`); the request folder at its budget of 32 (73); a unit that will not start: the request removed, exit 71; unprivileged (77); `--detach` with `--preview` or `--timer` (2) | covered | `DetachFlows.Without_systemd_a_detach_is_refused_with_69_and_nothing_is_written_or_started` (every OS); in-process: `DetachedRunsTests` (`Without_systemd_…`, `A_detach_while_a_run_is_queued_…`, `A_detach_meeting_a_wedged_run_…`, `A_detach_meeting_an_unreadable_running_json_…`, `A_detach_records_an_unusable_request_refused_…`, `A_detach_sweeps_an_orphaned_request_…`, `A_detach_while_another_run_holds_the_lock_…`, `A_timed_out_start_asks_the_unit_…`, `A_full_request_folder_…`, `A_unit_that_will_not_start_…`, `An_unprivileged_detach_…`, the parse theory) |
| `wsl-care act <A#>[,<A#>...] (--preview or --confirm) [--manual or --timer] [--detach] [--volume <name>]... [--only <file or ->] [--process <pid:start>]... [--json]` `A4 --confirm --detach --only -`: 10 000 names on stdin reach the request's `shown`; 10 001 names, 1 MiB + 1 bytes, a bad line (refused by its NUMBER, never echoed) and a stdin with no end within the ceiling (10 s; 0.3 s in the test) all exit 2 with nothing written | covered (in-process) | `DetachedRunsTests.Ten_thousand_names_on_stdin_…`, `Stdin_past_the_count_or_the_byte_cap_…`, `A_bad_line_on_stdin_…`, `Stdin_with_no_end_within_the_ceiling_…`; the relay through `wsl.exe` measured, not tested: facts note row 20 |
| `wsl-care act --request <runId>` meeting the run lock (the timer holds it): exit 75, ONE `refused` history line under the run id naming `busy:`, the request removed — never a silent busy; under an invalid layer: a `refused` line, exit 78 | covered (Linux legs for the built binary) | `DetachFlows.A_request_whose_unit_meets_the_timer_s_lock_records_refused_and_removes_itself`; in-process: `DetachedRunsTests.A_request_that_meets_the_lock_is_recorded_refused_…`, `A_request_under_an_invalid_configuration_…` |
| `wsl-care act --request <runId>` with no request: exit 80, a named no-op, no history line; with a request whose content root never writes, or one planted group-writable (Linux): exit 2 through the hardened reader, nothing run; a request of kind `collect`: a full run recorded under its id; while its run acts, running.json stands and the request is already gone; it sweeps another stale request whose unit is gone before it runs | covered (in-process) | `DetachedRunsTests.A_missing_request_is_a_named_no_op_…`, `A_request_whose_run_already_recorded_itself_…`, `A_request_whose_content_is_not_what_root_writes_…`, `A_planted_group_writable_request_…`, `A_collect_request_records_…`, `While_a_request_s_run_acts_…`, `A_request_sweeps_another_stale_request_…`; Core: `DetachedRunTests` |
| `wsl-care act --request <runId>` whose child never exits (a fake `journalctl --disk-usage` that sleeps 10 min): the step ends at its OWN 15 s ceiling (tree kill), the run still records ONE line under its id, the request goes, the lock is released (plan §15k #0) | covered (Linux legs) | `DetachFlows.A_child_that_never_exits_is_ended_by_its_own_ceiling_and_the_detached_run_still_records_and_releases_the_lock` |
| `wsl-care collect [--timer or --detach] [--json]` as root with a request 30 minutes old whose unit the fake `systemctl show` reports `inactive` with no job: the request swept as ONE `interrupted` line ("swept: the detached run never recorded itself … is inactive with no queued job"), the request gone, beside the collect's own line; `collect --detach` is accepted like an act | covered (Linux legs) | `DetachFlows.A_stale_request_whose_unit_is_gone_is_swept_interrupted_by_the_next_root_collect`; in-process: `DetachedRunsTests.A_detached_collect_is_accepted_the_same_way`; Core: `DetachedRunTests` (history first, within the 60 s monotonic grace, another boot, a future stamp, the history read again, an unusable request, a queued job or an active state pending, the own request never swept, an unreadable unit kept) |
| `wsl-care act --stop <runId> [--json]` of a WEDGED run whose process lives in `wsl-care.service` or its own `wsl-care-act@<runId>.service`: `systemctl stop <that unit>`, a stop marker, exit 0 `stopping`; outside those units: nothing stopped, its pid named, exit 2; a live run: 75; a run not wedged here: 2; systemd refusing: no marker left, exit 1 | covered (in-process) | `DetachedRunsTests.A_wedged_run_in_one_of_the_two_units_…`, `A_wedged_run_outside_the_two_units_…`, `A_live_run_is_not_stopped_…`, `A_stop_systemd_refuses_…`; the SIGKILL record: Core `DetachedRunTests.A_dead_run_whose_stop_was_asked_…`; a real systemd stop: the E6 daemon live gate |
| the other E3.S2 actions end to end against the built binary (A5–A7, A9, A11, A12, A14, A17 confirmed) | not covered | in-process only (`DockerCleanupTests`, `UserCacheTests`, `PackageCacheTests`, `SuspectTerminationTests`): a confirmed user-scoped run needs `runuser` as root, and A11's real sender is never wired under a sandbox — by design |
| `wsl-care collect [--timer or --detach] [--json]` as the TIMER (`--timer`, as its unit starts it) over the captured answers and a 1.5 G journal: exit 0, the action pass ran after measuring in its dry-run week, ONE history line (trigger `timer`, `dryRun`), on Linux A10 `dryRun` (its trigger fired), on Windows every action skipped naming the distro side; every fake call a read command — then `runs --json` and `logs --json` read that run back | covered | `LogsFlows.The_timers_full_run_acts_after_measuring_in_its_dry_run_week_and_runs_and_logs_read_it_back`; in-process: `TimerPassTests`, `MemoryActionTests` |
| `wsl-care logs [--period <today, yesterday, yyyy-MM-dd or from..to> or --from <instant> --to <instant>] [--action <A#>] [--detail] [--json]` over a seeded history: `--period 2026-10-01` holds the 23:59:59 run only (3 objects, 3 GB, 1 with a cleanup, 1 button); a reversed range exit 2 with ONE message; nothing written but its run log | covered | `LogsFlows.Logs_and_runs_over_a_seeded_history_answer_a_utc_date_and_a_range_and_write_nothing`; in-process: `LogsCommandTests`, `RunLogsTests` |
| `wsl-care runs [--period <today, yesterday, yyyy-MM-dd or from..to> or --from <instant> --to <instant>] [--json]` over the same history: the range lists both runs, the dry one with what it would free | covered | `LogsFlows.Logs_and_runs_over_a_seeded_history_answer_a_utc_date_and_a_range_and_write_nothing`; in-process: `LogsCommandTests`, `RunLogsTests` |
| `wsl-care logs [--period <today, yesterday, yyyy-MM-dd or from..to> or --from <instant> --to <instant>] [--action <A#>] [--detail] [--json]` and `wsl-care runs …` over the LOCAL day 2026-10-02 at +03:00 (`--from 2026-10-02T00:00:00+03:00 --to 2026-10-03T00:00:00+03:00`): exactly the runs at 21:00:00Z (the first instant), 23:59:59Z and 00:00:00Z — not the one a second before, not the one at the end instant; a run line carries its `metrics` (none for the act); `logs` sums the button's A4 and the timer's A10 that no UTC day holds together | covered | `ReadContractFlows.Logs_and_runs_over_a_local_day_sent_as_two_instants_hold_the_runs_on_both_sides_of_utc_midnight`; in-process: `RunLogsTests` (the local day, half-open ends, the refused shapes — no offset, a bare date, an impossible date, reversed, empty, over 366 days — `RunLine.metrics`, `IsCleanup` / `Freed`), `ReadContractCommandTests` (parse: with `--period` or half given refused; a bad instant exit 2) |
| the E3.S3 actions confirmed against the built binary (A1, A2, A3, A15, A16) | not covered | in-process only (`MemoryActionTests`, `BuildServerTests`, `FilesystemTrimTests`, `ClockFixTests`, the property test): `sysctl`, `fstrim`, `hwclock`, `chronyc` are never faked as root writes on PATH here, and A3 needs `runuser` as root; their previews ARE run by the built binary in `ActFlows.A_preview_of_every_action_this_build_holds_…` |
| `wsl-care runs show <runId> [--json]` of a confirmed act (root claimed, the fake journalctl): exit 0, `done`, the detail `present`, A10's command `journalctl --vacuum-time=30d` with exit 0, the act's note that it swept a dead run; of that dead run (a `running.json` staged with a pid no process has): `interrupted` naming the gone pid; of a run id nothing names: `unknown`; every answer exit 0, `schemaVersion` 1 | covered (Linux legs; skipped on Windows with the reason) | `ReadContractFlows.Runs_show_answers_a_confirmed_act_done_the_run_it_swept_interrupted_and_a_stranger_unknown`; in-process on every OS: `RunShowTests` (done with removed / not-removed / commands and exits, a full run's timer pass, a lost detail, interrupted, refused, running, a dead holder = interrupted and NOT swept, queued from a request, history first, unknown), `ReadContractCommandTests.Runs_show_*` (parse, a malformed run id refused, text) |
| `wsl-care runs show <runId> [--json]` of a QUEUED run (a request file E6.S1 will write) and of a RUNNING one (a live holder of `running.json`) | covered (in-process) | `RunShowTests.A_run_named_only_by_a_request_is_queued_with_its_request_counted_not_repeated`, `RunShowTests.A_run_holding_running_json_with_a_live_process_is_running_with_its_running_block`; against the built binary: `DetachFlows` (E6.S1), whose `--detach` writes the requests |
| `wsl-care runs log <runId>` | not covered | CUT by plan §15j M3: `runs show` answers the commands a run ran and their exits |
| `wsl-care agents list [--measure] [--json]` with `--measure` over a planted Claude Code folder and a fake `claude` on PATH: exit 0, `schemaVersion` 1, `sizes.source` `now`, the agent detected by binary and folder, 100 bytes (its `memory/` never entered, named in `excluded`), one session counted and named, the version "not asked", and the fake never started; the text form; an unknown option refused (2); before a full run `none` with how to measure, after a `collect` the run's totals with no session name, no recorded file naming a session | covered (the full-run flow on the Linux legs; skipped on Windows with the reason) | `AgentsFlows` (4); in-process: `Agents/AgentCatalogueTests`, `AgentDiscoveryTests`, `AgentWalkTests`, `AgentNoOpenTests` (Linux); golden `agents-list.json` |
| `wsl-care agents probe <path> [--json]` as root: exit 81 (`NotAsRoot`), nothing on stdout, the refusal naming uid 0 and the default-user fix; of a CLI (the fake tool at `~/.local/bin/mycli` with an execute bit): exit 0, usable, the suggested entry with `~/.mycli`, and the CLI never started; a path of the wrong shape refused (2) | covered (the CLI probe on the Linux legs; the root refusal on every OS) | `AgentsExtraFlows` (2 facts); in-process: `AgentsCommandTests` (root, shape, JSON), `Agents/AgentProbeTests` (incl. the inotify no-open proof, Linux) |
| `wsl-care units dropin <unit>` (E7.S2c): the drop-in `install.sh` writes for one of the four units, from the machine layer — the timer's `OnCalendar` from `timer.periodHours`, the services' Nice / MemoryMax / TimeoutStopSec, the follower's RestartSec; another unit refused (2) naming the four | covered (in-process, every OS; the installer's use on the Linux legs) | `Cli.Tests/UnitsCommandTests` (2); `ShippedFilesTests.The_drop_in_of_the_defaults_…`, `…A_drop_in_carries_the_configured_values`; `InstallFlows` (the render before any unit is enabled, its failure) |
| `wsl-care archive preview / run / restore / list` | not covered | not built yet (E9) |
| `install.sh`: a fresh install — binary 0755 at `/opt/wsl-care/bin/wsl-care`, the link to that ABSOLUTE path, every unit byte for byte 0644, the machine layer when absent, the state folders; `systemctl` daemon-reload → enable --now timer + follower → enable --now sysstat + atop → is-active ×2; the binary started by its absolute path for `collect` then `doctor --json`; no sudo; the temporary folder gone | covered (Linux legs; the Windows leg skips with the reason) | `InstallFlows.A_fresh_install_places_the_binary_link_units_and_machine_layer_enables_both_units_and_verifies_through_the_absolute_path` |
| `install.sh`: the newest `daemon-v*` release (the list's first entry is the extension's), archive then `.sha256`, gh verifying THAT archive before any `systemctl`; every curl call asks for https-only, redirects included, under `--max-time` | covered (Linux legs) | `InstallFlows.The_newest_daemon_release_is_downloaded_never_the_extensions_and_verified_before_any_write` |
| `install.sh`: the newest daemon release from a COMPACT releases list (one line, `"tag_name":"…"` with and without a space): by version number (0.10.0 over 0.9.1), a pre-release (`-rc.1`) and the extension never chosen | covered (Linux legs) | `InstallFlows.The_newest_daemon_release_is_chosen_by_version_number_from_a_compact_releases_list` |
| `install.sh --version <x.y.z>`: no releases-list call; a malformed version (`../`, `v`-prefix, `;`, a newline) exit 2 before anything runs | covered (Linux legs) | `InstallFlows.An_explicit_version_skips_the_releases_list_and_a_malformed_one_is_refused_before_anything_runs` |
| `install.sh`: checksum mismatch → step `checksum`, no gh call, nothing installed; no `.sha256` → step `download`, nothing installed | covered (Linux legs) | `InstallFlows.A_checksum_mismatch_aborts_before_the_attestation_and_before_anything_is_installed`, `InstallFlows.A_release_without_its_sha256_is_refused_never_installed_unchecked` |
| `install.sh`: a refused attestation → step `attestation`, nothing installed; under `SUDO_USER` (and a `GH_TOKEN` in the environment) root verifies a bundle it fetched itself — the attestation API read unauthenticated, no `runuser`, gh's `HOME` / `GH_CONFIG_DIR` / `XDG_*_HOME` inside the temporary folder, no token | covered (Linux legs) | `InstallAttestationFlows.A_refused_attestation_aborts_before_anything_is_installed`, `InstallAttestationFlows.Under_sudo_root_verifies_a_bundle_it_fetched_itself_with_no_login_no_token_and_no_runuser` |
| `install.sh`: the attestation identity is EXACT — `release.yml` built from a branch, `release.yml` at ANOTHER release's tag, or on a self-hosted runner is refused with nothing installed; the verification asks `--cert-identity release.yml@refs/tags/daemon-v<version>` + `--deny-self-hosted-runners` + `--repo`, never `--signer-workflow` | covered (Linux legs) | `InstallAttestationFlows.An_attestation_of_release_yml_built_from_a_branch_is_refused_and_nothing_is_installed`, `…An_attestation_of_another_release_tag_is_refused_the_identity_is_the_tag_of_the_version_installed`, `…An_attestation_made_on_a_self_hosted_runner_is_refused` |
| `install.sh`: the bundle decoder — a real GitHub bundle (captured) decompressed exactly, under the system awk, mawk and gawk; several attestations, one genuine, suffice and each is verified alone; no attestation, or a stream that is not snappy, refused before anything | covered (Linux legs) | `InstallAttestationFlows.A_real_github_bundle_is_decompressed_byte_for_byte_so_gh_reads_its_signer` (×3), `…Among_several_attestations_one_genuine_bundle_is_enough…`, `…No_attestation_or_an_unreadable_bundle_is_refused_before_anything_is_installed` |
| `install.sh` without a usable `gh`: none, gh 2.45.0 (no `gh attestation`, Ubuntu 24.04's), gh 2.49.0 (older than the measured floor 2.56.0), a `--help` that fails or lacks a flag the check uses → step `preflight` BEFORE any download, pointing at GitHub's apt repository (`cli.github.com/packages`) and `--skip-attestation`, never at `apt-get install gh` or a login; nothing installed | covered (Linux legs) | `InstallAttestationFlows.Without_gh_the_installer_stops_before_downloading_anything_and_says_how_to_proceed`, `…A_gh_without_attestation_verify_is_refused_before_any_download_pointing_at_githubs_apt_repository`, `…A_gh_that_has_attestation_verify_but_is_older_than_the_measured_floor_is_refused_before_any_download`, `…A_gh_whose_attestation_verify_fails_or_lacks_a_flag_the_check_uses_is_refused_before_any_download` |
| `install.sh --skip-attestation`: installs without gh, the banner on stderr, a bad checksum still refused | covered (Linux legs) | `InstallAttestationFlows.Skip_attestation_installs_without_gh_says_so_loudly_and_still_refuses_a_bad_checksum` |
| `install.sh`: an existing `/etc/wsl-care/config.json` kept byte for byte | covered (Linux legs) | `InstallFlows.An_existing_machine_configuration_is_kept_byte_for_byte` |
| `install.sh`: a unit not active → step `verify: wsl-care-events.service active`; an unhealthy `doctor --json` → step `verify: doctor healthy` with doctor's checks on stderr; sar missing after apt → step `verify: sysstat (sar on PATH)`; sysstat still off after `dpkg-reconfigure` → step `packages` | covered (Linux legs) | `InstallFlows.A_unit_that_is_not_active_after_enabling_fails_the_install_naming_that_step`, `…An_unhealthy_doctor_fails…`, `…Missing_sysstat_and_atop_are_installed_with_apt…`, `…Sysstat_switched_off_is_switched_on_through_debconf…` |
| `install.sh --uninstall`: units, binary, link and `/opt/wsl-care` gone, history / logs / machine layer kept and named; `--purge` removes exactly the three folders and the lock, names each, touches nothing else | covered (Linux legs) | `InstallUninstallFlows.Uninstall_removes_the_units_binary_and_link_and_keeps_history_logs_and_machine_config`, `InstallUninstallFlows.Uninstall_with_purge_removes_exactly_the_state_logs_machine_config_and_lock_and_names_them` |
| `install.sh --dry-run` (install and uninstall): the prefix tree identical, no unit / package / binary call, every step printed, no root needed | covered (Linux legs) | `InstallFlows.Dry_run_changes_nothing_needs_no_root_and_prints_every_step` |
| `install.sh --set-default-user <name>`: without the flag nothing written (advice printed); with it and no default, `[user] default=` appended and read back by the daemon's `TargetUserDiscovery.DefaultUser`; an existing default never rewritten; an unknown user or a `[user]` section without `default=` refused before anything; the installer's reader and the daemon's agree on ten wsl.conf shapes | covered (Linux legs) | `InstallDefaultUserFlows.Without_the_flag_wsl_conf_is_never_written…`, `…With_the_flag_and_no_default_user…`, `…With_the_flag_an_existing_default_user_is_never_rewritten`, `…The_flag_for_an_unknown_user…`, `…The_installer_and_the_daemon_read_the_same_default_user_from_every_wsl_conf_shape` |
| `install.sh` preflight refusals: not root (the `sudo sh -s --` line, sudo never called), no systemd, an unknown architecture, a foreign `/usr/local/bin/wsl-care`, a hostile archive member (`..`, outside the folder, a link — each riding a complete release); arm64 installs the `linux-arm64` asset; an upgrade restarts the follower | covered (Linux legs) | `InstallFlows.A_non_root_run_is_refused…`, `…Without_systemd_running…`, `…On_arm64…`, `…A_wsl_care_on_the_link_path…`, `…An_archive_member_that_leaves_its_folder_or_is_a_link…`, `InstallUpgradeFlows.An_upgrade_restarts_the_running_follower…` |
| the shipped units and machine layer: `ExecStart` argv parsed by the CLI (`collect --timer`, `events follow`), `SuccessExitStatus` = `ExitCode.Busy`, the timer's calendar, no breaking sandbox directive, `install.sh`'s unit list = the folder, every key of every unit and drop-in in the section systemd reads it from (the act template's `CollectMode` in `[Unit]`), the machine layer valid and empty | covered (every OS) | `ShippedFilesTests`; systemd's own parser: CI `verify-systemd-units.sh` (Linux legs) |
| the CI unit gate (`verify-systemd-units.sh`): every file of the folder read by `systemd-analyze verify`, a template through an instance, each unit with its rendered drop-in; any output fails (an unknown key, a key in the wrong section, a drop-in's key), a failed drop-in render and an empty folder fail; the workflow runs it over the folder with the built binary and names no unit | covered (Linux legs with `systemd-analyze`; the structural check on every OS) | `SystemdUnitVerifyFlows` |
| POST_DEPLOY items 1, 5, 7, 11 as `post-deploy-check --target` runs them: each passes on the healthy installation and FAILS on the broken state it names (a unit not active, `"healthy": false`, `CollectMode=inactive`, a `collect` without `--timer`, an empty journal, an OOM kill, a logged snap refusal; a journal line with a quote or `$(…)` read as data) | covered (Linux legs; stand-in `wsl.exe` that re-parses a `--` line as the real one does, `systemctl` / `journalctl` / `wsl-care`; the extraction is the checker's own `inspect`) | `PostDeployCommandFlows` |
| `install.sh`'s pinned identity is this repository's attesting `release.yml` AT the release tag, and no command line of it uses `--signer-workflow` | covered (every OS) | `InstallAttestationFlows.The_identity_the_installer_pins_is_this_repositorys_attesting_release_workflow_at_the_release_tag`; `ReleaseWorkflowTests.The_release_scripts_agree_with_the_installer_on_what_a_version_is` (the identity's tag = the trigger) |
| the release archive (`package-daemon.sh`, as `release.yml` runs it): per Linux RID exactly the members `install.sh`'s unpack loop requires plus their folders, regular files and folders only, owner 0:0, 0755 binary / 0644 units and machine layer byte for byte, the `.sha256` line `<hash>  <name>`; the Windows zip holds `wsl-care.exe` alone; a bad version / unknown RID / missing binary refused, nothing written | covered (Linux legs; the zip where 7-Zip is on `PATH`) | `PackageFlows.A_linux_archive_holds_exactly_what_install_sh_unpacks_as_regular_files_under_one_folder` (linux-x64, linux-arm64), `…The_windows_archive_holds_the_exe_alone_under_its_folder`, `…A_bad_version_an_unknown_rid_or_a_missing_binary_is_refused_and_nothing_is_written` |
| `install.sh` installs the archive the release script packed (the packer and the installer agree, end to end) | covered (Linux legs) | `PackageFlows.The_installer_installs_the_archive_the_release_script_packed` |
| the release guard (`release-guard.sh`): `daemon-v<version>` with `version.txt` agreeing is admitted and the version reaches `GITHUB_OUTPUT`; another component's tag, a malformed version, a disagreeing `version.txt`, a commit off `main` are refused (exit 1, `::error::`) | covered (Linux legs) | `ReleaseScriptFlows.The_guard_admits_…`, `…The_guard_refuses_a_tag_it_cannot_release` (5 cases), `…The_guard_refuses_a_tagged_commit_that_is_not_on_main` |
| the completeness check (`verify-release-assets.sh`): every RID's archive and a matching `.sha256` passes; a missing archive, a missing `.sha256`, a tampered archive, a `.sha256` naming another file, an extra asset, a whole RID missing are each refused, every problem named | covered (Linux legs) | `ReleaseScriptFlows.A_complete_set_passes_the_completeness_check`, `…An_incomplete_or_wrong_set_is_refused_naming_every_problem` |
| the release pipeline's structure: tag-push-only trigger, per-job permissions across every workflow (signing = the build job, `contents: write` = the publish job), `publish` needs guard + every leg, RIDs = the asset contract = the pull-request legs on the same runners, the shared smoke, stage and publish order, SHA pins, ceilings, no credential left in a checkout, no expression in a `run:`; release-please's tag = the trigger = the tag ruleset, manifest = `version.txt`, first version 0.1.0; required checks = the gating jobs | covered (every OS) | `ReleaseWorkflowTests` (18), `ReleaseConfigTests` (8) — since the E4 review also: every workflow declares its permissions, the gating workflows derived, every pull-request leg packs and opens the path outside bash, no inert `exclude-paths`, the pre-1.0 bump rules |
| the published binary's smoke (`smoke-daemon.sh`: help/version, config round trip, `status` — since E6.S0 with its running block `none`, `capabilities` and `actions`, and `runs show` of a stranger answering `unknown` — a recorded full run read back with `doctor`, `act --preview` of every action, `preview --all --json` with no docker) | covered by CI, not by the suite | `ci · daemon` runs it on every pull request on all three RIDs; `release.yml` runs the same file on every leg; run by hand on 2026-10-03 against the JIT builds (Linux in WSL, Windows in Git Bash), red with a planted `version.txt`, and part 6 red in WSL with the `PATH` emptying removed (the live Docker answered: `rows shown as available … ['A4', 'A5', 'A5Testcontainers', 'A6', 'A6Unused', 'A7']`); E6.S0 (2026-10-04, JIT builds, Linux in WSL and Windows in Git Bash): passed all six parts, and red in WSL with runs show answering a misspelt state — ::error::runs show of a run nothing names is not unknown (plan §15j M3) |
| the release archive packed on EVERY pull-request leg from the published AOT binary, its pair checked, its path opened outside bash | covered by CI | `ci · daemon`, steps *Package the archive* and *The archive path opens outside bash*; their presence on every leg and their order: `ReleaseWorkflowTests.Every_pull_request_leg_packs_the_archive_as_the_release_does_and_opens_its_path_outside_bash` |
| the release run itself — the App token's tag starting `release.yml`, the attestation, the upload to the draft, the publish | not covered | needs the owner's settings (`docs/repo-settings.md`) and the cut of `daemon-v0.1.0`; verified after it by `POST_DEPLOY.md` items 8–10 |
| the extension's release run — `release-extension.yml` from the guard to github-public, the Marketplace publish | not covered | needs the E5 live gate (`docs/repo-settings.md` steps 9–11 and *Cutting `extension-v0.1.0`*); verified after it by `POST_DEPLOY.md` items 3, 6 and 12 |
| `install.sh` against a real release, a real `gh attestation verify`, real systemd and apt | not covered | the first live install on the owner's machine is the E4 live gate (plan §16), after the owner cuts `daemon-v0.1.0` (`docs/repo-settings.md`) |
| the extension: status bar, panel, buttons, logs page, settings sync, help | not covered | the extension is not built yet (E5–E8) |

## What it does not prove

The extension's own limits are listed in its section (§ *The extension* — *What the extension's tests do not prove*).

- **A scenario run is waited on its fakes, not on a clock** (since 2026-10-05, `ProgressWait`). A CLI that keeps calling
  fakes is never cut off before 5 minutes, so a scenario no longer catches a pass that is merely SLOW — its duration is
  nobody's assertion, and the product's own per-command ceilings are proved in their own tests. A CLI that does long
  work without calling any fake (a folder walk, a procfs read) gets 30 s of silence, the same as the old total.
- **The goldens are the fakes' answers, not this machine's.** `contracts/golden/head/` is the built CLI over the
  captured fixtures and the fake tools: doctor's unit checks answer `unknown` (no `systemctl show` of those units was
  captured, so the fake refuses), the clock offset and `df /` are normalised away, and a value the normalisation list
  rewrites is a fixed value of the right type, not a reading. They prove the SHAPE the extension will parse and that it
  is current; the live check of the real extension → `wsl.exe` → daemon path is the E5 live gate's (plan §15g m6).
- **A carried verdict is the full run's judgement, not a new one.** `status` re-judges only the eight thresholds its own
  sample decides; a setting changed after the newest full run reaches the carried ones at the next full run (their
  `limit` says what was applied). Tested as such; the suite cannot call it wrong.
- **The read contract's reader precedes its writer** (E6.S0). The `queued` state and `runs show` of a queued run read a
  request-file format nothing writes yet — E6.S1's `--detach` will; the scenarios stage the file by hand, so the format is
  proved against the reader only until E6.S1's flows write it. The `unknown` running state (a pid the OS refuses to
  inspect) is in-process only: an unprivileged scenario cannot make one on cue. The `running` state of `runs show` over
  the built binary waits for E6.S1 too (a live act is staged in-process). SIGHUP is the signal a terminal sends; whether
  the `wsl.exe` relay delivers SIGHUP (rather than SIGKILL) when it is killed is observed at the E6 daemon live gate, not
  here — which is why the panel's confirm runs DETACHED (E6.S1) and SIGHUP is defence in depth.
- **The instant range trusts its client for the zone.** The daemon reads the two instants exactly as sent; whether they
  are the viewer's local midnights is the extension's (E6.S4) to get right and to test.
- **The installer is proved over a prefix and fakes, never against this machine.** systemd, apt, gh, curl and the
  release are fakes; "root" is a fake `id`. What the real tools DO with the argv the script sends — `systemctl enable
  --now`, apt's install, sysstat's postinst honouring the debconf switch, curl's `--proto =https` — is theirs, and is
  first observed at the live install (the E4 live gate, plan §16). The flows assert the argv sent and the files written, and say
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
- **The POST_DEPLOY flows prove the COMMANDS, not the installation.** `PostDeployCommandFlows` runs each item against
  stand-ins answering as systemd 255 and `doctor` were observed to answer on 2026-10-06; whether the owner's installation
  is healthy is what running the file with `--target` says. The command each flow runs is extracted by the conventions
  checker itself (`node` importing `post-deploy-check.mjs`'s exported `inspect`, from the submodule `ci · daemon` now
  fetches), so a change to the checker's row or span rules is what these flows run; without Node or the submodule they
  FAIL in CI and skip, saying which, on a developer machine.
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
Linux legs, and `verify-systemd-units.sh` there — `systemd-analyze verify` of every unit file, templates as instances,
with the job's own drop-ins); `ci · workflows` runs actionlint and shellcheck of
`install.sh` and `.github/scripts/`; `release.yml` runs the same three executables and the same smoke on every leg of a
`daemon-v*` tag before anything is packed; `ci · family checks` runs the shared plan, pin, adapter
and build-flags checks. `ci · extension` (unconditional, `windows-latest` and `ubuntu-24.04`, E5.S1) runs a clean `tsc`,
the linter and `npm test` — the extension's unit, structural, bundle and client-scenario tests. The live smoke on the owner's machine runs at every release (E4 onwards) — `install.sh` for real
and `POST_DEPLOY.md` against the installation — and so does the live contract with `WSL_CARE_REQUIRE_LIVE=1`
(§ *The live contract*).
