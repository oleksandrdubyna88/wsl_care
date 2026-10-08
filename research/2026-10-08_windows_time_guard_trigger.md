# What a Scheduled Task can trigger on when `w32time` stops — measured 2026-10-08

> The measurement build step 1 of [PLAN_windows_time_task.md](PLAN_windows_time_task.md) asked for before any
> trigger was written: can an `EventTrigger` match the Service Control Manager's event 7036 for the Windows Time service
> entering the stopped state? Every read below is **read-only and unelevated** (`Get-WinEvent`, `Get-Service`, the
> `Schedule.Service` COM object's `GetFolder`/`GetTask`); no task was registered, no service was started, stopped or
> reconfigured, and no UAC prompt was raised. The one process this task started that was not a read is §4's: an
> UNELEVATED `powershell.exe` through `ShellExecute`, whose only act was writing its own command-line length into the
> session's scratch folder.
>
> - **Machine:** Windows 11 Pro 10.0.26300, UI culture `en-US`, the same machine as
>   [2026-10-08_windows_time_stopped.md](2026-10-08_windows_time_stopped.md). The interactive user's SID is written
>   `S-1-5-21-…-1001` and the machine name is left out (this repository is public).
> - **When:** 2026-10-08, ≈ 12:40–13:10Z.
> - **Prediction recorded before the run** (the plan's text): event 7036 is in the System log for each stop, its `Data`
>   text localised and its `Binary` field the UTF-16 hex of the service key name; the open question was only which of the
>   two an XPath can match.

## 1. Event 7036 is not logged on this machine at all

| # | Read | Value |
|---|---|---|
| T1 | System log, records and span | 33 183 records, oldest 2026-10-05T12:39:39Z (circular, 20 MB) |
| T2 | `*[System[Provider[@Name='Service Control Manager'] and EventID=7036]]` in System | **0 events** |
| T3 | every 7036 in System, by provider | 56, all `VfpExt` (a Hyper-V switch extension reusing the number) — none from the SCM |
| T4 | every event of the provider `Service Control Manager` in the window | 32: **7040** × 28 (start type changed), 7026 × 4 (a boot driver did not load) |
| T5 | stops of `w32time` inside the same window, from the service's own log (§2) | 3 (2026-10-06 20:04Z, 2026-10-07 20:07Z, 2026-10-08 09:50Z) |

**Observed against predicted:** the prediction was wrong at its first premise. On this build the SCM writes no 7036 —
not for `w32time`, not for any service — while `w32time` stopped three times in the window. An `EventTrigger` on 7036
would never fire here, by `Data`, by `Binary` or by anything else, so the trigger the plan named is dropped.

## 2. The Windows Time service logs its own stop — event 258 — and an XPath matches it by number alone

The channel `Microsoft-Windows-Time-Service/Operational` (enabled, circular, 1 MB, oldest record 2026-08-31T17:13Z):

| # | Read | Value |
|---|---|---|
| T6 | event ids in the channel | 257 × 40 (*"W32time service has started at …"*), **258 × 25** (*"W32time service is stopping at …"*), 259, 260, 261 × 47 (*"has set the system time to …"*), 263, 264, 265, 266, 272 |
| T7 | event 258's XML | `Provider Name='Microsoft-Windows-Time-Service'`, `EventID 258`, `Security UserID='S-1-5-19'` (LocalService — the service itself, so it does NOT name who asked it to stop), `EventData Name='TMP_OPS_SHUTDOWN'` with `CurrentTime(UTC)`, `TickCount`, `ErrorMessage` (`0x00000000: Success.`) — no localised text is needed to match it |
| T8 | the exact `QueryList` the task carries, run through `Get-WinEvent -FilterXml` | `<QueryList><Query Id="0" Path="Microsoft-Windows-Time-Service/Operational"><Select Path="Microsoft-Windows-Time-Service/Operational">*[System[Provider[@Name='Microsoft-Windows-Time-Service'] and EventID=258]]</Select></Query></QueryList>` → **25 matches**, 2026-08-31T21:29Z … 2026-10-08T09:50:40Z |

So the event trigger is **258 by provider and number** — matched without any `Data` value, so the language of the
machine does not matter. `Get-WinEvent -FilterXml` and a task's `EventTrigger` both take the event log's own XPath subset
(an `EvtQuery` and an `EvtSubscribe` of the same query); that the REGISTERED trigger fires is not measured here — it
cannot be without registering a task — and is `POST_DEPLOY.md`'s check after the owner installs it.

**What 258 does not cover:** a stop that never reaches the service's own handler (its host process killed) logs nothing,
and a disabled channel logs nothing. The periodic trigger is what catches both; nothing here relies on the event alone.

## 3. What the two logs say about this machine's clock — read, not explained

| # | Read | Value |
|---|---|---|
| T9 | boots (Kernel-General 12), stamped with Windows' clock at the time | 2026-10-08 **05:43:03Z** and **09:43:03Z**; 2026-10-07 07:33:46Z; 2026-10-06 07:20:05Z |
| T10 | the corrections after a boot (261, *"Previous system time was …"*) | 10-08: 06:00:22.6 → **08:00:23.2** and 09:50:41.8 → **11:50:41.4**; 10-05: 06:03:59.8 → 08:03:59.9; 09-29: 13:44:47.9 → 15:44:48.0 — each **+7 200 s**, each a few seconds after a service START (257) |
| T11 | `w32time` at the 09:43Z boot | started 09:43:20 (257), *"receiving valid time data"* 09:43:21 — and did NOT correct the 2 h until it was stopped (258, 09:50:40) and started again (257, 09:50:41) |
| T12 | Windows' own boot sync task `\Microsoft\Windows\Time Synchronization\ForceSynchronizeTime` | last run 09:43:20Z (local 11:43:20), result 0 — it ran at that boot, and the clock stayed 2 h wrong |
| T13 | 7040 for W32Time (`EventData/Data[@Name='param4']='W32Time'` — the key name, not localised) | 4: demand start → **disabled** → demand start at 06:00:19–20Z and again at 09:50:40–41Z, each by `S-1-5-21-…-1001` (a process in the interactive user's account), each a second before the restart in T10 |
| T14 | the evening stops (258) | 20:04Z, 20:07Z, 20:37Z, 20:46Z, 21:06Z, 22:05Z on six evenings since 2026-09-30, return code `Success` |
| T15 | now | `Get-Service w32time`: `Running`, `Manual` |

**What this licenses:** on this machine Windows comes up **2 h slow after a boot** (twice today, and on 10-05 and 09-29),
and a running service that receives valid data, and Windows' own boot sync task, left it wrong for minutes (T11, T12).
§5 (T18) narrows what put it right: each correction followed an EXPLICIT resync request, not the start alone. That makes
the guard's *at startup* and *at logon* runs the part that matters most, and it is why the action resyncs even when the
service already runs.

**What it does not settle:** why the boot clock is 2 h behind (the RTC-in-UTC hypothesis of the incident record, Q5
there — still not measured); who stops the service each evening (258 carries the service's own SID, T7); and who made the
two disable/re-enable pairs (T13 names an account, not a program — the 06:00Z pair is the minute of the owner's manual
restart in the incident record, O8). Whether `w32tm /resync /force` on a service that has run since boot, without a
restart, corrects the 2 h is not measured directly — §5 T18–T19 is the evidence that it should.

## 4. How long a command line the elevated launch can carry

The install command hands the task's whole definition to ONE elevated PowerShell as `-EncodedCommand`, so its length is
bounded by the process launch.

| # | Read | Value |
|---|---|---|
| T16 | `ProcessStartInfo{UseShellExecute=true}` (no `runas`), `Arguments` of 1 911 / 8 579 / 21 915 / 32 583 characters | all four started; the child's `[Environment]::CommandLine.Length` 1 971 / 8 639 / 21 975 / **32 643** — nothing truncated |

`CreateProcess`' documented ceiling is 32 767 characters for the whole command line; the measured 32 643 sits under it.
The ELEVATED path (`runas`, through the AppInfo service) was not measured — measuring it raises a UAC prompt — so the
extension keeps the elevated command line under a fixed bound well inside both (`windowsTimeGuard.ts`, held by a test),
rather than near the measured edge.

## 5. Added after the plan round (same day, same conditions — read-only)

| # | Read | Value |
|---|---|---|
| T17 | `w32tm /resync /force /computer:wslcare-probe.invalid` against `/resync /bogus …` (a host that cannot exist, so nothing here is asked to resync) | `/force` parses — the call reaches *"The RPC server is unavailable"* exactly like `/resync` without it — while `/bogus` is refused *"The following arguments were unexpected"*. `/force` is NOT in this build's `w32tm /?` text: accepted, undocumented |
| T18 | event 266 (*"received notification to … resynchronize"*) and its reason code, around each correction | 09:43:26 and 09:45:33 reason **2** (network change) — no correction; 09:50:41.75 reason **0** (*"An explicit time resynchronization request from an administrator"*) → 261 the 2 h step 40 ms later; 06:00:21 reason 0 → the 08:00:23 step; 10-05 06:03:59 reason 0 → the step. Every correction followed an explicit resync, 37–50 ms after the start |
| T19 | `HKLM\SYSTEM\CurrentControlSet\Services\W32Time\Config` | `MaxPosPhaseCorrection` = `MaxNegPhaseCorrection` = **54 000 s** (15 h) — a 7 200 s step is inside both; `NtpServer` `time.windows.com,0x9`, `SpecialPollInterval` 16 384 s (4.6 h) |
| T20 | `sc.exe qtriggerinfo w32time` | two START triggers (domain joined; a custom system state change), **no stop trigger** — the evening stops (T14) are not Windows' own trigger stopping it |
| T21 | the Task Scheduler root folder's security descriptor, and which tasks an UNELEVATED reader sees | root `D:PAI(A;CI;FA;;;BA)(A;OI;0x1f019f;;;BA)(A;CI;FA;;;SY)(A;OI;0x1f019f;;;SY)(A;CI;FW;;;AU)…` — Authenticated Users may CREATE in it but get no read on the tasks; an installer's SYSTEM tasks in their own folder (`\GoogleSystem\GoogleUpdater`) are simply **not listed** to the unelevated reader, while tasks carrying an explicit read ACE (`(A;;FR;;;BU)`, `(A;;CCRPRC;;;WD)`) are. A guard registered with the default descriptor would therefore read as *not installed* from the panel |
| T22 | `NewTask(0).XmlText` with `RegistrationInfo/SecurityDescriptor` `D:(A;;FA;;;SY)(A;;FA;;;BA)(A;;FR;;;AU)` and an `Exec` whose `Arguments` is a plain `-Command` with `&amp;` | parsed in memory; the descriptor and the arguments (`&` unescaped) read back as written |

**What changed because of it:** the guard keeps the owner's action (resync, no restart of a running service) — T18–T19
say an explicit resync is what put the clock right; the registered task carries an explicit read ACE for Authenticated
Users (T21), requested in the XML's `RegistrationInfo/SecurityDescriptor` only (the folder keeps the inherited descriptor,
which T21 shows the unelevated reader can open), and whether Task Scheduler applies it is a fact about the receiver — the
panel's own read-back after install is that observation (`POST_DEPLOY.md`).

| # | Read (added in the code round, same day) | Value |
|---|---|---|
| T23 | `GetFolder('\wsl-care-absent-probe')` and `GetTask('wsl-care-absent-probe')` through `Schedule.Service`, in **Windows PowerShell 5.1.26100** (the product's PowerShell, not pwsh 7) | `$_.Exception` is `System.IO.FileNotFoundException`, `HResult` **0x80070002** for both — the COM error itself, not a wrapper; the status query's "not installed" branch reads it, and the Windows-leg test now runs the real query unelevated (it answered `guard=absent` here) |

## 6. Added for the fifth trigger: the start-type change, matched by key name (same day, ≈ 17:00Z, read-only)

| # | Read | Value |
|---|---|---|
| T24 | one W32Time 7040's XML (RecordId 258308) | `Provider Name='Service Control Manager'`, `EventID 7040`, `Channel System`, `Security UserID` the account that made the change; `EventData` `param1` *Windows Time*, `param2` *demand start*, `param3` *disabled* (all three localised text) and **`param4` `W32Time`** (the service key name) |
| T25 | `Get-WinEvent -FilterXml` with `<QueryList><Query Id="0" Path="System"><Select Path="System">*[System[Provider[@Name='Service Control Manager'] and EventID=7040] and EventData[Data[@Name='param4']='W32Time']]</Select></Query></QueryList>` | **4 matches** — RecordIds 257828, 257829, 258308, 258310, exactly the four W32Time 7040s of T13 |
| T26 | the same without the `EventData` clause | 28 — every service's 7040 (BITS, IsolationSession, W32Time) |
| T27 | the same with `'w32time'` | the same 4 — the event log's string comparison is case-insensitive, so one spelling suffices |

So the fifth trigger matches by provider, number and key name, with no localised text, on a channel that is always
enabled. As for T8, a REGISTERED trigger firing is not measured here; `POST_DEPLOY.md` item 9 is that check.
