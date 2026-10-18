/**
 * What the ONE Task Scheduler query prints (`windowsTime/guardScripts.ts` `QUERY_SCRIPT`), built for the tests: the tagged lines of
 * the folder, then one `task=<name>` section per task — never typed by hand in a test, so the fixtures cannot drift from each other.
 */

export interface TaskFixture {
  readonly name: string;
  readonly enabled?: string;
  /** Task Scheduler's `State`: 3 ready, 4 running, 1 disabled. */
  readonly state?: string;
  readonly lastRunUtc?: string;
  readonly lastResult?: string;
  readonly summary?: readonly string[];
  /** The task's own read failed: `read=unreadable` and this HRESULT, and nothing else of the task. */
  readonly unreadable?: string;
  /** Lines printed after the task's own, verbatim (a stray or hostile line). */
  readonly extra?: readonly string[];
}

export interface SchedulerFixture {
  readonly channel?: string;
  readonly stamp?: string;
  readonly user?: string;
  /** `present` (default), `absent`, or an HRESULT for `unreadable`. */
  readonly folder?: 'present' | 'absent' | { readonly unreadable: string };
  readonly tasks?: readonly TaskFixture[];
}

export const USER_SID = 'S-1-5-21-1000000000-2000000000-3000000000-1001';

function taskLines(task: TaskFixture): string[] {
  const head = [`task=${task.name}`];
  if (task.unreadable !== undefined) {
    return [...head, 'read=unreadable', `hresult=${task.unreadable}`, ...(task.extra ?? [])];
  }

  return [
    ...head,
    `enabled=${task.enabled ?? 'True'}`,
    `state=${task.state ?? '3'}`,
    `lastRunUtc=${task.lastRunUtc ?? '2026-10-16T08:00:00.0000000Z'}`,
    `lastResult=${task.lastResult ?? '0'}`,
    ...(task.summary ?? []).map((line) => `summary:${line}`),
    ...(task.extra ?? []),
  ];
}

function folderLines(folder: SchedulerFixture['folder']): string[] {
  if (folder === undefined || folder === 'present') {
    return ['folder=present'];
  }

  return folder === 'absent' ? ['folder=absent'] : ['folder=unreadable', `hresult=${folder.unreadable}`];
}

/** The stdout of one query, `\r\n` line ends as Windows PowerShell writes them. */
export function schedulerStdout(fixture: SchedulerFixture = {}): string {
  const lines = [
    `channel=${fixture.channel ?? 'enabled'}`,
    `stamp=${fixture.stamp ?? 'absent'}`,
    `user=${fixture.user ?? USER_SID}`,
    ...folderLines(fixture.folder),
    ...(fixture.tasks ?? []).flatMap(taskLines),
  ];

  return `${lines.join('\r\n')}\r\n`;
}
