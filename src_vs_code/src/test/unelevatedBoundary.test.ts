import assert from 'node:assert/strict';
import * as fs from 'node:fs';
import * as path from 'node:path';
import { test } from 'node:test';

import { fromExtensionRoot, shippedSources } from './support/paths';
import { importsOf } from './support/sourceScan';

/**
 * The unelevated side stands on shared modules, never on the elevated one (WR-E3, the coai code round efc512d4, findings 1, 2 and 5): what the
 * Windows tasks and the registration script need of PowerShell — the encoded command, the quotable path, the module path, the engine vocabulary,
 * the command-line bound — lives in modules BOTH sides import (\`powershell/commandLine.ts\`, \`modulePath.ts\`, \`engineTypes.ts\`, \`engineSource.ts\`).
 * Nothing REACHABLE (by relative imports, transitively) from \`src/windowsTasks/\` or \`scheduler/registerScript.ts\` imports the elevated launcher or the
 * elevated engine's module: a story that runs as the user may not be one refactor away from a \`runas\`.
 */

const FORBIDDEN = ['src/powershell/elevatedLauncher.ts', 'src/powershell/elevatedEngine.ts'];

interface Source {
  readonly file: string;
  readonly text: string;
}

function sources(): Source[] {
  return shippedSources().map((file) => ({ file: fromExtensionRoot(file), text: fs.readFileSync(file, 'utf8') }));
}

/** The shipped modules a source imports by a RELATIVE specifier, resolved to `src/…ts` names. */
function importsFrom(source: Source): string[] {
  const dir = path.posix.dirname(source.file);

  return importsOf(source.text).filter((m) => m.startsWith('.')).map((m) => `${path.posix.normalize(path.posix.join(dir, m))}.ts`);
}

/** Every module reachable from `roots`, each with the chain that reached it (the first found). */
function reachable(all: readonly Source[], roots: readonly string[]): Map<string, string[]> {
  const byFile = new Map(all.map((s) => [s.file, s]));
  const seen = new Map<string, string[]>(roots.map((root) => [root, [root]]));
  const queue = [...roots];
  while (queue.length > 0) {
    const file = queue.shift() as string;
    const source = byFile.get(file);
    for (const next of source === undefined ? [] : importsFrom(source)) {
      if (!seen.has(next)) {
        seen.set(next, [...(seen.get(file) ?? [file]), next]);
        queue.push(next);
      }
    }
  }

  return seen;
}

function roots(all: readonly Source[]): string[] {
  return all.filter((s) => s.file.startsWith('src/windowsTasks/') || s.file === 'src/scheduler/registerScript.ts' || s.file === 'src/scheduler/runScript.ts').map((s) => s.file);
}

/** The chains from a root to a forbidden module, as readable lines. */
function offenders(all: readonly Source[], from: readonly string[]): string[] {
  const seen = reachable(all, from);

  return FORBIDDEN.flatMap((module) => (seen.has(module) ? [(seen.get(module) ?? []).join(' → ')] : []));
}

test('nothing reachable from windowsTasks/ or the registration module imports the elevated launcher or the elevated engine module', () => {
  const all = sources();
  assert.ok(roots(all).length >= 10 && roots(all).includes('src/scheduler/runScript.ts'), 'the scan reads the Windows tasks\' modules');
  assert.deepEqual(offenders(all, roots(all)), []);
});

test('the reachability scan is alive: it follows a planted chain, and the elevated modules themselves are shipped sources', () => {
  const all = sources();
  assert.ok(FORBIDDEN.every((module) => all.some((s) => s.file === module)), 'the forbidden modules exist');
  const planted: Source[] = [
    { file: 'src/windowsTasks/a.ts', text: "import { b } from '../shared/b';" },
    { file: 'src/shared/b.ts', text: "import { c } from '../powershell/elevatedEngine';" },
    { file: 'src/powershell/elevatedEngine.ts', text: 'export const c = 1;' },
  ];
  assert.deepEqual(offenders(planted, ['src/windowsTasks/a.ts']), ['src/windowsTasks/a.ts → src/shared/b.ts → src/powershell/elevatedEngine.ts']);
  assert.deepEqual(offenders([{ file: 'src/windowsTasks/a.ts', text: "// import '../powershell/elevatedLauncher';\nexport const a = 1;" }], ['src/windowsTasks/a.ts']), []);
});
