/**
 * The strict fake `wsl.exe` — a Node script the client's tests start through the SAME runner seam the real one is
 * started through (`nodeScriptRunner`: `node fakeWsl.js <the argv the client built>`; a `.cmd` cannot be spawned
 * without a shell on current Node, plan §15g M6).
 *
 * <p><b>Stricter than the real thing, never more permissive</b> (`common.generated-code-tests` §3). It answers only the
 * argv shapes the client may send, each in the encoding the real `wsl.exe` uses (measured 2026-10-03,
 * research/2026-10-03_wsl_exe_facts.md): `wsl.exe`'s own output UTF-16LE, the Linux program's UTF-8. Everything else
 * is refused with a distinct exit code and a sentence naming why — including argv the real `wsl.exe` would happily run
 * but the client must never send: `-u` / `--user` outside the root shapes, `--` (argv handed to the distro's shell),
 * anything after the binary but the four read-only verbs (so `--timer`, `--confirm`, `--manual`, `config`, `act`,
 * `collect`), a `-d` to a STOPPED distro (the real one would start the VM), and a start as anything but an absolute
 * `…\System32\wsl.exe`.</p>
 *
 * <p><b>Since E6.S2, the root shapes</b> — its own copy of `rootCall.ts`'s closed union: `-d <distro> -u root --cd /
 * --exec <daemon>` followed by EXACTLY `act <ids> --preview --json`, `act <ids> --confirm --manual --detach [--only -]
 * --json`, `act --stop <runId> --json`, `collect --detach --json` or `--version`. It REFUSES a synchronous confirm (no
 * `--detach`, plan §15j m11), stdin anywhere but a confirm's `--only -` (and a piped line that is not a 64-hex name), ids
 * outside `contracts/actions.json` ∩ the scenario daemon's `status.actions`, an op whose capability that daemon does not
 * advertise, and `--timer` / `--user` / `config` anywhere. A call's stdin is recorded in the log.</p>
 *
 * <p><b>Since E10.S1, the one config write</b> — `config/configCall.ts`'s shape, unprivileged: `-d <distro> --cd / --exec <daemon>
 * config set archive.baseFolder <v>`, where `<v>` is empty or exactly the folder the scenario's `check-base` answer accepted.
 * Every other `config` argv — another key, `config reset`, `-u root` before it — is still refused.</p>
 *
 * <p>The four verbs are written out HERE, independently of the product's `VERBS`: the fake is the oracle of the plan's
 * decision (§15f #5), so a verb added to the client without a decision added here fails rather than passing because
 * both read one list.</p>
 *
 * <p>Its scenario is a JSON file named by `WSL_CARE_FAKE_SCENARIO`; every call is appended to the scenario's `log`.
 * Its own tests: `fakeWsl.test.ts`.</p>
 */
import * as fs from 'node:fs';
import * as path from 'node:path';

import { RUN_ID_SHAPE, UTC_INSTANT_SHAPE } from '../../shared/shapes';

export interface FakeDistro {
  readonly name: string;
  readonly running: boolean;
}

