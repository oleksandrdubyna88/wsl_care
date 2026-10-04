import * as assert from 'node:assert/strict';
import * as fs from 'node:fs';
import * as vscode from 'vscode';

import { FIELD_MAP } from '../../panel/fieldMap';
import { CLOSED_REASON } from '../../process/runner';
import type { WslCareTestApi } from '../../testApi';

/**
 * The extension-host scenarios: run INSIDE a real VS Code by `scripts/run-host.mjs` (`@vscode/test-electron`, 1.85.0
 * and stable), against the extension as it ships (`dist/extension.js`). They prove what only an extension host can:
 * the extension activates, the status-bar item shows the daemon's answer, the webview panel renders, and — the
 * polling policy's whole point — NO `-d` call is made while the distribution is stopped or the window unfocused, and a
 * Test-mode run without the fake starts nothing at all. Every observation is through the runner seam's call log and
 * the test API `activate` returns in Test mode only (`testApi.ts`).
 *
 * Not `node:test` and not mocha: test-electron hands a pass / fail back through a thrown error, so the list below is
 * a way of REPORTING (as in ConnectOtherAIs' host suite) — one framework in the repository, not two. An empty list
 * throws, so a launch that ran nothing is red.
 */

const EXTENSION_ID = 'publisher-tbd.wsl-care';
const MODE = process.env.WSL_CARE_HOST_MODE ?? '';
const SCENARIO_FILE = process.env.WSL_CARE_FAKE_SCENARIO ?? '';
const WINDOWS = process.platform === 'win32';
const RAM_LINE = 'WSL RAM 33% · swap 0.0G · 10 containers';

interface Scenario {
  readonly name: string;
  readonly run: (api: WslCareTestApi) => Promise<void>;
}

function pause(ms: number): Promise<void> {
  return new Promise((resolve) => setTimeout(resolve, ms));
}

/** Wait until `check` holds, or fail naming `what` after `ms`. */
async function until(what: string, check: () => boolean, ms = 30_000): Promise<void> {
  const deadline = Date.now() + ms;
  while (!check()) {
    if (Date.now() > deadline) {
      throw new Error(`timed out waiting for: ${what}`);
    }
    await pause(100);
  }
}

/** Rewrites the fake's scenario between scenarios (the fake reads it at every call). */
function scenario(patch: Record<string, unknown>): void {
  const current = JSON.parse(fs.readFileSync(SCENARIO_FILE, 'utf8')) as Record<string, unknown>;
  fs.writeFileSync(SCENARIO_FILE, JSON.stringify({ ...current, ...patch }));
}

function daemonCalls(api: WslCareTestApi): string[] {
  return api.calls().filter((c) => c.startsWith('-d '));
}

const RUNNING = { distros: [{ name: 'Ubuntu', running: true }], startable: false };
const STOPPED = { distros: [{ name: 'Ubuntu', running: false }], startable: false };

