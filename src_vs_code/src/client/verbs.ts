/**
 * The CLOSED set of daemon verbs this extension may run — the one module that holds it (plan §15f #5, §15g m1).
 *
 * <p>E5 is read-only and root-free: it calls `status --json`, `preview --all --json`, `doctor --json` and `--version`,
 * and nothing else. Every argv the client sends ends in exactly one of these tails; `structure.test.ts` fails when an
 * argv-shaped daemon word appears anywhere else in the shipped source, and `bundleScan.test.ts` fails when the bundle
 * carries a root, timer, confirm, manual or config word at all.</p>
 */

/** The verbs, by the name the client and its callers use. */
export const VERB_NAMES = ['status', 'preview', 'doctor', 'version'] as const;

export type Verb = (typeof VERB_NAMES)[number];

/** The daemon's argv tail for each verb — appended after `--exec /opt/wsl-care/bin/wsl-care`, never anything else. */
export const VERBS: { readonly [V in Verb]: readonly string[] } = {
  status: ['status', '--json'],
  preview: ['preview', '--all', '--json'],
  doctor: ['doctor', '--json'],
  version: ['--version'],
};

/**
 * How long the client waits for each verb before it kills `wsl.exe` (which ends the Linux process — measured,
 * research/2026-10-03_wsl_exe_facts.md) and reports a timeout. Each is ABOVE the sum of the daemon's own ceilings on
 * that verb's path, so the extension never kills an answer the daemon would still have given:
 *
 * - `status` starts no child process at all (plan §15b #5, the < 2 s budget) — 20 s is ten times its budget plus the
 *   `wsl.exe` relay start (≈ 0.13 s measured).
 * - `--version` answers before the machine is read, with no log file — the same 20 s.
 * - `doctor` runs, one after another, `systemctl show` for four units (15 s ceiling each, `SystemdCommands.Ceiling`),
 *   `systemctl --version` (15 s) and `docker version` (10 s, `DockerCommands.ProbeCeiling`): 85 s → 100 s.
 * - `preview --all` runs `docker version` (10 s), `system df` and `system df -v` (2 min each,
 *   `DockerCommands.DiskUsageCeiling`), the dangling-volume listing (30 s) and one `container inspect` per 100
 *   containers (30 s each): 280 s + one batch = 310 s → 330 s. A machine with more than 100 containers can exceed it,
 *   and is then told the preview timed out rather than waited on forever.
 */
export const VERB_TIMEOUT_MS: { readonly [V in Verb]: number } = {
  status: 20_000,
  version: 20_000,
  doctor: 100_000,
  preview: 330_000,
};
