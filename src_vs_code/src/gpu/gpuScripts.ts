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
 * <p><b>Which PowerShell runs elevated</b> (the owner's decision of 2026-10-10, the path of 2026-10-11): PowerShell 7 — the
 * MSI install under `%ProgramFiles%\PowerShell\7` when the read verified its signature there, else the Store build by its RESOLVED package path
 * `%ProgramFiles%\WindowsApps\Microsoft.PowerShell_<version>_<arch>__8wekyb3d8bbwe\pwsh.exe` — and Windows PowerShell 5.1
 * when neither is there. `WindowsApps` is written by TrustedInstaller and SYSTEM only, and Node cannot even see it (ENOENT,
 * measured 2026-10-11), so the unelevated QUERY resolves the package (`Get-AppxPackage`) and verifies its `pwsh.exe` — a real
 * file, no link, an Authenticode signature Windows reads as Valid from `CN=Microsoft Corporation` — at EVERY read; the host
 * elevates that path only while the LAST read (the one taken right after the modal, before the launch) said so AND the path
 * has the shape this module expects under this machine's Program Files ({@link storePwsh}). The user's app-execution alias
 * under `%LOCALAPPDATA%` is never started: it is the user's own folder (research G8).</p>
 */

/** The query's tags, per adapter `i` (`name.<i>=` …), the count printed LAST so a cut-off answer is visible, and the Store package. */
export const TAGS = { adapter: 'adapter', name: 'name', id: 'id', code: 'code', driver: 'driver', count: 'adapters', pwshKind: 'pwsh.store', pwshPath: 'pwsh.path', msiKind: 'pwsh.msi', msiPath: 'pwsh.msiPath' } as const;

/**
 * The ONE road every printed field takes (code round 2): a value carrying a line break — a device name, say — would end its
 * own line and let the rest read as another tag (`GPU\nadapters=0` read as "no display adapter reported"); the helper
 * flattens every break to a space, and a null prints as an empty value.
 */
export const TAG_HELPER = "function tag($n, $v) { $n + '=' + (([string]$v) -replace '[\\r\\n]+', ' ') }";

/**
 * ONE verdict for a PowerShell 7 the query looked at (the Store package and the MSI install alike, round 4): `signed` means
 * a real file with bytes, no link type, and `Get-AuthenticodeSignature` says Valid from `CN=Microsoft Corporation`;
 * `unsigned` anything else that is there; `absent` nothing.
 */
export const SIGNED_HELPER = "function verdict($i, $s) { if ($null -eq $i) { 'absent' } elseif ($i.Length -gt 0 -and [string]::IsNullOrEmpty($i.LinkType) -and $null -ne $s -and $s.Status -eq 'Valid' -and $s.SignerCertificate.Subject -like 'CN=Microsoft Corporation,*') { 'signed' } else { 'unsigned' } }";

/**
 * The query's look at the two PowerShell 7 installs, as WINDOWS resolves and verifies them. The Store package:
 * `Get-AppxPackage -Name Microsoft.PowerShell` names its install folder (the version in it moves with every Store update —
 * measured 7.6.4 on 2026-10-10 and 7.6.6 the next day — so it is resolved at every read, never pinned). The MSI install: the
 * path under the Program Files the KNOWN-FOLDER API names (the registry's, not the environment's — round 4: a `ProgramFiles`
 * variable pointed at a user folder must not name the program this extension elevates). Each gets {@link SIGNED_HELPER}'s
 * verdict and its path. Measured cost (research G9): 0.9–1.3 s under Windows PowerShell 5.1, most of it `Get-AppxPackage`.
 */
export const PWSH_PROBE = [
  SIGNED_HELPER,
  '$sp = Get-AppxPackage -Name Microsoft.PowerShell -ErrorAction SilentlyContinue | Select-Object -First 1',
  "$se = if ($null -ne $sp) { Join-Path $sp.InstallLocation 'pwsh.exe' } else { '' }",
  "$si = if ($se -ne '') { Get-Item -LiteralPath $se -ErrorAction SilentlyContinue } else { $null }",
  '$ss = if ($null -ne $si) { Get-AuthenticodeSignature -LiteralPath $se } else { $null }',
  `tag '${TAGS.pwshKind}' (verdict $si $ss)`,
  `tag '${TAGS.pwshPath}' $se`,
  "$mp = Join-Path ([Environment]::GetFolderPath('ProgramFiles')) 'PowerShell\\7\\pwsh.exe'",
  '$mi = Get-Item -LiteralPath $mp -ErrorAction SilentlyContinue',
  '$ms = if ($null -ne $mi) { Get-AuthenticodeSignature -LiteralPath $mp } else { $null }',
  `tag '${TAGS.msiKind}' (verdict $mi $ms)`,
  `tag '${TAGS.msiPath}' $mp`,
].join('\n');

/**
 * The UNELEVATED query (D1): `Win32_VideoController`, which lists a disabled adapter too (research G1), one tagged block per
 * adapter through {@link TAG_HELPER}, its NAME printed last (own review M6), the two PowerShell 7 installs as Windows resolves and verifies them, and the
 * count LAST. `$ProgressPreference` keeps Windows PowerShell's "Preparing modules for first use" off stderr (research G5).
 * It changes nothing — no word of the tripwire's mutating list is in it (asserted).
 */
export const QUERY_SCRIPT = [
  "$ProgressPreference = 'SilentlyContinue'",
  TAG_HELPER,
  '$i = 0',
  `foreach ($a in @(Get-CimInstance -ClassName Win32_VideoController -ErrorAction Stop)) { '${TAGS.adapter}=' + $i; tag ('${TAGS.id}.' + $i) $a.PNPDeviceID; tag ('${TAGS.code}.' + $i) $a.ConfigManagerErrorCode; tag ('${TAGS.driver}.' + $i) $a.DriverVersion; tag ('${TAGS.name}.' + $i) $a.Name; $i++ }`,
  PWSH_PROBE,
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

/** A PowerShell 7 as the LAST query verified it ({@link PWSH_PROBE}): the Store package, or the MSI install. */
export interface PwshProbe {
  readonly kind: 'signed' | 'unsigned' | 'absent';
  /** The `pwsh.exe` the query looked at — its stdout, used as a program path ONLY after this module accepted it. */
  readonly path: string;
}

/** The read's two PowerShell 7 facts, as the host hands them to the enable. */
export interface PwshFacts {
  readonly store: PwshProbe | undefined;
  readonly msi: PwshProbe | undefined;
}


/** The engine that runs ELEVATED — the program, the words the modal and the outcome name it by, and which of the three it is. */
export interface Engine {
  readonly file: string;
  /** "PowerShell 7 (C:\…\pwsh.exe)" / "Windows PowerShell 5.1 (C:\…\powershell.exe)". */
  readonly label: string;
  /** The MSI pwsh, the Store package, or Windows PowerShell 5.1 — the Store package is the one another account may not have (own review 2, F5). */
  readonly kind: 'msi' | 'store' | 'windows';
}

/** This machine's Program Files, from the environment (as the Windows Time launcher reads `SystemRoot`), or nothing. */
function programFilesOf(env: Readonly<Record<string, string | undefined>>): string | undefined {
  const programFiles = env.ProgramFiles ?? env.PROGRAMFILES;

  return programFiles === undefined || programFiles === '' ? undefined : programFiles;
}

/** The MSI PowerShell 7 — an administrator-only folder, preferred whenever it is installed; the read verifies it at this path. */
export function msiPwsh(env: Readonly<Record<string, string | undefined>>): string | undefined {
  const programFiles = programFilesOf(env);

  return programFiles === undefined ? undefined : path.win32.join(programFiles, 'PowerShell', '7', 'pwsh.exe');
}

/** Microsoft's Store publisher id — the suffix of every package folder Microsoft publishes through the Store. */
const STORE_PUBLISHER = '8wekyb3d8bbwe';

/** `text` as a regular expression matching itself. */
function literal(text: string): string {
  return text.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
}

/** The one shape a Microsoft Store PowerShell 7 package's `pwsh.exe` has under `programFiles` (case-insensitive). */
function storeShape(programFiles: string): RegExp {
  return new RegExp(`^${literal(programFiles)}\\\\WindowsApps\\\\Microsoft\\.PowerShell_[0-9.]+_[A-Za-z0-9]+__${STORE_PUBLISHER}\\\\pwsh\\.exe$`, 'i');
}

/** The resolved path, when the read verified the package's signature. */
function signedPath(store: PwshProbe | undefined): string | undefined {
  return store?.kind === 'signed' ? store.path : undefined;
}

/**
 * The Store package's `pwsh.exe` as the LAST read resolved it — accepted ONLY when the read verified its signature and the
 * path has exactly the shape Windows gives a Microsoft Store package under THIS machine's Program Files:
 * `<ProgramFiles>\WindowsApps\Microsoft.PowerShell_<version>_<arch>__8wekyb3d8bbwe\pwsh.exe` — a folder only
 * TrustedInstaller and SYSTEM may write. The stdout of a query becomes a program path through this check alone.
 */
export function storePwsh(env: Readonly<Record<string, string | undefined>>, store: PwshProbe | undefined): string | undefined {
  const programFiles = programFilesOf(env);
  const file = signedPath(store);

  return programFiles !== undefined && file !== undefined && storeShape(programFiles).test(file) ? file : undefined;
}

/** Whether `info` is a real file (not a link) with bytes in it. */
function realFile(info: FileInfo | undefined): boolean {
  return info !== undefined && !info.symlink && info.bytes > 0;
}

/** Whether the read verified a signed MSI pwsh at `file` — the path the known-folder API named, compared case-insensitively. */
function msiVerified(msi: PwshProbe | undefined, file: string): boolean {
  return msi !== undefined && msi.kind === 'signed' && msi.path.toLowerCase() === file.toLowerCase();
}

/**
 * The MSI pwsh, when the read verified its signature at the very path this module computes from `ProgramFiles` (round 4: an
 * environment pointed at a user folder makes the two differ, and nothing is elevated) and Node sees a real file there.
 */
function verifiedMsi(env: Readonly<Record<string, string | undefined>>, fileInfo: FileInfoOf, msi: PwshProbe | undefined): string | undefined {
  const file = msiPwsh(env);

  return file !== undefined && msiVerified(msi, file) && realFile(fileInfo(file)) ? file : undefined;
}

/** The MSI pwsh, when the read verified it and PowerShell can quote its path. */
function msiCandidate(env: Readonly<Record<string, string | undefined>>, fileInfo: FileInfoOf, msi: PwshProbe | undefined): string | undefined {
  const file = verifiedMsi(env, fileInfo, msi);

  return file !== undefined && quotablePath(file) ? file : undefined;
}

/** The Store package's pwsh, when the last read verified it, its path has the Store shape, and PowerShell can quote it. */
function storeCandidate(env: Readonly<Record<string, string | undefined>>, store: PwshProbe | undefined): string | undefined {
  const file = storePwsh(env, store);

  return file !== undefined && quotablePath(file) ? file : undefined;
}

/** No read yet: neither PowerShell 7 fact. */
const NO_FACTS: PwshFacts = { store: undefined, msi: undefined };

/** The PowerShell 7 that runs the enable elevated — the MSI one the read verified, else the verified Store package — and which. */
function sevenOf(env: Readonly<Record<string, string | undefined>>, fileInfo: FileInfoOf, facts: PwshFacts): { readonly file: string; readonly kind: 'msi' | 'store' } | undefined {
  const msi = msiCandidate(env, fileInfo, facts.msi);
  if (msi !== undefined) {
    return { file: msi, kind: 'msi' };
  }
  const store = storeCandidate(env, facts.store);

  return store === undefined ? undefined : { file: store, kind: 'store' };
}

/** The PowerShell 7 that runs the enable elevated (the MSI one, else the verified Store package), or `undefined` for 5.1. */
export function elevatedPowerShellFor(env: Readonly<Record<string, string | undefined>>, fileInfo: FileInfoOf, facts: PwshFacts | undefined): string | undefined {
  return sevenOf(env, fileInfo, facts ?? NO_FACTS)?.file;
}

/** The engine the enable elevates, named: PowerShell 7 when one is chosen, else Windows PowerShell 5.1 (or '' with no PowerShell at all). */
export function engineOf(env: Readonly<Record<string, string | undefined>>, fileInfo: FileInfoOf, facts: PwshFacts | undefined): Engine {
  const seven = sevenOf(env, fileInfo, facts ?? NO_FACTS);
  if (seven !== undefined) {
    return { ...seven, label: `PowerShell 7 (${seven.file})` };
  }
  const windows = powerShellOf(env);

  return typeof windows === 'string' ? { file: windows, label: `Windows PowerShell 5.1 (${windows})`, kind: 'windows' } : { file: '', label: '', kind: 'windows' };
}

/**
 * The one elevated request the enable ever builds — for the ENGINE the modal names and the settle value it shows, both looked
 * at once (own review 2, F1) — or why none can be (a refused id, no Windows PowerShell).
 */
export function enableRequestFor(env: Readonly<Record<string, string | undefined>>, id: string, timeoutMs: number, engine: Engine, settleSeconds: number): ProcessRequest | string {
  if (!INSTANCE_ID.test(id)) {
    return 'the device id Windows reported is not one this extension passes to an elevated command';
  }

  return elevatedRequest(env, enableScript(id, settleSeconds), timeoutMs, engine.kind === 'windows' ? undefined : engine.file);
}

/** {@link enableRequestFor} with the engine derived here — the one road, for a caller that holds no engine yet. */
export function enableRequest(env: Readonly<Record<string, string | undefined>>, id: string, timeoutMs: number, fileInfo: FileInfoOf, facts: PwshFacts | undefined, settleSeconds: number): ProcessRequest | string {
  return enableRequestFor(env, id, timeoutMs, engineOf(env, fileInfo, facts), settleSeconds);
}
