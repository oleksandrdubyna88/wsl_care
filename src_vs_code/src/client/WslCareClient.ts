import type { ProcessResult, Runner } from '../process/runner';
import { DISTRO_NAME, defaultRow, distroSettingText, isDistroName, parseQuietList, parseVerboseTable } from '../wsl/distros';
import { distroListOf, namesOf } from '../wsl/distroList';
import { targetKey, type DistroList, type DistroName, type DistroTarget } from '../wsl/distroTarget';
import { wslExecutable } from '../wsl/wslExecutable';
import { decodeWslText } from '../wsl/wslText';
import { DAEMON_EXIT } from './exitCodes';
import { classifyExit, launchFailure, wslRefusal } from './failures';
import { checkedBody, ownVersion, parseAnswer, versionRefusal } from './handshake';
import type { Answer, DaemonVersion, Failure, JsonObject, ReadOutcome, VerbOutcome } from './outcome';
import { DEFAULT_NUMBERS, type Numbers } from '../settings/numbers';
import type { DaemonLimits } from '../shared/daemonLimits';
import { ceilingMs } from './ceilings';
import { answerExitsOf, readRefusal } from './runReadChecks';
import { PREVIEW_CONTAINER_ASSUMPTION, RUN_READ_CALLS, runReadTail, VERBS, type RunRead, type Verb } from './verbs';

/**
 * The extension's one client of the `wsl-care` daemon — and the ONLY module that builds `wsl.exe` argv
 * (`structure.test.ts`). Every call it makes is one of:
 *
 * - `wsl.exe --list --quiet` — is the configured distribution one WSL reports?
 * - `wsl.exe -l -v` — which is the default (the `*` row), when none is configured? And, for a PICKED distribution (E18.W1a), is it
 *   listed, and under which WSL version? (The one table answers both.)
 * - `wsl.exe --list --running --quiet` — is it running? When it is NOT, the client stops there: no `-d` call at all,
 *   because a `-d` would start the VM (plan §15f #8, §15g m3).
 * - `wsl.exe -d <distro> --cd / --exec /opt/wsl-care/bin/wsl-care <one of the four closed verbs>` — never `--`
 *   (measured 2026-10-03: `-- echo '$HOME'` printed `/home/<user>`, the distro's shell expanded it; `--exec echo
 *   '$HOME'` printed `$HOME` — §15f #1), never `-u`, never anything but `VERBS`.
 * - since E6.S3, the two run reads with the same prefix: `runs show <runId> --json` and `runs --from <instant> --to
 *   <instant> --json` (`read`, `RUN_READ_NAMES`) — values checked before any spawn, never `-u`.
 *
 * <p>It also builds the one other `wsl.exe` argv the extension has: `-d <distro> --cd ~` for the terminal *Install daemon*
 * opens (`terminalTarget`, E5.S3) — built here so this stays the only module that spells a `wsl.exe` argument; the
 * client never starts that terminal itself.</p>
 *
 * <p><b>Which distribution (E18.W1a).</b> Every public road — `run`, `read`, `terminalTarget`, `rootTarget` — takes a
 * {@link DistroTarget}, required: the DEFAULT (the `wslCare.distro` setting, resolved here exactly as it always was) or a PICKED
 * `DistroName` that only WSL's own listing can mint (`listDistros`). A picked target is NOT checked against the setting's pattern (the
 * name came from WSL's list, §15h #4); it is one `-l -v` — which is both the listed check and the version read: a WSL 1 distribution
 * answers `wsl1` before any running check, because the daemon needs systemd in the WSL 2 VM — then the running question, then the
 * `-d`. A read never starts a distribution: a picked distro that is not running (or whose running question failed) answers `stopped`.
 * `listDistros` answers every distribution with its version and running state, and prunes the per-distribution maps to the names it
 * reported, so a machine's distro count bounds them.</p>
 *
 * <p>The distribution SETTING is validated against a strict pattern BEFORE anything starts; every distribution is
 * checked against `--list` before any `-d`, and a name WSL lists is taken as it is (only a leading `-` is refused —
 * §15h #4). One call per verb is in flight: a second `run` of a verb still running shares the first's outcome, and the
 * `--version` the handshake may need is shared the same way.
 * Remaining race, stated (§15g m3): a distribution that stops between the running check and the `-d` call is started
 * again by that call — the window is the ~50 ms between two `wsl.exe` starts. The one deliberate exception is
 * `startIfStopped` ("Start WSL and check", E5.S2): the running check is still asked, and its "not running" no longer
 * stops the call, because starting the distribution is what the user asked for.</p>
 */

