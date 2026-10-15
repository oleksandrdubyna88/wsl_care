# PLAN — E21 "Test leftovers and A5": A5's compose exemption, this repo's test prefix, the `testsToDelete-` sweep (Docker and `%TEMP%`)

> Status: **plan only, nothing implemented yet, 2026-10-15.** Scope: the stories of epic E21 of
> [PLAN_disk_hygiene_windows.md](PLAN_disk_hygiene_windows.md) (DH-2, DH-8, DH-6, DH-7, DH-9). The owner's yes for E20 and
> E21 came on 2026-10-15. Written by the Opus judge (epic → stories); the cornerstone **DH-6** (`model: fable`, the parent's
> ledger `Cornerstones: 2 of 2 — DH-4, DH-6`) has its scenarios and brief written by Fable (§4 DH-6).
>
> Parent: [PLAN_disk_hygiene_windows.md](PLAN_disk_hygiene_windows.md) § *Epics* — E21. Sibling:
> [PLAN_disk_hygiene_windows_E19.md](PLAN_disk_hygiene_windows_E19.md). Runner plan: [PLAN_windows_runner.md](PLAN_windows_runner.md)
> (WR-T1 — the `%TEMP%` engine DH-7 builds on — not built).

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

### E21.S2 = DH-8 — this repository's test leftovers take the prefix

- **Brief.** `TempRoot` names `testsToDelete-wsl-care-{purpose}-{guid32}`; its leftover regex takes BOTH shapes, exact case
  (`^(testsToDelete-wsl-care|wsl-care-test)-.+-[0-9a-f]{32}# PLAN — E21 "Test leftovers and A5": A5's compose exemption, this repo's test prefix, the `testsToDelete-` sweep (Docker and `%TEMP%`)

> Status: **plan only, nothing implemented yet, 2026-10-15.** Scope: the stories of epic E21 of
> [PLAN_disk_hygiene_windows.md](PLAN_disk_hygiene_windows.md) (DH-2, DH-8, DH-6, DH-7, DH-9). The owner's yes for E20 and
> E21 came on 2026-10-15. Written by the Opus judge (epic → stories); the cornerstone **DH-6** (`model: fable`, the parent's
> ledger `Cornerstones: 2 of 2 — DH-4, DH-6`) has its scenarios and brief written by Fable (§4 DH-6).
>
> Parent: [PLAN_disk_hygiene_windows.md](PLAN_disk_hygiene_windows.md) § *Epics* — E21. Sibling:
> [PLAN_disk_hygiene_windows_E19.md](PLAN_disk_hygiene_windows_E19.md). Runner plan: [PLAN_windows_runner.md](PLAN_windows_runner.md)
> (WR-T1 — the `%TEMP%` engine DH-7 builds on — not built).

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

### E21.S2 = DH-8 — this repository's test leftovers take the prefix

 — TempRoot only sweeps folders it made, so no case folding),
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

- **Depends on:** WR-T1 (A23, the engine's first Windows action and its `%TEMP%` walk — the runner plan) OR E20's DH-4 if it
  lands first and extracts the shared Windows engine path (the parent's §6: never two copies); E19.S2b (the Windows status
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

(Inserted from Fable's spec in the same commit.)

## 5. Order

E21.S1 → E21.S3 (they share FILES — the Docker collectors, the preview, the goldens — not data: A25 reads the container LIST
rows, `DockerInventory.cs:54-69`, never the inspect template, so DH-6 is not held behind DH-2; whichever merges second rebases); E21.S2 any time; E21.S4 after WR-T1 or E20's extraction;
E21.S5 last. E21.S1–S3 run beside E19 (different files, apart from contracts regenerated on rebase).

## 6. Gates

One coai plan round for this document (the gate's epic `3/3` — the gate numbers the parent's three epics 1-based: E19 = 1, E20 = 2, E21 = 3); a code round per story (each story's branch opens its own
session with a short plan round of its section, then `review_code`); the parent's `review_feature` after the last epic.

## 7. Definition of Done

- [ ] E21.S1–S3 merged with RED tests watched failing, break-it run, coai code rounds resolved, full suites green.
- [ ] E21.S4 merged when its dependency lands; E21 done only then (the parent's rule).
- [ ] M4 recorded in `research/`; contracts and goldens regenerated, never hand-edited.
- [ ] Docs per story; `plan-lifecycle` clean; `todo/README.md` row.
