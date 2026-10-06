import { DAEMON_EXIT, WSL_EXE_FAILED } from '../client/exitCodes';
import { classifyExit, launchFailure } from '../client/failures';
import { parseDaemonVersion } from '../client/handshake';
import type { DaemonVersion, Failure, JsonObject, VerbOutcome } from '../client/outcome';
import { DAEMON_PATH } from '../client/WslCareClient';
import type { ProcessResult, Runner } from '../process/runner';
import { actionGate, pickIds, type GateOpen } from './actionGate';
import { parseHandOff, parsePreview, runningOf, type HandOff, type HandOffResult, type PreviewContext } from './rootAnswers';
import { DEFAULT_NUMBERS, type Numbers } from '../settings/numbers';
import { callRoot, FULL_CHECK_ACTIONS, RUN_KINDS, type RootOp, type RootTarget } from './rootCall';
import { exitFailure } from './rootFailures';
import { runIdOf, shownCap, volumeNameOf, type ActionIds, type RunId, type VolumeName } from './rootIds';
import type { DaemonLimits } from '../shared/daemonLimits';
import type { HandOffOutcome, HeldPreview, PreviewOutcome, RootCheckOutcome, RootFailure, RunningBlock } from './rootOutcome';

/**
 * The host-side cleanup controller (E6.S2) — the API E6.S3's buttons call, and the ONLY module that imports
 * `rootCall.ts` (`structure.test.ts`). Each root op is one HOST transaction, in this order, and stops at the first refusal
 * having started nothing further:
 *
 * 1. the target — `WslCareClient.rootTarget()`: the validated, listed, RUNNING distribution (a root call never starts a
 *    stopped one);
 * 2. one root operation in flight per distribution — a second is refused at once (plan §15j m9; the daemon's run lock
 *    stays the authority, and its 75 comes back with the `running` block);
 * 3. a FRESH `status`, and the gate: the capabilities the op needs, then the ids — the compiled registry ∩ `status.actions`
 *    (§15f #2 / #3, §15j M5) — re-checked at the confirm, not only at the preview;
 * 4. the root check `-u root … --version`, cached per session per distribution once it answered (§15j m4) — a refusal greys
 *    the actions "needs root" and is asked again next time;
 * 5. the call, through the runner seam.
 *
 * <p><b>A4's names come from the held preview and nowhere else</b> (§15j B1): `preview()` returns a frozen `HeldPreview`
 * that this controller registers; `confirm()` takes only a registered one, and re-validates every name before it becomes
 * a stdin line, and a preview is CONSUMED by the confirm whose call went out (review M4) — it stays held only when nothing
 * started (a refusal before the call, a launcher that never started, the daemon refusing the piped list).</p>
 *
 * <p><b>A detach whose outcome is unknown is never reported as a failure</b> (§15k #3, the E6.S2 review round M1–M3, L3):</p>
 * <ul>
 *   <li>an exit the daemon does NOT guarantee as "nothing written" (70, 130, a signal's 137 / 143, anything outside
 *       `CERTAIN_DETACH_EXITS`), a timeout, a kill, an unreadable answer, `result: unknown`, a result this build does
 *       not know, or `accepted` without a run id — all are UNKNOWN;</li>
 *   <li>when the daemon NAMED a run id, `outcomeUnknown` carries it at once and the controller does not follow: E6.S3's
 *       durable poll follows that run (`runs show` / `status.running`) — one follower, not two;</li>
 *   <li>with NO run id the controller follows `status.running` of THIS distribution every `FOLLOW.intervalMs`, and adopts
 *       only a queued / live run that is provably this hand-off — trigger `manual` with exactly the asked actions
 *       (`["collect"]` for a full check) — as `acceptedObserved`. Any other run in flight (the timer's) is reported in
 *       `outcomeUnknown.otherRun`, never adopted; `outcomeUnknown.followed` counts the polls and how many status answered;</li>
 *   <li>the REAL bound: the loop stops starting polls at `FOLLOW.boundMs` (60 s) of the monotonic clock, and a poll in
 *       flight finishes — one `status` call is at most its three `wsl.exe` questions (15 s each) and the verb (20 s), so a
 *       follow ends within ~60 s + one interval (4 s) + one poll (65 s), about two minutes.</li>
 *   <li><b>The terminal state when that follow expires</b> (coai E6.S2 plan round #3) is `outcomeUnknown` with NO run id —
 *       the controller does not decide more. E6.S3 RESOLVES it on its next load from the daemon's own records: the runs
 *       (`runs --from <the confirm's instant> --to <now>`, and `status.lastCleanup`) over the window since the confirm,
 *       matching `trigger: manual` and exactly the confirmed ids (`["collect"]` for a full check) — one match is the run,
 *       none after the request budget's grace means it never ran, more than one stays "unknown" with the candidates shown.
 *       E6.S3 persists the confirm's instant and ids for exactly this (plan §16 E6.S3 row).</li>
 * </ul>
 */

