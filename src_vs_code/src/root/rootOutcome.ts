import type { EnumRead } from '../client/enumValue';
import type { DaemonVersion, Failure } from '../client/outcome';
import type { ActionId, ActionIds, RunId, VolumeName } from './rootIds';

/**
 * What a root call through the cleanup controller ends in (E6.S2). Each way it can fail is its own kind, because each wants
 * its own sentence (`rootFailureText.ts`) and, from E6.S3, its own button state — none of them is "an error happened".
 * The read-only client's `Failure` kinds pass through unchanged (a stopped distribution, a missing daemon, a refusal).
 */

/** The `running.state` values of the E6.S0 contract; another value reads as "unknown (<value>)" (`readEnum`). */
export const RUNNING_STATES = ['none', 'queued', 'live', 'wedged', 'dead', 'unknown', 'unreadable'] as const;

export type RunningState = (typeof RUNNING_STATES)[number];

/** `status.running`, as far as the root paths read it. */
export interface RunningBlock {
  readonly state: EnumRead<RunningState>;
  readonly runId: RunId | undefined;
  readonly trigger: string;
  readonly actions: readonly string[];
  readonly reason: string;
}

export type RootFailure =
  | Failure
  /** Another root operation of this window is still in flight in that distribution (host-side, plan §15j m9). */
  | { readonly kind: 'rootBusy'; readonly distro: string }
  /** The daemon does not ADVERTISE what this op needs: "Update daemon" (the capabilities decide, the version is the message — §15j M5). */
  | { readonly kind: 'actionsUnavailable'; readonly version: string; readonly minimum: string; readonly missing: readonly string[] }
  /** An id outside the compiled registry ∩ `status.actions`, or none at all; nothing was started. */
  | { readonly kind: 'idsRefused'; readonly refused: readonly string[]; readonly allowed: readonly ActionId[] }
  /** A run id the daemon would never write; nothing was started. */
  | { readonly kind: 'runIdRefused' }
  /** The root check (`-u root … --version`) did not answer: the actions are greyed "needs root" (§15j m4). */
  | { readonly kind: 'rootRefused'; readonly reason: Failure }
  /** A confirm of a preview this controller never issued. */
  | { readonly kind: 'previewNotHeld' }
  /** The distribution changed between the preview and its confirm. */
  | { readonly kind: 'distroChanged'; readonly previewed: string; readonly now: string }
  /** A4's shown list cannot be held or confirmed (a bad name, a count it does not match, none at all). */
  | { readonly kind: 'shownListInvalid'; readonly reason: string }
  /** Exit 2 on a confirm that piped a shown list (its 10 s ceiling, a bad line): nothing was started — a retry is offered (§15k #19). */
  | { readonly kind: 'shownListRefused'; readonly messages: readonly string[] }
  /** 69: no systemd — there is no synchronous fallback (§15j B2). */
  | { readonly kind: 'detachUnavailable'; readonly messages: readonly string[] }
  /** 71: the unit would not start; its request was removed. */
  | { readonly kind: 'detachStartFailed'; readonly messages: readonly string[] }
  /** 73: the request folder's budget is full. */
  | { readonly kind: 'queueFull'; readonly messages: readonly string[] }
  /** 75: a run is acting or queued — with the `running` block read right after. */
  | { readonly kind: 'busy'; readonly messages: readonly string[]; readonly running: RunningBlock | undefined }
  /** 76: a wedged (or uninspectable) run holds the lock — with the `running` block. */
  | { readonly kind: 'wedged'; readonly messages: readonly string[]; readonly running: RunningBlock | undefined }
  /** 77: the daemon says it needs root. */
  | { readonly kind: 'needsRoot'; readonly messages: readonly string[] }
  /** 78: the installation is observe-only. */
  | { readonly kind: 'observeOnly'; readonly messages: readonly string[] }
  /** 79: the daemon's running state cannot be read. */
  | { readonly kind: 'stateUnreadable'; readonly messages: readonly string[] }
  /** 80: no request names the run. */
  | { readonly kind: 'requestGone'; readonly messages: readonly string[] }
  /** 3: an action failed. */
  | { readonly kind: 'actionFailed'; readonly messages: readonly string[] }
  /** 4: the records cannot be read. */
  | { readonly kind: 'recordsUnreadable'; readonly messages: readonly string[] };

/** A4's shown list as the preview held it: every name validated, the total the daemon counted, whether the list was capped. */
export interface ShownSelection {
  readonly names: readonly VolumeName[];
  readonly count: number;
  readonly truncated: boolean;
}

/** One action of a preview, as far as the host reads it (the panel renders the rest from the same answer in E6.S3). */
export interface PreviewedAction {
  readonly id: ActionId;
  readonly status: string;
  readonly reason: string;
}

/**
 * A preview the controller TOOK and holds: a confirm may only name one of these (`previewNotHeld` otherwise), and A4's
 * names come from here and nowhere else (plan §15j B1) — never from a webview or a caller.
 */
export interface HeldPreview {
  readonly distro: string;
  readonly ids: ActionIds;
  readonly actions: readonly PreviewedAction[];
  readonly a4: ShownSelection | undefined;
  readonly takenAtMs: number;
  readonly productVersion: string | undefined;
}

export type PreviewOutcome = { readonly kind: 'previewed'; readonly preview: HeldPreview } | RootFailure;

/** What a detached run, a stop or a full check was handed off as. */
export type HandOffOutcome =
  /** The daemon wrote the request and started the unit (`result: accepted`). */
  | { readonly kind: 'accepted'; readonly runId: RunId; readonly unit: string; readonly productVersion: string | undefined }
  /** The answer was unknown (a timeout, a kill, `result: unknown`, a value this build does not know) and the run was SEEN queued or live. */
  | { readonly kind: 'acceptedObserved'; readonly runId: RunId | undefined; readonly running: RunningBlock }
  /** Unknown, and followed for the bound without seeing it: E6.S3 keeps the run id (when there is one) and asks `runs show`. */
  | { readonly kind: 'outcomeUnknown'; readonly runId: RunId | undefined; readonly reason: string; readonly followed: FollowCount | undefined; readonly otherRun: RunningBlock | undefined }
  /** `act --stop` was taken (`result: stopping`). */
  | { readonly kind: 'stopping'; readonly runId: RunId; readonly unit: string }
  | RootFailure;

/** How a follow went: the status polls made, and how many answered for this distribution. */
export interface FollowCount {
  readonly polls: number;
  readonly answered: number;
}

export type RootCheckOutcome = { readonly kind: 'rootOk'; readonly distro: string; readonly version: DaemonVersion } | RootFailure;
