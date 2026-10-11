import { readEnum, type EnumRead } from '../client/enumValue';
import { checkedBody } from '../client/handshake';
import { RUN_ID_BODY } from '../shared/shapes';
import type { JsonObject } from '../client/outcome';
import { actionIdOf, entryIdOf, MAX_PROCESS_ARGS, MAX_SHOWN_VOLUMES, runIdOf, shownCap, shownProcessOf, volumeNameOf, type ActionId, type ActionIds, type EntryId, type RunId, type ShownProcess, type VolumeName } from './rootIds';
import { RUNNING_STATES, type HeldPreview, type PreviewedAction, type PreviewItem, type RestoreSelection, type RootFailure, type RunningBlock, type ShownSelection } from './rootOutcome';

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
function figuresOf(preview: JsonObject, details: readonly PreviewItem[]): Pick<PreviewedAction, 'what' | 'available' | 'count' | 'bytes' | 'items' | 'basis'> {
  return { what: stringOr(preview.what, ''), available: preview.available !== false, count: countOrUndefined(preview.count), bytes: countOrUndefined(preview.bytes), items: details.map((d) => d.name), basis: stringOr(preview.basis, '') };
}

/** Every listed item whole (E10.S1b): its name, its bytes when the daemon gave a count, its note — strings as the daemon wrote them. */
function detailsOf(preview: JsonObject): readonly PreviewItem[] {
  const items = Array.isArray(preview.items) ? preview.items : [];

  return items.flatMap((item) => (isObject(item) && typeof item.name === 'string' ? [{ name: item.name, bytes: countOrUndefined(item.bytes), note: stringOr(item.note, '') }] : []));
}

function countOrUndefined(value: unknown): number | undefined {
  return isCount(value) ? value : undefined;
}

/** What a shown list holds: its validator, and what an entry that fails it is called in the refusal. */
interface ShownKind<T extends string> {
  readonly of: (value: unknown) => T | undefined;
  readonly entry: string;
}

/** A4's volumes: 64 lowercase hex digits. */
const VOLUMES: ShownKind<VolumeName> = { of: volumeNameOf, entry: "an anonymous volume's name (64 lowercase hex digits)" };

/** A21's processes (S7b.2b): the daemon's canonical `pid:start`. */
const PROCESSES: ShownKind<ShownProcess> = { of: shownProcessOf, entry: 'a process as pid:start (the pid above 1)' };

