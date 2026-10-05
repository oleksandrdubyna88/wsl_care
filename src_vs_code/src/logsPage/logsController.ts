import type { JsonObject, ReadOutcome, VerbOutcome } from '../client/outcome';
import type { RunRead } from '../client/verbs';
import type { DurableStore } from '../cleanup/journal';
import { failureText } from '../failureText';
import type { ViewLevel } from '../panel/view';
import { isJson, list } from '../panel/read';
import { RUN_ID_SHAPE } from '../shared/shapes';
import { parseLogsMessage, type LogsMessage } from './logsMessages';
import type { LogsState, LogsView, ReadState } from './logsView';
import { buildLogsView } from './logsViewModel';
import { periodOf, readsFor, retainedDays, windowOf, type Period, type PeriodWindow } from './period';

/**
 * The Logs page's host side (plan §7.4, §15j M7 / M8) — vscode-free, so every decision is tested without a window
 * (`logsController.test.ts`); `logsPanel.ts` wires it to a real `WebviewPanel`.
 *
 * - **The period is the host's.** It is kept in `globalState` under `LOGS_PERIOD_KEY`, WRITTEN before it is read
 *   (`common.durable-status`: the selection a person made survives a reload, a crash, a second window), read back through
 *   `periodOf` — a tampered or foreign value is no period, and the page opens on Today.
 * - **Every read is built here** from that period (`period.ts`), through the client's run reads (`WslCareClient.read`:
 *   unprivileged, values checked again before a spawn). A page message outside the closed set (`logsMessages.ts`) is
 *   dropped before anything happens; a well-formed one the host cannot honour (a range ending first, *This run* with no
 *   cleanup recorded, an index past the list, a line without a daemon run id, a daemon that does not advertise the
 *   capability) is told on the page and starts no process either.
 * - **Answers to a period no longer selected are dropped** (a generation count): the newest selection wins. A new
 *   selection begins SYNCHRONOUSLY — its generation, its cleared slots and its window before the persist await — so no
 *   render shows the old period's figures under the new label (review C6).
 * - **The window shown is the one the answers were read for** (review C2): built when a read starts, never per render — so
 *   past local midnight Today's answers are not relabelled as the next day.
 * - **An answer carrying `problem`** (the daemon could not read its history, exit 4) is its own state, `unreadable`: its
 *   zero figures and its "unknown" are never shown as facts (review C1).
 * - **`status` not read, failed, or answered without a cleanup** are three different reasons for a greyed *This run*,
 *   and a new `status` re-posts the view (`statusChanged`, review C3).
 */

/** The `globalState` key of the selected period. Versioned: a later shape is a new key, never a reinterpretation. */
export const LOGS_PERIOD_KEY = 'wslCare.logs.period.v1';

export interface LogsControllerOptions {
  readonly durable: DurableStore;
  readonly read: (request: RunRead) => Promise<ReadOutcome>;
  /** The newest `status` outcome (the store's): *This run*'s id and the daemon's capabilities. */
  readonly status: () => VerbOutcome | undefined;
  readonly post: (view: LogsView) => void;
  readonly wallNow: () => number;
}

const TODAY: Period = { kind: 'today' };
const IDLE: ReadState = { kind: 'idle' };
const READING: ReadState = { kind: 'reading' };

/** What each kind of read needs the daemon to advertise (`status.capabilities`, E6.S0). */
const NEEDS: { readonly [K in 'days' | 'run']: string } = { days: 'logs.instantRange', run: 'runs.show' };

function statusBody(status: VerbOutcome | undefined): JsonObject | undefined {
  return status?.kind === 'answered' && status.answer.verb === 'status' ? status.answer.body : undefined;
}

function runIdOf(value: unknown): string | undefined {
  return typeof value === 'string' && RUN_ID_SHAPE.test(value) ? value : undefined;
}

/** The last cleanup's run id, when `status` answered one of the daemon's spelling. */
function lastCleanupRun(status: VerbOutcome | undefined): string | undefined {
  const last = statusBody(status)?.lastCleanup;

  return isJson(last) ? runIdOf(last.runId) : undefined;
}

/** Why the daemon may not be asked for `need` — it answered `status` without that capability — or ''. */
function missingCapability(status: VerbOutcome | undefined, need: keyof typeof NEEDS): string {
  const listed = advertised(status);

  return listed === undefined || listed.includes(NEEDS[need]) ? '' : `The daemon does not advertise ${NEEDS[need]} — update the daemon to read this period.`;
}

/** `status.capabilities`, or undefined when `status` has not answered one (then the daemon is asked, and its refusal shown). */
function advertised(status: VerbOutcome | undefined): readonly unknown[] | undefined {
  const capabilities = statusBody(status)?.capabilities;

  return Array.isArray(capabilities) ? capabilities : undefined;
}

/**
 * Why there is no last cleanup to open (review C3): `status` not read yet, `status` failed (its label), or answered
 * without a `lastCleanup` — three different facts, never all "no cleanup is recorded".
 */
