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

type Launcher = (...args: unknown[]) => unknown;

function guard(original: Launcher, firstIsCommandLine: boolean): Launcher {
  return function guarded(this: unknown, ...args: unknown[]): unknown {
    const words = firstIsCommandLine ? String(args[0]).trim().split(/\s+/) : [];
    const program = firstIsCommandLine ? words[0] : args[0];
    if (isWslLauncher(program)) {
      throw new Error(`${TRIPWIRE_MESSAGE}: ${String(args[0])}`);
    }
    if (isElevatedPowerShell(program, firstIsCommandLine ? words.slice(1) : args[1])) {
      throw new Error(`${ELEVATION_TRIPWIRE_MESSAGE}: ${String(args[0])}`);
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
