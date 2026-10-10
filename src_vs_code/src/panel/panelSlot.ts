/**
 * The ONE panel of a page (review K3, and the security review's below-threshold note) — the Logs page's, and since E10.S2a the
 * Archive page's (moved here from `logsPage/` so both pages hold one slot each of the SAME type): exactly one panel receives the host's
 * views. `open` creates or reveals SYNCHRONOUSLY — so a second press of *Logs* while the first is still awaiting finds the
 * panel the first created — and `adopt` keeps a panel VS Code restored only when none is open, disposing the extra one.
 * vscode-free: `logsPanel.ts` and `archivePanel.ts` pass VS Code's `WebviewPanel`s, the tests fakes.
 */

export interface SlotPanel {
  reveal(): void;
  dispose(): void;
  onDidDispose(listener: () => void): unknown;
}

export class PanelSlot<P extends SlotPanel> {
  private panel: P | undefined;

  current(): P | undefined {
    return this.panel;
  }

  /** The open panel, revealed — or a new one from `create`, held before this returns. */
  open(create: () => P): { readonly panel: P; readonly created: boolean } {
    if (this.panel !== undefined) {
      this.panel.reveal();
      return { panel: this.panel, created: false };
    }

    return { panel: this.hold(create()), created: true };
  }

  /** A panel VS Code restored: held when none is open (true); otherwise disposed and the open one revealed (false). */
  adopt(panel: P): boolean {
    if (this.panel !== undefined && this.panel !== panel) {
      panel.dispose();
      this.panel.reveal();
      return false;
    }
    this.hold(panel);

    return true;
  }

  dispose(): void {
    this.panel?.dispose();
    this.panel = undefined;
  }

  private hold(panel: P): P {
    this.panel = panel;
    // A late dispose of an OLDER panel never empties the slot of a newer one.
    panel.onDidDispose(() => {
      if (this.panel === panel) {
        this.panel = undefined;
      }
    });

    return panel;
  }
}
