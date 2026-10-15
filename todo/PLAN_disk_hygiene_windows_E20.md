# PLAN — E20 "Clean it": A24/A24Orphans remove build output in idle worktrees, and the button route

> Status: **plan only, nothing implemented yet, 2026-10-15.** Scope: the stories of epic E20 of
> [PLAN_disk_hygiene_windows.md](PLAN_disk_hygiene_windows.md) (DH-4, DH-5). The owner's yes for E20 and E21 came on
> 2026-10-15. Written by the Opus judge (epic → stories); the cornerstone **DH-4** (`model: fable`, the parent's ledger
> `Cornerstones: 2 of 2 — DH-4, DH-6`) has its scenarios, brief and the security judgement on how a Windows action runs
> unelevated written by Fable (§4).
>
> Parent: [PLAN_disk_hygiene_windows.md](PLAN_disk_hygiene_windows.md) § *Epics* — E20. Siblings:
> [PLAN_disk_hygiene_windows_E19.md](PLAN_disk_hygiene_windows_E19.md) (E19.S3/S4 build the selection this epic deletes by),
> [PLAN_disk_hygiene_windows_E21.md](PLAN_disk_hygiene_windows_E21.md). Runner plan: [PLAN_windows_runner.md](PLAN_windows_runner.md).

## 1. What this epic must make true

The parent's E20 *done when*: the deletion policy refuses, by itself, every path under a configured root that is not an
admitted candidate; link confinement, hard-link attributes and interrupted runs proven on the Windows leg; nothing deleted
while dry; every deletion only through a panel button with a dry-run preview; the owner's first real preview on his roots
recorded in `POST_DEPLOY.md` before `dryRun` lifts.

## 2. Facts (verified 2026-10-15 on main `e3c6e364`, two Sonnet scouts; files opened; an Opus pre-review checked 22 references)

