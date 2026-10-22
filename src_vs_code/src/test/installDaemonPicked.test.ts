import assert from 'node:assert/strict';
import { test } from 'node:test';

import { WslCareClient } from '../client/WslCareClient';
import { INSTALL_COMMAND } from '../install/installCommand';
import { installDaemon, type InstallDeps, type TerminalSpec } from '../install/installDaemon';
import { pick } from './support/distroNames';
import { TEST_ENV } from './support/fakeWorld';
import { exitedUtf16, LIST_QUIET, LIST_RUNNING, LIST_VERBOSE, recordingRunner, verboseTable } from './support/recordingRunner';

/**
 * E18.W1a-3 (todo/PLAN_disk_health_E18.md §3.5, RED 19): *Install daemon* for a PICKED distribution — the terminal is `-d Debian --cd ~`,
 * built by the client from WSL's own listing; `installDaemon` itself takes no value a page could reach.
 */

function machine(rows: readonly { name: string; running: boolean; version?: number }[]) {
  const rec = recordingRunner({
    [LIST_QUIET]: exitedUtf16(0, rows.map((r) => `${r.name}\r\n`).join('')),
    [LIST_VERBOSE]: exitedUtf16(0, verboseTable(rows, 'Ubuntu')),
    [LIST_RUNNING]: exitedUtf16(0, rows.filter((r) => r.running).map((r) => `${r.name}\r\n`).join('')),
  });

  return { rec, client: new WslCareClient({ runner: rec.runner, platform: 'win32', env: TEST_ENV, distroSetting: () => 'Ubuntu' }) };
}

function flow(client: WslCareClient, target: ReturnType<typeof pick>): { deps: InstallDeps; terminals: TerminalSpec[]; prompts: string[]; typed: string[]; reports: string[] } {
  const seen = { terminals: [] as TerminalSpec[], prompts: [] as string[], typed: [] as string[], reports: [] as string[] };
  const deps: InstallDeps = {
    target: () => client.terminalTarget(target),
    confirm: (prompt) => { seen.prompts.push(prompt.message); return Promise.resolve(true); },
    openTerminal: (spec) => { seen.terminals.push(spec); return { show: () => undefined, sendText: (text) => { seen.typed.push(text); } }; },
    report: (message) => { seen.reports.push(message); },
  };

  return { deps, ...seen };
}

test('the terminal for a picked Debian is wsl.exe -d Debian --cd ~, the modal names Debian, the command is typed', async () => {
  const { client } = machine([{ name: 'Ubuntu', running: true }, { name: 'Debian', running: true }]);
  const f = flow(client, pick('Debian'));
  assert.deepEqual(await installDaemon(f.deps), { kind: 'typed', distro: 'Debian' });
  assert.deepEqual(f.terminals[0]?.shellArgs, ['-d', 'Debian', '--cd', '~']);
  assert.match(f.prompts[0] ?? '', /in "Debian"/);
  assert.deepEqual(f.typed, [INSTALL_COMMAND]);
});

test('a picked WSL 1 distribution gets no terminal: wsl1 is reported', async () => {
  const { client } = machine([{ name: 'Ubuntu', running: true }, { name: 'Debian', running: true, version: 1 }]);
  const f = flow(client, pick('Debian'));
  assert.equal((await installDaemon(f.deps)).kind, 'refused');
  assert.deepEqual(f.terminals, []);
  assert.match(f.reports[0] ?? '', /needs WSL 2/);
});
