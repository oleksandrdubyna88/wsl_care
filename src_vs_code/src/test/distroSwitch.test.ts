import assert from 'node:assert/strict';
import { test } from 'node:test';

import type { VerbOutcome } from '../client/outcome';
import { VERBS, type Verb } from '../client/verbs';
import { WslCareClient, type RunOptions } from '../client/WslCareClient';
import { Poller, type Timers } from '../poll/poller';
import type { ProcessResult } from '../process/runner';
import { OutcomeStore, type PanelVerb } from '../state/outcomeStore';
import { TEST_ENV } from './support/fakeWorld';
import { answered, headBody } from './support/outcomes';
import { golden } from './support/paths';
import { daemonArgv, exited, exitedUtf16, LIST_QUIET, LIST_RUNNING, recordingRunner, type Scripted } from './support/recordingRunner';

/**
 * Switching `wslCare.distro` while a call for the previous distribution is still in flight (retro review of PR #9,
 * 2026-10-06). `preview` may run for minutes (its ceiling is 330 s), so the window is wide: before this fix the client
 * shared the in-flight call by VERB alone, and the poller stored whatever answered last — so the panel showed one
 * distribution's preview under the other's heading. The guarantee: what the panel and the bar hold is about the
 * distribution the setting names NOW, and a same-distribution poll never drops a pending answer.
 */

interface Deferred<T> {
  readonly promise: Promise<T>;
  resolve(value: T): void;
}

function deferred<T>(): Deferred<T> {
  let resolve: (value: T) => void = () => undefined;
  const promise = new Promise<T>((r) => { resolve = r; });

  return { promise, resolve };
}

const NEVER: Timers = { every: () => () => undefined };

function distroOf(outcome: VerbOutcome | undefined): string {
  return outcome !== undefined && outcome.kind === 'answered' ? outcome.distro : `not answered: ${JSON.stringify(outcome)}`;
}

/** WSL lists and runs both distributions; every daemon verb answers at once, except Ubuntu's `preview`, held. */
function heldUbuntuPreview(): { script: Record<string, Scripted>; held: Deferred<ProcessResult>; asked: Deferred<void>; debianDoctorAsked: Deferred<void> } {
  const held = deferred<ProcessResult>();
  const asked = deferred<void>();
  const debianDoctorAsked = deferred<void>();
  const status = JSON.stringify({ ...golden('head', 'status'), productVersion: '0.1.0' });
  const script: Record<string, Scripted> = {
    [LIST_QUIET]: exitedUtf16(0, 'Ubuntu\r\nDebian\r\n'),
    [LIST_RUNNING]: exitedUtf16(0, 'Ubuntu\r\nDebian\r\n'),
  };
  for (const distro of ['Ubuntu', 'Debian']) {
    script[daemonArgv(distro, VERBS.status)] = exited(0, status);
    script[daemonArgv(distro, VERBS.doctor)] = exited(0, JSON.stringify(golden('head', 'doctor')));
    script[daemonArgv(distro, VERBS.version)] = exited(0, '0.1.0\n');
    script[daemonArgv(distro, VERBS.preview)] = exited(0, JSON.stringify(golden('head', 'preview')));
  }
  script[daemonArgv('Ubuntu', VERBS.preview)] = () => { asked.resolve(); return held.promise; };
  // The panel round asks preview and doctor together, preview first: once Debian's doctor is asked, the round's preview
  // call has been made too — shared with Ubuntu's or not.
  script[daemonArgv('Debian', VERBS.doctor)] = () => { debianDoctorAsked.resolve(); return exited(0, JSON.stringify(golden('head', 'doctor'))); };

  return { script, held, asked, debianDoctorAsked };
}

test('a preview asked for another distribution is not joined to the one still in flight for the previous one', async () => {
  const { script, held, asked } = heldUbuntuPreview();
  let distro = 'Ubuntu';
  const rec = recordingRunner(script);
  const client = new WslCareClient({ runner: rec.runner, platform: 'win32', env: TEST_ENV, distroSetting: () => distro });

  const first = client.run('preview');
  await asked.promise;
  distro = 'Debian';
  const second = client.run('preview');
  held.resolve(exited(0, JSON.stringify(golden('head', 'preview'))));

  assert.equal(distroOf(await first), 'Ubuntu');
  assert.equal(distroOf(await second), 'Debian', 'the call for Debian must reach Debian, not share the Ubuntu call in flight');
  assert.ok(rec.argvs().includes(daemonArgv('Debian', VERBS.preview)), `Debian's preview was never asked: ${rec.argvs().join(' | ')}`);
});

