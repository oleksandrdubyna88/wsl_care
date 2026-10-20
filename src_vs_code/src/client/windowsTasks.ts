import type { ProcessResult } from '../process/runner';
import { signed32 } from '../process/runner';
import { STAGED_DIGEST_SHAPE } from '../shared/shapes';
import { safeText } from '../text/safeText';
import { DAEMON_EXIT } from './exitCodes';
import { BUNDLED_DAEMON, checkedBody } from './handshake';
import type { Failure, JsonObject } from './outcome';
import { TASK_NAMES, type RunReadName, type TaskName } from './verbs';

/**
 * What the Windows daemon's `tasks` verbs and run reads answer, as the Windows client hands it on (WR-E1 of
 * PLAN_windows_runner.md §3.8): typed from the EXIT CODE and the JSON on stdout together — never a generic error, never free text,
 * and never a read of a file the daemon wrote earlier (`stage.json`): what a stage did is what THIS call's stdout says. A code and an
 * answer that disagree are a failure, not a guess. The daemon's contract is `src_daemon/src/WslCare.Core/Scheduler/` (`TasksRender`,
 * `StagingReports`) and `contracts/golden/windows-tasks-render.json`; unknown members are ignored (additive changes keep
 * `schemaVersion` 1), an unknown outcome is not.
 *
 * Nothing here names the exe or starts a process: the pure readers take a finished {@link ProcessResult}.
 */

/** Why a call produced no answer to read: it could not be started here, or it ended without one. */
export type NotAnswered =
  | { readonly kind: 'unavailable'; readonly reason: string }
  | { readonly kind: 'failed'; readonly reason: string };

/** One staging folder a sweep or a removal acted on, or kept, and why (`StagingFolder`). */
export interface StagingFolder {
  readonly digest: string;
  readonly path: string;
  readonly why: string;
}

export interface TaskNumber {
  readonly key: string;
  readonly value: number;
}

/** The Windows dry state of a render (`DryRunPeek`): the setting, the week, and one sentence. */
export interface DryState {
  readonly dryRun: boolean;
  readonly setting: boolean;
  readonly week: string;
  readonly reason: string;
}

/** `tasks render <task> --json` (`TasksRenderReport`). */
export interface RenderAnswer {
  readonly task: TaskName;
  readonly taskPath: string;
  readonly registrable: boolean;
  readonly reason: string;
  readonly userSid: string;
  readonly command: string;
  readonly digest: string;
  readonly version: string;
  readonly numbers: readonly TaskNumber[];
  readonly executionTimeLimitMinutes: number;
  readonly summary: readonly string[];
  readonly xml: string;
  readonly dry: DryState;
}

/**
 * What one render ended in. `registrable` and `notRegistrable` are both exit 0 (the daemon answers either way and says which);
 * `wrongSide` is exit 2 — the daemon refused the command, which is the distro's binary (or a daemon that does not know `tasks`) —
 * and `configurationInError` exit 78 (no task is rendered from a configuration the daemon itself refuses).
 */
export type RenderResult =
  | { readonly kind: 'registrable'; readonly answer: RenderAnswer }
  | { readonly kind: 'notRegistrable'; readonly answer: RenderAnswer; readonly reason: string }
  | { readonly kind: 'wrongSide'; readonly reason: string }
  | { readonly kind: 'configurationInError'; readonly reason: string }
  | NotAnswered;

export type StageOutcome = 'staged' | 'reused' | 'refused' | 'busy';

/** `tasks stage --json` (`StageReport`). */
export interface StageReport {
  readonly outcome: StageOutcome;
  readonly reason: string;
  readonly digest: string;
  readonly path: string;
  readonly version: string;
  readonly source: string;
  readonly swept: readonly StagingFolder[];
  readonly kept: readonly StagingFolder[];
  readonly retained: readonly StagingFolder[];
}

/**
 * What one stage ended in: exit 0 `staged` / `reused` (judged against the expected provenance first — see
 * {@link checkProvenance}), exit 1 `refused` (no verified copy), exit 75 `busy` (the run lock is held; try again), exit 2 `wrongSide`.
 */
export type StageResult =
  | { readonly kind: 'staged' | 'reused'; readonly report: StageReport }
  | { readonly kind: 'refused'; readonly reason: string; readonly report: StageReport }
  | { readonly kind: 'busy'; readonly reason: string }
  | { readonly kind: 'provenanceRefused'; readonly reason: string; readonly report: StageReport }
  | { readonly kind: 'wrongSide'; readonly reason: string }
  | NotAnswered;

