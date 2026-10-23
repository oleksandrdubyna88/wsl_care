import assert from 'node:assert/strict';
import * as fs from 'node:fs';
import * as path from 'node:path';
import { test } from 'node:test';
import * as ts from 'typescript';

import { MAX_REFRESH_SECONDS } from '../poll/poller';
import { DISTRO_NAME } from '../wsl/distros';
import { bundleEntries, webviewEntries } from './support/bundleEntries';
import { decodePng } from './support/iconPng';
import { EXTENSION_ROOT, REPOSITORY_ROOT } from './support/paths';
import { NUMBER_NAMES, NUMBER_SETTINGS } from '../settings/numbers';
import { listLines, PUBLISHER_PLACEHOLDER } from './support/vsixCheck';

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
  assert.equal(manifest.scripts.package, 'vsce package --no-dependencies --target win32-x64', 'E7.S5a: ONE targeted package — Linux, macOS and Windows on Arm keep the last universal release (0.5.0 or earlier)');
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
  assert.deepEqual(manifest.activationEvents, ['onStartupFinished', 'onWebviewPanel:wslCare.logs', 'onWebviewPanel:wslCare.archive'], 'E6.S4 / E10.S2a: a Logs or Archive page open at a reload is restored by its serializer');
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

/** The exact kit release the full view is built from (W0 of `todo/PLAN_disk_health_E18.md`): bundled by esbuild, so a development dependency. */
const KIT = '@oleksandrdubyna88/vscode-webview-kit';

test('W0 item 1: the kit is a development dependency pinned to EXACTLY 0.2.0 — no ^, ~, range or tag — and package.json still has no dependencies', () => {
  assert.equal(manifest.devDependencies[KIT], '0.2.0');
  const raw = JSON.parse(fs.readFileSync(path.join(EXTENSION_ROOT, 'package.json'), 'utf8')) as Record<string, unknown>;
  assert.equal('dependencies' in raw, false, 'the bundle carries everything: nothing is a runtime dependency');
});

/** The two lines of `.npmrc`: the scope's registry and a token that is read from the environment, never written down. */
const NPMRC_LINES = ['@oleksandrdubyna88:registry=https://npm.pkg.github.com', '//npm.pkg.github.com/:_authToken=${NODE_AUTH_TOKEN}'];

test('W0 item 2: .npmrc is exactly the scope registry and the token read from NODE_AUTH_TOKEN — no literal token — and it never ships', () => {
  const text = fs.readFileSync(path.join(EXTENSION_ROOT, '.npmrc'), 'utf8');
  assert.deepEqual(text.split(/\r?\n/).filter((line) => line.trim() !== ''), NPMRC_LINES);
  assert.doesNotMatch(text, /_authToken=(?!\$\{NODE_AUTH_TOKEN\}\s*$)/m, 'the token is never a literal');
  const listed = listLines(fs.readFileSync(path.join(EXTENSION_ROOT, 'vsix-files.txt'), 'utf8'));
  assert.equal(listed.some((file) => file.includes('npmrc')), false, '.npmrc is not in vsix-files.txt');
  const ignore = listLines(fs.readFileSync(path.join(EXTENSION_ROOT, '.vscodeignore'), 'utf8'));
  assert.equal(ignore.some((line) => line.startsWith('!') && line.includes('npmrc')), false, '.vscodeignore never lets .npmrc back in');
});

