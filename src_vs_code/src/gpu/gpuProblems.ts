/**
 * What a display adapter's `ConfigManagerErrorCode` means, and what a person does about it (PLAN_gpu_health.md D3). The
 * codes and meanings are Microsoft's documented Device Manager error codes ("Error codes in Device Manager in Windows");
 * the fix kind and the restart flag are this product's advice. ONE table: the verdict, the alert's words and the tests all
 * read it, never a copy (`common.testing`, *derive the list*). The daemon-side verdict of story G2 takes it into
 * `contracts/` with one writer.
 */

/** What a person does: enable it (the one with a button), reconnect it, reinstall its driver, restart, or look it up. */
export type FixKind = 'enable' | 'reconnect' | 'driver' | 'restart' | 'unknown';

export interface DeviceProblem {
  readonly code: number;
  /** Device Manager's meaning, in plain words. */
  readonly meaning: string;
  readonly fix: Exclude<FixKind, 'unknown'>;
}

/** The documented codes this product names. Anything else reads as `unknown`, with its number. */
export const DEVICE_PROBLEMS: readonly DeviceProblem[] = [
  { code: 1, meaning: 'not configured correctly', fix: 'driver' },
  { code: 3, meaning: 'its driver may be corrupted, or Windows ran low on memory', fix: 'restart' },
  { code: 10, meaning: 'it cannot start', fix: 'restart' },
  { code: 12, meaning: 'it cannot find enough free resources', fix: 'restart' },
  { code: 14, meaning: 'it cannot work until Windows is restarted', fix: 'restart' },
  { code: 18, meaning: 'its driver must be reinstalled', fix: 'driver' },
  { code: 19, meaning: 'its configuration in the registry is incomplete or damaged', fix: 'driver' },
  { code: 21, meaning: 'Windows is removing it', fix: 'restart' },
  { code: 22, meaning: 'it is disabled', fix: 'enable' },
  { code: 24, meaning: 'it is not present, not working, or has no driver installed', fix: 'reconnect' },
  { code: 28, meaning: 'no driver is installed for it', fix: 'driver' },
  { code: 29, meaning: 'the firmware did not give it the resources it needs', fix: 'restart' },
  { code: 31, meaning: 'Windows cannot load its driver', fix: 'restart' },
  { code: 32, meaning: 'its driver service is disabled', fix: 'driver' },
  { code: 37, meaning: 'its driver failed to initialise', fix: 'driver' },
  { code: 38, meaning: 'a previous instance of its driver is still in memory', fix: 'restart' },
  { code: 39, meaning: 'its driver is corrupted or missing', fix: 'driver' },
  { code: 40, meaning: 'its driver\'s registry entry is missing or wrong', fix: 'driver' },
  { code: 41, meaning: 'its driver loaded, but Windows cannot find the hardware', fix: 'reconnect' },
  { code: 43, meaning: 'Windows stopped it because it reported problems', fix: 'restart' },
  { code: 44, meaning: 'an application or service shut it down', fix: 'restart' },
  { code: 45, meaning: 'it is not connected to the computer', fix: 'reconnect' },
  { code: 46, meaning: 'Windows is shutting down', fix: 'restart' },
  { code: 47, meaning: 'it was prepared for safe removal', fix: 'reconnect' },
  { code: 48, meaning: 'its driver was blocked from starting', fix: 'driver' },
  { code: 49, meaning: 'the system registry is too large', fix: 'restart' },
  { code: 52, meaning: 'Windows cannot verify its driver\'s digital signature', fix: 'driver' },
  { code: 54, meaning: 'it failed and is being reset', fix: 'restart' },
];

/** The words a person reads for each fix kind — `N` is replaced by the code. */
export const FIX_WORDS: { readonly [K in FixKind]: string } = {
  enable: 'It is disabled. Enable it — no restart is needed.',
  reconnect: 'Windows does not see the card now. Check that it is seated and powered (an external GPU: its cable and its power), then restart Windows.',
  driver: 'Reinstall the graphics driver, then restart Windows.',
  restart: 'Restart Windows. If it is still not working after the restart, reinstall the graphics driver.',
  unknown: 'Restart Windows. If it stays, Device Manager shows what code N means for this device.',
};

/** Whether the fix ends in a restart of Windows — said in the alert, so nobody restarts for code 22. */
export const NEEDS_RESTART: { readonly [K in FixKind]: boolean } = { enable: false, reconnect: true, driver: true, restart: true, unknown: true };

/** The row for `code`, or `undefined` for a code this table does not name. */
export function problemOf(code: number): DeviceProblem | undefined {
  return DEVICE_PROBLEMS.find((p) => p.code === code);
}
