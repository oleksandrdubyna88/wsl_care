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

/** `wsl.exe --list --quiet`, decoded: one name per line. */
export function parseQuietList(text: string): string[] {
  return textLines(text).map((line) => line.trim());
}

/**
 * The default distribution of `wsl.exe -l -v`: the ONE row that starts with `*`. The header and the state words are
 * localised ("NAME / STATE / Running" here), the marker is not — so only the marker is read. No marked row, or more
 * than one, is no default.
 */
export function parseDefaultDistro(verbose: string): string | undefined {
  const marked = textLines(verbose).flatMap((line) => {
    const found = /^\*\s+(\S+)/.exec(line);
    return found?.[1] === undefined ? [] : [found[1]];
  });

  return marked.length === 1 ? marked[0] : undefined;
}