export interface CleanupClient {
  rootTarget(): Promise<RootTarget | Failure>;
  run(verb: 'status'): Promise<VerbOutcome>;
}

export interface CleanupOptions {
  readonly client: CleanupClient;
  readonly runner: Runner;
  /** A monotonic clock in milliseconds (`performance.now()` in the product). */
  readonly now: () => number;
  readonly sleep: (ms: number) => Promise<void>;
  /** The number settings, read at each call (`settings/numbers.ts`): the root ceilings and the unknown-detach follow; the defaults when absent. */
  readonly numbers?: () => Numbers;
}

/** Following an unknown detach — the DEFAULTS: `status` every `wslCare.cleanup.followPollSeconds` (4 s, plan §15j M6's 3–5 s), for at most `wslCare.cleanup.unknownDetachFollowSeconds` (a minute). */
export const FOLLOW = { intervalMs: DEFAULT_NUMBERS.followPollSeconds * 1000, boundMs: DEFAULT_NUMBERS.unknownDetachFollowSeconds * 1000 } as const;

const BASE = ['running.block', 'runs.show'];

/**
 * The exits of a DETACH the daemon guarantees as "nothing written, nothing removed" (review M1): 1 (the write refused, or the
 * not-installed / old-glibc readings), 2, 69, 71, 73, 75–80, and wsl.exe's own -1. Every other exit — 70, 130, a signal's
 * 137 / 143, 3, 4 — may have come after the request was written, so it is followed, never reported as a failure.
 */
export const CERTAIN_DETACH_EXITS: ReadonlySet<number> = new Set([
  DAEMON_EXIT.runFailed, DAEMON_EXIT.usage, DAEMON_EXIT.detachUnavailable, DAEMON_EXIT.detachStartFailed, DAEMON_EXIT.queueFull,
  DAEMON_EXIT.busy, DAEMON_EXIT.wedged, DAEMON_EXIT.needsRoot, DAEMON_EXIT.observeOnly, DAEMON_EXIT.stateUnreadable, DAEMON_EXIT.requestGone, WSL_EXE_FAILED,
]);

/** What a follow with no run id may adopt: a run of THIS distribution, started by the panel, holding exactly these actions. */
interface Expected {
  readonly distro: string;
  readonly actions: readonly string[];
}

/** One status poll: answered for this distribution (with its running block, if any), or not. */
type Poll = { readonly answered: false } | { readonly answered: true; readonly running: RunningBlock | undefined };

/** A follow so far. */
interface Watch {
  readonly polls: number;
  readonly answered: number;
  readonly ours: RunningBlock | undefined;
  readonly other: RunningBlock | undefined;
}

const NO_WATCH: Watch = { polls: 0, answered: 0, ours: undefined, other: undefined };

/** What each op needs the daemon to ADVERTISE (`status.capabilities`) — the authority for acting (§15j M5). */
export const CAPABILITIES = {
  preview: [...BASE, 'act.shownList'],
  confirm: [...BASE, 'act.detach'],
  confirmA4: [...BASE, 'act.detach', 'act.onlyStdin'],
  stop: [...BASE, 'act.stop'],
  fullCheck: [...BASE, 'act.detach'],
} as const;

