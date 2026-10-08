import { createRequire } from 'node:module';
import * as path from 'node:path';

/**
 * The tripwire every test process is started with (`node --require out/test/support/noRealWsl.js --test …`,
 * `scripts/run-tests.mjs`): after it loads, any attempt in that process to start a program named `wsl` or `wsl.exe` —
 * through `spawn`, `spawnSync`, `execFile`, `execFileSync`, `exec` or `execSync` — throws instead of starting it.
 *
 * <p>Why a tripwire and not a convention: the client builds `%SystemRoot%\System32\wsl.exe` from the environment, and a
 * test that handed it the real environment and the real runner would reach the owner's WSL — start a stopped distro,
 * run whatever is installed there. Every test injects a recording runner or the strict fake instead; this makes the
 * one that forgot fail loudly, wherever it is (plan §16 E5.S1: "no test can reach the real wsl.exe (asserted)").
 * `noRealWsl.test.ts` asserts the tripwire is armed in the very process it runs in.</p>
 *
 * <p>It refuses by the program's NAME, so it also refuses the fake's requested file — which is fine: the fake runner
 * starts `node` with the fake script and only passes the requested path along as data.</p>
 */

export const TRIPWIRE_MESSAGE = 'noRealWsl: a test tried to start the real wsl.exe';

/** Whether `file` names a WSL launcher, by its base name, on either path style. */
export function isWslLauncher(file: unknown): boolean {
  const name = path.win32.basename(String(file)).toLowerCase();

  return name === 'wsl' || name === 'wsl.exe';
}

export const ELEVATION_TRIPWIRE_MESSAGE = 'noRealWsl: a test tried to start an ELEVATED PowerShell (RunAs)';

/**
 * Whether a start would ask Windows for elevation (PLAN_windows_time_guard.md D7): PowerShell with `RunAs` anywhere in what
 * it is handed. *Start Windows Time* records instead in Test mode; a test that reached the real launcher would put a UAC
 * prompt on the machine running the tests — and, answered, change its services. A PowerShell WITHOUT RunAs (the parse
 * check of the fix's script) is allowed.
 */
export function isElevatedPowerShell(file: unknown, rest: unknown): boolean {
  const name = path.win32.basename(String(file)).toLowerCase();
  const text = Array.isArray(rest) ? rest.map(String).join(' ') : String(rest ?? '');

  return (name === 'powershell' || name === 'powershell.exe' || name === 'pwsh' || name === 'pwsh.exe') && /runas/i.test(text);
}

const POWERSHELLS: ReadonlySet<string> = new Set(['powershell', 'powershell.exe', 'pwsh', 'pwsh.exe']);

function baseName(file: unknown): string {
  return path.win32.basename(String(file)).toLowerCase();
}

export const TASK_TRIPWIRE_MESSAGE = 'noRealWsl: a test tried to change Task Scheduler, a service or the machine registry';

/** The programs that change scheduled tasks or services by themselves (PLAN_windows_time_task.md D7, D8 o4). */
const MACHINE_TOOLS: ReadonlySet<string> = new Set(['schtasks', 'schtasks.exe', 'sc', 'sc.exe']);

/**
 * The words a PowerShell would need to change this machine the way the Windows Time guard does — register or delete a task,
 * start / stop / reconfigure a service, write the registry, resync the clock (D8 o4). Matched as WORDS of the decoded text,
 * so a read — `NewTask(0).XmlText`, `GetTask`, PowerShell's parser — stays allowed, and the guard's own status query too.
 */
export const MUTATING = /\b(?:Register-ScheduledTask|Unregister-ScheduledTask|(?:Start|Stop|Enable|Disable|Set)-ScheduledTask|RegisterTask(?:Definition)?|DeleteTask|DeleteFolder|CreateFolder|(?:Start|Stop|Restart|Set|Suspend|Resume)-Service|(?:Set|New|Remove|Rename|Clear)-ItemProperty|New-Item|Remove-Item|w32tm|schtasks|sc(?:\.exe)?\s+(?:start|stop|config|delete|create|failure|sdset)|net(?:\.exe)?\s+(?:start|stop))\b/i;

/** `-EncodedCommand`, and every prefix PowerShell accepts for it (`-e`, `-ec`, `-enc`, …). */
const ENCODED_SWITCH = /^-(?:ec|e(?:n(?:c(?:o(?:d(?:e(?:d(?:c(?:o(?:m(?:m(?:a(?:n(?:d)?)?)?)?)?)?)?)?)?)?)?)?)?)$/i;

function decodedPayload(b64: string): string {
  return Buffer.from(b64, 'base64').toString('utf16le');
}

/**
 * What a PowerShell is really handed: its arguments, plus every `-EncodedCommand` payload DECODED — as an argument of its
 * own, and inside a `-Command` text that starts a second PowerShell with one (story 1's launcher). Recursive, so a payload
 * that carries a payload is read too.
 */
