# WSL Care

**Preview.** A read-only view of what the `wsl-care` daemon measures inside WSL: memory, swap, disk, containers, the
biggest holders and what each cleanup would free — in the status bar and in a side panel. **Windows with WSL only**; the
daemon is installed separately, inside the distribution (the panel's **Install daemon** types the command for you).

## What you see

- **The status bar** — `WSL RAM <used>% · swap <x>G · <n> containers`, coloured by the worst memory or kernel warning
  the daemon reports; "WSL stopped" when the distribution is not running. A click opens the panel.
- **The panel** (the *WSL Care* icon in the activity bar) — Memory, Top holders, Swap, Disk, Folders, Containers,
  Container starts, Cleanup (what each cleanup would free, read-only), Health. A figure the daemon could not read says
  why ("unavailable — reason"); a row this version cannot fill yet says so; nothing is shown as a made-up 0.

## Read-only, and quiet

- It asks the daemon four questions and nothing else: `status --json`, `preview --all --json`, `doctor --json` and
  `--version`. It never runs a cleanup, never changes the daemon's settings, and never runs anything with elevated
  rights.
- **A stopped WSL is never started.** Before each question it asks `wsl.exe` whether the distribution is running; when
  it is not, nothing is called in it. **Start WSL and check** in the panel is the one button that starts it.
- Only the focused VS Code window polls, every `wslCare.refreshSeconds` seconds (default 120), and only for `status`.
- No telemetry and no network calls of its own.

## Install the daemon

The extension shows what the daemon reports; the daemon itself is installed in the distribution by its release's
`install.sh`. When the panel says *daemon not installed*, **Install daemon** (also *WSL Care: Install daemon…* in the
command palette) shows the exact command and what the distribution needs, then opens a terminal in that distribution
with the command **typed but not run** — you read it and press Enter:

```sh
curl -fsSL https://raw.githubusercontent.com/oleksandrdubyna88/wsl_care/daemon-v0.1.0/install.sh | sudo sh -s -- --version 0.1.0
```

The distribution needs systemd, Ubuntu 24.04 or newer (glibc 2.39), `gh` 2.56.0 or newer from GitHub's apt repository
(the installer verifies the release's build-provenance attestation with it; no `gh` login is needed) and `sudo`. The
installer checks each of these and stops before it changes anything when one is missing.

## Settings

Both are user settings only (`"scope": "application"`), so a repository's `.vscode/settings.json` cannot change them:

| Setting | Default | Meaning |
|---|---|---|
| `wslCare.distro` | empty | The distribution to show. Empty means WSL's default distribution. A name `wsl.exe --list` does not report is refused before anything starts. |
| `wslCare.refreshSeconds` | 120 | How often the focused window asks for `status`, in seconds (at least 30). |

## Requirements

- Windows 10/11 with WSL 2. The extension runs on the Windows side (also in a Remote – WSL window); on any other system
  it says "Windows + WSL only" and asks nothing.
- VS Code 1.85 or newer.
- The `wsl-care` daemon in the distribution (see above).

## Source, issues and licence

Source and issues: [github.com/oleksandrdubyna88/wsl_care](https://github.com/oleksandrdubyna88/wsl_care). MIT licence.
Every release's `.vsix` is also attached to its GitHub release, with a build-provenance attestation.
