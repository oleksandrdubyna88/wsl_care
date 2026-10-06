# todo — open plans

Plans for work that is not finished. A finished plan moves to `research/` with status `IMPLEMENTED <date>`.

## Currently open

| Plan | Status | Scope |
|---|---|---|
| [PLAN_wsl_care_daemon.md](PLAN_wsl_care_daemon.md) | in progress (2026-10-02): Phase 0.2 done; E1.S1 skeleton, E1.S2 seams + `config` verbs, E1.S3 scenario harness, E2.S1 collectors + `status`, E2.S2 Docker collectors + `preview` + live contract built | C# AOT daemon + systemd timer, VS Code extension (cleanup, logs, AI agents, help), CI/CD as CredsForDevs |
| [PLAN_windows_care.md](PLAN_windows_care.md) | plan only (2026-10-02) | the daemon on the Windows host: Task Scheduler, memory/pools/Ollama/TEMP/auto-start/events, safe cleanups, config advisors |
| [PLAN_ai_session_archive.md](PLAN_ai_session_archive.md) | in progress (2026-10-06): its daemon half split into E9.S0–E9.S5 by the daemon plan's §15r; E9.S0 built (catalogue blocks, keys, base folder rules, `archive check-base`), E9.S1 built (selection, `archive preview`) | move AI-agent sessions older than N days to <base>/<agent>/<yyyy>/<MM>/, verified, indexed, restorable |
| [PLAN_twenty_sessions_all_day.md](PLAN_twenty_sessions_all_day.md) | in progress (2026-10-09): S1 built (the MCP CPU over the interval since the previous sample); S2a built (A19, the idle MCP watchdog); S2c built (the user's own MCP programs); S2d built (playwright-mcp in the catalogue); S3 built (A3's timer waits for idle build servers; language servers for A11); S5 built (memory and swap before the evening, a report); S6 built (the "machine busy" signal); S7a built (the Windows side's MCP servers, read-only, and the vmmem advice); S2b built (the watch timer and A19's busy half); S4, S7b, S8 plan only | epic E14: twenty Claude sessions for 24 h — MCP metric, MCP watchdog, build-server reaper, CPU fairness, memory/swap, a "machine busy" signal, the Windows side, a 24 h soak |
| [PLAN_marketplace_entra_publish.md](PLAN_marketplace_entra_publish.md) | plan only (2026-10-09) | the extension's Marketplace publish moves from the global `VSCE_PAT` (dead on 2026-12-01) to `--azure-credential` through GitHub OIDC and an Entra app registration made a publisher member |
| [PLAN_shared_vscode_kit.md](PLAN_shared_vscode_kit.md) | proposal (2026-10-02) | help / language / text size / tone from coai as a shared public npm package |
