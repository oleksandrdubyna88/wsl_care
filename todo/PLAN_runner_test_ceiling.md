# PLAN — the runner test "a child past its ceiling…" waits on a ceiling shorter than node's start-up

> Status: **plan only, nothing implemented yet, 2026-10-18.** Scope: one test of the extension, `src_vs_code/src/test/runner.test.ts`.
> Found by the WR-E2 full runs (2026-10-17/18, [PLAN_windows_runner.md](PLAN_windows_runner.md) §14 *WR-E2 as built*): a load-only flake that is
> not a defect of the runner. Related docs: [module_tests.md](../research/module_tests.md), [module_vs_code.md](../research/module_vs_code.md).

## 1. Symptom

`npm test` on a loaded machine (other agents' test runs, a build) fails ONE test now and then, and passes when run alone:

```
✖ a child past its ceiling is reported timed out within the ceiling plus the kill grace, and is dead afterwards
  AssertionError [ERR_ASSERTION]: the child printed its pid before hanging
```

Measured 2026-10-17/18: run alone, the file passed 15 of 15 on two runs and failed this case on a third; in the whole suite (8 test files at once, two other
heavy runs on the machine) it failed in one full run of the WR-E2 branch, a branch that did not change the runner's kill-less path this case exercises.

**Cause (read in the code).** The test starts a node child that prints its pid and hangs, with a ceiling of **700 ms**
(`runner.test.ts:50`, asserted back at `:54` and bounded at `:55`) and then asserts that the child printed its pid before the ceiling
(`runner.test.ts:57`: `pid > 0`). Node's own start-up — a new process, its module loading, the first write — is not bounded by 700 ms
when the machine is loaded; a child that has not printed by then is killed at the ceiling with an empty stdout, `Number('')` is 0, and
the assertion reads "the child printed its pid before hanging". The runner behaved exactly as specified (`process/runner.ts`: the ceiling,
then the kill, then the grace). Only the test's premise — "node starts within 700 ms" — is false under load.

## 2. Goal

The test proves what it names — a child past its ceiling is reported `timedOut`, within the ceiling plus the kill grace, and is dead
afterwards — on a loaded machine as on an idle one, without lengthening every run and without weakening the assertions.

## 3. Proposed fix (one of two; the first is preferred)

1. **A ceiling derived from a measured start-up, not a guess.** Before the case, the test measures how long this machine takes to start
   the same node child and print (`script('process.stdout.write("x")', 10_000)` timed once, or the median of three), and uses
   `ceiling = max(700, 4 × measured)` — the child then has a start-up margin in proportion to the machine's own load. The bounds at
   `:55` follow the ceiling (`ceiling + KILL_GRACE_MS + 3_000`, already so written). Cost: one extra node start per run of the case.
2. **A fake clock** for the ceiling (`Run` takes its timers from the caller, as `ProcessRequest.timeoutMs` is the only timer today): the
   ceiling is fired by the test AFTER the child printed. It would also remove the real-time wait, but it needs a timer seam in
   `runner.ts` — a larger change for one test, so option 1 first.

A third option is rejected: **a longer fixed ceiling** (say 5 s) only moves the flake to a slower machine and makes the case wait that
long every run.

## 4. Build order

1. Write the measurement helper and the derived ceiling in `runner.test.ts` (test code only; no product change).
2. Make the case read the ceiling from it; keep every assertion (`timedOut`, `timeoutMs`, the elapsed bound, the pid, `alive(pid) === false`).
3. Prove the teeth: with the runner's kill removed the case still fails (the child stays alive); with the ceiling forced back to 700 ms
   under an artificial 2 s start delay the old case fails and the new one passes.

## 5. Test plan

- The case itself, run 20 times alone and 3 times inside the whole suite on a loaded machine: no failure.
- A unit test of the derived ceiling's arithmetic: `max(700, 4 × measured)` for a measured 50 ms, 300 ms, 1 s.
- Break-it on product code: `child.kill()` removed from `Run.stop` → the case is red ("the timed-out child … is still running").
- Break-it on the test's premise: a delayed child (a node child that sleeps 2 s before printing) with the old fixed ceiling is red, with
  the derived ceiling green — shown once, in the story's report, not kept as a slow test.

## 6. Definition of Done

- [ ] `runner.test.ts` derives the case's ceiling from a measured start-up; no fixed 700 ms premise remains.
- [ ] Every assertion of the case is unchanged or stricter; the runner's source is untouched.
- [ ] The case passes 20 of 20 alone and in three full runs on a loaded machine.
- [ ] `module_tests.md` names the derived ceiling where it names this test.
- [ ] This plan moves to `research/` as IMPLEMENTED with its date (`/promote-plan`), and its row leaves `todo/README.md`.
