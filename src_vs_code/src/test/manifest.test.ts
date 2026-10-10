import assert from 'node:assert/strict';
import * as fs from 'node:fs';
import * as path from 'node:path';
import { test } from 'node:test';
import * as ts from 'typescript';

import { MAX_REFRESH_SECONDS } from '../poll/poller';
import { DISTRO_NAME } from '../wsl/distros';
import { decodePng } from './support/iconPng';
import { EXTENSION_ROOT, REPOSITORY_ROOT } from './support/paths';
import { NUMBER_NAMES, NUMBER_SETTINGS } from '../settings/numbers';
import { PUBLISHER_PLACEHOLDER } from './support/vsixCheck';

/**
 * The manifest E5.S1 owes (plan §16 E5.S1, §15f #5 / #12, §15g M3): what VS Code reads from `package.json` decides
 * where the extension runs, what a workspace may steer and which API it may use — none of it is checked by a compiler.
 */

interface Setting {
  readonly type: string;
  readonly default: unknown;
  readonly scope?: string;
  readonly minimum?: number;
  readonly maximum?: number;
  readonly pattern?: string;
}

interface Manifest {
  readonly name: string;
  readonly displayName: string;
  readonly description: string;
  readonly version: string;
  readonly publisher: string;
  readonly preview: boolean;
  readonly license: string;
  readonly main: string;
  readonly engines: Readonly<Record<string, string>>;
  readonly extensionKind: readonly string[];
  readonly activationEvents: readonly string[];
  readonly capabilities: { readonly untrustedWorkspaces: { readonly supported: boolean | string }; readonly virtualWorkspaces: boolean };
  readonly contributes: { readonly configuration: { readonly properties: Readonly<Record<string, Setting>> } };
  readonly scripts: Readonly<Record<string, string>>;
  readonly dependencies?: Readonly<Record<string, string>>;
  readonly devDependencies: Readonly<Record<string, string>>;
}

const manifest = JSON.parse(fs.readFileSync(path.join(EXTENSION_ROOT, 'package.json'), 'utf8')) as Manifest;
const settings = manifest.contributes.configuration.properties;

/** A Marketplace publisher id: lower-case letters, digits and dashes (the placeholder has the same shape). */
const PUBLISHER_ID = /^[a-z0-9][a-z0-9-]*$/;

test('identity: AI OS Care, the permanent id ai-os-care (owner decision 2026-10-06), an x.y.z version (0.0.0 until release-please bumps it), a preview, MIT', () => {
  assert.equal(manifest.name, 'ai-os-care');
  assert.equal(manifest.displayName, 'AI OS Care');
  assert.match(manifest.version, /^\d+\.\d+\.\d+$/);
  assert.equal(manifest.preview, true);
  assert.equal(manifest.license, 'MIT');
});

test('the publisher is the owner\'s Marketplace publisher remsoftdev (E5 live gate, step 1, 2026-10-06) — never the placeholder a RELEASE refuses (vsixCheck.test.ts, release-extension-guard.sh)', () => {
  assert.match(manifest.publisher, PUBLISHER_ID);
  assert.equal(manifest.publisher, 'remsoftdev');
  assert.notEqual(manifest.publisher, PUBLISHER_PLACEHOLDER);
});

interface Listing {
  readonly icon: string;
  readonly pricing: string;
  readonly repository: { readonly url: string };
  readonly bugs: { readonly url: string };
  readonly homepage: string;
  readonly categories: readonly string[];
  readonly keywords: readonly string[];
}

test('the Marketplace listing (plan §15f #6): a PNG icon of at least 128 px, Free, https repository / bugs / homepage, categories and keywords', () => {
  const listing = manifest as unknown as Listing;
  assert.match(listing.icon, /\.png$/);
  const icon = decodePng(fs.readFileSync(path.join(EXTENSION_ROOT, listing.icon)));
  assert.ok(icon.width >= 128 && icon.height >= 128, `${icon.width}x${icon.height}`);
  assert.equal(listing.pricing, 'Free');
  for (const url of [listing.repository.url, listing.bugs.url, listing.homepage]) {
    assert.match(url, /^https:\/\/github\.com\/oleksandrdubyna88\/wsl_care/, url);
  }
  assert.ok(listing.categories.length > 0 && listing.keywords.length > 0 && listing.keywords.length <= 30);
});

