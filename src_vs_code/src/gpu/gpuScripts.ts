import * as path from 'node:path';

import type { ProcessRequest } from '../process/runner';
import { elevatedRequest, powerShellOf, quotablePath } from '../windowsTime/windowsTimeFix';

/**
 * The two PowerShell texts of the GPU story (PLAN_gpu_health.md D1, D4): the UNELEVATED read of Windows' display adapters,
 * and the ELEVATED enable of ONE disabled adapter, run once through the Windows Time launcher (`elevatedRequest`:
 * `Process.Start` with `runas`, the inner script as `-EncodedCommand`; 1223 = declined, 13 = launch failed).
 *
 * <p>The query is a module constant. The enable is a pure function of ONE instance id — the id the extension's OWN query
 * read from Windows, selected on the host by an index (the page never sends an id) and refused unless it matches
 * {@link INSTANCE_ID}: no quote, no `$`, no backtick, no whitespace, so inside its single-quoted literal it is only text.
 * Nothing from WSL, the page or a setting's text reaches either script.</p>
 *
 * <p><b>Which PowerShell runs elevated</b> (the owner's decision of 2026-10-10): PowerShell 7 — the MSI install under
 * `%ProgramFiles%\PowerShell\7` when that file exists, else the Store build through the user's app-execution alias
 * `%LOCALAPPDATA%\Microsoft\WindowsApps\pwsh.exe` — and Windows PowerShell 5.1 when neither is there. Node cannot read the
 * alias (`lstat` answers EACCES on an app-execution alias, measured 2026-10-10), so the unelevated QUERY asks Windows about
 * it and the host elevates it only while that reading says: a zero-byte reparse point that is not a link, at the very path
 * this module computes. A real file or a link planted there is never elevated; a forged app-execution alias cannot be told
 * from Windows' own — the residual the owner accepted ({@link PwshAlias}).</p>
 */

/** The query's tags, per adapter `i` (`name.<i>=` …), the count printed LAST so a cut-off answer is visible, and the alias. */
export const TAGS = { adapter: 'adapter', name: 'name', id: 'id', code: 'code', driver: 'driver', count: 'adapters', pwshKind: 'pwsh.kind', pwshPath: 'pwsh.path' } as const;

/**
 * The ONE road every printed field takes (code round 2): a value carrying a line break — a device name, say — would end its
 * own line and let the rest read as another tag (`GPU\nadapters=0` read as "no display adapter reported"); the helper
 * flattens every break to a space, and a null prints as an empty value.
 */
export const TAG_HELPER = "function tag($n, $v) { $n + '=' + (([string]$v) -replace '[\\r\\n]+', ' ') }";

/**
 * The query's look at the Store alias, as WINDOWS sees it (`Get-Item`): `alias` is a zero-byte reparse point with no link
 * type — what an app-execution alias is (measured 2026-10-10: Length 0, `Archive, ReparsePoint`, LinkType empty) — `file`
 * anything else that is there, `absent` nothing. The path comes from the shell folder API, not the environment, and is
 * printed so the host can hold it against the path IT would start.
 */
export const ALIAS_PROBE = [
  "$ap = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'Microsoft\\WindowsApps\\pwsh.exe'",
  '$ai = Get-Item -LiteralPath $ap -ErrorAction SilentlyContinue',
  `tag '${TAGS.pwshKind}' $(if ($null -eq $ai) { 'absent' } elseif ($ai.Length -eq 0 -and (($ai.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) -and [string]::IsNullOrEmpty($ai.LinkType)) { 'alias' } else { 'file' })`,
  `tag '${TAGS.pwshPath}' $ap`,
].join('\n');

/**
 * The UNELEVATED query (D1): `Win32_VideoController`, which lists a disabled adapter too (research G1), one tagged block per
 * adapter through {@link TAG_HELPER}, its NAME printed last (own review M6), the Store alias as Windows sees it, and the
 * count LAST. `$ProgressPreference` keeps Windows PowerShell's "Preparing modules for first use" off stderr (research G5).
 * It changes nothing — no word of the tripwire's mutating list is in it (asserted).
 */
