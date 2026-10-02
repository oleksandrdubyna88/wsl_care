# WSL resource baseline — 2026-10-02

> Status: **measured 2026-10-02**, read-only diagnostics, nothing was changed on the machine.
> Scripts that produced every number below: [diagnostics/](diagnostics/).
>
> Plan built on this: [PLAN_wsl_care_daemon.md](../todo/PLAN_wsl_care_daemon.md).

## Symptom

WSL Ubuntu "eats a lot of resources and by the end of the day is terribly slow". The machine is shut down
every night, so every day starts from a fresh VM and degrades over the working day.

## Machine

| | |
|---|---|
| Host | Windows 11 Pro 10.0.26200, AMD Ryzen AI 9 HX 370 (12C/24T), **91.6 GB RAM**, pagefile 52 GB |
| WSL | 2.7.10.0, kernel 6.18.33.2-microsoft-standard-WSL2, `networkingMode=mirrored` |
| Distros (all running, all WSL2, **one shared VM and kernel**) | `Ubuntu` (24.04.1, default, systemd=true), `Ubuntu-26.04`, `docker-desktop` |
| VM memory ceiling | 46 908 MB (`hv_balloon: Max. dynamic memory size`) — the default 50 % of host RAM; `.wslconfig` sets no `memory=` |
| VM swap | 12 GB |
| `.wslconfig` | only `networkingMode=mirrored` — **no** `memory`, `autoMemoryReclaim`, `sparseVhd` |
| `vmmemWSL` 3 minutes after boot | 11.3 GB working set |

## Log sources and their reach

The request was "look at the logs for the last 2 months". What actually exists:

| Source | Reach | Note |
|---|---|---|
| rsyslog `/var/log/syslog*`, `kern.log*` (Ubuntu) | **2026-08-31 → now** (~5 weeks) | the only long history; contains NUL bytes from unclean shutdowns — read with `grep -a` |
| journald (both Ubuntu distros) | **2026-10-01 23:49 → now only** | 380 MB on disk but history destroyed, see *Clock jumps* below |
| Windows System log | 2026-09-26 → now | 20 MB circular log |
| Windows Application log | 2026-09-08 → now | 20 MB circular log |
| Docker Desktop `%LOCALAPPDATA%\Docker\log` | 39 MB, rolling 1 MB files, mostly API chatter | no memory/OOM records |
| sysstat / atop | **not installed** | there is no process-level history at all |

Consequence: per-process memory over a day **cannot be reconstructed** — the first job of any daemon is
to start recording it.

## Finding 1 — the VM runs out of memory by the evening (primary cause)

`page allocation failure` happened three times: 2026-09-09 15:41 (`kworker`, order 7), 2026-09-16 17:38
(`kworker`, order 7), 2026-10-01 18:36 (`find`, order 4, twice, inside `v9fs_dir_readdir_dotl` — a
directory walk over `/mnt/*` through 9p). Never an OOM kill — the VM does not crash, it suffocates.

Memory dump at 2026-10-01 18:36 (VM total 46 GB, 4 KiB pages):

| bucket | pages | ≈ GB |
|---|---|---|
| `inactive_anon` | 5 553 683 | **21.2** |
| `active_anon` | 204 843 | 0.8 |
| page cache total | 4 968 477 | **19.0** (`active_file` 13.8, `inactive_file` 4.2) |
| `slab_reclaimable` / `slab_unreclaimable` | 492 343 / 148 091 | 1.9 / 0.6 |
| `shmem` | 187 499 | 0.7 |
| `unevictable` | 245 559 | 0.9 |
| `free` | 69 510 | **0.27** |
| swap used | — | 2.2 of 12 |

Buddy allocator, zone Normal: `12594*4kB 5327*8kB 948*16kB 173*32kB 0*64kB …` — **no free block of
64 KB or larger**: memory is not only full but fragmented, and 9p asks for large contiguous buffers.

Reading:
- ~22 GB of anonymous memory is *inactive* — processes that allocated it hours ago and are not touching
  it: idle build servers, language servers, extension hosts, finished-but-alive test hosts, long-running
  containers. Which ones is exactly what was not recorded.
- ~19 GB of page cache is never handed back to Windows: without `autoMemoryReclaim` WSL keeps the cache
  until the VM stops.
- Together they fill the 46 GB ceiling; Windows sees `vmmemWSL` at ~46 GB.

Caveat: Ubuntu's `ps` sees only its own PID namespace. Processes of `Ubuntu-26.04` and of the
`docker-desktop` distro (all containers) live in the same kernel and count in these totals, but are
invisible to Ubuntu's `ps`. Containers are visible through `docker stats`.

## Finding 2 — Docker / Testcontainers churn and leftovers

- Container network churn in `kern.log`: ~11 400 `veth … disabled` events; since **2026-09-18** some
  2 000–3 000 container starts per day (test suites with Testcontainers).
- `docker system df` (2026-10-02):

| type | total | active | size | reclaimable |
|---|---|---|---|---|
| Images | 101 | 29 | 41.1 GB | 16.0 GB |
| Containers | 86 | 10 | 0.8 GB | 0.8 GB |
| Local volumes | **474** | 80 | **104.9 GB** | **71.2 GB** |
| Build cache | 2 190 | 0 | 34.7 GB | 20.9 GB |

