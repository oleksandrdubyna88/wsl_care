# The first live install, the release week's facts, and the WSL slowdown of 2026-10-06 — measured

> Everything measured or observed on 2026-10-06 while daemon 0.1.0 → 0.1.2 and extension 0.1.0 shipped, kept in one
> place so no number lives only in a chat transcript or a PR body. Each row says HOW it was measured and by whom
> (the owner's own run, an agent's read-only run as the login account, or CI). Nothing here was measured as root by
> an agent: root steps were the owner's.
>
> - **Machine:** Windows 11 Pro 10.0.26300; WSL 2 (WSL 2.7.10.0, kernel 6.18.33.2-2) with Ubuntu 24.04, 24 logical
>   CPUs, 44 GiB `MemTotal` inside the VM (`free -g`), 12 GiB swap; systemd 255 (255.4-1ubuntu8.17); Docker Engine 29.6.1.
> - **Subjects:** daemon `daemon-v0.1.0` (tag → `ff02a83`), `daemon-v0.1.2` (tag → `f7652a3`); extension
>   `ai-os-care-0.1.0.vsix` built from `62a0186` (= main `7d3b6bd`'s `src_vs_code`); the ConnectOtherAIs MCP server
>   `coai-mcp` 0.43.0 (WSL) and 0.41.1 (Windows).
> - **Related:** [2026-10-02_wsl_resource_baseline.md](2026-10-02_wsl_resource_baseline.md) (the evening slowdown
>   first measured), [2026-10-03_wsl_exe_facts.md](2026-10-03_wsl_exe_facts.md) (`wsl.exe` behaviour; row W2 below
>   extends its row 7), [module_tests.md](module_tests.md), [architecture.md](architecture.md).

## 1. The first install of a published release (owner, `curl … | sudo sh`, 16:11Z)

| # | Observed | Value |
|---|---|---|
| I1 | integrity | `checksum ok: 7b4f51ce…3940` for `wsl-care-0.1.0-linux-x64.tar.gz` |
| I2 | authenticity | `gh attestation verify` passed: SLSA v1 predicate, SAN `…/release.yml@refs/tags/daemon-v0.1.0`, github-hosted runner |
| I3 | existing machine config | kept (`/etc/wsl-care/config.json` is still the empty `{}` of the 2026-10-04 local install) |
| I4 | default user | `/etc/wsl.conf` names none → the single login account (uid ≥ 1000) is the target; nothing written |
| I5 | first full run | 30 s (16:11:42 → 16:12:12Z), recorded as `20261006T161142Z-<pid>` |
| I6 | first-run warnings | page cache 16.80 GiB · inactive anonymous 19.38 GiB · swap 10.13 GiB · fragmentation (1 order-7 block, 382 order-4) · journal reaches 4.1 days · 274 clock jumps in 2.2 h (≈ 504 per 4 h) · 1 failed unit (`getty@tty1.service`, not ours) · wsl-pro enabled + active · 11.41 GB unused tagged Docker images |
| I7 | installed binary | sha256 `ec14cc13…2677` = the `wsl-care` inside the release archive (agent, read-only) |
| I8 | `dryRun` | `true (default)`; the first-week window runs from 2026-10-04T14:02:48Z to 2026-10-11T14:02:48Z — the timer stays dry while EITHER holds, so cleanups do **not** start by themselves on 2026-10-11 (`DryRunWindow`) |

## 2. POST_DEPLOY against the live install, and what the checks themselves did

| # | Check | Result | How |
|---|---|---|---|
| P1 | item 4 — the live contract suite | **24 / 24 passed**, 25.8 s, 21 containers running | agent, login account, fresh clone of `4782ccf`, Release build, `WSL_CARE_REQUIRE_LIVE=1` |
| P2 | item 7 — the act unit's limits | **FAILED**: `CollectMode=inactive` loaded; the journal said `wsl-care-act@.service:46: Unknown key name 'CollectMode' in section 'Service', ignoring.` | agent, `systemctl show` on two never-run instances; fixed in 0.1.2 (key moved to `[Unit]`) |
| P3 | `systemd-analyze verify` on that unit | prints the warning **and exits 0** | agent, systemd 255, login account |
| P4 | why CI missed P2 | the verify step listed three units by hand (E4.S1); the template `wsl-care-act@.service` (E6.S1) was never in it; a template is only parsed through an instance name | agent; CI now verifies every file in `src_daemon/systemd`, templates as `…@20000101T000000Z-1.service`, with the release binary's own drop-ins |
| P5 | `systemctl is-active a b` | prints `active` / `inactive` and **exits 0** when any one unit is active | agent; item 1 now requires two `active` lines |
| P6 | `wsl-care doctor --json` | exits 0 healthy or not | item 5 now greps `"healthy": true` with `install.sh`'s own pattern |
| P7 | systemd 255 journal of `wsl-care.service` | carries `Consumed …s CPU time` per run, **no `memory peak`** (`MemoryPeak=[not set]`) | agent, 28 runs read; item 11 now requires a `Consumed` line |
| P8 | `journalctl -u <unit>` | never returns kernel AppArmor lines | item 11's AppArmor pattern removed |
| P9 | item 11's old form `wsl.exe … -- sh -c '…$(journalctl …)…'` | could never pass, and journal text was parsed by root's shell — see W2 | rewritten: `wsl.exe` runs `journalctl` alone, the text is read on the Windows side |
| P10 | the checker's PASS on item 7 before the fix | the cell's first code span was `systemctl cat` alone — exit 0 regardless | every automated item now ends in a real comparison |

## 3. `wsl.exe` (extends [2026-10-03_wsl_exe_facts.md](2026-10-03_wsl_exe_facts.md))

| # | Observed | Consequence |
|---|---|---|
| W1 | Git Bash rewrites a `/mnt/c/…` argument into `C:/Program Files/Git/mnt/c/…` before `wsl.exe` sees it | run with `MSYS_NO_PATHCONV=1` |
| W2 | after `--`, `wsl.exe` hands the rest to the distro's shell as ONE command line: `wsl.exe -d Ubuntu -- sh -c 'x=1; echo "[$x]"'` prints `[]` | never put `$(…)`, variables or quotes meant for the inner shell after `--`; ship a script file and run it by path |

## 4. CI: the win-x64 timing failures (main `4782ccf`, run 37479558026)

| # | Test | Symptom | Cause, reproduced |
|---|---|---|---|
| C1 | `PhysicalFileSystemTests.An_atomic_replace_waits_out_a_reader…` | `UnauthorizedAccessException` from `MoveReplacing` | the test released its reader from a pool task that got no thread within the product's 2 s retry |
| C2 | `RunProgressTests.A_beat_without_a_step…` | heartbeat time equal to the start time | a 40 ms timer made no beat within the test's 300 ms real sleep |

Reproduction harness (temporary, not kept): 400 pool work items blocking for 4 s, then the old test bodies — C1 failed
3 of 3, C2 2 of 3, each with the CI message; after the fix (the reader released at the first refusal on the writing
thread; the heartbeat driven by a test clock that fires only on `Advance`) 5 of 5 green under the same harness.

## 5. NTFS behaviour the archive seam is built on (E9.S2a, measured before building)

| # | Observed |
|---|---|
| N1 | `FlushFileBuffers` on a directory handle works only with `FILE_ADD_FILE` access or more; a read-only or attributes-only handle answers error 5 |
| N2 | while a folder is held open without delete sharing, neither it nor its parent can be renamed |
| N3 | `FILE_RENAME_INFO` with a bare file name resolves against the PROCESS's current folder; with a `RootDirectory` handle it answers error 87 — so the rename holds the folder and passes the full path |
| N4 | .NET `Path.GetFullPath` expands an 8.3 short name (any path containing `~`) on Windows |
| N5 | `Path.GetDirectoryName` of a Windows path, run inside the distro, returns empty (crashed the check with `IndexOutOfRangeException`; `PathRules.Parent` added) |

Not measured yet (live-gate items before E9.S5): `FlushFileBuffers` on a directory handle over SMB (a NAS base);
`fsync` of a directory on real drvfs; an unaligned end-of-file read with `FILE_FLAG_NO_BUFFERING` over SMB.

## 6. The WSL slowdown of 2026-10-06 evening (agent, read-only `/proc` samples, login account)

The owner reported a sudden WSL slowdown and `MCP server "coai" connection timed out after 30000ms` in about half of
the Claude Code sessions.

### 6a. What loaded the VM (19:01–19:10 local, = 17:01–17:10Z)

| # | Observed | Value |
|---|---|---|
| L1 | load average | 44 → 21 → 61 on 24 CPUs across ten minutes |
| L2 | pressure (PSI) | CPU `some avg10` 60 %; memory and I/O ≈ 0 — the slowdown was CPU, not memory or disk |
| L3 | memory | 26 GiB used, 19 GiB cache, 1–2 GiB free, swap 8–9 GiB of 12 in use |
| L4 | CPU by working folder (one `ps` snapshot) | the owner's other projects ≈ 9 cores (builds, `dotnet test`, jest e2e, Playwright Chromium); the shared compiler server `VBCSCompiler` 3–7 cores; this repository's agent builds and test runs ≈ 3–4 cores; `coai-mcp` ≈ 2.6 cores |

### 6b. `coai-mcp` 0.43.0 — a slow start that becomes a restart storm

The ConnectOtherAIs extension auto-updated the WSL `coai-mcp` to 0.43.0 at 16:54Z (binary in the extension's
`globalStorage` under `~/.vscode-server`); the Windows side stayed 0.41.1. Its log lines (`~/.local/share/coai-mcp/
logs/<UTC day>/coai-mcp-<HH-mm-ss>-<pid>.log`) give the start time as `starting` → `consultants: wrote …/health/
consultants.json`:

| # | Observed | Value |
|---|---|---|
| M1 | start time before 0.43.0 (earlier logs that day) | 3–15 s |
| M2 | start time after 0.43.0 | 19–32 s, at ≈ 100 % of one core |
| M3 | Claude Code's MCP connect budget | 30 s → `SIGTERM asked this server to stop` at ≈ 30 s, then a new start |
| M4 | starts per 10 minutes | 34 in 16:50–17:00Z (≤ 9 in any earlier 10-minute slot that day) |
| M5 | client kills | 20 logs ending in SIGTERM within 30 minutes |
| M6 | idle CPU of a started instance | 27–54 % of one core EACH over a 45 s `/proc/<pid>/stat` sample (7 instances ≈ 2.6 cores) while its log had no line for 10+ minutes; a shorter 20 s sample earlier read lower — the load is bursty |
| M7 | what an idle instance touches | rewrites `runs/<id>.json` (≈ 140 B) about every 10 s; the whole state folder is 68 MB — not a large scan |
| M8 | the Windows side (0.41.1) | two instances, 0 % CPU over 10 s |

The feedback loop: each failed start costs a core for ≈ 30 s; the extra load makes the next start slower still.
Remediation applied on the owner's word: `"env": {"MCP_TIMEOUT": "90000"}` in the distro's `~/.claude/settings.json`
(takes effect for new sessions / a window reload). The defect itself is being fixed in ConnectOtherAIs (answer
`initialize` first, run and share the consultants health probe in the background, no idle polling); this repository
adds a read-only MCP-instance metric (E7.S2d: instance count, idle, busy-without-activity, restarts per window).

## 7. Release and publishing facts

| # | Observed |
|---|---|
| R1 | release-please proposes a DAEMON release for any `feat:`/`fix:` commit touching `src_daemon/` — tests included; a rename PR was therefore merged by rebase as a `test(release)` commit plus a `feat(extension)` commit |
| R2 | `gh pr merge --auto` did not fire on a PR that showed CLEAN with every required check green (#18); a plain `gh pr merge --squash` merged it |
| R3 | `release.yml` runs the Scenarios suite, which reads the conventions submodule; `daemon-v0.1.1` failed both Linux legs because only `ci-daemon.yml` fetched it — the tag exists, its draft is empty, nothing was published; 0.1.2 carries the fix |
| R4 | `release-extension.yml` job order: guard → build → attest → github draft → marketplace → github public; the attested `.vsix` lands in the DRAFT before the marketplace job; `github public` needs the marketplace job to succeed |
| R5 | the extension guard refuses while `POST_DEPLOY.md`'s `Last verified:` line names no date (run 37520151276) |
| R6 | manual Marketplace upload of `ai-os-care-0.1.0.vsix` (34 734 bytes, sha256 `76596f7c…d60b`, 11 files) was accepted ("Verifying 0.1.0", public) |
| R7 | CodeRabbit posted nothing on any wsl_care PR of this week |

## 8. The review gate this day

| # | Observed |
|---|---|
| G1 | ConnectOtherAIs had ONE enabled reviewer (codex); the consultant answered (codex, `gpt-6-astra`) |
| G2 | consult calls are capped per caller session per 24 h (`COAI_CONSULT_CALLS_PER_SESSION`), shared by every agent of one Claude session: 10, then 20 after the owner raised it; both caps were reached the same day |
| G3 | the question consultant (`ask_consultants`) answered `none_available` — no row switched on |
