import { notAnsweredWords } from '../process/notAnswered';
import type { ProcessResult } from '../process/runner';
import { signed32 } from '../process/runner';

/**
 * The ONE Task Scheduler read, parsed (WR-E2 of PLAN_windows_runner.md §3.8). The guard's unelevated query
 * (`windowsTime/guardScripts.ts` `QUERY_SCRIPT`) lists every task under `\wsl-care` and prints tagged lines: the folder's own
 * (`channel=`, `stamp=`, `user=`, `folder=`), then one section per task that opens with `task=<name>`. Everything here is pure
 * text → closed types, and the unit of failure is the TASK: a task whose own read failed, or whose fields do not parse, is
 * `unreadable` — its neighbours and the read as a whole are untouched (the story's plan round 4fc3f328, finding 3).
 *
 * Nothing in the text is trusted: a line opens a task only when it BEGINS `task=` (a hostile action argument is printed as
 * `summary:…`, so it cannot), the first occurrence of a tag or a task name wins, and every value a person reads goes through the
 * presentation edge's sanitiser, not through here.
 */

export type Channel = 'enabled' | 'disabled' | 'unknown';

/** One task's section of the answer, raw: its tags (`enabled`, `state`, `lastRunUtc`, `lastResult`, `read`, `hresult`) and summary lines. */
export interface TaskSection {
  readonly name: string;
  readonly tags: ReadonlyMap<string, string>;
  readonly summary: readonly string[];
}

export type FolderState =
  | { readonly kind: 'present' }
  /** `0x80070002`: the folder does not exist — a machine nothing was ever installed on. An EMPTY task set, not a failure. */
  | { readonly kind: 'absent' }
  | { readonly kind: 'unreadable'; readonly hresult: string };

export type SchedulerRead =
  /**
   * No answer: not asked yet, the query ended without one, or it timed out (`timeoutMs`; `treeEnded` is what the runner could say of
   * the PowerShell's process tree at that ceiling — true only when it VERIFIED it ended, absent when no tree kill was asked for).
   */
  | { readonly kind: 'unknown'; readonly cause: 'checking' | 'timedOut' | 'failed'; readonly reason: string; readonly timeoutMs?: number; readonly treeEnded?: boolean }
  | { readonly kind: 'read'; readonly user: string; readonly channel: Channel; readonly stampLeft: boolean; readonly folder: FolderState; readonly sections: ReadonlyMap<string, TaskSection> };

/** A task that has never run, or its last run: the instant as Task Scheduler printed it, and the unsigned 32-bit result. */
export type LastRun =
  | { readonly kind: 'never' }
  | { readonly kind: 'ran'; readonly utc: string; readonly result: number };

export type TaskRead =
  | { readonly kind: 'absent' }
  | { readonly kind: 'unreadable'; readonly reason: string; readonly hresult?: string }
  | { readonly kind: 'present'; readonly enabled: boolean; readonly running: boolean; readonly lastRun: LastRun; readonly summary: readonly string[] };

export const NOT_ASKED_READ: SchedulerRead = { kind: 'unknown', cause: 'checking', reason: 'checking…' };

/**
 * Task Scheduler's own last results — the ONE place their numbers and words live (the guard's rows and the Windows tasks' rows both
 * read them): `SCHED_S_TASK_RUNNING`, `SCHED_S_TASK_HAS_NOT_RUN`, `SCHED_S_TASK_TERMINATED` (stopped by its time limit) and
 * `SCHED_E_ALREADY_RUNNING` (a run skipped because one was still going).
 */
export const SCHEDULER_RESULT = { running: 0x41301, notRunYet: 0x41303, timeLimit: 0x41306, skipped: 0x8004131f } as const;

export const SCHEDULER_RESULT_WORDS: Readonly<Record<number, string>> = {
  [SCHEDULER_RESULT.running]: 'running now',
  [SCHEDULER_RESULT.notRunYet]: 'not run yet',
  [SCHEDULER_RESULT.timeLimit]: 'stopped by its time limit',
  [SCHEDULER_RESULT.skipped]: 'skipped — a run of it was still going',
};

/** `TASK_STATE_RUNNING`. */
const STATE_RUNNING = 4;

const SUMMARY_TAG = 'summary:';
const TASK_TAG = 'task=';

