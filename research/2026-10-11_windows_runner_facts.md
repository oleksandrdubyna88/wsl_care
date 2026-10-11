# How can the bundled `wsl-care.exe` run on its own? — measured 2026-10-11

> What [PLAN_windows_runner.md](../todo/PLAN_windows_runner.md) rests on. Every step was **read-only** on the owner's
> machine (Windows 11 Pro 10.0.26300), from an unelevated shell. No task was registered, nothing was installed, and the one
> product binary that was started ran `--version` only, from a scratch folder.

## 1. The cadence the CPU ledger needs

Read in the code, not measured on the machine:

| fact | where |
|---|---|
| a ledger point older than `mcpServers.cpuIntervalMaxMinutes` is no baseline; default **20** minutes | `src_daemon/src/WslCare.Core/Config/default.json:353`, `ConfigKeys.Numbers.cs:620` |
| the WSL watch period must stay under that maximum, or no sample finds a baseline | `src_daemon/src/WslCare.Core/Config/NumberRules.cs:91-92` |
| the Windows ledger advances only when `wsl-care.exe status` runs | `todo/PLAN_twenty_sessions_all_day.md` § S7b.1 |

So a run every 4 hours (E11's `\wsl-care\collect` in `todo/PLAN_windows_care.md` §2) cannot give A22 a dense CPU chain. The
Windows side needs a short sampling cadence of its own, as the WSL side has its 5-minute watch timer (S2b).

## 2. Can an unelevated user create a task in `\wsl-care`?

Task Scheduler COM (`Schedule.Service`), `GetFolder('\wsl-care').GetSecurityDescriptor(4)`:

| item | value |
|---|---|
| `\wsl-care` exists | yes — created by the elevated install of the Windows Time guard; it holds `windows-time-guard` |
| its DACL | `D:AI(A;CIID;FA;;;BA)(A;OIIOID;0x1f019f;;;BA)(A;CIID;FA;;;SY)(A;OIIOID;0x1f019f;;;SY)(A;CIID;FW;;;AU)(A;CIID;FW;;;NS)(A;CIID;FW;;;LS)(A;OICIIOID;FA;;;CO)` |
| the root folder's DACL | the same entries, not inherited (`PAI`) |

The folder only inherits the root's entries. `(A;CIID;FW;;;AU)` lets Authenticated Users create tasks in it, and
`(A;OICIIOID;FA;;;CO)` gives the creator full rights on the task it created. So the folder's ACL does not stop an unelevated
registration or removal of the user's own task. **Not tried:** the registration itself. The first one is the owner's press.

## 3. Does a copy of the attested exe run?

`bin/wsl-care.exe` (daemon 0.4.0, fetched and attested by `fetch-windows-daemon.sh`) was copied to a fresh scratch folder:

| check | result |
|---|---|
| SHA-256 of the copy and the original | equal (`a14ad2eb…`) |
| `Get-AuthenticodeSignature` | `NotSigned` |
| `wsl-care.exe --version` from the copy | exit 0: `0.4.0+9cacb0bd033e3208311794b2d3f07bbf0aa498e1` |
| `HKLM\SYSTEM\CurrentControlSet\Control\CI\Policy` `VerifiedAndReputablePolicyState` | `0` |

The copy runs like the original. The Smart App Control value reads 0 today, while the 2026-10-10 record found SAC enforced; a
blocked start must still be shown with Windows' own message.

## 4. What is left for the owner's first press

- the per-user registration (Interactive logon, limited run level) and its removal, without UAC;
- the first task run: its result code, the token's elevation (expected: not elevated), and a run record the panel reads.
