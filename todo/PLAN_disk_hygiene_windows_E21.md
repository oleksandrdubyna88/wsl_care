# PLAN — E21 "Test leftovers and A5": A5's compose exemption, this repo's test prefix, the `testsToDelete-` sweep (Docker and `%TEMP%`)

> Status: **in progress, 2026-10-23 — E21.S1 (DH-2, A5 never selects a compose project's container) merged as #160 (`1dfeb6e8`); DH-8, DH-6, DH-7 and DH-9 not started.** Scope: the stories of epic E21 of
> [PLAN_disk_hygiene_windows.md](PLAN_disk_hygiene_windows.md) (DH-2, DH-8, DH-6, DH-7, DH-9). The owner's yes for E20 and
> E21 came on 2026-10-15. Written by the Opus judge (epic → stories); the cornerstone **DH-6** (`model: fable`, the parent's
> ledger `Cornerstones: 2 of 2 — DH-4, DH-6`) has its scenarios and brief written by Fable (§4 DH-6).
>
> Parent: [PLAN_disk_hygiene_windows.md](PLAN_disk_hygiene_windows.md) § *Epics* — E21. Sibling:
> [PLAN_disk_hygiene_windows_E19.md](PLAN_disk_hygiene_windows_E19.md). Runner plan: [PLAN_windows_runner.md](PLAN_windows_runner.md)
> (WR-T1 — the `%TEMP%` engine DH-7 builds on — implemented on `feat/wc-wr-t1-windows-temp`, in review 2026-10-15; DH-7 consumes its privilege rule and A23's walk, never copies them — see E20 §4b).

## 1. What this epic must make true

The parent's E21 *done when*: A5 never selects a compose container (`auto.A5` still off); this repository's tests name what
they leave `testsToDelete-…`; the sweep removes exactly the anchored, case-insensitive prefix, never anything younger than an
hour, never while a test run is in flight on either side, with every signal shown in the preview; E21 is done only when the
`%TEMP%` half (DH-7) works.

## 2. Facts (verified 2026-10-15 on main `e3c6e364`, five Sonnet scouts; files opened)

| Fact | Where |
|---|---|
| A5 selects stopped non-Testcontainers containers kept only by `wsl-care.keep` — no compose exemption; the engine re-previews LIVE before every run, so a change in `StoppedContainers` covers row and run alike | `Preview/CleanupTargets.cs:24-31,77-97`, `Actions/Engine/ActionEngine.cs:311`, `Actions/DockerCleanups/ContainerRemoval.cs:38-81` |
| The inspect template is one hand-built JSON line per container; `ContainerDetail.From` reads it by `DockerText.Field` (absent → empty); the policy template follows the constant | `Docker/DockerCommands.cs:41-47,103-111`, `Docker/ContainerDetail.cs:25-67`, `Processes/Policy/ReadCommandTemplates.cs:24` |
| A5's preview row carries notes `RowNote(what, count, bytes)`; the extension does not render notes, only counts and bytes | `Preview/CleanupPreview.cs:87-92,174-209`, `Preview/PreviewReport.cs:51,124`, `src_vs_code/src/cleanup/cleanupView.ts:169` |
| `DockerWorld` edits the captured inspect TEXT per container; `CleanupPreviewTests` edits parsed details with `with` | `tests/WslCare.Core.Tests/Actions/DockerWorld.cs:30-55`, `Docker/CleanupPreviewTests.cs:143-154` |
| The fixture's redaction drops compose labels (they carry project names) | `research/diagnostics/docker_fixture_redact.mjs:33`, `tests/fixtures/docker/ubuntu-2026-10-02/SOURCE.txt` |
| `TempRoot` names `wsl-care-test-{purpose}-{guid}` and sweeps its own > 24 h by `^wsl-care-test-.+-[0-9a-f]{32}$`; `WindowsIdleTests` uses `wsl-care-idle-`; the extension's tests use eight `mkdtemp` prefixes; one stray `GetTempFileName()` in `ProcessCommandRunnerTests` | `tests/WslCare.TestSupport/TempRoot.cs:15,26,35-51`, `Core.Tests/Archive/WindowsIdleTests.cs:24`, `Core.Tests/Processes/ProcessCommandRunnerTests.cs:29,120`, `src_vs_code/scripts/run-host.mjs:49`, `src_vs_code/src/test/{fakeWsl,runner,gpu,vsixCheck,windowsTimeFix,windowsTimeGuardTask}.test.ts`, `test/support/fakeWorld.ts:43` |
| No test of this repo creates Docker objects, git worktrees or branches | scout, 2026-10-15 |
| **M4, measured 2026-10-15 (read-only):** 20 compose-labelled containers in 9 projects, **12 exited** — A5 would take them after 7 days, and `docker rm -v` their ANONYMOUS volumes (one exited sample mounts a 64-hex anonymous volume beside a named `…-neo4j-data`). The product template plus the compose field answers on real containers: absent labels are JSON `null`, `compose` is the plain project name, times RFC 3339 with `Z`. No Testcontainers or Ryuk container exists on this machine now. Six RUNNING `testsToDelete-` containers (redis ×4, mysql ×2), no such volumes or networks. `docker network ls --format '{{json .}}'` gives `CreatedAt` as `yyyy-MM-dd HH:mm:ss.fffffffff +0000 UTC` and `Labels` as one comma-joined string; `docker volume inspect` gives `CreatedAt` RFC 3339 and `Labels` as an object | scout `m4`, raw outputs kept outside the repo |
| (DH-6 facts: in its Fable spec, §4) | |

## 3. Stories

Each story: one branch, one PR; Sonnet codes (RED first), Opus reviews — for DH-6 and DH-7, which delete, a Fable security
judgement as well (the coordinator's rule, 2026-10-15); a coai code round per story; full test executables and `npm test`;
manual squash-merge on all-green.

### E21.S1 = DH-2 — A5 keeps every compose container

- **Brief.** `DockerCommands.InspectTemplate` gains `"compose":{{json (index .Config.Labels "com.docker.compose.project")}},`
  between `keep` and `mounts`; `ContainerDetail` gains `string ComposeProject` (empty when absent — `DockerText.Field`
  gives "" for a missing key; the fixtures' lines keep parsing); `DockerLabels.ComposeProject = "com.docker.compose.project"`.
  `ContainerTargets` gains `ComposeKept`; `StoppedContainers`, ONLY when `testcontainers == false`, splits stopped containers
  with a non-empty project off BEFORE the keep/age split (a compose container that is also `keep=true` counts once, as
  compose — the stronger reason); they are never in `Younger` or `Selected`. `ContainerFigures` adds the note with the FIXED text "containers of a
  docker compose project (kept — compose owns them)" (tests and the golden key on `What`); the projects' names go in a NEW
  optional `RowNote.Names` field (≤ 5 names + `more: N`), shown by the CLI preview; the extension renders no notes today
  (`cleanupView.ts:169`), so the panel shows only the counts — said in README. A5Testcontainers is unchanged.
- **M4 (measured 2026-10-15, §2):** the product template plus the compose field answered on two real stopped compose
  containers; no Ryuk exists locally (DH-6's concern, not this story's). The fixture gets one compose line derived from that
  capture (project mapped to `project-1`); the redaction script's allow-list keeps `com.docker.compose.project` with its
  value mapped to `project-<n>`. `DockerWorld` gains a `compose:` parameter that inserts `"compose":"project-1",` before
  `"mounts":[` in the chosen container's line. Recorded in `research/2026-10-15_disk_hygiene_measurements.md` — ONE file for
  every measurement of this plan, created by whichever story lands first (E19.S3 is briefed to create it).
- **Scenarios (RED first).** `A5_keeps_a_stopped_container_of_a_compose_project_and_names_the_project`;
  `A5Testcontainers_still_takes_a_labelled_compose_module_container`; `A_container_without_the_label_is_selected_as_before`;
  `The_inspect_template_reads_the_compose_label` (beside `DockerCollectorTests.cs:58-62`);
  `A_compose_container_with_the_keep_label_counts_once_as_compose`; `A_fixture_line_without_the_compose_key_still_parses`;
  `The_A5_run_never_removes_a_compose_container` (the engine's live re-preview, DockerWorld).
- **Break-it:** the compose split removed; the split applied to A5Testcontainers; the template field dropped.
- **Files:** `Docker/DockerCommands.cs`, `Docker/ContainerDetail.cs`, `Docker/DockerInventory.cs`, `Preview/CleanupTargets.cs`,
  `Preview/CleanupPreview.cs`, `tests/WslCare.Core.Tests/Actions/DockerWorld.cs`, `DockerCleanupTests.cs`,
  `Docker/CleanupPreviewTests.cs`, `Docker/DockerCollectorTests.cs`, `research/diagnostics/docker_fixture_redact.mjs`,
  fixtures (M4), `contracts/golden/head/preview.json` (regenerated on the Linux leg, the WSL recipe), README (A5's row),
  `research/module_daemon.md`, `research/module_tests.md`.
- **As built (2026-10-22, PR #160).** As briefed, with these deviations and additions:
  - **One fixture line, not two.** M4 measured two real stopped compose containers, but ONE recaptured line (project mapped
    to `project-1`) is all the fixture needs; the second capture added nothing a test reads. The capture is recorded in
    `research/2026-10-15_disk_hygiene_measurements.md`.
  - **Only the project name is read.** The template reads `com.docker.compose.project` and no other compose label: the
    config-file and working-directory labels carry host paths.
  - **The names are shown printable** (coai code round): a project name is a label anyone can set, so each listed name
    passes through `ShownText.Printable` and a newline or an escape can never forge a preview row or drive the terminal.
  - **The golden moved as expected:** the fixture's compose container and the named volume it holds left A5's
    figures (count 13 → 12, named volumes 6 → 5) and appear in the new note.

### E21.S2 = DH-8 — this repository's test leftovers take the prefix

- **Brief.** `TempRoot` names `testsToDelete-wsl-care-{purpose}-{guid32}`; its leftover regex takes BOTH shapes, exact case
  (`^(testsToDelete-wsl-care|wsl-care-test)-.+-[0-9a-f]{32}$` — TempRoot only sweeps folders it made, so no case folding),
  with a dated comment: the old half goes after 2026-11-15; the sweep also removes a `<root>.owner` marker whose folder is gone
  (A26 removes folders only). `WindowsIdleTests` → a `TempRoot`. `ProcessCommandRunnerTests`' `GetTempFileName()` child
  writes into the test's own `TempRoot` instead. E19.S4's `TestSupport/ThrowAwayGit.cs` (a `GetTempPath()` + `CreateDirectory`
  sandbox) moves onto `TempRoot`. Every hard-coded `wsl-care-test-` in tests (`TempRootTests`,
  `DockerHygieneTests.cs:50`) follows. The extension: one helper `test/support/tempDir.ts` making
  `testsToDelete-wsl-care-<purpose>-` mkdtemp folders, used by the nine call sites (eight prefixes) and `scripts/run-host.mjs`. A23's
  `temp.allowGlobs` default keeps `wsl-care-test-*` for the backlog (the parent's §6).
- **Scenarios.** `TempRoot_names_its_folder_with_the_prefix`; `The_transition_sweep_takes_both_shapes`;
  `No_test_names_a_temp_folder_without_the_prefix` (an architecture test scanning `src_daemon/tests` — excluding bin/obj and
  its own source, with a planted instance proving it can fail, like the scanners beside `ArchitectureTests.cs:19` — for
  `CreateTempSubdirectory(`, `GetTempFileName(`, `GetTempPath(` and reads of `TEMP`/`TMP`/`TMPDIR` outside `TempRoot.cs`) and its
  extension twin (`structure.test.ts` style: no `mkdtemp(` or `tmpdir(` outside `test/support/tempDir.ts`; `scripts/run-host.mjs`
  imports `out/test/support/tempDir.js` and is scanned explicitly).
- **Files:** `tests/WslCare.TestSupport/TempRoot.cs`, the call sites, `src_vs_code/src/test/support/tempDir.ts` (new), the eight
  tests, `scripts/run-host.mjs`, `research/module_tests.md`, `research/2026-10-11_windows_temp.md` (a dated note).

### E21.S3 = DH-6 — the A25 Docker sweep and `TestRuns.Judge` — **cornerstone, `model: fable`**

The scenarios and the brief are Fable's: §4 below.

### E21.S4 = DH-7 — the A26 `%TEMP%` sweep and the both-sides panel flow — waits for WR-T1

- **Depends on:** WR-T1 (A23, the privilege rule) AND E20's DH-4 (the handle-based `ITreeRemoval` it extracts from WR-T1's
  walk — a prerequisite, §4b item 6; the parent's §6: never two copies); E19.S2b (the Windows status
  read — the panel's both-sides conjunction reads the Windows preview); DH-6 (the shared judge).
- **Brief (when unblocked).** A26: top-level `testsToDelete-*` folders of the user's `%TEMP%` (shell-folder API), newest
  descendant write older than `testLeftovers.minAgeMinutes`, through the shared walk (`%TEMP%\claude\` guard first, owner
  SID, reparse refusal, re-validated delete); A23's allow-list never takes the prefix (a validation rule on
  `temp.allowGlobs` and a preview group). The Windows side of `TestRuns.Judge` reads the Windows process table with the
  managed command-line tokenizer DH-6 builds. The panel's *Sweep test leftovers…* previews BOTH sides and runs either only
  when both read `inFlight: none`; the run is the person's own `\wsl-care\test-leftovers` task (E20's DH-5 route).
- **Fable security judgement** before code (it deletes); scenarios per the parent's §11 DH-7 list.

### E21.S5 = DH-9 — the closing docs and the promotion

- Lands with the last story; the parent's §13 promotion conditions; the open tail (whole-worktree removal) named.

## 4. DH-6 — the cornerstone spec (Fable, 2026-10-15)

Written by Fable (model `fable`) on 2026-10-15 as the cornerstone spec; inserted verbatim (headings demoted one level). Where it and §3 disagree, this section wins for DH-6.

#### E21.S? — DH-6: the A25 Docker sweep of `testsToDelete-` leftovers and `TestRuns.Judge` (CORNERSTONE, `model: fable`)

> Cornerstone spec written by Fable 2026-10-15 over checkout `D:\rsd\_wt\wc-e19` (main `e3c6e364`, HEAD `6c517626`),
> every cited file opened; the measured Docker shapes are a read-only scout's of the same day (no Ryuk or Testcontainers
> container on this machine today — the live-session rule is pinned to Testcontainers' documented label and image, below;
> six RUNNING `testsToDelete-` redis/mysql containers exist, no such volume or network). Sonnet codes, Opus reviews,
> Fable's own review of §5 stays. The story number is the judge's.
>
> **Proves:** E21 done-when (3). **Depends on:** DH-2 (ordering only: both PRs touch `Docker/DockerCommands.cs`; DH-6 adds
> NO field to `InspectTemplate` — every label it reads comes from `docker system df -v`'s `Labels` column, which
> `ContainerRow.Labels` already parses, `Docker/DockerInventory.cs:51-69`). **Does not depend on** any runner story. DH-7
> (A26, the `%TEMP%` half, the both-sides panel flow) is NOT here: this story leaves the judge consumable from the Windows
> table (§1.6) and its preview says the Windows runners are not read from this side (§5).

##### 0. The one sentence

A25 stops and removes every Docker container, volume and network whose name starts with `testsToDelete-` in any case and
that is older than an hour — containers, then volumes, then networks, never `-f` — and does so only while
`TestRuns.Judge`, asked in the preview and again before every batch, reads no test run in flight and nothing it could
not read. A wrong judge sweeps under a running test on both sides and nothing downstream re-checks it; that is why this
is the cornerstone.

##### 0a. The six pre-review points, settled (Opus pre-review of the E21 draft, relayed 2026-10-15)

1. **Stopping a running leftover before DH-7.** Until the Windows side is read, a running `testsToDelete-` container older
   than 24 h COULD be another repository's live end-to-end run on Docker Desktop that the distro cannot see. Settled:
   **A25 lists running leftovers and stops NONE by default** — a new switch `testLeftovers.stopRunning` (`BoolKey`, default
   `false`, trust `KeyTrust.Off`: the user layer can only turn it off; the machine layer may turn it on where the owner knows
   no Windows test drives this Docker) gates the stop; the 24 h rule (`stopRunningOlderThanHours`) is the SECOND gate and
   stays. DH-7 flips the default to `true` in the same PR that adds the Windows runner signal, and says so. The preview lists
   every running leftover it will not stop with the reason (§5.1); the stopped containers, the volumes and the networks —
   the disk — are swept now. Reason: the sweep has no evidence either way about a Windows run, and "list, don't stop" costs
   only the running leftovers' memory for the weeks until DH-7; a stop under a live run costs a corrupted test.
2. **The Ryuk signature is UNMEASURED.** No Ryuk ran on this machine on 2026-10-15; M4 (the parent's measurements) has not
   captured one. Pinned, in this order, to what Testcontainers' sources name: the IMAGE — the repository path `testcontainers/ryuk`
   with any registry prefix and any tag (`Image` equals `testcontainers/ryuk[:tag]` or ends with `/testcontainers/ryuk[:tag]`,
   ordinal) — then the LABEL `org.testcontainers.ryuk=true`. The code comment and README say "unmeasured, from the
   Testcontainers sources (file and line recorded in the PR)"; the coder reads the current `testcontainers-dotnet`
   `ResourceReaper` and `testcontainers-java` `RyukResourceReaper` sources read-only and records the lines; no capture is
   invented. The BACKSTOP that does not depend on the signature: a Ryuk container itself carries `org.testcontainers=true`
   and is created at session start, so every Testcontainers session younger than the floor is caught by the young-labelled
   rule whatever the Ryuk signature; only a session running longer than the floor relies on it. M4 stays owed.
3. **DH-6 reads names, labels, image, state and created from the container LIST rows** (`ContainerRow`, `DockerInventory.cs:51-69`:
   `system df -v` for the preview, `ps -a` for the re-judgement) and NEVER from the inspect template; it does **not** depend on
   DH-2's M4 fixture or on DH-2's compose label. DH-2 and DH-6 share `Docker/DockerCommands.cs` and `DockerInventory.cs` as
   FILES only; the parent's "after DH-2" is a merge-order note, not a build dependency.
4. **The `docker stop` template**: `docker stop --time <1..120> <64-hex>`, ONE container per command, ceiling key
   `docker.removeTimeoutSeconds` (300 default, 30–300, MachineOnly), with `NumberRules` holding
   `testLeftovers.stopSeconds + 10 ≤ docker.removeTimeoutSeconds` (§3.3).
5. **The cost of re-judging before every batch, bounded.** One re-judgement = one process-table read through
   `ActionContext.Processes` (the `/proc` walk A11 already takes, no command) + `docker ps -a` + `docker network ls` +
   `docker volume ls` + at most ONE `docker volume inspect` (only for matched names the preview did not see), each under
   `docker.listTimeoutSeconds` (30 s): worst case `4 × listTimeoutSeconds + the /proc walk` per batch. A phase with no
   targets reads nothing. Batches = ⌈containers/batchSize⌉ + ⌈volumes/batchSize⌉ + ⌈networks/batchSize⌉; on this machine today
   (six containers, no volume, no network) that is ONE re-judgement after the preview's. A confirm runs detached under the
   run's own limit as every act does; the extension's `worstCases.ts` counts A25's PREVIEW as one Docker snapshot (§5.4).
6. **The volume re-inspect**: one `docker volume inspect` per volume immediately before its batch's `rm`, compared on
   `CreatedAt` AND labels to the preview's fingerprint and re-checked against the age floor at that instant (§2.5, §2.7);
   a gone volume is "already gone", a changed one is kept by name, the residue is one sentence in the preview and README.

Also settled: **`RowNote.What` stays a FIXED string** (tests key on it); names and ages go into the ACTION preview's
`basis` and item notes, never into a `RowNote`.

##### 1. The contract of `TestRuns.Judge` (`Core/TestLeftovers/TestRuns.cs`, new; namespace `WslCare.Core.TestLeftovers`)

PURE, static, compiled into both binaries, in the style of `Thresholds/MachineBusy.cs` (an enum state, records, every
unread input named, `Invariant(...)` for every number). No I/O, no clock read — every "now" is a parameter.

**1.1 Types**

```csharp
/// What one signal read.
public enum SignalState
{
    Clear,        // read, and it says no test run is in flight
    InFlight,     // read, and it names a test run
    Unknown,      // it could NOT be read (a process table, a Docker list) — BLOCKS, exactly as InFlight does
    NotObserved,  // by DESIGN not read on this side (the other side's processes) — never blocks, always listed
}

/// One signal: a stable id, its state, what it read (one line a person reads), and the reasons when it blocks.
public sealed record TestRunSignal(string Id, SignalState State, string Reading, IReadOnlyList<string> Reasons);

public enum InFlight { None, InFlight, Unknown }

/// The answer: the state, every signal with its reading, the reasons of the blocking ones (empty when None).
public sealed record TestRunJudgement(InFlight State, IReadOnlyList<TestRunSignal> Signals)
{
    public bool Blocks => State != InFlight.None;
    /// "in flight: runner.wsl: 12345 dotnet matches \"dotnet test\"; young: testsToDelete-pg-3f9a created 12 min ago"
    /// or "unknown: runner.wsl: the process table could not be read (/proc does not exist: ...)"
    public string Refusal { get; }   // "" when State == None; the word first, then "id: reason" joined by "; "
}

/// A process this side's collector matched: the pid, the program (argv[0]'s base name, .exe stripped) and the pattern text.
public sealed record RunnerMatch(int Pid, string Program, string Pattern);

/// A named thing with a creation instant (a container, a volume, a network — for DH-7 a folder's newest write).
public sealed record AgedName(string Kind, string Name, Reading<DateTimeOffset> At);
```

**1.2 The signal constructors** (each pure; each is one unit test per branch)

| constructor | `Id` | `InFlight` when | `Unknown` when | `Clear` reading |
|---|---|---|---|---|
| `TestRuns.Runner(HostSide side, Reading<IReadOnlyList<RunnerMatch>> matches)` | `runner.wsl` / `runner.windows` by `side` | any match; reasons = one per match: `"{Pid} {Program} matches \"{Pattern}\""` (at most 20 named, then `"… and N more"`) | the reading is unavailable; reason = `"the process table could not be read ({ReasonOrEmpty})"` | `"none ({N} match of testLeftovers.runnerPatterns)"` → `"none"` |
| `TestRuns.OtherSide(HostSide thisSide)` | the OTHER side's `runner.*` id | never | never | state `NotObserved`, reading `"not read from this side: the Windows side's test runners are read by the Windows binary (A26, DH-7); until then the panel runs A25 from the distro alone"` (WSL) / the mirror sentence for Windows |
| `TestRuns.LiveTestcontainers(Reading<IReadOnlyList<ContainerRow>> containers, DateTimeOffset now, TimeSpan floor)` | `testcontainers` | a RUNNING container (`State == "running"`) that is Ryuk — `Image`'s repository path is `testcontainers/ryuk` (equal to, or ending with `/testcontainers/ryuk`, before an optional `:tag`, ordinal — any registry prefix) OR `Labels["org.testcontainers.ryuk"] == "true"` (both UNMEASURED, §0a.2) — reason `"Ryuk {name} is running ({image})"`; or a RUNNING container with `Labels.ContainsKey("org.testcontainers")` whose `CreatedAt` is available and `now - CreatedAt < floor`, reason `"{name} (org.testcontainers) started {age} ago, younger than the {floor} floor"`. **Never** any other running labelled container — a stale running one must not block forever (`A_stale_running_testcontainers_container_does_not_block_forever`). A labelled running container whose `CreatedAt` is unavailable counts as young (unreadable age is the unsafe direction) — reason names it. | the list is unavailable: `"the container list could not be read ({reason})"` | `"none (no running Ryuk, no org.testcontainers container younger than {floor})"` |
| `TestRuns.Young(Reading<IReadOnlyList<AgedName>> matched, DateTimeOffset now, TimeSpan floor)` | `young` | any matched name with `At` available and `now - At < floor`; reason `"{kind} {name} created {age} ago"`. A matched name with `At` UNAVAILABLE is NOT young here — it is reported by the selection as "unaged, never selected" (§2.3); the judge does not block on one resource's missing time. | the list is unavailable: `"the {kind} list could not be read ({reason})"` — the caller passes one `Young` per Docker list it could not read, or ONE combined reading | `"none (no matched resource younger than {floor})"` |

`floor` is `testLeftovers.minAgeMinutes` as a `TimeSpan`; age text is `Invariant($"{age.TotalMinutes:0} min")` under two hours, else `"{age.TotalHours:0.0} h"`, else days `"{age.TotalDays:0.0} d"` — one helper `TestRuns.AgeText(TimeSpan)` used by every reading and every preview note.

**1.3 The judgement** — `TestRuns.Judge(IReadOnlyList<TestRunSignal> signals)`:
`State = any InFlight ? InFlight : any Unknown ? Unknown : None`. `NotObserved` never changes the state. The signals are
kept in the order given. `Refusal` is built as in 1.1. A judgement over an empty signal list is `Unknown` with one synthetic
signal `none` / `"no signal was read"` — a caller that forgot to read anything cannot sweep.

**1.4 `NotObserved` is unreachable from a Reading.** Only `OtherSide` produces it; every other constructor maps
`Reading.Unavailable` to `Unknown`. Pinned by `An_unreadable_signal_is_unknown_never_not_observed` (every constructor over
`Reading.Missing`) — this is the loophole the state would otherwise open.

**1.5 The distro's four for A25** — `TestRuns.ForDockerSweep(DockerSweepInputs inputs)` returns the judgement over, in
this order: `Runner(Wsl, inputs.Runners)`, `OtherSide(Wsl)`, `LiveTestcontainers(inputs.Containers, now, floor)`,
`Young(inputs.Matched, now, floor)`. `DockerSweepInputs(Reading<IReadOnlyList<RunnerMatch>> Runners, Reading<IReadOnlyList<ContainerRow>> Containers, Reading<IReadOnlyList<AgedName>> Matched, DateTimeOffset Now, TimeSpan Floor)`.
`Matched` is every prefixed container, volume and network with its creation instant (§2.2), one combined reading: when
ANY of the three Docker lists is unavailable the caller passes `Reading.Missing` with the first failure's reason (a sweep
over a partial inventory is not a sweep).

**1.6 What DH-7 consumes (shipped here, wired there):** `WindowsRunnerScan.Read(IWindowsProcessTable table, RunnerPatterns patterns)`
→ `Reading<IReadOnlyList<RunnerMatch>>` (§3.3) and `TestRuns.Runner(HostSide.Windows, …)`, `TestRuns.OtherSide(HostSide.Windows)`,
`TestRuns.Young(folders…)`. DH-7 adds nothing to the judge; the panel's conjunction is "both sides' `TestRunJudgement.State == None`".

**1.7 Per-batch re-judgement (the run, §2.6):** the same `ForDockerSweep` over FRESH inputs before EVERY batch; the preview's
judgement is never reused for a removal.

##### 2. The sweep: selection and removal

**2.1 The match** — `LeftoverNames` (`Core/TestLeftovers/LeftoverNames.cs`): `public const string Prefix = "testsToDelete-"`;
`Matches(string name) => name.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase)`; `ContainerNames(ContainerRow row)`
= `row.Name.Split(',')` each `TrimStart('/')` (the CLI prints `Names` without `/`, comma-joined — fixture
`tests/fixtures/docker/ubuntu-2026-10-02/system-df-v.out` `"Names":"container-01"`; the trim is belt-and-braces for an
inspect-shaped name); a container matches when ANY name matches, and is DISPLAYED by its first matching name. Never
`docker … --filter name=`, never `Contains`, never a wider prefix, never the underscore form (`teststodelete_x` does not
match; the rule reserves it for SQL identifiers).

**2.2 Inputs** — one `DockerLook` (the same `DockerCollector` snapshot every Docker action takes, `DockerLook.cs:19`),
widened:
- `DockerSnapshot` gains `Reading<IReadOnlyList<NetworkRow>> Networks` (from the new `DockerCommands.NetworkList`) and
  `Reading<IReadOnlyList<VolumeDetail>> LeftoverVolumes` — `docker volume inspect` of ONLY the inventory's volumes whose
  name matches, in batches of `docker.batchSize`; zero commands when none matches; one failed batch makes the part
  unavailable (the collector's `DetailsAsync` rule, `DockerCollector.cs:50-69`). Both are collected after `Dangling`,
  before `Details`.
- Candidates (`LeftoverCandidate(string Kind, string Id, string Name, Reading<DateTimeOffset> CreatedAt, bool Running, IReadOnlyDictionary<string,string> Labels, Reading<long> Bytes)`):
  containers from `Inventory.Containers` (`Id` 64-hex, `CreatedAt`, `State == "running"`, `Labels`, `SizeBytes`); volumes
  from `Inventory.Volumes` whose name matches, joined by name to `LeftoverVolumes` for `CreatedAt` + labels (a matched
  volume missing from the inspect answer → `CreatedAt` unavailable → unaged); networks from `Networks` (`Id` 64-hex,
  `CreatedAt`, `Labels`, bytes unavailable).

**2.3 Selection** — `LeftoverSelection.Select(IReadOnlyList<LeftoverCandidate> candidates, DateTimeOffset now, LeftoverLimits limits)`
→ `LeftoverTargets` (the record below rule 6), PURE, in `Core/TestLeftovers/LeftoverSelection.cs`;
`LeftoverLimits` (below rule 6) with `From(EffectiveConfig)`. Per
candidate, first rule that applies:
1. `Labels["wsl-care.keep"] == "true"` → `Kept` ("protected by the wsl-care.keep=true label") — containers, volumes AND
   networks alike.
2. `CreatedAt` unavailable → `Unaged` ("no readable creation time: never selected") — never selected, never a signal.
3. `now - CreatedAt < MinAge` → `Younger` (and the `young` signal fires through §1.2 — the same list feeds both).
4. `Running && !StopRunning` → `RunningNotStopped` ("running: listed, not stopped — testLeftovers.stopRunning is off until
   the Windows side is read (DH-7)") — §0a.1.
5. `Running && now - CreatedAt < StopRunningOlderThan` → `RunningYounger` ("running, younger than the stop limit: kept").
6. else `Selected`; a selected running container is removed by stop-then-rm (§2.5).
`LeftoverLimits` is therefore `(TimeSpan MinAge, bool StopRunning, TimeSpan StopRunningOlderThan, int StopSeconds)` and
`LeftoverTargets` is `(Selected, Younger, RunningNotStopped, RunningYounger, Kept, Unaged)`.
Order within `Selected`: containers (by name, ordinal), then volumes, then networks. The age floor is the ONE guarantee a
snapshot gives (§7 of the parent: a test that starts after a judgement is unseen until the next one; its resources are
younger than an hour) — nothing the signals say ever overrides rules 2–3.

**2.4 Preview** — `TestLeftoverSweep.PreviewAsync` (the action, `Actions/TestLeftovers/TestLeftoverSweep.cs`, `ICleanupAction`,
`Id = ActionId.Find("A25")`, `Scope Machine`, `Idle Never`, `Sides [Wsl]`, `Commands = DockerCleanupCommands.LeftoverSweepCommands`):
take the look; build the candidates; judge with `ForDockerSweep` over `context.Processes(ct)` (the engine's fresh process
table — `ActionContext.cs:44`; the default context has none, so a context built without one reads `Unknown` and can never
sweep), the snapshot's `Inventory.Containers`, the matched `AgedName`s (or `Reading.Missing` when any list failed); the row
figures from `CleanupPreviews.A25Row` (§5); `Refusal = judgement.Refusal` (empty when `None`) — the engine turns a non-empty
refusal into status `refused` with that reason and never runs the action (`ActionEngine.cs:394`). A look whose `Inventory`
is unavailable is `ActionPreview.Unavailable(what, reason)` as every Docker action.

**2.5 Run** — `RunAsync(context, preview, commands, ct)`; targets by kind from `preview.Targets` (keys: a container's and a
network's 64-hex id; a volume's `VolumeFingerprint` §2.7). The engine re-previews LIVE before `RunAsync`
(`ActionEngine.cs:311`), so the targets are at most seconds old. Then:

```
removed, notRemoved, failures = []
PHASE containers: foreach batch in containers.Chunk(DockerCleanupCommands.Batch):
    J = await RejudgeAsync(...)                         // §2.6; Unknown or InFlight → STOP (below)
    stopped = []
    foreach target in batch where target.Running:        // ONE docker stop per container, never a batch (§3.3); a running target exists ONLY when testLeftovers.stopRunning is on (§0a.1, selection rule 4)
        outcome = commands.RunAsync(ContainerStop, [stopSeconds, id])
        verdict = DockerCleanupAnswers.Removal([id], stdout, stderr, aliases)[id]
          Removed      → stopped += target            (docker stop prints the id it stopped)
          AlreadyGone  → notRemoved += "already gone: …"      (not passed to rm)
          InUse/Unknown→ notRemoved += "not confirmed: …", failures += …  (not passed to rm)
    rm = batch.Where(!Running) ++ stopped
    DockerRemovals.RemoveAsync(commands, ContainerRemove, rm, aliases)   // rm -v <64-hex>…; a container restarted in between is refused by Docker ("container is running") → InUse → kept
PHASE volumes: foreach batch in volumes.Chunk(Batch):
    J = RejudgeAsync → STOP on block
    eligible = []
    foreach target in batch:
        answer = DockerCli.Classify(VolumeInspectCommand([name]), commands.RunAsync(VolumeInspect, [name]))   // ONE name per inspect, so a gone volume is "already gone", not a failed batch
          Failed, "no such volume" → notRemoved "already gone: …"
          Failed, other            → notRemoved "not confirmed: …", failures += …
          Answered → detail = VolumeDetail.Parse(stdout).Single
             fingerprint(detail) == target.Fingerprint AND now - detail.CreatedAt ≥ MinAge → eligible += target
             else → notRemoved "kept: a different volume under the same name since the preview (created {detail.CreatedAt}, labels {…})"
    DockerRemovals.RemoveAsync(commands, NamedVolumeRemove, eligible, [])   // volume rm <name>…; "volume is in use" → InUse → kept
PHASE networks: foreach batch in networks.Chunk(Batch):
    J = RejudgeAsync → STOP on block
    DockerRemovals.RemoveAsync(commands, NetworkRemove, batch, aliases)      // network rm <64-hex>…; "has active endpoints" → InUse → kept
STOP: every target not yet judged → notRemoved "not attempted: {J.Refusal}"; Failure = $"the sweep ended before batch {n}: {J.Refusal}" — status FAILED, the history row says why (decision §10.2).
```
Cancellation mid-command: exactly `DockerRemovals`' rule (`VolumeRemoval.cs:200-209, 230-240`): what Docker confirmed
before stays `Removed`, the batch in flight is `unknown: cut off mid-command`, the rest `not attempted: the run was
interrupted`, `Interrupted = true`. `FreedBytes` = the `system df -v` sizes, read in the preview's look, of exactly the
containers (writable layer) and volumes Docker printed back; a network frees nothing; `FreedBasis` says so. `Notes` carries
one line per phase: `"containers: 3 stopped, 4 removed, 1 kept by Docker (running); volumes: 2 removed, 1 recreated since the
preview (kept); networks: 1 removed"` and the judgement count: `"in-flight judged 4 times (preview + 3 batches)"`.

**2.6 `RejudgeAsync`** — the same `ForDockerSweep` over FRESH readings through the action's own declared templates:
`context.Processes(ct)` (the process table, no command); `docker ps -a --no-trunc --format {{json .}}`
(`DockerCommands.ContainerList`, `ContainerRow.From` rows — NOT `system df -v`: a re-judgement must cost a listing, not
seconds-to-minutes) for the Testcontainers signal and the young containers; `docker network ls …` for the young networks;
`docker volume ls --format {{.Name}}` (`VolumeList`) and `docker volume inspect` of the matched names NOT in the preview's
volume set (one batch; ≤ `docker.batchSize`, the rest counted unknown) for the young volumes. Any list that fails → the
`Matched` reading is `Missing` → `Unknown` → STOP. `Now` is `context.Clock.GetUtcNow()`.

**2.7 The volume fingerprint** — `VolumeFingerprint.Of(VolumeDetail) → string` = `name + "|" + CreatedAt as "O" (UTC) + "|" + labels sorted ordinal joined "k=v,"`
(a volume name holds no `|`); the preview puts it in the volume item's `Key`; `TryParse(key)` gives the name back for the
argv. The recycled-name guard for containers and networks is the 64-hex id itself, re-read by the engine's live preview;
for a volume it is this fingerprint PLUS the age floor re-applied at the inspect instant. What is left — the milliseconds
between that inspect and the `rm`, against a volume both newly created under a name whose whole promise is that it is
safe to delete and already holding data — is named in the preview's `basis` and the README, verbatim: *"residue: a volume
created under the same testsToDelete- name between the re-inspect and the rm (milliseconds) is removed"*.

**2.8 `wsl-care.keep=true`** — honoured on all three kinds (§2.3 rule 1), the one label the product already reads
(`DockerLabels.Keep`, `DockerInventory.cs:140`); `A_kept_container_is_never_selected` covers a container, and one theory
row each a volume and a network.

##### 3. Commands: the new slot kind, read commands, templates — exact argv shapes

**3.1 `SlotKind.LeftoverName`** (`Processes/Policy/SlotKind.cs`, a new sealed record beside `SnapName`):
`Accepts(value)`: length 15–255; `value.StartsWith("testsToDelete-", OrdinalIgnoreCase)`; every char in `[A-Za-z0-9_.-]`
(Docker's name charset — so no `/`, no space, no control character, no `=`); `value[0]` is a letter by construction, so a
value can never start with `-` (the SlotKind doctrine, `:10-13`). `Describe => "testsToDelete-<docker name>"`. NOT
`Prefixed("testsToDelete-", …)` — `Prefixed` is case-sensitive (`:202`) and the rule's lowercase form must bind.
`HostileInputs.Valid` (`tests/WslCare.TestSupport/HostileInputs.cs:92-112`) gains the kind (a valid instance
`"testsToDelete-pg-" + hex6`), or the property test throws on the unknown kind.

**3.2 Read side** (`Docker/DockerCommands.cs`):
- `NetworkList => new(Executable, "network-ls", ["network", "ls", "--no-trunc", "--format", "{{json .}}"], ListingCeiling, LargeCap)`.
  Row (measured 2026-10-15): `{"CreatedAt":"2026-08-27 09:15:56.687902875 +0000 UTC","Driver":"bridge","ID":"<64 hex>","IPv4":"true","IPv6":"false","Internal":"false","Labels":"k=v,k=v","Name":"…","Scope":"local"}` —
  `DockerText.Instant` already reads that `CreatedAt` shape (`DockerText.cs:68-74, Normalise`), `DockerJson.Labels` that
  `Labels` shape. `NetworkRow(string Id, string Name, Reading<DateTimeOffset> CreatedAt, IReadOnlyDictionary<string,string> Labels)` +
  `NetworkRow.Parse(stdout)` via `DockerJson.Lines(stdout, "docker network ls", …)` in `Docker/DockerInventory.cs`.
- `public const string VolumeInspectTemplate = "{\"name\":{{json .Name}},\"created\":{{json .CreatedAt}},\"labels\":{{json .Labels}}}"`;
  `VolumeInspect(IReadOnlyList<string> names)` → `["volume", "inspect", "--format", VolumeInspectTemplate, .. names]`,
  `ListingCeiling`, `SmallCap`, name `"volume-inspect"`, throws `ArgumentOutOfRangeException` on 0 or > `InspectBatch` names
  (as `ContainerInspect`, `:103-114`) and on NOTHING else — the shape is the template slot's business. The labels member is a
  JSON OBJECT or `null` (measured: `"Labels":{…object…}`; `DockerText.Field` returns empty for an object), so
  `DockerJson.LabelsObject(JsonElement)` (new: `EnumerateObject` → dictionary, ordinal; null/absent → empty) reads it.
  `VolumeDetail(string Name, Reading<DateTimeOffset> CreatedAt, IReadOnlyDictionary<string,string> Labels)` + `Parse(stdout)`
  (one JSON line per volume; `CreatedAt` is RFC 3339 `2026-10-13T08:00:46Z`, `Instant` reads it).
- `ReadVerbs` += `["network", "ls"]`, `["volume", "inspect"]` — and NOTHING else; `stop`, `network rm`, `volume rm` of a
  name stay write verbs (`DockerCollectorTests.cs:43-56` gains `"stop x"`, `"network rm x"`, `"network prune"` rows).
- `ReadCommandTemplates` (`Processes/Policy/ReadCommandTemplates.cs`): `NetworkList` joins the `Func<ToolCommand>` list of
  `All` (`:88-89`); `VolumeInspect { get; } = Machine("volume-inspect", DockerCommands.Executable, [L("volume"), L("inspect"), L("--format"), L(DockerCommands.VolumeInspectTemplate), new ArgPart.Repeat("volume", new SlotKind.LeftoverName(), 1, ConfigKeys.Docker.BatchSize.Max)], ConfigKeys.Docker.ListTimeoutSeconds)`
  declared BEFORE `All` (static order, `:28`) and listed in it. The collector can therefore inspect ONLY prefixed volumes.

**3.3 Write side** (`Actions/DockerCleanups/DockerCleanupCommands.cs`; `Machine(...)` helper `:90`, `FullId` `:32`):
- `ContainerStop = Machine("docker-stop", [L("stop"), L("--time"), new ArgPart.Slot("seconds", new SlotKind.Number(ConfigKeys.TestLeftovers.StopSeconds.Min, ConfigKeys.TestLeftovers.StopSeconds.Max)), new ArgPart.Slot("container", FullId)], ConfigKeys.Docker.RemoveTimeoutSeconds)`
  — argv `docker stop --time <1..120> <64-hex>`: ONE container per command (decision §10.3), so the template's ceiling
  (`docker.removeTimeoutSeconds`) bounds one stop; `NumberRules` gains `testLeftovers.stopSeconds + 10 ≤ docker.removeTimeoutSeconds`.
- `ContainerRemove` reused as it is (`rm -v <64-hex>…`, `:39`).
- `NamedVolumeRemove = Machine("docker-volume-rm-leftover", [L("volume"), L("rm"), new ArgPart.Repeat("volume", new SlotKind.LeftoverName(), 1, ConfigKeys.Docker.BatchSize.Max)], ConfigKeys.Docker.RemoveTimeoutSeconds)`
  — argv `docker volume rm testsToDelete-…`; A4's `VolumeRemove` (64-hex only, `:36`) is untouched.
- `NetworkRemove = Machine("docker-network-rm", [L("network"), L("rm"), new ArgPart.Repeat("network", FullId, 1, ConfigKeys.Docker.BatchSize.Max)], ConfigKeys.Docker.RemoveTimeoutSeconds)`
  — argv `docker network rm <64-hex>…`.
- `NetworkListRead = CommandTemplate.Fixed(() => DockerCommands.NetworkList)`; `ContainerListRead = CommandTemplate.Fixed(() => DockerCommands.ContainerList)`.
- `LeftoverSweepCommands { get; } = [.. Reads, NetworkListRead, ContainerListRead, ReadCommandTemplates.VolumeInspect, ContainerStop, ContainerRemove, NamedVolumeRemove, NetworkRemove]`
  — `Reads` (`:79-83`) itself is NOT widened (A4–A7 keep their declarations).
- No template here takes `-f`, `--force`, `prune` or `--filter name=` — `The_sweep_templates_cannot_be_bent_into_force_or_prune` holds every template of `LeftoverSweepCommands` to: no literal in {`-f`,`--force`,`prune`}, no slot that accepts `-f`.
- `DockerCleanupAnswers` (`:164-165`): `GonePhrases` += `"no such network"`; `InUsePhrases` += `"has active endpoints"`,
  `"is in use by"`. (`docker stop` prints the stopped id on stdout and `Error response from daemon: No such container: x` —
  `"no such container"` already in `GonePhrases`, matched case-insensitively `:192`.)

**3.4 The never-list** (`Processes/Policy/NeverList.cs:46`): a new rule right after `docker-volume-prune`:
`new("docker-network-prune", "docker network prune, in any form (DH-6: A25 removes a re-checked list by id)", argv => IsDocker(argv, "network", "prune"))`.
Three tests, all three or the property suite lies: `CommandPolicyTests.Instances["docker-network-prune"] = ["docker", "network", "prune", "-f"]`
(`:25-42`; the rule-ids theory fails without it); `HostileInputs.NeverCommands` += `["docker", "network", "prune"]`,
`["docker", "network", "prune", "--force"]` (`tests/WslCare.TestSupport/HostileInputs.cs:49`); `NeverOracle.IsNeverCommand`
+= `|| (exe == "docker" && Regex.IsMatch(line, @" network( \S+)* prune "))` (`tests/…/Policy/NeverOracle.cs:44-45` — the
SECOND implementation, kept in its different shape).

**3.5 The runner match** (`Core/TestLeftovers/RunnerPatterns.cs`, PURE):
- A pattern is a line of whole-token words: `<program>( <word>)*`. `<program>` tests argv[0]'s base name — `NeverList.Exe(argv)`
  (`NeverList.cs:62-75`: last path segment, lowercased, `.exe/.com/.bat/.cmd/.ps1` stripped) — exactly, or with ONE leading
  `*` (suffix: `*.tests`) or ONE trailing `*` (prefix: `testhost*`). Each later `<word>` must match, IN ORDER, a later argv
  token as an ordered subsequence (each pattern word consumes one token); a token is compared by its FILE NAME (last segment
  after `/` or `\`), case-insensitively, ordinal — a whole token, never a substring, never the redacted `CommandLine`, never
  `Programs`.
- `TextRule.RunnerPattern` (member rule of the key): `^(\*[a-z0-9_.-]+|[a-z0-9_.-]+\*|[a-z0-9_.-]+)( [a-z0-9_.-]+){0,7}$`
  case-insensitive, ≤ 128 chars; a bare `*`, two `*`, a `*` in a later word, a `/`, `\`, `=` or space-run is refused.
- Defaults (`default.json` `"testLeftovers": { "runnerPatterns": [...] }`):
  `"*.tests"`, `"testhost*"`, `"dotnet test"`, `"dotnet testhost.dll"`, `"node vitest.mjs"`, `"node vitest"`, `"npm test"`,
  `"npm run test"`, `"node npm-cli.js test"`, `"node npm-cli.js run test"`. (`*.tests` matches `WslCare.Core.Tests.exe` and
  `WslCare.Core.Tests` alike after the `.exe` strip; `dotnet testhost.dll` matches `dotnet exec --depsfile … testhost.dll`
  because the match is a subsequence; `node npm-cli.js test` is what `npm test` IS on both sides.) A false positive — `dotnet
  build test` — blocks a sweep, the safe direction; the README says so.
- `RunnerPatterns.From(EffectiveConfig)` (the key's list, each `Parse`d — a member the rule refused never reaches here) and
  `RunnerPatterns.Match(IReadOnlyList<string> argv) → string` (the first matching pattern's text, `""` when none).
- WSL: `ProcessEntry` gains `public string TestRunner { get; init; } = string.Empty;` — the matched pattern text, set by
  `ProcessCollector.Entry` (`Collectors/ProcessCollector.cs:153` beside `ProcessFamilies.Of(raw.Argv, …)`) from `raw.Argv`
  (the raw `/proc/<pid>/cmdline` words, `:193`), for every counted process whose `State != 'Z'`; an entry built without it
  has NONE — deliberately NOT derived from `CommandLine` as `Programs` is (`:45-52`), because that fallback is the
  redacted, cut line. `ProcessSnapshot.TestRunners => [.. All.Where(e => e.TestRunner.Length > 0).Select(e => new RunnerMatch(e.Pid, Program(e), e.TestRunner))]`.
  `ProcessCollector` takes the patterns: a fourth constructor parameter `RunnerPatterns patterns`; `Tuning` gains
  `TextList(ConfigKey.TextListKey) => config.TextList(key)` and `LinuxProbe.cs:28` passes `RunnerPatterns.From(Tuning.Current)`
  (or the probe's own `EffectiveConfig`, whichever it already holds — one source, no second parse).
- Windows (shipped here, consumed by DH-7): `Processes/WindowsArgv.cs` — `public static IReadOnlyList<string> Split(string commandLine)`,
  a managed implementation of `CommandLineToArgvW`'s rules, the inverse of `WindowsCommandLine.Append` (`Processes/WindowsCommandLine.cs:26-59`):
  the first token ends at the first space unless it starts with `"` (then at the closing quote), backslashes NOT special in
  it; afterwards `2n` backslashes before `"` → `n` backslashes and the quote toggles; `2n+1` → `n` backslashes and a literal
  `"`; backslashes not before a quote are literal; `""` inside a quoted run is a literal `"`; whitespace outside quotes
  separates; an empty line → `[]`. `WindowsRunnerScan.Read(IWindowsProcessTable table, RunnerPatterns patterns)`: `List()`
  unavailable → `Reading.Missing` (→ `Unknown`); per entry, `CommandLine(pid)` available → `Split` → `Match`; unavailable
  (another account's, a protected one — `Win32ProcessTable.cs:84-96`) → match on `[ExeName]` alone (an image named
  `X.Tests.exe` or `testhost.exe` is still seen; `dotnet test` of another account is not — decision §10.5) and the
  reading counts them: `"none (3 of 212 command lines unreadable, judged by image name)"`.

##### 4. The keys (`Config/ConfigKeys.TestLeftovers.cs`, new; `All`; `NumberKeys()`; `default.json`; `contracts/config-keys.json`; `NumbersAreConfigurationTests`; README)

| key | kind | default | range | trust | notes |
|---|---|---|---|---|---|
| `auto.A25` | `BoolKey` | `false` | — | `KeyTrust.Off` | `ConfigKeys.Auto` after `A21` (`ConfigKeys.cs:70`), in `All` after `Auto.A21` (`:134`); `ActionId.All` derives the id; `ConfigSchemaTests.The_plan_defaults_are_the_shipped_defaults` asserts false |
| `testLeftovers.minAgeMinutes` | `IntKey` | 60 | **60**–10080 | `KeyTrust.Higher` | the rule's hour is the FLOOR: the range minimum IS the floor, so no layer can lower it (`A_floor_below_an_hour_is_refused_by_the_key_itself`) |
| `testLeftovers.stopRunning` | `BoolKey` | **`false`** | — | `KeyTrust.Off` | §0a.1: whether a running leftover is stopped at all; off until DH-7 reads the Windows side (DH-7 flips the default); the user layer may only turn it off; it is listed in `All` beside the other `testLeftovers.*` keys, NOT under `auto.` (it is not an action id — `ActionId.All` derives ids from the `auto.` prefix only, `ActionId.cs:50`) |
| `testLeftovers.stopRunningOlderThanHours` | `IntKey` | 24 | 2–720 | `KeyTrust.Higher` | the second gate, applied only when `stopRunning` is on |
| `testLeftovers.stopSeconds` | `IntKey` | 10 | 1–120 | `KeyTrust.Display` | `NumberRules`: `stopSeconds + 10 ≤ docker.removeTimeoutSeconds` |
| `testLeftovers.runnerPatterns` | `TextListKey("testLeftovers.runnerPatterns", [], new TextRule.RunnerPattern(), 32)` | §3.5's ten | ≤ 32 members | `new(SafeDirection.None, MachineOnly: true)` | the OPEN-list precedent is `mcpServers.programs` (`ConfigKeys.McpServers.cs:21`); `ContractFilesTests.The_config_keys_contract_describes_the_open_list` (`:176`) now has TWO open lists — widen it to both, do not drop one |

The three int keys join `NumberKeys()` (group A, behaviour) and the name list in `NumbersAreConfigurationTests`
(`tests/…/Config/NumbersAreConfigurationTests.cs:49-52` style). `default.json` gains the `"testLeftovers"` block and
`"A25": false` in `"auto"`. `ActionId.ExecutionOrder` (`ActionId.cs:63`, hand-typed): `"A25"` FIRST — before
`A5Testcontainers` — so the sweep's containers and volumes are gone before A4/A5 read their lists.
`contracts/actions.json` and `config-keys.json` regenerate (§8).

##### 5. The preview the owner sees, the row, the modals

**5.1 `act A25 --preview --json`** (the `ActionPreview`, `Actions/ICleanupAction.cs:118-152`; wire shape as `act-a4-preview.json`):
- `what`: `test leftovers named testsToDelete- (any case) older than {minAgeMinutes} minutes: containers, then volumes, then networks; a running container is {stopRunning ? "stopped first (docker stop --time {stopSeconds}) only when older than {stopRunningOlderThanHours} h" : "listed and not stopped (testLeftovers.stopRunning is off until the Windows side is read)"}; only while no test run is in flight on this side`.
- `count` = `Selected.Count` (all kinds); `bytes` = the sum of the known sizes; `facts`: `containers`, `volumes`, `networks`
  (selected per kind), `runningToStop`, `runningNotStopped`, `keptYounger`, `keptRunningYounger`, `keptByLabel`, `unaged`,
  `signalsInFlight`, `signalsUnknown`, plus `RowPreviews`' `count`/`bytes`/`unsized`.
- `items` (first 20; `Targets` all): kind `container` | `volume` | `network`, `name` = the display name, `bytes`, `note` =
  `created {age} ago` or `running {age}: docker stop --time {s}, then rm -v`; `Key` = the 64-hex id, or the volume fingerprint.
  A running leftover that is NOT stopped (§0a.1) is not a target: it is counted in `runningNotStopped` and named in the
  `basis` kept list (`"{k0} running, listed, not stopped — testLeftovers.stopRunning off ({names…})"`).
- `basis` — the signals, EVERY one with its reading, then the kept reasons, in this fixed order and wording:
  `docker system df -v (containers, volumes), docker network ls, docker volume inspect of the {n} matched volumes, the process table from /proc; in-flight — runner.wsl: {reading}; runner.windows: {reading}; testcontainers: {reading}; young: {reading}; kept — {k1} running younger than {h} h ({names…}), {k2} younger than {m} min, {k3} by the wsl-care.keep=true label, {k4} without a readable creation time; a test started after this judgement is not seen until the next one — the age floor protects it, and the judgement is repeated before every batch of {batchSize}; residue: a volume created under the same testsToDelete- name between the re-inspect and the rm (milliseconds) is removed`.
  The extension's first modal already shows `basis` for A21 (`rootOutcome.ts:125`, `modalText.ts`); A25 joins that set so the
  owner reads every signal before confirming.
- `refusal`: `judgement.Refusal` (§1.1) — the engine answers status `refused`, reason = that text, and the panel shows it.
- `Skip`: `"no testsToDelete- container, volume or network older than {m} min"` when nothing is selected AND nothing blocks.

**5.2 The cleanup-table row** — `CleanupPreviews.A25Row(DockerSnapshot snapshot, EffectiveConfig config, DateTimeOffset now)`
added to `Build` (`Preview/CleanupPreview.cs:60-70`) after `A7Row`: id `A25`, action `A25`, what as 5.1, `autoSwitch`
`auto.A25`, basis `"docker system df -v sizes (containers, volumes); networks have no size"`, figures from
`LeftoverSelection.Select` over the snapshot with `RowNote`s whose `What` is FIXED text (tests key on it; no name, no
number in it): `running, listed, not stopped (testLeftovers.stopRunning is off)` / `running, younger than the stop limit
(kept)` / `younger than the age floor (kept)` / `protected by the wsl-care.keep=true label` / `no readable creation time
(never selected)`; `Refusal` EMPTY —
the in-flight judgement is the ACTION's (its preview and every batch), not the table's (decision §10.1); the row's `what`
ends `"; swept only while no test run is in flight (act A25 judges it)"`. `Every_docker_and_folder_actions_preview_equals_its_row_of_preview_all`
(`DockerCleanupTests.cs:391`) then covers A25 for free.

**5.3 The second modal** (`src_vs_code/src/cleanup/modalText.ts:38-44`, `SECOND`): `A25: 'A25 stops and removes EVERY
testsToDelete- container, volume and network on this Docker — including one of a run still in progress in another repository
or CI job on this machine, if the in-flight check missed it — older than testLeftovers.minAgeMinutes; a running one is stopped
only when older than testLeftovers.stopRunningOlderThanHours.'`. `RECHECKED` (`:32`) += `'A25'`.

**5.4 Extension plumbing:** `ROW_IDS` += `'A25'` (`cleanup/rowIds.ts:11`); `ACTION_IDS` += `'A25'` (`root/rootIds.ts:18-20`;
`rootIds.test.ts` compares to the regenerated `contracts/actions.json`); `DOCKER_ROW_IDS` += `'A25'`
(`client/worstCases.ts:103` — its preview is one snapshot + `network ls` + inspect, counted as a snapshot, the dearest unit);
tests pinned to the row count move: `ceilings.test.ts:48` `previewWorstCaseS([...ROW_IDS])` 2014 → 2344 (`:47` stays 1980,
it names six); `rootCall.test.ts:111` seven snapshots `(7 * 350 + 34 + 20) * 1000` and the id list; `cleanupView.test.ts:33`
the row list; `modalText.test.ts:70-73` (`RECHECKED` loop) and `:99-106` (the second modal names A25 and its two settings).
The panel's cleanup row reads `Clean A25` from the existing derivation (`cleanupView.ts`), no new control.

##### 6. The RED scenarios — complete; each names what it asserts and its fixture

Test projects: xUnit v3 + FluentAssertions, run as executables (`WslCare.Core.Tests.exe`, `WslCare.Scenarios.exe`), never
`dotnet test`. Fixtures: `DockerWorld` (`tests/WslCare.Core.Tests/Actions/DockerWorld.cs`, the captured 2026-10-02 Docker
edited as TEXT through the product's parsers) widened with `LeftoverWorld` (same file family, new
`tests/…/Actions/LeftoverWorld.cs`): a builder that APPENDS synthetic rows to the captured `system df -v` text
(`AddContainer(name, id, state, createdAt, labels, image)`, `AddVolume(name, createdAt, labels)`), scripts
`docker network ls` (`AddNetwork(name, id, createdAt, labels)`), `docker volume inspect` (per name and per batch),
`docker ps -a`, `docker stop`, `docker rm -v`, `docker volume rm`, `docker network rm` (each an `ScriptEffect` echoing the
asked names unless the test scripts a refusal), and a `Reading<ProcessSnapshot>` for `ActionContext.Processes` (`Processes(entries…)`).
The world's constructor asserts `DockerInventory.Parse(dfv).IsAvailable`, `NetworkRow.Parse(...).IsAvailable` and
`VolumeDetail.Parse(...).IsAvailable` — a fixture the parser rejects proves nothing (testing.md). Every synthetic name is
`testsToDelete-…`; `Now = DockerFixture.CapturedAt` and ages are set relative to it.

**6.1 `tests/WslCare.Core.Tests/TestLeftovers/TestRunsTests.cs`** (pure, no fixture but records):
- `A_running_test_runner_refuses_the_sweep_and_names_it` — `Runner(Wsl, Of([RunnerMatch(4242,"dotnet","dotnet test")]))` → `InFlight`, reason `4242 dotnet matches "dotnet test"`; `Judge` over it → `Blocks`, `Refusal` starts `in flight: runner.wsl:`.
- `An_unreadable_process_table_refuses_the_sweep` — `Runner(Wsl, Missing("…"))` → `Unknown`; the judgement `Unknown`, refusal `unknown: runner.wsl: the process table could not be read (…)`.
- `A_running_ryuk_blocks` — a `ContainerRow` `State running`, labels `org.testcontainers.ryuk=true`, 3 days old → `InFlight`; a second row with image `testcontainers/ryuk:0.11.0` and no label → `InFlight`.
- `A_running_testcontainers_container_refuses_the_sweep` — label `org.testcontainers=true`, running, 12 min old, floor 60 → `InFlight` naming the age.
- `A_stale_running_testcontainers_container_does_not_block_forever` — same label, running, 3 days old → `Clear`; stopped and 12 min old → `Clear`.
- `A_running_labelled_container_without_a_readable_age_counts_as_young` — `CreatedAt` Missing → `InFlight` naming the unread age.
- `An_unreadable_docker_list_is_unknown` — `LiveTestcontainers(Missing)` and `Young(Missing)` → `Unknown` each.
- `A_young_matched_resource_blocks_and_an_unaged_one_does_not` — `Young([AgedName("volume","testsToDelete-v", 12 min)])` → `InFlight`; `[AgedName(…, Missing)]` → `Clear`.
- `An_unreadable_signal_is_unknown_never_not_observed` — theory over every constructor with `Reading.Missing` → state is `Unknown`, never `NotObserved`.
- `The_other_side_is_listed_as_not_observed_and_never_blocks` — `OtherSide(Wsl)` id `runner.windows`, `NotObserved`, reading contains `not read from this side`; `Judge([OtherSide(Wsl)] ++ four Clear)` → `None`.
- `Unknown_blocks_exactly_as_in_flight_and_in_flight_wins_the_word` — `[Unknown, InFlight]` → `State InFlight`, refusal lists BOTH reasons; `[Unknown, Clear]` → `Unknown`.
- `A_judgement_over_no_signal_is_unknown` — `Judge([])` → `Unknown`.
- `Every_signal_is_in_the_judgement_in_order_with_its_reading` — four signals in → four out, same order, readings non-empty.

**6.2 `tests/…/TestLeftovers/RunnerPatternsTests.cs`** (pure):
- `Npm_test_is_matched_on_the_raw_argv_not_on_Programs` — argv `["node","/usr/lib/node_modules/npm/bin/npm-cli.js","test"]` → `Match` = `node npm-cli.js test`; the same words through `CommandLineText.ProgramNames` give `["node","npm-cli.js"]` and `RunnerPatterns.Match(programs)` is `""` (the test SAYS why `Programs` cannot be the input).
- `Dotnet_exec_testhost_is_matched` — `["dotnet","exec","--depsfile","/x/a.deps.json","--runtimeconfig","/x/a.runtimeconfig.json","/x/testhost.dll","--port","5"]` → `dotnet testhost.dll`.
- `A_tests_exe_matches_by_image_suffix_in_any_case` — `["/home/me/x/bin/Debug/net10.0/WslCare.Core.Tests"]`, `["C:\\x\\WslCare.Core.TESTS.exe"]`, `["testhost.x86.exe"]` each match; `["prod-tests-exporter"]` does not (not a suffix).
- `A_substring_of_the_command_line_never_matches` — `["code","--goto","dotnet test runner.md"]`, `["bash","-c","dotnet test"]` (one token holding both words), `["vim","vitest.mjs.bak"]` → `""`.
- `A_shell_whose_text_mentions_test_is_not_a_runner` — `["zsh","-i"]` with cwd text irrelevant; `["node","/x/vitest-ui-server.js"]` → `""`.
- `A_watcher_blocks` — `["node","/x/node_modules/vitest/vitest.mjs","--watch"]` → match (the plan: a watcher blocks and is named).
- `Words_match_as_an_ordered_subsequence_of_whole_tokens` — `["npm","run","test"]` matches `npm run test`; `["npm","test","run"]` does not match `npm run test` (order); `["npm","run","test"]` ALSO matches `npm test` (subsequence — documented).
- `The_pattern_rule_refuses_what_is_not_a_closed_token_shape` — theory: `"*"`, `"**.tests"`, `"node *"`, `"dotnet test/x"`, `"a=b"`, `""`, `" node"`, 129 chars → `Problem` non-empty; the ten defaults → empty.
- `The_defaults_are_the_shipped_defaults` — `RunnerPatterns.From(embedded default config).Patterns` equals §3.5's ten in order.

**6.3 `tests/…/Collectors/ProcessCollectorTests.cs`** (a `LinuxSandbox` `/proc` written by the test — the sandbox pattern of `A21PreviewGolden.cs:48-60`: `status`, `stat`, `cmdline` with NUL-separated words):
- `The_collector_marks_a_test_runner_from_the_raw_cmdline_and_keeps_only_the_match` — a pid whose cmdline is `node\0/usr/lib/node_modules/npm/bin/npm-cli.js\0test\0` → `entry.TestRunner == "node npm-cli.js test"`, `snapshot.TestRunners` has it with `Program == "node"`; a pid `bash\0-c\0dotnet test\0` → `TestRunner == ""`.
- `A_zombie_is_never_a_runner` — state `Z` → `""`.
- `An_entry_built_without_the_collector_has_no_runner` — `new ProcessEntry(... CommandLine: "dotnet test" ...)` → `TestRunner == ""` (no derivation from the shown line).

**6.4 `tests/…/Processes/WindowsArgvTests.cs`** (the precedent `WindowsCommandLineTests.cs:28-52`):
- `The_split_is_the_inverse_of_the_command_line_builder` — theory over `WindowsCommandLineTests.Lines` plus `["C:\\Program Files\\dotnet\\dotnet.exe","test","--no-build"]`, `["w.exe","",""]`: `WindowsArgv.Split(WindowsCommandLine.Of(argv[0], argv[1..]))` equals `argv` on every leg.
- `The_split_agrees_with_CommandLineToArgvW` — Windows leg (`Assert.SkipUnless(OperatingSystem.IsWindows(), …)`), theory over raw lines including an unquoted first token with backslashes (`C:\a\b.exe "x y"`), `a\\\"b`, `"a""b"`, trailing spaces, an empty line: `Split(line)` equals the P/Invoke's answer (the test project's own import, `:51-55`).
- `An_unquoted_program_with_a_space_in_its_folder_splits_at_the_space` — `C:\Program Files\x\w.exe a` → `["C:\Program","Files\x\w.exe","a"]` (CommandLineToArgvW's documented behaviour; the test records it so nobody "fixes" it).

**6.5 `tests/…/TestLeftovers/WindowsRunnerScanTests.cs`** (`GivenTable`-style fake, `tests/…/Archive/WindowsSideFakes.cs:14-20`, widened to a per-pid command line):
- `A_runner_on_the_windows_table_is_matched_from_its_split_command_line` — pid 7 `dotnet.exe` with line `"C:\Program Files\dotnet\dotnet.exe" test --no-build` → one `RunnerMatch(7,"dotnet","dotnet test")`.
- `An_unreadable_command_line_is_judged_by_image_name_and_counted` — `CommandLine` Missing, `ExeName` `WslCare.Core.Tests.exe` → matched by `*.tests`; `ExeName` `dotnet.exe` → not matched; the reading text counts `1 of N command lines unreadable`.
- `An_unreadable_table_is_unknown` — `List()` Missing → `Reading.Missing`, and `TestRuns.Runner(Windows, it)` → `Unknown`.

**6.6 `tests/…/TestLeftovers/LeftoverSelectionTests.cs`** (pure, `LeftoverCandidate` records, `now` fixed):
- `A_name_with_the_prefix_in_another_case_matches` — `TESTSTODELETE-x`, `teststodelete-x`, `TestsToDelete-x` all match; a container row `Names` `"keep-me,testsToDelete-pg-1"` matches and displays `testsToDelete-pg-1`.
- `A_name_with_the_prefix_later_in_it_never_matches` — `prod-testsToDelete-cache`, `/testsToDelete-x` is matched only AFTER the `/` trim of a container name (volumes/networks never carry one), `xtestsToDelete-`.
- `The_underscore_form_is_not_matched` — `teststodelete_pg`, `testsToDelete_pg`.
- `A_resource_younger_than_an_hour_is_never_selected` — 59 min old, running or not, labels or not → `Younger`, whatever the signals (the selection has no signal input at all — the test says so).
- `A_running_leftover_is_listed_and_not_stopped_while_stopRunning_is_off` — running, 3 days old, `StopRunning false` → `RunningNotStopped`; the same with `StopRunning true` → `Selected` (§0a.1).
- `A_running_leftover_younger_than_24h_is_kept` — `StopRunning true`, running, 5 h old, limit 24 h → `RunningYounger` with the note; stopped, 5 h old → `Selected` whatever `StopRunning`.
- `A_running_leftover_older_than_24h_is_selected_to_be_stopped` — `StopRunning true`, running, 25 h → `Selected` with `Running == true`.
- `StopRunning_defaults_to_off_and_the_user_layer_cannot_turn_it_on` — the embedded defaults read `false`; a user layer `true` over a machine `false` is refused as loosening (`KeySafety.IsNoLooser`, `KeyRules.cs:171-178`).
- `A_kept_container_is_never_selected` — theory: a container, a volume, a network with `wsl-care.keep=true`, 3 days old → `Kept`.
- `An_unaged_resource_is_listed_and_never_selected` — `CreatedAt` Missing → `Unaged`.
- `The_order_is_containers_volumes_networks` — mixed input → `Selected` kinds in that order, names ordinal within a kind.
- `The_floor_only_grows` — `LeftoverLimits.From(config with minAgeMinutes 30)` is impossible: the key's `Min` is 60, so the loader refuses the layer (`ConfigLoader` over a user layer with `30` → the key's problem names the range).

**6.7 `tests/…/Docker/DockerParserTests.cs`, `DockerCollectorTests.cs`:**
- `A_network_row_parses_the_measured_shape` — the 2026-10-15 line (§3.2) → `Id` 64-hex, `CreatedAt` available, `Labels["k"]`.
- `A_volume_detail_parses_labels_as_an_object_and_null_as_none` — `{"name":"testsToDelete-v","created":"2026-10-13T08:00:46Z","labels":{"a":"b"}}` and `"labels":null`.
- `Every_docker_command_the_product_can_build_is_a_read_verb_with_a_ceiling` (`:37-44`) — the name list gains `"network-ls"`, `"volume-inspect"`; `EveryCommand()`'s reflection builds `VolumeInspect` with the `List<string>` sample as it does `ContainerInspect` — the factory must not refuse a 64-'a' name (the template does).
- `A_write_verb_is_not_a_read_verb` — rows `"stop x"`, `"network rm x"`, `"network prune"`, `"volume rm testsToDelete-x"`.
- `The_collector_inspects_only_the_matched_volumes_and_none_when_none_match` — over the captured fixture (no match) the runner saw NO `volume inspect`; with one matched volume appended, exactly one inspect naming it.
- `A_failed_network_list_leaves_the_part_unavailable_with_its_reason`.

**6.8 `tests/…/Processes/Policy/*`:**
- `Network_prune_is_on_the_never_list` — `CommandPolicyTests.Instances["docker-network-prune"]`; the rule-id theory (`:51-60`) covers the refusal text `never-list (docker-network-prune:`; `A_harmless_argv_breaks_no_never_rule` gains `["docker","network","rm","<64-hex>"]` and `["docker","volume","rm","testsToDelete-pg-1"]`.
- `The_leftover_name_slot_accepts_only_the_prefix_in_any_case_and_dockers_charset` — `SlotKind.LeftoverName`: accepts `testsToDelete-pg-3f9a1c`, `TESTSTODELETE-X`, `teststodelete-x.y_z`; refuses `-testsToDelete-x` (cannot occur: no leading `-` by construction — assert anyway), `prod-testsToDelete-x`, `testsToDelete-`, `testsToDelete-a/b`, `testsToDelete-a b`, `testsToDelete-a\n`, `teststodelete_x`, 256 chars, a 64-hex id.
- `The_sweep_templates_cannot_be_bent_into_force_or_prune` — every template of `LeftoverSweepCommands`: no `Literal` in {`-f`,`--force`,`prune`,`--filter`}, and `Bind` of `["-f", …]` / `["--force"]` into any slot is `Refused`.
- `The_stop_template_takes_one_container_and_the_key_range` — `ContainerStop.Bind(["10", hex64])` bound to `["stop","--time","10",hex64]`; `Bind(["0", hex64])`, `Bind(["121", hex64])`, `Bind(["10", hex64, hex64])` refused.
- `CommandPolicyPropertyTests` (the generated world, `GeneratedWorld.cs:40-62`) — the world scripts `network ls` (one synthetic row), `volume inspect` (echo per name with a created time), `ps -a`, `stop`, `volume rm` of a name, `network rm`; the action property then exercises A25's run path with hostile configs — no new assertion, the existing three (allowed, declared, never a never-command) must stay green over A25.

**6.9 `tests/…/Actions/TestLeftoverSweepTests.cs`** (over `LeftoverWorld`; `Context` as `DockerWorld.Context(sandbox, …)` with `Processes` supplied):
- `The_judgement_is_repeated_before_every_batch` — `docker.batchSize` 1 in the user layer (MachineOnly → the test's layer is the machine layer, or `Tuning.Use`), three stopped old containers, a process snapshot that is CLEAR for the preview and the first batch and reads a runner from the SECOND `Processes` call on → one container removed, two `not attempted: in flight: …`, `Failure` names batch 2, and the runner saw exactly ONE `docker rm`; the process table was read 1 + 2 times.
- `A_running_leftover_is_stopped_then_removed_never_forced` — `testLeftovers.stopRunning` true in the machine layer, a running 3-day container: argv sequence is exactly `docker stop --time 10 <id>` then `docker rm -v <id>`; no argv anywhere holds `-f` or `--force` (assert over `Runner.Requests`).
- `With_stopRunning_off_a_running_leftover_is_listed_and_no_stop_is_sent` — the defaults: the same container is in the preview's `runningNotStopped` fact and basis, NOT in `Targets`; the runner saw no `docker stop` and no `rm` naming it; the stopped leftovers beside it are removed.
- `A_running_ryuk_image_under_a_registry_prefix_blocks` — `Image` `ghcr.io/testcontainers/ryuk:0.11.0` and `testcontainers/ryuk` → `InFlight`; `my-testcontainers/ryuk-fork:1` → `Clear` (the path must be exactly `testcontainers/ryuk`).
- `A_leftover_restarted_after_the_stop_is_kept_by_docker` — `rm -v` scripted to answer `Error response from daemon: cannot remove container "/testsToDelete-x": container is running` → `NotRemoved` `kept by Docker, in use since the preview`, no failure.
- `A_stop_that_docker_cannot_confirm_skips_the_rm_and_fails_by_name` — stop answers nothing → not confirmed, no `rm` for that id, `Failure` names it.
- `A_volume_recreated_under_the_same_name_after_the_preview_is_kept` — the preview's inspect says created 3 d ago, labels `{}`; the run's inspect answers created 2 min ago → no `volume rm` is sent for it, note `kept: a different volume under the same name since the preview`.
- `A_volume_gone_before_its_re_inspect_is_already_gone` — inspect exits 1 `Error response from daemon: get testsToDelete-v: no such volume` → `already gone`, no failure.
- `A_volume_in_use_is_refused_by_docker_and_kept` — `volume rm` stderr `volume is in use - [abc]` → `InUse`.
- `A_network_with_endpoints_is_refused_by_docker_and_kept` — `network rm` stderr `error while removing network: network testsToDelete-n id … has active endpoints` → `InUse`; `no such network` → `already gone`.
- `The_order_is_containers_volumes_networks` — one of each: the runner's write argv order is `stop?`, `rm -v`, `volume rm`, `network rm`; the re-judgement reads (`ps -a`, `network ls`, `volume ls`) appear before EACH write.
- `Nothing_younger_than_the_floor_is_ever_sent_to_docker` — a 30-min container, volume and network beside old ones → no write argv names them; the preview's `Younger` note counts 3.
- `A_preview_whose_judgement_blocks_refuses_and_runs_nothing` — through `ActionEngine` (`ActionEngineTests` style): status `refused`, reason starts `in flight:`; the runner saw no write.
- `The_preview_names_every_signal_with_its_reading_and_the_other_side` — `basis` contains `runner.wsl: none`, `runner.windows: not read from this side`, `testcontainers: none`, `young: none`, the four kept counts, the snapshot sentence, the residue sentence.
- `The_preview_counts_per_kind_and_keys_each_target` — facts `containers`/`volumes`/`networks`; container and network keys are their 64-hex ids; a volume key round-trips through `VolumeFingerprint.TryParse`.
- `A_cut_off_sweep_keeps_what_docker_confirmed` — cancel during the volumes phase → `Interrupted`, containers confirmed stay `Removed`, the volume batch `unknown: cut off mid-command`, networks `not attempted: the run was interrupted`.
- `Freed_bytes_are_the_sizes_of_exactly_the_confirmed_containers_and_volumes` — a network frees 0; a refused container counts 0.
- `A25_on_the_windows_binary_is_skipped_naming_the_side` — `Sides == [Wsl]`, engine over `WindowsHostPaths` → `skipped`, reason `runs on the WSL distro side`.
- `A25s_preview_equals_its_row_of_preview_all` — the existing theory `:391` extended to `A25` (count, bytes, unsized).

**6.10 Scenarios (`tests/WslCare.Scenarios/ActFlows.cs`, built CLI, fakebin, `claimsRoot`)**:
- `Act_A25_preview_over_the_captured_docker_reads_only_read_verbs_and_judges_from_the_fixture` — `Docker("act-a25")` with the procfs fixture copied (`ProcfsFixture.CopyTo`, as `CollectFlows` does) and `network-ls.out` scripted (§8, the fixture file); `act A25 --preview --json` exits 0, every `docker` argv in `home.Calls` passes `DockerCommands.IsReadVerb`, the preview's basis names the four signals and `runner.windows: not read from this side`; the Windows leg exits `Usage` naming the side (the existing arm, `:151-155`).
- `A_preview_of_every_action_this_build_holds_answers_each_from_live_state_and_starts_only_read_commands` (`:140`) — `ActionRegistry.Product` now holds A25; `Docker(...)` scripts `network-ls.out` so A25's part is available.
- `Act_A25_confirm_over_a_leftover_world_stops_then_removes_and_records_the_run` — a scenario `system df -v` edited to hold one old running `testsToDelete-` container (the `DockerWorld` text-edit technique on the captured file, inside the scenario's fixture copy), scripted `stop` / `rm -v` echoes, process fixture clear: `act A25 --confirm --json` → the run detail (`runs show`) lists `removed` with the container, `commands` with `docker-stop` then `docker-rm-v`, no `-f` in any `fake-calls.jsonl` argv.
- Golden `act-a25-preview-action.json` (`GoldenContracts.cs:247-254` pattern): the preview line of the first scenario, normalised; `contracts/golden/head/preview.json` regenerates with the A25 row.

**6.11 Contracts/config tests:** `ConfigSchemaTests.The_plan_defaults_are_the_shipped_defaults` (+ `auto.A25` false,
`testLeftovers.stopRunning` false, the three numbers, the ten patterns); `NumbersAreConfigurationTests` (+ three names); `ContractFilesTests.The_checked_in_contracts_equal_what_the_types_enumerate`
(regenerated `actions.json` — `A25` in `ids` and FIRST in `executionOrder` — and `config-keys.json`);
`The_config_keys_contract_describes_the_open_list` widened to two; `NumberRules` test for the stop/remove coupling.

**6.12 Extension (`node --test` through the repo's runner):** `rootIds.test.ts` (A25 in the registry; `actionIdOf('A25')`),
`ceilings.test.ts:48`, `rootCall.test.ts:111`, `cleanupView.test.ts:33`, `modalText.test.ts` — `the second confirmation names
A25 with testLeftovers.minAgeMinutes and testLeftovers.stopRunningOlderThanHours` and the `RECHECKED` loop includes A25;
`the first modal shows A25's basis` (as A21's).

##### 7. Break-it (each on PRODUCT code, red, then restored — one at a time, named in the PR)

1. The anchored match replaced by `Contains` → `A_name_with_the_prefix_later_in_it_never_matches` red.
2. `OrdinalIgnoreCase` → `Ordinal` → `A_name_with_the_prefix_in_another_case_matches` red.
3. The age floor removed from `Select` (rule 3 deleted) → `Nothing_younger_than_the_floor_is_ever_sent_to_docker`, `A_resource_younger_than_an_hour_is_never_selected` red.
4. The key's minimum lowered to 1 → `The_floor_only_grows` red.
5. The runner match moved onto `Programs` → `Npm_test_is_matched_on_the_raw_argv_not_on_Programs`, `Dotnet_exec_testhost_is_matched` red.
6. The token match made a substring (`Contains` over the joined line) → `A_substring_of_the_command_line_never_matches` red.
7. "Any running labelled container blocks" → `A_stale_running_testcontainers_container_does_not_block_forever` red.
8. The Ryuk image test dropped (label only) → the second row of `A_running_ryuk_blocks` red.
9. The per-batch re-judgement removed (judged once before the first batch) → `The_judgement_is_repeated_before_every_batch` red.
10. `Unknown` read as clear (`State = any InFlight ? InFlight : None`) → `An_unreadable_process_table_refuses_the_sweep`, `Unknown_blocks_exactly_as_in_flight…` red.
11. A `Reading.Missing` mapped to `NotObserved` in one constructor → `An_unreadable_signal_is_unknown_never_not_observed` red.
12. `-f` added to `ContainerRemove`/`NamedVolumeRemove`/`NetworkRemove`, or `stop` replaced by `kill` → `The_sweep_templates_cannot_be_bent_into_force_or_prune`, `A_running_leftover_is_stopped_then_removed_never_forced` red.
13. The volume re-inspect removed (rm straight from the preview's list) → `A_volume_recreated_under_the_same_name_after_the_preview_is_kept` red.
14. The fingerprint compared on `CreatedAt` only (labels ignored) → a theory row of the same test (same time, different labels) red.
15. The running-younger rule removed (every old running leftover stopped) → `A_running_leftover_younger_than_24h_is_kept` red.
16. `network prune` rule removed from `NeverList` → `Network_prune_is_on_the_never_list` AND `CommandPolicyPropertyTests` (the oracle still forbids it) red — two tests, two implementations.
17. `["network","ls"]` left out of `ReadVerbs` → `Every_docker_command_the_product_can_build_is_a_read_verb…` red; `["stop"]` ADDED to `ReadVerbs` → `A_write_verb_is_not_a_read_verb` red.
18. The order changed to volumes before containers → `The_order_is_containers_volumes_networks` red.
19. `ROW_IDS` without `A25` while `preview.json` carries the row → `rowIds`/`cleanupView` tests red; `ACTION_IDS` without it → `rootIds.test.ts` red.
20. The stop template widened to a `Repeat` → `The_stop_template_takes_one_container_and_the_key_range` red.
21. `stopRunning` ignored (an old running leftover stopped under the defaults) → `With_stopRunning_off_a_running_leftover_is_listed_and_no_stop_is_sent` red; its default flipped to `true` in `default.json` → `StopRunning_defaults_to_off…` and `ConfigSchemaTests` red.
22. The Ryuk image test made a substring (`Contains("ryuk")`) → the fork row of `A_running_ryuk_image_under_a_registry_prefix_blocks` red.

##### 8. Files

Daemon (`src_daemon/src/WslCare.Core/` unless said): NEW `TestLeftovers/TestRuns.cs`, `TestLeftovers/RunnerPatterns.cs`,
`TestLeftovers/LeftoverNames.cs`, `TestLeftovers/LeftoverSelection.cs`, `TestLeftovers/WindowsRunnerScan.cs`,
`TestLeftovers/VolumeFingerprint.cs`, `Processes/WindowsArgv.cs`, `Actions/TestLeftovers/TestLeftoverSweep.cs`,
`Config/ConfigKeys.TestLeftovers.cs`; CHANGED `Docker/DockerCommands.cs` (NetworkList, VolumeInspect, ReadVerbs),
`Docker/DockerInventory.cs` (NetworkRow, VolumeDetail), `Docker/DockerJson.cs` (LabelsObject), `Docker/DockerCollector.cs`
(+ Networks, LeftoverVolumes), `Collectors/ProcessCollector.cs` (+ TestRunner, the patterns parameter),
`Collectors/LinuxProbe.cs:28`, `Config/Tuning.cs` (TextList), `Config/ConfigKeys.cs` (Auto.A25, All), `Config/ConfigKeys.Numbers.cs`
(NumberKeys), `Config/KeyRules.cs` (TextRule.RunnerPattern), `Config/NumberRules.cs`, `Config/default.json`,
`Processes/Policy/SlotKind.cs` (LeftoverName), `Processes/Policy/ReadCommandTemplates.cs`, `Processes/Policy/NeverList.cs`,
`Actions/DockerCleanups/DockerCleanupCommands.cs` (templates, phrases, LeftoverSweepCommands), `Actions/DockerCleanups/DockerLook.cs`
(nothing if the collector carries the new parts), `Actions/ActionRegistry.cs:25` (+ `new TestLeftovers.TestLeftoverSweep()`),
`Actions/ActionId.cs:63` (ExecutionOrder), `Preview/CleanupPreview.cs` (A25Row, Build), `Json/WslCareJsonContext.cs` (the new
records if serialised). Tests: §6's files; `tests/WslCare.TestSupport/HostileInputs.cs` (NeverCommands, Valid for the slot),
`tests/WslCare.TestSupport/DockerFixture.cs` (`Answers` += `(DockerCommands.NetworkList, "network-ls.out")`),
`tests/fixtures/docker/ubuntu-2026-10-02/network-ls.out` — CAPTURED by hand in WSL with EXACTLY the product's argv
(`docker network ls --no-trunc --format '{{json .}}'`), passed through `research/diagnostics/docker_fixture_redact.mjs`
(it maps network names), `SOURCE.txt` amended: "network-ls.out captured 2026-10-xx, one command, same machine, after the
2026-10-02 state — the only later file"; never a hand-written fixture. Contracts (regenerated on the Linux leg: a fresh
clone in WSL, `WSL_CARE_WRITE_GOLDENS=1 ./WslCare.Scenarios --filter-class "*GoldenContractTests"` and
`--filter-class "*ContractFilesTests"`, the diff reviewed as a contract change): `contracts/actions.json`,
`contracts/config-keys.json`, `contracts/golden/head/preview.json`, `contracts/golden/head/act-a25-preview-action.json` (new).
Extension: `src_vs_code/src/cleanup/rowIds.ts`, `root/rootIds.ts`, `cleanup/modalText.ts`, `client/worstCases.ts`, the five
tests of §5.4. Docs: README (the actions table row for A25 beside `:572`; the `testLeftovers.*` keys; a "test leftovers"
section with the four signals, what a snapshot cannot do, the residue, the false-positive direction of `dotnet test`),
`research/module_daemon.md` (a DH-6 section: the judge, the sweep, the templates, Mermaid sequence preview → batch
re-judgement → stop → rm), `research/module_tests.md` (the flows, the fixture file, `LeftoverWorld`),
`research/module_vs_code.md` (row, second modal), `research/architecture.md` (the judge as the cross-side contract DH-7
consumes), and the parent plan's §5 DH-6 row status.

##### 9. What the coder must NOT do

- Never `-f`/`--force` on any removal, never `docker kill`, never `prune` of anything; never `docker ps --filter name=`;
  never `Contains`/`IndexOf` for the prefix; never widen the prefix or add `test*`; never match the underscore form.
- Never match a runner on `ProcessEntry.CommandLine`, `Programs`, `Script` or any joined string; only the raw argv at
  collection (WSL) or the split command line (Windows); never a regex over the command line.
- Never make a `Reading.Unavailable` anything but `Unknown`; never let `NotObserved` come from anywhere but `OtherSide`;
  never let a `null`/empty signal list read `None`.
- Never remove a volume by a name the preview did not fingerprint, never skip the per-volume re-inspect, never batch the
  re-inspect (one name per inspect, so "gone" is gone and not a failed batch).
- Never touch `DockerCommands.InspectTemplate` (DH-2's), never widen `DockerCleanupCommands.Reads`, never put a write
  verb in `ReadVerbs`, never add a slot kind that accepts a leading `-` or a control character, never make `LeftoverName`
  a `Prefixed`.
- Never read the process table through anything but `ActionContext.Processes` (WSL) / `IWindowsProcessTable` (Windows);
  never start `ps`, `tasklist` or PowerShell for it; never kill or signal any process — the sweep stops CONTAINERS by id.
- Never run a real `docker` from a test; never weaken `DockerCollectorTests`, `CommandPolicyTests`, the oracle or the
  property world to go green; never hand-write `network-ls.out`; never edit goldens by hand.
- Never a `Task.Run`, an unbounded wait or a command without a ceiling (reliability.md); every number a key (§4).
- Never change A4–A7's selections, templates or tests; never re-use A4's `VolumeRemove` for a named volume.
- Never add a Windows-side action or a panel flow (DH-7's); never let A25's `Sides` include Windows.

##### 10. Decisions taken here (each a deviation or a choice the parent left open), and why

1. **The `preview --all` row carries no in-flight refusal** — `CleanupPreviews.Build` has a Docker snapshot and no process
   table; the judgement belongs to `act A25 --preview` and every batch, where it is enforced (`RefusalStop`). The row's
   `what` says so. Alternative rejected: wiring processes into every `preview --all`/`collect` for one row.
2. **A sweep that a batch's re-judgement blocks ends as `failed` with the refusal as `Failure`**, the untouched targets
   `not attempted: …`. `ActionRun` has no "refused mid-way" status; `Interrupted` means a signal cut it; a run that stopped
   short of what its preview promised should be red in the history, with the reason. Alternative: `Succeeded` + notes — hides it.
3. **One `docker stop` per container**, slot not repeat: the CLI's parallel stop would make one batch's wall time hard to
   bound; one stop per command keeps `docker.removeTimeoutSeconds` the ceiling of one `--time`, and `NumberRules` couples the two.
4. **The per-batch re-judgement reads `docker ps -a`, `network ls`, `volume ls` (+ inspect of new matched names), not
   `system df -v`** — a listing costs a second; the disk-usage walk costs up to two minutes per batch. Both parse to the same
   `ContainerRow`.
5. **A Windows command line that cannot be read is judged by image name and counted**, not `Unknown`: on the owner's
   machine every protected/system process would otherwise block every sweep forever, the exact failure mode the parent
   rejected for Testcontainers; a `List()` failure still blocks. DH-7 may tighten it (the count is in the reading).
6. **Pattern words match as an ordered SUBSEQUENCE of whole tokens** (not fixed positions): `dotnet exec … testhost.dll`
   and `node … npm-cli.js test` need it; the cost is `dotnet build test` blocking a sweep — the safe direction, documented.
7. **Labels for the Testcontainers signal come from `system df -v` / `ps -a` rows**, so DH-6 adds nothing to the inspect
   template; the Ryuk rule is pinned to Testcontainers' documented `org.testcontainers.ryuk=true` and `testcontainers/ryuk`
   image because no Ryuk ran on this machine on 2026-10-15 to capture from (the scout's reading).
8. **`NotObserved` is a fourth state**, not a `Clear` with a note: the preview must SHOW the other side as unread, and a
   `Clear` would read as evidence. §1.4 pins that no reading produces it.
9. **`A25` is first in `ExecutionOrder`** so A4/A5 read lists the sweep has already cleaned.
10. **The volume fingerprint rides in `ActionItem.Key`** (`name|created|labels`), the precedent being A5's holder list in
    the key; nothing is written to disk between preview and run.
11. **`testLeftovers.minAgeMinutes` has `Min = 60`** — the floor as the key's own bound, not a `NumberRule`, so no layer,
    not even the machine layer, can take it below the rule's hour.
12. **A labelled RUNNING container with an unreadable age counts as young** (blocks); an unaged CANDIDATE is merely never
    selected — the one is a signal about a possibly live session, the other one resource's missing fact.
13. **Runner matching covers `ProcessSnapshot.All` (counted processes; container members excluded as the snapshot already
    excludes them; zombies excluded)** — a runner inside a container is that container's run, protected by the age floor.
14. **`network-ls.out` is a later single-command capture added to the 2026-10-02 fixture** rather than a full recapture
    (which would change every golden) or a synthetic file (forbidden): named in `SOURCE.txt`.
15. **`Tuning` gains `TextList`** so the collector reads the patterns through the one tuning seam instead of a second config parse.
16. **Running leftovers are listed, not stopped, until DH-7** (§0a.1) — the reviewer's proposal, taken, with the gate made a
    key (`testLeftovers.stopRunning`, `Off` trust, default false) rather than code DH-7 must rewrite: DH-7 flips a default
    and the owner may flip it earlier on a machine he knows. The parent's 24 h rule is kept as the second gate so that
    turning the switch on never stops a run younger than a day.
17. **The Ryuk signature is image-first and marked unmeasured** (§0a.2); the young-labelled rule is the measured-independent
    backstop for every session younger than the floor. M4 stays owed and is the acceptance item that pins the label.
18. **The build-order note "after DH-2" is not a dependency** (§0a.3): the two PRs touch the same files; whichever merges
    second rebases. Nothing in DH-6 reads the inspect template or DH-2's fixture.

## 4a. Plan round (coai, 2026-10-15) — binding on §4 where they differ

Round for the gate's epic 3/3: **proceed** (6 gating against 6). Seven findings; three accepted, four rejected with reasons.

**Accepted:**

1. **`testLeftovers.stopRunning` stays opt-in for good** (finding 1: a default flipped by DH-7 would also let a distro-only
   `act A25` — CLI or timer — stop containers with `runner.windows` still `NotObserved`). DH-7 does NOT flip the default; §0a.1's
   and decision 16's "DH-7 flips the default" are struck. The key stays `BoolKey`, default `false`, trust `Off`, so only the
   machine layer can turn it on — the owner's statement about one machine. DH-7's both-sides flow lists running leftovers
   with both sides' readings and still stops none unless that key is on.
2. **Age is not evidence that a run ended** (finding 6). Even with `stopRunning` on, a running container that carries the
   `org.testcontainers` label is NEVER stopped (selection rule 4b, before rule 5: `RunningTestcontainers`, "running, owned by a
   Testcontainers session: its reaper removes it"); only an unlabelled running `testsToDelete-` container older than
   `stopRunningOlderThanHours`, on a side whose runner signal reads none, may be stopped. RED:
   `A_running_testcontainers_leftover_is_never_stopped_even_with_stopRunning_on`. Break-it: rule 4b removed.
3. **Ryuk is measured before the story merges** (finding 3). M-DH6-1: in a `testsToDelete-wsl-care-ryuk-<guid>` scratch
   folder, a minimal `testcontainers-dotnet` session starts one container; while it runs, `docker ps -a --no-trunc --format
   '{{json .}}'` is captured with exactly the product's argv and passed through the redaction script; the Ryuk row's image
   and labels pin the rule and become a fixture line for `A_running_ryuk_blocks` (the synthetic rows stay as the
   registry-prefix and fork theory rows). Recorded in `research/2026-10-15_disk_hygiene_measurements.md`. If the session
   cannot run on this machine (no network, no Docker), the story says so and ships with the rule marked unmeasured — the
   young-labelled backstop (§0a.2) stands either way.

**Rejected (reasons recorded in the gate):**

- Finding 0 (`runner.windows` `NotObserved` lets the distro sweep a live Windows test): what the distro may remove without
  the Windows reading is, by §0a.1 and item 1 above, only STOPPED containers, volumes and networks older than the hour floor;
  Docker itself refuses a volume referenced by any container (running or stopped) and a network with endpoints; nothing
  running is stopped. That residue is exactly what the `testsToDelete-` convention declares disposable.
- Finding 2 (fingerprint precision): a recreated volume's `CreatedAt` is the recreation time, at least the hour floor after
  the previewed one, so the fingerprint cannot match; and `docker volume rm` refuses any volume a container references.
- Finding 4 (partial report): §2.5 already records every confirmed removal in `Removed`, every other target in
  `NotRemoved` with its reason, and one notes line per phase; a blocked run is `failed` WITH those lists.
- Finding 5 (inspect-to-rm window on a name): Docker removes volumes only by name; no lock can be shared with arbitrary
  third-party test frameworks; the window is milliseconds against a name whose convention says "delete me", Docker still
  refuses an in-use volume, and §2.7 names the residue verbatim in the preview and README.

## 4b. Risk consultations for stories 3.3 and 3.4 (2026-10-15) — binding on §3, §4 and §4a where they differ

Both gate-ordered consultations ran (`25402b8a` for 3.3, `4e0a8210` for 3.4); each point was checked against the code.

**DH-6 (3.3):**

1. **An unreadable process is not "no runner".** Verified: `ProcessCollector.cs:193` turns a failed `cmdline` read into `[]`, and
   `:125` drops a process whose `status` failed. The runner reading carries both counts: a process whose `status` is readable
   but whose `cmdline` is not, or whose `status` failed with anything but "does not exist" (ENOENT/ESRCH = vanished, the
   normal race), makes `runner.wsl` **Unknown** (`act` runs as root, so this is rare and it blocks — the safe direction). RED:
   `An_access_denied_cmdline_makes_the_runner_signal_unknown`, `A_vanished_process_is_not_unknown`.
2. **Runners inside containers count.** Decision 13 is struck: a test runner in a dev container drives Docker through the
   socket like any other, so the match covers EVERY user process of the snapshot, counted container members included (zombies
   still excluded). RED: `A_runner_inside_a_counted_container_blocks`.
3. **What the distro cannot see is said, not implied.** A fifth signal `runner.otherDistros`, `NotObserved` by design: "other
   WSL distros sharing this Docker daemon are not read from this distro" — always listed in the basis beside `runner.windows`.
   The guarantee is stated as what it is: **a snapshot of this distro's processes, repeated before every batch, plus the hour
   floor**; README says a test that runs in another distro or on Windows is protected only by the floor and by Docker's own
   refusals until DH-7, and that a long test reusing an OLD stopped `testsToDelete-` container across phases is the named
   residue (the convention says such a container is disposable once stopped for an hour).
4. **More launchers by default, and a test per shape.** `testLeftovers.runnerPatterns` gains `"pnpm test"`, `"pnpm run test"`,
   `"yarn test"`, `"bun test"`, `"go test"`, `"pytest"` (the program) and `"python pytest"` (`python -m pytest` as a
   subsequence); a table test over those exact argv arrays plus `npx vitest` (matched through its `node … vitest.mjs` child)
   and `dotnet vstest` (through `testhost`); an MTP test exe with an arbitrary name is added by the machine layer (the key is
   an open list) — README says so. The ≤ 32-member cap stays.
5. **A25 removes containers WITHOUT `-v`.** `rm -v` would delete anonymous volumes no A25 check ever saw (prefix, fingerprint,
   age). A25 gets its own template `docker-rm-leftover` = `docker rm <64-hex>…` (never `-v`, never `-f`); the anonymous volumes
   it leaves become dangling and are A4's, under A4's own rules. `ContainerRemove` (A5's `rm -v`) is untouched. Break-it:
   `-v` added → `A25_never_removes_an_anonymous_volume` red. §4 2.5's pseudo-code and §3.3's template list read accordingly.

**DH-7 (3.4):**

6. **DH-4's extraction is a PREREQUISITE, not an alternative.** §3's "WR-T1 OR E20's DH-4" is struck: A26 is built only on the
   handle-based `ITreeRemoval` with the verify-pass manifest and the no-share-delete opens (E20 §4b items 2–4). Verified in
   WR-T1: its folder removal reaches `Directory.Delete(path, recursive: true)` (`PhysicalFileSystem.cs:624-625`) after the
   walk, so a child created after enumeration would go unchecked — reported to WR-T1's review (its fix uses
   `DeleteEmptyDirectory`, `:627-628`). A26 keeps A23's stricter link rule: ANY reparse point in a candidate retains it.
7. **A26 is all-or-nothing per candidate.** A test folder half-deleted is worse than one kept: the verify pass probes every
   file with the delete pass's own open (DELETE, share READ|WRITE, never DELETE); any sharing violation or denied open retains
   the WHOLE candidate before its first delete ("in use: <first name>"). What appears between the probe and the delete is kept
   by the manifest rule (E20 §4b item 3). RED: `A_test_folder_with_one_file_in_use_is_kept_whole`.
8. **The Windows runner signal refuses the rest of the sweep, re-read before EVERY candidate** (no runner-to-folder
   association exists, so no per-candidate exemption). On Windows, an unreadable command line of a process of the SAME user
   whose image is a launcher (`dotnet`, `node`, `npm`, `pnpm`, `yarn`, `bun`, `go`, `python`, `pwsh`, `powershell`)
   reads **Unknown**; other accounts' processes stay judged by image name (DH-6 decision 5) — only the user's own processes
   write into the user's `%TEMP%`.
9. **The panel's conjunction is re-read at the start of each side's run**, not trusted from the two previews: the Windows
   task's run judges both its own side and (through E19.S2b's status read of the distro) refuses when the distro's last
   judgement is older than the run's start; A25's batches keep re-judging the distro. The panel's sentence says
   "while no test run was seen on either side, re-checked before each batch" — a snapshot guarantee, worded as one.

## 5. Order

E21.S1 → E21.S3 (they share FILES — the Docker collectors, the preview, the goldens — not data: A25 reads the container LIST
rows, `DockerInventory.cs:54-69`, never the inspect template, so DH-6 is not held behind DH-2; whichever merges second rebases); E21.S2 any time; E21.S4 after WR-T1 AND E20.S1 (DH-4's extraction, §4b item 6);
E21.S5 last. E21.S1–S3 run beside E19 (different files, apart from contracts regenerated on rebase).

## 6. Gates

One coai plan round for this document (the gate's epic `3/3` — the gate numbers the parent's three epics 1-based: E19 = 1, E20 = 2, E21 = 3); a code round per story (each story's branch opens its own
session with a short plan round of its section, then `review_code`); the parent's `review_feature` after the last epic.

## 7. Definition of Done

- [ ] E21.S1–S3 merged with RED tests watched failing, break-it run, coai code rounds resolved, full suites green.
- [ ] E21.S4 merged when its dependency lands; E21 done only then (the parent's rule).
- [ ] M4 recorded in `research/`; contracts and goldens regenerated, never hand-edited.
- [ ] Docs per story; `plan-lifecycle` clean; `todo/README.md` row.
