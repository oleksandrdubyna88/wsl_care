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

- The interop proof pairs relays and Windows children by ORDER per program path, only when the counts are equal and a tie
  (relays started within a configurable number of seconds) agrees on the client's state; anything else keeps every child.
- The distro is named by the `wsl.exe` parent's command line; a stopped distro is never started to ask.
- Not measured, and never assumed: whether ending a client-gone relay ends its Windows child too (owner question Q-S7b-3).
