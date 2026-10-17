import assert from 'node:assert/strict';
import { spawnSync } from 'node:child_process';
import { createHash } from 'node:crypto';
import * as fs from 'node:fs';
import * as os from 'node:os';
import * as path from 'node:path';
import { test } from 'node:test';

import { BUNDLED_EXE_FILE, onCi, shippedAllowlist } from './support/bundledExe';
import { iconPng } from './support/iconPng';
import { BUNDLE, EXTENSION_ROOT } from './support/paths';
import { listLines, machineUserNames, machineUserNameSplit, minDaemonFindings, PUBLISHER_PLACEHOLDER, SERVICE_ACCOUNTS, VSIX_OWN_FILES, vsixEntryName, vsixFindings, type VsixCheckOptions } from './support/vsixCheck';
import { readZip, writeZip } from './support/zipFile';

/**
 * The `.vsix` content checks (plan §15g M8), each shown RED by planting the offending content into an otherwise clean
 * archive — built from the files the allowlist really names, the real bundle included (`npm test` bundles first) — and
 * the clean archive shown GREEN, so no check passes vacuously. `scripts/check-vsix.mjs` runs the same function over the
 * `.vsix` vsce built, in CI on both OSes and in the release's build job.
 */

// E7.S5a: the exe is fetched, not committed — read through the CI-guarded rule (`support/bundledExe.ts`).
const SHIPPED = shippedAllowlist();
const ALLOWLIST = SHIPPED.files;
const DENYLIST = listLines(fs.readFileSync(path.join(EXTENSION_ROOT, 'vsix-denylist.txt'), 'utf8'));
const OPTIONS: VsixCheckOptions = { expectedFiles: ALLOWLIST, deniedWords: ['someowner', ...DENYLIST], release: false, ...(SHIPPED.exe === undefined ? {} : { bundledExeDigest: SHIPPED.exe.digest }) };
const BUNDLE_ENTRY = 'extension/dist/extension.js';
const BACKSLASH = String.fromCharCode(92);
/** The vsixmanifest of a targeted package, as vsce 4.0.0 writes its Identity (E7.S5a §3.3). */
const TARGETED_MANIFEST = '<?xml version="1.0"?><PackageManifest><Metadata><Identity Language="en-US" Id="ai-os-care" Version="0.4.0" Publisher="p" TargetPlatform="win32-x64"/></Metadata></PackageManifest>';

test('E7.S5a: a RELEASE .vsix must target win32-x64 — the vsixmanifest is the artefact\'s truth, not the file name', () => {
  const entries = cleanEntries();
  const release = { ...OPTIONS, release: true, bundledExeDigest: OPTIONS.bundledExeDigest ?? '0'.repeat(64) };
  assert.doesNotMatch(vsixFindings(entries, release).join(' | '), /TargetPlatform/);
  // Own Fable review of the code, finding 6: the target counts only as the Identity's attribute — the same text in a comment,
  // in another element's attribute or in a description is not the package's target.
  const elsewhere = [
    '<?xml version="1.0"?><PackageManifest><Metadata><Identity Id="ai-os-care"/><Description>TargetPlatform="win32-x64"</Description></Metadata></PackageManifest>',
    '<?xml version="1.0"?><PackageManifest><Metadata><Identity Id="ai-os-care"/><Property Id="x" TargetPlatform="win32-x64"/></Metadata></PackageManifest>',
    '<?xml version="1.0"?><PackageManifest><!-- TargetPlatform="win32-x64" --><Metadata><Identity Id="ai-os-care"/></Metadata></PackageManifest>',
  ];
  for (const manifest of ['<?xml version="1.0"?><PackageManifest><Metadata><Identity Id="ai-os-care"/></Metadata></PackageManifest>', TARGETED_MANIFEST.replace('win32-x64', 'linux-x64'), ...elsewhere]) {
    entries.set('extension.vsixmanifest', Buffer.from(manifest));
    assert.match(vsixFindings(entries, release).join(' | '), /a release \.vsix must target win32-x64: extension\.vsixmanifest carries no TargetPlatform="win32-x64"/, manifest);
    assert.doesNotMatch(vsixFindings(entries, OPTIONS).join(' | '), /TargetPlatform/, 'a pull request package is checked for content, not for its release target');
  }
});