export interface ClientOptions {
  readonly runner: Runner;
  /** `process.platform`; anything but `win32` starts nothing (`extensionKind: ["ui"]` runs on the Windows side). */
  readonly platform: string;
  readonly env: Readonly<Record<string, string | undefined>>;
  /** The `wslCare.distro` setting, read at every call; empty means WSL's default distribution. */
  readonly distroSetting: () => unknown;
  /** The number settings (`settings/numbers.ts`), read at every call — the call ceilings come from them; the defaults when absent. */
  readonly numbers?: () => Numbers;
  /** The daemon's published limits (`status.limits`, from the store); the fallback when absent. */
  readonly limits?: () => DaemonLimits;
}

/** How one call may treat a stopped distribution. */
export interface RunOptions {
  readonly startIfStopped?: boolean;
}

/**
 * Where *Install daemon* opens its terminal (plan §15g m2): the absolute launcher and `-d <distro> --cd ~` — a
 * distribution that passed the same pattern and `--list` checks as every daemon call, and its user's home folder rather
 * than whatever Windows folder VS Code was started from (observed 2026-10-04, WSL 2.7.10.0: `--cd ~` lands in
 * `/home/<user>`). No `--exec`, no `-u`: the terminal is the person's own login shell in that distribution, and what is
 * typed into it is `install/installCommand.ts`'s.
 */
export interface TerminalTarget {
  readonly kind: 'terminal';
  readonly shellPath: string;
  readonly shellArgs: readonly string[];
  readonly distro: string;
  /** Only for a PICKED target: whether the distribution runs; `undefined` when the running question FAILED (never "stopped" on an unanswered question). */
  readonly running?: boolean | undefined;
}

/** The DEFAULT ceiling of each of the three WSL questions (`wslCare.timeouts.wslListSeconds`; measured: each answers in about 50 ms). */
export const LIST_TIMEOUT_MS = DEFAULT_NUMBERS.wslListSeconds * 1000;

/** Where the daemon is installed (E4.S1's `install.sh`). */
export const DAEMON_PATH = '/opt/wsl-care/bin/wsl-care';

const LIST_QUIET = ['--list', '--quiet'];
const LIST_VERBOSE = ['-l', '-v'];
const LIST_RUNNING = ['--list', '--running', '--quiet'];

type Step<T> = { readonly ok: true; readonly value: T } | { readonly ok: false; readonly failure: Failure };

function ok<T>(value: T): Step<T> {
  return { ok: true, value };
}

function fail<T>(failure: Failure): Step<T> {
  return { ok: false, failure };
}

/** The launcher and the distribution a call may go to. */
type Resolved = { readonly wsl: string; readonly distro: string };

/** One call's subject: the target the caller named, and the `wslCare.distro` setting as it read ONCE for this call. */
interface Scope {
  readonly target: DistroTarget;
  readonly setting: string;
}

/**
 * A daemon call's `wsl.exe` argv — the one place it is built: `-d <distro> [<as user>] --cd / --exec <daemon> <tail>`.
 * The read-only verbs pass no user; `root/rootCall.ts` — the ONE module that spells a root argv word (plan §15j M1) —
 * passes its `-u root` and its closed tails. This module spells the `wsl.exe` words and the daemon's path, nothing else.
 */
export function daemonArgv(distro: string, tail: readonly string[], asUser: readonly string[] = []): string[] {
  return ['-d', distro, ...asUser, '--cd', '/', '--exec', DAEMON_PATH, ...tail];
}

/** The daemon argv for `verb` in `distro`. */
function daemonArgs(distro: string, verb: Verb): string[] {
  return daemonArgv(distro, VERBS[verb]);
}