/** A call that went out: the op, and how it ended (`undefined`: the op could not be built, nothing started). */
interface Call {
  readonly op: RootOp;
  readonly result: ProcessResult | undefined;
  /** The daemon's published limits in the fresh status the call was gated on (daemon #17). */
  readonly limits: DaemonLimits;
}

type Prepared = RootOp | RootFailure;

/** An op that could not be built: nothing was started (a defect, never a daemon answer). */
const NOT_BUILT: Failure = { kind: 'unparseable', detail: 'the call could not be built; nothing was started' };

export class CleanupController {
  private readonly inFlight = new Set<string>();
  private readonly rootOk = new Map<string, DaemonVersion>();
  private readonly issued = new WeakSet<HeldPreview>();

  constructor(private readonly options: CleanupOptions) {}

  /** The number settings as they are now. */
  private numbers(): Numbers {
    return this.options.numbers?.() ?? DEFAULT_NUMBERS;
  }

  /** The root check alone (§15j m4): what root sees as the daemon's version, or why the actions are greyed "needs root". */
  rootCheck(): Promise<RootCheckOutcome> {
    return this.transaction((target) => this.checkedRoot(target));
  }

  /** `act <ids> --preview --json` as root — held, so a confirm can name it. */
  preview(ids: readonly string[]): Promise<PreviewOutcome> {
    return this.transaction(async (target) => {
      const call = await this.acting(target, CAPABILITIES.preview, (gate) => previewOp(ids, gate));
      return 'kind' in call ? call : this.held(call, target);
    });
  }

  /** `act <the preview's ids> --confirm --manual --detach [--only -] --json` as root, A4's names from the held preview. */
  confirm(preview: HeldPreview): Promise<HandOffOutcome> {
    if (!this.issued.has(preview)) {
      return Promise.resolve({ kind: 'previewNotHeld' });
    }

    return this.transaction((target) => this.confirmIn(target, preview));
  }

  /** `act --stop <runId> --json` as root — only for a run id the daemon writes. */
  stop(runIdText: string): Promise<HandOffOutcome> {
    const runId = runIdOf(runIdText);
    if (runId === undefined) {
      return Promise.resolve({ kind: 'runIdRefused' });
    }

    return this.transaction(async (target) => {
      const call = await this.acting(target, CAPABILITIES.stop, () => ({ op: 'stop', runId }));
      return 'kind' in call ? call : this.handedOff(call, target.distro, false);
    });
  }

  /** *Run full check now*: `collect --detach --json` as root — never `--timer` (§15j M9). */
  runFullCheck(): Promise<HandOffOutcome> {
    return this.transaction(async (target) => {
      const call = await this.acting(target, CAPABILITIES.fullCheck, () => ({ op: 'fullCheck' }));
      return 'kind' in call ? call : this.handedOff(call, target.distro, true);
    });
  }

  /** Steps 1 and 2: the target, and this distribution's one slot. */
  private async transaction<T>(body: (target: RootTarget) => Promise<T | RootFailure>): Promise<T | RootFailure> {
    const target = await this.options.client.rootTarget();
    if ('kind' in target) {
      return target;
    }
    if (this.inFlight.has(target.distro)) {
      return { kind: 'rootBusy', distro: target.distro };
    }
    this.inFlight.add(target.distro);
    try {
      return await body(target);
    } finally {
      this.inFlight.delete(target.distro);
    }
  }

  /** Steps 3–5: the gate over a fresh status, the op (or its refusal), the root check, the call. */
  private async acting(target: RootTarget, required: readonly string[], prepare: (gate: GateOpen) => Prepared): Promise<Call | RootFailure> {
    const gate = await this.gated(target, required);
    if (gate.kind !== 'open') {
      return gate;
    }
    const op = prepare(gate);
    if (!('op' in op)) {
      return op;
    }
    const root = await this.checkedRoot(target);

    return root.kind === 'rootOk' ? { op, result: await callRoot(this.options.runner, target, op, this.numbers(), gate.limits), limits: gate.limits } : root;
  }