export interface FakeScenario {
  readonly distros: readonly FakeDistro[];
  /** The distribution `-l -v` marks with `*`, if any. */
  readonly defaultDistro?: string;
  /** What is at `/opt/wsl-care/bin/wsl-care`: the daemon, nothing, or a build needing a newer glibc. */
  readonly binary: 'present' | 'missing' | 'oldGlibc';
  /** The folder holding `status.json`, `preview.json`, `doctor.json` (a golden set, or a test's edited copies). */
  readonly answers: string;
  /** What `--version` prints. */
  readonly version?: string;
  /** Milliseconds to wait before a daemon call answers — the hung daemon. */
  readonly delayMs?: number;
  /** The daemon exits with this code and writes this stderr instead of answering. */
  readonly daemonExit?: { readonly code: number; readonly stderr: string };
  /** How `--list --running --quiet` answers when nothing runs: nothing (exit 0), or a sentence and exit -1 (NOT measured). */
  readonly noneRunning?: 'empty' | 'message';
  /** Emulate `WSL_UTF8=1`: `wsl.exe`'s own output in UTF-8 (measured for `--list --quiet`). */
  readonly wslUtf8?: boolean;
  /** A file every call is appended to, one JSON line `{ argv, file }`. */
  readonly log?: string;
  /**
   * A `-d` to a stopped distribution STARTS it, as the real `wsl.exe` does — for the one call the user asks for
   * ("Start WSL and check", `startIfStopped`). Off by default: then such a `-d` is refused (95), because no other
   * call may start the VM. When on, the fake rewrites its scenario file so the distribution runs from then on.
   */
  readonly startable?: boolean;
  /** E6.S2: how the root calls answer — the detach's `result`, a root exit, the root check's exit. */
  readonly root?: FakeRoot;
  /** E6.S3: the file (in `answers`) `runs show <runId> --json` answers, with the asked run id; `runs-show-unknown.json` by default. */
  readonly runsShow?: string;
  /** E6.S3: the file (in `answers`) `runs --from --to --json` answers; `runs-local-day.json` by default. */
  readonly runs?: string;
  /** E6.S4: the file (in `answers`) `logs --from --to --json` answers; `logs-local-day.json` by default. */
  readonly logs?: string;
  /** E10.S1: the file (in `answers`) `archive check-base <path> --json` answers; `archive-check-base.json` by default. */
  readonly checkBase?: string;
  /** E10.S1: `config set archive.baseFolder <v>` exits with this code and stderr instead of writing. */
  readonly configExit?: { readonly code: number; readonly stderr: string };
}

/** The root calls' scripted answers (E6.S2); absent, every root call of the closed set answers as the goldens do. */
export interface FakeRoot {
  /** The `result` a detach answers (`act-detach-accepted.json` with this result): `accepted` by default. */
  readonly detach?: string;
  /** Every root call but the root check exits with this code and stderr. */
  readonly exit?: { readonly code: number; readonly stderr: string };
  /** The root check (`--version` as root) exits with this code and stderr. */
  readonly checkExit?: { readonly code: number; readonly stderr: string };
  /** Milliseconds before a root call answers. */
  readonly delayMs?: number;
}

/** Exit codes the fake refuses with — none of them is a code the daemon or `wsl.exe` uses. */
export const FAKE_EXIT = {
  /** Started as something other than an absolute `…\System32\wsl.exe`. */
  wrongFile: 96,
  /** No scenario: a fake started outside a test refuses to answer anything. */
  noScenario: 97,
  /** An argv the client must never send. */
  refused: 98,
  /** A `-d` to a stopped distro — the real `wsl.exe` would START it. */
  wouldStart: 95,
} as const;

export const DAEMON = '/opt/wsl-care/bin/wsl-care';

/** The four daemon argv tails E5 may send (plan §15f #5) — the fake's own copy, see the header. */
export const ALLOWED_TAILS: readonly (readonly string[])[] = [
  ['status', '--json'],
  ['preview', '--all', '--json'],
  ['doctor', '--json'],
  ['--version'],
];

/** The words that name a privileged or mutating path; a refusal says which one it saw. */
const FORBIDDEN_WORDS = ['-u', '--user', '--timer', '--confirm', '--manual', 'config', 'act', 'collect', '--', '-e', '--shell-type', '--system', '--shutdown', '--terminate', '-t', '--unregister'];

/** The measured `execvpe` refusal of a missing binary (2026-10-03, exit 1, UTF-8 on stderr). */
export function missingBinaryStderr(pid: number): string {
  return `<3>WSL (${pid} - Relay) ERROR: CreateProcessCommon:818: execvpe(${DAEMON}) failed: No such file or directory\n`;
}

/** The dynamic loader's refusal of a binary needing a newer glibc — its documented shape, NOT measured here. */
export const OLD_GLIBC_STDERR = DAEMON + ': /lib/x86_64-linux-gnu/libc.so.6: version `GLIBC_2.38\' not found (required by ' + DAEMON + ')\n';

/** The measured answer of `wsl.exe -d <unknown>` (2026-10-03): UTF-16LE on stdout, exit -1. */
export const NO_SUCH_DISTRO = 'There is no distribution with the supplied name.\r\nError code: Wsl/Service/WSL_E_DISTRO_NOT_FOUND\r\n';

