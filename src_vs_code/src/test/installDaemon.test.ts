import assert from 'node:assert/strict';
import * as fs from 'node:fs';
import * as path from 'node:path';
import { test } from 'node:test';

import { INSTALL_DAEMON, MIN_DAEMON_FOR_ACTIONS, MIN_DAEMON_FOR_RENDER, versionAtLeast } from '../client/handshake';
import type { Failure } from '../client/outcome';
import { WslCareClient, type TerminalTarget } from '../client/WslCareClient';
import { INSTALL_COMMAND, INSTALL_PREREQUISITES, INSTALL_VERSION } from '../install/installCommand';
import { CONFIRM_LABEL, installDaemon, installPrompt, type InstallDeps, type InstallPrompt, type TerminalSpec } from '../install/installDaemon';
import { parsePageMessage } from '../panel/messages';
import { TEST_ENV } from './support/fakeWorld';
import { EXTENSION_ROOT } from './support/paths';
import { exitedUtf16, LIST_QUIET, recordingRunner } from './support/recordingRunner';

/**
 * *Install daemon* (plan §15g m2, §15f #5): the command is PINNED to the minimum daemon and never skips the
 * attestation; the distribution is validated before any terminal exists; the command is TYPED (`sendText(…, false)`),
 * never executed; and nothing the page sends can reach its text. Every step through injected collaborators — no VS
 * Code, no terminal, no `wsl.exe`.
 */

const WSL = 'C:\\Windows\\System32\\wsl.exe';

interface Recorded {
  readonly events: string[];
  readonly prompts: InstallPrompt[];
  readonly terminals: TerminalSpec[];
  readonly typed: { text: string; addNewLine: boolean }[];
  readonly reports: string[];
}

function deps(target: () => Promise<TerminalTarget | Failure>, answer: boolean): { deps: InstallDeps; seen: Recorded } {
  const seen: Recorded = { events: [], prompts: [], terminals: [], typed: [], reports: [] };
  return {
    seen,
    deps: {
      target: async () => {
        seen.events.push('target');
        return target();
      },
      confirm: (prompt) => {
        seen.events.push('confirm');
        seen.prompts.push(prompt);
        return Promise.resolve(answer);
      },
      openTerminal: (spec) => {
        seen.events.push('openTerminal');
        seen.terminals.push(spec);
        return {
          show: () => { seen.events.push('show'); },
          sendText: (text, addNewLine) => {
            seen.events.push('sendText');
            seen.typed.push({ text, addNewLine });
          },
        };
      },
      report: (message) => { seen.reports.push(message); },
    },
  };
}

const UBUNTU: TerminalTarget = { kind: 'terminal', shellPath: WSL, shellArgs: ['-d', 'Ubuntu', '--cd', '~'], distro: 'Ubuntu' };

test('the command installs daemon 0.1.2 — the installer from its TAG and --version of the same — not a minimum', () => {
  // daemon 0.1.0's act unit carries the CollectMode defect (fixed in 0.1.1, first published as 0.1.2; #37): a new user
  // installing from the panel must get 0.1.2. The minima the extension RENDERS and ACTS with stay lower.
  assert.equal(INSTALL_DAEMON, '0.1.2');
  assert.equal(INSTALL_VERSION, INSTALL_DAEMON);
  assert.equal(
    INSTALL_COMMAND,
    'curl -fsSL https://raw.githubusercontent.com/oleksandrdubyna88/wsl_care/refs/tags/daemon-v0.1.2/install.sh | sudo sh -s -- --version 0.1.2',
  );
});

test('the version installed is never below the minimum the extension renders, nor the one it acts with', () => {
  assert.equal(versionAtLeast(INSTALL_DAEMON, MIN_DAEMON_FOR_RENDER), true, `INSTALL_DAEMON ${INSTALL_DAEMON} is below MIN_DAEMON_FOR_RENDER ${MIN_DAEMON_FOR_RENDER}: the panel would install a daemon it then refuses`);
  assert.equal(versionAtLeast(INSTALL_DAEMON, MIN_DAEMON_FOR_ACTIONS), true, `INSTALL_DAEMON ${INSTALL_DAEMON} is below MIN_DAEMON_FOR_ACTIONS ${MIN_DAEMON_FOR_ACTIONS}: a fresh install could not run a cleanup`);
});

