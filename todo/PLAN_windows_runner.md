# PLAN — the Windows runner: two per-user scheduled tasks run the staged `wsl-care.exe` under one bounded supervisor

> Status: **in progress, 2026-10-21 — WR-D1 built (`tasks render`, the `windowsTasks.*` keys, `DryRunWindow.Peek`, the render golden; *WR-D1 as built* in §14); WR-D2 built (`tasks stage`, `tasks stage --remove`, the sweep `Staging.Sweep`, wired into the start of `tasks run`; *WR-D2 as built* in §14); WR-D3 built (`tasks run`, the Job Object supervisor, exit 84 `TaskEnded`, the job scan; *WR-D3 as built* in §14); WR-D4 built (the Windows `watch`: elevated refused with 81, the run lock, the ledger under the ledger lock `mcp-cpu.lock`, `RunRetention` swept, the A22 hook — nothing acts, no history line; *WR-D4 as built* in §14); WR-D5 built (the archive body of `tasks run archive`: dry → `archive preview`, real → `archive run`, no run lock; *WR-D5 as built* in §14); WR-E1 built (the widened `WindowsCareClient`: the staged exe, `tasks render` / `stage` / `stage --remove` typed, the run reads; the strict fake learns them; *WR-E1 as built* in §14); WR-T1 built (A23 `windowsTemp`: the engine's first Windows action, the `temp.*` keys, `act` unelevated on the Windows side; *WR-T1 as built* and *the Fable own review* in §14); WR-E2 built (one Task Scheduler read for `\wsl-care`, the guard consuming it; the Windows tasks' state, host and panel lines, read only; the runner's tree kill; *WR-E2 as built* and *the story's plan round (4fc3f328)* in §14); WR-E3 built (the install / remove flows of the two Windows tasks: ONE unelevated PowerShell, the render checked before it is shown, the pending record cleared on every answer but a timeout, the peer `scheduler/registerScript.ts` the one home of the registration; *WR-E3 as built* and *the story's plan round (efc512d4)* in §14; the live gate waits for the owner's press); WR-E4 built (the run-now buttons: the task's own `Run` asked of Task Scheduler by ONE unelevated PowerShell after a read of the task's row, an overlap told and never silent, the same pending record holding all five buttons; *WR-E4 as built* and *the story's plan round (1c023671)* in §14; the live press is the owner's); WR-T2 built (the cleanup task on the daemon side: `tasks render cleanup`, `tasks run cleanup` = the supervisor's third closed-table entry `act A23 --timer --confirm --json`, `tasks preview cleanup`, the `windowsTasks.cleanup*` keys, the principal guards for all three tasks; *WR-T2 as built* in §14); WR-E5 onward (except WR-T1, WR-T2) not built.** Scope: the `wsl-care` daemon (`src_daemon/`: a `tasks` verb family — `render`, `stage`, `run` — a Windows branch of `watch`, one new exit code, new `windowsTasks.*` keys) and the VS Code extension (`src_vs_code/`: a `windowsTasks/` module, the widened `WindowsCareClient`, five panel buttons, settings, the Archive page's Windows side), and the Windows `%TEMP%` cleanup (W-A2, §3.12, added 2026-10-11). Nothing elevated, nothing as SYSTEM.
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
- new for the `%TEMP%` cleanup (§3.12): `temp.olderThanDays` (7, 1–365, Higher), `temp.allowGlobs` (a list, default `["coai-*", "wsl-care-test-*", "stage-*", "mindex-*", "git-commit-*", "docrag-*", "bge-seed-*"]` — the owner's decision of 2026-10-12, from the patterns measured in research/2026-10-11_windows_temp.md; a glob may not contain a path separator and may not match `claude`), `temp.scanMaxNodes` (200000, 1000–10000000, Lower), `temp.scanMaxSeconds` (120, 10–3600, Lower), `temp.maxItemsPerRun` (5000, 1–1000000, Lower), `windowsTasks.cleanupLogonDelayMinutes` (30, 0–720, Higher), `windowsTasks.cleanupEveryHours` (24, 1–168, Higher).

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
| `%LOCALAPPDATA%\wsl-care\bin\<digest>\` | 13.7 MB × (2 + the versions still executing) while the tasks run: the bound rests on runs happening (WR-D2 as built, §14). A stage keeps every VERIFIED copy (it cannot know which one the tasks name); the `tasks run` start sweep, run from the registered copy, keeps its own and the newest verified one and retires the rest, and a folder kept because its exe executes goes once that run ends (every run ends by its job deadline). With the tasks never run, every staged version stays until *Remove*. The panel names every kept and retained folder | `tasks stage`'s sweep (unverified folders only); the `tasks run` start sweep (the older verified copies); *Remove* (all) | a half-copied folder has no `stage.json` → swept as unverified; a same-digest one is replaced by the next stage |
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
| the Windows engine path and the `%TEMP%` walk (`claude` guard, owner SID, reparse refusal, re-validated delete) | here, WR-T1 builds it first (A23) | [PLAN_disk_hygiene_windows.md](PLAN_disk_hygiene_windows.md) (2026-10-14) REUSES it for A24/A24Orphans (worktree build output) and A26 (`testsToDelete-*` in `%TEMP%`, 1 h); if its DH-4 is ready first the shared core is extracted here, never copied |
| the Windows `act` path: the per-side privilege rule (`RunsOnWindows` → `Elevated`, exit 81, `ActionContext.UserSid`) and the tree delete walk | WR-T1 builds both FIRST (A23; the walk is path-based, `TempDeletion`) | [PLAN_disk_hygiene_windows_E20.md](PLAN_disk_hygiene_windows_E20.md) E20.S1 (DH-4) consumes the rule unchanged and EXTRACTS the walk into the handle-based `ITreeRemoval` seam, moving `TempDeletion` onto it in its own PR (A23 keeps "a link stops the candidate" as an option; admission stays per action) — its §4b, 2026-10-15; never two copies |
| `testsToDelete-*` names in `%TEMP%` | A23 never selects them (`temp.allowGlobs` may not match the prefix); its preview names them "the test-leftover sweep's" | A26 owns them |
| the tasks `\wsl-care\worktrees-scan` (daily + on demand), `build-output`, `build-output-orphans`, `test-leftovers` (on demand only), each one fixed argv line, none taking the run lock | D1's definitions and D3's closed argv table grow by four lines; E3/E4 install and run them like the others; A26 shares A23's `%TEMP%` lease | PLAN_disk_hygiene_windows.md DH-5 |
| the Windows `status --json` read of the BUNDLED exe in `WindowsCareClient`, its fake answer, a `windowsStatus` outcome slot and its poll | carved OUT of WR-E1 (2026-10-15): WR-E1 keeps the staged exe, `tasks` and the run reads | [PLAN_disk_hygiene_windows_E19.md](PLAN_disk_hygiene_windows_E19.md) E19.S2b builds it first (built 2026-10-15 on `feat/wc-e19-s2b-windows-status-read`: `WindowsCareClient.status()` beside `version()`, the typing in `client/windowsStatus.ts`, `OutcomeStore.windowsStatus`, the poller's `windows` source, the fake's `status --json`); WR-E1 builds on it, never a second read |
| the Windows watch (WR-D4) and its run-retention sweep | here (WR-D4) | PLAN_disk_hygiene_windows.md waits on it three times: DH-1b (every lettered volume's free space read by the watch every 5 minutes — the owner's priority after DH-1, 2026-10-14), DH-4 and DH-7 (their run details are retired by WR-D4's retention sweep); WR-E1 also gates the extension half of DH-1b and DH-3 |

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
- **Selection for deletion — an ALLOW-list:** `temp.allowGlobs` (default `["coai-*", "wsl-care-test-*", "stage-*", "mindex-*",
  "git-commit-*", "docrag-*", "bge-seed-*"]` — the owner's decision of 2026-10-12, from the patterns measured in
  [2026-10-11_windows_temp.md](../research/2026-10-11_windows_temp.md): scratch whose disposal is known — coai's review rounds and
  tests, this repo's own tests, and the measured tool scratch folders), each candidate's NEWEST descendant write time (files AND
  folders) older than `temp.olderThanDays` (7). A tree not scanned to its end within the budget is "unknown — retained".
  Files directly in `%TEMP%` follow the same rules with their own time.
- **The preview shows MORE than it would delete:** the resolved root, the cutoff and the age rule; eligible / unknown / excluded
  / deferred counts; "deleted logical bytes" estimated (never "disk freed"); groups by name pattern with exact paths on expand —
  including the older-than groups that the allow-list does NOT cover ("would be eligible if allowed"), so the owner can widen
  `temp.allowGlobs` (`wsl-care.exe config set …` — the extension never writes daemon config) from evidence, not guesses.
- **Budgets on every run:** `temp.scanMaxNodes`, `temp.scanMaxSeconds`, `temp.maxItemsPerRun`; a cursor in
  `%LOCALAPPDATA%\wsl-care\temp-cursor.json` resumes after the last decided candidate, so a 138 k backlog drains over runs. The two
  scan budgets are spent by ONE shared allowance for the plan (the top-level listing included, as built after the coai code round,
  finding 11) and by ONE shared allowance for the run's re-validation scans (the Fable own review m6) — neither is a cap on a whole
  run: the delete walk itself has no time cap of its own, the supervisor's deadline and the cancellation end it, and the checkpoint
  makes that safe. `timer.runLimitMinutes` must at least cover the two allowances (the NumberRule of WR-T2): the MINIMUM needed to
  delete anything, not a guarantee that a run finishes.
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
- **Dry first:** `dryRun` and `timer.firstDryWindowDays` on the Windows paths (§3.5) — while dry the run previews only. A23 keeps its
  OWN first-week stamp (`first-timer-run-A23.json`): the common stamp is started by the Windows watch and the archive body, so sharing
  it would open deletion on the first A23 run (as built, the Fable own review M3). A23 keeps its
  OWN first-week stamp (`first-timer-run-A23.json`): the common stamp is started by the Windows watch and the archive body, so sharing
  it would open deletion on the first A23 run (as built, the Fable own review M3).

**On the runner:** a third task, `\wsl-care\cleanup` (`LogonTrigger` + `windowsTasks.cleanupLogonDelayMinutes` (30),
repetition `PT<windowsTasks.cleanupEveryHours>H` (24), deadline `timer.runLimitMinutes`), runs `tasks run cleanup` under the
same supervisor; the body is `act A23` with the timer trigger (the supervisor spells it, never the extension). Its own lock is
the run lock, taken without waiting (a cleanup never runs beside a watch; a busy lock is "try at the next run"). As built (WR-T2, §14): the body is `act A23 --timer --confirm --json`, a busy run is exit 75 as the watch's and the archive's are, and the dry week is A23's own.

**In the panel:** *Preview the Windows TEMP cleanup* (read-only, through `WindowsCareClient` on the staged exe as `tasks preview
cleanup` — the extension spells neither `act` nor `--confirm`; its own timeout `wslCare.timeouts.windowsTempPreviewSeconds`),
*Install the Windows cleanup task*, *Clean Windows TEMP now* (the task's own `Run`, as §3.8's run-now buttons), and the line
"last cleanup: <t>, N deleted, M kept in use, <bytes> logical" from the run records. As built (WR-T2): `tasks preview cleanup` answers with the exact paths on expand, bounded by `preview.maxItems` per group.

## 4. Never

- Never run the bundled or staged exe elevated, as SYSTEM, from a task with a non-interactive logon, or from a path outside the user's profile; never register the extension folder's path.
- Never resume a worker that is not inside the job; never let the job handle be inherited; never start free argv through the supervisor.
- Never stop a process by image name — pid + creation time only (A22's rule; the supervisor terminates only the job it created).
- Never take the run lock in the archive body; never hold it across a wait.
- Never write daemon config from the extension; never spell `--timer` or `act` in the extension's shipped source, and `config` only in the one sanctioned writer, `config/configCall.ts` (E10.S1).
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
| WR-E1 | `WindowsCareClient` widened (NOT the bundled `status` read: E19.S2b built it); the fake learns the staged exe and `tasks`; reads | `src/client/WindowsCareClient.ts`, `verbs.ts`, `test/fake/fakeWsl.ts`, `test/structure.test.ts` | client.test, fakeWsl.test, structure | no | D1–D2 (answers) |
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

- [ ] Every RED test seen red for the right reason, then green; every break-it red on product code, then restored. (WR-E3's part is done 2026-10-18 — see *WR-E3 as built*; the box is the plan's, so it stays open until the last story.)
- [ ] The child + grandchild timeout test green on the Windows leg; no test registers, runs or deletes a real task or starts a real `wsl-care.exe`.
- [ ] `contracts/config-keys.json`, `contracts/exit-codes.json`, the render golden regenerated; `plan-lifecycle`, lint, typecheck, the daemon's Windows suites and CI's two legs green.
- [ ] WR-E3/E4 live gate (the Opus own review of WR-E2, m6; WR-E3's test half is built, the live press is the owner's — both tasks, finding 4 of the plan round efc512d4): the REAL registered `\wsl-care\watch` AND `\wsl-care\archive`, each read back through `QUERY_SCRIPT`, satisfies `summariesMatch(registered, tasks render's summary)` — the panel says "installed, …", never "install again to update" — before WR-E3 is called done; WR-E4 repeats it after a run-now (`state`, `lastResult`).
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
- **"Elevated" is the token's elevation flag** (`Environment.IsPrivilegedProcess`). With UAC off (`EnableLUA=0`) or on the
  built-in Administrator account every process reports elevated, so the Windows `watch` exits 81 every period and the task never
  samples — a known limitation shared with the archive verbs (WR-D4 own review m3). WR-E3's modal must say it. **Since WR-T2's own
  review the same holds for all three tasks:** `tasks render` is never registrable from an elevated process, and `tasks run` (any task)
  and `tasks preview cleanup` answer 81 from one — so with UAC off the `render` is never registrable for that account (nothing can be
  installed), and **WR-T3's modal and the panel's cleanup row must say so** (a machine where every process is elevated cannot install,
  preview or run the cleanup task; "run it from an account that is not an administrator, or turn UAC on").
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
solved): an ALLOW-list for real deletion (first `coai-*`, `wsl-care-test-*`; widened by the owner's decision of 2026-10-12, from the
patterns measured in [2026-10-11_windows_temp.md](../research/2026-10-11_windows_temp.md), to `coai-*`, `wsl-care-test-*`, `stage-*`,
`mindex-*`, `git-commit-*`, `docrag-*`, `bge-seed-*`), the broad age view in the preview only, the newest
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
- **The coai code round on WR-D1** (same session): proceed, two findings, both accepted. The flow now runs over the BUILT CLI in
  the product harness (`Scenarios/WindowsTasksRenderFlows`: both tasks answered and not registrable from the build folder, nothing
  written; the refusal on the Linux legs; break-it: a `Peek` that starts the week turns both red). The week's boundary rule is one
  function, `DryRunWindow.InWeek`, used by `Decide` and `Peek` alike, so the shown dry state cannot drift from the next run's.

**WR-D3 as built (2026-10-12).** `Cli/Commands/TasksCommand.Run.cs` (`TasksCommand` became `partial`), `Core/Processes/`
(`IWorkerSupervisor.cs` — `IWorkerSupervisor`, `WorkerLaunch`, `WorkerOutcome`, `IJobControl`, `RefusingWorkerSupervisor`;
`JobSupervisor.cs`; `JobSupervisor.Win32.cs` — `Win32JobControl`; `WindowsCommandLine.cs`), `Core/Scheduler/TaskWorkers.cs`,
`CliHost.Supervisor`, exit `84 TaskEnded` (`contracts/exit-codes.json` regenerated; the extension's `DAEMON_EXIT.taskEnded` and its
root kind `taskEnded`); the docs in [module_daemon.md](../research/module_daemon.md) § *The Windows runner's supervisor* and
[module_tests.md](../research/module_tests.md). Deviations and decisions the plan did not spell out:

- **Two files, one supervisor.** The rules (the order of the calls, the deadline loop) are `JobSupervisor` over an `IJobControl`
  seam, so a fake reads the order; the Win32 calls are `Win32JobControl` in `JobSupervisor.Win32.cs`, and THAT file is the job
  scan's only allowed site (`AssemblyMetadata WslCare.JobSeam`). The scan also covers `CreateProcess*` (one name more than §3.3
  lists): a process created through Win32 outside the child runner would otherwise slip past the process-start scan.
- **`archive` is refused as not built** (exit 2, `TaskWorkers.ArchiveNotBuilt`, before any job or process exists) — *superseded by WR-D5:
  the archive body is in the closed table now.* Its deadline key (`timer.runLimitMinutes`) is already `TaskWorkers.DeadlineKey`.
- **`--json` passes through**: the table gives `watch --timer`, plus `--json` when the supervisor got it (the task's action always
  passes it) — still a closed table of two shapes; a person running `tasks run watch` by hand reads text.
- **The other exits**: a supervisor that could not start the worker inside a job (no job, no process, a standard handle that could
  not be passed, a failed assign or resume, an unreadable unbiased clock) or could not read its exit code exits **1** with the reason as its one stderr line — no new code beyond 84; a signal exits **130**
  after the job is closed; a host whose own path is not absolute starts nothing (2). A configuration in error is NOT refused by
  `tasks run` (the worker reads the same layers and answers for itself).
- **The cancellation stop is the close**, not `TerminateJobObject`: Ctrl+C / a console close returns the wait and the job's handle is
  closed, so kill-on-close ends the tree — which is what makes kill-on-close testable (the cancellation test turns red without it).
  Task Scheduler's *End* terminates the supervisor itself; the OS closes the handle and kill-on-close does the same.
- **After the deadline** the worker is waited for within `commands.drainGraceMilliseconds` (the child runner's existing grace,
  reused — no new key); a mid-run clock that stops answering counts as past the deadline (fail closed). The standard handles are
  inherited as inheritable DUPLICATES (closed right after `CreateProcessW`); a missing one is not passed, with none at all nothing
  is inherited, and one that exists but cannot be copied refuses the start (n1 below). The worker starts by `lpApplicationName` = its absolute path (no search).
- **The WR-D2 seam**: `TasksCommand.SweepStagingAtRunStart` is called before the worker starts; WR-D3 left it empty, and WR-D2
  fills it with ITS staging sweep (§3.10) — no second sweep was written. Wired when WR-D2 rebased onto WR-D3 (2026-10-12):
  `Staging.Sweep` with the digest the run's own copy is staged under (`StagedCopies.DigestOf`, read from the path, nothing hashed),
  before the worker takes the run lock; a retirement or a failure is one stderr line, and a failure never stops the run
  (`TasksRunCommandTests.The_run_start_sweep_retires_an_older_verified_copy_before_the_worker_starts_and_keeps_its_own_and_the_newest`,
  red before the wiring; break-it: the call removed → red).
- **The Windows test leg's helper tree** (`TestSupport/JobHelperTree.cs`, shared by Core.Tests and Cli.Tests) is Windows PowerShell
  (`%SystemRoot%\System32\WindowsPowerShell\v1.0\powershell.exe`, the worker) starting `ping.exe` (the grandchild): the precedent
  `TestSupport/HoldingChild` uses PowerShell; the FakeTool needs a scenario script. The helper writes both pids AND both creation
  times (`StartTime.ToFileTimeUtc()`); a pid is held only when the creation time read through the handle just opened equals the
  recorded one — a gone or reused pid is never held and never ended, and the test fails naming it. The wait for the helper's line
  (20 s) is shorter than the run's deadline (30 s), so both are read while the job still holds them. A failed test ends what is left
  through the held handles only.