interface Reply {
  readonly code: number;
  readonly stdout?: Buffer;
  readonly stderr?: string;
  readonly delayMs?: number;
}

function wslText(scenario: FakeScenario, text: string): Buffer {
  return Buffer.from(text, scenario.wslUtf8 === true ? 'utf8' : 'utf16le');
}

function refuse(why: string, argv: readonly string[]): Reply {
  return { code: FAKE_EXIT.refused, stderr: `fake wsl: refused ${JSON.stringify(argv)}: ${why}\n` };
}

function verboseTable(scenario: FakeScenario): string {
  const head = `  ${'NAME'.padEnd(18)}${'STATE'.padEnd(16)}VERSION\r\n`;
  const rows = scenario.distros.map((d) => `${d.name === scenario.defaultDistro ? '*' : ' '} ${d.name.padEnd(18)}${(d.running ? 'Running' : 'Stopped').padEnd(16)}2\r\n`);

  return head + rows.join('');
}

function runningList(scenario: FakeScenario): Reply {
  const running = scenario.distros.filter((d) => d.running).map((d) => `${d.name}\r\n`);
  if (running.length > 0 || scenario.noneRunning !== 'message') {
    return { code: 0, stdout: wslText(scenario, running.join('')) };
  }

  return { code: -1, stdout: wslText(scenario, 'There are no running distributions.\r\n') };
}

function listReply(scenario: FakeScenario, argv: readonly string[]): Reply | undefined {
  switch (argv.join(' ')) {
    case '--list --quiet':
      return { code: 0, stdout: wslText(scenario, scenario.distros.map((d) => `${d.name}\r\n`).join('')) };
    case '-l -v':
      return { code: 0, stdout: wslText(scenario, verboseTable(scenario)) };
    case '--list --running --quiet':
      return runningList(scenario);
    default:
      return undefined;
  }
}

function sameTail(tail: readonly string[], allowed: readonly string[]): boolean {
  return tail.length === allowed.length && tail.every((word, i) => word === allowed[i]);
}

/** What the binary answers before it can run at all: missing, or built for a newer glibc. */
function binaryReply(scenario: FakeScenario): Reply | undefined {
  if (scenario.binary === 'missing') {
    return { code: 1, stderr: missingBinaryStderr(4000 + process.pid % 1000) };
  }

  return scenario.binary === 'oldGlibc' ? { code: 1, stderr: OLD_GLIBC_STDERR } : undefined;
}

function daemonAnswer(scenario: FakeScenario, tail: readonly string[]): Reply {
  const binary = binaryReply(scenario);
  if (binary !== undefined) {
    return binary;
  }
  if (scenario.daemonExit !== undefined) {
    return { code: scenario.daemonExit.code, stderr: scenario.daemonExit.stderr, ...delay(scenario) };
  }
  if (tail[0] === '--version') {
    return { code: 0, stdout: Buffer.from(`${scenario.version ?? '0.1.0'}\n`, 'utf8'), ...delay(scenario) };
  }

  return { code: 0, stdout: fs.readFileSync(path.join(scenario.answers, `${tail[0]}.json`)), ...delay(scenario) };
}

function delay(scenario: FakeScenario): { delayMs?: number } {
  return scenario.delayMs === undefined ? {} : { delayMs: scenario.delayMs };
}

function daemonReply(scenario: FakeScenario, argv: readonly string[]): Reply {
  const [d, , cd, slash, exec, binary, ...tail] = argv;
  if (d !== '-d' || cd !== '--cd' || slash !== '/' || exec !== '--exec' || binary !== DAEMON) {
    return refuse('not the one shape the client sends: -d <distro> --cd / --exec /opt/wsl-care/bin/wsl-care <verb>', argv);
  }
  const archive = archiveReadOf(tail);
  if (archive !== undefined) {
    return distroReply(scenario, argv) ?? { code: 0, stdout: fs.readFileSync(path.join(scenario.answers, archive(scenario))), ...delay(scenario) };
  }
  const runRead = runReadOf(tail);
  if (runRead !== undefined) {
    return distroReply(scenario, argv) ?? runReadAnswer(scenario, runRead);
  }
  if (!ALLOWED_TAILS.some((allowed) => sameTail(tail, allowed))) {
    return refuse(`outside the four read-only verbs and the three run reads: ${JSON.stringify(tail)}`, argv);
  }

  return distroReply(scenario, argv) ?? daemonAnswer(scenario, tail);
}