/** `tasks stage --remove --json` (`StageRemoveReport`; its staging-folder member is not read — only what the person is told). */
export interface RemoveReport {
  readonly outcome: 'removed' | 'busy';
  readonly reason: string;
  readonly rootRemoved: boolean;
  readonly removed: readonly StagingFolder[];
  readonly kept: readonly StagingFolder[];
  readonly untouched: readonly string[];
}

export type RemoveResult =
  | { readonly kind: 'removed'; readonly report: RemoveReport }
  | { readonly kind: 'busy'; readonly reason: string }
  /** The call hit its ceiling (`treeEnded`: whether the runner VERIFIED the tree ended — false when no tree kill was asked): TYPED, so a caller that persists a pending run can keep it (WR-E3, plan round efc512d4 finding 1). */
  | { readonly kind: 'timedOut'; readonly timeoutMs: number; readonly treeEnded: boolean }
  | { readonly kind: 'wrongSide'; readonly reason: string }
  | NotAnswered;

/** A run read of the Windows side: its body, why it was not asked, or why it did not answer. */
export type WindowsReadOutcome =
  | { readonly kind: 'read'; readonly read: RunReadName; readonly body: JsonObject }
  | { readonly kind: 'readRefused'; readonly read: RunReadName; readonly detail: string }
  | (NotAnswered & { readonly read: RunReadName });

/** What the extension knows of the copy it asked the daemon to make: the SHA-256 of its bundled exe, as it computed it. */
export interface Provenance {
  readonly digest: string;
  /** The staged exe the client names for that digest (`WindowsCareClient.exe(staged(digest))`): the stage answer must report it. */
  readonly path: string;
}

export type ProvenanceVerdict = { readonly ok: true } | { readonly ok: false; readonly reason: string };

/** `text` made printable, clipped and quoted — for a sentence that names a value it was handed. */
function quoted(text: string, max: number): string {
  return JSON.stringify(safeText(text, max));
}

/** The longest stretch of a daemon sentence this module hands on (it is shown to a person). */
const REASON_MAX = 1_000;

// ---- the staged digest ----

/**
 * `text` as a staged copy's folder name: 64 LOWER-case hex digits, or a `RangeError` — thrown, because a digest that is anything
 * else is a bug in the caller, and the very next step builds a PATH from it (`../../other.exe` must never get that far). The one
 * reader of the shape (`shared/shapes.ts`) for the client.
 */
export function stagedDigest(text: string): string {
  if (!STAGED_DIGEST_SHAPE.test(text)) {
    throw new RangeError(`a staged copy's digest is 64 lower-case hex digits, not ${quoted(text, 80)}`);
  }

  return text;
}

// ---- provenance (plan §3.7) ----

/**
 * Whether a stage answer is the copy the extension asked for. Refused when the digest the daemon reports is not 64 lower-case hex
 * digits, when it differs from the digest of the bundled exe the extension computed, when it reports another path than the one the
 * extension would start (compared without case, as NTFS does), or when the release `stage.json` names is not `BUNDLED_DAEMON` — a
 * stage must never hand a task a copy of some other build. (Whether the staged BYTES are the staged digest's is the daemon's, at
 * every render: WR-D1.)
 */
export function checkProvenance(report: StageReport, expected: Provenance): ProvenanceVerdict {
  const digest = stagedDigest(expected.digest);
  const problem = [digestProblem(report, digest), pathProblem(report, expected), versionProblem(report)].find((p) => p !== '');

  return problem === undefined ? { ok: true } : refusal(problem);
}

function digestProblem(report: StageReport, expected: string): string {
  if (!STAGED_DIGEST_SHAPE.test(report.digest)) {
    return `the daemon reported the digest ${quoted(report.digest, 80)}, which is not 64 lower-case hex digits`;
  }

  return report.digest === expected ? '' : `the daemon staged a copy with the digest ${report.digest}, but the bundled exe this extension carries has ${expected}`;
}

function pathProblem(report: StageReport, expected: Provenance): string {
  return report.path.toLowerCase() === expected.path.toLowerCase() ? '' : `the daemon staged the copy at ${quoted(report.path, 300)}, but the extension expects it at ${quoted(expected.path, 300)}`;
}

