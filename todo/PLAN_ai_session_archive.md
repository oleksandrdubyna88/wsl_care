# PLAN — move old AI-agent sessions into a dated archive instead of losing them

> Status: **in progress, 2026-10-09 — E9.S0, E9.S1, E9.S2a (the archive's file seam), E9.S2b (the two-phase move, `archive run`), E9.S3 (`archive restore`, `archive list`), E9.S4 (A13 and A20 in the timer's engine, the user's own process doing every byte) and E9.S5 (the Windows side's open-file check) built, the review rounds fixed; the E9 live gate and the release carrying it are owed** (the catalogue's archive blocks, the archive's keys, the base folder
> rules and `archive check-base`; the selection and `archive preview`, read-only; the parent plan's §15r *E9.S0 as built* and
> *E9.S1 as built*, *E9.S2b as built*); sessions move on both sides by `archive run`, and in the distro's timer by A13 (E9.S4); the Windows side asks the Restart Manager (E9.S5). Planned 2026-10-02. Scope: a new `archive` capability of the
> `wsl-care` daemon on **both** sides (WSL and Windows), its settings, its page in the VS Code extension.
>
> Parent plan: [PLAN_wsl_care_daemon.md](PLAN_wsl_care_daemon.md) (§4.6 AI-agent monitoring).
> Evidence: [2026-10-02_wsl_resource_baseline.md](../research/2026-10-02_wsl_resource_baseline.md) Finding 6;
> [2026-10-02_ai_session_archive_run.md](../research/2026-10-02_ai_session_archive_run.md) (a one-time
> manual run, §8b).

## 1. Goal and the decision behind it

AI-agent folders grow without a limit (Windows: 477 Claude project folders, 2 631 session files; one WSL
project 1.4 GB). The user's decision (2026-10-02): **do not delete — move** everything older than N days
into a folder the user chooses. Under that base folder the tool creates one folder per agent, and inside
it one folder per **year**, then per **month**:

```
<base>/
  claude/
    2026/
      09/
        windows/<project>/<session>.jsonl          ← original path relative to the agent's root
        wsl/<project>/<session>.jsonl
        index.jsonl                                 ← one line per archived item (§4)
      10/ …
  codex/2026/09/windows/sessions/2026/09/14/rollout-….jsonl
  gemini/…
  antigravity/2026/09/windows/conversations/<id>.db    ← no project level: paths stay relative to
  antigravity/2026/09/windows/brain/<id>/…               the agent's root (~/.gemini/antigravity-cli)
  <manual-agent-name>/…
```

The month is the **last-modified** month of the session (local time) — the moment the conversation ended,
which is what a person looking for "that September session" remembers. `windows/` and `wsl/` keep the two
sides apart because the same project path can exist on both.

## 2. A finding that sets the default N

Claude Code deletes its own transcripts after `cleanupPeriodDays` — **30 days by default**, and it is not
set on this machine. Measured 2026-10-02: Claude session files older than 30 days — 10 on Windows, 2 in
WSL; older than 7 days — 1 812 files / 1.26 GB on Windows, 310 files / 0.56 GB in WSL. So today sessions
are silently **lost** at day 30.

Consequences:
- Default `archive.olderThanDays` = **14**: well before day 30, and leaves two weeks for `claude --resume`.
- The tool reads each agent's own retention setting where one exists (Claude: `cleanupPeriodDays` in
  `~/.claude/settings.json`, default 30) and **warns** when `olderThanDays` ≥ it — the agent would delete
  first.
- It **offers** (button with preview, never automatic) to raise `cleanupPeriodDays` (e.g. to 3650) so
  that the archive, not the agent, owns retention. Editing another tool's settings is the user's call.

## 3. What one "session" is, per agent

The catalogue entry of each agent (parent plan §4.6) gains an `archive` block: which files form one
session, and what must **never** move.

**Boundary with the parent's E7 (parent plan §15q, *Boundaries with the neighbouring plans*):** E7 builds the catalogue
(`agents.json`), its session layouts — taken from the table below for the four confirmed agents, "monitor only" for the
rest — the protected roots, and `aiAgents.extra` with a VALIDATED `sessionGlob` (relative, no `..`). This plan adds the
`archive` block and the move, reads E7's definitions, and does not redefine "one session". E7 goes first.

