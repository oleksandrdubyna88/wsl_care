import { ENTRY_ID_SHAPE, MAX_RESTORE_ENTRIES, RUN_ID_SHAPE } from '../shared/shapes';

export { MAX_RESTORE_ENTRIES };

/**
 * The typed values a root call may carry (E6.S2, plan §15f #2, §15j B1 / M1): each arrives as untrusted text — from a
 * daemon answer, from host state, later from a webview message's index — and becomes argv or a stdin line ONLY through
 * one of the validators below. The types are branded, so the closed `ROOT_OPS` union of `rootCall.ts` cannot be handed a
 * raw string by mistake; the validators are the one road in (`common.security`: one road, not a check at each site).
 */

/**
 * The COMPILED action registry — the ids this extension version knows how to offer. Acting is limited to this list ∩
 * the ids the daemon reports in `status.actions` (plan §15f #2, §15j M5). `rootIds.test.ts` holds it EQUAL to
 * `contracts/actions.json`, which the daemon writes from `ActionId.All`: an id the daemon adds is not acted on until this
 * list knows it, and the test says so on the day the contract changes.
 */
export const ACTION_IDS = [
  'A1', 'A2', 'A3', 'A4', 'A5', 'A5Testcontainers', 'A6', 'A6Unused', 'A7', 'A8', 'A9', 'A10', 'A11', 'A12', 'A13', 'A14', 'A15', 'A16', 'A17', 'A18', 'A19', 'A20', 'A21', 'A23',
] as const;

export type ActionId = (typeof ACTION_IDS)[number];

/**
 * Ids the registry knows but the E6 cleanup ops never act on (daemon #17): A18 ends orphaned agent processes, A19 stops
 * idle MCP servers (daemon E14 S2a — the timer may run it, but a button run of it takes `--process <pid:start>`, as A18's) and
 * A20 restores archived AI sessions (daemon E9.S4 — a button only; the extension pipes its entry ids on stdin, `--entry -`, E10.S0) —
 * arguments no E6 op carries. The gate leaves them out of what the cleanup ops allow, so a status reporting them changes nothing
 * there. A20 is acted on ONLY through the two restore ops of `rootCall.ts` (E10.S2b), which take `EntryIds`, and only when the
 * daemon reports it (`GateOpen.reported`). A21 (daemon E14 S7b.2, the client-gone relays) left this list with its button
 * (S7b.2b): its confirm carries the held `--process` list, and as no row id it can never join *Clean selected*. A23 (daemon WR-T1, the
 * Windows `%TEMP%` cleanup) runs on the Windows side only and is acted on by the Windows runner's own panel (WR-T3), never by a WSL
 * cleanup op - so the gate leaves it out of what those allow.
 */
export const BUTTON_ONLY_IDS: readonly ActionId[] = ['A18', 'A19', 'A20', 'A23'];

/** One or more registry ids — never an empty `act`. */
export type ActionIds = readonly [ActionId, ...ActionId[]];

/** A run id as the daemon spells it (`RunId.New`): `yyyyMMddTHHmmssZ-<pid>`. */
export type RunId = string & { readonly __brand: 'RunId' };

/** A process the daemon's preview SHOWED, as `pid:start` (S7b.2b) — what A21's `--process` arguments hold. */
export type ShownProcess = string & { readonly __brand: 'ShownProcess' };

/** An anonymous Docker volume's full name — what A4's shown list holds. */
export type VolumeName = string & { readonly __brand: 'VolumeName' };

/**
 * The most names A4's preview shows and a confirm may pipe back — the daemon's `ShownList.MaxNames` (plan §15k #11). A full
 * list is 650 000 bytes on stdin; that it relays through `wsl.exe` byte for byte, with its end, was measured 2026-10-04
 * (research/2026-10-03_wsl_exe_facts.md row 20; re-measured with `-u root` at the E6 live gate).
 */
export const MAX_SHOWN_VOLUMES = 10_000;

/**
 * The most `--process <pid:start>` pairs one confirm carries (S7b.2b): about 26 characters each, so 500 are about 13 KB — well
 * under Windows' 32 767-character command line, which the daemon's 10 000-name stdin cap would overrun. A list the daemon showed
 * longer is cut to this, and the modal says so; the timer takes the rest.
 */
