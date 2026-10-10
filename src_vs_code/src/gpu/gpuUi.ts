import * as vscode from 'vscode';

import type { Runner } from '../process/runner';
import type { GpuRecorder, GpuUi } from './gpuRecorder';

export { newGpuRecorder, type GpuRecorder, type GpuUi } from './gpuRecorder';

/**
 * The surfaces *Enable <GPU>* and the adapter query use (PLAN_gpu_health.md D4), chosen the way the Windows Time guard's
 * are (`windowsTime/guardUi.ts`): by whether the extension host runs the extension in Test mode, and by nothing else.
 *
 * - **not Test mode** — a modal, the real runner under a progress notification for the ELEVATED run, the real runner for
 *   the unelevated query, and a notification with the outcome.
 * - **Test mode** — a RECORDER: every prompt, request and query is recorded and answered from it; no PowerShell is started
 *   (the elevated one would raise UAC, and the tripwire refuses it — and the device verbs — besides).
 */

function realUi(runner: Runner): GpuUi {
  return {
    confirm: async (prompt) => (await vscode.window.showWarningMessage(prompt.message, { modal: true, detail: prompt.detail }, prompt.confirm)) === prompt.confirm,
    run: (request) => Promise.resolve(vscode.window.withProgress({ location: vscode.ProgressLocation.Notification, title: 'Waiting for the elevated PowerShell that enables the GPU…' }, () => runner(request))),
    query: runner,
    report: (message, failed) => { void (failed ? vscode.window.showErrorMessage(message) : vscode.window.showInformationMessage(message)); },
  };
}

function recordingUi(recorder: GpuRecorder): GpuUi {
  return {
    confirm: (prompt) => { recorder.prompts.push(prompt); return Promise.resolve(recorder.answer); },
    run: (request) => { recorder.requests.push(request); return Promise.resolve(recorder.result); },
    query: (request) => { recorder.queries.push(request); return Promise.resolve(recorder.queryResult); },
    report: (message, failed) => { recorder.reports.push({ message, failed }); },
  };
}

export function gpuUiFor(isTestMode: boolean, recorder: GpuRecorder, realRunner: Runner): GpuUi {
  return isTestMode ? recordingUi(recorder) : realUi(realRunner);
}
