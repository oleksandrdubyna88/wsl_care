import * as vscode from 'vscode';

import type { InstallDeps, InstallPrompt, InstallTerminal, TerminalSpec } from './installDaemon';

/**
 * The two VS Code surfaces *Install daemon* uses — the modal and the terminal — chosen the way the runner is
 * (`process/runnerSelection.ts`): by whether the extension host runs the extension in Test mode, and by nothing else.
 *
 * - **not Test mode** — `showWarningMessage({ modal: true })` and `createTerminal({ shellPath, shellArgs })`.
 * - **Test mode** — a RECORDER for both: the prompt is recorded and answered from the recorder (declined unless a
 *   scenario says otherwise), and the "terminal" records its spec, `show()` and every `sendText`. A real terminal is
 *   never opened in Test mode — its shell would be the real `%SystemRoot%\System32\wsl.exe`, which no test may start,
 *   and a pseudo-terminal is outside the `child_process` tripwire's reach.
 */

export type InstallUi = Pick<InstallDeps, 'confirm' | 'openTerminal' | 'report'>;

export interface RecordedTerminal {
  readonly spec: TerminalSpec;
  shown: boolean;
  readonly typed: { readonly text: string; readonly addNewLine: boolean }[];
}

export interface InstallRecorder {
  readonly prompts: InstallPrompt[];
  readonly terminals: RecordedTerminal[];
  readonly reports: string[];
  /** How the recorded modal is answered. */
  answer: boolean;
}

export function newInstallRecorder(): InstallRecorder {
  return { prompts: [], terminals: [], reports: [], answer: false };
}

function realUi(): InstallUi {
  return {
    confirm: async (prompt) => (await vscode.window.showWarningMessage(prompt.message, { modal: true, detail: prompt.detail }, prompt.confirm)) === prompt.confirm,
    openTerminal: (spec) => vscode.window.createTerminal({ name: spec.name, shellPath: spec.shellPath, shellArgs: [...spec.shellArgs] }),
    report: (message) => { void vscode.window.showErrorMessage(message); },
  };
}

function recordingUi(recorder: InstallRecorder): InstallUi {
  return {
    confirm: (prompt) => {
      recorder.prompts.push(prompt);
      return Promise.resolve(recorder.answer);
    },
    openTerminal: (spec): InstallTerminal => {
      const terminal: RecordedTerminal = { spec, shown: false, typed: [] };
      recorder.terminals.push(terminal);
      return { show: () => { terminal.shown = true; }, sendText: (text, addNewLine) => { terminal.typed.push({ text, addNewLine }); } };
    },
    report: (message) => { recorder.reports.push(message); },
  };
}

export function installUiFor(isTestMode: boolean, recorder: InstallRecorder): InstallUi {
  return isTestMode ? recordingUi(recorder) : realUi();
}
