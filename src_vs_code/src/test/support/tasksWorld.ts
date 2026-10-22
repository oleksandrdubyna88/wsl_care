import type { ProcessRequest, ProcessResult } from '../../process/runner';
import { schedulerReadOf, type SchedulerRead } from '../../scheduler/schedulerAnswer';
import type { WindowsCareClient } from '../../client/WindowsCareClient';
import { TASKS_PREVIEW_TAIL, TASKS_REMOVE_TAIL, TASKS_STAGE_TAIL, TASK_NAMES, type TaskName } from '../../client/verbs';
import { recordingTasksUi, newTasksRecorder, type TasksRecorder } from '../../windowsTasks/tasksRecorder';
import { readPending } from '../../windowsTasks/tasksPending';
import type { TasksFlowDeps } from '../../windowsTasks/tasksFlow';
import { fixedEngine, PS51_READ } from './engines';
import { MapStore } from './memento';
import { schedulerStdout, type SchedulerFixture } from './schedulerAnswers';
import { exited as textExited } from './recordingRunner';
import { EXTENSION, exited, renderAnswer, stageFixture, windowsClient, type ClientOptions } from './windowsTasks';
import type { EngineRead } from '../../powershell/engineHost';

/**
 * One made-up machine for the Windows tasks' flow tests (WR-E3): the REAL Windows client over a runner that starts NOTHING — it answers the
 * daemon's `tasks` verbs from the golden render answers and the stage fixtures, by exact argv — and the flow's surfaces recorded in ONE
 * event list, so a test can pin the ORDER of every step. No PowerShell is started: the install / remove request goes to the recorder.
 */

/** Every request any collaborator made, in order, with who made it. */
export interface Call {
  readonly who: 'daemon' | 'powershell';
  readonly request: ProcessRequest;
}

export interface WorldOptions {
  /** The golden render answer of each task ('watch from the staged copy', …). */
  readonly renders?: Partial<Record<TaskName, string>>;
  /** The stage's fixture and exit code (default: `stage-staged.json`, 0). */
  readonly stage?: readonly [string, number][];
  readonly stageRemove?: readonly [string, number][];
  /** The preview's fixture, exit code and (optional) stderr, in order, the last repeating (default: `preview-cleanup.json`, 0). A fixture name of `''` prints nothing. */
  readonly preview?: readonly (readonly [string, number] | readonly [string, number, string])[];
  /** WR-T3: the fixtures the run reads answer — `runs` (the window's history) and `runs show` (one run's detail). */
  readonly runs?: string;
  readonly runsShow?: string;
  readonly read?: SchedulerFixture | SchedulerRead;
  readonly client?: ClientOptions;
  readonly engine?: readonly [EngineRead, EngineRead];
  readonly answer?: boolean;
  readonly run?: (request: ProcessRequest) => Promise<ProcessResult>;
  readonly now?: number;
  readonly store?: MapStore;
  /** Edit the render's JSON body before the daemon "prints" it (for the doctored-XML cases). */
  readonly editRender?: (body: Record<string, unknown>) => Record<string, unknown>;
  readonly extensionFolder?: string;
  readonly busyRetryMs?: number;
  /** The cleanup gate the flow is given: '' (open, the default) or the closed sentence. */
  readonly cleanupGate?: string;
  /** A daemon call that ENDS in a scripted way (a timeout, say) instead of answering: by its argv joined with spaces. */
  readonly ending?: (argv: string) => ProcessResult | undefined;
}

export interface World {
  readonly deps: TasksFlowDeps;
  readonly events: string[];
  readonly calls: Call[];
  readonly recorder: TasksRecorder;
  readonly store: MapStore;
  readonly sleeps: number[];
  /** The lines the flow wrote to the extension's log. */
  readonly logs: string[];
  readonly client: WindowsCareClient;
  /** The requests of the ONE unelevated PowerShell, as the recorder saw them. */
  ps(): ProcessRequest[];
  daemon(): string[];
}

function isRead(read: SchedulerFixture | SchedulerRead): read is SchedulerRead {
  return 'kind' in read;
}

function readOf(read: SchedulerFixture | SchedulerRead | undefined): SchedulerRead {
  if (read !== undefined && isRead(read)) {
    return read;
  }

  return schedulerReadOf(textExited(0, schedulerStdout(read ?? {})));
}

/** The answers of a script in order, the last one repeating. */
function sequence<T>(items: readonly T[]): () => T {
  let at = 0;

  return () => items[Math.min(at++, items.length - 1)] as T;
}