test('@vscode/vsce is a pinned development dependency — the one packager, the same in CI and in the release', () => {
  assert.match(manifest.devDependencies['@vscode/vsce'] ?? '', /^\d+\.\d+\.\d+$/);
  assert.equal(manifest.scripts.package, 'vsce package --no-dependencies');
  assert.equal(manifest.scripts['vscode:prepublish'], 'npm run bundle', 'vsce runs the bundle before it packs — the .vsix never carries a stale one');
});

test('the API floor is 1.85.0 and @types/vscode is held EXACTLY at it — newer typings would let code use API 1.85 lacks', () => {
  assert.equal(manifest.engines.vscode, '^1.85.0');
  assert.equal(manifest.devDependencies['@types/vscode'], '1.85.0');
});

test('it runs on the Windows side, in untrusted and virtual workspaces, activated after startup, from the bundle', () => {
  assert.deepEqual(manifest.extensionKind, ['ui']);
  assert.equal(manifest.capabilities.untrustedWorkspaces.supported, true);
  assert.equal(manifest.capabilities.virtualWorkspaces, true);
  assert.deepEqual(manifest.activationEvents, ['onStartupFinished', 'onWebviewPanel:wslCare.logs'], 'E6.S4: a Logs page open at a reload is restored by its serializer');
  assert.equal(manifest.main, './dist/extension.js');
});

test('every wslCare setting is application-scoped — a cloned repository\'s .vscode/settings.json cannot steer it', () => {
  const names = Object.keys(settings);
  assert.deepEqual(names.sort(), ['wslCare.distro', 'wslCare.refreshSeconds', 'wslCare.windowsTime.setAutomaticStart', ...NUMBER_NAMES.map((name) => `wslCare.${NUMBER_SETTINGS[name].key}`)].sort(), 'the two E5 settings, the Windows Time fix switch (PLAN_windows_time_guard.md D7) and the number table (numbers.test.ts)');
  for (const name of names) {
    assert.equal(settings[name]?.scope, 'application', name);
  }
});

test('wslCare.distro: empty means WSL\'s default; the schema pattern is the client\'s own pattern', () => {
  const distro = settings['wslCare.distro'];
  assert.equal(distro?.type, 'string');
  assert.equal(distro?.default, '');
  assert.equal(distro?.pattern, `^$|${DISTRO_NAME.source}`);
});

test('wslCare.refreshSeconds: 120 by default, never below 30, never above a day (setInterval overflows past 2^31-1 ms)', () => {
  const refresh = settings['wslCare.refreshSeconds'];
  assert.equal(refresh?.type, 'integer');
  assert.equal(refresh?.default, 120);
  assert.equal(refresh?.minimum, 30);
  assert.equal(refresh?.maximum, MAX_REFRESH_SECONDS, 'the schema and the clamp say the same day');
});

test('no runtime dependency ships, and every development dependency is pinned to one version', () => {
  assert.deepEqual(manifest.dependencies ?? {}, {});
  for (const [name, version] of Object.entries(manifest.devDependencies)) {
    assert.match(version, /^\d+\.\d+\.\d+$/, `${name}@${version} is a range`);
  }
});

/** The options object scripts/bundle.mjs hands esbuild's buildSync, read with the TypeScript parser: name → source text. */
function bundleOptions(): Map<string, string> {
  const file = ts.createSourceFile('bundle.mjs', fs.readFileSync(path.join(EXTENSION_ROOT, 'scripts', 'bundle.mjs'), 'utf8'), ts.ScriptTarget.ES2022, true, ts.ScriptKind.JS);
  const options = new Map<string, string>();
  const visit = (node: ts.Node): void => {
    if (ts.isCallExpression(node) && node.expression.getText(file) === 'buildSync' && node.arguments[0] !== undefined && ts.isObjectLiteralExpression(node.arguments[0])) {
      for (const property of node.arguments[0].properties) {
        if (ts.isPropertyAssignment(property)) {
          options.set(property.name.getText(file), property.initializer.getText(file));
        }
      }
    }
    ts.forEachChild(node, visit);
  };
  visit(file);

  return options;
}

