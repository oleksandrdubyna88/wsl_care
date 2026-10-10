import * as path from 'node:path';

import type { ProcessRequest } from '../process/runner';
import { elevatedRequest, powerShellOf } from '../windowsTime/windowsTimeFix';

/**
 * The two PowerShell texts of the GPU story (PLAN_gpu_health.md D1, D4): the UNELEVATED read of Windows' display adapters,
 * and the ELEVATED enable of ONE disabled adapter, run once through the Windows Time launcher (`elevatedRequest`:
 * `Process.Start` with `runas`, the inner script as `-EncodedCommand`; 1223 = declined, 13 = launch failed).
 *
 * <p>The query is a module constant. The enable is a pure function of ONE instance id — the id the extension's OWN query
 * read from Windows, selected on the host by an index (the page never sends an id) and refused unless it matches
 * {@link INSTANCE_ID}: no quote, no `$`, no backtick, no whitespace, so inside its single-quoted literal it is only text.
 * Nothing from WSL, the page or a setting's text reaches either script.</p>
 */

/** The query's tags, per adapter `i` (`name.<i>=` …), and the count printed LAST so a cut-off answer is visible. */
export const TAGS = { adapter: 'adapter', name: 'name', id: 'id', code: 'code', driver: 'driver', count: 'adapters' } as const;

/**
 * The UNELEVATED query (D1): `Win32_VideoController`, which lists a disabled adapter too (research G1), one tagged block per
 * adapter, its NAME printed last: a name carrying a newline cannot shadow its id or code, because the parser keeps a tag's FIRST
 * value (own review M6). `$ProgressPreference` keeps Windows PowerShell's "Preparing modules for first use" off stderr (research G5).
 * It changes nothing — no word of the tripwire's mutating list is in it (asserted).
 */
export const QUERY_SCRIPT = [
  "$ProgressPreference = 'SilentlyContinue'",
  '$i = 0',
  `foreach ($a in @(Get-CimInstance -ClassName Win32_VideoController -ErrorAction Stop)) { '${TAGS.adapter}=' + $i; '${TAGS.id}.' + $i + '=' + $a.PNPDeviceID; '${TAGS.code}.' + $i + '=' + $a.ConfigManagerErrorCode; '${TAGS.driver}.' + $i + '=' + $a.DriverVersion; '${TAGS.name}.' + $i + '=' + $a.Name; $i++ }`,
  `'${TAGS.count}=' + $i`,
  'exit 0',
].join('\n');

/** The read-only query, UNELEVATED: the absolute Windows PowerShell with the fixed script — no `runas`, no payload. */
export function queryRequest(env: Readonly<Record<string, string | undefined>>, timeoutMs: number): ProcessRequest | string {
  const powerShell = powerShellOf(env);

  return typeof powerShell === 'string' ? { file: powerShell, args: ['-NoProfile', '-NonInteractive', '-Command', QUERY_SCRIPT], timeoutMs } : powerShell.reason;
}

/** A PnP instance id as Windows spells one (`PCI\VEN_1002&DEV_7551&…\6&2A3DA1BE&0&00000019`) — and nothing a script reads. */
export const INSTANCE_ID = /^[A-Za-z0-9\\&_.#{}-]{1,200}$/;

/** The enable script's own exits, beside the launcher's 13 and 1223 (D4). */
export const ENABLE_EXIT = { done: 0, importFailed: 40, absent: 41, notDisplay: 42, notDisabled: 43, enableFailed: 44 } as const;

/** The only problem code the enable acts on: "disabled" (research G1). */
export const DISABLED_CODE = 22;

/**
 * The ELEVATED enable (D4) of the device `id`, which must already have passed {@link INSTANCE_ID}. It re-checks, as
 * administrator, that the device exists, is a display adapter and is still disabled — so an index that went stale between
 * the panel's render and the click acts on nothing it should not — and only then runs `Enable-PnpDevice`. `PnpDevice` comes
 * by absolute path because the module path is cut to `$PSHOME\Modules` (a user's Documents folder supplies nothing), where
 * `pwsh` 7 does not hold it (research G6); System32 is `[Environment]::SystemDirectory` — GetSystemDirectory, not an
 * environment variable the elevated process inherits from the person's registry (own review M1). It waits `settleSeconds` (`wslCare.gpu.enableSettleSeconds`) after the enable, so the
 * re-read that follows sees the driver started rather than starting — a NUMBER from a setting, clamped, never text.
 */
export function enableScript(id: string, settleSeconds: number): string {
  return [
    "$env:PSModulePath = \"$PSHOME\\Modules\"; $ProgressPreference = 'SilentlyContinue'",
    `try { Import-Module -Name (Join-Path ([Environment]::SystemDirectory) 'WindowsPowerShell\\v1.0\\Modules\\PnpDevice') -ErrorAction Stop } catch { exit ${ENABLE_EXIT.importFailed} }`,
    `$id = '${id}'`,
    `try { $d = Get-PnpDevice -InstanceId $id -ErrorAction Stop } catch { exit ${ENABLE_EXIT.absent} }`,
    `if ($d.Class -ne 'Display') { exit ${ENABLE_EXIT.notDisplay} }`,
    `if ([int]$d.ConfigManagerErrorCode -ne ${DISABLED_CODE}) { exit ${ENABLE_EXIT.notDisabled} }`,
    `try { Enable-PnpDevice -InstanceId $id -Confirm:$false -ErrorAction Stop } catch { exit ${ENABLE_EXIT.enableFailed} }`,
    `Start-Sleep -Seconds ${wholeSeconds(settleSeconds)}`,
    `exit ${ENABLE_EXIT.done}`,
  ].join('\n');
}

/** A whole, non-negative number of seconds; NaN reads as 0 (own review). */
export function wholeSeconds(value: number): number {
  return Number.isFinite(value) ? Math.max(0, Math.trunc(value)) : 0;
}

/** The program the enable elevates, as the modal names it (own review M1): the MSI `pwsh` when present, else Windows PowerShell. */
export function elevatedProgramFor(env: Readonly<Record<string, string | undefined>>, exists: (file: string) => boolean): string {
  const windows = powerShellOf(env);

  return elevatedPowerShellFor(env, exists) ?? (typeof windows === 'string' ? windows : '');
}

/** The MSI PowerShell 7, the one the enable prefers ELEVATED — an administrator-only folder (the Store `pwsh` is an alias). */
export function msiPwsh(env: Readonly<Record<string, string | undefined>>): string | undefined {
  const programFiles = env.ProgramFiles ?? env.PROGRAMFILES;

  return programFiles === undefined || programFiles === '' ? undefined : path.win32.join(programFiles, 'PowerShell', '7', 'pwsh.exe');
}

/** The PowerShell that runs the enable elevated: the MSI `pwsh` when that file exists, else the Windows PowerShell. */
export function elevatedPowerShellFor(env: Readonly<Record<string, string | undefined>>, exists: (file: string) => boolean): string | undefined {
  const pwsh = msiPwsh(env);

  return pwsh !== undefined && exists(pwsh) ? pwsh : undefined;
}

/** The one elevated request the enable ever builds — or why none can be (a refused id, no Windows PowerShell). */
export function enableRequest(env: Readonly<Record<string, string | undefined>>, id: string, timeoutMs: number, exists: (file: string) => boolean, settleSeconds: number): ProcessRequest | string {
  if (!INSTANCE_ID.test(id)) {
    return 'the device id Windows reported is not one this extension passes to an elevated command';
  }

  return elevatedRequest(env, enableScript(id, settleSeconds), timeoutMs, elevatedPowerShellFor(env, exists));
}
