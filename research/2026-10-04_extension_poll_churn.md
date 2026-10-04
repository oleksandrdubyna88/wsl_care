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
