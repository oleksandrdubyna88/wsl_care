import type { Failure } from '../client/outcome';
import { failureText, type FailureText } from '../failureText';
import { safeText } from '../text/safeText';
import type { RootFailure } from './rootOutcome';

/**
 * The words for each way a root call can fail (E6.S2): a short LABEL for a button's greyed state and a SENTENCE for its
 * tooltip or notice — one table typed by the kind, so a kind added to `rootOutcome.ts` without words here does not
 * compile; the read-only client's kinds keep `failureText.ts`'s words. E6.S3 renders them.
 *
 * <p>This module is where the prose about root lives: the bundle scan forbids the word "root" in every other region of
 * the bundle unless it is one of THESE exact literals (`bundleScan.test.ts`), as it allows `sudo` only in the install
 * command. No argv word is spelt here — those are `rootCall.ts`'s alone.</p>
 */

type RootKind = Exclude<RootFailure['kind'], Failure['kind']>;
type Words = { readonly [K in RootKind]: (failure: Extract<RootFailure, { kind: K }>) => FailureText };

/** Daemon text and caller-supplied names reach a native message: through the one sanitiser, short. */
function safe(text: string): string {
  return safeText(text, 80);
}

function lines(messages: readonly string[]): string {
  return messages.length === 0 ? '' : ` ${messages.map(safe).join(' ')}`;
}

function runningText(failure: { readonly running: { readonly reason: string } | undefined }): string {
  return failure.running === undefined ? '' : ` ${safe(failure.running.reason)}`;
}

const WORDS: Words = {
  rootBusy: (f) => ({ label: 'a cleanup call is in flight', sentence: `Another AI OS Care cleanup call to "${safe(f.distro)}" is still in flight; wait for its answer.` }),
  actionsUnavailable: (f) => ({ label: 'Update daemon', sentence: `The daemon is ${safe(f.version)}; cleanups need ${f.minimum} or newer (it does not offer ${f.missing.map(safe).join(', ')}) — Update daemon.` }),
  idsRefused: (f) => ({ label: 'not offered', sentence: `Not offered by this daemon and extension: ${f.refused.length === 0 ? 'no action was named' : f.refused.map(safe).join(', ')}. Offered: ${f.allowed.join(', ') || 'none'}. Nothing was started.` }),
  runIdRefused: () => ({ label: 'run id refused', sentence: 'That run id is not one the daemon writes (yyyyMMddTHHmmssZ-<pid>); nothing was started.' }),
  rootRefused: (f) => ({ label: 'needs root', sentence: `WSL could not run the daemon as root in this distribution, so cleanups are unavailable: ${failureText(f.reason).sentence}` }),
  previewNotHeld: () => ({ label: 'preview again', sentence: 'This confirmation does not belong to a preview this window took; preview again.' }),
  distroChanged: (f) => ({ label: 'preview again', sentence: `The preview was taken in "${safe(f.previewed)}", but the distribution is now "${safe(f.now)}"; preview again.` }),
  shownListInvalid: (f) => ({ label: 'preview unreadable', sentence: `A4's preview cannot be confirmed: ${safe(f.reason)}. Nothing was started.` }),
  shownListRefused: (f) => ({ label: 'refused — retry', sentence: `The daemon refused the list of volumes it was handed${lines(f.messages)}. Nothing was started — retry the cleanup.` }),
  detachUnavailable: (f) => ({ label: 'needs systemd', sentence: `A cleanup runs in its own systemd unit, and this distribution has no systemd; there is no fallback.${lines(f.messages)}` }),
  detachStartFailed: (f) => ({ label: 'did not start', sentence: `The cleanup's unit would not start; nothing runs, and its request was removed.${lines(f.messages)}` }),
  queueFull: (f) => ({ label: 'too many requests', sentence: `The daemon already holds as many waiting requests as it accepts; nothing was written.${lines(f.messages)}` }),
  busy: (f) => ({ label: 'a run is in flight', sentence: `Another run holds the daemon; nothing was written.${runningText(f)}${lines(f.messages)}` }),
  wedged: (f) => ({ label: 'a run is wedged', sentence: `A wedged run holds the daemon; nothing was written.${runningText(f)}${lines(f.messages)}` }),
  needsRoot: (f) => ({ label: 'needs root', sentence: `The daemon answered that this needs root.${lines(f.messages)}` }),
  observeOnly: (f) => ({ label: 'observe-only', sentence: `This installation is set to observe only; it runs no cleanup.${lines(f.messages)}` }),
  stateUnreadable: (f) => ({ label: 'state unreadable', sentence: `The daemon cannot read its own running state; nothing was written.${lines(f.messages)}` }),
  requestGone: (f) => ({ label: 'request gone', sentence: `No request names that run any more.${lines(f.messages)}` }),
  actionFailed: (f) => ({ label: 'an action failed', sentence: `An action failed.${lines(f.messages)}` }),
  recordsUnreadable: (f) => ({ label: 'records unreadable', sentence: `The daemon's records cannot be read.${lines(f.messages)}` }),
};

/** Every root-only kind, in the table's order — what the tests walk. */
export const ROOT_FAILURE_KINDS = Object.keys(WORDS) as readonly RootKind[];

function isRootKind(failure: RootFailure): failure is Extract<RootFailure, { kind: RootKind }> {
  return Object.hasOwn(WORDS, failure.kind);
}

export function rootFailureText(failure: RootFailure): FailureText {
  if (!isRootKind(failure)) {
    return failureText(failure as Failure);
  }
  const words = WORDS[failure.kind] as (f: RootFailure) => FailureText;

  return words(failure);
}
