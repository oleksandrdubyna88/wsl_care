# Post-deploy checks — wsl_care

Target: the owner's own installation (WSL `Ubuntu` + the Windows host + the VS Code extension).
Last verified: never — nothing released yet.

| # | What a person loses if this is broken | Check | Auto |
|---|---|---|---|
| 1 | The timer does not run, so nothing is recorded or cleaned | `wsl.exe -d Ubuntu -- systemctl is-active wsl-care.timer` | auto |
| 2 | The installed daemon is not the released version | `wsl.exe -d Ubuntu -- /opt/wsl-care/bin/wsl-care --version` equals the release tag | auto |
| 3 | The extension shows nothing because it cannot reach the daemon | open the WSL Care panel; the status bar shows RAM/swap/disk | manual |
