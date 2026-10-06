---
requires: ["README.md","research/architecture.md","research/architecture-daemon-e6.md","research/architecture-extension-e6.md"]
---
# Project instructions — wsl_care

Shared family rules are mounted at `.agents/conventions` (the `dew_flow_conventions` submodule,
tracking its `release` branch) and apply here exactly as local rules would. Fresh clone:
`git submodule update --init .agents/conventions` and
`npm ci --ignore-scripts --prefix .agents/conventions`.

## What this repository is

**wsl_care** keeps one developer workstation from degrading over the working day: a C# Native-AOT
daemon/CLI (`src_daemon/`) run on a schedule inside WSL Ubuntu (systemd timer) and on the Windows host
(Task Scheduler), and a VS Code extension (`src_vs_code/`) that shows its state, its logs and its help
and runs cleanups on demand. The plans are in `todo/` (start at `todo/PLAN_wsl_care_daemon.md`), the
measurements that motivated them in `research/`.

## Commands (as the code lands)

```bash
dotnet build wsl_care.slnx -c Debug          # bounded worker pool via Directory.Build.rsp
./src_daemon/tests/<Project>.Tests/bin/Debug/net10.0/<Project>.Tests.exe   # never `dotnet test`
cd src_vs_code && npm ci && npm test
node .agents/conventions/tools/plan-lifecycle.mjs
```

## Non-negotiables

- **It changes a person's machine.** Every cleanup has a preview, a measured result, a run record, and
  a switch; the *never* lists in the plans are tested, not trusted. Nothing under an AI agent's folder
  is ever deleted (the archive MOVES, verified by hash); nothing under `%TEMP%\claude\` is cleaned.
- **`git worktree prune` is never run from WSL** — it unregisters every worktree created from Windows.
- **The daemon runs unattended**, so the family reliability rules apply in full: every wait has a
  ceiling, every child process is killed with its tree on timeout, one failed action never ends a run,
  and a run that dies leaves a state the next one sweeps.
- **Measured before recommended.** A threshold or a setting the extension advises carries the
  measurement it came from (`research/`), and a claim about this machine is re-measured, not
  remembered.