function versionProblem(report: StageReport): string {
  return report.version === BUNDLED_DAEMON ? '' : `the staged copy's stage.json names the release ${quoted(report.version, 80)}, but this extension carries ${BUNDLED_DAEMON}`;
}

function refusal(reason: string): ProvenanceVerdict {
  return { ok: false, reason };
}

// ---- reading one answer's fields ----

/** One object's fields, read in order; the FIRST that is missing or of the wrong kind is remembered and every later read is inert. */
class Fields {
  private failure = '';

  constructor(private readonly body: unknown, private readonly label: string) {}

  /** The first thing wrong, or '' when every field read so far was right. */
  get problem(): string {
    return this.failure;
  }

  string(key: string): string {
    const value = this.member(key);
    return typeof value === 'string' ? value : this.missing(key, 'text', '');
  }

  /** A sentence of the daemon's: sanitised and clipped, because a person reads it. */
  sentence(key: string): string {
    return safeText(this.string(key), REASON_MAX);
  }

  boolean(key: string): boolean {
    const value = this.member(key);
    return typeof value === 'boolean' ? value : this.missing(key, 'true or false', false);
  }

  integer(key: string): number {
    const value = this.member(key);
    return typeof value === 'number' && Number.isInteger(value) ? value : this.missing(key, 'a whole number', 0);
  }

  strings(key: string): string[] {
    const value = this.member(key);
    return Array.isArray(value) && value.every((item): item is string => typeof item === 'string') ? value : this.missing(key, 'a list of text', []);
  }

  /** A nested object, read by `each`; one that is missing is the first problem, and `each` then reads an empty one. */
  object<T>(key: string, each: (fields: Fields) => T): T {
    const value = this.member(key);
    if (!isObject(value)) {
      this.missing(key, 'an object', undefined);
    }
    const child = new Fields(isObject(value) ? value : {}, `${this.label}.${key}`);

    return this.absorb(child, each(child));
  }

  /** A list of objects, each read by `each`; an entry that is not an object is the first problem. */
  list<T>(key: string, each: (fields: Fields) => T): T[] {
    const value = this.member(key);
    if (!Array.isArray(value)) {
      return this.missing(key, 'a list', []);
    }
    const items = value.map((item, i) => new Fields(item, `${this.label}.${key}[${i}]`));
    const bad = items.find((fields) => !isObject(fields.body));
    if (bad !== undefined) {
      return this.missing(key, 'a list of objects', []);
    }

    return items.map((fields) => this.absorb(fields, each(fields)));
  }

  private absorb<T>(child: Fields, value: T): T {
    this.failure = this.failure === '' ? child.problem : this.failure;

    return value;
  }

  /** The dotted place of `key` in the answer, for a sentence. */
  private path(key: string): string {
    return this.label === '' ? key : `${this.label}.${key}`;
  }

  private member(key: string): unknown {
    return isObject(this.body) ? this.body[key] : undefined;
  }

  private missing<T>(key: string, wanted: string, fallback: T): T {
    this.failure = this.failure === '' ? `the answer carries no ${wanted} member ${quoted(this.path(key), 80)}` : this.failure;

    return fallback;
  }
}

function isObject(value: unknown): value is JsonObject {
  return typeof value === 'object' && value !== null && !Array.isArray(value);
}

// ---- reading one process ending ----

const NOT_EXITED: { readonly [K in Exclude<ProcessResult['kind'], 'exited'>]: (r: Extract<ProcessResult, { kind: K }>, setting: string) => string } = {
  timedOut: (r, setting) => `it did not answer within ${Math.round(r.timeoutMs / 1000)} s${setting === '' ? '' : ` (${setting})`}`,
  failedToStart: (r) => `it could not start: ${r.reason}`,
  signalled: (r) => `it was stopped (${r.signal})`,
  tooMuchOutput: () => 'it printed more than the extension reads',
};

/** Why a process that did not exit gave no answer; `setting` names the ceiling that ended it, when the caller has one to name. */
export function endingReason(result: Exclude<ProcessResult, { kind: 'exited' }>, setting = ''): string {
  return (NOT_EXITED[result.kind] as (r: ProcessResult, s: string) => string)(result, setting);
}

function ended(result: Exclude<ProcessResult, { kind: 'exited' }>): NotAnswered {
  return { kind: 'failed', reason: endingReason(result) };
}

