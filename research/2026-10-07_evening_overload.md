# The evening overload of 2026-10-07 — measured, and the MCP metric that could not see it

> The owner's goal, 2026-10-07 evening: **twenty Claude sessions must run normally for 24 hours.** On that evening the
> machine degraded again, MCP servers could not reconnect ("connection timed out after 30000ms" on Windows and in WSL),
> and the daemon's own MCP metric — daemon 0.2.0, installed that day — reported every instance idle at 0 % while they
> burned up to 1.7 cores together. This record keeps the numbers that motivated
> [PLAN_twenty_sessions_all_day.md](../todo/PLAN_twenty_sessions_all_day.md), each with HOW and WHEN it was measured.
>
> - **Machine:** Windows 11 Pro 10.0.26300, 24 logical processors, 91.6 GB RAM; WSL 2 with Ubuntu 24.04
>   (47 GB `MemTotal` inside the VM per row L3, 12 GB swap). Local time on this evening is UTC+2.
> - **Subjects:** daemon `daemon-v0.2.0` (main `56d9c0b`, installed that day); ConnectOtherAIs' `coai-mcp` 0.44.0 in WSL
>   (the fix for its start path, ConnectOtherAIs #690, is merged there; 0.44.1 failed to publish, 0.44.2 was being
>   prepared) and 0.41.1 on Windows.
> - **Who measured:** rows marked **C** were measured by the coordinating agent session, read-only, as the login account,
>   and handed to the author of this record, who did not re-run them (the machine was overloaded and WSL reads were
>   deliberately deferred). Rows marked **A** were measured by the author on the Windows side, read-only, at the time
>   given. Nothing was measured as root; nothing was stopped or killed to take any of them.
> - **Related:** [2026-10-06_live_measurements.md](2026-10-06_live_measurements.md) § 6 (the slowdown of the evening
>   before, and `coai-mcp` 0.43.0's restart storm), [module_mcp_servers.md](module_mcp_servers.md) (the metric whose
>   defect § 3 records), [2026-10-02_wsl_resource_baseline.md](2026-10-02_wsl_resource_baseline.md).

## 1. Windows host (rows W1–W5 by C around 19:30Z = 21:30 local; W6–W9 by A at 19:47Z)

