import type { ActionId } from '../root/rootIds';
import type { HeldPreview, PreviewedAction, PreviewItem } from '../root/rootOutcome';
import { gb, sizeText } from '../text/format';
import { safeText } from '../text/safeText';
import { runWordsOf } from './runWords';

/**
 * The words of the native confirmation modals (E6.S3, plan §7.3, §15j M8, m8). Every daemon string — a `what`, a reason,
 * an item's name (a container or image name is chosen by whoever made it) — goes through the one sanitiser
 * (`text/safeText.ts`: control and bidirectional-override characters shown as U+FFFD) and is cut short before VS Code's
 * modal shows it. Pure: the flow (`cleanFlow.ts`) decides when each modal is shown.
 */

export interface Modal {
  readonly message: string;
  readonly detail: string;
  /** The one button that confirms; dismissing the modal or any other answer declines. */
  readonly confirm: string;
}

export const CLEAN_LABEL = 'Clean';
export const CLEAN_ANYWAY_LABEL = 'Clean anyway';
export const ARCHIVE_LABEL = runWordsOf(['A13'])?.confirm ?? 'Archive';
export const RESTORE_LABEL = runWordsOf(['A20'])?.confirm ?? 'Restore';
export const STOP_RELAYS_CONFIRM = runWordsOf(['A21'])?.confirm ?? 'Stop relays';

/** An attacker-settable name (a container's, an image's) is cut here; a `what` or a reason a little later. */
const NAME_MAX = 80;
const TEXT_MAX = 200;

/** The actions whose targets the daemon selects again when it runs (§15f #11) — their preview is a forecast. */
const RECHECKED: ReadonlySet<ActionId> = new Set<ActionId>(['A5', 'A5Testcontainers', 'A6', 'A6Unused', 'A7']);

/**
 * The second confirmation (§7.3): actions that touch a person's own data or force large re-downloads, each with the
 * setting that decides what it takes — or, for a whole-cache action, the switch that keeps it from the timer.
 */
const SECOND: Partial<Record<ActionId, string>> = {
  A5: 'A5 removes YOUR stopped containers (not only Testcontainers) — as old as containers.stoppedOlderThanDays allows — with their anonymous volumes.',
  A6Unused: 'A6Unused removes tagged images no container uses — created at least images.unusedOlderThanDays ago; they are downloaded or built again when needed.',
  A8: 'A8 empties the whole npm cache; packages are downloaded again (the timer runs it only when auto.A8 is on).',
  A11: 'A11 ends idle processes of the configured families — idle for at least processes.idleOlderThanHours.',
  A12: 'A12 removes Playwright browsers no project references and the NuGet http-cache; they are downloaded again (the timer runs it only when auto.A12 is on).',
};

function figures(action: PreviewedAction): string {
  if (!action.available) {
    return `not read — ${safeText(action.reason, TEXT_MAX)}`;
  }

  return `${action.count ?? '?'} objects, ${action.bytes === undefined ? '? GB' : gb(action.bytes)}`;
}

/** How this action's targets are chosen: A4 bound to the list shown, the re-selecting ones re-checked, the rest as previewed. */
function binding(action: PreviewedAction, preview: HeldPreview): string[] {
  if (action.id === 'A4' && preview.a4 !== undefined) {
    return a4Binding(preview.a4.names.length, preview.a4.count, preview.a4.truncated, preview.a4.cap);
  }

  return RECHECKED.has(action.id) ? ['Its targets are re-checked at run time: what is still eligible then is removed, which can differ from these numbers.'] : [];
}

function a4Binding(shown: number, count: number, truncated: boolean, cap: number): string[] {
  const bound = `Removes exactly the ${shown} volumes this preview listed — bound to that list; a volume attached since is skipped.`;

  return truncated ? [bound, `Only the ${cap} volumes shown are removed; ${count} were selected.`] : [bound];
}

function names(action: PreviewedAction): string[] {
  const listed = action.items.map((name) => `  • ${safeText(name, NAME_MAX)}`);
  const more = (action.count ?? 0) - action.items.length;

  return more > 0 && listed.length > 0 ? [...listed, `  … and ${more} more`] : listed;
}

function block(action: PreviewedAction, preview: HeldPreview): string {
  const head = `${action.id} — ${safeText(action.what, TEXT_MAX)}: ${figures(action)}`;

  return [head, ...binding(action, preview), ...names(action)].join('\n');
}

