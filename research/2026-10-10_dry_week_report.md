# What the dry week would have done — the run records, read 2026-10-10

> Read-only. The installed daemon's own read-only verbs were run as the default user from one script file:
> `wsl-care runs --period 2026-10-01..2026-10-10 --json`, `wsl-care logs` over the same period, and `wsl-care runs show <id> --json`
> for each of the 70 runs. Nothing ran as root and nothing was changed. `act … --preview` refuses without root, so A11 could not be
> previewed (see A11 below).
>
> - **Machine:** the owner's (WSL 2, Ubuntu, target user found as "single login account").
> - **Installed daemon:** `0.3.0+609ba17a`, tagged 2026-10-09 15:47Z. The runs before 2026-10-09 18:02Z were made by `daemon-v0.2.0`.
>   0.3.0 does NOT contain A11-on-by-default (#75) or anything merged after it.
> - **Run ids** carry the guest's wall clock. That clock came up about 2 h ahead after the two boots that followed
>   `RealTimeIsUniversal=1` (the `20261009T2112…–2119…` and `20261010T0944…` runs;
>   [2026-10-08_windows_time_stopped.md](2026-10-08_windows_time_stopped.md) § 5).

## 1. The week

- **70 runs:** 65 by the timer, 5 by the CLI. None failed and none was interrupted.
- **Every timer run was dry**, with the reason *"the setting dryRun is on"*. The first timer run (`20261004T140248Z`) started the
  dry-run week.
  - The week's own lock ends **2026-10-11 14:02Z**.
  - `dryRun` is still on. So the timer stays dry after the week until the owner sets `dryRun` to false: the two locks are
    independent (`DryRunWindow`).
- **Nothing was removed or stopped:** `freedBytes` 0 and `objectsRemoved` 0.
- **The boot catch-up storm:** 10 timer runs between `20261009T211259Z` and `211927Z`, a few seconds to a minute apart. This is the
  contention that boot settle (#82, after 0.3.0) now waits out.

## 2. Per action — what the timer would have done

"Would have run" means that the trigger was reached and only the dry run stopped it.

| Action | Would have run | What it would have done | Otherwise |
|---|---|---|---|
| **A9** snap revisions | **65 of 65** | the same 2 disabled snap revisions (`snapd` 27738 among them), about **0.17 GiB**. It would have done this once; `logs`' sum of 10.4 GiB counts the same bytes 65 times | — |
| **A14** editor builds | **34 of 65** | 1–3 obsolete VS Code server extensions each time, at most **0.30 GiB**: old `connect-other-ais` (0.63.0 ×16, 0.64.0), `claude-code` 2.1.289–2.1.294, `ms-python` 2026.6.0, `google-antigravity` 1.6.0 | 31 runs found nothing to remove |
| **A2** memory compaction | **6** (2026-10-05 to 10-08) | `vm.compact_memory=1`, each after a page-allocation failure (1–20 failures since the previous run) | below its trigger otherwise |
| **A3** .NET build servers | **2**, both under 0.2.0 | 2026-10-06 14:01Z: **88** build servers (MSBuild nodes 0.2 h old, one `VBCSCompiler` 1.8 h old). 2026-10-06 18:01Z: 1 MSBuild node, 9.8 h old | since 0.3.0's idle wait (E14 S3): kept, because servers "used CPU within the window or are not measured yet" (7 runs), or none is old enough (18), or none runs (36) |
| **A19** idle MCP servers | **0 of 14** (since 0.3.0, 2026-10-09 18:02Z) | nothing: 0 targets each time. The distro's watched servers had no idle instance. The leaking `creds-mcp.exe` relays are interop processes (`/init …`), which A19 does not match; A21 (S7b.2) is for them | — |
| **A11** suspects | **unknown** | `auto.A11` is off in 0.3.0 (the default became on in #75, after 0.3.0), so the timer never previewed it. Only a root `wsl-care act A11 --preview --json` can say | 65 × "auto.A11 is off" |
| **A16** the clock | 0 | — | the guest was synchronised (27 runs), or there was no drift on two observations (24). It was **refused** when `powershell.exe` was not on PATH or exited 66 (4 runs). It **said** "the Windows clock is ≈ 7 197 s fast — start Windows Time" in 5 runs after the RTC boots | 
| **A4** anonymous volumes | 0 | — | grew from 0 to **58 volumes, 10.8 GiB** (2026-10-06 to 10-09). The trigger is > 100 volumes or > 20 GiB |
| **A7** build cache | 0 | — | 1.2 GiB growing to **6.57 GiB**. The trigger is > 20 GiB |
| **A5Testcontainers**, **A6** | 0 | — | nothing stopped or dangling. Docker was unreachable in 6–7 runs (not on PATH, daemon stopped, or `docker version` timed out) |
| **A10** journal, **A15** trim, **A1** page cache | 0 | — | below their triggers. `/` is mounted with discard, so A15 never needs to run |
| **A5**, **A6Unused**, **A8**, **A12**, **A17** | — | — | `auto` off (by default) |

## 3. What this licenses

- **Turning `dryRun` off after 2026-10-11 14:02Z would, on this record, have:**
  - removed 2 snap revisions once;
  - removed obsolete editor extensions most days (≤ 0.3 GiB);
  - compacted memory about once a day after allocation failures.

  It would not have touched Docker, the journal or the page cache, and it would not have stopped any MCP server. A3 under 0.3.0
  kept every build server.
- **Not known from the records:**
  - what A11 would stop (it needs the root preview, or a daemon with #75 installed);
  - whether A3's 88-server case recurs under the idle wait.
- **The Windows-side leak is invisible to every distro action as installed.** That is S7b.2's reason.
