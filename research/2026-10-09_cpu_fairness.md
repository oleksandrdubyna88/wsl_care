# CPU fairness inside this WSL, measured (2026-10-09): `nice` works, a `systemd --user` scope backfires

> E14 S4 ([PLAN_twenty_sessions_all_day.md](../todo/PLAN_twenty_sessions_all_day.md) § *S4*) opened with a belief: "`nice`
> has no effect in this WSL (no `cpu` controller delegated below `system.slice`)", and a design built on it — a systemd
> drop-in delegating `cpu io memory pids` to `user@.service`, and `wsl-care low -- <cmd…>` running a command in a
> `systemd-run --user --scope` with a low `CPUWeight`. The plan said to measure first. This record is that measurement.
> **The belief is false on this machine:** its first half ("no effect") is false; its bracketed reason is literally true
> (`system.slice` delegates no `cpu`, G4) but irrelevant, because the sessions live in `/init.scope` and the user manager
> has `cpu` already (P1–P4, G7, G8). And the planned design made a "low" job run 7–21 times FASTER than a session's own
> process (9× in E4), not slower.
>
> - **Machine:** Windows 11 Pro 10.0.26300, 24 logical processors; WSL 2, kernel `6.18.33.2-microsoft-standard-WSL2`,
>   Ubuntu with systemd 255 (255.4-1ubuntu8.17), daemon `0.3.0+609ba17` installed.
> - **Who, when, how:** the author (an agent session), 2026-10-09 19:15Z–19:21Z, 19:54Z–19:56Z (§ 3b, the consultant's checks) and 20:02Z (§ 3c, the plan round's check), as the login account (uid 1000), through
>   script files run with `wsl.exe -d Ubuntu -- sh <file>`. Nothing as root, nothing deleted, nothing written outside `/tmp` (§ 3c's build folder). The busy loops
>   were pinned to ONE CPU (`taskset -c 23`), each under `timeout 20`, and every one was ended by the pid the script
>   started; the transient scopes went away with their processes. Load at the start: 4.47 / 4.78 / 5.17 on 24 CPUs, PSI cpu
>   some avg10 0.21 %.
> - **Related:** [2026-10-07_evening_overload.md](2026-10-07_evening_overload.md) (the overload S4 answers: `dotnet` 4.9 and
>   `VBCSCompiler` 4.9 cores beside the sessions they serve), [module_daemon.md](module_daemon.md).

## 1. The cgroup tree (read 19:15Z)

| # | What | Value |
|---|---|---|
| G1 | hierarchy | cgroup v2 only (`cgroup2 /sys/fs/cgroup … nsdelegate`) |
| G2 | `/sys/fs/cgroup/cgroup.controllers` | `cpuset cpu io memory hugetlb pids rdma` |
| G3 | `/` `cgroup.subtree_control` | `cpuset cpu io memory hugetlb pids rdma` |
| G4 | `system.slice` `subtree_control` | `memory pids` |
| G5 | `user.slice` `subtree_control` | `cpu memory pids` |
| G6 | `user-1000.slice` controllers / `subtree_control` | `cpu memory pids` / `cpu memory pids` |
| G7 | `user@1000.service` controllers / `subtree_control` | `cpu memory pids` / `cpu memory pids` |
| G8 | `systemctl show user@1000.service -p Delegate -p DelegateControllers` | `Delegate=yes`, `DelegateControllers=cpu memory pids` — systemd 255's default; no drop-in exists (`/etc/systemd/system/user@.service.d` absent) |
| G9 | `cpu.weight` of `init.scope`, `user.slice`, `system.slice` | 100, 100, 100 |
| G10 | autogroup | `/proc/sys/kernel/sched_autogroup_enabled` and `/proc/self/autogroup` absent: the kernel has no autogroup |
| G11 | block-device I/O schedulers | `[none]` on every `sd*` and `loop*` device (no BFQ, no mq-deadline) |
| G12 | `app.slice` (under `user@1000.service`) `subtree_control`, read 19:16Z | `memory pids` — no `cpu` until a child asks for a weight |
| G13 | `/sys/fs/cgroup/io.cost.qos`, `io.cost.model`, read 19:54Z | both absent: the `io.cost` controller is not configured |

**So `cpu` IS delegated to the user manager already** (G7, G8) — the drop-in the plan proposed would add nothing for CPU.
`io` is not delegated below `/` (G4–G7), and with the `none` scheduler on every disk (G11) there is nothing for an I/O
weight or an `ionice` class to act on: `io.weight` needs BFQ or the `io.cost` controller, and `io.cost` is not configured
either (G13); I/O priority classes need an I/O scheduler that honours them (BFQ, or mq-deadline since kernel 5.14), and
every device here runs `none`. (The kernel side is documentation, not measured here; the consultant corrected an earlier
"only BFQ", the own review an earlier "I/O priorities need BFQ".)

## 2. Where the sessions live (read 19:16Z)

| # | What | Value |
|---|---|---|
| P1 | processes per cgroup | **159 in `/init.scope`**; the next largest 6 (`systemd-udevd`); `user@1000.service` holds 2 (its own init) plus one `dbus` |
| P2 | the families that matter | `claude` 6, VS Code server `MainThread` 26, `coai-mcp` 7, `dotnet` 4, `sh` 13, `bash` 5 — **all in `/init.scope`** |
| P3 | a `claude` process's ancestry | `claude` → `MainThread` (VS Code server) → … `sh` → `Relay(…)` → `SessionLeader` → `init-systemd(Ub…)`, every one in `/init.scope` |
| P4 | a shell started by `wsl.exe -d Ubuntu --` | `0::/init.scope` |

WSL starts everything it launches (`wsl.exe`, the VS Code server, through them every agent session and every build or test
the agents run) in `/init.scope`, not in a login session under `user.slice`. Only `loginctl` sessions (here `session-7`, a
`pts` login) land in `user.slice`.

## 3. Two busy loops on one CPU (19:16:59Z–19:20:18Z)

Each row: two `sh -c 'while :; do :; done'` loops started together, pinned to CPU 23, read from `/proc/<pid>/stat`
(`utime + stime`, `CLK_TCK` 100) over the same 18 s window (started 1 s after the loops). The kernel's weights: nice 0 =
1024, nice 10 = 110, nice 19 = 15, `SCHED_IDLE` = 3.

| # | Loop A | Loop B | A's share | B's share | A : B | Expected from the weights |
|---|---|---|---|---|---|---|
| E1 | `/init.scope`, nice 0 | `/init.scope`, nice 19 | 98.2 % | 1.4 % | **68.1 : 1** | 1024 / 15 = 68.3 |
| E2 | `systemd-run --user --scope -p CPUWeight=100`, nice 10 | the same with `CPUWeight=10`, nice 10 | 91.0 % | 9.1 % | **10.0 : 1** | 100 / 10 = 10 |
| E3 | `systemd-run --user --scope` (no weight), nice 0 | the same, nice 19 | 98.0 % | 1.4 % | 67.9 : 1 | 1024 / 15 = 68.3: the scopes had no `cpu.weight` file (the script read none) and `app.slice` enables no `cpu` (G12), so the two loops shared one CPU group and nice decided |
| E4 | **`/init.scope`, nice 10 — where the sessions are** | **a "low" scope, `CPUWeight=10`, nice 10 — the plan's design** | **9.7 %** | **89.6 %** | **0.1 : 1** | the design assumed ≈ 10 : 1 for A |
| E5 | `/init.scope`, nice 0 | a "low" scope, `CPUWeight=10`, **nice 19** | 23.7 % | 76.2 % | 0.3 : 1 | — |
| E6 | `/init.scope`, nice 0 | `/init.scope`, `chrt -i 0` (`SCHED_IDLE`) | 98.8 % | 0.3 % | **356 : 1** | 1024 / 3 = 341 |
| E7 | `/init.scope`, nice 19 | `/init.scope`, `SCHED_IDLE` | 83.1 % | 16.6 % | 5.0 : 1 | 15 / 3 = 5 |

(The CPU's remaining share in each row was 0.9 % or less: nothing else ran on CPU 23.)

### 3b. The consultant's checks (19:54Z–19:56Z)

| # | What | Value |
|---|---|---|
| C1 | `cpu.max` of `init.scope`, `user.slice`, `user-1000.slice`, `user@1000.service`, `app.slice` | `max 100000` on all five |
| C2 | `nr_throttled` / `throttled_usec` of `init.scope` and `user.slice`, before and after E4b and E8 | 0 / 0 — no bandwidth limit is in play |
| E4b | E4 again: `/init.scope` nice 10 vs the "low" scope (`CPUWeight=10`, nice 10), both on CPU 23 | **4.5 %** vs **94.4 %** |
| E8 | the same pair, plus a third `/init.scope` nice-10 loop on CPU **22** | **12.9 %** vs **86.7 %** |
| B1 | MSBuild processes alive at 19:55Z | three node-reuse workers, `dotnet …/MSBuild.dll /nodemode:1 /nodeReuse:true /low:false`, each at **nice 0** |
| B2 | the SDK's own help (10.0.112, in WSL) | `dotnet build --disable-build-servers` "Force the command to ignore any persistent build servers"; `dotnet msbuild -lowPriority` (`-low`) "Causes MSBuild to run at low process priority" |
| B3 | `dotnet <cmd> --help` (SDK 10.0.400, Windows, inside this repository whose `global.json` selects the Microsoft Testing Platform runner), read 2026-10-10 by the own review's finding | `--disable-build-servers` listed by `build`, `publish`, `pack`, `run`; **NOT by `test`** — under MTP `dotnet test` forwards an unknown option to the test application, which may fail the run |
| W1 | Windows, PowerShell 7: `Get-Command start` | an **alias of `Start-Process`**, not cmd's `start` |
| W2 | Windows: `cmd /c 'start "" /b /wait /belownormal cmd /c exit 37'` | exit **0** — the child's 37 is lost; `Start-Process … -Wait -PassThru` kept 37 |

### 3c. A real niced build (20:02:42Z–20:02:51Z; the coai plan round's finding)

One console project (`net10.0`, one `Program.cs`) built as
`nice -n 19 timeout 240 dotnet build --disable-build-servers`, everything under a new folder in `/tmp` (`HOME`,
`DOTNET_CLI_HOME` and `NUGET_PACKAGES` pointed there, telemetry off). Every process carrying that `HOME` was sampled every
0.3 s while the build ran.

| # | What | Value |
|---|---|---|
| R1 | the build | succeeded, 8.0 s, exit 0 |
| R2 | the processes that carried it | `timeout` (0 ticks), the `dotnet build` driver (**3.96 s** CPU), `csc` from `sdk/10.0.112/Roslyn/bincore` (**6.19 s** CPU, a separate process because shared compilation is off) — all **nice 19** in all 22 samples |
| R3 | the three MSBuild node-reuse workers at nice 0 alive before (B1) | CPU ticks 840 / 1498 / 942 before and after — **unchanged**: none of the build's work went to them |
| R4 | left behind | nothing (no process with the build's `HOME` after it ended) |

So with `--disable-build-servers` the work of a single-project build stays in processes that inherit the niceness. Not
measured: a multi-project build with `-m` (its worker nodes are then children started with node reuse off, so they would
inherit the niceness too — expected, not observed), and a build WITHOUT the flag (whether it hands work to the nice-0
workers stays the risk of point 5, not an observation).

`dotnet test` is the exception (B3): it does not take `--disable-build-servers` under MTP, so the form that keeps a test run's
build at the inherited priority is to build first (`nice -n 19 dotnet build --disable-build-servers`) and then run the tests
without building (`nice -n 19 dotnet test --no-build`, or the test executable itself).

## 4. What the numbers say

1. **`nice` works here, fully** (E1): a nice-19 job beside a nice-0 process in the same cgroup gets 1.4 % of a contended
   CPU, 68.1 : 1 against the kernel's 68.3 (within 0.3 %). Because every session and everything the sessions start share `/init.scope`
   (P1–P4) and the kernel has no autogroup (G10), **a nice-19 build competes with the sessions as E1 shows** — per runnable thread: a build with N busy threads holds N such
   shares (15 N against a session thread's 1024).
   `SCHED_IDLE` (E6) gets a much smaller share still under contention (0.3 %, 356 : 1) — not zero: it is NOT "only CPU
   time nobody else wants". It is not recommended for builds: a build that other work waits on (a shared server, a lock)
   would be delayed further, and nothing donates priority back.
2. **A weight on a sibling cgroup works only against its siblings** (E2): 10 : 1 between two scopes in `app.slice`.
3. **The plan's design backfires** (E4, E5): moved into a `systemd --user` scope, the "low" job leaves `/init.scope` for
   `user.slice` — a different subtree whose weight (100) competes with `/init.scope`'s (100) at the root, and its own
   `CPUWeight=10` only orders it against `app.slice`'s other children, of which there are none. It then got 89.6 % of the
   CPU against a session-like process's 9.7 % — **nine times more, not ten times less** (E4; 21× in E4b, 6.7× in E8). In
   E5 BOTH nice values changed (the session-side loop from 10 to 0, the job from 10 to 19) and the job still got 3.2 : 1
   in its favour; no mechanism is claimed for that step. The direction held
   in all three runs (E4 9.7 %, E4b 4.5 %, E8 12.9 % for the session-side loop; ≥ 86 % for the "low" job), with no
   throttling in play (C1, C2). **Why it is so lopsided is NOT established.** The first explanation (a group's weight is
   spread over the CPUs where its load is, so the busy `/init.scope` brings only a slice of its weight to CPU 23)
   predicted that a third `/init.scope` loop on another CPU would LOWER the pinned loop's share; E8 raised it (4.5 → 12.9 %).
   That explanation is withdrawn; the result stands on its measurements alone.
4. **The delegation drop-in is not needed for CPU** (G7, G8) and would not help I/O on this machine (G11). Its only effect
   would be to enable the `io` controller in a subtree no session lives in.
5. **Build servers can carry work past `nice`** (B1, B2): a `nice -n 19 dotnet build` may hand its work to MSBuild
   workers already alive at nice 0 (B1), and the workers a niced build starts keep nice 19 and may later serve a normal
   build. Whether a build WITHOUT the flag actually picks a live worker was not observed — a risk, not a verified hand-off.
   WITH `--disable-build-servers` (B2) the work was seen staying at nice 19 (§ 3c: the driver and `csc`, the nice-0 workers'
   ticks unchanged), for a single-project build; the flag's cost in build time against a server-backed build was NOT
   measured. `-lowPriority` (a separate
   pool of low-priority MSBuild workers) is a later experiment if that cost proves high, not a recommendation.
6. **The root daemon's own `Nice=19` orders it only inside `system.slice`** (G4, G9):
   `system.slice` and `/init.scope` are siblings at the root, so a busy daemon run competes with the sessions as a group,
   not at nice 19. A weight on the unit would only reorder it among `system.slice`'s services. Recorded, not acted on: no
   daemon run was measured under load (its runs start children, so "one thread" is not an established bound).
7. **Windows hosts are not covered** (W1, W2): the obvious `start /belownormal` form is a different command under
   PowerShell and lost the exit status under `cmd`; no Windows form was measured, so none is recommended.

## 5. Consequences for S4

The plan's S4 design (the Q3 drop-in, `wsl-care low` through `systemd-run --user --scope`) is withdrawn by this
measurement. What it leaves, and the choice taken with the coai consultant, are in the plan's *S4 — revised after the
measurement*.

**Scope of the evidence:** this one WSL 2 machine. A native Linux desktop usually has autogroup on, and terminals or apps
often get their own systemd scopes; there `nice` orders work only inside its own group (the effect § 4.3 shows for the
scope design). `nice -n 19` is harmless there, but its effect on Linux or macOS was not measured.

## 6. Method (the scripts, kept out of the repository)

`wc_probe1.sh` (the cgroup files, `systemctl show`, the daemon's config layers), `wc_probe2.sh` (the cgroup of every
process, tallied by `comm`), `wc_measure_cpu.sh` (E1–E5), `wc_measure_cpu2.sh` (E6–E7), `wc_measure_cpu3.sh` (C1, C2, B1,
E4b, E8), `wc_probe4.sh` (the build servers' argv), `wc_ls_tmp.sh` (the `/tmp` folder after the restart) and `wc_build_probe.sh` (§ 3c; its folder
`/tmp/wc-s4-build-20261009T200242Z` was left in place, per this machine's no-deletion rule, and was gone after the PC
restarted that night — WSL starts with an empty `/tmp`) — each a POSIX `sh` script
that reads `/proc` and `/sys` and, for the measurement, starts two loops with
`[systemd-run --user --scope -q -p CPUWeight=N --] nice -n N taskset -c 23 timeout 20 sh -c '…'` (or `chrt -i 0`), finds
each loop as the child of its `timeout`, reads `utime + stime` 1 s and 19 s after the start, and ends every pid it started
in an `EXIT` trap. Re-run with the same commands to re-measure; one run takes ≈ 2 minutes and one CPU. W1, W2 and B3 were
read in the author's Windows shell (PowerShell 7 and Git Bash): `Get-Command start`, the two `start` / `Start-Process`
forms with a child `cmd /c exit 37`, and `dotnet <cmd> --help` piped to a count of `--disable-build-servers`.
