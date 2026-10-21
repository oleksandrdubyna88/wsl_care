import { createDataTable, createRecommendationCard, createTabs } from '@oleksandrdubyna88/vscode-webview-kit/components';
import '@oleksandrdubyna88/vscode-webview-kit/components.css';

/**
 * The full view's page script — the entry of the SECOND esbuild bundle (`scripts/bundle.mjs`: `dist/webview/full.js`, an
 * IIFE for the Chromium of VS Code 1.85, and `dist/webview/full.css`, the kit's stylesheet esbuild emits beside it).
 *
 * W0 of `todo/PLAN_disk_health_E18.md` lays the road and nothing visible: this file imports the kit's components and their
 * stylesheet, acquires the VS Code API once, and leaves an empty mount point. W1b builds the view from the components here. No panel
 * opens this page yet, so the extension's behaviour does not change.
 *
 * It runs in a WEBVIEW, so it is compiled apart (`tsconfig.webview.json`, with the DOM) and may import nothing of the host:
 * no `vscode`, no Node module, no module outside this folder — and no host module imports it (`webviewBoundary.test.ts`).
 */

declare function acquireVsCodeApi(): { postMessage(message: unknown): void };

/** The VS Code API, acquired ONCE for the page — VS Code throws on a second call. */
export const vscode = acquireVsCodeApi();

/** The kit's components W1b builds the view from — held here so the bundle carries them (W0 proves the road, not a layout). */
export const kit = { createDataTable, createRecommendationCard, createTabs };

// The mount point is `view`, not `root`: no shipped source spells the word `root` but the root-call module (structure.test.ts).
document.getElementById('view')?.replaceChildren();
