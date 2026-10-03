import * as fs from 'node:fs';
import * as path from 'node:path';

/**
 * Where the tests find the repository's files — resolved from THIS compiled file (`out/test/support/paths.js`), never
 * from the working directory, so a run from any folder reads the same files (TypeScript doctrine §4: build in place).
 */

/** `src_vs_code/`. */
export const EXTENSION_ROOT = path.resolve(__dirname, '..', '..', '..');

/** The repository root, one above the extension. */
export const REPOSITORY_ROOT = path.resolve(EXTENSION_ROOT, '..');

/** `src_vs_code/src/` — the TypeScript sources the structural tests read. */
export const SOURCE_ROOT = path.join(EXTENSION_ROOT, 'src');

/** The compiled strict fake `wsl.exe` — a Node script, started through the same runner seam as the real one. */
export const FAKE_SCRIPT = path.join(EXTENSION_ROOT, 'out', 'test', 'fake', 'fakeWsl.js');

/** The bundle esbuild writes — what ships. */
export const BUNDLE = path.join(EXTENSION_ROOT, 'dist', 'extension.js');

/** `contracts/golden/` — written by the daemon's scenario harness (E5.S0), only READ here (plan §15g m7). */
export const GOLDEN_ROOT = path.join(REPOSITORY_ROOT, 'contracts', 'golden');

/** Every golden set present — `head` (E5.S0) and, once frozen at the E5 live gate, `daemon-0.1.0` — DERIVED from the folder. */
export function goldenSets(): string[] {
  return fs.readdirSync(GOLDEN_ROOT, { withFileTypes: true }).filter((e) => e.isDirectory()).map((e) => e.name).sort();
}

/** One golden answer, parsed. */
export function golden(set: string, verb: 'status' | 'preview' | 'doctor'): Record<string, unknown> {
  return JSON.parse(fs.readFileSync(path.join(GOLDEN_ROOT, set, `${verb}.json`), 'utf8')) as Record<string, unknown>;
}

/** Every `.ts` file under `dir`, recursively, as absolute paths. */
export function tsFilesUnder(dir: string): string[] {
  return fs.readdirSync(dir, { withFileTypes: true }).flatMap((entry) => {
    const full = path.join(dir, entry.name);
    if (entry.isDirectory()) {
      return tsFilesUnder(full);
    }

    return entry.name.endsWith('.ts') ? [full] : [];
  });
}

/** A path relative to `src_vs_code/`, with forward slashes — how the structural tests name a file. */
export function fromExtensionRoot(file: string): string {
  return path.relative(EXTENSION_ROOT, file).split(path.sep).join('/');
}

/** The shipped sources: everything under `src/` but `src/test/`. */
export function shippedSources(): string[] {
  return tsFilesUnder(SOURCE_ROOT).filter((file) => !fromExtensionRoot(file).startsWith('src/test/'));
}