  private async gated(target: RootTarget, required: readonly string[]): Promise<GateOpen | RootFailure> {
    const outcome = await this.options.client.run('status');
    if (outcome.kind !== 'answered') {
      return withoutVerb(outcome);
    }
    const body = statusBody(outcome);

    return outcome.distro === target.distro ? actionGate(body, outcome.daemonVersion, required) : { kind: 'distroChanged', previewed: target.distro, now: outcome.distro };
  }

  private async checkedRoot(target: RootTarget): Promise<RootCheckOutcome> {
    const known = this.rootOk.get(target.distro);
    if (known !== undefined) {
      return { kind: 'rootOk', distro: target.distro, version: known };
    }
    const outcome = rootCheckOf(await callRoot(this.options.runner, target, { op: 'rootCheck' }, this.numbers()), target.distro);
    if (outcome.kind === 'rootOk') {
      this.rootOk.set(target.distro, outcome.version);
    }

    return outcome;
  }

  private async confirmIn(target: RootTarget, preview: HeldPreview): Promise<HandOffOutcome> {
    if (!this.issued.has(preview)) {
      return { kind: 'previewNotHeld' };
    }

    return target.distro === preview.distro ? this.confirmCall(target, preview) : { kind: 'distroChanged', previewed: preview.distro, now: target.distro };
  }

  /** The confirm's call; the preview is consumed once the call went out and the daemon took it (review M4). */
  private async confirmCall(target: RootTarget, preview: HeldPreview): Promise<HandOffOutcome> {
    const required = preview.ids.includes('A4') ? CAPABILITIES.confirmA4 : CAPABILITIES.confirm;
    const call = await this.acting(target, required, (gate) => confirmOp(preview, gate));
    if ('kind' in call) {
      return call;
    }
    const outcome = await this.handedOff(call, target.distro, true);
    if (consumed(call, outcome)) {
      this.issued.delete(preview);
    }

    return outcome;
  }

  /** A preview's answer, held and registered — or why not. */
  private held(call: Call, target: RootTarget): PreviewOutcome {
    const ids = previewIds(call.op);
    const answer = answerOf(call.result, target.distro);
    if (ids === undefined || typeof answer !== 'string') {
      return typeof answer === 'string' ? NOT_BUILT : answer;
    }

    return this.registered(parsePreview(answer, { distro: target.distro, ids, takenAtMs: this.options.now(), maxShownNames: call.limits.maxShownNames } satisfies PreviewContext));
  }

  private registered(preview: HeldPreview | RootFailure): PreviewOutcome {
    if ('kind' in preview) {
      return preview;
    }
    const frozen = freezePreview(preview);
    this.issued.add(frozen);

    return { kind: 'previewed', preview: frozen };
  }

  /** A detach or a stop, answered or not. `follows`: a detach, whose unknown outcome is followed through `status.running`. */
  private async handedOff(call: Call, distro: string, follows: boolean): Promise<HandOffOutcome> {
    const result = call.result;
    if (result === undefined) {
      return NOT_BUILT;
    }
    const expected = { distro, actions: expectedActions(call.op) };

    return result.kind === 'exited' && result.code !== 0 ? this.refusedOrUnknown(call, result, expected, follows) : this.notRefused(result, expected, follows);
  }

  /** A non-zero exit: certain when the daemon guarantees nothing was written — for a detach, otherwise unknown (review M1). */
  private refusedOrUnknown(call: Call, result: Extract<ProcessResult, { kind: 'exited' }>, expected: Expected, follows: boolean): Promise<HandOffOutcome> {
    return follows && !CERTAIN_DETACH_EXITS.has(result.code)
      ? this.follow(`the detach exited ${result.code} (${exitFailure(result, expected.distro, false).kind}) — it may have written its request`, expected)
      : this.withRunning(exitFailure(result, expected.distro, pipedShown(call.op)), expected.distro);
  }

