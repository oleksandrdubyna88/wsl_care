# todo — open plans

Plans for work that is not finished. A finished plan moves to `research/` with status `IMPLEMENTED <date>`.

## Currently open

| Plan | Status | Scope |
|---|---|---|
| [PLAN_wsl_care_daemon.md](PLAN_wsl_care_daemon.md) | in progress (2026-10-02): Phase 0.2 done; E1.S1 skeleton, E1.S2 seams + `config` verbs, E1.S3 scenario harness, E2.S1 collectors + `status`, E2.S2 Docker collectors + `preview` + live contract built | C# AOT daemon + systemd timer, VS Code extension (cleanup, logs, AI agents, help), CI/CD as CredsForDevs |
| [PLAN_windows_care.md](PLAN_windows_care.md) | plan only (2026-10-02) | the daemon on the Windows host: Task Scheduler, memory/pools/Ollama/TEMP/auto-start/events, safe cleanups, config advisors |
| [PLAN_ai_session_archive.md](PLAN_ai_session_archive.md) | plan only (2026-10-02) | move AI-agent sessions older than N days to <base>/<agent>/<yyyy>/<MM>/, verified, indexed, restorable |
| [PLAN_windows_time_guard.md](PLAN_windows_time_guard.md) | in progress (2026-10-08): story 1 (detection, the A16 brake, the elevated *Start Windows Time* button) building; story 2 (the SYSTEM task) planned | the `w32time` incident: which clock is wrong, never step WSL to a wrong host, start the Windows Time service |
| [PLAN_shared_vscode_kit.md](PLAN_shared_vscode_kit.md) | proposal (2026-10-02) | help / language / text size / tone from coai as a shared public npm package |