/** The first non-blank line of a stream — a daemon's one-sentence stderr. */
export function firstLine(data: Buffer): string {
  return data.toString('utf8').split(/\r?\n/).map((line) => line.trim()).find((line) => line !== '') ?? '';
}

function said(stderr: Buffer, fallback: string): string {
  return safeText(firstLine(stderr) || fallback, REASON_MAX);
}

function otherExit(code: number, stderr: Buffer): NotAnswered {
  return { kind: 'failed', reason: `it exited ${code}: ${safeText(firstLine(stderr), REASON_MAX)}` };
}

/** The body of an answer, or why it cannot be read (not JSON, no integer `schemaVersion`, a newer schema than this build reads). */
function bodyOf(stdout: Buffer): { readonly body: JsonObject } | NotAnswered {
  const checked = checkedBody(stdout.toString('utf8'));

  return 'body' in checked ? { body: checked.body } : { kind: 'failed', reason: unreadable(checked) };
}

function unreadable(failure: Failure): string {
  switch (failure.kind) {
    case 'unparseable':
      return failure.detail;
    case 'needsNewerExtension':
      return `the answer's schemaVersion ${failure.schemaVersion} is newer than this extension reads`;
    default:
      return failure.kind;
  }
}

type Exited = Extract<ProcessResult, { kind: 'exited' }>;

/** One entry per exit code a verb answers to; a process that did not exit, or exited with another code, is a failure. */
function dispatch<T>(result: ProcessResult, table: Readonly<Record<number, (exited: Exited) => T>>): T | NotAnswered {
  if (result.kind !== 'exited') {
    return ended(result);
  }
  const code = signed32(result.code);

  return (table[code] ?? ((exited: Exited) => otherExit(code, exited.stderr)))(result);
}

const wrongSide = (exited: Exited): { readonly kind: 'wrongSide'; readonly reason: string } => ({
  kind: 'wrongSide',
  reason: said(exited.stderr, 'the daemon refused the command: it is not the Windows binary'),
});

const busy = (exited: Exited): { readonly kind: 'busy'; readonly reason: string } | NotAnswered => busyOf(exited.stdout);

// ---- tasks render ----

function folderOf(fields: Fields): StagingFolder {
  return { digest: fields.string('digest'), path: fields.string('path'), why: fields.sentence('why') };
}

function dryOf(fields: Fields): DryState {
  return { dryRun: fields.boolean('dryRun'), setting: fields.boolean('setting'), week: fields.string('week'), reason: fields.sentence('reason') };
}

function renderOf(body: JsonObject, task: TaskName): RenderAnswer | string {
  const fields = new Fields(body, '');
  const named = fields.string('task');
  const answer: RenderAnswer = {
    task,
    taskPath: fields.string('taskPath'),
    registrable: fields.boolean('registrable'),
    reason: fields.sentence('reason'),
    userSid: fields.string('userSid'),
    command: fields.string('command'),
    digest: fields.string('digest'),
    version: fields.string('version'),
    numbers: fields.list('numbers', (n) => ({ key: n.string('key'), value: n.integer('value') })),
    executionTimeLimitMinutes: fields.integer('executionTimeLimitMinutes'),
    summary: fields.strings('summary'),
    xml: fields.string('xml'),
    dry: fields.object('dry', dryOf),
  };

  return fields.problem !== '' ? fields.problem : namedTaskProblem(named, task) || answer;
}

/** '' when the answer is for the task asked, else the sentence saying it is not. */
function namedTaskProblem(named: string, task: TaskName): string {
  return named === task && TASK_NAMES.includes(task) ? '' : `the answer is for the task ${quoted(named, 40)}, not ${quoted(task, 40)}`;
}

/** Why a render the daemon calls registrable is not THIS build's copy that the client ran — or '' when it is. */
function notThisCopy(answer: RenderAnswer, digest: string, file: string): string {
  if (answer.digest !== digest) {
    return `the copy that answered has the digest ${safeText(answer.digest, 80)}, not the ${digest} asked for`;
  }
  if (answer.version !== BUNDLED_DAEMON) {
    return `the copy that answered is the release ${quoted(answer.version, 80)}, not ${BUNDLED_DAEMON}, the build this extension carries`;
  }

  return answer.command.toLowerCase() === file.toLowerCase() ? '' : `the copy that answered is ${quoted(answer.command, 300)}, not the file the extension started, ${quoted(file, 300)}`;
}

