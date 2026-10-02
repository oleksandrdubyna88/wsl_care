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
| **Scenario** | `src_daemon/tests/WslCare.Scenarios` | the BUILT `wsl-care` driven the way a user and the extension drive it, over a temporary home, with fake `docker` / `systemctl` / `journalctl` alone on its `PATH`; the derived verb register |
| AOT smoke | `.github/workflows/ci-daemon.yml` | the Native AOT binary of each RID answers `--help` / `--version` and performs the configuration round trip |

Shared doubles live in `src_daemon/tests/WslCare.TestSupport` (`TempRoot`, `SandboxHost`,
`RecordingCommandRunner`, `FixedTimeProvider`, `DirectoryLinks`, and `ChildProcess` — the one launcher
the process-level tests share: argv list, 30 s ceiling, the whole tree killed on timeout, UTF-8 streams).
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
| `fakebin/` | the CLI's **whole** `PATH`: the fake tool's apphost copied as `docker`, `systemctl`, `journalctl` (`.exe` on Windows) beside its dll. Nothing else is on `PATH`, so a verb can reach only what the scenario fakes; an unfaked tool fails to start rather than reaching the real one (the ubuntu runner has a real `docker`) |
| `fake-calls.jsonl` | the argv log: every fake invocation appends `{"tool", "argv"}` under an exclusive open |
| `fake-script.json` | the scripted answers: for a tool and an EXACT argv, the fixture whose bytes go to stdout, a stderr text and an exit code; an unscripted call exits 98 and says so, and a fake started outside a scenario exits 97 |

**Why a C# fake and not scripts.** One fake must work on both families. On Windows a `docker.cmd` is
not found when a program starts a bare `docker` without a shell (.NET's `Process.Start` and
`CreateProcess` resolve `.exe` only), so script fakes would be a second, different fake per OS. A
renamed apphost is a real `docker.exe` / `docker` on each, and it is the product's language (scenario
rule point 2). The protocol — variable names, line format, exit codes — is one file,
`WslCare.FakeTool/FakeToolProtocol.cs`, which the harness references instead of retyping.

**Fixtures** (`WslCare.Scenarios/fixtures/`, copied beside the harness). Today: one **synthetic** file,
`synthetic/harness-self-test.txt`, labelled as such in its first line, used only to prove the fake
answers from fixtures byte for byte. No real `docker` / `systemctl` / `journalctl` output is invented
here: E2's collectors record real captures from this machine, and E2's live contract check (plan §15a
C2) compares them against the real tools.

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

1. runs its `Example` argv against the built CLI in a fresh home and expects exit 0 or the usage code 2,
   never 70 (internal) or a crash;
2. requires a row in **§ Flow catalogue** below whose FIRST cell starts with `` `wsl-care <usage>` ``,
   the usage exactly as the register spells it. Prose and other tables do not count.

Two companions keep (2) from passing vacuously: a planted verb `planted-verb-without-a-row <thing>` must
be reported missing from this real file, and a synthetic file mentioning verbs only in prose, in another
table, and after the catalogue section must report them missing. **Observed red first** (2026-10-02,
against a stub check that never found anything missing): the planted companion failed with *Expected
collection to be equal to {"planted-verb-without-a-row <thing>"}, but found empty collection*, the
synthetic one likewise — and the main check passed **green** against that stub, which is exactly why the
companions exist.

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
| The BUILT `wsl-care` as a child process (through `TestSupport/ChildProcess`: 30 s ceiling, tree kill): `--help` exits 0 and lists the config verbs; an unknown verb exits 2 with one stderr line; `config set` / `config get --json` / a refused `set` read and write under `WSL_CARE_ROOT` and each run leaves its own log file there; `--help`, `--version` and a refusal write nothing under the root | `WslCare.Cli.Tests/BuiltBinaryTests.cs` |
| The harness's own fakes: a bare `docker` looked up by a real shell on the scenario `PATH` reaches the fake, which records its argv and prints the scripted fixture byte for byte with the scripted exit code and stderr; each of `docker`, `systemctl`, `journalctl` answers as itself, records argv exactly (spaces included) and refuses an unscripted call (98); `git` is NOT reachable on the scenario `PATH`; a fake started outside a scenario refuses (97) | `WslCare.Scenarios/FakeToolFlows.cs` |
| The flows of § Flow catalogue, against the built CLI | `WslCare.Scenarios/HelpAndVersionFlows.cs`, `WslCare.Scenarios/ConfigFlows.cs` |
| The derived register: every verb of `CommandLine.Commands` runs its example (exit 0 or 2) and has a flow-catalogue row; a planted verb is reported missing; prose, other tables and rows after the section do not count | `WslCare.Scenarios/VerbRegisterTests.cs` |

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
| no `config` verb starts any tool: every config verb's example, a refused set, a broken layer and its repair leave the fakes' argv log empty | covered | `ConfigFlows.No_config_verb_starts_any_tool`; the log is proved alive by `FakeToolFlows` |
| every registered verb's `Example` runs against the built CLI: exit 0 or 2, never 70 | covered | `VerbRegisterTests.Every_registered_verb_runs_its_example_against_the_built_cli_without_crashing` (one case per verb, derived) |
| `wsl-care status --json` / `collect` / `preview --all --json` / `doctor --json` / `events follow` | not covered | not built yet (E2) |
| `wsl-care act <A#> [--preview] --json`: a cleanup previewed, then run | not covered | not built yet (E3) |
| `wsl-care logs --period …` / `runs show` / `runs log` | not covered | not built yet (E3) |
| `wsl-care agents list` / `agents probe <path>` | not covered | not built yet (E7) |
| `wsl-care archive preview / run / restore / list` | not covered | not built yet (E9) |
| the extension: status bar, panel, buttons, logs page, settings sync, help | not covered | the extension is not built yet (E5–E8) |

## What it does not prove

- **The fakes prove invocation, not the real tools' output.** No `docker`, `systemctl` or `journalctl`
  output is captured here yet; E2 records real captures from this machine and adds the live contract
  check against the real tools (plan §15a C2) — skipped in CI with its reason, required at release.
- **No E1 verb shells out**, so the fakes are exercised today only by the harness's own self-test
  (`FakeToolFlows`) and by the break-it below. The first verbs that start a tool arrive in E2.
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
- `ShutdownSignals` is thin wiring over `PosixSignalRegistration` and is not tested in isolation; the
  token's effect on a run is (`ProgramTests`, `ProcessCommandRunnerTests`).
- The `AllowAllCommandPolicy` is a placeholder; the never-list and its property test are E3.S1.
- Nothing here runs on a real WSL VM under memory pressure; the live smoke on the owner's machine is
  the only place that happens, at release time.

## When it runs

On every push to `main` and every pull request: `ci · daemon` (unconditional, no path filter) runs the
three test executables and both AOT smoke steps on `ubuntu-latest`, `ubuntu-24.04-arm` and
`windows-latest`; `ci · family checks` runs the shared plan, pin, adapter and build-flags checks. The
live smoke on the owner's machine runs at every release (E4 onwards).
