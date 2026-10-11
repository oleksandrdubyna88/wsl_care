# PLAN — the Windows runner: two per-user scheduled tasks run the staged `wsl-care.exe` under one bounded supervisor

> Status: **in progress, 2026-10-11 — WR-D1 built (`tasks render`, the `windowsTasks.*` keys, `DryRunWindow.Peek`, the render golden; *WR-D1 as built* in §14); WR-D2 onward not built.** Scope: the `wsl-care` daemon (`src_daemon/`: a `tasks` verb family — `render`, `stage`, `run` — a Windows branch of `watch`, one new exit code, new `windowsTasks.*` keys) and the VS Code extension (`src_vs_code/`: a `windowsTasks/` module, the widened `WindowsCareClient`, five panel buttons, settings, the Archive page's Windows side), and the Windows `%TEMP%` cleanup (W-A2, §3.12, added 2026-10-11). Nothing elevated, nothing as SYSTEM.
>
> Related: [PLAN_twenty_sessions_all_day.md](PLAN_twenty_sessions_all_day.md) S7b (A22 = S7b.3, the Windows stop; S7b.1 the ledger; S2b the watch), [PLAN_windows_care.md](PLAN_windows_care.md) §2 and W-A15 (E11), [PLAN_wsl_care_daemon.md](PLAN_wsl_care_daemon.md) D9 (:3010–3017), [PLAN_ai_session_archive.md](PLAN_ai_session_archive.md), [PLAN_bundle_windows_binary.md](../research/PLAN_bundle_windows_binary.md) (E7.S5a, §4 and its forward note), [PLAN_windows_time_task.md](../research/PLAN_windows_time_task.md) (the task-registration precedent), [PLAN_pwsh_engine_windows_time.md](../research/PLAN_pwsh_engine_windows_time.md) (the engine rule, F1/F2), [2026-10-10_dry_week_report.md](../research/2026-10-10_dry_week_report.md).
>
> Decision record: coai consultation `0b22e1a4` (closed solved, its claims checked in the code and on the machine, 2026-10-11):
> option (b), opt-in per-user tasks, two cadences, one supervisor. The measurements:
> [2026-10-11_windows_runner_facts.md](../research/2026-10-11_windows_runner_facts.md).

## 1. Symptom and goal

**Symptom.** The extension ships the attested `bin/wsl-care.exe` (E7.S5a), but it runs it only for `--version` on the panel's refresh (`src_vs_code/src/client/WindowsCareClient.ts:102`, `src_vs_code/src/bundled/bundledHost.ts:29`). Nothing runs the Windows binary periodically. Three consumers wait:

1. **A22 (S7b.3)**, the Windows-side stop of orphaned or idle MCP servers, needs a Windows `watch` that runs every few minutes (PLAN_twenty_sessions_all_day.md:1229–1233). Today `wsl-care.exe watch` is refused on Windows (`src_daemon/src/WslCare.Cli/Commands/WatchCommand.cs:50–52`, `Core/Watch/WatchRun.cs:86, 92–95`).
2. **The Windows CPU ledger** (`%LOCALAPPDATA%\wsl-care\mcp-cpu.json`) advances only when `wsl-care.exe status` runs (`Cli/Commands/StatusCommand.cs:70–72`, `Core/Mcp/McpCpuLedger.cs:86–87`). A baseline is usable for at most `mcpServers.cpuIntervalMaxMinutes` (default 20, `Core/Config/default.json:353`; range 10–1440, `ConfigKeys.Numbers.cs:620`). The watch period must stay under it (`NumberRules.cs:90–92`). So the cadence must be minutes, not E11's 4 hours.
3. **W-A15**, the Windows archive: until E11 the owner's Windows Claude sessions move only by a button (PLAN_wsl_care_daemon.md:3010–3013). The engine supports Windows (`Core/Archive/WindowsSide.cs`, `Core/Files/PhysicalFileSystem.Archive.Windows.cs`; `Cli/Commands/ArchiveRunCommand.cs:30–43` runs `archive run` as the user and refuses an elevated process at :27–36).

**Goal.** When this is done:

- A person can press one button per task to register, in `\wsl-care`, a per-user scheduled task that runs a verified COPY of the bundled exe: `watch` every `mcpWatchdog.periodMinutes` (5), and `archive` at logon + delay and every N hours. One button removes everything. No UAC prompt, ever.
- Every task run and every button run goes through ONE supervisor: a kill-on-close Job Object with a configured deadline, so no child or grandchild outlives a run.
- The panel shows, after any reload, the real state: staged version and digest, each task's presence, its last run, the Windows dry-run state; read from Task Scheduler and the daemon, never from a window's flag.
- Runs are visible through the existing readers: `wsl-care.exe runs --json` / `logs --json` (they read `{state}/history.jsonl` and `runs/` of the host's paths, `Cli/Commands/LogsCommand.cs:32, 44`; `Core/Records/RunRecordWriter.cs:25`, `RunDetailStore.cs:41`), `archive status --json` and the per-side archive index (`Core/Archive/ArchiveNames.cs:60–71`, side `windows-<host>`).

## 2. Measured and verified

**On the owner's machine, 2026-10-11, read-only from an unelevated shell** ([the record](../research/2026-10-11_windows_runner_facts.md)):

- Task Scheduler folder `\wsl-care` exists (the Windows Time guard's elevated install made it). Its SDDL inherits the root's: `(A;CIID;FW;;;AU)` and `(A;OICIIOID;FA;;;CO)`. Authenticated Users may create tasks there; a creator has full control of its own task. Unelevated registration is not blocked by the ACL. **Registration itself was NOT tried**: the owner's first press is the acceptance gate (§7 Q4, §9).
- A byte-equal copy of the attested exe (sha256 `a14ad2eb…`) in a fresh folder is `NotSigned` and runs `--version` (exit 0). `VerifiedAndReputablePolicyState` reads 0 today (an earlier record said enforced). A blocked start must still be shown as Windows reports it.
- The measuring shell was unelevated.

**In the code (main at 1f3a0c6c, after E7.S5a #108 merged):**

- The `.vsix` carries `bin/wsl-care.exe` and no digest file (`src_vs_code/vsix-files.txt`; `.vscodeignore:12–13`: "never its .sha256"). `min-daemon.json` pins `bundledDaemon` (0.4.0).
- The bundle plan forbids running the extension-folder exe from a task and registering its path (research/PLAN_bundle_windows_binary.md:184–188), and its forward note asks S7b to COPY the exe and own the process tree with a Job Object (:191–194). No Job Object code exists today (grep `JobObject|CREATE_SUSPENDED` over `src_daemon/src`, `src_vs_code/src`: none).
- The daemon's child runner kills the whole tree by `Process.Kill(entireProcessTree: true)` (`Core/Processes/ProcessCommandRunner.cs:269–279`), a Toolhelp walk that misses a grandchild re-parented before the kill; the family rule asks for one shared launcher with a tree kill (`.agents/conventions/common/reliability.md` § *A timed-out child process is a killed child process*).
- The daemon renders its own scheduler unit from config: `wsl-care units dropin <unit>` (`Cli/Program.cs:124, 136–145`; `CommandLine.cs:286, 665–668`; `Core/Systemd/UnitDropIns.cs:38`). This is the precedent for the Windows task XML.
- The run lock on Windows is `%LOCALAPPDATA%\wsl-care\wsl-care.lock` (`Core/Hosting/WindowsHostPaths.cs:65`), an exclusive open released by the OS on death (`Core/Records/RunLock.cs:12–14, 33`). The watch takes it without waiting and exits 75 when busy (`WatchRun.cs:108–121`; `Cli/ExitCode.cs:54`).
- The Windows binary reads its own config layers: `%ProgramData%\wsl-care\config.json` and `%APPDATA%\wsl-care\config.json` (`WindowsHostPaths.cs:67–69`). `dryRun` defaults on (`ConfigKeys.cs:32`). `DryRunWindow.Decide` is per host paths and WRITES `first-timer-run.json` when missing (`Core/Actions/Engine/DryRunWindow.cs:35, 41–55, 66–78`); on Windows that file lands in `%LOCALAPPDATA%\wsl-care` — a Windows-local dry state by construction.
- `SelfBinary.Product()` refuses on Windows ("never starts itself as another user", `Core/Processes/SelfBinary.cs:41–44`). The supervisor starts the SAME user's copy, so it does not use that path.
- Retention: `LogRetention.Prune` runs at every logger start (`Cli/Logging/WslCareLogging.cs:94`), so the per-run log files (`DailyRunFileSink.cs:80–94`, one file per run) are pruned on Windows already. `RunRetention.Sweep` runs ONLY in `collect` (`Core/Collect/CollectRun.cs:381`) — nothing sweeps `runs/` on Windows today.
- The archive run records its own last run (`Core/Archive/ArchiveRun.cs:304`, `LastRunRecord`) and takes the per-side lease on the base (`Core/Archive/SideLease.cs:31–35, 44–56`); it never takes the run lock.
- The extension's shipped source may not spell `--timer`, `config`, `act`, `collect`, `--confirm` (`src_vs_code/src/test/structure.test.ts:179, 182, 261`); only `verbs.ts` spells verb words (:26); only `runner.ts` imports `child_process` (:59); `WindowsCareClient` alone names the exe (`WindowsCareClient.ts:25`). The runner starts by absolute path, no shell, `windowsHide` (`src_vs_code/src/process/runner.ts:117–122`), ceiling then kill (:107–108).
- The task-registration precedent: `guardTaskXml`, `SUMMARY_FUNCTION`, `durationSeconds`, `xmlText` (`src_vs_code/src/windowsTime/guardTask.ts:168–203, 210–224, 236–244, 159–161`); the install script with the XML in a here-string and `Register-ScheduledTask -Xml -Force` after `IMPORT_SCHEDULED_TASKS` (`guardScripts.ts:22, 36–49`); the remove script (:56–67); the unelevated status query through `powerShellOf` (:76–91, 106–110); the state parser and summary compare (`guardState.ts:85, 98, 208, 261, 274`); the pending record (`guardPending.ts:14`, `state/pendingRun.ts:42–60`); one query in flight (`guardHost.ts:79–87`); the flow order (`guardFlow.ts:164–172, 183–196`). The tripwire refuses `Register-ScheduledTask`, `schtasks`, `Start-ScheduledTask` and the rest (`src_vs_code/src/test/support/noRealWsl.ts:49–62`).
- The engine rule: the owner's standing rule is "PowerShell 7 first, 5.1 only when it is absent"; the shared resolver in `src_vs_code/src/powershell/` picks pwsh 7 by its verified path, else 5.1, and the SYSTEM task stays on 5.1 (research/PLAN_pwsh_engine_windows_time.md:19–21). Under pwsh 7's cut module path `Register-ScheduledTask` is absent and needs the absolute import (:41–47, F1). The COM road `Schedule.Service` is the same under both (:48–50, F2).
- The strict fake answers `--version` for `bundledExe` and refuses every other argv of that file (`src_vs_code/src/test/fake/fakeWsl.ts:66–70, 434`). The Archive page says "the Windows archive is shown by a later version" (`src_vs_code/src/archivePage/archiveViewModel.ts:73`).

## 3. Design

### 3.1 Decision record — (b) per-user tasks, not (a) an extension timer

| | (a) an extension-owned timer (S7b item 5's assumption) | (b) per-user scheduled tasks, opt-in (chosen) |
|---|---|---|
| coverage | only while a VS Code window runs; every window has a timer | logged-in coverage without VS Code; one runner per user |
| the exe's image | the extension folder — locks the image, blocks update/uninstall (bundle plan :191–194) | a verified copy under `%LOCALAPPDATA%\wsl-care\bin\<digest>\` |
| a long archive | holds the extension's runner for up to an hour, dies with the host | Task Scheduler's own process, survives a reload |
| supervision | the runner kills one process, not a tree | one Job Object supervisor for tasks AND buttons |
| consent | none (starts unasked) | the first registration is the owner's press |

The consultant's point stands: the supervisor is justified by supervision (a bounded tree), not by attestation. A copy does not weaken the attestation and does not add runtime integrity against the same user; both are said in the panel's modal.

### 3.2 What the daemon gains — the `tasks` verb family (the Windows binary; refused on Linux as `watch` is on Windows)

**`tasks render <watch|archive> [--json]`** — the precedent `units dropin` (Program.cs:124). Answers:
- the task XML, a pure function of the Windows config layers + this process's own path + the current user's SID (`WindowsIdentity.GetCurrent().User`);
- the numbers it used (the keys of §3.6) and the expected canonical summary lines (the guard's `SUMMARY_FUNCTION` shape: `principal=<sid> logon=3 level=0`, one line per trigger, settings, action);
- `registrable`: true only when its own `Environment.ProcessPath` is `<stagingRoot>\<digest>\wsl-care.exe` AND the file hashes to that digest AND `stage.json` beside it agrees — so no XML can ever name the extension folder (the bundle plan's §4 rule, enforced in the daemon too);
- the Windows dry state, read-only: a new `DryRunWindow.Peek(paths, files, config, now)` that reads the stamp and never writes it (`Decide` writes, :66–78) — "dryRun on", "first week until …", or "not started: the first task run starts it".

**`tasks stage [--json]`** — run on the BUNDLED exe from the extension folder (a short run, no child). It copies its own file (`Environment.ProcessPath`) to `<stagingRoot>\<digest>\wsl-care.exe` with an exclusive create, reads the copy back and hashes it, writes `stage.json` `{ version, sha256, stagedAtUtc, source }` atomically (the ledger's writer shape, `McpCpuLedger.cs:260–287`), then sweeps older `<digest>` folders under the root. A folder whose exe is still executing cannot be deleted (sharing violation): it is kept and named, and the next stage or remove retries. The staging root is `WindowsHostPaths.StateDirectory` + `bin` (`WindowsHostPaths.cs:51`, the shell-folder API, never the environment's `LOCALAPPDATA`). Answers the digest, the path, what was swept and what was kept.

**`tasks stage --remove [--json]`** — the *Remove* button's last step: deletes every `<digest>` folder under the staging root, newest last, and the root itself when empty. A folder whose exe is executing is kept and named (exit 0, `kept: [...]`); a folder that is not a staging folder (no `<64 hex>` name) is never touched. Run from the BUNDLED exe, as `tasks stage` is.

**`tasks run <watch|archive> [--json]`** — the supervisor (§3.3). The task's action and the buttons' "run now" both reach it. It starts `<self> watch --timer --json` or the archive body (§3.5) as a child in the job. The supervisor spells `--timer`; the extension never does (structure.test.ts:182).

**`watch` on the Windows binary** (today refused, `WatchCommand.cs:50–52`): a Windows branch of `WatchRun` — refuse an ELEVATED process (the opposite of the distro's root check, `ProcessPrivilege.cs:30–33`; S7b item 4's per-side gate; the wording of `ArchiveRunCommand.ElevatedRefusal`, :27–28; exit 81 `NotAsRoot`, `ExitCode.cs:79`); take the run lock without waiting (exit 75 is normal); sample the Windows MCP collector with `McpCpuLedgerPlace.ForWindowsStatus(windows, elevated: false)` (`McpCpuLedger.cs:86–87`, `StatusCommand.cs:70–72`) so the ledger advances every period — its read-modify-write under a LEDGER LOCK (`mcp-cpu.lock` beside the ledger, `IFileSystem.TryLockExclusive`, never waited on; the Windows `status` takes the same lock): the file is read AFTER the lock is held, and when another writer holds it this sample answers from the window and records nothing, naming why, so no write ever replaces a sample it did not read (coai plan round, finding 1; today `McpCpuLedger.Record` takes no lock, `McpCpuLedger.cs:243–251`); invoke `RunRetention.Sweep` (today only `collect`'s, `CollectRun.cs:381`); then — only with `--timer`, not observe-only, under `DryRunWindow.Decide(RunTrigger.Timer, …)` on the Windows paths — the A22 act when S7b.3 has built it. **Until A22 lands the Windows watch samples only** and writes no history line (S2b's rule, PLAN_twenty_sessions_all_day.md:326–327). The boundary with S7b.3 is in the table at the end of §3.

**One new exit code,** `84 TaskEnded`: "the supervised worker was ended at its deadline; its whole tree is gone". `contracts/exit-codes.json` regenerates; `src_vs_code/src/client/exitCodes.ts` reads it.

### 3.3 The supervisor — one bounded Job Object (`Core/Processes/JobSupervisor.cs`, win-x64 only, behind `IWorkerSupervisor`)

1. `CreateJobObjectW` with a NON-inheritable handle; `SetInformationJobObject` with `JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE` (and `JOB_OBJECT_LIMIT_BREAKAWAY_OK` off: no child may leave).
2. `CreateProcessW` of `<self>` with `CREATE_SUSPENDED | CREATE_NO_WINDOW`, the std handles inherited through `PROC_THREAD_ATTRIBUTE_HANDLE_LIST` (only those three; the job handle never), the argv a CLOSED table keyed by `watch|archive` (never free text).
3. `AssignProcessToJobObject`; on failure (a nested-job refusal on an old build) the suspended worker is terminated and the run is refused — fail closed, never resumed outside the job. `ResumeThread`.
4. The deadline: `mcpWatchdog.runLimitMinutes` for `watch` (`ConfigKeys.Numbers.cs:646`; held ≥ the watch worst case by `NumberRules.cs:93–95`), `timer.runLimitMinutes` for `archive` (:309; held ≥ the timer worst case, `NumberRules.cs:86–88`, which includes the archive child's ceiling through `RunBudget`). Waited on the process handle; on `WAIT_TIMEOUT` the elapsed time is re-checked on `QueryUnbiasedInterruptTime` (`IWindowsBoot.UnbiasedMilliseconds`, `Core/Mcp/WindowsBoot.cs:17`), so a host sleep is not run time; when the unbiased deadline has passed: `TerminateJobObject(job, 0xC5A1)`, exit 84. `CloseHandle(job)` always — kill-on-close is the net under the net.
5. The worker's stdout IS the supervisor's stdout (inherited handles); the supervisor writes one stderr line on timeout and exits with the worker's code otherwise. Ctrl+C / a task stop reaches the supervisor's `ShutdownSignals` and closes the job.
6. `ArchitectureTests`' signal scan (`src_daemon/tests/WslCare.Core.Tests/ArchitectureTests.cs:91`) is widened to `TerminateJobObject`, `TerminateProcess`, `AssignProcessToJobObject`; `JobSupervisor` is the only allowed site. The Task Scheduler `ExecutionTimeLimit` (deadline + `RunMarginMinutes`) is the outer net; the job is the real one.

### 3.4 The two tasks — XML (rendered by the daemon), registered by the extension

Common: `<Principal><UserId>S-1-5-21-…</UserId><LogonType>InteractiveToken</LogonType><RunLevel>LeastPrivilege</RunLevel>`; `<Settings>`: `MultipleInstancesPolicy IgnoreNew`, `StartWhenAvailable`, `DisallowStartIfOnBatteries false`, `StopIfGoingOnBatteries false`, `WakeToRun false`, `RunOnlyIfIdle false`, `Hidden false`, `Priority 7` (below normal; the family's local-CPU-budget rule), `ExecutionTimeLimit` = the deadline + `NumberRules.RunMarginMinutes`; `<Actions><Exec><Command>` the ABSOLUTE staged path, `<Arguments>tasks run <name> --json`.

| task | triggers | repetition | deadline |
|---|---|---|---|
| `\wsl-care\watch` | `LogonTrigger` + `windowsTasks.watchLogonDelaySeconds` | `PT<mcpWatchdog.periodMinutes>M`, indefinite | `mcpWatchdog.runLimitMinutes` |
| `\wsl-care\archive` | `LogonTrigger` + `windowsTasks.archiveLogonDelayMinutes` | `PT<windowsTasks.archiveEveryHours>H`, indefinite | `timer.runLimitMinutes` |

**Registration road — compared and chosen:**
- (1) **Unelevated PowerShell through the shared resolver** (chosen; the owner's answer to Q1, 2026-10-11): the engine the resolver in `src_vs_code/src/powershell/` picks — PowerShell 7 by its verified path, else Windows PowerShell 5.1 — started WITHOUT `runas`; the install script's shape (`guardScripts.ts:36–49`: `MODULES`, the ABSOLUTE `IMPORT_SCHEDULED_TASKS` that pwsh 7's cut module path needs (F1), the XML in a here-string, the folder through `Schedule.Service` when missing, `Register-ScheduledTask -Xml -Force`). The owner's acceptance of 2026-10-11 showed ScheduledTasks registering under elevated pwsh 7 through that import. Nothing elevated, the person sees the exact text (the guard's o7 rule), and "install is also update" (`-Force`). The panel names the engine that ran, as the Windows Time part does.
- (2) Task Scheduler COM from C# Native AOT: `ITaskService`/`ITaskFolder`/`ITaskDefinition`/… through `[GeneratedComInterface]` — a large new interop surface with no reuse; rejected.
- (3) `schtasks /create /xml <file>`: the XML must be a FILE (a swap window between showing and registering, which the guard's design closed) and a second program; rejected.

**Switching versions:** a new extension version stages a new `<digest>` folder, then registers each installed task again with the new path (`-Force`), then sweeps. A task instance still executing the old image keeps its folder until it exits (the delete fails; retried). The panel reads "installed, but not as this version would install it — install again" until then (the guard's D5 row).

### 3.5 The archive body and the Windows dry-run window

`tasks run archive` runs, as the user: `DryRunWindow.Decide(RunTrigger.Timer, …)` on the Windows paths; while dry → `<self> archive preview --json` (the existing verb, `ArchiveCommand.Preview`) and the run log records "would move N sessions, M bytes"; when real → `<self> archive run --json` (`ArchiveRunCommand.Run`). The archive run takes the side lease only (`SideLease.cs`), never the run lock, so a long archive does not hold the sampling lock and never suppresses a watch sample. A second archive (a button during a task run) is refused by the lease, naming the holder. `archive run`'s own last-run record (`ArchiveRun.cs:304`) and the per-side index are the records; `archive status --json` reads them.

**The dry week on Windows** is the daemon's own: `dryRun` (default on, `ConfigKeys.cs:32`) read from the Windows layers, and `timer.firstDryWindowDays` (7, `ConfigKeys.Numbers.cs:293`) counted from the FIRST `tasks run` that reaches a gated body — the stamp in `%LOCALAPPDATA%\wsl-care\first-timer-run.json`. Two locks on one door, as the distro had (dry-week report :17–22). The panel names both from `tasks render`'s dry fields, and the modal says the owner ends it with `wsl-care.exe config set dryRun false` (the extension never writes daemon config, S7b item 6).

### 3.6 Keys and settings — every number a key, the existing split

**Daemon keys (the Windows layers; `contracts/config-keys.json` regenerated):**
- reused: `mcpWatchdog.periodMinutes` (5, 2–15, machine-only; the rule `< cpuIntervalMaxMinutes` at `NumberRules.cs:90–92` holds the cadence under the baseline's maximum), `mcpWatchdog.runLimitMinutes`, `timer.runLimitMinutes`, `timer.firstDryWindowDays`, `dryRun`, `archive.*`, `logging.retentionDays`, `runs.historyRetentionDays`, `records.maxStateFileBytes`;
- new `windowsTasks.watchLogonDelaySeconds` (60, 0–3600, `SafeDirection.Higher`), `windowsTasks.archiveLogonDelayMinutes` (20, 0–720, Higher; E11's "logon + 20 min"), `windowsTasks.archiveEveryHours` (4, 1–24, Higher). None machine-only (an unelevated runner's user layer is the natural place; Q2).
- new for the `%TEMP%` cleanup (§3.12): `temp.olderThanDays` (7, 1–365, Higher), `temp.allowGlobs` (a list, default `["coai-*", "wsl-care-test-*"]`; a glob may not contain a path separator and may not match `claude`), `temp.scanMaxNodes` (200000, 1000–10000000, Lower), `temp.scanMaxSeconds` (120, 10–3600, Lower), `temp.maxItemsPerRun` (5000, 1–1000000, Lower), `windowsTasks.cleanupLogonDelayMinutes` (30, 0–720, Higher), `windowsTasks.cleanupEveryHours` (24, 1–168, Higher).

**Extension settings (`settings/numbers.ts`, held equal to `package.json`):** `wslCare.timeouts.windowsTasksSeconds` (60, 10–600: the unelevated install / remove / run-now PowerShell), `wslCare.timeouts.windowsTasksStageSeconds` (60, 10–600: `tasks stage` and `tasks render`), and the Task Scheduler read reuses `wslCare.timeouts.windowsTimeGuardQuerySeconds` because the read is ONE query (§3.8, Q3).

### 3.7 Provenance — where the expected digest comes from

The `.vsix` carries no digest file. The expected digest is the SHA-256 of `<extension>/bin/wsl-care.exe` as the extension itself computes it (Node `crypto`, a read) — the bytes the attest job verified member for member (bundle plan §3.5, §11). `tasks stage` reports the digest of the copy it read back; the extension refuses the stage when the two differ or when `stage.json`'s `version` is not `BUNDLED_DAEMON`. `tasks render` re-verifies the staged file against `stage.json` at every render. Recorded: version + digest in `stage.json` and in the panel line. Said plainly in the modal: GitHub's attestation proves the released bytes; nothing here is integrity against the same user.

### 3.8 The extension — `src/windowsTasks/`, the buttons, the reads

- **`WindowsCareClient` widened** (still the one module naming the exe; `structure.test.ts` with its planted companion): `exe(source)` for `bundled` and `staged(<digest>)`; `tasks(stage | render <name> | …)`; `read(request)` over the existing `RUN_READ_VERBS` table (`verbs.ts:62–70`: `runs`, `logs`, `runsShow`, `archiveStatus`, `archiveList`). New verb words go in `verbs.ts` only. The strict fake learns `stagedExe` and the `tasks` answers (scenario files in `answers/`).
- **One Task Scheduler read:** the guard's `QUERY_SCRIPT` (`guardScripts.ts:76–91`) is widened (reuse-first move 1) to enumerate every task under `\wsl-care` and print, per task, `task=<name>`, `enabled`, `state`, `lastRunUtc`, `lastResult` and its `summary:` lines, plus `user=<sid>`; `guardState.ts`'s parser and `summariesMatch` take a task name. A `schedulerHost.ts` holds the one read in flight (the `guardHost.ts:79–87` shape) and both the guard and the new part consume it.
- **The flows** (`windowsTasks/tasksFlow.ts`, the `guardFlow.ts` order): *Install the Windows watch task* / *Install the Windows archive task*: stage when needed (bundled exe, `tasks stage`) → `tasks render <name>` on the STAGED exe → the XML shown read-only (the guard's `show`) → a modal that names the principal, triggers, deadline, the staged path + digest, the engine, the dry state and what the body does → the pending record written (`state/pendingRun.ts`, key `wslCare.windowsTasks.pending`) → the ONE unelevated PowerShell → the read again. *Remove the Windows tasks*: delete both tasks if present, then the folder only when empty (the guard's `REMOVE_SCRIPT` rule, `guardScripts.ts:62`), then `tasks stage --remove`, which deletes the staging folders it can (an executing image is kept and named, and retried by the next stage or remove). *Run the Windows watch now* / *Archive Windows sessions now*: `Schedule.Service` → `GetTask(<name>).Run($null)` of the person's OWN registered task (unelevated COM) — so a button run IS a task run: same supervisor, `IgnoreNew` makes an overlap a no-op that the panel reports, and the run survives a reload. Not installed → the button says "install the task first".
- **The panel** (`PanelView` gains `windowsTasks`, beside `windowsTimeGuard`, `src_vs_code/src/panel/view.ts:114–119`; a `div[data-windows-tasks]` in *Health*): "Windows daemon copy: <version> (<digest12>) staged / not staged"; one line per task from the state table (the guard's D5 rows: not installed · installed, last run <t>: <sentence> · install again to update · disabled · unreadable/not Windows); the dry line. Buttons: the five above, in `COMMAND_BUTTONS` (`panel/commandButtons.ts:23–38`) with `commandButtons.test.ts`. Palette entries duplicate them.
- **Durable status** (`common.durable-status`): the pending record is written BEFORE each run and cleared on its answer (kept to its deadline after a timeout); `sweepPendingAt` at activation beside the guard's (`extension.ts:238`); the truth is Task Scheduler's `state`/`lastResult` and the daemon's records; a `0x41301` running task reads "running" in every window.
- **The Archive page's Windows side (E-W5):** `archiveStatus` / `archiveList` through the Windows client; a side selector replaces `ARCHIVE_SCOPE` (`archiveViewModel.ts:73`). **The Logs page's Windows side (E-W6)** reads `runs`/`logs`/`runs show` the same way; it has value once A22 writes history lines, so it is last.

### 3.9 Concurrency

| case | what holds |
|---|---|
| a button run and a task run at once | `IgnoreNew`; the watch's run lock (exit 75 is normal); the archive's side lease refuses the second and names the holder |
| a manual `wsl-care.exe status` and the watch task both write the ledger | the ledger lock (§3.2): the second writer reads nothing stale and writes nothing — its sample is not recorded, said in its answer — so neither replaces the other's sample; the atomic private write (`McpCpuLedger.cs:260–287`) stays the crash rule. A sample lost to a held lock only makes the next interval longer (still under `mcpServers.cpuIntervalMaxMinutes` at a 5-minute period), and a missing point is "not idle" for A22, never "idle" |
| an upgrade during a run | a new `<digest>` folder; the tasks re-registered to it; the executing old image keeps its folder until it exits; swept later |
| sleep / resume | the repetition fires at its next interval; the supervisor's deadline on unbiased time; the run lock is an open handle, released by the OS |
| VS Code closed | the tasks run; the panel re-reads on open; nothing of the window is a source of truth |
| two VS Code windows | one pending record in `globalState`; a second press reads `busy` |

### 3.10 Growth — before the first write

| surface | projected size | retired by | interrupted |
|---|---|---|---|
| `%LOCALAPPDATA%\wsl-care\bin\<digest>\` | 13.7 MB × (1 + the versions still executing). An old folder is kept only while a run started from it executes, and every run ends by its job deadline (`timer.runLimitMinutes` for the archive), so an old folder outlives its switch by at most one deadline. Worst case: one folder per extension upgrade within one deadline — in practice 2; the panel names every kept folder | `tasks stage`'s sweep at every stage; *Remove*; the sweep also runs at every `tasks run` start, so a kept folder goes as soon as its run ends | a half-copied folder has no `stage.json` → swept as unverified |
| `stage.json` | ~200 B | overwritten atomically | — |
| run logs `logs\<day>\` | 288 watch runs/day × ~1 KB ≈ 0.3 MB/day | `LogRetention.Prune` at every start (`WslCareLogging.cs:94`, `logging.retentionDays` 14) ≈ 5 MB | — |
| `mcp-cpu.json` | ≤ `mcpServers.maxInstances` × 2 points, under `records.maxStateFileBytes` | the ledger's own rule | its temp files swept (`McpCpuLedger.cs:295–304`) |
| `history.jsonl`, `runs/` | 0 until A22; then S7b item 7's ≤ 5 MB/day, ≤ 70 MB | `RunRetention.Sweep`, now invoked by the Windows watch | the engine's reconcile |
| the archive's state and index | the archive plan's own (D11: kept forever by decision) | — | the archive's reconcile |
| `temp-cursor.json` (§3.12) | ~200 B (the last decided name and the run id) | overwritten atomically at each cleanup run | a missing or unreadable cursor starts from the first candidate |

### 3.11 Not in scope

Elevated work and `collect-elevated` (PLAN_windows_care §2, §8a); logged-out runs (an `S4U`/password principal); E12's other `%TEMP%` items (stale `swap.vhdx`, which W-A2 also names — left to E12) and every other W-A action; A22's stop logic (S7b.3 — this plan gives it the verb and the cadence); the WSL→Windows `tar` hand-off; Authenticode signing; the Logs page's Windows side before A22.

**Boundary table (to be written into PLAN_twenty_sessions_all_day.md S7b.3, PLAN_windows_care.md E11, PLAN_wsl_care_daemon.md D9 and PLAN_bundle_windows_binary.md §4 — "named on both sides"):**

| item | this plan | the other plan |
|---|---|---|
| the Windows `watch` verb, its lock, ledger write, retention sweep, dry gate | here (WR-D4) | S7b.3 adds the A22 act inside the `--timer` branch |
| the Windows watch cadence and runner | here (the `\wsl-care\watch` task) | S7b.3 item 5's "extension timer" assumption is superseded |
| the staged copy and its path | here | bundle plan §4: "never register its path" stays true of the EXTENSION folder |
| `\wsl-care\collect` (4-hourly Windows collect), elevated tasks | — | E11 (PLAN_windows_care) |
| W-A15's schedule | here (the `\wsl-care\archive` task) | the archive plan keeps the verbs; D9's "until E11" clause is closed by this plan |
| W-A2, the `%TEMP%` cleanup (top-level scratch older than N days, allow-listed) | here (A23, WR-T1–T3, the `\wsl-care\cleanup` task) | PLAN_windows_care.md W-A2 points here; its stale-`swap.vhdx` item stays E12 |

### 3.12 The Windows `%TEMP%` cleanup — W-A2 on the runner (the owner's decision, 2026-10-11)

**Why.** Measured read-only on 2026-10-11 ([2026-10-11_windows_temp.md](../research/2026-10-11_windows_temp.md)): the user's
`%TEMP%` holds 165 579 top-level entries (156 943 folders, 8 636 files); 138 790 folders and 256 files are older than 7 days. The
bulk comes from many tools' scratch folders (`stage-<hex>` 12 148, `mindex-idx-<hex>` 5 328, `git-commit-<hex>` 993, …),
not only coai's (`coai-*` ≈ 4 600, all from that day — coai's own pruner is a separate issue) — and this repository's own tests
leave `wsl-care-test-*` folders behind (hundreds within 4 days; a test-hygiene defect fixed in its own PR, never hidden by
this cleanup). W-A2 (PLAN_windows_care.md §4) owns the action; this plan builds it on the runner, so the W-A2 row points here.

**One measured correction to W-A2's text** ("locked = in use = skipped"): a file held open with `FILE_SHARE_DELETE` is deleted
with NO error on this machine (2026-10-11, a disposable file in the scratchpad), so a sharing violation does not prove "not in
use". The guards below do not rely on it alone (coai consultation `f2317bca`).

**The action — A23 `windowsTemp`, the engine's FIRST Windows-side action** (`ICleanupAction.Sides`,
`Core/Actions/ICleanupAction.cs:231`; `act` refuses an action of the other side, `Cli/Commands/ActCommand.cs:116–122`). The
story proves the engine's Windows path before relying on it: the run records under `%LOCALAPPDATA%\wsl-care` read by the
existing readers, and the dry window of §3.5.

- **Root:** the current user's `%TEMP%` from the shell-folder API (never the environment variable), its top-level entries
  only as candidates.
- **Never:** anything under `%TEMP%\claude\` (the owner's 2026-10-02 rule, a hard-coded guard checked BEFORE any traversal and
  independent of every setting); an entry not owned by the current user's SID; an entry that is or contains a reparse point
  (the walk never follows one, and a tree that holds one is retained as "unknown").
- **Selection for deletion — an ALLOW-list:** `temp.allowGlobs` (default `["coai-*", "wsl-care-test-*"]`: scratch whose disposal
  is known — coai's review rounds and tests, and this repo's own tests), each candidate's NEWEST descendant write time (files AND
  folders) older than `temp.olderThanDays` (7). A tree not scanned to its end within the budget is "unknown — retained".
  Files directly in `%TEMP%` follow the same rules with their own time.
- **The preview shows MORE than it would delete:** the resolved root, the cutoff and the age rule; eligible / unknown / excluded
  / deferred counts; "deleted logical bytes" estimated (never "disk freed"); groups by name pattern with exact paths on expand —
  including the older-than groups that the allow-list does NOT cover ("would be eligible if allowed"), so the owner can widen
  `temp.allowGlobs` (`wsl-care.exe config set …` — the extension never writes daemon config) from evidence, not guesses.
- **Budgets on every run:** `temp.scanMaxNodes`, `temp.scanMaxSeconds`, `temp.maxItemsPerRun`; a cursor in
  `%LOCALAPPDATA%\wsl-care\temp-cursor.json` resumes after the last decided candidate, so a 138 k backlog drains over runs.
- **A stable order that wraps** (coai plan round, finding 1): candidates are the top-level names in ORDINAL order; a run starts
  after the cursor, and when it reaches the end it wraps to the first name and goes on until its budget or the cursor's own name,
  so an entry created before the cursor is reached on the same pass or the next. The cursor file records when the current pass
  began; the preview names "pass started <t>, N of M names decided".
- **Recovery after a kill mid-delete** (finding 2): before the first delete inside a candidate, the cursor file records it as
  `inProgress` with the newest FILE write time and the owner seen when it was judged, written atomically. Deleting children
  updates a folder's own write time, so a resumed candidate is never re-judged by folder times: the next run takes the
  `inProgress` candidate FIRST, re-checks owner, allow-list and reparse points, compares only its remaining FILES' newest write
  time with the recorded one (any newer file → retained, "changed while interrupted"), and finishes or retains it before the
  cursor moves on. The run itself follows the engine's interrupted-run rule (the reconcile of an `act` run that never wrote its
  end), and the panel's last-cleanup line reads "interrupted" for it, never "completed".
- **The delete:** each candidate is re-validated just before (owner, age, no reparse point, still allowed); a sharing violation
  or a partial delete is kept and counted, never retried in the same run; the run detail records counts per outcome, per group,
  and the deleted logical bytes.
- **Dry first:** `dryRun` and `timer.firstDryWindowDays` on the Windows paths (§3.5) — while dry the run previews only.

**On the runner:** a third task, `\wsl-care\cleanup` (`LogonTrigger` + `windowsTasks.cleanupLogonDelayMinutes` (30),
repetition `PT<windowsTasks.cleanupEveryHours>H` (24), deadline `timer.runLimitMinutes`), runs `tasks run cleanup` under the
same supervisor; the body is `act A23` with the timer trigger (the supervisor spells it, never the extension). Its own lock is
the run lock, taken without waiting (a cleanup never runs beside a watch; a busy lock is "try at the next run").

**In the panel:** *Preview the Windows TEMP cleanup* (read-only, through `WindowsCareClient` on the staged exe as `tasks preview
cleanup` — the extension spells neither `act` nor `--confirm`; its own timeout `wslCare.timeouts.windowsTempPreviewSeconds`),
*Install the Windows cleanup task*, *Clean Windows TEMP now* (the task's own `Run`, as §3.8's run-now buttons), and the line
"last cleanup: <t>, N deleted, M kept in use, <bytes> logical" from the run records.

## 4. Never

- Never run the bundled or staged exe elevated, as SYSTEM, from a task with a non-interactive logon, or from a path outside the user's profile; never register the extension folder's path.
- Never resume a worker that is not inside the job; never let the job handle be inherited; never start free argv through the supervisor.
- Never stop a process by image name — pid + creation time only (A22's rule; the supervisor terminates only the job it created).
- Never take the run lock in the archive body; never hold it across a wait.
- Never write daemon config from the extension; never spell `--timer`, `config`, `act` in the extension's shipped source.
- Never register, run or delete a real task, start a real `wsl-care.exe`, or signal a real process from a test; never weaken the tripwire, a recorder or a fake to prove anything.
- Never stage over an existing folder in place; never delete a folder whose image is executing (it is named and retried).
- Never guess a refused start's code: Node's or Task Scheduler's own text/number is shown.

## 5. Epics and stories (one PR per story)

| id | title | files | tests | Fable own reviewer | depends on |
|---|---|---|---|---|---|
| WR-D1 | `tasks render` + the `windowsTasks.*` keys + `DryRunWindow.Peek` | `Cli/Commands/TasksCommand.cs` (new), `Cli/CommandLine.cs`, `Core/Scheduler/WindowsTaskDefinitions.cs` (new), `Core/Config/ConfigKeys.Numbers.cs`, `default.json`, `NumberRules.cs`, `Core/Actions/Engine/DryRunWindow.cs`, `contracts/config-keys.json`, `contracts/golden/windows-tasks-render.json` (new) | Core.Tests goldens; Cli.Tests parse; Peek never writes | no | — |
| WR-D2 | `tasks stage` and `tasks stage --remove`: copy, verify, `stage.json`, sweep, remove | `Core/Scheduler/Staging.cs` (new), `TasksCommand.cs` | Core.Tests over `WSL_CARE_ROOT`; Scenarios: the built binary stages into a sandbox root | **yes** (deletes folders) | D1 |
| WR-D3 | `tasks run`: the Job Object supervisor, exit 84, the scan widened | `Core/Processes/JobSupervisor.cs` (new), `IWorkerSupervisor`, `Cli/ExitCode.cs`, `contracts/exit-codes.json`, `tests/…/ArchitectureTests.cs` | Core.Tests with a fake; Windows leg: child + grandchild timeout on the test's OWN helper process | **yes** (ends processes) | D1 |
| WR-D4 | the Windows `watch`: elevated refused, lock, ledger (under the ledger lock), retention sweep, the A22 hook | `Core/Watch/WatchRun.cs`, `Cli/Commands/WatchCommand.cs`, `Core/Mcp/McpCpuLedger.cs`, `Cli/Commands/StatusCommand.cs` | Core.Tests; a Windows flow in Scenarios | **yes** (retention deletes details) | D3 |
| WR-D5 | the archive body: dry → preview, real → `archive run`, no run lock | `TasksCommand.cs`, `Core/Scheduler/ArchiveBody.cs` (new) | Core.Tests; Scenarios on Windows with the fake side | **yes** (moves files) | D3 |
| WR-E1 | `WindowsCareClient` widened; the fake learns the staged exe and `tasks`; reads | `src/client/WindowsCareClient.ts`, `verbs.ts`, `test/fake/fakeWsl.ts`, `test/structure.test.ts` | client.test, fakeWsl.test, structure | no | D1–D2 (answers) |
| WR-E2 | one Task Scheduler read for the folder; `windowsTasks/` state + view; settings; read-only panel lines | `src/windowsTime/guardScripts.ts`, `guardState.ts`, `src/windowsTasks/schedulerHost.ts`, `tasksState.ts`, `tasksView.ts`, `settings/numbers.ts`, `package.json`, `panel/*` | unit; page harness; Windows leg parses the golden XML with `NewTask(0).XmlText` and compares `SUMMARY_FUNCTION`'s read-back to the golden's summary | no | E1 |
| WR-E3 | install / remove flows, pending records, buttons, commands | `src/windowsTasks/tasksFlow.ts`, `tasksHost.ts`, `tasksPending.ts`, `tasksUi.ts`, `tasksRecorder.ts`, `panel/commandButtons.ts`, `extension.ts`, `test/support/noRealWsl.ts` (the tripwire gains `.Run(`/`RunEx(` on a task object) | flow tests with recorders; tripwire tests | **yes** (removes folders; registers a periodic runner) | E2, D2 |
| WR-E4 | run-now buttons through the task's own `Run`, "running" state | `tasksFlow.ts`, `tasksHost.ts`, `schedulerHost.ts` | flow + page | **yes** | E3 |
| WR-E5 | the Archive page's Windows side | `src/archivePage/*`, `archive/archiveHost.ts`, `media/archive.js` | archivePage tests; scenarios | no | E1, D5 |
| WR-E6 | the Logs page's Windows side (after A22) | `src/logsPage/*` | logsPage tests | no | E1, S7b.3 |
| WR-T1 | A23 `windowsTemp`: the engine's first Windows action — selection (allow-list, newest-descendant age, owner, reparse, the `claude` guard), budgets + cursor, re-validated delete, run detail; the `temp.*` keys | `Core/Actions/WindowsTemp/` (new), `ActionId.cs`, `ActionRegistry.cs`, `ConfigKeys.Numbers.cs`, `default.json`, `contracts/*` | Core.Tests over a fake file system; a Windows Scenarios flow in a sandbox `%TEMP%` (never the real one) | **yes** (deletes) | D1, D4 |
| WR-T2 | the cleanup task: `tasks render/run cleanup`, `tasks preview cleanup`, the `windowsTasks.cleanup*` keys | `TasksCommand.cs`, `WindowsTaskDefinitions.cs`, `Core/Scheduler/*` | goldens; supervisor flow | **yes** | T1, D3 |
| WR-T3 | the panel: preview button, install, clean-now, the last-cleanup line, the preview timeout setting | `src/windowsTasks/*`, `panel/*`, `settings/numbers.ts`, `package.json` | flow + page tests; tripwire | **yes** | T2, E3, E4 |
| WR-X1 | docs, POST_DEPLOY acceptance item, the boundary rows in four plans | `research/module_daemon.md`, `module_vs_code.md`, `module_archive.md`, `module_tests.md`, `architecture.md`, `POST_DEPLOY.md`, the four plans, `todo/README.md` | plan-lifecycle | no | lands with D1 and E3 |

## 6. Tests

**RED first (named before the product code):**
- D1: `The_render_refuses_a_copy_outside_the_staging_root`; `The_render_names_the_user_sid_and_the_staged_path`; `The_watch_task_repeats_every_period_minutes_and_the_archive_every_n_hours`; `Peek_never_writes_the_stamp`; `A_period_at_or_above_the_interval_maximum_breaks_the_rule` (exists; held); `The_render_is_refused_on_the_linux_binary`.
- D2: `A_stage_copies_byte_for_byte_and_records_version_and_digest`; `A_stage_into_an_existing_digest_folder_never_overwrites`; `An_older_folder_is_swept_and_an_executing_one_is_kept_and_named`; `A_mismatched_read_back_leaves_no_stage_json`; `Remove_deletes_every_staging_folder_but_keeps_and_names_an_executing_one`; `Remove_never_touches_a_folder_that_is_not_a_digest`.
- D3: `The_worker_is_created_suspended_and_resumed_only_inside_the_job`; `A_failed_assign_terminates_the_suspended_worker`; `The_job_handle_is_not_inheritable`; `A_child_and_its_grandchild_are_gone_after_the_deadline` (Windows leg, the test's own helper process); `The_deadline_counts_unbiased_time`; `The_scan_finds_TerminateJobObject_outside_the_supervisor`; `Free_argv_is_refused`.
- D4: `An_elevated_windows_watch_is_refused_as_not_as_root`; `A_busy_lock_exits_75_as_normal`; `The_windows_watch_writes_the_ledger_and_no_history_line`; `The_windows_watch_sweeps_run_retention`; `Without_A22_the_timer_branch_acts_on_nothing`; `A_held_ledger_lock_records_nothing_and_says_why`; `The_ledger_is_read_after_its_lock_is_held` (two writers interleaved through the fake file system: neither drops the other's sample).
- D5: `A_dry_archive_body_runs_the_preview_and_moves_nothing`; `A_real_archive_body_runs_archive_run`; `The_archive_body_never_takes_the_run_lock`; `A_second_archive_is_refused_by_the_lease_naming_the_holder`.
- E1: `the staged exe is named only by WindowsCareClient`; `the fake answers tasks and refuses unknown argv`; `the extension never spells --timer`.
- E2: `one query reads every task under \wsl-care`; `each state row renders`; `the Windows leg reads back the golden XML as the golden summary`.
- E3: `the order is stage → render → show → modal → pending → one unelevated PowerShell → read`; `a digest mismatch stages nothing and registers nothing`; `a render that is not registrable registers nothing`; `a decline runs nothing`; `a pending run holds every button across a reload`; `the tripwire refuses Register-ScheduledTask and a task's Run(`; `the remove keeps the folder while the guard's task is in it`.
- E4: `run-now starts the person's own task and nothing else`; `a running task reads running in every window`; `not installed → install first`.
- E5/E6: `the Windows side's archive status and list render`; `the Logs page reads the Windows history`.
- T1: `Nothing_under_temp_claude_is_ever_selected_whatever_the_allow_list`; `An_entry_of_another_owner_is_excluded`; `A_tree_holding_a_reparse_point_is_retained_and_never_followed`; `An_old_parent_with_a_fresh_nested_file_is_retained`; `A_tree_scanned_past_its_budget_is_unknown_and_retained`; `An_old_folder_outside_the_allow_list_is_previewed_as_would_be_eligible_and_never_deleted`; `A_sharing_violation_is_kept_and_counted`; `The_candidate_is_revalidated_just_before_the_delete`; `The_cursor_resumes_after_the_last_decided_candidate`; `An_entry_created_before_the_cursor_is_reached_after_the_wrap`; `An_in_progress_candidate_is_taken_first_and_judged_by_its_file_times_not_its_folder_times`; `A_file_newer_than_the_checkpoint_retains_the_interrupted_candidate`; `An_interrupted_run_reads_interrupted_not_completed`; `While_dry_the_run_deletes_nothing`; `The_run_detail_counts_outcomes_and_logical_bytes`.
- T2/T3: `the cleanup task renders with its own cadence`; `the preview button never spells act or --confirm`; `clean-now starts the person's own cleanup task`.

**Break-it on PRODUCT code only (each red, then restored):** the job's kill-on-close flag off; the assign failure ignored (worker resumed anyway); the job handle made inheritable; the deadline on wall time; the registrable check removed from `render`; the digest compare removed from the stage; the run lock taken in the archive body; the dry decision ignored in the archive body; the ledger read before its lock; the sweep at `tasks run` start removed; `--timer` spelled in the extension; the summary compare dropping the principal; the pending record written after the launch; the remove deleting the folder with the guard's task in it; the query reading one task only; the `claude` guard removed; the owner check removed; reparse points followed; the age read from the top-level folder only; the budget ignored (scan to the end); the re-validation before the delete removed; the wrap removed (the scan stops at the end); the `inProgress` checkpoint written after the delete; a resumed candidate judged by its folder times.

## 7. Owner questions — answered 2026-10-11 (through the coordinator)

- **Q1 — the registration engine.** Answered **no** to "5.1 because nothing is elevated": the owner's standing rule holds — the
  shared resolver (`src_vs_code/src/powershell/`), PowerShell 7 by its verified path, else 5.1, with the absolute
  ScheduledTasks import (F1). §3.4 says so.
- **Q2 — the cadence key.** Keep: `mcpWatchdog.periodMinutes` stays machine-only; the new `windowsTasks.*` keys are user-settable.
- **Q3 — one Task Scheduler read** for the folder under `wslCare.timeouts.windowsTimeGuardQuerySeconds` (its description widened,
  no rename). Yes.
- **Q4 — the acceptance gate.** Yes: the owner's first *Install* is the acceptance gate, a POST_DEPLOY item (WR-X1 with WR-E3).
- **Q5 — dry archive.** Yes: while `dryRun` is on, the archive task previews only, as the watch samples only.
- **Q6 — the Logs page's Windows side waits for A22.** Yes.
- **Q7 — the staging folder** is `%LOCALAPPDATA%\wsl-care\bin\<digest>\`. Yes.

## 8. Build order

1. WR-D1 (render, keys, Peek, golden) — the contract every later step reads.
2. WR-D2 (stage), WR-D3 (supervisor) — independent of each other.
3. WR-D4 (the Windows watch), WR-D5 (the archive body).
4. WR-E1 (client + fake), WR-E2 (reader + settings + lines).
5. WR-E3 (install/remove), WR-E4 (run now) — the first release with buttons; POST_DEPLOY item with E3.
6. WR-E5 (Archive page's Windows side).
7. S7b.3 (A22) plugs into D4's `--timer` branch; then WR-E6.
7a. WR-T1 (A23) after D1 and D4; WR-T2 after T1 and D3; WR-T3 after T2, E3 and E4.
8. Docs land in each PR; the boundary rows with D1 and E3; promote this plan when E5 ships (E6 stays an open tail named in the status line).

## 9. Definition of Done

- [ ] Every RED test seen red for the right reason, then green; every break-it red on product code, then restored.
- [ ] The child + grandchild timeout test green on the Windows leg; no test registers, runs or deletes a real task or starts a real `wsl-care.exe`.
- [ ] `contracts/config-keys.json`, `contracts/exit-codes.json`, the render golden regenerated; `plan-lifecycle`, lint, typecheck, the daemon's Windows suites and CI's two legs green.
- [ ] The owner's acceptance (POST_DEPLOY): *Install the Windows watch task* registers `\wsl-care\watch` unelevated; Task Scheduler's first run exits 0 and the ledger advances; *Remove* leaves no task, no folder (when empty) and no staging folder; a blocked start, if any, is shown with Windows' own code.
- [ ] The Windows `%TEMP%` cleanup: its preview read by the owner before the dry window ends (the not-allow-listed groups named), one real run recorded with counts and logical bytes; nothing under `%TEMP%\claude\` touched.
- [ ] One dry week on Windows read through `tasks render`'s dry state and the run logs, recorded in `research/` before `dryRun` is turned off there.
- [ ] Docs updated; the boundary table written into the four plans; the own Fable reviewer of D2, D3, D4, D5, E3 and E4 finds no way to end a process outside the job or move a file outside the archive's rules.

## 10. Open risks

- **Unelevated registration in `\wsl-care` is unmeasured** (the ACL allows it; the folder was made elevated). If `Register-ScheduledTask` refuses, the fallback is the root folder's own `\wsl-care-user\` folder — a one-line change in the render and the scripts, said in the modal.
- **Smart App Control / Defender** may refuse or delay the staged copy's first start (today a copy ran). The task's `LastTaskResult` and Node's message are shown raw.
- **Nested jobs**: Task Scheduler and VS Code may run their children inside a job; nested jobs work since Windows 8, and the supervisor fails closed otherwise.
- **`IgnoreNew` hides a stuck run**: a worker past its deadline is ended by the job; a supervisor itself stuck is ended by `ExecutionTimeLimit`.
- **The interactive-token task does not run logged out** — by design (§3.4); said in the modal.
- **The SID in the XML** ties the task to this account; a renamed or migrated profile needs *Install* again (the panel reads "install again").
- **The unelevated registration under pwsh 7 from the Store** is unmeasured (the elevated one worked, 2026-10-11); 5.1 is the resolver's own fallback when pwsh 7 is absent, not when it fails — a refusal is shown with its text.

## 11. Plan round (coai session `8138f140`, 2026-10-11)

**proceed**, three findings (one reviewer), all accepted and written in above:

- **The ledger's concurrent writers** (§3.2, §3.9). Two writers that both read before either writes would each replace the
  file and drop the other's sample; the atomic write does not serialise them. Verified in the code: `McpCpuLedger.Record` takes no
  lock (`McpCpuLedger.cs:243–251`). Now a ledger lock, never waited on; a held lock records nothing and says so.
- **The staging bound** (§3.10). "Current + one older" was wrong when upgrades come faster than a run ends. The bound is now time-
  based (an old folder outlives its switch by at most one job deadline), with a sweep at every `tasks run` start.
- **`tasks stage --remove`** (§3.2, WR-D2) was used by the *Remove* flow but not defined; now defined, with two RED tests.

## 12. Added 2026-10-11 — the Windows `%TEMP%` cleanup (the owner's decision)

The owner asked for a `%TEMP%` cleanup run by this runner: unelevated, a preview and the dry window first, a panel button, the
age as a setting, only the user's own entries older than the limit, never one in use, never through a reparse point, counts and
bytes in the run detail, a Fable own reviewer. The default selection was decided with the coai consultant (`f2317bca`, closed
solved): an ALLOW-list for real deletion (`coai-*`, `wsl-care-test-*`), the broad age view in the preview only, the newest
descendant's time as the age, budgets with a cursor, and "unknown = retained". The consultant's claim that a held file can be
deleted without a sharing violation was verified on this machine before it changed the design (§3.12). Stories WR-T1–T3.

## 13. Plan round on the `%TEMP%` addition (coai session `de0d1828`, 2026-10-11)

**proceed**, two findings (one reviewer), both accepted and written into §3.12 and §6:

- **The cursor never wrapped**: an entry created before the cursor could be skipped forever. Now an ordinal order that wraps to the
  first name within the same run, a pass start in the cursor file, and a RED test that inserts an entry before the cursor.
- **No recovery after a kill mid-delete.** Now an `inProgress` checkpoint written before the first delete inside a candidate,
  taken first by the next run and judged by its remaining FILES' times — deleting children rewrites a folder's own time, so folder
  times would make a half-deleted tree look fresh forever — and the run reads "interrupted" through the engine's reconcile.

## 14. As built

**WR-D1 as built (2026-10-11).** `Cli/Commands/TasksCommand.cs`, `Core/Scheduler/` (`WindowsTask`, `WindowsTaskDefinitions`,
`StageRecord`, `StagedCopies`, `TasksRender`), `DryRunWindow.Peek`, `Config/ConfigKeys.WindowsTasks.cs`,
`contracts/golden/windows-tasks-render.json`; the docs in [module_daemon.md](../research/module_daemon.md) § *The Windows
runner's render* and [module_tests.md](../research/module_tests.md); the boundary rows of §3.11 written into the four plans.
Deviations and decisions the plan did not spell out:

- **The summary's logon line carries `interval=`** (`trigger=logon enabled=True delay=PT60S interval=PT5M`): one field more than
  the guard's `SUMMARY_FUNCTION` prints, because the cadence is the number a stale registration most needs to show. **WR-E2
  widens `SUMMARY_FUNCTION`'s logon case** to print `$t.Repetition.Interval` (the guard's logon line then ends `interval=`).
- **The logon trigger names the user's SID** (`<UserId>` inside `LogonTrigger`): an unelevated account may register a logon trigger
  for itself only. A delay of 0 writes no `Delay` element (the guard's rule).
- **The usage is spelt `tasks render <task> [--json]`**: a `|` in the usage would split the flow catalogue's table cell.
- **`registrable` also needs the SID**: a render without one is answered (exit 0) and says it is not registrable. A configuration
  layer in error refuses the render with exit 78 (the `units dropin` rule).
- **`stage.json`'s `version` is the release number** (the version text before `+`, `StagedCopies.ReleaseOf`), so the extension can
  compare it with `BUNDLED_DAEMON`; the render checks it equals this build's. The staged bytes are read through the archive's
  no-link open (`IArchiveFiles.OpenSource`).
- **The golden is generated by `Scenarios/ContractFilesTests`** — the repository's one contract generator
  (`WSL_CARE_WRITE_GOLDENS=1`) — from the pure parts, on every OS, not by a new mechanism in `Core.Tests`.
- **No new `NumberRule`**: the period rule exists (`NumberRules.cs:90–92`); its named test was added and passed at once (held).
- **The coai plan round on WR-D1** (session `c7cd2516`): proceed, three findings, all accepted. Two were already covered by the
  story's tests (a copy whose bytes do not hash to its folder, a `stage.json` that disagrees or is malformed — `StagedCopiesTests`;
  a path with `&` and `<` escaped and read back — `A_path_with_xml_characters_is_escaped_in_the_xml_and_plain_in_the_summary`);
  the third added the real command line for the archive task (`A_verified_staged_copy_renders_the_archive_task_with_its_own_numbers`).
  The fixtures' profile folder is `C:\Users\user` (the repository's privacy rule).
