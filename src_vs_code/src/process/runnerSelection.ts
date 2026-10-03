import * as path from 'node:path';

import { closedRunner, nodeScriptRunner, spawnRunner, type Runner } from './runner';

/**
 * Which runner the extension wires (plan §15g M6). `extension.ts` passes whether the extension host runs it in Test
 * mode (`context.extensionMode === ExtensionMode.Test`); nothing else can switch it:
 *
 * - **not Test mode** — the real runner, always. The variable below is not even read, so no environment can redirect
 *   an installed extension.
 * - **Test mode, the fake named** — `WSL_CARE_TEST_FAKE_WSL` holding an absolute path to the compiled fake (`.js`):
 *   that script is started through `nodeScriptRunner` in place of `wsl.exe`.
 * - **Test mode, no usable fake** — CLOSED: a runner that starts nothing. A test run that forgot the fake must not fall
 *   through to the real `wsl.exe` of whoever runs the tests.
 */

export const FAKE_WSL_VARIABLE = 'WSL_CARE_TEST_FAKE_WSL';

export type RunnerChoice =
  | { readonly kind: 'real' }
  | { readonly kind: 'fake'; readonly script: string }
  | { readonly kind: 'closed'; readonly reason: string };

function fakeIn(env: Readonly<Record<string, string | undefined>>): RunnerChoice {
  const script = env[FAKE_WSL_VARIABLE] ?? '';
  if (path.isAbsolute(script) && script.endsWith('.js')) {
    return { kind: 'fake', script };
  }

  return { kind: 'closed', reason: `Test mode without ${FAKE_WSL_VARIABLE} naming an absolute .js fake` };
}

export function chooseRunner(isTestMode: boolean, env: Readonly<Record<string, string | undefined>>): RunnerChoice {
  return isTestMode ? fakeIn(env) : { kind: 'real' };
}

export function runnerFor(choice: RunnerChoice): Runner {
  switch (choice.kind) {
    case 'real':
      return spawnRunner;
    case 'fake':
      return nodeScriptRunner(choice.script);
    case 'closed':
      return closedRunner;
  }
}
