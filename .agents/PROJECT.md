---
requires: ["README.md","research/architecture.md","research/architecture-daemon-e6.md","research/architecture-extension-e6.md","research/architecture-build.md"]
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
- **Every extension action has a button in the panel UI** (owner, 2026-10-09). A command-palette
  entry may duplicate a button; it is never the only way. Where each contributed command's button
  lives is declared in `src_vs_code/src/panel/commandButtons.ts`, and `commandButtons.test.ts`
  fails for a command without one. A new action ships with its button in the same change.

## Commit titles

- **Name the daemon's CLI as the daemon's** (owner, 2026-10-09). A commit that touches `src_vs_code/`
  and names a `wsl-care` verb says "the `wsl-care` daemon's `<verb>` command" (for example "the
  `wsl-care` daemon's `busy` command"), never a bare "wsl-care <verb>". release-please copies such
  titles into `src_vs_code/CHANGELOG.md`, which is Marketplace text, and `productName.test.ts`
  allows `wsl-care` there only as the daemon. The test stays strict; the title follows it. (Extension
  0.3.0's release PR failed on exactly this and was fixed by hand.)
