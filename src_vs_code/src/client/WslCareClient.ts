import type { ProcessResult, Runner } from '../process/runner';
import { DISTRO_NAME, distroSettingText, isDistroName, parseDefaultDistro, parseQuietList } from '../wsl/distros';
import { wslExecutable } from '../wsl/wslExecutable';
import { decodeWslText } from '../wsl/wslText';
import { DAEMON_EXIT } from './exitCodes';
import { classifyExit, wslRefusal } from './failures';
import { parseAnswer, parseDaemonVersion, versionRefusal } from './handshake';
import type { Answer, DaemonVersion, Failure, VerbOutcome } from './outcome';
import { PREVIEW_CONTAINER_ASSUMPTION, VERB_TIMEOUT_MS, VERBS, type Verb } from './verbs';

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
 * <p>It also builds the one other `wsl.exe` argv the extension has: `-d <distro> --cd ~` for the terminal *Install daemon*
 * opens (`terminalTarget`, E5.S3) — built here so this stays the only module that spells a `wsl.exe` argument; the
 * client never starts that terminal itself.</p>
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
  /** The `--version` call in flight per distribution — `preview` and `doctor` asked at once share it (E5 code round). */
  private readonly versionsInFlight = new Map<string, Promise<DaemonVersion>>();
  private knownVersion: { readonly distro: string; readonly version: DaemonVersion } | undefined;
  /** The running-container count of the newest `status` answer — what a timed-out `preview` is judged against. */
  private knownContainers: { readonly distro: string; readonly count: number } | undefined;

  constructor(private readonly options: ClientOptions) {}

  /**
   * Run `verb`; a run of the same verb for the same `wslCare.distro` value already in flight is shared, never doubled —
   * except that a run the user asked to START a stopped distribution is never folded into a poll (which would answer
   * "stopped"), so the two are keyed apart. The setting is read ONCE, here, and is part of the key: a call made after
   * the setting changed never shares the call still running for the previous distribution, whose answer would be shown
   * under the new one (retro review of PR #9, `distroSwitch.test.ts`).
   */
  run(verb: Verb, options: RunOptions = {}): Promise<VerbOutcome> {
    const startIfStopped = options.startIfStopped === true;
    const setting = distroSettingText(this.options.distroSetting());
    const key = `${setting}|${verb}${startIfStopped ? '+start' : ''}`;
    const running = this.inFlight.get(key);
    if (running !== undefined) {
      return running;
    }
    const started = this.runOnce(verb, startIfStopped, setting).finally(() => this.inFlight.delete(key));
    this.inFlight.set(key, started);

    return started;
  }

  /**
   * The terminal *Install daemon* may open, or why not — the distribution validated by its shape BEFORE anything starts
   * and checked against `wsl.exe --list` (which starts no VM) before the terminal exists. Not asked whether it runs: the
   * person confirmed opening a shell there, which starts it.
   */
  async terminalTarget(): Promise<TerminalTarget | Failure> {
    const wsl = this.launcher();
    if (!wsl.ok) {
      return wsl.failure;
    }
    const distro = await this.listedDistro(wsl.value, distroSettingText(this.options.distroSetting()));

    return distro.ok ? { kind: 'terminal', shellPath: wsl.value, shellArgs: ['-d', distro.value, '--cd', '~'], distro: distro.value } : distro.failure;
  }

  private async runOnce(verb: Verb, startIfStopped: boolean, setting: string): Promise<VerbOutcome> {
    const target = await this.target(startIfStopped, setting);
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
    this.knownContainers = count === undefined ? this.knownContainers : { distro, count };
  }

  /**
   * A `preview` that timed out on a machine with more running containers than its ceiling assumes (one
   * `container inspect` batch, `verbs.ts`) says so with the count, instead of a bare timeout (§15h #1). The count is
   * `status`'s RUNNING containers — a lower bound of what `preview` inspects, so a machine with many STOPPED containers
   * still reads as a plain timeout.
   */
  private explained(verb: Verb, distro: string, failure: Failure): Failure {
    return verb === 'preview' && failure.kind === 'timedOut' ? crowdedPreview(this.containersIn(distro), failure) : failure;
  }

  private containersIn(distro: string): number {
    return this.knownContainers?.distro === distro ? this.knownContainers.count : 0;
  }

  /**
   * Where to run: the launcher and a validated, listed, RUNNING distribution — or why not. With `startIfStopped` (the
   * user's "Start WSL and check") a listed distribution that is not running is still the target: the `-d` call that
   * follows starts it, because the user asked for exactly that. Every other check stands.
   */
  private async target(startIfStopped: boolean, setting: string): Promise<Step<{ wsl: string; distro: string }>> {
    const wsl = this.launcher();
    if (!wsl.ok) {
      return wsl;
    }
    const distro = await this.listedDistro(wsl.value, setting);
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

  /** One `--version` per distribution at a time: a second asker (the other of `preview` / `doctor`) shares the call in
   * flight instead of starting its own — the "one call per verb in flight" rule, for the verb `run` does not route. */
  private askVersion(wsl: string, distro: string): Promise<DaemonVersion> {
    const running = this.versionsInFlight.get(distro);
    if (running !== undefined) {
      return running;
    }
    const started = this.readVersion(wsl, distro).finally(() => this.versionsInFlight.delete(distro));
    this.versionsInFlight.set(distro, started);

    return started;
  }

  private async readVersion(wsl: string, distro: string): Promise<DaemonVersion> {
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

/** `status`'s `vm.containers.count`, when it answered one. */
function containerCount(body: Readonly<Record<string, unknown>>): number | undefined {
  const count = ['vm', 'containers', 'count'].reduce<unknown>((value, key) => fieldOf(value, key), body);

  return Number.isInteger(count) ? (count as number) : undefined;
}

function fieldOf(value: unknown, key: string): unknown {
  return typeof value === 'object' && value !== null ? (value as Readonly<Record<string, unknown>>)[key] : undefined;
}

/** A timed-out preview with more running containers than its ceiling assumes, said with the count. */
function crowdedPreview(containers: number, failure: Failure): Failure {
  return containers > PREVIEW_CONTAINER_ASSUMPTION ? { kind: 'previewTooManyContainers', containers, timeoutMs: VERB_TIMEOUT_MS.preview } : failure;
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

function ownVersion(answer: Answer): DaemonVersion | undefined {
  if (answer.verb === 'version') {
    return answer.version;
  }

  return answer.verb === 'status' && answer.productVersion !== undefined ? parseDaemonVersion(answer.productVersion) : undefined;
}
