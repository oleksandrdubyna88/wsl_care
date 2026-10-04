import { classifyExit } from '../client/failures';
import { parseDaemonVersion } from '../client/handshake';
import type { DaemonVersion, Failure, JsonObject, VerbOutcome } from '../client/outcome';
import { DAEMON_PATH } from '../client/WslCareClient';
import type { ProcessResult, Runner } from '../process/runner';
import { actionGate, pickIds, type GateOpen } from './actionGate';
import { parseHandOff, parsePreview, runningOf, type HandOff, type HandOffResult, type PreviewContext } from './rootAnswers';
import { callRoot, type RootOp, type RootTarget } from './rootCall';
import { exitFailure, startFailure } from './rootFailures';
import { runIdOf, volumeNameOf, type ActionIds, type RunId, type VolumeName } from './rootIds';
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
 * a stdin line. <b>A detach whose outcome is unknown</b> — a timeout, a kill, an unreadable answer, `result: unknown`, a
 * result this build does not know — is never reported as a failure: the controller follows `status.running` every
 * `FOLLOW.intervalMs` for `FOLLOW.boundMs`, and reports the run it SAW queued or live, or "outcome unknown" with the run
 * id when it has one (§15k #3). Residual, stated: with no run id (a detach that timed out before answering), a run seen in
 * flight may be another's — the timer's — and is reported as the run in flight, not as ours.</p>
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
}

/** Following an unknown detach: `status` every 4 s (plan §15j M6's 3–5 s), for at most a minute. */
export const FOLLOW = { intervalMs: 4_000, boundMs: 60_000 } as const;

const BASE = ['running.block', 'runs.show'];

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
}

type Prepared = RootOp | RootFailure;

/** An op that could not be built: nothing was started (a defect, never a daemon answer). */
const NOT_BUILT: Failure = { kind: 'unparseable', detail: 'the call could not be built; nothing was started' };

export class CleanupController {
  private readonly inFlight = new Set<string>();
  private readonly rootOk = new Map<string, DaemonVersion>();
  private readonly issued = new WeakSet<HeldPreview>();

  constructor(private readonly options: CleanupOptions) {}

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
    const op = gate.kind === 'open' ? prepare(gate) : gate;
    if (!('op' in op)) {
      return op;
    }
    const root = await this.checkedRoot(target);

    return root.kind === 'rootOk' ? { op, result: await callRoot(this.options.runner, target, op) } : root;
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
    const outcome = rootCheckOf(await callRoot(this.options.runner, target, { op: 'rootCheck' }), target.distro);
    if (outcome.kind === 'rootOk') {
      this.rootOk.set(target.distro, outcome.version);
    }

