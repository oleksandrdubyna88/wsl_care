import assert from 'node:assert/strict';
import * as fs from 'node:fs';
import { builtinModules } from 'node:module';
import * as path from 'node:path';
import { test } from 'node:test';
import * as ts from 'typescript';

import { EXTENSION_ROOT, fromExtensionRoot, shippedSources } from './support/paths';
import { importsOf, resolveRelative } from './support/sourceScan';

/**
 * W0 of `todo/PLAN_disk_health_E18.md`, RED item 9: the boundary between the extension host and the full view's page code,
 * held BOTH ways, over the SHIPPED sources (everything under `src/` but `src/test/`), read by the TypeScript parser.
 *
 * `src/webview/` is compiled for the browser (`tsconfig.webview.json`, with the DOM) and bundled into a page that has no
 * `require`; the host is compiled with NO DOM (`tsconfig.json`) and runs in Node. So nothing under `src/webview/` may import
 * `vscode`, a Node module or host code, and no host module may import `src/webview/` — a file that crossed either way would
 * type-check against the wrong environment and fail at run time, in the page or in the host. The one thing a page file may
 * import besides its own folder is the kit's COMPONENTS entry and its stylesheet: the package's root entry re-exports a host
 * half that imports `node:crypto`.
 *
 * Each scan has a known-instance companion (it still finds the sanctioned site) and a planted-instance companion (it finds a
 * violation in a fixture) — `common.testing`: a structural test that matches nothing passes forever.
 */

const WEBVIEW_DIR = 'src/webview/';
const FULL_VIEW = 'src/webview/fullView.ts';
const KIT = '@oleksandrdubyna88/vscode-webview-kit';
const KIT_ENTRIES = [`${KIT}/components`, `${KIT}/components.css`];

interface Source {
  readonly file: string;
  readonly text: string;
}

function sources(): Source[] {
  return shippedSources().map((file) => ({ file: fromExtensionRoot(file), text: fs.readFileSync(file, 'utf8') }));
}

const inWebview = (file: string): boolean => file.startsWith(WEBVIEW_DIR);

/** Whether a resolved path (a file without its extension, or a folder) is `src/webview` or under it. */
const underWebview = (resolved: string): boolean => `${resolved}/`.startsWith(WEBVIEW_DIR);

/** Whether a specifier names Node: a `node:` module or a bare built-in (`fs`, `path/posix`). */
function isNode(specifier: string): boolean {
  return specifier.startsWith('node:') || builtinModules.includes(specifier.split('/')[0] ?? '');
}

/** Why one import of a PAGE file is not allowed, or an empty string when it is. */
function whyRefused(file: string, specifier: string): string {
  if (specifier.startsWith('.')) {
    return underWebview(resolveRelative(file, specifier)) ? '' : `host code (${resolveRelative(file, specifier)})`;
  }
  if (specifier === 'vscode') {
    return 'vscode';
  }
  if (isNode(specifier)) {
    return `the Node module ${specifier}`;
  }

  return KIT_ENTRIES.includes(specifier) ? '' : `${specifier}, which is neither the kit's components entry nor its stylesheet`;
}

/** Every refused import of every page file, as `file: imports <why>`. */
function pageFindings(all: readonly Source[]): string[] {
  return all.filter((s) => inWebview(s.file)).flatMap((s) => importsOf(s.text).flatMap((specifier) => {
    const why = whyRefused(s.file, specifier);
    return why === '' ? [] : [`${s.file}: imports ${why}`];
  }));
}

/** Every host file that imports something under `src/webview/`, as `file: imports <specifier>`. */
function hostFindings(all: readonly Source[]): string[] {
  return all.filter((s) => !inWebview(s.file)).flatMap((s) => importsOf(s.text)
    .filter((specifier) => specifier.startsWith('.') && underWebview(resolveRelative(s.file, specifier)))
    .map((specifier) => `${s.file}: imports ${specifier}`));
}

test('the scan reads the page entry and the host: src/webview/fullView.ts is a shipped source, and so are the host\'s own modules', () => {
  const files = sources().map((s) => s.file);
  assert.ok(files.includes(FULL_VIEW), files.filter(inWebview).join(', '));
  assert.ok(files.includes('src/extension.ts') && files.includes('src/panel/panelProvider.ts'));
});

test('nothing under src/webview/ imports vscode, a Node module or host code — only its own folder and the kit\'s components and stylesheet', () => {
  assert.deepEqual(pageFindings(sources()), []);
});

