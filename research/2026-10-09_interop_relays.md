# How a Windows MCP server under `wsl.exe` relates to its client in the distro — measured 2026-10-09

> The fact the S7b stop on Windows rests on ([PLAN_twenty_sessions_all_day.md](../todo/PLAN_twenty_sessions_all_day.md)
> § *S7b*): can it be SHOWN that the WSL client of a Windows interop child is gone? Every read below is **read-only and
> unelevated**: one `Get-CimInstance Win32_Process` on Windows, and two script files run in the distro as the default user
> (`/proc` reads only, output to stdout). No process was signalled, stopped or started, apart from the measuring `wsl.exe`
> itself. One `creds.exe` command line carried a credential key; it is not copied here, and no argument of any process
> beyond its program path is.
>
> - **Machine:** the owner's (Windows 11 Pro 10.0.26300, WSL 2, distro Ubuntu, one VS Code window connected to WSL).
> - **Time:** 2026-10-09 18:46:41Z (Windows read) and 18:46:51Z–18:47:09Z (distro reads).

## 1. The Windows side

| Parent | `creds-mcp.exe` / `coai-mcp.exe` children |
|---|---|
| `wsl.exe` pid 38052 — `wsl.exe -d Ubuntu sh -c "$VSCODE_WSL_EXT_LOCATION/scripts/wslServer.sh" … code-server …` (VS Code's WSL connection), created 09:12:52Z | **16** `creds-mcp.exe` |
| six `claude.exe` (Windows Claude Code sessions) | 2 each (12) |
| gone | 0 |

The interop children were created between 09:15:06Z and 17:09:25Z (Windows clock). Their own command line is the bare
program name (`creds-mcp.exe`). The parent `wsl.exe`'s command line names the distro (`-d Ubuntu`).

## 2. The distro side — every interop child has a relay

A Windows program started from the distro runs as a Linux process whose `/proc/<pid>/exe` is **`/init`** and whose argv is
`/init <the program's /mnt path> <argv…>` — the interop relay (binfmt_misc `WSLInterop`, interpreter `/init`).

For `/mnt/c/Users/<user>/AppData/Local/Programs/creds/creds-mcp.exe` there were **16 relays**, the same count as the 16
Windows children under the VS Code `wsl.exe`:

| The relay's parent | Relays | What it means |
|---|---|---|
| the distro's `creds-mcp` shim (`~/.local/bin/creds-mcp`), itself a child of a `claude` session | **6** | the client lives |
| `Relay(1402)` (pid 1401) — the WSL session's init | **10** | the caller that exec'd the relay is gone; the relay was re-parented |

The 10 re-parented relays started at 09:55, 16:18, seven at 17:09:11–12 and one at 17:09:14 (guest wall clock, see §3).
Other interop relays seen: `creds.exe` (the credential store's CLI) under a `creds` process and under `Relay(1402)`;
they are not MCP servers.

**So "the client is gone" is a Linux fact:** a relay whose parent is the session's init (`Relay(<n>)`), or whose parent pid
is gone, has no caller any more — readable from `/proc` without timing or guessing.

## 3. The two clocks cannot pair them

The oldest Windows child was created at 09:15:06Z (Windows clock); the oldest relay's start computed as btime + start ticks
reads 09:27:12Z. The guest's wall clock is stepped on this machine (`clock.jumps` 301 in 2 h, the same day's POST_DEPLOY
evidence), and btime + start ticks inherits every step since boot. **A relay and a Windows child cannot be paired by wall
time.** Their ORDER is reliable on each side: start ticks are the guest's monotonic clock, and a relay creates its Windows
child after it starts.

## 4. What the design takes from this (S7b)

A first draft paired relays and Windows children by ORDER. The plan round (coai `1bc694ea` and the own Opus review)
refuted it, and the refutation is part of this record:
- **Order is not identity.**
  - A slow launch can invert two children outside any tie window: Defender scanning the `.exe`, or a cold interop server.
  - The Windows creation time is itself a steppable wall clock.
  - The Windows snapshot and the distro read are seconds apart, so one child exiting and another starting shifts every pair.
- **"Re-parented to the session init" is not "client gone" in every case:**
  - a relay run as `wsl.exe`'s top-level command is born there while its Windows-side caller lives;
  - a daemonising caller re-parents it while still holding its pipe;
  - any process can name itself `init`.

So the Windows side pairs nothing and stops no interop child. The 10 client-gone relays above remained a measured fact, not
a proof per Windows process. The exact route — ending the client-gone relay ITSELF in the distro, by pid and start ticks —
needed one fact first: whether ending a relay ends its Windows child. §5 measures it. **The route works**, which corrects
this section's first conclusion ("the interop route refuted": only the cross-OS PAIRING was refuted, not the distro route).

## 5. Q-S7b-3, measured 2026-10-10 — the Windows child exits with its relay

The owner approved the experiment, and the coordinator ran it. This agent ran nothing on the machine; the scripts were the
ones prepared for it, kept in [`diagnostics/`](diagnostics/): [`relay-list.sh`](diagnostics/relay-list.sh), [`win-sampler.ps1`](diagnostics/win-sampler.ps1) (its argument is the `wsl.exe` pid) and [`relay-term.py`](diagnostics/relay-term.py).

| time (UTC) | side | what |
|---|---|---|
| 13:59:54Z | distro | `relay-list.sh`: 2 `creds-mcp.exe` relays. Pid **12062**, start ticks 36501, parent `Relay(7411)` (uid 0), **client gone**. Pid 13226, parent `creds-mcp`, client alive |
| 14:00:26.426Z | Windows | sampler START: 2 `creds-mcp.exe` under `wsl.exe` 17372 — `34248@09:49:55.699667Z`, `40308@09:50:23.631016Z` |
| 14:01:23.532Z | distro | `relay-term.py 12062 36501`: every check passed (same start ticks, parent a uid-0 `Relay(n)`, exe `/init`, program `creds-mcp.exe`, this user's, no other holder of its stdio); **SIGTERM** sent through the pidfd |
| 14:01:23.558Z | distro | the relay exited, **26 ms** after SIGTERM. No SIGKILL was needed or sent |
| 14:01:23.735Z | Windows | sampler CHANGE: `gone=[34248]`, **about 0.2 s** after the relay's SIGTERM |
| 14:02:26Z | Windows | sampler END: count 1. `40308`, whose relay has a live client, untouched |

**Conclusion:**
- A Windows interop child exits within about 0.2 s of its distro relay ending on SIGTERM.
- The relay of a live client, and its Windows child, are not affected.
- So the leak is closed EXACTLY from the distro side: the relay is the user's own Linux process, identified by pid and start
  ticks, and its "client gone" evidence is local to `/proc`. No cross-OS pairing is needed.
- S7b.2 (in [PLAN_twenty_sessions_all_day.md](../todo/PLAN_twenty_sessions_all_day.md)) builds that route.

**Not covered by this one sample:**
- a relay that ignores SIGTERM;
- a relay born under `Relay(n)` (`wsl.exe`'s top-level command);
- a daemonising caller.

The design keeps all three: TERM only, a relay born under its `Relay(n)` kept (§ 6), and no other holder of its piped stdio.

## 6. What the S7b.2 review found unmeasured — read 2026-10-10 14:25Z

The S7b.2 plan review (the own Fable reviewer) asked for three facts that § 5's run did not record:
- what a relay's fd 0/1/2 are: pipes, sockets or files. The two ends of a socketpair have different inodes, so an inode scan
  cannot see a socket's peer;
- whether `Relay(n)` (uid 0) holds a relay's stdio;
- the session id of a command born under `Relay(n)`.

[`diagnostics/relay-facts.sh`](diagnostics/relay-facts.sh) prints all three that the user can read: the ancestry with session
ids, the stdio links, and each pipe's or socket's other readable holders, with a socket's peer from `ss -xpn`. Run as the
default user at 14:24:59Z, it found **no `.exe` relay at all**: no AI-agent session was running in the distro. Another
read-only listing at the same time showed the shape of the session inits:
- `Relay(562)` (pid 540), `Relay(1136)` (pid 1135), `Relay(220668)` (pid 220666), `Relay(225368)` (pid 225366);
- the child of `Relay(220668)` is pid **220668**.

So, with `Relay(1402)` (pid 1401) in § 2, a `Relay(n)` is created for one command and `n` is that command's pid. A relay whose
pid equals its parent's `n` was born there (`wsl.exe`'s top-level command); the client-gone relay 12062 of § 5 was under
`Relay(7411)`. S7b.2 keeps the born-there relay by this test and does not use the session id. It keeps a relay whose stdio is
a socket until a peer can be shown gone.
