import * as fs from 'node:fs';
import * as path from 'node:path';

import { EXTENSION_ROOT } from './paths';
import { listLines } from './vsixCheck';

/**
 * The bundled Windows daemon as the tests see it (E7.S5a, research/PLAN_bundle_windows_binary.md §3.6): `bin/wsl-care.exe` is
 * NOT in git — `npm run fetch:windows-daemon` (or the workflow's fetch step) writes it and its `.sha256` from the attested
 * `daemon-v<bundledDaemon>` release. Every test that reads the allowlisted files reads the exe through
 * {@link shippedAllowlist}:
 *
 * - present (with its digest): it is shipped like every other file;
 * - missing OFF CI: the entry is DROPPED from the expected list too (never a partial archive checked against the full list),
 *   and the reason names the fetch;
 * - missing ON CI (`CI` or `GITHUB_ACTIONS` set): it FAILS, naming the fetch — a misordered workflow can never go green with
 *   the entry silently dropped (own review, finding 3).
 */

export const BUNDLED_EXE_FILE = 'bin/wsl-care.exe';

export const FETCH_COMMAND = 'npm run fetch:windows-daemon';

export interface BundledExe {
  readonly data: Buffer;
  /** The SHA-256 the fetch wrote beside it (`bin/wsl-care.exe.sha256`), lower-case hex. */
  readonly digest: string;
}

export interface ShippedAllowlist {
  /** `vsix-files.txt` — without the exe only when it is missing off CI. */
  readonly files: readonly string[];
  readonly exe: BundledExe | undefined;
  /** Why the exe is not part of this run ('' when it is). */
  readonly skipped: string;
}

/** Whether this run is CI's: `CI` or `GITHUB_ACTIONS` set (GitHub sets both). */
export function onCi(env: Readonly<Record<string, string | undefined>> = process.env): boolean {
  return (env.CI ?? '') !== '' || (env.GITHUB_ACTIONS ?? '') !== '';
}

/** The digest a `.sha256` line names (`<64 hex>  wsl-care.exe`), or undefined when it holds none. */
export function digestOf(line: string): string | undefined {
  return /^([0-9a-f]{64})\s/.exec(line)?.[1];
}

function readExe(root: string): BundledExe | undefined {
  const file = path.join(root, BUNDLED_EXE_FILE);
  const digest = fs.existsSync(file) && fs.existsSync(`${file}.sha256`) ? digestOf(fs.readFileSync(`${file}.sha256`, 'utf8')) : undefined;

  return digest === undefined ? undefined : { data: fs.readFileSync(file), digest };
}

/** The allowlist a test ships, the exe read through the CI-guarded rule above. */
export function shippedAllowlist(root: string = EXTENSION_ROOT, env: Readonly<Record<string, string | undefined>> = process.env): ShippedAllowlist {
  const files = listLines(fs.readFileSync(path.join(root, 'vsix-files.txt'), 'utf8'));
  const exe = readExe(root);
  if (exe !== undefined) {
    return { files, exe, skipped: '' };
  }
  if (onCi(env)) {
    throw new Error(`${BUNDLED_EXE_FILE} (with its .sha256) is missing on CI — the workflow must run \`${FETCH_COMMAND}\` before npm test`);
  }

  return { files: files.filter((file) => file !== BUNDLED_EXE_FILE), exe: undefined, skipped: `${BUNDLED_EXE_FILE} is not fetched here, so it is left out of the expected list — run \`${FETCH_COMMAND}\` to check it too` };
}