  /** Not a refusal: a launcher that never started (certain), or an answer — read, or none at all (a timeout, a kill). */
  private notRefused(result: ProcessResult, expected: Expected, follows: boolean): Promise<HandOffOutcome> {
    if (result.kind === 'failedToStart') {
      return Promise.resolve(launchFailure(result, 'daemonCall'));
    }

    return this.settled(result.kind === 'exited' ? parseHandOff(result.stdout.toString('utf8')) : undefined, expected, follows);
  }

  /** Accepted or stopping when the answer says so with its run id; otherwise unknown. */
  private settled(answer: HandOff | RootFailure | undefined, expected: Expected, follows: boolean): Promise<HandOffOutcome> {
    const read = readable(answer);
    const certain = read === undefined ? undefined : certainOf(read);
    if (certain !== undefined) {
      return Promise.resolve(certain);
    }

    return this.unknown(read === undefined ? undefined : read.runId, unknownReason(answer), expected, follows);
  }

  /** Review M3: a named run id goes back AT ONCE (E6.S3 follows it); only a detach with no run id is followed here. */
  private unknown(runId: RunId | undefined, reason: string, expected: Expected, follows: boolean): Promise<HandOffOutcome> {
    return runId === undefined && follows ? this.follow(reason, expected) : Promise.resolve(unknownOutcome(runId, reason));
  }

  /** Review M2 / L3: poll THIS distribution's status until a run provably ours is seen, or the bound passes. */
  private async follow(reason: string, expected: Expected): Promise<HandOffOutcome> {
    const numbers = this.numbers();
    const deadline = this.options.now() + numbers.unknownDetachFollowSeconds * 1000;
    let watch = NO_WATCH;
    while (this.options.now() < deadline && watch.ours === undefined) {
      await this.options.sleep(numbers.followPollSeconds * 1000);
      watch = watched(watch, await this.poll(expected.distro), expected);
    }

    return watch.ours === undefined ? followedUnknown(reason, watch) : { kind: 'acceptedObserved', runId: watch.ours.runId, running: watch.ours };
  }

  /** One status read — counted only when it answered for THIS distribution (review L3). */
  private async poll(distro: string): Promise<Poll> {
    const outcome = await this.options.client.run('status');

    return outcome.kind === 'answered' && outcome.distro === distro ? { answered: true, running: runningOf(statusBody(outcome)) } : { answered: false };
  }

  private async runningNow(distro: string): Promise<RunningBlock | undefined> {
    const poll = await this.poll(distro);

    return poll.answered ? poll.running : undefined;
  }

  /** 75 / 76 come back with the `running` block the daemon reports right after (§15j m9). */
  private async withRunning(failure: RootFailure, distro: string): Promise<RootFailure> {
    return failure.kind === 'busy' || failure.kind === 'wedged' ? { ...failure, running: await this.runningNow(distro) } : failure;
  }
}

/** The body of an answered `status` (any other answer reads as an empty body: no capabilities, no running block). */
function statusBody(outcome: Extract<VerbOutcome, { kind: 'answered' }>): JsonObject {
  return outcome.answer.verb === 'status' ? outcome.answer.body : {};
}

function withoutVerb(outcome: Exclude<VerbOutcome, { kind: 'answered' }>): Failure {
  const { verb: _verb, ...failure } = outcome;

  return failure as Failure;
}

function previewOp(requested: readonly string[], gate: GateOpen): Prepared {
  const ids = pickIds(requested, gate);

  return 'kind' in ids ? ids : { op: 'preview', ids };
}

/** The confirm of a held preview: its ids, still allowed; A4's names, re-validated — or why not. */
function confirmOp(preview: HeldPreview, gate: GateOpen): Prepared {
  const ids = pickIds(preview.ids, gate);
  if ('kind' in ids) {
    return ids;
  }
  const shown = ids.includes('A4') ? revalidated(preview, shownCap(gate.limits.maxShownNames)) : { names: undefined };

  return 'kind' in shown ? shown : { op: 'confirm', ids, shown: shown.names };
}

