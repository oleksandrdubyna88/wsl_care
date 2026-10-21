import type { ArchiveAction } from '../archive/archiveView';
import type { GpuAction } from '../gpu/gpuState';
import type { RelaysControls } from '../relays/relaysView';
import type { TasksAction } from '../windowsTasks/tasksView';
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
  | { readonly where: 'page'; readonly action: PageAction | GuardAction | TasksAction | ArchiveAction | GpuAction | NonNullable<RelaysControls['button']>['id'] }
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
  'wslCare.installWindowsWatchTask': { where: 'page', action: 'installWindowsWatchTask' },
  'wslCare.installWindowsArchiveTask': { where: 'page', action: 'installWindowsArchiveTask' },
  'wslCare.removeWindowsTasks': { where: 'page', action: 'removeWindowsTasks' },
  'wslCare.runWindowsWatchTask': { where: 'page', action: 'runWindowsWatchTask' },
  'wslCare.runWindowsArchiveTask': { where: 'page', action: 'runWindowsArchiveTask' },
  'wslCare.chooseArchiveFolder': { where: 'page', action: 'chooseArchiveFolder' },
  'wslCare.stopArchiving': { where: 'page', action: 'stopArchiving' },
  'wslCare.archiveNow': { where: 'page', action: 'archiveNow' },
  // PLAN_gpu_health.md D7: the alert's *Enable <GPU>* (the command picks the disabled adapter; the button names it).
  'wslCare.enableGpu': { where: 'page', action: 'enableGpu' },
  'wslCare.openArchive': { where: 'page', action: 'openArchive' },
  'wslCare.stopRelays': { where: 'page', action: 'stopRelays' },
  'wslCare.openLogs': { where: 'title' },
};