/** A clean archive: every allowlisted file as vsce names it, from disk (the fetched exe as fetched), plus vsce's own two files. */
function cleanEntries(): Map<string, Buffer> {
  assert.ok(fs.existsSync(BUNDLE), 'npm test bundles before it tests');
  const entries = new Map<string, Buffer>(VSIX_OWN_FILES.map((name) => [name, Buffer.from(name === 'extension.vsixmanifest' ? TARGETED_MANIFEST : '<?xml version="1.0"?><x/>')]));
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
  assert.ok(ALLOWLIST.every((f) => f === 'package.json' || f === 'README.md' || f === 'CHANGELOG.md' || f === 'LICENSE' || f === 'dist/extension.js' || f === BUNDLED_EXE_FILE || f.startsWith('media/')), ALLOWLIST.join(', '));
  const ignore = fs.readFileSync(path.join(EXTENSION_ROOT, '.vscodeignore'), 'utf8');
  assert.equal(listLines(ignore)[0], '**', '.vscodeignore starts by excluding everything — an allowlist');
});

// ---- E7.S5a (research/PLAN_bundle_windows_binary.md §3.4, §3.6): the bundled exe, exempt from the TEXT scan by its digest only ----

/** A stand-in exe carrying exactly what the real one carries and the text scan would refuse: the runner's build path and `\wsl`. */
const FAKE_EXE = Buffer.from(`MZ${BACKSLASH}${BACKSLASH}wsl.localhost D:${BACKSLASH}a${BACKSLASH}wsl_care${BACKSLASH}src build`, 'utf8');
const FAKE_DIGEST = createHash('sha256').update(FAKE_EXE).digest('hex');
const EXE_ENTRY = 'extension/bin/wsl-care.exe';

/** The clean archive with the stand-in exe at `at`, and options that know `digest` (`null`: no digest) and list the exe. */
function withExe(digest: string | null = FAKE_DIGEST, at: string = EXE_ENTRY): { entries: Map<string, Buffer>; options: VsixCheckOptions } {
  const entries = cleanEntries();
  entries.delete(EXE_ENTRY);
  entries.set(at, FAKE_EXE);
  const expectedFiles = [...new Set([...ALLOWLIST, BUNDLED_EXE_FILE])];

  return { entries, options: { ...OPTIONS, expectedFiles, bundledExeDigest: digest ?? undefined } };
}

test('E7.S5a: the exe at exactly extension/bin/wsl-care.exe, with its digest, is exempt from the text scan — and only then', () => {
  const { entries, options } = withExe();
  assert.deepEqual(vsixFindings(entries, options), [], 'its build path and \\wsl are the attested bytes, not a leak');
});

test('E7.S5a: a wrong digest is refused, and the exe is then scanned like any file', () => {
  const { entries, options } = withExe('0'.repeat(64));
  const findings = vsixFindings(entries, options).join(' | ');
  assert.match(findings, /extension\/bin\/wsl-care\.exe is not the fetched member: its SHA-256 is [0-9a-f]{64}, bin\/wsl-care\.exe\.sha256 says 0{64}/);
  assert.match(findings, /extension\/bin\/wsl-care\.exe:1 holds a drive path/);
});

test('E7.S5a: the exe without a digest to compare is refused and scanned', () => {
  const { entries, options } = withExe(null);
  const findings = vsixFindings(entries, options).join(' | ');
  assert.match(findings, /carries extension\/bin\/wsl-care\.exe but no digest was given to check it against/);
  assert.match(findings, /extension\/bin\/wsl-care\.exe:1 holds a drive path/);
});

test('E7.S5a: the same bytes anywhere else are refused by the allowlist AND scanned — the exemption is the exact path', () => {
  for (const at of ['extension/media/wsl-care.exe', 'extension/bin/other.exe', 'extension/bin/WSL-CARE.exe']) {
    const { entries, options } = withExe(FAKE_DIGEST, at);
    const findings = vsixFindings(entries, options).join(' | ');
    assert.match(findings, new RegExp(`the \\.vsix holds ${at.replace(/[.]/g, '\\.')}, which the allowlist does not name`), at);
    assert.match(findings, new RegExp(`${at.replace(/[.]/g, '\\.')}:1 holds a drive path`), at);
  }
});

test('E7.S5a: a RELEASE .vsix needs the digest file even before it carries the exe', () => {
  const entries = cleanEntries();
  assert.match(vsixFindings(entries, { ...OPTIONS, release: true, bundledExeDigest: undefined }).join(' | '), /a release \.vsix needs bin\/wsl-care\.exe\.sha256/);
});

