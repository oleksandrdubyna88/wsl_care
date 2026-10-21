import { parseDefaultDistro, parseVerboseTable, type VerboseRow } from './distros';
import { listedNameOf, type DistroInfo, type DistroList, type DistroName } from './distroTarget';

/**
 * What `listDistros()` answers, built from the two things `wsl.exe` said (E18.W1a, plan §3.2): the `-l -v` table (names, the `*`, the
 * version column) and the names `--list --running --quiet` printed — or `undefined` when that second question FAILED, so that no
 * distribution is ever called "stopped" on the strength of a question that did not answer. "Running" is never read from the state
 * column: it is localised, and the running list is the unlocalised signal the client already trusts.
 *
 * Pure: every name is minted through {@link listedNameOf} against the names of the table itself, so the only road to a `DistroName`
 * is a row `wsl.exe` printed.
 */
export function distroListOf(table: string, running: readonly string[] | undefined): DistroList {
  const rows = parseVerboseTable(table);
  const names = rows.map((row) => row.name);
  const marked = parseDefaultDistro(table);
  const distros = rows.flatMap((row) => infoOf(row, names, running, marked));

  return { distros, default: distros.find((info) => info.isDefault)?.name };
}

function infoOf(row: VerboseRow, names: readonly string[], running: readonly string[] | undefined, marked: string | undefined): DistroInfo[] {
  const name = listedNameOf(row.name, names);

  return name === undefined ? [] : [{ name, running: running?.includes(name), version: row.version, isDefault: name === marked, stateText: row.stateText }];
}

/** The names of a list, as the plain texts the per-distro maps are keyed by. */
export function namesOf(list: DistroList): ReadonlySet<DistroName> {
  return new Set(list.distros.map((info) => info.name));
}