// ---- E10.S1: the three archive reads — the fake's OWN copy of their shapes ----

/** The daemon's own path rule for check-base (`ArchiveArguments.IsPathArgument`), and the key's limit. */
function isPathValue(value: string | undefined): value is string {
  return value !== undefined && value.length > 0 && value.length <= 1024 && !value.startsWith('-') && ![...value].some((c) => c.charCodeAt(0) < 32 || c.charCodeAt(0) === 127);
}

/** `archive status --json`, `archive preview --json`, `archive check-base <path> --json` — the answer file each reads — or not one. */
function archiveReadOf(tail: readonly string[]): ((scenario: FakeScenario) => string) | undefined {
  const [verb, sub, third, fourth, ...rest] = tail;
  if (verb !== 'archive' || rest.length > 0) {
    return undefined;
  }
  if (third === '--json' && fourth === undefined && (sub === 'status' || sub === 'preview')) {
    return () => `archive-${sub}.json`;
  }

  return sub === 'check-base' && isPathValue(third) && fourth === '--json' ? (scenario) => scenario.checkBase ?? 'archive-check-base.json' : undefined;
}

// ---- E10.S1: the ONE config write — the fake's OWN copy of its shape ----

/** The ten words of the one config call the extension sends: `-d <distro> --cd / --exec <daemon> config set archive.baseFolder <v>`. */
function configSetOf(argv: readonly string[]): { readonly value: string } | undefined {
  const [d, , cd, slash, exec, binary, config, set, key, value, ...rest] = argv;
  const shaped = d === '-d' && cd === '--cd' && slash === '/' && exec === '--exec' && binary === DAEMON && rest.length === 0;

  return shaped && config === 'config' && set === 'set' && key === 'archive.baseFolder' && value !== undefined ? { value } : undefined;
}

/**
 * The value is written only when it is empty (*Stop archiving*) or EXACTLY the folder the scenario's own `check-base` answer
 * accepted — stricter than the daemon, which judges any folder again: a value the extension never had judged is refused.
 */
function configReply(scenario: FakeScenario, config: { readonly value: string }, argv: readonly string[]): Reply {
  const report = readJson(path.join(scenario.answers, scenario.checkBase ?? 'archive-check-base.json'));
  const judged = report.accepted === true && report.folder === config.value;
  if (config.value !== '' && !judged) {
    return refuse(`config set archive.baseFolder with a value the check-base answer did not accept: ${JSON.stringify(config.value)}`, argv);
  }
  if (scenario.configExit !== undefined) {
    return { code: scenario.configExit.code, stderr: scenario.configExit.stderr, ...delay(scenario) };
  }

  return { code: 0, stdout: Buffer.from(`archive.baseFolder = ${config.value === '' ? '(none)' : config.value} (user)\n`, 'utf8'), ...delay(scenario) };
}

// ---- E6.S3 / E6.S4: the three run reads — the fake's OWN copy of their shapes ----

/** The one instant shape the client sends, with its offset spelt (the daemon's `LogPeriod.ParseInstants` takes more; the client sends this). */
const INSTANT = UTC_INSTANT_SHAPE;

/** The two verbs that take the instant window — `runs` (E6.S3) and `logs` (E6.S4) — and the answer file each defaults to. */
const WINDOW_READS = {
  runs: (scenario: FakeScenario) => scenario.runs ?? 'runs-local-day.json',
  logs: (scenario: FakeScenario) => scenario.logs ?? 'logs-local-day.json',
} as const;

type WindowVerb = keyof typeof WINDOW_READS;

type RunReadShape = { readonly read: 'runsShow'; readonly runId: string } | { readonly read: WindowVerb };

function isWindowVerb(verb: string | undefined): verb is WindowVerb {
  return verb !== undefined && Object.hasOwn(WINDOW_READS, verb);
}