/** A `name=value` line's tag; the FIRST occurrence of a name wins (a value printed twice is never re-read). */
function tagInto(line: string, tags: Map<string, string>): void {
  const at = line.indexOf('=');
  const name = line.slice(0, at);
  if (at > 0 && !tags.has(name)) {
    tags.set(name, line.slice(at + 1).trim());
  }
}

interface Building {
  readonly name: string;
  readonly tags: Map<string, string>;
  readonly summary: string[];
}

interface Parsed {
  readonly folderTags: Map<string, string>;
  readonly sections: Map<string, TaskSection>;
}

/** One line into the task section being built: a summary line, or a tag. */
function addTo(building: Building, line: string): void {
  if (line.startsWith(SUMMARY_TAG)) {
    building.summary.push(line.slice(SUMMARY_TAG.length));
  } else {
    tagInto(line, building.tags);
  }
}

/**
 * The answer's lines, fed one at a time: before the first `task=` line they are the folder's tags; after it each belongs to the
 * section that line opened. A repeated task name keeps its FIRST section — the later one is read into a sink nobody keeps.
 */
class Sections implements Parsed {
  readonly folderTags = new Map<string, string>();
  readonly sections = new Map<string, TaskSection>();
  private sink: Building | undefined;
  private current: Building | undefined;

  feed(line: string): void {
    if (line.startsWith(TASK_TAG)) {
      this.open(line.slice(TASK_TAG.length).trim());
    } else if (this.sink === undefined) {
      tagInto(line, this.folderTags);
    } else {
      addTo(this.sink, line);
    }
  }

  /** The section being built is complete: it is kept. */
  close(): void {
    if (this.current !== undefined) {
      this.sections.set(this.current.name, this.current);
    }
    this.current = undefined;
  }

  private open(name: string): void {
    this.close();
    this.sink = { name, tags: new Map(), summary: [] };
    this.current = this.sections.has(name) ? undefined : this.sink;
  }
}

/** The answer's lines split into the folder's tags and one section per task name (a repeated name keeps the first). */
function sectionsOf(stdout: string): Parsed {
  const parts = new Sections();
  stdout.split(/\r?\n/).filter((l) => l.trim() !== '').forEach((line) => { parts.feed(line); });
  parts.close();

  return parts;
}

function channelOf(value: string | undefined): Channel {
  return value === 'enabled' || value === 'disabled' ? value : 'unknown';
}

/** The HRESULT a failed read printed, or `?` when it printed none. */
function hresultOf(tags: ReadonlyMap<string, string>): string {
  return tags.get('hresult') || '?';
}

function folderOf(tags: ReadonlyMap<string, string>): FolderState | undefined {
  const kind = tags.get('folder');
  if (kind === 'unreadable') {
    return { kind, hresult: hresultOf(tags) };
  }

  return kind === 'present' || kind === 'absent' ? { kind } : undefined;
}

const NO_ANSWER: SchedulerRead = { kind: 'unknown', cause: 'failed', reason: 'the Task Scheduler query printed no answer' };

/** The query's stdout as one read; anything outside the tagged shape is `unknown` with the reason, never a guess. */
export function parseSchedulerAnswer(stdout: string): SchedulerRead {
  const { folderTags, sections } = sectionsOf(stdout);
  const folder = folderOf(folderTags);

  return folder === undefined
    ? NO_ANSWER
    : { kind: 'read', user: folderTags.get('user') ?? '', channel: channelOf(folderTags.get('channel')), stampLeft: folderTags.get('stamp') === 'present', folder, sections };
}

/** The query's process result as one read: its answer, or why there is none (a timeout says so, with the ceiling it hit). */
export function schedulerReadOf(result: ProcessResult): SchedulerRead {
  if (result.kind === 'exited') {
    const code = signed32(result.code);

    return code === 0 ? parseSchedulerAnswer(result.stdout.toString('utf8')) : { kind: 'unknown', cause: 'failed', reason: `the Task Scheduler query exited ${code}` };
  }
  const reason = notAnsweredWords(result, 'the Task Scheduler query', 'wslCare.timeouts.windowsTimeGuardQuerySeconds');

  return result.kind === 'timedOut' ? timedOutRead(result, reason) : { kind: 'unknown', cause: 'failed', reason };
}

