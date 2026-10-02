# One-time AI-session archive run — 2026-10-02

> Status: **done 2026-10-02 (~17:28–19:15 CEST)**, by hand, ahead of the `archive` capability planned in
> [PLAN_ai_session_archive.md](../todo/PLAN_ai_session_archive.md). The user asked for it directly: every
> Claude Code, Codex and Gemini session older than **7 days**, on Windows and in WSL Ubuntu, moved to a
> folder on a network drive — copied first, confirmed, then deleted at the source.
>
> Scripts as run: [diagnostics/archive-ai-sessions.sh](diagnostics/archive-ai-sessions.sh) (both sides) and
> [diagnostics/wsl-stream-copy.sh](diagnostics/wsl-stream-copy.sh) (the WSL copy that finished).

This is a record of one run on one machine. It confirms the session layouts the plan left open, and it
measured two things the plan had assumed differently: the agents delete their own files *during* an
archive run, and writing the archive from WSL through drvfs to a network drive is too slow to rely on.

## Conditions

| | |
|---|---|
| Machine | Windows 11 + WSL 2 (`Ubuntu`; the second distro `Ubuntu-26.04` has no agent folders) |
| Target | a 5.5 TB network-mapped drive, `V:` — `/mnt/v` inside WSL (drvfs) |
| Cutoff | `date -d '7 days ago'` at 17:28:52 CEST = **2026-09-25 15:28:52 UTC** (epoch `1790350132`), the same number on both sides |
| Unit | one **session**: archived only when its NEWEST file is older than the cutoff, so a session is never split |
| Age | file modification time (`find -printf %T@`) |
| Harness | the two scripts above, sha256 `b0b1b120…075012d` and `69c3d0bb…a260882d` as committed (the archive script gained two modes during the run, see *What changed in the script*) |
| Verification | SHA-256 of every source file, `sha256sum -c` against the archive before any delete, and again against the source per file at delete time |

**No prediction was written down before the run** (the shared measurement rule, §4, asks for one).
The one recorded expectation it can be held against is the plan's own §4 sentence — *"volumes are small
(≤ 2 GB today), so the 9p write path is acceptable"* — and that is the finding below that did not hold.

## What one session is — confirmed on the installed versions

| Agent | One session = | Left in place |
|---|---|---|
| Claude Code | `~/.claude/projects/<project>/<id>.jsonl` + `~/.claude/projects/<project>/<id>/` when present — it holds `subagents/agent-*.jsonl` + `agent-*.meta.json` and `tool-results/` | `projects/*/memory/` (433 folders on Windows, counted again after the run: 433), everything outside `projects/`. **`file-history/<id>/` was NOT moved in this run** although the plan lists it |
| Codex | `~/.codex/sessions/YYYY/MM/DD/rollout-*.jsonl` (no `archived_sessions/` on either side) | the `*.sqlite` stores, `session_index.jsonl`, `history.jsonl`, config, auth |
| Gemini CLI | `~/.gemini/tmp/<project>/chats/session-<timestamp>-<id>.jsonl` — `.jsonl`, not `.json` | `tmp/<project>/logs.json`, `history/`, settings, OAuth files |
| Antigravity CLI (`~/.gemini/antigravity-cli/`, and the older `~/.gemini/antigravity/`) | the files keyed by one conversation id: `conversations/<id>.db`, `brain/<id>/**`, `annotations/<id>.pbtxt`, `presence/<id>.lock`; plus `log/cli-<timestamp>.log`, one per CLI start | `conversation_summaries.db` (+ `-wal`/`-shm`), `implicit/*.pb` (ids that match no conversation), `bin/`, `builtin/`, settings, state and token files |

Also present and **not** touched, as outside "sessions": Claude Desktop's `%APPDATA%\Claude\local-agent-mode-sessions`
(225 files, 4.3 MB) and its `logs/`; `%LOCALAPPDATA%\claude-cli-nodejs` (per-project MCP server logs,
5 324 files, 28 MB); `~/.codex/log/`.

## Numbers

| Side · agent | Before: sessions / files / MB | Archived: sessions / files / MB | After: sessions / files / MB |
|---|---|---|---|
| Windows · Claude Code | 2 115 / 3 813 / 2 158.0 | 1 495 / 2 352 / 1 293.5 | 621 / 1 514 / 893.1 |
| Windows · Codex | 388 / 388 / 294.9 | 145 / 145 / 69.9 | 243 / 243 / 225.0 |
| Windows · Gemini CLI | 53 / 53 / 1.2 | 53 / 53 / 1.2 | 0 |
| Windows · Antigravity | 3 727 / 10 208 / 515.6 | 2 889 / 6 068 / 91.9 | 873 / 4 278 / 448.0 |
| WSL · Claude Code | 142 / 1 652 / 1 475.3 | 102 / 599 / 679.9 | 44 / 1 089 / 821.3 |
| WSL · Codex | 41 / 41 / 16.2 | 6 / 6 / 0.5 | 36 / 36 / 16.2 |
| WSL · Antigravity | 4 229 / 9 492 / 663.3 | 3 028 / 4 089 / 40.3 | 1 275 / 5 520 / 654.3 |
| **Total** | | **8 618 + 4 694 = 13 312 files, ≈ 2.18 GB** | |

"Before" and "after" are different instants and the agents kept writing in between, so before − archived
≠ after. After the run, **no session older than the cutoff remained on either side** (the same selection,
re-run).

| Delete step | Deleted | Skipped |
|---|---|---|
| Windows | 8 562 | 56, all *already gone* at the source — see Finding 1 |
| WSL | 4 694 | 0 |