function judgedRender(answer: RenderAnswer, digest: string, file: string): RenderResult {
  if (!answer.registrable) {
    return { kind: 'notRegistrable', answer, reason: answer.reason };
  }
  const reason = notThisCopy(answer, digest, file);

  return reason === '' ? { kind: 'registrable', answer } : { kind: 'notRegistrable', answer, reason };
}

function renderedResult(stdout: Buffer, task: TaskName, digest: string, file: string): RenderResult {
  const read = bodyOf(stdout);
  if (!('body' in read)) {
    return read;
  }
  const answer = renderOf(read.body, task);

  return typeof answer === 'string' ? { kind: 'failed', reason: answer } : judgedRender(answer, digest, file);
}

/**
 * A finished `tasks render <task>` on the staged copy `digest`. Exit 0 is an answer — registrable or not, the JSON says — exit 2 the
 * wrong side, exit 78 a configuration in error; any other exit is a failure, and so is an answer that cannot be read. A render that
 * says `registrable` for another digest than the copy asked, for another release than `BUNDLED_DAEMON`, or for another file than
 * `file` (the one the client started; compared without case) is not registrable — an older retained copy answers for itself.
 */
export function renderResultOf(result: ProcessResult, task: TaskName, digest: string, file: string): RenderResult {
  return dispatch<RenderResult>(result, {
    [DAEMON_EXIT.ok]: (exited) => renderedResult(exited.stdout, task, digest, file),
    [DAEMON_EXIT.usage]: wrongSide,
    [DAEMON_EXIT.observeOnly]: (exited) => ({ kind: 'configurationInError', reason: said(exited.stderr, 'the configuration is in error; no task is rendered from it') }),
  });
}

// ---- tasks stage ----

const STAGE_OUTCOMES: readonly StageOutcome[] = ['staged', 'reused', 'refused', 'busy'];

function stageReportOf(body: JsonObject): StageReport | string {
  const fields = new Fields(body, '');
  const outcome = fields.string('outcome');
  const report = {
    reason: fields.sentence('reason'),
    digest: fields.string('digest'),
    path: fields.string('path'),
    version: fields.string('version'),
    source: fields.string('source'),
    swept: fields.list('swept', folderOf),
    kept: fields.list('kept', folderOf),
    retained: fields.list('retained', folderOf),
  };
  const known = STAGE_OUTCOMES.find((o) => o === outcome);
  if (fields.problem !== '') {
    return fields.problem;
  }

  return known === undefined ? `the answer's outcome ${quoted(outcome, 40)} is not one this extension knows` : { outcome: known, ...report };
}

/** The outcomes each exit code may carry: a code and an outcome that are not paired here disagree. */
const STAGE_PAIRS: ReadonlyMap<number, readonly StageOutcome[]> = new Map<number, readonly StageOutcome[]>([
  [DAEMON_EXIT.ok, ['staged', 'reused']],
  [DAEMON_EXIT.runFailed, ['refused']],
]);

function settledStage(code: number, report: StageReport, expected: Provenance): StageResult {
  if (!STAGE_PAIRS.get(code)?.includes(report.outcome)) {
    return { kind: 'failed', reason: `it exited ${code} but the answer says ${quoted(report.outcome, 40)}: the two disagree, so nothing is taken as staged` };
  }

  return report.outcome === 'refused' ? { kind: 'refused', reason: report.reason, report } : provenanceJudged(report, expected);
}

function provenanceJudged(report: StageReport, expected: Provenance): StageResult {
  const verdict = checkProvenance(report, expected);

  return verdict.ok ? { kind: report.outcome as 'staged' | 'reused', report } : { kind: 'provenanceRefused', reason: verdict.reason, report };
}

function stagedResult(code: number, stdout: Buffer, expected: Provenance): StageResult {
  const read = bodyOf(stdout);
  if (!('body' in read)) {
    return read;
  }
  const report = stageReportOf(read.body);

  return typeof report === 'string' ? { kind: 'failed', reason: report } : settledStage(code, report, expected);
}

const BUSY_FALLBACK = 'another run holds the run lock; nothing was done, try again';

/**
 * Exit 75 is busy: the daemon's sentence when it printed a readable busy answer, the plain fact when it printed none — but an answer
 * that says another outcome is a code and a JSON that disagree, and no busy.
 */