/** `runs show <runId> --json` — exactly four words, the run id of the daemon's spelling. */
function showOf(runId: string | undefined, json: string | undefined, more: string | undefined): RunReadShape | undefined {
  return json === '--json' && more === undefined && RUN_ID.test(runId ?? '') ? { read: 'runsShow', runId: runId ?? '' } : undefined;
}

/**
 * `runs show <runId> --json`, or `runs` / `logs --from <instant> --to <instant> --json` ending after it starts — or not a run
 * read: `logs --period`, `--detail` and `--action` are never sent (the Logs page reads objects through `runs show`).
 */
function runReadOf(tail: readonly string[]): RunReadShape | undefined {
  const [verb, first, second, third, fourth, fifth, ...rest] = tail;
  if (rest.length > 0) {
    return undefined;
  }
  if (verb === 'runs' && first === 'show') {
    return showOf(second, third, fourth);
  }

  return isWindowVerb(verb) && first === '--from' && third === '--to' && fifth === '--json' && validWindow(second, fourth) ? { read: verb } : undefined;
}

function validWindow(from: string | undefined, to: string | undefined): boolean {
  return from !== undefined && to !== undefined && INSTANT.test(from) && INSTANT.test(to) && Date.parse(to) > Date.parse(from);
}

/** The scenario's answer file, with the asked run id written into it (the top level and its run line). */
function runReadAnswer(scenario: FakeScenario, shape: RunReadShape): Reply {
  if (shape.read !== 'runsShow') {
    return { code: 0, stdout: fs.readFileSync(path.join(scenario.answers, WINDOW_READS[shape.read](scenario))), ...delay(scenario) };
  }
  const body = readJson(path.join(scenario.answers, scenario.runsShow ?? 'runs-show-unknown.json'));
  const run = typeof body.run === 'object' && body.run !== null ? { run: { ...(body.run as Record<string, unknown>), runId: shape.runId } } : {};

  return { code: 0, stdout: Buffer.from(JSON.stringify({ ...body, runId: shape.runId, ...run }), 'utf8'), ...delay(scenario) };
}

/** The distribution of a `-d` call, as wsl.exe treats it — or nothing to say, when the call may go on. */
function distroReply(scenario: FakeScenario, argv: readonly string[]): Reply | undefined {
  const distro = argv[1];
  if (distro === undefined || distro.startsWith('-')) {
    // The real wsl.exe would read it as an option; a listed name of any other shape is taken as it is (§15h #4).
    return refuse(`a -d value starting with "-" is read by wsl.exe as an option: ${JSON.stringify(distro)}`, argv);
  }
  const known = scenario.distros.find((x) => x.name === distro);
  if (known === undefined) {
    return { code: -1, stdout: wslText(scenario, NO_SUCH_DISTRO) };
  }
  if (!known.running && scenario.startable !== true) {
    return { code: FAKE_EXIT.wouldStart, stderr: `fake wsl: -d ${distro} would START the stopped distribution\n` };
  }

  return undefined;
}

/** The fake's whole decision, pure: what `argv` (with `stdin`) answers under `scenario`. Exported for its own tests. */
export function decide(scenario: FakeScenario, argv: readonly string[], stdin: Buffer = Buffer.alloc(0), wslenv = false): Reply {
  if (isRootCall(argv)) {
    // E6.S2 review S2: the Windows user's WSLENV must never reach a root call (it would choose what the daemon's env holds).
    return wslenv ? refuse('WSLENV reached a root call — the environment of the root daemon must not be steered from Windows', argv) : rootReply(scenario, argv, stdin);
  }
  if (stdin.length > 0) {
    return refuse('stdin outside --only -: only a root confirm of A4 pipes a list', argv);
  }
  const config = configSetOf(argv);
  if (config !== undefined) {
    return distroReply(scenario, argv) ?? configReply(scenario, config, argv);
  }
  const forbidden = argv.find((word) => FORBIDDEN_WORDS.includes(word));
  if (forbidden !== undefined) {
    return refuse(`"${forbidden}" is never sent outside the root shapes`, argv);
  }

  return listReply(scenario, argv) ?? daemonReply(scenario, argv);
}

