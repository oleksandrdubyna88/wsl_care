import { MAX_SHOWN_VOLUMES, type ActionId } from '../root/rootIds';
import type { HeldPreview, PreviewedAction } from '../root/rootOutcome';
import { safeText } from '../text/safeText';

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

function gb(bytes: number): string {
  return `${(bytes / 1e9).toFixed(1)} GB`;
}

function figures(action: PreviewedAction): string {
  if (!action.available) {
    return `not read — ${safeText(action.reason, TEXT_MAX)}`;
  }

  return `${action.count ?? '?'} objects, ${action.bytes === undefined ? '? GB' : gb(action.bytes)}`;
}

/** How this action's targets are chosen: A4 bound to the list shown, the re-selecting ones re-checked, the rest as previewed. */
function binding(action: PreviewedAction, preview: HeldPreview): string[] {
  if (action.id === 'A4' && preview.a4 !== undefined) {
    return a4Binding(preview.a4.names.length, preview.a4.count, preview.a4.truncated);
  }

  return RECHECKED.has(action.id) ? ['Its targets are re-checked at run time: what is still eligible then is removed, which can differ from these numbers.'] : [];
}

function a4Binding(shown: number, count: number, truncated: boolean): string[] {
  const bound = `Removes exactly the ${shown} volumes this preview listed — bound to that list; a volume attached since is skipped.`;

  return truncated ? [bound, `Only the ${MAX_SHOWN_VOLUMES} volumes shown are removed; ${count} were selected.`] : [bound];
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

/** The modal every cleanup shows: what each action removes, how much, and how its targets are chosen. */
export function firstModal(preview: HeldPreview, selected: boolean): Modal {
  const distro = safeText(preview.distro, 64);
  const message = selected ? `Clean the ${preview.ids.length} selected rows in "${distro}"?` : `Clean ${preview.ids[0]} in "${distro}"?`;
  const blocks = preview.actions.map((action) => block(action, preview));

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