**The Fable own reviewer's round on WR-D3 (2026-10-12)**: no blocking issue, no product path ends a process it did not start; every
item fixed in the same story, RED first:

- **M1** — the test could hold, and end, a reused pid: the M1 rules above.
- **m1** — `Win32JobControl.Resume` accepts ONLY a previous suspend count of exactly 1; anything else answers "the worker was not in
  its suspended start", and the supervisor ends the job and refuses (exit 1). A real test proves `CREATE_SUSPENDED`: a suspended
  `cmd /c exit 7` in its own job has not exited after 2 s, and ended while suspended it exits `0xC5A1`.
- **m2** — `EndSuspended` answers `TerminateProcess`'s error; the refusal then says ending it failed and that it stays suspended,
  never "ended".
- **m3** — the job scan also catches `(Nt|Zw)?Terminate(JobObject|Process|Thread)`, as a call and as a quoted entry point.
- **n1** — a standard handle that exists but cannot be duplicated refuses the start (exit 1, one stderr line) and closes the copies
  already made (`Win32JobControl.StandardCopies`); a handle this process does not have is still simply not passed.
- **n2** — an exit code `GetExitCodeProcess` cannot read is a refusal (exit 1) naming the error, never an invented `Exited(-1)`.
- **n3** — a failed wait (`WAIT_FAILED`) throws from `WaitHandle.WaitAny`; the job's `using` closes the job on the way out and the
  program's last frame answers **70 `Internal`** — fail closed.
- **n4** — **WR-D4 must change `Scenarios/WindowsTasksRunFlows.cs` in the same PR**: today that flow relies on the Windows `watch`
  being refused; once D4 lets it run, the flow would start a real `watch --timer` of the built binary.
- **n5** — the `PROC_THREAD_ATTRIBUTE_HANDLE_LIST` and `lpApplicationName` (no search) are defence in depth with no test of their
  own (the job handle's non-inheritance has one; the list's contents and the no-search start are read in the code only).
- **n7** — exit 1's description in `ExitCode.cs` names `tasks run`'s "no worker could start inside a job".
- **The coai plan round on WR-D3** (session `ce1d140d`, item 10): the shutdown path for real —
  `Cli.Tests/TasksRunCommandTests.A_stopped_run_exits_130_and_its_whole_tree_is_gone` runs the whole program over the real
  supervisor with the helper tree in place of the worker, cancels the run's token (what `ShutdownSignals` fires), and asserts exit
  130 and both helpers gone.

**The coai code round on WR-D3** (session `ce1d140d`): proceed, three findings, all accepted and fixed, RED first:

