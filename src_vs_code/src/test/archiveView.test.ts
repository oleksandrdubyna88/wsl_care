import assert from 'node:assert/strict';
import * as fs from 'node:fs';
import * as path from 'node:path';
import { test } from 'node:test';

import { ARCHIVE_CAPABILITY, deriveArchive, NO_ARCHIVE, retentionBadge, type ArchiveState } from '../archive/archiveView';
import type { ReadOutcome } from '../client/outcome';
import { buildPanelView } from '../panel/viewModel';
import type { Snapshot } from '../state/outcomeStore';
import { goldenOutcomes } from './support/outcomes';
import { Element, runPageScript } from './support/pageHarness';
import { GOLDEN_ROOT, PAGE_SCRIPT } from './support/paths';

/**
 * Plan §15s, E10.S1: the archive's part of the panel, derived from the daemon's `archive status` / `archive preview` answers and
 * the capabilities its `status` advertised — and the page rendering it in *AI agents* with createElement / textContent only.
 */

function golden(name: string): Record<string, unknown> {
  return JSON.parse(fs.readFileSync(path.join(GOLDEN_ROOT, 'head', name), 'utf8')) as Record<string, unknown>;
}

function read(read: 'archiveStatus' | 'archivePreview', body: Record<string, unknown>): ReadOutcome {
  return { kind: 'read', read, distro: 'Ubuntu', body };
}

/** The panel snapshot of a refreshed panel over the head goldens — typed, no cast. */
function answeredSnapshot(): Snapshot {
  return { checking: false, ...goldenOutcomes() };
}

const CAPABLE = [ARCHIVE_CAPABILITY, 'archive.preview', 'archive.run'];

function state(overrides: Partial<ArchiveState> = {}): ArchiveState {
  return { status: read('archiveStatus', golden('archive-status.json')), preview: read('archivePreview', golden('archive-preview.json')), capabilities: CAPABLE, busy: '', reading: false, asked: true, unavailable: '', cleanupFree: true, a13Offered: true, ...overrides };
}

function enabled(controls: ReturnType<typeof deriveArchive>): Record<string, boolean> {
  return Object.fromEntries(controls.buttons.map((b) => [b.id, b.enabled]));
}

test('over the goldens: the base folder the daemon reads, every enabled agent with what is due, the lock and the last run', () => {
  const controls = deriveArchive(state());
  assert.equal(controls.line, 'Archive folder: /mnt/v/ai-archive');
  assert.equal(controls.level, 'ok');
  assert.equal(controls.agents[0]?.name, 'Claude Code');
  assert.equal(controls.agents[0]?.due, '1 session · 2 files · 0.0 GB older than 14 d');
  assert.equal(controls.agents[0]?.retention, 'its own cleanup deletes after 30 d');
  assert.equal(controls.lock, 'No archive run in progress');
  assert.match(controls.lastRun, /^Last archive run: done — 1 copied, 0 removed, 0\.0 GB, /);
  assert.deepEqual(enabled(controls), { chooseArchiveFolder: true, stopArchiving: true, archiveNow: false });
});

test('no folder set: the archive reads "off", and Stop archiving is greyed', () => {
  const controls = deriveArchive(state({ status: read('archiveStatus', { ...golden('archive-status.json'), baseFolder: '' }) }));
  assert.match(controls.line, /^Archive: off — no archive folder is chosen/);
  assert.deepEqual(enabled(controls), { chooseArchiveFolder: true, stopArchiving: false, archiveNow: false });
});

test('the retention badge shows EXACTLY when the agent\'s own retention is below the effective age', () => {
  const agent = (days: number, known: boolean, age: number): Record<string, unknown> => ({ retention: { known, days }, effectiveAgeDays: age });
  assert.equal(retentionBadge(agent(30, true, 14)), '', 'retention above the age');
  assert.equal(retentionBadge(agent(14, true, 14)), '', 'equal: the archive takes them on the same day the agent would delete them');
  assert.equal(retentionBadge(agent(7, true, 14)), 'its own cleanup deletes sessions after 7 d — before the archive takes them at 14 d');
  assert.equal(retentionBadge(agent(7, false, 14)), '', 'an unknown retention says nothing it does not know');
  const preview = golden('archive-preview.json');
  const agents = (preview.agents as Record<string, unknown>[]).map((a, i) => (i === 0 ? { ...a, retention: { known: true, days: 7, source: 's' } } : a));
  assert.equal(deriveArchive(state({ preview: read('archivePreview', { ...preview, agents }) })).agents[0]?.badge, 'its own cleanup deletes sessions after 7 d — before the archive takes them at 14 d');
});