export function tasksWorld(options: WorldOptions = {}): World {
  const events: string[] = [];
  const calls: Call[] = [];
  const recorder = newTasksRecorder();
  recorder.answer = options.answer ?? true;
  const store = options.store ?? new MapStore();
  const now = options.now ?? 1_000_000;
  const sleeps: number[] = [];
  const logs: string[] = [];
  const nextStage = sequence<readonly [string, number]>(options.stage ?? [['stage-staged.json', 0]]);
  const nextRemove = sequence<readonly [string, number]>(options.stageRemove ?? [['stage-remove-removed.json', 0]]);
  const nextPreview = sequence<readonly [string, number] | readonly [string, number, string]>(options.preview ?? [['preview-cleanup.json', 0]]);
  const renderOf = (task: TaskName): string => options.renders?.[task] ?? `${task} from the staged copy`;
  const daemon = (request: ProcessRequest): ProcessResult => {
    const argv = request.args.join(' ');
    calls.push({ who: 'daemon', request });
    const scripted = options.ending?.(argv);
    if (scripted !== undefined) {
      events.push(argv === TASKS_REMOVE_TAIL.join(' ') ? 'stageRemove' : 'daemon');
      return scripted;
    }
    if (argv === TASKS_STAGE_TAIL.join(' ')) {
      events.push('stage');
      const [name, code] = nextStage();

      return exited(code, stageFixture(name));
    }
    if (argv === TASKS_REMOVE_TAIL.join(' ')) {
      events.push('stageRemove');
      const [name, code] = nextRemove();

      return exited(code, stageFixture(name));
    }
    if (request.args[0] === 'runs') {
      const show = request.args[1] === 'show';
      events.push(show ? 'runsShow' : 'runs');

      return exited(0, stageFixture(show ? (options.runsShow ?? 'runs-show-a23-done.json') : (options.runs ?? 'runs-windows-a23.json')));
    }
    if (argv === TASKS_PREVIEW_TAIL.join(' ')) {
      events.push('preview');
      const [name, code, stderr = ''] = nextPreview();

      return exited(code, name === '' ? '' : stageFixture(name), stderr);
    }
    const task = TASK_NAMES.find((name) => name === request.args[2]) ?? 'watch';
    events.push('render');
    const body = renderAnswer(renderOf(task));

    return exited(0, options.editRender === undefined ? body : options.editRender({ ...body }));
  };
  const client = windowsClient((request) => Promise.resolve(daemon(request)), options.client);
  const [before, after] = options.engine ?? [PS51_READ, PS51_READ];
  const pendingNow = (): string => {
    const pending = readPending(store, now);

    return pending === undefined ? 'none' : pending.op === 'remove' ? 'remove' : `${pending.op}:${pending.task}`;
  };
  const ui = recordingTasksUi(recorder);
  const deps: TasksFlowDeps = {
    client,
    read: () => readOf(options.read),
    engine: fixedEngine(before, after, events),
    ui: {
      show: (text, task) => { events.push('show'); return ui.show(text, task); },
      showPreview: (text) => { events.push('showPreview'); return ui.showPreview(text); },
      confirm: (prompt) => { events.push('confirm'); return ui.confirm(prompt); },
      run: async (request, title) => {
        events.push(`run(pending=${pendingNow()})`);
        calls.push({ who: 'powershell', request });
        if (options.run !== undefined) {
          recorder.requests.push(request);
          return options.run(request);
        }

        return ui.run(request, title);
      },
      report: (message, failed) => { events.push('report'); ui.report(message, failed); },
    },
    durable: store,
    nowUtcMs: () => now,
    sleep: (ms) => { sleeps.push(ms); events.push('sleep'); return Promise.resolve(); },
    timeoutMs: () => 60_000,
    stageTimeoutMs: () => 60_000,
    busyRetryMs: () => options.busyRetryMs ?? 3_000,
    afterRun: () => { events.push('query'); },
    extensionFolder: options.extensionFolder ?? EXTENSION.fsPath,
    log: (line) => { logs.push(line); },
    cleanupGate: options.cleanupGate ?? '',
  };

  return {
    deps, events, calls, recorder, store, sleeps, logs, client,
    ps: () => calls.filter((c) => c.who === 'powershell').map((c) => c.request),
    daemon: () => calls.filter((c) => c.who === 'daemon').map((c) => c.request.args.join(' ')),
  };
}

/** The script a request's `-EncodedCommand` carries: what PowerShell decodes, byte for byte. */
export function decoded(request: ProcessRequest | undefined): string {
  return Buffer.from(request?.args[3] ?? '', 'base64').toString('utf16le');
}
