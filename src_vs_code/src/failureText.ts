import type { Failure } from './client/outcome';
import { PREVIEW_CONTAINER_ASSUMPTION } from './client/verbs';

/**
 * The words for each way a verb can fail to answer (`client/outcome.ts`): a short LABEL for the status bar and the
 * panel's rows, and a SENTENCE for the tooltip and the panel's notice. One table, typed by the failure kind, so a kind
 * added to the client without words here does not compile.
 */

export interface FailureText {
  readonly label: string;
  readonly sentence: string;
}

type Kind = Failure['kind'];
type Words = { readonly [K in Kind]: (failure: Extract<Failure, { kind: K }>) => FailureText };

function lines(messages: readonly string[]): string {
  return messages.length === 0 ? '' : ` ${messages.join(' ')}`;
}

const WORDS: Words = {
  notWindows: (f) => ({ label: 'Windows + WSL only', sentence: `AI OS Care runs on Windows with WSL; this window runs on ${f.platform}, so nothing is asked.` }),
  wslMissing: (f) => ({ label: 'WSL not found', sentence: `wsl.exe was not found: ${f.detail}.` }),
  distroRefused: (f) => ({ label: 'distribution refused', sentence: `The distribution "${f.distro}" was refused: ${f.reason}. Check the wslCare.distro setting.` }),
  noDefaultDistro: (f) => ({ label: 'no default distribution', sentence: `${f.detail}. Set wslCare.distro to the distribution to show.` }),
  stopped: (f) => ({ label: 'WSL stopped', sentence: `The distribution "${f.distro}" is not running. AI OS Care makes no call that would start it — use "Start WSL and check" in the panel.` }),
  wslFailed: (f) => ({ label: 'WSL failed', sentence: `wsl.exe failed: ${f.message}` }),
  notInstalled: (f) => ({ label: 'daemon not installed', sentence: `The wsl-care daemon is not installed in "${f.distro}" (/opt/wsl-care/bin/wsl-care is missing).` }),
  unsupportedDistro: (f) => ({ label: 'unsupported distro', sentence: `"${f.distro}" is too old for the daemon (it needs Ubuntu 24.04 or newer, glibc 2.39): ${f.detail}` }),
  refused: (f) => ({ label: 'refused by the daemon', sentence: `The daemon refused the request.${lines(f.messages)}` }),
  internalDefect: (f) => ({ label: 'daemon defect', sentence: `The daemon hit an internal error.${lines(f.messages)}` }),
  interrupted: () => ({ label: 'interrupted', sentence: 'The daemon was stopped by a signal before it answered.' }),
  timedOut: (f) => ({ label: 'timed out', sentence: `The daemon did not answer within ${Math.round(f.timeoutMs / 1000)} s; wsl.exe was stopped.` }),
  previewTooManyContainers: (f) => ({ label: `too many containers for a quick preview (${f.containers})`, sentence: `The cleanup preview did not finish within ${Math.round(f.timeoutMs / 1000)} s: ${f.containers} containers are running, more than the ${PREVIEW_CONTAINER_ASSUMPTION} its ceiling allows for — too many containers for a quick preview; wsl.exe was stopped.` }),
  unknownFailure: (f) => ({ label: 'failed', sentence: `The daemon call failed (exit ${f.code ?? 'unknown'}).${lines(f.messages)}` }),
  unparseable: (f) => ({ label: 'unreadable answer', sentence: `The daemon's answer could not be read: ${f.detail}.` }),
  needsNewerExtension: (f) => ({ label: 'needs a newer extension', sentence: `The daemon answers in schema ${f.schemaVersion}, which this extension does not know — update the extension.` }),
  daemonTooOld: (f) => ({ label: 'daemon too old', sentence: `The daemon is ${f.version}; this extension needs ${f.minimum} or newer — update the daemon.` }),
  readRefused: (f) => ({ label: 'not asked', sentence: `The daemon was not asked: ${f.detail}.` }),
};

/** Every failure kind, in the table's order — what the tests walk. */
export const FAILURE_KINDS = Object.keys(WORDS) as readonly Kind[];

export function failureText(failure: Failure): FailureText {
  const words = WORDS[failure.kind] as (f: Failure) => FailureText;

  return words(failure);
}