test('the panel never shows the previous distribution\'s preview under the new one (real client, poller and store)', async () => {
  const { script, held, asked, debianDoctorAsked } = heldUbuntuPreview();
  let distro = 'Ubuntu';
  const rec = recordingRunner(script);
  const client = new WslCareClient({ runner: rec.runner, platform: 'win32', env: TEST_ENV, distroSetting: () => distro });
  const store = new OutcomeStore();
  const options = {
    run: (verb: Verb, runOptions?: RunOptions) => client.run(verb, runOptions),
    store,
    focused: () => true,
    refreshSeconds: () => undefined,
    timers: NEVER,
    target: () => distro,
  };
  const poller = new Poller(options);

  void poller.refreshPanel();
  await asked.promise;
  distro = 'Debian';
  void poller.refreshPanel();
  await debianDoctorAsked.promise;
  held.resolve(exited(0, JSON.stringify(golden('head', 'preview'))));
  await poller.settled();

  const snapshot = store.snapshot();
  assert.equal(distroOf(snapshot.status), 'Debian');
  assert.equal(distroOf(snapshot.preview), 'Debian', 'the panel holds a preview of a distribution the setting no longer names');
  assert.equal(distroOf(snapshot.doctor), 'Debian');
  assert.equal(snapshot.checking, false);
});

/** A poller whose `run` answers as the distribution named at the moment of the call; Ubuntu's `preview` is held. */
function pollerWorld(): { poller: Poller; store: OutcomeStore; held: Deferred<VerbOutcome>; asked: Promise<void>; setDistro(d: string): void } {
  const held = deferred<VerbOutcome>();
  const asked = deferred<void>();
  let distro = 'Ubuntu';
  const store = new OutcomeStore();
  const run = (verb: Verb): Promise<VerbOutcome> => {
    const panelVerb = verb as PanelVerb;
    if (distro === 'Ubuntu' && verb === 'preview') {
      asked.resolve();
      return held.promise;
    }

    return Promise.resolve(answered(panelVerb, headBody(panelVerb), distro));
  };
  const options = { run, store, focused: () => true, refreshSeconds: () => undefined, timers: NEVER, target: () => distro };

  return { poller: new Poller(options), store, held, asked: asked.promise, setDistro: (d) => { distro = d; } };
}

test('a late answer for the previous distribution never overwrites what the new one answered', async () => {
  const w = pollerWorld();
  void w.poller.refreshPanel();
  await w.asked;
  w.setDistro('Debian');
  await w.poller.refreshPanel();
  assert.equal(distroOf(w.store.snapshot().preview), 'Debian');

  w.held.resolve(answered('preview', headBody('preview'), 'Ubuntu'));
  await w.poller.settled();

  assert.equal(distroOf(w.store.snapshot().preview), 'Debian', 'Ubuntu\'s late preview replaced Debian\'s');
});

test('a distribution switch seen by a status-only poll leaves no preview or doctor of the previous one behind', async () => {
  const w = pollerWorld();
  w.held.resolve(answered('preview', headBody('preview'), 'Ubuntu'));
  await w.poller.refreshPanel();
  assert.equal(distroOf(w.store.snapshot().preview), 'Ubuntu');

  w.setDistro('Debian');
  await w.poller.tick();

  const snapshot = w.store.snapshot();
  assert.equal(distroOf(snapshot.status), 'Debian');
  assert.equal(snapshot.preview, undefined, `still showing ${distroOf(snapshot.preview)}'s preview under Debian`);
  assert.equal(snapshot.doctor, undefined, `still showing ${distroOf(snapshot.doctor)}'s doctor under Debian`);
});

test('a status poll of the SAME distribution never drops the preview still in flight', async () => {
  const w = pollerWorld();
  void w.poller.refreshPanel();
  await w.asked;
  await w.poller.tick();
  w.held.resolve(answered('preview', headBody('preview'), 'Ubuntu'));
  await w.poller.settled();

  const snapshot = w.store.snapshot();
  assert.equal(distroOf(snapshot.preview), 'Ubuntu');
  assert.equal(snapshot.checking, false);
});
