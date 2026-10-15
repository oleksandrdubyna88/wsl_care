# PLAN — Windows disk hygiene: a verdict for every lettered volume, the AI-agent worktree report, a build-output cleanup, the `testsToDelete-` sweep, and A5's compose exemption

> Status: **plan only, nothing implemented yet, 2026-10-14 — coai plan round 1: proceed; the owner's answers to Q1–Q4 and the
> own review written in, plan round 2 (§12a); the epics split by Fable, 2026-10-14 (E19–E21, cornerstones DH-4 and DH-6,
> `## Epics`); implementation waits for the owner's go.** Scope: the daemon's disk verdicts (`Thresholds/`), a
> Windows-side worktree scanner and two Windows-side cleanup actions (`Core/Worktrees/` new, `Core/Actions/`), the Docker
> cleanup's selection (`Preview/CleanupTargets.cs`), a test-leftover sweep on both sides, this repository's own test-temp
> prefix, and the extension's panel rows and buttons for all of it. The split into epics is `## Epics` (the last section):
> three outcome epics — E19 *See it*, E20 *Clean it*, E21 *Test leftovers and A5*.
>
> Related: [PLAN_disk_health.md](PLAN_disk_health.md) (E15–E18), [PLAN_windows_runner.md](PLAN_windows_runner.md) (WR-T1–T3,
> A23, the tasks and the supervisor), [PLAN_windows_care.md](PLAN_windows_care.md) (W-A2), [PLAN_wsl_care_daemon.md](PLAN_wsl_care_daemon.md)
> (A5), [module_daemon.md](../research/module_daemon.md), [module_tests.md](../research/module_tests.md),
> [architecture.md](../research/architecture.md). Shared rule: `.agents/conventions/common/test-leftovers.md` (conventions
> `release` e29883f, this repository's pin since main moved to it on 2026-10-14; its text is also carried over in §3.6).

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
- **Small volumes are judged by percent alone** (coai plan round 1, finding 2): the absolute floors apply only to a volume
  of at least `disk.volumeFreeAbsoluteMinSizeGib` (100) — below that a 16 GiB volume with 12 GiB free would warn forever
  and a 9 GiB one would be critical forever, whatever it held. The limit text says which rule judged each volume. RED:
  `A_small_mostly_empty_volume_is_ok`.
- **Unknown, never ok,** when the sample is unavailable or a volume's size/free is unread (the reading's own reason), as
  `disk.hostFree` does. **Precedence across volumes: critical > warn > unknown > ok** — one unread volume never hides a
  critical one, and never lets the rest read ok.
- **Relation to `disk.hostFree`:** both stay. `disk.hostFree` asks "can the VHDX files grow here"; `disk.volumeFree` asks
  "is this volume running out". C: can breach both; they say different things.
- **The status bar — a MARK by the exact id, the GPU mark's shape** (the owner's answer to Q2, 2026-10-14):
  `RELEVANT_VERDICT_PREFIXES` stays `memory.`, `kernel.`, `pressure.` (`src_vs_code/src/statusBar/statusBarModel.ts:24`) and
  the test that holds the E15.S2 disk verdicts out of the bar (`src_vs_code/src/test/statusBar.test.ts:61`) stays true — no
  `disk.*` prefix joins. DH-1 adds a pure `withVolumeMark(view, mark)` beside `withGpuMark` (`statusBarModel.ts:109`): when
  the verdict with the EXACT id `disk.volumeFree` is `critical`, the bar text gains ` · D: low space` (the worst volume's
  letter), the level becomes critical, and the tooltip's first line gives that volume's free GiB, free % and the reading's
  age (`basis.ageSeconds`); a `warn` adds no mark (it stays in the panel); a mark never LOWERS the bar's level, exactly as
  `withGpuMark` holds. RED: `a critical disk.volumeFree marks the bar with the volume and its age`,
  `a warn disk.volumeFree adds no mark`, `the mark never lowers a critical bar`, `no other disk.* verdict colours the bar`.
