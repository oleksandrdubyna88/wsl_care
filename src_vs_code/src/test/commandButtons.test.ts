import assert from 'node:assert/strict';
import * as fs from 'node:fs';
import * as path from 'node:path';
import { test } from 'node:test';

import { COMMAND_BUTTONS } from '../panel/commandButtons';
import { PAGE_MESSAGE_TYPES } from '../panel/messages';
import { PAGE_ACTIONS } from '../panel/view';
import { GUARD_LABELS } from '../windowsTime/guardState';

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

test('a page button is one the page renders, the host accepts, and that runs the command\'s own operation', () => {
  const rendered = new Set<string>([...PAGE_ACTIONS, ...Object.keys(GUARD_LABELS)]);
  for (const [command, button] of Object.entries(COMMAND_BUTTONS)) {
    if (button.where !== 'page') {
      continue;
    }
    assert.ok(rendered.has(button.action), `${command}: '${button.action}' is no button the page renders`);
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
