/**
 * E10.S1: the ONE rule for a folder value the extension ever puts in argv — the folder asked about by `archive check-base <path>`
 * and the folder `config set archive.baseFolder <folder>` writes. It is the daemon's own rule
 * (`ArchiveArguments.IsPathArgument`: not empty, not starting with `-`, no control character) plus the key's limit, so a value
 * the daemon would refuse is refused here, before anything starts.
 */

/** The longest folder `archive.baseFolder` takes (its rule's limit in `contracts/config-keys.json`). */
export const MAX_BASE_FOLDER_CHARS = 1024;

/** Why `path` may not be sent as a folder value, or `undefined` when it may. Pure. */
export function basePathRefusal(path: string): string | undefined {
  return lengthRefusal(path) ?? shapeRefusal(path);
}

function lengthRefusal(path: string): string | undefined {
  return path.length === 0 || path.length > MAX_BASE_FOLDER_CHARS ? `a folder of 1 to ${MAX_BASE_FOLDER_CHARS} characters is asked about` : undefined;
}

function shapeRefusal(path: string): string | undefined {
  return path.startsWith('-') || [...path].some(isControl) ? 'a folder that starts with "-" or holds a control character is never sent' : undefined;
}

function isControl(c: string): boolean {
  const code = c.charCodeAt(0);

  return code < 32 || code === 127;
}