function noRunReason(status: VerbOutcome | undefined): string {
  if (status === undefined) {
    return 'status has not been read yet';
  }

  return status.kind === 'answered' ? 'no cleanup is recorded yet' : `status did not answer: ${failureText(status).label}`;
}

/** Why *This run* is greyed: no run to open, or a daemon without `runs show` — '' while it shows a run already. */
function thisRunReason(period: Period, status: VerbOutcome | undefined): string {
  if (period.kind === 'thisRun') {
    return '';
  }

  return lastCleanupRun(status) === undefined ? noRunReason(status) : missingCapability(status, 'run');
}

const NO_RUN_ID = 'That line carries no run id the daemon writes, so its objects cannot be read.';

/** *This run* of the last cleanup `status` names — what *Logs* beside *Last cleanup* opens — or none when none is recorded. */
export function lastCleanupPeriod(status: VerbOutcome | undefined): Period | undefined {
  const runId = lastCleanupRun(status);

  return runId === undefined ? undefined : { kind: 'thisRun', runId };
}

/**
 * An answer, read: one carrying `problem` (the daemon could not read the history — exit 4, review C1) is its OWN state, so
 * its zero figures and its "unknown" are never shown as facts.
 */
function answerOf(body: JsonObject): ReadState {
  return typeof body.problem === 'string' && body.problem !== '' ? { kind: 'unreadable', problem: body.problem } : { kind: 'answered', body };
}

function stateOf(outcome: ReadOutcome): ReadState {
  return outcome.kind === 'read' ? answerOf(outcome.body) : { kind: 'failed', sentence: failureText(outcome).sentence };
}

export class LogsController {
  private current: Period;
  private logs: ReadState = IDLE;
  private runs: ReadState = IDLE;
  private show: ReadState = IDLE;
  private readonly details = new Map<number, ReadState>();
  /** The run ids of the run list the host read, by index — what `expand` names. */
  private runIds: readonly (string | undefined)[] = [];
  private notice = '';
  private noticeLevel: ViewLevel = 'none';
  private generation = 0;
  private rendered: number | undefined;
  /** The window the slots' reads were BUILT for (review C2) — never recomputed for a render, only when a read starts. */
  private window: PeriodWindow | undefined;

  constructor(private readonly options: LogsControllerOptions) {
    this.current = periodOf(options.durable.get(LOGS_PERIOD_KEY)) ?? TODAY;
  }

  period(): Period {
    return this.current;
  }

  /** The store's `status` changed (review C3): *This run* and the capability gate follow it — the view is re-posted. */
  statusChanged(): void {
    void this.post();
  }

  lastRendered(): number | undefined {
    return this.rendered;
  }

  view(): LogsView {
    return buildLogsView(this.state());
  }

  /** Opens on `period` (the command, or *Logs* beside *Last cleanup*) — or on the persisted one when none is given. */
  open(period?: Period): Promise<void> {
    return period === undefined ? this.refresh() : this.select(period);
  }

  /** A message from the page: anything outside the closed set is dropped here, before anything starts. */
  receive(raw: unknown): Promise<void> {
    const message = parseLogsMessage(raw);

    return message === undefined ? Promise.resolve() : this.handle(message);
  }

  private handle(message: LogsMessage): Promise<void> {
    const handlers: { readonly [K in LogsMessage['type']]: (m: Extract<LogsMessage, { type: K }>) => Promise<void> } = {
      ready: () => (this.hasAnswer() ? this.post() : this.refresh()),
      refresh: () => this.refresh(),
      today: () => this.select({ kind: 'today' }),
      yesterday: () => this.select({ kind: 'yesterday' }),
      thisRun: () => this.thisRun(),
      day: (m) => this.select({ kind: 'day', day: m.day }),
      range: (m) => this.range(m.from, m.to),
      expand: (m) => this.expand(m.index),
      collapse: (m) => { this.details.delete(m.index); return this.post(); },
      rendered: (m) => { this.rendered = m.blocks; return Promise.resolve(); },
    };

    return (handlers[message.type] as (m: LogsMessage) => Promise<void>)(message);
  }

  private thisRun(): Promise<void> {
    const status = this.options.status();
    const runId = lastCleanupRun(status);

    return runId === undefined ? this.tell(`There is no run to show: ${noRunReason(status)}.`) : this.select({ kind: 'thisRun', runId });
  }

  /** Whether the slots hold an answer (or a read in flight) for the current selection — review C5: a returning tab is shown it. */
  private hasAnswer(): boolean {
    return this.current.kind === 'thisRun' ? this.show.kind !== 'idle' : this.logs.kind !== 'idle' || this.runs.kind !== 'idle';
  }

