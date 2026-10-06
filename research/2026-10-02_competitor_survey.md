# Competitor survey — Windows and WSL maintenance tools (2026-10-02)

> Status: **web survey, 2026-10-02.** What comparable tools monitor, clean and optimise, what is worth
> adopting in `wsl-care`, and what must never be done. Sources are linked inline; claims marked
> *(our assessment)* are reasoning, not citations.
>
> Feeds: [PLAN_wsl_care_daemon.md](../todo/PLAN_wsl_care_daemon.md),
> [PLAN_windows_care.md](../todo/PLAN_windows_care.md).

## 1. Windows side

### Tools surveyed

| Tool | Monitors | Cleans | Optimises | Safety model | Schedule |
|---|---|---|---|---|---|
| Storage Sense | free space | temp, Recycle Bin by age, optional Downloads, OneDrive online-only | — | none (no preview) | ● ([thewindowsclub](https://www.thewindowsclub.com/storage-sense-in-windows-11)) |
| Settings → Cleanup recommendations | large/unused files, unused apps | — (suggests) | — | report only | — ([pureinfotech](https://pureinfotech.com/free-up-space-cleanup-recommendations-windows-11/)) |
| `cleanmgr` | — | VolumeCaches handlers: WU cleanup, Delivery Optimization, WER, thumbnails, temp | — | profile via `/sageset:N` | `/sagerun:N` ([MS](https://learn.microsoft.com/en-us/troubleshoot/windows-server/backup-and-storage/automating-disk-cleanup-tool)) |
| DISM | `/AnalyzeComponentStore` | `/StartComponentCleanup` (WinSxS) | — | analyse first; `/ResetBase` makes updates permanent; manual WinSxS deletion can make the PC unbootable | built-in task ([MS](https://learn.microsoft.com/en-us/windows-hardware/manufacture/desktop/clean-up-the-winsxs-folder)) |
| Microsoft PC Manager | RAM, temp size | temp, WU leftovers, browser caches, large files | startup, process end, "Boost" | partial | Smart Boost thresholds ([windowsforum](https://windowsforum.com/threads/microsoft-pc-manager-one-click-windows-maintenance-and-cleanup.394270/)) |
| Dev Drive | trust state | — | ReFS + block cloning; Defender **performance mode** (async scan) instead of exclusions; cache env vars (`npm_config_cache`, `NUGET_PACKAGES`, `PIP_CACHE_DIR`, …) | trusted volume | — ([MS](https://learn.microsoft.com/en-us/windows/dev-drive)) |
| CCleaner | health check | browser/app caches, temp, registry | "sleep" background apps, driver updater | `.reg` backup | Pro ([ccleaner](https://www.ccleaner.com/ccleaner/performance-optimizer)) |
| BleachBit | — | temp, logs, memory dumps, MUICache, Prefetch, update uninstallers | — | **preview + whitelist** | CLI only ([makeuseof](https://www.makeuseof.com/use-bleachbit-safely-on-windows/)) |
| Wise Care 365 / Glary / IObit ASC | "issues" counters | everything above + registry | RAM optimiser, registry defrag, startup | weak; IObit flagged PUP ([Malwarebytes](https://www.malwarebytes.com/blog/detections/pup-optional-advancedsystemcare)); registry cleaning in Wise Care 365 ([TechSpot](https://www.techspot.com/downloads/5539-wise-care-365.html)) and Glary ([Technibble](https://www.technibble.com/repair-tool-of-the-week-glary-utilities/)) | ● |
| Sysinternals Autoruns | **every** auto-start point (Run, tasks, services, drivers, Winlogon), "hide Microsoft", VirusTotal | — | disable entries | **disable before delete** | — ([MS](https://learn.microsoft.com/en-us/sysinternals/downloads/autoruns)) |
| RAMMap | memory by use (standby, modified, pool) | manual empty standby / working sets | — | diagnostic | — ([4sysops](https://4sysops.com/archives/analyze-windows-memory-usage-with-rammap/)) |
| Process Lasso ProBalance | CPU hogs | — | **temporarily** lowers priority of background CPU hogs, restores automatically, never the foreground window | auto-revert | ● ([bitsum](https://bitsum.com/apps/process-lasso/docs/algorithms/probalance/)) |
| Chris Titus WinUtil | — | temp, WU | tweaks, services | restore point + undo — yet "services → manual" broke WU/NTP/Bluetooth irreversibly ([#1098](https://github.com/ChrisTitusTech/winutil/issues/1098), [#4471](https://github.com/ChrisTitusTech/winutil/issues/4471)) | — |
| O&O ShutUp10++ | — | — | privacy settings | session history + undo, `.cfg` export | — ([binaryfork](https://binaryfork.com/o-and-o-shut-up-review-1433/)) |
| Bulk Crap Uninstaller | orphaned uninstallers | leftovers (files, services, tasks, registry) with confidence | batch uninstall | confidence levels | — ([GitHub](https://github.com/BCUninstaller-Bulk-Cleaner/)) |
| WizTree / WinDirStat 2 | disk usage via MFT (seconds per TB) | — | — | report | — ([XDA](https://www.xda-developers.com/stop-using-windirstat-and-switch-to-this-free-tool-instead/)) |
| npkill, kondo, `dotnet nuget locals` | build artefacts / dev caches | `node_modules`, `bin/obj`, `target`, NuGet http-cache/temp | — | preview | — ([npkill](https://npkill.js.org/), [kondo](https://github.com/tbillington/kondo), [NuGet](https://learn.microsoft.com/en-us/nuget/consume-packages/managing-the-global-packages-and-cache-folders)) |
| VS Code caches | — | `Cache`, `CachedData`, `GPUCache`, logs, `CachedExtensionVSIXs`; `workspaceStorage` of deleted projects never self-cleans | — | — | — ([kleaner](https://kleaner.pro/en/blog/vscode-cleanup)) |

### Worth adopting (Windows), ranked for a dev workstation

1. **"What grew since yesterday"** — size snapshot + delta (WizTree-style). Report.
2. **Auto-start baseline diff** (Autoruns model): Run keys, tasks, services, drivers, non-Microsoft only,
   "new since <date>"; action = **disable** (reversible), never delete. Button with preview.
3. **ProBalance-style temporary de-prioritisation** of background CPU hogs, auto-revert, never the
   foreground app, never protected processes. Auto.
4. **WSL memory / `vmmem`** visibility and `.wslconfig` audit. Report + button.
5. **VHDX compaction** after `wsl --shutdown` (`Optimize-VHD` / diskpart). Button.
6. **Temp** (`%TEMP%`, `C:\Windows\Temp`) older than N days, skipping locked files. Auto.
7. **cleanmgr profile** (`/sagerun`) for WU cleanup, Delivery Optimization, WER, thumbnails. Auto (admin task).
8. **WinSxS**: `AnalyzeComponentStore`, then `StartComponentCleanup` **without** `/ResetBase`. Button (admin).
9. **Delivery Optimization cache** (`Delete-DeliveryOptimizationCache -Force`). Auto (admin).
10. **Crash dumps as a signal**: LiveKernelReports (WATCHDOG = display driver TDR), Minidump, WER queues,
    `%LOCALAPPDATA%\CrashDumps`; delete older than N days by button.
11. **Dev caches**: NuGet `http-cache`/`temp` auto, `global-packages` button; npm, pnpm `store prune`, pip,
    cargo, gradle; Ollama model sizes report-only.
12. **Build artefacts** in projects untouched for N days (kondo/npkill). Button with preview.
13. **VS Code**: caches auto **only while Code is closed**; `workspaceStorage` entries whose project folder
    no longer exists — button.
14. **Defender + Dev Drive audit**: is a Dev Drive present and trusted, is performance mode on; move caches
    via the official env vars. Report + wizard.
15. **Windows Search**: indexed `node_modules`/repos → offer exclusion. Button.
16. **DriverStore** old duplicates (`pnputil /enum-drivers`), export first, never `/force`. Button.
17. **Undo journal / restore point** before any system tweak. Infrastructure.
18. **Uninstall leftovers** report with confidence (BCU model). Report.
19. **Threshold triggers** (Smart Boost model): temp > X GB, low free space → run the safe rules off-schedule.

## 2. WSL / Docker side

### Findings that change the existing plan

- **`autoMemoryReclaim=dropCache` is reported as the default since WSL 2.1.3** — the VM drops cache after
  ~10 min of *idle* ([release 2.1.3](https://github.com/microsoft/WSL/releases/tag/2.1.3),
  [wsl-config](https://learn.microsoft.com/en-us/windows/wsl/wsl-config)). On a machine that is never idle
  during the day it never fires — consistent with the 19 GB of cache seen at 18:36. **To verify on this
  machine** before relying on it.
- **Evening degradation is fragmentation, not only memory volume** *(our assessment, from the reports below)*: VMBus needs order-7 (512 KiB)
  contiguous blocks; when they run out new sessions hang and interop fails with plenty of free memory
  ([#11612](https://github.com/microsoft/WSL/issues/11612), [#41634](https://github.com/microsoft/WSL/issues/41634)).
  Our own `kern.log` shows **order-7 `kworker` allocation failures on 2026-09-09 and 2026-09-16**
  (measured: [2026-10-02_wsl_resource_baseline.md](2026-10-02_wsl_resource_baseline.md)) — the same signature *(our
  assessment: the reports describe this failure shape; that it is the cause here is not proven)*. `compact_memory` must be triggered by a fragmentation signal, not only by a timer;
  `drop_caches` does not fix fragmentation.
- **`sparseVhd` was disabled from WSL 2.5.6/2.5.8 after data-corruption reports** — only with
  `--allow-unsafe`, and a sparse VHDX cannot be compacted with diskpart
  ([#13075](https://github.com/microsoft/WSL/issues/13075), [vramlab](https://vramlab.com/posts/wsl2-sparse-vhd-cannot-compact/)).
  **Remove the "enable sparseVhd" advice.**
- `autoMemoryReclaim=gradual` is known to hang with systemd, with dockerd inside the distro, and together
  with Docker Desktop Resource Saver ([#10675](https://github.com/microsoft/WSL/issues/10675),
  [#10497](https://github.com/microsoft/WSL/issues/10497), [#11066](https://github.com/microsoft/WSL/issues/11066)).
  This machine has systemd and Docker Desktop → **stay on `dropCache`**.
- Docker Desktop Resource Saver on WSL pauses the engine (CPU) but **does not reduce memory**
  ([docs](https://docs.docker.com/desktop/use-desktop/resource-saver/)).

### Worth adding (WSL), ranked

1. **Fragmentation detector**: `/proc/buddyinfo` order ≥ 7 headroom + `page allocation failure: order:N`
   in the journal → `compact_memory` + notify. Auto.
2. **PSI as the primary "VM is struggling" signal**: `/proc/pressure/{memory,io,cpu}` trend and trigger
   ([below](https://github.com/psy-repos-rust/below)). Auto (metric).
3. **Heavy actions only when idle**: low CPU for N minutes, no `docker build` / `dotnet build` running
   ([WSL devblog](https://devblogs.microsoft.com/commandline/windows-subsystem-for-linux-september-2023-update/)). Auto.
4. **`.wslconfig` audit** incl. the `gradual` conflicts above. Report.
5. **VHDX bloat**: `df /` vs real file size; > 10 GB → compaction button (as WSL UI /
   [wsl2-distro-manager](https://github.com/bostrot/wsl2-distro-manager)). Button.
6. **`discard` in `/proc/mounts`**; if absent, `fstrim.timer` / weekly `fstrim -av`. Auto.
7. **Own age tracking for volumes**: `docker volume prune` has no `until` filter — record first-seen date
   of each anonymous volume, prune only those unused for N days; respect a protect label
   ([docs](https://docs.docker.com/reference/cli/docker/volume/prune/)). Auto (anonymous) / button (named).
8. **Container log audit**: `json-file` without `max-size`, big `*-json.log`. Report + `daemon.json` snippet.
9. **BuildKit GC and forgotten `docker-container` builders** (`buildx_buildkit_*_state` volumes outside the
   daemon's GC) ([docs](https://docs.docker.com/build/cache/garbage-collection/)). Button.
10. **Testcontainers zombies** incl. reuse containers Ryuk keeps on purpose
    ([coffeesprout](https://www.coffeesprout.nl/en/testcontainers-reuse-ryuk-zombie-containers.html)). Button.
11. **Old VS Code Server builds**: `~/.vscode-server/bin/<commit>`, `cli/servers/*`, `~/.cursor-server`,
    `~/.windsurf-server` not referenced by a running process; keep the newest 2; `.obsolete` extensions
    ([remote-ide-server-cleanup](https://github.com/TioSisai/remote-ide-server-cleanup)). Auto.
12. **Orphaned dev processes** + inotify instance/watch usage against limits
    ([vscode-remote-release#3195](https://github.com/microsoft/vscode-remote-release/issues/3195)). Button, never auto-kill.
13. **Clock-skew detector**: `CLOCK_BOOTTIME` vs `REALTIME`, compare with Windows time; on drift >
    N s → `hwclock -s` / `chronyc makestep` once, by event, not by cron
    ([#4179](https://github.com/microsoft/WSL/issues/4179), [cr0x](https://cr0x.net/en/wsl2-time-drift-fix/)). Auto.
14. **OOM forensics**: who was killed and when; earlyoom/systemd-oomd presence. Report.
15. **meminfo explainer**: growth of `SUnreclaim` (kernel leak), `Shmem` (`/dev/shm`, tmpfs),
    `Inactive(anon)`, swap — each with a plain-language explanation. Report.
16. **"What grew since yesterday"** for `$HOME`, `/var`, `~/.cache`. Report.
17. **Package-manager-native cleanups**: `uv cache prune`, `pnpm store prune`,
    `dotnet nuget locals http-cache --clear`, `cargo sweep --time 30`, `pip cache purge`, Gradle retention.
18. **Playwright**: remove only browser revisions no project references (`uninstall --all` wipes every
    project's browsers). Button.
19. **Build-artefact scanner** with a confirming project file next to the folder; delete to trash. Button.
20. **`git maintenance`** for discovered repos; `worktree prune` removes only broken ones — but see the
    WSL↔Windows prune hazard in the daemon plan: **never** from WSL. Report.
21. **Retention of our own monitoring**: atop `LOGGENERATIONS`, coredumps, `/var/crash`, `%TEMP%\wsl-crashes`,
    `%TEMP%\swap.vhdx`. Auto / report.

### Recommended configuration (to offer, never to apply silently)

```ini
# %USERPROFILE%\.wslconfig
[wsl2]
memory=<cap, see the Windows baseline>
swap=8GB
maxCrashDumpCount=3
[experimental]
autoMemoryReclaim=dropCache   ; not gradual with systemd + Docker Desktop
sparseVhd=false               ; corruption reports, cannot be compacted
```

```json
// Docker Desktop: %USERPROFILE%\.docker\daemon.json (Settings → Docker Engine) — NOT /etc/docker in the distro
{
  "log-driver": "local",
  "log-opts": { "max-size": "10m", "max-file": "3" },
  "builder": { "gc": { "enabled": true, "defaultKeepStorage": "15GB",
    "policy": [ { "reservedSpace": "5GB", "keepDuration": ["168h"] }, { "reservedSpace": "15GB", "all": true } ] } }
}
```
`log-opts` apply only to containers created afterwards.

Inside the distro: inotify `max_user_watches=524288`, `max_user_instances=1024`;
`vm.compaction_proactiveness` raised to 30–50 **only** with a measured fragmentation problem (it is
measured here); journald `SystemMaxUse=500M`; atop `LOGGENERATIONS=7`.

## 3. Never (both sides)

| Don't | Why |
|---|---|
| Registry cleaning | unsupported by Microsoft, can break the system irreparably ([MS policy](https://support.microsoft.com/en-au/topic/microsoft-support-policy-for-the-use-of-registry-cleaning-utilities-0485f4df-9520-3691-2461-7b0fd54e8b3a)) |
| RAM "boosters", periodic standby/working-set flushing | standby is cache; flushing it forces re-reads ([How-To Geek](https://www.howtogeek.com/171424/why-memory-optimizers-and-ram-boosters-are-worse-than-useless/)) |
| Deleting Prefetch / disabling SysMain | self-maintaining; slows app starts ([MS Q&A](https://learn.microsoft.com/en-us/answers/questions/2432551/can-i-delete-the-data-in-the-prefetch-folder)) |
| Third-party driver updaters | wrong drivers, riskware ([XDA](https://www.xda-developers.com/third-party-driver-update-tools-are-scam-windows-has-better-solution/)) |
| Bulk services → Manual/Disabled | confirmed breakage of WU, NTP, Bluetooth (WinUtil issues above) |
| Manual WinSxS deletion, automatic `/ResetBase` | unbootable system / updates no longer removable |
| Broad Defender exclusions, disabling AV, `fsutil devdrv enable /disallowAv` | "every exclusion is a protection gap" ([MS](https://learn.microsoft.com/en-us/defender-endpoint/microsoft-defender-antivirus-exclusions-overview)); use Dev Drive performance mode |
| Free-space / MFT / USN wiping, SSD or registry defragmentation | privacy features, SSD wear, no speed gain *(our assessment)* |
| Auto-deleting Downloads by age, wiping all of `workspaceStorage` | data / project-state loss |
| "Sleeping" background processes on a dev machine | breaks daemons, watchers, Docker, agents *(our assessment)* |
| `sparseVhd`, `autoMemoryReclaim=gradual` with systemd + Docker | see §2 |
| `docker volume prune --all`, `system prune -a --volumes` | named volumes = databases |
| `npx playwright uninstall --all` | removes every project's browsers |
| Periodic `hwclock -s` from cron | creates new jumps; fix by event |
| Scare counters / bundled offers | the IObit PUP model |