export class WslCareClient {
  private readonly inFlight = new Map<string, Promise<VerbOutcome>>();
  /** The run reads in flight, by their tail — two asks for one run share one call. */
  private readonly readsInFlight = new Map<string, Promise<ReadOutcome>>();
  /** The `--version` call in flight per distribution — `preview` and `doctor` asked at once share it (E5 code round). */
  private versionsInFlight: ReadonlyMap<string, Promise<DaemonVersion>> = new Map();
  /** The daemon version last read, per RESOLVED distribution name; pruned by `listDistros` to the names WSL reports. Replaced, never edited. */
  private knownVersion: ReadonlyMap<string, DaemonVersion> = new Map();
  /** The running-container count of the newest `status` answer, per distribution — what a timed-out `preview` is judged against. Replaced, never edited. */
  private knownContainers: ReadonlyMap<string, number> = new Map();
  /**
   * The names of the last `listDistros` answer — `undefined` until one answered. After it, an answer that comes back late for a name it did not
   * hold (a distribution unregistered while a call was in flight) remembers nothing, so no entry is re-inserted after the prune.
   */
  private listed: ReadonlySet<string> | undefined;

  constructor(private readonly options: ClientOptions) {}

  /**
   * Run `verb` in the distribution `target` names; a run of the same verb for the same target already in flight is shared, never
   * doubled — except that a run the user asked to START a stopped distribution is never folded into a poll (which would answer
   * "stopped"), so the two are keyed apart. The setting is read ONCE, here, and for the DEFAULT target it is part of the key: a call
   * made after the setting changed never shares the call still running for the previous distribution, whose answer would be shown
   * under the new one (retro review of PR #9, `distroSwitch.test.ts`). A PICKED target's key is its name, so a default call and a
   * picked call for the same distribution are two calls: sharing would couple the setting's generation to a picked read.
   */
  run(verb: Verb, target: DistroTarget, options: RunOptions = {}): Promise<VerbOutcome> {
    const startIfStopped = options.startIfStopped === true;
    const scope = this.scopeOf(target);
    const key = `${targetKey(target, scope.setting)}|${verb}${startIfStopped ? '+start' : ''}`;
    const running = this.inFlight.get(key);
    if (running !== undefined) {
      return running;
    }
    const started = this.runOnce(verb, startIfStopped, scope).finally(() => this.inFlight.delete(key));
    this.inFlight.set(key, started);

    return started;
  }

  /**
   * Every distribution `wsl.exe` reports, with its version and whether it runs (E18.W1a): `-l -v` (names, the `*`, the version
   * column) then `--list --running --quiet` (the unlocalised running signal — the state column is localised and is only shown).
   * A failed running question is NOT a failure of the list: every distribution then reads `running: undefined`, "state not known" —
   * never "stopped". A failed `-l -v` is the whole answer, and prunes nothing. On an answer the per-distribution maps drop every
   * name the list does not hold (a machine's distro count bounds them; there is no LRU).
   */
  async listDistros(): Promise<DistroList | Failure> {
    const wsl = this.launcher();
    if (!wsl.ok) {
      return wsl.failure;
    }
    const table = await this.wslText(wsl.value, LIST_VERBOSE);
    if (!table.ok) {
      return table.failure;
    }
    const list = distroListOf(table.value, await this.runningNames(wsl.value));
    this.forgetUnlisted(namesOf(list));

    return list;
  }

  /**
   * The terminal *Install daemon* may open, or why not. The DEFAULT: the distribution validated by its shape BEFORE anything starts
   * and checked against `wsl.exe --list` (which starts no VM) before the terminal exists; not asked whether it runs — the person
   * confirmed opening a shell there, which starts it. A PICKED distribution applies the listed and version checks (a WSL 1 one answers
   * `wsl1`: no terminal) and answers whether it runs, so the modal can say "<distro> is stopped and will be started" — the user's
   * explicit act, as *Start … and check* is.
   */
  async terminalTarget(target: DistroTarget): Promise<TerminalTarget | Failure> {
    const wsl = this.launcher();
    if (!wsl.ok) {
      return wsl.failure;
    }

    return target.kind === 'picked' ? this.pickedTerminal(wsl.value, target.distro) : this.defaultTerminal(wsl.value);
  }

