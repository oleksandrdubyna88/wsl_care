# Architecture — wsl_care

> As of 2026-10-02 this repository holds **no product code yet** — measurements in `research/` and the
> plans in `todo/`. This file describes what exists and is rewritten as each part lands.

## What exists

- The family rules at `.agents/conventions` (tracking `release`) and the Claude host adapter.
- Read-only diagnostic scripts under `research/diagnostics/`, which produced the baselines.
- Plans: the daemon and extension (`todo/PLAN_wsl_care_daemon.md`), the Windows side
  (`todo/PLAN_windows_care.md`), the AI-session archive (`todo/PLAN_ai_session_archive.md`), the shared
  VS Code kit (`todo/PLAN_shared_vscode_kit.md`).

## Planned module map

| Part | Where | Role |
|---|---|---|
| daemon / CLI | `src_daemon/` | C# Native AOT, `linux-x64` + `win-x64`: collectors, rules, actions, run records |
| extension | `src_vs_code/` | status bar, panel, cleanup table, logs page, settings, help |

## Cross-repository

| Repository | Relationship |
|---|---|
| `dew_flow_vscode_kit` | the extension's help page and display controls come from its npm package |
| `dew_flow_creds_for_devs` | the model for this repository's CI/CD |