- **Major — no window outside the job; a DEVIATION from §3.3 point 3** ("`AssignProcessToJobObject` after `CreateProcessW`"). With
  assign-after-create, a supervisor ended (Task Scheduler's *End*, a crash) between the suspended start and a successful assign left
  a suspended worker OUTSIDE the kill-on-close job until reboot. Now the worker is CREATED INSIDE the job:
  `PROC_THREAD_ATTRIBUTE_JOB_LIST` (`0x2000D`, Windows 10+) in the same attribute list as the handle list (two attributes; one when
  there is no standard handle to pass), its value the job's handle — which stays NON-inheritable (the list needs no inheritance; it
  is add-ref'd while its raw value sits in the list). A `CreateProcessW` Windows refuses (a parent job that forbids nesting) started
  nothing → refused, exit 1. Before the resume `IsProcessInJob` verifies the membership; a worker not in the job is ended through the
  job and then by its own handle while still suspended, and refused. The previous-suspend-count-of-1 rule stays. **The assign step is
  gone** (not kept as a check: `IsProcessInJob` is the read-only check); `AssignProcessToJobObject` stays in the job scan's list, so
  no file may call it, the seam included in effect (it no longer declares it). `IJobControl.Assign` became `InJob`, and
  `StartSuspended` takes the job.
- **Minor — one entry per task**: `TaskWorkers`' closed table holds the task, its arguments and its deadline KEY in ONE entry; the
  launch carries the duration and `WorkerLaunch.DeadlineKey` from it, and the timeout line names `launch.DeadlineKey` — it can no
  longer name another key.
- **Minor — two stderr lines per run**: `tasks run <task>: starting <argv> in a kill-on-close job, deadline N min (<key>)` before the
  worker starts, and ONE terminal line: `the worker exited N`, the deadline line, `refused: <why>`, or `stopped by a signal` (then the
  program's `interrupted.`). So "one stderr line on timeout" (§3.3 point 5) is now the terminal line of two.

**The second coai code round on WR-D3** (session `ce1d140d`): proceed, four findings, all accepted and fixed:

- **Blocking — the cross-module record**: `research/architecture.md` § *Exit codes* now names 84 `TaskEnded` and how the extension
  reads it (`contracts/exit-codes.json` → `DAEMON_EXIT.taskEnded` → the root kind `taskEnded`; no systemd unit reaches it).
- **Major — the public contract owns its data**: `WorkerLaunch.Arguments` is `{ get; init; } = []` and the constructor copies its
  input (`[.. arguments]`), so no caller's list can change what is started; RED first (`WorkerLaunchTests`).
- **Minor — the clock seam**: `JobSupervisor` takes `IUnbiasedClock` (`Core/Processes/IUnbiasedClock.cs`), not the MCP ledger's
  `IWindowsBoot`; `IWindowsBoot` extends it, so `Win32Boot` serves both.
- **Minor — a loop**: the recursive timeout re-check is a loop; behaviour unchanged, the unbiased-time tests green (and red when the
  loop stops re-reading the clock).

**WR-D2 as built (2026-10-12).** `Core/Scheduler/Staging.cs` (the stage) and `Staging.Sweep.cs` (the sweep and the removal),
`TasksCommand.Stage`, the `tasks stage [--remove] [--json]` row of `CommandLine.cs`; the docs in
[module_daemon.md](../research/module_daemon.md) § *The Windows runner's staged copy* and [module_tests.md](../research/module_tests.md).
Deviations and decisions the plan did not spell out:

- **No new file code.** The copy goes through the archive's seam only (`IArchiveFiles.OpenSource` for the source, twice: once to
  hash it for the folder's name, once to copy; `OpenFolderBeneath` holding the digest folder; `CreateExclusive`; `FlushFolder`;
  `ReadBack`; `RemoveOwnCopy` and `RemoveEmptyFolder` to undo a failed copy); `stage.json` through `IFileSystem.WriteFileAtomically`;
  the sweep through `IFileSystem.DeleteFile` / `DeleteDirectory`; the root through `RemoveEmptyFolder`. Every act is judged by the
  deletion policy, action `tasks-stage`, inside the staging root (the root itself inside the state directory).
- **What is at the digest name decides** (the Fable own review of `58ecb008`, M1): the staging root's listing, by ORDINAL name — a
  real FOLDER is judged as an existing copy; a LINK (a junction) is refused, never followed and never replaced (before this, a
  junction to a sibling `B` led the replace to delete `B`'s exe and the sweep then to remove `B`, the registered copy); a FILE is
  refused as in the way; the same name in another case is refused (NTFS folds case, the sweep does not); a free name is staged.
- **An existing same-digest FOLDER** (the coai plan round on the review fix, session `01e5e611`): it VERIFIES → reused, success,
  nothing written; it does NOT verify (a half copy, a missing or disagreeing `stage.json`, bytes that do not hash to the name) and
  its exe is not in use → removed whole through the deletion policy (the sweep's own exe-first removal) and staged FRESH, so a retry
  after an interrupted stage recovers by itself; it does not verify and its exe is in use → refused and named. Nothing is ever
  written into an existing folder. (This replaces the first build's "refused until *Remove*".)
- **The exclusive create is the second guard**: a name taken between the classification and the create is judged as an existing
  folder, reused only when it verifies and never replaced (no second removal, so no loop). Since the replace, removing the outer
  check ALONE does go red (the three retry cases and `…never_overwrites`: the folder is then only ever "taken meanwhile").
- **A refused stage sweeps nothing**: the older folder may be the one the registered tasks still run.
- **The keep rule** (the review of WR-D2; the coordinator's option (1) on the own review's M2). Only a VERIFIED folder is ever
  protected (`StagedCopies.Verify`: the render's checks without the release — an older copy is another release); an unverified
  folder never is, even one named with the running digest. `--remove` keeps nothing.
  - **`tasks stage` keeps EVERY verified folder and sweeps the unverified ones only.** The first fix kept "the new digest and the
    newest OTHER verified one", which still swept the registered folder after ONE declined registration (WR-E3 stages before the
    modal: V1 registered, V2 staged and declined, V3 staged → V1 gone) or one future-skewed `stagedAtUtc`. A stage does not know
    which copy the tasks name; keeping every verified one is the only safe answer there. *Deviation from §3.2*, which says the stage
    "sweeps older `<digest>` folders": it sweeps only unverified ones.
  - **The `tasks run` start sweep retires the older verified copies** (`Staging.Sweep(StagingAccess, ownDigest)`, public; WR-D3's
    `TasksCommand.SweepStagingAtRunStart(...)` calls it with the running digest): it runs FROM the registered copy, so it keeps
    {own, the newest verified} — from the new copy it sweeps the old folder unless it is executing; from an old copy (its
    re-registration failed) it keeps the new one, so nothing is lost and the next install finds it.
  - **The newest is chosen with a clamp**: a `stagedAtUtc` after now counts as now and loses a tie to a genuine stamp, so a skewed
    clock never makes an old copy "the newest" over the copy staged now. (A skewed stamp still beats genuine stamps EARLIER than
    now; at a run start that only keeps one extra folder, because the running, registered copy is kept as its own.)
  - The answers name each folder kept by the rule in `retained` (besides the sweeper's own, which `path` already names).
  - **§3.10's bound now rests on runs happening**: a stage keeps every verified copy, so old folders are retired only by a `tasks run`
    start from the newest copy (or by *Remove*). With the tasks registered and running, at most two verified folders stay after a
    run-start sweep (its own and the newest) plus any whose exe is still executing; with the tasks never run, every staged version
    stays (13.7 MB each) until *Remove*.
- **The run lock** (the own review's m4): `tasks stage`, `--remove` and the run-start sweep take THE run lock
  (`WindowsHostPaths.RunLockFile`, `StagingPlace.RunLockFile`) without waiting; a held lock answers `busy` and does nothing (the CLI
  exits 75, `ExitCode.Busy`, its doc widened). So two stages of one digest are serialised, and a stage never runs beside a sweep. A
  stage pressed while the watch task samples is therefore busy for that moment: the extension (WR-E3) retries or says "try again".
- **A folder goes exe first**: the exe's delete fails while it runs, so a kept folder stays WHOLE (its `stage.json` too) and the
  copy a running task still uses stays verifiable; only then the rest of the folder. A sharing violation, or access denied
  on the exe (a running image), is answered "in use"; any other failure "could not be removed (…)" (m1). What a swept folder was
  comes from the same `StagedCopies.Verify` the keep rule uses, so a folder whose bytes disagree with its `stage.json` is
  "unverified: …", never "an older copy" (m6); `StagedCheck` carries `StagedAtUtc`, so each `stage.json` is read once.
- **What the sweep touches**: only a FOLDER entry whose name is 64 lower-case hex digits; a link (never followed), a file, an
  upper-case or short name is listed as `untouched` and left. The removal removes the root only when nothing is kept or untouched.
- **Exit codes**: 0 staged, reused and every removal (a kept folder is named, not an error); 1 (`RunFailed`, its doc widened — no new
  code, so `contracts/exit-codes.json` is unchanged) when no verified copy was staged; 75 (`Busy`) when the run lock is held; 2 on
  the distro's binary. A configuration in
  error does not refuse the stage (nothing is rendered from it, and *Remove* must still work).
- **No elevated refusal**: the plan does not ask one for the stage. An elevated stage writes the same user's `%LOCALAPPDATA%`, and
  a copy owned by Administrators is refused by the render's no-link open (not registrable), so nothing is registered from it.
- **The answers**: `StageReport {schemaVersion, outcome (staged, reused, refused, busy), reason, digest, path, version, source,
  swept[], kept[], retained[]}` and `StageRemoveReport {schemaVersion, outcome (removed, busy), reason, root, rootRemoved, removed[],
  kept[], untouched[]}`, each folder `{digest, path, why}`; "newest
  last" is the folders' last write time, oldest first.
- **The built-CLI flow cannot run `tasks render` from the staged copy**: the build's `wsl-care.exe` is a framework-dependent apphost
  (a copy alone fails: "The application to execute does not exist: …\wsl-care.dll", exit 154). The flow tests what the built CLI
  can: the stage, the reuse, the sweep, the removal with a held copy (`Scenarios/WindowsTasksStageFlows`). The shipped Native AOT
  exe is one file.
- **The coai code round on WR-D2** (session `01e5e611`): three findings accepted, one rejected.
  - *Finding 1 (major), the results of the undo steps*: the folder flush after the copy is now checked — a failed flush refuses the
    stage (a name that may not survive a crash is never recorded), its own copy removed, no `stage.json`; and the removal of the
    stage's own copy after any failure is reported as it went — "the copy was removed", "the copy is gone", or "the copy could NOT be
    removed (…): it stays at <exe> with no stage.json, and the next stage replaces it" — never "removed" when it was not.
  - *Finding 2 (major), hashing in the run-start sweep* (~27 MB read every 5 minutes otherwise): the run-start sweep keeps its OWN
    folder when its `stage.json` agrees, UNHASHED (the render verified that copy at registration); it walks the other folders with an
    agreeing `stage.json` newest first (the clamped `stagedAtUtc`) and hashes only until one verifies — that one is kept; every other
    folder is retired without hashing (its description comes from its `stage.json`: "an older copy by its stage.json (…; not hashed)").
    From the newest copy — the usual case — it hashes nothing. The stage-time sweep and `--remove` (rare) keep the full verification
    of every folder. The keep rule's "only VERIFIED folders are protected" holds at a run start in this lighter form for the run's
    own folder: protected only when its `stage.json` agrees (an executing image is kept anyway by the exe-first removal).
  - *Finding 3 (minor)*: `tasks run` writes one stderr line when its sweep was skipped (the run lock held) and one naming the folders it
    could not remove; nothing when the sweep did nothing (the existing "retired" line stays).
  - *Rejected*: a sweep status line on every run — noise on an unattended five-minute task; after finding 2 the common sweep is free.

**WR-D4 as built (2026-10-12).** `Core/Watch/WatchRun.Windows.cs` (`WatchRun` became `partial`; `RunAsync` dispatches on the
layout, the distro's path unchanged), `Core/Mcp/McpCpuLedgerLock.cs` (`McpCpuLedgerLock`, `LedgerLease`),
`McpCpuLedgerPlace.WindowsState.LockFile`, `WindowsMcpCollector` (the lease around read → window → write),
`RunRetention.TrySweep`, `Cli/Commands/WatchCommand.cs` (the elevated refusal, the Windows seams, the log), the `watch` help text;
`Scenarios/WindowsWatchFlows` (new) and `Scenarios/WindowsTasksRunFlows` (changed, the WR-D3 review's n4); the docs in
[module_daemon.md](../research/module_daemon.md) § *The Windows watch and the ledger lock*,
[module_mcp_servers.md](../research/module_mcp_servers.md), [module_tests.md](../research/module_tests.md), and the A22 hook's place
in [PLAN_twenty_sessions_all_day.md](PLAN_twenty_sessions_all_day.md) S7b.3. Decisions the plan did not spell out:

- **The elevated refusal is the verb's**, before the lock or any state (as the distro's root check is): exit 81 with
  `WatchCommand.ElevatedRefusal`, a sentence in `ArchiveRunCommand.ElevatedRefusal`'s words naming the watch. The sandbox's
  privilege claim (`WSL_CARE_SANDBOX_PRIVILEGED=1`) counts as elevated, so the built-binary flow proves it.
- **The ledger lock lives in `WindowsMcpCollector`**, the one road both Windows writers take (the watch and `status`), so neither
  can skip it. It is held from BEFORE the ledger read, across the CPU window, to after the write; a writer that comes meanwhile
  records nothing. Held elsewhere (or unopenable: `UnauthorizedAccessException`), the sample keeps no ledger
  (`WindowsCpuLedger.None`): `cpuBaseline.file` is then empty and `cpuBaseline.reason` names the held lock. An ELEVATED status
  writes nothing and takes no lock. The lock path is derived from the ledger path (same folder, same separator). The distro's
  ledgers take no ledger lock: root's is written under the run lock; the unprivileged distro `status` (`OwnState`) can still race
  another one — out of this story, named as open.
- **The `--timer` branch runs after the run lock is released**, as the distro's watch's act does (an engine act takes the lock).
- **Until A22, `DryRunWindow.Decide` is not reached.** The gates run `--timer` → not observe-only → `WatchRun.WindowsActing`
  (the A22 hook, EMPTY) → `Decide`. `Decide` writes the first-week stamp, so a watch with nothing to act on would start the Windows
  dry-run week before any Windows action could use it; the order keeps the week for A22's own decisions (the distro's watch also
  checks its switches before the dry decision). A test holds it (`Without_A22_…`: no `first-timer-run.json`).
- **The sweep is under the run lock** (as `collect`'s), through a new guarded `RunRetention.TrySweep`: a failing sweep is a problem
  in its report, never a lost sample. `collect` keeps its own guard (it could adopt `TrySweep`; not changed here).
- **The answer:** `watch --json` gains `retention` (`historyLinesRemoved`, `detailsRemoved`, `problems`; absent on the distro);
  `historyReason` is `WatchRun.NoAgentHistory` on Windows (the agents' CPU history is the distro's evidence) and the CLI does not
  warn about it. The busy answer names "another watch or an act". `WatchRun.NotTheDistro` is gone; an unknown layout answers
  `WatchRun.NoSide`.
- **No new key and no new exit code**: 81 and 75 exist, the cadence and the retention are existing keys.
- **The built-binary flows count the test runner itself as the MCP server** (the sandbox's user layer lists its program name in
  `mcpServers.programs`), so a reading exists on any machine; the real process table is only read, and every write lands under
  `WSL_CARE_ROOT`. They skip on the Linux legs or when the runner is `dotnet`. The sampling flows are **skipped when the runner is
  elevated (CI's Windows runner is)**; there the elevated legs prove the rest: the 81 refusal in `WindowsWatchFlows`, and the
  elevated leg of `WindowsTasksRunFlows` — the REAL supervisor over the built binary, the worker refused with 81 and passed through
  (`the worker exited 81`), nothing written — so CI keeps running the supervisor end to end. A second supervisor test forces that leg
  on any Windows run through the sandbox's privilege claim.

**The own reviews of WR-D4 (2026-10-12)**: no blocking issue; every item accepted and fixed in the same story.

- **M1** — CI's Windows runner is elevated: the elevated leg above (`WindowsTasksRunFlows`).
- **m1** — the last arm of `WindowsTimerBranch` is the dry reason itself: EMPTY means every gate is open, the place of A22's act.
- **m2** — the CLI's retention log is one method (`LogRetention(log, result.Swept)`).
- **m3** — the elevation limit is in §10 (open risks).
- **m4** — a busy Windows watch's `historyReason` is `NoAgentHistory` (it carried the busy reason, a false warning); RED first.
- **The second reviewer**: a failing retention sweep (`IOException`, `TimeoutException`) is now tested — the ledger recorded, the
  problem in `Swept`; an elevated status is asserted to create no `mcp-cpu.lock`.
- **Notes, no change in this story:** the ledger lock file is opened without a reparse check, the same as the run lock beside it
  (pre-existing); `RunLock.TakeAsync` does not catch `UnauthorizedAccessException` (pre-existing); the retention runs on the wall
  clock, as `collect`'s does; S7b.3 must decide whether `auto.A22` needs a `UserLayerUnread`-style gate (the distro's watch has one).

**The coai code round on WR-D4 (session `062e684e`, 2026-10-14)**: verdict `proceed`, eleven findings, one accepted, ten rejected.

- *Accepted (finding 6, minor)*: the text answer of `watch` (no `--json`) now names the retention sweep in its one line —
  `; retention: N history line(s) and M detail(s) removed` and its problems in brackets — as `--json`'s `retention` does, not only the
  log (`Cli.Tests/WatchCommandTests.A_windows_watch_in_text_names_what_its_retention_sweep_removed`: red first, the line without it;
  break-it: the line's retention part dropped → red).
- *Rejected, the ledger lock held across the CPU window (findings 4, 5)*: that is the plan round's finding 1 itself — releasing it
  across the window lets two writers read one baseline and drop each other's sample.
- *Rejected, "only `UnauthorizedAccessException` is caught" (findings 3, 9)*: refuted — `PhysicalFileSystem.TryLockExclusive` already
  turns every `IOException` (missing folder, long path included) into `ExclusiveLock.Busy`.
- *Rejected, the retention sweep "needs a preview, a switch and a run record" (finding 7, blocking)*: it trims the daemon's OWN run
  records, as `collect` always has, through the deletion policy, with its result reported — record keeping, not a cleanup action.
- *Rejected, out of this story*: A22's dispatch after the hook (finding 8 — S7b.3's), per-OS seams out of `WatchContext` (finding 0),
  the sweep as its own pipeline step (finding 1), the lock's name by `Path.Combine` (finding 2 — the separator is deliberate), a
  progress line on stderr (finding 10 — an unattended task).

**WR-D5 as built (2026-10-14).** `Core/Scheduler/ArchiveBody.cs` (new), `Core/Scheduler/TaskWorkers.cs` (the archive entry of the
closed table), `Cli/Commands/TasksCommand.Run.cs` (the dry note), `Cli/Commands/ArchiveCommand.cs` (`SayWouldMove`), the usage text of
`tasks run`; tests `Cli.Tests/TasksRunArchiveBodyTests` (4, the plan's §6 names), `Core.Tests/Scheduler/TaskWorkersTests` (the table),
`Scenarios/WindowsTasksRunFlows` (the old "archive starts nothing" case replaced by a dry and a real flow); docs in
[module_daemon.md](../research/module_daemon.md) § *The Windows runner's archive body* and [module_tests.md](../research/module_tests.md).
Decisions the plan did not spell out:

- **The dry decision is taken in the SUPERVISING process**, before the worker's argv is chosen (the plan text: "`tasks run archive`
  runs `DryRunWindow.Decide` … while dry → `<self> archive preview --json`"). WR-D4's watch decides inside its worker because its argv
  does not depend on it; the archive's does. `TaskWorkers.For` takes a `Func<ArchiveBody.Choice>` that ONLY the archive entry calls
  (`Decide` writes the first-week stamp, so a `watch` task must never ask — `TaskWorkersTests` hands the watch a function that throws).
- **"The run log records would move N sessions, M bytes"** is ONE stderr line written by the preview worker itself
  (`archive preview: would move N session(s), M bytes; nothing was moved`), because the supervisor's worker streams are inherited and
  it cannot read the worker's answer. It is written on the Windows binary's `--json` run only, so the distro's root-run preview child
  (whose stderr root reads) is untouched; the text form already prints the counts. Enabled agents only. The supervisor adds its own
  line first: `tasks run archive: a dry run (<reason>): the worker is "archive preview" …`, which also names how the owner ends the window.
- **No new argv beyond the two verbs**: `archive preview --json` / `archive run --json`, no `--budget-seconds` (the run's own default
  `archive.runBudgetMinutes` applies and `timer.runLimitMinutes` is the supervisor's deadline, as the plan's closed table already had).
- **The lease, not the lock, is what the plan names for "a second archive"**: `archive run` takes the side's lock FIRST (a second
  concurrent run answers `busy`, exit 75, before any base I/O) and the lease after; the test plants a live lease (this test process, as
  the product writes it) so the refusal is the lease's, naming host, pid and run. Which of the two stops a real second run is a matter
  of order: the lock is tried before the lease (corrected by the own review, M3, below); neither is the run lock.
- **`dryRun` is turned off in the machine layer in tests** (`SafeDirection.On`: a user layer may only turn it on).
- **Break-its** (product code, sandbox only), each red then restored byte for byte (`cmp`): the dry branch made to run `archive run`;
  the archive task made to take the run lock; the lease refusal's holder text dropped.

**The Fable own review of WR-D5 (2026-10-14)**: no blocking issue; every item accepted and fixed in the same story:

- **M1** — an observe-only configuration never runs `archive run`: `ConfigLoader` merges the other entries, so `loaded.Config` can carry
  `dryRun` false while `IsObserveOnly` is true. `ArchiveBody.Decide` previews with `{ObserveOnlyReason}: {errors}` BEFORE
  `DryRunWindow.Decide` (the order of `WatchRun.Windows`), so the stamp is untouched. Test
  `An_observe_only_configuration_previews_and_never_runs_archive_run`.
- **M2** — the closed table is a TYPE guarantee again: `TaskWorkers.For(…, Func<DryRunDecision>)`; the only decision to argv mapping is
  `ArchiveBody.ArgumentsFor` inside Core; `ArchiveBody.Choice` and the public argument lists are gone.
- **M3** — a real second archive meets `archive.lock` BEFORE the lease and used to answer `busy` without a holder; `ArchiveRun.BusyReason`
  now reads `holder.json` (as `archive status` does) and names pid, run and since. The "matter of timing" above is corrected: it is
  the order. Test `A_second_archive_during_a_run_is_refused_busy_naming_the_holder`.
- **m1** — an elevated supervisor previews and starts no week (`ArchiveBody.Decide` takes `host.Privilege.IsRoot`); the elevated
  Scenarios legs assert no stamp. **m2** — the lock test's second half only proves the lock is free when the supervisor launches the
  worker; its reason and the docs say exactly that (the worker's own lock and lease are held by the M3 and lease tests). **m3** — the
  real Scenarios flow asserts exit 0, `done`, `Copied == 1`. **m4** — doc counts and the one-week wording fixed. **m5** — a REAL
  run says why (`a real run (dryRun is off and the 7 days since the first timer run (…) have passed)`), one stderr line.
- **RED evidence**: the fixes were written before their tests this round, so each test's RED was taken by reverting its fix in the sandbox
  build: M1 `Expected supervisor.Launches to be equal to {"archive", "preview", "--json"}, but {"archive", "run", "--json"} differs at
  index 1`; m1 `Expected Lines(stderr) {…} to have an item matching l.Contains("an elevated process", Ordinal)`; M3 `Expected
  report.Stop "another archive run of this side holds its lock" to contain "pid 4242"`. Each restored byte for byte (`cmp`).
- **Suites**: Core 2759 (2678 passed, 81 skipped), Cli 432 (430, 2), LiveContract 28 (12, 16), Scenarios 624 (the Linux legs skip). One Scenarios run had `FetchWindowsDaemonFlows...(shape: "bin-dll")` time out under load (1 min 1 s); the class run alone passes 43/43 and D5 does not touch it. M2 is a compile-time guarantee (the table takes a `DryRunDecision`; `ArchiveBody.Choice` is gone), so its proof is that nothing outside Core can build the argv.

**The story's coai plan round (session `ad924fee`, 2026-10-15)**: `proceed`, five findings, one accepted, four rejected. *Accepted*:
the two archive locks are named apart in the docs — `archive.lock` (this user, this machine, an exclusive open released by the OS,
taken first, its holder from `holder.json`) and the side lease (one side across machines, in the base, a dead holder of this host and
boot taken over); neither is the run lock (module_daemon.md § *The Windows runner's archive body*). *Rejected*: a stale holder after a
supervisor kill (refuted — the OS releases `archive.lock`, `SideLease` takes over a dead holder); the stamp only after a successful
preview (the distro's `Decide` rule, which only ever errs towards more dry runs); an interrupted archive's recovery (the archive's own
two-phase protocol, proven by `ArchiveRunFlows`' 14-point kill theory — a deadline kill is the same kill); the dry log's counts
untested (refuted — `TasksRunArchiveBodyTests` and the Scenarios flow assert "would move 1 session(s), 14 bytes").

**The coai code round on WR-D5 (session `ad924fee`, 2026-10-15)**: `proceed`, seven findings, five accepted, two rejected.

- *Accepted, finding 6*: `holder.json` is written just AFTER `archive.lock` is taken, so a second run in that instant can read the
  previous run's record — the busy answer now says "the last recorded holder: pid …, run …, since …", never a certainty
  (`A_second_archive_during_a_run_is_refused_busy_naming_the_holder` tightened; the old wording reverted → red, restored → green).
- *Accepted, finding 5 (and 2, 3)*: the "would move N session(s), M bytes" sentence is the preview's own, `ArchivePreview.WouldMove`
  in `Core/Archive` — `archive preview` no longer depends on the task runner — and it sums in one pass.
- *Accepted, finding 0*: the supervisor says the archive's window explicitly for the archive task (`TasksCommand.Started`), no longer
  inferred from `Lazy.IsValueCreated` inside an argument. A refactor: the existing note tests hold it.
- *Rejected*: the nested delegate in the table's entry (finding 1 — it is what keeps the table closed: the archive entry alone asks
  for the decision, and only Core maps it to argv); the two prefixes in the log (finding 4 — each process names its own verb, as every
  worker's stderr passes through the supervisor).

**WR-E1 as built (2026-10-15).** `src_vs_code/src/client/WindowsCareClient.ts` (widened), `client/windowsTasks.ts` (new: the typed
answers and their pure readers), `client/runReadChecks.ts` (new: the run-read checks, extracted from `WslCareClient`), `client/verbs.ts`
(the `tasks` tails), `shared/shapes.ts` (`STAGED_DIGEST_SHAPE`), the strict fake `test/fake/fakeWsl.ts`, the stage fixtures
`test/fixtures/windows-tasks/`; the docs in [module_vs_code.md](../research/module_vs_code.md). Deviations and decisions the plan did
not spell out:

- **`exe(source)`** takes `ExeSource` = `bundled` | `staged(<digest>)`. The staged path is `<LocalAppData>\wsl-care\bin\<digest>\wsl-care.exe`, where
  `localAppData` is a client OPTION (the profile's `%LOCALAPPDATA%`, absolute, a local drive, in its normal form) and the folder is
  DERIVED from it (the Opus own review, M1 — the first build took the whole staging folder as a free option). Without it (or off
  Windows) the call answers `unavailable` with the reason.
- **`tasks render` runs on a staged copy only** (`tasksRender(task, digest)`), `tasks stage` / `--remove` on the bundled exe only —
  by construction, so the wrong exe cannot be asked; the strict fake refuses the other combination as well.
- **`tasks run` is not in the client**: it is the scheduled task's action (its supervisor spells `--timer`); the run-now buttons
  (E4) start the person's own registered task. The fake refuses it.
- **Timeouts**: `tasksTimeoutMs` (default `TASKS_DEFAULT_TIMEOUT_MS` = 60 s, the default WR-E2's `windowsTasksStageSeconds` will carry)
  and `readTimeoutMs` (default: the run reads' ceilings). No setting added in E1.
- **`read(request, source = bundled)`** serves the five Windows reads through the checks and exit sets now shared with `WslCareClient`
  (`runReadChecks.ts`, reuse-first move 2: extracted unchanged, the suite green before the new code).
- **The expected digest is the client's** (the Opus own review, m6 — the first build took it from the caller): `tasksStage()` hashes
  the bundled exe itself through a seam (`digestOf`, default `client/fileDigest.ts`: Node `crypto`, streamed) and judges the stage
  answer against it; a bundled exe that cannot be read fails the stage before the daemon is asked.
- **`wrongSide` is exit 2 as the daemon gives it**, which is also what an older daemon without `tasks` answers (usage); the reason is
  the daemon's stderr line. A `registrable: true` render whose `digest` is not the staged copy asked for is surfaced as
  `notRegistrable` ("the copy that answered …").
- **No `root` member is read** from `tasks stage --remove`'s answer: `bundleScan.test.ts` forbids the word `root` in any shipped
  literal outside the root module, and the extension needs only what went, what was kept and what stays untouched.
- **No stage golden exists** (the daemon writes only the render golden): the stage and remove answers are hand-written fixtures
  from `StageReport` / `StageRemoveReport` (profile folder `C:\Users\user`); the render answers are the daemon's golden, read by the
  fake. A stage golden from `ContractFilesTests` would remove the hand-written copy — an open tail.
- **The coai plan round on WR-E1 (session `ef8a966f`, 2026-10-14)** — accepted findings, each with its own RED test
  (`windowsTasksClient.test.ts`, `fakeWindowsTasks.test.ts`):
  1. `staged(<digest>)` accepts only `^[0-9a-f]{64}$` and THROWS (`RangeError`) before any path is built or process started — tests
     for `../../other.exe`, upper case, 63/65 characters, a trailing newline, empty, on `exe`, `tasksRender`, `tasksStage` and `read`
     (red: `Missing expected exception (RangeError)`).
  2. `tasks stage` outcomes are typed from the exit code AND the JSON — 0 staged / reused, 1 refused (a refusal that still names a
     digest is never `staged`), 75 busy — each a distinct result; a code and an outcome that disagree are a failure; `stage.json` is
     never read (red: `actual 'failed', expected 'refused'`).
  3. A pure provenance check (`checkProvenance`): a stage answer whose digest differs from the expected one (or is not 64 lower-case
     hex, an empty one included) or whose `version` is not `BUNDLED_DAEMON` is `provenanceRefused`; the render's re-verification
     stays the daemon's (WR-D1) and its not-registrable answer is surfaced typed.
  4. `tasks render` outcomes: 0 (registrable or not, from the JSON), 2 wrong side, 78 configuration in error — the strict fake has
     scenario answers for 2 and 78 and the tests read them.
- **Break-it (product code only, each restored byte for byte)**: the digest check removed; the provenance digest compare removed; the
  version compare removed; the registrable-digest compare removed; the exit/outcome pairing removed; a second module naming the staged
  exe; the strict fake answering an unknown `tasks` argv. Each turns named tests red (results in the story report).
- **Not built here (E2–E4)**: buttons, Task Scheduler reads and registration, PowerShell, the pending record, settings.

- **The Opus own review of WR-E1 (2026-10-15)** — two major, seven minor findings, all fixed, RED first (`windowsTasksReview.test.ts`,
  `noRealWsl.test.ts`):
  - **M1 — the staging folder was free.** The client derives it: the option is `localAppData` and the segments `wsl-care\bin` are
    fixed; a LocalAppData with `path.win32.normalize(x) !== x`, a `..` or `.` segment, forward slashes, a doubled or trailing
    separator, a share or a relative path names no staged exe. A stage answer must also report the path the client would start
    (`exe(staged(report.digest))`, compared without case): else `provenanceRefused`.
  - **M2 — a render was registrable for any copy that said so.** It is `registrable` only when the answer's `version` is
    `BUNDLED_DAEMON` AND its `command` is the file the client ran (without case; the file is passed into `renderResultOf`) AND its
    digest is the one asked for — an older retained copy answers registrable for itself and is now `notRegistrable` with the reason.
  - **m1** `read()` takes the five Windows reads only (`WindowsRunRead`, `WINDOWS_READ_NAMES`), by type and at runtime — `archive
    preview` and `archive check-base` are refused (`readRefused`). **m2** exit 75 with a readable answer whose outcome is not `busy`
    is a failure (stage and remove). **m3** one table of "it did not end" sentences (`endingReason`, with the setting a caller may
    name), no copy in the client. **m4** `noRealWsl.ts` also trips on `wsl-care.exe` / `wsl-care` by base name (its own tripwire
    test; the fake runner starts `node` and passes the exe as data, so the fake is untouched). **m5** the stage fixtures use the
    daemon's real sentences (`Staging.cs`: the copied line, the already-staged line, the busy line with the lock file, the busy report's
    `source`) and one source path with the extension tests' own publisher. **m6** see above. **m7** §4 reworded (above).

- **The coai code round on WR-E1 (session `ef8a966f`, 2026-10-15)** — `proceed`, ten findings, four accepted, six rejected.
  *Accepted*: two comments no longer spell the timer's mark (findings 0, 1; the enforced rule is `bundleScan.test.ts` over the
  bundle, but the words cost nothing); exit 75 with a readable answer that names NO outcome is a failure, not a busy (finding 2 —
  test `code round finding 2…`, red first; break-it: the old default restored → red); a LocalAppData that names no staged copy says
  the value it received (finding 8 — test `code round finding 8…`, red first; break-it → red). *Rejected*: a second path authority
  (finding 3 — `checkProvenance` already compares with `exe(staged(digest))`); exit 75 with no readable body being busy (finding 4 —
  the code is the daemon's busy by contract, and busy is the fail-safe reading); the digest stream's error handling (findings 5, 7 —
  a Readable's async iterator rejects with the error and destroys the stream); trailing dots or spaces in LocalAppData (finding 6 —
  fails closed through the path comparison); `firstLine` splitting a capped buffer (finding 9).

**WR-T1 as built (2026-10-15).** `Core/Actions/WindowsTemp/` (`WindowsTempAction`, `TempSettings` / `TempFacts`, `TempGlob`, `TempGuards`,
`TempRoots`, `TempCursor`, `TempPlanner`, `TempJudge`, `TempScanner`, `TempBudget`, `TempRun`, `TempDeletion`), `Config/ConfigKeys.Temp.cs`
(`temp.*`, `auto.A23`), `TextRule.TempGlobName`, `IFileSystem.OwnerSidOf` + `PhysicalFileSystem.Owner.cs`, `FileEntry.CreatedUtc`,
`ActionPreview.Carry`, `ActionContext.UserSid` / `EngineContext.UserSid`, `ActCommand` (the Windows privilege rule), `ActionId`,
`ActionRegistry`, `contracts/actions.json` and `config-keys.json` regenerated; the docs in [module_daemon.md](../research/module_daemon.md)
§ *The Windows `%TEMP%` cleanup: A23* and [module_tests.md](../research/module_tests.md). Deviations and decisions the plan did not spell out:

- **The root (the story's plan round 56c596c6, finding 2) — a deviation from §3.12's "the shell-folder API".** There is no shell known
  folder for TEMP. The root is `LocalApplicationData` + `\Temp`, cross-checked with `GetTempPath` (via `WindowsEnvironment`): both must name
  the same folder (case-insensitive, normalised) strictly inside the user's profile, else the preview REFUSES with the reason and nothing is
  deleted. A `TEMP` / `TMP` naming ANOTHER folder, or a short 8.3 profile spelling, differs in spelling and is therefore a refusal, not a guess (a later decision may widen that); a junction AT `%LOCALAPPDATA%\Temp` leading to another drive is NOT a refusal — the spellings agree and the policy and the walk judge the real path (corrected by the Fable own review m7).
  It sits behind `IHostPaths` (the sandbox layout), so tests pass a sandbox root.
- **The story's plan round (coai session 56c596c6, 2026-10-14): seven findings, all accepted.** (1) *Reparse before traversal*: a
  candidate whose own listing attributes say reparse point is `Unknown` before any child is listed; every enumeration reports reparse
  entries (`AttributesToSkip = 0`, so they are seen, never skipped or entered) and one inside a tree retains the tree. (2) The root, above.
  (3) *The cursor always advances*: every candidate the run reaches is decided (deleted, retained, excluded, unknown) and the cursor moves
  to the last, and a run that decides its first name after the wrap completes a pass (`PassesCompleted`, `PassStarted`, `PassDecided`).
  (4) *The `claude` guard* compares the name (ordinal, ignore case; trailing separators, dots and spaces stripped as Win32 does) and the
  canonical full path against `<root>\claude`, before any traversal; the deletion policy's `ClaudeTemp` root is the second line. (5) *A git
  working folder* (a `.git` file or folder at its top) is retained "a git working folder - never deleted" whatever the allow-list. (6)
  *The delete walk never follows*: each folder is listed without following, `ReadLink` is asked of each sub-folder just before entering
  it, an entry that is or became a link stops the candidate ("a link appeared") and nothing beyond it is touched; every delete goes through
  the deletion policy. (7) *Resume*: the interrupted candidate is retained when a remaining FILE is newer than the checkpoint's recorded
  newest file time OR a remaining FOLDER was CREATED after the checkpoint (deleting children rewrites a folder's write time, never its
  creation time) — a fresh subfolder created during the interruption retains it.
- **`act` on the Windows side is unelevated (not root).** The plan said the engine's Windows path would be proved; `act` refused every
  non-root process. On the Windows side the rule is inverted: an elevated process is refused (exit 81, "NOT elevated"; its files belong to
  the Administrators group, which the owner check would not accept) and `--detach` is refused. `ActionContext.UserSid` carries the
  account's SID; empty owns nothing. The distro's refusal message now names the side an action runs on ("A23 runs on the Windows host, not
  on this side (the WSL binary)").
- **A23 is not part of a full check's timer pass** (`ICleanupAction.InFullCheck`, default true, false for A23): `collect` on either side never lists it, so the Linux run records are unchanged and the cleanup runs only from its own `act A23` (the WR-T2 task). The Windows order (the side refusal first, then "elevated") applies when the request holds at least one Windows action, so `act A23,A4` from an unelevated process is told "A4 runs inside the WSL distro"; a request of distro actions alone keeps the distro's order (root first), exactly as before.
- **`auto.A23` is ON by default** like A11/A19/A21: installing the cleanup task (WR-T2) is the opt-in, and `dryRun` plus A23's OWN first week
  (`first-timer-run-A23.json`, below) make the timer dry. The extension's `ACTION_IDS` / `BUTTON_ONLY_IDS` gained `A23` (`rootIds.test.ts` holds `ACTION_IDS` equal to
  `actions.json`; no WSL cleanup op may act on it). Those TypeScript tests were NOT run (no `node_modules`, no installs).
- **One extra key, `temp.maxTopLevelEntries`** (500000): a cut top-level listing has no stable order for the cursor, so a folder holding
  more refuses whole. `temp.allowGlobs` is an open list (`TextRule.TempGlobName`, at most 32): a pattern is letters, digits, `. _ - * ?`,
  may not match `claude` or a `testsToDelete-` name (the DH plan's boundary), so a key value can never name them; the selection refuses
  both again regardless of the key.
- **The timer does not scan names off the allow-list.** A timer trigger spends its budget on what it may delete (`NotAllowed`, unscanned);
  every other trigger scans them for the "would be eligible if allowed" groups. Run-time costs are bounded by `temp.scanMaxNodes` /
  `temp.scanMaxSeconds`, shared by one run's scans, plus ONE shared allowance for the run's re-validation scans (the Fable own review m6; the delete walk itself has no time cap).
- **Re-validation is a stat of the candidate's own path plus a fresh scan** (`ReadLink`, `DirectoryLastWrite` / `FileSize`, then the same
  `TempJudge`), never a re-listing of the 100 k-entry folder. A link now, a changed owner, a fresh file are `NotRemoved` "changed since the
  preview"; a vanished one is "already gone".
- **The checkpoint is written for folder candidates only** (a file is deleted by one call). A cancelled run keeps it and returns
  `ActionRun.Interrupted`, which the engine records as `interrupted`; a killed run is swept by the engine's existing reconcile. Cursor
  writes after each candidate (marker cleared, `Last` moved) and once at the end (the plan's `Last` and pass counters).
- **The run detail** rides the existing `ActionRun`: `Count` = candidates deleted, `FreedBytes` = logical bytes of the files deleted
  (the basis says "not disk space freed"), counts per outcome and per group in `Notes`, the first `preview.maxItems` in `Removed` /
  `NotRemoved`. The preview's counts and per-pattern groups are `Facts`; exact paths on expand are WR-T2's `tasks preview cleanup`.
- **No lease with A26 and no `tasks`/task/panel code** — WR-T2 / WR-T3 and DH-7. `wsl-care-test-*` stays on the allow-list (DH-8 renames
  the harness later). WR-T2 should size `timer.runLimitMinutes` / `windowsTasks.cleanup*` against `temp.scanMaxSeconds` (120) plus the
  delete walk, which has no time cap of its own (the supervisor's deadline and the cancellation end it; the checkpoint makes that safe).
- **Tests.** 80 WindowsTemp tests in Core.Tests (an in-memory `%TEMP%` for the logic; the real engine and file system in a `TempRoot` sandbox on the Windows
  legs), 4 Cli.Tests, 4 Scenarios; the break-it list is in `module_tests.md`. The elevated CI runner: the engine tests take the owner of
  what they create as "the current account", the two Scenarios that need an unelevated child skip on an elevated account.

**WR-T1, the Fable own review (security), 2026-10-15.** The review found no way for A23 to delete something it must not; it found four
holes that could widen what a delete reached and seven smaller defects. Each behavioural fix has its own RED test (the failure
message is in [module_tests.md](../research/module_tests.md)), and a break-it.

- **M1 (must) — the folder removal was recursive.** The delete walk removed the last folder with a recursive delete, so a file that
  appeared AFTER the listing was deleted unjudged. `IFileSystem.DeleteEmptyDirectory` (non-recursive; `Directory.Delete(path, false)`
  in `PhysicalFileSystem`, judged by the deletion policy like every delete) now removes a folder only when it is empty: a folder that
  still holds anything is kept and counted "kept in use" (the operating system's own "directory not empty"). No A23 delete is
  recursive any more.
- **M2 — creation times count.** The scan tracks the newest CREATION time over files AND folders (`TreeScan.NewestCreated`), the age
  is the later of a write time and a creation time (a copy keeps the write time and takes a new creation time; the candidate's own
  too: `Candidate.Newest`), and a resumed candidate is also retained when anything inside was created after the checkpoint.
  `FileEntry.CreatedUtc`, `FileSizeResult.Measured.CreatedAt` and `DirectoryTimeResult.Measured.CreatedAt` carry it; each defaults to
  the far future, so a stat that does not say keeps rather than deletes.
  **The residual (WR-T2 own review, Fable iv):** a copier that preserves the creation time as well (`robocopy /COPY:DAT /DCOPY:T`) can still place an
  "old" file into a half-deleted candidate; nothing on the file system tells it from a file that was always there, and the plan accepts that class.
- **M3 — "the first week is dry" is true for A23.** The common stamp `first-timer-run.json` is started by the Windows watch and by the
  archive body, so with `auto.A23` ON it would have opened deletion on the first A23 run. `ICleanupAction.DryWindowStamp` (empty for
  every action but A23) names a per-action stamp, `first-timer-run-A23.json`: `DryRunWindow.Decide` / `Peek` / `File` take the stamp
  name, the engine decides each action's dry gate on ITS stamp (the run's record uses A23's stamp when A23 is the only action) and the
  week starts at the first timer run that reaches A23's deletion gate. `auto.A23` stays ON by default; the doc comments say what
  governs the timer.
- **M4 — the walk checks the real path.** Before every delete the walk asks `ResolvePath` and requires the REAL temp root plus the entry's
  relative path; any other answer (a link swapped in anywhere on the way) stops the candidate "a link appeared". **Residual, stated:**
  the check and the delete are two calls and the seam has no handle-relative delete (`NtSetInformationFile` on an open handle), so a swap
  inside that window is not excluded; the deletion policy still judges the path and a swap can only reach what this account may delete.
- **m1/m2 — the glob rule is exact.** A pattern is refused when SOME name starting with `testsToDelete-` could match it (exact: a
  leading part of the pattern matching the whole prefix, not a sample of lengths 0..64 — `testsToDelete-a*`, `?estsToDelete-?y*`,
  `tests*`), and when it does not start with two literal characters (`a*`, `?o-*`, `c`). The default list passes both.
- **m3 — a `.git` at ANY depth, in any case, retains the tree** (a nested repository is somebody's working copy).
- **m4 — a resumed candidate never drags the cursor back.** Finishing the interrupted candidate clears its marker and leaves `Last`
  where the run began (it belongs to an earlier pass).
- **m5 — an unreadable stat is "unreadable", not "already gone"**: its own note and count (`unreadable N`), nothing deleted.
- **m6 — the delete phase is bounded.** One shared allowance of `temp.scanMaxNodes` / `temp.scanMaxSeconds` for all the run's
  re-validation scans; a candidate that cannot be re-validated inside what is left is retained. §3.12's "Budgets on every run" is
  reworded: the delete walk itself still has no time cap (the supervisor's deadline and the cancellation end it; the checkpoint makes
  that safe).
- **m7 — a junction at `%LOCALAPPDATA%\Temp` is not a refusal** (both spellings agree; the deletion policy and the walk judge the real
  path). A `TEMP` / `TMP` naming another folder, or an 8.3 short profile spelling, still is. The wording of §14's root bullet and of
  `module_daemon.md` is corrected.
- **m8 — the side refusal comes before "needs root".** A request holding a Windows action (`act A23,A4`) from an unelevated Windows
  process is told "A4 runs inside the WSL distro"; a request of distro actions alone keeps the distro's order (root first, which the
  existing `ActFlows` test holds).
- **m9 — for WR-T2:** the supervisor must spawn exactly `act A23 --timer` (the timer trigger; `--json` as the other tasks do) — the
  cleanup is a timer run under A23's own dry week, never `--manual` or `--confirm` alone.
- **m10 — UAC off:** with User Account Control off an administrator's token is always elevated, so A23 always answers 81 (not
  elevated required) for that account — fail closed, by design; the owner of such a machine runs the cleanup from a standard account.
- **m11 — not here:** the `testsToDelete-` rename of the test harness is DH-8's debt; `wsl-care-test-*` stays on the allow-list.
- **A defect found on `main`, not fixed here:** this plan file holds §1–§14 twice on `main` (a doubled paste around line 759 of the
  rebased file); §3.12 was corrected in both copies, the as-built blocks live at the end.

**WR-T1, the coai code round (56c596c6), 2026-10-15.** The round passed; four findings accepted, each with a RED test, a fix and a
break-it (messages in [module_tests.md](../research/module_tests.md)):

- **Finding 4 — the candidate folder is judged before it is listed.** `TempDeletion.DeleteTree` asks `ReadLink` (not a link) and the
  real-path comparison for the CANDIDATE folder itself before listing it; before, only sub-folders and entries were asked. A folder that
  resolves elsewhere at delete time stops "a link appeared" with nothing deleted.
- **Finding 10 — nothing newer than the cutoff is deleted.** The walk gets the cutoff (`now - temp.olderThanDays`); an entry (file or
  folder) whose later of write and creation time is newer stops the candidate, retained "changed while deleting", nothing after it
  deleted. A file dropped into a not-yet-listed sub-folder after the scan is therefore never judged by the old scan.
- **Finding 11 — one budget for the listing and the planning.** `WindowsTempAction.PreviewRoot` creates the run's `TempBudget` before the
  top-level listing and passes it to `TempPlanner.Plan`; time spent listing counts against `temp.scanMaxSeconds`.
- **Finding 8 — the cap defers arithmetically.** `TempPlanner.Run.DecideAll` stops at the first name past `temp.maxItemsPerRun` (or an
  exhausted budget) and adds the number of names left to `Deferred`; it no longer loops over them (the count is unchanged).

Rejected (nine, one line each): 0 refuted — `TryStat` asks `ReadLink` first; 1 a mixed-week request cannot exist today (A23 is the only
action with its own week); 2 the two refusal orders are by design, and m8 fixed the one that mattered; 3 refuted — `Walk.Entry` halts on a
`Link`; 5 the scanner never deletes and the walk re-checks; 6 the per-entry real-path check is M4's guarantee; 7 the cursor written per
decided candidate is crash safety; 9 one glob matcher in one assembly; 12 the top-level listing is bounded by `temp.maxTopLevelEntries`
and the wrap needs the sort.

**WR-T1, CI's Linux legs (2026-10-16).** Two places took "every action this build holds" as "every action that previews here", and
the first Windows action broke both on the WSL binary (A23 is refused there, exit 2, naming the side): `Scenarios/ActFlows`'
all-actions preview now previews this side's actions and asserts the other side's refusal separately; and `--help`'s
`"act" holds these actions:` line — which the release smoke (`.github/scripts/smoke-daemon.sh`, `smoke_act_preview`) previews from —
now names the WSL distro's actions only, with the Windows host's on their own line (`On the Windows host, "act" holds: A23.`). Test
`ActCommandTests.The_help_names_the_windows_hosts_actions_on_their_own_line` (red first; break-it: A23 back on the distro's line →
red). On the Windows binary the smoke still expects exit 2: the distro's actions are refused there, as before.

**WR-E2 as built (2026-10-17).** `src_vs_code/src/windowsTime/guardScripts.ts` (`QUERY_SCRIPT` widened — the guard's one script, not a
second), `windowsTime/guardState.ts` and `guardHost.ts` (the guard consumes the read), `windowsTime/guardTask.ts` (`SUMMARY_FUNCTION`'s
logon case), `scheduler/` (the peer both consumers use, after the coai code round: `schedulerAnswer.ts` the parser, `schedulerHost.ts` the ONE read in flight, `queryScript.ts`, `taskSummary.ts`), `windowsTasks/tasksState.ts`, `tasksView.ts`,
`tasksHost.ts`, `process/runner.ts` (`killTree`), `client/WindowsCareClient.ts` (`bundledDigest()`), `settings/numbers.ts` +
`package.json` (`wslCare.timeouts.windowsTasksStageSeconds`), `panel/view.ts` / `viewModel.ts` / `panelProvider.ts`, `media/panel.js`,
`extension.ts`, `testApi.ts`; the docs in [module_vs_code.md](../research/module_vs_code.md) and [module_tests.md](../research/module_tests.md).
The story is READ ONLY: nothing is registered, removed, run or written to Task Scheduler. Decisions the plan did not spell out:

- **The query's answer is one tagged format** (`windowsTime/guardScripts.ts`): the folder's lines (`channel=`, `stamp=`, `user=<sid>`,
  `folder=present|absent|unreadable` + `hresult=`), then one section per task opened by a line that BEGINS `task=<name>` (`enabled=`,
  `state=`, `lastRunUtc=`, `lastResult=`, `summary:` lines; or `read=unreadable` + `hresult=` for a task that could not be read). A task's
  lines are collected before they are printed, so a failure leaves no half section; control characters of a name and line breaks in a
  summary line cannot add a line. The old `guard=…` lines are gone: the guard is read out of the folder's tasks BY NAME
  (`parseGuardAnswer(stdout, task = GUARD_NAME)`), so `summariesMatch` needs no task name — it compares two summaries — and the PARSER
  takes it. A task whose fields do not parse (the guard's too) is `unreadable`/`unknown` for that task only; the guard's former "answered
  without the last run → unknown" is now "its definition printed no summary lines → unknown".
- **`SUMMARY_FUNCTION`'s logon case prints `interval=`** (WR-D1 asked for it): the guard's own summary became
  `trigger=logon enabled=True delay=PT60S interval=` (an empty interval — the guard's logon trigger repeats nothing). Both sides of the
  compare read the REGISTERED task through the same function, so no installed guard went stale by this.
- **`SchedulerHost` holds the one read** (`guardHost.ts`'s shape, lifted): one query in flight, a refresh during a flight answered by
  ONE more, a listener list; the guard host now REQUIRES it in its deps (`queryTimeoutMs` left the guard's deps; `GuardUi.query` is the
  runner `extension.ts` gives the shared host). The recorder's default query answer is the new format ("no folder").
- **`TasksHost` (read side) is `windowsTasks/tasksHost.ts`**; WR-E3 widens the same class with the install/remove flows. It is the part's
  only caller of the client.
- **The trigger (plan round, finding 4; amended by the coai code round, finding 0 — below).** `extension.ts`'s `panelRefresh` is the only caller of the shared read (`parts.scheduler.refresh()`) and of the probe (`TasksHost.refresh()`, which no longer reads Task Scheduler) —
  the panel opening (`resolveWebviewView`), becoming visible (`visibilityChanged`), its *Refresh* button and the `wslCare.refresh`
  command: the moments the guard's read has always run at. `panelRefresh` no longer calls `parts.guard.refresh()` beside it (two reads
  for one refresh); the guard's `afterRun` still reads once after an elevated run answers. Tests: `tasksHost.test.ts` (no query at
  construction, no timer in `windowsTasks/`, ONE `parts.scheduler.refresh()` and ONE `parts.tasks.refresh()` call site inside `panelRefresh`, none of the guard's).
- **The copy probe.** The digest is the client's own SHA-256 of the bundled exe (`WindowsCareClient.bundledDigest()`, a file read; the
  stage's expected copy reuses it). "Staged" is `tasks render watch` on `staged(<digest>)` ANSWERING (registrable, not registrable, or
  the daemon refusing its configuration — exit 78); then `tasks render archive` is asked too. A start that finds no file (Node's
  `ENOENT`) is "not staged" and NOTHING further is rendered; any other ending — a blocked start (an Application Control policy), a
  timeout, an older daemon's exit 2 — is "could not be checked" with the start's own words, never "not staged". A probe that throws is
  that too and never rejects the refresh. The render's ceiling is the new `wslCare.timeouts.windowsTasksStageSeconds` (60, 10–600);
  the read's ceiling is the guard's `windowsTimeGuardQuerySeconds` (30, its description widened, no second setting — Q3).
- **Rows** (`tasksState.ts`, precedence): unreadable (its own read failed; the folder not readable is one line on each task with
  Task Scheduler's HRESULT), disabled, running (`State` 4 or `0x41301`), then the comparison with the staged copy's summary through
  `summariesMatch` (`differs` → "install again to update"; not staged → "this version's copy is not staged, install again to update";
  a render that gave nothing for THIS task → "cannot be compared with this version's copy (<why>)"), then the last run. A task that never
  ran — an empty or `never` instant, an empty result or `0x41303` — reads "installed, never run". A folder that does not exist
  (`0x80070002`) is an empty task set: every task "not installed". The last result in words is the daemon's exits (0, 1, 75 normal,
  78, 81, 84) and Task Scheduler's codes; 0, 75 and `0x8004131f` are `ok`, any other result `warn`.
- **The dry line** is the first answered render's `dry.reason` ("Windows tasks, dry run: …"), and empty until one answered (so never
  claimed for a copy that is not staged). `user=<sid>` is parsed and held on the read for WR-E3's comparison with the render's `userSid`.
- **The panel** (`PanelView.windowsTasks`, `buildPanelView`'s eighth parameter): a `div[data-windows-tasks]` in *Health*, between the
  bundled daemon's line and the guard's: `p[data-windows-tasks-copy]` ("Windows daemon copy: 0.4.0 (0123456789ab) staged" / "not staged";
  version = `BUNDLED_DAEMON`), `p[data-windows-task=<watch|archive>]` per task, `p[data-windows-tasks-dry]` when there is a dry line;
  nothing at all off Windows. Text only (`textContent`, every supplied word through `noticeText`).
- **The runner's tree kill (finding 8) is NEW, not "the existing kill"**: the runner only ever called `child.kill()` (one process). A
  request may now carry `killTree`: at its ceiling the runner runs ONE fixed, verifying PowerShell (`process/treeKill.ts`) that ends
  the child's tree by pid AND creation time — the host's own child only, never by image name, and never once the child has exited —
  waits for its verdict, and only then calls `child.kill()`; the `timedOut` result says through `treeEnded` whether the tree was
  VERIFIED ended (the first design was `taskkill /PID <pid> /T /F`; *the Opus own review of WR-E2* below replaced it). The query asks
  for it; no other caller changed. A node helper cannot test this (libuv puts the children of every node process in a kill-on-close
  job, so a node grandchild dies with its parent) — the test's helper is a Windows PowerShell starting `ping.exe` (45 pings, ends by
  itself after ~44 s) and the control proves a plain kill leaves it alive.
- **The canonical summary (finding 10)**: the golden's summary lines equal `summariesMatch`'s reading of SUMMARY_FUNCTION's read-back on
  EVERY OS through `test/support/summaryOfXml.ts` (an independent TypeScript reference reader of the XML, also held against the guard's
  own XML), and the Windows leg parses each golden XML with `NewTask(0).XmlText` (read only, a made-up SID, never registered) and asserts
  the real read-back lines equal the golden's, byte for byte.
- **The publication scan reads `package.json` as raw text** (`vsixCheck`): a setting description may not hold a `\\wsl` — written as the JSON-escaped `\\wsl-care` it reads as a UNC share — so the widened `windowsTimeGuardQuerySeconds` description says "every task this extension registers in Task Scheduler" (productName.test allows the extension's text to name wsl-care only as the daemon, so the folder's name is not spelt in it either) (the first full run caught it: 24 of its 25 failures were this one line).
- **Not built here (E3–E4)**: the install / remove / run-now flows and their buttons, the pending record, registration through PowerShell.
  Open for WR-E3: `TasksHost` is read only; its `view()` has no button list.

**The story's plan round (4fc3f328, 2026-10-16) — ten findings, all ACCEPTED, each a RED test first:**

1. *The `\wsl-care` folder does not exist on a machine never installed* → `0x80070002` on the folder is `folder=absent`, an empty
   task set; every task "not installed" (`schedulerAnswer.test.ts`, `windowsTasksState.test.ts`; break-it: the `absent` case removed → red).
2. *Not staged → no `tasks render`* → one probe, no second render; the copy line "not staged"; each registered task "installed — this
   version's copy is not staged, install again to update" (`tasksHost.test.ts`; break-it: the stop removed → red).
3. *Per-task isolation* → a task whose read failed or whose fields do not parse is that task's `unreadable` (break-it: one bad task
   unanswers the whole read → red).
4. *The trigger is stated and tested* → above.
5. *The copy line's sources* → above (`bundledDigest.test.ts`, `tasksHost.test.ts`).
6. *Access denied / any Task Scheduler error is a visible line* → `folder=unreadable` + HRESULT on each task, `read=unreadable` per task,
   `unknown — <reason>` when the query itself failed.
7. *A bounded ceiling* → the read reuses `wslCare.timeouts.windowsTimeGuardQuerySeconds` (read at every query; tested).
8. *On timeout the tree is killed and the panel says it timed out* → `killTree` (above); "unknown — the Task Scheduler read timed out
   after 30 s (…); its PowerShell and everything it started were ended" on each task (break-it: `killTree` ignored → the grandchild
   outlives the run → red).
9. *A never-run task reads "never run"* → "installed, never run", never `undefined` (tested for four spellings).
10. *One canonical summary format* → above.

**RED messages and break-its of WR-E2** (each break-it on PRODUCT code, restored byte for byte, the working tree clean after):

- RED: the first batch against stubs failed 35 of 78 with assertion errors — `the folder's catch maps the not-found HRESULT to
  folder=absent`, `expected 'read', actual 'unknown'`, `actual { kind: 'unknown', reason: 'checking…' } expected { kind: 'absent',
  channel: 'enabled', stampLeft: true }`; the tree kill: `the grandchild 39432 outlived the run it belonged to` (and the first, node,
  helper was a bad probe — see above); the setting: `wslCare.timeouts.windowsTasksStageSeconds is not contributed`; the state and host
  against stubs: 17 of 22 and 15 of 16 red.
- Break-its: the folder-not-found case removed (3 red); one unreadable task unanswering the whole read (2 red); the up-to-date compare
  reversed (10+ red); `killTree` ignored (1 red, the real grandchild); the Windows tasks lines dropped from the page (3 red); the guard
  asked beside the tasks in `panelRefresh` (1 red); the stop at "not staged" removed (2 red); the summary compare dropping the principal
  (3 red); the query asking for one task by name (1 red).

**The Opus own review of WR-E2 (2026-10-17)** — two blocking, three major, seven minor findings, all fixed, RED first
(`runnerKillTree.test.ts`, `schedulerAnswer.test.ts`, `windowsTasksState.test.ts`, `windowsTimeGuardFlow.test.ts`, `singleFlight.test.ts`):

- **B1 (blocking) — the tree kill could end a process that was not the runner's running child.** `Run.stop()` can fire after Node saw
  the child `exit` (the ceiling's timer is cleared only on `close`, and a pipe may stay open), by when its pid may be another
  process's; and `taskkill /T` ends whatever the pid names. **What was done: BOTH** — the exit guard AND the family kill rule (pid +
  creation time, own children only), not the guard alone. (1) `endTree` asks for a tree kill ONLY while `child.exitCode === null &&
  child.signalCode === null` and the pid is known; otherwise it makes NO request and the run falls back to the plain `child.kill()`
  (which Node does not send to an exited child). (2) The tree kill is no longer `taskkill`: it is ONE fixed, verifying Windows
  PowerShell started by its absolute path (`process/treeKill.ts`, reusing `powerShellOf`), whose script is constant but for three
  validated integers (the root's pid, this host's pid, this host's start in epoch milliseconds, a little early). It confirms the
  root's `ParentProcessId` is this host and its `CreationDate` is after this host started (exits 3/4/5 otherwise), walks the CIM
  `Win32_Process` descendants keeping a child only when its `CreationDate` is not earlier than its parent's (a reused parent pid
  cannot adopt a stranger; a `seen` set stops a cycle), stops each by id — oldest first, so the root cannot start more — after
  re-reading its creation time and only if it is still the process that was judged, waits 300 ms and READS BACK that none of the
  judged set is left (exit 7 otherwise). Exit 0 alone is "verified ended". It is never `-Name`, never `taskkill`, never an image name.
  **Residual race, for the owner's sign-off:** a process the tree starts AFTER the walk (between the CIM read and the stop) is not in
  the judged set and survives; the root is stopped first to shrink that window, and the read-back covers only the judged set. The
  window between re-reading one pid's creation time and its `Stop-Process` is milliseconds. RED: `Expected values to be strictly equal …`
  for the exited root (a request was made), and after the fake child's `exit` `stop()` made a request.
- **B2 (blocking) — `$channel` was printed LAST**, so the parser filed it under the last task and the channel read `unknown` whenever
  a task existed (the guard's "stop event is not logged" note was lost; the fixtures printed it first, hiding it). It is printed
  right after it is computed, before `stamp=`, and the trailing print and the folder catch's copy are gone. Tests: the script
  prints it once, before the stamp, the folder and the first task; the fixtures print in the script's REAL order; and on the Windows
  leg the real output has `channel=` before `folder=` and none after a task opened. RED: `before stamp=`, and on the real run
  `channel= is printed before folder=`.
- **M1 — a task this account cannot read read as "not installed".** `GetTasks(1)` skips it. Each expected task (`watch`,
  `archive`, `windows-time-guard`, from the constants) the list lacks is now asked for by name: `0x80070002` prints nothing (absent),
  any other refusal prints that task's `read=unreadable` + HRESULT. Tests: the script's loop, and a restricted `watch` beside an
  absent `archive`.
- **M2 — a hung killer left the child unkilled** (the old `child.kill()` ran only on the killer's exit). The killer is bounded
  (`TREE_KILL_BOUND_MS`, 10 s): at the bound it is stopped and the tree counts as not ended; the child is killed plainly right after.
- **M3 — the timeout line always claimed the tree was ended.** A `timedOut` result carries `treeEnded` (true only for the verified
  verdict, false otherwise, absent when no tree kill was asked for); nothing settles before the verdict (bounded); the panel line says
  "its PowerShell and everything it started were ended" only for true and "the extension could not confirm that …" otherwise.
- **m1** a registered task reads "checking…" (not the update warning) while the copy is probed. **m2** ONE table of Task Scheduler's
  results (`SCHEDULER_RESULT`, `SCHEDULER_RESULT_WORDS` in `schedulerAnswer.ts`; the guard's table is it plus its own time-limit
  sentence) and ONE single-flight helper (`shared/singleFlight.ts`, used by the shared read and the probe). **m3** `shared/listeners.ts`
  isolates a listener's fault (the shared read, the host, the guard host), and the guard host's settle of a pending run is caught (the
  store may refuse; the next read settles it). **m4** below. **m5** "(0x41301)" is said only when the last result IS that code
  (`byCode`); a task running by its State alone reads "running". **m6** below. **m7** below.
- **m4 — the guard's parse changes, recorded.** The guard is now read OUT OF the one query's answer by name
  (`parseGuardAnswer(stdout, task = GUARD_NAME)`, `guardStateFromRead`); the old `guard=absent|present|unreadable` lines are gone; a
  missing folder or task is `absent` (the stamp note kept), a folder or task Task Scheduler refuses is `unreadable` with its HRESULT,
  a task whose fields do not parse — including "its definition printed no summary lines" — is `unknown` with the reason (the former
  "answered without the last run"); a never-run guard keeps `lastRunUtc: 'never'` and result `0x41303`. `GuardHostDeps` requires the
  shared `scheduler` (its `queryTimeoutMs` moved to `SchedulerHost`), the guard host's listeners are isolated, and `panelRefresh` no
  longer calls `parts.guard.refresh()` (that would be a second read).
- **m6 — a LIVE GATE for WR-E3/E4.** Nothing of E2 was ever read back from a REAL registration: the golden proves
  `SUMMARY_FUNCTION`'s read-back of the XML in memory, not of a registered task (Task Scheduler normalises `PT60S` to `PT1M`, and the
  principal's name to a SID). WR-E3's acceptance (POST_DEPLOY, the owner's first *Install*) must register `\wsl-care\watch`, read it
  back through `QUERY_SCRIPT`, and require `summariesMatch(registered summary, tasks render's summary)` to be TRUE — the panel line
  reading "installed, … " and not "install again to update" — before E3 is called done; WR-E4 does the same after a run-now
  (`lastResult`, `state`). Added to §9's Definition of Done.
- **m7 — task names are case-insensitive in Task Scheduler.** The query's by-name fallback compares with `-eq` (case-insensitive), but
  the parser keys sections by the exact name and the extension registers lower-case names: a task someone re-created as `Watch` is
  listed, so not fetched by name, and reads "not installed" here. Noted, not handled (the registration is the extension's own).
- **The full run after the review found one more** — the verifying script is a string literal of the shipped bundle, and `bundleScan` forbids the word `root` in any region but the root module: the script named its variable `$root`. It is `$origin` now, with a test that reads the script for every word that scan forbids (break-it: `$root` back → red).
- **Proposed, not done (reuse-first: name it, propose it).** `gpu/gpuHost.ts` (and `bundled/bundledHost.ts`, `archive/archiveHost.ts`)
  carry their own copies of "one in flight, one more when asked meanwhile" and of the listener list; `SingleFlight` and `Listeners` could
  replace them — a separate change in code this story did not otherwise touch, so it is a question for the owner.
- **RED and break-its of the review** (each break-it on PRODUCT code, restored byte for byte, the tree clean after): B1 the exit guard
  removed (2 red); B2 the channel printed last as well (2 red, one the real run); M1 the by-name loop broken (1 red); M2 the hung killer not
  stopped (1 red); M3 the verdict dropped (4 red, the real grandchild test among them); m1 "checking" back to the update warning (1 red);
  m5 "(0x41301)" for every running task (1 red); m3 the settle uncaught (1 red); m3 listeners not isolated (2 red); m2 the single flight
  without its one more run (5 red).

**The coai code round (4fc3f328, 2026-10-18)** — `proceed`, sixteen findings: ten accepted (0, 1, 3, 4, 6, 8, 11, 13, 14, 15), six rejected.
Each accepted fix has its RED message and its break-it (product code only, restored byte for byte, the tree clean after):

- **Findings 1 + 8 + 14 — one peer module (a refactor; the existing tests held it).** The query, its answer's parser, the host that holds
  the read in flight and the canonical summary move to `src/scheduler/` — `queryScript.ts` (the script built from the CALLER's facts and
  expected task names, `NOT_FOUND_HRESULT`, the request), `schedulerAnswer.ts`, `schedulerHost.ts`, `taskSummary.ts`
  (`SUMMARY_FUNCTION`, `durationSeconds`, `summariesMatch`) — and `windowsTime/` and `windowsTasks/` consume it as PEERS: the peer imports
  neither, and neither imports the other (before it, the Windows tasks' state imported `summariesMatch` out of the guard's state, and the
  guard's state imported the parser out of `windowsTasks/`). `guardScripts.ts` keeps what is the guard's — its `channel=` and `stamp=`
  facts and the names to look for — and builds `QUERY_SCRIPT` with `queryScript(...)` (a deviation from "move QUERY_SCRIPT": the
  generic reading moved, the guard's facts stayed; `guardTask.ts`, `guardState.ts` and `guardScripts.ts` re-export what their tests
  import). The query's RUNNER is now handed in from the process layer (`extension.ts`: the real unelevated runner, or the guard
  recorder's new `recordingQuery` in Test mode) to the `SchedulerHost`; `GuardUi.query` is gone, so the shared read no longer
  depends on a surface the guard owns. RED: `schedulerModule.test.ts` (the peer's four files, none importing a consumer, the two
  consumers not importing each other) — `src/scheduler/queryScript.ts is a shipped source` missing and the offenders listed. Break-it: a
  planted `export type … from '../windowsTime/guardTask'` in `schedulerHost.ts` → red.
- **Finding 0 — one panel refresh, one read.** `panelRefresh` asks `parts.scheduler.refresh()` ONCE and `parts.tasks.refresh()`; the latter
  now only probes the staged copy, and the guard host and the tasks host both FOLLOW the scheduler's change — neither starts its own read
  (before it, the tasks host asked the shared read itself, so the one trigger was two call sites of one read). RED: one panel refresh made
  two query requests; the scan of `extension.ts`. Break-its: the tasks host asking the scheduler again → 3 red; the guard asked beside the
  shared read in `panelRefresh` → red.
- **Finding 3 — `Run` completes once.** The child's close, the grace timer and a failure to start share one completion (a flag, and the
  grace timer cleared): a child that closes while the tree kill is in flight no longer gets a second `done` from the grace timer. RED:
  `one completion, not one from the close and one from the grace timer` (the run takes a `graceMs` so the test is fast). Break-its: the
  flag removed (1 red), the flag and the timer's clearing both removed (2 red).
- **Finding 4 — `SingleFlight` keeps a queued run after a failure.** A run that rejects no longer drops the request queued behind it: the
  queue is drained, then the LAST error any run threw rejects the request (a later success does not hide an earlier error — nothing is
  swallowed). RED: `the queued run was not dropped`, and `The input did not match /run 2 failed/`. Break-it: the error rethrown at once → 2 red.
- **Findings 6 + 15 — the bundled exe is hashed once per client.** `bundledDigest()` is a memoised promise (concurrent askers share it, so
  one refresh burst hashes the 13.7 MB exe once); `unavailable` and a failed read are NOT remembered. RED: `one read of the 13.7 MB exe`,
  and the burst of three panel refreshes hashed twice. Break-it: the memo removed → 2 red.
- **Finding 11 — `treeEnded` is a required boolean** on the `timedOut` result (false when no tree kill was asked for, true only for a
  verified one); the tests that build such a result (twenty files) say `treeEnded: false`. RED: the plain-timeout control read
  `absent`, not `false`. Break-it: the verdict forced true → 2 red.
- **Finding 13 — the description of `wslCare.timeouts.windowsTimeGuardQuerySeconds`** says it bounds the ONE Task Scheduler read that
  feeds both the Windows Time guard line and the Windows tasks lines (`numbers.ts`, `package.json`, the test and the docs agree).
  Break-it: the sentence shortened → the manifest test and the numbers test red.
- **Rejected, one line each.** 2 the default-parameter seam is the runner's established one. 5 after the origin exited, the verified walk
  refuses to act — it can spare a process, never end a stranger (the accepted residual of the Opus review's B1). 7 plan §6 specifies
  `NewTask(0).XmlText` — an in-memory definition, nothing registered or read. 9 the digest is already behind the `digestOf` seam
  (`client/fileDigest.ts`) and the client must be the one module naming the exe. 10 the tree-kill script lives in `treeKill.ts`; the real
  defect was finding 3. 12 the PowerShell text stays in TypeScript so the bundle scan and the script-interpolation scan read it.
- **Follow-up filed:** the flaky `runner.test.ts` case "a child past its ceiling…" (a 700 ms ceiling shorter than node's start-up under
  load) is [PLAN_runner_test_ceiling.md](PLAN_runner_test_ceiling.md).

**WR-E3 as built (2026-10-18).** `src_vs_code/src/windowsTasks/tasksFlow.ts` (the flow), `tasksOutcome.ts` (the closed outcomes and their sentences), `tasksPrompts.ts` (the two modals), `tasksGuards.ts` (what is checked of the render), `tasksScripts.ts`, `tasksPending.ts`, `tasksUi.ts` / `tasksRecorder.ts` (the real and the Test-mode surfaces), `tasksHost.ts` / `tasksState.ts` / `tasksView.ts` (widened: one flow at a time, `finishedBy`, the buttons and the waiting line); the peer `scheduler/registerScript.ts` (+ `taskFolder.ts`, `xmlText.ts`), `powershell/unelevatedLauncher.ts`, `shared/readOnlyDocuments.ts`; `panel/messages.ts`, `panelProvider.ts`, `commandButtons.ts`, `media/panel.js`, `extension.ts`, `testApi.ts`, `settings/numbers.ts` + `package.json`; the tripwire's `TASK_RUN` in `test/support/noRealWsl.ts`. The story registers a periodic per-user runner and removes folders; no test starts a PowerShell or touches Task Scheduler (the recorder receives the one request, and the tripwire refuses both shapes). Decisions the plan did not spell out:

- **The unelevated start is direct (reading A of the spec's (g)-1).** `unelevatedRequest` starts the engine's own program — `engine.file`, the verified PowerShell 7 or Windows PowerShell 5.1 — with `-NoProfile -NonInteractive -EncodedCommand <UTF-16LE base64>`, `killTree: true` and the new ceiling `wslCare.timeouts.windowsTasksSeconds` (60, 10–600). One process: the runner's ceiling and tree kill apply, and the child's stderr is READABLE (the elevated road's is not), so a `Register-ScheduledTask` refusal is shown in Task Scheduler's own first line. `-EncodedCommand` because the task XML carries double quotes that `-Command` would carry through `CommandLineToArgvW`; what is shown is byte for byte what PowerShell decodes (asserted). **Known risk, UNMEASURED (§10):** on the owner's machine PowerShell 7 is the Store package whose `pwsh.exe` Node could not `lstat` (ENOENT, 2026-10-11), so its direct start may fail; the flow then says `could not start: <Node's text>` plus a Store note ("install PowerShell 7 (MSI), or install again to try once more") and does NOT fall back to 5.1 (§10: a refusal is shown with its text). The app-execution alias is not an option (a user-writable folder, research G8). If the owner prefers the 5.1 launcher starting it without a verb, the change is confined to `unelevatedRequest` and one test.
- **The registration scripts are the peer's, extracted text-preserving.** `scheduler/registerScript.ts` holds `OP_EXIT`, `IMPORT_SCHEDULED_TASKS`, `HERE_END`, `registerTaskScript(folder, name, xml)`, `deleteTasksLines` and `removeTasksScript`; `Register-ScheduledTask -`, `DeleteTask(`, `CreateFolder(` and `DeleteFolder(` are spelt in that ONE shipped module (`registrationPath.test.ts`; the scan's first word is the cmdlet WITH a parameter, because the guard's and this story's failure sentences name the cmdlet in prose). The guard's `installScript`, `REMOVE_SCRIPT` and `QUERY_SCRIPT` are byte for byte unchanged: their SHA-256 were compared before and after the extraction, and the guard's own tests stayed green unedited. `TASK_FOLDER` and `xmlText` moved to the peer (`windowsTasks/` may not import `windowsTime/`), re-exported by `guardTask.ts` as `GUARD_FOLDER` / `xmlText`; `commandLineLength` and `ELEVATED_COMMAND_LINE_MAX` moved to `unelevatedLauncher.ts`, re-exported by `guardScripts.ts`; the read-only tab is `shared/readOnlyDocuments.ts`, used by the guard's install tab too (its URI is unchanged).
- **The order** (the guard's, with the stage and the render first): busy check (any window's record) → `tasks stage` (ALWAYS; the daemon's `reused` is "not needed"; a busy run lock is waited out `wslCare.windowsTasks.busyRetrySeconds` (3, 1–60) and asked ONCE more, a second busy is told, never looped) → a FRESH `tasks render` (never the probe's cached one) → the checks → the engine → the exact script in a read-only tab (`/windows-<task>-task-install-<n>.ps1`) → the modal → the engine AGAIN → the time → the busy check AGAIN → the pending record → the ONE PowerShell → one read of Task Scheduler and one probe of the copy (detached) → the outcome told. The stage happens before the modal because the modal must name the copy's path and digest; the modal says the copy is already staged, and a decline tells where it is (the one info line) and that *Remove the Windows tasks* deletes it.
- **What is checked of the render** (`tasksGuards.ts`, pure, fail closed, each refusal names the FIELD, never the XML): the read must have answered and its `user=` SID must equal the render's `userSid`, which must have the account shape `S-1-5-21-…` (a task for another account, SYSTEM, LOCAL SERVICE, NETWORK SERVICE are refused); `command` equals the staged file the client started (no case) AND the XML runs exactly it in `<Command>`; no `S-1-5-18/19/20` (matched as whole numbers), `HighestAvailable`, `<GroupId>`, `RunAs`, `<LogonType>` S4U / Password / ServiceAccount, nor the extension folder's path (never when the folder is unknown — an empty string matches everything); EVERY `<LogonType>` is `InteractiveToken`, EVERY `<RunLevel>` is `LeastPrivilege` and EVERY `<UserId>` is the reader's SID (stricter than the spec's "contains": a second principal or trigger account is refused); no line begins with the here-string terminator `'@`; `taskPath` is `\wsl-care\<task>` for the task asked. The registration module refuses a terminator line too (`RangeError`), and a task name or folder outside `^[a-z-]+$`; `installTaskScript` re-checks the name against the closed list at runtime. The XML is the daemon's alone — a scan shows no shipped source of `windowsTasks/` or the registration module spells a task definition.
- **The pending record** (`tasksPending.ts`, key `wslCare.windowsTasks.pending`, `{ op: 'install', task } | { op: 'remove' }` + times). Written BEFORE the launch; cleared on EVERY answer but a timeout — a failed exit, a crash, a refused start, a signalled or flooding PowerShell, and a runner that THROWS (the thrown step is turned into a `failed` outcome so it is told and the record is cleared) — finding 1 of the plan round efc512d4; kept to its deadline only after `timedOut`. The install's deadline is twice the PowerShell's ceiling; the removal's is `2 × (windowsTasksSeconds + 2 × windowsTasksStageSeconds + busyRetrySeconds)` (every step under it). It holds all three buttons in every window and across a reload; `extension.ts` sweeps a record past its deadline at activation; the host settles a record Task Scheduler and the probe already show finished (`finishedBy`: an install when THAT task's row is `installed` — registered, enabled, idle, its summary `summariesMatch`ing the staged copy's render of THAT task, so the watch's summary never settles an archive install — finding 4's test half; a removal when both tasks are `notInstalled` and the probe found no staged copy; never while this window's flow runs).
- **The removal** (`removeTasksScript()`): the two names only — it never names the guard's task, HKLM or a service; `DeleteFolder` only inside the `GetTasks(1).Count -eq 0 -and GetFolders(0).Count -eq 0` guard (exit 32 when it fails: the tasks are gone and the flow CONTINUES, saying the folder could not be removed); exit 31 (a task that would not delete) stops: `tasks stage --remove` is NOT run, because a registered task keeps its copy. After exit 0 or 32 the daemon removes every staged copy (a busy run lock: ONE retry, then "the tasks are removed, but the staged copies were not … press Remove again"); a copy whose exe is executing is kept and named, never forced. The modal holds the script verbatim (no document). **Deviation from the spec's (a):** the "the folder stays" sentence is derived from the read taken at the confirm — it names the other tasks found in the folder ("another task is in it (windows-time-guard; the Windows Time guard's task keeps it)") instead of asserting the guard, because the read after the removal has not answered when the outcome is told (the read is detached, as the guard's); the panel's own line then follows the read.
- **A timeout of the daemon's `tasks stage --remove` is TYPED, and keeps the record.** The client used to type every unanswered daemon call as `failed` with its reason; `RemoveResult` now carries `{ kind: 'timedOut', timeoutMs, treeEnded }` (only this verb: it is the one call made UNDER the pending record that is not the PowerShell). The removal flow turns it into a `timedOut` outcome naming `wslCare.timeouts.windowsTasksStageSeconds`, so the pending record stays to its deadline exactly like the registration PowerShell's own timeout (plan-round finding 1: kept only after a timeout) and every button stays held; `treeEnded` is false (no tree kill is asked of the daemon call) and the sentence says the extension could not confirm it. A first version of this story mapped it to `failed` and cleared the record; the coordinator's review caught the contradiction (RED: `failed: {"kind":"failed","sentence":"the tasks are removed, but the staged copies were not: it did not answer within 60 s"}`; break-it: the early return mapped back, 2 red).
- **The modal's dry sentence names the `dryRun` setting and the Windows side, not the command** — `bundleScan.test.ts` forbids the argv word `config` in every bundle literal (the break-it that spells it is red); the extension's README names the Windows daemon's own `config set dryRun false` (its Marketplace text may not spell the product name, so it says "the exe in the extension's `bin` folder"). The numbers in the modal (`windowsTasks.watchLogonDelaySeconds`, `mcpWatchdog.periodMinutes`, `…runLimitMinutes`, `windowsTasks.archiveLogonDelayMinutes`, `…archiveEveryHours`, `timer.runLimitMinutes`) are read from the render's `numbers[]` by key; a missing key reads "(not reported)", never a guess. Every daemon-supplied value passes `noticeText` (the modal's lines are sanitised one by one — `noticeText` replaces newlines, so the joined text is never passed through it — and every outcome sentence).
- **The buttons** (`TasksAction`: `installWindowsWatchTask`, `installWindowsArchiveTask`, `removeWindowsTasks`; bare ids in `messages.ts`; `button[data-windows-tasks-action]` inside `div[data-windows-tasks]`; commands `wslCare.installWindowsWatchTask` etc., titles ending "(as you, not elevated)…"): none off Windows; all three disabled while a pending record stands (any window) — and the copy line then reads "Waiting for the PowerShell that installs the Windows <task> task…" / "…that removes the Windows tasks…" — and while a flow runs in this window (the modal is open); otherwise the installs are enabled unless the copy is `digestUnreadable` / `checking` / `unavailable`, and *Remove* when any task row says something is registered or the copy is `staged` / `unreadable`. A staged copy with nothing registered keeps *Remove* enabled: it deletes the copy.
- **The tripwire** (`noRealWsl.ts`): `TASK_RUN = /\.Run(?:Ex)?\s*\(/` beside `MUTATING`, over the SAME revealed text (an encoded payload is read too); a task READ (`LastRunTime`, `State`, `Get-ScheduledTask`) stays allowed. Its tests are in `noRealWslTasks.test.ts` (a new file beside `noRealWsl.test.ts`, which still holds the older cases): the real install and remove requests are judged by the pure `refusalOf` (they are refused, so only the recorder can ever receive them in a test), and the one real start a test makes carries a call on a variable nobody set.
- **The Test-mode scenario** (`test/scenarios/windowsTasksFlows.test.ts`) runs one tier below VS Code: the real client over the strict fake daemon, the real host and flow, the product's recorder, and a Task Scheduler read rewritten to show the task registered. The extension-host tier (`test:host`) was NOT run or extended for WR-E3 — decided: the owner's live gate (POST_DEPLOY item 9) covers the real host.
- **`afterRun`** is the HOST's one read of Task Scheduler and one probe, through `this.deps.scheduler.refresh()` and `this.refresh()`, not `parts.*` — so the `panelRefresh` call-site scan of `tasksHost.test.ts` stays green; the guard's `afterRun` is unchanged.

**The story's plan round (coai session `efc512d4`, 2026-10-18).** Two accepted findings became requirements and are built and tested: (1) the pending record is cleared on ANY answer of the registration PowerShell and kept to its deadline only after `timedOut` (the guard's rule); (4) the live acceptance gate covers BOTH installed tasks — the REAL registered `\wsl-care\watch` AND `\wsl-care\archive`, each read back through `QUERY_SCRIPT` and satisfying `summariesMatch(registered summary, tasks render's summary)` (POST_DEPLOY item 9; the unit half is the `finishedBy` settle test). The live press is the owner's: the box in §9 stays open until it is made.

**Open tail of WR-E3 — until the owner's live gate (POST_DEPLOY item 9) is pressed, UNPROVEN:** (1) the REAL registered `\wsl-care\watch` AND `\wsl-care\archive`, read back through `QUERY_SCRIPT`, satisfy `summariesMatch` with `tasks render`'s summary (the unit half only compares the fixtures' read-back with the golden); (2) the Store package's `pwsh.exe` starts from the extension when Node starts it by its package path (UNMEASURED, §10); (3) what `DeleteTask` does to a task instance that is running (UNMEASURED); (4) §10's first risk — the UNELEVATED `Register-ScheduledTask` / `DeleteFolder` inside a `\wsl-care` folder that the Windows Time guard created ELEVATED (the folder's ACL may refuse an ordinary user: likely exit 30 on install or 32 on removal, honest in the outcome, but a possible BLOCKER for the live gate — record what Windows says). The Test-mode scenario runs at node level (the real client, host and flow over the strict fake and a recorder), and `test:host` was not run for E3.

**The Fable own review of WR-E3 (2026-10-19).** The reviewer found NO way to register anything elevated or for another SID, to run anything but the verified staged copy, or to delete what it must not. Fixed, each with its RED test first (messages in `research/module_tests.md`) and a break-it on product code:

- **Major 1 — the tripwire's `TASK_RUN` was case-sensitive** while PowerShell method names are not (`.run(`, `.RUNEX(` slipped through): it gets the `i` flag — a widening only. RED: `TASK_RUN lets $s.GetFolder('x').GetTask('watch').run($null) through`. No break-it: removing the flag would weaken the tripwire (skipped, named); the pure assertions of `noRealWslTasks.test.ts` (plain, mixed-case and encoded) are its teeth.
- **Major 2 — a live record could be settled early from another window.** Window A's install of an already installed task writes the record and starts the PowerShell; window B's refresh saw the row `installed`, cleared A's record and enabled its buttons, so B's *Remove* could run beside A's *Register*. `tasksHost.settlePending` now settles from the read only when `ceilingPassed` (`now >= started + (deadline - started) / 2`, the PowerShell's own ceiling — the deadline is twice it); the early settle stays for the post-timeout half. RED: `window B settled window A's live record`; break-it (the time condition dropped): red. **Open question for the owner, NOT changed:** `windowsTime/guardHost.ts` (the Windows Time guard's host) has the same inherited shape — its `settlePending` clears a record the moment the read shows the task, from any window; the guard's run is elevated and waits for a UAC prompt, so the window is wider. Left alone by this story's order.
- **Minor 3 — `valuesOf` read only attribute-free tags.** It now matches `<Name(?:\s[^>]*)?>`, and `sayingOnly` requires the number of `<Name\b` openings to equal the values found (a `<LogonType xmlns="…">S4U` beside a bare tag, a `<RunLevel/>`, a `<UserId xmlns=…>` are refused). RED: `The input did not match /S4U|interactive token/`; break-its: the attribute match dropped → 1 red (the count still catches the xmlns case — defence in depth; the "harmless attribute" assertion goes red), the count dropped → 1 red.
- **Minor 4 — the action.** Exactly one `<Exec>` and `sayingOnly(xml, 'Command', xmlText(command))` (every `<Command>`, attributes read, equals the staged path) replace the substring check. RED: `The input did not match /one <Exec>|does not run its command/`; break-it (the Exec count dropped): red.
- **Minor 6 — an unknown read no longer claims the folder was removed:** the removal's done sentence says "the folder (when it was empty)" when the read taken at the confirm did not answer. RED: `The input did not match /, the folder \(when it was empty\), and 1 staged copy\./`; break-it: red.
- **Minor 7 — two edges.** `extension.ts`'s activation sweep of the tasks' record has `.catch` + a log line (the guard's own `void sweepPending(…)` line is unchanged; noted); and a `clearPending` that throws in `underRecord` is logged (`deps.log`, new in `TasksFlowDeps` / `TasksHostDeps`) and the outcome is STILL told, the status read again. RED: `Error: the store refused` (the outcome was swallowed); break-it (rethrown): red. The activation catch is not observable in a test (wiring in `extension.ts`).
- **Minor 5 — noted, not fixed:** `registrationPath.test.ts` reads string literals, so a SPLIT spelling of `Register-ScheduledTask` (two concatenated literals, a template built from parts) evades the one-home scan. The scan is a tripwire for a copied script, not a proof against an author who is hiding one; the tripwire of the test process refuses the registration by the decoded text anyway.
- **Minor 8 — in the live gate's open tail above:** the unelevated register / delete inside a folder the guard created elevated.

**Open tail (4) measured read-only (2026-10-19, unelevated, the owner's machine, the guard's folder present):** `GetFolder('\wsl-care')
.GetSecurityDescriptor(0xF)` answers owner `BA` and the ACEs it inherits from the root — `(A;CIID;FA;;;BA)`, `(A;CIID;FA;;;SY)`,
`(A;CIID;FW;;;AU)` (Authenticated Users may WRITE, i.e. create tasks, in the folder) and `(A;OICIIOID;FA;;;CO)` (CREATOR OWNER has
full access to the tasks it created); the root folder carries the same ACEs, and per-user tasks registered unelevated already live
there. So an unelevated `Register-ScheduledTask` into `\wsl-care` and an unelevated `DeleteTask` of the user's own `watch` /
`archive` are ALLOWED by the descriptor — not a blocker. What it does NOT allow: `DeleteFolder` of a folder owned by `BA` (Authenticated
Users have no DELETE right on it). That only arises once the folder is otherwise empty (the guard's task gone too); the remove flow
then answers that the folder could not be removed (exit 32), honestly, with the tasks and the staged copies removed. A folder the
extension itself creates unelevated is owned by the user and removable. Nothing was registered or deleted by the probe; the live gate
still records what Windows answers.
Reading notes (the Fable re-check): `FW` (FILE_GENERIC_WRITE) on a container is "add file" (register a task) + "add subdirectory" +
write attributes — no DELETE and no LIST right; the owner `BA` and `(A;CIID;FA;;;BA)` give the UNELEVATED owner nothing, because a
filtered administrator token holds Administrators as deny-only — which is why `DeleteFolder` of the guard-created folder is refused.
This is a descriptor READING, not a measured registration: Task Scheduler applies its own checks on top. Reproduce with
`GetSecurityDescriptor(0xF)` from an unelevated PowerShell; it answered owner, group and DACL — no `S:` part (the SACL is not
readable unelevated and was left out without an error).

**Follow-ups from the Fable re-check (optional, not gate items):** (1) `ceilingPassed` has no margin: the record's `startedAtUtcMs` is
taken before the store write and the spawn, so the runner's ceiling fires a few ms after `started + timeout` — a refresh in another
window inside that gap could settle while the PowerShell is still alive; add the runner's kill grace as a margin. (2) A `writePending`
that throws BEFORE the launch still escapes `told()`: nothing starts, but the person is told nothing — report it as `notStarted`.

**The coai code round (efc512d4) on WR-E3.** Eight findings accepted and built (`9f0dafee`; the plan round of the same session is above):

- **Findings 1, 2 and 5 (refactor).** `encoded`, `quotablePath`, `powerShellOf` and the command-line bound (renamed `COMMAND_LINE_MAX`, `ELEVATED_COMMAND_LINE_MAX` stays as the guard's alias) moved out of `powershell/elevatedLauncher.ts` into `powershell/commandLine.ts`; `MODULES` into `modulePath.ts`; the engine vocabulary (`Engine`, `NO_ENGINE`, the program lines, the sentence for a changed engine) into `engineTypes.ts`; `EngineRead` / `EngineSource` / `engineProblem` into `engineSource.ts`; `ViewLevel` into `shared/viewLevel.ts` (the panel's view model imported every other part, the GPU's elevated scripts included). The elevated modules re-export what they gave up, so no elevated consumer changed. `unelevatedBoundary.test.ts` follows relative imports TRANSITIVELY from `src/windowsTasks/` and `scheduler/registerScript.ts` and fails if the elevated launcher or the elevated engine module is reachable (RED before the move: `src/scheduler/registerScript.ts → src/powershell/elevatedEngine.ts → src/powershell/elevatedLauncher.ts`, then `src/windowsTasks/tasksView.ts → src/panel/view.ts → src/gpu/gpuState.ts → …`; break-its: `registerScript` importing `MODULES` from the elevated engine again, `unelevatedLauncher` importing `quotablePath` from the elevated launcher again: 1 red each).
- **Finding 4.** The here-string terminator refusal names the terminator (`'@`) in the render's guard and in the registration module's `RangeError`. RED: `the sentence does not name the terminator` / `The input did not match the regular expression /'@/`; break-it (each message without it): 1 red each.
- **Findings 0, 7 and 8 (tests).** `windowsTasksGuards.test.ts` and `windowsTasksHost.test.ts` build their renders through a typed factory (`support/windowsTasks.ts renderAnswerOf`: every field read and checked from the golden, no `as unknown as RenderAnswer`); `windowsTasksFlow.test.ts` types the endings to the runner callback's real type (no `as never`). The two `as never` of `windowsTasksScripts.test.ts` stay on purpose: they pass an INVALID task name to prove the runtime check.
- **Finding 6.** `research/architecture.md` gains *The Windows tasks: install and remove, across the modules*, with its Mermaid flow: panel → `windowsTasks/tasksFlow` → client (stage / render / remove) → the scheduler peer (the one read, the registration scripts) → the unelevated launcher → the runner with the verified tree kill → Task Scheduler.

Rejected, with the reason:

- **3** — the install script's name allow-list (`^[a-z-]+$`, the closed task list) is part of the security boundary; reuse is through the shared script bodies, not through a looser entry point.
- **9** — the race needs two deliberate confirmations within milliseconds; the outcomes are idempotent or honestly read back; and `globalState` has no compare-and-set to close it with.
- **10** — refuted: `tasksView`'s `waitingLine` shows the persisted record in every window, beside the progress notifications.

**WR-E4 as built (2026-10-21).** `src_vs_code/src/scheduler/runScript.ts` (new peer: `runTaskScript`, `RUN_EXIT` 34–37; `registerScript.ts` exports its word check as `taskWord`), `windowsTasks/tasksFlow.ts` (`runNow`), `tasksOutcome.ts`, `tasksPrompts.ts` (`runPrompt`, `RUN_CONFIRM`), `tasksScripts.ts` (`runNowScript`), `tasksPending.ts`, `tasksState.ts` (`finishedBy`), `tasksView.ts`, `panel/messages.ts` / `panelProvider.ts` / `commandButtons.ts`, `extension.ts`, `package.json` (two commands; the ceiling's description widened), `settings/numbers.ts`. The story is the install flow with the stage, the render and the guards taken OUT and ONE read put in: it makes NO daemon call and starts no `wsl-care.exe` — the task's own `Run` starts the registered copy. No test starts a task, a PowerShell or the daemon (the recorder receives the one request; the tripwire refuses the real shape, judged by the pure function). The decisions the plan did not spell out (the spec's (f), as built):

- **A modal before a run-now:** yes, one, with the script verbatim in the detail (the removal's shape, no read-only tab) — the archive task MOVES files once the dry run is off, and the owner's rule is that the exact text is seen before any PowerShell this extension starts. A decline says nothing.
- **The buttons are enabled when the task is not installed** (unless its row is `checking` / `unknown` or a record / a running flow holds them): the PRESS says "install the Windows <task> task first", so the palette command has the same sentence. An `updateNeeded` row may run: the registered task is the person's own and is what Task Scheduler runs at its next trigger anyway.
- **"Already running" is decided twice** and is INFORMATION, never an error (`isFailure` does not list it): from the READ in the flow (State 4, or the result 0x41301 — the panel's own "running" row; no PowerShell, no record) and in the script right before `Run` (exit 35, because the read may be stale; the record is then cleared). (R2)
- **The script** (`runScript.ts`): COM only — connect, `GetFolder` / `GetTask` (not found = exit 34), print `state=`, disabled = exit 36 (a `Run` on a disabled task fails with 0x80041326; the script says why), running = exit 35, then `$task.Run($null)` (a throw = exit 37 with `hresult=0x…` printed), exit 0. No `Import-Module` (no exit 33, no PowerShell 7 module-path risk). `RUN_EXIT` 34–37 never alias `OP_EXIT` 30–33 in `FAILURES[op.kind]`. The folder and the task name are the only variable text, both through `taskWord` (`^[a-z-]+$`) and, for the task, the closed list at runtime (`runNowScript`).
- **One home of the start:** `registrationPath.test.ts` now also finds, over every shipped source's string literals, the tripwire's OWN pattern (`TASK_RUN`, case-insensitive since the Fable review of E3) — in `scheduler/runScript.ts` alone; a planted second spelling (plain, mixed case, over lines) is found, a comment is not. `registerScript.ts` stays the one home of register / delete (E3, rejected finding 3).
- **The exit 37 sentence carries Task Scheduler's HRESULT** from stdout (`tagsOf` of `powershell/taggedAnswer.ts`), then the first stderr line; the HRESULT words apply to ANY exit whose script printed one.
- **One record for install, remove and run** (`wslCare.windowsTasks.pending`; `PendingTasksOp` gains `{ op: 'run', task }`, a task outside the closed list is malformed and swept): all five buttons are held together; the deadline is `started + 2 × ceiling` (the install's rule) and the ceiling is the same `wslCare.timeouts.windowsTasksSeconds` (description widened to "installing, removing or starting now", in `numbers.ts` and `package.json`). `finishedBy` for a run record: the task is `running`, or `installed` with a last run at or after the record's start (less ONE minute — Task Scheduler's second resolution); everything else holds to the deadline, and `settleable` keeps the Fable major 2 rule of E3 (past the PowerShell's own ceiling only). `finishedBy` takes `PendingTasksOp & Partial<PendingTimes>`; without a start time a run record settles only on `running`.
- **No poll** (E2's design): the read runs at the panel's open / visible / Refresh and once after a flow answers; the persisted record holds every window's buttons meanwhile, and each window's next read shows the task running. The line after a run-now always comes from the READ ("installed, running (0x41301)", then its last result), never from the flow's outcome.
- **Refactors for the complexity bound** (behaviour unchanged, the E3 tests green): `tasksOutcome.ts` tables (`PASSING`, `DONE`, `SUBJECT`), `tasksView.ts` `WAITING`, `tasksState.ts` `FINISHED`, `tasksPending.ts` `TASKED`. The E3 tests that listed three buttons now list five (a deliberate widening, not a loosening); `unelevatedBoundary.test.ts` roots gain `runScript.ts` (`>= 10` roots), `schedulerModule.test.ts` lists it among the peer's files.
- **Left untouched, as ordered:** the open follow-ups of E3 (the `ceilingPassed` margin, a `writePending` that throws before the launch).

**The story's plan round (coai session 1c023671).** Two accepted requirements, both built and tested: (R1) the pending record is cleared on ANY answer but a timeout — a `Run` that throws or is refused, a non-zero exit, a runner that throws, a refused start (nine+ endings in a loop; `timedOut` keeps `{ op: 'run', task, startedAtUtcMs, deadlineUtcMs: started + 2 × ceiling }`, a second window is busy and then free at the deadline); (R2) an overlap is never silent (the read before the PowerShell, exit 35 in the script). Rejected, as the plan round decided: an atomic cross-window claim (`globalState` has no compare-and-set; `IgnoreNew` absorbs a double run) and a separate seam for the tripwire.

**Tests of WR-E4 — RED, then green; break-its on product code.** Against the spec's stub (`runNow` answering `notStarted: not built`, `runNowScript` throwing, `finishedBy` false, the buttons without the two ids) 27 of 48 tests of the touched files were red — assertions: `notStarted: {"kind":"notStarted","reason":"not built"}`, `exit 0: not told exactly once`, `The input did not match /install the Windows watch task first/`, `/Start the Windows watch task now/`, `/has not answered what is registered/`, `order:` (the record's place), `expected false, got true` (the held buttons), `wslCare.runWindowsWatchTask: no rendered page button posts 'runWindowsWatchTask'`; the two that read `Error: not built` / `Cannot read properties of undefined (reading 'file')` are the script tests meeting the specified throwing stub. The 20 break-its (`research/module_tests.md`) were each red and restored byte for byte: the state read / the running test removed (1 red), the flow's running pre-check removed (2), the absent pre-check removed (1), the record after the spawn (2), cleared only on done (3), the timedOut exception removed (1), exit 35 → done (1), alreadyRunning as a failure (1), the closed-list check dropped (1), `taskWord` dropped (1), a guard-task line planted in the script (1), `killTree` dropped (1), `-Verb RunAs` planted (2 — the spec also named the tripwire refusal test, which judges the pure `unelevatedRequest` and so cannot see a planted flow argument; the elevation and structure tests catch it), `ceilingPassed` dropped (3), `finishedBy` true for `notInstalled` (1), `held` ignored for the run buttons (7), the HRESULT tag not read (1), a second `.Run($null)` in `tasksScripts.ts` (1), a `tasks stage` call before the run (3), the install's words in the waiting line (1). No break-it touched `noRealWsl.ts`, a scan, a recorder or the strict fake.

**Open tail of WR-E4 — until the owner's live press (POST_DEPLOY item 9, the run-now half), UNPROVEN:** that the REAL `Run` on the registered task starts it under the person's own account (no UAC prompt); that `QUERY_SCRIPT` then reads `state` 4 / `lastResult` 0x41301, and afterwards the real last result (0, or 75 "skipped — another run held the run lock"); that `summariesMatch` stays true after a `Run` (a `Run` does not alter the definition); that a second press while it runs starts nothing (Task Scheduler's history shows ONE instance); the Store `pwsh.exe` start (E3's open item (2)); and whether a `Run` of the archive task while dry only previews. The Test-mode scenario runs at node level (`test:host` not run). The E3 follow-ups listed above stay open.

**The Fable own review of WR-E4 (2026-10-21).** The reviewer found no way to start anything but the person's own task; fixed:

- **Major 1 — a registered task that DIFFERS from this version (`updateNeeded`) could be one registered elsewhere with `RunLevel HighestAvailable` (elevated, no UAC prompt), while the modal says "your account, an interactive token, the least privilege".** The E3 rule "a problem starts nothing" now covers a task that already exists: `presentBlock` refuses, BEFORE "already running", unless the first summary line is exactly `principal=<the reader's SID> logon=3 level=0` (InteractiveToken = 3, LeastPrivilege = 0, pinned by `summaryOfXml.ts`); the sentence names the line it found ("the registered task does not run as you at the least privilege (<line>) — install it again"). RED: `highest available (elevated, no UAC prompt): done: {"kind":"done",…}` (and the foreign-account / SYSTEM / stored-password rows); an `updateNeeded` row whose principal IS ours still runs. Break-its: the check dropped (1 red), the principal judged after "already running" (1 red).
- **The TOCTOU on that check — the authoritative one is IN the script.** The flow checks the principal from the earlier read, but `Run` comes after the modal. `runScript.ts` now reads `$task.Definition.Principal` just before the running test and the `Run`: the UserId translated to a SID exactly as `SUMMARY_FUNCTION` does, compared with `[System.Security.Principal.WindowsIdentity]::GetCurrent().User.Value` (the reader's own SID, the call `queryScript` prints — chosen over a SID passed in as a validated word: nothing variable is added to the script, and the script stays a constant), `[int]LogonType -ne 3` and `[int]RunLevel -ne 0`; any mismatch is the new `RUN_EXIT.notYours` = 38 and nothing is started; `tasksOutcome` says the same sentence and the record is cleared like for any answer. The read-side check stays as the early answer (no modal, no PowerShell). RED: `the script has no $task.Definition.Principal` (presence-checked) and `The input did not match /the registered task does not run as you at the least privilege — install it again/` for exit 38. Break-its: the in-script check removed (1 red), the run-level comparison dropped (1 red), exit 38 not mapped (1 red); each restored and `git diff` checked clean.
- **Minor 1.** The order checks of the script test use `precedesRun`, which asserts the line is PRESENT before comparing positions (an absent line gave `-1 < n` and passed); a test proves the helper refuses an absent line. **Minor 2.** `noRealWslTasks.test.ts` also feeds the tripwire's `refusalOf` the request the FLOW records for a run-now (both tasks), beside the launcher's. **(b)** The settle tolerance for a run record is 5 s, not a minute (Task Scheduler truncates to the second); RED: `installed, ran 30 s before` must read false; break-it: back to 60 s (1 red). **Minor 4.** The README says the run buttons are always there and a press on a task that is not installed says so. **Minor 5.** The not-installed sentence reads "The start of the Windows <task> task could not begin: …" (a run-specific wording; break-it: 1 red).
- **Minor 3 — open item for the owner:** a refusal can carry `0x80131501`, PowerShell's method-invocation wrapper, not Task Scheduler's own HRESULT; unwrapping `InnerException` belongs in the SHARED scripts (the registration's too) and is not done here. POST_DEPLOY says to record the hresult shown.
- **Minor 6 (the re-check) — the authoritative guard refuses BY DEFAULT.** PowerShell goes on with the NEXT statement after a statement-terminating error, so the principal read and compare sitting outside a `try` could fall through to the running test and the `Run`. `runScript.ts` now starts the verdict refused and lifts it only by a completed read: `$ok = $false; try { $p = …; $ok = ($sid -eq $me -and [int]$p.LogonType -eq 3 -and [int]$p.RunLevel -eq 0) } catch { $ok = $false }; if (-not $ok) { exit 38 }`; the running test is the same shape (`$running = $true; try { $running = (…) } catch { …; exit 37 }; if ($running) { exit 35 }`) — a state that cannot be read is **exit 37** (`runFailed`, with the HRESULT printed): an honest "Task Scheduler did not start the task", not a false "already running", and never a fall through to `Run`. The script stays a constant (only the exit numbers are interpolated). RED: `the script has no [int]$p.LogonType -eq 3` against the old shape; the script test now asserts presence-checked: `$ok = $false` precedes the compare, the compare sits INSIDE a `try` whose `catch` refuses, `if (-not $ok)` and `if ($running)` precede `.Run(`, and an unreadable state is exit 37. Break-its (each red, restored, `git status`/`git diff` clean): the verdict starts allowed (1), the compare moved out of the guard (1), the catch no longer refuses (1), `$running = $false` (1), an unreadable state no longer exits 37 (1). A Windows-leg test has PowerShell's PARSER read both scripts (parse only, nothing run).

**The coai code round (1c023671) on WR-E4.** Seven findings accepted and built (`6735cb6c`); one refuted:

- **0** — the new tests derive their cases from `TASK_NAMES` (`RUN_OPS` in `windowsTasksRunNow.test.ts`, the script and tripwire tests), not a second hand-written pair.
- **1** — the principal policy (interactive token = LogonType 3, least privilege = RunLevel 0) is defined once in `scheduler/principalPolicy.ts` (`PRINCIPAL_POLICY`, `principalLine`); `runScript.ts` builds its in-script check from it and the flow's expected principal line from the same module. The scripts' bytes are unchanged by it (the numbers interpolated are 3 and 0); a break-it that changes the policy moves script and flow together and the fixtures catch it (16 red).
- **2** — the run-now modal's closing sentence is precise: "No Windows settings or other tasks are changed." and, for the archive task, "…; the archive task may move session files when the dry line says it acts." (it no longer claims "Nothing else on Windows is changed"). RED: `The input did not match … No Windows settings or other tasks are changed; the archive task may move…`.
- **4** — `tasksState.ts` `FINISHED.remove` takes `(state, _pending)`, the declared signature; the indexed call's `as` in `finishedBy` stays — the compiler still needs it for a call through a mapped-type index.
- **5** — the `RangeError` of `installTaskScript` and `runNowScript` says "is not a valid Windows task name". RED: `The input did not match /"other" is not a valid Windows task name/`.
- **6** — in `runScript.ts` ONLY, every catch that prints an HRESULT unwraps the exception chain first (`$ex = $_.Exception; while ($ex.InnerException) { $ex = $ex.InnerException }; 'hresult=0x{0:X8}' -f $ex.HResult`, exported as `HRESULT_PRINT`). The formatting needs NO `[uint32]` cast: `-f` with `X8` prints an Int32 as its two's-complement eight digits, and a `[uint32]` cast of a negative value would throw — a Windows-leg test throws a `COMException` with `-2147024894` inside an outer exception and reads `hresult=0x80070002` (parse and format only). RED: `a catch prints an HRESULT without unwrapping`. `registerScript` and `queryScript` are untouched (the guard's script bodies stay byte-identical); unwrapping there remains the owner's open item above.
- **7** — the principal read coerces `[string]$p.UserId` first and translates only when it is not empty (an empty or null UserId keeps `$sid = ''`, so the refusal stands, as before). RED: `the script does not coerce the UserId`.
- **3 — refuted:** the pending record IS cleared on a failed outcome (`underRecord` clears on every outcome except `timedOut`); the finding read the code before that line.
- Break-its (each red, restored, `git status`/`git diff` clean): no unwrap (2), a `[uint32]` cast (2), the raw UserId to NTAccount (1), the empty-UserId test removed (1), the modal sentence back (1), the RangeError text back (1), the shared policy changed to RunLevel 1 (16 — the fixtures disagree with the moved policy).

**Follow-up from the merge review of WR-E4 (#170, 2026-10-21):** in `scheduler/runScript.ts` the enabled check (`if (-not $task.Enabled) { exit 36 }`)
sits outside a `try`: the enabled check falls through on an exception (PowerShell continues with the next statement); `Run` then fails as 37 (a
disabled task cannot be started). It does not raise privilege — the principal check after it still refuses by default — but it departs from the
refuse-by-default shape every other check of the script has; to align with refuse-by-default in the next runner story.

**WR-T2 as built (2026-10-21).** `Core/Scheduler/WindowsTask.cs` (`Cleanup`, `All` of three, `DryWindowStamp`), `WindowsTaskDefinitions.cs` (the cleanup's
cadence row and description), `TasksRender.cs` (the elevated guard and `IsUserAccount`), `TaskWorkers.cs` (the third closed-table entry),
`Core/Actions/WindowsTemp/TempPlanning.cs` (new: the shared listing + plan), `TempPreviewReport.cs` + `TempPreviews.cs` (new: the report),
`WindowsTempAction.cs` (`StampName`; `PreviewAsync` on `Planning`), `Core/Config/ConfigKeys.WindowsTasks.cs` / `ConfigKeys.Numbers.cs` /
`default.json` (the two keys) and `NumberRules.cs` (the one rule), `Json/WslCareJsonContext.cs`, `Cli/CommandLine.cs` (`Request.TasksPreview`, the
`tasks preview` row, the usage texts), `Cli/Program.cs`, `Cli/Commands/TasksCommand.cs` (the render passes the elevation and the task's own
stamp), `TasksCommand.Run.cs` (A23's window line), `TasksCommand.Preview.cs` (new: the verb); `contracts/config-keys.json` and
`contracts/golden/windows-tasks-render.json` regenerated (a FOURTH answer, *cleanup from the staged copy*); the extension's
`windowsTasksState.test.ts` counts four golden answers. The docs are in [module_daemon.md](../research/module_daemon.md) § *The Windows
runner's cleanup task and `tasks preview cleanup`* and [module_tests.md](../research/module_tests.md); no new exit code and no new
contract file, so `architecture.md` is unchanged. The plan stays in `todo/`: WR-T3, WR-E5, WR-E6 and S7b.3 are open. The story adds a task
that DELETES (through A23): no test resolves the real `%TEMP%`, `%LOCALAPPDATA%` or home - every path is a fake (`TempWorld`) or under a
`TempRoot` (`SandboxHost`, `ScenarioHome`). Decisions the plan did not spell out, and where this story departs from its spec:

- **The worker is exactly `<self> act A23 --timer --confirm --json`.** `--timer` so A23's own dry week and its `auto.A23` switch gate the run
  (review m9); `--confirm` because the parser refuses an `act` without exactly one mode. The decision (dry or not) lives in the WORKER, on
  A23's own stamp; the table's entry ignores the archive body's `decide`, so the supervisor can start no week (it only PEEKS and says the
  window in one stderr line before `starting`).
- **Exit 75 is KEPT for a busy run** (plan §3.9; the watch and archive tasks; the panel reads `lastResult` 75 as busy). The worker's `act
  --confirm` takes the run lock without waiting, so "a cleanup never runs beside a watch" is the lock's own rule and the supervisor passes the
  75 through. **The coai plan round on the story (session `4663beda`) shows its finding (2) - "map a busy run lock to exit 0" - as ACCEPTED:
  that record is a MISTAKE of the orchestrator, not a decision. It was NOT applied; 75 stays**, held by `A_held_run_lock_makes_the_cleanup_task_answer_busy_75_and_delete_nothing`
  (break-it B19, the mapping implemented, turns it red). Accepted and built: finding (0) - every task's principal is the reader's SID,
  `InteractiveToken`, `LeastPrivilege`, exactly ONE `Principal`, and a non-account SID or an elevated render is not registrable (all three
  tasks); finding (3) - `tasks preview cleanup` bounds its exact paths by `preview.maxItems`. Rejected: a dedicated deadline key
  (`timer.runLimitMinutes` is the cleanup's deadline as it is the archive's), replaced by one `NumberRules` rule.
- **The one rule (`timer.runLimitMinutes` covers twice `temp.scanMaxSeconds` and 60 s)** is enforced but never fires ALONE in a real
  configuration: the older worst-case rule of a timer run (260 min at the defaults) already holds `timer.runLimitMinutes` far above 2 x the
  scan maximum (3600 s). The spec's example (`runLimitMinutes` 60 with `scanMaxSeconds` 3600) is therefore refused by BOTH rules; the boundary
  test (1770 s: 2 x 1770 + 60 = 3600) reads off the new rule's own error text, which is absent there while the older rule still names 60.
- **The render's `dry` for the cleanup task is A23's own week** (`DryRunWindow.Peek(..., stamp: "A23")`), because the week that gates DELETION
  is A23's, not the watch's (the T1 review M3); the watch and the archive keep the common one. `WindowsTask.DryWindowStamp` holds it; the
  literal `A23` lives in `WindowsTempAction.StampName` only.
- **The SID guard is an allow-list**: `S-1-5-21-n-n-n-rid` (local and domain accounts) and `S-1-12-1-n-n-n-n` (Entra ID accounts); SYSTEM, the
  service accounts, groups, service SIDs and the empty SID are refused with a reason naming the SID. The elevated guard comes from
  `host.Privilege.IsRoot`; both answer exit 0 with `registrable: false` and the XML shown (the extension registers only on `registrable: true`).
- **`tasks preview cleanup` runs in-process and is bounded by A23's own budgets** (`temp.scanMaxSeconds`, `temp.scanMaxNodes`,
  `temp.maxTopLevelEntries`); the extension's `windowsTempPreviewSeconds` (WR-T3) is the outer net. Refusals: the distro's binary (2), an
  elevated process (81, with its own reason - the Administrators-owned entries an elevated process sees; reworded in the own review below). A configuration in error does NOT refuse it: the report says `observeOnly` with the
  errors (`act --preview` does not refuse either; `tasks render` keeps its 78 because its answer would be registered). Its trigger is
  `RunTrigger.Cli`, so the names the allow-list does not cover are scanned for the "would be eligible if allowed" groups. It takes no lock, not
  even for a read; it PEEKS A23's week and never decides it.
- **Reuse-first extraction:** `WindowsTempAction.PreviewRoot` became `Planning.Of(context, settings, token)` (`Planned` | `Refused` | `Unreadable`),
  the ONE call site of the top-level listing; the engine's preview and the verb's report are two shapes of the same plan (no second selection
  code). `RunningSupervisor` moved from the archive body's tests to `Cli.Tests/RunningSupervisor.cs`; the sandbox tree helper
  (`TempTrees.Make`, write AND creation times) is in `TestSupport` and serves the engine tests, the scenarios and the new classes.
- **The report's shape** (`TempPreviewReport`, `schemaVersion` 1): `pass.*` is the cursor ON DISK (where the next run starts: `last`,
  `inProgress`), `total` the names now; `groups[]` carry `verdict`, `pattern`, `count`, `bytes`, `paths` (at most `preview.maxItems`) and `more`;
  `groupsNotShown` counts the groups past the cap per verdict. `maxPathsPerGroup` is read from the configuration (`preview.maxItems`), not
  passed in (the spec's signature took it as a parameter). Exact paths are NOT run through `ShownText.Printable` in JSON, as `ActionItem.Name` is not.
- **No golden for the preview JSON** (the spec's D7): a golden built from pure parts would need an in-memory `%TEMP%` in `Scenarios`; its
  shape is pinned by `TasksPreviewCommandTests` and `WindowsTempPreviewTests`. **WR-T3 should write its fixture from the daemon's real output.**
- **The text form's control-character test (spec T-P12's last clause) is not written**: NTFS refuses file names with control characters, so
  no sandbox tree can carry one; every line of the text form goes through `ShownText.Printable`, the one function `ControlCharacterTests` holds.
- **A scenario's "writes nothing" snapshot leaves out the CLI's own state folder** (`AppData\Local\wsl-care`): every run of the built binary keeps
  its per-run log there (the logging rule). Files are compared by length and write time, folders by name; the absence of `temp-cursor.json` and
  of every `first-timer-run*.json` is asserted separately. The in-process tests have no log and compare everything.
- **Clocks:** the cleanup-body tests use `TimeProvider.System` because the trees' times are real (a fixed 2026-10-02 clock would make "young" files
  older than the cutoff as the date moves). The tests that need the current account use the owner of what they create as the SID (an elevated
  CI runner creates Administrators-owned files); the two scenarios that need an unelevated child take the elevated leg (81) on such a runner.
- **The extension:** the render golden gained an answer, so `windowsTasksState.test.ts` (which counted three golden answers) counts four; its
  fake picks entries by `name`, so nothing else moved. `npm ci`, `compile`, `bundle` and the whole suite (1598 tests, 0 failed) ran in this worktree.
- **For WR-T3:** `TASK_NAMES` gains `cleanup`; the preview argv is `tasks preview cleanup --json` on the STAGED exe under its own ceiling; the
  install modal names A23, the allow-list, the age, `never %TEMP%\claude`, the cadence (30 min after logon, daily), the deadline, A23's own dry
  week (the render's `dry`) and that `lastResult` 75 means "busy - a watch was sampling; the next run retries". The run-now button is the task's
  own `Run` (`taskWord` must accept `cleanup`).
- **RED evidence** (each against the unfixed product, an assertion or the table's own refusal, never a compile error): the keys -
  `Expected key to be ConfigKey+IntKey ... but found <null>`; the closed set - `Expected WindowsTask.All.Select(t => t.TaskPath) to be equal to
  {..., "\wsl-care\cleanup"}, but {...} contains 1 item(s) less`; the render - `ArgumentOutOfRangeException: not a task of this plan` and, for
  the guards, `Expected answer["registrable"]!.GetValue<bool>() to be False`; the supervisor entry - `ArgumentOutOfRangeException: not a task of
  this plan; one of: watch, archive, cleanup`; the preview verb - `Expected exit to be 81, but found 0` (the stub), `JsonReaderException: The input
  does not contain any JSON tokens`; the report - `Expected report.MaxPathsPerGroup to be 5, but found 0`, `Expected report.Refusal "" to contain
  "different folders"`, `Expected report.Counts.Eligible to be 2L ... but found 0L`; the contracts - `contracts/config-keys.json must be what the
  types enumerate`.
- **Break-its** (product code only, sandboxed; each restored with `cmp`, `git diff` clean after every one): B1 no `--timer` (5 red); B2 `--manual` (4);
  B3 the watch's deadline key (3); B4 the entry calling `decide` (5); B5 the elevated guard removed (2); B6 the SID shape removed (5); B7 the
  archive's cadence keys (3); B8 the render's common stamp (1); B9 `Decide` in the supervisor's line (1 - the elevated-supervisor test; the busy
  test cannot see it because the planted stamp already exists); B10 the preview writing the cursor (3); B11 the preview deciding the week (3);
  B12 the preview taking a lock (2); B13 the preview's trigger Timer (2); B14 the path cap removed, and `more` fixed at 0 (1 each); B15 the group
  cap removed (1); B16 the number rule removed (1); B17 the parser accepting any task (2); B18 the preview's elevated refusal removed (2); B19 the
  busy 75 mapped to 0 (1). B20 (one listing call site) is reviewed: `ListEntries(root` appears once in `Core/Actions/WindowsTemp`, in `TempPlanning.cs`.

**WR-T2, the Fable own review (2026-10-21).** Seven fixes on top of the first delivery; each behaviour change red first, then green, then a break-it.

- **(1) The preview is faithful to the task.** The counts are those of the Timer plan (`TempPreviews.Report` plans with the Timer trigger, so names the
  allow-list does not cover are not scanned and not eligible); a second plan under the Cli trigger fills ONLY `wouldBeEligible`.
  `TempPreviewReport.WouldBeEligibleLimited` is true (the text says it) when that second scan ran out of its budget, so the list is a lower bound.
  Both plans are read-only. RED: `Expected report.Counts.Eligible to be 50L because the preview says what the task will do, but found 0L`.
- **(2) The rule is a minimum, not a cap.** The `timer.runLimitMinutes` rule's comment and error text now say it is the MINIMUM needed to delete anything
  (the plan scan, the top-level listing included, plus one re-validation, both one allowance of `temp.scanMaxSeconds`) and that it does not bound the delete
  walk. The reviewer's premise ("`TempRun` gives every candidate a fresh budget") was already stale - WR-T1 m6 shares the re-validation allowance - so only the
  wording moved; the behaviour is unchanged. §3.12 "Budgets on every run" says the same.
- **(3) Stale "two tasks" texts** (usage, summaries, the `TasksRender`, `WindowsTask`, `WindowsTaskDefinitions` and `ConfigKeys.WindowsTasks` comments) now name
  watch, archive and cleanup.
- **(4) `tasks run` refuses an elevated process before the sweep, for all three tasks** (81, "run it from a terminal that is not elevated. Nothing was started").
  RED: `Expected exit to be 81, but found 0 (difference of -81)` for watch, archive and cleanup. The workers still refuse themselves; the supervisor now never
  sweeps the staging folders, peeks a window or starts a job as an administrator. **Consequence for CI:** the scenarios that supervise a task on an elevated
  runner assert 81.
- **(5) Docs:** with UAC off (or the built-in Administrator) every process is elevated, so the `render` is NEVER registrable for that account and `run` and
  `preview` answer 81 - WR-T3's modal and the panel's cleanup row must say so (§3.x "Elevated" bullet; module_daemon.md).
- **(6) The preview's elevated message** explains why: an elevated process sees entries owned by the Administrators group, so its picture of what the cleanup
  would delete as the user would be misleading; it no longer mentions `act`.
- **(7)** A comment at `ActionEngine.RunStamp`: a mixed A23 + other batch falls back to the common stamp; unreachable on Windows today.
- **Break-its** (product only, sandboxed, `cmp`-restored, `git diff` clean after each): N1 the supervisor's elevated refusal removed (6 red); N2 the preview's
  counts planned with the Cli trigger (1); N3 the `wouldBeEligible` plan dropped (2); N4 the limited flag fixed false (2); N5 the old elevated message (1);
  N6 the refusal after the sweep (5).

**WR-T2 own review, item 8 - the recursive delete is a closed list (2026-10-21).** `IFileSystem.DeleteDirectory` (the recursive one,
`PhysicalFileSystem.cs:624`) is pinned by `ArchitectureTests.RecursiveDelete.cs` (`Core.Tests`, the `ReadSites` style, source-reading):
(a) no file under `Core/Actions/WindowsTemp/` spells `DeleteDirectory(` (so `DeleteEmptyDirectory(` stays the only folder removal there -
`TempDeletion.cs:141`); (b) the src files that call `.DeleteDirectory(` are exactly `ArchitectureTests.RecursiveDeleters`, and a new caller goes red with a
message that it must be justified (what, under which root, why a per-file delete does not do) and recorded (that table, this section,
`module_daemon.md`). The scanner is proven on a planted fixture first (`..._scan_matches_a_planted_call_and_not_the_empty_folder_removal`: a one-line
and a multi-line call match, `DeleteEmptyDirectory(`, a `//` comment and a declaration do not match as a member call, and the real
`RunRetention.cs` is found). Break-its (product code, `cmp`-restored, `git diff` clean): (1) `TempDeletion.cs:141` `files.DeleteEmptyDirectory(path, scope)` ->
`files.DeleteDirectory(path, scope)`: BOTH red - (a) `offenders ... {"...\WindowsTemp\TempDeletion.cs:141: DeleteDirectory("}` and (b) `... a NEW
caller of the recursive IFileSystem.DeleteDirectory`; (2) a method calling it in a fifth file (`Scheduler/StagedCopies.cs`): (b) only red -
`found at least one item {"WslCare.Core/Scheduler/StagedCopies.cs: a NEW caller of the recursive IFileSystem.DeleteDirectory"}`.

The four callers, and which of them can run inside an `act A23 --timer` process (the scheduled cleanup's worker, `tasks run cleanup`):

| Caller | Runs in the A23 worker? | Root it deletes under |
|---|---|---|
| `Cli/Logging/LogRetention.cs:70` (`PruneOne`), reached from `WslCareLogging.cs:56,94` | **YES** - the run-log prune runs at the start of EVERY CLI process that opens its file sink, so the worker prunes aged day folders before `act` starts | `DeletionScope(logRoot, ...)` (`LogRetention.cs:41`): `host.Paths.LogDirectory` = `%LOCALAPPDATA%\wsl-care\logs` (`WindowsHostPaths.cs:53`) |
| `Core/Records/RunRetention.cs:102,119` | no - only `collect` (`CollectRun.cs:381`) and the Windows `watch` (`WatchRun.Windows.cs:63`); `act` never sweeps | `RunDetailStore.Root(paths)` (`RunRetention.cs:38`) and `paths.StateDirectory` (`:70`) = `%LOCALAPPDATA%\wsl-care` (`WindowsHostPaths.cs:51`) |
| `Core/Scheduler/Staging.Sweep.cs:174` | no - the SUPERVISOR's start sweep (`TasksCommand.Run.cs:43,67`, after the elevated refusal), not the worker it launches | the staging root `StagedCopies.Root` (`StagedCopies.cs:37`), under the state directory |
| `Core/Actions/UserCaches/CacheFolders.cs:94` | no - the Linux user-cache actions; A23's `act` runs A23 alone | the action's own cache root passed in (`RemoveFolder(context, folder, root, action)`) |

None of them touches `%TEMP%`: the one that can run in the cleanup's process deletes only the daemon's own log day folders.

**WR-T2, the code round 4663beda: proceed (2026-10-21), and the last fix cycle.** The coai code round passed (`proceed`) and the Fable re-check said SHIP.
Accepted and built: findings 1 and 8, 2, 6 and 10; the Fable minors i, ii, iii and iv. Rejected, each with its reason: **0, 3 and 5** - the closed
task-worker binding is by design the one place that names a task's argv, and the literal is unified under finding 2; **4** - the preview is bounded by
ONE allowance after finding 1, and no other read verb prints progress; **7** - one previewable task by plan; **9** - in-memory passes over a bounded
plan, no measured cost.

- **(1) One listing, one budget.** `tasks preview cleanup` lists the top level ONCE (`Planning.OfTaskThenPerson`; `ListEntries(root` is still at its one call
  site, `TempPlanning.ListRoot`) and makes two plans over ONE `TempBudget`: the Timer plan first (what the task will do; it has the budget and the
  priority), then the person's plan for `wouldBeEligible` on what is left. The cost of a preview is therefore ONE `temp.scanMaxSeconds` allowance plus
  the listing (the listing's time is already spent from the same budget), not two. `WouldBeEligibleLimited` is `TempPlan.BudgetStopped` of the
  second plan only: a name deferred because the SCAN BUDGET ran out; reaching `temp.maxItemsPerRun` is not that and is not flagged (it is in
  `counts.deferred`, as it always was). **For WR-T3:** `windowsTempPreviewSeconds` must cover one `temp.scanMaxSeconds` allowance plus the top-level listing
  (and the process start), not twice the allowance.
  RED: `Expected _world.Listed.Count(path => PathRules.Windows.PathEquals(path, TempWorld.Root)) to be 1 because the Timer plan and the person-trigger plan read the same listing, but found 2.`;
  `Expected report.Counts.WouldBeEligible to be less than 75L because after the task's plan the second plan has only what is left of the SAME budget, but found 150L`;
  `Expected report.WouldBeEligibleLimited to be False because the scan budget was nowhere near exhausted: the cap is not the budget, but found True.`
- **(2) One constant for A23's id:** `WindowsTempAction.IdText` is the action's `Id`, its `StampName`, the task worker's argv entry, `WindowsTask.Cleanup`'s
  stamp, and the deletion scope's action name in `TempRun` and `TempCursor`. A pure refactor: the argv tests and the golden pass unchanged.
- **(3) The triggers are explicit:** `TempPreviews.Report` sets BOTH (Timer for the task's plan, Cli for the person's) whatever its context carries, and
  `TasksCommand.Previewed` says so; the stale "shows MORE than it would delete" comment is gone. No behaviour change.
- **(4)** The preview's elevated refusal ends "Nothing was read." (pinned in `TasksPreviewCommandTests`).
- **(5)** `ArchitectureTests.RecursiveDelete` catches a METHOD GROUP too: `\.\s*DeleteDirectory(?!\w)` and `(?<!\w)DeleteDirectory(?!\w)`; the planted
  fixture gained `Remove(files.DeleteDirectory);`. No `nameof` or doc mention in src matches (doc lines are `//` comments, which the scanner drops; both
  tests pass over the real tree). `DeleteEmptyDirectory` still does not match.
- **Break-its** (product code, `cmp`-restored, `git diff` clean after each): M1 the person's plan with a fresh budget (1 red: the shared-budget test);
  M2 the flag from `Deferred > 0` (1: the cap test); M3 the cap counted as a budget stop (1: the cap test); M4 the person's plan re-listing (2: the one-listing
  and the shared-budget tests); M5 the person's plan first (2: the shared-budget test and the Timer-counts test); M6 the argv literal changed (2:
  `TaskWorkersTests` cleanup worker, json and not); M7 the period dropped (1: `An_elevated_process_is_refused_before_anything_is_read`); M8 the OLD regexes in the
  scanner (1: `Expected RecursiveDeleteMemberCall()... to be equal to {1, 3, 5}, but {1, 3} contains 1 item(s) less.`).

**WR-T2, the CI lesson (2026-10-21, PR #172).** The Windows CI runner is ELEVATED, and the render tests in `TasksCommandTests` built their host with the real
process privilege, so 11 of them (the registrable cases, the SID-reason cases and the text answer) read "an elevated process" and failed there while passing on an
unelevated machine. Reproduced locally first by making `CliHost.Privilege`'s default elevated (the same 11 red, `Expected answer["registrable"]!.GetValue<bool>()
to be True because an elevated process: the tasks run as the user and are registered unelevated; ... but found False.`); fixed in the TEST (`WindowsWorld.Host`
pins `ProcessPrivilege(false, ...)`; the elevated render test overrides it), no product change. Re-run under the same simulation: Cli.Tests 521, 0 failed.
Core.Tests read no process privilege (fake SIDs, `TempWorld`), and the Scenarios branch on `Environment.IsPrivilegedProcess` (skip or the 81 leg). **A render test
must pin its privilege, because the Windows CI runner is elevated.**