test('a daemon without the archive capability: said, and every button greyed; before any status: checking, greyed', () => {
  const old = deriveArchive(state({ capabilities: ['act.detach'] }));
  assert.equal(old.line, 'Archive: this daemon has no AI-session archive — update the daemon');
  assert.deepEqual(enabled(old), { chooseArchiveFolder: false, stopArchiving: false, archiveNow: false });
  assert.equal(NO_ARCHIVE.line, 'Archive: checking…');
  assert.deepEqual(enabled(NO_ARCHIVE), { chooseArchiveFolder: false, stopArchiving: false, archiveNow: false });
});

test('a failed archive status reads as its failure\'s label; a flow in flight shows its text and greys both buttons', () => {
  const failed = deriveArchive(state({ status: { kind: 'timedOut', timeoutMs: 20_000, read: 'archiveStatus' } }));
  assert.equal(failed.line, 'Archive: timed out');
  assert.equal(failed.level, 'unknown');
  const busy = deriveArchive(state({ busy: 'Checking the folder…' }));
  assert.equal(busy.line, 'Checking the folder…');
  assert.deepEqual(enabled(busy), { chooseArchiveFolder: false, stopArchiving: false, archiveNow: false });
});

test('a daemon string is made printable and short before it reaches the view', () => {
  const evil = deriveArchive(state({ status: read('archiveStatus', { ...golden('archive-status.json'), baseFolder: `/mnt/v/a${String.fromCharCode(0x202e)}b` }) }));
  assert.equal(evil.line.includes(String.fromCharCode(0x202e)), false);
  assert.equal(deriveArchive(state({ status: read('archiveStatus', { baseFolder: 'x'.repeat(2000) }) })).line.length < 400, true);
});

test('the page renders the archive in the AI agents section — text only — and its buttons post their bare ids, a greyed one nothing', () => {
  const view = buildPanelView(answeredSnapshot(), undefined, undefined, deriveArchive(state({ status: read('archiveStatus', { ...golden('archive-status.json'), baseFolder: '' }) })));
  const root = new Element('MAIN');
  const page = runPageScript(fs.readFileSync(PAGE_SCRIPT, 'utf8'), { panel: root });
  page.message({ type: 'view', view: structuredClone(view) });
  const section = root.one('section[data-section="aiAgents"]');
  assert.match(section.one('p[data-archive-line]').textContent, /^Archive: off/);
  assert.equal(section.all('li[data-archive-agent]').length, view.archive.agents.length);
  const before = page.posted.length;
  page.click(section.one('button[data-archive-action="stopArchiving"]'));
  assert.equal(page.posted.length, before, 'a greyed Stop archiving posts nothing');
  page.click(section.one('button[data-archive-action="chooseArchiveFolder"]'));
  assert.deepEqual(page.posted.at(-1), { type: 'chooseArchiveFolder' });
});

// ---- the code round (coai 237ecc90): findings 5 and 6 ----

test('code round #6: a FAILED preview is said, never shown as nothing due', () => {
  const controls = deriveArchive(state({ preview: { kind: 'timedOut', timeoutMs: 630_000, read: 'archivePreview' } }));
  assert.deepEqual(controls.agents, []);
  assert.equal(controls.previewNote, 'What is due: timed out');
});

test('code round #6: an agent whose figures are missing reads "unknown", never "nothing older than 0 d"', () => {
  const preview = golden('archive-preview.json');
  const agents = (preview.agents as Record<string, unknown>[]).map((a, i) => (i === 0 ? { id: 'claude-code', name: 'Claude Code', enabled: true } : a));
  const line = deriveArchive(state({ preview: read('archivePreview', { ...preview, agents }) })).agents[0];
  assert.equal(line?.due, 'what is due is unknown — update the daemon to see this');
  assert.equal(line?.badge, '');
});

test('code round #6: a daemon that was not asked for its preview says so', () => {
  assert.equal(deriveArchive(state({ preview: undefined })).previewNote, 'What is due: not asked yet');
  assert.equal(deriveArchive(state()).previewNote, '');
});

test('code round #5: while the reads are in flight the panel says the lines are the previous answer', () => {
  assert.equal(deriveArchive(state({ reading: true })).reading, 'Asking the daemon about the archive again — the lines below are its previous answer');
  assert.equal(deriveArchive(state()).reading, '');
});

// ---- the own review on Fable: finding 1 ----

test('own review #1: a panel whose status failed reads "unavailable" with its reason — never a lasting "checking…"', () => {
  const stopped = deriveArchive(state({ status: undefined, preview: undefined, capabilities: undefined, unavailable: 'WSL stopped' }));
  assert.equal(stopped.line, 'Archive: unavailable — WSL stopped');
  assert.equal(stopped.level, 'unknown');
});

test('own review #1: a daemon that answered no archive status after a refresh says so; before the first refresh it is checking', () => {
  assert.equal(deriveArchive(state({ status: undefined, asked: true })).line, 'Archive: this daemon does not report its archive status — update the daemon');
  assert.equal(deriveArchive(state({ status: undefined, asked: false })).line, 'Archive: checking…');
});
