import { TASK_NAMES, type TaskName } from '../client/verbs';
import { registerTaskScript, removeTasksScript as removeScriptOf } from '../scheduler/registerScript';
import { runTaskScript } from '../scheduler/runScript';
import { TASK_FOLDER } from '../scheduler/taskFolder';

/**
 * The two PowerShell texts of the Windows tasks' install and remove (WR-E3 of PLAN_windows_runner.md §3.8): thin wrappers over the
 * peer's (`scheduler/registerScript.ts` — the ONE home of `Register-ScheduledTask` and the deletes) with this part's constants. Into
 * the install go the folder constant, a task name re-checked against the CLOSED list here (a cast cannot widen it), and the XML the
 * daemon rendered; into the removal nothing but the two names. The removal never names the guard's task, so it can never delete it —
 * and the folder goes only when nothing else is in it.
 */

/** The install of `task` with the daemon's rendered `xml`: a foreign task name is a `RangeError`, an XML with a terminator line too. */
export function installTaskScript(task: TaskName, xml: string): string {
  if (!TASK_NAMES.includes(task)) {
    throw new RangeError(`"${String(task)}" is not one of the two Windows tasks`);
  }

  return registerTaskScript(TASK_FOLDER, task, xml);
}

/** The start now of `task`: the person's own registered task, through the task's own `Run`. A foreign task name is a `RangeError`, checked against the closed list here (a cast cannot widen it). */
export function runNowScript(task: TaskName): string {
  if (!TASK_NAMES.includes(task)) {
    throw new RangeError(`"${String(task)}" is not one of the two Windows tasks`);
  }

  return runTaskScript(TASK_FOLDER, task);
}

/** The removal of both Windows tasks, and of their folder when it is then empty. */
export function removeTasksScript(): string {
  return removeScriptOf(TASK_FOLDER, TASK_NAMES);
}
