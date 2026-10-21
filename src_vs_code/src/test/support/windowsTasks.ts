import * as fs from 'node:fs';
import * as path from 'node:path';

import { WindowsCareClient } from '../../client/WindowsCareClient';
import type { TaskName } from '../../client/verbs';
import type { RenderAnswer } from '../../client/windowsTasks';
import type { ProcessRequest, ProcessResult } from '../../process/runner';
import type { FakeTasks } from '../fake/fakeWsl';
import { bodyAt, bodyOf, stringsAt, type Body } from './body';
import { GOLDEN_ROOT, SOURCE_ROOT } from './paths';

/**
 * What the WR-E1 tests share about the Windows runner's `tasks` verbs (PLAN_windows_runner.md §3.8): the staged exe's place on a
 * made-up machine, the daemon's render golden, the stage fixtures, and the recording runner the client tests start nothing with.
 * Fixtures name the profile folder `C:\Users\user` (the repository's privacy rule).
 */

/** A staged copy's folder name — the same digest the daemon's render golden uses. */
export const STAGED_DIGEST = '0123456789abcdef'.repeat(4);

/** The profile's LocalAppData on the made-up machine — what the client is given; it derives the staging folder itself. */
export const LOCAL_APP_DATA = 'C:\\Users\\user\\AppData\\Local';

/** The daemon's staging folder on the made-up machine. */
export const STAGING_ROOT = `${LOCAL_APP_DATA}\\wsl-care\\bin`;

/** The staged exe the client names for {@link STAGED_DIGEST}. */
export const STAGED_EXE = `${STAGING_ROOT}\\${STAGED_DIGEST}\\wsl-care.exe`;

/** The extension folder and the bundled exe in it. */
export const EXTENSION = { scheme: 'file', fsPath: 'C:\\Users\\user\\.vscode\\extensions\\pub.ai-os-care-0.5.0-win32-x64' };
export const BUNDLED_EXE_FILE = `${EXTENSION.fsPath}\\bin\\wsl-care.exe`;

/** The stage fixtures (hand-written from the daemon's `StageReport` / `StageRemoveReport` records). */
export const TASKS_FIXTURES = path.join(SOURCE_ROOT, 'test', 'fixtures', 'windows-tasks');

/** The daemon's golden of `tasks render` — only READ here. */
export const RENDER_GOLDEN = path.join(GOLDEN_ROOT, 'windows-tasks-render.json');

/** The fake's `tasks` scenario with its defaults: the two staged-copy render answers, `stage-staged`, `stage-remove-removed`. */
export const TASKS_SCENARIO: FakeTasks = { folder: TASKS_FIXTURES, renderGolden: RENDER_GOLDEN };

/** One golden entry of `tasks render`, by its `name`: the answer the daemon printed. */
export function renderAnswer(name: string): Body {
  const golden = bodyOf(JSON.parse(fs.readFileSync(RENDER_GOLDEN, 'utf8')), RENDER_GOLDEN);
  const entry = (golden.answers as unknown[]).map((e) => bodyOf(e, 'a golden entry')).find((e) => e.name === name);
  if (entry === undefined) {
    throw new Error(`no golden render answer named ${name}`);
  }

  return bodyOf(entry.answer, name);
}

/** One stage fixture, parsed. */
export function stageFixture(name: string): Body {
  return bodyOf(JSON.parse(fs.readFileSync(path.join(TASKS_FIXTURES, name), 'utf8')), name);
}

/** A process that exited with `code`, printing `stdout` (a body is printed as JSON). */
export function exited(code: number, stdout: string | Body = '', stderr = ''): ProcessResult {
  return { kind: 'exited', code, stdout: Buffer.from(typeof stdout === 'string' ? stdout : JSON.stringify(stdout)), stderr: Buffer.from(stderr) };
}

/** A runner that starts nothing and answers every request with `result`, remembering the requests. */
export function recording(result: ProcessResult): { runner: (r: ProcessRequest) => Promise<ProcessResult>; requests: ProcessRequest[] } {
  const requests: ProcessRequest[] = [];

  return { requests, runner: (request) => { requests.push(request); return Promise.resolve(result); } };
}

export interface ClientOptions {
  readonly platform?: string;
  /** The profile's LocalAppData the client derives its staging folder from; `null` gives it none. */
  readonly localAppData?: string | null;
  /** The SHA-256 the client computes for the bundled exe (default: the golden's digest). */
  readonly digest?: string;
}

export function windowsClient(runner: (r: ProcessRequest) => Promise<ProcessResult>, options: ClientOptions = {}): WindowsCareClient {
  const localAppData = options.localAppData === undefined ? LOCAL_APP_DATA : options.localAppData;
  const digest = options.digest ?? STAGED_DIGEST;

  return new WindowsCareClient({ runner, platform: options.platform ?? 'win32', extension: EXTENSION, timeoutMs: () => 20_000, statusTimeoutMs: () => 20_000, digestOf: () => Promise.resolve(digest), ...(localAppData === null ? {} : { localAppData }) });
}

function textAt(body: Body, key: string): string {
  const value = body[key];
  if (typeof value !== 'string') {
    throw new Error(`${key} is not text: ${JSON.stringify(value)}`);
  }

  return value;
}

function flagAt(body: Body, key: string): boolean {
  const value = body[key];
  if (typeof value !== 'boolean') {
    throw new Error(`${key} is not true or false: ${JSON.stringify(value)}`);
  }

  return value;
}

function wholeAt(body: Body, key: string): number {
  const value = body[key];
  if (typeof value !== 'number' || !Number.isInteger(value)) {
    throw new Error(`${key} is not a whole number: ${JSON.stringify(value)}`);
  }

  return value;
}

/**
 * One golden `tasks render` answer as the TYPED `RenderAnswer` the client hands on — every field read and CHECKED from the golden's body,
 * none cast (TypeScript doctrine §3): a golden that lost a field fails here, naming it. `task` is the task the answer is for.
 */
export function renderAnswerOf(name: string, task: TaskName): RenderAnswer {
  const body = renderAnswer(name);
  const dry = bodyAt(body, 'dry');
  const numbers = body.numbers;
  if (!Array.isArray(numbers)) {
    throw new Error('numbers is not a list');
  }

  return {
    task,
    taskPath: textAt(body, 'taskPath'),
    registrable: flagAt(body, 'registrable'),
    reason: textAt(body, 'reason'),
    userSid: textAt(body, 'userSid'),
    command: textAt(body, 'command'),
    digest: textAt(body, 'digest'),
    version: textAt(body, 'version'),
    numbers: numbers.map((entry) => ({ key: textAt(bodyOf(entry, 'a number'), 'key'), value: wholeAt(bodyOf(entry, 'a number'), 'value') })),
    executionTimeLimitMinutes: wholeAt(body, 'executionTimeLimitMinutes'),
    summary: stringsAt(body, 'summary'),
    xml: textAt(body, 'xml'),
    dry: { dryRun: flagAt(dry, 'dryRun'), setting: flagAt(dry, 'setting'), week: textAt(dry, 'week'), reason: textAt(dry, 'reason') },
  };
}

/** What Task Scheduler reads back of a REGISTERED task: the render's summary, the delay as Task Scheduler spells it (PT60S becomes PT1M). */
export function registeredSummary(task: TaskName): readonly string[] {
  return renderAnswerOf(task + ' from the staged copy', task).summary.map((line) => line.replaceAll('delay=PT60S', 'delay=PT1M'));
}