function revalidated(preview: HeldPreview, cap: number): { readonly names: readonly VolumeName[] } | RootFailure {
  const names = preview.a4 === undefined ? undefined : preview.a4.names;
  if (names === undefined) {
    return { kind: 'shownListInvalid', reason: 'its preview carried no shown list (the daemon could not read the volumes it would remove)' };
  }

  return names.length > cap ? { kind: 'shownListInvalid', reason: `its shown list holds ${names.length} names and the daemon now takes at most ${cap}: preview again` } : validNames(names);
}

function validNames(names: readonly string[]): { readonly names: readonly VolumeName[] } | RootFailure {
  const valid = names.flatMap((name) => volumeNameOf(name) ?? []);

  return valid.length === names.length ? { names: valid } : { kind: 'shownListInvalid', reason: 'a held name is no longer a 64-hex volume name' };
}

function previewIds(op: RootOp): ActionIds | undefined {
  return op.op === 'preview' ? op.ids : undefined;
}

/** A confirm that piped A4's list (`--only -`): its exit 2 is a refusal of that list, with a retry offered. */
function pipedShown(op: RootOp): boolean {
  return op.op === 'confirm' && op.shown !== undefined;
}

/** A root answer's text (exit 0), or why there is none. */
function answerOf(result: ProcessResult | undefined, distro: string): string | RootFailure {
  if (result === undefined) {
    return NOT_BUILT;
  }
  if (result.kind !== 'exited') {
    return launchFailure(result, 'daemonCall');
  }

  return result.code === 0 ? result.stdout.toString('utf8') : exitFailure(result, distro, false);
}

/** Review L2: only an EXIT of the root check (not 0, not wsl.exe's own -1) is "needs root"; a launcher failure is itself. */
function rootCheckOf(result: ProcessResult | undefined, distro: string): RootCheckOutcome {
  if (result === undefined) {
    return NOT_BUILT;
  }
  if (result.kind !== 'exited') {
    return launchFailure(result, 'daemonCall');
  }

  return result.code === 0 ? { kind: 'rootOk', distro, version: parseDaemonVersion(result.stdout.toString('utf8')) } : exitedRootCheck(result, distro);
}

function exitedRootCheck(result: Extract<ProcessResult, { kind: 'exited' }>, distro: string): RootCheckOutcome {
  const failure = classifyExit(result.code, result.stdout, result.stderr, distro, DAEMON_PATH);

  return result.code === WSL_EXE_FAILED ? failure : { kind: 'rootRefused', reason: failure };
}

/** Review M4: a confirm consumed its preview unless nothing started — no launcher, or the daemon refusing the piped list. */
function consumed(call: Call, outcome: HandOffOutcome): boolean {
  return call.result !== undefined && call.result.kind !== 'failedToStart' && outcome.kind !== 'shownListRefused';
}

/** The actions a run started by this op holds: the confirmed ids, or ["collect"] for a full check. */
function expectedActions(op: RootOp): readonly string[] {
  return op.op === 'confirm' ? op.ids : FULL_CHECK_ACTIONS;
}

function unknownOutcome(runId: RunId | undefined, reason: string): HandOffOutcome {
  return { kind: 'outcomeUnknown', runId, reason, followed: undefined, otherRun: undefined };
}

/** A queued or live run of the poll, sorted into ours (the panel's, exactly the asked actions) or another's. */
function watched(watch: Watch, poll: Poll, expected: Expected): Watch {
  const counted = { ...watch, polls: watch.polls + 1 };

  return poll.answered ? { ...counted, answered: watch.answered + 1, ...sorted(inFlightOf(poll.running), expected, watch.other) } : counted;
}

function inFlightOf(running: RunningBlock | undefined): RunningBlock | undefined {
  return running !== undefined && isQueuedOrLive(running) ? running : undefined;
}