  /**
   * Where a ROOT call may go (E6.S2): the launcher and a validated, listed, RUNNING distribution — exactly the checks of
   * every read-only verb, and never `startIfStopped`: no root call starts a stopped distribution. For a PICKED target the full
   * picked resolution (listed, version, running) applies: a stopped distribution answers `stopped`, a WSL 1 one `wsl1`, and NO
   * target is handed out — so no root op or config write can build a `-d` for it.
   */
  async rootTarget(target: DistroTarget): Promise<Resolved | Failure> {
    const resolved = await this.target(false, this.scopeOf(target));

    return resolved.ok ? resolved.value : resolved.failure;
  }

  /**
   * A run read (E6.S3): `runs show <runId>` or `runs --from <instant> --to <instant>`, unprivileged, with the checks of every
   * read-only verb and never `startIfStopped`. The values are checked BEFORE anything starts — a run id of the daemon's one
   * spelling, instants of exactly `yyyy-MM-ddTHH:mm:ssZ` — so nothing a caller holds reaches argv unvalidated. One read of
   * the same tail FOR THE SAME TARGET in flight is shared, as `run` shares a verb (the target is part of the key, as #23 made the
   * setting for the verbs: a read asked after a switch never joins the previous distribution's).
   */
  read(request: RunRead, target: DistroTarget): Promise<ReadOutcome> {
    const refusal = readRefusal(request);
    if (refusal !== undefined) {
      return Promise.resolve({ kind: 'readRefused', detail: refusal, read: request.read });
    }
    const scope = this.scopeOf(target);
    const key = `${targetKey(target, scope.setting)}|${runReadTail(request).join(' ')}`;
    const running = this.readsInFlight.get(key);
    if (running !== undefined) {
      return running;
    }
    const started = this.readOnce(request, scope).finally(() => this.readsInFlight.delete(key));
    this.readsInFlight.set(key, started);

    return started;
  }

  private scopeOf(target: DistroTarget): Scope {
    return { target, setting: distroSettingText(this.options.distroSetting()) };
  }

  private async readOnce(request: RunRead, scope: Scope): Promise<ReadOutcome> {
    const target = await this.target(false, scope);
    if (!target.ok) {
      return { ...target.failure, read: request.read };
    }
    const { wsl, distro } = target.value;
    const timeoutMs = this.ceiling(RUN_READ_CALLS[request.read]);
    const result = await this.options.runner({ file: wsl, args: daemonArgv(distro, runReadTail(request)), timeoutMs });
    const read = readAnswerOf(result, distro, timeoutMs, answerExitsOf(request.read));

    return 'body' in read ? { kind: 'read', read: request.read, distro, body: read.body } : { ...read, read: request.read };
  }

  private async runOnce(verb: Verb, startIfStopped: boolean, scope: Scope): Promise<VerbOutcome> {
    const target = await this.target(startIfStopped, scope);
    if (!target.ok) {
      return { ...target.failure, verb };
    }
    const answer = await this.ask(target.value.wsl, target.value.distro, verb);
    if (!answer.ok) {
      return { ...this.explained(verb, target.value.distro, answer.failure), verb };
    }
    this.remember(target.value.distro, answer.value);

    return this.judged(target.value.wsl, target.value.distro, verb, answer.value);
  }

  /** Keeps the running-container count a `status` answered, for `explained`. */
  private remember(distro: string, answer: Answer): void {
    const count = answer.verb === 'status' ? containerCount(answer.body) : undefined;
    if (count !== undefined && this.mayRemember(distro)) {
      this.knownContainers = withEntry(this.knownContainers, distro, count);
    }
  }