    return outcome;
  }

  private async confirmIn(target: RootTarget, preview: HeldPreview): Promise<HandOffOutcome> {
    if (target.distro !== preview.distro) {
      return { kind: 'distroChanged', previewed: preview.distro, now: target.distro };
    }
    const required = preview.ids.includes('A4') ? CAPABILITIES.confirmA4 : CAPABILITIES.confirm;
    const call = await this.acting(target, required, (gate) => confirmOp(preview, gate));

    return 'kind' in call ? call : this.handedOff(call, target.distro, true);
  }

  /** A preview's answer, held and registered — or why not. */
  private held(call: Call, target: RootTarget): PreviewOutcome {
    const ids = previewIds(call.op);
    const answer = answerOf(call.result, target.distro);
    if (ids === undefined || typeof answer !== 'string') {
      return typeof answer === 'string' ? NOT_BUILT : answer;
    }

    return this.registered(parsePreview(answer, { distro: target.distro, ids, takenAtMs: this.options.now() } satisfies PreviewContext));
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

    return result.kind === 'exited' && result.code !== 0 ? this.withRunning(exitFailure(result, distro, pipedShown(call.op))) : this.notRefused(result, follows);
  }

  /** Not a refusal: a launcher that never started (certain), or an answer — read, or none at all (a timeout, a kill). */
  private notRefused(result: ProcessResult, follows: boolean): Promise<HandOffOutcome> {
    if (result.kind === 'failedToStart') {
      return Promise.resolve(startFailure(result));
    }

    return this.settled(result.kind === 'exited' ? parseHandOff(result.stdout.toString('utf8')) : undefined, follows);
  }

  /** Accepted or stopping when the answer says so with its run id; otherwise unknown — followed, when it was a detach. */
  private settled(answer: HandOff | RootFailure | undefined, follows: boolean): Promise<HandOffOutcome> {
    const read = readable(answer);
    const certain = read === undefined ? undefined : certainOf(read);
    if (certain !== undefined) {
      return Promise.resolve(certain);
    }

    return this.unknown(read === undefined ? undefined : read.runId, unknownReason(answer), follows);
  }

  private unknown(runId: RunId | undefined, reason: string, follows: boolean): Promise<HandOffOutcome> {
    return follows ? this.follow(runId, reason) : Promise.resolve({ kind: 'outcomeUnknown', runId, reason });
  }

  private async follow(runId: RunId | undefined, reason: string): Promise<HandOffOutcome> {
    const deadline = this.options.now() + FOLLOW.boundMs;
    while (this.options.now() < deadline) {
      await this.options.sleep(FOLLOW.intervalMs);
      const seen = await this.inFlightRun(runId);
      if (seen !== undefined) {
        return { kind: 'acceptedObserved', runId: seen.runId ?? runId, running: seen };
      }
    }

    return { kind: 'outcomeUnknown', runId, reason };
  }

  private async inFlightRun(runId: RunId | undefined): Promise<RunningBlock | undefined> {
    const running = await this.runningNow();

    return running !== undefined && isOurs(running, runId) && isQueuedOrLive(running) ? running : undefined;
  }

  private async runningNow(): Promise<RunningBlock | undefined> {
    const outcome = await this.options.client.run('status');

    return outcome.kind === 'answered' ? runningOf(statusBody(outcome)) : undefined;
  }

  /** 75 / 76 come back with the `running` block the daemon reports right after (§15j m9). */
  private async withRunning(failure: RootFailure): Promise<RootFailure> {
    return failure.kind === 'busy' || failure.kind === 'wedged' ? { ...failure, running: await this.runningNow() } : failure;
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
  const shown = ids.includes('A4') ? revalidated(preview) : { names: undefined };

  return 'kind' in shown ? shown : { op: 'confirm', ids, shown: shown.names };
}

function revalidated(preview: HeldPreview): { readonly names: readonly VolumeName[] } | RootFailure {
  const names = preview.a4 === undefined ? undefined : preview.a4.names;
  if (names === undefined) {
    return { kind: 'shownListInvalid', reason: 'its preview carried no shown list (the daemon could not read the volumes it would remove)' };
  }
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
    return startFailure(result);
  }

  return result.code === 0 ? result.stdout.toString('utf8') : exitFailure(result, distro, false);
}

function rootCheckOf(result: ProcessResult | undefined, distro: string): RootCheckOutcome {
  if (result === undefined) {
    return { kind: 'rootRefused', reason: NOT_BUILT };
  }
  if (result.kind !== 'exited') {
    return { kind: 'rootRefused', reason: startFailure(result) };
  }

  return result.code === 0
    ? { kind: 'rootOk', distro, version: parseDaemonVersion(result.stdout.toString('utf8')) }
    : { kind: 'rootRefused', reason: classifyExit(result.code, result.stdout, result.stderr, distro, DAEMON_PATH) };
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

function isOurs(running: RunningBlock, runId: RunId | undefined): boolean {
  return runId === undefined || running.runId === runId;
}

function isQueuedOrLive(running: RunningBlock): boolean {
  return running.state.kind === 'known' && (running.state.value === 'queued' || running.state.value === 'live');
}

/** A held preview made immutable all the way down — the names a confirm pipes cannot be edited after the preview. */
function freezePreview(preview: HeldPreview): HeldPreview {
  const a4 = preview.a4 === undefined ? undefined : Object.freeze({ ...preview.a4, names: Object.freeze([...preview.a4.names]) });

  return Object.freeze({ ...preview, ids: Object.freeze([...preview.ids]) as unknown as HeldPreview['ids'], actions: Object.freeze(preview.actions.map((a) => Object.freeze({ ...a }))), a4 });
}
