import { DAEMON_EXIT } from '../client/exitCodes';
import { classifyExit, daemonMessages } from '../client/failures';
import { DAEMON_PATH } from '../client/WslCareClient';
import { decodeWslText, stripAnsi } from '../wsl/wslText';
import type { ProcessResult } from '../process/runner';
import type { RootFailure } from './rootOutcome';

/**
 * How a root call that did not answer is read (E6.S2, plan §15j m3, §15f #7). Each code of `contracts/exit-codes.json`
 * the root paths can meet is its own kind — `rootFailures.test.ts` walks the contract and fails on a code that falls
 * through to "unknown failure" — and the read-only client's reading (`classifyExit`) covers the rest: `wsl.exe`'s own -1,
 * the measured missing-binary signature, the old glibc, 2, 70, 130.
 */

type Messages = readonly string[];
type ByMessages = (messages: Messages) => RootFailure;

/** The root paths' own codes, by the daemon's name for them (`DAEMON_EXIT`) — never a number at a call site. */
const ROOT_CODES: ReadonlyMap<number, ByMessages> = new Map<number, ByMessages>([
  [DAEMON_EXIT.actionFailed, (messages) => ({ kind: 'actionFailed', messages })],
  [DAEMON_EXIT.recordsUnreadable, (messages) => ({ kind: 'recordsUnreadable', messages })],
  [DAEMON_EXIT.detachUnavailable, (messages) => ({ kind: 'detachUnavailable', messages })],
  [DAEMON_EXIT.detachStartFailed, (messages) => ({ kind: 'detachStartFailed', messages })],
  [DAEMON_EXIT.queueFull, (messages) => ({ kind: 'queueFull', messages })],
  [DAEMON_EXIT.busy, (messages) => ({ kind: 'busy', messages, running: undefined })],
  [DAEMON_EXIT.wedged, (messages) => ({ kind: 'wedged', messages, running: undefined })],
  [DAEMON_EXIT.needsRoot, (messages) => ({ kind: 'needsRoot', messages })],
  [DAEMON_EXIT.observeOnly, (messages) => ({ kind: 'observeOnly', messages })],
  [DAEMON_EXIT.stateUnreadable, (messages) => ({ kind: 'stateUnreadable', messages })],
  [DAEMON_EXIT.requestGone, (messages) => ({ kind: 'requestGone', messages })],
]);

/** How a root call that ended in an exit code other than 0 is read. `shownOnStdin`: the call piped A4's list (`--only -`). */
export function exitFailure(result: Extract<ProcessResult, { kind: 'exited' }>, distro: string, shownOnStdin: boolean): RootFailure {
  const messages = daemonMessages(stripAnsi(decodeWslText(result.stderr)));
  const own = ROOT_CODES.get(result.code);
  if (own !== undefined) {
    return own(messages);
  }

  return shownOnStdin && result.code === DAEMON_EXIT.usage ? { kind: 'shownListRefused', messages } : classifyExit(result.code, result.stdout, result.stderr, distro, DAEMON_PATH);
}
