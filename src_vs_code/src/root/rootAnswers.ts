import { readEnum, type EnumRead } from '../client/enumValue';
import { checkedBody } from '../client/handshake';
import { RUN_ID_BODY } from '../shared/shapes';
import type { JsonObject } from '../client/outcome';
import { actionIdOf, MAX_SHOWN_VOLUMES, runIdOf, shownCap, volumeNameOf, type ActionId, type ActionIds, type RunId, type VolumeName } from './rootIds';
import { RUNNING_STATES, type HeldPreview, type PreviewedAction, type PreviewItem, type RootFailure, type RunningBlock, type ShownSelection } from './rootOutcome';

/**
 * The root answers, read as untrusted input (E6.S2): the schema checked by the client's own `checkedBody`, every value that
 * may later become argv or a stdin line validated by `rootIds.ts`, every enum through `readEnum` (an unknown value is
 * "unknown (<value>)", never a crash, §15j m1), every other key ignored.
 */

/** The `result` values a detach / stop answer may carry (E6.S1's `HandOffReport`). */
export const HAND_OFF_RESULTS = ['accepted', 'unknown', 'stopping'] as const;

export type HandOffResult = (typeof HAND_OFF_RESULTS)[number];

/** A detach / stop answer, read. */
export interface HandOff {
  readonly result: EnumRead<HandOffResult>;
  readonly runId: RunId | undefined;
  readonly unit: string;
  readonly productVersion: string | undefined;
}

/** A unit name as the daemon spells one (`wsl-care-act@<runId>.service`, `wsl-care.service`) — anything else is not shown. */
const UNIT = new RegExp(`^wsl-care(?:-act@${RUN_ID_BODY})?\\.service$`);

function isObject(value: unknown): value is JsonObject {
  return typeof value === 'object' && value !== null && !Array.isArray(value);
}

function stringOr(value: unknown, fallback: string): string {
  return typeof value === 'string' ? value : fallback;
}

function optionalString(value: unknown): string | undefined {
  return typeof value === 'string' ? value : undefined;
}

function actionsOf(value: unknown): readonly string[] {
  return Array.isArray(value) ? value.filter((v): v is string => typeof v === 'string') : [];
}

function pidOf(value: unknown): number | undefined {
  return typeof value === 'number' && Number.isInteger(value) && value > 0 ? value : undefined;
}

/** `status.running`, or undefined when the status carries none (a daemon before E6.S0). */
export function runningOf(status: JsonObject): RunningBlock | undefined {
  const running = status.running;
  if (!isObject(running)) {
    return undefined;
  }

  return { state: readEnum(running.state, RUNNING_STATES), runId: runIdOf(running.runId), trigger: stringOr(running.trigger, ''), actions: actionsOf(running.actions), reason: stringOr(running.reason, ''), current: stringOr(running.current, ''), pid: pidOf(running.pid) };
}

/** A detach / stop answer (exit 0): its result, run id and unit — or why it cannot be read. */
export function parseHandOff(stdout: string): HandOff | RootFailure {
  const checked = checkedBody(stdout);
  if ('kind' in checked) {
    return checked;
  }
  const { body } = checked;
  const unit = stringOr(body.unit, '');

  return { result: readEnum(body.result, HAND_OFF_RESULTS), runId: runIdOf(body.runId), unit: UNIT.test(unit) ? unit : '', productVersion: optionalString(body.productVersion) };
}

function previewedAction(value: unknown): PreviewedAction[] {
  const id = isObject(value) ? actionIdOf(value.id) : undefined;
  if (!isObject(value) || id === undefined) {
    return [];
  }
  const preview = previewOf(value);
  const details = detailsOf(preview);

  return [{ id, status: stringOr(value.status, ''), reason: reasonOf(value, preview), ...figuresOf(preview, details), details }];
}

/** The preview part of an action's answer, or an empty one. */
function previewOf(action: JsonObject): JsonObject {
  return isObject(action.preview) ? action.preview : {};
}

/** The action's own reason, or — when it gives none — its preview's (an unreadable preview says why there). */
function reasonOf(action: JsonObject, preview: JsonObject): string {
  return stringOr(action.reason, '') || stringOr(preview.reason, '');
}

/** What a preview says it would remove: its text, whether it could be read, the count, the bytes, the listed names (the details' — one walk, own review #10). */
function figuresOf(preview: JsonObject, details: readonly PreviewItem[]): Pick<PreviewedAction, 'what' | 'available' | 'count' | 'bytes' | 'items'> {
  return { what: stringOr(preview.what, ''), available: preview.available !== false, count: countOrUndefined(preview.count), bytes: countOrUndefined(preview.bytes), items: details.map((d) => d.name) };
}