/** One relay's line: its name (pid and program) and the head of the daemon's note, within the sanitiser's cut. */
function relayLine(item: PreviewItem): string {
  return `  • ${safeText(item.name, NAME_MAX)} — ${safeText(item.note, TEXT_MAX)}`;
}

/** What the relays' modal binds the stop to: the shown list, and — when it was cut — how many stay for the timer. */
function relayBinding(preview: HeldPreview): string[] {
  const held = preview.processes;
  if (held === undefined) {
    return [];
  }
  const bound = `Stops exactly the ${held.names.length} relays this preview showed. The daemon judges each again just before the signal: one whose client came back, or that the timer's own watch ended first, is skipped — a smaller count is not a failure.`;

  return held.truncated ? [bound, `Only the first ${held.cap} shown are stopped; ${held.count} were selected — the timer takes the rest.`] : [bound];
}

/** The listed relays (at most the daemon's 20 items), and how many more the run stops beyond them. */
function relayList(action: PreviewedAction | undefined, stopped: number): string[] {
  const listed = (action?.details ?? []).map(relayLine);
  const more = stopped - listed.length;

  return more > 0 ? [...listed, `  … all ${stopped} are stopped; the first ${listed.length} are listed`] : listed;
}

/** *Stop client-gone relays*'s modal (S7b.2b): what SIGTERM asks, what is bound, each relay, and the agent's /mcp. */
function relaysModal(preview: HeldPreview): Modal {
  const action = preview.actions.find((a) => a.id === 'A21');
  const stopped = preview.processes?.names.length ?? 0;
  const how = [
    'SIGTERM asks each relay to end, and its Windows program ends with it. A relay that does not end is reported, and its Windows program keeps running — a relay is never sent SIGKILL.',
    "An AI agent's session may need /mcp to reconnect a server it still wanted.",
    "The run is the daemon's own unit; the panel follows it to its result.",
  ];

  return { message: `Stop ${stopped} client-gone relay${stopped === 1 ? '' : 's'} in "${safeText(preview.distro, 64)}"?`, detail: [...relayBinding(preview), ...relayList(action, stopped), '', ...how].join('\n'), confirm: STOP_RELAYS_CONFIRM };
}

/** A size in the unit it reaches (a 200-byte session reads 200 B), or "an unknown size". */
function sizeOrUnknown(bytes: number | undefined): string {
  return bytes === undefined ? 'an unknown size' : sizeText(bytes);
}

/** One item's line — an agent of A13, a session of A20: its name, the daemon's note, its bytes. */
function agentLine(item: PreviewItem): string {
  return `  • ${safeText(item.name, NAME_MAX)} — ${safeText(item.note, TEXT_MAX)} · ${sizeOrUnknown(item.bytes)}`;
}

function archiveBlock(action: PreviewedAction | undefined): string[] {
  return action !== undefined && action.available ? readBlock(action) : [`Not read — ${notReadWhy(action, 'A13')}`];
}

function notReadWhy(action: PreviewedAction | undefined, id: string): string {
  return safeText(action?.reason ?? `the daemon did not describe ${id}`, TEXT_MAX);
}

/** What A13 would move, per agent, and the daemon's own reason when it gave one (a skip). */
function readBlock(action: PreviewedAction): string[] {
  const said = action.reason === '' ? [] : [`The daemon says: ${safeText(action.reason, TEXT_MAX)}`];

  return [safeText(action.what, TEXT_MAX), ...action.details.map(agentLine), ...said];
}

/** *Archive now*'s modal (E10.S1b): each agent's sessions and bytes, and how the daemon moves them. */
function archiveModal(preview: HeldPreview): Modal {
  const action = preview.actions.find((a) => a.id === 'A13');
  const how = 'The daemon moves them as the target user, with its own binary: a session leaves its agent\'s folder only after its archived copy was verified. The run is the daemon\'s own unit; the panel follows it to its result.';

  return { message: `Archive the aged AI sessions in "${safeText(preview.distro, 64)}"?`, detail: [...archiveBlock(action), '', how].join('\n'), confirm: ARCHIVE_LABEL };
}

/** The restore modal lists at most this many sessions; the rest are counted ("… and N more"). */
const RESTORE_LISTED = 10;

function sessions(n: number): string {
  return `${n} archived session${n === 1 ? '' : 's'}`;
}