test('the command never skips the attestation, never fetches from main, and carries no shell trick beyond the one pipe', () => {
  assert.equal(INSTALL_COMMAND.includes('--skip-attestation'), false);
  assert.equal(INSTALL_COMMAND.includes('/main/'), false);
  assert.equal(INSTALL_COMMAND.split('|').length, 2, 'exactly one pipe');
  assert.equal(/[;&`$<>\n\r]/.test(INSTALL_COMMAND), false, 'no ;, &, backtick, $, redirection or newline');
});

test('confirmed: the distribution is resolved first, then the modal, then ONE terminal in it — and the command is typed, NOT executed', async () => {
  const { deps: d, seen } = deps(() => Promise.resolve(UBUNTU), true);
  assert.deepEqual(await installDaemon(d), { kind: 'typed', distro: 'Ubuntu' });
  assert.deepEqual(seen.events, ['target', 'confirm', 'openTerminal', 'show', 'sendText']);
  assert.deepEqual(seen.terminals, [{ name: 'AI OS Care — install (Ubuntu)', shellPath: WSL, shellArgs: ['-d', 'Ubuntu', '--cd', '~'] }]);
  assert.deepEqual(seen.typed, [{ text: INSTALL_COMMAND, addNewLine: false }], 'sendText(command, false): typed, never run');
});

test('declined (dismissed, or any other answer): no terminal, nothing typed', async () => {
  const { deps: d, seen } = deps(() => Promise.resolve(UBUNTU), false);
  assert.deepEqual(await installDaemon(d), { kind: 'declined', distro: 'Ubuntu' });
  assert.deepEqual(seen.events, ['target', 'confirm']);
  assert.deepEqual(seen.typed, []);
});

test('a refused distribution: reported, no modal, no terminal', async () => {
  const refused: Failure = { kind: 'distroRefused', distro: '-u', reason: 'the pattern' };
  const { deps: d, seen } = deps(() => Promise.resolve(refused), true);
  assert.deepEqual(await installDaemon(d), { kind: 'refused', failure: refused });
  assert.deepEqual(seen.events, ['target']);
  assert.equal(seen.reports.length, 1);
  assert.match(seen.reports[0] ?? '', /distribution "-u" was refused/);
});

test('with the real client: an out-of-pattern setting is refused BEFORE anything starts, and no terminal is created', async () => {
  const rec = recordingRunner({});
  const client = new WslCareClient({ runner: rec.runner, platform: 'win32', env: TEST_ENV, distroSetting: () => 'Ubuntu; rm -rf ~' });
  const { deps: d, seen } = deps(() => client.terminalTarget(), true);
  assert.equal((await installDaemon(d)).kind, 'refused');
  assert.equal(rec.requests.length, 0, 'nothing was started');
  assert.deepEqual(seen.terminals, []);
});

test('with the real client: the terminal is opened in the LISTED distribution, by the absolute launcher, after --list alone', async () => {
  const rec = recordingRunner({ [LIST_QUIET]: exitedUtf16(0, 'Ubuntu\r\nDebian\r\n') });
  const client = new WslCareClient({ runner: rec.runner, platform: 'win32', env: TEST_ENV, distroSetting: () => 'Debian' });
  const { deps: d, seen } = deps(() => client.terminalTarget(), true);
  assert.deepEqual(await installDaemon(d), { kind: 'typed', distro: 'Debian' });
  assert.deepEqual(rec.argvs(), [LIST_QUIET]);
  assert.deepEqual(seen.terminals[0]?.shellArgs, ['-d', 'Debian', '--cd', '~'], 'the terminal starts in the home folder, not in whatever folder VS Code was started from');
  assert.equal(seen.terminals[0]?.shellPath, WSL);
});

test('the modal shows the exact command and every prerequisite as text, with one confirm button', () => {
  const prompt = installPrompt('Ubuntu');
  assert.match(prompt.message, new RegExp(`Install the wsl-care daemon ${INSTALL_VERSION.replace(/\./g, '\\.')} in "Ubuntu"`));
  assert.ok(prompt.detail.split('\n').includes(INSTALL_COMMAND), 'the command on a line of its own, verbatim');
  for (const line of INSTALL_PREREQUISITES) {
    assert.ok(prompt.detail.includes(line), line);
  }
  assert.match(prompt.detail, /TYPED, not run/);
  assert.equal(prompt.confirm, CONFIRM_LABEL);
});

test('the prerequisites name systemd, Ubuntu 24.04 / glibc 2.39, gh 2.56.0 and sudo — the installer\'s own preflight', () => {
  const text = INSTALL_PREREQUISITES.join('\n');
  for (const needed of ['systemd', 'Ubuntu 24.04', 'glibc 2.39', 'gh 2.56.0', 'sudo']) {
    assert.ok(text.includes(needed), needed);
  }
});

test('a webview message cannot inject text: installDaemon is accepted only bare, and the flow takes no value from it', () => {
  assert.deepEqual(parsePageMessage({ type: 'installDaemon' }), { type: 'installDaemon' });
  for (const smuggled of [{ type: 'installDaemon', command: 'rm -rf ~' }, { type: 'installDaemon', version: '0.0.1' }, { type: 'installDaemon', distro: '-u' }, { type: 'installDaemon', args: ['--skip-attestation'] }]) {
    assert.equal(parsePageMessage(smuggled), undefined, JSON.stringify(smuggled));
  }
  assert.equal(installDaemon.length, 1, 'installDaemon(deps) — no parameter a page value could reach');
});

test('the Marketplace README shows the very command the extension types', () => {
  const readme = fs.readFileSync(path.join(EXTENSION_ROOT, 'README.md'), 'utf8');
  assert.ok(readme.split(/\r?\n/).includes(INSTALL_COMMAND), 'README.md carries INSTALL_COMMAND on a line of its own');
});

test('the install module reads INSTALL_DAEMON — never a minimum, whose value can equal it by coincidence', () => {
  const source = fs.readFileSync(path.join(EXTENSION_ROOT, 'src', 'install', 'installCommand.ts'), 'utf8');
  assert.match(source, /export const INSTALL_VERSION = pinnedVersion\(INSTALL_DAEMON\);/);
  assert.equal(/MIN_DAEMON_FOR_(RENDER|ACTIONS)/.test(source.replace(/\/\*[\s\S]*?\*\//g, '')), false, 'the code (comments aside) names no minimum');
});

test('E6.S3 review A1 (the class swept): a report built from wsl.exe text reaches the notification with its link syntax broken', async () => {
  const linked: Failure = { kind: 'wslFailed', message: 'see [here](command:workbench.action.reloadWindow)' };
  const { deps: d, seen } = deps(() => Promise.resolve(linked), true);
  await installDaemon(d);
  assert.equal((seen.reports[0] ?? '').includes(String.fromCharCode(93, 40)), false, seen.reports[0]);
  assert.match(seen.reports[0] ?? '', /here/);
});