  /**
   * A `preview` that timed out on a machine with more running containers than its ceiling assumes (one
   * `container inspect` batch, `verbs.ts`) says so with the count, instead of a bare timeout (§15h #1). The count is
   * `status`'s RUNNING containers — a lower bound of what `preview` inspects, so a machine with many STOPPED containers
   * still reads as a plain timeout.
   */
  private explained(verb: Verb, distro: string, failure: Failure): Failure {
    return verb === 'preview' && failure.kind === 'timedOut' ? crowdedPreview(this.containersIn(distro), failure, failure.timeoutMs) : failure;
  }

  private containersIn(distro: string): number {
    return this.knownContainers.get(distro) ?? 0;
  }

  /** Drops every per-distribution entry whose name `listDistros` did not report. */
  private forgetUnlisted(listed: ReadonlySet<string>): void {
    this.listed = listed;
    this.knownVersion = onlyListed(this.knownVersion, listed);
    this.knownContainers = onlyListed(this.knownContainers, listed);
    this.versionsInFlight = onlyListed(this.versionsInFlight, listed);
  }

  /** Whether an answer for `distro` may be remembered: always before the first list, afterwards only for a name that list held. */
  private mayRemember(distro: string): boolean {
    return this.listed === undefined || this.listed.has(distro);
  }

  /**
   * Where to run: the launcher and a validated, listed, RUNNING distribution — or why not. With `startIfStopped` (the
   * user's "Start WSL and check") a listed distribution that is not running is still the target: the `-d` call that
   * follows starts it, because the user asked for exactly that. Every other check stands.
   */
  private async target(startIfStopped: boolean, scope: Scope): Promise<Step<Resolved>> {
    const wsl = this.launcher();
    if (!wsl.ok) {
      return wsl;
    }
    const { target, setting } = scope;

    return target.kind === 'picked' ? this.pickedResolution(wsl.value, target.distro, startIfStopped) : this.defaultResolution(wsl.value, setting, startIfStopped);
  }

  /** The DEFAULT target: today's road, byte for byte — the setting's pattern, `--list --quiet`, `-l -v` when empty, then the running check. */
  private async defaultResolution(wsl: string, setting: string, startIfStopped: boolean): Promise<Step<Resolved>> {
    const distro = await this.listedDistro(wsl, setting);
    if (!distro.ok) {
      return distro;
    }
    const running = await this.isRunning(wsl, distro.value);

    return targetOf(wsl, distro.value, running || startIfStopped);
  }

  /**
   * A PICKED target: ONE `-l -v` that is both the listed check and the version read (WSL 1 stops here, before any running check),
   * then `--list --running --quiet`. Two `wsl.exe` starts, the count a SET setting's road has today. No pattern check: the name came
   * from WSL's own listing.
   */
  private async pickedResolution(wsl: string, picked: DistroName, startIfStopped: boolean): Promise<Step<Resolved>> {
    const distro = await this.pickedListed(wsl, picked);
    if (!distro.ok) {
      return distro;
    }
    const running = await this.isRunning(wsl, distro.value);

    return targetOf(wsl, distro.value, running || startIfStopped);
  }

  /** `picked` is in the `-l -v` table (`checkListed`) and is not a WSL 1 distribution. */
  private async pickedListed(wsl: string, picked: DistroName): Promise<Step<string>> {
    const table = await this.wslText(wsl, LIST_VERBOSE);
    if (!table.ok) {
      return table;
    }
    const rows = parseVerboseTable(table.value);
    const listed = checkListed(picked, rows.map((row) => row.name));

    return listed.ok ? versionJudged(picked, rows.find((row) => row.name === picked)?.version) : listed;
  }

  private async defaultTerminal(wsl: string): Promise<TerminalTarget | Failure> {
    const distro = await this.listedDistro(wsl, distroSettingText(this.options.distroSetting()));

    return distro.ok ? terminalOf(wsl, distro.value) : distro.failure;
  }

  private async pickedTerminal(wsl: string, picked: DistroName): Promise<TerminalTarget | Failure> {
    const distro = await this.pickedListed(wsl, picked);

    return distro.ok ? { ...terminalOf(wsl, distro.value), running: (await this.runningNames(wsl))?.includes(distro.value) } : distro.failure;
  }