/** One session's line: its entry id, the daemon's note (agent, month, files), its bytes. */
function restoreLines(action: PreviewedAction): string[] {
  const listed = action.details.slice(0, RESTORE_LISTED).map(agentLine);
  const more = (action.count ?? action.details.length) - listed.length;

  return more > 0 ? [...listed, `  … and ${more} more`] : listed;
}

/** How many sessions the held preview offers — the ids its confirm pipes. */
function restoreCount(preview: HeldPreview): number {
  return preview.restore?.names.length ?? 0;
}

/** What A20 would put back: the daemon's sentence, how many and how large, and the first sessions. */
function restoreBlock(action: PreviewedAction, count: number): string[] {
  return [safeText(action.what, TEXT_MAX), `${sessions(count)}, ${sizeOrUnknown(action.bytes)}`, ...restoreLines(action)];
}

/**
 * *Restore*'s modal (E10.S2b): how many sessions the HELD preview offers — the ids its confirm pipes, never the page's selection —
 * what the daemon would put back, and how. A preview that offers none still opens nothing: the flow tells it instead (`cleanFlow.ts`).
 */
function restoreModal(preview: HeldPreview): Modal {
  const action = preview.actions.find((a) => a.id === 'A20');
  const count = restoreCount(preview);
  const what = action !== undefined && action.available ? restoreBlock(action, count) : [`Not read — ${notReadWhy(action, 'A20')}`];
  const how = 'The daemon copies each session back to its agent\'s folder as the target user, with its own binary; files are only created, never replaced — a session already back is skipped, and one whose file is there with other bytes is refused. The run is the daemon\'s own unit; the panel follows it to its result.';

  return { message: `Restore ${sessions(count)} in "${safeText(preview.distro, 64)}"?`, detail: [...what, '', how].join('\n'), confirm: RESTORE_LABEL };
}

/** The single-action modals, by their run's id (`runWords.ts`): *Archive now*, *Restore* and the relay stop are never "Clean A13" / "Clean A20" / "Clean A21". */
const SINGLE: { readonly [K in 'A13' | 'A20' | 'A21']: (preview: HeldPreview) => Modal } = { A13: archiveModal, A20: restoreModal, A21: relaysModal };

/** The modal every cleanup shows: what each action removes, how much, and how its targets are chosen. */
export function firstModal(preview: HeldPreview, selected: boolean): Modal {
  const words = runWordsOf(preview.ids);

  return words === undefined ? cleanModal(preview, selected) : SINGLE[words.id](preview);
}

function cleanModal(preview: HeldPreview, selected: boolean): Modal {
  const distro = safeText(preview.distro, 64);
  const message = selected ? `Clean the ${preview.ids.length} selected rows in "${distro}"?` : `Clean ${preview.ids[0]} in "${distro}"?`;
  const blocks = preview.ids.flatMap((id) => preview.actions.filter((a) => a.id === id).slice(0, 1)).map((action) => block(action, preview));

  return { message, detail: [...blocks, 'The cleanup runs in the daemon\'s own unit; the panel follows it to its result.'].join('\n\n'), confirm: CLEAN_LABEL };
}

/** The second confirmation (§7.3) for the actions that touch a person's data or force re-downloads — or none. */
export function secondModal(ids: readonly ActionId[]): Modal | undefined {
  const lines = ids.flatMap((id) => SECOND[id] ?? []);

  return lines.length === 0 ? undefined : { message: 'This cleanup also touches your own data or forces downloads. Clean anyway?', detail: lines.join('\n\n'), confirm: CLEAN_ANYWAY_LABEL };
}

/** The stop's modal (§15j M4): a wedged run the daemon can stop — its unit is stopped, the run records itself interrupted. */
export function stopModal(runId: string): Modal {
  return {
    message: `Stop run ${runId}?`,
    detail: "The daemon stops the wedged run's unit (systemd sends it SIGTERM). The run records itself interrupted; what it already removed stays removed.",
    confirm: 'Stop',
  };
}

/**
 * The ids a confirm would act on that the preview does NOT describe (review A3): the modal is built from the confirmed ids,
 * so an id without a previewed entry would be confirmed unseen — the flow refuses instead.
 */
export function missingFromPreview(preview: HeldPreview): readonly ActionId[] {
  return preview.ids.filter((id) => !preview.actions.some((a) => a.id === id));
}
