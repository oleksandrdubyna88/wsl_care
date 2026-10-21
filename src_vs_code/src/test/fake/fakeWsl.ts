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
 * <p><b>Since WR-E1, the Windows exes</b> — the bundled one (`bundledExe`) and the staged copy (`stagedExe`), each started as EXACTLY the
 * scenario's absolute path: `--version` (bundled), `tasks stage --json` and `tasks stage --remove --json` (bundled only), `tasks render
 * <watch|archive> --json` (staged only) and the five Windows run reads (both). Everything else — `tasks run`, a `--timer`, a missing
 * `--json`, the right argv on the wrong exe — is refused (98).</p>
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
import { A20_PREVIEW_ACTION, pipedEntries, RESTORE_CAPABILITIES, restoreOpOf, restorePreviewLine, scenarioList } from './fakeRestore';

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
   * E7.S5a: the bundled Windows daemon's absolute path (`<extension>\bin\wsl-care.exe`) — started as EXACTLY this file, the fake
   * answers `--version` with {@link bundledVersion} and (E19.S2b) `status --json`, and refuses any other argv; any other non-`wsl.exe` file stays refused.
   */
  readonly bundledExe?: string;
  /** What the bundled exe's `--version` prints. */
  readonly bundledVersion?: string;
  /** E19.S2b: the bundled exe's `status --json` exits with this code and stderr instead of answering. */
  /** E19.S2b review: milliseconds before the bundled exe's `status --json` answers — the exe that hangs, killed by the runner's ceiling. */
  readonly bundledStatusDelayMs?: number;
  readonly bundledStatusExit?: { readonly code: number; readonly stderr: string };
  /**
   * WR-E1: the STAGED copy's absolute path (`<stagingRoot><digest>wsl-care.exe`) — started as EXACTLY this file; the fake answers
   * `tasks render <watch|archive> --json` and the run reads for it and refuses any other argv. Another path stays refused.
   */
  readonly stagedExe?: string;
  /** WR-E1: the `tasks` answers of the Windows exes (bundled and staged); absent, every `tasks` argv is refused. */
  readonly tasks?: FakeTasks;
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
  /** E10.S2a: the file (in `answers`) `archive list --json` answers; `archive-list.json` by default. */
  readonly archiveList?: string;
  /** E10.S2a own review #1: the exit `archive list` answers with — the daemon exits 1 AFTER its answer for every outcome but done / no-base. */
  readonly archiveListExit?: number;
  /** E10.S1: `config set archive.baseFolder <v>` exits with this code and stderr instead of writing. */
  readonly configExit?: { readonly code: number; readonly stderr: string };
}

/** One scripted answer of a `tasks` call: the fixture file it prints (in {@link FakeTasks.folder}), its exit code, and its stderr. */
export interface FakeTaskAnswer {
  readonly file?: string;
  readonly code: number;
  readonly stderr?: string;
}

/**
 * WR-E1: how the Windows exes answer `tasks`. The render answers are the daemon's own golden (`contracts/golden/windows-tasks-render.json`,
 * written by the daemon's contract tests), picked by the golden entry's `name`; the stage answers are fixtures written from the daemon's
 * `StageReport` / `StageRemoveReport` records (no golden exists for them yet).
 */
export interface FakeTasks {
  /** The folder of the stage fixtures. */
  readonly folder: string;
  /** The render golden's file. */
  readonly renderGolden: string;
  /** `tasks render`: the golden entry each task answers (default: the three "from the staged copy" entries), or a scripted exit (2: wrong side, 78: configuration in error). */
  readonly render?: { readonly watch?: string; readonly archive?: string; readonly cleanup?: string; readonly exit?: FakeTaskAnswer };
  /** `tasks stage --json`; default `stage-staged.json`, exit 0. */
  readonly stage?: FakeTaskAnswer;
  /** `tasks stage --remove --json`; default `stage-remove-removed.json`, exit 0. */
  readonly remove?: FakeTaskAnswer;
  /** WR-T3: `tasks preview cleanup --json` (the STAGED copy's only); default `preview-cleanup.json`, exit 0. */
  readonly preview?: FakeTaskAnswer;
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
    const code = tail[1] === 'list' ? (scenario.archiveListExit ?? 0) : 0;
    return distroReply(scenario, argv) ?? { code, stdout: fs.readFileSync(path.join(scenario.answers, archive(scenario))), ...delay(scenario) };
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
  // E10.S2a: the Archive page's list — `archive list --json` exactly; no filter is ever sent.
  if (sub === 'list' && third === '--json' && fourth === undefined) {
    return (scenario) => scenario.archiveList ?? 'archive-list.json';
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

// ---- E7.S5a / WR-E1: the Windows exes — the bundled one and the staged copy ----

/** Which of the two Windows exes the runner started. */
export type WindowsExe = 'bundled' | 'staged';

/**
 * What a Windows exe answers: E7.S5a's `--version` (the bundled exe alone), WR-E1's `tasks` argv (see {@link tasksReply}) and the five
 * run reads the Windows pages use (`runs show`, `runs`, `logs`, `archive status`, `archive list`) — on both exes. Everything else is
 * refused, naming the exe and the argv.
 */
export function windowsReply(scenario: FakeScenario, which: WindowsExe, argv: readonly string[]): Reply {
  if (which === 'bundled' && sameTail(argv, ['--version'])) {
    return { code: 0, stdout: Buffer.from(`${scenario.bundledVersion ?? '0.0.0+fake'}\r\n`, 'utf8') };
  }
  if (which === 'bundled' && sameTail(argv, ['status', '--json'])) {
    return bundledStatusReply(scenario);
  }

  return tasksReply(scenario, which, argv) ?? windowsReadReply(scenario, argv) ?? { code: FAKE_EXIT.refused, stderr: `fake wsl-care.exe (${which}): not an argv the extension sends it: ${JSON.stringify(argv)}\n` };
}

/** The five reads of the Windows side: `archive status` / `archive list` (never `preview` or `check-base`) and the three run reads. */
function windowsReadReply(scenario: FakeScenario, argv: readonly string[]): Reply | undefined {
  const archive = argv[0] === 'archive' && (argv[1] === 'status' || argv[1] === 'list') ? archiveReadOf(argv) : undefined;
  if (archive !== undefined) {
    const code = argv[1] === 'list' ? (scenario.archiveListExit ?? 0) : 0;
    return { code, stdout: fs.readFileSync(path.join(scenario.answers, archive(scenario))), ...delay(scenario) };
  }
  const runRead = runReadOf(argv);

  return runRead === undefined ? undefined : runReadAnswer(scenario, runRead);
}

/** The golden entry each task's render answers unless the scenario names another. */
const DEFAULT_RENDER = { watch: 'watch from the staged copy', archive: 'archive from the staged copy', cleanup: 'cleanup from the staged copy' } as const;

/** The three tasks the fake renders — its OWN list (the fake is held to the plan, not to the product's constant). */
type FakeTask = keyof typeof DEFAULT_RENDER;

/**
 * `tasks stage --json` and `tasks stage --remove --json` (the BUNDLED exe's), `tasks render <watch|archive|cleanup> --json` and
 * `tasks preview cleanup --json` (the STAGED copy's) — word for word, each on its own exe. `tasks run` is not here: it is the scheduled task's action, never the extension's, and
 * a `--timer` anywhere is refused like every unknown word. Absent a `tasks` scenario every `tasks` argv is refused.
 */
function tasksReply(scenario: FakeScenario, which: WindowsExe, argv: readonly string[]): Reply | undefined {
  if (argv[0] !== 'tasks') {
    return undefined;
  }
  const tasks = scenario.tasks;
  if (tasks === undefined) {
    return refuseTasks(which, argv, 'the scenario scripts no tasks answers');
  }
  if (which === 'bundled' && sameTail(argv, ['tasks', 'stage', '--json'])) {
    return taskAnswer(tasks.folder, tasks.stage ?? { file: 'stage-staged.json', code: 0 });
  }
  if (which === 'bundled' && sameTail(argv, ['tasks', 'stage', '--remove', '--json'])) {
    return taskAnswer(tasks.folder, tasks.remove ?? { file: 'stage-remove-removed.json', code: 0 });
  }
  if (which === 'staged' && sameTail(argv, ['tasks', 'preview', 'cleanup', '--json'])) {
    return taskAnswer(tasks.folder, tasks.preview ?? { file: 'preview-cleanup.json', code: 0 });
  }
  const task = renderTaskOf(argv);

  return which === 'staged' && task !== undefined ? renderReply(tasks, task) : refuseTasks(which, argv, 'not a tasks argv this exe is asked');
}

function refuseTasks(which: WindowsExe, argv: readonly string[], why: string): Reply {
  return { code: FAKE_EXIT.refused, stderr: `fake wsl-care.exe (${which}): ${why}: ${JSON.stringify(argv)}\n` };
}

/** `tasks render <watch|archive|cleanup> --json` — exactly four words — or not one. */
function renderTaskOf(argv: readonly string[]): FakeTask | undefined {
  const [verb, sub, task, json, ...rest] = argv;

  return verb === 'tasks' && sub === 'render' && json === '--json' && rest.length === 0 ? (Object.keys(DEFAULT_RENDER) as FakeTask[]).find((name) => name === task) : undefined;
}

function taskAnswer(folder: string, answer: FakeTaskAnswer): Reply {
  return { code: answer.code, ...(answer.file === undefined ? {} : { stdout: fs.readFileSync(path.join(folder, answer.file)) }), ...(answer.stderr === undefined ? {} : { stderr: answer.stderr }) };
}

/** The render golden's entry — or the scripted exit. */
function renderReply(tasks: FakeTasks, task: FakeTask): Reply {
  if (tasks.render?.exit !== undefined) {
    return taskAnswer(tasks.folder, tasks.render.exit);
  }
  const name = tasks.render?.[task] ?? DEFAULT_RENDER[task];
  const golden = readJson(tasks.renderGolden);
  const entry = (golden.answers as Record<string, unknown>[]).find((e) => e.name === name);
  if (entry === undefined) {
    return { code: FAKE_EXIT.refused, stderr: `fake wsl-care.exe: the render golden has no entry named ${JSON.stringify(name)}\n` };
  }

  return { code: 0, stdout: Buffer.from(JSON.stringify(entry.answer), 'utf8') };
}

/** Which Windows exe the runner started — EXACTLY the scenario's absolute path for it, nothing like it (another digest, a `..`) — or none. */
function windowsExeOf(scenario: FakeScenario, file: string | undefined): WindowsExe | undefined {
  if (file === undefined) {
    return undefined;
  }
  if (scenario.bundledExe === file) {
    return 'bundled';
  }

  return scenario.stagedExe === file ? 'staged' : undefined;
}

/**
 * E19.S2b: the bundled exe's `status --json`. No Windows golden exists (the daemon's scenario harness writes the `wsl` side only), so
 * the answer is the distro's `status.json` golden with the one field that names the side — `side: "windows"` — set the way the Windows
 * binary reports it; `bundledStatusExit` scripts a failing exit instead.
 */
function bundledStatusReply(scenario: FakeScenario): Reply {
  const { bundledStatusDelayMs: delayMs, bundledStatusExit: exit } = scenario;
  const reply: Reply = exit === undefined
    ? { code: 0, stdout: Buffer.from(JSON.stringify({ ...readJson(path.join(scenario.answers, 'status.json')), side: 'windows' }), 'utf8') }
    : { code: exit.code, stderr: exit.stderr };

  return delayMs === undefined ? reply : { ...reply, delayMs };
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
  const windows = windowsExeOf(scenario, file);
  if (windows !== undefined) {
    emit(windowsReply(scenario, windows, argv));
    return;
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

/** S7b.2b: a shown process as the daemon spells it (`SuspectSignals.Shown`) — the fake's own copy, written independently. */
const SHOWN_PROCESS = /^[1-9][0-9]*:(0|[1-9][0-9]*)$/;
const RUN_ID = RUN_ID_SHAPE;

/** The daemon's published registry (`contracts/actions.json`), read from the repository the compiled fake lives in. */
const CONTRACT_ACTIONS = path.resolve(__dirname, '..', '..', '..', '..', 'contracts', 'actions.json');

interface RootShape {
  readonly op: 'preview' | 'confirm' | 'stop' | 'collect' | 'check';
  readonly ids: readonly string[];
  readonly piped: boolean;
  readonly capabilities: readonly string[];
  readonly runId?: string;
  /** E10.S2b: A20's restore shapes — the entry ids on stdin (`fakeRestore.ts`). */
  readonly entries?: true;
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

  return processConfirm(list, flags) ?? `not one of the root shapes: ${JSON.stringify(tail)}`;
}

/**
 * S7b.2b: `act A21 --confirm --manual --detach --process <pid:start>… --json` — A21 alone, at least one pair, each a canonical
 * `pid:start` with a pid above 1; anything else is refused. `undefined` when the flags are not this shape at all.
 */
function processConfirm(ids: readonly string[], flags: readonly string[]): RootShape | string | undefined {
  const head = ['--confirm', '--manual', '--detach'];
  if (!sameTail(flags.slice(0, head.length), head) || flags.at(-1) !== '--json' || flags[head.length] !== '--process') {
    return undefined;
  }
  const pairs = flags.slice(head.length, -1);
  const keys = pairs.filter((_, i) => i % 2 === 1);
  const wellFormed = pairs.length % 2 === 0 && pairs.every((word, i) => (i % 2 === 0 ? word === '--process' : SHOWN_PROCESS.test(word) && Number(word.split(':')[0]) > 1));
  if (!wellFormed || keys.length === 0) {
    return `--process takes canonical pid:start pairs, at least one: ${JSON.stringify(pairs)}`;
  }

  return ids.length === 1 && ids[0] === 'A21' ? { op: 'confirm', ids, piped: false, capabilities: ['act.detach', 'act.processList'] } : `--process names the relays A21 showed; it is A21's alone: ${JSON.stringify(ids)}`;
}

