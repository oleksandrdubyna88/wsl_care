import assert from 'node:assert/strict';
import * as fs from 'node:fs';
import * as path from 'node:path';
import { test } from 'node:test';

import { iconPng } from './support/iconPng';
import { BUNDLE, EXTENSION_ROOT } from './support/paths';
import { listLines, machineUserNames, minDaemonFindings, PUBLISHER_PLACEHOLDER, VSIX_OWN_FILES, vsixEntryName, vsixFindings, type VsixCheckOptions } from './support/vsixCheck';
import { readZip, writeZip } from './support/zipFile';

/**
 * The `.vsix` content checks (plan §15g M8), each shown RED by planting the offending content into an otherwise clean
 * archive — built from the files the allowlist really names, the real bundle included (`npm test` bundles first) — and
 * the clean archive shown GREEN, so no check passes vacuously. `scripts/check-vsix.mjs` runs the same function over the
 * `.vsix` vsce built, in CI on both OSes and in the release's build job.
 */

const ALLOWLIST = listLines(fs.readFileSync(path.join(EXTENSION_ROOT, 'vsix-files.txt'), 'utf8'));
const DENYLIST = listLines(fs.readFileSync(path.join(EXTENSION_ROOT, 'vsix-denylist.txt'), 'utf8'));
const OPTIONS: VsixCheckOptions = { expectedFiles: ALLOWLIST, deniedWords: ['someowner', ...DENYLIST], release: false };
const BUNDLE_ENTRY = 'extension/dist/extension.js';
const BACKSLASH = String.fromCharCode(92);

/** A clean archive: every allowlisted file as vsce names it, from disk, plus vsce's own two files. */
function cleanEntries(): Map<string, Buffer> {
  assert.ok(fs.existsSync(BUNDLE), 'npm test bundles before it tests');
  const entries = new Map<string, Buffer>(VSIX_OWN_FILES.map((name) => [name, Buffer.from('<?xml version="1.0"?><x/>')]));
  for (const file of ALLOWLIST) {
    entries.set(vsixEntryName(file), fs.readFileSync(path.join(EXTENSION_ROOT, file)));
  }

  return entries;
}

function planted(entry: string, text: string): Map<string, Buffer> {
  const entries = cleanEntries();
  const before = entries.get(entry) ?? Buffer.alloc(0);
  entries.set(entry, Buffer.concat([before, Buffer.from(`\n${text}\n`, 'utf8')]));

  return entries;
}

function inBundle(literal: string): Map<string, Buffer> {
  return planted(BUNDLE_ENTRY, `var planted = ${JSON.stringify(literal)};`);
}

test('the clean archive passes every check (the positive every planted test is measured against)', () => {
  assert.deepEqual(vsixFindings(cleanEntries(), OPTIONS), []);
});

test('the allowlist is what .vscodeignore lets in: the bundle, media, the icon, README, CHANGELOG, LICENSE, package.json — nothing of src, tests, goldens or research', () => {
  assert.ok(ALLOWLIST.includes('dist/extension.js') && ALLOWLIST.includes('media/icon.png') && ALLOWLIST.includes('package.json'));
  for (const file of ['README.md', 'CHANGELOG.md', 'LICENSE']) {
    assert.ok(ALLOWLIST.includes(file), file);
  }
  assert.ok(ALLOWLIST.every((f) => f === 'package.json' || f === 'README.md' || f === 'CHANGELOG.md' || f === 'LICENSE' || f === 'dist/extension.js' || f.startsWith('media/')), ALLOWLIST.join(', '));
  const ignore = fs.readFileSync(path.join(EXTENSION_ROOT, '.vscodeignore'), 'utf8');
  assert.equal(listLines(ignore)[0], '**', '.vscodeignore starts by excluding everything — an allowlist');
});

test('an extra entry is a finding — a file the allowlist does not name', () => {
  const entries = cleanEntries();
  entries.set('extension/out/test/fake/fakeWsl.js', Buffer.from('x'));
  assert.deepEqual(vsixFindings(entries, OPTIONS), ['the .vsix holds extension/out/test/fake/fakeWsl.js, which the allowlist does not name']);
});

test('a missing entry is a finding — the allowlist names a file the archive lacks', () => {
  const entries = cleanEntries();
  entries.delete('extension/media/icon.png');
  assert.deepEqual(vsixFindings(entries, OPTIONS), ['the .vsix lacks extension/media/icon.png, which the allowlist names']);
});