| Agent | One session = | Never moved |
|---|---|---|
| Claude Code | `projects/<project>/<sessionId>.jsonl` **plus** `projects/<project>/<sessionId>/` (subagent transcripts, tool results) when present, and `file-history/<sessionId>/` | `projects/*/memory/` (the agent's long-term memory), `settings*.json`, `plugins/`, `skills/`, `security/`, the project folder itself (removed only when it is left empty) |
| Codex | `sessions/YYYY/MM/DD/rollout-*.jsonl` | `*.sqlite` state/history databases, `config.toml`, `auth.json` |
| Gemini CLI | `tmp/<project>/chats/session-<timestamp>-<id>.jsonl` (confirmed 2026-10-02 — `.jsonl`, not `.json`) | `tmp/<project>/logs.json`, `history/`, `settings.json`, OAuth files, `antigravity*`, `bin/` |
| Antigravity CLI (`~/.gemini/antigravity-cli/`, older `~/.gemini/antigravity/`) | the files keyed by one conversation id: `conversations/<id>.db`, `brain/<id>/**`, `annotations/<id>.pbtxt` (confirmed 2026-10-02). `log/cli-<timestamp>.log` (one per CLI start) has no conversation id: each log is its own unit, aged on its own mtime | `presence/<id>.lock` (a process lock, not session data — restoring a stale one could mark the conversation as held; the one-time run did move them), `conversation_summaries.db` (+ `-wal`/`-shm`), `implicit/*.pb`, `bin/`, `builtin/`, settings, state and token files |
| Copilot, Rovo Dev, … | to be filled per agent when its layout is confirmed; **until then: monitor only** | everything |
| Manual agent ("Add CLI path") | the session glob the user gives; **no glob → no archiving** | everything outside the glob |

## 4. How a move is done — safely

1. **Select:** files of a session whose newest file is older than `olderThanDays`.
2. **Skip if in use:** Linux — any `/proc/*/fd` pointing at the file; Windows — opening it with
   `FileShare.None` fails. Also skip when the agent's CLI is running with that project as its working
   directory. Skipped items are counted and listed, never forced.
3. **Copy, verify, then delete:** copy to the target path (creating `<agent>/<yyyy>/<MM>/<side>/…`),
   preserve the modification time, flush, compare SHA-256 of source and copy, and only then delete the
   source. ~~A move within one volume may use a rename, followed by the same hash check.~~ Struck
   2026-10-02 (document gate): a rename removes the source before it can be compared and before the index
   line is written, which §8a forbids — every volume takes the same copy → verify → index → delete path.
4. **Never overwrite:** if the target exists with the same hash, only the source is deleted; with a
   different hash the new copy gets a `~2` suffix.
5. **Index:** append `{archivedAt, agent, side, host, originalPath, archivedPath, size, sha256, mtime}`
   to `<base>/<agent>/<yyyy>/<MM>/index.jsonl`.
6. **Remove empty folders** the move left behind, except the agent's own top-level folders.

**Restore** is part of the feature, not an afterthought: `wsl-care archive restore` by session id, by
archived path, or by agent + month moves items back to their original paths (same copy-verify-delete), so
`claude --resume` sees them again. The extension offers it per session and per month.

**Where each side writes.** Each side archives its own agents. The base folder is one setting, a Windows
path (e.g. `V:\ai-archive`); the WSL daemon writes through its `/mnt/<drive>` translation (`wslpath`),
and `archive.linuxBasePath` can override it with a Linux path.

~~Volumes are small (≤ 2 GB today), so the 9p write path is acceptable.~~ **Refuted for a network drive,
2026-10-02** ([one-time archive run](../research/2026-10-02_ai_session_archive_run.md), Finding 2): WSL
`cp` through drvfs to a network-mapped drive wrote ≈ 2 files/s and did not finish 4 696 files in 28 minutes,
while a WSL `tar` read piped into a Windows-side write did all of them in ≈ 5 minutes (≈ 15 files/s).

So the WSL daemon **probes the write path at run time**, when `basePath` is set and again at the start of a
run: write, read back and remove ~100 files of ~10 KB in a scratch folder under the target (a local NTFS
drive was not measured, so it may well pass). Below **10 files/s** — between the two rates measured, a
starting value to re-measure, not a law — it does not write through `/mnt/<drive>` and hands the copy to
the Windows side instead. **Proposed handoff, to be settled in the epic's own plan round:** the WSL daemon
starts the Windows `wsl-care.exe` through WSL interop (`archive receive --base <path>`) and pipes a tar
stream of the selected files to its stdin — the shape that was measured. Verification reads the archive
from the side that reads it fast (Windows, for a Windows path).

## 5. Settings

| Setting | Default | Note |
|---|---|---|
| `wslCare.archive.enabled` | `false` | stays off until a base folder is chosen |
| `wslCare.archive.basePath` | — | chosen with a folder picker; validated: exists, writable, not inside an agent folder, not on the same folder tree as `~/.claude` etc. |
| `wslCare.archive.linuxBasePath` | derived | optional override for the WSL side |
| `wslCare.archive.olderThanDays` | 14 | warning when ≥ an agent's own retention |
| `wslCare.archive.agents` | all with a confirmed layout | per-agent switch |
| `wslCare.archive.auto` | `true` once enabled | daily, as part of the timer run; also a **Archive now** button |

## 6. In the extension

- **AI agents** section: per agent, next to live size and sessions — *archived* size and count, the date of
  the newest archived session, and "eligible now: X sessions / Y GB" (the preview).
- **Archive** page: tree agent → year → month with sizes and counts; per month **Restore month**; per
  session **Restore** and **Open folder**; a search box over `index.jsonl`.
- **Archive now** button: preview (per agent: count, GB, skipped-in-use) → confirm → result table.
- The **Logs** page shows archive runs as action **A13 "AI sessions archived"** — per agent counts and
  bytes, skipped items, and every moved file in the run detail.
- A warning badge when an agent's own retention would delete before the archive moves (§2).

## 7. Build order

1. Catalogue `archive` blocks for Claude, Codex, Gemini CLI and Antigravity CLI (layouts confirmed on this
   machine, 2026-10-02 — §3).
2. `ArchiveAction` (select → in-use check → copy/verify/delete → index) behind `ICommandRunner`/a file
   system seam; `archive preview|run|restore|list` CLI commands.
3. Windows side (`win-x64`) and WSL side, with `wslpath` translation.
4. Retention warnings (`cleanupPeriodDays`) and the "raise it" button.
5. Extension: settings with folder picker, AI-agents columns, Archive page, Logs integration.

## 8. Test plan

- Layout: a fake Claude tree → a session's `.jsonl` + its same-name folder + its `file-history` entry move
  together; `memory/` and settings **never** move (property test over random trees).
- Target paths: year/month from mtime in local time, across a month and a year boundary; `windows`/`wsl`
  split; collision → `~2`; identical file → source deleted, no duplicate.
- Safety: a file held open is skipped (Linux fd scan, Windows sharing violation); a hash mismatch leaves the
  source in place and reports the error; a full or read-only target aborts before deleting anything.
- Index: every move writes exactly one line; restore uses it and puts the file back byte-identical with
  its mtime; restore of a missing archive entry reports, not crashes.
- Settings: base path inside an agent folder is rejected; `olderThanDays` ≥ `cleanupPeriodDays` warns.
- Live smoke: preview on this machine matches the §2 numbers; archive one month of WSL Codex sessions to a
  temp base and restore it.

## 8a. Gate round 1 — amendments (2026-10-02)

- **One session, one month** (finding 2): a session moves as ONE unit under the month of its NEWEST file;
  it is never split across months.
- **The index is written before the source goes** (finding 7): copy → fsync → verify hash → append the
  index line → fsync the index → delete the source. At startup a reconcile finishes the delete for an
  index entry whose source still exists with the same hash, re-indexes an archive file that has no
  entry, and REPORTS any mismatch — it never deletes on a mismatch. *(Read with §8b, never alone: the
  delete — and its resume — is decided per SESSION, not per file; a session one of whose files changed
  keeps EVERY file. §15r D2 step 9 of the parent plan says how a removal stopped half way is finished:
  the transcript first, and when it changed nothing of the session goes — consult 26b4a958, C-3.)*
- **Whose month** (finding 16): the machine's local time zone at archive time; the index records the UTC
  instant and the zone id, so the placement can be reproduced.

## 8b. Evidence from the one-time archive run — amendments (2026-10-02)

The user ran the move once by hand before this plan is built (7 days, both sides, ≈ 2.18 GB, 13 312 files):
[research/2026-10-02_ai_session_archive_run.md](../research/2026-10-02_ai_session_archive_run.md). What it
changes here:

- **The agents delete during the run.** Claude Code's own sweep ran three times in under two hours, and
  Antigravity removed conversations while the copy was running. So a source that disappears between select,
  copy and delete is a normal outcome: the copy drops it from the run (counted, not an error) and reports
  it as *removed by the agent*.
- **The delete is decided per SESSION, never per file** (document gate, 2026-10-02 — the one-time script
  decided per file, which is wrong for a multi-file session). At delete time every still-present file of
  the session must hash to its archived copy:
  - all match → the session's files are deleted (files the agent already removed are simply absent);
  - **any one changed** — the session was resumed or written to — → **no file of that session is
    deleted**; it stays live and whole, the archived copy of this run is kept as a snapshot marked
    `superseded` in the index, and the session is reported. A session is never left half in the archive
    and half live.
  This keeps §8a's *one session, one unit* and the non-negotiable that nothing under an agent's folder is
  deleted without a verified copy. Test: a session whose `.jsonl` changes between copy and delete keeps its
  `<id>/subagents/` files at the source.
- **Antigravity is archivable**: its layout is confirmed (§3), so it leaves "monitor only".
- **The WSL write path** is probed at run time before it is used (§4).
- **Not checked yet, and a test-plan item now:** how Codex (`session_index.jsonl`, `state_5.sqlite`,
  `codex resume`) and Antigravity (`conversation_summaries.db`) behave while a session is in the archive,
  and that restore makes it visible again.

## 8c. The E9 split and design — amendments (2026-10-06)

The parent plan's §15r ([PLAN_wsl_care_daemon.md](PLAN_wsl_care_daemon.md) §15r, plan only) splits this plan's daemon half
into E9.S0–E9.S5 (S2 as S2a / S2b; E9.S6 only on the owner's word), with its review round folded in, and OVERRIDES the
sections below where they differ; the extension half (§6) stays E10, the Windows schedule is the Windows plan's E11 (W-A15).

| Here | §15r decides |
|---|---|
| §4 (who moves) | the TARGET USER's process moves; root's timer only starts it (`runuser`) and records its counts — root never opens a session file or writes the base (D1) |
| §4.2 (in use) | Linux: the user's open descriptors, plus a live Claude Code process in the project's folder (said honestly: Claude keeps no transcript open, so age and no-replace renames are the real guards); Windows: the Restart Manager is asked — never an exclusive open (D2) |
| §4.3–§4.6, §8a, §8b | TWO phases: a run copies (an exclusive create through no link, read-back hash, one retry, then the run stops) and indexes; the source is removed only by a LATER run after `archive.removeAfterHours` and a re-hash of the archived copy, through no-replace quarantine renames and a per-session check; a local in-flight file and self-describing quarantine names drive the reconcile (D2, D3) |
| §4.5 (one index per month) | one index per month PER SIDE AND HOST (`…/<MM>/<side>/index.jsonl`, side `windows-<host>` / `wsl-<host>-<distro>`), a lease per side, every line MAC'd — one writer per file on a shared drive, the index untrusted (D4) |
| §4 (the write-path probe, the `tar` hand-off) | no probe and no hand-off in E9: the rate is measured on every run, the WSL side writes through drvfs within a time budget taken from the timer run's slack, oldest first (D8, D9) |
| §4 restore, §8 test plan ("with its mtime") | restore is create-only from the current layout root and the stored RELATIVE path, never overwrites, keeps the archived copy, and gives the restored files the restore time; a restored session is archived again later as an event only (D6) |
| §5 (`basePath`, `linuxBasePath`) | `archive.baseFolder` per side, as that side sees it, an ordinary key behind the base rules — an existing folder never created, its mount recorded and verified every run, its readers reported (D7) |
| §2 (the "raise it" button) | the product never writes into an agent's settings; a coupled rule keeps ⌈`archive.removeAfterHours` / 24⌉ + `archive.olderThanDays` + `archive.marginDays` ≤ `archive.agentRetentionDays` (1 + 14 + 7 ≤ 30), and the measured `cleanupPeriodDays` (managed settings first) SHORTENS the effective age (D10) |

## 9. Definition of Done

- [ ] Claude Code, Codex, Gemini CLI and Antigravity CLI sessions older than N days move to
      `<base>/<agent>/<yyyy>/<MM>/<side>/…` on both sides, verified by hash, indexed, restorable — and a
      session changed mid-run stays whole at the source (§8b).
- [ ] Nothing outside a session's definition can be moved — enforced by tests; `memory/` never moves.
- [ ] Retention conflict with the agent's own cleanup is detected and shown.
- [ ] Extension: settings with folder picker, archive columns, Archive page with restore, Logs entries.
- [ ] Plan promoted to `research/` when shipped.