export const QUERY_SCRIPT = [
  "$ProgressPreference = 'SilentlyContinue'",
  TAG_HELPER,
  '$i = 0',
  `foreach ($a in @(Get-CimInstance -ClassName Win32_VideoController -ErrorAction Stop)) { '${TAGS.adapter}=' + $i; tag ('${TAGS.id}.' + $i) $a.PNPDeviceID; tag ('${TAGS.code}.' + $i) $a.ConfigManagerErrorCode; tag ('${TAGS.driver}.' + $i) $a.DriverVersion; tag ('${TAGS.name}.' + $i) $a.Name; $i++ }`,
  ALIAS_PROBE,
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

/** What Node's `lstat` says of a file the enable might elevate — `undefined` when it cannot be read (absent, or EACCES). */
export interface FileInfo {
  readonly bytes: number;
  readonly symlink: boolean;
}

export type FileInfoOf = (file: string) => FileInfo | undefined;

/** The Store alias as the LAST query reported it ({@link ALIAS_PROBE}). */
export interface PwshAlias {
  readonly kind: 'alias' | 'file' | 'absent';
  readonly path: string;
}

/** The engine that runs ELEVATED — the program, and the words the modal and the outcome name it by. */
export interface Engine {
  readonly file: string;
  /** "PowerShell 7 (C:\…\pwsh.exe)" / "Windows PowerShell 5.1 (C:\…\powershell.exe)". */
  readonly label: string;
}

/** The MSI PowerShell 7 — an administrator-only folder, preferred whenever it is installed. */
export function msiPwsh(env: Readonly<Record<string, string | undefined>>): string | undefined {
  const programFiles = env.ProgramFiles ?? env.PROGRAMFILES;

  return programFiles === undefined || programFiles === '' ? undefined : path.win32.join(programFiles, 'PowerShell', '7', 'pwsh.exe');
}

/** The Store PowerShell 7's app-execution alias under the user's LOCALAPPDATA — the path THIS extension would start. */
export function aliasPwsh(env: Readonly<Record<string, string | undefined>>): string | undefined {
  const local = env.LOCALAPPDATA;

  return local === undefined || local === '' ? undefined : path.win32.join(local, 'Microsoft', 'WindowsApps', 'pwsh.exe');
}

/** Whether `info` is a real file (not a link) with bytes in it. */
function realFile(info: FileInfo | undefined): boolean {
  return info !== undefined && !info.symlink && info.bytes > 0;
}

/** The MSI pwsh, when it is a real file PowerShell can quote. */
function msiCandidate(env: Readonly<Record<string, string | undefined>>, fileInfo: FileInfoOf): string | undefined {
  const file = msiPwsh(env);

  return file !== undefined && realFile(fileInfo(file)) && quotablePath(file) ? file : undefined;
}

/** Whether the last query saw an app-execution alias at `file` — the path this extension computes, compared case-insensitively. */
function aliasSeen(alias: PwshAlias | undefined, file: string): boolean {
  return alias !== undefined && alias.kind === 'alias' && alias.path.toLowerCase() === file.toLowerCase();
}

/** The Store alias, when the last query saw an app-execution alias at the path this extension computes, and PowerShell can quote it. */
function aliasCandidate(env: Readonly<Record<string, string | undefined>>, alias: PwshAlias | undefined): string | undefined {
  const file = aliasPwsh(env);

  return file !== undefined && aliasSeen(alias, file) && quotablePath(file) ? file : undefined;
}

/** The PowerShell 7 that runs the enable elevated (the MSI one, else the verified Store alias), or `undefined` for 5.1. */
export function elevatedPowerShellFor(env: Readonly<Record<string, string | undefined>>, fileInfo: FileInfoOf, alias: PwshAlias | undefined): string | undefined {
  return msiCandidate(env, fileInfo) ?? aliasCandidate(env, alias);
}

/** The engine the enable elevates, named: PowerShell 7 when one is chosen, else Windows PowerShell 5.1 (or '' with no PowerShell at all). */
export function engineOf(env: Readonly<Record<string, string | undefined>>, fileInfo: FileInfoOf, alias: PwshAlias | undefined): Engine {
  const seven = elevatedPowerShellFor(env, fileInfo, alias);
  if (seven !== undefined) {
    return { file: seven, label: `PowerShell 7 (${seven})` };
  }
  const windows = powerShellOf(env);

  return typeof windows === 'string' ? { file: windows, label: `Windows PowerShell 5.1 (${windows})` } : { file: '', label: '' };
}

/** The one elevated request the enable ever builds — or why none can be (a refused id, no Windows PowerShell). */
export function enableRequest(env: Readonly<Record<string, string | undefined>>, id: string, timeoutMs: number, fileInfo: FileInfoOf, alias: PwshAlias | undefined, settleSeconds: number): ProcessRequest | string {
  if (!INSTANCE_ID.test(id)) {
    return 'the device id Windows reported is not one this extension passes to an elevated command';
  }

  return elevatedRequest(env, enableScript(id, settleSeconds), timeoutMs, elevatedPowerShellFor(env, fileInfo, alias));
}
