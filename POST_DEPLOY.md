# Post-deploy checks — wsl_care

Target: the owner's own installation (WSL `Ubuntu` + the Windows host + the VS Code extension).
Last verified: never — nothing released yet.

| # | What a person loses if this is broken | Check | Auto |
|---|---|---|---|
| 1 | The timer does not run, so nothing is recorded or cleaned | `wsl.exe -d Ubuntu -- systemctl is-active wsl-care.timer` | auto |
| 2 | The installed daemon is not the released version | `wsl.exe -d Ubuntu -- /opt/wsl-care/bin/wsl-care --version` equals the release tag | auto |
| 3 | The extension shows nothing because it cannot reach the daemon | open the WSL Care panel; the status bar shows RAM/swap/disk | manual |
| 4 | A Docker or systemd update changed an output the parsers read, so the cleanup table or the health figures silently go unavailable | BEFORE tagging `daemon-v*`: inside WSL `Ubuntu`, build and run `WSL_CARE_REQUIRE_LIVE=1 ./src_daemon/tests/WslCare.LiveContract/bin/Release/net10.0/WslCare.LiveContract` while Docker is quiet; 0 failed, 0 skipped (plan §15b #2) — the release checklist. Since E2.S3 it also proves the health tools, the Windows clock probe through interop and that Docker's event stream closes at a future `--until` (what the follower's segments rest on) | manual |
| 5 | The timer runs but records nothing (the state directory is not root's, a full run fails to write), or the follower is down, so history and the 24 h container count go silent while every unit looks active | `wsl.exe -d Ubuntu -u root -- /opt/wsl-care/bin/wsl-care doctor --json` shows `"lastRun"` and `"eventsFollower"` with `"state": "ok"` | auto |
