import { isDistroName } from './distros';

/**
 * The typed distro (E18.W1a, todo/PLAN_disk_health_E18.md §3.1): WHICH distribution a call is about.
 *
 * Every public road into `WslCareClient` takes a `DistroTarget` — required, never optional — so a call site that forgot the
 * distro does not compile, and "the setting at the call site" cannot happen: the call site holds a token, not a text. The token is
 * either the DEFAULT (the `wslCare.distro` setting, resolved inside the client as it always was) or a PICKED name that only
 * `wsl.exe`'s own listing can mint.
 *
 * It lives under `wsl/` so that `state/`, `poll/`, `panel/`, `install/` and the pages may import it: none of them may import
 * `root/` or `config/` (`structure.test.ts`), and it imports nothing from the client, `root/` or `config/` itself.
 */

/** A name `wsl.exe` LISTED — minted only by {@link listedNameOf} (and, for a persisted name, {@link persistedNameOf}): never a raw string across a boundary. */
export type DistroName = string & { readonly __brand: 'DistroName' };

/** Which distro a call is about: the `wslCare.distro` setting (resolved inside the client, as today), or a picked, listed name. */
export type DistroTarget = { readonly kind: 'default' } | { readonly kind: 'picked'; readonly distro: DistroName };

export const DEFAULT_TARGET: DistroTarget = { kind: 'default' };

export function pickedTarget(distro: DistroName): DistroTarget {
  return { kind: 'picked', distro };
}

/**
 * `text` as a `DistroName` when it is one of `listed` (exact, case-sensitive) and does not start with "-" — the rule of the client's
 * `checkListed`: argv reaches `wsl.exe` without a shell, so the listing is the authority, and a leading dash after `-d` would be read
 * as an option. `undefined` otherwise.
 */
export function listedNameOf(text: string, listed: readonly string[]): DistroName | undefined {
  return listed.includes(text) && !text.startsWith('-') ? (text as DistroName) : undefined;
}

/**
 * A name persisted by the journal, re-minted at reload by its SHAPE alone (§3.5): no list exists at reload time, so this is a compile-time
 * promise only — the runtime authority stays the client's listed check on the next call, which answers `distroRefused` for a name WSL no
 * longer lists. The setting's own pattern (`DISTRO_NAME`: letters, digits, `.` `_` `-`, at most 64, never a leading `-`) — every name `listDistros` can
 * mint already satisfies it, so a journal name with a space, a quote, a newline or `~` is not one.
 */
export function persistedNameOf(text: string): DistroName | undefined {
  return isDistroName(text) ? (text as DistroName) : undefined;
}

/**
 * The in-flight key's prefix: `default|<setting text>` or `picked|<name>`. The default's identity stays the setting TEXT, so a setting
 * change still makes a new key (#23); a picked call's is its name, so a default call and a picked call for the same distro never share.
 */
export function targetKey(target: DistroTarget, settingText: string): string {
  return target.kind === 'picked' ? `picked|${target.distro}` : `default|${settingText}`;
}

/** One distribution `wsl.exe` reports (`listDistros`). */
export interface DistroInfo {
  readonly name: DistroName;
  /** From `--list --running --quiet` (unlocalised), never from the state word; `undefined` when that question FAILED ("state not known", never "stopped"). */
  readonly running: boolean | undefined;
  /** The version column's digit; `undefined` when it is not 1 or 2. */
  readonly version: 1 | 2 | undefined;
  readonly isDefault: boolean;
  /** The state column as `wsl.exe` printed it (localised — shown, never judged). */
  readonly stateText: string;
}

export interface DistroList {
  readonly distros: readonly DistroInfo[];
  /** The distribution the single `*` row marks; `undefined` when none or more than one does. */
  readonly default: DistroName | undefined;
}
