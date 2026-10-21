import assert from 'node:assert/strict';
import * as fs from 'node:fs';
import * as path from 'node:path';
import { test } from 'node:test';

import { fromExtensionRoot, shippedSources } from './support/paths';
import { importsOf } from './support/sourceScan';

/**
 * Ownership of the ONE Task Scheduler read (WR-E2, the coai code round 4fc3f328, findings 1, 8, 14): the query, its answer's parser,
 * the host that holds it in flight and the canonical summary live in `src/scheduler/` — a PEER that `windowsTime/` (the guard) and
 * `windowsTasks/` (the Windows tasks) both consume. The peer imports neither of them, and neither of them imports the other: the
 * guard does not own the read the tasks' part depends on, and the tasks' part does not reach into the guard.
 */

interface Source {
  readonly file: string;
  readonly text: string;
}

function sources(): Source[] {
  return shippedSources().map((file) => ({ file: fromExtensionRoot(file), text: fs.readFileSync(file, 'utf8') }));
}

/** The folders a source imports from by a RELATIVE specifier, as `src/<folder>/` prefixes. */
function importedFolders(source: Source): string[] {
  const dir = path.posix.dirname(source.file);

  return importsOf(source.text)
    .filter((m) => m.startsWith('.'))
    .map((m) => path.posix.normalize(path.posix.join(dir, m)))
    .map((resolved) => resolved.split('/').slice(0, 2).join('/'));
}

function within(all: readonly Source[], folder: string): Source[] {
  return all.filter((s) => s.file.startsWith(`${folder}/`));
}

test('the one read lives in src/scheduler/: its query script, answer parser, host and canonical summary', () => {
  const files = within(sources(), 'src/scheduler').map((s) => s.file);
  for (const expected of ['src/scheduler/queryScript.ts', 'src/scheduler/schedulerAnswer.ts', 'src/scheduler/schedulerHost.ts', 'src/scheduler/taskSummary.ts']) {
    assert.ok(files.includes(expected), `${expected} is a shipped source (found ${files.join(', ')})`);
  }
});

test('the peer imports neither of its consumers: nothing under src/scheduler/ imports src/windowsTime/ or src/windowsTasks/', () => {
  const offenders = within(sources(), 'src/scheduler').filter((s) => importedFolders(s).some((f) => f === 'src/windowsTime' || f === 'src/windowsTasks'));
  assert.deepEqual(offenders.map((s) => s.file), []);
});

test('the two consumers are peers of each other: nothing under src/windowsTime/ imports src/windowsTasks/, and nothing under src/windowsTasks/ imports src/windowsTime/', () => {
  const all = sources();
  assert.deepEqual(within(all, 'src/windowsTime').filter((s) => importedFolders(s).includes('src/windowsTasks')).map((s) => s.file), []);
  assert.deepEqual(within(all, 'src/windowsTasks').filter((s) => importedFolders(s).includes('src/windowsTime')).map((s) => s.file), []);
  assert.ok(within(all, 'src/windowsTime').length > 0 && within(all, 'src/windowsTasks').length > 0, 'the scan sees both');
});

test('the scan finds a planted import of a peer, and ignores a mention', () => {
  const planted: Source = { file: 'src/scheduler/x.ts', text: "import { y } from '../windowsTime/guardState';\n// from '../windowsTasks/z'\nconst s = '../windowsTasks/q';" };
  assert.deepEqual(importedFolders(planted), ['src/windowsTime']);
});