test('W0 item 4 (code round: one list): scripts/bundle.mjs loops buildSync over scripts/bundle-entries.json, the ONE list of bundles — a host bundle and a webview bundle', () => {
  const source = fs.readFileSync(path.join(EXTENSION_ROOT, 'scripts', 'bundle.mjs'), 'utf8');
  assert.equal((source.match(/\bbuildSync\(/g) ?? []).length, 1, 'ONE buildSync call, looped over the entries');
  assert.match(source, /bundle-entries\.json/);
  assert.deepEqual(bundleEntries().map((entry) => entry.kind), ['host', 'webview'], 'the extension host and the full view');
});

test('W0 item 4: EVERY webview entry is an IIFE for the browser (the Chromium of VS Code 1.85), nothing external, no source map, not minified — the full view is src/webview/fullView.ts to dist/webview/full.js', () => {
  const entries = webviewEntries();
  assert.ok(entries.length >= 1, 'the scan sees a webview entry');
  for (const entry of entries) {
    assert.equal(entry.bundle, true, entry.outfile);
    assert.equal(entry.format, 'iife', entry.outfile);
    assert.equal(entry.platform, 'browser', entry.outfile);
    assert.equal(entry.target, 'chrome114', `${entry.outfile}: Electron 25 / Chromium 114 is what VS Code 1.85 ships`);
    assert.equal(entry.sourcemap, false, entry.outfile);
    assert.equal(entry.minify, false, entry.outfile);
    assert.equal(entry.external, undefined, `${entry.outfile}: nothing is left external: the page has no require`);
    assert.ok(entry.entryPoints.every((file) => file.startsWith('src/webview/')), `${entry.outfile}: a webview entry lives under src/webview/`);
    assert.ok(entry.outfile.startsWith('dist/webview/'), entry.outfile);
  }
  const full = entries.find((entry) => entry.outfile === 'dist/webview/full.js');
  assert.deepEqual(full?.entryPoints, ['src/webview/fullView.ts']);
});

test('the bundle is CommonJS for node 18 with vscode left external, no source map, and the build stamp (scripts/bundle.mjs)', () => {
  assert.equal(manifest.scripts.bundle, 'node scripts/clean.mjs dist && node scripts/bundle.mjs');
  const host = bundleEntries().find((entry) => entry.kind === 'host');
  assert.ok(host !== undefined);
  assert.deepEqual(host, { kind: 'host', entryPoints: ['src/extension.ts'], outfile: 'dist/extension.js', bundle: true, external: ['vscode'], format: 'cjs', platform: 'node', target: 'node18', sourcemap: false, minify: false });
  assert.deepEqual(definedKeys(), ['host'], 'the build stamp is defined for the host bundle ONLY');
  assert.match(fs.readFileSync(path.join(EXTENSION_ROOT, 'scripts', 'bundle.mjs'), 'utf8'), /DEFINES = \{ host: \{ WSL_CARE_BUILD_STAMP/, 'and that definition is the stamp');
});

test('the build stamp is in the host bundle only: the webview bundle carries no "wsl-care-build"', () => {
  const webview = fs.readFileSync(path.join(EXTENSION_ROOT, 'dist', 'webview', 'full.js'), 'utf8');
  assert.equal(webview.includes('wsl-care-build'), false, 'dist/webview/full.js is not stamped');
  assert.ok(fs.readFileSync(path.join(EXTENSION_ROOT, 'dist', 'extension.js'), 'utf8').includes('wsl-care-build'), 'the scan is alive: the host bundle is');
});

/** The keys of the `DEFINES` object in scripts/bundle.mjs — one key per bundle kind that is stamped — read with the TypeScript parser. */
function definedKeys(): string[] {
  const file = ts.createSourceFile('bundle.mjs', fs.readFileSync(path.join(EXTENSION_ROOT, 'scripts', 'bundle.mjs'), 'utf8'), ts.ScriptTarget.ES2022, true, ts.ScriptKind.JS);
  const keys: string[] = [];
  const visit = (node: ts.Node): void => {
    if (ts.isVariableDeclaration(node) && node.name.getText(file) === 'DEFINES' && node.initializer !== undefined && ts.isObjectLiteralExpression(node.initializer)) {
      keys.push(...node.initializer.properties.map((property) => property.name?.getText(file) ?? ''));
    }
    ts.forEachChild(node, visit);
  };
  visit(file);

  return keys;
}

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

test('the contributed surface: the panel view and twenty-two argument-free commands (E5 + Logs of E6.S4 + Start Windows Time + the Windows Time guard + the install and remove of the Windows tasks, WR-E3 + the run-now of WR-E4 + the install, preview and clean-now of the cleanup task, WR-T3 + the archive folder of E10.S1 + Archive now of E10.S1b + Enable a disabled GPU + Open archive of E10.S2a + Stop client-gone relays of S7b.2b), no URI handler', () => {
  assert.deepEqual(contributed.commands.map((c) => c.command), ['wslCare.openPanel', 'wslCare.refresh', 'wslCare.startWsl', 'wslCare.installDaemon', 'wslCare.startWindowsTime', 'wslCare.installWindowsTimeGuard', 'wslCare.removeWindowsTimeGuard', 'wslCare.installWindowsWatchTask', 'wslCare.installWindowsArchiveTask', 'wslCare.removeWindowsTasks', 'wslCare.runWindowsWatchTask', 'wslCare.runWindowsArchiveTask', 'wslCare.installWindowsCleanupTask', 'wslCare.previewWindowsTempCleanup', 'wslCare.runWindowsCleanupTask', 'wslCare.chooseArchiveFolder', 'wslCare.stopArchiving', 'wslCare.archiveNow', 'wslCare.enableGpu', 'wslCare.openArchive', 'wslCare.stopRelays', 'wslCare.openLogs']);
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