| # | Observed | Value | How |
|---|---|---|---|
| W1 | CPU | **100 %** of 24 logical processors | C, Task Manager / performance counter, 19:30Z |
| W2 | memory | 91.6 GB total, **22.5 GB free**; commit **89 of 146 GB** | C, same minute |
| W3 | `vmmemWSL` working set | **29.5 GB** | C, same minute |
| W4 | Defender real-time protection | on | C, same minute |
| W5 | a load test of another agent | ~12 `node -e "for(;;){}"` busy loops added at 19:20Z (21:20 local); they ended by themselves | C, process list; **this is a confound for every 19:20–19:35Z row below** — part of W1 and L1 is that test |
| W6 | CPU after the confound ended | 36.9, 18.5, 23.1 % (three 2 s samples) | A, `Get-Counter '\Processor(_Total)\% Processor Time'`, 19:47:43Z |
| W7 | memory then | 19.7 GB available; committed 100.8 of 156.4 GB (commit limit) | A, `Get-Counter '\Memory\…'`, same samples |
| W8 | `vmmemWSL` then | **32.5 GB** working set | A, `Get-Process vmmemWSL` |
| W9 | `creds-mcp.exe` (CredsForDevs' MCP server, Windows) | **85 processes, 1.32 GB working set together; 66 of them with a parent that no longer exists**, 16 under `wsl.exe`, 3 under `claude.exe`; started between 07:38Z and 16:45Z, 27 of them in the 16Z hour | A, `Get-CimInstance Win32_Process` (name, parent pid, creation date), parent looked up in the same snapshot; read-only. No `coai-mcp.exe` was running on Windows at that minute |

W9 is new evidence and the strongest single Windows finding: an MCP server that is not reaped when its client goes. One
per Claude session would be ~20 on a day of 20 sessions, not 85; two thirds are orphans holding memory. Not measured: their
CPU over time, and whether the parent-gone instances still hold a pipe to anything.

## 2. WSL (rows L1–L8 by C at 19:30Z)

| # | Observed | Value | How |
|---|---|---|---|
| L1 | load average | **36** (1-minute) on 24 CPUs | C, `/proc/loadavg` |
| L2 | PSI | cpu some avg10 **31 %**; io some avg10 5 %, io full avg300 4 %; memory pressure 0 | C, `/proc/pressure/{cpu,io,memory}` |
| L3 | memory | free 15 GB, available 28 GB; swap **9.9 of 12 GB used (2.4 GB left)**; `Committed_AS` 49 GB **above** `MemTotal` 47 GB | C, `/proc/meminfo` |
| L4 | sessions and servers | 10 `claude` sessions, 8 `coai-mcp`, 51 `dotnet` processes | C, `/proc/*/cmdline` |
| L5 | processes in D state | `bash` and `creds.exe` (interop) processes | C, `/proc/*/stat` field 3 |
| L6 | CredsForDevs per session | one `creds-mcp` plus a Windows `creds-mcp.exe` interop process per Claude session | C, process tree |
| L7 | CPU by command over 10 s | `coai-mcp` **6.9 cores** (8 instances of 0.44.0), `dotnet` 4.9, `VBCSCompiler` 4.9 (builds, among them this repository's agents' WSL test runs — paused afterwards), `claude` 0.65 | C, `/proc/<pid>/stat` utime + stime summed per command, two reads 10 s apart, during the W5 confound |
| L8 | why the MCP servers could not reconnect | `coai-mcp` 0.44.0 reads its vault through `creds config` (a Windows interop call) and sweeps its session files BEFORE it answers `initialize`; under this load that took **more than 30 s**, Claude Code killed it at its 30 s MCP connect budget and reported "connection timed out after 30000ms" — on Windows (0.41.1) and in WSL alike | C, the server's start path read in ConnectOtherAIs' source and its run logs; the fix (#690) answers `initialize` first |

## 3. The metric's defect — a 1 s window cannot see a 60 s burst (row M1–M3)

| # | Observed | Value | How |
|---|---|---|---|
| M1 | `coai-mcp` CPU per 5 s slice, all instances together, over 90 s after a calmer minute | 0.72, 0.30, 1.53, 0.13, 0.78, 0.00, 0.00, 0.00, 0.35, 1.47, 1.10, 1.69, 0.25, 0.57, 1.24, 0.26, 0.65, 0.00 **cores** — mean 0.61, peak 1.69, five slices at or near zero: **bursty**, the shape of a periodic sweep (~60 s) | C, `/proc/<pid>/stat` utime + stime, 19 reads 5 s apart, 19:40Z |
| M2 | daemon 0.2.0 `wsl-care status --json`, the same minute | `mcpServers`: all 8 instances `cpuPercent` **0**, kind **`idle`**, `busyWithoutActivityCount` **0** | C, the installed binary, login account |
| M3 | why | `mcpServers.cpuWindowMilliseconds` (200–5000, default 1000) is ONE short window per `status`: two `stat` reads 1 s apart (`src_daemon/src/WslCare.Core/Mcp/McpServerCollector.cs:50-63` at `56d9c0b`). A server that burns in a burst every ~60 s and is quiet between is caught only when the second happens to fall inside a burst; most calls read 0 — and the kind follows the reading, so the metric reports exactly the state the owner was hit by as "idle" | author, reading the code at `56d9c0b` against M1 |

**What M1–M3 license, and what they do not.** They show one evening's servers at 0.44.0 bursting with a period near a
minute, and one `status` call missing it. They do not establish the period's exact length, that every server version
bursts, or how often a 1 s window would catch a burst over a day (that is the duty cycle, not measured). The fix in plan
story S1 does not depend on the period: it measures over the real interval since the previous sample.

## 4. Not measured yet (the plan's soak campaign, S8, owns these)

- Restarts (`mcp.starts`) under daemon 0.2.0 — no full run had recorded a sample yet.
- The same machine with twenty sessions for a whole day: what the peak is, when it comes, and which of W/L above recur.
- Whether `nice` has any effect inside WSL (plan S4 measures it before building on it).
- The CPU of the orphaned `creds-mcp.exe` processes of W9 over time.