const PATH_PLANTS: readonly { readonly what: string; readonly literal: string }[] = [
  { what: 'a drive path', literal: `D:${BACKSLASH}work${BACKSLASH}wsl_care` },
  { what: 'a drive path', literal: 'C:/Users/x/project' },
  { what: 'a /home/ path', literal: '/home/x/.config' },
  { what: 'a /mnt/ path', literal: '/mnt/c/tools' },
  { what: `a ${BACKSLASH}${BACKSLASH}wsl share`, literal: `${BACKSLASH}${BACKSLASH}wsl.localhost${BACKSLASH}Ubuntu` },
  { what: 'an e-mail address', literal: 'contact x.y@example.org' },
  { what: 'a source map reference', literal: '//# sourceMappingURL=extension.js.map' },
  { what: 'a source map reference', literal: '"sourcesContent": []' },
];

for (const plant of PATH_PLANTS) {
  test(`planted in the bundle, ${JSON.stringify(plant.literal)} is found as ${plant.what}`, () => {
    const findings = vsixFindings(inBundle(plant.literal), OPTIONS);
    assert.equal(findings.length, 1, findings.join(' | '));
    assert.match(findings[0] ?? '', new RegExp(`^extension/dist/extension\\.js:\\d+ holds ${plant.what.replace(/[\\.]/g, (c) => BACKSLASH + c)}$`));
  });
}

test('planted in a non-code entry (the README) a path is found too', () => {
  const findings = vsixFindings(planted('extension/readme.md', 'see /home/x/notes'), OPTIONS);
  assert.deepEqual(findings.map((f) => f.replace(/:\d+/, '')), ['extension/readme.md holds a /home/ path']);
});

test('the near misses are not findings: a URL scheme, one escaped backslash before wsl, an npm scope, a version', () => {
  for (const literal of ['https://github.com/oleksandrdubyna88/wsl_care', `System32${BACKSLASH}wsl.exe`, '@vscode/vsce@4.0.0', 'x@1.85.0', 'a: b']) {
    assert.deepEqual(vsixFindings(inBundle(literal), OPTIONS), [], literal);
  }
});

test('a user name of the build machine or a denylist word is found as a whole word, and never printed', () => {
  const owner = vsixFindings(inBundle('built by SomeOwner today'), OPTIONS);
  assert.deepEqual(owner, ['extension/dist/extension.js holds denied word #1 (a user name or a denylist entry; not printed)']);
  assert.equal(owner.join(' ').toLowerCase().includes('someowner'), false);
  const listed = vsixFindings(planted('extension/changelog.md', 'from C-scratchpad-notes'), OPTIONS);
  assert.equal(listed.length, 1, listed.join(' | '));
  assert.deepEqual(vsixFindings(inBundle('someowners and theowner'), OPTIONS), [], 'a longer word is not the name');
});

test('in the bundle only string LITERALS are read for names: a CI account called "runner" does not trip on the code that names a runner', () => {
  const options = { ...OPTIONS, deniedWords: ['runner', 'runneradmin'] };
  assert.deepEqual(vsixFindings(cleanEntries(), options), [], 'the real bundle carries the identifier, not the word in a string');
  assert.equal(vsixFindings(inBundle('the runner said'), options).length, 1, 'the same word in a string IS found');
});

test('a source map entry is a finding', () => {
  const entries = cleanEntries();
  entries.set('extension/dist/extension.js.map', Buffer.from('{}'));
  const findings = vsixFindings(entries, OPTIONS);
  assert.ok(findings.includes('the .vsix holds a source map, extension/dist/extension.js.map'), findings.join(' | '));
});

test('the build stamp: the real bundle carries exactly one, equal to the package version', () => {
  const manifest = JSON.parse(fs.readFileSync(path.join(EXTENSION_ROOT, 'package.json'), 'utf8')) as { version: string };
  assert.ok(fs.readFileSync(BUNDLE, 'utf8').includes(`"wsl-care-build ${manifest.version}"`), 'scripts/bundle.mjs stamped the bundle');
});

test('a stale bundle — stamped for another version — is a finding', () => {
  const entries = cleanEntries();
  const manifest = JSON.parse((entries.get('extension/package.json') ?? Buffer.alloc(0)).toString('utf8')) as Record<string, unknown>;
  entries.set('extension/package.json', Buffer.from(JSON.stringify({ ...manifest, version: '9.9.9' })));
  assert.deepEqual(vsixFindings(entries, OPTIONS), [`extension/dist/extension.js was built for ${String(manifest.version)}, but the .vsix is version 9.9.9 — the bundle is stale`]);
});

