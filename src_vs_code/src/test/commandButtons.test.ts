import assert from 'node:assert/strict';
import * as fs from 'node:fs';
import * as path from 'node:path';
import { test } from 'node:test';

import { ARCHIVE_LABELS, deriveArchive } from '../archive/archiveView';
import { COMMAND_BUTTONS } from '../panel/commandButtons';
import { PAGE_MESSAGE_TYPES } from '../panel/messages';
import { PAGE_ACTIONS } from '../panel/view';
import { buildPanelView } from '../panel/viewModel';
import type { Snapshot } from '../state/outcomeStore';
import { GUARD_LABELS, guardView } from '../windowsTime/guardState';
import { guardSummary, type GuardOptions } from '../windowsTime/guardTask';
import { failed, goldenOutcomes } from './support/outcomes';
import { Element, runPageScript } from './support/pageHarness';
import { PAGE_SCRIPT } from './support/paths';

/**
 * The owner's standing rule (2026-10-09): every extension action is reachable by a BUTTON in the panel UI — a
 * command-palette entry may duplicate a button, never be the only way. Each command `package.json` contributes must
 * have a button where `COMMAND_BUTTONS` says, and that button must really exist: a page action the page renders and the
 * host accepts, or an icon in the panel view's own title bar.
 */

interface Manifest {
  readonly contributes: {
    readonly commands: readonly { readonly command: string }[];
    readonly menus?: { readonly 'view/title'?: readonly { readonly command: string; readonly when?: string }[] };
    readonly views?: Readonly<Record<string, readonly { readonly id: string }[]>>;
  };
}

const MANIFEST = JSON.parse(fs.readFileSync(path.join(__dirname, '..', '..', 'package.json'), 'utf8')) as Manifest;
const PANEL_VIEW = 'wslCare.panel';
const COMMANDS = MANIFEST.contributes.commands.map((c) => c.command);

test('every contributed command has a button in the panel UI — the palette is never the only way', () => {
  const without = COMMANDS.filter((command) => COMMAND_BUTTONS[command] === undefined);
  assert.deepEqual(without, [], `commands with no panel button: ${without.join(', ')}`);
  const stale = Object.keys(COMMAND_BUTTONS).filter((command) => !COMMANDS.includes(command));
  assert.deepEqual(stale, [], `buttons declared for commands the manifest no longer contributes: ${stale.join(', ')}`);
});

/** What the page's buttons post when pressed, over states that between them show every button the host can ask for: the
 * REAL view model and guard view build each view, `media/panel.js` renders it in the strict harness, every button is
 * clicked (coai code round: an allowed action is no proof that a button is rendered). */
function postedByPageButtons(): Set<string> {
  const failedWith = (kind: string): Snapshot => ({ status: failed('status', { kind, distro: 'Ubuntu' } as never), preview: undefined, doctor: undefined, checking: false });
  const verdicts = { status: { kind: 'answered', distro: 'Ubuntu', answer: { verb: 'status', body: { verdicts: [{ id: 'clock.timeService', level: 'critical' }] } } }, preview: undefined, doctor: undefined, checking: false } as unknown as Snapshot;
  const local = (iso: string): string => iso;
  const guards = [
    guardView({ kind: 'absent', channel: 'enabled' }, GUARD_OPTIONS, undefined, local),
    guardView({ kind: 'present', enabled: true, lastRunUtc: 'never', lastResult: 0, summary: guardSummary(GUARD_OPTIONS), channel: 'enabled' }, GUARD_OPTIONS, undefined, local),
  ];
  const views = [
    buildPanelView(failedWith('stopped')),
    buildPanelView(failedWith('notInstalled')),
    buildPanelView(verdicts),
    ...guards.map((guard) => buildPanelView({ checking: false, ...goldenOutcomes() } as Snapshot, undefined, guard)),
    // E10.S1: an archiving daemon with a base folder set — both archive buttons enabled.
    buildPanelView({ checking: false, ...goldenOutcomes() } as Snapshot, undefined, undefined, deriveArchive({ status: { kind: 'read', read: 'archiveStatus', distro: 'Ubuntu', body: { schemaVersion: 1, baseFolder: '/mnt/v/a' } }, preview: undefined, capabilities: ['archive.checkBase'], busy: '' })),
  ];
  const posted = new Set<string>();
  for (const view of views) {
    const root = new Element('MAIN');
    const page = runPageScript(fs.readFileSync(PAGE_SCRIPT, 'utf8'), { panel: root });
    page.message({ type: 'view', view: structuredClone(view) });
    for (const button of [...root.all('button[data-action]'), ...root.all('button[data-guard-action]'), ...root.all('button[data-archive-action]')]) {
      page.click(button);
    }
    for (const message of page.posted) {
      posted.add((message as { type: string }).type);
    }
  }
  return posted;
}

const GUARD_OPTIONS: GuardOptions = { setAutomaticStart: true, everyHours: 4, minMinutesBetweenStarts: 10, delaySeconds: 60, timeLimitMinutes: 5 };

test('a page button is one the page renders, the host accepts, and that runs the command\'s own operation', () => {
  const rendered = postedByPageButtons();
  for (const [command, button] of Object.entries(COMMAND_BUTTONS)) {
    if (button.where !== 'page') {
      continue;
    }
    assert.ok(rendered.has(button.action), `${command}: no rendered page button posts '${button.action}' (pressed: ${[...rendered].sort().join(', ')})`);
    assert.ok((PAGE_ACTIONS as readonly string[]).includes(button.action) || button.action in GUARD_LABELS || button.action in ARCHIVE_LABELS, `${command}: '${button.action}' is no page action`);
    assert.ok((PAGE_MESSAGE_TYPES as readonly string[]).includes(button.action), `${command}: the host does not accept '${button.action}'`);
    assert.equal(command, `wslCare.${button.action}`, `${command}: its button posts '${button.action}', another operation`);
  }
});

test('a title-bar button is in the panel view\'s own title bar, and only the panel\'s opener has none of its own', () => {
  const titleBar = (MANIFEST.contributes.menus?.['view/title'] ?? []).filter((item) => item.when === `view == ${PANEL_VIEW}`).map((item) => item.command);
  for (const [command, button] of Object.entries(COMMAND_BUTTONS)) {
    if (button.where === 'title') {
      assert.ok(titleBar.includes(command), `${command}: not in the panel's title bar (view/title, when view == ${PANEL_VIEW})`);
    }
  }
  const openers = Object.entries(COMMAND_BUTTONS).filter(([, button]) => button.where === 'opener').map(([command]) => command);
  assert.deepEqual(openers, ['wslCare.openPanel'], 'only the command that opens the panel relies on its activity-bar icon');
  const views = Object.values(MANIFEST.contributes.views ?? {}).flat().map((view) => view.id);
  assert.ok(views.includes(PANEL_VIEW), 'the panel is a contributed view, so its activity-bar icon exists');
});