/**
 * E10.S1b, the fake's own copy of A13's rule: A13 is archived on its own, never beside another id, and its preview needs
 * `archive.preview` advertised, its confirm `archive.run` — what `cleanupController.ts` gates on.
 */
function withArchive(shape: RootShape | string): RootShape | string {
  if (typeof shape !== 'string' && shape.ids.includes('A20')) {
    return `A20 is restored only through act A20 … --entry - (E10.S2b), never through a cleanup shape: ${JSON.stringify(shape.ids)}`;
  }
  if (typeof shape === 'string' || !shape.ids.includes('A13')) {
    return shape;
  }
  if (shape.ids.length > 1) {
    return `A13 is archived on its own, never beside another id: ${JSON.stringify(shape.ids)}`;
  }

  return { ...shape, capabilities: [...shape.capabilities, shape.op === 'preview' ? 'archive.preview' : 'archive.run'] };
}

/** S7b.2b, the fake's own copy of A21's rule: A21 is stopped on its own, and its preview needs `act.processList` advertised. */
function withRelays(shape: RootShape | string): RootShape | string {
  if (typeof shape === 'string' || !shape.ids.includes('A21')) {
    return shape;
  }
  if (shape.ids.length > 1) {
    return `A21 is stopped on its own, never beside another id: ${JSON.stringify(shape.ids)}`;
  }

  return shape.op === 'preview' ? { ...shape, capabilities: [...shape.capabilities, 'act.processList'] } : shape;
}