test('a bundle with no stamp, or two, is a finding', () => {
  const entries = cleanEntries();
  const bundle = (entries.get(BUNDLE_ENTRY) ?? Buffer.alloc(0)).toString('utf8');
  entries.set(BUNDLE_ENTRY, Buffer.from(bundle.replace(/"wsl-care-build [^"]+"/, '"unstamped"')));
  assert.match(vsixFindings(entries, OPTIONS).join(' | '), /carries 0 build stamps/);
  assert.match(vsixFindings(inBundle('wsl-care-build 0.0.1'), OPTIONS).join(' | '), /carries 2 build stamps/);
});

test('a RELEASE refuses the placeholder publisher; an ordinary package does not', () => {
  const entries = cleanEntries();
  const manifest = JSON.parse((entries.get('extension/package.json') ?? Buffer.alloc(0)).toString('utf8')) as Record<string, unknown>;
  entries.set('extension/package.json', Buffer.from(JSON.stringify({ ...manifest, publisher: PUBLISHER_PLACEHOLDER })));
  assert.deepEqual(vsixFindings(entries, OPTIONS), []);
  assert.match(vsixFindings(entries, { ...OPTIONS, release: true }).join(' | '), /placeholder publisher "publisher-tbd"/);
  entries.set('extension/package.json', Buffer.from(JSON.stringify({ ...manifest, publisher: 'real-publisher' })));
  assert.deepEqual(vsixFindings(entries, { ...OPTIONS, release: true }), [], 'a real publisher passes the release check');
});

test('the build machine\'s user names are derived, deduplicated, case-folded, and too-short ones dropped', () => {
  assert.deepEqual(machineUserNames(['Alice', 'alice', undefined, ' ab ', 'runner']), ['alice', 'runner']);
});

test('the ZIP reader reads what the writer wrote — stored names, inflated bytes, directories skipped', () => {
  const files = new Map<string, Buffer>([['a.txt', Buffer.from('hello')], ['dir/', Buffer.alloc(0)], ['dir/b.bin', iconPng()]]);
  const read = readZip(writeZip(files));
  assert.deepEqual([...read.keys()], ['a.txt', 'dir/b.bin']);
  assert.deepEqual(read.get('dir/b.bin'), iconPng());
});

test('the ZIP reader refuses what it cannot read safely: a climbing name, an absolute one, a duplicate, not a ZIP at all', () => {
  for (const name of ['../escape.txt', '/abs.txt', 'a\\b.txt']) {
    assert.throws(() => readZip(writeZip(new Map([[name, Buffer.from('x')]]))), /absolute or climbs out/, name);
  }
  assert.throws(() => readZip(writeZip([['a', Buffer.from('1')], ['a', Buffer.from('2')]])), /appears twice/);
  assert.throws(() => readZip(Buffer.from('not a zip at all, just text')), /no end-of-central-directory/);
});

test('the packaged .vsix, when one is present here, is read by the reader and passes the checks (the real artefact)', (t) => {
  const manifest = JSON.parse(fs.readFileSync(path.join(EXTENSION_ROOT, 'package.json'), 'utf8')) as { version: string };
  const vsix = path.join(EXTENSION_ROOT, `wsl-care-${manifest.version}.vsix`);
  if (!fs.existsSync(vsix) || fs.statSync(vsix).mtimeMs < fs.statSync(BUNDLE).mtimeMs) {
    t.skip('no .vsix newer than the bundle — `npm run package` then `npm run check:vsix` checks the real artefact (CI does both)');
    return;
  }
  assert.deepEqual(vsixFindings(readZip(fs.readFileSync(vsix)), OPTIONS), []);
});

// ---- E5 code round #2/#5: the minimum daemon, as an artefact the release guard reads ----

const AGREED = { constant: '0.1.0', emitted: { minDaemonForRender: '0.1.0' }, checkedIn: { minDaemonForRender: '0.1.0' }, released: '0.1.0' };

test('the minimum daemon: the constant, the emitted artefact, the checked-in one and the guard\'s verified one agree — clean', () => {
  assert.deepEqual(minDaemonFindings(AGREED), []);
  assert.deepEqual(minDaemonFindings({ ...AGREED, released: undefined }), [], 'a pull request has no guard: three places, not four');
});

test('each place that disagrees is named — the stale bundle, the checked-in artefact, the guard\'s minimum — and an unreadable one says so', () => {
  assert.deepEqual(minDaemonFindings({ ...AGREED, emitted: { minDaemonForRender: '0.0.9' } }).map((f) => f.split(' ')[0]), ['dist/min-daemon.json']);
  assert.match(minDaemonFindings({ ...AGREED, emitted: undefined })[0] ?? '', /dist\/min-daemon\.json says nothing readable/);
  assert.match(minDaemonFindings({ ...AGREED, checkedIn: { minDaemonForRender: '0.2.0' } })[0] ?? '', /src_vs_code\/min-daemon\.json \(what the release guard reads at the tag\) says 0\.2\.0/);
  assert.match(minDaemonFindings({ ...AGREED, checkedIn: { other: '0.1.0' } })[0] ?? '', /says nothing readable/);
  assert.match(minDaemonFindings({ ...AGREED, released: '0.2.0' })[0] ?? '', /the release guard verified the minimum daemon 0\.2\.0, but this \.vsix renders and installs 0\.1\.0/);
  assert.equal(minDaemonFindings({ constant: '0.1.0', emitted: {}, checkedIn: [], released: '9.9.9' }).length, 3);
});
