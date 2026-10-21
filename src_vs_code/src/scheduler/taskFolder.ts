/**
 * The Task Scheduler folder every task this extension registers lives in, without its leading backslash (WR-E3 of
 * PLAN_windows_runner.md §3.8). It is a constant of the PEER (`src/scheduler/`) because both consumers need it — the Windows Time
 * guard's task and the two Windows tasks — and neither may import the other: `windowsTime/guardTask.ts` re-exports it as
 * `GUARD_FOLDER`, `windowsTasks/` takes it from here.
 */
export const TASK_FOLDER = 'wsl-care';
