/**
 * The strict fake `wsl.exe` — a Node script the client's tests start through the SAME runner seam the real one is
 * started through (`nodeScriptRunner`: `node fakeWsl.js <the argv the client built>`; a `.cmd` cannot be spawned
 * without a shell on current Node, plan §15g M6).
 *
 * <p><b>Stricter than the real thing, never more permissive</b> (`common.generated-code-tests` §3). It answers only the
 * argv shapes the client may send, each in the encoding the real `wsl.exe` uses (measured 2026-10-03,
 * research/2026-10-03_wsl_exe_facts.md): `wsl.exe`'s own output UTF-16LE, the Linux program's UTF-8. Everything else
 * is refused with a distinct exit code and a sentence naming why — including argv the real `wsl.exe` would happily run
 * but the client must never send: `-u` / `--user`, `--` (argv handed to the distro's shell), anything after the binary
 * but the four read-only verbs (so `--timer`, `--confirm`, `--manual`, `config`, `act`, `collect`), a `-d` to a STOPPED
 * distro (the real one would start the VM), and a start as anything but an absolute `…\System32\wsl.exe`.</p>
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

function daemonAnswer(scenario: FakeScenario, tail: readonly string[]): Reply {
  if (scenario.binary === 'missing') {
    return { code: 1, stderr: missingBinaryStderr(4000 + process.pid % 1000) };
  }
  if (scenario.binary === 'oldGlibc') {
    return { code: 1, stderr: OLD_GLIBC_STDERR };
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
  const [d, distro, cd, slash, exec, binary, ...tail] = argv;
  if (d !== '-d' || cd !== '--cd' || slash !== '/' || exec !== '--exec' || binary !== DAEMON) {
    return refuse('not the one shape the client sends: -d <distro> --cd / --exec /opt/wsl-care/bin/wsl-care <verb>', argv);
  }
  if (!ALLOWED_TAILS.some((allowed) => sameTail(tail, allowed))) {
    return refuse(`outside the four read-only verbs: ${JSON.stringify(tail)}`, argv);
  }
  const known = scenario.distros.find((x) => x.name === distro);
  if (known === undefined) {
    return { code: -1, stdout: wslText(scenario, NO_SUCH_DISTRO) };
  }
  if (!known.running && scenario.startable !== true) {
    return { code: FAKE_EXIT.wouldStart, stderr: `fake wsl: -d ${distro} would START the stopped distribution\n` };
  }

  return daemonAnswer(scenario, tail);
}

/** The fake's whole decision, pure: what `argv` answers under `scenario`. Exported for its own tests. */
export function decide(scenario: FakeScenario, argv: readonly string[]): Reply {
  const forbidden = argv.find((word) => FORBIDDEN_WORDS.includes(word));
  if (forbidden !== undefined) {
    return refuse(`"${forbidden}" is never sent by the read-only client`, argv);
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

function main(): void {
  const argv = process.argv.slice(2);
  const scenarioFile = process.env.WSL_CARE_FAKE_SCENARIO;
  if (scenarioFile === undefined || scenarioFile === '') {
    emit({ code: FAKE_EXIT.noScenario, stderr: 'fake wsl: no scenario (WSL_CARE_FAKE_SCENARIO) — refusing to answer\n' });
    return;
  }
  const scenario = JSON.parse(fs.readFileSync(scenarioFile, 'utf8')) as FakeScenario;
  const file = process.env.WSL_CARE_FAKE_REQUESTED_FILE;
  if (scenario.log !== undefined) {
    fs.appendFileSync(scenario.log, `${JSON.stringify({ argv, file: file ?? null })}\n`);
  }
  if (!isSystemWsl(file)) {
    emit({ code: FAKE_EXIT.wrongFile, stderr: `fake wsl: started as ${JSON.stringify(file)}, not as an absolute ...\\System32\\wsl.exe\n` });
    return;
  }
  const reply = decide(scenario, argv);
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
  main();
}
