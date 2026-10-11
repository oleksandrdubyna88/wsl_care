# PLAN — the Windows runner: two per-user scheduled tasks run the staged `wsl-care.exe` under one bounded supervisor

> Status: **plan only, nothing implemented yet, 2026-10-11.** Scope: the `wsl-care` daemon (`src_daemon/`: a `tasks` verb family — `render`, `stage`, `run` — a Windows branch of `watch`, one new exit code, new `windowsTasks.*` keys) and the VS Code extension (`src_vs_code/`: a `windowsTasks/` module, the widened `WindowsCareClient`, five panel buttons, settings, the Archive page's Windows side). Nothing elevated, nothing as SYSTEM.
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

**`tasks run <watch|archive> [--json]`** — the supervisor (§3.3). The task's action and the buttons' "run now" both reach it. It starts `<self> watch --timer --json` or the archive body (§3.5) as a child in the job. The supervisor spells `--timer`; the extension never does (structure.test.ts:182).

**`watch` on the Windows binary** (today refused, `WatchCommand.cs:50–52`): a Windows branch of `WatchRun` — refuse an ELEVATED process (the opposite of the distro's root check, `ProcessPrivilege.cs:30–33`; S7b item 4's per-side gate; the wording of `ArchiveRunCommand.ElevatedRefusal`, :27–28; exit 81 `NotAsRoot`, `ExitCode.cs:79`); take the run lock without waiting (exit 75 is normal); sample the Windows MCP collector with `McpCpuLedgerPlace.ForWindowsStatus(windows, elevated: false)` (`McpCpuLedger.cs:86–87`, `StatusCommand.cs:70–72`) so the ledger advances every period; invoke `RunRetention.Sweep` (today only `collect`'s, `CollectRun.cs:381`); then — only with `--timer`, not observe-only, under `DryRunWindow.Decide(RunTrigger.Timer, …)` on the Windows paths — the A22 act when S7b.3 has built it. **Until A22 lands the Windows watch samples only** and writes no history line (S2b's rule, PLAN_twenty_sessions_all_day.md:326–327). The boundary with S7b.3 is in the table at the end of §3.

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
| the panel's `status` and the watch task both write the ledger | both atomic private writes of the two-point rule (`McpCpuLedger.cs:243–287`); the last wins, no baseline is lost |
| an upgrade during a run | a new `<digest>` folder; the tasks re-registered to it; the executing old image keeps its folder until it exits; swept later |
| sleep / resume | the repetition fires at its next interval; the supervisor's deadline on unbiased time; the run lock is an open handle, released by the OS |
| VS Code closed | the tasks run; the panel re-reads on open; nothing of the window is a source of truth |
| two VS Code windows | one pending record in `globalState`; a second press reads `busy` |

### 3.10 Growth — before the first write

| surface | projected size | retired by | interrupted |
|---|---|---|---|
| `%LOCALAPPDATA%\wsl-care\bin\<digest>\` | ≤ 2 × 13.7 MB (current + one executing older) | `tasks stage`'s sweep; *Remove* | a half-copied folder has no `stage.json` → swept as unverified |
| `stage.json` | ~200 B | overwritten atomically | — |
| run logs `logs\<day>\` | 288 watch runs/day × ~1 KB ≈ 0.3 MB/day | `LogRetention.Prune` at every start (`WslCareLogging.cs:94`, `logging.retentionDays` 14) ≈ 5 MB | — |
| `mcp-cpu.json` | ≤ `mcpServers.maxInstances` × 2 points, under `records.maxStateFileBytes` | the ledger's own rule | its temp files swept (`McpCpuLedger.cs:295–304`) |
| `history.jsonl`, `runs/` | 0 until A22; then S7b item 7's ≤ 5 MB/day, ≤ 70 MB | `RunRetention.Sweep`, now invoked by the Windows watch | the engine's reconcile |
| the archive's state and index | the archive plan's own (D11: kept forever by decision) | — | the archive's reconcile |

### 3.11 Not in scope

Elevated work and `collect-elevated` (PLAN_windows_care §2, §8a); logged-out runs (an `S4U`/password principal); E12's `%TEMP%` cleanup and every other W-A action; A22's stop logic (S7b.3 — this plan gives it the verb and the cadence); the WSL→Windows `tar` hand-off; Authenticode signing; the Logs page's Windows side before A22.

**Boundary table (to be written into PLAN_twenty_sessions_all_day.md S7b.3, PLAN_windows_care.md E11, PLAN_wsl_care_daemon.md D9 and PLAN_bundle_windows_binary.md §4 — "named on both sides"):**

| item | this plan | the other plan |
|---|---|---|
| the Windows `watch` verb, its lock, ledger write, retention sweep, dry gate | here (WR-D4) | S7b.3 adds the A22 act inside the `--timer` branch |
| the Windows watch cadence and runner | here (the `\wsl-care\watch` task) | S7b.3 item 5's "extension timer" assumption is superseded |
| the staged copy and its path | here | bundle plan §4: "never register its path" stays true of the EXTENSION folder |
| `\wsl-care\collect` (4-hourly Windows collect), elevated tasks | — | E11 (PLAN_windows_care) |
| W-A15's schedule | here (the `\wsl-care\archive` task) | the archive plan keeps the verbs; D9's "until E11" clause is closed by this plan |

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
| WR-D2 | `tasks stage`: copy, verify, `stage.json`, sweep | `Core/Scheduler/Staging.cs` (new), `TasksCommand.cs` | Core.Tests over `WSL_CARE_ROOT`; Scenarios: the built binary stages into a sandbox root | **yes** (deletes folders) | D1 |
| WR-D3 | `tasks run`: the Job Object supervisor, exit 84, the scan widened | `Core/Processes/JobSupervisor.cs` (new), `IWorkerSupervisor`, `Cli/ExitCode.cs`, `contracts/exit-codes.json`, `tests/…/ArchitectureTests.cs` | Core.Tests with a fake; Windows leg: child + grandchild timeout on the test's OWN helper process | **yes** (ends processes) | D1 |
| WR-D4 | the Windows `watch`: elevated refused, lock, ledger, retention sweep, the A22 hook | `Core/Watch/WatchRun.cs`, `Cli/Commands/WatchCommand.cs` | Core.Tests; a Windows flow in Scenarios | **yes** (retention deletes details) | D3 |
| WR-D5 | the archive body: dry → preview, real → `archive run`, no run lock | `TasksCommand.cs`, `Core/Scheduler/ArchiveBody.cs` (new) | Core.Tests; Scenarios on Windows with the fake side | **yes** (moves files) | D3 |
| WR-E1 | `WindowsCareClient` widened; the fake learns the staged exe and `tasks`; reads | `src/client/WindowsCareClient.ts`, `verbs.ts`, `test/fake/fakeWsl.ts`, `test/structure.test.ts` | client.test, fakeWsl.test, structure | no | D1–D2 (answers) |
| WR-E2 | one Task Scheduler read for the folder; `windowsTasks/` state + view; settings; read-only panel lines | `src/windowsTime/guardScripts.ts`, `guardState.ts`, `src/windowsTasks/schedulerHost.ts`, `tasksState.ts`, `tasksView.ts`, `settings/numbers.ts`, `package.json`, `panel/*` | unit; page harness; Windows leg parses the golden XML with `NewTask(0).XmlText` and compares `SUMMARY_FUNCTION`'s read-back to the golden's summary | no | E1 |
| WR-E3 | install / remove flows, pending records, buttons, commands | `src/windowsTasks/tasksFlow.ts`, `tasksHost.ts`, `tasksPending.ts`, `tasksUi.ts`, `tasksRecorder.ts`, `panel/commandButtons.ts`, `extension.ts`, `test/support/noRealWsl.ts` (the tripwire gains `.Run(`/`RunEx(` on a task object) | flow tests with recorders; tripwire tests | **yes** (removes folders; registers a periodic runner) | E2, D2 |
| WR-E4 | run-now buttons through the task's own `Run`, "running" state | `tasksFlow.ts`, `tasksHost.ts`, `schedulerHost.ts` | flow + page | **yes** | E3 |
| WR-E5 | the Archive page's Windows side | `src/archivePage/*`, `archive/archiveHost.ts`, `media/archive.js` | archivePage tests; scenarios | no | E1, D5 |
| WR-E6 | the Logs page's Windows side (after A22) | `src/logsPage/*` | logsPage tests | no | E1, S7b.3 |
| WR-X1 | docs, POST_DEPLOY acceptance item, the boundary rows in four plans | `research/module_daemon.md`, `module_vs_code.md`, `module_archive.md`, `module_tests.md`, `architecture.md`, `POST_DEPLOY.md`, the four plans, `todo/README.md` | plan-lifecycle | no | lands with D1 and E3 |

## 6. Tests

**RED first (named before the product code):**
- D1: `The_render_refuses_a_copy_outside_the_staging_root`; `The_render_names_the_user_sid_and_the_staged_path`; `The_watch_task_repeats_every_period_minutes_and_the_archive_every_n_hours`; `Peek_never_writes_the_stamp`; `A_period_at_or_above_the_interval_maximum_breaks_the_rule` (exists; held); `The_render_is_refused_on_the_linux_binary`.
- D2: `A_stage_copies_byte_for_byte_and_records_version_and_digest`; `A_stage_into_an_existing_digest_folder_never_overwrites`; `An_older_folder_is_swept_and_an_executing_one_is_kept_and_named`; `A_mismatched_read_back_leaves_no_stage_json`.
- D3: `The_worker_is_created_suspended_and_resumed_only_inside_the_job`; `A_failed_assign_terminates_the_suspended_worker`; `The_job_handle_is_not_inheritable`; `A_child_and_its_grandchild_are_gone_after_the_deadline` (Windows leg, the test's own helper process); `The_deadline_counts_unbiased_time`; `The_scan_finds_TerminateJobObject_outside_the_supervisor`; `Free_argv_is_refused`.
- D4: `An_elevated_windows_watch_is_refused_as_not_as_root`; `A_busy_lock_exits_75_as_normal`; `The_windows_watch_writes_the_ledger_and_no_history_line`; `The_windows_watch_sweeps_run_retention`; `Without_A22_the_timer_branch_acts_on_nothing`.
- D5: `A_dry_archive_body_runs_the_preview_and_moves_nothing`; `A_real_archive_body_runs_archive_run`; `The_archive_body_never_takes_the_run_lock`; `A_second_archive_is_refused_by_the_lease_naming_the_holder`.
- E1: `the staged exe is named only by WindowsCareClient`; `the fake answers tasks and refuses unknown argv`; `the extension never spells --timer`.
- E2: `one query reads every task under \wsl-care`; `each state row renders`; `the Windows leg reads back the golden XML as the golden summary`.
- E3: `the order is stage → render → show → modal → pending → one unelevated PowerShell → read`; `a digest mismatch stages nothing and registers nothing`; `a render that is not registrable registers nothing`; `a decline runs nothing`; `a pending run holds every button across a reload`; `the tripwire refuses Register-ScheduledTask and a task's Run(`; `the remove keeps the folder while the guard's task is in it`.
- E4: `run-now starts the person's own task and nothing else`; `a running task reads running in every window`; `not installed → install first`.
- E5/E6: `the Windows side's archive status and list render`; `the Logs page reads the Windows history`.

**Break-it on PRODUCT code only (each red, then restored):** the job's kill-on-close flag off; the assign failure ignored (worker resumed anyway); the job handle made inheritable; the deadline on wall time; the registrable check removed from `render`; the digest compare removed from the stage; the run lock taken in the archive body; the dry decision ignored in the archive body; `--timer` spelled in the extension; the summary compare dropping the principal; the pending record written after the launch; the remove deleting the folder with the guard's task in it; the query reading one task only.

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
8. Docs land in each PR; the boundary rows with D1 and E3; promote this plan when E5 ships (E6 stays an open tail named in the status line).

## 9. Definition of Done

- [ ] Every RED test seen red for the right reason, then green; every break-it red on product code, then restored.
- [ ] The child + grandchild timeout test green on the Windows leg; no test registers, runs or deletes a real task or starts a real `wsl-care.exe`.
- [ ] `contracts/config-keys.json`, `contracts/exit-codes.json`, the render golden regenerated; `plan-lifecycle`, lint, typecheck, the daemon's Windows suites and CI's two legs green.
- [ ] The owner's acceptance (POST_DEPLOY): *Install the Windows watch task* registers `\wsl-care\watch` unelevated; Task Scheduler's first run exits 0 and the ledger advances; *Remove* leaves no task, no folder (when empty) and no staging folder; a blocked start, if any, is shown with Windows' own code.
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
