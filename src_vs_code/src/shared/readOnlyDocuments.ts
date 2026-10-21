import * as vscode from 'vscode';

/**
 * A READ-ONLY document of text the extension is about to run (a script the person confirms): a `TextDocumentContentProvider` under its own
 * scheme — its documents cannot be edited — where each document gets its own URI and is served from a snapshot, so nothing typed anywhere
 * can reach the request. Shared by the Windows Time guard's install tab and the Windows tasks' (WR-E3, reuse-first step 2.2).
 */
/** What a read-only document holds: a script the person confirms, or a report they read. */
export type DocumentKind = 'install' | 'preview';

const EXTENSION_OF: { readonly [K in DocumentKind]: string } = { install: 'ps1', preview: 'txt' };

export class ReadOnlyDocuments implements vscode.TextDocumentContentProvider {
  private readonly snapshots = new Map<string, string>();
  private count = 0;

  constructor(private readonly scheme: string, private readonly stem: string) {}

  /**
   * A new URI for `text` — the previous snapshot is dropped, the new one is what the tab serves. `part` names the tab (`windows-watch-task`…); `kind` is what
   * the text IS: a script (`install`, `.ps1`) or a report to read (`preview`, `.txt` — a text report must not get PowerShell highlighting).
   */
  add(text: string, part = '', kind: DocumentKind = 'install'): vscode.Uri {
    this.count += 1;
    this.snapshots.clear();
    const uri = vscode.Uri.from({ scheme: this.scheme, path: `/${this.stem}${part === '' ? '' : `-${part}`}-${kind}-${this.count}.${EXTENSION_OF[kind]}` });
    this.snapshots.set(uri.toString(), text);

    return uri;
  }

  provideTextDocumentContent(uri: vscode.Uri): string {
    return this.snapshots.get(uri.toString()) ?? '';
  }

  /** Opens `text` in a read-only tab beside nothing else: the active column, a preview. */
  async show(text: string, part = '', kind: DocumentKind = 'install'): Promise<void> {
    const document = await vscode.workspace.openTextDocument(this.add(text, part, kind));
    await vscode.window.showTextDocument(document, { preview: true, viewColumn: vscode.ViewColumn.Active });
  }
}
