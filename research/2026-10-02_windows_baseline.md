# Windows baseline — why the workstation lags after ~3 months (2026-10-02)

> Status: **measured 2026-10-02 ~10:25–10:50 CEST**, read-only, **not elevated** (items needing admin are
> marked). Script: [diagnostics/win_collect.ps1](diagnostics/win_collect.ps1) plus the follow-up probes
> quoted below. Nothing was changed on the machine.
>
> Plan built on this: [PLAN_windows_care.md](../todo/PLAN_windows_care.md). Companion:
> [2026-10-02_competitor_survey.md](2026-10-02_competitor_survey.md).

## Machine

| | |
|---|---|
| OS | Windows 11 Pro 10.0.26300, **installed 2026-07-16** (≈ 2.5 months ago) |
| CPU / RAM | Ryzen AI 9 HX 370 (12C/24T), **91.6 GB** RAM, pagefile 52 GB, commit limit 143.6 GB |
| GPU | AMD Radeon AI PRO R9700 (32 GB, discrete) + Radeon 890M (iGPU); driver 32.0.31041.1004 (2026-08-17) |
| Disks | one NVMe (Solidigm 3.5 TB): `C:` NTFS 2.4 TB (1.1 TB free), `D:` ReFS "git" 195 GB, `F:` ReFS "Models" 244 GB; plus `E:` — a 30 MB FAT volume on the **ClawsKey keyboard's USB storage** |
| Power | plan *Balanced*; **Fast Startup ON** (`HiberbootEnabled=1`), `hiberfil.sys` 36.6 GB |
| Reliability index | **3.5 / 10** (falling from 5.2 on 2026-10-01) |

## The headline: memory is gone 55 minutes after boot

Measured at uptime 0:55 (boot 09:29):

| | value |
|---|---|
| Available RAM | **2.3 GB of 91.6** |
| Committed | 112.8 of 143.6 GB (paging into the 52 GB pagefile) |
| CPU | 100 % at the sample |
| **`vmmemWSL`** | **40.5 GB** private (21 GB working set) — the WSL VM, see the WSL baseline |
| **`llama-server`** (Ollama) | **29.9 GB private, 40.4 GB working set**, 1 357 CPU-seconds in 40 minutes |
| everything else | ~20 GB (34 `msedgewebview2` 3.5 GB, 22 `Code` 4.6 GB, 30 `node` 4.0 GB, `msedge` 2.1 GB, 3 `claude` 1.8 GB, …) |

**Two consumers hold ~70 GB of 92.** Windows itself is left with almost nothing, so every app switch pages.
That alone explains "it lags"; the months add the rest below.

### Ollama keeps a 26 GB model forever — and a host-side copy

- `ollama ps`: `Qwen3.5-35B-A3B-Q5_vk128` — 26 GB, *100 % GPU*, context **131 072**, until **Forever**.
- Machine environment `OLLAMA_KEEP_ALIVE=-1` → never unloaded; user env `OLLAMA_NUM_PARALLEL=4`,
  `OLLAMA_KV_CACHE_TYPE=q8_0`, `OLLAMA_FLASH_ATTENTION=1`, `OLLAMA_MODELS=F:\OllamaModels` (89 GB of models).
- Although the model runs on the R9700, `llama-server` still holds ~30 GB of **system** RAM (private bytes) —
  the Vulkan backend's host-side buffers for weights and a 131k-token KV cache.
- Ollama, Ollama app and Docker Desktop all start with Windows.

### WSL

Covered in [2026-10-02_wsl_resource_baseline.md](2026-10-02_wsl_resource_baseline.md): the VM ceiling is
46 GB and it fills; at 55 minutes it already held 40 GB.

## The months: what accumulated since the 2026-07-16 install

### Kernel and drivers — never reset by "shut down"

- **Fast Startup is on.** "Shut down" then hibernates the kernel session and drivers; only restart gives a
  clean kernel. Driver leaks and kernel state therefore survive the nightly shutdown for days.
- **Non-paged pool: 5.5 GB** at uptime 0:55 (paged pool 1.3 GB). Typical for a desktop is well under
  1 GB; 5.5 GB this early points to a driver allocating heavily. Suspects by what is installed: Razer
  (Chroma SDK ×3 services, Game Manager, AppEngine ×10 processes), Interhaptics HapticService, UniFi
  Identity (2 services), **two OpenVPN stacks** (OpenVPN Connect + OpenVPN GUI, 3 services, their
  adapters), Hyper-V vSwitch (mirrored networking; 99 "failed to restore port" events), AMD. The tags were
  then read without admin — see *Non-paged pool, attributed* below: the bulk is NIC receive buffers, not those suspects.
