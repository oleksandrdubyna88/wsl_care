import type { ArchiveAction } from '../archive/archiveView';
import type { GuardAction } from '../windowsTime/guardState';
import type { PageAction } from './view';

/**
 * Where each contributed command's BUTTON lives (the owner's standing rule of 2026-10-09: every extension action is
 * reachable by a button in the panel UI; a command-palette entry may duplicate one, never be the only way).
 *
 * - `page`: a button the panel page shows, which posts `action` — the same operation the command runs.
 * - `title`: an icon in the panel view's own title bar (`contributes.menus["view/title"]` when `view == wslCare.panel`).
 * - `opener`: the command opens the panel itself; its button is the panel's activity-bar icon.
 *
 * `commandButtons.test.ts` holds this table to `package.json`'s commands and to the button sets the page really has.
 */
export type CommandButton =
  | { readonly where: 'page'; readonly action: PageAction | GuardAction | ArchiveAction }
  | { readonly where: 'title' }
  | { readonly where: 'opener' };

export const COMMAND_BUTTONS: Readonly<Record<string, CommandButton>> = {
  'wslCare.openPanel': { where: 'opener' },
  'wslCare.refresh': { where: 'page', action: 'refresh' },
  'wslCare.startWsl': { where: 'page', action: 'startWsl' },
  'wslCare.installDaemon': { where: 'page', action: 'installDaemon' },
  'wslCare.startWindowsTime': { where: 'page', action: 'startWindowsTime' },
  'wslCare.installWindowsTimeGuard': { where: 'page', action: 'installWindowsTimeGuard' },
  'wslCare.removeWindowsTimeGuard': { where: 'page', action: 'removeWindowsTimeGuard' },
  'wslCare.chooseArchiveFolder': { where: 'page', action: 'chooseArchiveFolder' },
  'wslCare.stopArchiving': { where: 'page', action: 'stopArchiving' },
  'wslCare.openLogs': { where: 'title' },
};