const WINDOWS_FAKE: readonly Scenario[] = [
  {
    name: 'activates in Test mode with the fake runner',
    run: async (api) => assert.equal(api.runnerKind(), 'fake'),
  },
  {
    name: 'the focused window asks status and the status-bar item shows the RAM line, uncoloured',
    run: async (api) => {
      scenario(RUNNING);
      api.resetCalls();
      api.setFocused(true);
      await api.settled();
      assert.deepEqual(daemonCalls(api), ['-d Ubuntu --cd / --exec /opt/wsl-care/bin/wsl-care status --json']);
      assert.equal(api.statusBar().text, RAM_LINE);
      assert.equal(api.statusBar().level, 'none');
    },
  },
  {
    name: 'opening the panel asks preview and doctor once and the webview renders every field-map row',
    run: async (api) => {
      api.resetCalls();
      await vscode.commands.executeCommand('wslCare.openPanel');
      await until('the panel asked doctor', () => api.calls().some((c) => c.endsWith('doctor --json')));
      await api.settled();
      await until('the webview reported its rows', () => api.lastRendered() === FIELD_MAP.length);
      assert.equal(daemonCalls(api).filter((c) => c.endsWith('preview --all --json')).length, 1);
    },
  },
  {
    name: 'a stopped distribution: NO -d call on focus, on a poll or on a panel refresh; the bar says "WSL stopped"',
    run: async (api) => {
      scenario(STOPPED);
      api.resetCalls();
      api.setFocused(true);
      await api.tick();
      await api.refreshPanel();
      await api.settled();
      assert.ok(api.calls().length > 0, 'the running check was asked');
      assert.deepEqual(daemonCalls(api), []);
      assert.equal(api.statusBar().text, 'WSL stopped');
    },
  },
  {
    name: 'an unfocused window starts nothing when the interval fires',
    run: async (api) => {
      scenario(RUNNING);
      api.setFocused(false);
      api.resetCalls();
      await api.tick();
      await api.settled();
      assert.deepEqual(api.calls(), []);
    },
  },
  {
    name: '"Start WSL and check" makes the one -d the user asked for',
    run: async (api) => {
      scenario({ ...STOPPED, startable: true });
      api.resetCalls();
      await api.startWsl();
      await api.settled();
      assert.equal(daemonCalls(api).filter((c) => c.endsWith('status --json')).length, 1);
      assert.equal(api.statusBar().text, RAM_LINE);
      scenario(RUNNING);
    },
  },
];

const WINDOWS_CLOSED: readonly Scenario[] = [
  {
    name: 'Test mode without the fake starts nothing: every request ends at the closed runner, the fake never ran',
    run: async (api) => {
      assert.equal(api.runnerKind(), 'closed');
      api.setFocused(true);
      await api.settled();
      assert.ok(api.calls().length > 0, 'a request reached the seam');
      assert.ok(api.statusBar().tooltip.includes(CLOSED_REASON), api.statusBar().tooltip);
      const log = (JSON.parse(fs.readFileSync(SCENARIO_FILE, 'utf8')) as { log: string }).log;
      assert.equal(fs.existsSync(log), false, 'the fake was never started');
    },
  },
];

const ELSEWHERE: readonly Scenario[] = [
  {
    name: 'off Windows: the bar says "Windows + WSL only", nothing reaches the runner, and the panel still renders',
    run: async (api) => {
      api.setFocused(true);
      await api.settled();
      assert.equal(api.statusBar().text, 'WSL Care: Windows + WSL only');
      await vscode.commands.executeCommand('wslCare.openPanel');
      await until('the webview reported its rows', () => api.lastRendered() === FIELD_MAP.length);
      await api.settled();
      assert.deepEqual(api.calls(), []);
    },
  },
];

function scenarios(): readonly Scenario[] {
  if (!WINDOWS) {
    return ELSEWHERE;
  }

  return MODE === 'fake' ? WINDOWS_FAKE : WINDOWS_CLOSED;
}

async function activated(): Promise<WslCareTestApi> {
  const extension = vscode.extensions.getExtension<WslCareTestApi | undefined>(EXTENSION_ID);
  assert.ok(extension !== undefined, `${EXTENSION_ID} is not installed in this host`);
  const api = await extension.activate();
  assert.ok(api !== undefined, 'in Test mode activate returns the test API');

  return api;
}

/** test-electron's entry point: run every scenario, throw if any failed or none ran. */
export async function run(): Promise<void> {
  const list = scenarios();
  assert.ok(list.length > 0, 'no extension-host scenario selected');
  const api = await activated();
  const failures: string[] = [];
  for (const each of list) {
    try {
      await each.run(api);
      console.log(`  ok   ${each.name}`);
    } catch (error) {
      failures.push(`${each.name}: ${error instanceof Error ? error.message : String(error)}`);
      console.log(`  FAIL ${each.name}`);
    }
  }
  if (failures.length > 0) {
    throw new Error(`${failures.length} of ${list.length} extension-host scenarios failed:\n${failures.join('\n')}`);
  }
}
