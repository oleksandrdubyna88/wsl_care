import * as ts from 'typescript';

import { stringLiteralsOf } from './sourceScan';

/**
 * What the packaged `.vsix` may hold, checked on the ARTEFACT (plan §15g M8; the family's *Verify the ARTEFACT* rule):
 * `scripts/check-vsix.mjs` unzips the file vsce built and hands its entries here. Each check names the entry and why;
 * an empty list is a pass. The checks, each with its planted-instance test in `vsixCheck.test.ts`:
 *
 * 1. **The entry list is exactly the allowlist** — `vsix-files.txt` (what `vsce ls --no-dependencies` printed, checked
 *    in) under `extension/`, with vsce's three renames, plus the two files vsce writes itself. Nothing missing, nothing
 *    extra.
 * 2. **No machine path** — a drive path (`X:\` or `X:/`), `/home/`, `/mnt/`, a `\\wsl` share.
 * 3. **No user name** — the build machine's user name (derived when the check runs, never written into the repository)
 *    and every entry of `vsix-denylist.txt`, as whole words. In `extension.js` only its STRING LITERALS are read (an
 *    identifier is code, not a leak — the bundle legitimately names a `runner`, which is a CI account's name).
 * 4. **No e-mail address.**
 * 5. **No source map** — no `.map` entry, no `sourceMappingURL=`, no `sourcesContent`.
 * 6. **The build stamp is the package version** — `extension.js` carries exactly one `wsl-care-build <version>` literal
 *    (`src/buildStamp.ts`, stamped by `scripts/bundle.mjs`) and it equals `extension/package.json`'s version.
 * 7. **A release carries a real publisher** — with `release`, the placeholder publisher is refused (the owner creates the
 *    publisher at the E5 live gate; a `.vsix` cut before that must never reach the Marketplace).
 */

export const PUBLISHER_PLACEHOLDER = 'publisher-tbd';

/** The two files vsce writes into every `.vsix` beside the extension's own. */
export const VSIX_OWN_FILES: readonly string[] = ['[Content_Types].xml', 'extension.vsixmanifest'];

/** vsce renames these when it packs them (observed on @vscode/vsce 4.0.0). */
const RENAMES: Readonly<Record<string, string>> = { 'README.md': 'readme.md', 'CHANGELOG.md': 'changelog.md', LICENSE: 'LICENSE.txt' };

const BUNDLE_ENTRY = 'extension/dist/extension.js';
const MANIFEST_ENTRY = 'extension/package.json';
const STAMP_PREFIX = 'wsl-care-build ';

/** Binary entries are not read as text (the icon is checked by its own test: it carries no text chunk). */
const BINARY = /\.(png|jpe?g|gif|ico)$/i;

export interface VsixCheckOptions {
  /** The checked-in allowlist — `vsix-files.txt`, one path per line as `vsce ls` prints it. */
  readonly expectedFiles: readonly string[];
  /** Words that must not appear: the build machine's user name and `vsix-denylist.txt`. */
  readonly deniedWords: readonly string[];
  /** A release build: the placeholder publisher is refused. */
  readonly release: boolean;
}

export type VsixEntries = ReadonlyMap<string, Buffer>;

/** Where vsce puts a file `vsce ls` names. */
export function vsixEntryName(file: string): string {
  return `extension/${RENAMES[file] ?? file}`;
}

function entryFindings(entries: VsixEntries, expected: readonly string[]): string[] {
  const want = new Set([...VSIX_OWN_FILES, ...expected.map(vsixEntryName)]);
  const have = [...entries.keys()];

  return [
    ...[...want].filter((name) => !entries.has(name)).map((name) => `the .vsix lacks ${name}, which the allowlist names`),
    ...have.filter((name) => !want.has(name)).map((name) => `the .vsix holds ${name}, which the allowlist does not name`),
  ];
}