- Total handles 273 676 across 562 processes; `explorer` 8 182 and `SnagitEditor` 8 148 handles.

#### Non-paged pool, attributed (measured 2026-10-02 ~11:00, uptime 105 min, unelevated)

`NtQuerySystemInformation(SystemPoolTagInformation)` answers without admin
([diagnostics/pooltags.ps1](diagnostics/pooltags.ps1)); each tag's owner was found by scanning all
899 `.sys` files under `System32\drivers` and the DriverStore for the 4-byte tag.

| Tag | NP MB | Live allocations | Owner | What it is |
|---|---|---|---|---|
| `cxbm` | **2 252** | 197 120 — **never freed** | `netadaptercx.sys` (+ `netcxrd.sys`) | NetAdapterCx **buffer manager**: receive buffers of the NIC drivers built on it |
| `NxRx` | 133 | 229 384 — never freed | `netadaptercx.sys` | NetAdapterCx receive-queue bookkeeping |
| `DAL3` | 593 | 6 993 | `amdkmdag.sys` (AMD display driver, DAL) | display abstraction layer of the R9700/890M driver |
| `SW03` | 521 | 32 (≈ 16 MB each) | **not found as a literal** in any driver | built at run time; large contiguous chunks — unattributed |
| `EtwB` | 188 | 2 145 | ETW (named in `Usb4HostRouter.sys`) | trace-session buffers |
| `HalD` | 167 | 261 | HAL DMA (`acpi.sys`, `amdppm.sys`, …) | DMA adapters |
| `smNp` + `smCB` + `smBt` | 306 | — | store manager | memory compression |
| `VdMm` | 101 | 1 094 | dxgkrnl | video memory manager |
| everything else | ~1 000 | | | |

**Who uses NetAdapterCx/WiFiCx here:** two PCI Realtek Gaming 2.5GbE controllers (`rtcx21x64.sys`,
one Up at 2.5 Gbps, one Disconnected), a Realtek USB 2.5GbE (`rtucx22x64.sys`, Disconnected) and the
MediaTek Wi-Fi 7 MT7925 (`mtkwecx.sys`, one physical card exposing three present interfaces, all
Disconnected). **~2.4 GB of the 5.3 GB is receive buffers pre-allocated by these adapters**, including
the ones with no cable or no association. Which adapter holds how much cannot be read from the tag; it
needs the elevated per-adapter experiment in the Windows plan §5.

**Is it a leak?** Not within the morning: 5.49 GB at 10:25, 5.36 GB at ~11:00 — flat. The allocations
are made once (allocs = live) and kept. It is *reservation*, not growth. Whether it grows across days
with Fast Startup on is what the daemon's per-run tag snapshot will show.

**What helps, in order:** disable the adapters that are never used (the second PCI port, the USB
dongle, Wi-Fi if Ethernet is the only link) — each drops its receive pool; then the Realtek driver's
*Receive Buffers* (512 today) can be lowered on the one in use. `SW03` stays unattributed until it can
be tied to a component (candidate check: its size with WSL stopped).

### `%TEMP%` — 35 GB, 155 105 entries