/** Every listed item whole (E10.S1b): its name, its bytes when the daemon gave a count, its note — strings as the daemon wrote them. */
function detailsOf(preview: JsonObject): readonly PreviewItem[] {
  const items = Array.isArray(preview.items) ? preview.items : [];

  return items.flatMap((item) => (isObject(item) && typeof item.name === 'string' ? [{ name: item.name, bytes: countOrUndefined(item.bytes), note: stringOr(item.note, '') }] : []));
}

function countOrUndefined(value: unknown): number | undefined {
  return isCount(value) ? value : undefined;
}

/** Every shown name validated, in order — or the position of the first that is not a 64-hex name. */
function validNames(shown: readonly unknown[]): readonly VolumeName[] | number {
  const names: VolumeName[] = [];
  for (const [index, value] of shown.entries()) {
    const name = volumeNameOf(value);
    if (name === undefined) {
      return index;
    }
    names.push(name);
  }

  return names;
}

function isCount(value: unknown): value is number {
  return typeof value === 'number' && Number.isInteger(value) && value >= 0;
}

function countOf(a4: JsonObject): number | undefined {
  const count = isObject(a4.preview) ? a4.preview.count : undefined;

  return isCount(count) ? count : undefined;
}

/** The plan's invariant (§15k #11): `shown.length == min(count, cap)`, and `shownTruncated` exactly when the cap in force cut it. */
function consistent(names: readonly VolumeName[], count: number, capped: boolean, cap: number): boolean {
  return names.length === Math.min(count, cap) && capped === count > cap;
}

function selectionOf(names: readonly VolumeName[], count: number | undefined, truncated: unknown, cap: number): ShownSelection | RootFailure {
  const capped = truncated === true;

  return count !== undefined && consistent(names, count, capped, cap) ? { names, count, truncated: capped, cap } : invalid(`its shown list holds ${names.length} names, which does not match its count (${String(count)}) and its cap flag (${String(capped)})`);
}

function invalid(reason: string): RootFailure {
  return { kind: 'shownListInvalid', reason };
}

/** The raw shown list: absent (the preview could not read the volumes), a list within the cap, or refused. */
function shownList(a4: JsonObject | undefined, cap: number): readonly unknown[] | undefined | RootFailure {
  const shown = a4 === undefined ? undefined : a4.shown;

  return shown === undefined ? undefined : cappedList(shown, cap);
}

function cappedList(shown: unknown, cap: number): readonly unknown[] | RootFailure {
  return Array.isArray(shown) && shown.length <= cap ? shown : invalid(`its shown list is not a list of at most ${cap} names`);
}

/** A4's shown list of a preview: absent (A4 then cannot be confirmed), held, or refused. */
function a4Selection(a4: JsonObject | undefined, cap: number): ShownSelection | undefined | RootFailure {
  const shown = shownList(a4, cap);
  if (shown === undefined || !Array.isArray(shown)) {
    return shown as RootFailure | undefined;
  }
  const names = validNames(shown);

  return typeof names === 'number' ? invalid(`entry ${names + 1} of its shown list is not an anonymous volume's name (64 lowercase hex digits)`) : selectionOf(names, countOf(a4 as JsonObject), (a4 as JsonObject).shownTruncated, cap);
}

function actionEntry(actions: unknown, id: ActionId): JsonObject | undefined {
  const found = Array.isArray(actions) ? actions.find((a) => isObject(a) && a.id === id) : undefined;

  return isObject(found) ? found : undefined;
}

/** What the preview needs beyond its body: where and when it was taken, and what was asked. */
export interface PreviewContext {
  readonly distro: string;
  readonly ids: ActionIds;
  readonly takenAtMs: number;
  /** The daemon's published `maxShownNames` in the status the preview was gated on (`MAX_SHOWN_VOLUMES` when absent). */
  readonly maxShownNames?: number;
}

/** A4's selection when the preview asked for A4, else nothing. */
function a4Of(body: JsonObject, context: PreviewContext): ShownSelection | undefined | RootFailure {
  return context.ids.includes('A4') ? a4Selection(actionEntry(body.actions, 'A4'), shownCap(context.maxShownNames ?? MAX_SHOWN_VOLUMES)) : undefined;
}

/** `act <ids> --preview --json` (exit 0), held — or why it cannot be. */
export function parsePreview(stdout: string, context: PreviewContext): HeldPreview | RootFailure {
  const checked = checkedBody(stdout);
  const a4 = 'kind' in checked ? checked : a4Of(checked.body, context);
  if (a4 !== undefined && 'kind' in a4) {
    return a4;
  }

  return heldOf(checked as { body: JsonObject }, a4, context);
}

function heldOf({ body }: { body: JsonObject }, a4: ShownSelection | undefined, context: PreviewContext): HeldPreview {
  const actions = Array.isArray(body.actions) ? body.actions.flatMap(previewedAction) : [];

  return { distro: context.distro, ids: context.ids, actions, a4, takenAtMs: context.takenAtMs, productVersion: optionalString(body.productVersion) };
}
