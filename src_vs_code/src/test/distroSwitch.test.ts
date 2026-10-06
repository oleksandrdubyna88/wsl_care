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

test('a round made obsolete while its status is pending asks no preview or doctor afterwards', async () => {
  const heldStatus = deferred<VerbOutcome>();
  const statusAsked = deferred<void>();
  let distro = 'Ubuntu';
  const calls: string[] = [];
  const store = new OutcomeStore();
  const run = (verb: Verb): Promise<VerbOutcome> => {
    calls.push(`${distro} ${verb}`);
    if (distro === 'Ubuntu' && verb === 'status') {
      statusAsked.resolve();
      return heldStatus.promise;
    }

    return Promise.resolve(answered(verb as PanelVerb, headBody(verb as PanelVerb), distro));
  };
  const poller = new Poller({ run, store, focused: () => true, refreshSeconds: () => undefined, timers: NEVER, target: () => distro });

  void poller.refreshPanel();
  await statusAsked.promise;
  distro = 'Debian';
  await poller.refreshPanel();
  const beforeRelease = [...calls];
  heldStatus.resolve(answered('status', headBody('status'), 'Ubuntu'));
  await poller.settled();

  assert.deepEqual(beforeRelease, ['Ubuntu status', 'Debian status', 'Debian preview', 'Debian doctor']);
  assert.deepEqual(calls, beforeRelease, 'the obsolete round went on to ask preview / doctor after its status answered');
  assert.equal(distroOf(store.snapshot().status), 'Debian');
});

/** A poller over a `run` that records `<distro> <verb>` per call; `heldUbuntuStatus` holds Ubuntu's first status. */
function recordedWorld(heldUbuntuStatus: boolean): {
  poller: Poller; store: OutcomeStore; calls: string[]; held: Deferred<VerbOutcome>; statusAsked: Promise<void>;
  setDistro(d: string): void; setFocused(f: boolean): void;
} {
  const held = deferred<VerbOutcome>();
  const statusAsked = deferred<void>();
  const state = { distro: 'Ubuntu', focused: true };
  const calls: string[] = [];
  const store = new OutcomeStore();
  const run = (verb: Verb): Promise<VerbOutcome> => {
    calls.push(`${state.distro} ${verb}`);
    if (heldUbuntuStatus && state.distro === 'Ubuntu' && verb === 'status') {
      statusAsked.resolve();
      return held.promise;
    }

    return Promise.resolve(answered(verb as PanelVerb, headBody(verb as PanelVerb), state.distro));
  };
  const poller = new Poller({ run, store, focused: () => state.focused, refreshSeconds: () => undefined, timers: NEVER, target: () => state.distro });

  return { poller, store, calls, held, statusAsked: statusAsked.promise, setDistro: (d) => { state.distro = d; }, setFocused: (f) => { state.focused = f; } };
}

test('a switch while a round waits for its status, with NO new round started, ends that round without mixing', async () => {
  const w = recordedWorld(true);
  void w.poller.refreshPanel();
  await w.statusAsked;
  w.setDistro('Debian');
  w.held.resolve(answered('status', headBody('status'), 'Ubuntu'));
  await w.poller.settled();

  assert.deepEqual(w.calls, ['Ubuntu status'], 'the round asked the new distribution for preview / doctor beside the old status');
  const snapshot = w.store.snapshot();
  assert.deepEqual([snapshot.status, snapshot.preview, snapshot.doctor, snapshot.checking], [undefined, undefined, undefined, false]);
});

test('a switch between the status being stored and the round going on asks no preview or doctor', async () => {
  const w = recordedWorld(false);
  const unsubscribe = w.store.onChange((snapshot) => {
    if (distroOf(snapshot.status) === 'Ubuntu') {
      unsubscribe();
      queueMicrotask(() => { w.setDistro('Debian'); });
    }
  });
  await w.poller.refreshPanel();

  assert.deepEqual(w.calls, ['Ubuntu status'], 'the round went on to ask the new distribution for preview / doctor');
});

test('a switch in an unfocused window clears what was shown, and asks nothing', async () => {
  const w = recordedWorld(false);
  await w.poller.refreshPanel();
  assert.equal(distroOf(w.store.snapshot().preview), 'Ubuntu');
  w.setFocused(false);
  w.setDistro('Debian');
  await w.poller.tick();

  assert.deepEqual(w.calls, ['Ubuntu status', 'Ubuntu preview', 'Ubuntu doctor'], 'an unfocused window must ask nothing');
  const snapshot = w.store.snapshot();
  assert.deepEqual([snapshot.status, snapshot.preview, snapshot.doctor], [undefined, undefined, undefined], 'the previous distribution\'s answers are still shown');
});

test('a non-string wslCare.distro (a number, null, an object in settings.json) is refused as a value, never thrown', async () => {
  for (const raw of [42, null, { name: 'Ubuntu' }, JSON.parse('{"toString": null}') as unknown]) {
    const rec = recordingRunner({});
    const client = new WslCareClient({ runner: rec.runner, platform: 'win32', env: TEST_ENV, distroSetting: () => raw as unknown as string });
    let outcome: VerbOutcome | undefined;
    assert.doesNotThrow(() => { void client.run('status').then((o) => { outcome = o; }); }, `run() threw for ${JSON.stringify(raw)}`);
    await new Promise((resolve) => setImmediate(resolve));
    assert.equal(outcome?.kind, 'distroRefused', `${JSON.stringify(raw)}: ${JSON.stringify(outcome)}`);
    assert.equal(rec.requests.length, 0, 'nothing may be started for a setting that is not a name');
  }
});
