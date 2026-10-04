import type { ProcessResult, Runner } from '../process/runner';
import { isDistroName, parseDefaultDistro, parseQuietList } from '../wsl/distros';
import { wslExecutable } from '../wsl/wslExecutable';
import { decodeWslText } from '../wsl/wslText';
import { DAEMON_EXIT } from './exitCodes';
import { classifyExit, wslRefusal } from './failures';
import { parseAnswer, parseDaemonVersion, versionRefusal } from './handshake';
import type { Answer, DaemonVersion, Failure, VerbOutcome } from './outcome';
import { VERB_TIMEOUT_MS, VERBS, type Verb } from './verbs';

/**
 * The extension's one client of the `wsl-care` daemon — and the ONLY module that builds `wsl.exe` argv
 * (`structure.test.ts`). Every call it makes is one of:
 *
 * - `wsl.exe --list --quiet` — is the configured distribution one WSL reports?
 * - `wsl.exe -l -v` — which is the default (the `*` row), when none is configured?
 * - `wsl.exe --list --running --quiet` — is it running? When it is NOT, the client stops there: no `-d` call at all,
 *   because a `-d` would start the VM (plan §15f #8, §15g m3).
 * - `wsl.exe -d <distro> --cd / --exec /opt/wsl-care/bin/wsl-care <one of the four closed verbs>` — never `--`
 *   (measured 2026-10-03: `-- echo '$HOME'` printed `/home/<user>`, the distro's shell expanded it; `--exec echo
 *   '$HOME'` printed `$HOME` — §15f #1), never `-u`, never anything but `VERBS`.
 *
 * <p>The distribution is validated against a strict pattern BEFORE anything starts and against `--list` before any
 * `-d`. One call per verb is in flight: a second `run` of a verb still running shares the first's outcome.
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
  readonly distroSetting: () => string;
}

/** How one call may treat a stopped distribution. */
export interface RunOptions {
  readonly startIfStopped?: boolean;
}

/** The ceiling of each of the three WSL questions (measured: each answers in about 50 ms). */
export const LIST_TIMEOUT_MS = 15_000;

/** Where the daemon is installed (E4.S1's `install.sh`). */
const DAEMON_PATH = '/opt/wsl-care/bin/wsl-care';

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

/** The daemon argv for `verb` in `distro` — the one place it is built. */
function daemonArgs(distro: string, verb: Verb): string[] {
  return ['-d', distro, '--cd', '/', '--exec', DAEMON_PATH, ...VERBS[verb]];
}

function startFailure(result: ProcessResult): Failure {
  switch (result.kind) {
    case 'failedToStart':
      return { kind: 'wslFailed', message: `wsl.exe could not be started: ${result.reason}` };
    case 'tooMuchOutput':
      return { kind: 'unparseable', detail: `the answer exceeded ${result.limitBytes} bytes on ${result.stream}` };
    case 'timedOut':
      return { kind: 'wslFailed', message: `wsl.exe did not answer within ${result.timeoutMs} ms` };
    default:
      return { kind: 'unknownFailure', code: undefined, messages: [] };
  }
}

export class WslCareClient {
  private readonly inFlight = new Map<string, Promise<VerbOutcome>>();
  private knownVersion: { readonly distro: string; readonly version: DaemonVersion } | undefined;

  constructor(private readonly options: ClientOptions) {}

  /**
   * Run `verb`; a run of the same verb already in flight is shared, never doubled — except that a run the user asked
   * to START a stopped distribution is never folded into a poll (which would answer "stopped"), so the two are keyed
   * apart.
   */
  run(verb: Verb, options: RunOptions = {}): Promise<VerbOutcome> {
    const startIfStopped = options.startIfStopped === true;
    const key = `${verb}${startIfStopped ? '+start' : ''}`;
    const running = this.inFlight.get(key);
    if (running !== undefined) {
      return running;
    }
    const started = this.runOnce(verb, startIfStopped).finally(() => this.inFlight.delete(key));
    this.inFlight.set(key, started);

    return started;
  }

  private async runOnce(verb: Verb, startIfStopped: boolean): Promise<VerbOutcome> {
    const target = await this.target(startIfStopped);
    if (!target.ok) {
      return { ...target.failure, verb };
    }
    const answer = await this.ask(target.value.wsl, target.value.distro, verb);
    if (!answer.ok) {
      return { ...answer.failure, verb };
    }

    return this.judged(target.value.wsl, target.value.distro, verb, answer.value);
  }

  /**
   * Where to run: the launcher and a validated, listed, RUNNING distribution — or why not. With `startIfStopped` (the
   * user's "Start WSL and check") a listed distribution that is not running is still the target: the `-d` call that
   * follows starts it, because the user asked for exactly that. Every other check stands.
   */
  private async target(startIfStopped: boolean): Promise<Step<{ wsl: string; distro: string }>> {
    const wsl = this.launcher();
    if (!wsl.ok) {
      return wsl;
    }
    const distro = await this.listedDistro(wsl.value);
    if (!distro.ok) {
      return distro;
    }
    const running = await this.isRunning(wsl.value, distro.value);

    return targetOf(wsl.value, distro.value, running || startIfStopped);
  }

