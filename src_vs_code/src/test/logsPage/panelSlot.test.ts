import assert from 'node:assert/strict';
import { test } from 'node:test';

import { PanelSlot, type SlotPanel } from '../../logsPage/panelSlot';

/**
 * Review K3 (coai) and the security review's below-threshold note: exactly ONE Logs panel receives views. A second press
 * of *Logs* before the first one's awaits finish must reveal the panel the first created, and a panel VS Code restores
 * while one is already open is disposed — vscode-free, so `logsPanel.ts` is thin wiring over this slot.
 */

class FakePanel implements SlotPanel {
  revealed = 0;
  disposed = false;
  private readonly listeners: (() => void)[] = [];

  reveal(): void {
    this.revealed += 1;
  }

  /** Fires its listeners on every call: a late dispose event of an OLD panel is what the guard is for. */
  dispose(): void {
    this.disposed = true;
    this.listeners.forEach((listener) => listener());
  }

  onDidDispose(listener: () => void): void {
    this.listeners.push(listener);
  }
}

test('review K3: two presses in a row create ONE panel — the second, before any await, finds and reveals it', async () => {
  const slot = new PanelSlot<FakePanel>();
  const created: FakePanel[] = [];
  const create = (): FakePanel => { const panel = new FakePanel(); created.push(panel); return panel; };
  const first = slot.open(create);
  const second = slot.open(create);
  await Promise.resolve();
  assert.equal(created.length, 1);
  assert.deepEqual([first.created, second.created], [true, false]);
  assert.equal(second.panel, first.panel);
  assert.equal(first.panel.revealed, 1);
});

test('review K3: a restored panel is kept when none is open, and disposed when one is — the open one revealed', () => {
  const slot = new PanelSlot<FakePanel>();
  const restored = new FakePanel();
  assert.equal(slot.adopt(restored), true);
  assert.equal(slot.current(), restored);
  const extra = new FakePanel();
  assert.equal(slot.adopt(extra), false);
  assert.equal(extra.disposed, true, 'the extra one is disposed');
  assert.equal(restored.revealed, 1);
  assert.equal(slot.current(), restored, 'exactly one receives views');
});

test('review K3: a panel closed by the user empties the slot — a disposed OLD panel never empties a newer one', () => {
  const slot = new PanelSlot<FakePanel>();
  const first = slot.open(() => new FakePanel()).panel;
  first.dispose();
  assert.equal(slot.current(), undefined);
  const second = slot.open(() => new FakePanel()).panel;
  first.dispose();
  assert.equal(slot.current(), second);
  slot.dispose();
  assert.equal(second.disposed, true);
  assert.equal(slot.current(), undefined);
});
