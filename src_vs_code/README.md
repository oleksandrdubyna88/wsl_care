# AI OS Care

**Preview.** A view of what the `wsl-care` daemon measures inside WSL: memory, swap, disk, containers, the biggest holders
and what each cleanup would free — in the status bar and in a side panel — and the daemon's cleanups, each run only after
you confirm it. **Windows with WSL only**; the daemon is installed separately, inside the distribution (the panel's
**Install daemon** types the command for you).

## What you see

- **The status bar** — `WSL RAM <used>% · swap <x>G · <n> containers`, coloured by the worst memory or kernel warning
  the daemon reports; "WSL stopped" when the distribution is not running. A click opens the panel.
- **The panel** (the *AI OS Care* icon in the activity bar) — Memory, Top holders, Swap, Disk, Folders, Containers,
  Container starts, Cleanup (what each cleanup would free, with **Clean**, **Select**, **Clean selected** and **Run full
  check now**), Health, Last cleanup. A figure the daemon could not read says
  why ("unavailable — reason"); a row this version cannot fill yet says so; nothing is shown as a made-up 0.
- **The Logs page** (*Logs* in the panel's title, or beside *Last cleanup*) — what the runs of a period did: This run,
  Today, Yesterday, a day or a range of your LOCAL days (the 90 the daemon keeps). Totals, runs with and without a
  cleanup, the runs that freed the most and the least, each figure's max and min, the MemAvailable and swap trend, and the
  run list — *Show objects* lists every object a run removed, and every command it ran with its exit. Shown exactly as the
  daemon answers; the chosen period survives a reload.

## What it runs, and when

- **Reading** — it asks the daemon four questions: `status --json`, `preview --all --json`, `doctor --json` and
  `--version` — and, for the cleanups' results and the Logs page, three about runs: `runs show <run> --json`,
  `runs --from <instant> --to <instant> --json` and `logs --from <instant> --to <instant> --json`. They run as your own
  user and change nothing.
- **Cleaning — only after you confirm, and only through five calls.** A cleanup needs root inside the distribution, so
  the extension has exactly five root calls, all built in one place and nowhere else: a cleanup's preview
  (`act <ids> --preview`), its run once you confirmed (`act <ids> --confirm --manual --detach`, with the list of volumes
  the preview showed handed over on stdin), stopping a wedged run (`act --stop <run>`), *Run full check now*
  (`collect --detach`) and a root check (`--version`). It never sends the timer's mark, never changes the daemon's
  settings, and never acts on an action the daemon does not offer. A confirmed cleanup runs in the daemon's own systemd
  unit, so reloading the window does not cut it off: the panel writes the run down before it starts and follows it to its
  result — "Cleaning… A4" after a reload, then what it freed; a run that died says *interrupted*. (Whether the run also
  survives closing every WSL window is measured before the first release.)
- **Each cleanup shows its preview first** — a modal with what each action removes and how much, and a second one for the
  actions that touch your own data or force downloads (A5, A6Unused, A8, A11, A12), naming the setting they use. A preview
  older than five minutes when you confirm is taken again. *Clean selected* runs all ticked rows as one run.
- **The daemon must be new enough to clean.** Whether it may act is decided by what the daemon says it can do; an older
  one reads "Update daemon" and nothing is run.
- **A stopped WSL is never started.** Before each question it asks `wsl.exe` whether the distribution is running; when
  it is not, nothing is called in it. **Start WSL and check** in the panel is the one button that starts it.
- Only the focused VS Code window polls, every `wslCare.refreshSeconds` seconds (default 120), and only for `status`.
- No telemetry and no network calls of its own.

## Install the daemon

The extension shows what the daemon reports; the daemon itself is installed in the distribution by its release's
`install.sh`. When the panel says *daemon not installed*, **Install daemon** (also *AI OS Care: Install daemon…* in the
command palette) shows the exact command and what the distribution needs, then opens a terminal in that distribution
with the command **typed but not run** — you read it and press Enter:

```sh
curl -fsSL https://raw.githubusercontent.com/oleksandrdubyna88/wsl_care/refs/tags/daemon-v0.1.2/install.sh | sudo sh -s -- --version 0.1.2
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

## Workspace trust

AI OS Care works in untrusted (Restricted Mode) workspaces too, and that is deliberate: no input from the workspace reaches a
root call. Every setting it reads is `"scope": "application"` — a user setting, which a repository's
`.vscode/settings.json` cannot set — and none of its calls takes a value from the open folder, its files or its tasks.
What a cleanup acts on comes from the daemon's own preview and your confirmation in a VS Code dialog, never from the
workspace, so trusting or not trusting a folder changes nothing about what it can run.

## Requirements

- Windows 10/11 with WSL 2. The extension runs on the Windows side (also in a Remote – WSL window); on any other system
  it says "Windows + WSL only" and asks nothing.
- VS Code 1.85 or newer.
- The `wsl-care` daemon in the distribution (see above).

## Source, issues and licence

Source and issues: [github.com/oleksandrdubyna88/wsl_care](https://github.com/oleksandrdubyna88/wsl_care). MIT licence.
Every release's `.vsix` is also attached to its GitHub release, with a build-provenance attestation.