  private launcher(): Step<string> {
    if (this.options.platform !== 'win32') {
      return fail({ kind: 'notWindows', platform: this.options.platform });
    }
    const wsl = wslExecutable(this.options.env);

    return wsl === undefined ? fail({ kind: 'wslMissing', detail: 'SystemRoot does not name a drive folder holding System32\\wsl.exe' }) : ok(wsl);
  }

  /** The configured distribution (`configured`, the trimmed setting) — refused by its shape BEFORE anything starts — or
   * WSL's default, checked against `--list`. */
  private async listedDistro(wsl: string, configured: string): Promise<Step<string>> {
    if (configured !== '' && !isDistroName(configured)) {
      return fail(notAName(configured));
    }
    const listed = await this.wslText(wsl, LIST_QUIET);

    return listed.ok ? this.chosenAmong(wsl, configured, parseQuietList(listed.value)) : listed;
  }

  private async chosenAmong(wsl: string, configured: string, listed: readonly string[]): Promise<Step<string>> {
    const chosen = configured === '' ? await this.defaultDistro(wsl) : ok(configured);

    return chosen.ok ? checkListed(chosen.value, listed) : chosen;
  }

  /** WSL's default (the `*` row of `-l -v`); a WSL 1 default answers `wsl1` — the table was read anyway, so the check costs no call. */
  private async defaultDistro(wsl: string): Promise<Step<string>> {
    const verbose = await this.wslText(wsl, LIST_VERBOSE);
    if (!verbose.ok) {
      return verbose;
    }
    const marked = defaultRow(verbose.value);
    if (marked === undefined) {
      return fail({ kind: 'noDefaultDistro', detail: 'wsl.exe -l -v marks no single distribution with *' });
    }

    return marked.version === 1 ? fail({ kind: 'wsl1', distro: marked.name }) : ok(marked.name);
  }

  /** Running, as `--list --running --quiet` says. Any failure of that question reads as "not running": no `-d`. */
  private async isRunning(wsl: string, distro: string): Promise<boolean> {
    return (await this.runningNames(wsl))?.includes(distro) === true;
  }

  /** The names `--list --running --quiet` printed — `undefined` when the question failed (it did not answer, so nothing is known). */
  private async runningNames(wsl: string): Promise<string[] | undefined> {
    const running = await this.wslText(wsl, LIST_RUNNING);

    return running.ok ? parseQuietList(running.value) : undefined;
  }

  /** One of `wsl.exe`'s own questions, decoded; a failure is a WSL failure. */
  private async wslText(wsl: string, args: readonly string[]): Promise<Step<string>> {
    const result = await this.options.runner({ file: wsl, args, timeoutMs: this.listCeilingMs() });
    if (result.kind !== 'exited') {
      return fail(launchFailure(result, 'wslQuestion'));
    }

    return result.code === DAEMON_EXIT.ok ? ok(decodeWslText(result.stdout)) : fail(wslRefusal(result.stdout, result.stderr));
  }

  /** One daemon verb in `distro`, its answer parsed and its schema checked. */
  private async ask(wsl: string, distro: string, verb: Verb): Promise<Step<Answer>> {
    const timeoutMs = this.ceiling(verb);
    const result = await this.options.runner({ file: wsl, args: daemonArgs(distro, verb), timeoutMs });

    return answerOf(result, distro, verb, timeoutMs);
  }

  /** The `wsl.exe --list` questions' ceiling (`wslCare.timeouts.wslListSeconds`), as the settings are NOW. */
  private listCeilingMs(): number {
    return (this.options.numbers?.() ?? DEFAULT_NUMBERS).wslListSeconds * 1000;
  }

  /** The call's ceiling, from the number settings as they are NOW (`client/ceilings.ts`). */
  private ceiling(call: Verb | 'runRead' | 'archivePreview'): number {
    return ceilingMs(this.options.numbers?.() ?? DEFAULT_NUMBERS, { call }, this.options.limits?.());
  }

  /** The answer, judged against the daemon version: a released daemon below the minimum blanks the view. */
  private async judged(wsl: string, distro: string, verb: Verb, answer: Answer): Promise<VerbOutcome> {
    const version = await this.versionFor(wsl, distro, answer);
    const refusal = verb === 'version' ? undefined : versionRefusal(version);

    return refusal === undefined ? { kind: 'answered', verb, distro, answer, daemonVersion: version } : { ...refusal, verb };
  }