- `docker_data.vhdx` = **123.3 GB** on `C:`.

## Finding 3 — disk

| item | size |
|---|---|
| `Ubuntu` `ext4.vhdx` | 142 GB (122 GB used inside) |
| `Ubuntu-26.04` `ext4.vhdx` | 70.6 GB |
| `~/git` | 89 GB, of which `~/git/_wt` (worktrees) **41 GB** |
| `bin/` + `obj/` under `~/git`, `~/coai-*` | ~9.5 GB |
| `node_modules` dirs | 111 |
| `~/.npm` | 7.0 GB |
| `~/.vscode-server` | 4.3 GB (`data` 2.5 GB) |
| `~/.nuget/packages` | 3.7 GB |
| `~/.cache` | 3.1 GB (`uicm` 1.3, `ms-playwright` 1.3) |
| `~/.claude` | 1.6 GB (`projects` 1.4) |
| `~/coai-514`, `~/coai-462` | 1.4 GB each |

VHDX files never shrink by themselves; space freed inside stays allocated on `C:`.

## Finding 4 — clock jumps destroy the journal

- ~1 700 "Clock change detected" (`systemd-resolved`) per day, every day of the period, up to 3 600 on
  some days; plus "Time jumped backwards, rotating" from `systemd-journald`.
- On 2026-10-01 between 23:52 and 00:06 journald rotated a new 8 MB file **every ~30 seconds**; with the
  default cap of 100 files that evicted all earlier history. This is why journald starts at 2026-10-01.
- `resolved` flushes its DNS cache on every jump.
- Hypothesis (not verified): several time keepers adjust the **one shared kernel clock** — WSL's own host
  sync, `systemd-timesyncd` in `Ubuntu` and `chronyd` in `Ubuntu-26.04`.

## Finding 5 — log noise and boot

- `wsl-pro-service`: 24 730 cycles of "could not connect to Windows Agent … .ubuntupro/.address" — ~4 000
  syslog lines per day, a reconnect every ~20 s. The Ubuntu Pro Windows agent is not installed.
- `Microsoft.VisualStudio.Code.Server` (C# Dev Kit ServiceHub) wrote **236 000** syslog lines;
  `Microsoft.CodeAnalysis.LanguageServer` 21 700.
- Boot: `WaitForBootProcess: /sbin/init failed to start within 10000ms` on 7 days; systemd startup 4–13 s;
  `getty@tty1` failed 49× (timeout), `snapd` 15×. Lingering for `jinx` is on (an earlier fix, see the
  `XDG_RUNTIME_DIR` incident).
- `snapd` runs with only `core22` + `snapd` installed (no user snaps); `snapfuse` uses CPU at boot.
- `misc dxg: dxgkio_query_adapter_info: Ioctl failed` ×600 — GPU paravirtualisation noise.

## Finding 6 — AI-agent folders grow without a limit

Measured 2026-10-02 (`ai_agents.sh` for WSL, a PowerShell inventory for Windows):

| Side | CLIs on `PATH` | Data folders | Sessions |
|---|---|---|---|
| WSL | `claude` 2.1.223 (nvm), `codex` 0.155.0 (nvm), `gemini` (the Windows npm shim) | `~/.claude` 1.6 GB, `~/.gemini` 0.9 GB (`antigravity-cli` 666 MB), `~/.codex` 174 MB, `~/.rovodev` 6.7 MB, `~/.copilot`, `~/.cache/antigravity` | Claude: 32 project folders, 732 session files (2026-08-31 → now); Codex: 33 |
| Windows | `claude`, `codex`, `gemini`, `agy` (Antigravity), `ollama` | `.claude` 2.8 GB, `AnthropicClaude` 1.2 GB, `.gemini` 0.5 GB, `.codex` 0.45 GB, `Roaming\Claude` 0.3 GB | Claude: **477** project folders, **2 631** session files; Codex: 385 |

- One Claude project, `-home-jinx-git-scoreMeter`, holds **1.4 GB** — almost all of WSL's `~/.claude`.
- Every coai gate run leaves its own Claude project folder (`…-coai-wt-<hash>-r1`), so the count only grows.
- Agents live on both sides; the Windows side is larger.

## Windows side (short reach)

- No `Resource-Exhaustion-Detector` 2004 (low virtual memory) events since 2026-09-26.
- Bugcheck **0x19C** (`WIN32K_POWER_WATCHDOG_TIMEOUT`, param 0x50) on 2026-09-29 13:30, and an
  unexplained power loss (Kernel-Power 41, code 0) on 2026-09-27 — most likely display/power driver, not
  WSL; noted only so it is not mistaken for a WSL symptom later.
- `Hyper-V-VmSwitch` event 15 ×88 ("Failed to restore configuration for port") — mirrored-networking noise.

## What is NOT known yet

1. Which processes hold the ~22 GB of inactive anonymous memory by the evening.
2. Whether `autoMemoryReclaim` alone would be enough.
3. Whether the clock jumps really come from competing time daemons.
4. How much `Ubuntu-26.04` costs while it idles.