test('E7.S5a: the CI guard — on CI a missing exe FAILS naming the fetch; off CI it is dropped from the expected list too', () => {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), 'bundled-exe-'));
  try {
    fs.writeFileSync(path.join(root, 'vsix-files.txt'), `package.json\n${BUNDLED_EXE_FILE}\n`);
    assert.throws(() => shippedAllowlist(root, { CI: 'true' }), /bin\/wsl-care\.exe \(with its \.sha256\) is missing on CI — the workflow must run `npm run fetch:windows-daemon` before npm test/);
    assert.throws(() => shippedAllowlist(root, { GITHUB_ACTIONS: 'true' }), /missing on CI/);
    const off = shippedAllowlist(root, {});
    assert.deepEqual(off.files, ['package.json'], 'never a partial archive checked against the full list');
    assert.match(off.skipped, /npm run fetch:windows-daemon/);
    fs.mkdirSync(path.join(root, 'bin'));
    fs.writeFileSync(path.join(root, BUNDLED_EXE_FILE), FAKE_EXE);
    fs.writeFileSync(path.join(root, `${BUNDLED_EXE_FILE}.sha256`), `${FAKE_DIGEST}  wsl-care.exe\n`);
    const present = shippedAllowlist(root, { CI: 'true' });
    assert.deepEqual(present.files, ['package.json', BUNDLED_EXE_FILE]);
    assert.equal(present.exe?.digest, FAKE_DIGEST);
  } finally {
    fs.rmSync(root, { recursive: true, force: true });
  }
  assert.equal(onCi({}), false);
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
  { what: 'an e-mail address', literal: 'name@company.co.uk' },
  { what: 'an e-mail address', literal: 'a@b.io' },
  { what: 'an e-mail address', literal: 'user@service.example.com' },
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