/** The tail after the root prefix, read against the five shapes — or why it is none of them. */
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

  if (tail[0] === 'act' && tail[1] === 'A20') {
    return restoreShape(tail);
  }

  return tail[0] === 'act' && !(tail[1] ?? '').startsWith('-') ? withRelays(withArchive(actShape(tail))) : `not one of the root shapes: ${JSON.stringify(tail)}`;
}

/** E10.S2b: A20's two shapes, word for word the shared request's (`fakeRestore.ts`). */
function restoreShape(tail: readonly string[]): RootShape | string {
  const op = restoreOpOf(tail);

  return op === undefined ? `A20 takes exactly the shared request's preview or confirm (--entry -): ${JSON.stringify(tail)}` : { op, ids: ['A20'], piped: false, entries: true, capabilities: RESTORE_CAPABILITIES[op] };
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

/** The daemon's own A13 line, which every A13 preview answers from (E10.S1b). */
const A13_PREVIEW_ACTION = 'act-a13-preview-action.json';

/** S7b.2b: A21's line — the daemon-written golden (two relays), or the one a test writes into its answers folder. */
const A21_PREVIEW_ACTION = 'act-a21-preview-action.json';

/** An answers folder without the daemon's A13 line cannot answer A13's preview: refused, never thrown (E10.S1b own review #11). */
function archiveLineProblem(scenario: FakeScenario, shape: RootShape): string | undefined {
  const needed = shape.op === 'preview' && shape.ids.includes('A13');

  return needed && !fs.existsSync(path.join(scenario.answers, A13_PREVIEW_ACTION)) ? `the answers folder has no ${A13_PREVIEW_ACTION}` : undefined;
}

function shapeProblem(scenario: FakeScenario, shape: RootShape, stdin: Buffer): string | undefined {
  if (shape.entries === true) {
    return entriesProblem(scenario, shape, stdin);
  }
  if (!shape.piped && stdin.length > 0) {
    return 'stdin outside --only -: only a confirm of A4 pipes a list';
  }

  return (shape.piped ? pipedProblem(stdin) : undefined) ?? (shape.ids.length > 0 ? idsProblem(scenario, shape.ids) : undefined) ?? capabilityProblem(scenario, shape.capabilities) ?? archiveLineProblem(scenario, shape);
}

/** E10.S2b: A20's stdin, the registry ∩ status.actions, the capabilities and the daemon's A20 line in the answers folder. */
function entriesProblem(scenario: FakeScenario, shape: RootShape, stdin: Buffer): string | undefined {
  const piped = pipedEntries(stdin, scenarioList(scenario.answers, scenario.archiveList ?? 'archive-list.json').ceiling);
  const lineMissing = shape.op === 'preview' && !fs.existsSync(path.join(scenario.answers, A20_PREVIEW_ACTION)) ? `the answers folder has no ${A20_PREVIEW_ACTION}` : undefined;

  return (typeof piped === 'string' ? piped : undefined) ?? idsProblem(scenario, shape.ids) ?? capabilityProblem(scenario, shape.capabilities) ?? lineMissing;
}

function handOff(scenario: FakeScenario, kind: 'act' | 'collect'): Buffer {
  const accepted = readJson(path.join(scenario.answers, 'act-detach-accepted.json'));

  return Buffer.from(JSON.stringify({ ...accepted, result: scenario.root?.detach ?? 'accepted', kind }), 'utf8');
}

function previewAnswer(scenario: FakeScenario, ids: readonly string[]): Buffer {
  const golden = readJson(path.join(scenario.answers, 'act-a4-preview.json'));
  const entries = Array.isArray(golden.actions) ? (golden.actions as Record<string, unknown>[]) : [];
  const own = (id: string): Record<string, unknown> | undefined => {
    const file = id === 'A13' ? A13_PREVIEW_ACTION : id === 'A21' ? A21_PREVIEW_ACTION : undefined;
    return file !== undefined && fs.existsSync(path.join(scenario.answers, file)) ? readJson(path.join(scenario.answers, file)) : undefined;
  };
  const actions = ids.map((id) => own(id) ?? entries.find((a) => a.id === id) ?? { id, summary: '', status: 'previewed', reason: '', preview: { available: true, count: 0, bytes: 0 } });

  return Buffer.from(JSON.stringify({ ...golden, actions }), 'utf8');
}

/** E10.S2b: A20's preview — the act envelope of the A4 golden, the daemon's A20 line narrowed to the piped ids inside. */
function restorePreviewAnswer(scenario: FakeScenario, stdin: Buffer): Buffer {
  const golden = readJson(path.join(scenario.answers, 'act-a4-preview.json'));
  const piped = stdin.toString('utf8').split('\n').slice(0, -1);

  return Buffer.from(JSON.stringify({ ...golden, actions: [restorePreviewLine(scenario.answers, scenario.archiveList ?? 'archive-list.json', piped)] }), 'utf8');
}

function rootAnswer(scenario: FakeScenario, shape: RootShape, stdin: Buffer): Reply {
  const scripted = shape.op === 'check' ? scenario.root?.checkExit : scenario.root?.exit;
  const wait = scenario.root?.delayMs === undefined ? {} : { delayMs: scenario.root.delayMs };
  if (scripted !== undefined) {
    return { code: scripted.code, stderr: scripted.stderr, ...wait };
  }
  const answers: Record<RootShape['op'], () => Buffer> = {
    check: () => Buffer.from(`${scenario.version ?? '0.1.0'}\n`, 'utf8'),
    preview: () => (shape.entries === true ? restorePreviewAnswer(scenario, stdin) : previewAnswer(scenario, shape.ids)),
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

  return distroReply(scenario, argv) ?? binaryReply(scenario) ?? rootAnswer(scenario, shape, stdin);
}

/** The shapes the fake checks run ids and instants against — the shared module's own objects (`sharedShapes.test.ts`). */
export const FAKE_SHAPES = { runId: RUN_ID, instant: INSTANT } as const;
