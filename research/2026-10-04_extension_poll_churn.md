# The extension's polling, and the run-log files it costs the daemon — measured 2026-10-04

> For the OWNER's decision on plan §15g M1, which is **open**: accept the file count below, or record a family-rule
> exception (the logging rule's "a file per run") for a high-frequency read-only verb. Nothing here decides it.
>
> - **Subject:** the repository at E5.S2 (branch `feat/wc-e5-extension`): `src_vs_code/src/poll/poller.ts` (the policy),
>   `src_daemon/src/WslCare.Cli/Program.cs` + `Logging/WslCareLogging.cs` (what one daemon run writes).
> - **Method:** the number of `status` runs was MEASURED by running the real `Poller` over a simulated day on a manual
>   clock — `poller.test.ts`, *M1 measured: status runs per window-day …* — not computed by hand. What one run writes was
>   READ in the daemon's source, not observed: the daemon is not installed on this machine (an E5 live-gate check).

## What one `status` costs the daemon

Every daemon verb except `--help` / `--version` is a run, and every run opens ONE log file (`Program.cs`: "Each such run
writes one log file (family logging rule) with at least its request line"; the family rule
`.agents/conventions/common/logging-serilog.md`: a file per run, never a rolling-by-day file). The extension's calls are
unprivileged, so the files go to `$XDG_STATE_HOME/wsl-care/logs/{yyyy-MM-dd}/` (plan §15b #3), and every run first
prunes day folders older than `logging.retentionDays` (14 by default, `LogRetention.Prune`) — a listing of at most ~15
folders.

## The policy that was measured (plan §15f #8, §15g M1, m3)

- Only the FOCUSED window polls (`window.state.focused`): once when it gains focus (and at activation if focused), then
  every `wslCare.refreshSeconds` while it keeps focus. Losing focus disarms the timer.
- A poll asks `status` only. `preview` and `doctor` are asked when the panel opens or Refresh is pressed.
- A stopped distribution is not asked at all: the client's `wsl.exe --list --running --quiet` comes first and no `-d`
  call follows — so a stopped distribution costs the daemon **zero** files (the three `wsl.exe` list questions write
  nothing inside the distribution).

## Measured: `status` runs (= run-log files) per window-day

| Focused time in the day | `wslCare.refreshSeconds` | `status` runs | Files kept at steady state (× 14 days) |
|---|---|---|---|
| 24 h | 120 (default) | **721** (720 interval polls + 1 on focus) | ~10 094 |
| 8 h (a working day) | 120 (default) | **241** | ~3 374 |
| 24 h | 30 (the floor) | **2 881** | ~40 334 |
| an instant | 120 | **1** (the refresh on focus) | — |

On top of the polls: each panel open or Refresh adds 3 runs (`status`, `preview`, `doctor`), and each focus GAIN adds 1
(the refresh on focus). A daemon that predates `productVersion` would add one `--version` per distribution — which opens
no log file.

**Windows do not multiply it.** Focus is exclusive: at any moment at most one VS Code window is focused, so N open
windows cost what one focused window costs, plus one run per focus switch. Before the amendment (60 s, every window) the
same day cost **1 440 files per open window** (§15g M1's figure); the measured policy at its default costs at most 721
per machine-day, 241 for an 8-hour focused day.

## The decision this is for (open — the owner's)

1. **Accept** — up to ~240–720 small files a day in `$XDG_STATE_HOME/wsl-care/logs/`, ~3 400–10 100 kept, each holding
   at least the request line; retention keeps it bounded.
2. **Record a family-rule exception** for a high-frequency READ-ONLY verb (`status`): e.g. no file for a `status` run
   that ends 0, or one file per day for `status` alone — a change to the daemon and a recorded exception to the logging
   rule, both outside E5.
3. **Change the default** (`wslCare.refreshSeconds`): each doubling halves the count; the floor (30 s) stays the user's
   choice.

What is NOT measured: the daemon's own wall time and disk bytes per `status` run on this machine (not installed here) —
the E5 live gate's check, together with whether `wsl-care` ends when `wsl.exe` is killed.

## E6.S3: the poll during a cleanup (plan §15j M6) — measured 2026-10-05

> Still for the OWNER: the M6 churn decision is **open**, with §15g M1 above. E6.S3 implements the poll exactly as M6
> specifies and adds no family-rule exception; these are the numbers it costs.

- **Subject:** branch `feat/wc-e6-cleanup-logs`, `src_vs_code/src/cleanup/runFollower.ts` (the policy).
- **Method:** the real `RunFollower` over a journal and a scripted daemon on a manual clock — `runFollower.test.ts`, *M6 churn,
  measured* — not computed by hand. What one run writes is as above: one run-log file per `status` (and per `runs show`).
- **The policy:** a poll only while something is in flight (a run this extension started that has had no terminal answer
  shown, an unresolved confirm, or — in the focused window — `status.running` queued / live); every 4 s; `status` only; ONE
  `runs show` when a followed run is no longer in flight; a 30-minute ceiling per run; a poll armed before the run ended asks
  nothing (the defect the extension-host tier found).

| A followed run | `status` runs | `runs show` runs | Run-log files |
|---|---|---|---|
| 2 minutes, then done | **30** | **1** | 31, + 3 for the panel round after the answer |
| 10 minutes, then done | **150** | **1** | 151, + 3 |
| never ends (wedged) — the 30-minute ceiling | **451** | **0** | 451, + 3 |

**Per cleanup**, then, roughly 15 files a minute of the run plus 4; against the M1 polling above (721 a focused day at the
default), a day with two 5-minute cleanups adds about 160. The run's OWN unit writes its own log as any run does — not counted
here. The owner's choices remain those of the M1 section: accept, record a family-rule exception for the read-only `status`
(or a new `status --running`), or change the interval (M6 allows 3–5 s; 5 s would make the 10-minute run 120 + 1).

What is NOT measured: the daemon's wall time and bytes per `status` run on this machine (the daemon is not installed here).
