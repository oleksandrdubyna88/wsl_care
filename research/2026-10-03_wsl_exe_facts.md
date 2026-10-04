# `wsl.exe` as the extension meets it — measured 2026-10-03

> The facts `WslCareClient` and the strict fake are built on (plan §15g M6, M3; §15f #1), measured on the owner's
> machine BEFORE the client was written. Read-only: nothing was installed, no distribution was stopped or terminated,
> no `--shutdown`, no `-u`. Every call was made the way the extension's runner makes it — Node `spawn` of the ABSOLUTE
> `%SystemRoot%\System32\wsl.exe` with `shell: false` — so what is recorded is what the client receives.
>
> - **Harness:** [diagnostics/wsl_exe_facts.mjs](diagnostics/wsl_exe_facts.mjs) (exit code, raw bytes, both decodings per
>   call), [diagnostics/wsl_exe_kill.mjs](diagnostics/wsl_exe_kill.mjs) and
>   [diagnostics/wsl_exe_kill_sighup.mjs](diagnostics/wsl_exe_kill_sighup.mjs) (kill only the `wsl.exe` the script
>   started, then a read-only `ps` in the distro). Node 24.18.0. Added 2026-10-04 (§15h #3):
>   [diagnostics/wsl_exe_kill_preview.mjs](diagnostics/wsl_exe_kill_preview.mjs) — row 18.
> - **Subject:** the repository at `65c3198` (E5.S0); the daemon is NOT installed in any distribution here (row 18: a
>   throwaway build in `/tmp`, removed after).
> - **Machine:** Windows 11 Pro 10.0.26300, three WSL 2 distributions, all running during the measurement.

## The WSL build

`wsl.exe --version` (exit 0; UTF-16LE on stdout, decoded):

```
WSL version: 2.7.10.0
Kernel version: 6.18.33.2-2
WSLg version: 1.0.73.2
MSRDC version: 1.2.6676
Direct3D version: 1.611.1-81528511
DXCore version: 10.0.26100.1-240331-1435.ge-release
Windows version: 10.0.26300.9550
```

## Facts, each with what it decides

| # | Question | Observed | Decides |
|---|---|---|---|
| 1 | Which `wsl.exe`? | `%SystemRoot%` = `C:\WINDOWS`; `C:\WINDOWS\System32\wsl.exe` exists. `where.exe wsl` lists TWO: `C:\Windows\System32\wsl.exe` and the Store alias `C:\Users\<user>\AppData\Local\Microsoft\WindowsApps\wsl.exe` | the client starts `%SystemRoot%\System32\wsl.exe` by absolute path (`wslExecutable`), never a bare name — and with no drive-absolute `SystemRoot`, nothing |
| 2 | `--list --quiet` encoding | exit 0, 76 bytes, UTF-16LE, CRLF, no BOM: `55 00 62 00 75 00 6e 00 74 00 75 00 0d 00 0a 00 …` → `Ubuntu\r\ndocker-desktop\r\nUbuntu-26.04\r\n` | `decodeWslText` (UTF-16LE when a NUL sits at an odd offset or a BOM leads) |
| 3 | `-l -v` | exit 0, UTF-16LE: `  NAME              STATE           VERSION\r\n* Ubuntu            Running         2\r\n  docker-desktop    Running         2\r\n  Ubuntu-26.04      Running         2\r\n` — the default carries `*` in column 0, names padded to 18, states to 16 | `parseDefaultDistro` reads ONLY the `*` row (the header and the state words are localised; the marker is not); the fake prints exactly this layout |
| 4 | `--list --running --quiet` | exit 0, UTF-16LE, the running names (here all three) | the running check; a distribution absent from it gets no `-d` call |
| 5 | `--list --quiet` with `WSL_UTF8=1` | exit 0, 38 bytes, **UTF-8**: `55 62 75 6e 74 75 0d 0a …` | the decoder decides from the BYTES, not from the call, so an inherited `WSL_UTF8=1` cannot turn every name into NUL-interleaved garbage |
| 6 | `-d Ubuntu --exec echo '$HOME'` (§15f #1) | exit 0, stdout `24 48 4f 4d 45 0a` = `$HOME\n` — the argument arrived **verbatim** | the client uses `--exec`, always |
| 7 | `-d Ubuntu -- echo '$HOME'` (§15f #1) | exit 0, stdout `/home/user\n` — the distribution's shell **expanded** it | `--` is never sent; the fake refuses it |
| 8 | Linux stdout encoding | `--cd / --exec printf 'café ж'` → `63 61 66 c3 a9 20 d0 b6` — the program's own UTF-8 bytes, untouched | the daemon's JSON is read as UTF-8 |
| 9 | the daemon binary absent: `-d Ubuntu --cd / --exec /opt/wsl-care/bin/wsl-care --version` | **exit 1**, stdout empty, stderr ONE UTF-8 line: `<3>WSL (190724 - Relay) ERROR: CreateProcessCommon:818: execvpe(/opt/wsl-care/bin/wsl-care) failed: No such file or directory` | exit 1 is also the daemon's `RunFailed` — the collision §15g M6 predicted. "Not installed" is read ONLY from this signature (`notInstalledSignature`, the relay pid and the source line number as `\d+`), for exactly our path, at exit 1; anything else at 1 is an unknown failure |
| 10 | a deliberately missing path, `--exec /opt/wsl-care-missing-e5s1/nope` | exit 1, the same line naming that path; identical with `WSL_UTF8=1` (the relay's line is UTF-8 either way) | the signature must carry OUR path, or a missing helper would read as a missing daemon |
| 11 | a non-executable file, `--exec /etc/hostname` | exit 1, `… execvpe(/etc/hostname) failed: Permission denied` | a permission refusal of our path is an unknown failure, not "not installed" |
| 12 | a Linux program's own exit and stderr: `--exec sh -c 'echo to-err >&2; exit 2'` | exit **2**, stderr `to-err\n` (UTF-8) | `wsl.exe` passes the program's exit code through unchanged — the daemon's 0 / 2 / 70 / 130 reach the client as they are |
| 13 | an unknown distribution: `-d NoSuchDistro-e5s1 --cd / --exec true` | exit **4294967295** (as Node reports it on Windows; -1 as a signed 32-bit value), stderr empty, **stdout** UTF-16LE: `There is no distribution with the supplied name.\r\nError code: Wsl/Service/WSL_E_DISTRO_NOT_FOUND\r\n` | `signed32` in the runner; exit -1 = `wsl.exe` refused, its message read from STDOUT; the fake answers an unlisted distribution exactly so |
| 14 | an invalid option: `--no-such-option-e5s1` | exit 4294967295, stdout UTF-16LE `Invalid command line argument: --no-such-option-e5s1\r\nPlease use 'wsl.exe --help' to get a list of supported arguments.\r\n` | the same reading as 13 |
| 15 | does killing `wsl.exe` end the Linux process? `--exec sleep 37.123`, then `child.kill()` of THAT `wsl.exe` (pid 12432) after 3 s | before: `190746 190744 sleep 37.123`; `wsl.exe` ended `SIGTERM`; **0.5 s and 3 s later the `sleep` was gone** (`ps -eo pid,ppid,args`) | the runner's timeout is `child.kill()` of `wsl.exe` — no `taskkill /T` and no second Linux call |
| 16 | the same with SIGHUP ignored: `--exec sh -c 'trap "" HUP; exec sleep 38.517'` | `wsl.exe` ended; the `sleep` **survived, reparented to PID 1** (`190776 1 sleep 38.517`), and ended on its own | the Linux side is ended by a HANG-UP when the relay goes: a program that ignores SIGHUP outlives the kill. Whether `wsl-care` does: row 18 (2026-10-04) — it does not survive |
| 17 | timing | every `wsl.exe` own question answered in 46–59 ms; an `--exec` into the running distro in 130–150 ms | the WSL questions' ceiling (`LIST_TIMEOUT_MS`, 15 s) is three hundred times what they take |
| 18 | **2026-10-04, §15h #3** — does killing `wsl.exe` end a running `wsl-care preview --all --json` and the `docker` CLI child it waits on? A linux-x64 build of `wsl-care` at `9703aef` (`dotnet publish -r linux-x64 -p:PublishAot=false --self-contained` into `/tmp` — the distro has no `clang`, so NOT the AOT binary), started as the runner starts it (`--exec <binary> preview --all --json`, stdin ignored, stdout / stderr piped), then `child.kill()` of THAT `wsl.exe` only. Twice: killed after 1.5 s (during `docker system df`) and after 3.2 s (during `docker system df -v`), 12 containers running; a full preview took 5.2 s | before: `2626 2624 2626 2626 Ssl+ … wsl-care preview --all --json` and its child `2671 2626 2626 2626 Sl+ /usr/bin/docker system df -v --format {{json .}}` — `wsl-care` leads its own session and process group, both in the foreground (`+`); `wsl.exe` ended `SIGTERM`; **0.5 s, 3 s and 10 s later neither `wsl-care` nor the `docker` child was left** (the same with stdin piped) | the relay's hang-up reaches the whole foreground group, the .NET runtime ends on it, and the `docker` child goes with it: the runner's `child.kill()` of `wsl.exe` is enough and NO daemon-side cleanup (exit on stdin EOF / SIGHUP) is needed. Not measured: the AOT binary itself — the E5 live gate's installed daemon (`POST_DEPLOY.md`) |
| 19 | **2026-10-04, E5 code round C4** — where does `-d Ubuntu --cd ~` start? `--cd '~' --exec pwd` (WSL 2.7.10.0) | exit 0, stdout `/home/<user>` — the distribution user's home, not the Windows folder the caller was started from | *Install daemon*'s terminal is `-d <distro> --cd ~` |
| 20 | **2026-10-04, plan §15j M2 / coai E6 plan round #6, measured by the coordinator** — does stdin reach a Linux program through `wsl.exe` byte for byte? Node 24 `spawn` of `%SystemRoot%\System32\wsl.exe -d Ubuntu --cd / --exec /usr/bin/wc -c`, writing 650 000 bytes (10 000 × 64-hex + LF — a full shown list) then `end()` | `wc` received **650000**, exit 0, ~250 ms; identical with `WSL_UTF8=1` inherited | the M2 relay row: `--only -` (stdin) carries a full shown list through the relay, EOF included. Re-checked at the E6 daemon live gate with `-u root` (not measured here: no `-u` in this note) |

## What was NOT measured, and why

- **A distribution that is not running.** All three were running, and stopping or terminating one is forbidden for this
  task. What `--list --running --quiet` prints when NO distribution runs (an empty answer, or a sentence with a non-zero
  exit) is therefore not known: the client treats ANY failure of the running check as "not running" and makes no `-d`
  call either way, and the fake can answer both shapes (`noneRunning: 'empty' | 'message'`). An E5 live-gate
  observation.
- **A binary built for a newer glibc** ("GLIBC_2.38 not found"): no distribution here has a glibc old enough. The
  loader's documented shape (`` <binary>: /lib/…/libc.so.6: version `GLIBC_2.38' not found (required by <binary>) ``)
  is matched by `GLIBC_MISSING` at any exit code; the line itself is unobserved.
- **The daemon's own stderr through `wsl.exe`** (its coloured console log lines before a `wsl-care:` message): the
  binary is not installed here. E1.S3 observed the shape on the built CLI directly; through `wsl.exe` it is a live-gate
  check.
- **Which settings file a UI-kind extension reads in a Remote-WSL window** (§15g M3). It needs an interactive VS Code
  attached to the distribution; nothing here can open one. The settings are `"scope": "application"`, which VS Code
  documents as user settings only (never workspace, never remote) — an E5 live-gate observation, not a claim.

## Predictions against observations

Written in the plan before the run (§15f #1, §15g M6) and checked here:

| Predicted | Observed |
|---|---|
| `wsl.exe`'s messages and `--list` are UTF-16LE, Linux stdout UTF-8 | yes (2, 3, 4, 8, 13) — plus one unpredicted case: `WSL_UTF8=1` makes `wsl.exe`'s own output UTF-8 (5) |
| `-- …` hands argv to the distro's shell, `--exec` does not | yes (6, 7) |
| a missing binary likely collides with `RunFailed` = 1 | yes (9) |
| (no prediction for the kill) | killing `wsl.exe` ends an ordinary Linux process (15) but not one that ignores SIGHUP (16). The family's credential-store extension states that `wsl.exe` "starts a distribution process that outlives the signal" and tree-kills; for an ordinary process that was not the case here |
