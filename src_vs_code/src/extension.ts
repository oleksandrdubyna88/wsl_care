import * as vscode from 'vscode';

import { WslCareClient } from './client/WslCareClient';
import { chooseRunner, runnerFor } from './process/runnerSelection';

/**
 * WSL Care — the read-only extension over the `wsl-care` daemon (plan §7, E5). It runs on the Windows side
 * (`extensionKind: ["ui"]`) and reaches the daemon only through `WslCareClient`, which starts the absolute
 * `%SystemRoot%\System32\wsl.exe` with one of four read-only verbs; no root call path exists in it (§15f #5).
 *
 * E5.S1 wires the client and nothing visible: activation starts no process. The status bar and the panel that call it
 * — and the polling policy that decides when — are E5.S2's.
 */

/** The client this window uses; E5.S2 hangs the status bar and the panel on it. */
let client: WslCareClient | undefined;

export function activate(context: vscode.ExtensionContext): void {
  const choice = chooseRunner(context.extensionMode === vscode.ExtensionMode.Test, process.env);
  client = new WslCareClient({
    runner: runnerFor(choice),
    platform: process.platform,
    env: process.env,
    // Application-scoped (package.json): a workspace's .vscode/settings.json cannot name the distribution.
    distroSetting: () => vscode.workspace.getConfiguration('wslCare').get<string>('distro', ''),
  });
}

/** This window's client, once activated — what E5.S2's views read. */
export function currentClient(): WslCareClient | undefined {
  return client;
}

export function deactivate(): void {
  client = undefined;
}