/** Every shown entry validated, in order — or the position of the first that fails `kind`. */
function validNames<T extends string>(shown: readonly unknown[], kind: ShownKind<T>): readonly T[] | number {
  const names: T[] = [];
  for (const [index, value] of shown.entries()) {
    const name = kind.of(value);
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
function consistent(names: readonly string[], count: number, capped: boolean, cap: number): boolean {
  return names.length === Math.min(count, cap) && capped === count > cap;
}

function selectionOf<T extends string>(names: readonly T[], count: number | undefined, truncated: unknown, cap: number): ShownSelection<T> | RootFailure {
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

/** An action's shown list of a preview: absent (the action then cannot be confirmed), held, or refused. */
function shownSelection<T extends string>(action: JsonObject | undefined, cap: number, kind: ShownKind<T>): ShownSelection<T> | undefined | RootFailure {
  const shown = shownList(action, cap);
  if (shown === undefined || !Array.isArray(shown)) {
    return shown as RootFailure | undefined;
  }
  const names = validNames(shown, kind);
  if (typeof names === 'number') {
    return invalid(`entry ${names + 1} of its shown list is not ${kind.entry}`);
  }

  return distinctSelection(names, action as JsonObject, cap);
}

/** A shown list naming one entry twice is refused — the confirm would bind a set the preview never showed. */
function distinctSelection<T extends string>(names: readonly T[], action: JsonObject, cap: number): ShownSelection<T> | RootFailure {
  return new Set(names).size === names.length ? selectionOf(names, countOf(action), action.shownTruncated, cap) : invalid('its shown list names an entry twice');
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

/**
 * A21's processes cut to what one command line carries (S7b.2b): the daemon's invariant is checked against ITS cap first; a held
 * list past `max` keeps its first `max`, the daemon's count, and says it was cut — the modal names how many stay for the timer.
 */
function cutTo<T extends string>(selection: ShownSelection<T> | undefined | RootFailure, max: number): ShownSelection<T> | undefined | RootFailure {
  return selection === undefined || 'kind' in selection || selection.names.length <= max ? selection : { names: selection.names.slice(0, max), count: selection.count, truncated: true, cap: max };
}

/** `id`'s selection when the preview asked for it, else nothing. */
function selectedOf<T extends string>(body: JsonObject, context: PreviewContext, id: ActionId, kind: ShownKind<T>): ShownSelection<T> | undefined | RootFailure {
  return context.ids.includes(id) ? shownSelection(actionEntry(body.actions, id), shownCap(context.maxShownNames ?? MAX_SHOWN_VOLUMES), kind) : undefined;
}

// ---- E10.S2b: A20's shown entries — the SAME shown-list rule, over entry ids ----

/** A20's entries: an archived entry's id (`ArchiveIndex.IsEntryId`). */
const ENTRIES: ShownKind<EntryId> = { of: entryIdOf, entry: "an archived entry's id (16 lowercase hex digits)" };

function invalidRestore(reason: string): RootFailure {
  return { kind: 'restoreListInvalid', reason };
}

/** A20's selection when the preview asked for A20 (the restore preview), else nothing — read by `selectedOf`, as A4's and A21's are. */
function restoreOf(body: JsonObject, context: PreviewContext): RestoreSelection | undefined | RootFailure {
  return context.ids.includes('A20') ? asRestore(selectedOf(body, context, 'A20', ENTRIES)) : undefined;
}

/** An A20 preview always carries its shown list (none is no restore at all), and a list it cannot hold is told as the restore's. */
function asRestore(read: RestoreSelection | undefined | RootFailure): RestoreSelection | RootFailure {
  if (read === undefined) {
    return invalidRestore('its preview carried no shown list');
  }

  return 'kind' in read && read.kind === 'shownListInvalid' ? invalidRestore(read.reason) : read;
}

/** `act <ids> --preview --json` (exit 0), held — or why it cannot be: A4's volumes, (S7b.2b) A21's processes and (E10.S2b) A20's entries each held. */
export function parsePreview(stdout: string, context: PreviewContext): HeldPreview | RootFailure {
  const checked = checkedBody(stdout);
  if ('kind' in checked) {
    return checked;
  }
  const a4 = selectedOf(checked.body, context, 'A4', VOLUMES);
  const processes = cutTo(selectedOf(checked.body, context, 'A21', PROCESSES), MAX_PROCESS_ARGS);
  const restore = restoreOf(checked.body, context);
  const refused = [a4, processes, restore].find((s): s is RootFailure => s !== undefined && 'kind' in s);

  return refused ?? heldOf(checked, { a4: a4 as ShownSelection | undefined, processes: processes as ShownSelection<ShownProcess> | undefined, restore: restore as RestoreSelection | undefined }, context);
}

/** The lists a held preview carries, each validated. */
interface HeldLists {
  readonly a4: ShownSelection | undefined;
  readonly processes: ShownSelection<ShownProcess> | undefined;
  readonly restore: RestoreSelection | undefined;
}

function heldOf({ body }: { body: JsonObject }, lists: HeldLists, context: PreviewContext): HeldPreview {
  const actions = Array.isArray(body.actions) ? body.actions.flatMap(previewedAction) : [];
  const { a4, processes, restore } = lists;

  return { distro: context.distro, ids: context.ids, actions, a4, ...(processes === undefined ? {} : { processes }), ...(restore === undefined ? {} : { restore }), takenAtMs: context.takenAtMs, productVersion: optionalString(body.productVersion) };
}
