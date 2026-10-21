import type { StatusAnswer, VerbOutcome } from '../client/outcome';
import { at } from '../panel/jsonPath';
import { gib, percent } from '../text/format';
import { safeText } from '../text/safeText';

/**
 * The status bar's VOLUME mark (E19.S1; PLAN_disk_hygiene_windows.md §3.2): when the verdict with the EXACT id `disk.volumeFree` is
 * `critical`, the bar says which volume is nearly full — ` · D: low space` — and the tooltip's first line gives that volume's free
 * space, its free share and how old the reading is. A `warn` adds nothing (the panel shows it); no other `disk.*` verdict marks the bar.
 * Read from the RAW body: the typed `Verdict` drops `basis`, and `disk.lettered` is the daemon's own per-volume list, so no text is parsed.
 * Pure.
 */

/** Same shape as the GPU host's mark: appended to the bar's text, lines first in the tooltip, never LOWERING the bar's level. */
export interface VolumeBarMark {
  readonly text: string;
  readonly level: 'none' | 'warn' | 'critical';
  readonly lines: readonly string[];
}

/** One entry of `status.disk.lettered` as the daemon writes it: a figure that was not read is ABSENT (never 0). */
export interface LetteredVolume {
  readonly letter: string;
  readonly sizeBytes: number | undefined;
  readonly freeBytes: number | undefined;
  readonly freePercent: number | undefined;
  readonly level: string;
  readonly judgedBy: string;
}

export const NO_VOLUME_MARK: VolumeBarMark = { text: '', level: 'none', lines: [] };

const VERDICT = 'verdicts[id=disk.volumeFree]';
const LETTER = /^[A-Z]$/;
const LEVELS: readonly string[] = ['ok', 'warn', 'critical', 'unknown'];
const NOT_JUDGED = 'not judged';
const VALUE_MAX = 200;

function isNumber(value: unknown): value is number {
  return typeof value === 'number' && Number.isFinite(value);
}

function numberOrUndefined(value: unknown): number | undefined {
  return isNumber(value) ? value : undefined;
}

function isLetter(value: unknown): value is string {
  return typeof value === 'string' && LETTER.test(value);
}

function isLevel(value: unknown): value is string {
  return typeof value === 'string' && LEVELS.includes(value);
}

/** A listed volume exactly as the contract says (a capital letter, a known level, a rule text), or nothing for an entry that is not. */
function lettered(entry: unknown): LetteredVolume[] {
  const letter = at(entry, 'letter');
  const level = at(entry, 'level');
  const judgedBy = at(entry, 'judgedBy');

  return isLetter(letter) && isLevel(level) && typeof judgedBy === 'string'
    ? [{ letter, sizeBytes: numberOrUndefined(at(entry, 'sizeBytes')), freeBytes: numberOrUndefined(at(entry, 'freeBytes')), freePercent: numberOrUndefined(at(entry, 'freePercent')), level, judgedBy }]
    : [];
}

/** Every well-formed entry of `disk.lettered` of a `status` body — the extension's one reader of the daemon's per-volume list. */
export function readLettered(body: unknown): readonly LetteredVolume[] {
  const list = at(body, 'disk.lettered');

  return Array.isArray(list) ? list.flatMap(lettered) : [];
}

/** A volume that took part in the level, came out critical and has both figures the tooltip quotes — one that was not judged is never named. */
interface CriticalVolume extends LetteredVolume {
  readonly freeBytes: number;
  readonly freePercent: number;
}

function isNameable(volume: LetteredVolume): volume is CriticalVolume {
  return volume.level === 'critical' && !volume.judgedBy.startsWith(NOT_JUDGED) && volume.freeBytes !== undefined && volume.freePercent !== undefined;
}

/** The free share, exact when both byte figures are there — the wire percent is rounded to one decimal and ties where the volumes do not. */
function shareOf(volume: CriticalVolume): number {
  return volume.sizeBytes !== undefined && volume.sizeBytes > 0 ? (volume.freeBytes / volume.sizeBytes) * 100 : volume.freePercent;
}

/** The volume with the smallest free share — the one the verdict's value names first; equal shares go by letter. */
function worstOf(volumes: readonly CriticalVolume[]): CriticalVolume | undefined {
  return [...volumes].sort((a, b) => shareOf(a) - shareOf(b) || a.letter.localeCompare(b.letter))[0];
}

/** `3 h 12 min`, `12 min`, `under a minute`, `2 d 5 h` — a reading's age in the words of the tooltip. */
function elapsed(seconds: number): string {
  const minutes = Math.floor(seconds / 60);
  if (minutes < 1) {
    return 'under a minute';
  }
  if (minutes < 60) {
    return `${minutes} min`;
  }
  const hours = Math.floor(minutes / 60);

  return minutes < 48 * 60 ? `${hours} h ${minutes % 60} min` : `${Math.floor(hours / 24)} d ${hours % 24} h`;
}

/** `3 h 12 min ago by the full run` — the verdict's own basis; an age the daemon did not give is said so, never guessed. */
function readWhen(verdict: unknown): string {
  const seconds = at(verdict, 'basis.ageSeconds');
  const by = at(verdict, 'basis.source') === 'sample' ? 'the latest sample' : 'the full run';

  return isNumber(seconds) && seconds >= 0 ? `${elapsed(seconds)} ago by ${by}` : `at an unknown time by ${by}`;
}

function markOf(volume: CriticalVolume, when: string): VolumeBarMark {
  return {
    text: ` · ${volume.letter}: low space`,
    level: 'critical',
    lines: [`${volume.letter}: ${gib(volume.freeBytes)} free (${percent(volume.freePercent)}), read ${when}`],
  };
}

/** The verdict is critical but `disk.lettered` names no volume (a daemon that wrote the verdict only): the bar still warns, with no letter invented. */
function unlistedMark(verdict: unknown, when: string): VolumeBarMark {
  const value = at(verdict, 'value');

  return {
    text: ' · low space',
    level: 'critical',
    lines: [`disk.volumeFree: critical — ${typeof value === 'string' ? safeText(value, VALUE_MAX) : 'no figure'} (read ${when})`],
  };
}

export function volumeMarkOf(answer: StatusAnswer): VolumeBarMark {
  const verdict = at(answer.body, VERDICT);
  if (at(verdict, 'level') !== 'critical') {
    return NO_VOLUME_MARK;
  }
  const worst = worstOf(readLettered(answer.body).filter(isNameable));

  return worst === undefined ? unlistedMark(verdict, readWhen(verdict)) : markOf(worst, readWhen(verdict));
}

/** The mark of the newest `status` outcome — none while there is no answer, or when the verb failed. */
export function volumeMarkOfOutcome(status: VerbOutcome | undefined): VolumeBarMark {
  return status?.kind === 'answered' && status.answer.verb === 'status' ? volumeMarkOf(status.answer) : NO_VOLUME_MARK;
}
