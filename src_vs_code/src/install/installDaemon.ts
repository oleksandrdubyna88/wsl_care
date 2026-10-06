import type { Failure } from '../client/outcome';
import type { TerminalTarget } from '../client/WslCareClient';
import { failureText } from '../failureText';
import { INSTALL_COMMAND, INSTALL_PREREQUISITES, INSTALL_VERSION } from './installCommand';

/**
 * *Install daemon* (plan §7.7 as amended by §15f #5 and §15g m2), in the order that makes it safe:
 *
 * 1. the distribution is resolved and VALIDATED — the setting's shape, then `wsl.exe --list` — before anything else;
 *    a refusal is reported and nothing is opened;
 * 2. a MODAL shows the exact command and the prerequisites as text; anything but the confirm button ends it;
 * 3. a terminal opens in that distribution (`wsl.exe -d <distro>`, absolute launcher) and the command is TYPED into it
 *    with `sendText(command, false)` — never executed: the person reads it and presses Enter.
 *
 * Nothing reaches the command from outside: it is the module constant `INSTALL_COMMAND`, and this function takes no
 * value from the page that asked for it (`panel/messages.ts` accepts `installDaemon` only as a bare message). The
 * collaborators are injected so the order is testable without VS Code; `extension.ts` wires the real modal and
 * terminal — and, in Test mode, a recorder in place of both, so no test ever opens a real `wsl.exe` terminal.
 */

export interface InstallPrompt {
  readonly message: string;
  readonly detail: string;
  /** The one button that confirms; dismissing the modal or any other answer declines. */
  readonly confirm: string;
}

export interface TerminalSpec {
  readonly name: string;
  readonly shellPath: string;
  readonly shellArgs: readonly string[];
}

/** The part of a terminal this flow uses. */
export interface InstallTerminal {
  show(): void;
  sendText(text: string, addNewLine: boolean): void;
}

export interface InstallDeps {
  readonly target: () => Promise<TerminalTarget | Failure>;
  readonly confirm: (prompt: InstallPrompt) => Promise<boolean>;
  readonly openTerminal: (spec: TerminalSpec) => InstallTerminal;
  readonly report: (message: string) => void;
}

export type InstallResult =
  | { readonly kind: 'typed'; readonly distro: string }
  | { readonly kind: 'declined'; readonly distro: string }
  | { readonly kind: 'refused'; readonly failure: Failure };

export const CONFIRM_LABEL = 'Open a terminal and type it';

/** The modal's text for `distro` — already validated: a setting that passed the strict pattern, or a name `wsl.exe --list`
 * reported, taken as it is (anything not starting with `-`, §15h #4); it reaches VS Code's modal as text, never a shell. */
export function installPrompt(distro: string): InstallPrompt {
  return {
    message: `Install the wsl-care daemon ${INSTALL_VERSION} in "${distro}"?`,
    detail: [
      `A terminal opens in "${distro}" with this command TYPED, not run. Read it, then press Enter to run it:`,
      '',
      INSTALL_COMMAND,
      '',
      'The distribution needs:',
      ...INSTALL_PREREQUISITES.map((line) => `• ${line}`),
      '',
      'The installer checks each of these and stops before it changes anything when one is missing.',
    ].join('\n'),
    confirm: CONFIRM_LABEL,
  };
}

export async function installDaemon(deps: InstallDeps): Promise<InstallResult> {
  const target = await deps.target();
  if (target.kind !== 'terminal') {
    deps.report(`AI OS Care cannot open an install terminal: ${failureText(target).sentence}`);
    return { kind: 'refused', failure: target };
  }
  if (!(await deps.confirm(installPrompt(target.distro)))) {
    return { kind: 'declined', distro: target.distro };
  }
  const terminal = deps.openTerminal({ name: `AI OS Care — install (${target.distro})`, shellPath: target.shellPath, shellArgs: [...target.shellArgs] });
  terminal.show();
  terminal.sendText(INSTALL_COMMAND, false);

  return { kind: 'typed', distro: target.distro };
}