test('the page entry does import the kit: its components and its stylesheet (the scan is alive)', () => {
  const entry = sources().find((s) => s.file === FULL_VIEW);
  assert.ok(entry !== undefined);
  const imports = importsOf(entry.text);
  assert.ok(imports.includes(`${KIT}/components`), imports.join(', '));
  assert.ok(imports.includes(`${KIT}/components.css`), imports.join(', '));
});

test('no host module imports anything under src/webview/', () => {
  assert.deepEqual(hostFindings(sources()), []);
});

test('the page scan finds a planted import of vscode, a node: module, a bare built-in, host code and a stranger package — and leaves the kit and the folder alone', () => {
  const planted: Source[] = [
    { file: 'src/webview/a.ts', text: "import * as vscode from 'vscode';" },
    { file: 'src/webview/b.ts', text: "import { readFileSync } from 'node:fs';" },
    { file: 'src/webview/c.ts', text: "const p = require('path/posix');" },
    { file: 'src/webview/d.ts', text: "import { view } from '../panel/view';" },
    { file: 'src/webview/e.ts', text: "import { left } from 'left-pad';" },
    { file: 'src/webview/f.ts', text: `import { nonce } from '${KIT}';` },
    { file: 'src/webview/ok.ts', text: `import { createTabs } from '${KIT}/components';\nimport '${KIT}/components.css';\nimport { shape } from './shape';` },
    { file: 'src/panel/not-a-page.ts', text: "import * as vscode from 'vscode';" },
  ];
  assert.deepEqual(pageFindings(planted), [
    'src/webview/a.ts: imports vscode',
    'src/webview/b.ts: imports the Node module node:fs',
    'src/webview/c.ts: imports the Node module path/posix',
    'src/webview/d.ts: imports host code (src/panel/view)',
    "src/webview/e.ts: imports left-pad, which is neither the kit's components entry nor its stylesheet",
    `src/webview/f.ts: imports ${KIT}, which is neither the kit's components entry nor its stylesheet`,
  ]);
});

test('the host scan finds a planted import of the page entry from a host module, in every relative spelling, and leaves a mention alone', () => {
  const planted: Source[] = [
    { file: 'src/extension.ts', text: "import { start } from './webview/fullView';" },
    { file: 'src/panel/x.ts', text: "import '../webview/fullView';" },
    { file: 'src/panel/y.ts', text: "const later = import('../webview/other');" },
    { file: 'src/panel/z.ts', text: "// the page is ../webview/fullView, bundled apart\nconst s = '../webview/fullView';" },
  ];
  assert.deepEqual(hostFindings(planted), ['src/extension.ts: imports ./webview/fullView', 'src/panel/x.ts: imports ../webview/fullView', 'src/panel/y.ts: imports ../webview/other']);
});

/** A tsconfig file (JSONC) as an object — read with the TypeScript parser, comments and all. */
function tsconfig(name: string): { compilerOptions: { lib: string[]; noEmit?: boolean }; include: string[]; exclude?: string[] } {
  const read = ts.readConfigFile(path.join(EXTENSION_ROOT, name), (file) => fs.readFileSync(file, 'utf8'));
  assert.equal(read.error, undefined, `${name} does not parse`);

  return read.config as { compilerOptions: { lib: string[]; noEmit?: boolean }; include: string[]; exclude?: string[] };
}

test('the host keeps "no DOM" and leaves src/webview/ out; the page has its own config WITH the DOM, and npm run typecheck runs both', () => {
  const host = tsconfig('tsconfig.json');
  assert.deepEqual(host.compilerOptions.lib, ['ES2022'], 'the host never touches a document');
  assert.ok(host.exclude?.includes('src/webview'), 'tsconfig.json excludes src/webview');
  const page = tsconfig('tsconfig.webview.json');
  assert.deepEqual(page.compilerOptions.lib, ['ES2022', 'DOM']);
  assert.ok(page.include.includes('src/webview'), 'tsconfig.webview.json includes src/webview');
  const manifest = JSON.parse(fs.readFileSync(path.join(EXTENSION_ROOT, 'package.json'), 'utf8')) as { scripts: Record<string, string> };
  assert.equal(manifest.scripts.typecheck, 'tsc -p ./ --noEmit && tsc -p tsconfig.webview.json --noEmit');
});