  /** The daemon version for `answer`: its own (`status.productVersion`, `--version`), else the one known for this
   * distribution, else one `--version` call. A version that could not be read is not remembered — the next verb asks
   * again. */
  private async versionFor(wsl: string, distro: string, answer: Answer): Promise<DaemonVersion> {
    const own = ownVersion(answer);
    const version = own ?? this.knownVersion.get(distro) ?? (await this.askVersion(wsl, distro));
    this.rememberVersion(distro, version);

    return version;
  }

  private rememberVersion(distro: string, version: DaemonVersion): void {
    if (version.kind === 'notRead') {
      this.knownVersion = withoutEntry(this.knownVersion, distro);
    } else if (this.mayRemember(distro)) {
      this.knownVersion = withEntry(this.knownVersion, distro, version);
    }
  }

  /** One `--version` per distribution at a time: a second asker (the other of `preview` / `doctor`) shares the call in
   * flight instead of starting its own — the "one call per verb in flight" rule, for the verb `run` does not route. */
  private askVersion(wsl: string, distro: string): Promise<DaemonVersion> {
    const running = this.versionsInFlight.get(distro);
    if (running !== undefined) {
      return running;
    }
    const started = this.readVersion(wsl, distro).finally(() => { this.versionsInFlight = withoutEntry(this.versionsInFlight, distro); });
    this.versionsInFlight = withEntry(this.versionsInFlight, distro, started);

    return started;
  }

  private async readVersion(wsl: string, distro: string): Promise<DaemonVersion> {
    const answer = await this.ask(wsl, distro, 'version');

    return answer.ok && answer.value.verb === 'version' ? answer.value.version : { kind: 'notRead', reason: answer.ok ? 'no version' : answer.failure.kind };
  }
}

/** What one daemon call ended in: the parsed, schema-checked answer, or the failure it reads as. */
function answerOf(result: ProcessResult, distro: string, verb: Verb, timeoutMs: number): Step<Answer> {
  switch (result.kind) {
    case 'timedOut':
      return fail({ kind: 'timedOut', timeoutMs });
    case 'exited':
      return exitedAnswer(result, distro, verb);
    default:
      return fail(launchFailure(result, 'daemonCall'));
  }
}

function exitedAnswer(result: Extract<ProcessResult, { kind: 'exited' }>, distro: string, verb: Verb): Step<Answer> {
  if (result.code !== DAEMON_EXIT.ok) {
    return fail(classifyExit(result.code, result.stdout, result.stderr, distro, DAEMON_PATH));
  }
  const parsed = parseAnswer(verb, result.stdout.toString('utf8'));

  return 'kind' in parsed ? fail(parsed) : ok(parsed);
}

/** A run read's ending: the checked body — exit 0, or 4 with a readable answer (the history could not be read) — or why not. */
function readAnswerOf(result: ProcessResult, distro: string, timeoutMs: number, answers: ReadonlySet<number>): { readonly body: JsonObject } | Failure {
  if (result.kind === 'timedOut') {
    return { kind: 'timedOut', timeoutMs };
  }
  if (result.kind !== 'exited') {
    return launchFailure(result, 'daemonCall');
  }

  return exitedRead(result, distro, answers);
}

function exitedRead(result: Extract<ProcessResult, { kind: 'exited' }>, distro: string, answers: ReadonlySet<number>): { readonly body: JsonObject } | Failure {
  const checked = answers.has(result.code) ? checkedBody(result.stdout.toString('utf8')) : undefined;
  if (checked !== undefined && !('kind' in checked)) {
    return { body: checked.body };
  }

  return unanswered(result, checked, distro);
}

/** An exit 0 whose answer could not be read is that reading; any other exit is the exit's. */
function unanswered(result: Extract<ProcessResult, { kind: 'exited' }>, checked: Failure | undefined, distro: string): Failure {
  return result.code === DAEMON_EXIT.ok && checked !== undefined ? checked : classifyExit(result.code, result.stdout, result.stderr, distro, DAEMON_PATH);
}