  private launcher(): Step<string> {
    if (this.options.platform !== 'win32') {
      return fail({ kind: 'notWindows', platform: this.options.platform });
    }
    const wsl = wslExecutable(this.options.env);

    return wsl === undefined ? fail({ kind: 'wslMissing', detail: 'SystemRoot does not name a drive folder holding System32\\wsl.exe' }) : ok(wsl);
  }

  /** The configured distribution — refused by its shape BEFORE anything starts — or WSL's default, checked against
   * `--list`. */
  private async listedDistro(wsl: string): Promise<Step<string>> {
    const configured = this.options.distroSetting().trim();
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

  private async defaultDistro(wsl: string): Promise<Step<string>> {
    const verbose = await this.wslText(wsl, LIST_VERBOSE);
    if (!verbose.ok) {
      return verbose;
    }
    const marked = parseDefaultDistro(verbose.value);

    return marked === undefined ? fail({ kind: 'noDefaultDistro', detail: 'wsl.exe -l -v marks no single distribution with *' }) : ok(marked);
  }

  /** Running, as `--list --running --quiet` says. Any failure of that question reads as "not running": no `-d`. */
  private async isRunning(wsl: string, distro: string): Promise<boolean> {
    const running = await this.wslText(wsl, LIST_RUNNING);

    return running.ok && parseQuietList(running.value).includes(distro);
  }

  /** One of `wsl.exe`'s own questions, decoded; a failure is a WSL failure. */
  private async wslText(wsl: string, args: readonly string[]): Promise<Step<string>> {
    const result = await this.options.runner({ file: wsl, args, timeoutMs: LIST_TIMEOUT_MS });
    if (result.kind !== 'exited') {
      return fail(startFailure(result));
    }

    return result.code === DAEMON_EXIT.ok ? ok(decodeWslText(result.stdout)) : fail(wslRefusal(result.stdout, result.stderr));
  }

  /** One daemon verb in `distro`, its answer parsed and its schema checked. */
  private async ask(wsl: string, distro: string, verb: Verb): Promise<Step<Answer>> {
    const result = await this.options.runner({ file: wsl, args: daemonArgs(distro, verb), timeoutMs: VERB_TIMEOUT_MS[verb] });

    return answerOf(result, distro, verb);
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
    const version = own ?? this.cachedVersion(distro) ?? (await this.askVersion(wsl, distro));
    this.knownVersion = version.kind === 'notRead' ? undefined : { distro, version };

    return version;
  }

  private cachedVersion(distro: string): DaemonVersion | undefined {
    return this.knownVersion?.distro === distro ? this.knownVersion.version : undefined;
  }

  private async askVersion(wsl: string, distro: string): Promise<DaemonVersion> {
    const answer = await this.ask(wsl, distro, 'version');

    return answer.ok && answer.value.verb === 'version' ? answer.value.version : { kind: 'notRead', reason: answer.ok ? 'no version' : answer.failure.kind };
  }
}

/** What one daemon call ended in: the parsed, schema-checked answer, or the failure it reads as. */
function answerOf(result: ProcessResult, distro: string, verb: Verb): Step<Answer> {
  switch (result.kind) {
    case 'timedOut':
      return fail({ kind: 'timedOut', timeoutMs: VERB_TIMEOUT_MS[verb] });
    case 'exited':
      return exitedAnswer(result, distro, verb);
    default:
      return fail(startFailure(result));
  }
}

function exitedAnswer(result: Extract<ProcessResult, { kind: 'exited' }>, distro: string, verb: Verb): Step<Answer> {
  if (result.code !== DAEMON_EXIT.ok) {
    return fail(classifyExit(result.code, result.stdout, result.stderr, distro, DAEMON_PATH));
  }
  const parsed = parseAnswer(verb, result.stdout.toString('utf8'));

  return 'kind' in parsed ? fail(parsed) : ok(parsed);
}

/** The target when the distribution may be asked (running, or the user asked to start it); "stopped" otherwise. */
function targetOf(wsl: string, distro: string, mayAsk: boolean): Step<{ wsl: string; distro: string }> {
  return mayAsk ? ok({ wsl, distro }) : fail({ kind: 'stopped', distro });
}

function notAName(distro: string): Failure {
  return { kind: 'distroRefused', distro, reason: 'not a distribution name (letters, digits, ".", "_", "-", not starting with "-")' };
}

function checkListed(distro: string, listed: readonly string[]): Step<string> {
  if (!isDistroName(distro)) {
    return fail(notAName(distro));
  }

  return listed.includes(distro) ? ok(distro) : fail({ kind: 'distroRefused', distro, reason: 'wsl.exe --list does not report it' });
}

function ownVersion(answer: Answer): DaemonVersion | undefined {
  if (answer.verb === 'version') {
    return answer.version;
  }

  return answer.verb === 'status' && answer.productVersion !== undefined ? parseDaemonVersion(answer.productVersion) : undefined;
}
