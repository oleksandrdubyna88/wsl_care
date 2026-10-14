# PLAN — Windows disk hygiene: a verdict for every lettered volume, the AI-agent worktree report, a build-output cleanup, the `testsToDelete-` sweep, and A5's compose exemption

> Status: **plan only, nothing implemented yet, 2026-10-14.** Scope: the daemon's disk verdicts (`Thresholds/`), a
> Windows-side worktree scanner and two Windows-side cleanup actions (`Core/Worktrees/` new, `Core/Actions/`), the Docker
> cleanup's selection (`Preview/CleanupTargets.cs`), a test-leftover sweep on both sides, this repository's own test-temp
> prefix, and the extension's panel rows and buttons for all of it. The split into epics is a DRAFT (see `## Epics`, the
> last section): it is Fable's to make.
>
> Related: [PLAN_disk_health.md](PLAN_disk_health.md) (E15–E18), [PLAN_windows_runner.md](PLAN_windows_runner.md) (WR-T1–T3,
> A23, the tasks and the supervisor), [PLAN_windows_care.md](PLAN_windows_care.md) (W-A2), [PLAN_wsl_care_daemon.md](PLAN_wsl_care_daemon.md)
> (A5), [module_daemon.md](../research/module_daemon.md), [module_tests.md](../research/module_tests.md),
> [architecture.md](../research/architecture.md). Shared rule: `.agents/conventions/common/test-leftovers.md` (conventions
> `release` e29883f; NOT yet in this repo's pin 12f7850 — its text is carried over in §3.6 so nothing here depends on the bump).

## 1. Symptom (2026-10-14, measured)

The Windows volume D: (195.3 GiB, ReFS, the repositories; research/2026-10-10_disk.md D1) reached **0 bytes free**. `git`
could not fetch and agents' builds failed. The product said nothing.

| What | Measured | What the product says today |
|---|---|---|
| D: at 0 bytes free | owner, 2026-10-14; 2026-10-10 it was already 84.3 % used, 30.6 GiB free (D1) | nothing: `disk.hostFree` judges only a volume that hosts a VHDX (`Thresholds/DiskRules.cs:27`, `:36`; `Health/VirtualDisks.cs:141-151`); every lettered volume's free space is READ (`Health/WindowsDiskCommands.cs:139`, `Health/WindowsDiskSample.cs:17`) and judged by no rule |
| `D:\rsd\_wt` held 372 worktrees of AI-agent sessions, ≈170–180 GB | owner's count, 2026-10-14; 183 unchanged > 3 days (≈100 GB) | nothing: the only worktree measurement is the WSL folder `~/git/_wt` (`Folders/FolderSizes.cs:57`), Linux only (`Collect/CollectRun.cs:411`) |
| Most of it regenerable build output | the one-off clean (the owner's yes, 2026-10-14) deleted **762** folders, **45.0 GB**: `bin` 354 (41.7 GB), `obj` 354 (0.5 GB), `node_modules` 54 (2.1 GB), in 44 worktree folders | nothing |
| **≈44 GB of those 45 GB sat in 24 ORPHANED worktree folders** | 37 orphans in all (their `.git` file points at metadata that is gone, most likely a `git worktree prune` run from WSL); per-line sizes are rounded to 0.01 GB, so the split is ±4 GB | nothing |
| Whole-tree removal mostly refused | of 134 old clean candidates: 73 locked, 39 refused (`git worktree remove` without `--force` refuses a worktree holding a submodule), 5 detached, 3 main working trees, 1 holding an ignored `.env`; 13 removed (0.1 GB) | — |
| After the clean, re-measured (2026-10-14 21:3x, this plan's author, read-only) | `D:\rsd\_wt`: 311 top-level folders, **1 242 710 files, 65.4 GB** of file length by an enumeration that skips reparse points; files under a `node_modules`/`bin`/`obj` segment: 615 795, **54.0 GB** — the build output still there lives in worktrees the one-off did not select (recent, locked, or with a submodule). The walk took **97 s** at below-normal priority. D: 81 GiB free. | — |
| ≈90 leftover test containers in Docker | owner, 2026-10-14 | A5Testcontainers takes stopped `org.testcontainers` ones; nothing knows the `testsToDelete-` prefix (zero hits in the repo) |

The 65.4 GB re-measure and the 170–180 GB count disagree by more than the 45 GB freed; the count was taken before the
clean by a method this plan did not see (size on disk, or a walk that entered links, would both read higher). Measurement
M1 (§7) reconciles them on the product's own walker before any default is set from either.

## 2. Goal — what must be true when this plan is done

1. **Seen.** A verdict warns, then goes critical, when ANY lettered Windows volume runs low — absolute GiB and percent, the
   volume named — from the data the Windows disk report already reads. A critical one colours the status bar.
2. **Explained.** A Windows-side report says, per configured root: how many worktrees, their size, how many are idle more
   than N days, how much build output they hold, and which folders are orphaned — every git fact read without changing a
   repository.
3. **Fixable by a button.** *Clean build output in idle worktrees…* removes `bin`/`obj` next to a project file and
   top-most `node_modules` next to a `package.json`, in worktrees idle more than N days — with a dry-run preview, never
   following a link, never leaving the roots, never touching a source or an ignored non-build file, skipping locked
   worktrees unless a setting says otherwise. Orphans have their own button, because git can no longer vouch for them.
4. **Test leftovers swept.** *Sweep test leftovers…* removes `testsToDelete-…` containers, volumes, networks and Windows
   `%TEMP%` folders older than an hour, by the shared rule's anchored case-insensitive match, only when no test run is in
   flight — and the preview says how that was decided.
5. **A5 safe to automate.** A5 never selects a container of a `docker compose` project, before `auto.A5` may be turned on.
6. **Every number a setting, every action a panel button, nothing deleted without the owner's choice.** Every new action's
   `auto.*` key defaults to OFF and its trust is `Off` (the user layer cannot loosen it, `Config/ConfigKeys.cs:46-47`).

## 3. Design

### 3.1 Which side does what

| Part | Side | Why |
|---|---|---|
| `disk.volumeFree` (the volume verdict) | the distro's full run (`collect`, every `timer.periodHours` = 4 h, `Config/ConfigKeys.Timer.cs:14`) | the facts are already in `HealthSample.WindowsDisk`; no new command, no new script — PowerShell's closed set (`HealthCommands.FixedScripts`) stays two scripts |
| the worktree report, A24, A24Orphans | the **Windows binary** | Windows-created worktrees write Windows paths into their `gitdir:` pointers, which Linux git cannot resolve — exactly how a WSL-side prune made the 37 orphans (the never-list's `git-worktree-prune`, `Processes/Policy/NeverList.cs:44`). Walking 1.2 M files over the drvfs mount is the slow road; NTFS/ReFS reparse points are first-class only on the Windows side. The consultant (coai `d2330f7e`) chose the same and it is verified against `NeverList.cs:44` and `Scheduler/WindowsTaskDefinitions.cs` |
| A25 (Docker part of the sweep) | the distro | the Docker collectors and removal templates live there (`Docker/DockerCommands.cs:81`, `Actions/DockerCleanups/DockerCleanupCommands.cs`) |
| A26 (`%TEMP%` part of the sweep) | the Windows binary | it reuses WR-T1's `%TEMP%` engine (the `claude` guard, owner SID, reparse refusal, re-validated delete) |

A Windows-side action is run by the person's own scheduled task through the task's `Run`, under the WR-D3 supervisor —
the pattern WR-T3 builds for *Clean Windows TEMP now* (PLAN_windows_runner.md §3.12, "In the panel"). The extension never
spells `act` or `--confirm` towards the Windows binary (PLAN_windows_runner.md §4). Its read-only previews go through
`WindowsCareClient` (`src_vs_code/src/client/WindowsCareClient.ts:25`, widened by WR-E1).

### 3.2 `disk.volumeFree` — every lettered volume (DH-1)

- **Input:** `WindowsDiskFacts.Volumes` — `WindowsVolume(Letter, DiskNumber, FileSystem, SizeBytes, FreeBytes, Label)`
  (`Health/WindowsDiskSample.cs:17`), already read by `Get-Volume` (`Health/WindowsDiskCommands.cs:139`). A pure rule in
  `Thresholds/DiskRules.cs`, spread into `ThresholdRules.FromFullRun` with the other `disk.*` verdicts, so `status` carries
  it with `basis {fullRun, runId, ageSeconds}` exactly like `disk.hostFree`.
- **Levels — OR, per volume, critical first** (the consultant's answer and this author's, independently): a volume is
  `critical` when free < `disk.volumeFreeCriticalGib` **or** free % < `disk.volumeFreeCriticalPercent`; `warn` when
  free < `disk.volumeFreeWarnGib` **or** free % < `disk.volumeFreeWarnPercent`; else `ok`. AND would let the 195 GiB D: run
  to ≈19.5 GiB before warning; OR warns there at 30 GiB. One verdict over every judged volume, the worst level, the value
  naming the WORST volume first and every breaching one after it (`"D: 4.1 GiB free (2.1 %) — critical; C: ok"`), the
  limit `"warn < 30 GiB or < 10 %, critical < 10 GiB or < 3 %"` (the shape of `memory.available`,
  `Thresholds/ThresholdRules.cs:156-168`).
- **Starting defaults** (STARTING VALUES, said so in README and the key descriptions, as E15.S2's are): warn 30 GiB / 10 %,
  critical 10 GiB / 3 %. Basis: D: was 30.6 GiB free four days before it hit 0 (D1); a coai worktree's build output is
  ≈1.7–2.4 GB (the one-off's log), so 10 GiB is four or five builds. On the 2.4 TiB C: the 10 % floor warns at ≈245 GiB —
  acceptable for a warn, and it is a key.
- **Which volumes:** every lettered volume whose size ≥ `disk.volumeFreeMinSizeGib` (8) and whose letter is not in
  `disk.volumeFreeIgnoreLetters` (`[]`). The size floor exists for the measured E: — the keyboard's USB FAT volume (D1) —
  so a full device volume never warns; measurement M5 records its size before the floor is fixed.
- **Unknown, never ok,** when the sample is unavailable or a volume's size/free is unread (the reading's own reason), as
  `disk.hostFree` does.
- **Relation to `disk.hostFree`:** both stay. `disk.hostFree` asks "can the VHDX files grow here"; `disk.volumeFree` asks
  "is this volume running out". C: can breach both; they say different things.
- **The status bar:** `RELEVANT_VERDICT_PREFIXES` is `memory.`, `kernel.`, `pressure.` (`src_vs_code/src/statusBar/statusBarModel.ts:24`)
  and a test holds the three E15.S2 disk verdicts out of the bar (`src_vs_code/src/test/statusBar.test.ts:61`). DH-1 adds
  `disk.volumeFree` at **critical only** (`warn` stays in the panel): a volume at 0 bytes is what the bar is for. Q2.
- **Cadence:** the full run, 4-hourly. A faster reading is DH-1b (the Windows watch, every 5 min, reads `GetDiskFreeSpaceEx`
  natively once WR-D4 exists) — a follow-up, not a precondition.

### 3.3 The worktree report (DH-3) — read-only, no git process

**Roots.** `worktrees.roots` — a list of absolute local paths, default **`[]`** (no hard-coded user path; with no root the
report says "no root configured — `wsl-care.exe config set worktrees.roots …`", the way `temp.allowGlobs` is widened,
PLAN_windows_runner.md §3.12). A root is refused, with its reason, when it is: not absolute; not on a local FIXED drive (the
`[IO.DriveInfo]` rule of E15.S1, `research/module_daemon.md:315-317`); a drive root; a reparse point or under one; the user
profile itself or an ancestor of it; inside or an ancestor of any `DeletionPolicy` agent root or Claude temp root
(`Files/Deletion/DeletionPolicy.cs:96-100`). At most `worktrees.maxRoots` (8).

**What each direct child folder of a root is** — answered by FILE READS only (bounded, no-link, `Files/BeneathFiles.cs:102`
style), never by starting git:

| kind | how it is known |
|---|---|
| `worktree` | a `.git` FILE whose `gitdir: <path>` names an existing admin folder (`<common>/worktrees/<name>`) whose own `gitdir` file points back to this folder (case-insensitive) |
| `locked` (a flag on a worktree) | the admin folder holds a `locked` file — how git stores `git worktree lock`; its text (the reason) is shown, capped |
| `orphan` | a `.git` FILE whose `gitdir:` target does not exist, or whose admin folder points elsewhere |
| `mainClone` | a `.git` FOLDER (3 such in the one-off) — reported, never a cleanup candidate |
| `notGit` | no `.git` at all — reported as "not a git folder", never a candidate |
| `testLeftover` (a flag) | the folder's own name matches the §3.6 prefix — reported as a count; NOT swept by this plan (§9) |

**Idle.** A folder's idle age = now − the NEWEST of: every descendant's write time found by the walk (files AND folders,
the WR-T1 rule), and for a worktree its admin folder's `HEAD`, `index` and `logs/HEAD` (git touches `index` on every
status/add/checkout, which agents run constantly). A folder whose walk did not finish within budget is `unknown — not idle`.
This is conservative by design: a build today, or a `git status` today, makes the worktree active. Measurement M2 compares it
with the admin-files-only signal over the owner's tree; if the cheap signal agrees on every folder it may become the default.

**Build output** (the selection rule A24 deletes, measured in the report):

- a folder named `bin` or `obj` whose PARENT holds a file whose extension is in `worktrees.projectFileExtensions` (default
  `[".csproj", ".fsproj", ".vbproj", ".esproj", ".sqlproj", ".vcxproj"]` — a closed list, where the one-off's `*.*proj`
  glob would also have matched any `.proj`);
- a folder named `node_modules` whose parent holds a `package.json`, TOP-MOST only (the walk never descends into it to find
  another) — the one-off took any `node_modules`; this rule is narrower on purpose;
- never `.git` (folder or file), never anything under one; never a reparse point, never anything under one.

**The walk.** `TreeWalk` (`Files/TreeWalk.cs:105-110`: `RecurseSubdirectories`, `IgnoreInaccessible`,
`AttributesToSkip = ReparsePoint`) through `IFileSystem.WalkTree` with `TreeRules.NeverEnter` for `.git`, budgets
`worktrees.scanMaxEntries` (5 000 000) and `worktrees.scanMaxSeconds` (900) for the WHOLE scan and per root a fair share, a
cancellation check every 1024 entries (`TreeWalk.cs:43`). M1's 1.24 M files in 97 s is the basis (×4 headroom); a budget
that runs out makes the remaining folders `unknown`, never `idle`. Sizes are "logical bytes" (file length), never "disk
freed" — ReFS block cloning and hard links (pnpm) make the two differ.

**The report** — `worktrees scan --json` (new verb, Windows binary only; the distro's binary refuses it with the side's
reason, as `act` does, `Cli/Commands/ActCommand.cs:116-122`) writes `%LOCALAPPDATA%\wsl-care\worktrees.json` atomically
(`{schemaVersion, scannedAt, elapsed, complete, roots[{path, refused?, folders, worktrees, locked, orphans, mainClones, notGit,
testLeftovers, sizeBytes, idleOver{days, count, sizeBytes}, buildOutput{all, inIdleUnlocked, inIdleLocked, inOrphans}
{count, bytes}, unknown{count, why}}], top[]}` — `top` the `worktrees.reportTop` (20) largest reclaimable folders by name,
never their contents). The Windows binary's `status --json` carries it as `worktrees` with `basis {source: scan, scannedAt,
ageSeconds}` and one verdict, **`worktrees.reclaimable`**: warn when the build output in idle unlocked worktrees PLUS in
orphans exceeds `worktrees.reclaimableWarnGib` (20); `unknown` with no root or no scan; the value names the root and both
parts. The scan is read-only: it starts no process, writes only its own file.

### 3.4 A24 `worktreeBuildOutput` and A24Orphans (DH-4)

Two ids, one implementation, the A5/A5Testcontainers shape (`Preview/CleanupTargets.cs:77`): **A24** takes worktrees,
**A24Orphans** takes orphan folders. Both `Sides = [Windows]` (`Actions/ICleanupAction.cs:231`), both `auto.*` OFF with
trust `Off`, both a second modal. Main clones and `notGit` folders are never candidates.

**Selection (the preview), per candidate folder (§3.3 rule):** its folder is idle > `worktrees.idleDays` (3, the one-off's
cut); for A24 the worktree is unlocked, or `worktrees.cleanLocked` (false) is on; the candidate's OWN newest descendant
write is older than the idle cut too; and — A24 only — **git confirms it**:

- `git ls-files -z --cached -- <relative candidates…>` run in the candidate's parent folder (so a candidate inside a
  submodule is asked of the submodule's own repository) must list NOTHING — a tracked file inside retains the whole
  candidate ("holds tracked files");
- `git check-ignore -z --stdin` must name the candidate as ignored — an untracked, non-ignored build folder is retained
  ("not ignored by git"). Each candidate is passed WITH a trailing `/`, because a directory-only pattern (`bin/`) matches
  a path spelt as a directory; a RED test pins the spelling.
- Run read-only: `--no-optional-locks`, `-c core.fsmonitor=false` (a repository's own config can name a program as its
  fsmonitor, which `git` would start), environment `GIT_OPTIONAL_LOCKS=0`, `GIT_TERMINAL_PROMPT=0`, a clean environment
  (`CommandEnvironment`), the timeout `worktrees.gitTimeoutSeconds` (30), stdin closed, exit 0 REQUIRED (an empty answer
  with a non-zero exit is not "clean" — the neighbour `ReviewTreeKeeper` in dew_flow_connect_other_ais
  `src_mcp/runners/Worktrees/ReviewTreeKeeper.cs:298-308` learnt this in a plan round). "Dubious ownership", "not a git
  repository" and every other refusal RETAIN the candidate by its own name.
- The program: the daemon runs no git today (scout: no `git` invocation under `src_daemon/src`); DH-4 adds two declared
  `CommandTemplate`s (`Processes/Policy/CommandTemplate.cs:55`) — exactly these two argv shapes, a path slot that accepts
  only a folder under a configured root — through `ICommandRunner` (`Processes/ICommandRunner.cs:9`). `git.exe` is the
  system Git under the Program Files folder (an administrator-only folder, the reasoning of the PowerShell fallback,
  `research/module_daemon.md:340-344`), else `worktrees.gitPath` when the owner names one; nothing is taken from `PATH`.
  The never-list keeps refusing `worktree prune` and `clean` (`NeverList.cs:44`, `:149`) and gains a test that neither
  template can be bent into them.

**A24Orphans** has no git to ask (its `.git` points nowhere). It deletes by the structural rule alone, and its preview says
so in its first line: "not confirmed by git — the folder's repository metadata is gone". That is why it is its own button
and never part of A24 (consultant `d2330f7e`, answer 4, verified against `ReviewTreeKeeper.cs:298-308`, which refuses
a broken `.git` for the same reason). It is where the measured space was (≈44 of 45 GB).

**The delete — never follows a link, never leaves a root:**

- Just before each candidate: re-validate everything the preview decided (the kind, lock, idle cut, the candidate's own
  newest write, git's answer for A24) and the path itself: real path beneath the root's real path + separator; the
  candidate and every ancestor up to the root NOT a reparse point (`BeneathFiles.cs:102`'s per-component check).
- The removal is the product's own bottom-up walk: files deleted (the read-only attribute cleared first — `node_modules`
  and `.git`-like trees carry it); a directory that IS a reparse point is removed AS A LINK (`RemoveDirectory` on the link
  itself, never enumerated); long paths through the extended-length prefix. `Directory.Delete(recursive)` is not used, so
  the behaviour is the product's, tested, not the runtime's.
- `DeletionPolicy` refuses everything under the Windows `GitRoots` today (`Hosting/WindowsHostPaths.cs:89`,
  `Files/Deletion/DeletionPolicy.cs:90-91`). DH-4 adds ONE narrow rule, `DeletionRule.WorktreeBuildOutput`, admitted only
  for A24/A24Orphans and only for a path that passes the §3.3 predicate again inside the policy (name, sibling project file
  or `package.json`, beneath a configured root, no reparse point on the way). Every other path under a git root stays
  refused, and `DeletionPolicyTests` gains the cases that prove it.
- A sharing violation or partial delete is kept and counted, never retried in the same run. A partial `bin` is still
  regenerable; the run detail names it "partly removed — the next build restores it".
- Budgets: `worktrees.maxItemsPerRun` (2000 candidates; the one-off was 762) and the scan budgets. A checkpoint
  `%LOCALAPPDATA%\wsl-care\build-output-cursor.json` records the in-progress candidate with its newest FILE write and
  the run id before its first delete (WR-T1's recovery rule, PLAN_windows_runner.md §3.12): the next run takes it first,
  re-judges it by its remaining files, finishes or retains it, and the run reads "interrupted", never "completed".
- Dry first: `dryRun` and `timer.firstDryWindowDays` on the Windows paths (PLAN_windows_runner.md §3.5).

**The run record** counts per outcome (deleted / retained-by-reason / failed), per root, the deleted logical bytes and the
candidate paths (relative to their root), in the run detail under `%LOCALAPPDATA%\wsl-care`, read by the existing readers.

### 3.5 The button route on Windows (DH-5)

- *Scan AI-agent worktrees* — read-only, through `WindowsCareClient` on the staged exe (`worktrees scan --json`), its own
  timeout `wslCare.timeouts.worktreesScanSeconds` (default above `worktrees.scanMaxSeconds` + a drain, held by the
  extension's `ceilings.test.ts` pattern).
- *Clean build output in idle worktrees…* / *…in orphaned folders…* — the panel shows the preview
  (`tasks preview hygiene --action A24|A24Orphans`, read-only), two modals, then runs the person's own on-demand task
  `\wsl-care\hygiene` through its `Run`. The task has NO trigger; its body is `act <id>` with the manual trigger, spelt by
  the supervisor from a CLOSED table (D3, PLAN_windows_runner.md §3.3). Which id: the task is registered with
  `tasks run hygiene $(Arg0)` and run with one parameter that the supervisor accepts only from {`A24`, `A24Orphans`,
  `A26`}; measurement M3 proves `$(Arg0)` substitution on this machine first, and if it fails the fallback is one
  on-demand task per id (`\wsl-care\hygiene-a24`, …). Either way the run re-checks everything (A5's "re-checked at run time"
  model, `research/architecture.md:1233`), and the panel's line reads the run record ("last: <t>, N removed, M kept,
  <bytes> logical"), "running" while it runs (durable status from the run record, not a webview flag).
- Every new command has its `COMMAND_BUTTONS` entry (`src_vs_code/src/panel/commandButtons.ts:22-38`) in the same change
  (`commandButtons.test.ts`).

### 3.6 The `testsToDelete-` sweep — A25 (Docker) and A26 (`%TEMP%`) (DH-6, DH-7)

**The shared rule, carried over** (conventions `common/test-leftovers.md` at `release` e29883f): whatever a run creates
outside its process carries `testsToDelete-<what>-<short id>`; where a namespace refuses capitals, `teststodelete-`. The
sweep matches **anchored at the start of the name, case-insensitively** — here `name.StartsWith("testsToDelete-",
StringComparison.OrdinalIgnoreCase)`, NEVER `docker ps --filter name=` (a substring match: it would take
`prod-testsToDelete-cache`). The rule's underscore form `teststodelete_` is for SQL identifiers the sweep does not claim;
it is NOT matched (the rule's own `grep -i '^testsToDelete-'` does not match it either). Never widened (no `test*`), never
a variable that can be empty.

**A25 — Docker, in the distro:**

- Containers from the existing list (`Docker/DockerCommands.cs:81`); a name is matched without Docker's leading `/`, and a
  row with several names matches when any does.
- Volumes: `VolumeRow` has no created time (`Docker/DockerInventory.cs:74`); DH-6 adds a bounded `docker volume inspect
  --format` read of `CreatedAt` for the matched names only (≤ `docker.batchSize`).
- Networks: not collected today (the only network command is `network inspect bridge`, `DockerCommands.cs:90`); DH-6 adds
  `docker network ls --format {{json .}}` (name, id, created) as a READ verb.
- **The age floor: every candidate is older than `testLeftovers.minAgeMinutes` (60)** — by `Created` for a container,
  `CreatedAt` for a volume and a network. A run that started in the last hour is never touched, whatever the in-flight
  signals say.
- Removal, never `-f` (the removal templates' rule, `Actions/DockerCleanups/DockerCleanupCommands.cs:9-14`): a RUNNING
  leftover is stopped first (`docker stop --time <testLeftovers.stopSeconds> <64-hex id>`, a new declared template), then
  `docker rm -v <id>` — which Docker refuses if it was restarted in between; then `docker volume rm <name>` (refuses a volume
  in use), then `docker network rm <name>` (refuses one with endpoints). Order: containers, volumes, networks. Each removal
  re-reads the name by id just before (a recycled name never takes another object).
- A container carrying `wsl-care.keep=true` is kept, as everywhere (`DockerInventory.cs:140`).

**A26 — Windows `%TEMP%`, in the Windows binary:** top-level folders of the current user's `%TEMP%` whose own name matches,
whose newest descendant write is older than `testLeftovers.minAgeMinutes` (60) — the rule's one hour — through WR-T1's
engine: the root from the shell-folder API, the hard-coded `%TEMP%\claude\` guard checked before any traversal, the owner
SID, the reparse refusal, the re-validated delete. A23's allow-list never takes a `testsToDelete-` name (§6's boundary):
its preview lists them as "the test-leftover sweep's".

**"No test run in flight" — how it is decided** (a pure `TestRuns.Judge` in `WslCare.Core`, compiled into both binaries,
run in the preview AND again immediately before the first removal; any `inFlight` or `unknown` refuses the whole run with
the reasons, and the preview shows every signal with its reading):

| signal | side | in flight when | source |
|---|---|---|---|
| a test-runner process | each side its own | a process matches `testLeftovers.runnerPatterns` (default: an image name ending `.Tests.exe` or `.Tests`; `testhost`; a `vitest` in a node command line; `dotnet test`; `npm test` / `npm run test`) — matched on the program name and, on Windows, the command line | WSL `ProcessSnapshot.All` (`Collectors/ProcessCollector.cs:97`, `Programs`); Windows `IWindowsProcessTable.List` + `CommandLine(pid)` (`Mcp/WindowsMcp.cs:18-26`, `Mcp/Win32ProcessTable.cs:30`) |
| a running Testcontainers container | distro (A25) | any RUNNING container labelled `org.testcontainers=true` (Ryuk included — it carries the label) | the container list's labels |
| a young prefixed resource | the side being swept | any matched resource younger than `testLeftovers.minAgeMinutes` exists | the candidates' own times |
| an unreadable signal | — | `unknown` blocks: a process table that could not be read, a Docker list that failed | the reading's reason |

- **What a snapshot cannot do**, said in the preview and README: a test started after the check is not seen — the age floor
  is what protects it (its resources are younger than an hour), and the re-check just before the first removal narrows the
  window to seconds. A strict guarantee would need a lease every test launcher takes; that is a family-convention change,
  not this product's (consultant `d2330f7e`, answer 3) — follow-up F3.
- **The other side's processes.** The distro's binary reads no Windows process table (`Cli/CliHost.cs:233-234`), and the
  Windows binary none of the distro's. A Windows test run that uses Docker Desktop is therefore seen from A25 only through
  its Testcontainers containers and the age floor. The panel's *Sweep test leftovers…* previews BOTH sides and runs either
  only when BOTH previews read `inFlight: none` — a conjunction of two Core verdicts, never a threshold in the extension. A
  shell `act A25` checks its own side and says that it did.

### 3.7 A5 never selects a compose container (DH-2)

`StoppedContainers` (`Preview/CleanupTargets.cs:77-97`) keeps only `wsl-care.keep` (`:82-83`); the inspect template
captures only `org.testcontainers` and `wsl-care.keep` (`Docker/DockerCommands.cs:41-47`), and `DockerLabels` knows no
compose label (`Docker/DockerInventory.cs:137-148`). So a stopped container of a compose project — a dev stack stopped
over the weekend — is selected by A5 after 7 days, and `docker rm -v` takes its anonymous volumes (a `postgres` image's
`VOLUME` is one) with it.

- The inspect template gains `"compose":{{json (index .Config.Labels "com.docker.compose.project")}}`;
  `ContainerDetail.ComposeProject` (`Docker/ContainerDetail.cs`); `DockerLabels.ComposeProject`.
- **A5** keeps every container with a compose project, as a new kept group "a container of compose project <name>" in
  its preview (named, counted, never selected). **A5Testcontainers** is unchanged: it selects only `org.testcontainers=true`
  containers, and a Testcontainers compose module's containers carry that label on purpose.
- A25 is unaffected: a compose project named `testsToDelete-…` names its containers with the prefix, and the prefix is the
  promise.
- `auto.A5` stays OFF (`Config/default.json:10`, `ConfigSchemaTests.cs:52`). DH-2 is the precondition for ever turning it
  on; this plan does not turn it on.

### 3.8 This repository's own test leftovers take the prefix (DH-8)

`TempRoot` names folders `wsl-care-test-{purpose}-{guid}` (`src_daemon/tests/WslCare.TestSupport/TempRoot.cs:15`) and sweeps
its own leftovers older than 24 h by `^wsl-care-test-.+-[0-9a-f]{32}$` (`:36`); `WindowsIdleTests` uses
`CreateTempSubdirectory("wsl-care-idle-")` (`src_daemon/tests/WslCare.Core.Tests/Archive/WindowsIdleTests.cs:24`). DH-8
renames both to `testsToDelete-wsl-care-{purpose}-{guid}` and keeps sweeping the OLD shape for 30 days of transition (the
regex takes both; a dated comment says when the old half goes). The extension's tests get the same audit
(`mkdtemp` prefixes under `src_vs_code/src/test`). `temp.allowGlobs`' `wsl-care-test-*` (A23) stays for the existing
backlog.

## 4. Never

- Never delete under a root that failed validation, outside the roots, or through a reparse point; never enumerate into a
  link; never `Directory.Delete(recursive)` on a candidate.
- Never touch a source, a `.git` folder or file, or an ignored non-build file (`.env`, `appsettings.*.json` outside
  `bin`/`obj`, an IDE folder) — the selection is a POSITIVE rule; anything it does not name stays.
- Never run `git worktree prune`, `git worktree remove`, `git clean`, or any git command that writes; never start git from
  `PATH`; never let a repository's config start a program (`core.fsmonitor`).
- Never remove a whole worktree in this plan (§9).
- Never select by a substring or an unanchored match; never widen the prefix; never `-f` on a Docker removal; never
  `docker system prune` / `volume prune` / `network prune` (the never-list refuses the first two in any form; DH-6 adds the
  third).
- Never sweep while a signal reads `inFlight` or `unknown`; never sweep a resource younger than the age floor.
- Never anything under `%TEMP%\claude\` (the owner's 2026-10-02 rule, WR-T1's hard-coded guard).
- Never turn an `auto.*` key on by default; never write daemon config from the extension.
- Never stop a process by image name; never kill anything at all — the sweep stops CONTAINERS by id, through Docker.
- Never run a real cleanup, a real `git`, a real `docker` or a real task from a test; never weaken a tripwire, a recorder or
  a fake to make a test pass.

## 5. Stories (one pull request each)

| id | title | side | files (reuse → new) | Fable own reviewer | depends on |
|---|---|---|---|---|---|
| DH-1 | `disk.volumeFree`: every lettered volume, warn/critical, GiB OR %, the critical colouring the bar; the five `disk.volumeFree*` keys; the panel's `disk.hostC` "arrives in E7" row (`src_vs_code/src/panel/fieldMap.ts:140`) becomes the verdict row | distro + extension | `Thresholds/DiskRules.cs`, `ThresholdRules.cs` (spread), `Config/ConfigKeys.Disk.cs`, `ConfigKeys.Numbers.cs`, `default.json`, `contracts/config-keys.json`, `contracts/golden/head/status*.json`, `statusBar/statusBarModel.ts`, `panel/fieldMap.ts`, README | no | — |
| DH-1b | (follow-up) the Windows watch reads every volume's free space natively every 5 min and carries `disk.volumeFree` with `basis sample` | Windows | `Core/Watch/WatchRun.cs` | no | WR-D4, DH-1 |
| DH-2 | A5 keeps every compose container; the label in the inspect template and `ContainerDetail`; a compose fixture captured from a real `docker ps`/`inspect` (redacted) | distro | `Docker/DockerCommands.cs`, `ContainerDetail.cs`, `DockerInventory.cs`, `Preview/CleanupTargets.cs`, `Preview/CleanupPreview.cs`, fixtures, goldens | no | M4 |
| DH-3 | the worktree report: `worktrees.*` keys, root validation, the folder classifier (file reads only), the bounded walk, idle, build-output measure, `worktrees scan --json`, `worktrees.json`, the Windows `status`'s `worktrees` + `worktrees.reclaimable` | Windows | `Core/Worktrees/` (new: `WorktreeRoots`, `FolderKinds`, `WorktreeScan`, `BuildOutputRule`), `Files/TreeWalk.cs` (reused), `Cli/Commands/WorktreesCommand.cs` (new), `Cli/CommandLine.cs`, `Status/*`, keys, contracts | no (read-only) | M1, M2 |
| DH-4 | A24 + A24Orphans: selection, the two git templates, `DeletionRule.WorktreeBuildOutput`, the no-link bottom-up delete, re-validation, budgets, checkpoint, run detail, dry window | Windows | `Core/Actions/WorktreeBuildOutput/` (new), `ActionId.cs` (+`ExecutionOrder`), `ActionRegistry.cs`, `ConfigKeys.cs` (`Auto`), `Files/Deletion/DeletionPolicy.cs`, `Processes/Policy/*`, keys, contracts | **yes** (deletes; adds git) | DH-3, WR-T1 (the engine's Windows path) |
| DH-5 | the button route: `\wsl-care\hygiene` (on-demand, closed parameter), `tasks preview hygiene`, the panel's three buttons + lines, `COMMAND_BUTTONS`, settings timeout | Windows + extension | `Core/Scheduler/WindowsTaskDefinitions.cs`, `Cli/Commands/TasksCommand.cs`, `src/windowsTasks/*`, `panel/*`, `package.json`, `settings/numbers.ts` | **yes** (runs deletes) | DH-4, WR-E1, WR-E3, WR-E4, M3 |
| DH-6 | A25 Docker sweep: prefix match, volume `CreatedAt`, `network ls`, `TestRuns.Judge` (both binaries), the age floor, stop-then-rm, volume/network rm, `network prune` on the never-list, the `testLeftovers.*` keys, preview + second modal, the cleanup-table row | distro + extension | `Core/TestLeftovers/` (new), `Docker/DockerCommands.cs`, `Actions/DockerCleanups/*`, `Processes/Policy/NeverList.cs`, `Preview/CleanupPreview.cs`, `src/cleanup/*`, keys, contracts | **yes** (deletes, stops containers) | DH-2 (shares the inspect template change) |
| DH-7 | A26 `%TEMP%` sweep on WR-T1's engine; A23 excludes the prefix; the both-sides *Sweep test leftovers…* flow in the panel | Windows + extension | `Core/Actions/WindowsTemp/` (WR-T1's, reused), `Core/TestLeftovers/`, `src/cleanup/*` or `src/windowsTasks/*` | **yes** (deletes) | WR-T1, DH-5, DH-6 |
| DH-8 | this repo's own test temp folders take the prefix, with a 30-day transition sweep | tests | `TestSupport/TempRoot.cs`, `Core.Tests/Archive/WindowsIdleTests.cs`, `src_vs_code/src/test/**` | no | — |
| DH-9 | docs: `research/module_daemon.md` (three new sections), `module_vs_code.md`, `module_tests.md`, `architecture.md` (the Windows side's new verb and actions, the field-map table regenerated), README, `POST_DEPLOY.md` (the owner's first A24 preview on his own roots is the acceptance item), the boundary rows | docs | as named | no | lands with each story's own docs; the boundary rows land with this plan |

### 5a. Build order

1. **DH-1, DH-2, DH-8** — independent, distro or tests only, no runner dependency; DH-1 first (it is the warning that was
   missing). DH-2 after M4.
2. **DH-6** — after DH-2 (both change the inspect template; one change, two readers). The Docker sweep works from the distro
   alone; its preview names the other side as "not read from this side" until DH-7.
3. **DH-3** — after M1 and M2; the Windows binary's CLI is usable at once, its panel row after WR-E1.
4. **WR-T1** (the runner plan) — the engine's first Windows-side action; DH-4 and DH-7 build on its path.
5. **DH-4** — after DH-3 and WR-T1.
6. **DH-5** — after DH-4, M3, and the runner's WR-E1, E3, E4.
7. **DH-7** — after WR-T1, DH-5 and DH-6; the both-sides sweep flow is its extension half.
8. **DH-9** — each story lands its own docs; the boundary rows land with this plan; promote when DH-7 ships.
9. DH-1b — whenever WR-D4 exists.

## 6. Boundaries with other plans (named on both sides)

| item | this plan | the other plan |
|---|---|---|
| the Windows engine path for a Windows-side action, `%TEMP%` walk + `claude` guard + owner SID + re-validated delete | REUSES it (A24, A26) | PLAN_windows_runner.md WR-T1 builds it (A23) first; if DH-4 is ready first, the shared core is extracted into WR-T1's place and A23 consumes it — never two copies |
| `testsToDelete-*` in `%TEMP%` | A26, 1 h | A23 (`temp.allowGlobs`, 7 d) never selects the prefix and names it "the test-leftover sweep's" |
| `wsl-care-test-*` in `%TEMP%` (the backlog) | DH-8 stops producing new ones | A23's allow-list keeps taking the old backlog |
| the on-demand `\wsl-care\hygiene` task and its closed parameter | here (DH-5) | the runner's task set (D1's `WindowsTaskDefinitions`, D3's closed argv table) grows by one; E3/E4 install and run it like the others |
| every lettered volume's free space | here (`disk.volumeFree`, DH-1) | PLAN_disk_health.md E15: `disk.hostFree` (E15.S2) stays the VHDX-growth question; E15.S1's script is reused unchanged |
| the sidebar's rows (§3.5 of PLAN_disk_health.md, "the sidebar keeps 20 parameters") | proposes rows 15 widened, 21 and 22 (§8) | E18.W2 builds the slim sidebar; the count is the owner's to approve with E18's go (Q1) |
| A5's compose exemption | here (DH-2) | PLAN_wsl_care_daemon.md §5 A5 row: `auto.A5` stays off until DH-2 ships |
| the WSL `~/git/_wt` folder size | untouched | `Folders/FolderSizes.cs:57` keeps measuring it; nothing under `~/git` is deleted (`DeletionPolicy.cs:90-91`) |

What is disjoint: the WSL-side worktrees (never cleaned here), whole-worktree removal (§9), databases/queues with the
prefix (the rule leaves them to their own repositories), and every other W-A action.

## 7. Measurements owed (before the defaults they set)

- **M1** — the product's walker over the owner's roots: entries, seconds, logical bytes per kind; reconcile with the
  2026-10-14 count (§1). Sets `worktrees.scanMax*`. (A first read-only measure is in §1: 1.24 M files, 97 s.)
- **M2** — idle by full walk vs by admin files only, per folder, over the owner's roots; the default stays the full walk
  unless the two agree everywhere.
- **M3** — `$(Arg0)` substitution in a per-user on-demand task, on this machine, with a test task under a
  `testsToDelete-` name removed by its own teardown.
- **M4** — a captured, redacted `docker ps -a` + `inspect` of a real compose project (labels present in both).
- **M5** — the E: volume's size (the keyboard's USB volume, D1), for `disk.volumeFreeMinSizeGib`.

Each lands in `research/2026-10-1x_disk_hygiene.md` with its conditions, in the story that needs it.

## 8. Where the new rows appear

**Before E18** (today's panel, `src_vs_code/src/panel/fieldMap.ts:16-42` sections, `:118-164` rows):

- `disk` section: `disk.hostC` "C: free — arrives in E7" (`:140`) is replaced by **`disk.volumes`** — "Windows volumes:
  lowest free" (`verdicts[id=disk.volumeFree]`, kind `verdict`); a new **`disk.worktrees`** — "Build output in idle
  AI-agent worktrees" (the Windows `status`'s `worktrees.reclaimable`, through `WindowsCareClient`; "not available yet"
  until WR-E1).
- `containers` section: **`containers.testLeftovers`** — "Test leftovers (`testsToDelete-`)" from A25's preview row.
- `cleanup` section: the A25 row in the cleanup table; the A24/A24Orphans/A26 buttons and their last-run lines next to the
  Windows TEMP lines WR-T3 adds.

**In E18** (the mockup at `D:\rsd\_wt\e18-mockup`, not edited by this plan — E18.W1/W2 implement):

- **Sidebar** (`sidebar.html:59-71`, "Disk and Docker"): the row "Host C: free → cleanups" (`:65`) becomes "Windows volumes:
  lowest free → cleanups" (row 15 of PLAN_disk_health.md §3.5 widened to `disk.volumeFree` + `disk.hostFree`); two new rows
  — **21** "Build output in idle worktrees → Clean build output…" (`worktrees.reclaimable`) and **22** "Test leftovers →
  Sweep test leftovers…"; buttons *Clean build output…* and *Sweep test leftovers…* join *Clean…* / *Clean selected…*
  (`:67-70`). The sidebar becomes 22 rows (Q1).
- **Full view, Windows tab:** "Volumes and virtual disks" (`full.html:318-335`) gains Free % and a level per volume (the
  verdict's per-volume breakdown); a new card **"AI-agent worktrees"** right after it — per root: worktrees, size, idle > N
  days, locked, orphans, build output (in idle / in locked / in orphans), the scan's age, *Scan*, *Clean build output…*,
  *Clean orphaned folders…*.
- **Full view, WSL tab:** the Docker card (`full.html:146`) gains "Test leftovers" (containers/volumes/networks, and the
  Windows `%TEMP%` count from the other side) with *Sweep test leftovers…* and the in-flight reading.

## 9. Out of scope (named so nobody builds it by accident)

- **Removing whole idle worktrees.** Measured: `git worktree remove` without `--force` refuses a worktree with a submodule
  (39 of 134) and the owner's agents lock theirs (73 of 134). Doing it safely needs `--force` reasoning, unpushed-commit
  checks and the lock contract — a plan of its own (Q4).
- Sweeping `testsToDelete-` worktrees and branches (the rule lists them; the report counts the worktrees) and the distro's
  `/tmp`; databases and queues (the rule leaves them to their repositories).
- Other build output (`target`, `dist`, `.vs`, `artifacts`, `TestResults`) — the report may list them later; the cleanup
  takes exactly the owner's three names.
- WSL-side worktrees under `~/git` (`DeletionPolicy`'s `GitFolder` rule stands).

## 10. Growth surfaces (before the first write)

| surface | projected size | retired by | interrupted |
|---|---|---|---|
| `%LOCALAPPDATA%\wsl-care\worktrees.json` | ≈372 folders × ≈0.4 KB + `top` 20 ≈ 150 KB; ONE file, overwritten | overwritten by the next scan; `worktrees.json` older than `worktrees.reportMaxAgeDays` (7) reads stale | written atomically (temp + rename); a killed scan leaves the previous file, and `status` shows its age |
| A24/A24Orphans run details | ≤ `worktrees.maxItemsPerRun` (2000) × ≈0.2 KB ≈ 400 KB per run; the one-off would have been ≈150 KB | the Windows run retention (WR-D4's retention sweep), as every run detail | the engine's interrupted-run rule + the checkpoint (§3.4) |
| `build-output-cursor.json` | one record, < 1 KB | overwritten; cleared when its candidate is decided | it IS the interruption record; the next run takes it first |
| A25/A26 run details | ≤ the ≈90 containers + volumes + networks + folders × 0.2 KB ≈ 40 KB | the run retention on each side | the engine's interrupted-run rule; Docker's own refusals keep a half-removed set consistent |
| new config keys | ≈20 keys | — (configuration) | — |

## 11. Test plan

Every story: RED tests first, then GREEN, then a break-it on PRODUCT code only (red, then restored); every number a key;
goldens regenerated through `ContractFilesTests`; the new key names added to `NumbersAreConfigurationTests`; tests run as
the built executables through `low-priority.mjs`, never `dotnet test`; nothing real is deleted, started or registered —
fakes, a `WSL_CARE_ROOT` sandbox, the scenario harness's `fakebin` (`research/module_tests.md:64-95`), and any folder,
container or task a test creates is named `testsToDelete-…` and removed by the test.

**RED first (named before the product code):**

- DH-1: `A_volume_below_the_critical_gib_is_critical_whatever_its_percent`; `A_volume_below_the_warn_percent_warns_whatever_its_gib`;
  `The_value_names_the_worst_volume_first`; `A_volume_smaller_than_the_floor_is_not_judged`; `An_ignored_letter_is_not_judged`;
  `An_unread_free_is_unknown_never_ok`; `An_unavailable_sample_is_unknown_with_its_reason`; `The_limit_text_names_both_floors`;
  extension: `a critical disk.volumeFree colours the bar`, `a warn disk.volumeFree does not`, `the hostFree verdicts still do not`.
- DH-2: `A5_keeps_a_stopped_container_of_a_compose_project_and_names_the_project`;
  `A5Testcontainers_still_takes_a_labelled_compose_module_container`; `A_container_without_the_label_is_selected_as_before`;
  `The_inspect_template_reads_the_compose_label`.
- DH-3: `A_relative_or_network_or_drive_root_is_refused`; `A_root_holding_the_profile_or_an_agent_folder_is_refused`;
  `A_git_file_pointing_back_is_a_worktree`; `A_git_file_pointing_nowhere_is_an_orphan`; `A_locked_file_marks_a_worktree_locked`;
  `A_git_folder_is_a_main_clone_and_never_a_candidate`; `Bin_without_a_project_file_beside_it_is_not_build_output`;
  `Node_modules_without_package_json_is_not_build_output`; `A_nested_node_modules_is_not_counted_twice`;
  `A_fresh_file_deep_inside_makes_the_worktree_active`; `A_fresh_index_in_the_admin_folder_makes_it_active`;
  `A_walk_past_its_budget_is_unknown_never_idle`; `A_junction_inside_a_worktree_is_never_entered` (Windows leg, a sandbox
  junction to a sandbox folder outside the root whose file must not be counted); `The_scan_starts_no_process`;
  `The_distro_binary_refuses_worktrees_scan`.
- DH-4: `A_tracked_file_inside_bin_retains_the_candidate`; `A_build_folder_git_does_not_ignore_is_retained`;
  `A_git_refusal_retains_the_candidate_by_name`; `An_empty_answer_with_a_nonzero_exit_retains`; `A_locked_worktree_is_skipped_unless_cleanLocked`;
  `A_candidate_written_since_the_preview_is_retained`; `A_candidate_replaced_by_a_junction_after_the_preview_is_retained`;
  `A_junction_inside_node_modules_is_removed_as_a_link_and_its_target_survives` (Windows leg, sandbox);
  `The_env_file_beside_bin_survives`; `An_ignored_non_build_folder_survives`; `Nothing_outside_the_root_is_ever_deleted`;
  `DeletionPolicy_refuses_every_other_path_under_a_git_root`; `The_git_templates_cannot_be_bent_into_prune_remove_or_clean`;
  `Git_is_never_taken_from_PATH`; `A24Orphans_says_not_confirmed_by_git_first`; `The_checkpoint_is_written_before_the_first_delete`;
  `An_interrupted_candidate_is_taken_first_and_judged_by_its_files`; `While_dry_nothing_is_deleted`; `The_run_detail_counts_logical_bytes_per_outcome`.
- DH-5: `the hygiene task has no trigger`; `the supervisor accepts only A24, A24Orphans and A26 as the parameter`;
  `the preview button never spells act or --confirm`; `a running hygiene run reads running across a reload`;
  `every new command has a panel button`.
- DH-6: `A_name_with_the_prefix_in_another_case_matches`; `A_name_with_the_prefix_later_in_it_never_matches`;
  `The_underscore_form_is_not_matched`; `A_resource_younger_than_an_hour_is_never_selected`;
  `A_running_test_runner_refuses_the_sweep_and_names_it`; `A_running_testcontainers_container_refuses_the_sweep`;
  `An_unreadable_process_table_refuses_the_sweep`; `The_judgement_is_repeated_before_the_first_removal`;
  `A_running_leftover_is_stopped_then_removed_never_forced`; `A_kept_container_is_never_selected`;
  `Network_prune_is_on_the_never_list`; `The_order_is_containers_volumes_networks`.
- DH-7: `Nothing_under_temp_claude_is_selected_even_with_the_prefix`; `A23_never_selects_the_prefix`;
  `the panel runs neither side unless both previews read no test run`.
- DH-8: `TempRoot_names_its_folder_with_the_prefix`; `The_transition_sweep_takes_both_shapes`.

**Break-it (each red, then restored):** the OR made AND; the critical bar colouring removed; the size floor ignored; the
compose label dropped from the template; the reparse check removed from the walk; the delete enumerating into a link;
the root containment check removed; the git confirmation skipped; a non-zero git exit read as clean; the fsmonitor
override dropped; `cleanLocked` ignored; the re-validation removed; the checkpoint written after the delete; the age floor
removed; the anchored match replaced by `Contains`; `-f` added to a removal; the in-flight re-check before removal removed;
an `unknown` signal read as clear; the `claude` guard removed.

## 12. Questions

Asked first of the coai question consultants (`d2330f7e`, one row answered); their answers were verified and are taken
above (OR levels and defaults, the Windows binary as the side, the in-flight signals and the age floor, orphans as an
opt-in). What remains for the owner:

- **Q1** — the E18 sidebar grows from 20 to 22 rows (§8). Approve with E18's go, or fold rows 21–22 into row 15?
- **Q2** — `disk.volumeFree` at `critical` colours the status bar, the first disk verdict to do so (§3.2). Yes?
- **Q3** — `worktrees.roots` defaults to `[]` and is set with `wsl-care.exe config set`; the panel offers no root picker
  (the extension never writes daemon config). Acceptable, or should DH-5 add a panel picker that runs the daemon's own
  `config set` through the hygiene task?
- **Q4** — whole-worktree removal (§9) as its own later plan?

## 13. Definition of Done

- [ ] DH-1: `disk.volumeFree` judges every lettered volume above the size floor; critical colours the bar; the `disk.hostC`
      arriving row is gone; README says the defaults are starting values.
- [ ] DH-2: A5 never selects a compose container (a RED test proved it did); `auto.A5` still off.
- [ ] DH-3: the Windows `status` carries `worktrees` and `worktrees.reclaimable`; the scan starts no process.
- [ ] DH-4: A24 and A24Orphans delete only the positive rule's folders, never through a link, never outside a root, with git
      confirming A24's; the Fable own review ran.
- [ ] DH-5: every action has a panel button with a dry-run preview; the hygiene task is on-demand only.
- [ ] DH-6/DH-7: the sweep refuses while any signal reads `inFlight` or `unknown`, never touches anything younger than an
      hour, and the preview shows how it decided.
- [ ] DH-8: this repo's test temp folders carry `testsToDelete-`.
- [ ] M1–M5 recorded in `research/` with their conditions before the defaults they set.
- [ ] Docs: `module_daemon.md`, `module_vs_code.md`, `module_tests.md`, `architecture.md`, README, POST_DEPLOY; both sides of
      every boundary in §6 written; `node .agents/conventions/tools/plan-lifecycle.mjs` clean.
- [ ] The plan promoted to `research/` when DH-7 ships (DH-1b may stay as an open tail named in the status line).

## Epics

> **DRAFT — the split into epics is Fable's** (`.agents/conventions/common/subagent-models.md`: plan → epics is the
> `fable` tier). This grouping exists so the reviewers can see an order; Fable may regroup, renumber and designate the
> cornerstones. The epic numbers continue PLAN_disk_health.md's E15–E18 and are provisional.

- **E19 — See it** (DH-1, DH-3; DH-1b later). Goal: the owner is warned before a volume fills and can see where the space
  went. Scope: two verdicts, one read-only scan, panel rows. Depends on: — (DH-3's panel row on WR-E1). Done when the owner's
  D: would have warned at 30 GiB and the report shows his roots.
- **E20 — Clean it** (DH-4, DH-5). Goal: one button frees the idle build output, a second the orphans'. Depends on: E19,
  WR-T1, WR-E1/E3/E4. Done when the owner's first preview on his own roots is the POST_DEPLOY acceptance item.
- **E21 — Test leftovers and A5** (DH-2, DH-6, DH-7, DH-8). Goal: leftovers named by the rule are swept safely; A5 can be
  automated. Depends on: — for DH-2/DH-6/DH-8; WR-T1 and DH-5 for DH-7.

Cornerstones: 0 of 2 — not designated (draft; candidates: DH-4, the delete and its `DeletionPolicy` exception; DH-6's
`TestRuns.Judge`, the in-flight contract both binaries share).
