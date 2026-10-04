# research — the system as it is, and what was measured

| Document | What it records |
|---|---|
| [architecture.md](architecture.md) | what exists today and the module map |
| [module_tests.md](module_tests.md) | the test harness, the flow catalogue, what it does not prove |
| [2026-10-02_wsl_resource_baseline.md](2026-10-02_wsl_resource_baseline.md) | the WSL VM measured: memory exhaustion by evening, Docker leftovers, clock jumps, AI-agent folders |
| [2026-10-02_one_time_cleanup.md](2026-10-02_one_time_cleanup.md) | the one-time cleanup and the ≈ 135 GB it freed |
| [2026-10-02_windows_baseline.md](2026-10-02_windows_baseline.md) | the Windows host measured: Ollama, Fast Startup, non-paged pool attributed, TEMP |
| [2026-10-02_competitor_survey.md](2026-10-02_competitor_survey.md) | what comparable tools do, what to adopt, what never to do |
| [2026-10-02_ai_session_archive_run.md](2026-10-02_ai_session_archive_run.md) | the one-time move of AI sessions older than 7 days (≈ 2.18 GB, both sides): layouts confirmed, agents deleting mid-run, the slow WSL write path |
| [2026-10-03_wsl_exe_facts.md](2026-10-03_wsl_exe_facts.md) | `wsl.exe` as the extension meets it (WSL 2.7.10.0): encodings, the `*` default marker, `--exec` vs `--`, the missing-binary signature (exit 1), wsl.exe's own -1, what killing `wsl.exe` does to the Linux process |
| [2026-10-04_extension_poll_churn.md](2026-10-04_extension_poll_churn.md) | the extension's polling policy measured over a simulated day: `status` runs (= daemon run-log files) per window-day — 721 at the default 120 s, 241 for an 8-hour focused day — for the owner's open M1 decision |