  /**
   * The selection changes NOW, before any await (review C6): a new generation, the old answers dropped, the new window —
   * so no render, of any cause, shows the old period's figures under the new period's label.
   */
  private begin(period: Period): number {
    this.current = period;
    this.notice = '';
    this.logs = IDLE;
    this.runs = IDLE;
    this.show = IDLE;
    this.details.clear();
    this.runIds = [];
    this.window = period.kind === 'thisRun' ? undefined : windowOf(period, this.options.wallNow());

    return ++this.generation;
  }

  private range(from: string, to: string): Promise<void> {
    const period = periodOf({ kind: 'range', from, to });

    return period === undefined ? this.tell(`A range must end on or after its first day (${from} to ${to}).`) : this.select(period);
  }

  /** Why the current daemon may not be asked for `period`, or ''. */
  private refusalFor(period: Period): string {
    return missingCapability(this.options.status(), period.kind === 'thisRun' ? 'run' : 'days');
  }

  /**
   * Makes `period` the selection WITHOUT reading it — for a page about to load, whose `ready` reads it (so a new page is
   * read once). Refused with its reason when the daemon may not be asked for it; true when it was persisted and is still
   * the selection (a later one may have begun while it was written).
   */
  async choose(period: Period): Promise<boolean> {
    const refusal = this.refusalFor(period);
    if (refusal !== '') {
      await this.tell(refusal);
      return false;
    }
    const generation = this.begin(period);
    await this.options.durable.update(LOGS_PERIOD_KEY, period);

    return generation === this.generation;
  }

  /** A new selection: refused with its reason, or PERSISTED first and then read. */
  private async select(period: Period): Promise<void> {
    if (await this.choose(period)) {
      await this.readPeriod(period);
    }
  }

  private refresh(): Promise<void> {
    const refusal = this.refusalFor(this.current);

    return refusal === '' ? this.readPeriod(this.current) : this.tell(refusal);
  }

  /** Reads `period` — the one THIS call selected (a later selection may have replaced `current` while it was persisted). */
  private async readPeriod(period: Period): Promise<void> {
    const generation = ++this.generation;
    this.details.clear();
    this.window = period.kind === 'thisRun' ? undefined : windowOf(period, this.options.wallNow());
    const reads = readsFor(period, this.window);
    this.mark(reads, READING);
    await this.post();
    const outcomes = await Promise.all(reads.map((request) => this.options.read(request)));
    if (generation === this.generation) {
      reads.forEach((request, i) => this.mark([request], stateOf(outcomes[i] as ReadOutcome)));
      await this.post();
    }
  }

  /** Sets the state of each read's slot; a runs answer also gives the run ids the list's indexes name. */
  private mark(reads: readonly RunRead[], state: ReadState): void {
    for (const request of reads) {
      const slots: { readonly [K in RunRead['read']]: () => void } = {
        logs: () => { this.logs = state; },
        runs: () => { this.runs = state; this.runIds = state.kind === 'answered' ? list(state.body, 'runs').map((run) => runIdOf(run.runId)) : []; },
        runsShow: () => { this.show = state; },
      };
      slots[request.read]();
    }
  }

  /** The run id the line at `index` names — or why it is not read ('' when the request is simply ignored). */
  private expandTarget(index: number): { readonly runId: string } | { readonly reason: string } {
    if (this.current.kind === 'thisRun') {
      return { reason: '' };
    }
    const capability = missingCapability(this.options.status(), 'run');
    if (capability !== '') {
      return { reason: capability };
    }
    const runId = this.runIds[index];

    return runId === undefined ? { reason: NO_RUN_ID } : { runId };
  }

  private async expand(index: number): Promise<void> {
    const target = this.expandTarget(index);
    if ('reason' in target) {
      return target.reason === '' ? undefined : this.tell(target.reason);
    }

    return this.readDetail(index, target.runId);
  }

  private async readDetail(index: number, runId: string): Promise<void> {
    const generation = this.generation;
    this.details.set(index, READING);
    await this.post();
    const outcome = await this.options.read({ read: 'runsShow', runId });
    if (generation === this.generation && this.details.has(index)) {
      this.details.set(index, stateOf(outcome));
      await this.post();
    }
  }

  private tell(sentence: string): Promise<void> {
    this.notice = sentence;
    this.noticeLevel = 'warn';

    return this.post();
  }

  private post(): Promise<void> {
    this.options.post(this.view());

    return Promise.resolve();
  }

  /** The window the answers were read for — computed only before the first read of a persisted selection. */
  private windowOfCurrent(now: number): PeriodWindow | undefined {
    return this.current.kind === 'thisRun' ? undefined : (this.window ?? windowOf(this.current, now));
  }

  private state(): LogsState {
    const now = this.options.wallNow();

    return {
      period: this.current,
      window: this.windowOfCurrent(now),
      retained: retainedDays(now),
      logs: this.logs,
      runs: this.runs,
      show: this.show,
      details: new Map(this.details),
      thisRunReason: thisRunReason(this.current, this.options.status()),
      notice: this.notice,
      noticeLevel: this.notice === '' ? 'none' : this.noticeLevel,
    };
  }
}
