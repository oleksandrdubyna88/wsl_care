import { INSTALL_DAEMON } from '../client/handshake';

/**
 * The ONE place the *Install daemon* command is built (plan §15g m2, §15f #5). The extension never RUNS it: the host
 * shows it in a modal, and on confirmation opens a terminal in the distribution and TYPES it there
 * (`sendText(command, false)` — no newline), so the person reads it and presses Enter, and `sudo` asks for their
 * password in that terminal. Nothing in this module starts a process or reaches the runner seam
 * (`structure.test.ts` holds both: only this module spells the installer, the pipe to `sudo sh` or the raw URL, and it
 * imports nothing but the version constant).
 *
 * <p>What is fixed, and why:</p>
 * <ul>
 *   <li>the version is the compiled `INSTALL_DAEMON` — the release a new install gets (0.1.2 since #37, 2026-10-06:
 *       0.1.0's act unit is defective), never below the minimum this extension renders (`MIN_DAEMON_FOR_RENDER`) nor the
 *       one it acts with (`MIN_DAEMON_FOR_ACTIONS`, plan §15j M5) — checked against a strict `x.y.z` pattern when the
 *       module loads; a constant outside it is a defect, refused loudly, never typed;</li>
 *   <li>the script is fetched from the TAG `daemon-v&lt;MIN&gt;`, not from `main`, so the installer is the one released
 *       with that daemon, and `--version &lt;MIN&gt;` installs exactly that release; the URL spells the ref in full,
 *       `refs/tags/daemon-v&lt;MIN&gt;`, so a branch of the same name can never be served instead (raw.githubusercontent.com
 *       answers that form — observed 2026-10-04: 200 for an existing tag of cli/cli, 404 for a missing one);</li>
 *   <li>never `--skip-attestation`: the installer verifies the archive's build-provenance attestation (plan §15e A1–A3),
 *       which is why `gh` 2.56.0 or newer is a prerequisite;</li>
 *   <li>no value from the page, a setting or the daemon reaches the text — it takes no argument at all.</li>
 * </ul>
 */

const REPOSITORY = 'oleksandrdubyna88/wsl_care';

/** A release version as `install.sh` accepts it for `--version`, without a pre-release part (no released minimum has one). */
const RELEASE_VERSION = /^\d+\.\d+\.\d+$/;

function pinnedVersion(version: string): string {
  if (!RELEASE_VERSION.test(version)) {
    throw new Error(`installCommand: the daemon version to install "${version}" is not x.y.z`);
  }

  return version;
}

/** The daemon release the command installs — `INSTALL_DAEMON`, never below the render or the actions minimum. */
export const INSTALL_VERSION = pinnedVersion(INSTALL_DAEMON);

/** The command typed into the terminal, verbatim. */
export const INSTALL_COMMAND = `curl -fsSL https://raw.githubusercontent.com/${REPOSITORY}/refs/tags/daemon-v${INSTALL_VERSION}/install.sh | sudo sh -s -- --version ${INSTALL_VERSION}`;

/**
 * What the distribution must have, shown as TEXT in the modal. Nothing here is probed: `gh --version` and the like are
 * outside the four verbs this extension may run (plan §15f #5), and the installer checks each one itself and stops
 * before it changes anything when one is missing.
 */
export const INSTALL_PREREQUISITES: readonly string[] = [
  'systemd running in the distribution (WSL with systemd=true in /etc/wsl.conf)',
  'Ubuntu 24.04 or newer (glibc 2.39)',
  'gh 2.56.0 or newer, from GitHub\'s apt repository — the installer verifies the release\'s attestation with it; no gh login is needed',
  'sudo rights — the installer asks for your password in the terminal',
];