/** Whether the runner started this fake as an absolute `…\System32\wsl.exe` (the client's one launcher). */
export function isSystemWsl(file: string | undefined): boolean {
  return file !== undefined && /^[A-Za-z]:\\(?:[^\\/:*?"<>|]+\\)*System32\\wsl\.exe$/i.test(file);
}

function emit(reply: Reply): void {
  const finish = (): void => {
    if (reply.stderr !== undefined) {
      process.stderr.write(reply.stderr);
    }
    process.stdout.write(reply.stdout ?? Buffer.alloc(0), () => process.exit(reply.code));
  };
  if (reply.delayMs === undefined) {
    finish();
  } else {
    setTimeout(finish, reply.delayMs);
  }
}

/** Everything on stdin, to its end — nothing when the runner gave none (its stdin is then closed from the start). */
function readStdin(): Promise<Buffer> {
  return new Promise((resolve) => {
    const chunks: Buffer[] = [];
    process.stdin.on('data', (chunk: Buffer) => chunks.push(chunk));
    process.stdin.on('end', () => resolve(Buffer.concat(chunks)));
    process.stdin.on('error', () => resolve(Buffer.concat(chunks)));
  });
}

async function main(): Promise<void> {
  const argv = process.argv.slice(2);
  const stdin = await readStdin();
  const scenarioFile = process.env.WSL_CARE_FAKE_SCENARIO;
  if (scenarioFile === undefined || scenarioFile === '') {
    emit({ code: FAKE_EXIT.noScenario, stderr: 'fake wsl: no scenario (WSL_CARE_FAKE_SCENARIO) — refusing to answer\n' });
    return;
  }
  const scenario = JSON.parse(fs.readFileSync(scenarioFile, 'utf8')) as FakeScenario;
  const file = process.env.WSL_CARE_FAKE_REQUESTED_FILE;
  if (scenario.log !== undefined) {
    fs.appendFileSync(scenario.log, `${JSON.stringify({ argv, file: file ?? null, ...(stdin.length > 0 ? { stdin: stdin.toString('utf8') } : {}) })}\n`);
  }
  if (!isSystemWsl(file)) {
    emit({ code: FAKE_EXIT.wrongFile, stderr: `fake wsl: started as ${JSON.stringify(file)}, not as an absolute ...\\System32\\wsl.exe\n` });
    return;
  }
  const reply = decide(scenario, argv, stdin, process.env.WSLENV !== undefined);
  if (reply.code === 0) {
    markStarted(scenarioFile, scenario, argv);
  }
  emit(reply);
}

/** A `startable` scenario's answered `-d` to a stopped distribution started it: written back, so it runs from now on. */
function markStarted(file: string, scenario: FakeScenario, argv: readonly string[]): void {
  const stopped = argv[0] === '-d' ? scenario.distros.find((d) => d.name === argv[1] && !d.running) : undefined;
  if (stopped === undefined) {
    return;
  }
  const distros = scenario.distros.map((d) => (d === stopped ? { ...d, running: true } : d));
  fs.writeFileSync(file, JSON.stringify({ ...scenario, distros }));
}

if (require.main === module) {
  void main();
}

// ---- E6.S2: the root shapes — the fake's OWN copy of the closed union (plan §15j M1 / M2 / m11) ----

/** What follows `-d <distro>` in every root call, word for word. */
export const ROOT_PREFIX: readonly string[] = ['-u', 'root', '--cd', '/', '--exec', DAEMON];

/** Words no root call carries anywhere: the timer's mark, another user, the settings verb. */
const NEVER_IN_ROOT = ['--timer', '--user', 'config'];

const VOLUME_NAME = /^[0-9a-f]{64}$/;
const RUN_ID = RUN_ID_SHAPE;

/** The daemon's published registry (`contracts/actions.json`), read from the repository the compiled fake lives in. */
const CONTRACT_ACTIONS = path.resolve(__dirname, '..', '..', '..', '..', 'contracts', 'actions.json');

interface RootShape {
  readonly op: 'preview' | 'confirm' | 'stop' | 'collect' | 'check';
  readonly ids: readonly string[];
  readonly piped: boolean;
  readonly capabilities: readonly string[];
  readonly runId?: string;
}

function isRootCall(argv: readonly string[]): boolean {
  return argv[0] === '-d' && sameTail(argv.slice(2, 2 + ROOT_PREFIX.length), ROOT_PREFIX);
}

function actShape(tail: readonly string[]): RootShape | string {
  const [, ids = '', ...flags] = tail;
  const list = ids.split(',');
  if (sameTail(flags, ['--preview', '--json'])) {
    return { op: 'preview', ids: list, piped: false, capabilities: ['act.shownList'] };
  }
  if (flags.includes('--confirm') && !flags.includes('--detach')) {
    return 'a synchronous confirm (no --detach) is never sent — a confirm runs in its own unit';
  }
  if (sameTail(flags, ['--confirm', '--manual', '--detach', '--json'])) {
    return { op: 'confirm', ids: list, piped: false, capabilities: ['act.detach'] };
  }
  if (sameTail(flags, ['--confirm', '--manual', '--detach', '--only', '-', '--json'])) {
    return list.includes('A4') ? { op: 'confirm', ids: list, piped: true, capabilities: ['act.detach', 'act.onlyStdin'] } : '--only - names the volumes A4 showed; A4 is not among the ids';
  }

  return `not one of the root shapes: ${JSON.stringify(tail)}`;
}

/** The tail after the root prefix, read against the five shapes — or why it is none of them. */
/**
 * E10.S1b, the fake's own copy of A13's rule: A13 is archived on its own, never beside another id, and its preview needs
 * `archive.preview` advertised, its confirm `archive.run` — what `cleanupController.ts` gates on.
 */
function withArchive(shape: RootShape | string): RootShape | string {
  if (typeof shape === 'string' || !shape.ids.includes('A13')) {
    return shape;
  }
  if (shape.ids.length > 1) {
    return `A13 is archived on its own, never beside another id: ${JSON.stringify(shape.ids)}`;
  }

  return { ...shape, capabilities: [...shape.capabilities, shape.op === 'preview' ? 'archive.preview' : 'archive.run'] };
}

function rootShape(tail: readonly string[]): RootShape | string {
  if (sameTail(tail, ['--version'])) {
    return { op: 'check', ids: [], piped: false, capabilities: [] };
  }
  if (sameTail(tail, ['collect', '--detach', '--json'])) {
    return { op: 'collect', ids: [], piped: false, capabilities: ['act.detach'] };
  }
  if (tail.length === 4 && tail[0] === 'act' && tail[1] === '--stop' && tail[3] === '--json') {
    return RUN_ID.test(tail[2] ?? '') ? { op: 'stop', ids: [], piped: false, capabilities: ['act.stop'], runId: tail[2] ?? '' } : `not a run id the daemon writes: ${JSON.stringify(tail[2])}`;
  }

  return tail[0] === 'act' && !(tail[1] ?? '').startsWith('-') ? withArchive(actShape(tail)) : `not one of the root shapes: ${JSON.stringify(tail)}`;
}

function readJson(file: string): Record<string, unknown> {
  return JSON.parse(fs.readFileSync(file, 'utf8')) as Record<string, unknown>;
}

function strings(value: unknown): string[] {
  return Array.isArray(value) ? value.filter((v): v is string => typeof v === 'string') : [];
}

/** Every line of a piped list one 64-hex name, every line ended — the empty list legal (A4 bound to nothing). */
function pipedProblem(stdin: Buffer): string | undefined {
  const text = stdin.toString('utf8');
  if (text.length > 0 && !text.endsWith('\n')) {
    return 'the piped list does not end with a newline';
  }
  const bad = text.split('\n').slice(0, -1).findIndex((line) => !VOLUME_NAME.test(line));

  return bad >= 0 ? `line ${bad + 1} of the piped list is not a 64-hex volume name` : undefined;
}

/** The intersection the extension may act on: the daemon's registry (the contract) ∩ what this daemon reports. */
function idsProblem(scenario: FakeScenario, ids: readonly string[]): string | undefined {
  const registry = strings(readJson(CONTRACT_ACTIONS).ids);
  const reported = strings(readJson(path.join(scenario.answers, 'status.json')).actions);
  const outside = ids.filter((id) => !registry.includes(id) || !reported.includes(id));

  return outside.length > 0 ? `ids outside the intersection of the registry and status.actions: ${JSON.stringify(outside)}` : undefined;
}

function capabilityProblem(scenario: FakeScenario, needed: readonly string[]): string | undefined {
  const advertised = strings(readJson(path.join(scenario.answers, 'status.json')).capabilities);
  const missing = needed.filter((c) => !advertised.includes(c));

  return missing.length > 0 ? `the daemon does not advertise ${JSON.stringify(missing)}` : undefined;
}

function shapeProblem(scenario: FakeScenario, shape: RootShape, stdin: Buffer): string | undefined {
  if (!shape.piped && stdin.length > 0) {
    return 'stdin outside --only -: only a confirm of A4 pipes a list';
  }

  return (shape.piped ? pipedProblem(stdin) : undefined) ?? (shape.ids.length > 0 ? idsProblem(scenario, shape.ids) : undefined) ?? capabilityProblem(scenario, shape.capabilities);
}

function handOff(scenario: FakeScenario, kind: 'act' | 'collect'): Buffer {
  const accepted = readJson(path.join(scenario.answers, 'act-detach-accepted.json'));

  return Buffer.from(JSON.stringify({ ...accepted, result: scenario.root?.detach ?? 'accepted', kind }), 'utf8');
}

function previewAnswer(scenario: FakeScenario, ids: readonly string[]): Buffer {
  const golden = readJson(path.join(scenario.answers, 'act-a4-preview.json'));
  const entries = Array.isArray(golden.actions) ? (golden.actions as Record<string, unknown>[]) : [];
  const actions = ids.map((id) => (id === 'A13' ? readJson(path.join(scenario.answers, 'act-a13-preview-action.json')) : undefined) ?? entries.find((a) => a.id === id) ?? { id, summary: '', status: 'previewed', reason: '', preview: { available: true, count: 0, bytes: 0 } });

  return Buffer.from(JSON.stringify({ ...golden, actions }), 'utf8');
}

function rootAnswer(scenario: FakeScenario, shape: RootShape): Reply {
  const scripted = shape.op === 'check' ? scenario.root?.checkExit : scenario.root?.exit;
  const wait = scenario.root?.delayMs === undefined ? {} : { delayMs: scenario.root.delayMs };
  if (scripted !== undefined) {
    return { code: scripted.code, stderr: scripted.stderr, ...wait };
  }
  const answers: Record<RootShape['op'], () => Buffer> = {
    check: () => Buffer.from(`${scenario.version ?? '0.1.0'}\n`, 'utf8'),
    preview: () => previewAnswer(scenario, shape.ids),
    confirm: () => handOff(scenario, 'act'),
    collect: () => handOff(scenario, 'collect'),
    stop: () => Buffer.from(JSON.stringify({ schemaVersion: 1, result: 'stopping', kind: 'act', runId: shape.runId, unit: `wsl-care-act@${shape.runId ?? ''}.service`, productVersion: 'unknown' }), 'utf8'),
  };

  return { code: 0, stdout: answers[shape.op](), ...wait };
}

/** A root call: the distribution as every `-d`, then the closed shapes, stdin, the intersection and the capabilities. */
function rootReply(scenario: FakeScenario, argv: readonly string[], stdin: Buffer): Reply {
  const tail = argv.slice(2 + ROOT_PREFIX.length);
  const never = tail.find((word) => NEVER_IN_ROOT.includes(word));
  if (never !== undefined) {
    return refuse(`"${never}" is never part of a root call`, argv);
  }
  const shape = rootShape(tail);
  const problem = typeof shape === 'string' ? shape : shapeProblem(scenario, shape, stdin);
  if (problem !== undefined || typeof shape === 'string') {
    return refuse(problem ?? 'unreachable', argv);
  }

  return distroReply(scenario, argv) ?? binaryReply(scenario) ?? rootAnswer(scenario, shape);
}

/** The shapes the fake checks run ids and instants against — the shared module's own objects (`sharedShapes.test.ts`). */
export const FAKE_SHAPES = { runId: RUN_ID, instant: INSTANT } as const;
