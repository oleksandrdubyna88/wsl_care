import * as fs from 'node:fs';
import * as path from 'node:path';

import { EXTENSION_ROOT } from './paths';

/**
 * The extension's bundles as DATA: `scripts/bundle-entries.json` is the ONE list `scripts/bundle.mjs` loops over (the host's
 * bundle and the full view's webview bundle), and the tests read the very same file — so the options a test bundles a page with
 * are the options that ship, not a hand-kept copy that could drift.
 */

export interface BundleEntry {
  /** `host` runs in the extension host (Node); `webview` runs in a webview page (browser). */
  readonly kind: 'host' | 'webview';
  readonly entryPoints: readonly string[];
  readonly outfile: string;
  readonly bundle: boolean;
  readonly format: 'cjs' | 'iife';
  readonly platform: 'node' | 'browser';
  readonly target: string;
  readonly sourcemap: boolean;
  readonly minify: boolean;
  readonly external?: readonly string[];
}

/** Every entry of `scripts/bundle-entries.json`, in build order. */
export function bundleEntries(): BundleEntry[] {
  return JSON.parse(fs.readFileSync(path.join(EXTENSION_ROOT, 'scripts', 'bundle-entries.json'), 'utf8')) as BundleEntry[];
}

/** The entries that run in a webview page. */
export function webviewEntries(): BundleEntry[] {
  return bundleEntries().filter((entry) => entry.kind === 'webview');
}

/** The esbuild options of an entry as `bundle.mjs` passes them, without the `kind` esbuild does not know. */
export function esbuildOptionsOf(entry: BundleEntry): Record<string, unknown> {
  const { kind: _kind, ...options } = entry;

  return options;
}
