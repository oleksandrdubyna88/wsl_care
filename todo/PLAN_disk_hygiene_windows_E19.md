# PLAN — E19 "See it": the stories of the first disk-hygiene epic (volume verdicts, the 5-minute Windows reading, the worktree report)

> Status: **in progress, 2026-10-15 — E19.S2b built (PR pending); the other stories not started.** Scope: the stories of epic E19 of
> [PLAN_disk_hygiene_windows.md](PLAN_disk_hygiene_windows.md) (DH-1, DH-1b, DH-3) — read-only, nothing in this epic
> deletes. Written by the Opus judge per `.agents/conventions/common/subagent-models.md` (epic → stories); no story of E19
> is a cornerstone (the parent's ledger: `Cornerstones: 2 of 2 — DH-4, DH-6`, both outside this epic).
>
> Parent: [PLAN_disk_hygiene_windows.md](PLAN_disk_hygiene_windows.md) § *Epics* — E19. Runner plan:
> [PLAN_windows_runner.md](PLAN_windows_runner.md) (WR-D4 built 2026-10-14, #144; WR-E1 not built).

## 1. What this epic must make true

The owner is warned when any lettered Windows volume runs low, and can see per root where the worktree space went —
both from code that reads and never deletes. The parent's E19 *done when* (four points) is the acceptance; each story
below names which part it proves.

## 2. Facts the stories rest on (verified 2026-10-15 on main `28ebdeed`, six Sonnet scouts, cited files opened)

| Fact | Where |
|---|---|
| `DiskRules.Evaluate(Reading<DiskFacts>, …)` sees only HOSTING volumes; every lettered volume is in `HealthSample.WindowsDisk.Facts.Volumes` | `Thresholds/DiskRules.cs:35-40`, `Health/VirtualDisks.cs:42-44,141`, `Health/HealthSample.cs:69`, `Health/WindowsDiskSample.cs:17,67-79` |
| `DiskRules.Rank` has three ranks (ok, unknown, else) — `Critical` would rank as `Warn` | `Thresholds/DiskRules.cs:184-191` |
| A test pins the disk verdict list and its order after `disk.discard` | `tests/WslCare.Core.Tests/Thresholds/DiskRulesTests.cs:293`, `:~302` |
| The recorded fixture's volumes: E: FAT 29.7 MiB; D: ReFS 195.3 GiB, 23.1 GiB free (11.8 %) → `warn` under the defaults; C: 41 % free; F: 62 % free | `tests/fixtures/health/windows-disk-2026-10-10/powershell-windows-disk.out`; M5 re-measured live 2026-10-15: E: removable FAT 0.03 GiB |
| The status-bar mark pattern: `withGpuMark(view, mark)`, a mark never lowers the level; composed in `StatusBar.compose()` | `src_vs_code/src/statusBar/statusBarModel.ts:102,109-115`, `statusBar/statusBar.ts:52-54`, test `test/gpu.test.ts:187-196` |
| The extension's typed `Verdict` drops `basis`; the raw body keeps it (`verdicts[id=…]` path syntax) | `src_vs_code/src/client/outcome.ts:7-13`, `client/handshake.ts:107-113`, `panel/jsonPath.ts` |
| **Until E19.S2b:** the extension reads NOTHING from the Windows binary but `--version`; WR-E1 (not built) widens the client, and even its read table has no `status`; the store has one `status` slot | `src_vs_code/src/client/WindowsCareClient.ts:96-103`, `PLAN_windows_runner.md` §3.8/§5 WR-E1, `src/state/outcomeStore.ts:11` |
| The extension's poller reads only while the window is focused, every `wslCare.refreshSeconds` (120 s default, up to 86 400) | `src_vs_code/src/poll/poller.ts:8`, `package.json:173` |
| The Windows watch (WR-D4) samples the Windows MCP servers and sweeps run retention under the run lock (busy → no sample), writes no run record, has no volume field | `Watch/WatchRun.Windows.cs:36-65`, `Watch/WatchRun.cs:66` |
| The Windows `status` stamps sample verdicts `basis {sample, age 0}` (`StatusVerdicts.Sampled`); no sample verdict carries a real age yet | `Status/StatusVerdicts.cs:36-47`, `Cli/Commands/StatusCommand.cs:44,63-80` |
| `IFileSystem.MeasureVolume(path)` (DriveInfo) exists; NOTHING enumerates the lettered drives natively | `Files/IFileSystem.cs:269`, `Files/PhysicalFileSystem.cs:124-135` |
| `TreeWalk` sets `IgnoreInaccessible = true`; an unreadable folder is invisible and the result may say `Complete: true`; `TreeMeasure.Measured` has no denial count; the hook is `FileSystemEnumerator<T>.ContinueOnError` | `Files/TreeWalk.cs:105-111,117-118,212`, `Files/IFileSystem.cs:165-189` |
| Safe small reads: `IFileSystem.ReadUserFile(path, maxBytes, owner, beneath)` refuses a link on the way and caps exactly | `Files/IFileSystem.cs:249`, `Files/BeneathFiles.cs:28,96-109`, `Files/RegularFiles.cs:126-129,253-266` |
| Atomic state file pattern with `schemaVersion` | `Docker/VolumeSeen.cs:78-124`, `Files/IFileSystem.cs:361` (`WriteFileAtomically`), `Json/WslCareJsonContext.cs` |
| The forward path translation exists; its inverse (`/mnt/<l>/…` → `L:\…`) does not | `Health/HealthCollector.cs:103-109`, `Health/HealthParsers.cs:90-94` |
| Windows-only test legs are `[Fact]`s starting with `Assert.SkipUnless(OperatingSystem.IsWindows(), …)`; links via `TestSupport/DirectoryLinks.TryCreate` | `tests/WslCare.Core.Tests/Archive/WindowsBaseFolderTests.cs:154-161`, `tests/WslCare.TestSupport/DirectoryLinks.cs:16` |
| `config set` writes ONLY the user layer (`UserConfigWriter`), and refuses a MachineOnly key; the machine layer (`%ProgramData%`) has no writer | `Config/UserConfigWriter.cs:64-88,161`, `Cli/Commands/ConfigCommand.cs:51`, `Config/ConfigLoader.cs:230-234,255` |
| A user-layer key that names where the product acts already exists, judged at every use: `archive.baseFolder` (Display, `UserOwned`) | `Config/ConfigKeys.Archive.cs:29`, `Config/ConfigLoader.cs:255`, `Archive/BaseFolderPlacement.cs:44` |
| A verb is a `Request` record + a `Commands` row + a dispatch arm + a `module_tests.md` flow-catalogue row (`VerbRegisterTests`); stdin lists come through `StdinList.Read` (`--entry -`) | `Cli/CommandLine.cs:9-209,271-306`, `Cli/Program.cs:97-130`, `tests/WslCare.Scenarios/VerbRegisterTests.cs:17-41`, `Cli/StdinList.cs:28-57` |
| Command templates: the executable is a BARE name (`CommandCatalogue` refuses a path); there is NO path or relative-name slot; `HostileInputs.Valid` throws for an unknown slot kind; no stdin PAYLOAD option on `CommandRequest`; `WorkingDirectory` is never judged by the policy; the Windows binary resolves only from `PATH` | `Processes/Policy/CommandTemplate.cs:51,55`, `Policy/CommandCatalogue.cs:12,17,34-35`, `Policy/SlotKind.cs`, `tests/WslCare.TestSupport/HostileInputs.cs:92-112`, `Processes/CommandRequest.cs:270,292,303`, `Policy/CommandPolicy.cs:262-273`, `Processes/ExecutableResolver.cs:76-90,156-159` |
| A clean environment clears EVERYTHING (`info.Environment.Clear()`); real git on Windows needs `SystemRoot` and a home for its config | `Processes/ProcessCommandRunner.cs:254-261` |

## 3. Decisions this document takes (each a deviation from the parent's text, recorded there in the same commit)

1. **DH-1b keeps the watch AND adds a live fallback** (consultant `cabfc357`, verified: the poller reads only while the
   window is focused, so a live read in `status` alone is not a frequent observation). The Windows watch takes the native
   reading every period into ONE file, `%LOCALAPPDATA%\wsl-care\volumes.json`; the Windows `status` judges
   `disk.volumeFree` from it with its REAL age (`basis {source: sample, sampledAt, ageSeconds}` — the first sample basis
   with a non-zero age), and reads the volumes live (no process) when the file is missing, unreadable or older than
   `3 × mcpWatchdog.periodMinutes`, saying "read live by status" in the reason.
2. **The extension halves of DH-1b and DH-3 are one blocked story, E19.S5**, because the extension cannot read the Windows
   binary's `status` until a client read and a second outcome slot exist (§2). Whether E19 builds that narrow read itself
   (the consultant's option "c": Windows `status --json` through `WindowsCareClient`, its fake, a Windows outcome slot,
   polling) or waits for WR-E1 is the OWNER's decision (§7 Q-E19-1). Until then the bar's mark comes from the distro's
   4-hourly reading only (E19.S1), and DH-1b's 5-minute reading is visible through `wsl-care.exe status` and the scan
   through `wsl-care.exe status`/`worktrees scan`.
3. **`worktrees.roots` is a USER-layer key, `UserOwned` like `archive.baseFolder`, not MachineOnly** (scout finding, verified
   in `ConfigCommand.cs:51` and `ConfigLoader.cs:230-234`: a MachineOnly key cannot be written by the user-run verb, and
   the machine layer has no writer and is not user-writable). Its safety is the parent's §3.3 root rules, judged by
   `worktrees roots set` AND again at the start of every scan and every run, exactly as the archive base folder is.
   `worktrees.gitPath` stays MachineOnly (an administrator names a program to run).
4. **Git names travel on argv, not stdin**: `CommandRequest` has no stdin payload, and adding one (a concurrent writer
   beside both drains) is a runner change no E19 story needs. `check-ignore` and `ls-files` take their names as a bounded
   `Repeat` of a new `RelativeName` slot, in batches bounded by BOTH a count (`worktrees.gitNamesPerCall`, 64) and the
   encoded command line (`worktrees.gitCommandLineMaxChars`, 7000 — under Windows' 8 191 / 32 767 limits with room for the
   fixed words; coai plan round, findings 0 and 5); a single name that cannot fit alone is retained, named "too long for
   one git call", never dropped.
5. **The git environment is clean plus what git needs — and NO user or system git configuration** (coai plan round,
   finding 4: with `HOME` set, a user's `core.excludesFile` or `~/.config/git/ignore` would make `check-ignore` answer with
   the user's rules as if they were the repository's): `SystemRoot`, `WINDIR`, `TEMP`, `TMP`, the three `GIT_*` of the parent,
   plus `GIT_CONFIG_NOSYSTEM=1` and `GIT_CONFIG_GLOBAL=NUL`; NO `HOME`, `USERPROFILE` or `XDG_CONFIG_HOME`; and
   `-c core.excludesFile=` in argv. Measured against the real `git.exe` on this machine in E19.S4 before final; a scenario
   with a user excludes file that conflicts with the repository's rules pins it.
6. **A real `git.exe` runs in tests only against a throw-away `testsToDelete-` repository the test creates**, read-only
   commands only (the parent's §4 "never a real git from a test" is narrowed to "never against a real repository" — the
   round-2 rejection already relied on such a test).
8. **A WSL-made worktree's pointer is translated only through known automount roots** (coai plan round, finding 1): the
   Windows side cannot read the distro's `wsl.conf`, so `worktrees.wslAutomountRoots` (default `["/mnt/"]`, a closed shape:
   absolute, ends with `/`) lists the prefixes; a POSIX pointer under none of them is `foreignPointer`, never a candidate —
   the safe failure. `worktrees.json` stores Windows paths only.
9. **`git.exe` is admitted when no non-administrator can REPLACE it or plant beside it** (coai plan round, finding 2; the
   cadence consultation `7cf11e9a`, verified 2026-10-15: `C:\` grants Authenticated Users `CreateDirectories`, and
   `C:\Program Files\Git\cmd` grants Users only read and execute). For anyone but Administrators, SYSTEM and
   TrustedInstaller: on `git.exe` no write, append, delete, change-permissions or take-ownership; on its own folder (and
   git's `mingw64\bin`, where its DLLs load from) no create-files either; on every ancestor up to the volume root no
   delete, delete-child, change-permissions or take-ownership — creating an UNRELATED folder under `C:\` cannot replace an
   existing path component and is allowed. A positive test, `The_supported_Program_Files_git_is_accepted`, runs against the
   real ACLs on the Windows leg. A per-user install
   (`%LOCALAPPDATA%\Programs\Git`) is refused unless an administrator names it in `worktrees.gitPath` (machine layer).
11. **A content check by name is not proof of regenerability** (the cadence consultation, verified against the parent's
   §3.3 artifact list, which admits `*.json`, `*.txt`, `*.xml`, `*.js`): an app may write `bin\local-state.json` at run
   time. So a `bin`/`obj` file also retains its candidate when it was written more than `worktrees.artifactSkewMinutes` (10)
   after the newest `*.dll`/`*.exe`/`*.pdb` in the same folder (a build writes them together; a running app writes later),
   and these are never artifacts whatever their folder: `*.db`, `*.sqlite*`, `*.mdf`, `*.ldf`, `*.env`, `.env*`,
   `*.pfx`, `*.key`, `*.pem`. The parent's §3.3 gains the same sentence. RED: `A_json_written_after_the_build_retains_its_bin`.
12. **The Windows `status` read moves INTO E19, as E19.S2b, before the worktree stories** (consultants `cabfc357` and
   `7cf11e9a`, both verified against `WindowsCareClient.ts:81-103` and `outcomeStore.ts:11`): without it the 5-minute reading
   never reaches the bar, so the original failure stays half closed. It is the narrow slice of WR-E1 the bar needs —
   `status --json` on the BUNDLED exe through `WindowsCareClient`, the fake's answer, a Windows outcome slot, the poll — and
   nothing else of WR-E1 (no staged exe, no tasks, no run reads). The runner plan's WR-E1 row says so (both sides). E19.S5
   becomes REQUIRED for the epic. **The visible-warning latency, defined:** while a VS Code window is focused, a volume that
   crosses the critical floor shows in the bar within `mcpWatchdog.periodMinutes` + `wslCare.refreshSeconds` (5 min + 120 s
   by default) — the next watch sample, then the next poll; on focus regained, at once (the poller reads on focus); with no
   window focused, nothing shows until one is (the poller's own rule, `poller.ts:8`). This answers Q-E19-1 under an
   assumption the owner may overrule.
10. **Absent is not zero** (coai plan round, finding 3): until E19.S4, `worktrees.reclaimable` carries no byte figure —
   the report's `reclaimable` is `{available: false, reason}` and the verdict `unknown`; the panel renders "—".
7. **`disk.volumeFree` needs seven keys, not five** (the parent's §3.9 table is right, its DH-1 row said five).

## 4. Stories

Each story: one branch from the previous story's merge, one PR; scenarios and brief by Opus (this document), code and
tests by a Sonnet coder (RED first, watched failing for the right reason), review by an Opus judge, fixes by Sonnet; a
coai code round per story; the full test executables and `npm test` before the PR; manual squash-merge on all-green.

### E19.S1 — `disk.volumeFree` from the distro's full run, and the bar's mark (DH-1)

- **Proves:** E19 done-when (1).
- **Brief (daemon).** New pure `Thresholds/VolumeFreeRules.cs`: `Evaluate(Reading<IReadOnlyList<WindowsVolume>> volumes,
  VolumeFreeLimits limits)` → one `Verdict` `disk.volumeFree`, per the parent's §3.2 exactly (OR per volume, critical
  first; percent alone below `disk.volumeFreeAbsoluteMinSizeGib`; not judged below `disk.volumeFreeMinSizeGib` or in
  `disk.volumeFreeIgnoreLetters`; precedence critical > warn > unknown > ok; value worst-first, limit text naming both
  floors and which rule judged each volume). `VolumeFreeLimits.From(EffectiveConfig)` reads the seven keys. Called from
  `ThresholdRules.FromFullRun` right after `.. DiskRules.Evaluate(…)` with `inputs.Health.WindowsDisk.Facts.Bind(f =>
  f.Volumes)` — so it does not depend on the Distros map being readable. The `status --json` `disk` section gains
  `lettered[{letter, sizeBytes, freeBytes, freePercent, level, judgedBy}]` (from the same evaluation: the rule returns the
  per-volume judgements beside the verdict) so the extension never parses the value text.
- **Keys** (`Config/ConfigKeys.Disk.cs`, `NumberKeys()`, `default.json` `"disk"`, `contracts/config-keys.json`,
  `NumbersAreConfigurationTests` group A): `disk.volumeFreeWarnGib` 30, `…CriticalGib` 10, `…WarnPercent` 10,
  `…CriticalPercent` 3, `…MinSizeGib` 8, `…AbsoluteMinSizeGib` 100 (all `IntKey`, Display, ranges of the parent's §3.9),
  `disk.volumeFreeIgnoreLetters` (`TextListKey`, `[]`, `TextRule.Matching("^[A-Z]$", "a drive letter A-Z")`, ≤ 26).
  `NumberRules`: `critical < warn` for GiB and for percent; `minSize ≤ absoluteMinSize`.
- **Brief (extension).** `statusBarModel.ts`: a pure `withVolumeMark(view, mark)` beside `withGpuMark`; `volumeMarkOf(answer)`
  reads the raw body's `verdicts[id=disk.volumeFree]` (level, `basis.ageSeconds`) and `disk.lettered` (worst critical
  volume); mark text ` · D: low space`, tooltip first line `D: 4.1 GiB free (2.1 %), read 3 h 12 min ago by the full run`;
  only `critical` marks; never lowers the level. `statusBar.ts` `compose()` = `withVolumeMark(withGpuMark(…), …)`.
  `fieldMap.ts:140`: the `disk.hostC` arriving row becomes `read('disk','disk.volumes','Windows volumes: worst free',
  'status','verdicts[id=disk.volumeFree]','verdict')`; `npm run fieldmap:doc` regenerates `research/architecture.md`'s table.
- **Scenarios (RED first).** Daemon: `A_volume_below_the_critical_gib_is_critical_whatever_its_percent`;
  `A_volume_below_the_warn_percent_warns_whatever_its_gib`; `A_small_mostly_empty_volume_is_ok`;
  `The_value_names_the_worst_volume_first`; `A_volume_smaller_than_the_floor_is_not_judged`; `An_ignored_letter_is_not_judged`;
  `An_unread_free_is_unknown_never_ok`; `An_unread_volume_beside_a_critical_one_leaves_it_critical`;
  `An_unavailable_sample_is_unknown_with_its_reason`; `The_limit_text_names_both_floors`;
  `The_recorded_d1_sample_warns_on_D_and_does_not_judge_E` (the fixture); `The_disk_verdicts_follow_disk_discard_in_order`
  (the pinned list widened); `Critical_below_warn_is_held_by_the_number_rules`; `Status_carries_the_lettered_judgements`.
  Extension: `a critical disk.volumeFree marks the bar with the volume and its age`; `a warn disk.volumeFree adds no mark`;
  `the mark never lowers a critical bar`; `no other disk.* verdict colours the bar` (beside the held `statusBar.test.ts:61`);
  `the disk.volumes row reads the verdict`.
- **Break-it:** OR made AND; the absolute-size floor ignored; `Rank` without `Critical`; the mark keyed on the `disk.` prefix;
  the mark lowering a critical bar.
- **Files:** `Thresholds/VolumeFreeRules.cs` (new), `Thresholds/ThresholdRules.cs`, `Config/ConfigKeys.Disk.cs`,
  `ConfigKeys.Numbers.cs`, `NumberRules.cs`, `default.json`, `Collect/DiskReport.cs` (+ `Status`), `Json/WslCareJsonContext.cs`,
  `contracts/config-keys.json`, `contracts/golden/head/status*.json` (regenerated on WSL: `WSL_CARE_WRITE_GOLDENS=1`
  `WslCare.Scenarios --filter-class "*GoldenContractTests"`), `tests/…/Thresholds/VolumeFreeRulesTests.cs` (new),
  `DiskRulesTests.cs`, `Scenarios/VirtualDiskFlows.cs`, `src_vs_code/src/statusBar/*`, `panel/fieldMap.ts`,
  `src/test/statusBar.test.ts`, `research/architecture.md` (generated table), README (the disk section: four verdicts →
  five, the starting values, the bar mark), `research/module_daemon.md` (E15.S2's verdicts bullet + a `disk.volumeFree`
  paragraph), `research/module_vs_code.md`, `research/module_tests.md`.

### E19.S2 — the 5-minute Windows reading (DH-1b, daemon half)

- **Proves:** E19 done-when (2), daemon half (the panel half is E19.S5).
- **Brief.** A seam `Collectors/IWindowsVolumes` with `Read() → Reading<IReadOnlyList<WindowsVolume>>`, real
  implementation over `DriveInfo.GetDrives()` (each drive's letter, `DriveType` — `Fixed` and `Removable` kept, `Network`,
  `CDRom`, `Ram`, `NoRootDirectory`, `Unknown` dropped — `IsReady`, `TotalSize`, `AvailableFreeSpace`, `DriveFormat`; a
  not-ready or throwing drive is a volume with unread size/free and its reason), wired only by the Windows binary's host
  (the distro's host gets an unread table, as `IWindowsProcessTable` does, `Cli/CliHost.cs:233-234`). The Windows watch's
  `SampleOnWindowsAsync` reads it and writes `volumes.json` (`{schemaVersion, sampledAt, volumes[…]}`, atomically, the
  `VolumeSeen` pattern, `DeletionScope(StateDirectory, "volumes")`); a write failure is reported in the watch's answer and
  never ends the run. The Windows `status` (`StatusCommand.WithWindowsSide`) evaluates `VolumeFreeRules` over that file
  with `basis {source: sample, sampledAt, ageSeconds}`; missing / unreadable / older than `3 × mcpWatchdog.periodMinutes`
  → a live read, basis `{source: sample, ageSeconds: 0}` with "read live by status (no watch sample within N min)".
  `status.disk.lettered` is filled on Windows too.
- **Growth:** one file, ≈ 26 volumes × 0.15 KB, overwritten every period; a killed write leaves the previous file
  (atomic rename) and the age says how old it is.
- **Scenarios (RED first).** `The_windows_watch_writes_the_volume_sample_without_a_process`;
  `A_busy_run_lock_writes_no_sample_and_status_reads_live`; `Status_judges_the_sample_with_its_real_age`;
  `A_sample_older_than_three_periods_is_replaced_by_a_live_read_and_says_so`; `A_network_drive_is_not_read`;
  `A_not_ready_drive_is_unknown_with_its_reason`; `The_distro_binary_reads_no_windows_volume`; `A_failed_sample_write_is_reported_and_the_watch_goes_on`.
  Windows legs skip elsewhere.
- **Break-it:** the age left at 0; the live fallback removed; network drives read.
- **Files:** `Collectors/IWindowsVolumes.cs` (new) + `WindowsVolumes.cs` (real), `Watch/WatchRun.Windows.cs`,
  `Watch/VolumeSample.cs` (new: the record, the store), `Status/StatusVerdicts.cs` (a sample basis with an age),
  `Cli/Commands/StatusCommand.cs`, `Cli/CliHost.cs`, `Json/WslCareJsonContext.cs`, tests in `Core.Tests/Watch`,
  `Cli.Tests/StatusCommandTests.cs`, `Scenarios/WindowsWatchFlows.cs`, docs (`module_daemon.md` *WR-D4* section gains the
  volume sample, README *Watch* and *Status*).

### E19.S3 — the worktree report without git (DH-3, part 1)

- **Proves:** E19 done-when (3), all but the git confirmation.
- **Brief.** Keys `worktrees.*` of the parent's §3.9 that this story uses (roots `UserOwned` per §3 decision 3; maxRoots,
  idleDays, projectFileExtensions, buildArtifactPatterns, scanMaxEntries, scanMaxSeconds, reportTop, reportMaxAgeDays,
  reclaimableWarnGib) with a new `TextRule` for a Windows absolute local path (no `/…`, no UNC, no `\\?\`, no empty).
  `Core/Worktrees/WorktreeRoots.cs`: the parent's root rules (absolute, local FIXED drive via `DriveInfo`, not a drive
  root, no reparse point on the way, not the profile or an ancestor, not inside/an ancestor of an agent root or the
  Claude temp root, no nested or overlapping roots, ≤ maxRoots), judged in `worktrees roots set` and at every scan start.
  Verbs: `worktrees roots set` (paths on stdin through `StdinList.Read`, all-or-nothing, a per-path verdict list on
  stdout JSON, exit 2 and "Nothing was written." on any refusal; writes through `UserConfigWriter`), `worktrees roots
  clear`, `worktrees scan --json`. `Core/Worktrees/FolderKinds.cs`: the classifier of the parent's §3.3 table by
  `ReadUserFile` reads only (worktree / locked / orphan / foreignPointer / mainClone / notGit / testLeftover flag), with
  the inverse `/mnt/<letter>/` translation (`Health/PathTranslation.cs` or beside `InDistro`). `Files/TreeWalk.cs`: a
  `TreeRules.CountDenied` mode on a `FileSystemEnumerator<T>` subclass whose `ContinueOnError` counts and continues;
  `TreeMeasure.Measured.Denied` (existing callers unchanged). `Core/Worktrees/WorktreeScan.cs`: per root, per folder the
  kind, idle (the parent's rule: outside build output + each build folder's own time + the admin files), build-output
  candidates (bin/obj beside a listed project file, top-most node_modules beside package.json, nested `.git` retains,
  content check against `buildArtifactPatterns` for bin/obj), sizes in logical bytes, budgets → `unknown`, denied →
  `unknown`. Writes `worktrees.json` atomically (one file, ≈150 KB) and removes its own stale temp file first. The
  Windows `status` carries `worktrees` (`basis {source: scan, scannedAt, ageSeconds}`) and `worktrees.reclaimable`, which
  in THIS story is always `unknown — "git has not confirmed the candidates yet (E19.S4)"` with the structural counts in
  the value — never `ok`, never a byte figure called reclaimable. The distro's binary refuses all three verbs naming the
  Windows side (the verbs carry their own not-on-this-side text; see *As built*).
- **Scenarios (RED first)** — the parent's DH-3 list minus the git ones, plus:
  `An_unreadable_subfolder_makes_the_worktree_unknown_not_idle`; `A_wsl_made_worktree_with_a_mnt_pointer_is_a_worktree_not_an_orphan`;
  `An_unmappable_posix_pointer_is_foreign_and_never_a_candidate`; `A_bin_holding_a_nested_repository_is_retained_whole`;
  `A_bin_holding_a_sqlite_file_is_retained_and_names_it`; `A_bin_of_only_build_artifacts_is_a_candidate`;
  `Roots_set_reads_stdin_validates_and_writes_nothing_on_a_refusal`; `Roots_set_answers_a_verdict_per_path`;
  `A_root_set_by_hand_that_breaks_a_rule_is_refused_at_scan_start`; `Reclaimable_is_unknown_until_git_confirms`;
  `The_classifier_and_the_scan_start_no_process`; `The_distro_binary_refuses_the_worktrees_verbs_naming_the_windows_side`;
  `Existing_walks_keep_ignoring_inaccessible_folders`. Windows legs build their trees under a `testsToDelete-` sandbox.
  Added by the Fable security review (2026-10-18) and the story's plan round: `A_short_name_spelling_of_the_profile_is_refused`;
  `Two_spellings_of_one_folder_are_one_root`; `A_trailing_dot_says_it_is_spelt_differently_not_that_it_is_a_link`;
  `A_tree_of_many_empty_folders_still_stops_at_the_budget`; `A_tree_of_many_empty_folders_asks_the_token`;
  `An_admin_folder_pointing_at_this_folder_under_another_spelling_is_not_an_orphan`;
  `A_report_missing_what_the_status_reads_is_unreadable_not_a_crash`; `A_corrupt_report_makes_status_say_it_could_not_be_read_never_fail`;
  `A_report_with_huge_figures_still_gives_a_status`; `A_system_folder_or_the_temporary_folder_is_refused_and_so_is_one_inside_it`;
  `A_folder_holding_the_temporary_folder_or_a_system_folder_is_refused`; `A_report_that_is_too_large_even_trimmed_is_refused_and_no_unreadable_file_is_left`.
- **Break-it:** `IgnoreInaccessible` back on in the scan; the `/mnt/` translation removed; the nested-`.git` retain removed;
  the content check removed; the root re-validation at scan start removed. For the review's conditions: the final-path
  equality removed; folders not counted toward the walk's budget; the exists-under-another-spelling check removed; the
  report's post-parse check removed; one protected place (the temporary folder) removed; the write's size bound removed.
- **Files:** `Core/Worktrees/*` (new), `Files/TreeWalk.cs`, `Files/IFileSystem.cs` (`Measured.Denied`), `Config/ConfigKeys.Worktrees.cs`
  (new), `Config/KeyRules.cs` (the Windows path rule), `Config/ConfigLoader.cs` (`UserOwned` += roots), `default.json`,
  `Cli/CommandLine.cs`, `Cli/Program.cs`, `Cli/Commands/WorktreesCommand.cs` (new),
  `Cli/Commands/StatusCommand.cs`, `Json/WslCareJsonContext.cs`, `contracts/config-keys.json`,
  `contracts/requests/worktrees-roots-set-stdin.json` (new, the shared fixture), tests (`Core.Tests/Worktrees/*` new,
  `Cli.Tests/WorktreesCommandTests.cs` new, `Cli.Tests/CommandLineTests.cs`, `Scenarios/WorktreesFlows.cs` new,
  `VerbRegisterTests` via the catalogue row), docs (README *Worktrees* section, `module_daemon.md` new section,
  `module_tests.md` flow-catalogue rows, `architecture.md` verbs).
- **Fable own review** (the parent's §5: it writes a key that admits deletion) — outside the quota.
- **As built (2026-10-19, PR #158).** As briefed, with these deviations and additions:
  - **`ActCommand.OtherSide` was NOT widened.** The three `worktrees` verbs carry their own not-on-this-side text
    (`WorktreesCommand`); `act`'s wording stays as it was. Recorded on the coai plan round.
  - **The Fable security review (approve with conditions) is built.** A root is held and its FINAL path
    (`GetFinalPathNameByHandle`, without the device prefix) must equal its spelling — rule `spelt-differently` — so an 8.3
    name, a `subst` drive or a trailing dot never admits the profile or a second spelling of one root; the profile,
    overlap and protected-place rules run on the real name. Protected places add `<SystemDrive>\Windows`, both
    `Program Files`, `ProgramData` and the whole user temporary folder. The counting walk counts every folder toward
    its budget, deadline and token. An admin back-pointer naming an EXISTING `.git` under another spelling is
    unreadable, never `orphan`. A `worktrees.json` that lacks what `status` reads is unreadable with its reason, and the
    status sums its byte figures without overflow. The report is written under the cap it is read with (trimmed to its
    counts, else refused with the previous report kept).
  - **The gap between the scan's judgement and its listing** (a junction swapped in between) is harmless here: this
    story only reads and reports, and E20 acts only on handles whose final path it verified at delete time
    (`PLAN_disk_hygiene_windows_E20.md` §4 A.5, §4a item 1, §4b item 4).
  - **`TestSupport/ToDeleteRoot`** names its folder `testsToDelete-wsl-care-<purpose>-<guid>` under the temp root; it
    duplicates `TempRoot` with the shared prefix until E21.S2 (DH-8) moves `TempRoot` onto the prefix and the two merge.

### E19.S4 — git's three read-only questions, and "reclaimable" made real (DH-3, part 2)

- **Proves:** E19 done-when (3), the git half.
- **Brief.** `Processes/Policy/SlotKind.cs`: a `RelativeName` record (refuses empty, a leading `-`, `..`, `:`, a rooted
  path, `\` at the start, glob characters `*?[`, the pathspec magic prefix `:(`, control characters, > 260 chars) and its
  `HostileInputs.Valid` case. `ReadCommandTemplates`: three templates on the bare `git.exe` — `rev-parse --show-toplevel`,
  `--literal-pathspecs ls-files -z --cached -- <names…>`, `--literal-pathspecs check-ignore -v -n -z -- <names…>` — each
  prefixed `-c core.fsmonitor=false --no-optional-locks`; plus the orphan form `--git-dir=<common> --work-tree=. check-ignore
  --no-index -v -n -z -- <names…>` where `<common>` is a new `AdminFolder` slot accepting only a path the classifier read
  from a `.git` file under a configured root. `Processes/GitExecutable.cs`: the resolver — candidates
  `%ProgramFiles%\Git\cmd\git.exe` (and the 64-bit Program Files under a 32-bit process), then `worktrees.gitPath`
  (MachineOnly); a candidate is admitted only when it exists, is a PE file (`MZ` header) and its folder's owner and ACL
  deny write to non-administrators (an `IAdminOnlyFolder` seam, real one via `GetSecurityInfo`); never `PATH`. Wired
  through `ProcessCommandRunner`'s resolve seam as a product constructor taking a resolver (NOT the
  `UnguardedForItsOwnTests` seam). `Core/Worktrees/GitQuestions.cs`: working directory = the candidate's parent (checked
  beneath a configured root by THIS code — the policy never judges it), names relative, batches of
  `worktrees.gitNamesPerCall` (64), the clean environment of §3 decision 5 with `GIT_CEILING_DIRECTORIES=<root>`, the
  per-path parse (`-n` lines; 0/1 an answer, 128 a refusal; `rev-parse`/`ls-files` require 0; a negated rule retains;
  "dubious ownership" and every refusal retain by name), timeout `worktrees.gitTimeoutSeconds`. The scan runs them for
  every candidate that passed the structural checks; `worktrees.reclaimable` becomes the parent's §3.3 verdict
  (`a24` + `a24Orphans`, the rest under `retained{reason}`).
- **Scenarios (RED first).** The parent's DH-4 git list moved here where it bites the REPORT:
  `Check_ignore_exit_1_is_an_answer_and_128_a_refusal`; `A_batch_is_judged_per_path_not_by_the_exit_code`;
  `A_negated_ignore_rule_retains`; `A_tracked_file_inside_bin_retains_the_candidate`;
  `A_build_folder_git_does_not_ignore_is_retained`; `Git_discovery_that_escapes_the_root_retains_every_candidate`;
  `A_git_refusal_retains_the_candidate_by_name`; `An_empty_answer_with_a_nonzero_exit_retains`;
  `An_orphan_with_its_repository_still_there_is_ignore_confirmed` (real git, a `testsToDelete-` repository the test
  builds); `The_git_templates_cannot_be_bent_into_prune_remove_or_clean` (+ the property suite over the new slot);
  `Git_is_never_taken_from_PATH`; `A_git_in_a_user_writable_folder_is_refused`; `The_clean_environment_is_enough_for_real_git`
  (real git, read-only, the throw-away repository); `A_relative_name_with_dot_dot_or_magic_is_refused`;
  `Reclaimable_counts_only_candidates_that_pass_every_check_and_names_the_rest`.
- **Break-it:** a non-zero exit read as clean; the exit code judged instead of the lines; `GIT_CEILING_DIRECTORIES` dropped;
  `core.fsmonitor` override dropped; `PATH` allowed in the resolver; the ACL check removed.
- **Files:** `Processes/Policy/SlotKind.cs`, `ReadCommandTemplates.cs`, `Processes/GitExecutable.cs` (new),
  `Processes/ProcessCommandRunner.cs` (the resolver constructor), `Core/Worktrees/GitQuestions.cs` (new), `WorktreeScan.cs`,
  `Config/ConfigKeys.Worktrees.cs` (gitPath, gitTimeoutSeconds, gitNamesPerCall), tests (`HostileInputs.cs`,
  `CommandPolicyTests.cs`, `Core.Tests/Worktrees/GitQuestionsTests.cs` new, `Core.Tests/Processes/GitExecutableTests.cs`
  new), docs.
- **Fable own review** (adds git) — outside the quota.
- **As built (2026-10-21, PR #159).** As briefed, with these deviations and additions (the measurements are in
  [research/2026-10-15_disk_hygiene_git_probe.md](../research/2026-10-15_disk_hygiene_git_probe.md)):
  - **`check-ignore` runs in argv form WITHOUT `-z` and WITHOUT `--literal-pathspecs`** (git 2.55.0 refuses both: exit 128). Its output is read
    per line by the FIRST tab (`<source>:<line>:<pattern>\t<path>`, or `::\t<path>` for a non-match AND for a path whose deciding rule is a
    negation — both read "not ignored by git"); the exit code (0 / 1 an answer, 128 a refusal) is never what judges a name. What makes a name a
    literal is the `RelativeName` slot, which refuses `* ? [`, `:`, a backslash, a leading `-` or `/`, `..`/`.`/empty segments, control characters,
    `" < > |`, over 260 characters, and — found by the template property — the whole names `clean`, `prune` and `worktree`, so no combination of
    names can spell a never-command. `ls-files` keeps `--literal-pathspecs -z`.
  - **Every git command carries `-c core.fsmonitor=false -c core.excludesFile= -c core.quotePath=false --no-optional-locks`**, runs in a clean
    environment with NO `HOME`/`USERPROFILE`/`XDG_CONFIG_HOME` (a user excludes file and the XDG ignore file both leaked otherwise, measured), and its
    output is decoded as UTF-8 (`CommandRequest.Utf8Output`, a small additive option: .NET otherwise decodes with the console's OEM code page and a
    non-ASCII folder name no longer matched). `The_scan_never_changes_the_index_time` holds the index's bytes and mtime, with a `git status` control.
  - **The orphan form's `--git-dir` is the git folder the classifier's own admin value lies in** (`<repository>\.git` from `<repository>\.git\worktrees\<name>`),
    derived in the scanner and checked to exist by `GitQuestions`; an admin folder not shaped so, or whose repository is gone, is "not confirmed by git" and no
    process starts. A path under `<home>\git` on that argv is refused by the never-list's protected-path rule — left intact, so such an orphan stays unconfirmed.
  - **Git is asked only where nothing else already keeps the candidate**: a candidate that passed every structural check, in a folder that is idle and (for a
    worktree) unlocked, within the scan's time. The first reason a candidate stays is the one reported (structural, unknown, recent, locked, then git's), so asking git
    about a recent or locked folder would only spend processes. One `rev-parse` per folder, then `ls-files` and `check-ignore` per batch of names (64 names AND 7 000
    characters of encoded command line; a name that cannot fit alone is retained, "too long for one git call").
  - **`worktrees.reclaimable` is real** (`ReclaimableReport`: `a24`, `a24Orphans`, `retained[{reason, count, bytes}]`, `notAsked`; the figures are ABSENT, not zero,
    when the scan had no git). The verdict warns above `worktrees.reclaimableWarnGib` once git confirmed; a figure below it is `unknown`, never `ok`, when the scan
    did not finish or a candidate could not be ASKED of git (a refusal, a timeout, no `git.exe`); a stale scan is `unknown`; a complete scan that confirmed nothing is `ok`.
  - **`git.exe`** comes from `%ProgramFiles%\Git\cmd` (and `%ProgramW6432%`'s) or the machine layer's `worktrees.gitPath`, admitted by the ACL rule of decision 9 over the
    real security descriptors (`AdminOnlyJudge`, pure; `WindowsAdminOnlyPath`, the managed `GetAccessControl`, AOT-safe); never `PATH`. The four keys are in
    `ConfigKeys.Worktrees` beside S3's.
  - **The scan's tripwire moved, it was not weakened**: `The_classifier_and_the_scan_start_no_process` now allows exactly `GitQuestions.cs`, `GitReplies.cs` and `GitSupport.cs` to name the
    runner (with companions proving they do, and that `WorktreeScanner` is handed `GitQuestions` and never a runner).
  - **After the Fable review and the coai plan round (2026-10-21)**:
    - `ls-files` is asked per name as `:(literal,icase)<name>` and WITHOUT `--literal-pathspecs` (git's pathspec match ignores `core.ignorecase`; its ignore match
      does not, so index `Bin/x` with disk `bin` read "ignored, nothing tracked"; under `--literal-pathspecs` git parses no magic). The `:(literal,icase)` is a template
      literal in front of the unchanged `RelativeName` slot. Real-git RED/GREEN: `A_tracked_file_under_a_differently_cased_index_spelling_retains_the_candidate`.
      `check-ignore` keeps the hole (it says ignored) — the `ls-files` question is the guard.
    - The ACL judge keeps GENERIC_ALL, GENERIC_WRITE and MAXIMUM_ALLOWED (an entry may carry them unmapped); GENERIC_READ/EXECUTE are kept and grant no write
      (deviation from the review's wording, which would have refused the ordinary read-and-execute of `Users`).
    - `GitExecutable` also judges the real `git.exe` of each of `mingw64\bin`, `mingw32\bin` and `clangarm64\bin` that exists as a program file, checks each of those
      folders, and refuses a git with none. It reuses a passed judgement for the life of the process while the program files keep their path, length, creation and
      last-write time: **measured 12 ms a resolve without it (50 in 601 ms)**, a scan makes hundreds.
    - A tracked gitlink ABOVE a candidate retains it (`HoldsTracked`). Measured: real git prints nothing for a pathspec inside a gitlink and `check-ignore` refuses it
      (exit 128, "is in submodule"), so git retains it by name too; the scripted test pins the gitlink-above case.
    - `check-ignore -v` lines are split at the LAST tab (a pattern may hold a tab and a colon; a name cannot); a line for a path nobody asked about maps to no name.
    - No admitted `git.exe` is a named state: `reclaimable.available` is false, `gitUnavailable` holds the why, `retained` counts every candidate that needed git under
      "git unavailable", the figures are absent, and the verdict is unknown with "git could not be asked: …" — never ok, never zero.
    - A candidate named `clean`, `prune` or `worktree` is retained with "a folder named … cannot be put to git safely", not the generic refusal. Accepted blind spot:
      the never-list scans argv tokens, so a path that merely contains the word as a segment is not a token and is asked about normally.
    - A git timeout kills git and its descendants: the shared launcher (`Process.Kill(entireProcessTree: true)`) already did; `GitTimeoutTests` pins it with a fake git that
      starts a child and hangs. No change was needed there.
    - **For E20** (also its §4c): `a24Orphans` is ignore-only by construction (a pruned worktree has no index) — E20 may act on it only under the owner's explicit
      per-run consent, never as `a24`; "tracked" means in the index at scan time; E20's delete-time re-run uses the same `GitQuestions`.
    - A scan end-to-end over a REAL linked worktree is covered: `git worktree add --orphan` makes the linked worktree on an unborn branch, so no commit and no identity
      is needed anywhere (`A_scan_over_a_real_linked_worktree_confirms_its_ignored_bin_end_to_end`).

### E19.S2b — the Windows `status` read in the extension (the narrow slice of WR-E1, decision 12)

- **Proves:** the path E19.S5 needs; nothing visible on its own.
- **Brief.** `client/WindowsCareClient.ts`: a `status()` read — the bundled exe with `VERBS.status` (`['status','--json']`,
  `verbs.ts:24`), its own timeout `wslCare.timeouts.windowsStatusSeconds` (default above the Windows status's worst case,
  the `ceilings.test.ts` pattern), parsed by the same `checkedBody`/`statusAnswer` (`handshake.ts:120-125`) into a
  `StatusAnswer` with the raw body. `state/outcomeStore.ts`: a second slot `windowsStatus` beside `status`, its own
  `VerbOutcome`, never mixed. The poller reads it on the same tick as the distro's `status`, only on Windows hosts, and a
  failure there never touches the distro's slot (WSL stopped still shows the Windows reading). The fake (`test/fake/*`)
  answers the bundled exe's `status --json` from a golden. `structure.test.ts` and `bundledExeStructure.test.ts` stay true
  (only `WindowsCareClient` names the exe; it starts only through its runner).
- **Scenarios.** `the windows status is read through the bundled exe and parsed like the distro's`; `a windows status failure
  leaves the distro outcome untouched`; `the windows slot is read on the same tick and only on windows`; `wsl stopped still
  reads the windows status`; `the client names no other verb than status and --version`.
- **Files:** `src_vs_code/src/client/WindowsCareClient.ts`, `client/verbs.ts`, `state/outcomeStore.ts`, `poll/*`,
  `settings/numbers.ts`, `package.json`, `test/*`, `research/module_vs_code.md`, the runner plan's WR-E1 row.
- **As built (2026-10-15, PR pending).** `WindowsCareClient.status()` beside `version()`; the typing of the result in
  `client/windowsStatus.ts` (the same `parseAnswer` / `versionRefusal` / failure kinds as the distro's, `distro: 'Windows'`);
  `OutcomeStore.windowsStatus` (optional; `clear()` keeps it); the poller's `windows` source, read on the same tick and on a
  panel Refresh, **one read at a time** (a tick or Refresh that finds it running joins it: one start, one store write). Added by the
  review: the hung-exe test (`windowsStatusTimeout.test.ts` — the ceiling ends it as the client's `timedOut`, the runner kills the
  fake child, the distro slot is untouched), `focus regained reads the windows status at once`, the wiring scan
  (`extensionWiring.test.ts`) and the host scenario, and the churn count (the Windows binary doubles the run logs per window-day:
  [2026-10-04_extension_poll_churn.md](../research/2026-10-04_extension_poll_churn.md)). **OPEN:** the fake's Windows answer is the
  distro `status.json` golden with `side: "windows"` — no Windows golden exists; E19.S5 needs a real Windows `status` golden from
  the daemon's scenario harness before it renders `disk.volumeFree` from it.

### E19.S5 — the extension halves: the Windows reading in the bar and panel, the worktree row

- **Proves:** E19 done-when (2)'s panel half and (3)'s row.
- **Depends on:** E19.S2b (the Windows `status` read, decision 12). No longer blocked.
- **Brief when unblocked.** `volumeMarkOf` takes the NEWER of the distro's and the Windows `disk.volumeFree` (by
  `basis.sampledAt`/`evaluatedAt`); the `disk.volumes` row the same; the row's suffix "· N GiB reclaimable in worktrees"
  from the Windows `worktrees.reclaimable` ("worktrees not configured" with no root; "not available yet" while the Windows
  read is unavailable). *Choose folders…* stays E20's DH-5.
- **Scenarios:** `the newer of the full run and the windows sample is shown` (both ways); `with no root the row says not
  configured`; `an unavailable windows read keeps the distro reading`.

## 5. Order

E19.S1 → E19.S2 → E19.S2b → E19.S5 (the bar and panel half of DH-1b first: it closes the original failure) → E19.S3 →
E19.S4 → E19.S5's worktree row (a small follow-up PR on S5's code). E21's distro half (DH-2, DH-8, DH-6) may run beside
this epic — not in this document.

## 6. Gates

The coai plan round runs ONCE for this document (the gate's epic `1/3` — the gate numbers the parent's three epics 1-based). A code round runs per story on its
branch, with this document's story section as `planText` plus what the previous round changed; every finding is
resolved, rejections with reasons, in round one.

## 6a. Plan round and cadence consultation (coai, 2026-10-15)

One round, two reviewers: **proceed** (4 gating against 6). All six findings accepted and written into §3: batches bounded
by the command-line length as well as the count (0, 5), automount roots as a key with `foreignPointer` as the safe failure
(1), the ACL rule about WRITE (2), absent is not zero for `reclaimable` (3), no user or system git configuration (4). The cadence consultation `7cf11e9a` (epics 1–3) added decisions 9 (as
verified), 11 and 12; its three points were checked against the ACLs, the artifact list and the extension's client.

## 7. Questions

Asked first of the coai consultants (`3d4d02e6` on the split, `cabfc357` on decisions 1 and 2); taken above.
What remains for the owner:

- **Q-E19-1 — taken under an assumption (decision 12):** the narrow Windows `status` read is built inside E19 as E19.S2b.
  The owner may overrule it before E19.S2b starts; the stories before it do not depend on the answer.

## 8. Definition of Done (this epic)

- [ ] E19.S1–S4 merged, each with its RED tests watched failing, its break-it run, its coai code round resolved, the full
      daemon test executables and `npm test` green.
- [ ] The parent's E19 done-when points 1–4 proven, E19.S2b and E19.S5 included; the visible-warning latency of decision 12
      stated in README.
- [ ] The parent plan's §3.2, §3.3, §3.9, §4 and §5 carry the deviations of §3 here; `todo/README.md` has this document's row;
      `plan-lifecycle` clean.
- [ ] Docs per story (README, `module_daemon.md`, `module_vs_code.md`, `module_tests.md`, `architecture.md`).