/** A timeout, with the ceiling it hit and — when the runner was asked for a tree kill — whether it VERIFIED the tree ended. */
function timedOutRead(result: Extract<ProcessResult, { kind: 'timedOut' }>, reason: string): SchedulerRead {
  const { timeoutMs, treeEnded } = result;

  return { kind: 'unknown', cause: 'timedOut', timeoutMs, reason, ...(treeEnded === undefined ? {} : { treeEnded }) };
}

// ---- one task out of the read ----

const INTEGER = /^-?\d+$/;

function unreadable(reason: string): TaskRead {
  return { kind: 'unreadable', reason };
}

/** A value of the task's own answer, shown in a sentence: bounded, so a hostile one cannot run on. */
function shown(value: string): string {
  return JSON.stringify(value.length > 40 ? `${value.slice(0, 40)}…` : value);
}

function enabledOf(tags: ReadonlyMap<string, string>): boolean | string {
  const value = tags.get('enabled') ?? '';

  return value === 'True' || value === 'False' ? value === 'True' : `its enabled field is ${shown(value)}, not True or False`;
}

function stateOf(tags: ReadonlyMap<string, string>): number | string {
  const value = tags.get('state') ?? '';

  return INTEGER.test(value) ? Number(value) : `its state field is ${shown(value)}, not a number`;
}

const NEVER: LastRun = { kind: 'never' };

/** A run that did not happen: no instant, "never" for it, or no result — Task Scheduler's own zero values. */
function neverRan(utc: string, result: string): boolean {
  return utc === '' || utc === 'never' || result === '';
}

/** A run that happened: its instant and result read, or why one of them cannot be (`0x41303`, "not run yet", is no run). */
function ranOf(utc: string, result: string): LastRun | string {
  if (!Number.isFinite(Date.parse(utc))) {
    return `its lastRunUtc field is ${shown(utc)}, not a time`;
  }
  if (!INTEGER.test(result)) {
    return `its lastResult field is ${shown(result)}, not a number`;
  }
  const code = Number(result) >>> 0;

  return code === SCHEDULER_RESULT.notRunYet ? NEVER : { kind: 'ran', utc, result: code };
}

/** The last run: `never` for an empty or "never" instant, an empty result or Task Scheduler's "not run yet"; else the run, or why not. */
function lastRunOf(tags: ReadonlyMap<string, string>): LastRun | string {
  const utc = tags.get('lastRunUtc') ?? '';
  const result = tags.get('lastResult') ?? '';

  return neverRan(utc, result) ? NEVER : ranOf(utc, result);
}

/** Running: Task Scheduler's own state says so, or the last result is its "running" code. */
function runningOf(state: number, lastRun: LastRun): boolean {
  return state === STATE_RUNNING || (lastRun.kind === 'ran' && lastRun.result === SCHEDULER_RESULT.running);
}

function presentOf(section: TaskSection): TaskRead {
  const enabled = enabledOf(section.tags);
  const state = stateOf(section.tags);
  const lastRun = lastRunOf(section.tags);
  const problem = [enabled, state, lastRun].find((part): part is string => typeof part === 'string');
  if (problem !== undefined) {
    return unreadable(problem);
  }
  if (section.summary.length === 0) {
    return unreadable('its definition printed no summary lines');
  }

  return { kind: 'present', enabled: enabled as boolean, running: runningOf(state as number, lastRun as LastRun), lastRun: lastRun as LastRun, summary: section.summary };
}

function sectionOf(section: TaskSection): TaskRead {
  if (section.tags.get('read') !== 'unreadable') {
    return presentOf(section);
  }
  const hresult = hresultOf(section.tags);

  return { kind: 'unreadable', reason: `Task Scheduler did not let this account read its definition (${hresult})`, hresult };
}

/** One task read out of the folder's read, by name: absent when the folder or the task is not there, unreadable when it cannot be told. */
export function taskOf(read: Extract<SchedulerRead, { kind: 'read' }>, name: string): TaskRead {
  if (read.folder.kind === 'unreadable') {
    return { kind: 'unreadable', reason: `Task Scheduler does not let this account read it (${read.folder.hresult}) — installed or not cannot be told`, hresult: read.folder.hresult };
  }
  const section = read.folder.kind === 'present' ? read.sections.get(name) : undefined;

  return section === undefined ? { kind: 'absent' } : sectionOf(section);
}
