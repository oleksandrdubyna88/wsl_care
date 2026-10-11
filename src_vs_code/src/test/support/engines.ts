import type { Engine } from '../../powershell/elevatedEngine';
import type { EngineRead, EngineSource } from '../../powershell/engineHost';
import type { ProcessRequest } from '../../process/runner';

/** The program a launcher request elevates — the `-ArgumentList` of its `ProcessStartInfo` ('' when none). */
export function elevatedProgramOf(request: ProcessRequest | undefined): string {
  return /ProcessStartInfo -ArgumentList '([^']+)'/.exec(request?.args[3] ?? '')?.[1] ?? '';
}

/** The engines the Windows Time flow tests elevate (PLAN_pwsh_engine_windows_time.md D3). */
export const PS51 = 'C:\\Windows\\System32\\WindowsPowerShell\\v1.0\\powershell.exe';
export const STORE_PWSH = 'C:\\Program Files\\WindowsApps\\Microsoft.PowerShell_7.6.6.0_x64__8wekyb3d8bbwe\\pwsh.exe';

export const PS51_ENGINE: Engine = { file: PS51, label: `Windows PowerShell 5.1 (${PS51})`, kind: 'windows' };
export const STORE_ENGINE: Engine = { file: STORE_PWSH, label: `PowerShell 7 (${STORE_PWSH})`, kind: 'store' };

export const PS51_READ: EngineRead = { engine: PS51_ENGINE, failure: '' };
export const STORE_READ: EngineRead = { engine: STORE_ENGINE, failure: '' };

/** A read that did not answer: 5.1, labelled with why PowerShell 7 could not be checked. */
export function failedRead(why: string): EngineRead {
  return { engine: { ...PS51_ENGINE, label: `${PS51_ENGINE.label} — PowerShell 7 could not be checked: ${why}` }, failure: why };
}

/**
 * A fixed engine source: `before` answers the read before the modal, `after` the re-read right before the launch. Each ask
 * is pushed to `events` (when given) as `engine` / `reread`, so a test can pin the ORDER.
 */
export function fixedEngine(before: EngineRead, after: EngineRead = before, events?: string[]): EngineSource {
  return {
    engine: () => { events?.push('engine'); return Promise.resolve(before); },
    reread: () => { events?.push('reread'); return Promise.resolve(after); },
    last: () => after,
  };
}