export const MAX_PROCESS_ARGS = 500;

/**
 * A4's shown-list cap IN FORCE (daemon #17): the daemon's published `act.maxShownNames` (`status.limits.maxShownNames`),
 * never above the compiled `MAX_SHOWN_VOLUMES` — the bound the stdin relay and the shape checks were sized for.
 */
export function shownCap(maxShownNames: number): number {
  return Math.min(maxShownNames, MAX_SHOWN_VOLUMES);
}

/**
 * The daemon's one spelling of a run id: the UTC stamp, a dash, the pid with NO leading zero (`RunId.TryParse`, E6.S0
 * review S4 — two spellings would name one run twice). Stricter than the daemon in one place: pid 0 is refused, because no
 * run has it.
 */
const RUN_ID = RUN_ID_SHAPE;

/** 64 lowercase hex digits — the daemon's `DockerJson.IsFullId`, the only shape A4's `--only` lines may take. */
const VOLUME_NAME = /^[0-9a-f]{64}$/;

export function actionIdOf(value: unknown): ActionId | undefined {
  return ACTION_IDS.find((id) => id === value);
}

export function runIdOf(value: unknown): RunId | undefined {
  return typeof value === 'string' && RUN_ID.test(value) ? (value as RunId) : undefined;
}

export function volumeNameOf(value: unknown): VolumeName | undefined {
  return typeof value === 'string' && VOLUME_NAME.test(value) ? (value as VolumeName) : undefined;
}

/** `pid:start` in the daemon's canonical spelling (`SuspectSignals.Shown`): no sign, no space, no leading zero. */
const SHOWN_PROCESS = /^([1-9][0-9]{0,9}):(0|[1-9][0-9]{0,18})$/;

/** The largest pid the daemon parses (`int`) and the largest start ticks (`long`). */
const MAX_PID = 2_147_483_647;
const MAX_START = 9_223_372_036_854_775_807n;

/**
 * A process a confirm may name (S7b.2b): the daemon's `SuspectSignals.IsShownKey` — two whole numbers, the pid above 1 (never
 * init) — in the one spelling its previews write. Anything else is refused before it can become an argv word.
 */
export function shownProcessOf(value: unknown): ShownProcess | undefined {
  return typeof value === 'string' && canonical(value) ? (value as ShownProcess) : undefined;
}

function canonical(text: string): boolean {
  const match = SHOWN_PROCESS.exec(text);

  return match !== null && inRange(String(match[1]), String(match[2]));
}

/** The pid above 1 and within the daemon's `int`, the start within its `long` (2^63 − 1 is past `MAX_SAFE_INTEGER`: a BigInt). */
function inRange(pidText: string, startText: string): boolean {
  const pid = Number(pidText);

  return pid > 1 && pid <= MAX_PID && BigInt(startText) <= MAX_START;
}
/** An archived entry's id (`ArchiveIndex.IsEntryId`): what A20's stdin lines carry (E10.S2b). */
export type EntryId = string & { readonly __brand: 'EntryId' };

/** One to `MAX_RESTORE_ENTRIES` entry ids, each once — what a restore op takes. Made only by `entryIdsOf`. */
export type EntryIds = readonly [EntryId, ...EntryId[]] & { readonly __brand: 'EntryIds' };

export function entryIdOf(value: unknown): EntryId | undefined {
  return typeof value === 'string' && ENTRY_ID_SHAPE.test(value) ? (value as EntryId) : undefined;
}

/** `texts` as a restore's entry list — every one valid, none twice, 1 to `MAX_RESTORE_ENTRIES` — or nothing at all (never a subset). */
export function entryIdsOf(texts: readonly unknown[]): EntryIds | undefined {
  const ids = texts.flatMap((text) => entryIdOf(text) ?? []);
  const whole = ids.length === texts.length && new Set(ids).size === ids.length;

  return whole && fitsOneRestore(ids.length) ? (ids as unknown as EntryIds) : undefined;
}

function fitsOneRestore(count: number): boolean {
  return count >= 1 && count <= MAX_RESTORE_ENTRIES;
}
