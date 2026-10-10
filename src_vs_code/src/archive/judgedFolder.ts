import type { JsonObject } from '../client/outcome';
import { basePathRefusal } from '../shared/basePath';

/**
 * E10.S1 (plan §15s): the one value the user-layer writer (`config/configCall.ts`) may write as `archive.baseFolder` — a folder the
 * daemon's `archive check-base` ACCEPTED, as the daemon spelt it in its report (`folder`: on WSL the mount path, `/mnt/v/…`, not
 * the Windows spelling the person picked). A branded string: nothing but `judgedFolderOf` makes one, so a value that was never
 * judged does not type-check as one.
 */

declare const JUDGED: unique symbol;

export type JudgedFolder = string & { readonly [JUDGED]: true };

/** The report's `folder` when the report ACCEPTED it and the folder is one the path rule lets into argv; otherwise none. Pure. */
export function judgedFolderOf(report: JsonObject): JudgedFolder | undefined {
  const folder = report.folder;

  return report.accepted === true && typeof folder === 'string' && basePathRefusal(folder) === undefined ? (folder as JudgedFolder) : undefined;
}