/** The target when the distribution may be asked (running, or the user asked to start it); "stopped" otherwise. */
function targetOf(wsl: string, distro: string, mayAsk: boolean): Step<Resolved> {
  return mayAsk ? ok({ wsl, distro }) : fail({ kind: 'stopped', distro });
}

/** Only a provable WSL 2 passes: 1 is `wsl1`, and a version column that is neither 1 nor 2 is `versionUnknown` — never assumed to be 2. */
function versionJudged(distro: string, version: 1 | 2 | undefined): Step<string> {
  if (version === 2) {
    return ok(distro);
  }

  return fail(version === 1 ? { kind: 'wsl1', distro } : { kind: 'versionUnknown', distro });
}

/** The terminal *Install daemon* opens in `distro`: the person's own login shell in their home folder. */
function terminalOf(wsl: string, distro: string): TerminalTarget {
  return { kind: 'terminal', shellPath: wsl, shellArgs: ['-d', distro, '--cd', '~'], distro };
}

/** A copy of `entries` with `key` set — the maps are replaced, never edited (the repository's immutability rule). */
function withEntry<V>(entries: ReadonlyMap<string, V>, key: string, value: V): ReadonlyMap<string, V> {
  return new Map(entries).set(key, value);
}

function withoutEntry<V>(entries: ReadonlyMap<string, V>, key: string): ReadonlyMap<string, V> {
  return new Map([...entries].filter(([name]) => name !== key));
}

/** A copy of `entries` holding only the keys `listed` holds. */
function onlyListed<V>(entries: ReadonlyMap<string, V>, listed: ReadonlySet<string>): ReadonlyMap<string, V> {
  return new Map([...entries].filter(([name]) => listed.has(name)));
}

/** `status`'s `vm.containers.count`, when it answered one. */
function containerCount(body: Readonly<Record<string, unknown>>): number | undefined {
  const count = ['vm', 'containers', 'count'].reduce<unknown>((value, key) => fieldOf(value, key), body);

  return Number.isInteger(count) ? (count as number) : undefined;
}

function fieldOf(value: unknown, key: string): unknown {
  return typeof value === 'object' && value !== null ? (value as Readonly<Record<string, unknown>>)[key] : undefined;
}

/** A timed-out preview with more running containers than its ceiling assumes, said with the count. */
function crowdedPreview(containers: number, failure: Failure, timeoutMs: number): Failure {
  return containers > PREVIEW_CONTAINER_ASSUMPTION ? { kind: 'previewTooManyContainers', containers, timeoutMs } : failure;
}

/** The SETTING's value fails the strict pattern — refused before anything starts, so no list was asked. */
function notAName(distro: string): Failure {
  return { kind: 'distroRefused', distro, reason: `the wslCare.distro setting must match the pattern ${DISTRO_NAME.source} (letters, digits, ".", "_", "-", not starting with "-"); nothing was started, so WSL's list was not asked` };
}

/**
 * A name `wsl.exe` reports is taken AS IT IS (§15h #4): argv reaches `wsl.exe` without a shell, so the listing is the
 * authority on what a distribution may be called. The one name refused is one starting with `-`, which `wsl.exe` would
 * read after `-d` as an option. The strict pattern is the SETTING's (`listedDistro`), checked before any spawn.
 */
function checkListed(distro: string, listed: readonly string[]): Step<string> {
  if (distro.startsWith('-')) {
    return fail({ kind: 'distroRefused', distro, reason: 'a name starting with "-" would be read by wsl.exe as an option after -d' });
  }

  return listed.includes(distro) ? ok(distro) : fail(unlisted(distro, listed));
}

function unlisted(distro: string, listed: readonly string[]): Failure {
  const names = listed.length === 0 ? 'none' : listed.map((name) => `"${name}"`).join(', ');

  return { kind: 'distroRefused', distro, reason: `wsl.exe --list does not report it (it reports: ${names}); a wslCare.distro setting must also match the pattern ${DISTRO_NAME.source}` };
}
