import assert from 'node:assert/strict';
import { test } from 'node:test';

import { FAILURE_KINDS, failureText } from '../failureText';
import type { Failure } from '../client/outcome';
import { barView, RELEVANT_VERDICT_PREFIXES } from '../statusBar/statusBarModel';
import { answered, failed, headBody, removeAt, setAt, type Body } from './support/outcomes';

/**
 * The status bar's text, tooltip and colour (plan §7.2 as the E5.S2 brief words it, §15g B1): from `status --json`
 * and its `verdicts`, read through the client's own handshake over the `head` golden and edited copies.
 */

function verdict(body: Body, id: string, level: string): Body {
  const verdicts = body.verdicts as { id: string; level: string }[];
  const found = verdicts.find((v) => v.id === id);
  assert.ok(found !== undefined, id);
  found.level = level;

  return body;
}

test('over the head golden: RAM used, swap, containers — short — and uncoloured, because every RELEVANT verdict is ok', () => {
  const view = barView(answered('status', headBody('status')));
  assert.equal(view.text, 'WSL RAM 33% · swap 0.0G · 10 containers');
  assert.equal(view.level, 'none');
  assert.doesNotMatch(view.tooltip, /update the daemon/);
});

test('only the verdicts about what the bar shows colour it: the head golden carries clock / systemd / collector warnings and stays uncoloured', () => {
  const body = headBody('status');
  const warnings = (body.verdicts as { id: string; level: string }[]).filter((v) => v.level === 'warn').map((v) => v.id);
  assert.ok(warnings.includes('clock.jumps') && warnings.includes('systemd.failedUnits'), warnings.join(', '));
  assert.ok(warnings.every((id) => !RELEVANT_VERDICT_PREFIXES.some((p) => id.startsWith(p))));
  assert.equal(barView(answered('status', body)).level, 'none');
});

test('a memory warning colours the bar warn, a critical memory or kernel verdict colours it critical — the worst wins', () => {
  assert.equal(barView(answered('status', verdict(headBody('status'), 'memory.swap', 'warn'))).level, 'warn');
  assert.equal(barView(answered('status', verdict(headBody('status'), 'memory.available', 'critical'))).level, 'critical');
  assert.equal(barView(answered('status', verdict(headBody('status'), 'kernel.oomKills', 'critical'))).level, 'critical');
  const both = verdict(verdict(headBody('status'), 'memory.swap', 'warn'), 'memory.fragmentation', 'critical');
  assert.equal(barView(answered('status', both)).level, 'critical');
});

test('the tooltip names each relevant verdict that is not ok, with its figure', () => {
  const view = barView(answered('status', verdict(headBody('status'), 'memory.swap', 'warn')));
  assert.match(view.tooltip, /memory\.swap: warn — 0\.00 GiB/);
});

test('an unknown level does not colour the bar', () => {
  assert.equal(barView(answered('status', verdict(headBody('status'), 'memory.swap', 'unknown'))).level, 'none');
});

test('a daemon without verdicts: uncoloured, and the tooltip says to update the daemon to see warnings', () => {
  const view = barView(answered('status', removeAt(verdict(headBody('status'), 'memory.swap', 'critical'), 'verdicts')));
  assert.equal(view.level, 'none');
  assert.match(view.tooltip, /update the daemon to see warnings/i);
  assert.equal(view.text, 'WSL RAM 33% · swap 0.0G · 10 containers');
});

test('a figure the daemon could not read shows a question mark, never 0, and the tooltip says why', () => {
  const body = setAt(headBody('status'), 'vm.containers', { available: false, reason: 'Docker is not running' });
  setAt(body, 'vm.memory.availablePercent', { available: false, reason: '/proc/meminfo unreadable' });
  const view = barView(answered('status', body));
  assert.equal(view.text, 'WSL RAM ?% · swap 0.0G · ? containers');
  assert.match(view.tooltip, /Docker is not running/);
  assert.match(view.tooltip, /\/proc\/meminfo unreadable/);
});

test('one container is singular', () => {
  assert.match(barView(answered('status', setAt(headBody('status'), 'vm.containers.count', 1))).text, / 1 container$/);
});

test('the distribution stopped: "WSL stopped" — the client made no call that would start it', () => {
  const view = barView(failed('status', { kind: 'stopped', distro: 'Ubuntu' }));
  assert.equal(view.text, 'WSL stopped');
  assert.equal(view.level, 'none');
  assert.match(view.tooltip, /Start WSL and check/);
});

test('the client\'s outcomes become their short states: not installed, unsupported distro, needs a newer extension, Windows only', () => {
  assert.equal(barView(failed('status', { kind: 'notInstalled', distro: 'Ubuntu' })).text, 'WSL Care: daemon not installed');
  assert.equal(barView(failed('status', { kind: 'unsupportedDistro', distro: 'Ubuntu', detail: 'GLIBC_2.38' })).text, 'WSL Care: unsupported distro');
  assert.equal(barView(failed('status', { kind: 'needsNewerExtension', schemaVersion: 2 })).text, 'WSL Care: needs a newer extension');
  assert.equal(barView(failed('status', { kind: 'notWindows', platform: 'linux' })).text, 'WSL Care: Windows + WSL only');
});

test('before the first answer the bar says it is checking', () => {
  assert.equal(barView(undefined).text, 'WSL Care: checking…');
});

const SAMPLES: readonly Failure[] = [
  { kind: 'notWindows', platform: 'linux' }, { kind: 'wslMissing', detail: 'd' }, { kind: 'distroRefused', distro: 'x', reason: 'r' },
  { kind: 'noDefaultDistro', detail: 'd' }, { kind: 'stopped', distro: 'x' }, { kind: 'wslFailed', message: 'm' },
  { kind: 'notInstalled', distro: 'x' }, { kind: 'unsupportedDistro', distro: 'x', detail: 'd' }, { kind: 'refused', messages: ['m'] },
  { kind: 'internalDefect', messages: [] }, { kind: 'interrupted' }, { kind: 'timedOut', timeoutMs: 1 },
  { kind: 'unknownFailure', code: 9, messages: [] }, { kind: 'unparseable', detail: 'd' }, { kind: 'needsNewerExtension', schemaVersion: 2 },
  { kind: 'daemonTooOld', version: '0.0.9', minimum: '0.1.0' }, { kind: 'previewTooManyContainers', containers: 140, timeoutMs: 330_000 },
];

test('every failure kind has a short label and a sentence, and the kind list is complete', () => {
  assert.deepEqual([...FAILURE_KINDS].sort(), SAMPLES.map((s) => s.kind).sort());
  for (const sample of SAMPLES) {
    const text = failureText(sample);
    assert.ok(text.label.length > 0 && text.sentence.length > 0, sample.kind);
  }
});