function busyOf(stdout: Buffer): { readonly kind: 'busy'; readonly reason: string } | NotAnswered {
  const read = bodyOf(stdout);

  return 'body' in read ? busyAnswer(read.body) : { kind: 'busy', reason: BUSY_FALLBACK };
}

function busyAnswer(body: JsonObject): { readonly kind: 'busy'; readonly reason: string } | NotAnswered {
  // A readable answer must SAY busy: one that names no outcome is no busy either (the coai code round on WR-E1, finding 2).
  const outcome = typeof body.outcome === 'string' ? body.outcome : 'no outcome';
  const reason = new Fields(body, '').sentence('reason');

  return outcome === 'busy'
    ? { kind: 'busy', reason: reason !== '' ? reason : BUSY_FALLBACK }
    : { kind: 'failed', reason: `it exited 75 but the answer says ${quoted(outcome, 40)}: the two disagree, so nothing is taken as busy` };
}

/**
 * A finished `tasks stage` on the bundled exe. Exit 0 is `staged` / `reused`, exit 1 `refused`, exit 75 `busy`, exit 2 the wrong
 * side — each only when the JSON on stdout says the same; a stage whose digest or release is not the expected is `provenanceRefused`.
 */
export function stageResultOf(result: ProcessResult, expected: Provenance): StageResult {
  return dispatch<StageResult>(result, {
    [DAEMON_EXIT.ok]: (exited) => stagedResult(DAEMON_EXIT.ok, exited.stdout, expected),
    [DAEMON_EXIT.runFailed]: (exited) => stagedResult(DAEMON_EXIT.runFailed, exited.stdout, expected),
    [DAEMON_EXIT.busy]: busy,
    [DAEMON_EXIT.usage]: wrongSide,
  });
}

// ---- tasks stage --remove ----

function removeReportOf(body: JsonObject): RemoveReport | string {
  const fields = new Fields(body, '');
  const outcome = fields.string('outcome');
  const report = {
    reason: fields.sentence('reason'),
    rootRemoved: fields.boolean('rootRemoved'),
    removed: fields.list('removed', folderOf),
    kept: fields.list('kept', folderOf),
    untouched: fields.strings('untouched'),
  };
  if (fields.problem !== '') {
    return fields.problem;
  }

  return outcome === 'removed' || outcome === 'busy' ? { outcome, ...report } : `the answer's outcome ${quoted(outcome, 40)} is not one this extension knows`;
}

function removedResult(stdout: Buffer): RemoveResult {
  const read = bodyOf(stdout);
  if (!('body' in read)) {
    return read;
  }
  const report = removeReportOf(read.body);
  if (typeof report === 'string') {
    return { kind: 'failed', reason: report };
  }

  return report.outcome === 'removed' ? { kind: 'removed', report } : { kind: 'failed', reason: `it exited 0 but the answer says ${quoted(report.outcome, 40)}: the two disagree, so nothing is taken as removed` };
}

/** A finished `tasks stage --remove` on the bundled exe: exit 0 `removed` (what went and what was kept is in the report), 75 `busy`, 2 the wrong side. */
export function removeResultOf(result: ProcessResult): RemoveResult {
  if (result.kind === 'timedOut') {
    return { kind: 'timedOut', timeoutMs: result.timeoutMs, treeEnded: result.treeEnded };
  }

  return dispatch<RemoveResult>(result, {
    [DAEMON_EXIT.ok]: (exited) => removedResult(exited.stdout),
    [DAEMON_EXIT.busy]: busy,
    [DAEMON_EXIT.usage]: wrongSide,
  });
}

// ---- run reads ----

/**
 * A finished run read: the body when the exit is one of `answers` (0, and 4 or 1 where the daemon answers after them —
 * `runReadChecks.ts`) and the body is readable; otherwise why not.
 */
export function readOutcomeOf(result: ProcessResult, read: RunReadName, answers: ReadonlySet<number>): WindowsReadOutcome {
  if (result.kind !== 'exited') {
    return { ...ended(result), read };
  }
  const code = signed32(result.code);
  if (!answers.has(code)) {
    return { ...otherExit(code, result.stderr), read };
  }
  const body = bodyOf(result.stdout);

  return 'body' in body ? { kind: 'read', read, body: body.body } : { ...body, read };
}
