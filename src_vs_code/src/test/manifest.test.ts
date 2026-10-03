import assert from 'node:assert/strict';
import * as fs from 'node:fs';
import * as path from 'node:path';
import { test } from 'node:test';

import { DISTRO_NAME } from '../wsl/distros';
import { EXTENSION_ROOT, REPOSITORY_ROOT } from './support/paths';

/**
 * The manifest E5.S1 owes (plan §16 E5.S1, §15f #5 / #12, §15g M3): what VS Code reads from `package.json` decides
 * where the extension runs, what a workspace may steer and which API it may use — none of it is checked by a compiler.
 */

interface Setting {
  readonly type: string;
  readonly default: unknown;
  readonly scope?: string;
  readonly minimum?: number;
  readonly pattern?: string;
}

interface Manifest {
  readonly name: string;
  readonly displayName: string;
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

/** The placeholder the owner replaces with the permanent Marketplace publisher id at the E5 live gate, step 1. */
const PUBLISHER_PLACEHOLDER = 'publisher-tbd';

test('identity: WSL Care, version 0.0.0 until release-please, a preview, MIT', () => {
  assert.equal(manifest.name, 'wsl-care');
  assert.equal(manifest.displayName, 'WSL Care');
  assert.equal(manifest.version, '0.0.0');
  assert.equal(manifest.preview, true);
  assert.equal(manifest.license, 'MIT');
  assert.equal(manifest.publisher, PUBLISHER_PLACEHOLDER, 'the publisher id is created by the owner (E5 live gate, step 1)');
});

test('the API floor is 1.85.0 and @types/vscode is held EXACTLY at it — newer typings would let code use API 1.85 lacks', () => {
  assert.equal(manifest.engines.vscode, '^1.85.0');
  assert.equal(manifest.devDependencies['@types/vscode'], '1.85.0');
});

test('it runs on the Windows side, in untrusted and virtual workspaces, activated after startup, from the bundle', () => {
  assert.deepEqual(manifest.extensionKind, ['ui']);
  assert.equal(manifest.capabilities.untrustedWorkspaces.supported, true);
  assert.equal(manifest.capabilities.virtualWorkspaces, true);
  assert.deepEqual(manifest.activationEvents, ['onStartupFinished']);
  assert.equal(manifest.main, './dist/extension.js');
});

test('every wslCare setting is application-scoped — a cloned repository\'s .vscode/settings.json cannot steer it', () => {
  const names = Object.keys(settings);
  assert.deepEqual(names.sort(), ['wslCare.distro', 'wslCare.refreshSeconds']);
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

test('wslCare.refreshSeconds: 120 by default, never below 30', () => {
  const refresh = settings['wslCare.refreshSeconds'];
  assert.equal(refresh?.type, 'integer');
  assert.equal(refresh?.default, 120);
  assert.equal(refresh?.minimum, 30);
});

test('no runtime dependency ships, and every development dependency is pinned to one version', () => {
  assert.deepEqual(manifest.dependencies ?? {}, {});
  for (const [name, version] of Object.entries(manifest.devDependencies)) {
    assert.match(version, /^\d+\.\d+\.\d+$/, `${name}@${version} is a range`);
  }
});

test('the bundle is CommonJS for node 18 with vscode left external and no source map', () => {
  const bundle = manifest.scripts.bundle ?? '';
  for (const flag of ['--bundle', '--external:vscode', '--platform=node', '--target=node18', '--format=cjs', '--outfile=dist/extension.js']) {
    assert.ok(bundle.split(' ').includes(flag), `bundle script lacks ${flag}: ${bundle}`);
  }
  assert.equal(/--sourcemap|--sources-content/.test(bundle), false);
});

test('the extension carries the repository\'s licence, byte for byte', () => {
  assert.deepEqual(fs.readFileSync(path.join(EXTENSION_ROOT, 'LICENSE')), fs.readFileSync(path.join(REPOSITORY_ROOT, 'LICENSE')));
});
