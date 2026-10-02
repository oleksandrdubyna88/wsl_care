# module_tests — the harness, the flows it drives, and what it does not prove

> Adopted 2026-10-02 with no product code yet; every flow is `not covered` until its part lands.

## Where the harness is and how it runs

Planned: xUnit v3 MTP executables under `src_daemon/tests/` driving the built CLI against recorded
fixtures and a temporary home; the extension's `npm test` under `src_vs_code/`. CI runs exactly those.

## Flow catalogue

| Flow | Covered | By |
|---|---|---|
| `wsl-care status --json` / `collect` | not covered | the daemon is not built yet |
| a cleanup action, previewed then run | not covered | the daemon is not built yet |
| the logs page for a period | not covered | the extension is not built yet |
| the AI-session archive and restore | not covered | not built yet |

## What it does not prove

Nothing here runs on a real WSL VM under memory pressure; the live smoke on the owner's machine is the
only place that happens, at release time.

## When it runs

On every pull request once code exists; the live smoke at every release.
