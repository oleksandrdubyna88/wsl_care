# wsl_care

Keeps the WSL VM on this machine from degrading over the working day: a systemd timer inside `Ubuntu`
that records memory, disk and Docker state every 4 hours and applies safe cleanups, plus a VS Code
extension that shows the state and runs cleanups on demand.

| Folder | Holds |
|---|---|
| [todo/](todo/README.md) | open plans |
| [research/](research/) | measurements of the system as it is — start with [the 2026-10-02 baseline](research/2026-10-02_wsl_resource_baseline.md) |
| `research/diagnostics/` | the read-only scripts that produced the baseline |