test('the bundle is CommonJS for node 18 with vscode left external, no source map, and the build stamp (scripts/bundle.mjs)', () => {
  assert.equal(manifest.scripts.bundle, 'node scripts/clean.mjs dist && node scripts/bundle.mjs');
  const options = bundleOptions();
  assert.equal(options.get('bundle'), 'true');
  assert.equal(options.get('external'), "['vscode']");
  assert.equal(options.get('platform'), "'node'");
  assert.equal(options.get('target'), "'node18'");
  assert.equal(options.get('format'), "'cjs'");
  assert.equal(options.get('outfile'), "'dist/extension.js'");
  assert.equal(options.get('sourcemap'), 'false');
  assert.equal(options.has('sourcesContent'), false);
  assert.match(options.get('define') ?? '', /WSL_CARE_BUILD_STAMP/);
});

test('the extension carries the repository\'s licence, byte for byte', () => {
  assert.deepEqual(fs.readFileSync(path.join(EXTENSION_ROOT, 'LICENSE')), fs.readFileSync(path.join(REPOSITORY_ROOT, 'LICENSE')));
});

interface Contributions {
  readonly commands: readonly { readonly command: string; readonly title: string }[];
  readonly views: Readonly<Record<string, readonly { readonly id: string; readonly type?: string }[]>>;
  readonly viewsContainers: { readonly activitybar: readonly { readonly id: string; readonly icon: string }[] };
  readonly menus: Readonly<Record<string, readonly { readonly command: string }[]>>;
}

const contributed = (manifest as unknown as { contributes: Contributions }).contributes;

test('the contributed surface: the panel view and eleven argument-free commands (E5 + Logs of E6.S4 + Start Windows Time + the Windows Time guard + the archive folder of E10.S1 + Archive now of E10.S1b), no URI handler', () => {
  assert.deepEqual(contributed.commands.map((c) => c.command), ['wslCare.openPanel', 'wslCare.refresh', 'wslCare.startWsl', 'wslCare.installDaemon', 'wslCare.startWindowsTime', 'wslCare.installWindowsTimeGuard', 'wslCare.removeWindowsTimeGuard', 'wslCare.chooseArchiveFolder', 'wslCare.stopArchiving', 'wslCare.archiveNow', 'wslCare.openLogs']);
  assert.deepEqual(contributed.menus['view/title']?.map((m) => m.command), ['wslCare.refresh', 'wslCare.openLogs'], 'E6.S4: Logs in the panel title (§7.2)');
  assert.deepEqual(contributed.views.wslCare, [{ type: 'webview', id: 'wslCare.panel', name: 'AI OS Care' }]);
  assert.equal(contributed.viewsContainers.activitybar[0]?.id, 'wslCare');
  assert.ok(fs.existsSync(path.join(EXTENSION_ROOT, contributed.viewsContainers.activitybar[0]?.icon ?? '')), 'the activity-bar icon ships');
  assert.ok(manifest.activationEvents.every((e) => !e.startsWith('onUri')), 'no URI handler (plan §15f #2)');
  for (const menu of Object.values(contributed.menus)) {
    for (const item of menu) {
      assert.ok(contributed.commands.some((c) => c.command === item.command), item.command);
    }
  }
});

test('E6.S2 (plan §15j M1 (3)): the description no longer calls the extension read-only — cleanups run only after a confirmation', () => {
  assert.equal(/read-only/i.test(manifest.description), false, manifest.description);
  assert.match(manifest.description, /only after you confirm/);
});

test('E6.S2 (plan §15j m5): untrusted workspaces stay supported, with the reason written in the README', () => {
  assert.equal(manifest.capabilities.untrustedWorkspaces.supported, true);
  const readme = fs.readFileSync(path.join(EXTENSION_ROOT, 'README.md'), 'utf8');
  const section = (readme.split(/^## /m).find((s) => s.startsWith('Workspace trust\n')) ?? '').replace(/\s+/g, ' ');
  assert.match(section, /no input from the workspace reaches a root call/);
  assert.match(section, /"scope": "application"/);
});