## Finding 1 — the agents delete their own files during the run

The selection is a snapshot, and three things removed files between the snapshot and the next step:

- **Claude Code's own retention sweep** (`cleanupPeriodDays`, default 30, unset here) ran three times
  during the run, visible as `~/.claude/.last-cleanup` being rewritten: WSL 17:34 and 18:25, Windows 18:14.
  Each removed sessions dated 2026-09-02/03 — past 30 days. The 18:25 one coincided with several new
  Claude sessions starting in WSL; the sweep looks tied to a session's start, but that was not tested.
- **Antigravity** removed four `conversations/<id>.db` and the `brain/<id>/` of one conversation while
  the first Windows copy was running — between that copy's own listing and its `cp` reaching them, a matter
  of minutes — and 23 `brain/` + 4 `conversations/` files more between the second copy and the delete.
- Net effect: 1 Claude session vanished from WSL after it was copied (17:30 → 17:34), 2 more between the
  WSL copy and its hashing, 29 Claude files of one session and 27 Antigravity files on Windows between copy
  and delete. **Every one of them was already in the archive**; nothing was lost.

Two script defects this exposed, both fixed during the run: the first copy aborted at the first missing
file (so its hash step never ran), and the first delete mode refused to delete anything if any source
file no longer hashed.

**Consequence for the plan:** a missing source is a normal outcome, not an error. Copy tolerates it and
drops the file from the manifest; delete is decided **per file** — removed only when the source still
hashes to what was archived; a changed file stays, a vanished one is reported as *removed by the agent*.
And the plan's §2 race is not hypothetical: with the agent's retention at 30 days, the agent wins any
session that is 30 days old when the archive runs.

## Finding 2 — writing from WSL through drvfs to the network drive is the slow path

| Path | Work | Time |
|---|---|---|
| WSL `cp --parents` → `/mnt/v/…` (drvfs) | 3 563 of 4 696 files in 28 min (17:44 → 18:12), then stopped by a 30-minute ceiling | ≈ 2 files/s |
| WSL `tar -c` piped to Windows `tar -x` writing `V:` | all 4 696 files, ≈ 0.72 GB, written again from scratch | ≈ 5 min (18:23 → 18:28, including hashing in WSL) |
| Windows `cp --parents` → `V:` | 8 618 files, 1.46 GB | ≈ 14 min (17:45 → 17:59) |
| `sha256sum -c` of the 1.46 GB Windows archive read from Windows, then of the local sources | 8 618 files, twice | ≈ 7 min (18:12 → 18:19) |

Even a `find | wc -l` over the half-written WSL archive through `/mnt/v` did not return within 2 minutes.
So in this run, with these files (mostly small: 4 089 of the 4 694 WSL files were Antigravity files
averaging ≈ 10 KB), the WSL side was best served by **reading natively and letting the Windows side write**.
Verification read the archive from Windows, where it is fast; the WSL delete then only had to re-hash its
own sources.

**What this does not settle.** One run, one share, one file mix. It does not tell whether drvfs to a local
NTFS drive (`/mnt/c`, `/mnt/d`) is fast enough, nor whether the cost is per file (metadata round trips) or
per byte; the tar arm wrote 4 696 files and the cp arm 3 563 of the same, so the two are comparable in
count but not proven equal in every condition. The plan's sentence is refuted only for *a network drive,
many small files*.

## Finding 3 — what the move costs the agents (not checked)

Paths under the archive are the original paths relative to the home folder, so a restore is a copy back.
What the agents do while a session is away was **not** checked: whether `codex resume` and Codex's
`session_index.jsonl`/`state_5.sqlite` cope with missing rollouts, and whether Antigravity's
`conversation_summaries.db` lists conversations whose `.db` is gone. A Claude session moved out of
`projects/` is simply absent from `claude --resume`.

## The archive as it was written

```
V:\…\AI_history\
  windows\.claude\projects\<project>\<id>.jsonl …      ← path relative to the Windows home
  windows\_manifest\{files.tsv, files.lst, sha256.txt, deletable.lst, skipped.txt, source-check.txt, copy-errors.txt}
  wsl-ubuntu\.claude\…  wsl-ubuntu\.codex\…  wsl-ubuntu\.gemini\…
  wsl-ubuntu\_manifest\{…, vanished.txt}
```

`files.tsv` keeps the source mtime, size, agent, session key and path for every archived file; the
archive files carry the original mtimes as well (checked on one file per side). This is **not** the plan's
`<agent>/<yyyy>/<MM>/<side>/` layout — a one-time copy kept the simplest restorable shape. Three WSL
Claude files are in the archive but not in the manifest: they were archived and then removed by Claude's
sweep before they could be hashed. The archive is kept by the user; it grows only when someone runs this
again.

## What changed in the script during the run

- `copy` no longer aborts on a file that vanished after listing; it keeps only files present in both the
  source and the archive in the manifest, and counts the errors.
- `delete` checks the archive in full (any mismatch → nothing deleted), then deletes only the source files
  that still hash to the manifest; `delete-source` is the same with the archive check done by the caller
  (used for WSL, whose archive is fast to read only from Windows).
- Empty session folders are removed only below depth 4 and never under `memory/`.

One operational lesson from running it: **bash reads a script while it runs**, so editing the file under a
running instance can make it execute bytes from the edited version at the old offset. A WSL copy was stopped
by pid for that reason before it reached the end of the file, and every later run used a separate, never-
edited copy of the script.
