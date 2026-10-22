import { textLines } from './wslText';

/**
 * The distributions `wsl.exe` reports, and the shape the `wslCare.distro` SETTING must have before anything is started
 * (plan §15f #2, §15g M3). A name `wsl.exe` itself LISTS is taken as it is (§15h #4, `WslCareClient`): argv reaches
 * `wsl.exe` without a shell, so the listing is the authority — only a leading `-` is refused there.
 */

/**
 * The setting's shape: letters, digits, `.`, `_` and `-`, at most 64, starting with a letter or digit. Never starting
 * with `-` is the point — a name is placed right after `-d`, and one that began with a dash would be read by
 * `wsl.exe` as an option (`-u`). The VS Code setting's schema uses this same pattern (`manifest.test.ts`).
 */
export const DISTRO_NAME = /^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$/;

export function isDistroName(name: string): boolean {
  return DISTRO_NAME.test(name);
}

/**
 * The `wslCare.distro` setting as text, whatever settings.json holds: a string trimmed; anything else (a number,
 * `null`, an object — VS Code does not coerce a hand-edited value) a text that can never match `DISTRO_NAME`, so it is
 * refused as a value before any spawn instead of throwing (retro fix PR of PR #9, its final code round). Only the TYPE
 * is named: converting the value itself can throw (`{"toString": null}` is valid JSON).
 */
export function distroSettingText(raw: unknown): string {
  return typeof raw === 'string' ? raw.trim() : `<not a name: ${raw === null ? 'null' : typeof raw}>`;
}

/** `wsl.exe --list --quiet`, decoded: one name per line. */
export function parseQuietList(text: string): string[] {
  return textLines(text).map((line) => line.trim());
}

/** One row of `wsl.exe -l -v`, as far as the extension reads it. */
export interface VerboseRow {
  readonly name: string;
  /** The `*` marker — the one unlocalised sign of WSL's default distribution. */
  readonly marked: boolean;
  /** The state column as `wsl.exe` printed it: LOCALISED, shown and never judged ("Running" is "Wird ausgeführt" on a German machine). */
  readonly stateText: string;
  /** The version column's digit; `undefined` when it is neither 1 nor 2. */
  readonly version: 1 | 2 | undefined;
}

/** A row as the layout reads it, plus whether its NAME is wider than the name column (a name holding a space). */
interface TableRow extends VerboseRow {
  readonly spaced: boolean;
}

/**
 * A data row of `-l -v`: the marker column (`*` or blank), the name, the state (which may hold a space — "Wird ausgeführt"), and the
 * version digits to the end of the line. The header has no trailing digit, so it is no row. The `d` flag gives the state's start, which
 * is how a name holding a space is told from a two-word state.
 */
const VERBOSE_ROW = /^(\*?)\s+(\S+)\s+(.*?)\s+(\d+)\s*$/d;

/** The column the STATE starts at, read from the header line (the line IMMEDIATELY before the first row): its second word. `undefined` without a header. */
function stateColumn(lines: readonly string[]): number | undefined {
  const first = lines.findIndex((line) => VERBOSE_ROW.test(line));
  const header = first > 0 ? lines[first - 1] : undefined;
  const second = header === undefined ? null : /^\s*\S+\s+(?=\S)/.exec(header);

  return second === null ? undefined : second[0].length;
}

function versionOf(digits: string): 1 | 2 | undefined {
  return digits === '1' ? 1 : digits === '2' ? 2 : undefined;
}

/** Where the state text of a matched row starts (the `d` flag's indices). */
function stateStart(found: RegExpExecArray): number {
  return found.indices?.[3]?.[0] ?? 0;
}

function tableRow(line: string, column: number | undefined): TableRow[] {
  const found = VERBOSE_ROW.exec(line);
  if (found === null) {
    return [];
  }
  const spaced = column !== undefined && stateStart(found) < column;

  return [{ name: found[2], marked: found[1] === '*', stateText: found[3], version: versionOf(found[4]), spaced }];
}

function tableRows(text: string): TableRow[] {
  const lines = textLines(text);
  const column = stateColumn(lines);

  return lines.flatMap((line) => tableRow(line, column));
}

/**
 * Every distribution of `wsl.exe -l -v` that a `DistroName` may be minted from (E18.W1a, plan §3.2): the rows the layout parses, minus a
 * name that fails the setting's pattern (`DISTRO_NAME` — a leading `-`, a space as a side-loaded distribution may carry) and a name
 * holding a space, which the header's state column reveals. The header and the state words are localised and are not judged.
 */
export function parseVerboseTable(text: string): VerboseRow[] {
  return tableRows(text)
    .filter((row) => !row.spaced && isDistroName(row.name))
    .map(({ name, marked, stateText, version }) => ({ name, marked, stateText, version }));
}

/**
 * The default distribution of `wsl.exe -l -v`: the ONE row that starts with `*`. The header and the state words are
 * localised ("NAME / STATE / Running" here), the marker is not — so only the marker is read. No marked row, or more
 * than one, is no default. Read over the same table parser, but WITHOUT the name filter: the default road refuses a name
 * the pattern or the option rule rejects itself, by name (`checkListed`), where the picker simply never offers it.
 */
export function defaultRow(verbose: string): VerboseRow | undefined {
  const marked = tableRows(verbose).filter((row) => row.marked);
  const only = marked.length === 1 ? marked[0] : undefined;

  return only === undefined ? undefined : { name: only.name, marked: only.marked, stateText: only.stateText, version: only.version };
}

export function parseDefaultDistro(verbose: string): string | undefined {
  return defaultRow(verbose)?.name;
}