export function revealed(words: readonly string[], depth = 0): string {
  const text = words.join(' ');
  if (depth > 3) {
    return text;
  }
  const payloads = [
    ...words.flatMap((w, i) => (ENCODED_SWITCH.test(w) && words[i + 1] !== undefined ? [words[i + 1] as string] : [])),
    ...Array.from(text.matchAll(/-e(?:c|n\w*)?\s+'?([A-Za-z0-9+/=]{8,})/gi), (m) => m[1] ?? ''),
  ].map(decodedPayload);

  return [text, ...payloads.map((p) => revealed(p.split(/\s+/), depth + 1))].join(' ; ');
}

function wordsOf(rest: unknown): string[] {
  return Array.isArray(rest) ? rest.map(String) : String(rest ?? '').split(/\s+/);
}

/** Whether a start would change this machine's tasks, services or registry the way the guard does (D7, D8 o4). */
export function isMachineChange(file: unknown, rest: unknown): boolean {
  const name = baseName(file);

  return MACHINE_TOOLS.has(name) || (POWERSHELLS.has(name) && MUTATING.test(revealed(wordsOf(rest))));
}

type Launcher = (...args: unknown[]) => unknown;

/** A command line's words, the program's surrounding quotes taken off (`"C:\Program Files\x.exe" -a`). */
export function commandWords(line: string): string[] {
  const quoted = /^\s*"([^"]*)"\s*(.*)$/s.exec(line);
  const rest = (quoted?.[2] ?? line).trim();

  return quoted === null ? rest.split(/\s+/) : [quoted[1] ?? '', ...(rest === '' ? [] : rest.split(/\s+/))];
}

const COMMAND_SHELLS: ReadonlySet<string> = new Set(['cmd', 'cmd.exe']);

/** What `cmd /c <line>` (or `/k`) runs: the words after the switch, as a command line of their own. */
function shellCommand(program: unknown, rest: readonly string[]): string[] | undefined {
  const at = rest.findIndex((w) => /^\/[ck]$/i.test(w));

  return COMMAND_SHELLS.has(baseName(program)) && at >= 0 ? commandWords(rest.slice(at + 1).join(' ')) : undefined;
}

/** The refusal a start earns, or `undefined` — the program judged, then (through `cmd /c`) the program it runs. */
export function refusalOf(program: unknown, rest: readonly string[] | unknown, depth = 0): string | undefined {
  if (isWslLauncher(program)) {
    return TRIPWIRE_MESSAGE;
  }
  if (isElevatedPowerShell(program, rest)) {
    return ELEVATION_TRIPWIRE_MESSAGE;
  }
  if (isMachineChange(program, rest)) {
    return TASK_TRIPWIRE_MESSAGE;
  }
  const inner = Array.isArray(rest) && depth < 3 ? shellCommand(program, rest.map(String)) : undefined;

  return inner === undefined ? undefined : refusalOf(inner[0], inner.slice(1), depth + 1);
}

function shellOption(options: unknown): boolean {
  return typeof options === 'object' && options !== null && !Array.isArray(options) && Boolean((options as { shell?: unknown }).shell);
}

/** A launcher's arguments as (program, rest): a command line for exec / `shell: true`, else the file and its argv. */
function startOf(args: readonly unknown[], firstIsCommandLine: boolean): { readonly program: unknown; readonly rest: unknown } {
  const argv = Array.isArray(args[1]) ? args[1].map(String) : [];
  const viaShell = firstIsCommandLine || shellOption(Array.isArray(args[1]) ? args[2] : args[1]);
  if (!viaShell) {
    return { program: args[0], rest: args[1] };
  }
  const words = commandWords([String(args[0]), ...argv].join(' '));

  return { program: words[0], rest: words.slice(1) };
}

function guard(original: Launcher, firstIsCommandLine: boolean): Launcher {
  return function guarded(this: unknown, ...args: unknown[]): unknown {
    const { program, rest } = startOf(args, firstIsCommandLine);
    const refusal = refusalOf(program, rest);
    if (refusal !== undefined) {
      throw new Error(`${refusal}: ${String(args[0])}`);
    }

    return original.apply(this, args);
  };
}

// The RAW module object, not an import namespace: esModuleInterop's namespace copy has getters only, and the product's
// runner reads `spawn` off the module at call time, so the module itself is what must change.
const target = createRequire(__filename)('node:child_process') as Record<string, Launcher>;
for (const name of ['spawn', 'spawnSync', 'execFile', 'execFileSync']) {
  target[name] = guard(target[name] as Launcher, false);
}
for (const name of ['exec', 'execSync']) {
  target[name] = guard(target[name] as Launcher, true);
}

(globalThis as Record<string, unknown>).__wslCareTripwireArmed = true;