- **Cadence — DH-1b comes straight after DH-1** (the owner, 2026-10-14, on the consultant's advice): a 4-hourly full run
  cannot catch a volume that a few agents' builds fill in an hour, which is the failure that happened, so the frequent
  reading is part of the fix, not a follow-up. DH-1b: the Windows binary's watch (the `\wsl-care\watch` task, every
  `mcpWatchdog.periodMinutes` = 5 min) reads every lettered volume's free space natively (`GetDiskFreeSpaceEx`, no
  PowerShell, no process) and its `status` carries `disk.volumeFree` with `basis {source: sample, ageSeconds}` through the
  same pure rule; the panel and the bar take the NEWER of the two readings (the distro's full run, the Windows sample). Its
  dependency is explicit and real: the Windows watch is WR-D4 (PLAN_windows_runner.md §5), and DH-1b cannot ship before it.

### 3.3 The worktree report (DH-3) — read-only; git only through its three read-only questions

**Roots.** `worktrees.roots` — a list of absolute local paths, default **`[]`** (no hard-coded user path). **They are chosen
by a panel button, *Choose folders…*** (the owner's answer to Q3, 2026-10-14: every action is a panel button): the
extension opens VS Code's own folder picker (`showOpenDialog`, folders only, many), then hands the chosen paths to the
staged exe's dedicated verb `worktrees roots set` ON STDIN (the `--entry -` pattern A20 uses, `contracts/requests/`), which
validates each by the rules below and writes the key through the daemon's own configuration writer — so the extension
still never writes daemon config and never spells `config` (PLAN_windows_runner.md §4), and no path travels on argv.
`worktrees roots clear` empties it. The verb is all-or-nothing: if any path is refused, nothing is written and EVERY path
comes back with its own verdict (accepted / refused + reason). The panel then shows that list (coai plan round 2,
finding 3) with two buttons — *Save the N accepted folders* (the verb again with just those) and *Choose again* — so one
mistaken pick never costs the others and never leaves a half-written key. With no root, the
sidebar and the full view say "not configured" with the *Choose folders…* button beside it. `wsl-care.exe config set
worktrees.roots …` stays as the CLI duplicate. A root is refused, with its reason, when it is: not absolute; not on a local FIXED drive (the
`[IO.DriveInfo]` rule of E15.S1, `research/module_daemon.md:315-317`); a drive root; a reparse point or under one; the user
profile itself or an ancestor of it; inside or an ancestor of any `DeletionPolicy` agent root or Claude temp root
(`Files/Deletion/DeletionPolicy.cs:96-100`); inside or an ancestor of ANOTHER configured root (nested or overlapping roots
would count twice and could delete twice — own review). At most `worktrees.maxRoots` (8).

**What each direct child folder of a root is** — answered by FILE READS only (bounded, no-link, `Files/BeneathFiles.cs:102`
style), never by starting git:

| kind | how it is known |
|---|---|
| `worktree` | a `.git` FILE whose `gitdir: <path>` names an existing admin folder (`<common>/worktrees/<name>`) whose own `gitdir` file points back to this folder (case-insensitive) |
| `locked` (a flag on a worktree) | the admin folder holds a `locked` file — how git stores `git worktree lock`; its text (the reason) is shown, capped |
| `orphan` | a `.git` FILE whose Windows `gitdir:` target (after the `/mnt/` translation below) does not exist, or whose admin folder points elsewhere |
| `foreignPointer` | a `.git` FILE whose `gitdir:` is a POSIX path the translation cannot map (`/home/…`, a distro path) — a live worktree created inside WSL, NEVER a candidate (own review: the inverse of §3.1 — the Windows side would otherwise read a WSL-made worktree as an orphan and A24Orphans would clean it unconfirmed) |
| `mainClone` | a `.git` FOLDER (3 such in the one-off) — reported, never a cleanup candidate |
| `notGit` | no `.git` at all — reported as "not a git folder", never a candidate |
| `testLeftover` (a flag) | the folder's own name matches the §3.6 prefix — reported as a count; NOT swept by this plan (§9) |

A worktree made from WSL on a Windows drive writes `gitdir: /mnt/<letter>/…`; the classifier translates exactly that shape
(the automount root `HealthParsers.AutomountRoot` reads, default `/mnt/`, one lowercase letter, no `..`, the inverse of
`HealthCollector.InDistro`, `Health/HealthCollector.cs:103-118`) to `<LETTER>:\…` and judges the result like any Windows
pointer; anything else POSIX is `foreignPointer`. RED: `A_wsl_made_worktree_with_a_mnt_pointer_is_a_worktree_not_an_orphan`,
`An_unmappable_posix_pointer_is_foreign_and_never_a_candidate`.

**Idle.** A folder's idle age = now − the NEWEST of: every write time (files AND folders, the WR-T1 rule) found by the
walk OUTSIDE its build-output folders, each build-output folder's OWN write time (not its inside), and for a worktree its
admin folder's `HEAD`, `index` and `logs/HEAD` (git touches `index` on every status/add/checkout, which agents run
constantly). The inside of each build-output folder is walked separately, under its own budget, for its size and its own
newest write — which A24 checks per candidate (§3.4), so a build today still keeps that `bin` (coai plan round 1, finding 5:
otherwise the largest `node_modules` would spend the budget and the worktrees that most need cleaning would never read
idle). A worktree whose NON-build walk did not finish, or met an entry it could not read, is `unknown — not idle`; a
candidate whose own walk did not finish or met an unreadable entry is `unknown — retained`. Measurement M2 compares this
with the admin-files-only signal over the owner's tree; if the cheap signal agrees on every folder it may become the default.

**Build output** (the selection rule A24 deletes, measured in the report):

- a folder named `bin` or `obj` whose PARENT holds a file whose extension is in `worktrees.projectFileExtensions` (default
  `[".csproj", ".fsproj", ".vbproj", ".esproj", ".sqlproj", ".vcxproj"]` — a closed list, where the one-off's `*.*proj`
  glob would also have matched any `.proj`);
- a folder named `node_modules` whose parent holds a `package.json`, TOP-MOST only (the walk never descends into it to find
  another) — the one-off took any `node_modules`; this rule is narrower on purpose;
- never `.git` (folder or file), never anything under one; never a reparse point, never anything under one;
- a candidate that holds a `.git` (folder or file) ANYWHERE inside it — a nested repository — is retained WHOLE, not merely
  "not entered" (own review). RED: `A_bin_holding_a_nested_repository_is_retained_whole`;
- **a `bin`/`obj` candidate is deleted only when EVERY file inside it is a build artifact** (coai plan round 2, findings 0
  and 2): git's "ignored" says the folder is not source, not that nothing in it is valuable — a SQLite file an app created
  in its output folder, a hand-placed `.env` or tool, would go with it. The walk that sizes the candidate also classifies
  each file against `worktrees.buildArtifactPatterns` (a closed default list, MachineOnly: `*.dll`, `*.exe`, `*.pdb`,
  `*.xml`, `*.json`, `*.cache`, `*.props`, `*.targets`, `*.editorconfig`, `*.so`, `*.dylib`, `*.a`, `*.lib`, `*.obj`,
  `*.wasm`, `*.js`, `*.mjs`, `*.map`, `*.txt`, `*.nupkg`, `*.snupkg`, `*.resources`, `*.runtimeconfig.*`, `*.deps.*`,
  `*.assets.*`, `*.AssemblyInfo*.cs`, `*.GlobalUsings*.cs`, `*.g.cs`, `*.baml`, `*.ilk`, `*.exp`, `*.res`, `*.tlog`,
  `*.lastbuildstate`, `*.stamp`, `*.sourcelink.json` — the exact list is fixed by M6 against the owner's tree); ONE file
  outside it retains the whole candidate, named in the preview ("holds a file that is not a build artifact: local.db").
  A file written more than `worktrees.artifactSkewMinutes` (10) after the newest `*.dll`/`*.exe`/`*.pdb` of its folder, and
  any `*.db`, `*.sqlite*`, `*.mdf`, `*.ldf`, `*.env`, `.env*`, `*.pfx`, `*.key`, `*.pem`, also retains the candidate — a name is
  no proof of regenerability (E19 stories §3 decision 11, 2026-10-15).
  `node_modules` is exempt from the content check — its contents are packages that `package.json` and the lockfile
  restore, and that is the owner's rule. The same check applies to A24Orphans, so an orphan's `bin` with a database in it
  stays even where git cannot be asked. RED: `A_bin_holding_a_sqlite_file_is_retained_and_names_it`,
  `A_bin_of_only_build_artifacts_is_a_candidate`, `An_orphans_bin_holding_an_env_file_is_retained`.

**The walk.** `TreeWalk` (`Files/TreeWalk.cs:105-110`: `RecurseSubdirectories`, `IgnoreInaccessible`,
`AttributesToSkip = ReparsePoint`) through `IFileSystem.WalkTree` with `TreeRules.NeverEnter` for `.git`. **`IgnoreInaccessible`
is the one thing this scan may NOT inherit** (coai plan round 1, finding 1): a skipped, unreadable subtree would hide a
fresh write and let a worktree read idle. DH-3 widens `TreeRules` with a mode that COUNTS every entry the enumeration could
not open (an access error per directory, caught and counted, never silently passed) and returns it on the measure, so
any such count makes the folder or the candidate `unknown` — the existing callers keep today's behaviour. RED:
`An_unreadable_subfolder_makes_the_worktree_unknown_not_idle`. Budgets
`worktrees.scanMaxEntries` (5 000 000) and `worktrees.scanMaxSeconds` (900) for the WHOLE scan and per root a fair share, a
cancellation check every 1024 entries (`TreeWalk.cs:43`). M1's 1.24 M files in 97 s is the basis (×4 headroom); a budget
that runs out makes the remaining folders `unknown`, never `idle`. Sizes are "logical bytes" (file length), never "disk
freed" — ReFS block cloning and hard links (pnpm) make the two differ.

**The report** — `worktrees scan --json` (new verb, Windows binary only; the distro's binary refuses it with the side's
reason, as `act` does, `Cli/Commands/ActCommand.cs:116-122` — whose text today is hard-coded "runs inside the WSL distro"
(`:120`); DH-3 widens it to name the side the id or verb runs on) writes `%LOCALAPPDATA%\wsl-care\worktrees.json` atomically
(`{schemaVersion, scannedAt, elapsed, complete, roots[{path, refused?, folders, worktrees, locked, orphans, mainClones, notGit,
testLeftovers, sizeBytes, idleOver{days, count, sizeBytes}, buildOutput{all, inIdleUnlocked, inIdleLocked, inOrphans}
{count, bytes}, unknown{count, why}}], top[]}` — `top` the `worktrees.reportTop` (20) largest reclaimable folders by name,
never their contents). The Windows binary's `status --json` carries it as `worktrees` with `basis {source: scan, scannedAt,
ageSeconds}` and one verdict, **`worktrees.reclaimable`**.

**"Reclaimable" means only what a cleanup would actually be allowed to delete** (the owner, 2026-10-14) — never a
worktree's whole size, never all build output: the bytes of the candidates that pass EVERY selection check of §3.4 at scan
time — idle, unlocked (or `cleanLocked`), the candidate's own age, no nested repository, no foreign reparse tag, only build artifacts inside a `bin`/`obj`, and git's
confirmation (tracked / ignored) — reported as two parts, `a24` (worktrees) and `a24Orphans` (orphans, with how many were
ignore-confirmed). Everything else is counted under its own name (`retained{reason, count, bytes}`: locked, recent, tracked,
not ignored, unknown …), so the owner sees why the rest stays. The verdict warns when `a24 + a24Orphans` exceeds
`worktrees.reclaimableWarnGib` (20); `unknown` with no root or no scan; the value names the root and both parts.

The scan is therefore read-only but not process-free: it runs exactly the three read-only git templates of §3.4 (the git
questions the owner asked to have answered read-only), and nothing else; it writes only its own file (and, at its start,
removes the temp file a killed scan of its own left beside it — its own name, its own folder, nothing else). The CLASSIFIER
(§3.3's table) still starts no process.

**Who runs the scan** (own review: a 900 s walk as a child of VS Code dies on a window reload, and with no schedule the
verdict goes stale): the on-demand task `\wsl-care\worktrees-scan` (§3.5), also registered with a daily trigger
(`worktrees.scanEveryHours`, 24, after `windowsTasks.cleanupLogonDelayMinutes`-style logon delay
`worktrees.scanLogonDelayMinutes`, 45); the panel's *Scan now* runs that task through its `Run`, and the panel only READS
`worktrees.json` (through `status`). The scan takes the `worktrees` lease (§3.5), never the run lock.

### 3.4 A24 `worktreeBuildOutput` and A24Orphans (DH-4)

Two ids, one implementation, the A5/A5Testcontainers shape (`Preview/CleanupTargets.cs:77`): **A24** takes worktrees,
**A24Orphans** takes orphan folders. Both `Sides = [Windows]` (`Actions/ICleanupAction.cs:231`), both `auto.*` OFF with
trust `Off`, both a second modal. Main clones and `notGit` folders are never candidates.

**Selection (the preview), per candidate folder (§3.3 rule):** its folder is idle > `worktrees.idleDays` (3, the one-off's
cut); for A24 the worktree is unlocked, or `worktrees.cleanLocked` (false) is on; the candidate's OWN newest descendant
write is older than the idle cut too; and — A24 only — **git confirms it**:

All git questions are asked with the WORKING DIRECTORY set to the candidate's parent folder (so a candidate inside a
submodule is asked of the submodule's own repository) and the candidates spelt as RELATIVE names — never a path in argv,
which also keeps the never-list's `protected-path` rule (`Processes/Policy/NeverList.cs:51`, any argv naming a path under a
git root) satisfied for a root under `%USERPROFILE%\git`:

- `git rev-parse --show-toplevel` must print the worktree (or the submodule) the classifier expects — git discovery that
  escaped to another repository retains every candidate of that folder ("git answered for another repository");
- `git --literal-pathspecs ls-files -z --cached -- <names…>` must list NOTHING — a tracked file inside retains the whole
  candidate ("holds tracked files");
- `git check-ignore -v -n -z --stdin`, each name WITH a trailing `/` (a directory-only pattern such as `bin/` matches a path
  spelt as a directory), parsed PER PATH: `-n` prints a line for every path, matched or not, so a batch is judged path by
  path, never by the exit code — exit 0 or 1 is an answer (1 = nothing matched), 128 is a refusal (own review: "exit 0
  required" would have filed every "not ignored" as a refusal, and with `--stdin` exit 0 means only that ANY path matched).
  A candidate with no matching ignore rule — or matched by a NEGATED one (`!bin/`) — is retained ("not ignored by git").
- `ls-files` and `rev-parse` require exit 0 (an empty answer with a non-zero exit is not "clean" — the neighbour
  `ReviewTreeKeeper` in dew_flow_connect_other_ais `src_mcp/runners/Worktrees/ReviewTreeKeeper.cs:298-308` learnt this in a
  plan round). "Dubious ownership", "not a git repository" and every other refusal RETAIN the candidate by its own name.
- Every call read-only: `--no-optional-locks`, `-c core.fsmonitor=false` (a repository's own config can name a program as
  its fsmonitor, which `git` would start), a CLEAN environment (`CommandEnvironment`) carrying only `GIT_OPTIONAL_LOCKS=0`,
  `GIT_TERMINAL_PROMPT=0` and `GIT_CEILING_DIRECTORIES=<the root>` (discovery never climbs above the root; a worktree's
  own `.git` file sits below it), stdin the name list or closed, the timeout `worktrees.gitTimeoutSeconds` (30).
- **The program.** The daemon runs no git today (scout: no `git` invocation under `src_daemon/src`). `CommandTemplate`'s
  executable is "a bare name; never a path" (`Processes/Policy/CommandTemplate.cs:51`), so DH-3 adds a RESOLVER (DH-4 reuses it), the shape
  of the disk script's closed administrator-only fallback (`Health/WindowsDiskCommands.cs:22`): `git.exe` from a closed
  list under the Program Files folder (an administrator-only folder, `research/module_daemon.md:340-344`), checked as a
  PE file, never from `PATH`; `worktrees.gitPath`, when the owner names one, is accepted only under an administrator-only
  folder too (its trust: `MachineOnly`, the user layer cannot set it). Three declared templates — exactly the three argv
  shapes above — through `ICommandRunner` (`Processes/ICommandRunner.cs:9`). The never-list keeps refusing `worktree prune`
  and `clean` (`NeverList.cs:44`, `:149`) and gains a test that no template can be bent into them.

**A24Orphans** has no admin folder to ask. Where the orphan's REPOSITORY still exists (its `gitdir:` named
`<common>/worktrees/<name>` and `<common>` is still there), the ignore rules are still checked —
`git --git-dir=<common> --work-tree=<the orphan> check-ignore --no-index -v -n -z --stdin` from the orphan's folder, by the
same per-path parse (own review) — and the preview reads "ignore rules confirmed; tracked files NOT checked (the index is
gone)". Where `<common>` is gone too, it deletes by the structural rule alone and the preview's first line says "not
confirmed by git — the folder's repository metadata is gone". Either way it is its own button and never part of A24
(consultant `d2330f7e`, answer 4, verified against `ReviewTreeKeeper.cs:298-308`, which refuses a broken `.git` for the
same reason). It is where the measured space was (≈44 of 45 GB).

**The delete — never follows a link, never leaves a root:**

- Just before each candidate: re-validate everything the preview decided (the kind, lock, idle cut, the candidate's own
  newest write, git's answer for A24) and the path itself: real path beneath the root's real path + separator; the
  candidate and every ancestor up to the root NOT a reparse point (`BeneathFiles.cs:102`'s per-component check).
- The removal is the product's own bottom-up walk; `Directory.Delete(recursive)` is not used, so the behaviour is the
  product's, tested, not the runtime's. A file is deleted through `SetFileInformationByHandle(FileDispositionInfoEx)` with
  `FILE_DISPOSITION_FLAG_DELETE | FILE_DISPOSITION_FLAG_POSIX_SEMANTICS | FILE_DISPOSITION_FLAG_IGNORE_READONLY_ATTRIBUTE`
  on a handle opened with `FILE_FLAG_OPEN_REPARSE_POINT` — NEVER by clearing the read-only attribute first: a pnpm
  `node_modules` entry is a HARD LINK into a store outside the root, and its attributes are the store file's, so a
  `SetAttributes` would change a file outside the root (own review). A directory that is a reparse point is removed AS A
  LINK, and only when its tag is `IO_REPARSE_TAG_MOUNT_POINT` (a junction) or `IO_REPARSE_TAG_SYMLINK`; any other tag (a
  cloud placeholder, dedup, a container layer) retains the whole candidate ("holds a reparse point of kind X"). Long paths
  through the extended-length prefix.
- **`DeletionPolicy` must refuse the roots themselves, not only `~\git`** (own review, the plan's one blocker): the Windows
  `GitRoots` is only `%USERPROFILE%\git` (`Hosting/WindowsHostPaths.cs:89`, `Files/Deletion/DeletionPolicy.cs:90-91`), so
  `D:\rsd\_wt` is under NO protected root today and an exception to the git rule would protect nothing there. DH-4 adds a
  deny-by-default class, `DeletionRule.WorktreeRoot`: every path beneath a configured `worktrees.roots` entry is REFUSED by
  the policy itself, and ONE narrow admission, `DeletionRule.WorktreeBuildOutput`, lets through only A24/A24Orphans and only
  a path that passes the §3.3 predicate again inside the policy (name, sibling project file or `package.json`, beneath a
  configured root, no reparse point on the way, no `.git` inside). Under `~\git` the same admission is the only exception
  to `GitFolder`. RED: `A_source_file_under_a_D_root_is_refused_by_the_policy_itself`,
  `DeletionPolicy_refuses_every_other_path_under_a_worktree_root_or_a_git_root`.
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

- **One task per operation, no parameters** (own review: the runner's tasks use `MultipleInstancesPolicy IgnoreNew`,
  PLAN_windows_runner.md §3.4, so one shared task would silently drop a second click while the first ran; and the closed
  argv table of D3 stays a table of fixed lines): `\wsl-care\worktrees-scan` (daily trigger + on demand, `tasks run
  worktrees-scan`), `\wsl-care\build-output` (on demand only, body `act A24`), `\wsl-care\build-output-orphans` (on demand
  only, `act A24Orphans`), `\wsl-care\test-leftovers` (on demand only, `act A26`). The `$(Arg0)` route of the first draft
  is dropped, and with it measurement M3 — a parameter channel the product does not need is one it does not have to defend.
- **Locks.** None of them takes the run lock (the watch keeps sampling, as the archive body does, PLAN_windows_runner.md
  §3.5). The scan, A24 and A24Orphans share ONE lease, `worktrees` (the `SideLease` shape, `Archive/SideLease.cs:36`): a
  clean never runs beside a scan or another clean. A26 takes WR-T1's `%TEMP%` lease, shared with A23. A busy lease ends the
  run at once with "already running: <holder>", and the panel says so instead of a click that does nothing.
- *Clean build output in idle worktrees…* / *…in orphaned folders…* / *Sweep test leftovers…* — the panel shows the
  preview (`tasks preview build-output|build-output-orphans|test-leftovers`, read-only, through `WindowsCareClient` with
  its own timeout), two modals, then runs that operation's task through its `Run`. The run re-checks everything (A5's
  "re-checked at run time" model, `research/architecture.md:1233`).
- **Durable "running"** (own review): every run writes a heartbeat into its run record every `worktrees.heartbeatSeconds`
  (30); the panel reads "running" only while the heartbeat is younger than three periods, and "ended without a record —
  the task was stopped or the machine slept" after that, never "running" forever. The line reads the run record ("last:
  <t>, N removed, M kept, <bytes> logical"), not a webview flag.
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
- **A RUNNING leftover is stopped only when it is older than `testLeftovers.stopRunningOlderThanHours` (24)** (own review:
  a long end-to-end run's container older than one hour must not be killed under it); a younger running one is listed and
  kept ("running, younger than 24 h"). The rule's own concern — leftovers that "stay up for weeks" — is met at a day.
- Removal, never `-f` (the removal templates' rule, `Actions/DockerCleanups/DockerCleanupCommands.cs:9-14`): an eligible
  running leftover is stopped first (`docker stop --time <testLeftovers.stopSeconds> <64-hex id>`, a new declared template),
  then `docker rm -v <id>` — which Docker refuses if it was restarted in between; then `docker volume rm <name>` (refuses a volume
  in use), then `docker network rm <id>` (refuses one with endpoints). Order: containers, volumes, networks. A container
  and a network are removed by their 64-hex id, re-read just before (a recycled name never takes another object).
- **A volume has no id but its name** (coai plan round 1, finding 0): Docker offers no conditional delete. So immediately
  before `docker volume rm` the volume is inspected again and removed only when its `CreatedAt` and labels are EXACTLY the
  preview's and it is still older than the age floor — a replacement created under the same name after the preview has a
  new `CreatedAt`, and one created within the floor is never eligible anyway. What is left is the milliseconds between that
  inspect and the `rm`, against a volume both newly created under a `testsToDelete-` name — a name whose whole promise is
  that it is safe to delete — and already holding data; the preview and README name that residue. RED:
  `A_volume_recreated_under_the_same_name_after_the_preview_is_kept`.
- A container carrying `wsl-care.keep=true` is kept, as everywhere (`DockerInventory.cs:140`).

**A26 — Windows `%TEMP%`, in the Windows binary:** top-level folders of the current user's `%TEMP%` whose own name matches,
whose newest descendant write is older than `testLeftovers.minAgeMinutes` (60) — the rule's one hour — through WR-T1's
engine: the root from the shell-folder API, the hard-coded `%TEMP%\claude\` guard checked before any traversal, the owner
SID, the reparse refusal, the re-validated delete. A23's allow-list never takes a `testsToDelete-` name (§6's boundary):
its preview lists them as "the test-leftover sweep's".

**"No test run in flight" — how it is decided** (a pure `TestRuns.Judge` in `WslCare.Core`, compiled into both binaries,
run in the preview AND again immediately before EVERY removal batch — each `docker.batchSize` batch of containers, then
volumes, then networks, then each `%TEMP%` candidate — so a test that starts mid-sweep stops the rest of it (own review: a
single judgement before the first removal would leave minutes of serial `docker stop` unguarded); any `inFlight` or
`unknown` ends the run there with the reasons, and the preview shows every signal with its reading):

| signal | side | in flight when | source |
|---|---|---|---|
| a test-runner process | each side its own | a process matches `testLeftovers.runnerPatterns` (default: an image name ending `.Tests.exe` or `.Tests`; `testhost`; `node` whose script argument's file name is `vitest.mjs` / `vitest`; `dotnet` with argv[1] = `test`; `npm` with argv[1] = `test`, or `run` then `test`) — matched on ARGV TOKENS (the program's base name, then whole arguments), never by a substring of the command line (coai plan round 1, finding 6), so an editor or a shell whose text merely contains "test" never blocks; a test WATCHER does block, and the preview names it (pid, program) so the owner can stop it. Matched on the FULL RAW argv on both sides — never on `Programs`, which keeps only the first two words (`Collectors/Procfs/ProcessFiles.cs:105-109`, so `npm test` reads `node npm-cli.js` and `dotnet exec … testhost.dll` is missed; own review) and never on the redacted, 200-character `CommandLine`; the raw words are matched at collection time and only the match is kept | WSL `ProcessSnapshot.All` (`Collectors/ProcessCollector.cs:83`) from the raw `/proc/<pid>/cmdline` the collector already reads (`:158`); Windows `IWindowsProcessTable.List` + `CommandLine(pid)` (`Mcp/WindowsMcp.cs:18-26`, `Mcp/Win32ProcessTable.cs:30`) |
| a live Testcontainers session | distro (A25) | a RUNNING Ryuk container (`org.testcontainers.ryuk=true`, or the `testcontainers/ryuk` image), or a running `org.testcontainers=true` container YOUNGER than the age floor — never any running labelled container, because one stale running container would then block every sweep forever (own review) | the container list's labels, image and created time |
| a young prefixed resource | the side being swept | any matched resource younger than `testLeftovers.minAgeMinutes` exists | the candidates' own times |
| an unreadable signal | — | `unknown` blocks: a process table that could not be read, a Docker list that failed | the reading's reason |

- **What a snapshot cannot do**, said in the preview and README: a test started after a judgement is not seen until the
  next one — the age floor is what protects it (its resources are younger than an hour), and the re-judgement before every
  batch bounds the window to one batch (at most `docker.batchSize` removals, or one `docker stop --time` of
  `testLeftovers.stopSeconds` per running container in it). A strict guarantee would need a lease every test launcher
  takes; that is a family-convention change, not this product's (consultant `d2330f7e`, answer 3) — follow-up F3.
  M4 also captures a Ryuk container's real labels, so the "live session" signal is pinned to what Testcontainers prints.
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

### 3.9 The settings (every number a key)

Trust follows `ConfigKeys`' model: `Display` (either layer, no safety meaning), a SAFE DIRECTION (the user layer may move it
only that way), `MachineOnly` (the user layer cannot set it at all), `Off` (an `auto.*` the user layer cannot turn on).

| key | default | range | trust | why |
|---|---|---|---|---|
| `disk.volumeFreeWarnGib` / `…CriticalGib` | 30 / 10 | 1–10240 | Display | a warning only |
| `disk.volumeFreeWarnPercent` / `…CriticalPercent` | 10 / 3 | 1–90 | Display | a warning only; critical < warn held by `NumberRules` |
| `disk.volumeFreeMinSizeGib` | 8 | 1–1024 | Display | |
| `disk.volumeFreeAbsoluteMinSizeGib` | 100 | 8–10240 | Display | |
| `disk.volumeFreeIgnoreLetters` | `[]` | ≤ 26 letters `A`–`Z` | Display | |
| `worktrees.roots` | `[]` | ≤ `worktrees.maxRoots` absolute paths (§3.3's rule) | **UserOwned** (the user layer, judged at every use, as `archive.baseFolder`; changed 2026-10-15 by [the E19 stories](PLAN_disk_hygiene_windows_E19.md) §3: a MachineOnly key cannot be written by the user-run verb) | names where deletion is admitted |
| `worktrees.maxRoots` | 8 | 1–32 | MachineOnly | |
| `worktrees.idleDays` | 3 | 1–365 | Higher | a shorter idle loosens what is deleted |
| `worktrees.cleanLocked` | false | — | **Off** (user layer cannot turn it on) | loosens what is deleted |
| `worktrees.projectFileExtensions` | the six of §3.3 | closed allowed set | Subset | a new extension widens the rule |
| `worktrees.gitPath` | `""` | an administrator-only folder | **MachineOnly** | names a program to run |
| `worktrees.gitTimeoutSeconds` | 30 | 5–600 | Display | |
| `worktrees.scanMaxEntries` / `…scanMaxSeconds` | 5 000 000 / 900 | 10 000–50 000 000 / 60–7200 | Lower | budgets |
| `worktrees.scanEveryHours` / `…scanLogonDelayMinutes` | 24 / 45 | 1–168 / 0–720 | Higher | |
| `worktrees.maxItemsPerRun` | 2000 | 1–100 000 | Lower | |
| `worktrees.reclaimableWarnGib` | 20 | 1–10240 | Display | |
| `worktrees.reportTop` / `…reportMaxAgeDays` | 20 / 7 | 1–200 / 1–90 | Display | |
| `worktrees.heartbeatSeconds` | 30 | 5–300 | Display | |
| `testLeftovers.minAgeMinutes` | 60 | 60–10080 | Higher | the rule's hour is the FLOOR: it may only grow |
| `testLeftovers.stopRunningOlderThanHours` | 24 | 2–720 | Higher | |
| `testLeftovers.stopSeconds` | 10 | 1–120 | Display | |
| `testLeftovers.runnerPatterns` | §3.6's list | closed token shapes, ≤ 32 | **MachineOnly** (coai plan round 2, finding 4: no new trust kind) | fewer patterns would loosen the in-flight check |
| `worktrees.buildArtifactPatterns` | the M6-fixed list of §3.3 | closed glob shapes, ≤ 128 | **MachineOnly** | a wider list loosens what is deleted |
| `auto.A24`, `auto.A24Orphans`, `auto.A25`, `auto.A26` | false | — | Off | nothing is deleted without the owner's choice |

Every key: `ConfigKeys.*.cs`, `default.json`, the regenerated `contracts/config-keys.json`, a row in
`NumbersAreConfigurationTests`, a README sentence. No new `KeyTrust` kind is needed: every key here uses one that exists.

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
- Never run a real cleanup, a real `docker` or a real task from a test, nor a real `git` against a real repository (a read-only `git` against a throw-away `testsToDelete-` repository the test builds is allowed, E19 stories §3); never weaken a tripwire, a recorder or
  a fake to make a test pass.

## 5. Stories (one pull request each)

| id | title | side | files (reuse → new) | Fable own reviewer | depends on |
|---|---|---|---|---|---|
| DH-1 | `disk.volumeFree`: every lettered volume, warn/critical, GiB OR %, the critical mark on the bar (`withVolumeMark`, the exact id); the seven `disk.volumeFree*` keys (§3.9); the panel's `disk.hostC` "arrives in E7" row (`src_vs_code/src/panel/fieldMap.ts:140`) becomes the verdict row | distro + extension | `Thresholds/DiskRules.cs`, `ThresholdRules.cs` (spread), `Config/ConfigKeys.Disk.cs`, `ConfigKeys.Numbers.cs`, `default.json`, `contracts/config-keys.json`, `contracts/golden/head/status*.json`, `statusBar/statusBarModel.ts`, `panel/fieldMap.ts`, README | no | — |
| DH-1b | the Windows watch reads every lettered volume's free space natively every 5 min and its `status` carries `disk.volumeFree` with `basis sample`; panel and bar take the newer of the two readings — straight after DH-1, because only the frequent reading closes the original failure | Windows + extension | `Core/Watch/WatchRun.cs`, `Status/*`, `statusBar/statusBarModel.ts`, `panel/fieldMap.ts` | no | DH-1, **WR-D4** (the Windows watch; DH-1b cannot ship before it); its extension half **WR-E1** (today `WindowsCareClient` runs the bundled exe with `--version` alone, `src_vs_code/src/client/WindowsCareClient.ts:17` — the panel reads a Windows `status` only through the client WR-E1 widens; added at the epic split, 2026-10-14) |
| DH-2 | A5 keeps every compose container; the label in the inspect template and `ContainerDetail`; a compose fixture captured from a real `docker ps`/`inspect` (redacted) | distro | `Docker/DockerCommands.cs`, `ContainerDetail.cs`, `DockerInventory.cs`, `Preview/CleanupTargets.cs`, `Preview/CleanupPreview.cs`, fixtures, goldens | no | M4 |
| DH-3 | the worktree report: `worktrees.*` keys, root validation and `worktrees roots set/clear` (stdin), the folder classifier (file reads only), the bounded walk that counts access errors, idle, the git resolver and the THREE read-only git templates (§3.4), "reclaimable" = only what A24/A24Orphans may delete, `worktrees scan --json`, `worktrees.json`, the Windows `status`'s `worktrees` + `worktrees.reclaimable` | Windows | `Core/Worktrees/` (new: `WorktreeRoots`, `FolderKinds`, `WorktreeScan`, `BuildOutputRule`, `GitQuestions`), `Files/TreeWalk.cs` (widened), `Processes/Policy/*` (templates, resolver), `Cli/Commands/WorktreesCommand.cs` (new), `Cli/CommandLine.cs`, `Status/*`, keys, contracts | **yes** (adds git, writes a key that admits deletion) | M1, M2 |
| DH-4 | A24 + A24Orphans: selection (DH-3's rule and git questions, reused), `DeletionRule.WorktreeRoot` + `WorktreeBuildOutput`, the no-link bottom-up delete, re-validation, budgets, checkpoint, run detail, dry window | Windows | `Core/Actions/WorktreeBuildOutput/` (new), `ActionId.cs` (+`ExecutionOrder`), `ActionRegistry.cs`, `ConfigKeys.cs` (`Auto`), `Files/Deletion/DeletionPolicy.cs`, `Processes/Policy/*`, keys, contracts | **yes** (deletes) | DH-3, WR-T1 (the engine's Windows path), WR-D4 (the Windows run-retention sweep that retires its run details) |
| DH-5 | the button route: the four tasks of §3.5 (`worktrees-scan`, `build-output`, `build-output-orphans`, `test-leftovers`), the `worktrees` lease, `tasks preview …`, the heartbeat and its stale cut, *Choose folders…* (the picker → `worktrees roots set` on stdin), the panel's buttons + lines, `COMMAND_BUTTONS`, settings timeouts | Windows + extension | `Core/Scheduler/WindowsTaskDefinitions.cs`, `Cli/Commands/TasksCommand.cs`, `src/windowsTasks/*`, `panel/*`, `package.json`, `settings/numbers.ts` | **yes** (runs deletes) | DH-4, WR-E1, WR-E3, WR-E4 |
| DH-6 | A25 Docker sweep: prefix match, volume `CreatedAt`, `network ls`, `TestRuns.Judge` (both binaries), the age floor, stop-then-rm, volume/network rm, `network prune` on the never-list, the `testLeftovers.*` keys, preview + second modal, the cleanup-table row | distro + extension | `Core/TestLeftovers/` (new), `Docker/DockerCommands.cs`, `Actions/DockerCleanups/*`, `Processes/Policy/NeverList.cs`, `Preview/CleanupPreview.cs`, `src/cleanup/*`, keys, contracts | **yes** (deletes, stops containers) | DH-2 (shares the inspect template change) |
| DH-7 | A26 `%TEMP%` sweep on WR-T1's engine; A23 excludes the prefix; the both-sides *Sweep test leftovers…* flow in the panel | Windows + extension | `Core/Actions/WindowsTemp/` (WR-T1's, reused), `Core/TestLeftovers/`, `src/cleanup/*` or `src/windowsTasks/*` | **yes** (deletes) | WR-T1, WR-D4, DH-5, DH-6 |
| DH-8 | this repo's own test temp folders take the prefix, with a 30-day transition sweep | tests | `TestSupport/TempRoot.cs`, `Core.Tests/Archive/WindowsIdleTests.cs`, `src_vs_code/src/test/**` | no | — |
| DH-9 | docs: `research/module_daemon.md` (three new sections), `module_vs_code.md`, `module_tests.md`, `architecture.md` (the Windows side's new verb and actions, the field-map table regenerated), README, `POST_DEPLOY.md` (the owner's first A24 preview on his own roots is the acceptance item), the boundary rows | docs | as named | no | lands with each story's own docs; the boundary rows land with this plan |

### 5a. Build order

1. **DH-1** — first: it is the warning that was missing.
1b. **DH-1b** — straight after DH-1 (the owner, 2026-10-14): only a 5-minute reading closes the original failure. It waits
   for WR-D4, the Windows watch; if WR-D4 is not there yet, DH-1b is the next story the moment it is. Its extension half
   (the panel and the bar taking the newer reading) also waits for WR-E1, the client that reads the Windows `status`
   (§5's row; WR-E1 depends only on built runner stories, so it runs beside WR-D4).
1c. **DH-2, DH-8** — independent, distro or tests only. DH-2 after M4.
2. **DH-6** — after DH-2 (both change the inspect template; one change, two readers). The Docker sweep works from the distro
   alone; its preview names the other side as "not read from this side" until DH-7.
3. **DH-3** — after M1 and M2; the Windows binary's CLI is usable at once, its panel row after WR-E1.
4. **WR-T1** (the runner plan) — the engine's first Windows-side action; DH-4 and DH-7 build on its path.
5. **DH-4** — after DH-3 and WR-T1.
6. **DH-5** — after DH-4 and the runner's WR-E1, E3, E4 (DH-4 and DH-7 also wait for WR-D4, whose retention sweep retires their run details).
7. **DH-7** — after WR-T1, DH-5 and DH-6; the both-sides sweep flow is its extension half.
8. **DH-9** — each story lands its own docs; the boundary rows land with this plan; promote when DH-7 ships.

## 6. Boundaries with other plans (named on both sides)

| item | this plan | the other plan |
|---|---|---|
| the Windows engine path for a Windows-side action, `%TEMP%` walk + `claude` guard + owner SID + re-validated delete | REUSES it (A24, A26) | PLAN_windows_runner.md WR-T1 builds it (A23) first; if DH-4 is ready first, the shared core is extracted into WR-T1's place and A23 consumes it — never two copies |
| `testsToDelete-*` in `%TEMP%` | A26, 1 h | A23 (`temp.allowGlobs`, 7 d) never selects the prefix and names it "the test-leftover sweep's" |
| `wsl-care-test-*` in `%TEMP%` (the backlog) | DH-8 stops producing new ones | A23's allow-list keeps taking the old backlog |
| the four tasks `\wsl-care\worktrees-scan`, `build-output`, `build-output-orphans`, `test-leftovers`, and the `worktrees` lease | here (DH-5) | the runner's task set (D1's `WindowsTaskDefinitions`, D3's closed argv table) grows by four fixed lines; E3/E4 install and run them like the others; A26 shares A23's `%TEMP%` lease; none takes the run lock |
| every lettered volume's free space | here (`disk.volumeFree`, DH-1) | PLAN_disk_health.md E15: `disk.hostFree` (E15.S2) stays the VHDX-growth question; E15.S1's script is reused unchanged |
| the sidebar's rows (§3.5 of PLAN_disk_health.md, "the sidebar keeps 20 parameters") | the count STAYS 20 (the owner, 2026-10-14): row 15 widened to the worst Windows volume + a short reclaimable-build-output suffix, row 7 (Docker reclaimable) gains the test-leftover count (§8) | E18.W2 builds the slim sidebar with those two widened rows |
| A5's compose exemption | here (DH-2) | PLAN_wsl_care_daemon.md §5 A5 row: `auto.A5` stays off until DH-2 ships |
| the WSL `~/git/_wt` folder size | untouched | `Folders/FolderSizes.cs:57` keeps measuring it; nothing under `~/git` is deleted (`DeletionPolicy.cs:90-91`) |

What is disjoint: the WSL-side worktrees (never cleaned here), whole-worktree removal (§9), databases/queues with the
prefix (the rule leaves them to their own repositories), and every other W-A action.

## 7. Measurements owed (before the defaults they set)

- **M1** — the product's walker over the owner's roots: entries, seconds, logical bytes per kind; reconcile with the
  2026-10-14 count (§1). Sets `worktrees.scanMax*`. (A first read-only measure is in §1: 1.24 M files, 97 s.)
- **M2** — idle by full walk vs by admin files only, per folder, over the owner's roots; the default stays the full walk
  unless the two agree everywhere.
- **M3** — dropped with the `$(Arg0)` route (§3.5: one task per operation).
- **M4** — a captured, redacted `docker ps -a` + `inspect` of a real compose project (labels present in both), and of a running Ryuk container (its labels and image, for §3.6's live-session signal).
- **M5** — the E: volume's size (the keyboard's USB volume, D1), for `disk.volumeFreeMinSizeGib`.
- **M6** — every file extension found inside `bin`/`obj` candidates on the owner's roots, counted, to fix
  `worktrees.buildArtifactPatterns` so that a real build's output is fully covered and nothing else is; the extensions
  outside the list are reviewed by name before the default is written.

Each lands in `research/2026-10-1x_disk_hygiene.md` with its conditions, in the story that needs it.

## 8. Where the new rows appear

**Before E18** (today's panel, `src_vs_code/src/panel/fieldMap.ts:16-42` sections, `:118-164` rows):

- `disk` section: `disk.hostC` "C: free — arrives in E7" (`:140`) is replaced by **`disk.volumes`** — "Windows volumes:
  worst free" (`verdicts[id=disk.volumeFree]`, kind `verdict`), with the short suffix "· N GiB reclaimable in worktrees"
  from the Windows `status`'s `worktrees.reclaimable` (through `WindowsCareClient`; "not configured — *Choose folders…*"
  with no root, "not available yet" until WR-E1).
- `containers` section: the existing Docker-reclaimable row gains "· N test leftovers" from A25's preview row (no new row).
- `cleanup` section: the A25 row in the cleanup table; the A24/A24Orphans/A26 buttons and their last-run lines next to the
  Windows TEMP lines WR-T3 adds.

**In E18** (the mockup at `D:\rsd\_wt\e18-mockup`, not edited by this plan — E18.W1/W2 implement):

- **Sidebar — still 20 rows** (the owner's answer to Q1, 2026-10-14; `sidebar.html:59-71`, "Disk and Docker"): the row
  "Host C: free → cleanups" (`:65`, row 15 of PLAN_disk_health.md §3.5) becomes "Worst Windows volume → cleanups", e.g.
  "D: 4.1 GiB free (2 %) · 38 GiB reclaimable in worktrees" (`disk.volumeFree` + `disk.hostFree` + the short
  `worktrees.reclaimable` suffix — reclaimable meaning only what A24/A24Orphans may delete, §3.3); the row "Docker
  reclaimable → Clean selected" (`:63`, row 7) gains "· 12 test leftovers". No new row. Buttons *Clean build output…* and
  *Sweep test leftovers…* join *Clean…* / *Clean selected…* (`:67-70`); with no root the volume row's suffix reads
  "worktrees not configured" with *Choose folders…*.
- **Full view, Windows tab:** "Volumes and virtual disks" (`full.html:318-335`) gains Free % and a level per volume (the
  verdict's per-volume breakdown); a new card **"AI-agent worktrees"** right after it — per root: worktrees, size, idle > N
  days, locked, orphans, build output (in idle / in locked / in orphans), the scan's age, *Scan*, *Clean build output…*,
  *Clean orphaned folders…*.
- **Full view, WSL tab:** the Docker card (`full.html:146`) gains "Test leftovers" (containers/volumes/networks, and the
  Windows `%TEMP%` count from the other side) with *Sweep test leftovers…* and the in-flight reading.

## 9. Out of scope (named so nobody builds it by accident)

- **Removing whole idle worktrees.** Measured: `git worktree remove` without `--force` refuses a worktree with a submodule
  (39 of 134) and the owner's agents lock theirs (73 of 134). Doing it safely needs `--force` reasoning, unpushed-commit
  checks and the lock contract — a plan of its own, LATER (the owner's answer to Q4, 2026-10-14): an open tail of this plan,
  not part of it.
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
  extension: `a critical disk.volumeFree marks the bar with the volume and its age`, `a warn disk.volumeFree adds no mark`,
  `the mark never lowers a critical bar`, `no other disk.* verdict colours the bar`.
- DH-1b: `The_windows_watch_reads_every_lettered_volume_without_a_process`; `The_sample_carries_disk_volumeFree_with_basis_sample`;
  extension: `the newer of the full run and the windows sample is shown`.
- DH-2: `A5_keeps_a_stopped_container_of_a_compose_project_and_names_the_project`;
  `A5Testcontainers_still_takes_a_labelled_compose_module_container`; `A_container_without_the_label_is_selected_as_before`;
  `The_inspect_template_reads_the_compose_label`.
- DH-3: `A_nested_or_overlapping_root_is_refused`; `A_killed_scans_temp_file_is_removed_at_the_next_start`; `The_distro_refusal_names_the_windows_side`;
  `A_relative_or_network_or_drive_root_is_refused`; `A_root_holding_the_profile_or_an_agent_folder_is_refused`;
  `A_git_file_pointing_back_is_a_worktree`; `A_git_file_pointing_nowhere_is_an_orphan`; `A_locked_file_marks_a_worktree_locked`;
  `A_git_folder_is_a_main_clone_and_never_a_candidate`; `Bin_without_a_project_file_beside_it_is_not_build_output`;
  `Node_modules_without_package_json_is_not_build_output`; `A_nested_node_modules_is_not_counted_twice`;
  `A_fresh_file_deep_inside_makes_the_worktree_active`; `A_fresh_index_in_the_admin_folder_makes_it_active`;
  `A_walk_past_its_budget_is_unknown_never_idle`; `A_junction_inside_a_worktree_is_never_entered` (Windows leg, a sandbox
  junction to a sandbox folder outside the root whose file must not be counted); `The_classifier_starts_no_process`; `The_scan_starts_only_the_three_read_only_git_templates`;
  `Reclaimable_counts_only_candidates_that_pass_every_check_and_names_the_rest`; `Roots_set_reads_stdin_validates_and_writes_nothing_on_a_refusal`;
  `The_distro_binary_refuses_worktrees_scan`.
- DH-4: `A_source_file_under_a_D_root_is_refused_by_the_policy_itself`; `A_hard_linked_read_only_file_is_deleted_without_changing_the_store_file_attributes` (Windows leg, sandbox store outside the root);
  `A_reparse_tag_other_than_junction_or_symlink_retains_the_candidate`; `A_bin_holding_a_nested_repository_is_retained_whole`;
  `Check_ignore_exit_1_is_an_answer_and_128_a_refusal`; `A_batch_is_judged_per_path_not_by_the_exit_code`; `A_negated_ignore_rule_retains`;
  `Git_discovery_that_escapes_the_root_retains_every_candidate`; `An_orphan_with_its_repository_still_there_is_ignore_confirmed`;
  `A_tracked_file_inside_bin_retains_the_candidate`; `A_build_folder_git_does_not_ignore_is_retained`;
  `A_git_refusal_retains_the_candidate_by_name`; `An_empty_answer_with_a_nonzero_exit_retains`; `A_locked_worktree_is_skipped_unless_cleanLocked`;
  `A_candidate_written_since_the_preview_is_retained`; `A_candidate_replaced_by_a_junction_after_the_preview_is_retained`;
  `A_junction_inside_node_modules_is_removed_as_a_link_and_its_target_survives` (Windows leg, sandbox);
  `The_env_file_beside_bin_survives`; `An_ignored_non_build_folder_survives`; `Nothing_outside_the_root_is_ever_deleted`;
  `DeletionPolicy_refuses_every_other_path_under_a_git_root`; `The_git_templates_cannot_be_bent_into_prune_remove_or_clean`;
  `Git_is_never_taken_from_PATH`; `A24Orphans_says_not_confirmed_by_git_first`; `The_checkpoint_is_written_before_the_first_delete`;
  `An_interrupted_candidate_is_taken_first_and_judged_by_its_files`; `While_dry_nothing_is_deleted`; `The_run_detail_counts_logical_bytes_per_outcome`.
- DH-5: `the clean and sweep tasks have no trigger and the scan task one daily trigger`; `each task's argv is a fixed line of the closed table`;
  `a second clean while one holds the worktrees lease ends at once saying already running`; `no hygiene task takes the run lock`;
  `the preview button never spells act or --confirm`; `a running run reads running across a reload`;
  `a run whose heartbeat stopped reads ended without a record, never running`; `every new command has a panel button`; `choose folders sends the picked paths on stdin and spells neither config nor a path on argv`;
  `with no root the row reads not configured with the choose folders button`.
- DH-6: `Npm_test_is_matched_on_the_raw_argv_not_on_Programs`; `Dotnet_exec_testhost_is_matched`; `A_stale_running_testcontainers_container_does_not_block_forever`;
  `A_running_ryuk_blocks`; `A_running_leftover_younger_than_24h_is_kept`; `The_judgement_is_repeated_before_every_batch`;
  `A_name_with_the_prefix_in_another_case_matches`; `A_name_with_the_prefix_later_in_it_never_matches`;
  `The_underscore_form_is_not_matched`; `A_resource_younger_than_an_hour_is_never_selected`;
  `A_running_test_runner_refuses_the_sweep_and_names_it`; `A_running_testcontainers_container_refuses_the_sweep`;
  `An_unreadable_process_table_refuses_the_sweep`;
  `A_running_leftover_is_stopped_then_removed_never_forced`; `A_kept_container_is_never_selected`;
  `Network_prune_is_on_the_never_list`; `The_order_is_containers_volumes_networks`.
- DH-7: `Nothing_under_temp_claude_is_selected_even_with_the_prefix`; `A23_never_selects_the_prefix`;
  `the panel runs neither side unless both previews read no test run`.
- DH-8: `TempRoot_names_its_folder_with_the_prefix`; `The_transition_sweep_takes_both_shapes`.

**Break-it (each red, then restored):** the OR made AND; the volume mark removed from the bar, or keyed on the `disk.` prefix; the size floor ignored; the
compose label dropped from the template; the reparse check removed from the walk; the delete enumerating into a link;
the root containment check removed; the git confirmation skipped; a non-zero git exit read as clean; the fsmonitor
override dropped; `cleanLocked` ignored; the re-validation removed; the checkpoint written after the delete; the age floor
removed; the `WorktreeRoot` deny-by-default removed; the read-only attribute cleared instead of the disposition flag; a
non-junction reparse tag removed as a link; the `/mnt/` translation removed (a WSL worktree reads orphan); check-ignore
judged by the exit code; `GIT_CEILING_DIRECTORIES` dropped; the runner match on `Programs`; any running Testcontainers
container blocking; the per-batch re-judgement removed; the heartbeat's stale cut removed; the artifact-content check removed (a `bin` holding `local.db` deleted); the anchored match replaced by `Contains`; `-f` added to a removal; the in-flight re-check before removal removed;
an `unknown` signal read as clear; the `claude` guard removed.

## 12. Questions

Asked first of the coai question consultants (`d2330f7e`, one row answered); their answers were verified and are taken
above (OR levels and defaults, the Windows binary as the side, the in-flight signals and the age floor, orphans as an
opt-in).

Answered by the owner (through the coordinator, 2026-10-14, after consultant `57c7e54e`):

- **Q1 — answered:** the sidebar stays at 20 rows; row 15 is widened to the worst Windows volume with a short
  reclaimable-build-output suffix, and the test-leftover count joins row 7 (Docker reclaimable). "Reclaimable" is only the
  build output a cleanup may delete, never a worktree's whole size (§3.3, §8).
- **Q2 — answered yes:** a critical `disk.volumeFree` marks the bar by its EXACT id, the `withGpuMark` shape — a mark that
  never lowers the level, its tooltip the free GiB, % and the reading's age (§3.2).
- **Q3 — answered:** roots are chosen by a panel button, *Choose folders…*; default `[]` reads "not configured" with that
  button; the CLI stays as the duplicate (§3.3).
- **Q4 — answered:** whole-worktree removal is a later plan of its own, an open tail here (§9).
- **And:** DH-1b moves straight after DH-1, its WR-D4 dependency explicit (§3.2, §5a).

No question is open.

## 12a. Plan round (coai, 2026-10-14)

Round 1, two reviewers (codex, one chat preset): verdict **proceed** (5 gating against a threshold of 6). Accepted and
written in: a Docker volume has only its name, so it is re-inspected and removed only with the preview's `CreatedAt` and
labels (§3.6); `IgnoreInaccessible` would let an unreadable subtree read idle, so the scan counts access errors (§3.3);
small volumes are judged by percent alone (§3.2); the idle signal does not walk the inside of build output, which has its
own budget per candidate (§3.3); runner processes are matched on argv tokens, never substrings (§3.6). Rejected: (a)
"`$(Arg0)` is substituted only for event triggers" — `IRegisteredTask::Run(params)` documents the `Arg0` pairing for any
run; the plan already measures it (M3) and names the per-id fallback; (b) "`git check-ignore` from the parent folder breaks
root-anchored patterns" — git turns a path given relative to the working directory into the repository-relative path
before matching, so `/bin/` anchored at the root is still applied; the parent folder is chosen so a submodule's own
repository answers for its candidates.

**Own review (an Opus judge, run beside the gate, findings verified against the code before taking them), written in
after the gate's proceed:** `DeletionPolicy` protected nothing under `D:\rsd\_wt` — a deny-by-default `WorktreeRoot` class
is the fix (§3.4, the one blocker); a WSL-made worktree's `/mnt/` pointer would have read as an orphan (§3.3); orphans whose
repository survives get their ignore rules checked (§3.4); no read-only attribute is cleared (pnpm hard links; the
disposition flag instead), only junction and symlink tags are removed as links, a nested repository retains its candidate
(§3.4); `check-ignore` is parsed per path with `-v -n`, exit 1 is an answer, `GIT_CEILING_DIRECTORIES`, `--literal-pathspecs`
and a `rev-parse --show-toplevel` check (§3.4); `CommandTemplate` takes a bare name, so git needs a resolver, and argv
carries no path (§3.4); the runner match is on the raw argv (`Programs` keeps two words), the judgement is repeated per
batch, a stale running Testcontainers container no longer blocks forever, and running leftovers younger than 24 h are kept
(§3.6); one task per operation with a named lease instead of `$(Arg0)` and `IgnoreNew` drops (§3.5); the scan runs from a
task with a daily trigger (§3.3); a key table with trust directions (§3.9); WR-D4 is a dependency of DH-4/DH-7; a heartbeat
ends "running" (§3.5); verdict precedence and the bar's per-id rule (§3.2); nested roots refused; the side text widened;
citations corrected. The `$(Arg0)` finding the gate raised and this plan rejected is moot now: the route is gone.

Round 2 (branch `docs/plan-disk-hygiene-windows-r2`, the owner's answers and the own review in), two reviewers: verdict
**proceed** (4 gating against 6). Accepted: a `bin`/`obj` is deleted only when every file in it is a build artifact
(findings 0 and 2 — an app's SQLite file or a hand-placed `.env` in an output folder; `worktrees.buildArtifactPatterns`,
M6, §3.3); `worktrees roots set` returns a verdict per path and the panel offers to save the accepted ones (finding 3);
`testLeftovers.runnerPatterns` is MachineOnly, no new trust kind (finding 4). Rejected: finding 1, "`check-ignore
--no-index` cannot take `--git-dir` and skips the repository's ignore rules" — `--no-index` only stops git from consulting
the INDEX (so a tracked path is still judged by the patterns); `.gitignore` files in the work tree and `<common>/info/exclude`
are still read, and `--git-dir`/`--work-tree` are global options that apply to it as to any command. DH-4's RED test
`An_orphan_with_its_repository_still_there_is_ignore_confirmed` runs that exact argv against a real throw-away repository
in a `testsToDelete-` folder, so a wrong flag is caught before anything ships.

**A recording error, named:** the `resolve` call swapped two indices — the gate's record shows finding 4 (check-ignore)
accepted and finding 5 (the idle budget) rejected, the reverse of the decisions above. The session had already moved to
its code stage, so the record could not be corrected; this paragraph and the text of §3.3 are the decisions as made.

## 13. Definition of Done

- [ ] DH-1: `disk.volumeFree` judges every lettered volume above the size floor; a critical one marks the bar by its exact id; the `disk.hostC`
      arriving row is gone; README says the defaults are starting values.
- [ ] DH-2: A5 never selects a compose container (a RED test proved it did); `auto.A5` still off.
- [ ] DH-3: the Windows `status` carries `worktrees` and `worktrees.reclaimable`, whose bytes are only what A24/A24Orphans
      would be allowed to delete; the scan starts no process but the three read-only git templates; roots are set and
      validated through `worktrees roots set` on stdin (the *Choose folders…* button that feeds it is DH-5's).
- [ ] DH-4: A24 and A24Orphans delete only the positive rule's folders, never through a link, never outside a root, with git
      confirming A24's; `DeletionPolicy` refuses every other path under a configured root BY ITSELF; a WSL-made worktree is
      never read as an orphan; no attribute of a file outside the root changes; the Fable own review ran.
- [ ] DH-5: every action has a panel button with a dry-run preview; roots are chosen by *Choose folders…*; the clean and sweep tasks are on-demand only, the scan task daily + on demand.
- [ ] DH-6/DH-7: the sweep refuses while any signal reads `inFlight` or `unknown`, never touches anything younger than an
      hour, and the preview shows how it decided.
- [ ] DH-8: this repo's test temp folders carry `testsToDelete-`.
- [ ] M1, M2, M4, M5, M6 recorded in `research/` with their conditions before the defaults they set.
- [ ] Every key of §3.9 has its default, range and trust in `contracts/config-keys.json` and a `NumbersAreConfigurationTests` row.
- [ ] Docs: `module_daemon.md`, `module_vs_code.md`, `module_tests.md`, `architecture.md`, README, POST_DEPLOY; both sides of
      every boundary in §6 written; `node .agents/conventions/tools/plan-lifecycle.mjs` clean.
- [ ] DH-1b: the Windows watch carries `disk.volumeFree` every 5 minutes, and panel and bar show the newer reading.
- [ ] The plan promoted to `research/` when DH-7 and DH-1b ship; its open tail (whole-worktree removal, §9) named in the
      status line as a plan still to write.

## Epics

> The split is Fable's (model `fable`, the *Plan → epics* row of `.agents/conventions/common/subagent-models.md`), made
> 2026-10-14 after the question consultant was asked what should drive it (coai consult `3d4d02e6`: three OUTCOME epics —
> not a split by side, not one by runner availability — taken, and checked against §3, §5, §5a, §6, the runner plan's §5 and
> §14, and the extension's client code; where this section departs from the advice it says so). The numbers continue
> PLAN_disk_health.md's E15–E18 and are identifiers, not the build order: E21's distro half is built first of all (§5a 1c–2).
> Three epics, so the cornerstone quota is Q = ⌈2·3/5⌉ = 2. None of the three starts before the owner's go.

### E19 — See it (DH-1, DH-1b, DH-3)

Stories: [PLAN_disk_hygiene_windows_E19.md](PLAN_disk_hygiene_windows_E19.md) (2026-10-15; E19.S1, S2, S2b — the narrow
Windows `status` read carved out of WR-E1 — S5, S3, S4). DH-1b keeps the watch's 5-minute sample and gains a live fallback in the
Windows `status` (its §3 decision 1).

- **Goal.** The owner is warned before any lettered volume fills — within five minutes of the filling, not four hours — and
  can see, per root, where the space went: how many worktrees, how idle, how much build output, which folders git can no
  longer vouch for. Nothing in this epic deletes.
- **Scope.** DH-1 (`disk.volumeFree` and the bar's mark), DH-1b (the Windows watch's 5-minute native reading; the newer of the
  two readings wins), DH-3 (the worktree report: roots and `worktrees roots set/clear`, the classifier, the walk that counts
  access errors, idle, the git resolver and its three read-only templates, `worktrees scan --json`, `worktrees.reclaimable`).
- **Depends on.** WR-D4 (DH-1b: the Windows watch — real and explicit, DH-1b cannot ship before it); WR-E1 (the EXTENSION
  halves of DH-1b and DH-3: today `WindowsCareClient` runs the bundled exe with `--version` alone,
  `src_vs_code/src/client/WindowsCareClient.ts:17`, so the panel can read a Windows `status` — the sample, `worktrees` — only
  through the client WR-E1 widens; WR-E1 waits on nothing unbuilt); M1, M2 (DH-3's budgets and the idle default); M5 (DH-1's
  size floor). NOT on DH-5 (E20): until E20 the roots are set through DH-3's own `worktrees roots set` verb on stdin (or
  the `config set` duplicate) and the scan is started by hand, `worktrees scan --json`; the panel row reads `worktrees.json`
  through `status` and says "not configured" without a button until DH-5 adds *Choose folders…*. NOT on WR-T1, WR-E3, WR-E4.
- **Priority path.** **DH-1 → WR-D4 → DH-1b** (WR-E1 beside WR-D4; both depend only on built runner stories). DH-3 runs
  beside that path after M1/M2 — its CLI needs no runner story. E19 is the first epic to start and, by the runner's own
  order (WR-D4 is its step 3), the first to finish.
- **Done when (provable).** (1) DH-1: the pure rule over the recorded D1 sample (D: 195.3 GiB, 30.6 GiB free) reads `warn`
  naming D:, and over 4.1 GiB / 2.1 % reads `critical` with D: first; the E: USB volume is not judged (M5 recorded, below the
  floor); a 16 GiB volume with 12 GiB free is `ok`; one unread volume beside a critical one leaves the verdict `critical`,
  beside ok ones makes it `unknown`; the bar carries ` · D: low space` with the free GiB, % and the reading's age only on a
  critical `disk.volumeFree` (`a warn disk.volumeFree adds no mark`, `the mark never lowers a critical bar`, `no other disk.*
  verdict colours the bar` green beside the held `statusBar.test.ts:61`); the `disk.hostC` "arrives in E7" row is gone; README
  says the defaults are starting values. (2) DH-1b: the REAL `\wsl-care\watch` task on the owner's machine (WR-D4
  registered) writes a `disk.volumeFree` sample with `basis {source: sample, ageSeconds}` every `mcpWatchdog.periodMinutes`
  and starts no process doing it (`The_windows_watch_reads_every_lettered_volume_without_a_process`); the panel and the
  bar show the NEWER reading — a 4-hour-old `collect` verdict loses to a 1-minute-old sample, and the reverse (`the newer of
  the full run and the windows sample is shown`). (3) DH-3: `worktrees scan --json` over the owner's real roots (set through
  the verb) completes inside its budget with M1's entries / seconds / bytes recorded; the report distinguishes `worktree` /
  `locked` / `orphan` / `foreignPointer` / `mainClone` / `notGit`, a WSL-made `/mnt/<letter>/` pointer reading `worktree` and a
  `/home/…` pointer `foreignPointer` (never a candidate); `worktrees.reclaimable` counts ONLY candidates that pass every §3.4
  check at scan time, everything else under `retained{reason, count, bytes}`; the classifier starts no process and the scan
  starts only the three read-only templates (the two tripwire tests); a walk past its budget or an unreadable subfolder
  reads `unknown`, never `idle`; the distro binary refuses the verb naming the Windows side; `roots set` refuses nested,
  relative, network, drive-root, profile and agent-folder roots and writes nothing on any refusal. (4) No `auto.*` key is
  added or changed by E19; nothing it ships deletes.
- **Cornerstone areas.** None in the quota. DH-1's rule is pure and fully named by its RED list; DH-3's selection mistakes
  are caught twice downstream by design — DH-4's per-candidate re-validation and the policy's own re-run of the predicate
  (§3.4) — so its guarantees are proven where they bite, in E20's cornerstone. DH-3 keeps its Fable OWN REVIEW (§5's column:
  it adds git and writes a key that admits deletion), which is outside the quota.

### E20 — Clean it (DH-4, DH-5)

- **Goal.** One button frees the idle worktrees' build output, a second the orphaned folders' — each with a dry-run preview,
  run by the owner's own task — and the deletion policy refuses, by itself, everything under a root that is not an admitted
  `bin` / `obj` / `node_modules` candidate.
- **Scope.** DH-4 (A24 + A24Orphans: the selection reused from DH-3, `DeletionRule.WorktreeRoot` + `WorktreeBuildOutput`,
  the no-link bottom-up delete, re-validation, budgets, checkpoint, run detail, dry window) — **cornerstone**; DH-5 (the
  four tasks, the `worktrees` lease, `tasks preview …`, the heartbeat, *Choose folders…*, the panel's buttons and lines).
- **Depends on.** DH-3 (E19's selection contract and git questions — NOT on DH-1 or DH-1b); WR-T1 (the engine's Windows path:
  the walk, the re-validated delete, the cursor — or, if DH-4 is ready first, §6's extraction: the shared core moves into
  WR-T1's place and A23 consumes it, never two copies); WR-D4 (the run-retention sweep that retires A24's run details, §10);
  WR-E1, WR-E3, WR-E4 (DH-5's previews, install and run-now; WR-E2 arrives with WR-E3 by the runner's order); M6 (the
  artifact list). E20 therefore waits for the runner's steps 5 (WR-E3/E4) and 7a (WR-T1).
- **Priority path.** WR-T1 → DH-4 → DH-5 (WR-E1/E3/E4 may land any time before DH-5).
- **Done when (provable).** (1) The policy refusals are tested INDEPENDENTLY of selection: `DeletionPolicy` alone, with no
  selection code in the test, refuses a source file under a configured root and every path under a worktree root or `~\git`
  that is not an admitted candidate (`A_source_file_under_a_D_root_is_refused_by_the_policy_itself`,
  `DeletionPolicy_refuses_every_other_path_under_a_worktree_root_or_a_git_root`). (2) Link confinement, on the Windows leg
  in a sandbox: a junction inside `node_modules` is removed AS A LINK and its target survives; any reparse tag but junction
  and symlink retains the candidate; a candidate replaced by a junction after the preview is retained; nothing outside the
  root is ever deleted. (3) Hard links: a read-only pnpm hard link is deleted through the disposition flag and the store
  file's attributes are unchanged (`A_hard_linked_read_only_file_is_deleted_without_changing_the_store_file_attributes`).
  (4) Recovery after interruption: the checkpoint is written before the first delete; a run killed mid-candidate takes that
  candidate first next time, judges it by its remaining files, finishes or retains it, and reads "interrupted", never
  "completed". (5) git: exit 1 an answer and 128 a refusal; a batch judged per path; a negated rule, a tracked file, a
  discovery that escaped the root, any refusal — each retains by name; A24Orphans' preview opens with "not confirmed by git"
  (or "ignore rules confirmed; tracked files NOT checked"); the templates cannot be bent into `prune` / `remove` / `clean`; git
  never from `PATH`. (6) Content: a `bin` holding `local.db` or `.env` is retained and names the file; `node_modules` is
  exempt. (7) Nothing is deleted while dry; `auto.A24` / `auto.A24Orphans` default off, trust `Off`. (8) DH-5: the four tasks
  render as fixed lines of the closed table, the clean and sweep tasks with no trigger and the scan task one daily trigger; a
  second clean while the `worktrees` lease is held ends at once naming the holder; no hygiene task takes the run lock; the
  preview button never spells `act` or `--confirm`; "running" survives a reload and a stopped heartbeat reads "ended
  without a record"; *Choose folders…* sends the picked paths on stdin, spells neither `config` nor a path on argv, and a
  mixed verdict offers *Save the N accepted folders*; every new command has its `COMMAND_BUTTONS` entry. (9) Acceptance: the
  owner's FIRST real preview on his own roots — A24 and A24Orphans, dry — is recorded in `POST_DEPLOY.md` (DH-9's item, landed
  with DH-5) with the counts per outcome, the retained reasons and the logical bytes, before `dryRun` is lifted on these
  paths.
- **Cornerstone areas — DH-4, the deletion boundary.** Fable writes the scenarios and the brief for: `DeletionRule.WorktreeRoot`
  deny-by-default and the single `WorktreeBuildOutput` admission with the predicate re-run inside the policy; the content
  check (every file a build artifact, `node_modules` exempt); the no-link bottom-up delete (the disposition flag, never an
  attribute change; junction and symlink tags only; the extended-length prefix); per-candidate re-validation; the checkpoint
  and the interrupted run; and the consumption of DH-3's selection contract — the git questions' per-path parse, exit codes,
  ceiling and clean environment are proven HERE, where they decide a delete. Sonnet codes, Opus reviews; §5's Fable own
  review of DH-4 stays. DH-5 is not a cornerstone: its risks are the runner's (WR-D3's closed argv table, WR-E3's tripwire),
  built or owned there; it keeps §5's Fable own review.

### E21 — Test leftovers and A5 (DH-2, DH-8, DH-6, DH-7, DH-9)

- **Goal.** Everything a test run names `testsToDelete-…` is swept — containers, volumes and networks in the distro, `%TEMP%`
  folders on Windows — only when no test run is in flight on EITHER side and never younger than an hour; A5 can be automated
  because it never selects a compose container; this repository's own tests leave prefixed folders; the plan closes.
- **Scope.** DH-2 (A5's compose exemption), DH-8 (this repo's test-temp prefix), DH-6 (A25 and `TestRuns.Judge`) —
  **cornerstone**; DH-7 (A26 on WR-T1's engine, A23's exclusion, the both-sides panel flow); DH-9 (the closing docs and the
  promotion; its POST_DEPLOY A24 item lands with E20's DH-5).
- **Depends on.** The distro half — DH-2 (after M4), DH-8, DH-6 (after DH-2: one inspect-template change, two readers) — on
  NOTHING of the runner: they start at once, beside E19, so the A5 data-loss fix is never held by this epic's number (§5a
  1c–2); DH-6's preview names the other side "not read from this side" until DH-7. The Windows half — DH-7 — on WR-T1,
  WR-D4, DH-5 (E20) and DH-6; it is the last story of the plan (§5a 7).
- **Priority path.** DH-2 → DH-6 (DH-8 any time); then, after E20: DH-7 → DH-9.
- **Done when (provable).** (1) DH-2: a RED test on M4's redacted compose fixture showed A5 selecting a stopped compose
  container; then it is kept and named "a container of compose project <name>" in the preview; A5Testcontainers still takes a
  labelled compose-module container; `auto.A5` still off (`ConfigSchemaTests.cs:52`). (2) DH-8: `TempRoot` and
  `WindowsIdleTests` name `testsToDelete-wsl-care-…`; the transition sweep takes both shapes under a dated comment; the
  extension's `mkdtemp` prefixes audited. (3) DH-6: the match is anchored and case-insensitive — a prefix later in the name
  and the underscore form never match; nothing younger than `testLeftovers.minAgeMinutes` is ever selected, whatever the
  signals say; any `inFlight` or `unknown` signal — a runner matched on the RAW argv (`npm test`, `dotnet exec … testhost.dll`)
  and named by pid and program, a running Ryuk or a young labelled container, an unreadable process table or Docker list —
  ends the run with its reasons, and the judgement is repeated before EVERY batch
  (`The_judgement_is_repeated_before_every_batch`); a stale running Testcontainers container does not block forever; a
  running leftover younger than 24 h is kept, an older one stopped then removed, never `-f`; a volume recreated under the
  same name after the preview is kept; the order is containers, volumes, networks; `network prune` is on the never-list; the
  preview shows every signal with its reading. (4) DH-7: nothing under `%TEMP%\claude\` is selected even with the prefix; A23
  never selects the prefix; the panel runs neither side unless BOTH previews read `inFlight: none` — a conjunction of two
  Core verdicts, no threshold in the extension. (5) E21 is done only when DH-7 works; DH-9 then promotes the plan to
  `research/` with its open tail (whole-worktree removal, §9) named in the status line.
- **Cornerstone areas — DH-6, the in-flight contract.** Fable writes the scenarios and the brief for `TestRuns.Judge`, the
  one judge both binaries compile and DH-7 and the panel's conjunction consume: the four signals and `unknown` blocking; the
  argv-token runner match on the raw command line (never `Programs`, never a substring); the Ryuk / young-labelled rule
  (never "any running labelled container"); the age floor as the one guarantee a snapshot cannot give; the per-batch
  re-judgement — and A25's removal: stop-then-rm never `-f`, the volume re-inspect by `CreatedAt` + labels, the order, the
  never-list's third prune. A wrong judge sweeps under a running test on both sides and nothing downstream re-checks it —
  that is where a mistake compounds. Sonnet codes, Opus reviews; §5's Fable own review stays. DH-7 consumes the judge and
  WR-T1's engine and adds no deletion rule of its own (it keeps §5's Fable own review); DH-2 and DH-8 are bounded by their
  RED lists.

### Order, ledger, stories documents

**Build order** (as §5a, which this split leaves as it is): E19 and E21's distro half start together — DH-1, then DH-2 / DH-8
and DH-6, DH-3 after M1/M2; WR-D4 and WR-E1 next, then DH-1b (the priority path **DH-1 → WR-D4 → DH-1b**); WR-T1; E20 (DH-4,
then DH-5 after WR-E3/E4); E21's DH-7; DH-9. The plan is promoted when DH-7 and DH-1b have shipped (§13).

Cornerstones: 2 of 2 — DH-4, DH-6 (DH-4: the deletion boundary — the only code that removes files under a configured root,
and the policy's deny-by-default is what makes every other path there safe; DH-6: `TestRuns.Judge`, the one in-flight
contract both binaries and both sweeps rely on, with no second check behind it). Q = ⌈2·E/5⌉ = 2 for E = 3; a third mark
goes to the operator, not into this file.

When an epic starts — not now — its stories move out of §5 into `todo/PLAN_disk_hygiene_windows_E<n>.md` (its own status
line and `todo/README.md` row, a link here and from the epic above), written by an Opus judge, which marks the cornerstone
story `model: fable` within the ledger in the same commit; the parent and the three stories documents promote together.
