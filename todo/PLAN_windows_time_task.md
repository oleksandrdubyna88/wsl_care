# PLAN — the Windows Time task: restart `w32time` by itself when software stops it

> Status: **plan only, nothing implemented yet, 2026-10-08.** Scope: one extension command that registers (and removes) a
> single-purpose SYSTEM scheduled task on the Windows host; nothing in the daemon. Extracted from story 2 (D9) of
> [PLAN_windows_time_guard.md](../research/PLAN_windows_time_guard.md), whose story 1 shipped.
>
> Related: [2026-10-08_windows_time_stopped.md](../research/2026-10-08_windows_time_stopped.md) (the incident),
> [PLAN_windows_care.md](PLAN_windows_care.md) §2 *Admin work* and §8a (the generic elevated channel — NOT this),
> [module_vs_code.md](../research/module_vs_code.md) (*Start Windows Time*, the one-click fix this automates).

## 1. Symptom

On 2026-10-08 the Windows Time service `w32time` was found Stopped (StartType Manual) and Windows 7 200 s slow; the owner
says AMD driver software stops it. Story 1 of the guard detects it (`clock.timeService`, `clock.reference`) and gives a
one-click fix (*Start Windows Time*, which also sets StartType Automatic). But Automatic does NOT restart a service that
other software stops, so the fault returns between full runs and needs a person each time.

## 2. Goal

A task that (re)starts `w32time` and resyncs whenever it stops, with no person in the loop, registered ONCE with the
owner's consent — and that can do nothing else.

## 3. Design (to be reviewed in its own plan round)

- **Register:** an extension command *Install the Windows Time guard* shows the task XML in a modal and runs ONE elevated
  `schtasks /Create /XML` (or `Register-ScheduledTask`) through the same launcher shape as *Start Windows Time*
  (`src_vs_code/src/windowsTime/windowsTimeFix.ts`: `Process.Start` with `runas`, the UAC refusal read as 1223). Uninstall
  removes it the same way.
- **The task** `\wsl-care\windows-time-guard`: principal `SYSTEM`; triggers at startup, at logon, every `N` hours, and on
  the System log's event 7036 for W32Time entering the stopped state. The 7036 `Data` text is LOCALISED; the event's
  `Binary` field holds the service key name (UTF-16 hex of `W32Time`), which is not — MEASURE which one an `EventTrigger`
  XPath can match before relying on it.
- **The action** is fixed in the task definition itself — `powershell.exe -NoProfile -NonInteractive -EncodedCommand <the
  fix's inner script without Set-Service>` — no script file a user could edit; it only (re)starts `w32time` and resyncs.
- **A rate limit** — at most one start per `M` minutes (the task's own `MultipleInstancesPolicy` + a stamp the action
  checks) — so it never loops against software that stops the service again.

## 4. Build order

1. Measure the 7036 event's fields on this machine (read-only `Get-WinEvent`), and whether `EventTrigger` matches `Binary`.
2. The task XML as a module constant + its tests (parsed, never registered by a test).
3. The register / remove commands, the modal, the outcomes; the tripwire refuses `schtasks` from a test.
4. Docs, a `POST_DEPLOY.md` check that the task exists with exactly that action (replacing a weaker item: the file is
   at its cap of 12).

## 5. Test plan

- The XML parses (`[xml]`) and holds ONLY the fixed action; the principal is SYSTEM; the triggers are the four named.
- The launcher builds one request; a decline registers nothing; every exit is a closed outcome.
- No test registers a task or starts a service (the tripwire); a live check after install reads the task back.

## 6. Owner questions

- `N` (the periodic trigger) and `M` (the rate limit) — defaults 4 h and 10 min?
- Register from the extension (a person's click and UAC), or from `install.sh`'s Windows half when PLAN_windows_care.md's
  installer exists?

## 7. Definition of Done

- [ ] Plan round (coai `review_plan`) proceeded; findings resolved.
- [ ] The 7036 trigger measured and recorded in `research/`.
- [ ] Register / remove shipped with RED-first tests; no test registers a task.
- [ ] Docs and `POST_DEPLOY.md` updated; the plan promoted.