const PATH_LEAKS: readonly { readonly what: string; readonly pattern: RegExp }[] = [
  { what: 'a drive path', pattern: /(?<![A-Za-z0-9])[A-Za-z]:[\\/]/ },
  { what: 'a /home/ path', pattern: /\/home\// },
  { what: 'a /mnt/ path', pattern: /\/mnt\// },
  { what: 'a \\\\wsl share', pattern: /\\\\wsl/i },
  { what: 'an e-mail address', pattern: /[A-Za-z0-9._%+-]+@[A-Za-z0-9-]+(?:\.[A-Za-z0-9-]+)*\.[A-Za-z]{2,}/ },
  { what: 'a source map reference', pattern: /sourceMappingURL=|sourcesContent/ },
];

function lineOf(text: string, index: number): number {
  return text.slice(0, index).split('\n').length;
}

/**
 * The text a path is looked for in. The bundle is JavaScript, where one backslash is written as two: its escapes are
 * collapsed first, so `"System32\\wsl.exe"` (one backslash) is not a `\\wsl` share and `"\\\\wsl$"` (two) is.
 */
function pathText(name: string, text: string): string {
  return name === BUNDLE_ENTRY ? text.replace(/\\\\/g, '\\') : text;
}

function pathFindings(name: string, raw: string): string[] {
  const text = pathText(name, raw);

  return PATH_LEAKS.flatMap(({ what, pattern }) => {
    const found = pattern.exec(text);
    return found === null ? [] : [`${name}:${lineOf(text, found.index)} holds ${what}`];
  });
}

function escapeRegex(word: string): string {
  return word.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
}

/** The text a denied word is looked for in: the bundle's string literals, or the whole file. */
function wordText(name: string, text: string): string {
  return name === BUNDLE_ENTRY ? stringLiteralsOf(text, ts.ScriptKind.JS).join('\n') : text;
}

function wordFindings(name: string, text: string, words: readonly string[]): string[] {
  const haystack = wordText(name, text);

  return words.flatMap((word, index) => {
    const pattern = new RegExp(`(?<![A-Za-z0-9])${escapeRegex(word)}(?![A-Za-z0-9])`, 'i');
    return pattern.test(haystack) ? [`${name} holds denied word #${index + 1} (a user name or a denylist entry; not printed)`] : [];
  });
}

function textFindings(entries: VsixEntries, words: readonly string[]): string[] {
  return [...entries].filter(([name]) => !BINARY.test(name)).flatMap(([name, data]) => {
    const text = data.toString('utf8');
    return [...pathFindings(name, text), ...wordFindings(name, text, words)];
  });
}

function mapFindings(entries: VsixEntries): string[] {
  return [...entries.keys()].filter((name) => name.endsWith('.map')).map((name) => `the .vsix holds a source map, ${name}`);
}

function manifestOf(entries: VsixEntries): { version?: unknown; publisher?: unknown } {
  const raw = entries.get(MANIFEST_ENTRY);
  try {
    return raw === undefined ? {} : (JSON.parse(raw.toString('utf8')) as { version?: unknown; publisher?: unknown });
  } catch {
    return {};
  }
}

function stampFindings(entries: VsixEntries): string[] {
  const bundle = entries.get(BUNDLE_ENTRY);
  const version = manifestOf(entries).version;
  if (bundle === undefined || typeof version !== 'string') {
    return [`the build stamp cannot be compared: ${BUNDLE_ENTRY} or the version of ${MANIFEST_ENTRY} is missing`];
  }
  const stamps = stringLiteralsOf(bundle.toString('utf8'), ts.ScriptKind.JS).filter((s) => s.startsWith(STAMP_PREFIX));
  if (stamps.length !== 1) {
    return [`${BUNDLE_ENTRY} carries ${stamps.length} build stamps, not exactly one — was it bundled by scripts/bundle.mjs?`];
  }
  const stamped = stamps[0]?.slice(STAMP_PREFIX.length);

  return stamped === version ? [] : [`${BUNDLE_ENTRY} was built for ${stamped}, but the .vsix is version ${version} — the bundle is stale`];
}

function publisherFindings(entries: VsixEntries, release: boolean): string[] {
  const publisher = manifestOf(entries).publisher;

  return release && (publisher === PUBLISHER_PLACEHOLDER || typeof publisher !== 'string')
    ? [`a release .vsix carries the placeholder publisher "${String(publisher)}" — the owner creates the publisher first (E5 live gate, step 1)`]
    : [];
}

export function vsixFindings(entries: VsixEntries, options: VsixCheckOptions): string[] {
  return [
    ...entryFindings(entries, options.expectedFiles),
    ...mapFindings(entries),
    ...textFindings(entries, options.deniedWords),
    ...stampFindings(entries),
    ...publisherFindings(entries, options.release),
  ];
}

/** The lines of a list file (`vsix-files.txt`, `vsix-denylist.txt`): trimmed, without blanks and `#` comments. */
export function listLines(text: string): string[] {
  return text.split(/\r?\n/).map((line) => line.trim()).filter((line) => line.length > 0 && !line.startsWith('#'));
}

/**
 * The build machine's user name, as many ways as it can be read — `os.userInfo()`, `USERNAME`, `USER`, the home
 * folder's last segment — deduplicated, each at least three characters (a shorter one matches too much to mean
 * anything). Derived when the check runs, so no name is ever written into the repository.
 */
export function machineUserNames(candidates: readonly (string | undefined)[]): string[] {
  const names = candidates.map((c) => (c ?? '').trim()).filter((c) => c.length >= 3);

  return [...new Set(names.map((n) => n.toLowerCase()))];
}
