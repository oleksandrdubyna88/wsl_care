import { createDataTable, createRecommendationCard, createTabs } from '@oleksandrdubyna88/vscode-webview-kit/components';
import '@oleksandrdubyna88/vscode-webview-kit/components.css';

/**
 * The TEST entry of W0's kit page test (`kitPage.test.ts`): a small page built from the kit's three components with
 * wsl_care-shaped data — a table of the processes that hold memory (a program, a pid, bytes held, and a command line only
 * the HOST knows), beside a recommendation card — wired the way W1b's full view will wire them: the table's reveal posts an
 * index and a request, the host answers by message, and the page hands the answer back to `reveal`. It is bundled by
 * esbuild for the browser exactly as `scripts/bundle.mjs` bundles the real entry, then RUN in the strict page harness.
 *
 * It is a test fixture, not a shipped source: `src/test/` is outside the scans of the shipped sources, and the page owns
 * its few mutable bits (a flag the host flips) the way a page script does.
 */

declare function acquireVsCodeApi(): { postMessage(message: unknown): void };

interface Process {
  readonly program: string;
  readonly pid: number;
  readonly heldBytes: number | null;
}

interface HostMessage {
  readonly type: string;
  readonly index?: number;
  readonly text?: string;
  readonly request?: string;
  readonly rows?: readonly Process[];
  readonly on?: boolean;
}

const vscode = acquireVsCodeApi();
// Replaced, never mutated (coding-style: new objects): the host's failReveals message swaps the whole value.
let mode = { failReveals: false };

const INITIAL_ROWS: readonly Process[] = [
  { program: 'ollama', pid: 4120, heldBytes: 6_442_450_944 },
  { program: 'node', pid: 8812, heldBytes: 1_288_490_188 },
  { program: 'vmmem', pid: 3000, heldBytes: null },
  { program: 'dotnet', pid: 515, heldBytes: 734_003_200 },
];

const GIB = 2 ** 30;

const table = createDataTable(document, {
  label: 'Processes',
  columns: [
    { key: 'program', header: 'Program', value: (row: Process) => row.program },
    { key: 'pid', header: 'PID', value: (row: Process) => row.pid, align: 'end' },
    { key: 'held', header: 'Held', value: (row: Process) => row.heldBytes, format: (value) => `${(Number(value) / GIB).toFixed(1)} GiB`, align: 'end' },
  ],
  rows: INITIAL_ROWS,
  search: { label: 'Search processes' },
  reveal: {
    header: 'Command line',
    button: (row: Process) => `Show the command line of ${row.program}`,
    onReveal: (index, request) => {
      if (mode.failReveals) {
        throw new Error('post failed');
      }
      vscode.postMessage({ type: 'reveal', index, request });
    },
  },
  emptyText: 'No process matches.',
  stack: 'narrow',
});

const dropCaches = document.createElement('button');
dropCaches.setAttribute('type', 'button');
dropCaches.textContent = 'Drop caches';
dropCaches.addEventListener('click', () => { vscode.postMessage({ type: 'dropCaches' }); });

const card = createRecommendationCard(document, {
  tone: 'warning',
  word: 'Warning',
  sentence: 'Memory is low: 6 % available. Drop caches to free about 2.1 GiB.',
  action: dropCaches,
  evidence: ['WSL RAM available 6 %', 'Swap 92 % used'],
});

const tabs = createTabs(document, {
  idPrefix: 'full',
  label: 'Sides',
  tabs: [
    { id: 'wsl', label: 'WSL: Ubuntu', content: table.root, status: { tone: 'warning', word: '2 warnings' } },
    { id: 'windows', label: 'Windows', content: card },
  ],
  onSelect: (id) => { vscode.postMessage({ type: 'selected', id }); },
});

const root = document.getElementById('root');
if (root === null) {
  throw new Error('the page has no #root');
}
root.appendChild(tabs.root);

window.addEventListener('message', (event: { data: HostMessage }) => {
  const message = event.data;
  switch (message.type) {
    case 'answer':
      vscode.postMessage({ type: 'answered', index: message.index, ok: table.reveal(message.index ?? -1, message.text ?? '', message.request ?? '') });
      break;
    case 'rows':
      table.setRows(message.rows ?? []);
      break;
    case 'dismantle':
      table.setRows([]);
      table.dispose();
      vscode.postMessage({ type: 'dismantled' });
      break;
    case 'dispose':
      table.dispose();
      vscode.postMessage({ type: 'disposed' });
      break;
    case 'failReveals':
      mode = { ...mode, failReveals: message.on ?? true };
      break;
    default:
      throw new Error(`unknown host message ${message.type}`);
  }
});