test('a systemd unit name (getty@tty1.service) is not an e-mail address: the word after the last dot names a unit type, so a changelog line that quotes one stays clean', () => {
  for (const literal of ['getty@tty1.service', 'serial-getty@ttyS0.service', 'default getty@tty1.service', 'sshd@2.socket', 'fstrim@x.timer']) {
    assert.deepEqual(vsixFindings(inBundle(literal), OPTIONS), [], literal);
    assert.deepEqual(vsixFindings(planted('extension/changelog.md', `the verdict ignores a list, ${literal}`), OPTIONS), [], literal);
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

test('in the bundle only string LITERALS are read for a denied word: the code that names a runner does not trip, the word in a string does', () => {
  const options = { ...OPTIONS, deniedWords: ['runner', 'runneradmin'] };
  assert.match(fs.readFileSync(BUNDLE, 'utf8'), /(?<![A-Za-z0-9])runner(?![A-Za-z0-9])/, 'the real bundle names a runner in its code (the subject is present)');
  const inTheBundle = (entries: Map<string, Buffer>): string[] => vsixFindings(entries, options).filter((f) => f.startsWith(BUNDLE_ENTRY));
  assert.deepEqual(inTheBundle(cleanEntries()), [], 'the real bundle carries the identifier, not the word in a string');
  assert.equal(inTheBundle(inBundle('the runner said')).length, 1, 'the same word in a string IS found');
});

/** The names `scripts/check-vsix.mjs` denies for a machine whose account reads as `candidates`, plus the denylist. */
function machineOptions(candidates: readonly (string | undefined)[]): VsixCheckOptions {
  return { ...OPTIONS, deniedWords: [...machineUserNames(candidates), ...DENYLIST] };
}

test('a GitHub-hosted runner\'s own account (runner, runneradmin) is no person: a CHANGELOG that says "the win-x64 runner" passes (PR #47, run 37751902435)', () => {
  const prose = planted('extension/changelog.md', '* the runner seam takes stdin; the win-x64 runner signs it; runneradmin is its Windows account');
  assert.deepEqual(vsixFindings(prose, machineOptions(['runner', 'RUNNER', undefined])), [], 'the Linux and macOS images');
  assert.deepEqual(vsixFindings(prose, machineOptions(['runneradmin', 'RUNNERADMIN'])), [], 'the Windows image');
});

test('the narrowed guarantee, pinned: a service-account name is not looked for even as "built by runner" — but a home path is still a path leak whatever the name', () => {
  const options = machineOptions(['runner']);
  assert.deepEqual(vsixFindings(planted('extension/readme.md', 'built by runner'), options), [], 'a service account identifies nobody (SERVICE_ACCOUNTS)');
  assert.deepEqual(vsixFindings(planted('extension/readme.md', 'see /home/x/work'), options).map((f) => f.replace(/:\d+/, '')), ['extension/readme.md holds a /home/ path']);
});

test('a PERSON\'s account is still refused in prose — as a path segment and as a word — even beside a service account', () => {
  const options = machineOptions(['jdoe', 'JDoe', 'runner']);
  const asPath = vsixFindings(planted('extension/changelog.md', `notes in C:${BACKSLASH}Users${BACKSLASH}jdoe${BACKSLASH}notes, the runner said`), options);
  assert.deepEqual(asPath.map((f) => f.replace(/:\d+/, '')), ['extension/changelog.md holds a drive path', 'extension/changelog.md holds denied word #1 (a user name or a denylist entry; not printed)']);
  const asWord = vsixFindings(planted('extension/readme.md', 'thanks jdoe for the runner'), options);
  assert.deepEqual(asWord, ['extension/readme.md holds denied word #1 (a user name or a denylist entry; not printed)']);
  assert.equal(asWord.join(' ').toLowerCase().includes('jdoe'), false, 'the name itself is never printed');
  assert.equal(vsixFindings(inBundle('built by jdoe'), options).length, 1, 'and in a string literal of the bundle');
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
  assert.deepEqual(vsixFindings(entries, { ...OPTIONS, release: true, bundledExeDigest: OPTIONS.bundledExeDigest ?? FAKE_DIGEST }), [], 'a real publisher passes the release check (with the exe digest a release needs — E7.S5a)');
});

test('the build machine\'s user names are derived, deduplicated, case-folded, too-short ones dropped, and a service account left out', () => {
  assert.deepEqual(machineUserNames(['Alice', 'alice', undefined, ' ab ', 'runner']), ['alice']);
  assert.deepEqual(machineUserNameSplit(['RunnerAdmin', 'jdoe', 'runneradmin', 'ROOT']), { personal: ['jdoe'], serviceAccounts: ['runneradmin', 'root'] });
  for (const account of SERVICE_ACCOUNTS) {
    assert.deepEqual(machineUserNameSplit([account.toUpperCase()]), { personal: [], serviceAccounts: [account] }, account);
  }
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
  // E7.S5a: the targeted name, asked of the ONE Node implementation (check-vsix.mjs) rather than spelt again here.
  const named = spawnSync(process.execPath, [path.join(EXTENSION_ROOT, 'scripts', 'check-vsix.mjs'), '--print-default-name'], { encoding: 'utf8' });
  assert.equal(named.status, 0, named.stderr);
  const vsix = path.join(EXTENSION_ROOT, named.stdout.trim());
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
  assert.match(minDaemonFindings({ ...AGREED, released: '0.2.0' })[0] ?? '', /the release guard verified the minimum daemon 0\.2\.0, but this \.vsix renders 0\.1\.0/);
  assert.equal(minDaemonFindings({ constant: '0.1.0', emitted: {}, checkedIn: [], released: '9.9.9' }).length, 3);
});

// ---- E6.S2 (plan §15j M5): the actions minimum is held the same way, in the same four places ----

const ACTIONS_AGREED = { constant: '0.1.0', emitted: { minDaemonForActions: '0.1.0' }, checkedIn: { minDaemonForActions: '0.1.0' }, released: '0.1.0' };

test('the actions minimum: clean when the four agree; each place that disagrees named, under its own key', () => {
  assert.deepEqual(minDaemonFindings(ACTIONS_AGREED, 'minDaemonForActions'), []);
  assert.match(minDaemonFindings({ ...ACTIONS_AGREED, emitted: { minDaemonForActions: '0.0.9' } }, 'minDaemonForActions')[0] ?? '', /^dist\/min-daemon\.json says 0\.0\.9 for minDaemonForActions/);
  assert.match(minDaemonFindings({ ...ACTIONS_AGREED, checkedIn: { minDaemonForRender: '0.1.0' } }, 'minDaemonForActions')[0] ?? '', /says nothing readable for minDaemonForActions/);
  assert.match(minDaemonFindings({ ...ACTIONS_AGREED, released: '0.2.0' }, 'minDaemonForActions')[0] ?? '', /verified the actions minimum 0\.2\.0/);
});

const INSTALL_AGREED = { constant: '0.1.2', emitted: { minDaemonForRender: '0.1.0', minDaemonForActions: '0.1.0', installDaemon: '0.1.2' }, checkedIn: { minDaemonForRender: '0.1.0', minDaemonForActions: '0.1.0', installDaemon: '0.1.2' }, released: '0.1.2' };

test('the daemon to install (installDaemon, #37) is compared in the same four places, apart from both minima', () => {
  assert.deepEqual(minDaemonFindings(INSTALL_AGREED, 'installDaemon'), []);
  assert.deepEqual(minDaemonFindings({ ...INSTALL_AGREED, constant: '0.1.0', released: '0.1.0' }, 'minDaemonForRender'), [], 'the render minimum keeps its own value beside it');
  assert.deepEqual(minDaemonFindings({ ...INSTALL_AGREED, constant: '0.1.0', released: '0.1.0' }, 'minDaemonForActions'), [], 'so does the actions minimum');
  assert.match(minDaemonFindings({ ...INSTALL_AGREED, emitted: { minDaemonForRender: '0.1.0' } }, 'installDaemon')[0] ?? '', /dist\/min-daemon\.json says nothing readable for installDaemon/);
  assert.match(minDaemonFindings({ ...INSTALL_AGREED, checkedIn: { installDaemon: '0.1.0' } }, 'installDaemon')[0] ?? '', /says 0\.1\.0 for installDaemon, handshake\.ts says 0\.1\.2/);
  assert.match(minDaemonFindings({ ...INSTALL_AGREED, released: '0.1.0' }, 'installDaemon')[0] ?? '', /the release guard verified the daemon to install 0\.1\.0, but this \.vsix installs 0\.1\.2/);
});

const BUNDLED_AGREED = { constant: '0.4.0', emitted: { bundledDaemon: '0.4.0' }, checkedIn: { bundledDaemon: '0.4.0' }, released: '0.4.0' };

test('E7.S5a: the bundled daemon (bundledDaemon) is compared in the same four places, a pin of its own', () => {
  assert.deepEqual(minDaemonFindings(BUNDLED_AGREED, 'bundledDaemon'), []);
  assert.match(minDaemonFindings({ ...BUNDLED_AGREED, emitted: {} }, 'bundledDaemon')[0] ?? '', /dist\/min-daemon\.json says nothing readable for bundledDaemon/);
  assert.match(minDaemonFindings({ ...BUNDLED_AGREED, checkedIn: { bundledDaemon: '0.3.0' } }, 'bundledDaemon')[0] ?? '', /says 0\.3\.0 for bundledDaemon, handshake\.ts says 0\.4\.0/);
  assert.match(minDaemonFindings({ ...BUNDLED_AGREED, released: '0.3.0' }, 'bundledDaemon')[0] ?? '', /the release guard verified the bundled daemon 0\.3\.0, but this \.vsix carries the Windows build of 0\.4\.0/);
});

// ---- E6.S2 (plan §15j B3, §15k #7): a release the tags do not allow a root path must not carry the root module ----

const ROOT_MARKER_LINE = '// src/root/rootCall.ts';

test('the real bundle carries the root module\'s marker (the subject of the check is present)', () => {
  assert.ok(fs.readFileSync(BUNDLE, 'utf8').split('\n').includes(ROOT_MARKER_LINE));
});

test('rootAllowed false: a .vsix whose bundle carries the root module is refused, naming B3', () => {
  const found = vsixFindings(cleanEntries(), { ...OPTIONS, rootAllowed: false });
  assert.equal(found.length, 1, found.join('\n'));
  assert.match(found[0] ?? '', /carries the root module \(src\/root\/rootCall\.ts\).*§15j B3/);
});

test('rootAllowed true — or not asked (a pull request) — the root module is no finding', () => {
  assert.deepEqual(vsixFindings(cleanEntries(), { ...OPTIONS, rootAllowed: true }), []);
  assert.deepEqual(vsixFindings(cleanEntries(), OPTIONS), []);
});

test('rootAllowed false with a bundle that does NOT carry the root module (a 0.1.0 build) is clean', () => {
  const entries = cleanEntries();
  const bundle = (entries.get(BUNDLE_ENTRY) ?? Buffer.alloc(0)).toString('utf8').split('\n').filter((l) => l !== ROOT_MARKER_LINE).join('\n');
  entries.set(BUNDLE_ENTRY, Buffer.from(bundle, 'utf8'));
  assert.deepEqual(vsixFindings(entries, { ...OPTIONS, rootAllowed: false }), []);
});
