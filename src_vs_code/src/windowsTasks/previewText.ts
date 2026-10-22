import type { TempPreviewAnswer, TempPreviewGroup } from '../client/windowsTasks';
import { noticeText, safeText } from '../text/safeText';

/**
 * The text of *Preview the Windows TEMP cleanup* (WR-T3 of PLAN_windows_runner.md §3.12): the extension's OWN rendering of the daemon's typed report
 * (`tasks preview cleanup --json`), shown in a read-only tab. It mirrors the order of the daemon's text form but is not it, because every line must pass
 * THIS extension's sanitiser — a path in the temp folder is chosen by whoever made it, and the daemon's own text form also spells a settings command a
 * bundle literal may not — and because the report is never trusted for its bounds: the renderer caps the paths and the groups AGAIN.
 *
 * <p>It claims only what the daemon measured: sizes are LOGICAL bytes (an estimate of the files' sizes), a figure is a lower bound only when the
 * daemon says its scan ran out of budget, and the closing line says what a preview does not do. Pure: a report in, text out.</p>
 *
 * Words this module may not spell, because the shipped bundle is scanned for them: the daemon's settings verb, the daemon's act verb and the flags
 * `--confirm` and `--timer`. The command that widens the allow-list is the README's.
 */

/** The most paths one group lists, and the most groups one verdict lists — whatever the report claims. */
export const MAX_PATHS_SHOWN = 100;
export const MAX_GROUPS_SHOWN = 100;
/** The most entries of any other list a line or a block carries (the allow-list, the configuration errors, the excluded reasons). */
export const MAX_LIST_SHOWN = 100;

/** A path is clipped at a bound longer than Windows' own `MAX_PATH` (260); the clip says so with an ellipsis. */
const PATH_MAX = 320;
/** Every other word of the daemon's is clipped here. */
const WORD_MAX = 200;
const INDENT = '    ';

const path = (text: string): string => safeText(text, PATH_MAX);
const word = (text: string): string => safeText(text, WORD_MAX);

function orWords(text: string, none: string): string {
  return text === '' ? none : word(text);
}

function countsLine(report: TempPreviewAnswer): string {
  const c = report.counts;

  return `eligible ${c.eligible}, would be eligible if allowed ${c.wouldBeEligible}, young ${c.young}, excluded ${c.excluded}, unknown ${c.unknown}, changed ${c.changed}, not allowed ${c.notAllowed}, deferred ${c.deferred}; ${report.eligibleBytes} logical bytes (an estimate of the files' sizes, not a measure of disk space)`;
}

function dryLine(report: TempPreviewAnswer): string {
  const until = report.dry.until === undefined ? '' : `; until ${word(report.dry.until)}`;

  return `dry run (A23's own week): ${report.dry.dryRun ? 'yes' : 'no'} — ${word(report.dry.reason)}${until}`;
}

function passLine(report: TempPreviewAnswer): string {
  const pass = report.pass;
  const interrupted = pass.inProgress === '' ? '' : `; an interrupted deletion of ${path(pass.inProgress)} is taken first`;

  return `pass: started ${orWords(pass.started, '(not yet)')}, ${pass.decided} of ${pass.total} names decided, ${pass.passesCompleted} completed; the next run starts after ${orWords(pass.last, '(the first name)')}${interrupted}`;
}

/** Words joined at most {@link MAX_LIST_SHOWN} to a line, the rest counted: a report that lies about its bounds cannot make one line a megabyte. */
function capped(items: readonly string[], separator: string): string {
  const shown = items.slice(0, MAX_LIST_SHOWN).map(word);
  const rest = items.length - shown.length;

  return (rest > 0 ? [...shown, `... and ${rest} more`] : shown).join(separator);
}

/** The lines that say the run would be refused: a configuration in error, a root the daemon will not clean. */
function refusalLines(report: TempPreviewAnswer): string[] {
  const broken = report.observeOnly ? [`the configuration is in error (${capped(report.configurationErrors, '; ')}): a run would be refused until it is fixed`] : [];

  return report.refusal === '' ? broken : [...broken, `refused: ${word(report.refusal)}`];
}

/** The reasons things are never cleaned, one line each up to the cap, the rest counted. */
function excludedLines(report: TempPreviewAnswer): string[] {
  const shown = report.excludedByReason.slice(0, MAX_LIST_SHOWN).map((e) => `excluded ${e.count}: ${word(e.reason)}`);
  const rest = report.excludedByReason.length - shown.length;

  return rest > 0 ? [...shown, `... and ${rest} more excluded reasons not shown`] : shown;
}

function budgetLines(report: TempPreviewAnswer): string[] {
  const globs = report.allowGlobs.length === 0 ? '(empty)' : capped(report.allowGlobs, ', ');

  return [
    `allow-list (temp.allowGlobs): ${globs}`,
    `scan budget: ${report.scanMaxNodes} nodes, ${report.scanMaxSeconds} s; at most ${report.maxItemsPerRun} entries per run`,
  ];
}

/** One group: its heading, the paths up to the cap (indented), and how many more there are — the daemon's own `more` and the paths this renderer left out. */
function groupLines(group: TempPreviewGroup): string[] {
  const shown = group.paths.slice(0, MAX_PATHS_SHOWN);
  const rest = group.more + (group.paths.length - shown.length);

  return [
    `${group.verdict} ${word(group.pattern)}: ${group.count} (${group.bytes} logical bytes), showing ${shown.length}:`,
    ...shown.map((p) => `${INDENT}${path(p)}`),
    ...(rest > 0 ? [`${INDENT}... and ${rest} more`] : []),
  ];
}

/** The groups of one verdict, capped, then how many groups are not shown (the renderer's own cut and the daemon's). */
function verdictLines(report: TempPreviewAnswer, verdict: TempPreviewGroup['verdict']): string[] {
  const groups = report.groups.filter((g) => g.verdict === verdict);
  const notShown = groups.length - Math.min(groups.length, MAX_GROUPS_SHOWN) + report.groupsNotShown[verdict];
  const lines = groups.slice(0, MAX_GROUPS_SHOWN).flatMap(groupLines);

  return notShown > 0 ? [...lines, `... and ${notShown} more group(s) not shown`] : lines;
}

function wouldBeLines(report: TempPreviewAnswer): string[] {
  const lines = verdictLines(report, 'wouldBeEligible');

  return lines.length === 0 ? lines : ["would be eligible if allowed — widen temp.allowGlobs with the daemon's settings command (see the README):", ...lines];
}

const LOWER_BOUND = 'the scan of the names the allow-list does not cover ran out of the scan budget (temp.scanMaxNodes / temp.scanMaxSeconds): the would-be-eligible figures are a LOWER BOUND';

const CLOSING = 'A preview deletes nothing, writes nothing and moves no cursor. The task deletes only while the dry line says it acts.';

/** The preview as one block of text for the read-only tab, and the one-line headline the notification says. */
export function previewText(report: TempPreviewAnswer): { readonly headline: string; readonly text: string } {
  const counts = countsLine(report);
  const lines = [
    `Windows TEMP cleanup preview — ${path(report.root)}; cutoff ${word(report.cutoffUtc)} (temp.olderThanDays ${report.olderThanDays})`,
    counts,
    dryLine(report),
    passLine(report),
    ...refusalLines(report),
    ...budgetLines(report),
    ...verdictLines(report, 'eligible'),
    ...wouldBeLines(report),
    ...(report.wouldBeEligibleLimited ? [LOWER_BOUND] : []),
    ...excludedLines(report),
    CLOSING,
  ];

  return { headline: noticeText(`${counts} — opened in a read-only tab`), text: lines.join('\n') };
}