| What | Size | Note |
|---|---|---|
| 4 × `<GUID>\swap.vhdx` from earlier WSL boots (2026-09-28 … 10-01) | **15.0 GB** | WSL's swap disk; left behind by unclean VM stops; only the current one is in use |
| `claude\` — 497 work folders of Claude Code sessions/builds (since 2026-07-27) | **6.0 GB** | 29 348 files |
| `fj4r5vyi\` — Visual Studio / .NET workload installer payloads (2026-09-09) | 4.3 GB | 965 files |
| **23 572 random-name folders** (`xxxxxxxx.xxx`, `Path.GetRandomFileName` shape) | — | **23 284 are empty**; created at 250–2 300 per day (2 313 on 2026-10-01); the non-empty ones hold `microsoft.net.workloads.*.msi.x64` NuGet payloads — the .NET SDK's workload update/advertising downloads plus test runs that never delete their temp folders |
| `DiagOutputDir`, `node-compile-cache` (39 201 files), `Roslyn`, … | ~1.5 GB | |
| files older than 7 days | 6.9 GB / 15 474 files | |

Tens of thousands of entries in `%TEMP%` slow every tool that enumerates it, Defender included.

### Auto-start load

Run keys / Startup: Ollama, OneDrive (+ `Microsoft.Lists` sync service), AMD Noise Suppression (×3
entries), **Razer AppEngine**, Teams, Viber, Docker Desktop, UniFi Endpoint, **OpenVPN Connect and
OpenVPN GUI**, Edge auto-launch, Snagit, Realtek audio, PowerToys (task). Running non-Microsoft
services: OpenVPN ×3, Razer ×4, UniFi ×2, Interhaptics, TechSmith, AMD ×2, RGS updater. Claude desktop is
already disabled.

### Crash loops and noisy failures (System since 2026-09-26, Application since 2026-09-08 — 20 MB logs)

| Signal | Count | Meaning |
|---|---|---|
| `.NET Runtime 1026` — **`coai-bugs.exe` unhandled exception** | **441** | a service crash-looping (each crash = WER report + restart); see the coai-bugs SQLite-under-load note |
| `HttpEvent 15005` — cannot bind `127.0.0.1:50118` | 387 | some http.sys listener retrying forever |
| `disk 11` — controller error on `Harddisk1` | 79 (~15/day) | **the ClawsKey keyboard's USB storage**, not the NVMe; a stalling USB mass-storage device can freeze Explorer |
| `DistributedCOM 10016/10010` | 173 | noise |
| `FilterManager 3` — failed to attach to a volume | 30 | filter drivers vs. the ReFS/virtual volumes |
| `Kernel-PnP 219` — YubiKey `WUDFRd` failed | 27 | driver load failure at boot |
| `Volsnap 25` — shadow copies of `C:` deleted, storage could not grow | 1 | heavy I/O |
| `BitLocker 24635/24609` — TPM PCRs did not match at boot | 2 | after firmware/driver updates; keep the recovery key at hand |
| Bugcheck `0x19C` (2026-09-29), Kernel-Power 41 (09-27, 09-29) | 3 | power watchdog — display/power driver |
| `Application Error` — SnagitCapture (module `ShaferFilechck_Secondary`) | 8 | |
| `openvpnserv` exits | 17 | two VPN stacks |

### Software and caches

- **430 installed programs**; .NET piles: 5 runtimes, 5 Windows Desktop runtimes, 8 + 5 Emscripten
  workload manifests, MacCatalyst/Mono workload manifests for 10.x and 11 previews.
- `C:\Windows\Installer` 4.75 GB (**never touch**), `SoftwareDistribution\Download` 2.5 GB, NuGet
  `v3-cache` 3.9 GB, NuGet packages 2.2 GB, pip 0.5 GB, VS Code `CachedExtensionVSIXs` 0.9 GB and
  `CachedData` 0.4 GB, Teams 2.5 GB, Downloads 8.7 GB, Edge cache 0.4 GB.
- Docker Desktop `docker_data.vhdx` 123 GB (≈ 70 GB free inside after the 2026-10-02 cleanup — returns to
  `C:` only by compaction). `C:` has 1.1 TB free, so disk space is **not** a lag cause.
- AI agents: see the WSL baseline Finding 6 (Windows `.claude` 2.8 GB, 2 631 sessions).
- Process duplication: 16 `creds-mcp`, 9 `creds`, 22 `wsl.exe`, 17 `wslhost`, 84 `conhost`, 30 `node`,
  22 `Code` — MCP servers and shells spawned per agent session that may outlive it. Parents of
  `creds-mcp`: **13 under one `wsl.exe` (pid 9932)**, one under each of 3 running `claude.exe` (+ 1 more) —
  sessions inside WSL start the Windows `creds-mcp.exe` through interop and the copies pile up under the
  relay instead of exiting with their session. Worth an issue in `dew_flow_creds_for_devs`.

### Not measured (needs admin) — first step of the plan

Defender exclusions and whether `D:`/`F:` are **trusted Dev Drives** with performance mode
(`fsutil devdrv query` → access denied), memory compression / prefetch (`Get-MMAgent`), the non-paged pool
tag (`poolmon`), the search index size and scope, WinSxS (`DISM /AnalyzeComponentStore`).

## Ranked fixes — what will help, most first

| # | Fix | Expected effect | Kind | In [PLAN_windows_care.md](../todo/PLAN_windows_care.md) |
|---|---|---|---|---|
| W1 | **Ollama: `OLLAMA_KEEP_ALIVE` from `-1` to e.g. `15m`**, and/or a smaller default context than 131 072; unload by button (`ollama stop <model>`) | frees ~30–40 GB of system RAM whenever the model is idle; reload from `F:` takes seconds | config + button | §4 W-A1 + §4 configuration advisors |
| W2 | **Cap the WSL VM** (`memory=` in `.wslconfig`, e.g. 32–36 GB) together with the WSL plan's fragmentation and cache actions | Windows keeps ≥ 20 GB even when WSL is busy | config | §4 configuration advisors (shown, never written — user decision) |
| W3 | **Disable Fast Startup** (`HiberbootEnabled=0`; hibernate itself may stay) | every shutdown gives a clean kernel and drivers — so a driver leak, IF one exists, could no longer accumulate across days; not measured: the pool was flat over the two samples above, and growth across days is for the daemon's per-run tag snapshot to show | config (admin) | §4 configuration advisors |
| W4 | **Non-paged pool: the owner is now known** — ~2.4 GB NetAdapterCx receive buffers of the Realtek 2.5GbE ×2, Realtek USB and MediaTek Wi-Fi 7 adapters, 0.6 GB AMD display driver; disable unused adapters, lower *Receive Buffers* on the one in use; split per adapter needs the elevated experiment | up to ~2 GB of kernel memory back; flat, not a leak, as far as measured | user decision per adapter | §3 Memory (pool tags, every run) + §5 elevated per-adapter split |
| W5 | **`%TEMP%` hygiene**: stale `swap.vhdx` (15 GB), empty random folders (23 k), files older than N days — **every 4 h; never anything under `%TEMP%\claude\` (user decision)** | −20+ GB, −150 k entries; faster enumeration and scanning | auto (safe set) | §4 W-A2 |
| W6 | **Stop the .NET workload leakage**: `DOTNET_CLI_WORKLOAD_UPDATE_NOTIFY_DISABLE=1`, `dotnet workload clean`; remove unused SDK/workload previews | stops ~300–2 000 new temp folders a day | config + button | §4 W-A14 |
| W7 | **Fix `coai-bugs.exe` crash loop** (441 crashes) | stops repeated WER work and restarts | code fix in that repo | §3 Event signals (crash loops counted); the fix belongs to that repository |
| W8 | **Auto-start diet** (Autoruns model, disable not delete): one VPN client instead of two, Razer AppEngine/Chroma only if the peripherals need them, Viber/Teams/Slack/Snagit/Edge auto-launch on demand | fewer resident processes, services and drivers | button, user picks | §3 Auto-start + §4 W-A10 |
| W9 | **Orphaned MCP/agent processes** (16 `creds-mcp`, …) whose parent is gone | memory and handles back | report + button | §3 Orphans + §4 W-A11 |
| ~~W10~~ | ~~ClawsKey USB storage errors~~ — **dropped (user, 2026-10-02)**: nothing can be done from here; still counted | — | — | §4 *Dropped* (still counted) |
| W11 | **Dev Drive / Defender audit** (admin): `D:`/`F:` trusted, performance mode on; move `npm_config_cache`, `NUGET_PACKAGES`, pip caches to the Dev Drive | faster builds, less real-time scanning on `C:` | report + wizard | §4 configuration advisors + §5 |
| W12 | **Windows Search scope**: exclude `D:\` repos, `node_modules`, `%TEMP%` | indexer stops chasing build churn | button | §4 configuration advisors |
| W13 | Caches: NuGet `http-cache` (3.9 GB), `SoftwareDistribution\Download`, Delivery Optimization, VS Code caches while Code is closed, `CachedExtensionVSIXs` | a few GB | auto / button | §4 W-A4, W-A5, W-A7 |
| W14 | Compact `docker_data.vhdx` and the distro VHDX (WSL plan Phase 0.3) | ~70 GB back on `C:` | button | §4 W-A12 |
| W15 | Power mode *Best performance* on this desktop | lower latency under load | report | §4 configuration advisors (power mode) |
| W16 | Larger event logs (System/Application 20 MB → 100 MB) | history long enough to diagnose the next incident (this one only reaches back 6 days) | config (admin) | §4 configuration advisors |

What **not** to do here: registry cleaning, RAM "boosters", Prefetch deletion, bulk service disabling,
Defender exclusions for whole drives, third-party driver updaters — see the competitor survey §3.