| Fact | Where |
|---|---|
| No action runs on the Windows side today: all 23 have `Sides = [Wsl]`; WR-T1 (A23, "the engine's first Windows action") is not built | `Actions/ActionRegistry.cs:25-48`, `Actions/ICleanupAction.cs:231`, `PLAN_windows_runner.md` §5 |
| **`act` requires root, and root on Windows means ELEVATED**, while the Windows tasks run unelevated and the Windows `watch` / `archive run` refuse elevation (81) — so `act A24` from a task would be refused (77) as the code stands; the engine also takes THE run lock on every execute (`ActionEngine.cs:88`) | `Cli/Commands/ActCommand.cs:100-107`, `Hosting/ProcessPrivilege.cs:30-33`, `Scheduler/WindowsTaskDefinitions.cs` (LeastPrivilege), `Cli/Commands/WatchCommand.cs:58-64` |
| The engine defers every non-urgent action on Windows ("CPU use is unknown on the Windows binary") and has no target user there | `Actions/Engine/ActionEngine.cs:219-224,620-623` |
| `DeletionPolicy` protects only `%USERPROFILE%\git` among git roots on Windows; the only per-action admission precedent is the archive's permit-keyed one | `Files/Deletion/DeletionPolicy.cs:36,75,90-91,96-100,106-122,168`, `Hosting/WindowsHostPaths.cs:89,92` |
| `IFileSystem.DeleteDirectory` is a recursive .NET delete; architecture scanners ban raw deletes and `SetFileInformationByHandle` outside the seam files | `Files/PhysicalFileSystem.cs:620-624`, `tests/WslCare.Core.Tests/ArchitectureTests.cs:19`, `ArchitectureTests.ArchiveSeam.cs` |
| Handle primitives exist (`OpenToChangeWindows` with `OpenReparsePoint`, `MarkForDeletion` = `FileDispositionInfoEx` DELETE|POSIX), but no `IGNORE_READONLY_ATTRIBUTE`, `IsPlainFile` refuses hard links, no reparse TAG reading, no `\\?\` prefixing for raw `CreateFile` | `Files/BeneathWrites.cs:42-45,273-347,381-404` |
| The nearest checkpoint is the archive's `inflight.json`; the nearest lease is the archive's `SideLease` | `Archive/ArchiveState.cs`, `Archive/SideLease.cs:36` |
| DH-5's route is not built: only `watch` and `archive` tasks exist, the task XML always writes one logon trigger, no `tasks preview`, no `src_vs_code/src/windowsTasks/*` (WR-E2/E3/E4), WR-E1 only on its branch | `Core/Scheduler/WindowsTask.cs:7-34`, `WindowsTaskDefinitions.cs:46-131`, `Core/Scheduler/TaskWorkers.cs:35`, `Cli/CommandLine.cs:301-304` |

## 3. Stories

### E20.S1 = DH-4 — A24 + A24Orphans — **cornerstone, `model: fable`**

The scenarios, the brief and the security judgement are Fable's: §4 below.

- **Depends on:** E19.S3 (classifier, scan rule, `worktrees.*` keys) and E19.S4 (git questions — the git checks are RE-PROVEN
  at delete time per §4 B.1's R0–R6, not only at scan time); M6 (the artifact list, measured 2026-10-15); the E19 stories'
  decisions 4, 5 and 11 (names on argv, no user git config, the artifact time skew).
- **Order with WR-T1:** DH-4 builds the shared Windows engine path FIRST — the per-side privilege rule, the `ITreeRemoval` seam
  and the handle primitives (§4 B.3/B.5) — named so WR-T1's A23 consumes them; the runner plan's WR-T1 row says so in the
  same commit (both sides). Never two copies, and never "instead": WR-T1 still builds A23 on it.
- **The run lock:** `act` keeps taking THE run lock (the engine's own, `ActionEngine.cs:88`); while a clean runs, the Windows
  watch answers busy and writes no volume sample — E19.S2's live fallback in the Windows `status` covers that gap. The
  parent's "none takes the run lock" (§3.5) is amended for `act`-bodied tasks.

### E20.S2 = DH-5 — the button route — waits for WR-E1 and WR-E3/E4

- **Depends on:** E20.S1; the runner's WR-E1 (staged exe, `tasks` in the client; PR #149), WR-E2/E3 (the folder read,
  install/remove flows), WR-E4 (run-now through the task's own `Run`), WR-T2 (where `tasks preview` comes from); WR-D5 is built
  (#147). Scenarios: the parent's §11 DH-5 list.
- **Brief (when unblocked).** The four tasks of the parent's §3.5 (`worktrees-scan` on a logon trigger repeating every `worktrees.scanEveryHours` after `worktrees.scanLogonDelayMinutes`, plus on demand; `build-output`,
  `build-output-orphans`, `test-leftovers` on demand only — the task XML gains a no-trigger and a daily-trigger form, the
  render golden and `WindowsTaskDefinitionsTests.The_closed_set_of_tasks_is_watch_and_archive` widened); their fixed argv lines
  in `TaskWorkers`; `tasks preview build-output|build-output-orphans|test-leftovers` (read-only); no new lease and no new heartbeat — the `act`
  body already has THE run lock (busy → "already running" from the lock's refusal, holder from `running.json`) and the
  engine's `Heartbeat` / `running.json` / `running.heartbeatSeconds` with their wedged and dead readings (`ActionEngine.cs:29-30,237`,
  the `status-running-dead/wedged` goldens) — reused, never copied (Opus pre-review; the parent's `worktrees.heartbeatSeconds`
  is dropped); the scan task, which is not `act`, takes a local `TryLockExclusive` lock (the `RunLock.cs` / `McpCpuLedgerLock.cs`
  shape) with a small holder record, never `SideLease` (a cross-host archive lease); *Choose folders…* (`showOpenDialog` → `worktrees roots set` on stdin, the per-path verdict list with *Save the N
  accepted folders*); the panel's buttons and last-run lines; `COMMAND_BUTTONS` entries.
- Fable security judgement before code (it runs deletes).

## 4. DH-4 — the cornerstone spec and the security judgement (Fable, 2026-10-15)

Written by Fable (model `fable`) on 2026-10-15 as the security judgement and the cornerstone spec; inserted verbatim (headings demoted). Where it and §3 disagree, this section wins for DH-4.

> Written by Fable, 2026-10-15, for the Sonnet coder of story DH-4 of `wsl_care` (E20 "Clean it", the deletion-boundary
> cornerstone). Sources: `todo/PLAN_disk_hygiene_windows.md` §3.3, §3.4, §3.5, §4, §5, §10, §11, *Epics* E20 (checkout
> `D:\rsd\_wt\wc-e19`, branch `docs/plan-disk-hygiene-e19` at `6c517626`, which carries main `e3c6e364`);
> `todo/PLAN_disk_hygiene_windows_E19.md` §3 decisions 3–6, 8, 9, 11 and §4 E19.S3 / E19.S4; the measured facts
> (`git-probe-findings.md`, `m12/summary.txt`, `m12/m6_ext.csv`, `m12/m6_hits.txt`); every code fact below was opened in
> the checkout. Read-only everywhere; one probe was run in the scratchpad (§A.6) and nothing in any repository was touched.
>
> **What is already decided elsewhere and only CONSUMED here:** the selection rule and the classifier (E19.S3, branch
> `feat/wc-e19-s3-worktree-report`, uncommitted stubs in `D:\rsd\_wt\wc-e19-s3` — the TYPES exist and are named below; their
> bodies are being built now), git's three read-only questions (E19.S4, branch `feat/wc-e19-s4-git-questions`, nothing
> written yet — its API is named by the stories document and wrapped here behind one seam so DH-4's tests do not wait for
> it), the four tasks / the `worktrees` lease / the heartbeat (DH-5), the content check's exact list (M6).

---

#### A. The security judgement

##### A.0 Threat model (the whole of it, in six lines)

1. **A wrong configuration**, not a hostile one: `worktrees.roots` is a user-layer key (E19 §3 decision 3) that a hand edit, a
   stale value, or a typo can point at something valuable — the profile, a repository's source, another product's data.
2. **A concurrent process of the SAME account** — a build, an AI agent, pnpm, a test — writing, renaming, or linking inside a
   candidate between the moment it is judged and the moment it is removed (TOCTOU).
3. **Filesystem shapes that make a name mean another place**: junctions and symlinks (into `~\.claude`, into `C:\`), hard
   links into a pnpm store OUTSIDE the root (the file's attributes are the store's), cloud placeholders / dedup / container
   reparse tags, long paths, 8.3 short names.
4. **A kill mid-delete** (a reload of VS Code, a sleep, Task Scheduler's deadline): a half-removed candidate must never be
   re-judged idle by folder times and must never read "completed".
5. **Privilege**: the product must never have more rights than the owner sitting at the keyboard. An elevated or SYSTEM run
   would delete past the ACLs that are the owner's only boundary on a shared disk.
6. **Not in the model:** another account planting links under the owner's roots (the roots are the owner's own folders; the
   ACL is the boundary; the policy's link rules still hold), and git itself as an attacker (E19.S4's clean environment).

##### A.1 How `act A24` / `act A24Orphans` may run on the Windows binary (the decision)

**A per-side privilege rule, inside `act`; not a dedicated body.** Today `ActCommand.Refusal` (`Cli/Commands/ActCommand.cs:102-107`)
is `NotRoot ?? Unbuilt ?? OtherSide ?? ObserveOnly`, and `NotRoot` requires `host.Privilege.IsRoot` — which on Windows means an
ELEVATED token (`Hosting/ProcessPrivilege.cs:30-33`, `Environment.IsPrivilegedProcess`). The Windows tasks run unelevated
(`Scheduler/WindowsTaskDefinitions.cs:25-27, :101`: `InteractiveToken`, `LeastPrivilege`), and the Windows `watch` and `archive`
verbs already REFUSE an elevated process with exit **81** (`WatchCommand.cs:57-64`, `ArchiveRunCommand.cs:22-36`, `ExitCode.NotAsRoot`).

The rule DH-4 adds — `Cli/Commands/ActPrivilege.cs` (new, a pure function, unit-tested):

| side (`host.Paths.Side`) | `host.Privilege.IsRoot` | answer |
|---|---|---|
| Wsl | false | `(NeedsRoot 77, "needs root: every act runs as root (plan 15c #0) and {Basis}; nothing was done")` — unchanged |
| Wsl | true | go on |
| Windows | **true** (elevated, SYSTEM, or the sandbox claim `WSL_CARE_SANDBOX_PRIVILEGED=1`) | `(NotAsRoot 81, "act runs in this user's own, NOT elevated process — an elevated (administrator) one is refused (PLAN_disk_hygiene_windows.md §3.4; the runner plan's \"nothing elevated\", PLAN_windows_runner.md §4): the Windows actions delete only what this user's own token may delete. Run it from a terminal that is not elevated. Nothing was touched.")` |
| Windows | false | go on, **and** a second explicit check: `WindowsIdentity.GetCurrent().User` is `S-1-5-18` (SYSTEM) or the token's `IsSystem` → the same 81 (belt and braces: a task someone re-registered as SYSTEM must not get through on the strength of one API) |

- The refusal is asked FIRST, before the run lock, `running.json` or any state is touched — as `NotRoot` is today
  (`ActCommand.cs:49-54`). Exit 81 is the existing code; the extension already reads it (`ExitCode.cs:79-82`).
- **Why not a dedicated body:** `act` already owns the run lock, `running.json`, the heartbeat, the reconcile, the dry decision,
  the run detail and the history line — the "interrupted, never completed" guarantee of §3.4 is the engine's, and a second
  body would be a second copy of it. The per-side rule is one function; the engine's Windows path then runs for A24 exactly as
  it will for A23 (WR-T1).
- **The sandbox claim**: `ProcessPrivilege.Decide` keeps answering "root" for `WSL_CARE_SANDBOX_PRIVILEGED=1`; on the Windows
  side that claim therefore REFUSES `act` (it means "elevated"). The scenario harness drives Windows `act` flows with
  `ScenarioHome.ClaimsRoot = false` (`tests/WslCare.Scenarios/ScenarioHome.cs:80`), never with the claim.
- **What an elevated or SYSTEM caller gets:** exit 81, the one sentence above on stderr, no JSON body, no lock taken, no file
  written, no log line beyond the refusal at Warning. The task's own XML cannot be elevated (`RunLevel LeastPrivilege`,
  `LogonType InteractiveToken`), so the only way to reach this branch is a person in an elevated terminal — who is told how
  to run it instead.

##### A.2 Binding A24's deletions to the configured roots — the policy must not trust the stored value

`worktrees.roots` is **UserOwned** (E19 §3 decision 3; `ConfigLoader.cs:255` holds the list, today `archive.baseFolder` only):
an invalid value is a notice, never observe-only, and the value is judged **at every use**. For DH-4 "every use" is three
independent judgements, none of which trusts the other:

1. **Phase two of the host** (`CliHost.WithAgentExtras`, `Cli/CliHost.cs:107-146`, the precedent of plan §15q R2.2 review B2:
   "every shape-valid entry of this side, whether or not it passes the walk's rules") gains the worktree roots: every entry of
   `worktrees.roots` that is shape-valid (absolute, a drive letter, no `\\?\`, no UNC — S3's `TextRule`) goes into
   `WindowsHostPaths.WorktreeRoots.Denied`; every entry that `WorktreeRoots.Judge` (S3) ACCEPTS goes into
   `WindowsHostPaths.WorktreeRoots.Admitted`. `Rewire` rebuilds the file system over the new layout, so the `DeletionPolicy`
   (built from `ProtectedRoots.From(paths, …)`, `PhysicalFileSystem.cs:55`) learns both lists — as it learns the manual agents'
   folders today. Linux paths carry empty lists.
2. **The action, at preview AND at run start** (§B.1): `WorktreeRoots.Judge(paths, files, WorktreeRoots.ThisMachinesDrive, settings.MaxRoots, configured)`
   again; a refused root is skipped with its rule in the run's notes; only accepted roots are scanned. The action never reads
   `paths.WorktreeRoots` to decide — it judges for itself.
3. **The policy, per candidate and per entry** (§B.2): a path under a DENIED root is refused unless the request carries the
   `WorktreeBuildOutput` permit, names an ADMITTED root, and the facts the SEAM gathered (never the action) satisfy the
   predicate. A root the action accepted but phase two did not (the two loads disagree — impossible from one load, possible
   under a test) is refused by the policy: fail closed.

So a hand-edited `D:\` or `C:\Users\<user>` is refused by S3's rules (`drive-root`, `profile`) in all three places; a root that
is a junction is refused (`link-on-the-way`); a root under `~\git` is accepted (plan §3.3 allows it) and the GitFolder rule then
admits ONLY the `WorktreeBuildOutput` permit beneath it (§B.2).

##### A.3 Hard links — delete the NAME, never touch the store file

Measured in the scratchpad on 2026-10-15 (§A.6): a read-only hard link (`mklink /H`, `ReadOnly` set on the shared file record)
opened with `DELETE | FILE_READ_ATTRIBUTES`, share `R|W|D`, `FILE_FLAG_BACKUP_SEMANTICS | FILE_FLAG_OPEN_REPARSE_POINT`, then
`SetFileInformationByHandle(FileDispositionInfoEx, DELETE | POSIX_SEMANTICS)` answers **error 5**; with
`| IGNORE_READONLY_ATTRIBUTE (0x10)` it answers **0**, the link's name is gone at close, and the store file is still there with
`ReadOnly` still set. So: the flag, always; `SetAttributes` / `File.SetAttributes` / `FILE_BASIC_INFO` never — the architecture
scan gains `SetAttributes` and `FileBasicInfo` as forbidden outside the seam (§B.3.4). `WindowsFileInfo.IsPlainFile`
(`BeneathWrites.cs:45`, refuses `Links > 1`) is the ARCHIVE's rule and is NOT applied here: a pnpm `node_modules` is hard links by
design, and the deletion of one name is exactly what is wanted. `Links` is recorded in the run detail's counts (`hardLinkedNames`).

##### A.4 Reparse points — a tag read on the handle, two tags removed as links, everything else retains

The product reads NO reparse tag today (`FSCTL_GET_REPARSE_POINT` / `IO_REPARSE_TAG_*` are absent from `src_daemon/src`; the walks
skip `FileAttributes.ReparsePoint`, `TreeWalk.cs:105-110`). DH-4 adds `BeneathWrites.ReparseTag(SafeFileHandle)`:
`DeviceIoControl(handle, FSCTL_GET_REPARSE_POINT = 0x000900A8, …, 16 KiB out)`, the tag the first `ULONG` of
`REPARSE_DATA_BUFFER` (measured: a junction answers `0xA0000003`; a plain folder answers error 4390 `ERROR_NOT_A_REPARSE_POINT`).
The rule, applied to every entry whose `Describe` carries `FILE_ATTRIBUTE_REPARSE_POINT (0x400)`:

| tag | treatment |
|---|---|
| `IO_REPARSE_TAG_MOUNT_POINT 0xA0000003` (junction) | removed AS A LINK: the disposition on the handle opened with `OPEN_REPARSE_POINT` (measured: error 0; the target's files survive); never enumerated |
| `IO_REPARSE_TAG_SYMLINK 0xA000000C` (file or directory symlink) | the same |
| any other tag (`0x9000001A` cloud files, `0x80000013` dedup, `0x80000018` WCI, `0xA0000019` WSL symlink, AppExecLink, …), or a tag that cannot be read | the WHOLE candidate is retained BEFORE its first delete ("holds a reparse point of kind 0x…: <relative>") — found by the verify pass (§B.3.1), so nothing is half-removed for it |

A link is judged **by the handle it was opened on**; its target is never opened, never resolved, never named in a decision.

##### A.5 TOCTOU, long paths, read-only files, what is logged

- **Handle-based, with one fence per entry.** The candidate folder is HELD for the whole candidate
  (`BeneathWrites.HoldFolderWindows`: share `R|W`, never `D` — measured by the archive: neither the folder nor any ancestor can
  be renamed while held, `BeneathWrites.cs:286-292`). Every entry is opened as itself (`OPEN_REPARSE_POINT`), described
  (`Describe`), and its `FinalPath` (`BeneathWrites.cs:320`, `GetFinalPathNameByHandle`) must equal
  `candidateFinal + '\' + relative` **exactly** (`PathRules.Windows.PathEquals`) — not merely "beneath the root". A folder swapped
  for a junction after its listing makes every child's final path land elsewhere, and the check refuses it; nothing is ever
  acted on by a path after a check, only on the handle whose place was verified. The root's own final path is read once at run
  start from a handle on the root (must be a directory, no reparse attribute, equal to the configured spelling after
  normalisation — an 8.3 spelling in the key is refused there: "spelt differently from its real name").
- **Long paths.** Every raw `CreateFileW` in the new seam is given `\\?\` + the full path (`BeneathWrites.ExtendedLength(path)`,
  new; the archive's own opens are not changed in this story). `FinalPath` already strips the prefix (`WithoutDevicePrefix`).
  .NET's enumeration handles long paths itself. A path past 32 767 chars is kept, named "too long".
- **Read-only files**: §A.3 — the disposition flag, never an attribute write. On a filesystem that refuses POSIX semantics
  (`MarkForDeletion`'s classic fallback, `BeneathWrites.cs:344-357`, is ALLOWED here — `classicAllowed: true`, this is never an
  agent's folder) the classic `FileDispositionInfo` has no ignore-read-only flag, so a read-only file there is KEPT and named
  ("read-only on a file system without POSIX delete"); the folder then reads "partly removed". **Owed measurement
  M-DH4-1** (the owner's `D:` is ReFS; `fsutil fsinfo volumeinfo D:` needs elevation, so it was not read here): POSIX delete
  and `IGNORE_READONLY_ATTRIBUTE` on ReFS, run by the story's Windows leg against a `testsToDelete-` folder on `D:` before
  the first real run — the fallback above is what happens if either is refused.
- **Sharing violations** (error 32 on open) and **error 5 after the flag** keep the entry, counted `inUse` / `accessDenied`;
  never retried in the same run. A file held open with `FILE_SHARE_DELETE` deletes without error (the runner plan §3.12's
  measured correction) — fine: its name goes, its bytes go when the holder closes.
- **Logged** (Serilog, through `ILogger` as every verb): at Information — the roots' verdicts at run start (path, rule), per
  candidate ONE line when judged (kind, idle, lock, git's answer, bytes) and ONE when decided (outcome, deleted / kept counts,
  bytes); at Warning — every policy refusal with its `DeletionRule` name and sentence, a foreign reparse tag, an unreadable
  checkpoint, a candidate retained as changed; never a line per file (1.3 M files), never file contents, never the git
  environment's values (its three variable NAMES at most). The run detail (§B.3.5) carries the counts and the first 20 kept
  entries by relative path. Paths are the owner's own and are logged relative to their root.

##### A.6 The probe (scratchpad, NTFS, Windows 11 10.0.26300, 2026-10-15)

`…\scratchpad\dh4-probe\probe-disposition.ps1`, one `testsToDelete-dh4-probe-<guid>` folder, removed by exact path at the end:
(a) read-only hard link into a store outside the root: `DELETE|POSIX` → **5**; `DELETE|POSIX|IGNORE_READONLY` → **0**; link gone,
store file present, attributes `ReadOnly` before and after. (b) junction inside `bin` to a folder outside the root, opened with
`OPEN_REPARSE_POINT`: attributes `0x410`, tag `0xA0000003`, disposition → **0**, junction gone, the target's file survives.
(c) a plain folder: tag read → error **4390**; disposition on an EMPTY folder → **0**; on a NON-empty one → **145**.

---

#### B. The spec

##### B.1 Selection — A24 and A24Orphans, reusing S3's and S4's types by name; the run's re-validation

**Ids.** `ConfigKeys.Auto.A24`, `ConfigKeys.Auto.A24Orphans` (`BoolKey`, `Trust = KeyTrust.Off`, default `false` in
`default.json`), so `ActionId.All` derives them; `ActionId.ExecutionOrder` gains `"A24", "A24Orphans"` after `"A21"` (both
Windows-only, so their place among the distro's actions is immaterial; when WR-T1 lands, A23 goes before them).
`contracts/actions.json` regenerated by `ContractFilesTests`. One class, two instances, the `ContainerRemoval(testcontainers:)`
shape (`ActionRegistry.cs:28-29`): `new WorktreeBuildOutput.BuildOutputAction(orphans: false)`, `(orphans: true)`.

**Declarations** (`ICleanupAction`): `Sides = [HostSide.Windows]` (the first action with that side; `ActCommand.OtherSide` and
`ActionEngine.NotThisSide` already read the list; S3 widens the distro-side sentence); `Scope = CommandScope.Machine` (the
Windows binary IS the user; `DiscoverTarget` is `None` on Windows, `ActionEngine.cs:620-623`, and `TargetUserStop` applies to
user-scoped actions only — a decision, recorded in §B.11); `Idle = IdleRule.TimerOnly`; `HomeRoots = []`;
`Commands` = S4's three git templates for A24, the orphan `check-ignore --no-index` form for A24Orphans (the policy's catalogue
holds every template an action may bind; `The_git_templates_cannot_be_bent_into_prune_remove_or_clean` is S4's and is re-run over
these declarations); `MaxDeferHours = null`; **`DryUnderSetting = true`** (new member, §B.5).

**The preview (LIVE, never `worktrees.json`)** — `BuildOutputAction.PreviewAsync`:

1. `WorktreeSettings.From(context.Config)` (S3). Roots = `context.Config` `worktrees.roots`; `WorktreeRoots.Judge(...)` (S3) → accepted
   roots proceed, each refused root becomes a note `"root <path> skipped: <rule> — <reason>"`. No root configured →
   `preview.Skip = "no worktrees.roots is configured (Choose folders… in the panel, or wsl-care.exe worktrees roots set)"`;
   every root refused → `Skip` naming them.
2. The checkpoint (§B.4) is read first. Unreadable (exists, does not parse, or names a root/candidate that is not under an
   accepted root) → `preview.Refusal = "the checkpoint %LOCALAPPDATA%\wsl-care\build-output-cursor.json cannot be used (<why>); nothing is deleted until it is removed"`.
   Readable → its candidate is listed FIRST, note `"resumed: interrupted by run <id> at <t>"`.
3. `new WorktreeScanner(paths, files, clock, WorktreeRoots.ThisMachinesDrive).Run(acceptedRoots, settings, token)` (S3) → `WorktreeScanResult`.
   The scan's budgets are the keys S3 reads; a `FolderScan` whose `Idle.Kind == Unknown` is never a candidate (its reason is
   counted under `retained{unknown}`).
4. Per `RootScan` (not refused) per `FolderScan`, the **kind gate**: A24 takes `Facts.Kind == FolderKind.Worktree`; A24Orphans takes
   `FolderKind.Orphan`. `MainClone`, `NotGit`, `ForeignPointer`, `Link`, `Unreadable` are never candidates (counted, named).
5. Per folder: `Idle.Kind == Idle` (older than `settings.IdleDays`); A24 only: `!Facts.Locked || config.Bool(worktrees.cleanLocked)`
   (locked → `retained{locked}` with the lock's reason).
6. Per `BuildCandidate` of the folder: `Retained.Length == 0` (S3's structural verdict: a nested `.git`, a file that is not a build
   artifact, an unfinished walk — S3 names it; DH-4 does not re-derive it here, it re-derives it at run time, step R3); the
   candidate's own newest write (S3 computed it; DH-4 asks the candidate's walk again at run time) older than the idle cut.
7. **A24 only — git confirms** through `IGitConfirmation` (DH-4's seam over S4's `GitQuestions`, §B.9): per worktree, the
   candidates' relative names → one closed answer per name, `GitAnswer.Confirmed` | `GitAnswer.Retained(reason)` — the reasons are
   S4's sentences verbatim: "git answered for another repository", "holds tracked files", "not ignored by git", "too long for one
   git call", and every refusal by its own name ("dubious ownership", exit 128, a timeout). A24Orphans: where the orphan's
   `<common>` still exists, the ignore rules are checked (S4's `--no-index` form) and the item's note reads
   `"ignore rules confirmed; tracked files NOT checked (the index is gone)"`; where it is gone, the note reads
   `"not confirmed by git — the folder's repository metadata is gone"`, and `What`'s FIRST sentence says so for the whole preview.
8. The result: `ActionPreview.Of(what, count, bytes, basis: "a live scan of the roots (elapsed, entries, complete?); logical bytes (file lengths), never disk freed", facts, refusal, targets)`
   with one `ActionItem("buildOutput", "<root>\<folder>\<relative>", bytes, note)` per selected candidate (`Key` = the
   candidate's real path + kind + newest write, kept in memory within one engine call), `Facts` = `retained.<reason>` counts +
   `candidates`, `roots`, `worktrees`, `orphans`, `locked`. Items past `worktrees.maxItemsPerRun` are counted under
   `retained{overBudget}` ("the next run takes them"), the largest first within a root, roots in configured order.

**The run — per candidate, in this order, every check again** (`BuildOutputRun.Candidate`, pure where it can be):

- R0 The checkpoint candidate first (§B.4), then the preview's targets in preview order.
- R1 The root: judged again (`WorktreeRoots.Judge`), its handle opened and its final path read (§A.5); a root that now fails →
  every candidate under it retained "root refused at run time: <rule>".
- R2 The folder: `FolderKinds.Classify(...)` again — the kind must still be what the action takes; the lock re-read; the folder's
  idle re-derived (S3's rule) — EXCEPT for the resumed candidate, whose idle is judged by its remaining FILES (§B.4).
- R3 The candidate: its own walk again (`files.WalkTree(candidate, limits, TreeRules { ListFiles = true, NeverEnter = {".git"} }, token)`
  with S3's `CountDenied` mode — a denied entry retains), from which: the newest file write (older than the idle cut, and for a
  resumed candidate ≤ the recorded one), S3's `BuildOutputRule.Retention(name, candidate, listed, settings)` (the content check,
  `node_modules` exempt — S3's rule), no `.git` inside (S3's rule). Then the **verify pass** of the seam (§B.3.1): every reparse
  point's tag, every entry's final path, the counts.
- R4 A24: git again, for this candidate's name alone (one call per candidate at run time; batches are the preview's).
- R5 The policy: `DeletionScope(root, "A24"|"A24Orphans", DeletionPermit.WorktreeBuildOutput)`; the seam asks
  `DeletionPolicy.Decide` with the facts IT gathered (§B.2) for the candidate; a refusal retains, naming the rule.
- R6 Dry? (§B.5) → counted "would delete", nothing more. Else the checkpoint is written (§B.4), then the delete pass (§B.3.2),
  then the checkpoint is removed, then the outcome recorded (§B.3.3).

##### B.2 `DeletionPolicy` changes — `WorktreeRoot` deny-by-default and the one `WorktreeBuildOutput` admission

**New members** (`Files/Deletion/*`):

- `DeletionPermit.WorktreeBuildOutput` — "A24 / A24Orphans: delete a `bin`/`obj`/`node_modules` candidate under a configured,
  admitted worktree root, and the entries beneath an admitted candidate; nothing else, nowhere else."
- `DeletionRule.WorktreeRoot` — "the path is under a configured `worktrees.roots` entry and the request holds no build-output
  admission" (deny by default). `DeletionRule.WorktreeBuildOutput` — "the admission was asked for a path that does not satisfy its
  predicate" (wrong name, no sibling project file / `package.json`, a `.git` inside, a link on the way, not under an ADMITTED root,
  an entry not beneath an admitted candidate).
- `ProtectedRoots` gains `WorktreeRootsDenied` and `WorktreeRootsAdmitted` (`IReadOnlyList<string>`, real-or-spelled like the agent
  roots, from `IHostPaths.WorktreeRoots` — a new `WorktreeRootSet(Denied, Admitted)` on `IHostPaths`, empty on Linux and by default,
  filled by phase two, §A.2). `ProtectedRoots.From` resolves both as it resolves the agent roots (both the real place and the spelling).
- `DeletionRequest` gains `BuildOutputFacts Candidate { get; init; } = BuildOutputFacts.None` — the record the SEAM fills:
  `(string CandidateRealPath, string Name /*bin|obj|node_modules, lower-case*/, bool SiblingProjectFileOrPackageJson, bool HoldsGit, bool LinkOnTheWay, bool IsCandidateItself)`.
  `IsCandidateItself = true` for the request on the candidate folder; `false` for an entry beneath it (then `CandidateRealPath` is
  the admitted candidate's). `None` has empty strings and every bool false — and therefore never admits.

**The order of `Decide` / `JudgeSource` after the change** (the numbered list in the class remark is updated to match):

1. Agent memory — unchanged, first, unconditional.
2. The archive's two move permits — unchanged.
3. `NeverUnder`: Claude temp — unchanged, unconditional. **Then the new worktree rule, BEFORE `GitFolder`:**
   `Under(path, roots.WorktreeRootsDenied) is { } denied` →
   `request.Permit == WorktreeBuildOutput ? JudgeBuildOutput(request, denied) : Refuse(WorktreeRoot, "it is under the configured worktrees root {denied}; nothing under a worktree root is deleted except an admitted bin/obj/node_modules candidate of A24 / A24Orphans")`.
   Then `GitFolder` as today — **except** that a request that already passed `JudgeBuildOutput` under a worktree root that lies
   under `~\git` is allowed (the admission is the one exception to `GitFolder`; a `WorktreeBuildOutput` permit on a path under
   `~\git` that is NOT under a denied worktree root is still refused by `GitFolder`).
4. `HoldsProtectedRoot` — unchanged (a candidate that HOLDS an agent root, a git root, Claude temp, or a worktree root is refused;
   worktree roots join the list: a `node_modules` that holds a configured root is refused).
5. Agent roots — unchanged (the admission never reaches them; a worktree root inside an agent root was refused by S3's `protected-place` rule anyway).
6. `JudgeInsideRoot` — unchanged (`TooBroad`, strictly inside the declared root).

**`JudgeBuildOutput(request, deniedRoot)`** — pure, over the request's facts and the admitted list:

```
admitted  = Under(path, roots.WorktreeRootsAdmitted)           // null → Refuse(WorktreeBuildOutput, "…root is configured but not admitted (it failed a root rule at this load)")
facts     = request.Candidate
IsCandidateItself:
   facts.Name is "bin" or "obj" or "node_modules" (ordinal-ignore-case on the LAST segment of path, which must equal facts.Name)
   && facts.SiblingProjectFileOrPackageJson && !facts.HoldsGit && !facts.LinkOnTheWay
   && rules.PathEquals(path, facts.CandidateRealPath) && rules.IsStrictlyUnder(path, admitted)
   && the path has at least TWO segments below the root (a root's direct child "bin" is never a candidate: the worktree folder lies between)
   → fall through to JudgeInsideRoot (declared root = the worktree root)
   else → Refuse(WorktreeBuildOutput, the failing clause named)
!IsCandidateItself (an entry):
   rules.IsStrictlyUnder(path, facts.CandidateRealPath) && rules.IsStrictlyUnder(facts.CandidateRealPath, admitted) && !facts.LinkOnTheWay
   → JudgeInsideRoot; else Refuse(WorktreeBuildOutput, …)
```

**How the predicate is re-run INSIDE the policy and where the facts come from.** The policy stays pure (paths arrive real; the
facts arrive as data), but the facts are gathered by the FILE-SYSTEM SEAM at judging time — `PhysicalFileSystem.BuildOutputFacts(realPath)`
in the new seam file: the last segment's name; `ListEntries(parent)` for a sibling whose extension is in `worktrees.projectFileExtensions`
(for `bin`/`obj`) or a `package.json` (for `node_modules`) — a FILE, never a link; `HoldsGit` from the verify pass (any entry named
`.git`, file or folder, anywhere inside); `LinkOnTheWay` = any component from the root to the candidate carries the reparse
attribute (`BeneathFiles`'s per-component check, `BeneathFiles.cs:102`, the Windows branch). **The action never constructs a
`BuildOutputFacts`** — the seam's `Remove` and `Verify` build it themselves and ask the policy; `BuildOutputFacts`'s constructor is
`internal`, and an architecture test holds that only `PhysicalFileSystem.*` and the policy tests name it. The S3 selection and the
policy therefore judge the same predicate from two independent reads.

**How the policy learns the configured roots:** §A.2 item 1 — through `IHostPaths.WorktreeRoots`, set in phase two of the host
from `worktrees.roots`; the file system is rebuilt over the new layout exactly as for the manual agents. `DeletionPolicyTests`
build a `WindowsHostPaths` with `WithWorktreeRoots(denied: [@"D:\rsd\_wt", @"C:\Users\me\git\wt"], admitted: [same])` and pass
paths — no selection code in the test (E20 done-when 1).

##### B.3 The bottom-up handle-based delete

**Where it lives.** `Files/ITreeRemoval.cs` (the seam's interface — named for what it is, a tree removal under an admitted
root, so WR-T1's A23 consumes the same seam rather than copying it, the plan's §6 extraction clause) implemented by
`PhysicalFileSystem` in **`Files/PhysicalFileSystem.TreeRemoval.Windows.cs`** (new, `[SupportedOSPlatform("windows")]`, the Linux
answer `Refused("not this OS")` as the archive's `NotThisOs`); the natives in `BeneathWrites.cs` (widened: `ReparseTag`,
`ExtendedLength`, `MarkForDeletion(handle, classicAllowed, ignoreReadOnly)`). **The architecture scanners, updated deliberately:**
`ArchitectureTests.ArchiveSeam.cs:17` `FileSystemSeamFiles()` gains `PhysicalFileSystem.TreeRemoval.Windows.cs`; `ArchiveForbiddenCall`
gains `\bDeviceIoControl\b|\bFSCTL_\w+|\bSetAttributes\s*\(|\bFileBasicInfo\b` and the planted companion gains those lines (its
expected line list changes — that is the point); `The_archive_seam_scanner_still_finds_the_seams_own_calls` also asserts
`DeviceIoControl` is found in the seam. `IFileSystem.DeleteDirectory` (`PhysicalFileSystem.cs:623-624`, `Directory.Delete(recursive)`)
is NOT called by anything in this story; a new architecture assertion holds that no file under `Core/Actions/WorktreeBuildOutput/`
names `DeleteDirectory`, `DeleteFile` or `Directory.`.

**B.3.1 The verify pass** — `ITreeRemoval.Verify(TreeRemovalRequest request, TreeRemovalBudget budget, CancellationToken)` →
`TreeVerification { Ok(BuildOutputFacts facts, long Files, long Folders, long Links, long Bytes, DateTimeOffset NewestFileWrite, uint HardLinkedNames) | Retained(string why) }`:

1. Open the ROOT (`\\?\` + root) `OpenToChangeWindows`; `Describe`: directory, no reparse; `FinalPath` equals the configured
   spelling (`PathRules.Windows.PathEquals` after `WithoutDevicePrefix`) — else `Retained("root …")`. Keep `rootFinal`.
2. Open the CANDIDATE `HoldFolderWindows` (held until the delete pass ends); `Describe`: directory, no reparse; `FinalPath ==
   rootFinal + '\' + relative` exactly — else `Retained("the candidate is not where it was approved")`.
3. Every ancestor between root and candidate: opened as itself, `Describe` no reparse (`LinkOnTheWay` otherwise).
4. Iterative pre-order over the candidate with an explicit stack (no recursion — depth is unbounded by the caller):
   `new FileSystemEnumerable<FileEntry>(folder, …, new EnumerationOptions { RecurseSubdirectories = false, IgnoreInaccessible = false, AttributesToSkip = 0, ReturnSpecialDirectories = false })`
   — a listing that throws `UnauthorizedAccessException`/`IOException` → `Retained("<relative> could not be listed (…)")`. For each
   entry: open as itself (`\\?\`), `Describe`, `FinalPath` fence; a reparse attribute → `ReparseTag`: junction/symlink → counted
   `Links`, NOT entered; any other tag or an unreadable tag → `Retained("holds a reparse point of kind 0x…: <relative>")`;
   an entry named `.git` → `HoldsGit = true` → (the policy refuses; the pass answers `Retained("holds a nested repository")`);
   a directory → pushed; a file → `Files++`, `Bytes += Length` (the directory entry's length), `NewestFileWrite = max`,
   `HardLinkedNames += Links > 1 ? 1 : 0`. Every 1024 entries: `RunProgress.Mark()` + the token (as `TreeWalk.cs:135-139`).
   Past `budget.MaxEntries` → `Retained("past worktrees.deleteMaxEntries")`.
5. `facts` built here (§B.2) and the policy asked for the candidate: a refusal → `Retained(rule: sentence)`.

**B.3.2 The delete pass** — `ITreeRemoval.Remove(request, verification, budget, token)` → `TreeRemovalOutcome`:

1. Pre-condition: the checkpoint was written by the caller (the seam does not know the checkpoint; the ACTION writes it between
   `Verify` and `Remove`, and `TreeRemovalTests` hold the order through a recording fake, §B.7).
2. Post-order over the same stack discipline: for each folder, list children (same options); for each child, open as itself,
   `Describe`, `FinalPath` fence (`PathChanged` → the entry is kept, counted `changed`, the candidate ends `PartlyRemoved`);
   the policy asked PER ENTRY with the entry request (`IsCandidateItself = false`, the final path as `RealPath`); then:
   - reparse (junction/symlink) → `MarkForDeletion(h, classicAllowed: true, ignoreReadOnly: true)` on the link's own handle →
     `linksRemoved++`; any other tag seen now (it appeared after the verify pass) → kept, `foreignTag++`, the candidate `PartlyRemoved`;
   - file → `MarkForDeletion(...)` → 0: `filesDeleted++`, `bytesDeleted += Length`; 5: `accessDenied++`; 32 on the open: `inUse++`;
     other: `failed++` with the first 20 named; never a retry;
   - directory → recurse first, then `MarkForDeletion` on the folder's own handle → 0: `foldersDeleted++`; 145: `notEmpty++`
     (something was kept inside, or a classic-disposition name is still pending); other: `failed++`.
3. Last: the candidate folder itself — its HELD handle is released, it is re-opened `OpenToChangeWindows`, `Describe` + `FinalPath`
   fence again, then `MarkForDeletion` → 0 = `Deleted`; 145 = `PartlyRemoved`.
4. Every count is the directory entry's length, never a measured disk figure.

**B.3.3 Outcomes** (`TreeRemovalOutcome`, a closed record set; the run detail's per-candidate `outcome` is its name):

| outcome | meaning | run-detail sentence |
|---|---|---|
| `Deleted(files, folders, links, bytes)` | every entry and the candidate folder gone | "deleted: N files, M folders, L links, B logical bytes" |
| `KeptInUse(…, inUse)` | nothing deleted at all because the FIRST open was a sharing violation on the candidate itself, or every file was in use | "kept — in use (a build or an agent holds it)" |
| `PartlyRemoved(deleted counts, kept counts by reason)` | some entries kept (in use, access denied, read-only without POSIX delete, unlistable, changed, foreign tag) | "partly removed — N kept (in use K, denied D, …); the next build restores it" |
| `Retained(why)` | the verify pass or the policy said no; nothing deleted | the reason verbatim |
| `Failed(why, deleted counts)` | an exception after the first delete | "failed after N deletes: …" (the action's `Failure`) |

**B.3.4 Limits** (said in the run detail's `notes` when hit): `worktrees.deleteMaxEntries` per run (new key, §B.6);
`worktrees.maxItemsPerRun` candidates; the scan's own budgets for the preview; a cancellation ends the current entry (the file in
flight is deleted or not; the disposition is atomic) and the candidate reads `PartlyRemoved` with `Interrupted = true` on the
`ActionRun` (E6.S0 review D2's shape) — the checkpoint stays, the next run resumes (§B.4). Depth is unbounded (the stack is a
list); a path past 32 767 characters is kept, named.

**B.3.5 The run record** (`ActionRun`): `Count` = candidates `Deleted` whole; `FreedBytes` = logical bytes deleted (also from
partly removed); `FreedBasis = "logical bytes of the files deleted (file lengths); never disk freed — ReFS cloning and hard links differ"`;
`BeforeBytes`/`AfterBytes` = null; `Removed` = the `Deleted` candidates as items (`Kind: "buildOutput"`, name relative to the root,
bytes); `NotRemoved` = every other candidate with its outcome sentence as `Note`; `Commands` = the git records S4's runner
produces; `Notes` = per root `"<root>: candidates C, deleted D, partly P, kept K, retained R, failed F, bytes B"`, the resumed
candidate, the budgets hit, the dry sentence. Per-outcome counts also go into `Facts`-like fields of the detail
(`deleted`, `partlyRemoved`, `keptInUse`, `retained{reason}`, `failed`, `hardLinkedNames`, `linksRemoved`).

##### B.4 The checkpoint — record, path, when written, how resumed, the interrupted-run reading

**Path:** `%LOCALAPPDATA%\wsl-care\build-output-cursor.json` = `paths.Rules.Join(paths.StateDirectory, "build-output-cursor.json")`
(`WindowsHostPaths.StateDirectory`, `:51`). **Store:** `Core/Actions/WorktreeBuildOutput/BuildOutputCursor.cs` — the
`ArchiveState` shape (`Archive/ArchiveState.cs:175-194`): read through `files.ReadRegularFile(path, cap)` with the cap
`userFiles.maxSmallFileBytes` (S3's `WorktreeSettings.MaxSmallFileBytes`); written through `files.WritePrivateFileAtomically(path, bytes, new DeletionScope(paths.StateDirectory, action))`;
removed through `files.DeleteFile(path, scope)` (the ONE `DeleteFile` this story calls, and it is a state file under
`StateDirectory`). Source-generated JSON (`WslCareJsonContext.Compact`).

**Record** (`CursorRecord`): `{ v: 1, runId, action: "A24"|"A24Orphans", root: "<configured spelling>", candidate: "<relative to root, backslashes>", kind: "bin"|"obj"|"node_modules", folderKind: "worktree"|"orphan", newestFileWriteUtc, files, bytes, startedUtc }`.

**When written:** after `Verify` succeeded and the policy admitted the candidate, BEFORE the first delete — never while dry,
never for a candidate that was retained. **When removed:** right after the candidate's outcome is decided (`Deleted`,
`PartlyRemoved`, `KeptInUse`, `Failed`), before the next candidate's verify — so at most ONE record exists, and a crash between
the delete pass and the removal leaves a record whose candidate is already decided (harmless: the next run re-judges it, finds
nothing or finishes it).

**How resumed** (R0 of §B.1, any trigger, either action): the record's root must be among the accepted roots and its candidate
a direct `bin`/`obj`/`node_modules` beneath a folder of that root — else `Refusal` (§B.1 step 2). The candidate is judged FIRST
with every rule of R1–R5, with ONE substitution: its idle is NOT the folder-time rule (deleting children touched every folder's
write time, WR-T1's recovery rule, runner plan §3.12) but "every REMAINING file's write ≤ `newestFileWriteUtc`" — a newer file →
`Retained("changed while interrupted")`; the kind must still be what the running action takes (a resumed `worktree` candidate
under A24Orphans is `Retained("a worktree's candidate; run A24")` and the record is left for A24). Then it is finished (the delete
pass) or retained; the record is removed either way except when left for the other action. The run's notes carry
`"resumed <candidate>, interrupted by run <runId> at <startedUtc>: <outcome>"`.

**The interrupted-run reading** is the ENGINE's, untried on Windows and proven here: a run killed mid-delete leaves
`running.json` and a detail with no history line; the next `act` (or `collect`) runs `RunningSweep` (dead pid → swept with an
`interrupted` record) and `RunReconcile.Apply` (`Records/RunReconcile.cs:19-34`: a detail without a line gets one, outcome
`interrupted`). RED tests drive both over `WindowsHostPaths` + a fake process table (§B.7 "engine on Windows paths"). The
killed run therefore reads **interrupted**, never **completed**; the resuming run is its own, **completed**, with the note above.

##### B.5 The Windows privilege rule and the engine changes (SampleIdle, dry, button runs)

1. **Privilege** — §A.1: `ActCommand.Refusal` becomes `ActPrivilege.Refusal(host) ?? Unbuilt ?? OtherSide ?? ObserveOnly`.
2. **The idle gate.** `ActionEngine.SampleIdle` (`:219-224`) defers EVERY action on the Windows binary ("CPU use is unknown"). It is
   asked only when `IdleGate.Applies(action.Idle, run.Trigger)` (`IdleGate.cs:47-48`): `Always`, or `TimerOnly` under the TIMER.
   A24/A24Orphans declare `IdleRule.TimerOnly`, so a BUTTON or CLI run (`RunTrigger.Manual`/`Cli`) never asks it and is never
   deferred; a timer run (only when an administrator turns `auto.A24` on in the machine layer — trust `Off` keeps the user layer
   from it) is deferred with that reason until a Windows idle sample exists — visible in the run record, safe. **No change to
   `SampleIdle`.** A RED test pins that a manual Windows run of A24 never reaches `SampleIdle` (a context whose `Probe` throws).
3. **Dry first on the Windows paths** (plan §3.4; E20 done-when 7 and 9: nothing is deleted while dry; the owner's first real
   preview is recorded "before `dryRun` is lifted on these paths"). `DryRunWindow.Decide` (`:68-73`) says a button or the CLI
   NEVER dry-runs; the Windows tasks spell `--timer` for A23's body (runner §3.12) but A24's `auto.*` is off, so a `--timer` run
   would be SKIPPED by `TimerStop`, and a `--manual` run would never be dry. The decision: **`ICleanupAction.DryUnderSetting`**
   (default `false`; A24 and A24Orphans `true`): `ActionEngine.DryRunStop` becomes per action —
   `run.Dry.DryRun || (action.DryUnderSetting && run.Trigger != RunTrigger.Timer && c.Loaded.Config.Bool(ConfigKeys.DryRun))` →
   `Stop(ActionStatus.DryRun, "dry: the setting dryRun is on; a Windows deletion previews until the owner lifts it (wsl-care.exe config set dryRun false) — PLAN_disk_hygiene_windows.md §3.4")`.
   The 7-day week stays the timer's (`timer.firstDryWindowDays`; a button never writes the stamp, `DryRunWindow.cs:50-53`). The
   `ActRunDetail`'s `DryRun` flag is the run's; the action's outcome status says `dryRun` for it. The distro's actions are untouched
   (default `false`).
4. **The run lock.** `act` takes THE run lock as every act does (`ActionEngine` remark; `RunLock.cs`), so two clean runs serialise
   and a watch sample during a clean is skipped (busy = a normal answer, `WatchRun.Windows.cs:36-41`). Plan §3.5 says the hygiene
   tasks take only the `worktrees` lease and NOT the run lock — that is DH-5's concern and would need a body that is not `act`;
   DH-4 does not change the engine's lock. Recorded as an open point for DH-5 in §B.11.
5. **`DiscoverTarget` / `TargetUserStop`:** untouched; `Scope = Machine` keeps A24 clear of the "no target user" refusal.
6. **The Windows engine path is proven, not assumed**: `RunLock`, `RunningState`, `Heartbeat`, `RunRecorder`, `RunDetailStore`,
   `RunReconcile`, `RunningSweep` over `WindowsHostPaths` (sandboxed) with a fake process table — the RED list's "engine on Windows
   paths" group (§B.7). WR-D4's `RunRetention.TrySweep` already retires Windows run details (`WatchRun.Windows.cs:62`).

##### B.6 Keys (every number a key; `ConfigKeys.Worktrees.cs` is S3's file — DH-4 adds to it)

| key | default | range | trust | used by |
|---|---|---|---|---|
| `auto.A24`, `auto.A24Orphans` | `false` | — | `Off` | `ActionId.All`, the timer gate |
| `worktrees.cleanLocked` | `false` | — | `Off` (the user layer cannot turn it on) | selection step 5 |
| `worktrees.maxItemsPerRun` | 2000 | 1–100 000 | `Lower` | the preview's cut |
| `worktrees.deleteMaxEntries` (new) | 5 000 000 | 10 000–50 000 000 | `Lower` | the verify and delete passes, per run |

Consumed, not added: `worktrees.roots`, `maxRoots`, `idleDays`, `projectFileExtensions`, `buildArtifactPatterns`,
`artifactSkewMinutes`, `wslAutomountRoots`, `scanMaxEntries`, `scanMaxSeconds` (S3); `gitPath`, `gitTimeoutSeconds`,
`gitNamesPerCall`, `gitCommandLineMaxChars` (S4); `dryRun`, `timer.firstDryWindowDays`; `userFiles.maxSmallFileBytes`.
Every new key: `default.json`, `contracts/config-keys.json` regenerated, a row in `NumbersAreConfigurationTests`, a README line.
The two `auto.*` keys join `ConfigSchemaTests`' "every auto switch is off by default" assertion (`A5`'s row, `ConfigSchemaTests.cs:52`).

##### B.7 The RED scenario list — complete, named, each with its fake/fixture or Windows leg

**Fixtures.** `WorktreeWorld` (S3, `tests/WslCare.Core.Tests/Worktrees/WorktreeWorld.cs`): a `ToDeleteRoot("wt-<purpose>")` sandbox
(`testsToDelete-wsl-care-wt-…` under `%TEMP%`), `WindowsHostPaths(WindowsEnvironment.Sandboxed(root))`, the real
`PhysicalFileSystem`, `work\` as the root, `Main`/`Worktree`/`Pointing` builders — DH-4 adds `Bin(worktree, files…)`,
`NodeModules(worktree)`, `Orphan(name)`, `Locked(worktree, reason)`, `Aged(path, days)`. Windows legs skip on other OSes with
`WorktreeWorld.WindowsOnly`. Links: `TestSupport/DirectoryLinks.TryCreate` (a symlink where allowed, else `mklink /J`;
skip with the reason when neither). Hard links: `ChildProcess.RunAsync("cmd.exe", ["/c", "mklink", "/H", link, target], …)`
exactly as `ArchiveFilesTests.cs:113-117`. Git: `FakeGitConfirmation` (a dictionary name → `GitAnswer`), never a real git in
DH-4's tests (S4 owns the real-git legs). Engine: `WindowsHostPaths` sandboxed + `FakeProcessTable` + `FixedTimeProvider` +
`RecordingCommandRunner`. The seam: `RecordingTreeRemoval` (records `Verify`/`Remove` calls and answers scripted outcomes) for the
action's tests; the real seam for the Windows legs.

**Policy (pure; `DeletionPolicyTests`, no selection code):**
1. `A_source_file_under_a_D_root_is_refused_by_the_policy_itself` — `D:\rsd\_wt\x\src\Program.cs`, root `D:\rsd\_wt`, no permit → `WorktreeRoot`.
2. `DeletionPolicy_refuses_every_other_path_under_a_worktree_root_or_a_git_root` — a theory: the worktree folder itself, `x\.git`,
   `x\bin` with `Permit None`, `x\bin` with the permit but `Candidate = None`, `x\appsettings.json`, `C:\Users\me\git\wt\x\src` → `WorktreeRoot` / `GitFolder`.
3. `The_admission_needs_every_fact` — a theory over `BuildOutputFacts`: wrong name (`out`), no sibling, `HoldsGit`, `LinkOnTheWay`,
   `CandidateRealPath` ≠ path, the root's direct child `bin`, a root configured but not admitted → each `WorktreeBuildOutput`, with the clause named.
4. `An_admitted_candidate_under_a_root_under_the_git_folder_is_allowed_and_nothing_else_there_is` — `C:\Users\me\git\wt\x\bin` admitted; `C:\Users\me\git\other\bin` with the same permit → `GitFolder`.
5. `An_entry_is_allowed_only_beneath_an_admitted_candidate` — `x\bin\Debug\a.dll` with `CandidateRealPath = x\bin` allowed; `x\obj\a` with `CandidateRealPath = x\bin` refused.
6. `A_candidate_that_holds_a_worktree_root_or_an_agent_root_is_refused` — `HoldsProtectedRoot` extended.
7. `Agent_memory_and_claude_temp_are_refused_before_the_admission_is_even_asked`.

**Facts come from the seam (Windows leg, real file system):**
8. `The_seam_gathers_the_facts_from_the_disk_not_from_the_caller` — `bin` beside `app.csproj` → admitted; the csproj removed → refused by the policy with "no sibling project file"; a `.git` file planted inside `bin` → "holds a nested repository".
9. `A_sibling_project_file_that_is_a_link_does_not_count` (a symlink/junction named `app.csproj`… a FILE link needs Developer Mode: skip with reason, else assert).

**Selection (fake seam, fake git, `WorktreeWorld`):**
10. `A_locked_worktree_is_skipped_unless_cleanLocked` — and the reason text appears under `retained{locked}`.
11. `A24_takes_worktrees_only_and_A24Orphans_orphans_only` — a main clone, a notGit, a foreignPointer, a link, an unreadable folder are counted and never items.
12. `An_unknown_idle_is_never_a_candidate`.
13. `A_candidate_s_own_fresh_write_retains_it_even_in_an_idle_worktree`.
14. `A24Orphans_says_not_confirmed_by_git_first` — `What`'s first sentence; the per-item note for a gone `<common>`; "ignore rules confirmed; tracked files NOT checked" for a present one.
15. `A24_retains_every_candidate_git_does_not_confirm_by_the_answer_s_own_words` — a theory over `GitAnswer.Retained("holds tracked files")`, `("not ignored by git")`, `("git answered for another repository")`, `("dubious ownership")`, `("too long for one git call")`.
16. `The_preview_is_a_live_scan_never_the_stored_report` — a `worktrees.json` planted with a juicy candidate that does not exist on disk → not an item; the store is never read (a `DelegatingFileSystem` records reads).
17. `No_root_configured_is_a_skip_and_every_root_refused_is_a_skip_naming_them`.
18. `A_refused_root_is_skipped_and_the_others_proceed`.
19. `Items_past_maxItemsPerRun_are_counted_not_dropped`.
20. `The_largest_candidates_come_first_within_a_root`.

**Run-time re-validation (fake seam; the seam records what it was asked):**
21. `A_candidate_written_since_the_preview_is_retained` (a file newer than the cut appears between `PreviewAsync` and `RunAsync`).
22. `A_candidate_replaced_by_a_junction_after_the_preview_is_retained` (Windows leg, real seam: the `bin` folder swapped for a junction to a sandbox folder outside the root; nothing in the target is touched).
23. `A_worktree_whose_kind_changed_since_the_preview_retains_its_candidates` (the `.git` file becomes a folder).
24. `A_lock_taken_since_the_preview_retains_the_candidate`.
25. `Git_is_asked_again_per_candidate_at_run_time_and_a_changed_answer_retains`.
26. `A_root_that_fails_at_run_time_retains_every_candidate_under_it`.
27. `The_policy_is_asked_for_the_candidate_and_for_every_entry` (the recording seam / a recording policy hook: counts).

**The delete (Windows leg, real seam, sandbox under `testsToDelete-`):**
28. `A_hard_linked_read_only_file_is_deleted_without_changing_the_store_file_attributes` — store outside the root, `mklink /H`, `ReadOnly` on; after: the name gone, the store file present, `ReadOnly` still set, `hardLinkedNames = 1`.
29. `A_junction_inside_node_modules_is_removed_as_a_link_and_its_target_survives` — `DirectoryLinks.TryCreate`; `linksRemoved = 1`; the target's file present.
30. `A_reparse_tag_other_than_junction_or_symlink_retains_the_candidate` — the seam's `ReparseTag` seam-overridden to answer `0x9000001A` for one entry (a real foreign tag cannot be made without a filter driver: the natives are behind an `internal` `Func<SafeFileHandle, ReparseTagRead>` the test replaces) → `Retained("holds a reparse point of kind 0x9000001A: …")`, nothing deleted.
31. `A_bin_holding_a_nested_repository_is_retained_whole` — a `.git` file three levels down; nothing deleted.
32. `Nothing_outside_the_root_is_ever_deleted` — a junction from `bin\x` to a sandbox folder outside the root holding 3 files; after the run: the 3 files exist; the walk never listed them (a `DelegatingFileSystem` records every listing path).
33. `A_file_in_use_is_kept_and_the_candidate_reads_partly_removed` — a `HoldingChild` (TestSupport) holds a file with no `FILE_SHARE_DELETE`; `inUse = 1`, the folder remains, the sentence "the next build restores it".
34. `An_unlistable_subfolder_keeps_it_and_the_candidate_reads_partly_removed` — `AccessDenial` (TestSupport) on a subfolder.
35. `The_delete_never_enumerates_into_a_link` — a junction to a folder with 10 000 files: the entry count stays small (recorded).
36. `The_candidate_folder_is_removed_last_and_only_when_empty` — the order of the recorded native calls.
37. `A_long_path_is_deleted_through_the_extended_length_prefix` — a 300-char path under `bin`.
38. `The_env_file_beside_bin_survives` and `An_ignored_non_build_folder_survives` (`.vs\`, `TestResults\`): not candidates, present after.
39. `Logical_bytes_are_the_entry_lengths` — a sparse 1 MiB file counts 1 MiB.
40. `A_cancellation_ends_the_entry_in_flight_and_the_candidate_reads_partly_removed_interrupted` (token cancelled after N entries through the progress seam).
41. `Every_native_open_carries_the_extended_length_prefix_and_open_reparse_point` — the recording of the `CreateFileW` arguments (an `internal` seam like `_onArchiveStep`).

**Checkpoint and interruption:**
42. `The_checkpoint_is_written_before_the_first_delete` — the recording seam: `Verify` → cursor write → `Remove`; and NOT written for a retained candidate; NOT written while dry.
43. `The_checkpoint_is_removed_when_the_candidate_is_decided` — for `Deleted`, `PartlyRemoved`, `KeptInUse`, `Failed`.
44. `An_interrupted_candidate_is_taken_first_and_judged_by_its_files` — a planted cursor; the folder's write time is NOW (touched), its remaining files old → finished; the notes name the run id.
45. `An_interrupted_candidate_with_a_newer_remaining_file_is_retained_as_changed`.
46. `An_unreadable_checkpoint_refuses_the_action_and_deletes_nothing` — a cursor of `{` and one naming a root that is not configured.
47. `A_cursor_of_the_other_action_is_left_for_it`.
48. `The_killed_run_reads_interrupted_never_completed` — engine over `WindowsHostPaths` sandboxed: a `running.json` of a dead pid and a detail without a line → the next `act` sweeps and reconciles; the history line reads `interrupted`.

**Engine on Windows paths (fakes; the first Windows-side action proves the path):**
49. `A_manual_windows_run_of_A24_never_asks_the_idle_sample` (a `Probe` that throws; the outcome is `ran`).
50. `A_timer_run_of_A24_is_deferred_on_windows_with_the_cpu_unknown_reason` (auto on in the machine layer).
51. `While_dry_nothing_is_deleted` — `dryRun` on, trigger Manual: outcome `dryRun`, the seam's `Remove` never called, no cursor; `dryRun` off: `ran`.
52. `The_distro_actions_keep_never_dry_running_on_a_button` (default `DryUnderSetting`).
53. `The_run_detail_counts_logical_bytes_per_outcome` — the detail JSON over `%LOCALAPPDATA%\wsl-care\runs\<day>\<id>.json` carries `deleted`, `partlyRemoved`, `keptInUse`, `retained{…}`, `failed`, `hardLinkedNames`, `linksRemoved`, per-root notes.
54. `The_run_lock_running_json_and_the_heartbeat_work_under_the_windows_state_directory` (the heartbeat period shortened; `running.json` seen with `current = A24`).
55. `The_windows_binary_refuses_the_distro_actions_and_the_distro_binary_refuses_A24` (`OtherSide` both ways; S3's side text).

**Privilege (`Cli.Tests/ActPrivilegeTests`, pure):**
56. `An_elevated_windows_process_is_refused_with_exit_81_before_the_lock` — `IsRoot = true` on `WindowsHostPaths`; the lock file never created (a `DelegatingFileSystem` records `TryLockExclusive`).
57. `A_system_token_is_refused_the_same` (the SID check through a seam).
58. `The_sandbox_claim_counts_as_elevated_on_windows`.
59. `An_unelevated_windows_process_goes_on_and_a_non_root_distro_process_still_gets_77`.

**Architecture:**
60. `Only_the_seam_reads_a_reparse_tag_sets_a_disposition_or_touches_attributes` (the widened scan + its planted companion).
61. `The_action_folder_names_no_delete_primitive_of_IFileSystem`.
62. `BuildOutputFacts_is_constructed_only_by_the_seam_and_the_policy_tests`.

**Keys and contracts:** `NumbersAreConfigurationTests` rows; `ConfigSchemaTests` auto-off rows; `ContractFilesTests` goldens
(`actions.json`, `config-keys.json`); `ActionHomeRootsTests` unaffected (Machine scope).

##### B.8 The break-it list (each on PRODUCT code only; red, then restored)

1. `WorktreeRoot` deny-by-default removed (the `Under(path, Denied)` branch) → 1, 2 red.
2. The admission's `SiblingProjectFileOrPackageJson` clause dropped → 3, 8 red.
3. `HoldsGit` ignored → 31, 3 red.
4. The entry rule's "strictly under the admitted candidate" dropped → 5 red.
5. The `FinalPath` fence weakened to "beneath the root" → 22 red (the junction's children would land beneath the root).
6. `ignoreReadOnly` dropped from the disposition → 28 red (error 5).
7. A `File.SetAttributes` added before the delete → 60 red AND 28 red (the store's attribute changes).
8. The foreign-tag retention removed (every reparse point removed as a link) → 30 red.
9. The walk entering a junction (`ReparseTag` not consulted before push) → 32, 35 red.
10. The candidate folder removed first / `Directory.Delete(recursive)` used → 36, 61 red.
11. The checkpoint written AFTER the first delete → 42 red.
12. The resumed candidate judged by folder times → 44 red.
13. `cleanLocked` ignored → 10 red.
14. The run-time re-validation removed (the preview's `Key` trusted) → 21, 23, 24, 25 red.
15. Git's run-time answer not asked again → 25 red.
16. `DryUnderSetting` ignored in `DryRunStop` → 51 red.
17. The per-side privilege rule reverted to `NotRoot` → 56, 59 red (an unelevated Windows process would get 77).
18. The extended-length prefix dropped → 37 red.
19. The per-entry policy ask removed → 27 red.
20. The `\bDeviceIoControl\b` pattern dropped from the scan → 60's planted companion red.

##### B.9 The files

**New** — `src_daemon/src/WslCare.Core/Actions/WorktreeBuildOutput/`: `BuildOutputAction.cs` (the `ICleanupAction`, two ids),
`BuildOutputSelection.cs` (pure: `WorktreeScanResult` + settings + git answers → selected / retained with reasons),
`BuildOutputRun.cs` (R0–R6 per candidate), `BuildOutputCursor.cs` (`CursorRecord` + the store), `GitAnswer.cs` +
`IGitConfirmation.cs` (the seam over S4's `GitQuestions`; the product implementation `GitConfirmation.cs` binds S4's API when S4
lands — until then it is the one file that references S4 and may be a stub that answers `Retained("git questions not built: E19.S4")`,
which keeps A24 deleting NOTHING under a worktree while A24Orphans with a gone `<common>` already works). `Files/ITreeRemoval.cs`,
`Files/PhysicalFileSystem.TreeRemoval.Windows.cs`, `Cli/Commands/ActPrivilege.cs`.

**Changed** — `Files/BeneathWrites.cs` (`ReparseTag`, `ExtendedLength`, `MarkForDeletion` widened; the Windows constants
`IoReparseTagMountPoint = 0xA0000003`, `IoReparseTagSymlink = 0xA000000C`, `DispositionIgnoreReadOnly = 0x10`,
`FsctlGetReparsePoint = 0x000900A8`, `WindowsNotAReparsePoint = 4390`, `WindowsAccessDenied = 5`); `Files/Deletion/DeletionPolicy.cs`,
`DeletionScope.cs` (permit, `BuildOutputFacts`), `DeletionVerdict.cs` (two rules), `ProtectedRoots.cs`; `Hosting/IHostPaths.cs`,
`WindowsHostPaths.cs` (`WorktreeRoots`, `WithWorktreeRoots`), `LinuxHostPaths.cs` (empty); `Cli/CliHost.cs` (phase two);
`Cli/Commands/ActCommand.cs` (`Refusal`); `Actions/ICleanupAction.cs` (`DryUnderSetting`); `Actions/Engine/ActionEngine.cs`
(`DryRunStop` per action); `Actions/ActionId.cs` (`ExecutionOrder`); `Actions/ActionRegistry.cs`; `Config/ConfigKeys.cs` (`Auto`),
`ConfigKeys.Worktrees.cs` (S3's file: `cleanLocked`, `maxItemsPerRun`, `deleteMaxEntries`), `default.json`;
`Json/WslCareJsonContext.cs`; `contracts/actions.json`, `contracts/config-keys.json`; `README.md` (the two actions, the keys, the
dry rule, exit 81 for `act` on Windows); `research/module_daemon.md` (a *Worktree build output* section: the policy's two new rules,
the seam, the cursor, the privilege rule), `research/architecture.md` (the Windows side's first two actions),
`research/module_tests.md` (the Windows legs and the fixture additions).

**Tests** — `tests/WslCare.Core.Tests/Files/DeletionPolicyTests.cs` (+ `DeletionPolicyWorktreeTests.cs`),
`Files/TreeRemovalTests.cs` (Windows legs), `Files/BeneathWritesReparseTests.cs`, `Actions/WorktreeBuildOutput/*Tests.cs`
(selection, run, cursor, dry, idle), `Actions/Engine/WindowsEngineTests.cs`, `ArchitectureTests.ArchiveSeam.cs`,
`tests/WslCare.Cli.Tests/ActPrivilegeTests.cs`, `tests/WslCare.Scenarios/WorktreeCleanFlows.cs` (the built CLI over a
`WSL_CARE_ROOT` sandbox on Windows, `ClaimsRoot = false`: preview, dry run, real run after `config set dryRun false`,
the refusals), `tests/WslCare.TestSupport/` additions to `WorktreeWorld` (S3's; coordinate — do not fork it).

##### B.10 What the coder must NOT do

- Not `Directory.Delete`, `File.Delete`, `DeleteOnClose`, `FileInfo.Delete`, `MoveTo` anywhere but the seam file; not
  `IFileSystem.DeleteDirectory` at all; not `IFileSystem.DeleteFile` except for the cursor under `StateDirectory`.
- Not `File.SetAttributes`, `FileSystemInfo.Attributes = …`, `FILE_BASIC_INFO`, `attrib` — the read-only attribute is never written.
- Not resolve, open, enumerate or name a link's TARGET; not follow a reparse point for any reason; not treat any tag but the two
  as a link.
- Not open a file without `FILE_FLAG_OPEN_REPARSE_POINT`; not act on a path after a check — on the handle whose `FinalPath` was
  verified; not drop the `\\?\` prefix.
- Not construct `BuildOutputFacts` in the action; not pass selection evidence to the policy — the seam gathers it.
- Not read `worktrees.json` to select; not trust the stored `worktrees.roots` — judge it at preview, at run start and in the policy.
- Not relax `IsPlainFile` in the ARCHIVE's paths (the archive keeps refusing `Links > 1`; the tree removal has its own rule).
- Not write the cursor after the first delete, while dry, or for a retained candidate; not delete anything when the cursor is unreadable.
- Not change `SampleIdle`, `DiscoverTarget`, the run lock, the 7-day week; not make `DryUnderSetting` default `true`.
- Not add a dedicated body or a `--force`; not spell `act`, `--confirm`, `config` in the extension (DH-5's rule stays).
- Not run a real `git`, a real task, or anything against a real repository or the real `%TEMP%` from a test; every test folder is
  `testsToDelete-…` and removed; not weaken `ArchitectureTests`, the recording seams, or a fake to pass; not delete the
  `WSL_CARE_SANDBOX_PRIVILEGED` claim's meaning (on Windows it refuses `act`).
- Not touch `research/`-level plan status lines: DH-4 lands; the plan promotion is DH-9's.
- Not kill processes, not set the commit email, not merge on a red or pending check (the family's standing rules).

##### B.11 The decisions taken here (each a deviation or a resolution the plans left open)

1. **Privilege is a per-side rule inside `act`, exit 81 on an elevated/SYSTEM Windows process**, not a dedicated body — consistent
   with `watch`/`archive` and the runner's "nothing elevated"; the sandbox claim means "elevated" on Windows (§A.1).
2. **The policy learns the roots through phase two of the host** (`IHostPaths.WorktreeRoots`, `WithWorktreeRoots`, the manual-agent
   precedent): every shape-valid entry DENIED, every accepted entry ADMITTED; the action judges the roots independently at preview
   and at run start (§A.2). A refused root therefore also DENIES every other action beneath it (fail closed; over-protection is
   the safe side — `ProtectedRoots`'s own remark), and the refusal sentence names the fix.
3. **The policy stays pure; the facts come from the seam** (`BuildOutputFacts`, `internal` constructor) — "the predicate re-run
   inside the policy" is satisfied by data the policy did not get from the action (§B.2).
4. **The policy is asked per candidate AND per entry** (cheap string rules; the invariant "every delete goes through the policy"
   holds for 1.3 M files).
5. **Two passes per candidate** (verify, then delete) so a foreign reparse tag or a nested `.git` retains the candidate WHOLE,
   as the plan says, instead of after some deletes (§B.3.1).
6. **The `FinalPath` fence is exact equality with the expected relative place**, not "beneath the root" — the only check that
   catches a folder swapped for a junction inside the candidate (§A.5). The candidate folder is held (no rename of the chain).
7. **Hard links: the archive's `IsPlainFile` is not applied**; the name is deleted with `IGNORE_READONLY_ATTRIBUTE` (measured §A.6).
   The classic disposition fallback is allowed (not an agent folder); a read-only file there is kept and named.
8. **`DryUnderSetting`**: a button/CLI run of A24/A24Orphans is dry while `dryRun` is on; the week stays the timer's. The
   alternative — the task spelling `--timer` and the panel turning `auto.A24` on — is refused by two standing rules (the extension
   never writes daemon config; trust `Off`).
9. **`Idle = TimerOnly`, `Scope = Machine`**: buttons never meet `SampleIdle`'s Windows deferral; the timer path is deferred with
   the visible reason until a Windows idle sample exists. `SampleIdle` unchanged.
10. **A24 runs under the run lock** (the engine's); the plan's lease-only wording is DH-5's to resolve (a body that is not `act`
    would be needed; recorded as DH-5's open point, not changed here).
11. **The seam is `ITreeRemoval`** (not "build output files"): a tree removal under an admitted root with a verify pass, a delete
    pass and closed outcomes — what WR-T1's A23 consumes next, so the plan's §6 extraction happens by naming, not later.
12. **The cursor is taken by either action**; a worktree's candidate under A24Orphans (or the reverse) is left for the right action.
    An unreadable cursor refuses the action (fail closed) rather than being rewritten.
13. **The preview is a LIVE scan** (plan §15a #0), never `worktrees.json`, even at 83 s over 321 worktrees (measured); DH-5's
    preview timeout is theirs to size.
14. **`GitConfirmation` is the ONE file that binds S4**; until S4 lands it answers `Retained("git questions not built")`, so DH-4
    is buildable and testable now, and A24 deletes nothing under a worktree until git can confirm — A24Orphans with a gone
    `<common>` (≈44 of the 45 GB measured in the one-off) does not wait.
15. **Owed measurements, named**: M-DH4-1 ReFS POSIX delete + `IGNORE_READONLY_ATTRIBUTE` on `D:` (the fallback is specified);
    M-DH4-2 the verify+delete double pass over the owner's largest `node_modules` (pnpm, hard links) for the time budget.
16. **A measured risk for M6 / S3, not resolved here** (`m12/m6_ext.csv`): the idle bin/obj trees hold 9 842 extension-less
    files, 1 362 `.gz`, 1 030 `.out`, 407 `.current`, 370 `.stat`/`.procs`, 48 `.golden`, 22 `.jsonl`/`.ndjson` — this repository's
    own test fixtures copied to output. Under §3.3's AND rule ("every file a build artifact") most of those `bin` folders RETAIN.
    The time-skew rule (E19 §3 decision 11) would clear them (a copied fixture keeps its source write time, older than the
    build's dll) — but switching the rule to "listed OR not later than the build" loosens a plan-round decision, so it is
    **Q-DH4-1 for the owner**, raised with the first real preview's `retained{notArtifact}` figure (E20 done-when 9), not decided
    here. Also from `m6_hits.txt`: all 17 "data-file hits" are `Microsoft.Data.Sqlite.dll` — a `*.sqlite*` substring glob is a
    false positive; the never-artifact rule must match the FULL extension (`.db`, `.sqlite`, `.sqlite3`, `.sqlite-wal`,
    `.sqlite-shm`, `.sqlite-journal`, `.mdf`, `.ldf`, `.env`, `.pfx`, `.key`, `.pem`) and the `.env*` NAME prefix, never
    `Contains("sqlite")` — S3's `BuildOutputRule` owns it; DH-4's RED 13/38 assume it.
17. **Nothing here changes the parent plan's text**; the deviations above are to be written into `PLAN_disk_hygiene_windows.md`
    §3.4 by the story (the E19 stories document's convention: "each a deviation from the parent's text, recorded there in the
    same commit"), and E20's stories document — when the Opus judge writes it — carries DH-4 as `model: fable` with this spec.

## 4a. Plan round (coai, 2026-10-15) — three findings accepted, binding on §4

Round for the gate's epic 2/3: **proceed** (3 gating against 6). All three accepted; where they and §4 differ, these win:

1. **The candidate folder itself is removed through a fresh, verified handle** (finding 0: a hold opened without
   `FILE_SHARE_DELETE` makes the folder's own disposition fail with a sharing violation). After every child is gone, the hold
   is CLOSED; the folder is reopened with `DELETE | READ_ATTRIBUTES`, `OPEN_REPARSE_POINT | BACKUP_SEMANTICS`; it is
   disposed only when that handle's attributes say a plain directory (no reparse bit), its `FinalPath` equals the recorded
   candidate path exactly, and it is empty (error 145 otherwise → "partly removed"). A folder swapped for a link in that
   window is a link, fails the check and is retained. RED: `The_candidate_folder_is_removed_through_a_fresh_verified_handle`,
   `A_candidate_swapped_for_a_junction_after_its_children_are_gone_is_retained`.
2. **The checkpoint retires instead of trapping** (finding 1): `build-output-cursor.json` older than
   `worktrees.checkpointMaxAgeDays` (7, 1–90, Lower), or naming a candidate that no longer exists or a root no longer
   admitted, is retired with a logged note and the run goes on with a fresh preview; an UNREADABLE checkpoint is renamed
   aside (`build-output-cursor.unreadable-<utc>.json`, its own folder only, one file kept) and THAT run refuses once with
   the reason — the next run proceeds. The resumed candidate is ALWAYS re-verified (R0–R6 of §4 B.1) before any delete; the
   checkpoint only says where to look first. RED: `A_stale_checkpoint_is_retired_and_the_run_goes_on`,
   `An_unreadable_checkpoint_is_set_aside_once_and_never_blocks_twice`, `A_resumed_candidate_is_reverified_before_its_first_delete`.
3. **The file system is a precondition, not a surprise** (finding 2): a root is admitted only on a local fixed NTFS or ReFS
   volume (`DriveInfo.DriveFormat`, a new rule beside E19.S3's root rules — FAT/exFAT and network refused with the reason);
   a `FileDispositionInfoEx` that answers `ERROR_INVALID_PARAMETER` or `ERROR_NOT_SUPPORTED` falls back to the classic
   `FileDispositionInfo` (allowed here: not an agent folder), where a read-only file is kept and named, never its attribute
   changed. M-DH4-1 (ReFS POSIX delete on D:) is measured in the story before the default is relied on. RED:
   `A_root_on_a_fat_volume_is_refused`, `An_unsupported_posix_disposition_falls_back_and_keeps_read_only_files`.

## 5. Order and gates

E20.S1 after E19.S3+S4; E20.S2 after its runner dependencies. One coai plan round for this document (the gate's epic
`2/3`); a code round per story; the parent's `review_feature` after the last epic.

## 6. Definition of Done

- [ ] E20.S1 merged with RED tests watched failing, break-it run, the Fable security judgement applied, the coai code round
      resolved, full suites green on both CI legs.
- [ ] E20.S2 merged when its dependencies land; the owner's first real dry preview recorded in `POST_DEPLOY.md`.
- [ ] M6 (the artifact census) recorded in `research/2026-10-15_disk_hygiene_measurements.md` (created by whichever story lands first).
- [ ] Docs per story; `plan-lifecycle` clean; `todo/README.md` row.