function sorted(inFlight: RunningBlock | undefined, expected: Expected, other: RunningBlock | undefined): Pick<Watch, 'ours' | 'other'> {
  if (inFlight === undefined) {
    return { ours: undefined, other };
  }

  return isOurs(inFlight, expected) ? { ours: inFlight, other } : { ours: undefined, other: inFlight };
}

function followedUnknown(reason: string, watch: Watch): HandOffOutcome {
  const silent = watch.answered === 0 ? `; status answered none of ${watch.polls} polls` : '';
  const other = watch.other === undefined ? '' : `; another run is in flight (${watch.other.runId ?? 'no run id'}, ${watch.other.trigger}) — not this one`;

  return { kind: 'outcomeUnknown', runId: undefined, reason: reason + silent + other, followed: { polls: watch.polls, answered: watch.answered }, otherRun: watch.other };
}

function readable(answer: HandOff | RootFailure | undefined): HandOff | undefined {
  return answer === undefined || 'kind' in answer ? undefined : answer;
}

type Certain = (runId: RunId, answer: HandOff) => HandOffOutcome;

/** The results that settle a hand-off by themselves — with the run id they name. */
const CERTAIN: { readonly [K in Exclude<HandOffResult, 'unknown'>]: Certain } = {
  accepted: (runId, answer) => ({ kind: 'accepted', runId, unit: answer.unit, productVersion: answer.productVersion }),
  stopping: (runId, answer) => ({ kind: 'stopping', runId, unit: answer.unit }),
};

function certainMaker(result: HandOff['result']): Certain | undefined {
  return result.kind === 'known' && result.value !== 'unknown' ? CERTAIN[result.value] : undefined;
}

/** An answer that settles the hand-off by itself: accepted or stopping, WITH a run id — anything less is unknown. */
function certainOf(answer: HandOff): HandOffOutcome | undefined {
  const make = certainMaker(answer.result);

  return make === undefined || answer.runId === undefined ? undefined : make(answer.runId, answer);
}

function unknownReason(answer: HandOff | RootFailure | undefined): string {
  if (answer === undefined) {
    return 'the call did not answer in time (or was ended) — it may have been accepted';
  }

  return 'kind' in answer ? 'the daemon exited 0 with an answer that could not be read — it may have been accepted' : resultReason(answer);
}

function resultReason(answer: HandOff): string {
  const result = answer.result.kind === 'known' ? answer.result.value : answer.result.label;

  return `the daemon answered result ${result}${answer.runId === undefined ? ' with no run id' : ''}`;
}

/** Review M2: with no run id, only the panel's run of exactly the asked actions is this hand-off. */
function isOurs(running: RunningBlock, expected: Expected): boolean {
  return running.trigger === 'manual' && sameActions(running.actions, expected.actions);
}

function sameActions(a: readonly string[], b: readonly string[]): boolean {
  return a.length === b.length && [...a].sort().every((x, i) => x === [...b].sort()[i]);
}

function isQueuedOrLive(running: RunningBlock): boolean {
  return running.state.kind === 'known' && (running.state.value === 'queued' || running.state.value === 'live');
}

/** A held preview made immutable all the way down — the names a confirm pipes cannot be edited after the preview. */
function freezePreview(preview: HeldPreview): HeldPreview {
  const a4 = preview.a4 === undefined ? undefined : Object.freeze({ ...preview.a4, names: Object.freeze([...preview.a4.names]) });

  return Object.freeze({ ...preview, ids: Object.freeze([...preview.ids]) as unknown as HeldPreview['ids'], actions: Object.freeze(preview.actions.map((a) => Object.freeze({ ...a, items: Object.freeze([...a.items]) }))), a4 });
}

/** What a full check's run holds as its actions (`["collect"]`) — re-exported so the host's cleanup flow can name it
 * without importing the root module (only this controller imports `rootCall.ts`). */
export { FULL_CHECK_ACTIONS };

/** §15o's run kinds, re-exported for the follower's matching on the same terms. */
export { RUN_KINDS };
